using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Http;
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
            () => SharedSecretAuth.ValidateStartup(options, EmptyConfiguration()));
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

        var policy = SharedSecretAuth.ValidateStartup(options, EmptyConfiguration());

        Assert.False(policy.RequiresSharedSecret);
        Assert.Equal(options.Urls, policy.Urls);
    }

    [Theory]
    [InlineData("urls")]
    [InlineData("http_ports")]
    [InlineData("https_ports")]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("ASPNETCORE_HTTP_PORTS")]
    [InlineData("ASPNETCORE_HTTPS_PORTS")]
    [InlineData("DOTNET_URLS")]
    [InlineData("DOTNET_HTTP_PORTS")]
    [InlineData("DOTNET_HTTPS_PORTS")]
    public void Alternate_endpoint_authority_fails_validation(string key)
    {
        var options = new MesIngestHostOptions
        {
            Urls = "http://127.0.0.1:5088",
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = "http://0.0.0.0:5089",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => SharedSecretAuth.ValidateStartup(options, config));
        Assert.Contains(key, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MesIngest:Urls", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Kestrel_endpoints_fail_validation_even_when_MesIngest_urls_are_loopback()
    {
        var options = new MesIngestHostOptions
        {
            Urls = "http://127.0.0.1:5088",
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Remote:Url"] = "http://0.0.0.0:5089",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => SharedSecretAuth.ValidateStartup(options, config));
        Assert.Contains("Kestrel:Endpoints", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MesIngest:Urls", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_MesIngest_urls_fails_instead_of_using_a_Kestrel_default()
    {
        var options = new MesIngestHostOptions
        {
            Urls = " ",
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => SharedSecretAuth.ValidateStartup(options, EmptyConfiguration()));

        Assert.Contains("MesIngest:Urls is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Authorization_consumes_the_immutable_startup_policy_after_options_change()
    {
        var options = new MesIngestHostOptions
        {
            Urls = "http://0.0.0.0:5088",
            SharedSecret = "plant-secret",
        };
        var policy = SharedSecretAuth.ValidateStartup(options, EmptyConfiguration());
        options.Urls = "http://127.0.0.1:5088";
        options.SharedSecret = "changed-after-startup";
        var request = new DefaultHttpContext().Request;

        Assert.True(policy.RequiresSharedSecret);
        Assert.False(SharedSecretAuth.IsAuthorized(request, policy));

        request.Headers.Authorization = "Bearer plant-secret";
        Assert.True(SharedSecretAuth.IsAuthorized(request, policy));

        request.Headers.Authorization = "Bearer changed-after-startup";
        Assert.False(SharedSecretAuth.IsAuthorized(request, policy));
    }

    private WebApplicationFactory<Program> CreateFactory(string urls, string sharedSecret) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting(
                $"{MesIngestHostOptions.SectionName}:NewSqlServerConnectionString",
                "Server=auth.invalid;Database=auth;Integrated Security=true;Encrypt=false");
            builder.UseSetting(
                $"{MesIngestHostOptions.SectionName}:SnapshotSource",
                MesIngestHostOptions.NoRoundSource);
            builder.UseSetting($"{MesIngestHostOptions.SectionName}:ContinuousPollEnabled", "false");
            builder.UseSetting($"{MesIngestHostOptions.SectionName}:RunOneShotOnStartup", "false");
            builder.UseSetting($"{MesIngestHostOptions.SectionName}:Urls", urls);
            builder.UseSetting($"{MesIngestHostOptions.SectionName}:SharedSecret", sharedSecret);
            // Admission is decided before routing. Dropping the hosted services keeps
            // this fixture off a real SQL Server without weakening what it asserts.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        });

    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().Build();
}
