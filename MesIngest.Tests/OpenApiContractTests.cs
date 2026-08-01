using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MesIngest.Core;
using MesIngest.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 12 seams: live OpenAPI/Swagger HTTP surface, auth boundary vs /api/*,
/// and static pack OpenAPI path/security parity.
/// </summary>
public class OpenApiContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OpenApiContractTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OpenApi_json_is_public_even_when_shared_secret_is_required()
    {
        var path = await WriteEmptyCsvAsync();
        try
        {
            await using var factory = CreateFactory(
                path,
                urls: "http://0.0.0.0:5088",
                sharedSecret: "plant-secret");
            var client = factory.CreateClient();

            var openApi = await client.GetAsync("/openapi/v1.json");
            Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
            Assert.Contains("application/json", openApi.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);

            var swagger = await client.GetAsync("/swagger/index.html");
            Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);

            var api = await client.GetAsync("/api/demands");
            Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenApi_document_lists_read_endpoints_with_bearer_security_and_semantics()
    {
        var path = await WriteEmptyCsvAsync();
        try
        {
            await using var factory = CreateFactory(
                path,
                urls: "http://127.0.0.1:5088",
                sharedSecret: "");
            var client = factory.CreateClient();

            using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
            var root = doc.RootElement;

            Assert.Equal("3.0.1", root.GetProperty("openapi").GetString());
            AssertPaths(root, MesIngestOpenApi.ApiPaths);
            AssertInfoSemantics(root);
            AssertBearerSecurityScheme(root);
            AssertNoWriteOperations(root);
            AssertErrorResponsesDocumented(root);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenApi_bearer_authorize_allows_try_it_out_get_against_api()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "openapi-1",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q-OA",
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

            var response = await client.GetAsync("/api/demands");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Static_pack_openapi_matches_expected_paths_security_and_is_staged_by_publish()
    {
        var staticPath = Path.Combine(PackRoot, "openapi", "v1.json");
        Assert.True(File.Exists(staticPath), $"Missing static OpenAPI at {staticPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(staticPath));
        AssertPaths(doc.RootElement, MesIngestOpenApi.ApiPaths);
        AssertBearerSecurityScheme(doc.RootElement);
        AssertNoWriteOperations(doc.RootElement);
        AssertInfoSemantics(doc.RootElement);
        AssertErrorResponsesDocumented(doc.RootElement);

        var publish = File.ReadAllText(Path.Combine(PackRoot, "Publish-MesIngest.ps1"));
        Assert.Contains("openapi", publish, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("v1.json", publish, StringComparison.OrdinalIgnoreCase);

        var install = File.ReadAllText(Path.Combine(PackRoot, "INSTALL.md"));
        Assert.Contains("/swagger", install, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/openapi/v1.json", install, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_static_pack_openapi_when_MES_INGEST_EXPORT_OPENAPI_is_1()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MES_INGEST_EXPORT_OPENAPI"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var path = await WriteEmptyCsvAsync();
        try
        {
            await using var factory = CreateFactory(
                path,
                urls: "http://127.0.0.1:5088",
                sharedSecret: "");
            var client = factory.CreateClient();
            var json = await client.GetStringAsync("/openapi/v1.json");
            var staticPath = Path.Combine(PackRoot, "openapi", "v1.json");
            await File.WriteAllTextAsync(staticPath, json);
            Assert.True(File.Exists(staticPath));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Live_openapi_paths_match_static_pack_openapi()
    {
        var path = await WriteEmptyCsvAsync();
        try
        {
            await using var factory = CreateFactory(
                path,
                urls: "http://127.0.0.1:5088",
                sharedSecret: "");
            var client = factory.CreateClient();
            using var live = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
            using var packed = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(PackRoot, "openapi", "v1.json")));

            var livePaths = PathKeys(live.RootElement).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var packedPaths = PathKeys(packed.RootElement).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(packedPaths, livePaths);

            Assert.Equal(
                live.RootElement.GetProperty("info").GetProperty("description").GetString(),
                packed.RootElement.GetProperty("info").GetProperty("description").GetString());

            AssertBearerSecurityScheme(live.RootElement);
            AssertBearerSecurityScheme(packed.RootElement);
            AssertErrorResponsesDocumented(live.RootElement);
            AssertErrorResponsesDocumented(packed.RootElement);
        }
        finally
        {
            File.Delete(path);
        }
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

    private static string PackRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var pack = Path.Combine(dir.FullName, "pack");
                if (Directory.Exists(pack) && File.Exists(Path.Combine(pack, "Publish-MesIngest.ps1")))
                {
                    return pack;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("Could not locate mes/ingest/csharp/pack from test base directory.");
        }
    }

    private static void AssertPaths(JsonElement root, IReadOnlyList<string> expected)
    {
        var paths = root.GetProperty("paths");
        foreach (var expectedPath in expected)
        {
            Assert.True(
                paths.TryGetProperty(expectedPath, out var pathItem),
                $"OpenAPI missing path {expectedPath}");
            Assert.True(pathItem.TryGetProperty("get", out _), $"OpenAPI path {expectedPath} must expose GET");
        }
    }

    private static void AssertInfoSemantics(JsonElement root)
    {
        var description = root.GetProperty("info").GetProperty("description").GetString() ?? "";
        Assert.Contains("dates", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("current-step entered", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("step", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next process", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mesLastSeenAt", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("goneAt", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timezone", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATED", description, StringComparison.Ordinal);
        Assert.Contains("GONE", description, StringComparison.Ordinal);
        Assert.Contains("48", description, StringComparison.Ordinal);
        Assert.Contains("Bootstrap", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("documentation metadata", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SharedSecret", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("200", description, StringComparison.Ordinal);
        Assert.Contains("read-only", description, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertBearerSecurityScheme(JsonElement root)
    {
        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("Bearer", out var bearer));
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
    }

    private static void AssertErrorResponsesDocumented(JsonElement root)
    {
        var demands = root.GetProperty("paths").GetProperty("/api/demands").GetProperty("get").GetProperty("responses");
        Assert.True(demands.TryGetProperty("400", out _));
        Assert.True(demands.TryGetProperty("401", out _));

        var byId = root.GetProperty("paths").GetProperty("/api/demands/{demandId}").GetProperty("get").GetProperty("responses");
        Assert.True(byId.TryGetProperty("404", out _));

        var changes = root.GetProperty("paths").GetProperty("/api/demand-changes").GetProperty("get").GetProperty("responses");
        Assert.True(changes.TryGetProperty("410", out _));

        var demandGet = root.GetProperty("paths").GetProperty("/api/demands").GetProperty("get");
        var demandText = demandGet.GetProperty("description").GetString() ?? "";
        Assert.Contains("Example:", demandText, StringComparison.Ordinal);
    }

    private static void AssertNoWriteOperations(JsonElement root)
    {
        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject())
            {
                var method = op.Name.ToLowerInvariant();
                Assert.True(
                    method is "get" or "parameters",
                    $"OpenAPI must stay read-only; found {op.Name.ToUpperInvariant()} on {path.Name}");
            }
        }
    }

    private static IEnumerable<string> PathKeys(JsonElement root) =>
        root.GetProperty("paths").EnumerateObject().Select(p => p.Name);

    private static async Task<string> WriteEmptyCsvAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mes-ingest-openapi-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n", Encoding.UTF8);
        return path;
    }
}
