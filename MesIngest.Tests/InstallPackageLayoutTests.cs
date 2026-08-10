using System.Text.Json;
using System.Text.RegularExpressions;

namespace MesIngest.Tests;

/// <summary>
/// Supporting smoke for ticket 10 install package layout (not a formal product seam).
/// </summary>
public class InstallPackageLayoutTests
{
    private static string CSharpRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var hostProj = Path.Combine(dir.FullName, "MesIngest.Host", "MesIngest.Host.csproj");
                if (File.Exists(hostProj))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("Could not locate mes/ingest/csharp root from test base directory.");
        }
    }

    [Fact]
    public void Pack_sources_include_publish_scripts_install_doc_and_service_helpers()
    {
        var pack = Path.Combine(CSharpRoot, "pack");
        Assert.True(File.Exists(Path.Combine(pack, "Publish-MesIngest.ps1")));
        Assert.True(File.Exists(Path.Combine(pack, "INSTALL.md")));
        Assert.True(File.Exists(Path.Combine(pack, "UPGRADE.md")));
        Assert.True(File.Exists(Path.Combine(pack, "install-service.ps1")));
        Assert.True(File.Exists(Path.Combine(pack, "uninstall-service.ps1")));

        var install = File.ReadAllText(Path.Combine(pack, "INSTALL.md"));
        Assert.Contains("Start-Service", install, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stop-Service", install, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SharedSecret", install, StringComparison.Ordinal);
        Assert.Contains("事件", install, StringComparison.Ordinal);
        Assert.Contains("VERSION.txt", install, StringComparison.Ordinal);

        var upgrade = File.ReadAllText(Path.Combine(pack, "UPGRADE.md"));
        Assert.Contains("BACKUP DATABASE", upgrade, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RESTORE DATABASE", upgrade, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CONTRACT_VERSION_MISMATCH", upgrade, StringComparison.Ordinal);
        Assert.Contains("IngestAlerts_LegacyArchive", upgrade, StringComparison.Ordinal);
        Assert.Contains("openapi/v1.json", upgrade, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Committed_host_appsettings_defaults_to_localhost_with_empty_shared_secret()
    {
        var path = Path.Combine(CSharpRoot, "MesIngest.Host", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var mes = doc.RootElement.GetProperty("MesIngest");
        Assert.Equal("http://127.0.0.1:5088", mes.GetProperty("Urls").GetString());
        Assert.Equal("", mes.GetProperty("SharedSecret").GetString());
        Assert.Equal("", mes.GetProperty("SqlServerConnectionString").GetString());
        Assert.Equal("", mes.GetProperty("OracleUser").GetString());
        Assert.Equal("", mes.GetProperty("OraclePassword").GetString());
        Assert.Equal(48, mes.GetProperty("ChangeFeedRetentionHours").GetInt32());
        Assert.Equal(365, mes.GetProperty("AlertRetentionDays").GetInt32());
    }

    [Fact]
    public void Local_config_template_is_blank_placeholders_only()
    {
        var path = Path.Combine(CSharpRoot, "MesIngest.Host", "appsettings.Local.json.example");
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var mes = doc.RootElement.GetProperty("MesIngest");

        Assert.Equal("http://127.0.0.1:5088", mes.GetProperty("Urls").GetString());
        Assert.Equal("", mes.GetProperty("SharedSecret").GetString());
        Assert.Equal(48, mes.GetProperty("ChangeFeedRetentionHours").GetInt32());
        Assert.Equal(365, mes.GetProperty("AlertRetentionDays").GetInt32());
        Assert.Contains("pageLimits", text, StringComparison.Ordinal);
        Assert.Contains("1..200", text, StringComparison.Ordinal);

        var oracleUser = mes.GetProperty("OracleUser").GetString()!;
        var oraclePassword = mes.GetProperty("OraclePassword").GetString()!;
        var sql = mes.GetProperty("SqlServerConnectionString").GetString()!;

        Assert.StartsWith("<", oracleUser);
        Assert.Contains("PASSWORD", oraclePassword, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<SQL_", sql, StringComparison.Ordinal);

        Assert.DoesNotMatch(new Regex(@"Password\s*=\s*[^;<\s][^;]*", RegexOptions.IgnoreCase), sql);
        Assert.DoesNotContain("meslab", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Watch_local_config_template_covers_timeout_and_log_retention()
    {
        var path = Path.Combine(CSharpRoot, "MesIngest.Watch", "appsettings.Local.json.example");
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var watch = doc.RootElement.GetProperty("Watch");

        Assert.Equal("http://127.0.0.1:5088", watch.GetProperty("BaseUrl").GetString());
        Assert.Equal("SoftwareOnly", watch.GetProperty("RenderingMode").GetString());
        Assert.Equal(30, watch.GetProperty("RequestTimeoutSeconds").GetInt32());
        Assert.Equal(30, watch.GetProperty("ConnectionLogRetentionDays").GetInt32());
        Assert.Equal(100, watch.GetProperty("ConnectionLogMaxSizeMb").GetInt32());
        Assert.Equal("", watch.GetProperty("SharedSecret").GetString());
        Assert.Contains("MesIngestWatch__RequestTimeoutSeconds", text, StringComparison.Ordinal);
        Assert.Contains("MesIngestWatch__SharedSecret", text, StringComparison.Ordinal);
        Assert.Contains("external-configuration", text, StringComparison.Ordinal);
        Assert.Contains("%LocalAppData%", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gitignore_excludes_filled_local_appsettings()
    {
        var gitignore = File.ReadAllText(Path.Combine(CSharpRoot, ".gitignore"));
        Assert.Contains("appsettings.Local.json", gitignore, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_script_stages_expected_install_tree_names()
    {
        var script = File.ReadAllText(Path.Combine(CSharpRoot, "pack", "Publish-MesIngest.ps1"));
        foreach (var name in new[]
                 {
                     "service",
                     "watch",
                     "queries",
                     "templates",
                     "scripts",
                     "INSTALL.md",
                     "UPGRADE.md",
                     "VERSION.txt",
                     "RELEASE-MANIFEST.json",
                     "RELEASE-EVIDENCE.json",
                     "appsettings.Local.json.example",
                     "watch.appsettings.Local.json.example",
                     "Test-ReleasePackage.ps1",
                     "Invoke-ReleaseSmoke.ps1",
                 })
        {
            Assert.Contains(name, script, StringComparison.Ordinal);
        }

        Assert.Contains("appsettings.Local.json", script, StringComparison.Ordinal);
        Assert.Contains("credentials must not ship", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Clearing package output", script, StringComparison.Ordinal);
        Assert.Contains("--ignore-failed-sources", script, StringComparison.Ordinal);
        Assert.Contains("NuGetAudit=false", script, StringComparison.Ordinal);
        Assert.Contains(":(exclude).artifacts/**", script, StringComparison.Ordinal);
        Assert.Contains(":(exclude)MesIngest.Tests/TestResults/**", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Golden_renderer_exposes_packaged_release_gate()
    {
        var wrapper = File.ReadAllText(Path.Combine(CSharpRoot, "Invoke-GoldenRendererValidation.ps1"));

        Assert.Contains("watch-package-release", wrapper, StringComparison.Ordinal);
        Assert.Contains("Publish-MesIngest.ps1", wrapper, StringComparison.Ordinal);
        Assert.Contains("Invoke-ReleaseSmoke.ps1", wrapper, StringComparison.Ordinal);
        Assert.Contains("Invoke-WatchAcceptance.ps1", wrapper, StringComparison.Ordinal);
        Assert.Contains("MesIngest-win-x64.zip", wrapper, StringComparison.Ordinal);
        Assert.Contains("SqlSkipApprovalPath", wrapper, StringComparison.Ordinal);
        Assert.Contains("SQL_SERVER_SKIPS_REQUIRE_EXACT_USER_APPROVAL", wrapper, StringComparison.Ordinal);
        Assert.Contains("PACKAGED_WATCH_UI_SKIPS_NOT_ALLOWED", wrapper, StringComparison.Ordinal);
        Assert.Contains("ManualAcceptancePath", wrapper, StringComparison.Ordinal);
        Assert.Contains("RELEASE-SIGNOFF.json", wrapper, StringComparison.Ordinal);
        Assert.Contains("core-host-http-sql.trx", wrapper, StringComparison.Ordinal);
        Assert.Contains("environment-after-host-cleanup.json", wrapper, StringComparison.Ordinal);
        Assert.Contains(":(exclude).artifacts/**", wrapper, StringComparison.Ordinal);
        Assert.Contains(":(exclude)MesIngest.Tests/TestResults/**", wrapper, StringComparison.Ordinal);
        Assert.Contains("queries\\mes-task-union", wrapper, StringComparison.Ordinal);
        Assert.Contains("experiments\\definitions\\mes-ingest-factory-validation\\plan.md", wrapper, StringComparison.Ordinal);
        Assert.Contains("evidence\\README.md", wrapper, StringComparison.Ordinal);
        Assert.Contains("$_ -notin $approvedTests", wrapper, StringComparison.Ordinal);
    }
}
