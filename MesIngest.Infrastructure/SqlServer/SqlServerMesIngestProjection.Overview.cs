using System.Data;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    private static readonly string GlobalOverviewAreaKey = $"G{new string('0', 64)}";

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
            IsolationLevel.Snapshot,
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

            var seriesPage = await ReadCurrentDemandSeriesPageAsync(
                connection,
                transaction,
                new DemandSeriesBrowseFilter { MesAreas = areas },
                pageNumber: 1,
                pageSize: 1,
                cancellationToken).ConfigureAwait(false);

            var readability = await ReadCurrentOverviewReadabilityAsync(
                connection,
                transaction,
                snapshot,
                areas,
                cancellationToken).ConfigureAwait(false);

            var errorSummary = await ReadCurrentOverviewErrorSummaryAsync(
                connection,
                transaction,
                snapshot,
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
                    readability.ExactTotalDemandCount,
                    readability.ReadableCount,
                    readability.NotReadableCount,
                    readabilityNavigation,
                    ReadabilityIntent(areas, [ExternalReadabilityStates.Readable]),
                    ReadabilityIntent(areas, [ExternalReadabilityStates.NotReadable])),
                new WatchOverviewErrorSummary(
                    errorSummary.ActiveSeriesCount,
                    errorSummary.Prior7DaysSeriesCount,
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

    private static async Task<CurrentOverviewReadability> ReadCurrentOverviewReadabilityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        IReadOnlyList<string> areas,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                COALESCE(SUM(fact.ExactTotalDemandCount), 0),
                COALESCE(SUM(fact.ReadableCount), 0)
            FROM mesingest.CurrentOverviewAreaFacts AS fact
            WHERE fact.ProjectionCommitId = @projectionCommitId
              AND ((NOT EXISTS (SELECT 1 FROM OPENJSON(@areasJson))
                    AND fact.IsGlobal = 1)
                   OR (EXISTS (SELECT 1 FROM OPENJSON(@areasJson))
                       AND fact.IsGlobal = 0
                       AND fact.Area IN
                           (SELECT [value] COLLATE Latin1_General_100_BIN2
                            FROM OPENJSON(@areasJson))));
            """;
        AddNVarChar(command, "@projectionCommitId", 64, snapshot.ProjectionCommitId);
        AddNVarChar(command, "@areasJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            areas,
            static message => new WatchOverviewException(
                WatchOverviewErrorCodes.InvalidQuery,
                message)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The current overview readability aggregate is missing.");
        }

        var total = reader.GetInt64(0);
        var readable = reader.GetInt64(1);
        return new CurrentOverviewReadability(total, readable, checked(total - readable));
    }

    private static async Task<CurrentOverviewErrorSummary> ReadCurrentOverviewErrorSummaryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalSnapshotIdentity snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT_BIG(*)
                 FROM mesingest.CurrentOverviewErrorSeriesFacts
                 WHERE ProjectionCommitId = @projectionCommitId
                   AND EarliestActiveStartedAt IS NOT NULL),
                (SELECT COUNT_BIG(*)
                 FROM mesingest.CurrentOverviewErrorSeriesFacts
                 WHERE ProjectionCommitId = @projectionCommitId
                   AND ((EarliestActiveStartedAt IS NOT NULL
                         AND EarliestActiveStartedAt < @toUtc)
                        OR (LatestEndedAt > @fromUtc
                            AND LatestEndedPeriodStartedAt < @toUtc)));
            """;
        AddNVarChar(command, "@projectionCommitId", 64, snapshot.ProjectionCommitId);
        AddDateTimeOffset(command, "@fromUtc", snapshot.SnapshotAsOf.AddDays(-7));
        AddDateTimeOffset(command, "@toUtc", snapshot.SnapshotAsOf);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The current overview error aggregate is missing.");
        }

        return new CurrentOverviewErrorSummary(reader.GetInt64(0), reader.GetInt64(1));
    }

    private sealed record CurrentOverviewReadability(
        long ExactTotalDemandCount,
        long ReadableCount,
        long NotReadableCount);

    private sealed record CurrentOverviewErrorSummary(
        long ActiveSeriesCount,
        long Prior7DaysSeriesCount);

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
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT TOP (5)
                activity.EventId, activity.Kind, activity.EventType, activity.Severity,
                activity.OccurredAt, activity.SeriesId, activity.WorkType,
                activity.PollTraceId, activity.SourceProjectionCommitId,
                activity.NavigationTarget,
                COALESCE(
                    JSON_VALUE(seriesEvent.Payload, '$.code') COLLATE Latin1_General_100_BIN2,
                    openedPeriod.ErrorCode COLLATE Latin1_General_100_BIN2,
                    closedPeriod.ErrorCode COLLATE Latin1_General_100_BIN2),
                COALESCE(
                    seriesEvent.SubjectKind COLLATE Latin1_General_100_BIN2,
                    openedPeriod.SubjectKind COLLATE Latin1_General_100_BIN2,
                    closedPeriod.SubjectKind COLLATE Latin1_General_100_BIN2),
                COALESCE(
                    JSON_VALUE(seriesEvent.Payload, '$.observedValue') COLLATE Latin1_General_100_BIN2,
                    periodEvidence.ObservedValue COLLATE Latin1_General_100_BIN2),
                COALESCE(
                    JSON_VALUE(seriesEvent.Payload, '$.expectedRule') COLLATE Latin1_General_100_BIN2,
                    JSON_VALUE(seriesEvent.Payload, '$.ExpectedRule') COLLATE Latin1_General_100_BIN2,
                    periodEvidence.ExpectedRule COLLATE Latin1_General_100_BIN2),
                COALESCE(
                    JSON_VALUE(seriesEvent.Payload, '$.endReason') COLLATE Latin1_General_100_BIN2,
                    closedPeriod.EndReason COLLATE Latin1_General_100_BIN2),
                pollTrace.DiagnosticSafeDetail
            FROM mesingest.CurrentOverviewActivities AS activity
            LEFT JOIN mesingest.DemandSeriesEvents AS seriesEvent
                ON seriesEvent.EventId = activity.EventId
            LEFT JOIN mesingest.DemandSeriesErrorPeriods AS openedPeriod
                ON openedPeriod.OpenedEventId = activity.EventId
            LEFT JOIN mesingest.DemandSeriesErrorPeriods AS closedPeriod
                ON closedPeriod.ClosedEventId = activity.EventId
            LEFT JOIN mesingest.SeriesErrorPeriodEvidence AS periodEvidence
                ON periodEvidence.EventId = activity.EventId
            LEFT JOIN mesingest.PollTraces AS pollTrace
                ON pollTrace.PollTraceId = activity.PollTraceId
            WHERE activity.SnapshotProjectionCommitId = @projectionCommitId
              AND activity.OccurredAt >= @fromUtc
              AND activity.OccurredAt < @toUtc
            ORDER BY activity.OccurredAt DESC, activity.EventId;
            """;
        AddNVarChar(command, "@projectionCommitId", 64, snapshot.ProjectionCommitId);
        AddDateTimeOffset(command, "@fromUtc", snapshot.SnapshotAsOf.AddHours(-24));
        AddDateTimeOffset(command, "@toUtc", snapshot.SnapshotAsOf);
        var result = new List<WatchOverviewActivitySnapshot>(capacity: 5);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var eventType = reader.GetString(2);
            var explanation = BuildOverviewActivityExplanation(
                GetNullableString(reader, 10),
                GetNullableString(reader, 11),
                GetNullableString(reader, 12),
                GetNullableString(reader, 13),
                GetNullableString(reader, 14),
                GetNullableString(reader, 15));
            result.Add(new WatchOverviewActivitySnapshot(
                reader.GetString(0),
                reader.GetString(1),
                eventType,
                ProjectOverviewActivitySeverity(eventType, reader.GetString(3), explanation?.EndReason),
                reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime(),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                reader.GetString(7),
                GetNullableString(reader, 8),
                BuildOverviewActivityNavigation(
                    reader.GetString(1),
                    reader.GetString(9),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    reader.GetString(7)),
                explanation));
        }

        return result;
    }

    private static WatchOverviewActivityExplanation? BuildOverviewActivityExplanation(
        string? code,
        string? subjectKind,
        string? observedValue,
        string? expectedRule,
        string? endReason,
        string? safeDetail)
    {
        var observationCount = string.Equals(
                code,
                "DUPLICATE_TRANSPORT_DEMAND_KEY",
                StringComparison.Ordinal)
            ? JsonArrayCount(observedValue)
            : null;
        var relatedWorkTypes = string.Equals(
                code,
                "SUBLOT_MULTIPLE_WORK_TYPES",
                StringComparison.Ordinal)
            ? JsonStringArray(observedValue)
            : null;
        var scalarValue = observationCount is null && relatedWorkTypes is null
            ? observedValue
            : null;
        var projectedExpectedRule = string.Equals(
                code,
                "INVALID_MES_FIELD_FORMAT",
                StringComparison.Ordinal)
            && string.Equals(subjectKind, "AREA", StringComparison.Ordinal)
                ? "D7-4"
                : expectedRule;
        return new[] { code, subjectKind, scalarValue, projectedExpectedRule, endReason, safeDetail }
                .All(string.IsNullOrWhiteSpace)
            && observationCount is null
            && relatedWorkTypes is null
                ? null
                : new(
                    code,
                    subjectKind,
                    scalarValue,
                    projectedExpectedRule,
                    endReason,
                    observationCount,
                    relatedWorkTypes,
                    safeDetail);
    }

    private static long? JsonArrayCount(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? JsonStringArray(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToArray()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ProjectOverviewActivitySeverity(
        string eventType,
        string storedSeverity,
        string? endReason) => eventType switch
    {
        "SERIES_ERROR_PERIOD_STARTED" or "POLL_RUN_FAILED"
            or "UNASSIGNED_MES_OBSERVATION_APPEARED"
            or "UNASSIGNED_MES_OBSERVATION_CONTENT_CHANGED" => "ERROR",
        "SERIES_ERROR_PERIOD_ENDED" when string.Equals(
            endReason,
            "CONDITION_CLEARED",
            StringComparison.Ordinal) => "SUCCESS",
        "POLL_RUN_RECOVERED" or "TASK_TYPE_PROTECTION_CLEARED"
            or "TASK_TYPE_ABSENCE_AUTHORITY_RESTORED"
            or "UNASSIGNED_MES_OBSERVATION_CLEARED" => "SUCCESS",
        "DEMAND_SERIES_STARTED" or "TRANSPORT_DEMAND_CREATED" => "INFORMATION",
        "DEMAND_GONE" or "GONE_TIMEOUT_ARCHIVED"
            or "TASK_TYPE_PROTECTION_ENTERED"
            or "TASK_TYPE_PROTECTION_RECOVERY_PROGRESS"
            or "SERIES_ERROR_PERIOD_ENDED" => "WARNING",
        _ => storedSeverity,
    };

    private static OverviewNavigationIntent BuildOverviewActivityNavigation(
        string kind,
        string storedTarget,
        string? seriesId,
        string? workType,
        string pollTraceId) => kind switch
    {
        "SERIES_ERROR_PERIOD" => new(
            OverviewNavigationTargets.ErrorSearch,
            ErrorActivityStates: [
                ErrorSearchActivityStates.Active,
                ErrorSearchActivityStates.Ended,
            ],
            ErrorWindow: ErrorSearchWindowKinds.Last7Days,
            SeriesId: seriesId,
            WorkType: workType,
            PollTraceId: pollTraceId),
        "TASK_TYPE_PROTECTION" => new(
            OverviewNavigationTargets.CurrentIngestAttention,
            AttentionKinds: [CurrentIngestAttentionKinds.TaskTypeProtection],
            WorkType: workType,
            PollTraceId: pollTraceId),
        "UNASSIGNED_MES_OBSERVATION" => new(
            OverviewNavigationTargets.CurrentIngestAttention,
            AttentionKinds: [CurrentIngestAttentionKinds.UnassignedMesObservation],
            PollTraceId: pollTraceId),
        "POLL_RUN_FAILURE" => new(
            OverviewNavigationTargets.CurrentIngestAttention,
            AttentionKinds: [CurrentIngestAttentionKinds.PollRunFailure],
            PollTraceId: pollTraceId),
        _ => new(
            storedTarget,
            SeriesId: seriesId,
            WorkType: workType,
            PollTraceId: pollTraceId),
    };

    private static async Task UpdateCurrentOverviewReadabilityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string projectionCommitId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM mesingest.CurrentOverviewAreaFacts;

            WITH CurrentDemands AS
            (
                SELECT demand.DemandId,
                    CASE
                        WHEN demand.CurrentRawObservationCount = 1 AND
                        (
                            (DATALENGTH(demand.Area) = 8
                             AND demand.Area COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9]-[1-9]')
                            OR (DATALENGTH(demand.Area) = 10
                                AND demand.Area COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9][0-9]-[1-9]')
                            OR (DATALENGTH(demand.Area) = 10
                                AND demand.Area COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9]-[1-9][0-9]')
                            OR (DATALENGTH(demand.Area) = 12
                                AND demand.Area COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9][0-9]-[1-9][0-9]')
                        )
                        THEN demand.Area COLLATE Latin1_General_100_BIN2
                    END AS TrustedArea
                FROM mesingest.TransportDemands AS demand
            )
            INSERT INTO mesingest.CurrentOverviewAreaFacts
                (AreaKey, IsGlobal, Area, ExactTotalDemandCount, ReadableCount,
                 ProjectionCommitId)
            SELECT @globalAreaKey, 1, NULL,
                COUNT_BIG(*),
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN catalogItem.DemandId IS NULL
                    THEN 0 ELSE 1 END)), 0),
                @projectionCommitId
            FROM CurrentDemands AS demand
            LEFT JOIN mesingest.CatalogItems AS catalogItem
                ON catalogItem.DemandId = demand.DemandId
            UNION ALL
            SELECT N'A' + CONVERT(CHAR(64), HASHBYTES(
                    'SHA2_256', CONVERT(VARBINARY(MAX), demand.TrustedArea)), 2),
                0, demand.TrustedArea,
                COUNT_BIG(*),
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN catalogItem.DemandId IS NULL
                    THEN 0 ELSE 1 END)), 0),
                @projectionCommitId
            FROM CurrentDemands AS demand
            LEFT JOIN mesingest.CatalogItems AS catalogItem
                ON catalogItem.DemandId = demand.DemandId
            WHERE demand.TrustedArea IS NOT NULL
            GROUP BY demand.TrustedArea;
            """;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddChar(command, "@globalAreaKey", 65, GlobalOverviewAreaKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateCurrentOverviewActivityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MesTaskUnionRound round,
        string? projectionCommitId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @snapshotProjectionCommitId NVARCHAR(64) = COALESCE(
                @projectionCommitId,
                (SELECT TOP (1) ProjectionCommitId
                 FROM mesingest.ProjectionCommits
                 ORDER BY ProjectionSequence DESC));
            DECLARE @currentPollSequence BIGINT;
            DECLARE @currentOutcome NVARCHAR(16);
            DECLARE @previousOutcome NVARCHAR(16);
            DECLARE @pollCompletedAt DATETIMEOFFSET(7);
            SELECT @currentPollSequence = PollTraceSequence,
                @currentOutcome = Outcome,
                @pollCompletedAt = CompletedAt
            FROM mesingest.PollTraces
            WHERE PollTraceId = @pollTraceId;
            SELECT TOP (1) @previousOutcome = Outcome
            FROM mesingest.PollTraces
            WHERE PollTraceSequence < @currentPollSequence
            ORDER BY PollTraceSequence DESC;

            DECLARE @Candidates TABLE
            (
                EventId NVARCHAR(128) NOT NULL,
                Kind NVARCHAR(64) NOT NULL,
                EventType NVARCHAR(128) NOT NULL,
                Severity NVARCHAR(16) NOT NULL,
                OccurredAt DATETIMEOFFSET(7) NOT NULL,
                SeriesId NVARCHAR(64) NULL,
                WorkType NVARCHAR(128) NULL,
                PollTraceId NVARCHAR(128) NOT NULL,
                SourceProjectionCommitId NVARCHAR(64) NULL,
                NavigationTarget NVARCHAR(64) NOT NULL
            );

            INSERT INTO @Candidates
            SELECT EventId, Kind, EventType, Severity, OccurredAt,
                SeriesId, WorkType, PollTraceId, SourceProjectionCommitId,
                NavigationTarget
            FROM mesingest.CurrentOverviewActivities
            WHERE OccurredAt >= @fromUtc AND OccurredAt <= @toUtc;

            IF @projectionCommitId IS NOT NULL
            BEGIN
                INSERT INTO @Candidates
                SELECT eventRow.EventId,
                    CASE WHEN eventRow.EventType LIKE N'SERIES_ERROR[_]%'
                         THEN N'SERIES_ERROR_PERIOD' ELSE N'SERIES_LIFECYCLE' END,
                    eventRow.EventType,
                    CASE WHEN eventRow.EventType LIKE N'SERIES_ERROR[_]%'
                         THEN N'ERROR' ELSE N'WARNING' END,
                    eventRow.OccurredAt, eventRow.SeriesId, series.WorkType,
                    eventRow.PollTraceId, eventRow.ProjectionCommitId,
                    N'DEMAND_SERIES_DETAIL'
                FROM mesingest.DemandSeriesEvents AS eventRow
                INNER JOIN mesingest.DemandSeries AS series
                    ON series.SeriesId = eventRow.SeriesId
                WHERE eventRow.ProjectionCommitId = @projectionCommitId
                  AND (eventRow.EventType IN
                        (N'DEMAND_SERIES_STARTED', N'DEMAND_GONE', N'GONE_TIMEOUT_ARCHIVED',
                         N'SERIES_ERROR_PERIOD_STARTED', N'SERIES_ERROR_PERIOD_ENDED')
                       OR (eventRow.EventType = N'TRANSPORT_DEMAND_CREATED'
                           AND JSON_VALUE(eventRow.Payload, '$.predecessorDemandId') IS NOT NULL));

                INSERT INTO @Candidates
                SELECT eventRow.EventId, N'TASK_TYPE_PROTECTION', eventRow.EventType,
                    N'WARNING', eventRow.OccurredAt, NULL, eventRow.WorkType,
                    eventRow.PollTraceId, eventRow.ProjectionCommitId,
                    N'TASK_TYPE_PROTECTION'
                FROM mesingest.TaskTypeProtectionEvents AS eventRow
                WHERE eventRow.ProjectionCommitId = @projectionCommitId;

                INSERT INTO @Candidates
                SELECT eventRow.EventId, N'UNASSIGNED_MES_OBSERVATION',
                    eventRow.EventType, N'ERROR', eventRow.OccurredAt, NULL, NULL,
                    @pollTraceId, eventRow.ProjectionCommitId, N'POLL_TRACE'
                FROM mesingest.UnassignedMesObservationEvents AS eventRow
                WHERE eventRow.ProjectionCommitId = @projectionCommitId;
            END;

            IF ((@currentOutcome <> N'SUCCESS'
                    AND (@previousOutcome IS NULL OR @previousOutcome = N'SUCCESS'))
                OR (@currentOutcome = N'SUCCESS' AND @previousOutcome <> N'SUCCESS'))
            BEGIN
                INSERT INTO @Candidates
                VALUES
                (N'POLL:' + CONVERT(NVARCHAR(32), @currentPollSequence),
                 N'POLL_RUN_FAILURE',
                 CASE WHEN @currentOutcome = N'SUCCESS'
                      THEN N'POLL_RUN_RECOVERED' ELSE N'POLL_RUN_FAILED' END,
                 CASE WHEN @currentOutcome = N'SUCCESS' THEN N'WARNING' ELSE N'ERROR' END,
                 @pollCompletedAt, NULL, NULL, @pollTraceId, @projectionCommitId,
                 N'POLL_TRACE');
            END;

            DELETE FROM mesingest.CurrentOverviewActivities;
            WITH Bucketed AS
            (
                SELECT *, ROW_NUMBER() OVER
                    (PARTITION BY CASE WHEN OccurredAt < @toUtc THEN 0 ELSE 1 END
                     ORDER BY OccurredAt DESC, EventId) AS BucketRank
                FROM @Candidates
                WHERE OccurredAt >= @fromUtc AND OccurredAt <= @toUtc
            ),
            Ranked AS
            (
                SELECT *, ROW_NUMBER() OVER
                    (ORDER BY OccurredAt DESC, EventId) AS ActivityRank
                FROM Bucketed
                WHERE BucketRank <= 5
            )
            INSERT INTO mesingest.CurrentOverviewActivities
                (ActivityRank, SnapshotProjectionCommitId, EventId, Kind, EventType,
                 Severity, OccurredAt, SeriesId, WorkType, PollTraceId,
                 SourceProjectionCommitId, NavigationTarget)
            SELECT CONVERT(TINYINT, ActivityRank), @snapshotProjectionCommitId,
                EventId, Kind, EventType, Severity, OccurredAt, SeriesId, WorkType,
                PollTraceId, SourceProjectionCommitId, NavigationTarget
            FROM Ranked;
            """;
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNullableNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddDateTimeOffset(command, "@fromUtc", round.CompletedAt.ToUniversalTime().AddHours(-24));
        AddDateTimeOffset(command, "@toUtc", round.CompletedAt.ToUniversalTime());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
