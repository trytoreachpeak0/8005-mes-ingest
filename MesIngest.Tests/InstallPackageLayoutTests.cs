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
        Assert.Contains("NewSqlServerConnectionString", upgrade, StringComparison.Ordinal);
        Assert.Contains("service/queries/mes-task-union/query.sql", upgrade, StringComparison.Ordinal);
        Assert.Contains("/api/v2/contract", upgrade, StringComparison.Ordinal);
        Assert.Contains("openapi/v1.json", upgrade, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Committed_host_appsettings_defaults_to_localhost_without_legacy_only_keys()
    {
        var path = Path.Combine(CSharpRoot, "MesIngest.Host", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var mes = doc.RootElement.GetProperty("MesIngest");
        Assert.Equal("http://127.0.0.1:5088", mes.GetProperty("Urls").GetString());
        Assert.Equal("", mes.GetProperty("SharedSecret").GetString());
        Assert.Equal("", mes.GetProperty("NewSqlServerConnectionString").GetString());
        Assert.Equal("", mes.GetProperty("OracleUser").GetString());
        Assert.Equal("", mes.GetProperty("OraclePassword").GetString());
        // Ticket 24: the file ships inside the release package, so it may not carry
        // configuration that only the retired V1 surface reads.
        foreach (var legacyOnlyKey in new[]
                 {
                     "SnapshotCsvPath",
                     "SqlServerConnectionString",
                     "ChangeFeedRetentionHours",
                     "AlertRetentionDays",
                 })
        {
            Assert.False(
                mes.TryGetProperty(legacyOnlyKey, out _),
                $"Packaged host appsettings still carries the legacy-only key {legacyOnlyKey}.");
        }
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
        Assert.False(mes.TryGetProperty("ChangeFeedRetentionHours", out _));
        Assert.False(mes.TryGetProperty("AlertRetentionDays", out _));
        Assert.DoesNotContain("/api/demands", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DemandChangeFeed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IngestAlert", text, StringComparison.Ordinal);
        Assert.Contains("/api/v2", text, StringComparison.Ordinal);

        var oracleUser = mes.GetProperty("OracleUser").GetString()!;
        var oraclePassword = mes.GetProperty("OraclePassword").GetString()!;
        var sql = mes.GetProperty("NewSqlServerConnectionString").GetString()!;

        Assert.StartsWith("<", oracleUser);
        Assert.Contains("PASSWORD", oraclePassword, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<SQL_", sql, StringComparison.Ordinal);

        Assert.DoesNotMatch(new Regex(@"Password\s*=\s*[^;<\s][^;]*", RegexOptions.IgnoreCase), sql);
        Assert.DoesNotContain("meslab", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_doc_describes_production_v2_release_smoke_without_claiming_v1_openapi_or_watch()
    {
        var installPath = Path.Combine(CSharpRoot, "pack", "INSTALL.md");
        var install = File.ReadAllText(installPath);

        Assert.Contains("MES_INGEST_RELEASE_SMOKE_SQLSERVER", install, StringComparison.Ordinal);
        Assert.Contains("MES_INGEST_RELEASE_SMOKE_EMPTY_DATABASE_CONFIRMED", install, StringComparison.Ordinal);
        Assert.Contains("专用、可丢弃", install, StringComparison.Ordinal);
        Assert.Contains("不会为你删库或清表", install, StringComparison.Ordinal);
        Assert.Contains("NewSqlServerConnectionString", install, StringComparison.Ordinal);
        Assert.Contains("/api/v2/contract", install, StringComparison.Ordinal);
        Assert.Contains("canonical", install, StringComparison.OrdinalIgnoreCase);
        // Ticket 24: the packaged smoke covers the whole frozen surface it claims.
        Assert.Contains("/api/v2/externally-readable-demand-catalog", install, StringComparison.Ordinal);
        Assert.Contains("If-None-Match", install, StringComparison.Ordinal);
        Assert.Contains("304", install, StringComparison.Ordinal);
        Assert.Contains("raw-observations", install, StringComparison.Ordinal);
        Assert.Contains("pollTraceHighWater", install, StringComparison.Ordinal);
        Assert.Contains("FILE_REPLAY", install, StringComparison.Ordinal);
        Assert.Contains("PACKAGED_WATCH_PROCESS_INDEPENDENCE_AND_STARTUP_BUDGET", install, StringComparison.Ordinal);
        Assert.Contains("watch-production-preview", install, StringComparison.Ordinal);
        Assert.Contains("关闭 WPF 不会停止 Service", install, StringComparison.Ordinal);
        // ADR-mes-0017 retires the v1 surface, and INSTALL.md has to name
        // `/openapi/v1.json` in order to say it is gone. Forbidding the string
        // outright would forbid the sentence the contract needs, so what is
        // checked is that no mention presents v1 as a surface still served.
        var v1Mentions = File.ReadAllLines(installPath)
            .Where(line => line.Contains("openapi/v1.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(v1Mentions);
        Assert.All(v1Mentions, line => Assert.True(
            line.Contains("404", StringComparison.Ordinal) ||
            line.Contains("Development", StringComparison.OrdinalIgnoreCase),
            $"INSTALL.md mentions /openapi/v1.json without retiring it on the same line: {line.Trim()}"));
        Assert.DoesNotContain("临时 CSV", install, StringComparison.Ordinal);
        Assert.DoesNotContain("内存投影", install, StringComparison.Ordinal);
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
        Assert.Contains("SqlServerCredentialPath", wrapper, StringComparison.Ordinal);
        Assert.Contains("SqlServerDataSource", wrapper, StringComparison.Ordinal);
        Assert.Contains("SqlServerDatabase", wrapper, StringComparison.Ordinal);
        Assert.Contains("SqlServerDatabaseIsDedicatedEmpty", wrapper, StringComparison.Ordinal);
        Assert.Contains("$sqlInputCount = @($sqlInputs | Where-Object", wrapper, StringComparison.Ordinal);
        Assert.Contains("$sqlInputCount -ne 3", wrapper, StringComparison.Ordinal);
        Assert.Contains("SqlPasswordFromDpapiCredential", wrapper, StringComparison.Ordinal);
        Assert.Contains("$builder['Data Source']", wrapper, StringComparison.Ordinal);
        Assert.Contains("$builder['Initial Catalog']", wrapper, StringComparison.Ordinal);
        Assert.Contains("$env:MES_INGEST_RELEASE_SMOKE_SQLSERVER", wrapper, StringComparison.Ordinal);
        Assert.Contains("$env:MES_INGEST_RELEASE_SMOKE_EMPTY_DATABASE_CONFIRMED", wrapper, StringComparison.Ordinal);
        Assert.Contains("$env:MES_INGEST_SQLSERVER", wrapper, StringComparison.Ordinal);
        // Ticket 24: without its own variable and the expected engine identity, the
        // whole V2 projection suite reports NotExecuted and the packaged release
        // would claim a real SQL Server gate it never ran.
        Assert.Contains("$env:MES_INGEST_TICKET01_SQLSERVER", wrapper, StringComparison.Ordinal);
        Assert.Contains(
            "$env:MES_INGEST_TICKET01_EXPECTED_PRODUCT_MAJOR",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:MES_INGEST_TICKET01_EXPECTED_COMPATIBILITY_LEVEL",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains("SqlServerExpectedProductMajor", wrapper, StringComparison.Ordinal);
        Assert.Contains("SqlServerExpectedCompatibilityLevel", wrapper, StringComparison.Ordinal);
        Assert.Contains(
            "run instead of skipping",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $sqlConnectionStringPath", wrapper, StringComparison.Ordinal);
        Assert.Contains("SQL_SERVER_SKIPS_REQUIRE_EXACT_USER_APPROVAL", wrapper, StringComparison.Ordinal);
        Assert.Contains("PACKAGED_WATCH_UI_SKIPS_NOT_ALLOWED", wrapper, StringComparison.Ordinal);
        // Ticket 24: the packaged gate runs the published binaries through the
        // non-pixel suites and reuses the ticket 23 visual acceptance.
        Assert.Contains("-IncludePackagedWatch", wrapper, StringComparison.Ordinal);
        Assert.Contains("-Suite watch-production-preview", wrapper, StringComparison.Ordinal);
        Assert.Contains(
            "@('watch-ui-journeys', 'watch-vm-tests')",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains("ticket23VisualBaselinesReused", wrapper, StringComparison.Ordinal);
        // The two UI entry points that cannot run inside a release payload are named,
        // not tolerated as a count, so any other skip still fails the gate.
        Assert.Contains(
            "WatchWindowCandidateEquivalenceTests.Candidate_directories_are_visually_equivalent",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains(
            "WatchWindowVisualEquivalenceGoldenFixtureTests.The_recorded_antialiasing_flip_is_accepted",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains("skippedMatchesExpectedNamedSet", wrapper, StringComparison.Ordinal);
        Assert.Contains("$uiSkipDifference", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("-Suite all", wrapper, StringComparison.Ordinal);
        Assert.Contains("ManualAcceptancePath", wrapper, StringComparison.Ordinal);
        Assert.Contains("RELEASE-SIGNOFF.json", wrapper, StringComparison.Ordinal);
        Assert.Contains("READY_FOR_HOST_CLEANUP_AND_FINALIZATION", wrapper, StringComparison.Ordinal);
        Assert.Contains("finalPostCleanupEnvironment", wrapper, StringComparison.Ordinal);
        Assert.Contains("core-host-http-sql.trx", wrapper, StringComparison.Ordinal);
        Assert.Contains("environment-after-host-cleanup.json", wrapper, StringComparison.Ordinal);
        Assert.Contains(":(exclude).artifacts/**", wrapper, StringComparison.Ordinal);
        Assert.Contains(":(exclude)MesIngest.Tests/TestResults/**", wrapper, StringComparison.Ordinal);
        Assert.Contains("$gitStatus = @(", wrapper, StringComparison.Ordinal);
        Assert.Contains("GitDirty = $gitStatus.Count -gt 0", wrapper, StringComparison.Ordinal);
        Assert.Contains("queries\\mes-task-union", wrapper, StringComparison.Ordinal);
        Assert.Contains("experiments\\definitions\\mes-ingest-factory-validation\\plan.md", wrapper, StringComparison.Ordinal);
        Assert.Contains("evidence\\README.md", wrapper, StringComparison.Ordinal);
        Assert.Contains("$_ -notin $approvedTests", wrapper, StringComparison.Ordinal);

        var cleanupGate = wrapper.IndexOf(
            "Golden renderer cleanup or post-cleanup environment recheck failed",
            StringComparison.Ordinal);
        var signoffPromotion = wrapper.IndexOf("RELEASE-SIGNOFF.json", StringComparison.Ordinal);
        var zipPromotion = wrapper.IndexOf("MesIngest-win-x64.zip", StringComparison.Ordinal);
        Assert.True(cleanupGate >= 0 && cleanupGate < signoffPromotion);
        Assert.True(cleanupGate < zipPromotion);
    }
}
