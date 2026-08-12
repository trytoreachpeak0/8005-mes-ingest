using System.Data;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

/// <summary>
/// SQL Server implementation of the new MesIngest projection seam. A successful
/// MES_TASK_UNION round and every fact derived from it share one serializable
/// transaction and one projection-commit identity.
/// </summary>
public sealed class SqlServerMesIngestProjection : IMesIngestProjection
{
    private const string SuccessOutcome = "SUCCESS";
    private const string TrackingLifecycle = "TRACKING";
    private const string VisiblePresence = "VISIBLE";
    private const string VisibleDemandStatus = "VISIBLE";
    private const string SeriesStartedEvent = "DEMAND_SERIES_STARTED";
    private const string DemandCreatedEvent = "TRANSPORT_DEMAND_CREATED";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaEnsured;

    public SqlServerMesIngestProjection(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A dedicated new-MesIngest SQL Server connection string is required.",
                nameof(connectionString));
        }

        _connectionString = connectionString;
    }

    public async Task<SuccessRoundCommitReceipt> CommitSuccessRoundAsync(
        MesTaskUnionRound round,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = PrepareSuccessRound(round, cancellationToken);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);

        try
        {
            await RejectExistingPollTraceAsync(
                connection,
                transaction,
                round.PollTraceId,
                cancellationToken).ConfigureAwait(false);

            var projectionCommitId = NewId();
            await InsertPollTraceAndCommitAsync(
                connection,
                transaction,
                round,
                prepared.ContentDigest,
                projectionCommitId,
                cancellationToken).ConfigureAwait(false);

            var seriesIds = new List<string>(prepared.Observations.Count);
            var demandIds = new List<string>(prepared.Observations.Count);
            foreach (var item in prepared.Observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await LoadCurrentProjectionForUpdateAsync(
                    connection,
                    transaction,
                    item.KeyToken,
                    cancellationToken).ConfigureAwait(false);

                ProjectedIdentity identity;
                if (current is null)
                {
                    identity = await InsertFirstGenerationAsync(
                        connection,
                        transaction,
                        round,
                        projectionCommitId,
                        item,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    EnsureEquivalentCurrentObservation(current, item.Observation, round.CompletedAt);
                    await AdvanceEquivalentObservationAsync(
                        connection,
                        transaction,
                        current,
                        projectionCommitId,
                        round.CompletedAt,
                        cancellationToken).ConfigureAwait(false);
                    identity = new ProjectedIdentity(current.SeriesId, current.DemandId);
                }

                await InsertRawObservationAsync(
                    connection,
                    transaction,
                    round.PollTraceId,
                    projectionCommitId,
                    identity,
                    item,
                    cancellationToken).ConfigureAwait(false);
                seriesIds.Add(identity.SeriesId);
                demandIds.Add(identity.DemandId);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SuccessRoundCommitReceipt(
                round.PollTraceId,
                projectionCommitId,
                seriesIds,
                demandIds);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DemandSeriesSnapshot?> GetDemandSeriesByKeyAsync(
        string workType,
        string sublot,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(workType, nameof(workType), 128);
        ValidateRequiredText(sublot, nameof(sublot), 256);
        var keyToken = TransportDemandKeyIdentity.CreateToken(workType, sublot);
        return await ReadDemandSeriesAsync(
            "s.KeyToken = @identity",
            keyToken,
            expectedWorkType: workType,
            expectedSublot: sublot,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DemandSeriesSnapshot?> GetDemandSeriesAsync(
        string seriesId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(seriesId, nameof(seriesId), 64);
        return await ReadDemandSeriesAsync(
            "s.SeriesId = @identity",
            seriesId,
            expectedWorkType: null,
            expectedSublot: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PollTraceSnapshot?> GetPollTraceAsync(
        string pollTraceId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(pollTraceId, nameof(pollTraceId), 128);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);

        try
        {
            PollTraceRow? trace;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT
                        p.PollTraceId,
                        p.QueryVersion,
                        p.Outcome,
                        p.StartedAt,
                        p.CompletedAt,
                        p.[RowCount],
                        p.ContentDigest,
                        c.ProjectionCommitId,
                        c.CommittedAt
                    FROM mesingest.PollTraces AS p
                    INNER JOIN mesingest.ProjectionCommits AS c
                        ON c.PollTraceId = p.PollTraceId
                    WHERE p.PollTraceId = @pollTraceId;
                    """;
                AddNVarChar(command, "@pollTraceId", 128, pollTraceId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                trace = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? ReadPollTraceRow(reader)
                    : null;
            }

            if (trace is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var observations = await ReadRawObservationsAsync(
                connection,
                transaction,
                "o.PollTraceId = @identity",
                pollTraceId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new PollTraceSnapshot(
                trace.PollTraceId,
                trace.QueryVersion,
                trace.Outcome,
                trace.StartedAt,
                trace.CompletedAt,
                trace.RowCount,
                trace.ContentDigest,
                new ProjectionCommitSnapshot(
                    trace.ProjectionCommitId,
                    trace.PollTraceId,
                    trace.CommittedAt),
                observations);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<DemandSeriesSnapshot?> ReadDemandSeriesAsync(
        string predicate,
        string identity,
        string? expectedWorkType,
        string? expectedSublot,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);

        try
        {
            DemandSeriesRow? series;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    SELECT
                        s.SeriesId,
                        s.WorkType,
                        s.Sublot,
                        s.Lifecycle,
                        s.CurrentPresence,
                        s.StartedAt,
                        s.CreatedPollTraceId,
                        s.CreatedProjectionCommitId,
                        s.LatestProjectionCommitId,
                        d.DemandId,
                        d.Generation,
                        d.PredecessorDemandId,
                        d.Status,
                        d.CreatedAt,
                        d.DemandLastSeenAt,
                        d.CreatedPollTraceId,
                        d.CreatedProjectionCommitId,
                        d.LatestProjectionCommitId,
                        d.Area,
                        d.Eqp,
                        d.Step,
                        d.MesSourceDate,
                        d.Package
                    FROM mesingest.DemandSeries AS s
                    INNER JOIN mesingest.TransportDemands AS d
                        ON d.DemandId = s.CurrentDemandId
                    WHERE {predicate};
                    """;
                if (expectedWorkType is null)
                {
                    AddNVarChar(command, "@identity", 64, identity);
                }
                else
                {
                    AddChar(command, "@identity", 64, identity);
                }
                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                series = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? ReadDemandSeriesRow(reader)
                    : null;
            }

            if (series is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (expectedWorkType is not null
                && (!string.Equals(series.WorkType, expectedWorkType, StringComparison.Ordinal)
                    || !string.Equals(series.Sublot, expectedSublot, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "TransportDemandKey token collision detected; refusing to return another series.");
            }

            var observations = await ReadRawObservationsAsync(
                connection,
                transaction,
                "o.SeriesId = @identity",
                series.SeriesId,
                cancellationToken).ConfigureAwait(false);
            var events = await ReadEventsAsync(
                connection,
                transaction,
                series.SeriesId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new DemandSeriesSnapshot(
                series.SeriesId,
                series.WorkType,
                series.Sublot,
                series.Lifecycle,
                series.CurrentPresence,
                series.StartedAt,
                series.CreatedPollTraceId,
                series.CreatedProjectionCommitId,
                series.LatestProjectionCommitId,
                new TransportDemandSnapshot(
                    series.DemandId,
                    series.SeriesId,
                    series.Generation,
                    series.PredecessorDemandId,
                    series.DemandStatus,
                    series.DemandCreatedAt,
                    series.DemandLastSeenAt,
                    series.DemandCreatedPollTraceId,
                    series.DemandCreatedProjectionCommitId,
                    series.DemandLatestProjectionCommitId,
                    new LiveMesFieldSetSnapshot(
                        series.Area,
                        series.Eqp,
                        series.Step,
                        series.MesSourceDate,
                        series.Package)),
                observations,
                events);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqlServerMesIngestSchema.EnsureAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            _schemaEnsured = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static PreparedRound PrepareSuccessRound(
        MesTaskUnionRound round,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.Outcome != MesTaskUnionRoundOutcome.Success)
        {
            throw new ArgumentException(
                "This projection seam accepts complete SUCCESS rounds only.",
                nameof(round));
        }

        ValidateRequiredText(round.PollTraceId, nameof(round.PollTraceId), 128);
        ValidateRequiredText(round.QueryVersion, nameof(round.QueryVersion), 128);
        ArgumentNullException.ThrowIfNull(round.Observations);
        if (round.CompletedAt < round.StartedAt)
        {
            throw new ArgumentException(
                "A round cannot complete before it starts.",
                nameof(round));
        }

        var prepared = new List<PreparedObservation>(round.Observations.Count);
        var keyTokens = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < round.Observations.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = round.Observations[ordinal]
                ?? throw new ArgumentException("A round observation cannot be null.", nameof(round));
            if (string.IsNullOrWhiteSpace(observation.WorkType)
                || string.IsNullOrWhiteSpace(observation.Sublot))
            {
                throw new NotSupportedException(
                    "Unassigned MES observations are introduced by ticket 02 and are not supported by the ticket 01 spine.");
            }

            ValidateRequiredText(observation.WorkType, nameof(observation.WorkType), 128);
            ValidateRequiredText(observation.Sublot, nameof(observation.Sublot), 256);
            ValidateOptionalText(observation.Area, nameof(observation.Area), 128);
            ValidateOptionalText(observation.Eqp, nameof(observation.Eqp), 256);
            ValidateOptionalText(observation.Step, nameof(observation.Step), 256);
            ValidateOptionalText(observation.Package, nameof(observation.Package), 256);

            var keyToken = TransportDemandKeyIdentity.CreateToken(
                observation.WorkType,
                observation.Sublot);
            if (!keyTokens.Add(keyToken))
            {
                throw new NotSupportedException(
                    "Duplicate TransportDemandKey observations are introduced by ticket 04 and are not supported by the ticket 01 spine.");
            }

            prepared.Add(new PreparedObservation(ordinal, keyToken, observation));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedRound(prepared, MesTaskUnionRoundDigest.Compute(round.Observations));
    }

    private static async Task RejectExistingPollTraceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string pollTraceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM mesingest.PollTraces WITH (UPDLOCK, HOLDLOCK)
            WHERE PollTraceId = @pollTraceId;
            """;
        AddNVarChar(command, "@pollTraceId", 128, pollTraceId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new NotSupportedException(
                "PollTrace replay and conflict semantics are introduced by ticket 02; ticket 01 requires a new PollTraceId.");
        }
    }

    private static async Task InsertPollTraceAndCommitAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MesTaskUnionRound round,
        string contentDigest,
        string projectionCommitId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.PollTraces
                (PollTraceId, QueryVersion, Outcome, StartedAt, CompletedAt, [RowCount], ContentDigest)
            VALUES
                (@pollTraceId, @queryVersion, N'SUCCESS', @startedAt, @completedAt, @rowCount, @contentDigest);

            INSERT INTO mesingest.ProjectionCommits
                (ProjectionCommitId, PollTraceId, CommittedAt)
            VALUES
                (@projectionCommitId, @pollTraceId, @completedAt);
            """;
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNVarChar(command, "@queryVersion", 128, round.QueryVersion);
        AddDateTimeOffset(command, "@startedAt", round.StartedAt);
        AddDateTimeOffset(command, "@completedAt", round.CompletedAt);
        command.Parameters.Add("@rowCount", SqlDbType.Int).Value = round.Observations.Count;
        AddChar(command, "@contentDigest", 64, contentDigest);
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CurrentProjectionRow?> LoadCurrentProjectionForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string keyToken,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                s.SeriesId,
                s.WorkType,
                s.Sublot,
                s.Lifecycle,
                s.CurrentPresence,
                s.CurrentDemandId,
                d.Generation,
                d.Status,
                d.DemandLastSeenAt,
                d.Area,
                d.Eqp,
                d.Step,
                d.MesSourceDate,
                d.Package
            FROM mesingest.DemandSeries AS s WITH (UPDLOCK, HOLDLOCK)
            LEFT JOIN mesingest.TransportDemands AS d WITH (UPDLOCK, HOLDLOCK)
                ON d.DemandId = s.CurrentDemandId
            WHERE s.KeyToken = @keyToken;
            """;
        AddChar(command, "@keyToken", 64, keyToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(5))
        {
            throw new InvalidOperationException(
                "An existing DemandSeries has no current TransportDemand.");
        }

        return new CurrentProjectionRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            GetNullableString(reader, 9),
            GetNullableString(reader, 10),
            GetNullableString(reader, 11),
            GetNullableDateTimeOffset(reader, 12),
            GetNullableString(reader, 13));
    }

    private static async Task<ProjectedIdentity> InsertFirstGenerationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MesTaskUnionRound round,
        string projectionCommitId,
        PreparedObservation item,
        CancellationToken cancellationToken)
    {
        var seriesId = NewId();
        var demandId = NewId();
        var observation = item.Observation;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO mesingest.DemandSeries
                    (SeriesId, KeyToken, WorkType, Sublot, Lifecycle, CurrentPresence,
                     StartedAt, CreatedPollTraceId, CreatedProjectionCommitId,
                     LatestProjectionCommitId, CurrentDemandId, LastSeriesSequence)
                VALUES
                    (@seriesId, @keyToken, @workType, @sublot, N'TRACKING', N'VISIBLE',
                     @occurredAt, @pollTraceId, @projectionCommitId,
                     @projectionCommitId, NULL, 0);

                INSERT INTO mesingest.TransportDemands
                    (DemandId, SeriesId, Generation, PredecessorDemandId, Status,
                     CreatedAt, DemandLastSeenAt, CreatedPollTraceId,
                     CreatedProjectionCommitId, LatestProjectionCommitId,
                     Area, Eqp, Step, MesSourceDate, Package)
                VALUES
                    (@demandId, @seriesId, 1, NULL, N'VISIBLE',
                     @occurredAt, @occurredAt, @pollTraceId,
                     @projectionCommitId, @projectionCommitId,
                     @area, @eqp, @step, @mesSourceDate, @package);

                UPDATE mesingest.DemandSeries
                SET CurrentDemandId = @demandId,
                    LastSeriesSequence = 2
                WHERE SeriesId = @seriesId;
                """;
            AddNVarChar(command, "@seriesId", 64, seriesId);
            AddChar(command, "@keyToken", 64, item.KeyToken);
            AddNVarChar(command, "@workType", 128, observation.WorkType!);
            AddNVarChar(command, "@sublot", 256, observation.Sublot!);
            AddDateTimeOffset(command, "@occurredAt", round.CompletedAt);
            AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
            AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
            AddNVarChar(command, "@demandId", 64, demandId);
            AddNullableNVarChar(command, "@area", 128, observation.Area);
            AddNullableNVarChar(command, "@eqp", 256, observation.Eqp);
            AddNullableNVarChar(command, "@step", 256, observation.Step);
            AddNullableDateTimeOffset(command, "@mesSourceDate", observation.MesSourceDate);
            AddNullableNVarChar(command, "@package", 256, observation.Package);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertInitialEventAsync(
            connection,
            transaction,
            seriesId,
            sequence: 1,
            SeriesStartedEvent,
            subjectKind: "SERIES",
            subjectId: seriesId,
            round,
            projectionCommitId,
            JsonSerializer.Serialize(new
            {
                seriesId,
                observation.WorkType,
                observation.Sublot,
            }),
            cancellationToken).ConfigureAwait(false);
        await InsertInitialEventAsync(
            connection,
            transaction,
            seriesId,
            sequence: 2,
            DemandCreatedEvent,
            subjectKind: "DEMAND",
            subjectId: demandId,
            round,
            projectionCommitId,
            JsonSerializer.Serialize(new
            {
                demandId,
                generation = 1,
            }),
            cancellationToken).ConfigureAwait(false);

        return new ProjectedIdentity(seriesId, demandId);
    }

    private static async Task InsertInitialEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        long sequence,
        string eventType,
        string subjectKind,
        string subjectId,
        MesTaskUnionRound round,
        string projectionCommitId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.DemandSeriesEvents
                (EventId, SeriesId, SeriesSequence, EventType, OccurredAt,
                 SubjectKind, SubjectId, PollTraceId, ProjectionCommitId,
                 PayloadVersion, Payload)
            VALUES
                (@eventId, @seriesId, @seriesSequence, @eventType, @occurredAt,
                 @subjectKind, @subjectId, @pollTraceId, @projectionCommitId,
                 1, @payload);
            """;
        AddNVarChar(command, "@eventId", 64, NewId());
        AddNVarChar(command, "@seriesId", 64, seriesId);
        command.Parameters.Add("@seriesSequence", SqlDbType.BigInt).Value = sequence;
        AddNVarChar(command, "@eventType", 128, eventType);
        AddDateTimeOffset(command, "@occurredAt", round.CompletedAt);
        AddNVarChar(command, "@subjectKind", 64, subjectKind);
        AddNVarChar(command, "@subjectId", 128, subjectId);
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@payload", -1, payloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureEquivalentCurrentObservation(
        CurrentProjectionRow current,
        MesTaskUnionObservation observation,
        DateTimeOffset completedAt)
    {
        if (!string.Equals(current.WorkType, observation.WorkType, StringComparison.Ordinal)
            || !string.Equals(current.Sublot, observation.Sublot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "TransportDemandKey token collision detected while applying a round.");
        }

        if (!string.Equals(current.Lifecycle, TrackingLifecycle, StringComparison.Ordinal)
            || !string.Equals(current.CurrentPresence, VisiblePresence, StringComparison.Ordinal)
            || !string.Equals(current.DemandStatus, VisibleDemandStatus, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "Lifecycle transitions are outside the ticket 01 tracer spine.");
        }

        if (completedAt < current.DemandLastSeenAt)
        {
            throw new InvalidOperationException(
                "A successful round cannot move DemandLastSeenAt backwards.");
        }

        if (!string.Equals(current.Area, observation.Area, StringComparison.Ordinal)
            || !string.Equals(current.Eqp, observation.Eqp, StringComparison.Ordinal)
            || !string.Equals(current.Step, observation.Step, StringComparison.Ordinal)
            || !DateTimeOffsetEqualsExact(current.MesSourceDate, observation.MesSourceDate)
            || !string.Equals(current.Package, observation.Package, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "Live MES field changes are introduced by ticket 03 and are not supported by the ticket 01 spine.");
        }
    }

    private static async Task AdvanceEquivalentObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CurrentProjectionRow current,
        string projectionCommitId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE mesingest.DemandSeries
            SET LatestProjectionCommitId = @projectionCommitId
            WHERE SeriesId = @seriesId;

            UPDATE mesingest.TransportDemands
            SET LatestProjectionCommitId = @projectionCommitId,
                DemandLastSeenAt = @completedAt
            WHERE DemandId = @demandId;
            """;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@seriesId", 64, current.SeriesId);
        AddNVarChar(command, "@demandId", 64, current.DemandId);
        AddDateTimeOffset(command, "@completedAt", completedAt);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 2)
        {
            throw new InvalidOperationException(
                "The current DemandSeries projection changed while applying an equivalent observation.");
        }
    }

    private static async Task InsertRawObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string pollTraceId,
        string projectionCommitId,
        ProjectedIdentity identity,
        PreparedObservation item,
        CancellationToken cancellationToken)
    {
        var observation = item.Observation;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.DemandRawObservations
                (PollTraceId, Ordinal, ProjectionCommitId, SeriesId, DemandId,
                 WorkType, Sublot, Area, Eqp, Step, MesSourceDate, Package)
            VALUES
                (@pollTraceId, @ordinal, @projectionCommitId, @seriesId, @demandId,
                 @workType, @sublot, @area, @eqp, @step, @mesSourceDate, @package);
            """;
        AddNVarChar(command, "@pollTraceId", 128, pollTraceId);
        command.Parameters.Add("@ordinal", SqlDbType.Int).Value = item.Ordinal;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@seriesId", 64, identity.SeriesId);
        AddNVarChar(command, "@demandId", 64, identity.DemandId);
        AddNVarChar(command, "@workType", 128, observation.WorkType!);
        AddNVarChar(command, "@sublot", 256, observation.Sublot!);
        AddNullableNVarChar(command, "@area", 128, observation.Area);
        AddNullableNVarChar(command, "@eqp", 256, observation.Eqp);
        AddNullableNVarChar(command, "@step", 256, observation.Step);
        AddNullableDateTimeOffset(command, "@mesSourceDate", observation.MesSourceDate);
        AddNullableNVarChar(command, "@package", 256, observation.Package);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DemandRawObservationSnapshot>> ReadRawObservationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string predicate,
        string identity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT
                o.Ordinal,
                o.PollTraceId,
                o.ProjectionCommitId,
                o.SeriesId,
                o.DemandId,
                o.WorkType,
                o.Sublot,
                o.Area,
                o.Eqp,
                o.Step,
                o.MesSourceDate,
                o.Package
            FROM mesingest.DemandRawObservations AS o
            INNER JOIN mesingest.PollTraces AS p
                ON p.PollTraceId = o.PollTraceId
            WHERE {predicate}
            ORDER BY p.CompletedAt, o.PollTraceId, o.Ordinal;
            """;
        AddNVarChar(command, "@identity", 256, identity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var observations = new List<DemandRawObservationSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            observations.Add(new DemandRawObservationSnapshot(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                GetNullableString(reader, 9),
                GetNullableDateTimeOffset(reader, 10),
                GetNullableString(reader, 11)));
        }

        return observations;
    }

    private static async Task<IReadOnlyList<DemandSeriesEventSnapshot>> ReadEventsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                EventId,
                SeriesId,
                SeriesSequence,
                EventType,
                OccurredAt,
                SubjectKind,
                SubjectId,
                PollTraceId,
                ProjectionCommitId,
                PayloadVersion,
                Payload
            FROM mesingest.DemandSeriesEvents
            WHERE SeriesId = @seriesId
            ORDER BY SeriesSequence;
            """;
        AddNVarChar(command, "@seriesId", 64, seriesId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var events = new List<DemandSeriesEventSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new DemandSeriesEventSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetString(5),
                GetNullableString(reader, 6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9),
                reader.GetString(10)));
        }

        return events;
    }

    private static DemandSeriesRow ReadDemandSeriesRow(SqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt32(10),
            GetNullableString(reader, 11),
            reader.GetString(12),
            reader.GetFieldValue<DateTimeOffset>(13),
            reader.GetFieldValue<DateTimeOffset>(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetString(17),
            GetNullableString(reader, 18),
            GetNullableString(reader, 19),
            GetNullableString(reader, 20),
            GetNullableDateTimeOffset(reader, 21),
            GetNullableString(reader, 22));

    private static PollTraceRow ReadPollTraceRow(SqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8));

    private static bool DateTimeOffsetEqualsExact(
        DateTimeOffset? left,
        DateTimeOffset? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Value.EqualsExact(right.Value);
    }

    private static string? GetNullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableDateTimeOffset(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void AddNVarChar(
        SqlCommand command,
        string name,
        int size,
        string value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;

    private static void AddNullableNVarChar(
        SqlCommand command,
        string name,
        int size,
        string? value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value =
            value is null ? DBNull.Value : value;

    private static void AddChar(
        SqlCommand command,
        string name,
        int size,
        string value) =>
        command.Parameters.Add(name, SqlDbType.Char, size).Value = value;

    private static void AddDateTimeOffset(
        SqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value = value;

    private static void AddNullableDateTimeOffset(
        SqlCommand command,
        string name,
        DateTimeOffset? value) =>
        command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value =
            value is null ? DBNull.Value : value.Value;

    private static void ValidateRequiredText(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value exceeds the SQL contract maximum of {maxLength} characters.");
        }
    }

    private static void ValidateOptionalText(string? value, string parameterName, int maxLength)
    {
        if (value is not null && value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value exceeds the SQL contract maximum of {maxLength} characters.");
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private sealed record PreparedRound(
        IReadOnlyList<PreparedObservation> Observations,
        string ContentDigest);

    private sealed record PreparedObservation(
        int Ordinal,
        string KeyToken,
        MesTaskUnionObservation Observation);

    private sealed record ProjectedIdentity(string SeriesId, string DemandId);

    private sealed record CurrentProjectionRow(
        string SeriesId,
        string WorkType,
        string Sublot,
        string Lifecycle,
        string CurrentPresence,
        string DemandId,
        int Generation,
        string DemandStatus,
        DateTimeOffset DemandLastSeenAt,
        string? Area,
        string? Eqp,
        string? Step,
        DateTimeOffset? MesSourceDate,
        string? Package);

    private sealed record DemandSeriesRow(
        string SeriesId,
        string WorkType,
        string Sublot,
        string Lifecycle,
        string CurrentPresence,
        DateTimeOffset StartedAt,
        string CreatedPollTraceId,
        string CreatedProjectionCommitId,
        string LatestProjectionCommitId,
        string DemandId,
        int Generation,
        string? PredecessorDemandId,
        string DemandStatus,
        DateTimeOffset DemandCreatedAt,
        DateTimeOffset DemandLastSeenAt,
        string DemandCreatedPollTraceId,
        string DemandCreatedProjectionCommitId,
        string DemandLatestProjectionCommitId,
        string? Area,
        string? Eqp,
        string? Step,
        DateTimeOffset? MesSourceDate,
        string? Package);

    private sealed record PollTraceRow(
        string PollTraceId,
        string QueryVersion,
        string Outcome,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        int RowCount,
        string ContentDigest,
        string ProjectionCommitId,
        DateTimeOffset CommittedAt);
}
