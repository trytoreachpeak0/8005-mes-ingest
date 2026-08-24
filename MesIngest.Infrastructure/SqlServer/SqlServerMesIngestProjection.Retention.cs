using System.Data;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    public async Task<HistoryRetentionAdvanceResult> AdvanceHistoryRetentionAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var advancedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        var cutoff = advancedAt.Subtract(
            HistoryRetentionPolicy.RawObservationAvailabilityWindow);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireCommitRoundOrderLockAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DECLARE @expired TABLE
                (
                    PollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY
                );

                INSERT INTO @expired (PollTraceId)
                SELECT PollTraceId
                FROM mesingest.PollTraces WITH (UPDLOCK, HOLDLOCK)
                WHERE RawObservationsExpiredAt IS NULL
                  AND CompletedAt <= @cutoff;

                DECLARE @expiredPollTraceCount INT = @@ROWCOUNT;

                DELETE observation
                FROM mesingest.DemandRawObservations AS observation
                INNER JOIN @expired AS expired
                    ON expired.PollTraceId = observation.PollTraceId;

                DECLARE @deletedRawObservationCount INT = @@ROWCOUNT;

                UPDATE trace
                SET RawObservationsExpiredAt = @advancedAt
                FROM mesingest.PollTraces AS trace
                INNER JOIN @expired AS expired
                    ON expired.PollTraceId = trace.PollTraceId;

                DECLARE @candidateBoundary DATETIMEOFFSET(7) = COALESCE(
                    (SELECT MIN(CompletedAt)
                     FROM mesingest.PollTraces
                     WHERE RawObservationsExpiredAt IS NULL),
                    @cutoff);

                UPDATE mesingest.SchemaInfo
                SET EarliestAvailableHostUtc =
                    CASE
                        WHEN EarliestAvailableHostUtc IS NULL
                             OR @candidateBoundary > EarliestAvailableHostUtc
                        THEN @candidateBoundary
                        ELSE EarliestAvailableHostUtc
                    END
                WHERE Id = 1;

                SELECT
                    @expiredPollTraceCount,
                    @deletedRawObservationCount,
                    EarliestAvailableHostUtc
                FROM mesingest.SchemaInfo
                WHERE Id = 1;
                """;
            AddDateTimeOffset(command, "@advancedAt", advancedAt);
            AddDateTimeOffset(command, "@cutoff", cutoff);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The history-retention advance did not return its committed boundary.");
            }

            var result = new HistoryRetentionAdvanceResult(
                advancedAt,
                cutoff,
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetFieldValue<DateTimeOffset>(2));
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RetentionEligibleSeriesCleanupResult?> CleanupNextRetentionEligibleSeriesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var cleanedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        var eligibilityCutoff = cleanedAt.Subtract(
            HistoryRetentionPolicy.RetentionEligibleDemandSeriesWindow);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireCommitRoundOrderLockAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            RetentionEligibleSeriesCleanupCandidate? candidate = null;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT TOP (1)
                        series.SeriesId,
                        series.KeyToken,
                        series.WorkType,
                        series.Sublot,
                        series.ArchivedAt,
                        latestCommit.PollTraceId,
                        series.LatestProjectionCommitId
                    FROM mesingest.DemandSeries AS series WITH (UPDLOCK, HOLDLOCK)
                    INNER JOIN mesingest.TransportDemands AS demand WITH (UPDLOCK, HOLDLOCK)
                        ON demand.DemandId = series.CurrentDemandId
                    INNER JOIN mesingest.ProjectionCommits AS latestCommit
                        ON latestCommit.ProjectionCommitId = series.LatestProjectionCommitId
                    WHERE series.RetentionEligibilityAt IS NOT NULL
                      AND series.RetentionEligibilityAt <= @eligibilityCutoff
                      AND series.Lifecycle = N'ARCHIVED'
                      AND series.ArchivedAt IS NOT NULL
                      AND series.CurrentPresence NOT IN (N'VISIBLE', N'LONG_GONE_BUT_VISIBLE')
                      AND demand.Status NOT IN (N'VISIBLE', N'LONG_GONE_BUT_VISIBLE')
                      AND NOT EXISTS
                      (
                          SELECT 1
                          FROM mesingest.DemandSeriesCurrentConditions AS currentCondition
                          WHERE currentCondition.SeriesId = series.SeriesId
                      )
                      AND NOT EXISTS
                      (
                          SELECT 1
                          FROM mesingest.DemandSeriesErrorPeriods AS errorPeriod
                          WHERE errorPeriod.SeriesId = series.SeriesId
                            AND errorPeriod.EndedAt IS NULL
                      )
                      AND NOT EXISTS
                      (
                          SELECT 1
                          FROM mesingest.DemandRawObservations AS observation
                          WHERE observation.SeriesId = series.SeriesId
                      )
                    ORDER BY series.RetentionEligibilityAt, series.SeriesId;
                    """;
                AddDateTimeOffset(select, "@eligibilityCutoff", eligibilityCutoff);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    candidate = new RetentionEligibleSeriesCleanupCandidate(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetFieldValue<DateTimeOffset>(4),
                        reader.GetString(5),
                        reader.GetString(6));
                }
            }

            if (candidate is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var checkpointContext = new ProjectionCommitCheckpointContext(
                candidate.LatestPollTraceId,
                candidate.LatestProjectionCommitId,
                _historyEpoch);

            await using (var tombstone = connection.CreateCommand())
            {
                tombstone.Transaction = transaction;
                tombstone.CommandText = """
                    IF EXISTS
                    (
                        SELECT 1
                        FROM mesingest.ArchivedDemandKeyTombstones WITH (UPDLOCK, HOLDLOCK)
                        WHERE KeyToken = @keyToken
                          AND (WorkType <> @workType
                               OR Sublot <> @sublot
                               OR OriginalSeriesId <> @seriesId
                               OR ArchivedAt <> @archivedAt
                               OR ArchiveConclusion <> N'ARCHIVED'
                               OR TombstoneVersion <> @tombstoneVersion)
                    )
                        THROW 51040, 'An archived Demand key tombstone conflicts with the due Series identity.', 1;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM mesingest.ArchivedDemandKeyTombstones WITH (UPDLOCK, HOLDLOCK)
                        WHERE OriginalSeriesId = @seriesId AND KeyToken <> @keyToken
                    )
                        THROW 51040, 'An archived Series identity is already bound to another Demand key tombstone.', 1;

                    IF NOT EXISTS
                    (
                        SELECT 1
                        FROM mesingest.ArchivedDemandKeyTombstones WITH (UPDLOCK, HOLDLOCK)
                        WHERE KeyToken = @keyToken
                    )
                    BEGIN
                        INSERT INTO mesingest.ArchivedDemandKeyTombstones
                            (KeyToken, WorkType, Sublot, OriginalSeriesId, ArchivedAt,
                             ArchiveConclusion, TombstoneVersion)
                        VALUES
                            (@keyToken, @workType, @sublot, @seriesId, @archivedAt,
                             N'ARCHIVED', @tombstoneVersion);
                    END;
                    """;
                AddChar(tombstone, "@keyToken", 64, candidate.KeyToken);
                AddNVarChar(tombstone, "@workType", 128, candidate.WorkType);
                AddNVarChar(tombstone, "@sublot", 256, candidate.Sublot);
                AddNVarChar(tombstone, "@seriesId", 64, candidate.SeriesId);
                AddDateTimeOffset(tombstone, "@archivedAt", candidate.ArchivedAt);
                tombstone.Parameters.Add("@tombstoneVersion", SqlDbType.Int).Value =
                    ArchivedDemandKeyTombstoneContract.Version;
                await tombstone.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ObserveSeriesCleanupCheckpointAsync(
                SeriesCleanupCheckpoint.ArchivedKeyTombstonePersisted,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            await ExecuteSeriesCleanupStageAsync(
                connection,
                transaction,
                candidate.SeriesId,
                """
                IF EXISTS (SELECT 1 FROM mesingest.CatalogItems WHERE SeriesId = @seriesId)
                    THROW 51041, 'A retention-eligible archived Series unexpectedly remains externally readable.', 1;
                DELETE FROM mesingest.CurrentOverviewErrorSeriesFacts WHERE SeriesId = @seriesId;
                DELETE FROM mesingest.CurrentOverviewActivities WHERE SeriesId = @seriesId;
                """,
                cancellationToken).ConfigureAwait(false);
            await ObserveSeriesCleanupCheckpointAsync(
                SeriesCleanupCheckpoint.CurrentReadModelsDeleted,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            await ExecuteSeriesCleanupStageAsync(
                connection,
                transaction,
                candidate.SeriesId,
                """
                DELETE FROM mesingest.DemandSeriesCurrentConditions WHERE SeriesId = @seriesId;
                DELETE evidence
                FROM mesingest.SeriesErrorPeriodEvidence AS evidence
                INNER JOIN mesingest.DemandSeriesErrorPeriods AS period
                    ON period.PeriodId = evidence.PeriodId
                WHERE period.SeriesId = @seriesId;
                DELETE FROM mesingest.DemandSeriesErrorPeriods WHERE SeriesId = @seriesId;
                """,
                cancellationToken).ConfigureAwait(false);
            await ObserveSeriesCleanupCheckpointAsync(
                SeriesCleanupCheckpoint.ErrorGraphDeleted,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            await ExecuteSeriesCleanupStageAsync(
                connection,
                transaction,
                candidate.SeriesId,
                "DELETE FROM mesingest.DemandSeriesEvents WHERE SeriesId = @seriesId;",
                cancellationToken).ConfigureAwait(false);
            await ObserveSeriesCleanupCheckpointAsync(
                SeriesCleanupCheckpoint.EventsDeleted,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            await ExecuteSeriesCleanupStageAsync(
                connection,
                transaction,
                candidate.SeriesId,
                """
                UPDATE mesingest.DemandSeries
                SET CurrentDemandId = NULL
                WHERE SeriesId = @seriesId;
                DELETE FROM mesingest.TransportDemands WHERE SeriesId = @seriesId;
                """,
                cancellationToken).ConfigureAwait(false);
            await ObserveSeriesCleanupCheckpointAsync(
                SeriesCleanupCheckpoint.DemandsDeleted,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            var deletedSeriesCount = await ExecuteSeriesCleanupStageAsync(
                connection,
                transaction,
                candidate.SeriesId,
                "DELETE FROM mesingest.DemandSeries WHERE SeriesId = @seriesId;",
                cancellationToken).ConfigureAwait(false);
            if (deletedSeriesCount != 1)
            {
                throw new InvalidOperationException(
                    "The retention-eligible DemandSeries root changed during atomic cleanup.");
            }
            await ObserveSeriesCleanupCheckpointAsync(
                SeriesCleanupCheckpoint.SeriesRootDeleted,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            await UpdateCurrentOverviewReadabilityAsync(
                connection,
                transaction,
                candidate.LatestProjectionCommitId,
                cancellationToken).ConfigureAwait(false);
            await ObserveSeriesCleanupCheckpointAsync(
                SeriesCleanupCheckpoint.BeforeCommit,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RetentionEligibleSeriesCleanupResult(
                cleanedAt,
                candidate.SeriesId,
                candidate.WorkType,
                candidate.Sublot,
                candidate.ArchivedAt,
                ArchivedDemandKeyTombstoneContract.Version);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ObserveSeriesCleanupCheckpointAsync(
        SeriesCleanupCheckpoint checkpoint,
        ProjectionCommitCheckpointContext context,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken) =>
        await _checkpointObserver.OnSeriesCleanupCheckpointAsync(
            checkpoint,
            context,
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);

    private static async Task<int> ExecuteSeriesCleanupStageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddNVarChar(command, "@seriesId", 64, seriesId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RefreshSeriesRetentionEligibilityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTimeOffset eligibilityAt,
        string projectionCommitId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ;WITH RetentionState AS
            (
                SELECT
                    series.SeriesId,
                    CONVERT(BIT, CASE
                        WHEN series.Lifecycle = N'ARCHIVED'
                             AND series.CurrentPresence NOT IN (N'VISIBLE', N'LONG_GONE_BUT_VISIBLE')
                             AND demand.Status NOT IN (N'VISIBLE', N'LONG_GONE_BUT_VISIBLE')
                             AND NOT EXISTS
                             (
                                 SELECT 1
                                 FROM mesingest.DemandSeriesCurrentConditions AS currentCondition
                                 WHERE currentCondition.SeriesId = series.SeriesId
                             )
                             AND NOT EXISTS
                             (
                                 SELECT 1
                                 FROM mesingest.DemandSeriesErrorPeriods AS errorPeriod
                                 WHERE errorPeriod.SeriesId = series.SeriesId
                                   AND errorPeriod.EndedAt IS NULL
                             )
                        THEN 1
                        ELSE 0
                    END) AS IsEligible,
                    CONVERT(BIT, CASE
                        WHEN EXISTS
                             (
                                 SELECT 1
                                 FROM mesingest.DemandRawObservations AS observation
                                 WHERE observation.SeriesId = series.SeriesId
                                   AND observation.ProjectionCommitId = @projectionCommitId
                             )
                             OR EXISTS
                             (
                                 SELECT 1
                                 FROM mesingest.DemandSeriesEvents AS seriesEvent
                                 WHERE seriesEvent.SeriesId = series.SeriesId
                                   AND seriesEvent.ProjectionCommitId = @projectionCommitId
                             )
                        THEN 1
                        ELSE 0
                    END) AS HadActivityThisCommit
                FROM mesingest.DemandSeries AS series
                INNER JOIN mesingest.TransportDemands AS demand
                    ON demand.DemandId = series.CurrentDemandId
            )
            UPDATE series
            SET RetentionEligibilityAt =
                CASE
                    WHEN state.IsEligible = 1 THEN @eligibilityAt
                    ELSE NULL
                END
            FROM mesingest.DemandSeries AS series
            INNER JOIN RetentionState AS state ON state.SeriesId = series.SeriesId
            WHERE (state.IsEligible = 1
                   AND (series.RetentionEligibilityAt IS NULL
                        OR state.HadActivityThisCommit = 1))
               OR (state.IsEligible = 0 AND series.RetentionEligibilityAt IS NOT NULL);
            """;
        AddDateTimeOffset(command, "@eligibilityAt", eligibilityAt.ToUniversalTime());
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record RetentionEligibleSeriesCleanupCandidate(
        string SeriesId,
        string KeyToken,
        string WorkType,
        string Sublot,
        DateTimeOffset ArchivedAt,
        string LatestPollTraceId,
        string LatestProjectionCommitId);
}
