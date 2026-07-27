using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MesIngest.Core;
using MesIngest.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MesIngest.Tests;

public class ReadApiSharedSecretAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReadApiSharedSecretAuthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Localhost_binding_allows_api_without_shared_secret()
    {
        var path = await WriteEmptyCsvAsync();
        try
        {
            await using var factory = CreateFactory(path, urls: "http://127.0.0.1:5088", sharedSecret: "");
            var client = factory.CreateClient();

            var response = await client.GetAsync("/api/demands");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Non_localhost_binding_rejects_request_without_shared_secret()
    {
        var path = await WriteEmptyCsvAsync();
        try
        {
            await using var factory = CreateFactory(
                path,
                urls: "http://0.0.0.0:5088",
                sharedSecret: "plant-secret");
            var client = factory.CreateClient();

            var response = await client.GetAsync("/api/demands");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Non_localhost_binding_rejects_wrong_shared_secret()
    {
        var path = await WriteEmptyCsvAsync();
        try
        {
            await using var factory = CreateFactory(
                path,
                urls: "http://192.168.1.10:5088",
                sharedSecret: "plant-secret");
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "wrong-secret");

            var response = await client.GetAsync("/api/alerts");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Non_localhost_binding_accepts_correct_bearer_shared_secret()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "auth-1",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q-AUTH",
                Area = "N01-01",
                Eqp = "EQ1",
                Step = "烘箱",
                Dates = now,
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                DisappearCount = 0,
            },
        ]));

        var path = await WriteEmptyCsvAsync();
        try
        {
            await using var factory = CreateFactory(
                path,
                urls: "http://0.0.0.0:5088",
                sharedSecret: "plant-secret",
                store: store);
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "plant-secret");

            var list = await client.GetFromJsonAsync<JsonElement>("/api/demands");
            Assert.Equal(1, list.GetArrayLength());
            Assert.Equal("auth-1", list[0].GetProperty("demandId").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Non_localhost_without_shared_secret_fails_validation()
    {
        var options = new MesIngestHostOptions
        {
            Urls = "http://0.0.0.0:5088",
            SharedSecret = "",
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => SharedSecretAuth.ValidateStartup(options));
        Assert.Contains("SharedSecret", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Localhost_without_shared_secret_passes_validation()
    {
        var options = new MesIngestHostOptions
        {
            Urls = "http://127.0.0.1:5088",
            SharedSecret = "",
        };

        SharedSecretAuth.ValidateStartup(options);
    }

    [Fact]
    public void Empty_mesingest_urls_still_requires_secret_when_aspnetcore_urls_are_non_localhost()
    {
        var options = new MesIngestHostOptions
        {
            Urls = "",
            SharedSecret = "",
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["urls"] = "http://0.0.0.0:5088",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => SharedSecretAuth.ValidateStartup(options, config));
        Assert.Contains("SharedSecret", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private WebApplicationFactory<Program> CreateFactory(
        string csvPath,
        string urls,
        string sharedSecret,
        ITransportDemandStore? store = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new MesIngestHostOptions
                {
                    SnapshotCsvPath = csvPath,
                    GoLiveBaseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8)),
                    RunOneShotOnStartup = false,
                    ContinuousPollEnabled = false,
                    Urls = urls,
                    SharedSecret = sharedSecret,
                });
                if (store is not null)
                {
                    services.AddSingleton(store);
                }
            });
        });
    }

    private static async Task<string> WriteEmptyCsvAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mes-ingest-auth-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n", Encoding.UTF8);
        return path;
    }
}
