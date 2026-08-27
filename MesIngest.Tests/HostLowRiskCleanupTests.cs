using System.Text.Json;

namespace MesIngest.Tests;

public sealed class HostLowRiskCleanupTests
{
    [Fact]
    public void Development_launch_profile_uses_MesIngest_urls_as_the_only_authority_and_opens_swagger()
    {
        var path = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "MesIngest.Host",
            "Properties",
            "launchSettings.json");
        var text = File.ReadAllText(path);
        using var document = JsonDocument.Parse(text);

        var root = document.RootElement;
        Assert.False(root.TryGetProperty("iisSettings", out _));
        Assert.DoesNotContain("\"applicationUrl\"", text, StringComparison.Ordinal);

        var profiles = root.GetProperty("profiles");
        Assert.Single(profiles.EnumerateObject());
        var profile = profiles.GetProperty("http");
        Assert.Equal("Project", profile.GetProperty("commandName").GetString());
        Assert.True(profile.GetProperty("launchBrowser").GetBoolean());
        Assert.Equal("swagger", profile.GetProperty("launchUrl").GetString());

        var environment = profile.GetProperty("environmentVariables");
        Assert.Equal("Development", environment.GetProperty("ASPNETCORE_ENVIRONMENT").GetString());
        Assert.Equal(
            "http://127.0.0.1:5088",
            environment.GetProperty("MesIngest__Urls").GetString());
        Assert.False(environment.TryGetProperty("ASPNETCORE_URLS", out _));
        Assert.False(environment.TryGetProperty("urls", out _));
    }

    [Fact]
    public void Local_config_template_names_current_schema_29()
    {
        var path = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "MesIngest.Host",
            "appsettings.Local.json.example");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var comment = document.RootElement
            .GetProperty("_comments")
            .GetProperty("NewSqlServerConnectionString")
            .GetString();

        Assert.Contains("schema v29 exactly", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("schema v17", comment, StringComparison.Ordinal);
    }

    [Fact]
    public void Dead_DemandSeriesDto_is_absent_from_host_source_and_canonical_contract()
    {
        var hostSource = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "MesIngest.Host",
            "NewMesIngestEndpoints.cs"));

        Assert.DoesNotContain("internal sealed record DemandSeriesDto(", hostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("public static DemandSeriesDto From(", hostSource, StringComparison.Ordinal);

        var contractPath = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "openapi",
            "v2.json");
        using var contract = JsonDocument.Parse(File.ReadAllText(contractPath));
        var schemas = contract.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.False(schemas.TryGetProperty("DemandSeriesDto", out _));
        Assert.True(schemas.TryGetProperty("FrozenDemandSeriesDto", out _));
    }
}
