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
        OperationalSnapshotIdentity snapshot;
        CurrentIngestAttentionSnapshot result;
        try
        {
            await AcquireCommitRoundReadFenceLockAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            snapshot = await SelectOperationalSnapshotAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            await _readBoundaryObserver.OnFenceSelectedAsync(
                ProjectionReadSurface.CurrentIngestAttention,
                new ProjectionReadFence(
                    _historyEpoch,
                    snapshot.ProjectionCommitId,
                    snapshot.ProjectionSequence,
                    snapshot.CatalogRevision,
                    snapshot.PollTraceHighWater),
                cancellationToken).ConfigureAwait(false);
            result = await ReadCurrentAttentionAtFenceAsync(
                connection,
                transaction,
                snapshot,
                query,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }

        return result;
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
            _timeProvider.GetUtcNow().ToUniversalTime(),
            HistoryEpoch: _historyEpoch);
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
            items,
            cancellationToken).ConfigureAwait(false);
        await ReadTaskProtectionAttentionAsync(
            connection,
            transaction,
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
        var historyCleanup = await ReadHistoryCleanupAttentionAsync(
            connection,
            transaction,
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
            page,
            historyCleanup);
    }

    private static async Task ReadSeriesErrorAttentionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ICollection<CurrentIngestAttentionItemSnapshot> items,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT currentCondition.SeriesId, series.WorkType, currentCondition.ErrorCode,
                period.Severity, currentCondition.Target, currentCondition.SubjectKind,
                period.StartedAt, currentCondition.PeriodId,
                evidence.EvidenceId, evidence.DemandId,
                evidence.PollTraceId, evidence.ProjectionCommitId,
                evidenceCommit.ProjectionSequence
            FROM mesingest.DemandSeriesCurrentConditions AS currentCondition
            INNER JOIN mesingest.DemandSeries AS series
                ON series.SeriesId = currentCondition.SeriesId
            INNER JOIN mesingest.DemandSeriesErrorPeriods AS period
                ON period.PeriodId = currentCondition.PeriodId
            INNER JOIN mesingest.SeriesErrorPeriodEvidence AS evidence
                ON evidence.EvidenceId = currentCondition.LatestEvidenceId
            INNER JOIN mesingest.ProjectionCommits AS evidenceCommit
                ON evidenceCommit.ProjectionCommitId = evidence.ProjectionCommitId
            ORDER BY currentCondition.SeriesId, currentCondition.ErrorCode,
                currentCondition.Target, currentCondition.SubjectKind;
            """;
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
        ICollection<CurrentIngestAttentionItemSnapshot> items,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT state.WorkType, state.Phase, state.LatestProjectionCommitId,
                commitRow.ProjectionSequence, state.LatestPollTraceId,
                state.EnteredAt, eventRow.EventId
            FROM mesingest.TaskTypeProtectionStates AS state
            INNER JOIN mesingest.ProjectionCommits AS commitRow
                ON commitRow.ProjectionCommitId = state.LatestProjectionCommitId
            LEFT JOIN mesingest.TaskTypeProtectionEvents AS eventRow
                ON eventRow.WorkType = state.WorkType
               AND eventRow.WorkTypeSequence = state.LastSequence
            WHERE state.Phase <> N'MONITORING'
            ORDER BY state.WorkType;
            """;
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
            SELECT fact.ObservationCount, fact.ContentDigest,
                fact.StateEventId, fact.StateChangedAt
            FROM mesingest.ProjectionCommitUnassignedObservationFacts AS fact
            WHERE fact.ProjectionCommitId = @projectionCommitId
              AND fact.ObservationCount > 0;
            """;
        AddNVarChar(command, "@projectionCommitId", 64, snapshot.ProjectionCommitId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var observationCount = reader.GetInt32(0);
            var contentDigest = reader.GetString(1);
            items.Add(new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.UnassignedMesObservation,
                CurrentIngestAttentionSeverities.Error,
                reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime(),
                $"UNASSIGNED_MES_OBSERVATION:{contentDigest}",
                SeriesId: null,
                WorkType: null,
                ErrorCode: null,
                Target: null,
                SubjectKind: "UNASSIGNED_OBSERVATION_SET",
                new CurrentIngestAttentionEvidenceSnapshot(
                    snapshot.ProjectionCommitId,
                    snapshot.ProjectionSequence,
                    snapshot.PollTraceId,
                    SeriesId: null,
                    DemandId: null,
                    ObservationCount: observationCount,
                    EvidenceId: reader.GetString(2),
                    ContentDigest: contentDigest),
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

    private static async Task<HistoryCleanupStateSnapshot> ReadHistoryCleanupAttentionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ICollection<CurrentIngestAttentionItemSnapshot> items,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                HistoryCleanupStatus, HistoryCleanupRunId,
                HistoryCleanupLastStartedAt, HistoryCleanupLastCompletedAt,
                HistoryCleanupLastSuccessfulAt, HistoryCleanupNextCheckAt,
                HistoryCleanupLastExpiredPollTraceCount,
                HistoryCleanupLastDeletedRawObservationCount,
                HistoryCleanupLastDeletedSeriesCount,
                HistoryCleanupTotalExpiredPollTraceCount,
                HistoryCleanupTotalDeletedRawObservationCount,
                HistoryCleanupTotalDeletedSeriesCount,
                EarliestAvailableHostUtc,
                HistoryCleanupLastFailureCode, HistoryCleanupLastFailureReason
            FROM mesingest.SchemaInfo
            WHERE Id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The history cleanup state row is missing.");
        }

        var state = new HistoryCleanupStateSnapshot(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            ReadNullableDateTimeOffset(reader, 2),
            ReadNullableDateTimeOffset(reader, 3),
            ReadNullableDateTimeOffset(reader, 4),
            ReadNullableDateTimeOffset(reader, 5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            ReadNullableDateTimeOffset(reader, 12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
        if (state.LastFailureCode is null)
        {
            return state;
        }

        items.Add(new CurrentIngestAttentionItemSnapshot(
            CurrentIngestAttentionKinds.HistoryCleanupFailure,
            CurrentIngestAttentionSeverities.Error,
            state.LastCompletedAt ?? state.LastStartedAt
                ?? throw new InvalidOperationException(
                    "A history cleanup failure must have an occurrence time."),
            "HISTORY_CLEANUP_FAILURE",
            SeriesId: null,
            WorkType: null,
            ErrorCode: state.LastFailureCode,
            Target: null,
            SubjectKind: "HISTORY_CLEANUP",
            new CurrentIngestAttentionEvidenceSnapshot(
                EvidenceId: state.RunId,
                Phase: state.Status,
                FailureReason: state.LastFailureReason,
                NextCheckAt: state.NextCheckAt),
            new OverviewNavigationIntent(
                OverviewNavigationTargets.CurrentIngestAttention,
                AttentionKinds: [CurrentIngestAttentionKinds.HistoryCleanupFailure],
                AttentionSeverities: [CurrentIngestAttentionSeverities.Error])));
        return state;
    }

    private static int SeverityRank(string severity) => severity switch
    {
        CurrentIngestAttentionSeverities.Error => 0,
        CurrentIngestAttentionSeverities.Warning => 1,
        _ => 2,
    };
}
