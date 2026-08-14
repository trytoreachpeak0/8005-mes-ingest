using System.Data;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    private static readonly string ErrorSearchCategoryCatalogJson = JsonSerializer.Serialize(
        SeriesErrorCatalog.Definitions
            .Select(definition => definition.Category)
            .Distinct(StringComparer.Ordinal));
    private const string SqlTrustedAreaPredicate = """
        (
            (DATALENGTH({0}) = 8
             AND {0} COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9]-[1-9]')
            OR (DATALENGTH({0}) = 10
                AND {0} COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9][0-9]-[1-9]')
            OR (DATALENGTH({0}) = 10
                AND {0} COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9]-[1-9][0-9]')
            OR (DATALENGTH({0}) = 12
                AND {0} COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9][0-9]-[1-9][0-9]')
        )
        """;

    public async Task<ErrorSearchListSnapshot> ListErrorSearchAsync(
        ErrorSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        // Every candidate fact below is fenced by ProjectionSequence and AsOf,
        // so a regular read transaction remains stable even while later rounds
        // append facts or update a period's current closure columns.
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
            var signingKey = await ReadSnapshotTokenSigningKeyAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var snapshot = await ResolveErrorSearchSnapshotAsync(
                connection,
                transaction,
                query,
                signingKey,
                cancellationToken).ConfigureAwait(false);

            ErrorSearchCursor? cursor = null;
            var pageNumber = 1;
            if (query.Cursor is not null)
            {
                if (!ErrorSearchTokenCodec.TryReadCursor(
                        query.Cursor,
                        snapshot,
                        query.PageSize,
                        signingKey,
                        out cursor,
                        out var tokenError))
                {
                    throw new ErrorSearchException(tokenError!.Code, tokenError.Message);
                }

                pageNumber = cursor!.TargetPageNumber;
            }

            var page = await ReadErrorSearchPageAsync(
                connection,
                transaction,
                snapshot,
                query.PageSize,
                cursor,
                cancellationToken).ConfigureAwait(false);
            var totalPages = page.ExactTotalSeriesCount == 0
                ? 0
                : checked((int)((page.ExactTotalSeriesCount + query.PageSize - 1) / query.PageSize));
            var items = page.Rows.Select(row => new ErrorSearchListItemSnapshot(
                row.SeriesId,
                row.WorkType,
                row.Sublot,
                row.ActivityRank == 0
                    ? ErrorSearchActivityStates.Active
                    : ErrorSearchActivityStates.Ended,
                row.MatchedErrors,
                row.LatestMatchedEvidenceAt,
                row.MatchedPeriodCount,
                row.MatchedDemandGenerationCount,
                row.MesArea,
                row.MesAreaAvailability)).ToArray();
            var hasMore = pageNumber < totalPages && items.Length > 0;
            var nextCursor = hasMore
                ? ErrorSearchTokenCodec.CreateCursor(
                    snapshot,
                    query.PageSize,
                    checked(pageNumber + 1),
                    page.Rows[^1].ActivityRank,
                    page.Rows[^1].LatestMatchedEvidenceAt,
                    page.Rows[^1].SeriesId,
                    signingKey)
                : null;
            var snapshotReference = query.SnapshotReference
                ?? ErrorSearchTokenCodec.CreateSnapshotReference(snapshot, signingKey);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ErrorSearchListSnapshot(
                snapshotReference,
                snapshot.Snapshot,
                snapshot.Filter,
                snapshot.Window,
                snapshot.Order,
                page.ExactTotalSeriesCount,
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

    private async Task<ErrorSearchSnapshotReference> ResolveErrorSearchSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ErrorSearchQuery query,
        byte[] signingKey,
        CancellationToken cancellationToken)
    {
        ErrorSearchSnapshotReference? requested = null;
        if (query.SnapshotReference is not null
            && !ErrorSearchTokenCodec.TryReadSnapshotReference(
                query.SnapshotReference,
                signingKey,
                out requested,
                out var tokenError))
        {
            throw new ErrorSearchException(tokenError!.Code, tokenError.Message);
        }

        if (requested is not null)
        {
            var requestedWindow = query.Window.Resolve(requested.Snapshot.ErrorSearchAsOf);
            if (!string.Equals(
                    ErrorSearchTokenCodec.ComputeFilterHash(query.Filter),
                    ErrorSearchTokenCodec.ComputeFilterHash(requested.Filter),
                    StringComparison.Ordinal)
                || !string.Equals(
                    ErrorSearchTokenCodec.ComputeWindowHash(requestedWindow),
                    ErrorSearchTokenCodec.ComputeWindowHash(requested.Window),
                    StringComparison.Ordinal)
                || !string.Equals(query.Order, requested.Order, StringComparison.Ordinal))
            {
                throw new ErrorSearchException(
                    query.Cursor is null
                        ? ErrorSearchErrorCodes.SnapshotMismatch
                        : ErrorSearchErrorCodes.CursorMismatch,
                    query.Cursor is null
                        ? "The Error Search snapshot does not belong to this filter, window, or order."
                        : "The Error Search cursor does not belong to this filter, window, or order.");
            }
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
            AddNVarChar(
                command,
                "@projectionCommitId",
                64,
                requested.Snapshot.ProjectionCommitId);
            command.Parameters.Add("@projectionSequence", SqlDbType.BigInt).Value =
                requested.Snapshot.ProjectionSequence;
        }

        RetainedErrorSearchCommit? retained = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                retained = new RetainedErrorSearchCommit(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetFieldValue<DateTimeOffset>(2).ToUniversalTime(),
                    reader.GetString(3));
            }
        }

        if (retained is null)
        {
            throw new ErrorSearchException(
                requested is null
                    ? ErrorSearchErrorCodes.ProjectionNotAvailable
                    : ErrorSearchErrorCodes.SnapshotNotFound,
                requested is null
                    ? "No successful projection commit is available."
                    : "The referenced Error Search projection commit is not retained.");
        }

        if (requested is not null)
        {
            if (!string.Equals(
                    requested.Snapshot.ProjectionCommitId,
                    retained.ProjectionCommitId,
                    StringComparison.Ordinal)
                || requested.Snapshot.ProjectionSequence != retained.ProjectionSequence
                || requested.Snapshot.ProjectionCommittedAt != retained.ProjectionCommittedAt
                || !string.Equals(
                    requested.Snapshot.PollTraceId,
                    retained.PollTraceId,
                    StringComparison.Ordinal))
            {
                throw new ErrorSearchException(
                    ErrorSearchErrorCodes.SnapshotMismatch,
                    "The Error Search snapshot metadata does not match the retained commit.");
            }

            return requested;
        }

        // The selected sequence is the durable high-water fence. The Host clock
        // is sampled after that selection, and every mutable/as-of read below is
        // independently sequence-fenced, so later back-dated rounds cannot enter
        // this search without holding a range lock for the full history scan.
        var asOf = _timeProvider.GetUtcNow().ToUniversalTime();
        var identity = new ErrorSearchSnapshotIdentity(
            asOf,
            retained.ProjectionCommitId,
            retained.ProjectionSequence,
            retained.ProjectionCommittedAt,
            retained.PollTraceId);
        return new ErrorSearchSnapshotReference(
            identity,
            query.Filter,
            query.Window.Resolve(asOf),
            query.Order);
    }

    private static async Task<ErrorSearchPageState> ReadErrorSearchPageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ErrorSearchSnapshotReference snapshot,
        int pageSize,
        ErrorSearchCursor? cursor,
        CancellationToken cancellationToken)
    {
        var filter = snapshot.Filter.Normalize();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $$"""
            CREATE TABLE #CategoryCatalog
            (
                Ordinal INT NOT NULL,
                Category NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY
            );
            INSERT INTO #CategoryCatalog (Ordinal, Category)
            SELECT CONVERT(INT, [key]), [value]
            FROM OPENJSON(@categoryCatalogJson);

            CREATE TABLE #AsOfPeriods
            (
                PeriodId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ErrorCode NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Category NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Severity NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ActivityRank INT NOT NULL
            );

            INSERT INTO #AsOfPeriods
                (PeriodId, SeriesId, ErrorCode, Category, Severity, ActivityRank)
            SELECT period.PeriodId, period.SeriesId, period.ErrorCode,
                period.Category, period.Severity,
                CONVERT(INT, CASE
                    WHEN closedCommit.ProjectionSequence <= @snapshotSequence
                     AND period.EndedAt <= @asOf
                    THEN 1 ELSE 0 END)
            FROM mesingest.DemandSeriesErrorPeriods AS period
            INNER JOIN mesingest.DemandSeries AS series
                ON series.SeriesId = period.SeriesId
            INNER JOIN mesingest.DemandSeriesEvents AS opened
                ON opened.EventId = period.OpenedEventId
            INNER JOIN mesingest.ProjectionCommits AS openedCommit
                ON openedCommit.ProjectionCommitId = opened.ProjectionCommitId
            LEFT JOIN mesingest.DemandSeriesEvents AS closed
                ON closed.EventId = period.ClosedEventId
            LEFT JOIN mesingest.ProjectionCommits AS closedCommit
                ON closedCommit.ProjectionCommitId = closed.ProjectionCommitId
            WHERE openedCommit.ProjectionSequence <= @snapshotSequence
              AND period.StartedAt <= @asOf
              AND period.StartedAt < @windowTo
              AND
              (
                  @windowFrom IS NULL
                  OR COALESCE(
                      CASE
                          WHEN closedCommit.ProjectionSequence <= @snapshotSequence
                           AND period.EndedAt <= @asOf
                          THEN period.EndedAt
                      END,
                      @asOf) > @windowFrom
              )
              AND (@seriesId IS NULL
                   OR period.SeriesId = @seriesId COLLATE Latin1_General_100_CI_AS)
              AND (@sublotContains IS NULL
                   OR CHARINDEX(
                       @sublotContains COLLATE Latin1_General_100_CI_AS,
                       series.Sublot COLLATE Latin1_General_100_CI_AS) > 0);

            CREATE TABLE #EligibleEvidence
            (
                EvidenceId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                PeriodId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ObservedAt DATETIMEOFFSET(7) NOT NULL
            );
            INSERT INTO #EligibleEvidence (EvidenceId, PeriodId, DemandId, ObservedAt)
            SELECT evidence.EvidenceId, evidence.PeriodId, evidence.DemandId,
                evidence.ObservedAt
            FROM mesingest.SeriesErrorPeriodEvidence AS evidence
            INNER JOIN #AsOfPeriods AS period ON period.PeriodId = evidence.PeriodId
            INNER JOIN mesingest.ProjectionCommits AS evidenceCommit
                ON evidenceCommit.ProjectionCommitId = evidence.ProjectionCommitId
            WHERE evidenceCommit.ProjectionSequence <= @snapshotSequence
              AND evidence.ObservedAt <= @asOf
              AND (@demandId IS NULL
                   OR evidence.DemandId = @demandId COLLATE Latin1_General_100_CI_AS);

            -- DemandId is an evidence-level restriction. Removing periods with
            -- no qualifying evidence prevents a Series-level hit from pulling
            -- unrelated Demand generations into counts or ordering.
            DELETE period
            FROM #AsOfPeriods AS period
            WHERE NOT EXISTS
                (SELECT 1 FROM #EligibleEvidence AS evidence
                 WHERE evidence.PeriodId = period.PeriodId);

            CREATE INDEX IX_ErrorSearch_AsOfPeriods_Category
                ON #AsOfPeriods (Category, SeriesId, ActivityRank);
            CREATE INDEX IX_ErrorSearch_EligibleEvidence_Period
                ON #EligibleEvidence (PeriodId, ObservedAt DESC, DemandId);

            CREATE TABLE #FilteredPeriods
            (
                PeriodId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ErrorCode NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Category NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Severity NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ActivityRank INT NOT NULL
            );
            INSERT INTO #FilteredPeriods
                (PeriodId, SeriesId, ErrorCode, Category, Severity, ActivityRank)
            SELECT period.PeriodId, period.SeriesId, period.ErrorCode,
                period.Category, period.Severity, period.ActivityRank
            FROM #AsOfPeriods AS period
            WHERE
                (NOT EXISTS (SELECT 1 FROM OPENJSON(@categoriesJson))
                 OR period.Category IN
                    (SELECT [value] COLLATE Latin1_General_100_BIN2
                     FROM OPENJSON(@categoriesJson)))
              AND
                (NOT EXISTS (SELECT 1 FROM OPENJSON(@errorCodesJson))
                 OR period.ErrorCode IN
                    (SELECT [value] COLLATE Latin1_General_100_BIN2
                     FROM OPENJSON(@errorCodesJson)));

            CREATE TABLE #SeriesMatches
            (
                SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                ActivityRank INT NOT NULL,
                LatestMatchedEvidenceAt DATETIMEOFFSET(7) NOT NULL,
                MatchedPeriodCount INT NOT NULL,
                MatchedDemandGenerationCount INT NOT NULL
            );
            INSERT INTO #SeriesMatches
                (SeriesId, ActivityRank, LatestMatchedEvidenceAt,
                 MatchedPeriodCount, MatchedDemandGenerationCount)
            SELECT period.SeriesId, MIN(period.ActivityRank), MAX(evidence.ObservedAt),
                COUNT(DISTINCT period.PeriodId), COUNT(DISTINCT evidence.DemandId)
            FROM #FilteredPeriods AS period
            INNER JOIN #EligibleEvidence AS evidence
                ON evidence.PeriodId = period.PeriodId
            GROUP BY period.SeriesId;

            CREATE TABLE #ExactMatches
            (
                SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY
            );
            INSERT INTO #ExactMatches (SeriesId)
            SELECT matches.SeriesId
            FROM #SeriesMatches AS matches
            WHERE NOT EXISTS (SELECT 1 FROM OPENJSON(@activityStatesJson))
               OR CASE matches.ActivityRank
                    WHEN 0 THEN N'ACTIVE' ELSE N'ENDED' END IN
                    (SELECT [value] COLLATE Latin1_General_100_BIN2
                     FROM OPENJSON(@activityStatesJson));

            CREATE TABLE #PageSeries
            (
                SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL UNIQUE,
                WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Sublot NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ActivityRank INT NOT NULL,
                LatestMatchedEvidenceAt DATETIMEOFFSET(7) NOT NULL,
                MatchedPeriodCount INT NOT NULL,
                MatchedDemandGenerationCount INT NOT NULL
            );
            INSERT INTO #PageSeries
                (SeriesId, WorkType, Sublot, ActivityRank, LatestMatchedEvidenceAt,
                 MatchedPeriodCount, MatchedDemandGenerationCount)
            SELECT series.SeriesId, series.WorkType, series.Sublot,
                matches.ActivityRank, matches.LatestMatchedEvidenceAt,
                matches.MatchedPeriodCount, matches.MatchedDemandGenerationCount
            FROM #ExactMatches AS exact
            INNER JOIN #SeriesMatches AS matches ON matches.SeriesId = exact.SeriesId
            INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = exact.SeriesId
            WHERE @afterActivityRank IS NULL
               OR matches.ActivityRank > @afterActivityRank
               OR (matches.ActivityRank = @afterActivityRank
                   AND matches.LatestMatchedEvidenceAt < @afterEvidenceAt)
               OR (matches.ActivityRank = @afterActivityRank
                   AND matches.LatestMatchedEvidenceAt = @afterEvidenceAt
                   AND matches.SeriesId > @afterSeriesId COLLATE Latin1_General_100_BIN2)
            ORDER BY matches.ActivityRank,
                matches.LatestMatchedEvidenceAt DESC,
                matches.SeriesId ASC
            OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY;

            SELECT COUNT_BIG(*) AS ExactTotalSeriesCount FROM #ExactMatches;

            WITH CategorySeries AS
            (
                SELECT period.Category, period.SeriesId,
                    MIN(period.ActivityRank) AS ActivityRank
                FROM #AsOfPeriods AS period
                GROUP BY period.Category, period.SeriesId
            )
            SELECT catalog.Category, COUNT_BIG(filtered.SeriesId) AS SeriesCount
            FROM #CategoryCatalog AS catalog
            LEFT JOIN CategorySeries AS filtered
              ON filtered.Category = catalog.Category
             AND
             (
                 NOT EXISTS (SELECT 1 FROM OPENJSON(@activityStatesJson))
                 OR CASE filtered.ActivityRank
                      WHEN 0 THEN N'ACTIVE' ELSE N'ENDED' END IN
                      (SELECT [value] COLLATE Latin1_General_100_BIN2
                       FROM OPENJSON(@activityStatesJson))
             )
            GROUP BY catalog.Ordinal, catalog.Category
            ORDER BY catalog.Ordinal;

            SELECT stateFacet.State, COUNT_BIG(matches.SeriesId) AS SeriesCount
            FROM (VALUES (N'ACTIVE', 0), (N'ENDED', 1))
                AS stateFacet(State, ActivityRank)
            LEFT JOIN #SeriesMatches AS matches
                ON matches.ActivityRank = stateFacet.ActivityRank
            GROUP BY stateFacet.State, stateFacet.ActivityRank
            ORDER BY stateFacet.ActivityRank;

            SELECT page.SeriesId, page.WorkType, page.Sublot,
                page.ActivityRank, page.LatestMatchedEvidenceAt,
                page.MatchedPeriodCount, page.MatchedDemandGenerationCount,
                CASE
                    WHEN currentArea.ObservationCount = 1
                     AND currentArea.IsValidArea = 1
                        THEN currentArea.Area
                    WHEN trusted.Area IS NOT NULL THEN trusted.Area
                    WHEN currentArea.ObservationCount = 1
                     AND NULLIF(LTRIM(RTRIM(currentArea.Area)), N'') IS NOT NULL
                        THEN currentArea.Area
                END AS MesArea,
                CASE
                    WHEN currentArea.ObservationCount = 1
                     AND currentArea.IsValidArea = 1
                     AND currentGone.IsGone IS NULL
                        THEN N'CURRENT_TRUSTED'
                    WHEN trusted.Area IS NOT NULL THEN N'LAST_TRUSTED'
                    WHEN currentArea.ObservationCount = 1
                     AND NULLIF(LTRIM(RTRIM(currentArea.Area)), N'') IS NOT NULL
                        THEN N'INVALID'
                    ELSE N'UNKNOWN'
                END AS MesAreaAvailability
            FROM #PageSeries AS page
            OUTER APPLY
            (
                SELECT TOP (1) demand.DemandId
                FROM mesingest.TransportDemands AS demand
                INNER JOIN mesingest.ProjectionCommits AS createdCommit
                    ON createdCommit.ProjectionCommitId = demand.CreatedProjectionCommitId
                WHERE demand.SeriesId = page.SeriesId
                  AND createdCommit.ProjectionSequence <= @snapshotSequence
                  AND demand.CreatedAt <= @asOf
                ORDER BY demand.Generation DESC
            ) AS currentDemand
            OUTER APPLY
            (
                SELECT TOP (1) observationCommit.ProjectionCommitId,
                    observationCommit.ProjectionSequence
                FROM mesingest.DemandRawObservations AS observation
                INNER JOIN mesingest.ProjectionCommits AS observationCommit
                    ON observationCommit.ProjectionCommitId = observation.ProjectionCommitId
                WHERE observation.DemandId = currentDemand.DemandId
                  AND observationCommit.ProjectionSequence <= @snapshotSequence
                  AND observationCommit.CommittedAt <= @asOf
                GROUP BY observationCommit.ProjectionCommitId,
                    observationCommit.ProjectionSequence
                ORDER BY observationCommit.ProjectionSequence DESC
            ) AS currentObservation
            OUTER APPLY
            (
                SELECT COUNT_BIG(*) AS ObservationCount, MAX(observation.Area) AS Area,
                    CONVERT(BIT, CASE
                        WHEN COUNT_BIG(*) = 1 AND
                        {{string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            SqlTrustedAreaPredicate,
                            "MAX(observation.Area)")}} THEN 1 ELSE 0 END) AS IsValidArea
                FROM mesingest.DemandRawObservations AS observation
                WHERE observation.DemandId = currentDemand.DemandId
                  AND observation.ProjectionCommitId = currentObservation.ProjectionCommitId
            ) AS currentArea
            OUTER APPLY
            (
                SELECT TOP (1) 1 AS IsGone
                FROM mesingest.DemandSeriesEvents AS goneEvent
                INNER JOIN mesingest.ProjectionCommits AS goneCommit
                    ON goneCommit.ProjectionCommitId = goneEvent.ProjectionCommitId
                WHERE goneEvent.SeriesId = page.SeriesId
                  AND goneEvent.SubjectId = currentDemand.DemandId
                  AND goneEvent.EventType = N'DEMAND_GONE'
                  AND goneCommit.ProjectionSequence <= @snapshotSequence
                  AND goneEvent.OccurredAt <= @asOf
            ) AS currentGone
            OUTER APPLY
            (
                SELECT TOP (1) candidate.Area
                FROM
                (
                    SELECT observationCommit.ProjectionSequence,
                        MAX(observation.Area) AS Area
                    FROM mesingest.DemandRawObservations AS observation
                    INNER JOIN mesingest.ProjectionCommits AS observationCommit
                        ON observationCommit.ProjectionCommitId = observation.ProjectionCommitId
                    WHERE observation.SeriesId = page.SeriesId
                      AND observationCommit.ProjectionSequence <= @snapshotSequence
                      AND observationCommit.CommittedAt <= @asOf
                    GROUP BY observationCommit.ProjectionCommitId,
                        observationCommit.ProjectionSequence
                    HAVING COUNT_BIG(*) = 1 AND
                    {{string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        SqlTrustedAreaPredicate,
                        "MAX(observation.Area)")}}
                ) AS candidate
                ORDER BY candidate.ProjectionSequence DESC
            ) AS trusted
            ORDER BY page.ActivityRank,
                page.LatestMatchedEvidenceAt DESC,
                page.SeriesId ASC;

            SELECT page.SeriesId, period.ErrorCode, period.Category, period.Severity
            FROM #PageSeries AS page
            INNER JOIN #FilteredPeriods AS period ON period.SeriesId = page.SeriesId
            GROUP BY page.ActivityRank, page.LatestMatchedEvidenceAt, page.SeriesId,
                period.ErrorCode, period.Category, period.Severity
            ORDER BY page.ActivityRank,
                page.LatestMatchedEvidenceAt DESC,
                page.SeriesId ASC,
                period.Category,
                period.ErrorCode;
            """;

        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value =
            snapshot.Snapshot.ProjectionSequence;
        AddDateTimeOffset(command, "@asOf", snapshot.Snapshot.ErrorSearchAsOf);
        AddNullableDateTimeOffset(command, "@windowFrom", snapshot.Window.FromUtc);
        AddDateTimeOffset(command, "@windowTo", snapshot.Window.ToUtc);
        AddNVarChar(command, "@categoryCatalogJson", -1, ErrorSearchCategoryCatalogJson);
        AddNVarChar(command, "@categoriesJson", -1, JsonSerializer.Serialize(filter.Categories));
        AddNVarChar(command, "@errorCodesJson", -1, JsonSerializer.Serialize(filter.ErrorCodes));
        AddNVarChar(
            command,
            "@activityStatesJson",
            -1,
            JsonSerializer.Serialize(filter.ActivityStates));
        AddNullableNVarChar(command, "@seriesId", 64, filter.SeriesId);
        AddNullableNVarChar(command, "@demandId", 64, filter.DemandId);
        AddNullableNVarChar(command, "@sublotContains", 256, filter.SublotContains);
        command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;
        AddNullableInt(command, "@afterActivityRank", cursor?.AfterActivityRank);
        AddNullableDateTimeOffset(
            command,
            "@afterEvidenceAt",
            cursor?.AfterLatestMatchedEvidenceAt);
        AddNullableNVarChar(command, "@afterSeriesId", 64, cursor?.AfterSeriesId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Error Search exact total is missing.");
        }
        var exactTotal = reader.GetInt64(0);

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Error Search category facets are missing.");
        }
        var categoryFacets = new List<ErrorSearchCategoryFacetSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            categoryFacets.Add(new ErrorSearchCategoryFacetSnapshot(
                reader.GetString(0),
                reader.GetInt64(1)));
        }

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Error Search activity facets are missing.");
        }
        var activityFacets = new List<ErrorSearchActivityStateFacetSnapshot>(2);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            activityFacets.Add(new ErrorSearchActivityStateFacetSnapshot(
                reader.GetString(0),
                reader.GetInt64(1)));
        }

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Error Search page is missing.");
        }
        var rows = new List<MutableErrorSearchRow>(pageSize);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new MutableErrorSearchRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime(),
                reader.GetInt32(5),
                reader.GetInt32(6),
                GetNullableString(reader, 7),
                reader.GetString(8)));
        }

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Error Search matched-error summaries are missing.");
        }
        var rowBySeries = rows.ToDictionary(row => row.SeriesId, StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!rowBySeries.TryGetValue(reader.GetString(0), out var row))
            {
                throw new InvalidOperationException(
                    "An Error Search matched-error summary has no page row.");
            }
            row.MatchedErrors.Add(new ErrorSearchMatchedErrorSnapshot(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        if (rows.Any(row => row.MatchedErrors.Count == 0))
        {
            throw new InvalidOperationException("An Error Search page row has no matched error.");
        }

        return new ErrorSearchPageState(
            exactTotal,
            new ErrorSearchFacets(categoryFacets, activityFacets),
            rows.Select(row => row.Freeze()).ToArray());
    }

    private sealed record RetainedErrorSearchCommit(
        string ProjectionCommitId,
        long ProjectionSequence,
        DateTimeOffset ProjectionCommittedAt,
        string PollTraceId);

    private sealed record ErrorSearchPageState(
        long ExactTotalSeriesCount,
        ErrorSearchFacets Facets,
        IReadOnlyList<ErrorSearchSeriesRow> Rows);

    private sealed record ErrorSearchSeriesRow(
        string SeriesId,
        string WorkType,
        string Sublot,
        int ActivityRank,
        DateTimeOffset LatestMatchedEvidenceAt,
        int MatchedPeriodCount,
        int MatchedDemandGenerationCount,
        string? MesArea,
        string MesAreaAvailability,
        IReadOnlyList<ErrorSearchMatchedErrorSnapshot> MatchedErrors);

    private sealed class MutableErrorSearchRow(
        string seriesId,
        string workType,
        string sublot,
        int activityRank,
        DateTimeOffset latestMatchedEvidenceAt,
        int matchedPeriodCount,
        int matchedDemandGenerationCount,
        string? mesArea,
        string mesAreaAvailability)
    {
        public string SeriesId { get; } = seriesId;

        public string WorkType { get; } = workType;

        public string Sublot { get; } = sublot;

        public int ActivityRank { get; } = activityRank;

        public DateTimeOffset LatestMatchedEvidenceAt { get; } = latestMatchedEvidenceAt;

        public int MatchedPeriodCount { get; } = matchedPeriodCount;

        public int MatchedDemandGenerationCount { get; } = matchedDemandGenerationCount;

        public string? MesArea { get; } = mesArea;

        public string MesAreaAvailability { get; } = mesAreaAvailability;

        public List<ErrorSearchMatchedErrorSnapshot> MatchedErrors { get; } = [];

        public ErrorSearchSeriesRow Freeze() => new(
            SeriesId,
            WorkType,
            Sublot,
            ActivityRank,
            LatestMatchedEvidenceAt,
            MatchedPeriodCount,
            MatchedDemandGenerationCount,
            MesArea,
            MesAreaAvailability,
            MatchedErrors.ToArray());
    }
}
