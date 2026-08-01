using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MesIngest.Core;
using MesIngest.Host;
using MesIngest.Watch;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 14 seams: Watch↔Host contract version handshake and clear mismatch errors.
/// </summary>
public class ApiContractVersionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiContractVersionTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Host_exposes_matching_contract_version()
    {
        var path = await WriteEmptyCsvAsync();
        try
        {
            await using var factory = CreateFactory(path);
            var client = factory.CreateClient();
            var info = await client.GetFromJsonAsync<MesIngestContractInfo>("/api/contract");
            Assert.NotNull(info);
            Assert.Equal(MesIngestApiContract.Version, info!.ContractVersion);
            Assert.Equal(MesIngestApiContract.SchemaVersion, info.SchemaVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Watch_reports_clear_contract_mismatch_instead_of_blank_board()
    {
        using var host = new HttpListener();
        var prefix = $"http://127.0.0.1:{GetFreePort()}/";
        host.Prefixes.Add(prefix);
        host.Start();

        var serve = Task.Run(async () =>
        {
            var ctx = await host.GetContextAsync();
            Assert.Equal("/api/contract", ctx.Request.Url!.AbsolutePath);
            var body = Encoding.UTF8.GetBytes(
                """{"contractVersion":"ancient.0","schemaVersion":0}""");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(prefix) };
            var client = new MesIngestApiClient(http);
            var snapshot = await client.FetchSnapshotAsync();

            Assert.True(snapshot.HasFetchError);
            Assert.Contains(MesIngestApiContract.MismatchErrorCode, snapshot.FetchError, StringComparison.Ordinal);
            Assert.Contains("ancient.0", snapshot.FetchError, StringComparison.Ordinal);
            Assert.Contains(MesIngestApiContract.Version, snapshot.FetchError, StringComparison.Ordinal);
            Assert.Empty(snapshot.Demands);
            Assert.Equal("/api/contract", snapshot.FailedEndpoint);
        }
        finally
        {
            host.Stop();
            await serve;
        }
    }

    [Fact]
    public async Task Watch_reports_clear_error_when_host_lacks_contract_endpoint()
    {
        using var host = new HttpListener();
        var prefix = $"http://127.0.0.1:{GetFreePort()}/";
        host.Prefixes.Add(prefix);
        host.Start();

        var serve = Task.Run(async () =>
        {
            var ctx = await host.GetContextAsync();
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        });

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(prefix) };
            var client = new MesIngestApiClient(http);
            var snapshot = await client.FetchSnapshotAsync();

            Assert.True(snapshot.HasFetchError);
            Assert.Contains(MesIngestApiContract.MismatchErrorCode, snapshot.FetchError, StringComparison.Ordinal);
            Assert.Contains("/api/contract", snapshot.FetchError, StringComparison.Ordinal);
            Assert.Empty(snapshot.Demands);
        }
        finally
        {
            host.Stop();
            await serve;
        }
    }

    private WebApplicationFactory<Program> CreateFactory(string csvPath) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new MesIngestHostOptions
                {
                    SnapshotSource = "File",
                    SnapshotCsvPath = csvPath,
                    ContinuousPollEnabled = false,
                    Urls = "http://127.0.0.1:5088",
                    SharedSecret = "",
                });
            });
        });

    private static async Task<string> WriteEmptyCsvAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mes-contract-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n");
        return path;
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
