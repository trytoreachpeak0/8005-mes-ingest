using System.Data;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    public async Task<ExternallyReadableDemandCatalogRead> ReadExternallyReadableDemandCatalogAsync(
        long? knownRevision = null,
        CancellationToken cancellationToken = default)
    {
        if (knownRevision is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(knownRevision));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            // CatalogState and CatalogItems are one resource. Take the shared
            // side of the writer's commit-order lock before any table lock so
            // the reader is wholly before or after a commit and cannot deadlock
            // while converting the two resources in the opposite order.
            await AcquireCommitRoundReadFenceLockAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            long revision;
            string? projectionCommitId;
            long? projectionSequence;
            DateTimeOffset? projectionCommittedAt;
            await using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = """
                    SELECT state.CatalogRevision, state.ProjectionCommitId,
                           commitRow.ProjectionSequence, commitRow.CommittedAt
                    FROM mesingest.CatalogState AS state
                    LEFT JOIN mesingest.ProjectionCommits AS commitRow
                        ON commitRow.ProjectionCommitId = state.ProjectionCommitId
                    WHERE state.Id = 1;
                    """;
                await using var reader = await state.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The catalog singleton is missing.");
                }

                revision = reader.GetInt64(0);
                projectionCommitId = reader.IsDBNull(1) ? null : reader.GetString(1);
                projectionSequence = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                projectionCommittedAt = reader.IsDBNull(3)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(3);
            }

            await _readBoundaryObserver.OnFenceSelectedAsync(
                ProjectionReadSurface.Catalog,
                new ProjectionReadFence(
                    projectionCommitId,
                    projectionSequence,
                    revision),
                cancellationToken).ConfigureAwait(false);

            if (knownRevision == revision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ExternallyReadableDemandCatalogRead.Unchanged(revision);
            }

            var items = new List<ExternallyReadableDemandSnapshot>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT DemandId, SeriesId, WorkType, Sublot, Generation,
                           DemandRevision, CreatedAt, ValueObservedAt,
                           ValuePollTraceId, ValueProjectionCommitId,
                           Area, Eqp, Step, MesSourceDate, Package
                    FROM mesingest.CatalogItems
                    ORDER BY DemandId;
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    items.Add(new ExternallyReadableDemandSnapshot(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetInt32(4), reader.GetInt64(5),
                        reader.GetFieldValue<DateTimeOffset>(6),
                        reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8),
                        reader.GetString(9), new LiveMesFieldSetSnapshot(
                            reader.GetString(10), reader.GetString(11), reader.GetString(12),
                            reader.GetFieldValue<DateTimeOffset>(13), reader.GetString(14))));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = new ExternallyReadableDemandCatalogSnapshot(
                revision,
                projectionCommitId,
                projectionSequence,
                projectionCommittedAt,
                items).Validate();
            return ExternallyReadableDemandCatalogRead.Complete(snapshot);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ReconcileExternallyReadableDemandCatalogAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MesTaskUnionRound round,
        string projectionCommitId,
        CancellationToken cancellationToken)
    {
        var candidates = new List<CatalogCandidate>();
        var currentConditionCodes = await LoadCurrentConditionCodesAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT d.DemandId, d.SeriesId, s.WorkType, s.Sublot, d.Generation,
                       d.DemandRevision, d.CreatedAt, d.ValueObservedAt,
                       CASE WHEN catalogItem.DemandRevision = d.DemandRevision
                            THEN catalogItem.ValuePollTraceId
                            ELSE observationCommit.PollTraceId END,
                       CASE WHEN catalogItem.DemandRevision = d.DemandRevision
                            THEN catalogItem.ValueProjectionCommitId
                            ELSE d.LatestObservationProjectionCommitId END,
                       d.Area, d.Eqp, d.Step, d.MesSourceDate, d.Package,
                       s.Lifecycle, d.Status,
                       (SELECT COUNT_BIG(*)
                        FROM mesingest.DemandRawObservations AS observation
                        WHERE observation.DemandId = d.DemandId
                          AND observation.ProjectionCommitId = d.LatestObservationProjectionCommitId)
                FROM mesingest.DemandSeries AS s
                INNER JOIN mesingest.TransportDemands AS d ON d.DemandId = s.CurrentDemandId
                INNER JOIN mesingest.ProjectionCommits AS observationCommit
                    ON observationCommit.ProjectionCommitId = d.LatestObservationProjectionCommitId
                LEFT JOIN mesingest.CatalogItems AS catalogItem
                    ON catalogItem.DemandId = d.DemandId
                WHERE s.Lifecycle = N'TRACKING'
                  AND d.Status = N'VISIBLE'
                ORDER BY d.DemandId;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var fields = new LiveMesFieldSetSnapshot(
                    GetNullableString(reader, 10),
                    GetNullableString(reader, 11),
                    GetNullableString(reader, 12),
                    GetNullableDateTimeOffset(reader, 13),
                    GetNullableString(reader, 14));
                var seriesId = reader.GetString(1);
                var conditions = currentConditionCodes.TryGetValue(seriesId, out var codes)
                    ? codes
                    : [];
                var rawObservationCount = checked((int)reader.GetInt64(17));
                if (!ExternallyReadableDemandPolicy.IsEligible(
                        reader.GetString(15), reader.GetString(16), rawObservationCount,
                        fields, conditions))
                {
                    continue;
                }

                candidates.Add(new CatalogCandidate(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetString(9), fields));
            }
        }

        long oldRevision;
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = "SELECT CatalogRevision FROM mesingest.CatalogState WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1;";
            await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The catalog singleton is missing.");
            }
            oldRevision = reader.GetInt64(0);
        }

        var existingItems = await LoadCatalogItemsForUpdateAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        var candidatesByDemandId = candidates.ToDictionary(
            item => item.DemandId,
            StringComparer.Ordinal);
        var existingByDemandId = existingItems.ToDictionary(
            item => item.DemandId,
            StringComparer.Ordinal);
        var removals = existingItems
            .Where(item => !candidatesByDemandId.ContainsKey(item.DemandId))
            .ToArray();
        var updates = candidates
            .Where(item => existingByDemandId.TryGetValue(item.DemandId, out var existing)
                           && !CatalogItemsExactlyMatch(existing, item))
            .ToArray();
        var insertions = candidates
            .Where(item => !existingByDemandId.ContainsKey(item.DemandId))
            .ToArray();

        if (removals.Length == 0 && updates.Length == 0 && insertions.Length == 0)
        {
            await UpdateCommitCatalogRevisionAsync(
                connection, transaction, projectionCommitId, oldRevision, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var nextRevision = checked(oldRevision + 1);
        foreach (var item in removals)
        {
            await DeleteCatalogItemAsync(
                connection, transaction, item.DemandId, cancellationToken).ConfigureAwait(false);
        }
        foreach (var item in updates)
        {
            await UpdateCatalogItemAsync(connection, transaction, item, cancellationToken)
                .ConfigureAwait(false);
        }
        foreach (var item in insertions)
        {
            await InsertCatalogItemAsync(connection, transaction, item, cancellationToken)
                .ConfigureAwait(false);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE mesingest.CatalogState
                SET CatalogRevision = @revision,
                    ProjectionCommitId = @projectionCommitId
                WHERE Id = 1;
                """;
            command.Parameters.Add("@revision", SqlDbType.BigInt).Value = nextRevision;
            AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "The catalog singleton disappeared while advancing its revision.");
            }
        }
        await UpdateCommitCatalogRevisionAsync(
            connection, transaction, projectionCommitId, nextRevision, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, IReadOnlyCollection<string>>> LoadCurrentConditionCodesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT condition.SeriesId, condition.ErrorCode
            FROM mesingest.DemandSeriesCurrentConditions AS condition
            INNER JOIN mesingest.DemandSeries AS series
                ON series.SeriesId = condition.SeriesId
            INNER JOIN mesingest.TransportDemands AS demand
                ON demand.DemandId = series.CurrentDemandId
            WHERE series.Lifecycle = N'TRACKING'
              AND demand.Status = N'VISIBLE'
            ORDER BY condition.SeriesId, condition.ErrorCode,
                     condition.Target, condition.SubjectKind;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var mutable = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var seriesId = reader.GetString(0);
            if (!mutable.TryGetValue(seriesId, out var codes))
            {
                codes = [];
                mutable.Add(seriesId, codes);
            }
            codes.Add(reader.GetString(1));
        }
        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyCollection<string>)pair.Value.AsReadOnly(),
            StringComparer.Ordinal);
    }

    private static async Task<IReadOnlyList<CatalogCandidate>> LoadCatalogItemsForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DemandId, SeriesId, WorkType, Sublot, Generation, DemandRevision,
                   CreatedAt, ValueObservedAt, ValuePollTraceId, ValueProjectionCommitId,
                   Area, Eqp, Step, MesSourceDate, Package
            FROM mesingest.CatalogItems WITH (UPDLOCK, HOLDLOCK)
            ORDER BY DemandId;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = new List<CatalogCandidate>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new CatalogCandidate(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetString(9),
                new LiveMesFieldSetSnapshot(
                    reader.GetString(10), reader.GetString(11), reader.GetString(12),
                    reader.GetFieldValue<DateTimeOffset>(13), reader.GetString(14))));
        }
        return items;
    }

    private static bool CatalogItemsExactlyMatch(CatalogCandidate left, CatalogCandidate right) =>
        string.Equals(left.DemandId, right.DemandId, StringComparison.Ordinal)
        && string.Equals(left.SeriesId, right.SeriesId, StringComparison.Ordinal)
        && string.Equals(left.WorkType, right.WorkType, StringComparison.Ordinal)
        && string.Equals(left.Sublot, right.Sublot, StringComparison.Ordinal)
        && left.Generation == right.Generation
        && left.DemandRevision == right.DemandRevision
        && left.CreatedAt.EqualsExact(right.CreatedAt)
        && left.ValueObservedAt.EqualsExact(right.ValueObservedAt)
        && string.Equals(left.ValuePollTraceId, right.ValuePollTraceId, StringComparison.Ordinal)
        && string.Equals(left.ValueProjectionCommitId, right.ValueProjectionCommitId, StringComparison.Ordinal)
        && string.Equals(left.Fields.Area, right.Fields.Area, StringComparison.Ordinal)
        && string.Equals(left.Fields.Eqp, right.Fields.Eqp, StringComparison.Ordinal)
        && string.Equals(left.Fields.Step, right.Fields.Step, StringComparison.Ordinal)
        && left.Fields.MesSourceDate!.Value.EqualsExact(right.Fields.MesSourceDate!.Value)
        && string.Equals(left.Fields.Package, right.Fields.Package, StringComparison.Ordinal);

    private static async Task DeleteCatalogItemAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string demandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM mesingest.CatalogItems WHERE DemandId = @demandId;";
        AddNVarChar(command, "@demandId", 64, demandId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "A removed catalog item changed while applying its delta.");
        }
    }

    private static async Task UpdateCatalogItemAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CatalogCandidate item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE mesingest.CatalogItems
            SET SeriesId = @seriesId,
                WorkType = @workType,
                Sublot = @sublot,
                Generation = @generation,
                DemandRevision = @demandRevision,
                CreatedAt = @createdAt,
                ValueObservedAt = @valueObservedAt,
                ValuePollTraceId = @valuePollTraceId,
                ValueProjectionCommitId = @valueProjectionCommitId,
                Area = @area,
                Eqp = @eqp,
                Step = @step,
                MesSourceDate = @mesSourceDate,
                Package = @package
            WHERE DemandId = @demandId;
            """;
        AddCatalogItemParameters(command, item);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "A changed catalog item changed again while applying its delta.");
        }
    }

    private static async Task UpdateCommitCatalogRevisionAsync(
        SqlConnection connection, SqlTransaction transaction, string projectionCommitId,
        long revision, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE mesingest.ProjectionCommits SET CatalogRevision = @revision WHERE ProjectionCommitId = @projectionCommitId;";
        command.Parameters.Add("@revision", SqlDbType.BigInt).Value = revision;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "The projection commit disappeared while recording its catalog revision.");
        }
    }

    private static async Task InsertCatalogItemAsync(
        SqlConnection connection, SqlTransaction transaction, CatalogCandidate item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.CatalogItems
                (DemandId, SeriesId, WorkType, Sublot, Generation, DemandRevision,
                 CreatedAt, ValueObservedAt, ValuePollTraceId, ValueProjectionCommitId,
                 Area, Eqp, Step, MesSourceDate, Package)
            VALUES
                (@demandId, @seriesId, @workType, @sublot, @generation, @demandRevision,
                 @createdAt, @valueObservedAt, @valuePollTraceId, @valueProjectionCommitId,
                 @area, @eqp, @step, @mesSourceDate, @package);
            """;
        AddCatalogItemParameters(command, item);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "A new catalog item was not inserted exactly once.");
        }
    }

    private static void AddCatalogItemParameters(SqlCommand command, CatalogCandidate item)
    {
        AddNVarChar(command, "@demandId", 64, item.DemandId);
        AddNVarChar(command, "@seriesId", 64, item.SeriesId);
        AddNVarChar(command, "@workType", 128, item.WorkType);
        AddNVarChar(command, "@sublot", 256, item.Sublot);
        command.Parameters.Add("@generation", SqlDbType.Int).Value = item.Generation;
        command.Parameters.Add("@demandRevision", SqlDbType.BigInt).Value = item.DemandRevision;
        AddDateTimeOffset(command, "@createdAt", item.CreatedAt);
        AddDateTimeOffset(command, "@valueObservedAt", item.ValueObservedAt);
        AddNVarChar(command, "@valuePollTraceId", 128, item.ValuePollTraceId);
        AddNVarChar(command, "@valueProjectionCommitId", 64, item.ValueProjectionCommitId);
        AddNVarChar(command, "@area", CurrentMesFieldMaximumLength, item.Fields.Area!);
        AddNVarChar(command, "@eqp", CurrentMesFieldMaximumLength, item.Fields.Eqp!);
        AddNVarChar(command, "@step", CurrentMesFieldMaximumLength, item.Fields.Step!);
        AddDateTimeOffset(command, "@mesSourceDate", item.Fields.MesSourceDate!.Value);
        AddNVarChar(command, "@package", CurrentMesFieldMaximumLength, item.Fields.Package!);
    }

    private sealed record CatalogCandidate(
        string DemandId, string SeriesId, string WorkType, string Sublot,
        int Generation, long DemandRevision, DateTimeOffset CreatedAt,
        DateTimeOffset ValueObservedAt, string ValuePollTraceId,
        string ValueProjectionCommitId, LiveMesFieldSetSnapshot Fields);
}
