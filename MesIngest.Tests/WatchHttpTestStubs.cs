using System.Net;
using System.Text;
using MesIngest.Core;
using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>Shared stub responses so Watch client stubs survive the /api/contract handshake.</summary>
internal static class WatchHttpTestStubs
{
    public static string MatchingContractJson { get; } =
        $$"""{"contractVersion":"{{MesIngestApiContract.Version}}","schemaVersion":{{MesIngestApiContract.SchemaVersion}}}""";

    public static HttpResponseMessage MatchingContract(string path) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(MatchingContractJson, Encoding.UTF8, "application/json"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5088" + path),
        };

    public static bool IsContractPath(string path) =>
        path.EndsWith("/api/contract", StringComparison.Ordinal);
}

internal abstract class WatchHostQueryAdapterStub : IWatchHostQueryAdapter
{
    public abstract Task VerifyContractAsync(CancellationToken cancellationToken);
    public abstract Task<WatchPollHealthDto?> FetchPollHealthAsync(CancellationToken cancellationToken);

    public virtual Task<WatchSnapshot> FetchSnapshotAsync(
        WatchDemandBrowseQuery demandQuery,
        WatchAlertBrowseQuery alertQuery,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WatchSnapshot([], [], null, null,
            DemandsSucceeded: true,
            AlertsSucceeded: true,
            PollHealthSucceeded: true));

    public virtual Task<WatchDemandPage> FetchDemandPageAsync(
        WatchDemandBrowseQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WatchDemandPage([], null, false));

    public virtual Task<WatchAlertPage> FetchAlertPageAsync(
        WatchAlertBrowseQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WatchAlertPage([], null, false));

    public virtual Task<WatchDemandDto?> FetchDemandByIdAsync(
        string demandId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<WatchDemandDto?>(null);

    public virtual void Dispose()
    {
    }
}
