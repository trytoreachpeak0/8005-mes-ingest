using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    private async Task<HistoricalReadBoundary> ReadHistoricalReadBoundaryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT HistoryEpoch, EarliestAvailableHostUtc
            FROM mesingest.SchemaInfo
            WHERE Id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The historical read boundary identity is unavailable.");
        }

        var historyEpoch = HistoryEpoch.FromGuid(reader.GetGuid(0));
        if (historyEpoch != _historyEpoch)
        {
            throw new InvalidOperationException(
                "The historical read boundary belongs to another HistoryEpoch.");
        }

        var storedEarliest = GetNullableDateTimeOffset(reader, 1);
        var policyBoundary = _timeProvider.GetUtcNow().ToUniversalTime().Subtract(
            HistoryRetentionPolicy.RawObservationAvailabilityWindow);
        var earliestAvailableHostUtc = storedEarliest is null
            || policyBoundary > storedEarliest.Value
                ? policyBoundary
                : storedEarliest;
        return new HistoricalReadBoundary(historyEpoch, earliestAvailableHostUtc);
    }

    private async Task EnsureHistoricalPollTraceAvailableAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string pollTraceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CompletedAt, RawObservationsExpiredAt
            FROM mesingest.PollTraces
            WHERE PollTraceId = @pollTraceId;
            """;
        AddNVarChar(command, "@pollTraceId", 128, pollTraceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The resolved historical snapshot has no PollTrace identity.");
        }

        var completedAt = reader.GetFieldValue<DateTimeOffset>(0);
        var wasPhysicallyExpired = !reader.IsDBNull(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (wasPhysicallyExpired
            || HistoryRetentionPolicy.IsRawObservationExpired(
                completedAt,
                _timeProvider.GetUtcNow()))
        {
            throw new MesIngestHistoryExpiredException(
                await ReadHistoricalReadBoundaryAsync(
                    connection,
                    transaction,
                    cancellationToken).ConfigureAwait(false));
        }
    }
}
