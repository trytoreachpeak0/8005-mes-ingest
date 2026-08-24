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
        var cutoff = advancedAt.Subtract(HistoryRetentionPolicy.AvailabilityWindow);

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

    private static async Task RefreshSeriesRetentionEligibilityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTimeOffset eligibilityAt,
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
                    END) AS IsEligible
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
            WHERE (state.IsEligible = 1 AND series.RetentionEligibilityAt IS NULL)
               OR (state.IsEligible = 0 AND series.RetentionEligibilityAt IS NOT NULL);
            """;
        AddDateTimeOffset(command, "@eligibilityAt", eligibilityAt.ToUniversalTime());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
