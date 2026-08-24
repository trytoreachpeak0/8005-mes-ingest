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

        return new HistoricalReadBoundary(
            historyEpoch,
            GetNullableDateTimeOffset(reader, 1));
    }
}
