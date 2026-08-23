using System.Data;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    /// <summary>
    /// Reads the live readability projection without consulting raw observation,
    /// event, or error-period history. The caller holds the shared commit fence,
    /// so every maintained row belongs to the selected latest commit.
    /// </summary>
    private static async Task<AuditPageState> ReadCurrentAuditPageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ReadabilityAuditFilter filter,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var normalized = filter.Normalize();
        var offset = checked((long)(pageNumber - 1) * pageSize);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE #ReadabilityBlockerCatalog
            (
                Code NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                Priority INT NOT NULL
            );
            INSERT INTO #ReadabilityBlockerCatalog (Code, Priority)
            SELECT catalog.Code, catalog.Priority
            FROM OPENJSON(@blockerCatalogJson)
            WITH
            (
                Code NVARCHAR(128) '$.Code',
                Priority INT '$.Priority'
            ) AS catalog;

            CREATE TABLE #AuditStates
            (
                DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Sublot NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Generation INT NOT NULL,
                PredecessorDemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
                IsCurrentGeneration BIT NOT NULL,
                DemandCreatedAt DATETIMEOFFSET(7) NOT NULL,
                DemandLastSeenAt DATETIMEOFFSET(7) NOT NULL,
                GoneConfirmedAt DATETIMEOFFSET(7) NULL,
                LatestObservationProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                LatestObservationPollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                RawObservationCount INT NOT NULL,
                Area NVARCHAR(512) NULL,
                Eqp NVARCHAR(512) NULL,
                Step NVARCHAR(512) NULL,
                MesSourceDate DATETIMEOFFSET(7) NULL,
                Package NVARCHAR(512) NULL,
                IsAreaTrusted BIT NOT NULL,
                SeriesStartedAt DATETIMEOFFSET(7) NOT NULL,
                SeriesArchivedAt DATETIMEOFFSET(7) NULL,
                CurrentDemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                DemandStatus NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                SeriesLifecycle NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                SeriesCurrentPresence NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ReadabilityRank INT NOT NULL DEFAULT (1),
                LeadPriority INT NULL
            );

            INSERT INTO #AuditStates
                (DemandId, SeriesId, WorkType, Sublot, Generation, PredecessorDemandId,
                 IsCurrentGeneration, DemandCreatedAt, DemandLastSeenAt, GoneConfirmedAt,
                 LatestObservationProjectionCommitId, LatestObservationPollTraceId,
                 RawObservationCount, Area, Eqp, Step, MesSourceDate, Package, IsAreaTrusted,
                 SeriesStartedAt, SeriesArchivedAt, CurrentDemandId, DemandStatus,
                 SeriesLifecycle, SeriesCurrentPresence)
            SELECT demand.DemandId, demand.SeriesId, series.WorkType, series.Sublot,
                demand.Generation, demand.PredecessorDemandId,
                CONVERT(BIT, CASE WHEN demand.DemandId = series.CurrentDemandId THEN 1 ELSE 0 END),
                demand.CreatedAt, demand.DemandLastSeenAt, demand.GoneConfirmedAt,
                demand.LatestObservationProjectionCommitId, observationCommit.PollTraceId,
                demand.CurrentRawObservationCount,
                demand.Area, demand.Eqp, demand.Step, demand.MesSourceDate, demand.Package,
                CONVERT(BIT, CASE
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
                    ) THEN 1 ELSE 0 END),
                series.StartedAt, series.ArchivedAt, series.CurrentDemandId,
                demand.Status, series.Lifecycle, series.CurrentPresence
            FROM mesingest.TransportDemands AS demand
            INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = demand.SeriesId
            INNER JOIN mesingest.ProjectionCommits AS observationCommit
                ON observationCommit.ProjectionCommitId = demand.LatestObservationProjectionCommitId;

            CREATE TABLE #AuditBlockers
            (
                DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Code NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Priority INT NOT NULL,
                CONSTRAINT PK_CurrentAuditBlockers PRIMARY KEY (DemandId, Code)
            );

            INSERT INTO #AuditBlockers (DemandId, Code, Priority)
            SELECT DISTINCT evidence.DemandId, catalog.Code, catalog.Priority
            FROM mesingest.DemandSeriesCurrentConditions AS currentCondition
            INNER JOIN mesingest.SeriesErrorPeriodEvidence AS evidence
                ON evidence.EvidenceId = currentCondition.LatestEvidenceId
            INNER JOIN #ReadabilityBlockerCatalog AS catalog
                ON catalog.Code = currentCondition.ErrorCode
            INNER JOIN #AuditStates AS state ON state.DemandId = evidence.DemandId;

            INSERT INTO #AuditBlockers (DemandId, Code, Priority)
            SELECT state.DemandId, catalog.Code, catalog.Priority
            FROM #AuditStates AS state
            INNER JOIN #ReadabilityBlockerCatalog AS catalog ON catalog.Code = N'DEMAND_GONE'
            WHERE state.DemandStatus = N'GONE'
              AND NOT EXISTS
                  (SELECT 1 FROM #AuditBlockers AS existing
                   WHERE existing.DemandId = state.DemandId AND existing.Code = catalog.Code);

            INSERT INTO #AuditBlockers (DemandId, Code, Priority)
            SELECT state.DemandId, catalog.Code, catalog.Priority
            FROM #AuditStates AS state
            INNER JOIN #ReadabilityBlockerCatalog AS catalog ON catalog.Code = N'SERIES_ARCHIVED'
            WHERE state.SeriesLifecycle = N'ARCHIVED'
              AND NOT EXISTS
                  (SELECT 1 FROM #AuditBlockers AS existing
                   WHERE existing.DemandId = state.DemandId AND existing.Code = catalog.Code);

            INSERT INTO #AuditBlockers (DemandId, Code, Priority)
            SELECT state.DemandId, catalog.Code, catalog.Priority
            FROM #AuditStates AS state
            INNER JOIN #ReadabilityBlockerCatalog AS catalog
                ON catalog.Code = N'LONG_GONE_BUT_VISIBLE'
            WHERE state.DemandStatus = N'LONG_GONE_BUT_VISIBLE'
              AND NOT EXISTS
                  (SELECT 1 FROM #AuditBlockers AS existing
                   WHERE existing.DemandId = state.DemandId AND existing.Code = catalog.Code);

            UPDATE state
            SET ReadabilityRank = CASE WHEN lead.Priority IS NULL THEN 1 ELSE 0 END,
                LeadPriority = COALESCE(lead.Priority, 2147483647)
            FROM #AuditStates AS state
            OUTER APPLY
            (
                SELECT MIN(blocker.Priority) AS Priority
                FROM #AuditBlockers AS blocker
                WHERE blocker.DemandId = state.DemandId
            ) AS lead;

            CREATE INDEX IX_CurrentAuditStates_Order
                ON #AuditStates
                    (ReadabilityRank, LeadPriority, DemandLastSeenAt DESC, DemandId ASC);

            """ + ReadabilityAuditFilterAndFacetSql + """
            SELECT state.DemandId, state.SeriesId, state.WorkType, state.Sublot,
                state.Generation, state.PredecessorDemandId, state.IsCurrentGeneration,
                state.DemandCreatedAt, state.DemandLastSeenAt, state.GoneConfirmedAt,
                state.LatestObservationProjectionCommitId,
                state.LatestObservationPollTraceId, CONVERT(BIGINT, state.RawObservationCount),
                state.Area, state.Eqp, state.Step, state.MesSourceDate, state.Package,
                state.SeriesStartedAt, state.SeriesArchivedAt, state.CurrentDemandId,
                state.DemandStatus, state.SeriesLifecycle, state.SeriesCurrentPresence,
                blockerCodes.Codes
            FROM #ExactMatches AS exact
            INNER JOIN #AuditStates AS state ON state.DemandId = exact.DemandId
            OUTER APPLY
            (
                SELECT STRING_AGG(CONVERT(NVARCHAR(128), blocker.Code), NCHAR(31))
                    WITHIN GROUP (ORDER BY blocker.Priority, blocker.Code) AS Codes
                FROM #AuditBlockers AS blocker
                WHERE blocker.DemandId = state.DemandId
            ) AS blockerCodes
            ORDER BY state.ReadabilityRank, state.LeadPriority,
                state.DemandLastSeenAt DESC, state.DemandId ASC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;
        BindReadabilityAuditFilterParameters(command, normalized);
        command.Parameters.Add("@offset", SqlDbType.BigInt).Value = offset;
        command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadAuditPageResultsAsync(reader, pageSize, cancellationToken)
            .ConfigureAwait(false);
    }
}
