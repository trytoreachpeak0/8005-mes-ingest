using System.IO;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

/// <summary>
/// Headless validation entry that exercises the packaged Watch query client without
/// opening a window. It is accepted only through an explicit Ticket 28 switch and
/// writes a create-new, business-content-free receipt.
/// </summary>
internal static class WatchStabilityReadProbe
{
    private const string Switch = "--ticket28-stability-probe";

    public static async Task<int?> TryRunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0 || !string.Equals(arguments[0], Switch, StringComparison.Ordinal))
        {
            return null;
        }

        if (arguments.Count != 4)
        {
            return 2;
        }

        try
        {
            var secret = Environment.GetEnvironmentVariable(arguments[2]);
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException("The named probe credential is unavailable.");
            }

            var outputPath = Path.GetFullPath(arguments[3]);
            using var client = MesIngestV2ApiClient.CreateForHost(
                new WatchHostSettings(arguments[1], secret, requestTimeoutSeconds: 30));
            await client.VerifyContractAsync(cancellationToken).ConfigureAwait(false);
            var overview = await client.FetchOverviewAsync(
                new WatchOverviewQuery(),
                cancellationToken).ConfigureAwait(false);
            var demandSeries = await client.FetchDemandSeriesAsync(
                new DemandSeriesBrowseQuery(new DemandSeriesBrowseFilter()),
                cancellationToken).ConfigureAwait(false);
            var audit = await client.FetchReadabilityAuditAsync(
                new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
                cancellationToken).ConfigureAwait(false);
            var errors = await client.FetchErrorSearchAsync(
                new ErrorSearchQuery(new ErrorSearchFilter(), ErrorSearchWindowSelection.Last7Days),
                cancellationToken).ConfigureAwait(false);
            var attention = await client.FetchCurrentAttentionAsync(
                new CurrentIngestAttentionQuery(),
                cancellationToken).ConfigureAwait(false);

            await using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            await JsonSerializer.SerializeAsync(
                output,
                new
                {
                    schemaVersion = 1,
                    passed = true,
                    readCount = 5,
                    historyEpoch = overview.Snapshot.HistoryEpoch,
                    overviewProjectionCommitId = overview.Snapshot.ProjectionCommitId,
                    demandSeriesProjectionCommitId = demandSeries.Snapshot.ProjectionCommitId,
                    auditProjectionCommitId = audit.Snapshot.ProjectionCommitId,
                    errorSearchProjectionCommitId = errors.Snapshot.ProjectionCommitId,
                    currentAttentionProjectionCommitId = attention.Snapshot.ProjectionCommitId,
                    demandSeriesCount = demandSeries.Items.Count,
                    auditCount = audit.Items.Count,
                    errorCount = errors.Items.Count,
                    attentionCount = attention.Items.Count,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"WATCH_STABILITY_PROBE_FAILED:{exception.GetType().Name}");
            return 1;
        }
    }
}
