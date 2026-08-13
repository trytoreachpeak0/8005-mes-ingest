using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MesIngest.Tests;

public sealed class ReleasePackageValidationTests
{
    private const string CanonicalQueryId = "MES_TASK_UNION";
    private const string CanonicalQuerySha256 = "54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae";
    private const string CanonicalQueryRelativePath = "service/queries/mes-task-union/query.sql";

    [Fact]
    public void Publish_script_emits_one_content_addressed_service_query_without_a_root_mirror()
    {
        var script = File.ReadAllText(Path.Combine(CSharpRoot, "pack", "Publish-MesIngest.ps1"));

        Assert.Contains(CanonicalQueryRelativePath, script, StringComparison.Ordinal);
        Assert.Contains(CanonicalQuerySha256, script, StringComparison.Ordinal);
        Assert.Contains("query.manifest.json", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$queriesDir", script, StringComparison.Ordinal);
        Assert.DoesNotContain("mirror at install root", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Release_package_and_smoke_gate_only_the_production_v2_surface()
    {
        var publish = File.ReadAllText(Path.Combine(CSharpRoot, "pack", "Publish-MesIngest.ps1"));
        var validator = File.ReadAllText(Path.Combine(CSharpRoot, "pack", "Test-ReleasePackage.ps1"));
        var smoke = File.ReadAllText(
            Path.Combine(CSharpRoot, "pack", "validation", "Invoke-ReleaseSmoke.ps1"));

        Assert.DoesNotContain("openapiSrc", publish, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("openapi\\v1.json", validator, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MES_INGEST_RELEASE_SMOKE_SQLSERVER", smoke, StringComparison.Ordinal);
        Assert.Contains("MES_INGEST_RELEASE_SMOKE_EMPTY_DATABASE_CONFIRMED", smoke, StringComparison.Ordinal);
        Assert.Contains("dedicated, disposable, and empty", smoke, StringComparison.Ordinal);
        Assert.Contains("SELECT COUNT_BIG(*) FROM sys.tables", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP DATABASE", smoke, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE TABLE", smoke, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", smoke, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DOTNET_ENVIRONMENT", smoke, StringComparison.Ordinal);
        Assert.Contains("Production", smoke, StringComparison.Ordinal);
        Assert.Contains("MesIngest__NewSqlServerConnectionString", smoke, StringComparison.Ordinal);
        Assert.Contains("MesIngest__SnapshotSource'] = 'Oracle'", smoke, StringComparison.Ordinal);
        Assert.Contains("MesIngest__RunOneShotOnStartup'] = 'false'", smoke, StringComparison.Ordinal);
        Assert.Contains("MesIngest__ContinuousPollEnabled'] = 'false'", smoke, StringComparison.Ordinal);
        Assert.Contains("/api/v2/contract", smoke, StringComparison.Ordinal);
        Assert.Contains(CanonicalQueryRelativePath, smoke, StringComparison.Ordinal);
        Assert.Contains(CanonicalQuerySha256, smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/demands", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("SnapshotCsvPath", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("MesIngest.Watch.exe", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("host.stdout.log", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("host.stderr.log", smoke, StringComparison.Ordinal);
        Assert.Contains("BeginOutputReadLine", smoke, StringComparison.Ordinal);
        Assert.Contains("BeginErrorReadLine", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_read_only_package_is_accepted_and_gets_a_hashed_manifest()
    {
        var root = CreateFixture();
        try
        {
            var result = await RunValidatorAsync(root);

            Assert.Equal(0, result.ExitCode);
            var manifestPath = Path.Combine(root, "RELEASE-MANIFEST.json");
            Assert.True(File.Exists(manifestPath), result.Output);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.Equal("PASSED", manifest.RootElement.GetProperty("validationStatus").GetString());
            Assert.Equal("/api/v2/*", manifest.RootElement.GetProperty("productionSurface").GetString());
            Assert.Equal("/api/v2/contract", manifest.RootElement.GetProperty("contractDiscovery").GetString());
            Assert.Equal("DEFERRED_TO_TICKET_17", manifest.RootElement.GetProperty("openApiStatus").GetString());
            var rebuild = manifest.RootElement.GetProperty("rebuildEvidence");
            Assert.Equal("2026-08-09", rebuild.GetProperty("rebuildDecision").GetString());
            Assert.False(rebuild.GetProperty("oldVisualEvidenceAccepted").GetBoolean());
            Assert.Equal(19, rebuild.GetProperty("tickets").GetProperty("11").GetProperty("xamlScenarioCount").GetInt32());
            Assert.Equal(50, rebuild.GetProperty("tickets").GetProperty("12").GetProperty("completeGateRuns").GetInt32());
            var canonicalQuery = manifest.RootElement.GetProperty("canonicalQuery");
            Assert.Equal(CanonicalQueryId, canonicalQuery.GetProperty("id").GetString());
            Assert.Equal($"{CanonicalQueryId}/sha256:{CanonicalQuerySha256}", canonicalQuery.GetProperty("version").GetString());
            Assert.Equal(CanonicalQueryRelativePath, canonicalQuery.GetProperty("path").GetString());
            Assert.Equal(CanonicalQuerySha256, canonicalQuery.GetProperty("sha256").GetString());
            Assert.Equal(
                new FileInfo(Path.Combine(root, CanonicalQueryRelativePath.Replace('/', Path.DirectorySeparatorChar))).Length,
                canonicalQuery.GetProperty("length").GetInt64());
            Assert.NotEmpty(manifest.RootElement.GetProperty("files").EnumerateArray());
            Assert.All(
                manifest.RootElement.GetProperty("files").EnumerateArray(),
                file => Assert.Matches("^[A-F0-9]{64}$", file.GetProperty("sha256").GetString()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("tampered")]
    public async Task Missing_empty_or_tampered_canonical_query_is_rejected(string mutation)
    {
        var root = CreateFixture();
        try
        {
            var queryPath = Path.Combine(root, CanonicalQueryRelativePath.Replace('/', Path.DirectorySeparatorChar));
            switch (mutation)
            {
                case "missing":
                    File.Delete(queryPath);
                    break;
                case "empty":
                    await File.WriteAllBytesAsync(queryPath, []);
                    break;
                case "tampered":
                    await File.AppendAllTextAsync(queryPath, Environment.NewLine + "-- tampered");
                    break;
            }

            var result = await RunValidatorAsync(root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("canonical", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "RELEASE-MANIFEST.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Second_sql_copy_is_rejected()
    {
        var root = CreateFixture();
        try
        {
            var duplicateDir = Path.Combine(root, "queries", "mes-task-union");
            Directory.CreateDirectory(duplicateDir);
            File.Copy(
                Path.Combine(root, CanonicalQueryRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(duplicateDir, "query.sql"));

            var result = await RunValidatorAsync(root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("exactly one", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Canonical_query_manifest_mismatch_is_rejected()
    {
        var root = CreateFixture();
        try
        {
            var manifestPath = Path.Combine(root, "service", "queries", "mes-task-union", "query.manifest.json");
            var text = await File.ReadAllTextAsync(manifestPath);
            await File.WriteAllTextAsync(manifestPath, text.Replace(CanonicalQuerySha256, new string('0', 64), StringComparison.Ordinal));

            var result = await RunValidatorAsync(root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("manifest", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Package_without_production_v2_sql_placeholder_is_rejected()
    {
        var root = CreateFixture();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "templates", "appsettings.Local.json.example"),
                """{"MesIngest":{"SharedSecret":"","NewSqlServerConnectionString":""}}""");

            var result = await RunValidatorAsync(root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("NewSqlServerConnectionString", result.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root, "RELEASE-MANIFEST.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Package_with_test_fake_or_visual_candidate_is_rejected()
    {
        var root = CreateFixture();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "watch", "WatchWindowFakeHost.dll"), "fake");
            await File.WriteAllTextAsync(Path.Combine(root, "watch", "overview.received.png"), "candidate");

            var result = await RunValidatorAsync(root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("forbidden", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "RELEASE-MANIFEST.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mes-ingest-release-{Guid.NewGuid():N}");
        foreach (var directory in new[]
                 {
                     "service", "watch", "templates", "scripts", "validation",
                 })
        {
            Directory.CreateDirectory(Path.Combine(root, directory));
        }

        File.WriteAllText(Path.Combine(root, "service", "MesIngest.Host.exe"), "host");
        File.WriteAllText(Path.Combine(root, "watch", "MesIngest.Watch.exe"), "watch");
        var canonicalQueryDirectory = Path.Combine(root, "service", "queries", "mes-task-union");
        Directory.CreateDirectory(canonicalQueryDirectory);
        var canonicalQueryPath = Path.Combine(canonicalQueryDirectory, "query.sql");
        File.Copy(RepositoryCanonicalQueryPath, canonicalQueryPath);
        var queryLength = new FileInfo(canonicalQueryPath).Length;
        var queryHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(canonicalQueryPath))).ToLowerInvariant();
        Assert.Equal(CanonicalQuerySha256, queryHash);
        File.WriteAllText(
            Path.Combine(canonicalQueryDirectory, "query.manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    id = CanonicalQueryId,
                    version = $"{CanonicalQueryId}/sha256:{CanonicalQuerySha256}",
                    path = CanonicalQueryRelativePath,
                    length = queryLength,
                    sha256 = CanonicalQuerySha256,
                }));
        File.WriteAllText(Path.Combine(root, "scripts", "install-service.ps1"), "# install");
        File.WriteAllText(Path.Combine(root, "scripts", "uninstall-service.ps1"), "# uninstall");
        File.WriteAllText(Path.Combine(root, "scripts", "Test-ReleasePackage.ps1"), "# validate");
        File.WriteAllText(Path.Combine(root, "validation", "Invoke-FactoryValidation.ps1"), "# factory");
        File.WriteAllText(Path.Combine(root, "validation", "Invoke-ReleaseSmoke.ps1"), "# smoke");
        File.WriteAllText(Path.Combine(root, "validation", "Invoke-WatchAcceptance.ps1"), "# acceptance");
        File.WriteAllText(Path.Combine(root, "INSTALL.md"), "install");
        File.WriteAllText(Path.Combine(root, "UPGRADE.md"), "upgrade");
        File.WriteAllText(Path.Combine(root, "FACTORY-VALIDATION.md"), "validate");
        File.WriteAllText(
            Path.Combine(root, "RELEASE-EVIDENCE.json"),
            """{"schemaVersion":1,"rebuildDecision":"2026-08-09","oldVisualEvidenceAccepted":false,"tickets":{"11":{"generation":"2026-08-09-rebuild","xamlScenarioCount":19},"12":{"generation":"2026-08-09-rebuild","realWindowBaselineCount":5,"completeGateRuns":50},"13":{"generation":"2026-08-09-rebuild"}}}""");
        File.WriteAllText(Path.Combine(root, "VERSION.txt"), "version");
        File.WriteAllText(
            Path.Combine(root, "templates", "appsettings.Local.json.example"),
            """{"MesIngest":{"SharedSecret":"","NewSqlServerConnectionString":"<SQL>"}}""");
        File.WriteAllText(
            Path.Combine(root, "templates", "watch.appsettings.Local.json.example"),
            """{"Watch":{"BaseUrl":"http://127.0.0.1:5088","RenderingMode":"SoftwareOnly","RequestTimeoutSeconds":30,"ConnectionLogRetentionDays":30,"ConnectionLogMaxSizeMb":100,"SharedSecret":""},"_comments":{"SharedSecret":"MesIngestWatch__SharedSecret external-configuration","preferences":"%LocalAppData%"}}""");
        return root;
    }

    private static async Task<ValidationResult> RunValidatorAsync(string packageRoot)
    {
        var script = Path.Combine(CSharpRoot, "pack", "Test-ReleasePackage.ps1");
        var start = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-PackageRoot");
        start.ArgumentList.Add(packageRoot);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ValidationResult(process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }

    private static string CSharpRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "pack", "Publish-MesIngest.ps1")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate mes/ingest/csharp root.");
        }
    }

    private static string RepositoryCanonicalQueryPath => Path.GetFullPath(
        Path.Combine(CSharpRoot, "..", "..", "queries", "mes-task-union", "query.sql"));

    private sealed record ValidationResult(int ExitCode, string Output);
}
