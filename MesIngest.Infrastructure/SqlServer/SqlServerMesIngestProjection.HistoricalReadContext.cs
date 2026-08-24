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
        CancellationToken cancellationToken,
        DateTimeOffset? rawAvailabilityCutoff = null)
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
        var unavailableWhenSnapshotWasCreated = rawAvailabilityCutoff is not null
            && completedAt <= rawAvailabilityCutoff.Value;
        if (!unavailableWhenSnapshotWasCreated
            && (wasPhysicallyExpired
                || HistoryRetentionPolicy.IsRawObservationExpired(
                completedAt,
                _timeProvider.GetUtcNow())))
        {
            throw new MesIngestHistoryExpiredException(
                await ReadHistoricalReadBoundaryAsync(
                    connection,
                    transaction,
                    cancellationToken).ConfigureAwait(false));
        }
    }

    private static async Task<bool> IsLatestProjectionSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long snapshotSequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CONVERT(BIT, CASE
                WHEN @snapshotSequence = (SELECT MAX(ProjectionSequence)
                                           FROM mesingest.ProjectionCommits)
                    THEN 1 ELSE 0 END);
            """;
        command.Parameters.Add("@snapshotSequence", System.Data.SqlDbType.BigInt).Value =
            snapshotSequence;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false))!;
    }

    private async Task EnsureHistoricalSnapshotRetentionLeaseAvailableAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long snapshotSequence,
        DateTimeOffset rawAvailabilityCutoff,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MIN(trace.CompletedAt)
            FROM mesingest.ProjectionCommits AS projectionCommit
            INNER JOIN mesingest.PollTraces AS trace
                ON trace.PollTraceId = projectionCommit.PollTraceId
            WHERE projectionCommit.ProjectionSequence <= @snapshotSequence
              AND trace.[RowCount] > 0
              AND trace.CompletedAt > @rawAvailabilityCutoff;
            """;
        command.Parameters.Add("@snapshotSequence", System.Data.SqlDbType.BigInt).Value =
            snapshotSequence;
        AddDateTimeOffset(command, "@rawAvailabilityCutoff", rawAvailabilityCutoff);
        var earliestContributingCompletedAt = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (earliestContributingCompletedAt is DateTimeOffset completedAt
            && HistoryRetentionPolicy.IsRawObservationExpired(
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

    private async Task<DateTimeOffset> ReadSnapshotRawAvailabilityCutoffAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long snapshotSequence,
        CancellationToken cancellationToken)
    {
        var policyCutoff = _timeProvider.GetUtcNow().ToUniversalTime().Subtract(
            HistoryRetentionPolicy.RawObservationAvailabilityWindow);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MAX(trace.CompletedAt)
            FROM mesingest.ProjectionCommits AS projectionCommit
            INNER JOIN mesingest.PollTraces AS trace
                ON trace.PollTraceId = projectionCommit.PollTraceId
            WHERE projectionCommit.ProjectionSequence <= @snapshotSequence
              AND (trace.RawObservationsExpiredAt IS NOT NULL
                   OR trace.CompletedAt <= @policyCutoff);
            """;
        command.Parameters.Add("@snapshotSequence", System.Data.SqlDbType.BigInt).Value =
            snapshotSequence;
        AddDateTimeOffset(command, "@policyCutoff", policyCutoff);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DateTimeOffset cutoff
            ? cutoff.ToUniversalTime()
            : DateTimeOffset.MinValue;
    }

}
