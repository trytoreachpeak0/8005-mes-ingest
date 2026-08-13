using System.Data;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    public async Task<WatchOverviewSnapshot> ReadWatchOverviewAsync(
        WatchOverviewQuery query,
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
            await _overviewReadBoundaryObserver.OnFenceSelectedAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            var areas = query.MesAreas ?? Array.Empty<string>();

            var seriesPage = await ReadBrowsePageAsync(
                connection,
                transaction,
                snapshot.ProjectionSequence,
                new DemandSeriesBrowseFilter { MesAreas = areas },
                pageNumber: 1,
                pageSize: 1,
                afterStartedAt: null,
                afterSeriesId: null,
                cancellationToken).ConfigureAwait(false);

            var auditPage = await ReadAuditPageAsync(
                connection,
                transaction,
                snapshot.ProjectionSequence,
                new ReadabilityAuditFilter { MesAreas = areas },
                pageNumber: 1,
                pageSize: 1,
                cursor: null,
                cancellationToken).ConfigureAwait(false);

            var errorSnapshot = new ErrorSearchSnapshotReference(
                new ErrorSearchSnapshotIdentity(
                    snapshot.SnapshotAsOf,
                    snapshot.ProjectionCommitId,
                    snapshot.ProjectionSequence,
                    snapshot.ProjectionCommittedAt,
                    snapshot.PollTraceId),
                new ErrorSearchFilter(),
                ErrorSearchWindowSelection.Last7Days.Resolve(snapshot.SnapshotAsOf),
                ErrorSearchOrder.Default);
            var errorPage = await ReadErrorSearchPageAsync(
                connection,
                transaction,
                errorSnapshot,
                pageSize: 1,
                cursor: null,
                cancellationToken).ConfigureAwait(false);

            var attention = await ReadCurrentAttentionAtFenceAsync(
                connection,
                transaction,
                snapshot,
                new CurrentIngestAttentionQuery(PageSize: 1),
                cancellationToken).ConfigureAwait(false);
            var activity = await ReadOverviewActivityAsync(
                connection,
                transaction,
                snapshot,
                cancellationToken).ConfigureAwait(false);

            var seriesNavigation = SeriesIntent(areas);
            var readabilityNavigation = ReadabilityIntent(areas);
            var activeErrorIntent = new OverviewNavigationIntent(
                OverviewNavigationTargets.ErrorSearch,
                ErrorActivityStates: [ErrorSearchActivityStates.Active],
                ErrorWindow: ErrorSearchWindowKinds.Last7Days);
            var recentErrorIntent = new OverviewNavigationIntent(
                OverviewNavigationTargets.ErrorSearch,
                ErrorActivityStates: [ErrorSearchActivityStates.Active, ErrorSearchActivityStates.Ended],
                ErrorWindow: ErrorSearchWindowKinds.Last7Days);
            var attentionIntent = new OverviewNavigationIntent(
                OverviewNavigationTargets.CurrentIngestAttention);
            var activeSeriesCount = errorPage.Facets.ActivityStates
                .Single(facet => string.Equals(
                    facet.State,
                    ErrorSearchActivityStates.Active,
                    StringComparison.Ordinal))
                .SeriesCount;
            var readableCount = auditPage.Facets.ReadabilityStates
                .Single(facet => string.Equals(
                    facet.State,
                    ExternalReadabilityStates.Readable,
                    StringComparison.Ordinal))
                .DemandCount;
            var notReadableCount = auditPage.Facets.ReadabilityStates
                .Single(facet => string.Equals(
                    facet.State,
                    ExternalReadabilityStates.NotReadable,
                    StringComparison.Ordinal))
                .DemandCount;

            var result = new WatchOverviewSnapshot(
                snapshot,
                areas,
                new WatchOverviewSeriesSummary(
                    seriesPage.ExactTotalCount,
                    seriesPage.Facets.TrackingCount,
                    seriesPage.Facets.ArchivedCount,
                    seriesPage.Facets.GoneCount,
                    seriesPage.Facets.LongGoneButVisibleCount,
                    seriesNavigation,
                    SeriesIntent(areas, lifecycles: [DemandSeriesLifecycleContract.Tracking]),
                    SeriesIntent(areas, lifecycles: [DemandSeriesLifecycleContract.Archived]),
                    SeriesIntent(areas, presences: [DemandSeriesLifecycleContract.Gone]),
                    SeriesIntent(areas, presences: [DemandSeriesLifecycleContract.LongGoneButVisible])),
                new WatchOverviewReadabilitySummary(
                    auditPage.ExactTotalDemandCount,
                    readableCount,
                    notReadableCount,
                    readabilityNavigation,
                    ReadabilityIntent(areas, [ExternalReadabilityStates.Readable]),
                    ReadabilityIntent(areas, [ExternalReadabilityStates.NotReadable])),
                new WatchOverviewErrorSummary(
                    activeSeriesCount,
                    errorPage.ExactTotalSeriesCount,
                    activeErrorIntent,
                    activeErrorIntent,
                    recentErrorIntent),
                new WatchOverviewAttentionSummary(
                    attention.ExactTotalItemCount,
                    attention.Facets.Types.Select(facet => new OverviewFacetSnapshot(
                        facet.Value,
                        facet.ItemCount,
                        attentionIntent with { AttentionKinds = [facet.Value] })).ToArray(),
                    attention.Facets.Severities.Select(facet => new OverviewFacetSnapshot(
                        facet.Value,
                        facet.ItemCount,
                        attentionIntent with { AttentionSeverities = [facet.Value] })).ToArray(),
                    attentionIntent),
                activity,
                activity.Count == 0
                    ? WatchOverviewRecentActivityStates.NoRecentHighlights
                    : WatchOverviewRecentActivityStates.HasRecentHighlights,
                activity.Count == 0
                    ? WatchOverviewRecentActivityStates.NoRecentHighlightsMessage
                    : null);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (CurrentIngestAttentionException exception)
            when (string.Equals(
                exception.Code,
                CurrentIngestAttentionErrorCodes.ProjectionNotAvailable,
                StringComparison.Ordinal))
        {
            var translated = new WatchOverviewException(
                WatchOverviewErrorCodes.ProjectionNotAvailable,
                exception.Message);
            await transaction.RollbackBestEffortAsync(translated).ConfigureAwait(false);
            throw translated;
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private static OverviewNavigationIntent SeriesIntent(
        IReadOnlyList<string> areas,
        IReadOnlyList<string>? lifecycles = null,
        IReadOnlyList<string>? presences = null) =>
        new(
            OverviewNavigationTargets.DemandSeries,
            MesAreas: areas,
            Lifecycles: lifecycles ?? Array.Empty<string>(),
            CurrentPresences: presences ?? Array.Empty<string>());

    private static OverviewNavigationIntent ReadabilityIntent(
        IReadOnlyList<string> areas,
        IReadOnlyList<string>? states = null) =>
        new(
            OverviewNavigationTargets.ReadabilityAudit,
            MesAreas: areas,
            ReadabilityStates: states ?? Array.Empty<string>());

    private static async Task<IReadOnlyList<WatchOverviewActivitySnapshot>> ReadOverviewActivityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        CancellationToken cancellationToken)
    {
        var candidates = new List<WatchOverviewActivitySnapshot>();
        await ReadSeriesOverviewActivityAsync(
            connection,
            transaction,
            snapshot,
            candidates,
            cancellationToken).ConfigureAwait(false);
        await ReadTaskProtectionOverviewActivityAsync(
            connection,
            transaction,
            snapshot,
            candidates,
            cancellationToken).ConfigureAwait(false);
        await ReadUnassignedOverviewActivityAsync(
            connection,
            transaction,
            snapshot,
            candidates,
            cancellationToken).ConfigureAwait(false);
        await ReadPollOverviewActivityAsync(
            connection,
            transaction,
            snapshot,
            candidates,
            cancellationToken).ConfigureAwait(false);

        return candidates
            .Where(item => item.OccurredAt >= snapshot.SnapshotAsOf.AddHours(-24)
                           && item.OccurredAt < snapshot.SnapshotAsOf)
            .OrderByDescending(item => item.OccurredAt)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .Take(5)
            .ToArray();
    }

    private static async Task ReadSeriesOverviewActivityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        ICollection<WatchOverviewActivitySnapshot> candidates,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT eventRow.EventId, eventRow.EventType, eventRow.OccurredAt,
                eventRow.SeriesId, series.WorkType, eventRow.PollTraceId,
                eventRow.ProjectionCommitId
            FROM mesingest.DemandSeriesEvents AS eventRow
            INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = eventRow.SeriesId
            INNER JOIN mesingest.ProjectionCommits AS commitRow
                ON commitRow.ProjectionCommitId = eventRow.ProjectionCommitId
            WHERE commitRow.ProjectionSequence <= @snapshotSequence
              AND eventRow.OccurredAt >= @fromUtc
              AND eventRow.OccurredAt < @toUtc
              AND (
                    eventRow.EventType IN
                        (N'DEMAND_SERIES_STARTED', N'DEMAND_GONE', N'GONE_TIMEOUT_ARCHIVED',
                         N'SERIES_ERROR_PERIOD_STARTED', N'SERIES_ERROR_PERIOD_ENDED')
                    OR (
                        eventRow.EventType = N'TRANSPORT_DEMAND_CREATED'
                        AND JSON_VALUE(eventRow.Payload, '$.predecessorDemandId') IS NOT NULL
                    )
                  )
            ORDER BY eventRow.OccurredAt DESC, eventRow.EventId;
            """;
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshot.ProjectionSequence;
        AddDateTimeOffset(command, "@fromUtc", snapshot.SnapshotAsOf.AddHours(-24));
        AddDateTimeOffset(command, "@toUtc", snapshot.SnapshotAsOf);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var eventType = reader.GetString(1);
            var seriesId = reader.GetString(3);
            candidates.Add(new WatchOverviewActivitySnapshot(
                reader.GetString(0),
                eventType.StartsWith("SERIES_ERROR_", StringComparison.Ordinal)
                    ? "SERIES_ERROR_PERIOD"
                    : "SERIES_LIFECYCLE",
                eventType,
                eventType.StartsWith("SERIES_ERROR_", StringComparison.Ordinal)
                    ? CurrentIngestAttentionSeverities.Error
                    : CurrentIngestAttentionSeverities.Warning,
                reader.GetFieldValue<DateTimeOffset>(2).ToUniversalTime(),
                seriesId,
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeriesDetail,
                    SeriesId: seriesId)));
        }
    }

    private static async Task ReadTaskProtectionOverviewActivityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        ICollection<WatchOverviewActivitySnapshot> candidates,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT eventRow.EventId, eventRow.EventType, eventRow.OccurredAt,
                eventRow.WorkType, eventRow.PollTraceId, eventRow.ProjectionCommitId
            FROM mesingest.TaskTypeProtectionEvents AS eventRow
            INNER JOIN mesingest.ProjectionCommits AS commitRow
                ON commitRow.ProjectionCommitId = eventRow.ProjectionCommitId
            WHERE commitRow.ProjectionSequence <= @snapshotSequence
              AND eventRow.OccurredAt >= @fromUtc AND eventRow.OccurredAt < @toUtc;
            """;
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshot.ProjectionSequence;
        AddDateTimeOffset(command, "@fromUtc", snapshot.SnapshotAsOf.AddHours(-24));
        AddDateTimeOffset(command, "@toUtc", snapshot.SnapshotAsOf);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var workType = reader.GetString(3);
            candidates.Add(new WatchOverviewActivitySnapshot(
                reader.GetString(0),
                CurrentIngestAttentionKinds.TaskTypeProtection,
                reader.GetString(1),
                CurrentIngestAttentionSeverities.Warning,
                reader.GetFieldValue<DateTimeOffset>(2).ToUniversalTime(),
                SeriesId: null,
                workType,
                reader.GetString(4),
                reader.GetString(5),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.TaskTypeProtection,
                    WorkType: workType)));
        }
    }

    private static async Task ReadUnassignedOverviewActivityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        ICollection<WatchOverviewActivitySnapshot> candidates,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT eventRow.EventId, eventRow.EventType, eventRow.OccurredAt,
                commitRow.PollTraceId, eventRow.ProjectionCommitId
            FROM mesingest.UnassignedMesObservationEvents AS eventRow
            INNER JOIN mesingest.ProjectionCommits AS commitRow
                ON commitRow.ProjectionCommitId = eventRow.ProjectionCommitId
            WHERE commitRow.ProjectionSequence <= @snapshotSequence
              AND eventRow.OccurredAt >= @fromUtc AND eventRow.OccurredAt < @toUtc;
            """;
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshot.ProjectionSequence;
        AddDateTimeOffset(command, "@fromUtc", snapshot.SnapshotAsOf.AddHours(-24));
        AddDateTimeOffset(command, "@toUtc", snapshot.SnapshotAsOf);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var pollTraceId = reader.GetString(3);
            candidates.Add(new WatchOverviewActivitySnapshot(
                reader.GetString(0),
                CurrentIngestAttentionKinds.UnassignedMesObservation,
                reader.GetString(1),
                CurrentIngestAttentionSeverities.Error,
                reader.GetFieldValue<DateTimeOffset>(2).ToUniversalTime(),
                SeriesId: null,
                WorkType: null,
                pollTraceId,
                reader.GetString(4),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.PollTrace,
                    PollTraceId: pollTraceId)));
        }
    }

    private static async Task ReadPollOverviewActivityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        ICollection<WatchOverviewActivitySnapshot> candidates,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH Traces AS
            (
                SELECT PollTraceId, PollTraceSequence, Outcome, CompletedAt,
                    LAG(Outcome) OVER (ORDER BY PollTraceSequence) AS PreviousOutcome
                FROM mesingest.PollTraces
                WHERE PollTraceSequence <= @pollTraceHighWater
            )
            SELECT trace.PollTraceId, trace.PollTraceSequence, trace.Outcome,
                trace.PreviousOutcome, trace.CompletedAt, commitRow.ProjectionCommitId
            FROM Traces AS trace
            LEFT JOIN mesingest.ProjectionCommits AS commitRow
                ON commitRow.PollTraceId = trace.PollTraceId
            WHERE CompletedAt >= @fromUtc AND CompletedAt < @toUtc
              AND ((Outcome <> N'SUCCESS'
                    AND (PreviousOutcome IS NULL OR PreviousOutcome = N'SUCCESS'))
                   OR (Outcome = N'SUCCESS' AND PreviousOutcome <> N'SUCCESS'));
            """;
        command.Parameters.Add("@pollTraceHighWater", SqlDbType.BigInt).Value = snapshot.PollTraceHighWater;
        AddDateTimeOffset(command, "@fromUtc", snapshot.SnapshotAsOf.AddHours(-24));
        AddDateTimeOffset(command, "@toUtc", snapshot.SnapshotAsOf);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var pollTraceId = reader.GetString(0);
            var recovered = string.Equals(reader.GetString(2), SuccessOutcome, StringComparison.Ordinal);
            candidates.Add(new WatchOverviewActivitySnapshot(
                $"POLL:{reader.GetInt64(1).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                CurrentIngestAttentionKinds.PollRunFailure,
                recovered ? "POLL_RUN_RECOVERED" : "POLL_RUN_FAILED",
                recovered
                    ? CurrentIngestAttentionSeverities.Warning
                    : CurrentIngestAttentionSeverities.Error,
                reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime(),
                SeriesId: null,
                WorkType: null,
                pollTraceId,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.PollTrace,
                    PollTraceId: pollTraceId)));
        }
    }
}
