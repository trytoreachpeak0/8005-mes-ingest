using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

/// <summary>
/// SharedSecret admission for the only published read surface. Contract discovery is
/// the cheapest business read on that surface: it needs no projection round trip, so
/// these tests prove the admission decision itself rather than a query result.
/// </summary>
public class ReadApiSharedSecretAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ContractPath = "/api/v2/contract";

    private readonly WebApplicationFactory<Program> _factory;

    public ReadApiSharedSecretAuthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Localhost_binding_allows_api_without_shared_secret()
    {
        await using var factory = CreateFactory(urls: "http://127.0.0.1:5088", sharedSecret: "");
        var client = factory.CreateClient();

        var response = await client.GetAsync(ContractPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Non_localhost_binding_rejects_request_without_shared_secret()
    {
        await using var factory = CreateFactory(urls: "http://0.0.0.0:5088", sharedSecret: "plant-secret");
        var client = factory.CreateClient();

        var response = await client.GetAsync(ContractPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("UNAUTHORIZED", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Non_localhost_binding_rejects_wrong_shared_secret()
    {
        await using var factory = CreateFactory(urls: "http://192.168.1.10:5088", sharedSecret: "plant-secret");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "wrong-secret");

        var response = await client.GetAsync(ContractPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Non_localhost_binding_accepts_correct_bearer_shared_secret()
    {
        await using var factory = CreateFactory(urls: "http://0.0.0.0:5088", sharedSecret: "plant-secret");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "plant-secret");

        var contract = await client.GetFromJsonAsync<JsonElement>(ContractPath);

        Assert.Equal(
            NewMesIngestContract.Version,
            contract.GetProperty("contractVersion").GetString());
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

    private WebApplicationFactory<Program> CreateFactory(string urls, string sharedSecret) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.UseSetting(
                $"{MesIngestHostOptions.SectionName}:NewSqlServerConnectionString",
                "Server=auth.invalid;Database=auth;Integrated Security=true;Encrypt=false");
            builder.UseSetting(
                $"{MesIngestHostOptions.SectionName}:SnapshotSource",
                MesIngestHostOptions.NoRoundSource);
            builder.UseSetting($"{MesIngestHostOptions.SectionName}:ContinuousPollEnabled", "false");
            builder.UseSetting($"{MesIngestHostOptions.SectionName}:RunOneShotOnStartup", "false");
            // Admission is decided before routing. Dropping the hosted services keeps
            // this fixture off a real SQL Server without weakening what it asserts.
            // The binding and the secret are injected as the resolved options object
            // because that is exactly what the admission middleware reads.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<MesIngestHostOptions>();
                services.AddSingleton(new MesIngestHostOptions
                {
                    NewSqlServerConnectionString =
                        "Server=auth.invalid;Database=auth;Integrated Security=true;Encrypt=false",
                    SnapshotSource = MesIngestHostOptions.NoRoundSource,
                    ContinuousPollEnabled = false,
                    RunOneShotOnStartup = false,
                    Urls = urls,
                    SharedSecret = sharedSecret,
                });
            });
        });
}
