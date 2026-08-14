using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    public async Task<CurrentIngestAttentionSnapshot> ReadCurrentIngestAttentionAsync(
        CurrentIngestAttentionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query.NormalizeAndValidate();
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await SelectOperationalSnapshotAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            await _readBoundaryObserver.OnFenceSelectedAsync(
                ProjectionReadSurface.CurrentIngestAttention,
                new ProjectionReadFence(
                    snapshot.ProjectionCommitId,
                    snapshot.ProjectionSequence,
                    snapshot.CatalogRevision,
                    snapshot.PollTraceHighWater),
                cancellationToken).ConfigureAwait(false);
            var result = await ReadCurrentAttentionAtFenceAsync(
                connection,
                transaction,
                snapshot,
                query,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<OperationalSnapshotIdentity> SelectOperationalSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @pollTraceHighWater BIGINT;
            SELECT @pollTraceHighWater = COALESCE(MAX(PollTraceSequence), 0)
            FROM mesingest.PollTraces;

            SELECT TOP (1)
                commitRow.ProjectionCommitId,
                commitRow.ProjectionSequence,
                commitRow.CommittedAt,
                commitRow.PollTraceId,
                commitRow.CatalogRevision,
                @pollTraceHighWater
            FROM mesingest.ProjectionCommits AS commitRow
            INNER JOIN mesingest.PollTraces AS trace
                ON trace.PollTraceId = commitRow.PollTraceId
               AND trace.PollTraceSequence <= @pollTraceHighWater
            ORDER BY commitRow.ProjectionSequence DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new CurrentIngestAttentionException(
                CurrentIngestAttentionErrorCodes.ProjectionNotAvailable,
                "No successful projection commit is available.");
        }

        return new OperationalSnapshotIdentity(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetFieldValue<DateTimeOffset>(2).ToUniversalTime(),
            reader.GetString(3),
            reader.GetInt64(5),
            reader.GetInt64(4),
            _timeProvider.GetUtcNow().ToUniversalTime());
    }

    private static async Task<CurrentIngestAttentionSnapshot> ReadCurrentAttentionAtFenceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        CurrentIngestAttentionQuery query,
        CancellationToken cancellationToken)
    {
        var items = new List<CurrentIngestAttentionItemSnapshot>();
        await ReadSeriesErrorAttentionAsync(
            connection,
            transaction,
            snapshot,
            items,
            cancellationToken).ConfigureAwait(false);
        await ReadTaskProtectionAttentionAsync(
            connection,
            transaction,
            snapshot,
            items,
            cancellationToken).ConfigureAwait(false);
        await ReadUnassignedAttentionAsync(
            connection,
            transaction,
            snapshot,
            items,
            cancellationToken).ConfigureAwait(false);
        await ReadPollFailureAttentionAsync(
            connection,
            transaction,
            snapshot,
            items,
            cancellationToken).ConfigureAwait(false);

        var allItems = items.ToArray();
        var ordered = allItems
            .Where(item => query.Kinds is null || query.Kinds.Count == 0
                || query.Kinds.Contains(item.Kind, StringComparer.Ordinal))
            .Where(item => query.Severities is null || query.Severities.Count == 0
                || query.Severities.Contains(item.Severity, StringComparer.Ordinal))
            .OrderBy(item => SeverityRank(item.Severity))
            .ThenByDescending(item => item.OccurredAt)
            .ThenBy(item => item.StableIdentity, StringComparer.Ordinal)
            .ToArray();
        var exactTotal = ordered.LongLength;
        var offset = checked((long)(query.PageNumber - 1) * query.PageSize);
        var page = offset >= ordered.LongLength
            ? Array.Empty<CurrentIngestAttentionItemSnapshot>()
            : ordered.Skip(checked((int)offset)).Take(query.PageSize).ToArray();
        var totalPages = exactTotal == 0
            ? 0
            : checked((int)((exactTotal + query.PageSize - 1) / query.PageSize));

        return new CurrentIngestAttentionSnapshot(
            snapshot,
            exactTotal,
            new CurrentIngestAttentionFacets(
                CurrentIngestAttentionKinds.All.Select(kind =>
                    new CurrentIngestAttentionFacetSnapshot(
                        kind,
                        allItems.LongCount(item => string.Equals(item.Kind, kind, StringComparison.Ordinal))))
                    .ToArray(),
                CurrentIngestAttentionSeverities.All.Select(severity =>
                    new CurrentIngestAttentionFacetSnapshot(
                        severity,
                        allItems.LongCount(item => string.Equals(item.Severity, severity, StringComparison.Ordinal))))
                    .ToArray()),
            CurrentIngestAttentionOrder.Default,
            query.PageSize,
            query.PageNumber,
            totalPages,
            query.Kinds ?? Array.Empty<string>(),
            query.Severities ?? Array.Empty<string>(),
            page);
    }

    private static async Task ReadSeriesErrorAttentionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        ICollection<CurrentIngestAttentionItemSnapshot> items,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH ActivePeriods AS
            (
                SELECT period.PeriodId, period.SeriesId, period.ErrorCode,
                    period.Severity, period.Target, period.SubjectKind,
                    period.StartedAt
                FROM mesingest.DemandSeriesErrorPeriods AS period
                INNER JOIN mesingest.DemandSeriesEvents AS opened
                    ON opened.EventId = period.OpenedEventId
                INNER JOIN mesingest.ProjectionCommits AS openedCommit
                    ON openedCommit.ProjectionCommitId = opened.ProjectionCommitId
                LEFT JOIN mesingest.DemandSeriesEvents AS closed
                    ON closed.EventId = period.ClosedEventId
                LEFT JOIN mesingest.ProjectionCommits AS closedCommit
                    ON closedCommit.ProjectionCommitId = closed.ProjectionCommitId
                WHERE openedCommit.ProjectionSequence <= @snapshotSequence
                  AND (closedCommit.ProjectionSequence IS NULL
                       OR closedCommit.ProjectionSequence > @snapshotSequence)
            )
            SELECT active.SeriesId, series.WorkType, active.ErrorCode,
                active.Severity, active.Target, active.SubjectKind,
                active.StartedAt, active.PeriodId,
                evidence.EvidenceId, evidence.DemandId,
                evidence.PollTraceId, evidence.ProjectionCommitId,
                evidenceCommit.ProjectionSequence
            FROM ActivePeriods AS active
            INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = active.SeriesId
            OUTER APPLY
            (
                SELECT TOP (1) candidate.EvidenceId, candidate.DemandId,
                    candidate.PollTraceId, candidate.ProjectionCommitId
                FROM mesingest.SeriesErrorPeriodEvidence AS candidate
                INNER JOIN mesingest.ProjectionCommits AS candidateCommit
                    ON candidateCommit.ProjectionCommitId = candidate.ProjectionCommitId
                WHERE candidate.PeriodId = active.PeriodId
                  AND candidateCommit.ProjectionSequence <= @snapshotSequence
                ORDER BY candidateCommit.ProjectionSequence DESC, candidate.EvidenceId DESC
            ) AS evidence
            LEFT JOIN mesingest.ProjectionCommits AS evidenceCommit
                ON evidenceCommit.ProjectionCommitId = evidence.ProjectionCommitId
            ORDER BY active.SeriesId, active.ErrorCode, active.Target, active.SubjectKind;
            """;
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshot.ProjectionSequence;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var seriesId = reader.GetString(0);
            var workType = reader.GetString(1);
            var code = reader.GetString(2);
            var target = reader.GetString(4);
            var subject = reader.GetString(5);
            var stable = $"{seriesId}:{code}:{target}:{subject}";
            items.Add(new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.SeriesError,
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(6).ToUniversalTime(),
                stable,
                seriesId,
                workType,
                code,
                target,
                subject,
                new CurrentIngestAttentionEvidenceSnapshot(
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetInt64(12),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    SeriesId: seriesId,
                    DemandId: reader.IsDBNull(9) ? null : reader.GetString(9),
                    WorkType: workType,
                    EvidenceId: reader.IsDBNull(8) ? reader.GetString(7) : reader.GetString(8)),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.ErrorSearch,
                    ErrorActivityStates: [ErrorSearchActivityStates.Active],
                    ErrorWindow: ErrorSearchWindowKinds.AllHistory,
                    SeriesId: seriesId)));
        }
    }

    private static async Task ReadTaskProtectionAttentionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        ICollection<CurrentIngestAttentionItemSnapshot> items,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH LatestDecision AS
            (
                SELECT decision.WorkType, decision.PhaseAfter,
                    commitRow.ProjectionCommitId, commitRow.ProjectionSequence,
                    commitRow.PollTraceId, commitRow.CommittedAt,
                    ROW_NUMBER() OVER
                        (PARTITION BY decision.WorkType ORDER BY commitRow.ProjectionSequence DESC) AS rn
                FROM mesingest.ProjectionCommitTaskTypeProtectionDecisions AS decision
                INNER JOIN mesingest.ProjectionCommits AS commitRow
                    ON commitRow.ProjectionCommitId = decision.ProjectionCommitId
                WHERE commitRow.ProjectionSequence <= @snapshotSequence
            )
            SELECT latest.WorkType, latest.PhaseAfter, latest.ProjectionCommitId,
                latest.ProjectionSequence, latest.PollTraceId,
                COALESCE(eventRow.OccurredAt, latest.CommittedAt), eventRow.EventId
            FROM LatestDecision AS latest
            OUTER APPLY
            (
                SELECT TOP (1) eventCandidate.OccurredAt, eventCandidate.EventId
                FROM mesingest.TaskTypeProtectionEvents AS eventCandidate
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = eventCandidate.ProjectionCommitId
                WHERE eventCandidate.WorkType = latest.WorkType
                  AND eventCommit.ProjectionSequence <= @snapshotSequence
                ORDER BY eventCommit.ProjectionSequence DESC,
                    eventCandidate.WorkTypeSequence DESC
            ) AS eventRow
            WHERE latest.rn = 1 AND latest.PhaseAfter <> N'MONITORING'
            ORDER BY latest.WorkType;
            """;
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshot.ProjectionSequence;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var workType = reader.GetString(0);
            var phase = reader.GetString(1);
            items.Add(new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.TaskTypeProtection,
                CurrentIngestAttentionSeverities.Warning,
                reader.GetFieldValue<DateTimeOffset>(5).ToUniversalTime(),
                $"TASK_TYPE_PROTECTION:{workType}",
                SeriesId: null,
                workType,
                ErrorCode: null,
                Target: null,
                SubjectKind: "WORK_TYPE",
                new CurrentIngestAttentionEvidenceSnapshot(
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    WorkType: workType,
                    EvidenceId: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Phase: phase),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.TaskTypeProtection,
                    WorkType: workType)));
        }
    }

    private static async Task ReadUnassignedAttentionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        ICollection<CurrentIngestAttentionItemSnapshot> items,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT observation.Ordinal, observation.WorkType, observation.Sublot,
                observation.Area, observation.Eqp, observation.Step,
                observation.MesSourceDate, observation.Package,
                fact.ContentDigest, stateEvent.EventId, stateEvent.OccurredAt
            FROM mesingest.DemandRawObservations AS observation
            INNER JOIN mesingest.ProjectionCommitUnassignedObservationFacts AS fact
                ON fact.ProjectionCommitId = observation.ProjectionCommitId
            OUTER APPLY
            (
                SELECT TOP (1) candidate.EventId, candidate.EventType, candidate.OccurredAt
                FROM mesingest.UnassignedMesObservationEvents AS candidate
                INNER JOIN mesingest.ProjectionCommits AS candidateCommit
                    ON candidateCommit.ProjectionCommitId = candidate.ProjectionCommitId
                WHERE candidateCommit.ProjectionSequence <= @snapshotSequence
                ORDER BY candidateCommit.ProjectionSequence DESC, candidate.EventId
            ) AS stateEvent
            WHERE observation.ProjectionCommitId = @projectionCommitId
              AND observation.SeriesId IS NULL
              AND observation.DemandId IS NULL
              AND stateEvent.EventType <> N'UNASSIGNED_MES_OBSERVATION_CLEARED'
            ORDER BY observation.Ordinal;
            """;
        AddNVarChar(command, "@projectionCommitId", 64, snapshot.ProjectionCommitId);
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshot.ProjectionSequence;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var duplicateOrdinalByDigest = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var observation = new MesTaskUnionObservation(
                GetNullableString(reader, 1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableDateTimeOffset(reader, 6),
                GetNullableString(reader, 7));
            var rowDigest = MesTaskUnionRoundDigest.Compute([observation]);
            var duplicateOrdinal = duplicateOrdinalByDigest.GetValueOrDefault(rowDigest);
            duplicateOrdinalByDigest[rowDigest] = duplicateOrdinal + 1;
            var stable = $"UNASSIGNED_MES_OBSERVATION:{rowDigest}:{duplicateOrdinal.ToString(CultureInfo.InvariantCulture)}";
            items.Add(new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.UnassignedMesObservation,
                CurrentIngestAttentionSeverities.Error,
                reader.GetFieldValue<DateTimeOffset>(10).ToUniversalTime(),
                stable,
                SeriesId: null,
                observation.WorkType,
                ErrorCode: null,
                Target: null,
                SubjectKind: "RAW_OBSERVATION",
                new CurrentIngestAttentionEvidenceSnapshot(
                    snapshot.ProjectionCommitId,
                    snapshot.ProjectionSequence,
                    snapshot.PollTraceId,
                    SeriesId: null,
                    DemandId: null,
                    WorkType: observation.WorkType,
                    ObservationOrdinal: reader.GetInt32(0),
                    EvidenceId: reader.GetString(9),
                    ContentDigest: reader.GetString(8)),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.PollTrace,
                    PollTraceId: snapshot.PollTraceId)));
        }
    }

    private static async Task ReadPollFailureAttentionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        ICollection<CurrentIngestAttentionItemSnapshot> items,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT TOP (1) PollTraceId, PollTraceSequence, Outcome, CompletedAt
            FROM mesingest.PollTraces
            WHERE PollTraceSequence <= @pollTraceHighWater
            ORDER BY PollTraceSequence DESC;
            """;
        command.Parameters.Add("@pollTraceHighWater", SqlDbType.BigInt).Value = snapshot.PollTraceHighWater;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || string.Equals(reader.GetString(2), SuccessOutcome, StringComparison.Ordinal))
        {
            return;
        }

        var pollTraceId = reader.GetString(0);
        var pollSequence = reader.GetInt64(1);
        var outcome = reader.GetString(2);
        items.Add(new CurrentIngestAttentionItemSnapshot(
            CurrentIngestAttentionKinds.PollRunFailure,
            CurrentIngestAttentionSeverities.Error,
            reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime(),
            "POLL_RUN_FAILURE",
            SeriesId: null,
            WorkType: null,
            ErrorCode: null,
            Target: null,
            SubjectKind: "POLL_TRACE",
            new CurrentIngestAttentionEvidenceSnapshot(
                snapshot.ProjectionCommitId,
                snapshot.ProjectionSequence,
                pollTraceId,
                pollSequence,
                Outcome: outcome),
            new OverviewNavigationIntent(
                OverviewNavigationTargets.PollTrace,
                PollTraceId: pollTraceId)));
    }

    private static int SeverityRank(string severity) => severity switch
    {
        CurrentIngestAttentionSeverities.Error => 0,
        CurrentIngestAttentionSeverities.Warning => 1,
        _ => 2,
    };
}
