using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    private const string ReadabilityAuditFilterAndFacetSql = """
        CREATE TABLE #AreaScoped
        (
            DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY
        );
        INSERT INTO #AreaScoped (DemandId)
        SELECT state.DemandId
        FROM #AuditStates AS state
        WHERE NOT EXISTS (SELECT 1 FROM OPENJSON(@areasJson))
           OR (state.IsAreaTrusted = 1 AND state.Area IN
                (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@areasJson)));

        CREATE TABLE #ExactMatches
        (
            DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY
        );
        INSERT INTO #ExactMatches (DemandId)
        SELECT state.DemandId
        FROM #AreaScoped AS scoped
        INNER JOIN #AuditStates AS state ON state.DemandId = scoped.DemandId
        WHERE
            (NOT EXISTS (SELECT 1 FROM OPENJSON(@statesJson))
             OR CASE state.ReadabilityRank WHEN 0 THEN N'NOT_READABLE' ELSE N'READABLE' END IN
                (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@statesJson)))
          AND (NOT EXISTS (SELECT 1 FROM OPENJSON(@workTypesJson))
               OR state.WorkType IN
                  (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@workTypesJson)))
          AND (NOT EXISTS (SELECT 1 FROM OPENJSON(@blockersJson))
               OR EXISTS
                  (SELECT 1 FROM #AuditBlockers AS blocker
                   WHERE blocker.DemandId = state.DemandId
                     AND blocker.Code IN
                        (SELECT [value] COLLATE Latin1_General_100_BIN2
                         FROM OPENJSON(@blockersJson))))
          AND (@demandId IS NULL
               OR state.DemandId = @demandId COLLATE Latin1_General_100_CI_AS)
          AND (@sublotContains IS NULL
               OR CHARINDEX(
                    @sublotContains COLLATE Latin1_General_100_CI_AS,
                    state.Sublot COLLATE Latin1_General_100_CI_AS) > 0);

        CREATE TABLE #StateFacetBase
        (
            DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY
        );
        INSERT INTO #StateFacetBase (DemandId)
        SELECT state.DemandId
        FROM #AreaScoped AS scoped
        INNER JOIN #AuditStates AS state ON state.DemandId = scoped.DemandId
        WHERE
            (NOT EXISTS (SELECT 1 FROM OPENJSON(@workTypesJson))
             OR state.WorkType IN
                (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@workTypesJson)))
          AND (NOT EXISTS (SELECT 1 FROM OPENJSON(@blockersJson))
               OR EXISTS
                  (SELECT 1 FROM #AuditBlockers AS blocker
                   WHERE blocker.DemandId = state.DemandId
                     AND blocker.Code IN
                        (SELECT [value] COLLATE Latin1_General_100_BIN2
                         FROM OPENJSON(@blockersJson))))
          AND (@demandId IS NULL
               OR state.DemandId = @demandId COLLATE Latin1_General_100_CI_AS)
          AND (@sublotContains IS NULL
               OR CHARINDEX(
                    @sublotContains COLLATE Latin1_General_100_CI_AS,
                    state.Sublot COLLATE Latin1_General_100_CI_AS) > 0);

        CREATE TABLE #BlockerFacetBase
        (
            DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY
        );
        INSERT INTO #BlockerFacetBase (DemandId)
        SELECT state.DemandId
        FROM #AreaScoped AS scoped
        INNER JOIN #AuditStates AS state ON state.DemandId = scoped.DemandId
        WHERE state.ReadabilityRank = 0
          AND (NOT EXISTS (SELECT 1 FROM OPENJSON(@statesJson))
               OR N'NOT_READABLE' IN
                  (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@statesJson)))
          AND (NOT EXISTS (SELECT 1 FROM OPENJSON(@workTypesJson))
               OR state.WorkType IN
                  (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@workTypesJson)))
          AND (@demandId IS NULL
               OR state.DemandId = @demandId COLLATE Latin1_General_100_CI_AS)
          AND (@sublotContains IS NULL
               OR CHARINDEX(
                    @sublotContains COLLATE Latin1_General_100_CI_AS,
                    state.Sublot COLLATE Latin1_General_100_CI_AS) > 0);

        SELECT COUNT_BIG(*) FROM #ExactMatches;

        SELECT facet.State,
            COALESCE(SUM(CONVERT(BIGINT, CASE
                WHEN state.ReadabilityRank = facet.ReadabilityRank THEN 1 ELSE 0 END)), 0)
                AS DemandCount
        FROM (VALUES (N'READABLE', 1), (N'NOT_READABLE', 0))
            AS facet(State, ReadabilityRank)
        LEFT JOIN #StateFacetBase AS baseRow ON 1 = 1
        LEFT JOIN #AuditStates AS state ON state.DemandId = baseRow.DemandId
        GROUP BY facet.State, facet.ReadabilityRank
        ORDER BY facet.ReadabilityRank DESC;

        SELECT catalog.Code, COUNT_BIG(baseRow.DemandId) AS DemandCount
        FROM #ReadabilityBlockerCatalog AS catalog
        LEFT JOIN #AuditBlockers AS blocker ON blocker.Code = catalog.Code
        LEFT JOIN #BlockerFacetBase AS baseRow ON baseRow.DemandId = blocker.DemandId
        GROUP BY catalog.Code, catalog.Priority
        ORDER BY catalog.Priority;

        """;

    private static void BindReadabilityAuditFilterParameters(
        SqlCommand command,
        ReadabilityAuditFilter normalized)
    {
        AddNVarChar(
            command,
            "@blockerCatalogJson",
            SqlFilterJsonMaximumLength,
            ReadabilityBlockerCatalogJson);
        AddNVarChar(command, "@statesJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.ReadabilityStates,
            static message => new ReadabilityAuditException(ReadabilityAuditErrorCodes.InvalidQuery, message)));
        AddNVarChar(command, "@workTypesJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.WorkTypes,
            static message => new ReadabilityAuditException(ReadabilityAuditErrorCodes.InvalidQuery, message)));
        AddNVarChar(command, "@blockersJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.Blockers,
            static message => new ReadabilityAuditException(ReadabilityAuditErrorCodes.InvalidQuery, message)));
        AddNVarChar(command, "@areasJson", SqlFilterJsonMaximumLength, SerializeBoundedSqlFilter(
            normalized.MesAreas,
            static message => new ReadabilityAuditException(ReadabilityAuditErrorCodes.InvalidQuery, message)));
        AddNullableNVarChar(command, "@demandId", 64, normalized.DemandId);
        AddNullableNVarChar(command, "@sublotContains", 256, normalized.SublotContains);
    }
}
