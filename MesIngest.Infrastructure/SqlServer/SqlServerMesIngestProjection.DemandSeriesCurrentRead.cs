using System.Data;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    /// <summary>
    /// Reads the live DemandSeries projection without consulting raw observation or
    /// event history. The caller holds the shared commit fence for the duration of
    /// this query, so every maintained row belongs to the selected latest commit.
    /// </summary>
    private static async Task<BrowsePageState> ReadCurrentDemandSeriesPageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DemandSeriesBrowseFilter filter,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var normalized = filter.Normalize();
        var offset = checked((long)(pageNumber - 1) * pageSize);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE #CurrentDemandSeries
            (
                SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Sublot NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                StartedAt DATETIMEOFFSET(7) NULL,
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
                LatestObservationProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                LatestObservationPollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Area NVARCHAR(512) NULL,
                Eqp NVARCHAR(512) NULL,
                Step NVARCHAR(512) NULL,
                MesSourceDate DATETIMEOFFSET(7) NULL,
                Package NVARCHAR(512) NULL,
                LastSeriesSequence BIGINT NOT NULL,
                LatestProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                LatestPollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                HasDuplicateKeyCondition BIT NOT NULL,
                ConditionCodes NVARCHAR(2048) NULL
            );

            WITH ConditionState AS
            (
                SELECT
                    currentCondition.SeriesId,
                    MAX(CASE WHEN currentCondition.ErrorCode = N'DUPLICATE_TRANSPORT_DEMAND_KEY'
                        THEN 1 ELSE 0 END) AS HasDuplicateKeyCondition,
                    STRING_AGG(CONVERT(NVARCHAR(128), currentCondition.ErrorCode), NCHAR(31))
                        WITHIN GROUP (ORDER BY currentCondition.ErrorCode) AS ConditionCodes
                FROM mesingest.DemandSeriesCurrentConditions AS currentCondition
                GROUP BY currentCondition.SeriesId
            )
            INSERT INTO #CurrentDemandSeries
            SELECT
                series.SeriesId, series.WorkType, series.Sublot, series.StartedAt,
                series.CreatedPollTraceId, series.CreatedProjectionCommitId,
                demand.DemandId, demand.Generation, demand.PredecessorDemandId,
                demand.CreatedAt, demand.CreatedPollTraceId, demand.CreatedProjectionCommitId,
                series.Lifecycle, series.CurrentPresence, series.ArchivedAt,
                demand.Status, demand.DemandLastSeenAt, demand.GoneConfirmedAt,
                demand.LatestObservationProjectionCommitId,
                observationCommit.PollTraceId,
                demand.Area, demand.Eqp, demand.Step, demand.MesSourceDate, demand.Package,
                series.LastSeriesSequence, series.LatestProjectionCommitId,
                latestCommit.PollTraceId,
                CONVERT(BIT, COALESCE(conditionState.HasDuplicateKeyCondition, 0)),
                conditionState.ConditionCodes
            FROM mesingest.DemandSeries AS series
            INNER JOIN mesingest.TransportDemands AS demand
                ON demand.DemandId = series.CurrentDemandId
            INNER JOIN mesingest.ProjectionCommits AS observationCommit
                ON observationCommit.ProjectionCommitId = demand.LatestObservationProjectionCommitId
            INNER JOIN mesingest.ProjectionCommits AS latestCommit
                ON latestCommit.ProjectionCommitId = series.LatestProjectionCommitId
            LEFT JOIN ConditionState AS conditionState ON conditionState.SeriesId = series.SeriesId;

            CREATE TABLE #FilteredCurrentDemandSeries
            (
                SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                Lifecycle NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CurrentPresence NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL
            );

            INSERT INTO #FilteredCurrentDemandSeries (SeriesId, Lifecycle, CurrentPresence)
            SELECT state.SeriesId, state.Lifecycle, state.CurrentPresence
            FROM #CurrentDemandSeries AS state
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
                 OR CASE WHEN state.HasDuplicateKeyCondition = 0 THEN state.Area END IN
                    (SELECT [value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@areasJson)))
              AND (@sublotContains IS NULL
                   OR CHARINDEX(
                        @sublotContains COLLATE Latin1_General_100_BIN2,
                        state.Sublot COLLATE Latin1_General_100_BIN2) > 0)
              AND (@sublot IS NULL
                   OR state.Sublot = @sublot COLLATE Latin1_General_100_BIN2)
              AND (@seriesId IS NULL
                   OR state.SeriesId = @seriesId COLLATE Latin1_General_100_BIN2)
              AND (@demandId IS NULL OR EXISTS
                    (
                        SELECT 1
                        FROM mesingest.TransportDemands AS historicalDemand
                        WHERE historicalDemand.SeriesId = state.SeriesId
                          AND historicalDemand.DemandId = @demandId COLLATE Latin1_General_100_BIN2
                    ));

            SELECT
                COUNT_BIG(*) AS ExactTotalCount,
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN Lifecycle = N'TRACKING' THEN 1 ELSE 0 END)), 0),
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN Lifecycle = N'ARCHIVED' THEN 1 ELSE 0 END)), 0),
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN CurrentPresence = N'VISIBLE' THEN 1 ELSE 0 END)), 0),
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN CurrentPresence = N'GONE' THEN 1 ELSE 0 END)), 0),
                COALESCE(SUM(CONVERT(BIGINT, CASE WHEN CurrentPresence = N'LONG_GONE_BUT_VISIBLE' THEN 1 ELSE 0 END)), 0)
            FROM #FilteredCurrentDemandSeries;

            SELECT
                state.SeriesId, state.WorkType, state.Sublot, state.StartedAt,
                state.CreatedPollTraceId, state.CreatedProjectionCommitId,
                state.CurrentDemandId, state.CurrentGeneration, state.PredecessorDemandId,
                state.CurrentDemandCreatedAt, state.CurrentDemandCreatedPollTraceId,
                state.CurrentDemandCreatedProjectionCommitId,
                state.Lifecycle, state.CurrentPresence, state.ArchivedAt,
                state.CurrentDemandStatus, state.DemandLastSeenAt, state.GoneConfirmedAt,
                state.LatestObservationProjectionCommitId,
                state.LatestObservationPollTraceId,
                state.Area, state.Eqp, state.Step, state.MesSourceDate, state.Package,
                state.LastSeriesSequence, state.LatestProjectionCommitId,
                state.LatestPollTraceId, state.HasDuplicateKeyCondition,
                state.ConditionCodes
            FROM #FilteredCurrentDemandSeries AS filtered
            INNER JOIN #CurrentDemandSeries AS state ON state.SeriesId = filtered.SeriesId
            ORDER BY state.StartedAt DESC, state.SeriesId ASC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;
        BindDemandSeriesFilterParameters(command, normalized);
        command.Parameters.Add("@offset", SqlDbType.BigInt).Value = offset;
        command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The current DemandSeries aggregate result is missing.");
        }

        var exactTotalCount = reader.GetInt64(0);
        var facets = new DemandSeriesFacets(
            reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5));
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The current DemandSeries page result is missing.");
        }

        var states = new List<BrowseSeriesState>(pageSize);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var archivedAt = GetNullableDateTimeOffset(reader, 14);
            var goneConfirmedAt = GetNullableDateTimeOffset(reader, 17);
            var hasDuplicateKeyCondition = reader.GetBoolean(28);
            var blockers = new List<string>();
            if (goneConfirmedAt is not null) blockers.Add("DEMAND_GONE");
            if (archivedAt is not null) blockers.Add(SeriesArchivedBlocker);
            if (archivedAt is not null && goneConfirmedAt is null) blockers.Add(LongGoneButVisibleError);
            if (!reader.IsDBNull(29))
            {
                blockers.AddRange(reader.GetString(29)
                    .Split((char)31, StringSplitOptions.RemoveEmptyEntries));
            }
            blockers = blockers.Distinct(StringComparer.Ordinal).ToList();

            states.Add(new BrowseSeriesState(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                GetNullableDateTimeOffset(reader, 3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetInt32(7), GetNullableString(reader, 8),
                reader.GetFieldValue<DateTimeOffset>(9), reader.GetString(10), reader.GetString(11),
                reader.GetString(12), reader.GetString(13), archivedAt, reader.GetString(15),
                reader.GetFieldValue<DateTimeOffset>(16), goneConfirmedAt,
                reader.GetString(18), reader.GetString(19),
                hasDuplicateKeyCondition
                    ? null
                    : new LiveMesFieldSetSnapshot(
                        GetNullableString(reader, 20), GetNullableString(reader, 21),
                        GetNullableString(reader, 22), GetNullableDateTimeOffset(reader, 23),
                        GetNullableString(reader, 24)),
                blockers.Count == 0 ? "READABLE" : "NOT_READABLE", blockers,
                reader.GetInt64(25), reader.GetString(26), reader.GetString(27),
                Array.Empty<string>()));
        }

        return new BrowsePageState(exactTotalCount, facets, states);
    }

    private static async Task<DemandSeriesSnapshot?> ReadCurrentDemandSeriesDetailAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string predicate,
        string identity,
        string? expectedWorkType,
        string? expectedSublot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT
                series.SeriesId, series.WorkType, series.Sublot,
                series.Lifecycle, series.CurrentPresence, series.StartedAt,
                series.ArchivedAt, series.CreatedPollTraceId,
                series.CreatedProjectionCommitId, series.LatestProjectionCommitId,
                series.LastSeriesSequence,
                demand.DemandId, demand.Generation, demand.PredecessorDemandId,
                demand.Status, demand.CreatedAt, demand.DemandLastSeenAt,
                demand.GoneConfirmedAt, demand.CreatedPollTraceId,
                demand.CreatedProjectionCommitId, demand.LatestProjectionCommitId,
                demand.LatestObservationProjectionCommitId,
                observationCommit.PollTraceId,
                demand.Area, demand.Eqp, demand.Step, demand.MesSourceDate, demand.Package
            FROM mesingest.DemandSeries AS series
            INNER JOIN mesingest.TransportDemands AS demand
                ON demand.DemandId = series.CurrentDemandId
            INNER JOIN mesingest.ProjectionCommits AS observationCommit
                ON observationCommit.ProjectionCommitId = demand.LatestObservationProjectionCommitId
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

        CurrentDemandSeriesRow? row;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            row = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new CurrentDemandSeriesRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4),
                    GetNullableDateTimeOffset(reader, 5), GetNullableDateTimeOffset(reader, 6),
                    reader.GetString(7), reader.GetString(8), reader.GetString(9),
                    reader.GetInt64(10), reader.GetString(11), reader.GetInt32(12),
                    GetNullableString(reader, 13), reader.GetString(14),
                    reader.GetFieldValue<DateTimeOffset>(15),
                    reader.GetFieldValue<DateTimeOffset>(16),
                    GetNullableDateTimeOffset(reader, 17), reader.GetString(18),
                    reader.GetString(19), reader.GetString(20), reader.GetString(21),
                    reader.GetString(22), GetNullableString(reader, 23),
                    GetNullableString(reader, 24), GetNullableString(reader, 25),
                    GetNullableDateTimeOffset(reader, 26), GetNullableString(reader, 27))
                : null;
        }

        if (row is null)
        {
            return null;
        }
        if (expectedWorkType is not null
            && (!string.Equals(row.WorkType, expectedWorkType, StringComparison.Ordinal)
                || !string.Equals(row.Sublot, expectedSublot, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "TransportDemandKey token collision detected; refusing to return another series.");
        }

        var currentConditions = await ReadCurrentDemandSeriesConditionsAsync(
            connection,
            transaction,
            row.SeriesId,
            cancellationToken).ConfigureAwait(false);
        var blockers = currentConditions.Select(condition => condition.Code).ToList();
        if (row.GoneConfirmedAt is not null) blockers.Add("DEMAND_GONE");
        if (row.ArchivedAt is not null) blockers.Add(SeriesArchivedBlocker);
        if (string.Equals(
                row.CurrentPresence,
                LongGoneButVisiblePresence,
                StringComparison.Ordinal))
        {
            blockers.Add(LongGoneButVisibleError);
        }
        blockers = blockers.Distinct(StringComparer.Ordinal).ToList();
        var hasDuplicateKeyCondition = blockers.Contains(
            "DUPLICATE_TRANSPORT_DEMAND_KEY",
            StringComparer.Ordinal);
        var currentDemand = new TransportDemandSnapshot(
            row.DemandId,
            row.SeriesId,
            row.Generation,
            row.PredecessorDemandId,
            row.DemandStatus,
            row.DemandCreatedAt,
            row.DemandLastSeenAt,
            row.GoneConfirmedAt,
            row.DemandCreatedPollTraceId,
            row.DemandCreatedProjectionCommitId,
            row.DemandLatestProjectionCommitId,
            hasDuplicateKeyCondition
                ? null
                : new LiveMesFieldSetSnapshot(
                    row.Area,
                    row.Eqp,
                    row.Step,
                    row.MesSourceDate,
                    row.Package),
            blockers.Count == 0 ? "READABLE" : "NOT_READABLE",
            blockers,
            row.LatestObservationPollTraceId,
            row.LatestObservationProjectionCommitId,
            row.DemandLastSeenAt);
        return new DemandSeriesSnapshot(
            row.SeriesId,
            row.WorkType,
            row.Sublot,
            row.Lifecycle,
            row.CurrentPresence,
            row.StartedAt,
            row.CreatedPollTraceId,
            row.CreatedProjectionCommitId,
            row.LatestProjectionCommitId,
            currentDemand,
            [currentDemand],
            [],
            [],
            currentConditions,
            [],
            row.ArchivedAt,
            row.LastSeriesSequence);
    }

    private static async Task<IReadOnlyList<DemandSeriesCurrentConditionSnapshot>>
        ReadCurrentDemandSeriesConditionsAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            string seriesId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                period.PeriodId, period.ErrorCode, period.Category, period.Severity,
                period.Target, period.SubjectKind, period.StartedAt,
                evidence.ObservedAt, evidence.PollTraceId, evidence.ProjectionCommitId,
                evidence.DemandId, evidence.ObservedValue, evidence.ExpectedRule
            FROM mesingest.DemandSeriesCurrentConditions AS currentCondition
            INNER JOIN mesingest.DemandSeriesErrorPeriods AS period
                ON period.PeriodId = currentCondition.PeriodId
            INNER JOIN mesingest.SeriesErrorPeriodEvidence AS evidence
                ON evidence.EvidenceId = currentCondition.LatestEvidenceId
            WHERE currentCondition.SeriesId = @seriesId
            ORDER BY period.StartedAt, period.PeriodId;
            """;
        AddNVarChar(command, "@seriesId", 64, seriesId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var conditions = new List<DemandSeriesCurrentConditionSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            conditions.Add(new DemandSeriesCurrentConditionSnapshot(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8),
                reader.GetString(9), reader.GetString(10),
                GetNullableString(reader, 11), reader.GetString(12)));
        }

        return conditions;
    }

    private sealed record CurrentDemandSeriesRow(
        string SeriesId,
        string WorkType,
        string Sublot,
        string Lifecycle,
        string CurrentPresence,
        DateTimeOffset? StartedAt,
        DateTimeOffset? ArchivedAt,
        string CreatedPollTraceId,
        string CreatedProjectionCommitId,
        string LatestProjectionCommitId,
        long LastSeriesSequence,
        string DemandId,
        int Generation,
        string? PredecessorDemandId,
        string DemandStatus,
        DateTimeOffset DemandCreatedAt,
        DateTimeOffset DemandLastSeenAt,
        DateTimeOffset? GoneConfirmedAt,
        string DemandCreatedPollTraceId,
        string DemandCreatedProjectionCommitId,
        string DemandLatestProjectionCommitId,
        string LatestObservationProjectionCommitId,
        string LatestObservationPollTraceId,
        string? Area,
        string? Eqp,
        string? Step,
        DateTimeOffset? MesSourceDate,
        string? Package);
}
