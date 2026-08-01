using System.Net;
using System.Text;
using MesIngest.Core;

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
