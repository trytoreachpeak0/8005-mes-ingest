using System.Data;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    public async Task<DemandSeriesListSnapshot> ListDemandSeriesAsync(
        DemandSeriesBrowseQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query.NormalizeAndValidate();
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var signingKey = await ReadSnapshotTokenSigningKeyAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var snapshot = await ResolveSnapshotAsync(
                connection,
                transaction,
                query.SnapshotReference,
                signingKey,
                cancellationToken).ConfigureAwait(false);
            await _readBoundaryObserver.OnFenceSelectedAsync(
                ProjectionReadSurface.DemandSeries,
                new ProjectionReadFence(
                    snapshot.ProjectionCommitId,
                    snapshot.ProjectionSequence),
                cancellationToken).ConfigureAwait(false);

            var pageNumber = query.PageNumber;
            DemandSeriesBrowseCursor? cursor = null;
            if (query.Cursor is not null)
            {
                if (!DemandSeriesSnapshotTokenCodec.TryReadCursor(
                        query.Cursor,
                        snapshot,
                        query.Filter,
                        query.Order,
                        query.PageSize,
                        signingKey,
                        out cursor,
                        out var tokenError))
                {
                    throw new DemandSeriesBrowseException(
                        tokenError!.Code,
                        tokenError.Message);
                }

                pageNumber = cursor!.TargetPageNumber;
            }

            var snapshotReference = query.SnapshotReference
                ?? DemandSeriesSnapshotTokenCodec.CreateSnapshotReference(snapshot, signingKey);
            var page = await ReadBrowsePageAsync(
                connection,
                transaction,
                snapshot.ProjectionSequence,
                query.Filter,
                pageNumber,
                query.PageSize,
                cursor?.AfterStartedAt,
                cursor?.AfterSeriesId,
                cancellationToken).ConfigureAwait(false);
            var totalPages = page.ExactTotalCount == 0
                ? 0
                : checked((int)((page.ExactTotalCount + query.PageSize - 1) / query.PageSize));
            var items = page.States
                .Select(ToListItem)
                .ToArray();
            var hasMore = pageNumber < totalPages;
            var nextCursor = hasMore
                ? DemandSeriesSnapshotTokenCodec.CreateCursor(
                    snapshot,
                    query.Filter,
                    query.Order,
                    query.PageSize,
                    checked(pageNumber + 1),
                    items.Length == 0 ? null : items[^1].StartedAt,
                    items.Length == 0 ? null : items[^1].SeriesId,
                    signingKey)
                : null;

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DemandSeriesListSnapshot(
                snapshot,
                snapshotReference,
                query.Filter,
                query.Order,
                page.ExactTotalCount,
                page.Facets,
                query.PageSize,
                pageNumber,
                totalPages,
                items,
                nextCursor,
                hasMore);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DemandSeriesDetailSnapshot?> GetDemandSeriesAtSnapshotAsync(
        string seriesId,
        string snapshotReference,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(seriesId, nameof(seriesId), 64);
        ValidateRequiredText(snapshotReference, nameof(snapshotReference), 4096);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var signingKey = await ReadSnapshotTokenSigningKeyAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var snapshot = await ResolveSnapshotAsync(
                connection,
                transaction,
                snapshotReference,
                signingKey,
                cancellationToken).ConfigureAwait(false);
            await _readBoundaryObserver.OnFenceSelectedAsync(
                ProjectionReadSurface.DemandSeries,
                new ProjectionReadFence(
                    snapshot.ProjectionCommitId,
                    snapshot.ProjectionSequence),
                cancellationToken).ConfigureAwait(false);
            var series = (await ReadSeriesStatesAsync(
                    connection,
                    transaction,
                    snapshot.ProjectionSequence,
                    seriesId,
                    cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(item => string.Equals(item.SeriesId, seriesId, StringComparison.Ordinal));
            if (series is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var observations = await ReadRawObservationsAsOfAsync(
                connection,
                transaction,
                seriesId,
                snapshot.ProjectionSequence,
                cancellationToken).ConfigureAwait(false);
            var events = await ReadEventsAsOfAsync(
                connection,
                transaction,
                seriesId,
                snapshot.ProjectionSequence,
                cancellationToken).ConfigureAwait(false);
            var errorState = await ReadErrorStateAsOfAsync(
                connection,
                transaction,
                seriesId,
                snapshot.ProjectionSequence,
                cancellationToken).ConfigureAwait(false);
            var demands = await ReadDemandGenerationsAsOfAsync(
                connection,
                transaction,
                series,
                errorState.CurrentConditions,
                snapshot.ProjectionSequence,
                cancellationToken).ConfigureAwait(false);
            var currentDemand = demands.Single(demand =>
                string.Equals(demand.DemandId, series.CurrentDemandId, StringComparison.Ordinal));
            var detail = new DemandSeriesSnapshot(
                series.SeriesId,
                series.WorkType,
                series.Sublot,
                series.Lifecycle,
                series.CurrentPresence,
                series.StartedAt,
                series.CreatedPollTraceId,
                series.CreatedProjectionCommitId,
                series.LatestProjectionCommitId,
                currentDemand,
                demands,
                observations,
                events,
                errorState.CurrentConditions,
                errorState.ErrorPeriods,
                series.ArchivedAt,
                series.LastSeriesSequence);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DemandSeriesDetailSnapshot(snapshot, snapshotReference, detail);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private static DemandSeriesListItemSnapshot ToListItem(BrowseSeriesState state) =>
        new(
            state.SeriesId,
            state.WorkType,
            state.Sublot,
            state.Lifecycle,
            state.CurrentPresence,
            state.StartedAt,
            state.ArchivedAt,
            state.CurrentDemandId,
            state.CurrentGeneration,
            state.CurrentDemandStatus,
            state.DemandLastSeenAt,
            state.GoneConfirmedAt,
            state.LiveMesFields,
            state.ExternalReadabilityState,
            state.ReadabilityBlockers,
            state.LastSeriesSequence,
            state.LatestPollTraceId,
            state.LatestProjectionCommitId);

    private static async Task<BrowsePageState> ReadBrowsePageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long snapshotSequence,
        DemandSeriesBrowseFilter filter,
        int pageNumber,
        int pageSize,
        DateTimeOffset? afterStartedAt,
        string? afterSeriesId,
        CancellationToken cancellationToken)
    {
        var normalized = filter.Normalize();
        var offset = checked((long)(pageNumber - 1) * pageSize);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE #BrowseStates
            (
                SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Sublot NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                StartedAt DATETIMEOFFSET(7) NOT NULL,
                CreatedPollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CreatedProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CurrentDemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CurrentGeneration INT NOT NULL,
                PredecessorDemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
                CurrentDemandCreatedAt DATETIMEOFFSET(7) NOT NULL,
                CurrentDemandCreatedPollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CurrentDemandCreatedProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Lifecycle NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CurrentPresence NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ArchivedAt DATETIMEOFFSET(7) NULL,
                CurrentDemandStatus NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                DemandLastSeenAt DATETIMEOFFSET(7) NOT NULL,
                GoneConfirmedAt DATETIMEOFFSET(7) NULL,
                LatestObservationProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
                LatestObservationPollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NULL,
                LatestObservationCount BIGINT NOT NULL,
                Area NVARCHAR(512) NULL,
                Eqp NVARCHAR(512) NULL,
                Step NVARCHAR(512) NULL,
                MesSourceDate DATETIMEOFFSET(7) NULL,
                Package NVARCHAR(512) NULL,
                LastSeriesSequence BIGINT NOT NULL,
                LatestProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                LatestPollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ConditionCodes NVARCHAR(2048) NULL
            );

            WITH EligibleDemands AS
            (
                SELECT
                    d.DemandId, d.SeriesId, d.Generation, d.PredecessorDemandId,
                    d.CreatedAt, d.CreatedPollTraceId, d.CreatedProjectionCommitId,
                    ROW_NUMBER() OVER
                        (PARTITION BY d.SeriesId ORDER BY d.Generation DESC) AS CurrentRank
                FROM mesingest.TransportDemands AS d
                INNER JOIN mesingest.ProjectionCommits AS createdCommit
                    ON createdCommit.ProjectionCommitId = d.CreatedProjectionCommitId
                WHERE createdCommit.ProjectionSequence <= @snapshotSequence
            ),
            LatestObservations AS
            (
                SELECT
                    d.DemandId,
                    observationCommit.ProjectionCommitId,
                    observationCommit.ProjectionSequence,
                    observationCommit.PollTraceId,
                    observationCommit.CommittedAt,
                    ROW_NUMBER() OVER
                        (PARTITION BY d.DemandId ORDER BY observationCommit.ProjectionSequence DESC) AS ObservationRank
                FROM EligibleDemands AS d
                INNER JOIN mesingest.DemandRawObservations AS o ON o.DemandId = d.DemandId
                INNER JOIN mesingest.ProjectionCommits AS observationCommit
                    ON observationCommit.ProjectionCommitId = o.ProjectionCommitId
                WHERE observationCommit.ProjectionSequence <= @snapshotSequence
                GROUP BY d.DemandId, observationCommit.ProjectionCommitId,
                    observationCommit.ProjectionSequence, observationCommit.PollTraceId,
                    observationCommit.CommittedAt
            ),
            LatestObservationFields AS
            (
                SELECT
                    latest.DemandId, latest.ProjectionCommitId,
                    latest.PollTraceId, latest.CommittedAt,
                    COUNT_BIG(o.Ordinal) AS ObservationCount,
                    MAX(o.Area) AS Area, MAX(o.Eqp) AS Eqp, MAX(o.Step) AS Step,
                    MAX(SWITCHOFFSET(o.MesSourceDate, '+00:00')) AS MesSourceDate,
                    MAX(o.Package) AS Package
                FROM LatestObservations AS latest
                INNER JOIN mesingest.DemandRawObservations AS o
                    ON o.DemandId = latest.DemandId
                   AND o.ProjectionCommitId = latest.ProjectionCommitId
                WHERE latest.ObservationRank = 1
                GROUP BY latest.DemandId, latest.ProjectionCommitId,
                    latest.PollTraceId, latest.CommittedAt
            ),
            EventState AS
            (
                SELECT e.SeriesId, MAX(e.SeriesSequence) AS LastSeriesSequence
                FROM mesingest.DemandSeriesEvents AS e
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = e.ProjectionCommitId
                WHERE eventCommit.ProjectionSequence <= @snapshotSequence
                GROUP BY e.SeriesId
            ),
            ArchiveState AS
            (
                SELECT e.SeriesId, MIN(e.OccurredAt) AS ArchivedAt
                FROM mesingest.DemandSeriesEvents AS e
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = e.ProjectionCommitId
                WHERE eventCommit.ProjectionSequence <= @snapshotSequence
                  AND e.EventType = N'GONE_TIMEOUT_ARCHIVED'
                GROUP BY e.SeriesId
            ),
            GoneState AS
            (
                SELECT e.SubjectId AS DemandId, MAX(e.OccurredAt) AS GoneConfirmedAt
                FROM mesingest.DemandSeriesEvents AS e
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = e.ProjectionCommitId
                WHERE eventCommit.ProjectionSequence <= @snapshotSequence
                  AND e.EventType = N'DEMAND_GONE'
                GROUP BY e.SubjectId
            )
            INSERT INTO #BrowseStates
            SELECT
                s.SeriesId, s.WorkType, s.Sublot, s.StartedAt,
                s.CreatedPollTraceId, s.CreatedProjectionCommitId,
                currentDemand.DemandId, currentDemand.Generation,
                currentDemand.PredecessorDemandId, currentDemand.CreatedAt,
                currentDemand.CreatedPollTraceId, currentDemand.CreatedProjectionCommitId,
                CASE WHEN archive.ArchivedAt IS NULL THEN N'TRACKING' ELSE N'ARCHIVED' END,
                CASE
                    WHEN gone.GoneConfirmedAt IS NOT NULL THEN N'GONE'
                    WHEN archive.ArchivedAt IS NOT NULL THEN N'LONG_GONE_BUT_VISIBLE'
                    ELSE N'VISIBLE'
                END,
                archive.ArchivedAt,
                CASE
                    WHEN gone.GoneConfirmedAt IS NOT NULL THEN N'GONE'
                    WHEN archive.ArchivedAt IS NOT NULL
                         AND currentDemand.CreatedAt >= archive.ArchivedAt
                        THEN N'LONG_GONE_BUT_VISIBLE'
                    ELSE N'VISIBLE'
                END,
                COALESCE(fields.CommittedAt, currentDemand.CreatedAt),
                gone.GoneConfirmedAt,
                fields.ProjectionCommitId, fields.PollTraceId,
                COALESCE(fields.ObservationCount, 0),
                fields.Area, fields.Eqp, fields.Step, fields.MesSourceDate, fields.Package,
                COALESCE(eventState.LastSeriesSequence, 0),
                latest.ProjectionCommitId, latest.PollTraceId,
                conditions.ConditionCodes
            FROM mesingest.DemandSeries AS s
            INNER JOIN mesingest.ProjectionCommits AS seriesCreated
                ON seriesCreated.ProjectionCommitId = s.CreatedProjectionCommitId
            INNER JOIN EligibleDemands AS currentDemand
                ON currentDemand.SeriesId = s.SeriesId AND currentDemand.CurrentRank = 1
            LEFT JOIN LatestObservationFields AS fields ON fields.DemandId = currentDemand.DemandId
            LEFT JOIN EventState AS eventState ON eventState.SeriesId = s.SeriesId
            LEFT JOIN ArchiveState AS archive ON archive.SeriesId = s.SeriesId
            LEFT JOIN GoneState AS gone ON gone.DemandId = currentDemand.DemandId
            OUTER APPLY
            (
                SELECT TOP (1) candidate.ProjectionCommitId, candidate.PollTraceId
                FROM
                (
                    SELECT createdCommit.ProjectionCommitId, createdCommit.PollTraceId,
                           createdCommit.ProjectionSequence
                    FROM EligibleDemands AS demand
                    INNER JOIN mesingest.ProjectionCommits AS createdCommit
                        ON createdCommit.ProjectionCommitId = demand.CreatedProjectionCommitId
                    WHERE demand.SeriesId = s.SeriesId
                    UNION ALL
                    SELECT eventCommit.ProjectionCommitId, eventCommit.PollTraceId,
                           eventCommit.ProjectionSequence
                    FROM mesingest.DemandSeriesEvents AS event
                    INNER JOIN mesingest.ProjectionCommits AS eventCommit
                        ON eventCommit.ProjectionCommitId = event.ProjectionCommitId
                    WHERE event.SeriesId = s.SeriesId
                      AND eventCommit.ProjectionSequence <= @snapshotSequence
                    UNION ALL
                    SELECT observationCommit.ProjectionCommitId, observationCommit.PollTraceId,
                           observationCommit.ProjectionSequence
                    FROM mesingest.DemandRawObservations AS observation
                    INNER JOIN mesingest.ProjectionCommits AS observationCommit
                        ON observationCommit.ProjectionCommitId = observation.ProjectionCommitId
                    WHERE observation.SeriesId = s.SeriesId
                      AND observationCommit.ProjectionSequence <= @snapshotSequence
                ) AS candidate
                ORDER BY candidate.ProjectionSequence DESC
            ) AS latest
            OUTER APPLY
            (
                SELECT STRING_AGG(CONVERT(NVARCHAR(128), active.ErrorCode), NCHAR(31))
                    WITHIN GROUP (ORDER BY active.ErrorCode) AS ConditionCodes
                FROM
                (
                    SELECT DISTINCT period.ErrorCode
                    FROM mesingest.DemandSeriesErrorPeriods AS period
                    INNER JOIN mesingest.DemandSeriesEvents AS opened
                        ON opened.EventId = period.OpenedEventId
                    INNER JOIN mesingest.ProjectionCommits AS openedCommit
                        ON openedCommit.ProjectionCommitId = opened.ProjectionCommitId
                    LEFT JOIN mesingest.DemandSeriesEvents AS closed
                        ON closed.EventId = period.ClosedEventId
                    LEFT JOIN mesingest.ProjectionCommits AS closedCommit
                        ON closedCommit.ProjectionCommitId = closed.ProjectionCommitId
                    OUTER APPLY
                    (
                        SELECT TOP (1) evidence.DemandId
                        FROM mesingest.SeriesErrorPeriodEvidence AS evidence
                        INNER JOIN mesingest.ProjectionCommits AS evidenceCommit
                            ON evidenceCommit.ProjectionCommitId = evidence.ProjectionCommitId
                        WHERE evidence.PeriodId = period.PeriodId
                          AND evidenceCommit.ProjectionSequence <= @snapshotSequence
                        ORDER BY evidenceCommit.ProjectionSequence DESC, evidence.EvidenceId DESC
                    ) AS latestEvidence
                    WHERE period.SeriesId = s.SeriesId
                      AND openedCommit.ProjectionSequence <= @snapshotSequence
                      AND (closedCommit.ProjectionSequence IS NULL
                           OR closedCommit.ProjectionSequence > @snapshotSequence)
                      AND latestEvidence.DemandId = currentDemand.DemandId
                ) AS active
            ) AS conditions
            WHERE seriesCreated.ProjectionSequence <= @snapshotSequence;

            CREATE INDEX IX_BrowseStates_Order
                ON #BrowseStates (StartedAt DESC, SeriesId ASC);

            CREATE TABLE #FilteredSeries
            (
                SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                Lifecycle NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CurrentPresence NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL
            );

            INSERT INTO #FilteredSeries (SeriesId, Lifecycle, CurrentPresence)
            SELECT state.SeriesId, state.Lifecycle, state.CurrentPresence
            FROM #BrowseStates AS state
            WHERE
                (NOT EXISTS (SELECT 1 FROM OPENJSON(@lifecyclesJson))
                 OR state.Lifecycle IN
                    (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@lifecyclesJson)))
              AND
                (NOT EXISTS (SELECT 1 FROM OPENJSON(@presencesJson))
                 OR state.CurrentPresence IN
                    (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@presencesJson)))
              AND
                (NOT EXISTS (SELECT 1 FROM OPENJSON(@workTypesJson))
                 OR state.WorkType IN
                    (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@workTypesJson)))
              AND
                (NOT EXISTS (SELECT 1 FROM OPENJSON(@areasJson))
                 OR CASE WHEN state.LatestObservationCount = 1 THEN state.Area END IN
                    (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@areasJson)))
              AND (@sublotContains IS NULL
                   OR CHARINDEX(
                        @sublotContains COLLATE Latin1_General_100_BIN2,
                        state.Sublot COLLATE Latin1_General_100_BIN2) > 0)
              AND (@seriesId IS NULL
                   OR state.SeriesId = @seriesId COLLATE Latin1_General_100_BIN2)
              AND (@demandId IS NULL OR EXISTS
                    (
                        SELECT 1
                        FROM mesingest.TransportDemands AS demand
                        INNER JOIN mesingest.ProjectionCommits AS createdCommit
                            ON createdCommit.ProjectionCommitId = demand.CreatedProjectionCommitId
                        WHERE demand.SeriesId = state.SeriesId
                          AND demand.DemandId = @demandId COLLATE Latin1_General_100_BIN2
                          AND createdCommit.ProjectionSequence <= @snapshotSequence
                    ));

            SELECT
                COUNT_BIG(*) AS ExactTotalCount,
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN Lifecycle = N'TRACKING' THEN 1 ELSE 0 END)), 0)
                    AS TrackingCount,
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN Lifecycle = N'ARCHIVED' THEN 1 ELSE 0 END)), 0)
                    AS ArchivedCount,
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN CurrentPresence = N'VISIBLE' THEN 1 ELSE 0 END)), 0)
                    AS VisibleCount,
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN CurrentPresence = N'GONE' THEN 1 ELSE 0 END)), 0)
                    AS GoneCount,
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN CurrentPresence = N'LONG_GONE_BUT_VISIBLE' THEN 1 ELSE 0 END)), 0)
                    AS LongGoneButVisibleCount
            FROM #FilteredSeries;

            SELECT
                state.SeriesId, state.WorkType, state.Sublot, state.StartedAt,
                state.CreatedPollTraceId, state.CreatedProjectionCommitId,
                state.CurrentDemandId, state.CurrentGeneration, state.PredecessorDemandId,
                state.CurrentDemandCreatedAt, state.CurrentDemandCreatedPollTraceId,
                state.CurrentDemandCreatedProjectionCommitId,
                state.Lifecycle, state.CurrentPresence, state.ArchivedAt,
                state.CurrentDemandStatus, state.DemandLastSeenAt, state.GoneConfirmedAt,
                state.LatestObservationProjectionCommitId,
                state.LatestObservationPollTraceId, state.LatestObservationCount,
                state.Area, state.Eqp, state.Step, state.MesSourceDate, state.Package,
                state.LastSeriesSequence, state.LatestProjectionCommitId,
                state.LatestPollTraceId, state.ConditionCodes
            FROM #FilteredSeries AS filtered
            INNER JOIN #BrowseStates AS state ON state.SeriesId = filtered.SeriesId
            WHERE @afterStartedAt IS NULL
               OR state.StartedAt < @afterStartedAt
               OR (state.StartedAt = @afterStartedAt
                   AND state.SeriesId > @afterSeriesId COLLATE Latin1_General_100_BIN2)
            ORDER BY state.StartedAt DESC, state.SeriesId ASC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
        AddNVarChar(command, "@lifecyclesJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.Lifecycles,
            static message => new DemandSeriesBrowseException(DemandSeriesBrowseErrorCodes.InvalidQuery, message)));
        AddNVarChar(command, "@presencesJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.CurrentPresences,
            static message => new DemandSeriesBrowseException(DemandSeriesBrowseErrorCodes.InvalidQuery, message)));
        AddNVarChar(command, "@workTypesJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.WorkTypes,
            static message => new DemandSeriesBrowseException(DemandSeriesBrowseErrorCodes.InvalidQuery, message)));
        AddNVarChar(command, "@areasJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.MesAreas,
            static message => new DemandSeriesBrowseException(DemandSeriesBrowseErrorCodes.InvalidQuery, message)));
        AddNullableNVarChar(command, "@sublotContains", 256, normalized.SublotContains);
        AddNullableNVarChar(command, "@seriesId", 64, normalized.SeriesId);
        AddNullableNVarChar(command, "@demandId", 64, normalized.DemandId);
        AddNullableDateTimeOffset(command, "@afterStartedAt", afterStartedAt);
        AddNullableNVarChar(command, "@afterSeriesId", 64, afterSeriesId);
        command.Parameters.Add("@offset", SqlDbType.BigInt).Value = afterStartedAt is null ? offset : 0;
        command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The DemandSeries browse aggregate result is missing.");
        }

        var exactTotalCount = reader.GetInt64(0);
        var facets = new DemandSeriesFacets(
            reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5));
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The DemandSeries browse page result is missing.");
        }

        var states = new List<BrowseSeriesState>(pageSize);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var archivedAt = GetNullableDateTimeOffset(reader, 14);
            var goneConfirmedAt = GetNullableDateTimeOffset(reader, 17);
            var latestObservationCount = reader.GetInt64(20);
            var blockers = new List<string>();
            if (goneConfirmedAt is not null)
            {
                blockers.Add("DEMAND_GONE");
            }
            if (archivedAt is not null)
            {
                blockers.Add(SeriesArchivedBlocker);
            }
            if (archivedAt is not null && goneConfirmedAt is null)
            {
                blockers.Add(LongGoneButVisibleError);
            }
            if (!reader.IsDBNull(29))
            {
                blockers.AddRange(reader.GetString(29)
                    .Split((char)31, StringSplitOptions.RemoveEmptyEntries));
            }
            blockers = blockers.Distinct(StringComparer.Ordinal).ToList();

            states.Add(new BrowseSeriesState(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetInt32(7), GetNullableString(reader, 8),
                reader.GetFieldValue<DateTimeOffset>(9), reader.GetString(10), reader.GetString(11),
                reader.GetString(12), reader.GetString(13), archivedAt, reader.GetString(15),
                reader.GetFieldValue<DateTimeOffset>(16), goneConfirmedAt,
                GetNullableString(reader, 18), GetNullableString(reader, 19),
                latestObservationCount == 1
                    ? new LiveMesFieldSetSnapshot(
                        GetNullableString(reader, 21), GetNullableString(reader, 22),
                        GetNullableString(reader, 23), GetNullableDateTimeOffset(reader, 24),
                        GetNullableString(reader, 25))
                    : null,
                blockers.Count == 0 ? "READABLE" : "NOT_READABLE", blockers,
                reader.GetInt64(26), reader.GetString(27), reader.GetString(28),
                Array.Empty<string>()));
        }

        return new BrowsePageState(exactTotalCount, facets, states);
    }

    private static async Task<byte[]> ReadSnapshotTokenSigningKeyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SnapshotTokenSigningKey FROM mesingest.SchemaInfo WHERE Id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as byte[] is { Length: 32 } key
            ? key
            : throw new InvalidOperationException("The DemandSeries snapshot signing key is unavailable.");
    }

    private static async Task<DemandSeriesSnapshotIdentity> ResolveSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string? snapshotReference,
        byte[] signingKey,
        CancellationToken cancellationToken)
    {
        DemandSeriesSnapshotIdentity? requested = null;
        if (snapshotReference is not null
            && !DemandSeriesSnapshotTokenCodec.TryReadSnapshotReference(
                snapshotReference,
                signingKey,
                out requested,
                out var tokenError))
        {
            throw new DemandSeriesBrowseException(tokenError!.Code, tokenError.Message);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = requested is null
            ? """
              SELECT TOP (1) ProjectionCommitId, ProjectionSequence, CommittedAt, PollTraceId
              FROM mesingest.ProjectionCommits
              ORDER BY ProjectionSequence DESC;
              """
            : """
              SELECT ProjectionCommitId, ProjectionSequence, CommittedAt, PollTraceId
              FROM mesingest.ProjectionCommits
              WHERE ProjectionCommitId = @projectionCommitId
                AND ProjectionSequence = @projectionSequence;
              """;
        if (requested is not null)
        {
            AddNVarChar(command, "@projectionCommitId", 64, requested.ProjectionCommitId);
            command.Parameters.Add("@projectionSequence", SqlDbType.BigInt).Value = requested.ProjectionSequence;
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DemandSeriesBrowseException(
                requested is null
                    ? DemandSeriesBrowseErrorCodes.ProjectionNotAvailable
                    : DemandSeriesBrowseErrorCodes.SnapshotNotFound,
                requested is null
                    ? "No successful projection commit is available."
                    : "The referenced projection commit is not retained.");
        }

        var resolved = new DemandSeriesSnapshotIdentity(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetString(3));
        if (requested is not null
            && (!string.Equals(requested.PollTraceId, resolved.PollTraceId, StringComparison.Ordinal)
                || requested.ProjectionCommittedAt != resolved.ProjectionCommittedAt))
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.SnapshotMismatch,
                "The snapshot reference metadata does not match the retained commit.");
        }

        return resolved;
    }

    private static async Task<IReadOnlyList<BrowseSeriesState>> ReadSeriesStatesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long snapshotSequence,
        string seriesId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH EligibleDemands AS
            (
                SELECT
                    d.DemandId, d.SeriesId, d.Generation, d.PredecessorDemandId,
                    d.CreatedAt, d.CreatedPollTraceId, d.CreatedProjectionCommitId,
                    createdCommit.ProjectionSequence AS CreatedSequence,
                    ROW_NUMBER() OVER
                        (PARTITION BY d.SeriesId ORDER BY d.Generation DESC) AS CurrentRank
                FROM mesingest.TransportDemands AS d
                INNER JOIN mesingest.ProjectionCommits AS createdCommit
                    ON createdCommit.ProjectionCommitId = d.CreatedProjectionCommitId
                WHERE createdCommit.ProjectionSequence <= @snapshotSequence
            ),
            LatestObservations AS
            (
                SELECT
                    d.DemandId,
                    observationCommit.ProjectionCommitId,
                    observationCommit.ProjectionSequence,
                    observationCommit.PollTraceId,
                    observationCommit.CommittedAt,
                    ROW_NUMBER() OVER
                        (PARTITION BY d.DemandId ORDER BY observationCommit.ProjectionSequence DESC) AS ObservationRank
                FROM EligibleDemands AS d
                INNER JOIN mesingest.DemandRawObservations AS o ON o.DemandId = d.DemandId
                INNER JOIN mesingest.ProjectionCommits AS observationCommit
                    ON observationCommit.ProjectionCommitId = o.ProjectionCommitId
                WHERE observationCommit.ProjectionSequence <= @snapshotSequence
                GROUP BY d.DemandId, observationCommit.ProjectionCommitId,
                    observationCommit.ProjectionSequence, observationCommit.PollTraceId,
                    observationCommit.CommittedAt
            ),
            LatestObservationFields AS
            (
                SELECT
                    latest.DemandId, latest.ProjectionCommitId, latest.ProjectionSequence,
                    latest.PollTraceId, latest.CommittedAt,
                    COUNT_BIG(o.Ordinal) AS ObservationCount,
                    MAX(o.Area) AS Area, MAX(o.Eqp) AS Eqp, MAX(o.Step) AS Step,
                    MAX(SWITCHOFFSET(o.MesSourceDate, '+00:00')) AS MesSourceDate,
                    MAX(o.Package) AS Package
                FROM LatestObservations AS latest
                INNER JOIN mesingest.DemandRawObservations AS o
                    ON o.DemandId = latest.DemandId
                   AND o.ProjectionCommitId = latest.ProjectionCommitId
                WHERE latest.ObservationRank = 1
                GROUP BY latest.DemandId, latest.ProjectionCommitId,
                    latest.ProjectionSequence, latest.PollTraceId, latest.CommittedAt
            ),
            EventState AS
            (
                SELECT
                    e.SeriesId,
                    MAX(e.SeriesSequence) AS LastSeriesSequence,
                    MAX(eventCommit.ProjectionSequence) AS LatestEventSequence
                FROM mesingest.DemandSeriesEvents AS e
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = e.ProjectionCommitId
                WHERE eventCommit.ProjectionSequence <= @snapshotSequence
                GROUP BY e.SeriesId
            ),
            ArchiveState AS
            (
                SELECT e.SeriesId, MIN(e.OccurredAt) AS ArchivedAt
                FROM mesingest.DemandSeriesEvents AS e
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = e.ProjectionCommitId
                WHERE eventCommit.ProjectionSequence <= @snapshotSequence
                  AND e.EventType = N'GONE_TIMEOUT_ARCHIVED'
                GROUP BY e.SeriesId
            ),
            GoneState AS
            (
                SELECT e.SubjectId AS DemandId, MAX(e.OccurredAt) AS GoneConfirmedAt
                FROM mesingest.DemandSeriesEvents AS e
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = e.ProjectionCommitId
                WHERE eventCommit.ProjectionSequence <= @snapshotSequence
                  AND e.EventType = N'DEMAND_GONE'
                GROUP BY e.SubjectId
            )
            SELECT
                s.SeriesId, s.WorkType, s.Sublot, s.StartedAt,
                s.CreatedPollTraceId, s.CreatedProjectionCommitId,
                currentDemand.DemandId, currentDemand.Generation,
                currentDemand.PredecessorDemandId, currentDemand.CreatedAt,
                currentDemand.CreatedPollTraceId, currentDemand.CreatedProjectionCommitId,
                archive.ArchivedAt, gone.GoneConfirmedAt,
                fields.CommittedAt AS DemandLastSeenAt,
                fields.ProjectionCommitId AS LatestObservationCommitId,
                fields.PollTraceId AS LatestObservationPollTraceId,
                fields.ObservationCount, fields.Area, fields.Eqp, fields.Step,
                fields.MesSourceDate, fields.Package,
                eventState.LastSeriesSequence,
                latest.ProjectionCommitId AS LatestProjectionCommitId,
                latest.PollTraceId AS LatestPollTraceId,
                (SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), allIds.DemandId), NCHAR(31))
                    WITHIN GROUP (ORDER BY allIds.Generation)
                 FROM EligibleDemands AS allIds WHERE allIds.SeriesId = s.SeriesId) AS DemandIds
            FROM mesingest.DemandSeries AS s
            INNER JOIN mesingest.ProjectionCommits AS seriesCreated
                ON seriesCreated.ProjectionCommitId = s.CreatedProjectionCommitId
            INNER JOIN EligibleDemands AS currentDemand
                ON currentDemand.SeriesId = s.SeriesId AND currentDemand.CurrentRank = 1
            LEFT JOIN LatestObservationFields AS fields ON fields.DemandId = currentDemand.DemandId
            LEFT JOIN EventState AS eventState ON eventState.SeriesId = s.SeriesId
            LEFT JOIN ArchiveState AS archive ON archive.SeriesId = s.SeriesId
            LEFT JOIN GoneState AS gone ON gone.DemandId = currentDemand.DemandId
            OUTER APPLY
            (
                SELECT TOP (1) candidate.ProjectionCommitId, candidate.PollTraceId
                FROM
                (
                    SELECT createdCommit.ProjectionCommitId, createdCommit.PollTraceId,
                           createdCommit.ProjectionSequence
                    FROM EligibleDemands AS d
                    INNER JOIN mesingest.ProjectionCommits AS createdCommit
                        ON createdCommit.ProjectionCommitId = d.CreatedProjectionCommitId
                    WHERE d.SeriesId = s.SeriesId
                    UNION ALL
                    SELECT eventCommit.ProjectionCommitId, eventCommit.PollTraceId,
                           eventCommit.ProjectionSequence
                    FROM mesingest.DemandSeriesEvents AS e
                    INNER JOIN mesingest.ProjectionCommits AS eventCommit
                        ON eventCommit.ProjectionCommitId = e.ProjectionCommitId
                    WHERE e.SeriesId = s.SeriesId
                      AND eventCommit.ProjectionSequence <= @snapshotSequence
                    UNION ALL
                    SELECT observationCommit.ProjectionCommitId, observationCommit.PollTraceId,
                           observationCommit.ProjectionSequence
                    FROM mesingest.DemandRawObservations AS o
                    INNER JOIN mesingest.ProjectionCommits AS observationCommit
                        ON observationCommit.ProjectionCommitId = o.ProjectionCommitId
                    WHERE o.SeriesId = s.SeriesId
                      AND observationCommit.ProjectionSequence <= @snapshotSequence
                ) AS candidate
                ORDER BY candidate.ProjectionSequence DESC
            ) AS latest
            WHERE seriesCreated.ProjectionSequence <= @snapshotSequence
              AND s.SeriesId = @seriesId
            ORDER BY s.StartedAt DESC, s.SeriesId;
            """;
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
        AddNVarChar(command, "@seriesId", 64, seriesId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var states = new List<BrowseSeriesState>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var archivedAt = GetNullableDateTimeOffset(reader, 12);
            var goneConfirmedAt = GetNullableDateTimeOffset(reader, 13);
            var demandLastSeenAt = GetNullableDateTimeOffset(reader, 14) ?? reader.GetFieldValue<DateTimeOffset>(9);
            var latestObservationCount = reader.IsDBNull(17) ? 0 : reader.GetInt64(17);
            var isArchived = archivedAt is not null;
            var postarchiveGeneration = isArchived && reader.GetFieldValue<DateTimeOffset>(9) >= archivedAt;
            var status = goneConfirmedAt is not null
                ? GoneDemandStatus
                : postarchiveGeneration
                    ? LongGoneButVisibleDemandStatus
                    : VisibleDemandStatus;
            var lifecycle = isArchived ? ArchivedLifecycle : TrackingLifecycle;
            var presence = string.Equals(status, GoneDemandStatus, StringComparison.Ordinal)
                ? GonePresence
                : isArchived ? LongGoneButVisiblePresence : VisiblePresence;
            var liveFields = latestObservationCount == 1
                ? new LiveMesFieldSetSnapshot(
                    GetNullableString(reader, 18),
                    GetNullableString(reader, 19),
                    GetNullableString(reader, 20),
                    GetNullableDateTimeOffset(reader, 21),
                    GetNullableString(reader, 22))
                : null;
            var blockers = new List<string>();
            if (goneConfirmedAt is not null)
            {
                blockers.Add("DEMAND_GONE");
            }
            if (isArchived)
            {
                blockers.Add(SeriesArchivedBlocker);
            }
            if (isArchived && goneConfirmedAt is null)
            {
                blockers.Add(LongGoneButVisibleError);
            }

            states.Add(new BrowseSeriesState(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetInt32(7), GetNullableString(reader, 8),
                reader.GetFieldValue<DateTimeOffset>(9), reader.GetString(10), reader.GetString(11),
                lifecycle, presence, archivedAt, status, demandLastSeenAt, goneConfirmedAt,
                GetNullableString(reader, 15), GetNullableString(reader, 16), liveFields,
                blockers.Count == 0 ? "READABLE" : "NOT_READABLE", blockers,
                reader.IsDBNull(23) ? 0 : reader.GetInt64(23),
                reader.GetString(24), reader.GetString(25),
                reader.GetString(26).Split((char)31, StringSplitOptions.RemoveEmptyEntries)));
        }

        return states;
    }

    private static async Task<IReadOnlyList<DemandRawObservationSnapshot>> ReadRawObservationsAsOfAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        long snapshotSequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT o.Ordinal, o.PollTraceId, o.ProjectionCommitId,
                o.SeriesId, o.DemandId, o.WorkType, o.Sublot, o.Area, o.Eqp,
                o.Step, o.MesSourceDate, o.Package, p.CompletedAt, o.MesSourceDateRaw
            FROM mesingest.DemandRawObservations AS o
            INNER JOIN mesingest.PollTraces AS p ON p.PollTraceId = o.PollTraceId
            INNER JOIN mesingest.ProjectionCommits AS c ON c.ProjectionCommitId = o.ProjectionCommitId
            WHERE o.SeriesId = @seriesId AND c.ProjectionSequence <= @snapshotSequence
            ORDER BY c.ProjectionSequence, o.PollTraceId, o.Ordinal;
            """;
        AddNVarChar(command, "@seriesId", 64, seriesId);
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<DemandRawObservationSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new DemandRawObservationSnapshot(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) && reader.IsDBNull(4)
                    ? MesObservationAssignment.Unassigned
                    : MesObservationAssignment.Assigned,
                GetNullableString(reader, 3), GetNullableString(reader, 4),
                GetNullableString(reader, 5), GetNullableString(reader, 6),
                GetNullableString(reader, 7), GetNullableString(reader, 8),
                GetNullableString(reader, 9), GetNullableDateTimeOffset(reader, 10),
                GetNullableString(reader, 11), reader.GetFieldValue<DateTimeOffset>(12),
                GetNullableString(reader, 13)));
        }
        return items;
    }

    private static async Task<IReadOnlyList<DemandSeriesEventSnapshot>> ReadEventsAsOfAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        long snapshotSequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.EventId, e.SeriesId, e.SeriesSequence, e.EventType, e.OccurredAt,
                e.SubjectKind, e.SubjectId, e.PollTraceId, e.ProjectionCommitId,
                e.PayloadVersion, e.Payload
            FROM mesingest.DemandSeriesEvents AS e
            INNER JOIN mesingest.ProjectionCommits AS c ON c.ProjectionCommitId = e.ProjectionCommitId
            WHERE e.SeriesId = @seriesId AND c.ProjectionSequence <= @snapshotSequence
            ORDER BY e.SeriesSequence;
            """;
        AddNVarChar(command, "@seriesId", 64, seriesId);
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<DemandSeriesEventSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new DemandSeriesEventSnapshot(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5), GetNullableString(reader, 6),
                reader.GetString(7), reader.GetString(8), reader.GetInt32(9), reader.GetString(10)));
        }
        return items;
    }

    private static async Task<ErrorStateSnapshot> ReadErrorStateAsOfAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        long snapshotSequence,
        CancellationToken cancellationToken)
    {
        var periods = new List<AsOfErrorPeriodRow>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT p.PeriodId, p.ErrorCode, p.Category, p.Severity, p.Target,
                    p.SubjectKind, p.StartReason, p.StartedAt,
                    CASE WHEN closedCommit.ProjectionSequence <= @snapshotSequence THEN p.EndedAt END,
                    CASE WHEN closedCommit.ProjectionSequence <= @snapshotSequence THEN p.EndReason END
                FROM mesingest.DemandSeriesErrorPeriods AS p
                INNER JOIN mesingest.DemandSeriesEvents AS opened ON opened.EventId = p.OpenedEventId
                INNER JOIN mesingest.ProjectionCommits AS openedCommit
                    ON openedCommit.ProjectionCommitId = opened.ProjectionCommitId
                LEFT JOIN mesingest.DemandSeriesEvents AS closed ON closed.EventId = p.ClosedEventId
                LEFT JOIN mesingest.ProjectionCommits AS closedCommit
                    ON closedCommit.ProjectionCommitId = closed.ProjectionCommitId
                WHERE p.SeriesId = @seriesId
                  AND openedCommit.ProjectionSequence <= @snapshotSequence
                ORDER BY p.StartedAt, p.PeriodId;
                """;
            AddNVarChar(command, "@seriesId", 64, seriesId);
            command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                periods.Add(new AsOfErrorPeriodRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetFieldValue<DateTimeOffset>(7), GetNullableDateTimeOffset(reader, 8),
                    GetNullableString(reader, 9)));
            }
        }

        var evidenceByPeriod = periods.ToDictionary(
            period => period.PeriodId,
            _ => new List<SeriesErrorPeriodEvidenceSnapshot>(),
            StringComparer.Ordinal);
        if (periods.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT e.PeriodId, e.EvidenceId, e.EvidenceKind, e.ObservedAt,
                    e.PollTraceId, e.ProjectionCommitId, e.DemandId,
                    e.ObservedValue, e.ExpectedRule
                FROM mesingest.SeriesErrorPeriodEvidence AS e
                INNER JOIN mesingest.DemandSeriesErrorPeriods AS p ON p.PeriodId = e.PeriodId
                INNER JOIN mesingest.ProjectionCommits AS c ON c.ProjectionCommitId = e.ProjectionCommitId
                WHERE p.SeriesId = @seriesId AND c.ProjectionSequence <= @snapshotSequence
                ORDER BY c.ProjectionSequence, e.EvidenceId;
                """;
            AddNVarChar(command, "@seriesId", 64, seriesId);
            command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                evidenceByPeriod[reader.GetString(0)].Add(new SeriesErrorPeriodEvidenceSnapshot(
                    reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    GetNullableString(reader, 7), reader.GetString(8)));
            }
        }

        var snapshots = periods.Select(period => new DemandSeriesErrorPeriodSnapshot(
            period.PeriodId, period.Code, period.Category, period.Severity,
            period.Target, period.SubjectKind, period.StartReason, period.StartedAt,
            period.EndedAt, period.EndReason, evidenceByPeriod[period.PeriodId])).ToArray();
        var current = snapshots
            .Where(period => period.EndedAt is null && period.Evidence.Count > 0)
            .Select(period =>
            {
                var evidence = period.Evidence[^1];
                return new DemandSeriesCurrentConditionSnapshot(
                    period.PeriodId, period.Code, period.Category, period.Severity,
                    period.Target, period.SubjectKind, period.StartedAt, evidence.ObservedAt,
                    evidence.PollTraceId, evidence.ProjectionCommitId, evidence.DemandId,
                    evidence.ObservedValue, evidence.ExpectedRule);
            }).ToArray();
        return new ErrorStateSnapshot(current, snapshots);
    }

    private static async Task<IReadOnlyList<TransportDemandSnapshot>> ReadDemandGenerationsAsOfAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        BrowseSeriesState series,
        IReadOnlyList<DemandSeriesCurrentConditionSnapshot> currentConditions,
        long snapshotSequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH Eligible AS
            (
                SELECT d.DemandId, d.SeriesId, d.Generation, d.PredecessorDemandId,
                    d.CreatedAt, d.CreatedPollTraceId, d.CreatedProjectionCommitId
                FROM mesingest.TransportDemands AS d
                INNER JOIN mesingest.ProjectionCommits AS created
                    ON created.ProjectionCommitId = d.CreatedProjectionCommitId
                WHERE d.SeriesId = @seriesId AND created.ProjectionSequence <= @snapshotSequence
            ),
            LatestObservations AS
            (
                SELECT e.DemandId, c.ProjectionCommitId, c.PollTraceId,
                    c.ProjectionSequence, c.CommittedAt,
                    ROW_NUMBER() OVER (PARTITION BY e.DemandId ORDER BY c.ProjectionSequence DESC) AS rn
                FROM Eligible AS e
                INNER JOIN mesingest.DemandRawObservations AS o ON o.DemandId = e.DemandId
                INNER JOIN mesingest.ProjectionCommits AS c ON c.ProjectionCommitId = o.ProjectionCommitId
                WHERE c.ProjectionSequence <= @snapshotSequence
                GROUP BY e.DemandId, c.ProjectionCommitId, c.PollTraceId,
                    c.ProjectionSequence, c.CommittedAt
            ),
            Gone AS
            (
                SELECT e.SubjectId AS DemandId, MAX(e.OccurredAt) AS GoneAt
                FROM mesingest.DemandSeriesEvents AS e
                INNER JOIN mesingest.ProjectionCommits AS c ON c.ProjectionCommitId = e.ProjectionCommitId
                WHERE e.SeriesId = @seriesId AND e.EventType = N'DEMAND_GONE'
                  AND c.ProjectionSequence <= @snapshotSequence
                GROUP BY e.SubjectId
            ),
            LatestDemandCommit AS
            (
                SELECT candidates.DemandId, candidates.ProjectionCommitId,
                    candidates.PollTraceId, candidates.ProjectionSequence,
                    ROW_NUMBER() OVER
                        (PARTITION BY candidates.DemandId ORDER BY candidates.ProjectionSequence DESC) AS rn
                FROM
                (
                    SELECT d.DemandId, created.ProjectionCommitId, created.PollTraceId,
                        created.ProjectionSequence
                    FROM Eligible AS d
                    INNER JOIN mesingest.ProjectionCommits AS created
                        ON created.ProjectionCommitId = d.CreatedProjectionCommitId
                    UNION ALL
                    SELECT o.DemandId, observationCommit.ProjectionCommitId,
                        observationCommit.PollTraceId, observationCommit.ProjectionSequence
                    FROM mesingest.DemandRawObservations AS o
                    INNER JOIN mesingest.ProjectionCommits AS observationCommit
                        ON observationCommit.ProjectionCommitId = o.ProjectionCommitId
                    WHERE o.SeriesId = @seriesId
                      AND o.DemandId IS NOT NULL
                      AND observationCommit.ProjectionSequence <= @snapshotSequence
                    UNION ALL
                    SELECT e.SubjectId, eventCommit.ProjectionCommitId, eventCommit.PollTraceId,
                        eventCommit.ProjectionSequence
                    FROM mesingest.DemandSeriesEvents AS e
                    INNER JOIN mesingest.ProjectionCommits AS eventCommit
                        ON eventCommit.ProjectionCommitId = e.ProjectionCommitId
                    WHERE e.SeriesId = @seriesId
                      AND e.SubjectKind = N'DEMAND'
                      AND e.SubjectId IS NOT NULL
                      AND eventCommit.ProjectionSequence <= @snapshotSequence
                    UNION ALL
                    SELECT JSON_VALUE(e.Payload, N'$.demandId') COLLATE Latin1_General_100_BIN2,
                        eventCommit.ProjectionCommitId, eventCommit.PollTraceId,
                        eventCommit.ProjectionSequence
                    FROM mesingest.DemandSeriesEvents AS e
                    INNER JOIN mesingest.ProjectionCommits AS eventCommit
                        ON eventCommit.ProjectionCommitId = e.ProjectionCommitId
                    WHERE e.SeriesId = @seriesId
                      AND e.EventType = N'GONE_TIMEOUT_ARCHIVED'
                      AND JSON_VALUE(e.Payload, N'$.demandId') IS NOT NULL
                      AND eventCommit.ProjectionSequence <= @snapshotSequence
                ) AS candidates
            )
            SELECT d.DemandId, d.SeriesId, d.Generation, d.PredecessorDemandId,
                d.CreatedAt, d.CreatedPollTraceId, d.CreatedProjectionCommitId,
                gone.GoneAt, latest.CommittedAt, latest.PollTraceId,
                latest.ProjectionCommitId,
                (SELECT COUNT_BIG(*) FROM mesingest.DemandRawObservations AS o
                 WHERE o.DemandId = d.DemandId AND o.ProjectionCommitId = latest.ProjectionCommitId),
                (SELECT TOP (1) o.Area FROM mesingest.DemandRawObservations AS o
                 WHERE o.DemandId = d.DemandId AND o.ProjectionCommitId = latest.ProjectionCommitId ORDER BY o.Ordinal),
                (SELECT TOP (1) o.Eqp FROM mesingest.DemandRawObservations AS o
                 WHERE o.DemandId = d.DemandId AND o.ProjectionCommitId = latest.ProjectionCommitId ORDER BY o.Ordinal),
                (SELECT TOP (1) o.Step FROM mesingest.DemandRawObservations AS o
                 WHERE o.DemandId = d.DemandId AND o.ProjectionCommitId = latest.ProjectionCommitId ORDER BY o.Ordinal),
                (SELECT TOP (1) SWITCHOFFSET(o.MesSourceDate, '+00:00') FROM mesingest.DemandRawObservations AS o
                 WHERE o.DemandId = d.DemandId AND o.ProjectionCommitId = latest.ProjectionCommitId ORDER BY o.Ordinal),
                (SELECT TOP (1) o.Package FROM mesingest.DemandRawObservations AS o
                 WHERE o.DemandId = d.DemandId AND o.ProjectionCommitId = latest.ProjectionCommitId ORDER BY o.Ordinal),
                latestDemand.ProjectionCommitId,
                latestDemand.PollTraceId
            FROM Eligible AS d
            LEFT JOIN LatestObservations AS latest ON latest.DemandId = d.DemandId AND latest.rn = 1
            LEFT JOIN Gone AS gone ON gone.DemandId = d.DemandId
            LEFT JOIN LatestDemandCommit AS latestDemand
                ON latestDemand.DemandId = d.DemandId AND latestDemand.rn = 1
            ORDER BY d.Generation;
            """;
        AddNVarChar(command, "@seriesId", 64, series.SeriesId);
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<TransportDemandSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var demandId = reader.GetString(0);
            var goneAt = GetNullableDateTimeOffset(reader, 7);
            var isCurrent = string.Equals(demandId, series.CurrentDemandId, StringComparison.Ordinal);
            var postarchive = series.ArchivedAt is not null
                && reader.GetFieldValue<DateTimeOffset>(4) >= series.ArchivedAt;
            var status = goneAt is not null ? GoneDemandStatus
                : postarchive ? LongGoneButVisibleDemandStatus : VisibleDemandStatus;
            var count = reader.IsDBNull(11) ? 0 : reader.GetInt64(11);
            var blockers = isCurrent
                ? currentConditions.Where(condition =>
                        string.Equals(condition.DemandId, demandId, StringComparison.Ordinal))
                    .Select(condition => condition.Code).ToList()
                : new List<string>();
            if (goneAt is not null) blockers.Add("DEMAND_GONE");
            if (series.ArchivedAt is not null) blockers.Add(SeriesArchivedBlocker);
            if (isCurrent && postarchive && goneAt is null) blockers.Add(LongGoneButVisibleError);
            blockers = blockers.Distinct(StringComparer.Ordinal).ToList();

            var latestCommit = GetNullableString(reader, 17) ?? reader.GetString(6);
            items.Add(new TransportDemandSnapshot(
                demandId, reader.GetString(1), reader.GetInt32(2), GetNullableString(reader, 3),
                status, reader.GetFieldValue<DateTimeOffset>(4),
                GetNullableDateTimeOffset(reader, 8) ?? reader.GetFieldValue<DateTimeOffset>(4),
                goneAt, reader.GetString(5), reader.GetString(6), latestCommit,
                count == 1
                    ? new LiveMesFieldSetSnapshot(
                        GetNullableString(reader, 12), GetNullableString(reader, 13),
                        GetNullableString(reader, 14), GetNullableDateTimeOffset(reader, 15),
                        GetNullableString(reader, 16))
                    : null,
                blockers.Count == 0 ? "READABLE" : "NOT_READABLE", blockers,
                GetNullableString(reader, 9), GetNullableString(reader, 10),
                GetNullableDateTimeOffset(reader, 8)));
        }
        return items;
    }

    private sealed record BrowseSeriesState(
        string SeriesId,
        string WorkType,
        string Sublot,
        DateTimeOffset StartedAt,
        string CreatedPollTraceId,
        string CreatedProjectionCommitId,
        string CurrentDemandId,
        int CurrentGeneration,
        string? PredecessorDemandId,
        DateTimeOffset CurrentDemandCreatedAt,
        string CurrentDemandCreatedPollTraceId,
        string CurrentDemandCreatedProjectionCommitId,
        string Lifecycle,
        string CurrentPresence,
        DateTimeOffset? ArchivedAt,
        string CurrentDemandStatus,
        DateTimeOffset DemandLastSeenAt,
        DateTimeOffset? GoneConfirmedAt,
        string? LatestObservationProjectionCommitId,
        string? LatestObservationPollTraceId,
        LiveMesFieldSetSnapshot? LiveMesFields,
        string ExternalReadabilityState,
        IReadOnlyList<string> ReadabilityBlockers,
        long LastSeriesSequence,
        string LatestProjectionCommitId,
        string LatestPollTraceId,
        IReadOnlyList<string> DemandIds)
    {
        public string ExternalReadabilityState { get; set; } = ExternalReadabilityState;

        public IReadOnlyList<string> ReadabilityBlockers { get; set; } = ReadabilityBlockers;
    }

    private sealed record BrowsePageState(
        long ExactTotalCount,
        DemandSeriesFacets Facets,
        IReadOnlyList<BrowseSeriesState> States);

    private sealed record AsOfErrorPeriodRow(
        string PeriodId,
        string Code,
        string Category,
        string Severity,
        string Target,
        string SubjectKind,
        string StartReason,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        string? EndReason);
}
