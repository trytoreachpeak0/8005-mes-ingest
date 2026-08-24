using System.Data;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    private const string HistoryCleanupStateSelectSql = """
        SELECT
            state.HistoryCleanupStatus, state.HistoryCleanupRunId,
            state.HistoryCleanupLastStartedAt, state.HistoryCleanupLastCompletedAt,
            state.HistoryCleanupLastSuccessfulAt, state.HistoryCleanupNextCheckAt,
            state.HistoryCleanupLastExpiredPollTraceCount,
            state.HistoryCleanupLastDeletedRawObservationCount,
            state.HistoryCleanupLastDeletedSeriesCount,
            state.HistoryCleanupTotalExpiredPollTraceCount,
            state.HistoryCleanupTotalDeletedRawObservationCount,
            state.HistoryCleanupTotalDeletedSeriesCount,
            schemaInfo.EarliestAvailableHostUtc,
            state.HistoryCleanupLastFailureCode, state.HistoryCleanupLastFailureReason,
            state.HistoryCleanupLastFailureAt, state.HistoryCleanupLastFailureRunId
        FROM mesingest.HistoryCleanupState AS state
        INNER JOIN mesingest.SchemaInfo AS schemaInfo ON schemaInfo.Id = state.Id
        WHERE state.Id = 1;
        """;

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

    public async Task<HistoryRawCleanupBatchResult> AdvanceHistoryRetentionBatchAsync(
        string runId,
        int maximumRawObservationRows,
        int maximumPollTraces,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(runId, nameof(runId), 64);
        if (maximumRawObservationRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRawObservationRows));
        }

        if (maximumPollTraces <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPollTraces));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var advancedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        var cutoff = advancedAt.Subtract(HistoryRetentionPolicy.RawObservationAvailabilityWindow);

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

                ;WITH Due AS
                (
                    SELECT TOP (@maximumPollTraces)
                        trace.PollTraceId,
                        trace.CompletedAt,
                        CONVERT(BIGINT, trace.[RowCount]) AS ObservationCount
                    FROM mesingest.PollTraces AS trace WITH (UPDLOCK, HOLDLOCK)
                    WHERE trace.RawObservationsExpiredAt IS NULL
                      AND trace.CompletedAt <= @cutoff
                    ORDER BY trace.CompletedAt, trace.PollTraceId
                ),
                Ranked AS
                (
                    SELECT
                        PollTraceId,
                        CompletedAt,
                        ObservationCount,
                        SUM(ObservationCount) OVER
                            (ORDER BY CompletedAt, PollTraceId ROWS UNBOUNDED PRECEDING) AS RunningRows
                    FROM Due
                )
                INSERT INTO @expired (PollTraceId)
                SELECT PollTraceId
                FROM Ranked
                WHERE RunningRows <= @maximumRawObservationRows
                ORDER BY CompletedAt, PollTraceId;

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

                UPDATE mesingest.HistoryCleanupState
                SET
                    HistoryCleanupLastExpiredPollTraceCount =
                        HistoryCleanupLastExpiredPollTraceCount + @expiredPollTraceCount,
                    HistoryCleanupLastDeletedRawObservationCount =
                        HistoryCleanupLastDeletedRawObservationCount + @deletedRawObservationCount,
                    HistoryCleanupTotalExpiredPollTraceCount =
                        HistoryCleanupTotalExpiredPollTraceCount + @expiredPollTraceCount,
                    HistoryCleanupTotalDeletedRawObservationCount =
                        HistoryCleanupTotalDeletedRawObservationCount + @deletedRawObservationCount
                WHERE Id = 1 AND HistoryCleanupRunId = @runId;

                IF @@ROWCOUNT <> 1
                    THROW 51042, 'The history cleanup run identity changed during raw retention.', 1;

                SELECT
                    @expiredPollTraceCount,
                    @deletedRawObservationCount,
                    EarliestAvailableHostUtc,
                    CONVERT(BIT, CASE WHEN EXISTS
                    (
                        SELECT 1
                        FROM mesingest.PollTraces
                        WHERE RawObservationsExpiredAt IS NULL
                          AND CompletedAt <= @cutoff
                    ) THEN 1 ELSE 0 END)
                FROM mesingest.SchemaInfo
                WHERE Id = 1;
                """;
            AddDateTimeOffset(command, "@advancedAt", advancedAt);
            AddDateTimeOffset(command, "@cutoff", cutoff);
            AddNVarChar(command, "@runId", 64, runId);
            command.Parameters.Add("@maximumRawObservationRows", SqlDbType.Int).Value =
                maximumRawObservationRows;
            command.Parameters.Add("@maximumPollTraces", SqlDbType.Int).Value =
                maximumPollTraces;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The bounded history-retention advance did not return progress.");
            }

            var result = new HistoryRawCleanupBatchResult(
                advancedAt,
                cutoff,
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetBoolean(3));
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

    public Task<RetentionEligibleSeriesCleanupResult?> CleanupNextRetentionEligibleSeriesAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(runId, nameof(runId), 64);
        return CleanupNextRetentionEligibleSeriesCoreAsync(runId, cancellationToken);
    }

    public Task<RetentionEligibleSeriesCleanupResult?> CleanupNextRetentionEligibleSeriesAsync(
        CancellationToken cancellationToken = default) =>
        CleanupNextRetentionEligibleSeriesCoreAsync(runId: null, cancellationToken);

    private async Task<RetentionEligibleSeriesCleanupResult?> CleanupNextRetentionEligibleSeriesCoreAsync(
        string? runId,
        CancellationToken cancellationToken)
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

            if (runId is not null)
            {
                await using var progress = connection.CreateCommand();
                progress.Transaction = transaction;
                progress.CommandText = """
                    UPDATE mesingest.HistoryCleanupState
                    SET HistoryCleanupLastDeletedSeriesCount =
                            HistoryCleanupLastDeletedSeriesCount + 1,
                        HistoryCleanupTotalDeletedSeriesCount =
                            HistoryCleanupTotalDeletedSeriesCount + 1
                    WHERE Id = 1 AND HistoryCleanupRunId = @runId;
                    """;
                AddNVarChar(progress, "@runId", 64, runId);
                if (await progress.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        "The history cleanup run identity changed during Series cleanup.");
                }
            }

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

    public async Task<HistoryCleanupStateSnapshot> BeginHistoryCleanupRunAsync(
        string runId,
        DateTimeOffset startedAt,
        DateTimeOffset nextCheckAt,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(runId, nameof(runId), 64);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mesingest.HistoryCleanupState
            SET HistoryCleanupStatus = N'RUNNING',
                HistoryCleanupRunId = @runId,
                HistoryCleanupLastStartedAt = @startedAt,
                HistoryCleanupLastCompletedAt = NULL,
                HistoryCleanupNextCheckAt = @nextCheckAt,
                HistoryCleanupLastExpiredPollTraceCount = 0,
                HistoryCleanupLastDeletedRawObservationCount = 0,
                HistoryCleanupLastDeletedSeriesCount = 0
            WHERE Id = 1;

            """ + HistoryCleanupStateSelectSql;
        AddNVarChar(command, "@runId", 64, runId);
        AddDateTimeOffset(command, "@startedAt", startedAt.ToUniversalTime());
        AddDateTimeOffset(command, "@nextCheckAt", nextCheckAt.ToUniversalTime());
        return await ExecuteHistoryCleanupStateReaderAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<HistoryCleanupStateSnapshot> CompleteHistoryCleanupRunAsync(
        string runId,
        string status,
        DateTimeOffset completedAt,
        DateTimeOffset nextCheckAt,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(runId, nameof(runId), 64);
        if (status is not (HistoryCleanupRunStatuses.Succeeded
            or HistoryCleanupRunStatuses.BudgetExhausted
            or HistoryCleanupRunStatuses.YieldedToPoll
            or HistoryCleanupRunStatuses.Interrupted))
        {
            throw new ArgumentException("The cleanup completion status is invalid.", nameof(status));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mesingest.HistoryCleanupState
            SET HistoryCleanupStatus = @status,
                HistoryCleanupLastCompletedAt = @completedAt,
                HistoryCleanupLastSuccessfulAt =
                    CASE WHEN @status = N'INTERRUPTED'
                         THEN HistoryCleanupLastSuccessfulAt
                         ELSE @completedAt END,
                HistoryCleanupNextCheckAt = @nextCheckAt,
                HistoryCleanupLastFailureCode =
                    CASE WHEN @status = N'INTERRUPTED'
                         THEN HistoryCleanupLastFailureCode ELSE NULL END,
                HistoryCleanupLastFailureReason =
                    CASE WHEN @status = N'INTERRUPTED'
                         THEN HistoryCleanupLastFailureReason ELSE NULL END,
                HistoryCleanupLastFailureAt =
                    CASE WHEN @status = N'INTERRUPTED'
                         THEN HistoryCleanupLastFailureAt ELSE NULL END,
                HistoryCleanupLastFailureRunId =
                    CASE WHEN @status = N'INTERRUPTED'
                         THEN HistoryCleanupLastFailureRunId ELSE NULL END
            WHERE Id = 1 AND HistoryCleanupRunId = @runId;

            IF @@ROWCOUNT <> 1
                THROW 51042, 'The history cleanup run identity changed before completion.', 1;

            """ + HistoryCleanupStateSelectSql;
        AddNVarChar(command, "@runId", 64, runId);
        AddNVarChar(command, "@status", 32, status);
        AddDateTimeOffset(command, "@completedAt", completedAt.ToUniversalTime());
        AddDateTimeOffset(command, "@nextCheckAt", nextCheckAt.ToUniversalTime());
        return await ExecuteHistoryCleanupStateReaderAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<HistoryCleanupStateSnapshot> FailHistoryCleanupRunAsync(
        string runId,
        DateTimeOffset failedAt,
        DateTimeOffset nextCheckAt,
        string failureCode,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(runId, nameof(runId), 64);
        ValidateRequiredText(failureCode, nameof(failureCode), 128);
        ValidateRequiredText(failureReason, nameof(failureReason), 256);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mesingest.HistoryCleanupState
            SET HistoryCleanupStatus = N'FAILED',
                HistoryCleanupLastCompletedAt = @failedAt,
                HistoryCleanupNextCheckAt = @nextCheckAt,
                HistoryCleanupLastFailureCode = @failureCode,
                HistoryCleanupLastFailureReason = @failureReason,
                HistoryCleanupLastFailureAt = @failedAt,
                HistoryCleanupLastFailureRunId = @runId
            WHERE Id = 1 AND HistoryCleanupRunId = @runId;

            IF @@ROWCOUNT <> 1
                THROW 51042, 'The history cleanup run identity changed before failure recording.', 1;

            """ + HistoryCleanupStateSelectSql;
        AddNVarChar(command, "@runId", 64, runId);
        AddDateTimeOffset(command, "@failedAt", failedAt.ToUniversalTime());
        AddDateTimeOffset(command, "@nextCheckAt", nextCheckAt.ToUniversalTime());
        AddNVarChar(command, "@failureCode", 128, failureCode);
        AddNVarChar(command, "@failureReason", 256, failureReason);
        return await ExecuteHistoryCleanupStateReaderAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<HistoryCleanupStateSnapshot> ReadHistoryCleanupStateAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = HistoryCleanupStateSelectSql;
        return await ExecuteHistoryCleanupStateReaderAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<HistoryCleanupStateSnapshot> ExecuteHistoryCleanupStateReaderAsync(
        SqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The history cleanup state row is missing.");
        }

        return new HistoryCleanupStateSnapshot(
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
            reader.IsDBNull(14) ? null : reader.GetString(14),
            ReadNullableDateTimeOffset(reader, 15),
            reader.IsDBNull(16) ? null : reader.GetString(16));
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

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
