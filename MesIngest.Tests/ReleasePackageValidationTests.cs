using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MesIngest.Tests;

public sealed class ReleasePackageValidationTests
{
    private const string ContractVersion = "2026.08.new-mes-ingest.v2.0";
    private const int ContractSchemaVersion = 17;
    private const string CanonicalOpenApiRelativePath = "openapi/v2.json";
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

        Assert.Contains("openapiSrc", publish, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CanonicalOpenApiRelativePath, publish.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains(CanonicalOpenApiRelativePath, validator.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("openApiStatus = 'FROZEN'", validator, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", validator, StringComparison.Ordinal);
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
        // Ticket 24: recorded rounds drive the production entry so the smoke is
        // repeatable without factory Oracle, and they can never pass as live evidence.
        Assert.Contains("MesIngest__ReplayRoundsFromRecordingPath", smoke, StringComparison.Ordinal);
        Assert.Contains("RELEASE_SMOKE_NOT_FACTORY_EVIDENCE", smoke, StringComparison.Ordinal);
        Assert.Contains("mes-task-union-rounds.json", smoke, StringComparison.Ordinal);
        Assert.Contains("liveOracleAttested = $false", smoke, StringComparison.Ordinal);
        Assert.Contains("oracleConnectionAttempted = $false", smoke, StringComparison.Ordinal);
        // The checks ticket 24 requires of a packaged smoke.
        Assert.Contains("/api/v2/externally-readable-demand-catalog", smoke, StringComparison.Ordinal);
        Assert.Contains("If-None-Match", smoke, StringComparison.Ordinal);
        Assert.Contains("must be 304", smoke, StringComparison.Ordinal);
        Assert.Contains("raw-observations", smoke, StringComparison.Ordinal);
        Assert.Contains("sqlServerRestartPersistence", smoke, StringComparison.Ordinal);
        Assert.Contains("PollTrace high-water did not advance", smoke, StringComparison.Ordinal);
        Assert.Contains("PACKAGED_WATCH_PROCESS_INDEPENDENCE_AND_STARTUP_BUDGET", smoke, StringComparison.Ordinal);
        // A release claims the packaged Watch starts within a bound, so the smoke
        // measures process start to a shown main window instead of assuming it.
        Assert.Contains("PackagedWatchStartupBudgetSeconds", smoke, StringComparison.Ordinal);
        Assert.Contains("startupToMainWindowMs", smoke, StringComparison.Ordinal);
        Assert.Contains("startup budget", smoke, StringComparison.Ordinal);
        Assert.Contains("IncludePackagedWatch", smoke, StringComparison.Ordinal);
        Assert.Contains("UserInteractive", smoke, StringComparison.Ordinal);
        Assert.Contains("remoteBindWithoutSharedSecretExitCode", smoke, StringComparison.Ordinal);
        Assert.Contains("serviceAndWatchIdentical", smoke, StringComparison.Ordinal);
        Assert.Contains("serviceAndWatchIdentical", validator, StringComparison.Ordinal);
        Assert.Contains("/api/v2/contract", smoke, StringComparison.Ordinal);
        Assert.Contains("/openapi/v2.json", smoke, StringComparison.Ordinal);
        Assert.Contains("/openapi/v1.json", smoke, StringComparison.Ordinal);
        Assert.Contains("/api/demands", smoke, StringComparison.Ordinal);
        Assert.Contains("/api/alerts", smoke, StringComparison.Ordinal);
        Assert.Contains("/api/poll-health", smoke, StringComparison.Ordinal);
        Assert.Contains("/api/demand-changes", smoke, StringComparison.Ordinal);
        Assert.Contains("EXACT_VERSION_SCHEMA_AND_CAPABILITIES", smoke, StringComparison.Ordinal);
        Assert.Contains("$expectedCapabilityVersion = '1.0'", smoke, StringComparison.Ordinal);
        Assert.Contains("$expectedCapabilityOperations", smoke, StringComparison.Ordinal);
        Assert.Contains(
            "[string]$actualCapability[0].version -cne $expectedCapabilityVersion",
            smoke,
            StringComparison.Ordinal);
        Assert.Contains(CanonicalQueryRelativePath, smoke, StringComparison.Ordinal);
        Assert.Contains(CanonicalQuerySha256, smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("SnapshotCsvPath", smoke, StringComparison.Ordinal);
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

            Assert.True(result.ExitCode == 0, result.Output);
            var manifestPath = Path.Combine(root, "RELEASE-MANIFEST.json");
            Assert.True(File.Exists(manifestPath), result.Output);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.Equal(3, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("PASSED", manifest.RootElement.GetProperty("validationStatus").GetString());
            Assert.Equal("/api/v2/*", manifest.RootElement.GetProperty("productionSurface").GetString());
            Assert.Equal("/api/v2/contract", manifest.RootElement.GetProperty("contractDiscovery").GetString());
            Assert.Equal("FROZEN", manifest.RootElement.GetProperty("openApiStatus").GetString());
            var openApi = manifest.RootElement.GetProperty("openApi");
            Assert.Equal(CanonicalOpenApiRelativePath, openApi.GetProperty("path").GetString());
            Assert.Equal(ContractVersion, openApi.GetProperty("contractVersion").GetString());
            Assert.Equal(ContractSchemaVersion, openApi.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                ComputeSha256(Path.Combine(root, CanonicalOpenApiRelativePath.Replace('/', Path.DirectorySeparatorChar))),
                openApi.GetProperty("sha256").GetString());
            var rebuild = manifest.RootElement.GetProperty("rebuildEvidence");
            Assert.Equal("2026-08-09", rebuild.GetProperty("rebuildDecision").GetString());
            Assert.False(rebuild.GetProperty("oldVisualEvidenceAccepted").GetBoolean());
            Assert.Equal(19, rebuild.GetProperty("tickets").GetProperty("11").GetProperty("xamlScenarioCount").GetInt32());
            Assert.Equal(50, rebuild.GetProperty("tickets").GetProperty("12").GetProperty("completeGateRuns").GetInt32());
            var sharedContract = manifest.RootElement.GetProperty("sharedContract");
            Assert.Equal("MesIngest.Core.dll", sharedContract.GetProperty("assembly").GetString());
            Assert.True(sharedContract.GetProperty("serviceAndWatchIdentical").GetBoolean());
            Assert.Equal(
                ComputeSha256(Path.Combine(root, "service", "MesIngest.Core.dll")),
                sharedContract.GetProperty("sha256").GetString());
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

    [Fact]
    public async Task Release_smoke_rejects_semantically_equal_openapi_byte_drift_before_database_access()
    {
        var root = CreateFixture();
        try
        {
            File.Copy(
                Path.Combine(CSharpRoot, "pack", "validation", "Invoke-ReleaseSmoke.ps1"),
                Path.Combine(root, "validation", "Invoke-ReleaseSmoke.ps1"),
                overwrite: true);
            var validation = await RunValidatorAsync(root);
            Assert.Equal(0, validation.ExitCode);

            var openApiPath = Path.Combine(
                root,
                CanonicalOpenApiRelativePath.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(openApiPath, Environment.NewLine);

            var smoke = await RunReleaseSmokeAsync(root);

            Assert.NotEqual(0, smoke.ExitCode);
            Assert.Contains("SHA-256", smoke.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "MES_INGEST_RELEASE_SMOKE_SQLSERVER",
                smoke.Output,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-version")]
    [InlineData("legacy-path")]
    [InlineData("write-operation")]
    [InlineData("legacy-term")]
    public async Task Missing_or_noncanonical_v2_openapi_is_rejected(string mutation)
    {
        var root = CreateFixture();
        try
        {
            var openApiPath = Path.Combine(
                root,
                CanonicalOpenApiRelativePath.Replace('/', Path.DirectorySeparatorChar));
            switch (mutation)
            {
                case "missing":
                    File.Delete(openApiPath);
                    break;
                case "wrong-version":
                    await File.WriteAllTextAsync(
                        openApiPath,
                        (await File.ReadAllTextAsync(openApiPath)).Replace(
                            ContractVersion,
                            "2026.08.new-mes-ingest.v2.drift",
                            StringComparison.Ordinal));
                    break;
                case "legacy-path":
                    await MutateOpenApiAsync(openApiPath, rootElement =>
                    {
                        rootElement["paths"]!["/api/contract"] = new JsonObject
                        {
                            ["get"] = new JsonObject(),
                        };
                    });
                    break;
                case "write-operation":
                    await MutateOpenApiAsync(openApiPath, rootElement =>
                    {
                        rootElement["paths"]!["/api/v2/contract"]!["post"] = new JsonObject();
                    });
                    break;
                case "legacy-term":
                    await MutateOpenApiAsync(openApiPath, rootElement =>
                    {
                        rootElement["info"]!["description"] = "DemandChangeFeed";
                    });
                    break;
            }

            var result = await RunValidatorAsync(root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("OpenAPI", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "RELEASE-MANIFEST.json")));
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

    [Theory]
    [InlineData("INSTALL.md", "先调用 /api/demands 列出需求，再打开 Watch。")]
    [InlineData("INSTALL.md", "Bootstrap from the DemandChangeFeed highWatermark, then catch up.")]
    [InlineData("validation/RETURN-CHECKLIST.md", "- [ ] 附上 IngestAlert incident 导出")]
    public async Task Package_documentation_that_still_teaches_the_retired_contract_is_rejected(
        string relativePath,
        string line)
    {
        var root = CreateFixture();
        try
        {
            var target = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(target, Environment.NewLine + line + Environment.NewLine);

            var result = await RunValidatorAsync(root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("retired contract", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "RELEASE-MANIFEST.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Watch_built_against_a_different_contract_assembly_is_rejected()
    {
        var root = CreateFixture();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "watch", "MesIngest.Core.dll"),
                "a-different-contract");

            var result = await RunValidatorAsync(root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("same versioned contract", result.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root, "RELEASE-MANIFEST.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Packaged_configuration_with_a_legacy_only_key_is_rejected()
    {
        var root = CreateFixture();
        try
        {
            var settings = Path.Combine(root, "service", "appsettings.json");
            await File.WriteAllTextAsync(
                settings,
                (await File.ReadAllTextAsync(settings)).Replace(
                    "\"OracleMode\": \"Thin\",",
                    "\"ChangeFeedRetentionHours\": 48,\r\n    \"OracleMode\": \"Thin\",",
                    StringComparison.Ordinal));

            var result = await RunValidatorAsync(root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("ChangeFeedRetentionHours", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Retiring_a_legacy_endpoint_on_the_same_line_stays_publishable()
    {
        var root = CreateFixture();
        try
        {
            await File.AppendAllTextAsync(
                Path.Combine(root, "INSTALL.md"),
                Environment.NewLine
                + "- 旧 `/api/demands` 与 `/api/alerts` 在 Production 一律返回 404。"
                + Environment.NewLine);

            var result = await RunValidatorAsync(root);

            Assert.Equal(0, result.ExitCode);
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
                     "service", "watch", "templates", "scripts", "validation", "openapi",
                 })
        {
            Directory.CreateDirectory(Path.Combine(root, directory));
        }

        File.WriteAllText(Path.Combine(root, "service", "MesIngest.Host.exe"), "host");
        File.WriteAllText(Path.Combine(root, "watch", "MesIngest.Watch.exe"), "watch");
        // Service and Watch publish the same MesIngest.Core, which carries the frozen
        // contract identity. Identical bytes are what the validator checks.
        File.WriteAllText(Path.Combine(root, "service", "MesIngest.Core.dll"), "shared-contract");
        File.WriteAllText(Path.Combine(root, "watch", "MesIngest.Core.dll"), "shared-contract");
        File.Copy(
            Path.Combine(
                CSharpRoot,
                "pack",
                CanonicalOpenApiRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(root, CanonicalOpenApiRelativePath.Replace('/', Path.DirectorySeparatorChar)));
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
        // The cutover drill is copied, not stubbed: the validator asserts that the only
        // database-deleting path in the package is this attended one.
        var packagedCutover = Path.Combine(root, "scripts", "cutover");
        Directory.CreateDirectory(packagedCutover);
        foreach (var cutoverScript in new[]
                 {
                     "CutoverSqlTools.ps1",
                     "Invoke-EmptyDatabaseCutover.ps1",
                     "Invoke-CutoverRollback.ps1",
                 })
        {
            File.Copy(
                Path.Combine(CSharpRoot, "pack", "cutover", cutoverScript),
                Path.Combine(packagedCutover, cutoverScript));
        }

        File.WriteAllText(Path.Combine(root, "validation", "Invoke-FactoryValidation.ps1"), "# factory");
        File.WriteAllText(Path.Combine(root, "validation", "Invoke-ReleaseSmoke.ps1"), "# smoke");
        File.Copy(
            Path.Combine(CSharpRoot, "pack", "validation", "release-smoke-rounds.json"),
            Path.Combine(root, "validation", "release-smoke-rounds.json"));
        File.WriteAllText(Path.Combine(root, "validation", "Invoke-WatchAcceptance.ps1"), "# acceptance");
        // The shipped documentation and configuration are copied, not stubbed, so the
        // package-wide retired-contract scan runs against what actually ships.
        foreach (var document in new[] { "INSTALL.md", "UPGRADE.md", "FACTORY-VALIDATION.md" })
        {
            File.Copy(Path.Combine(CSharpRoot, "pack", document), Path.Combine(root, document));
        }

        File.Copy(
            Path.Combine(CSharpRoot, "MesIngest.Host", "appsettings.json"),
            Path.Combine(root, "service", "appsettings.json"));
        File.Copy(
            Path.Combine(CSharpRoot, "MesIngest.Watch", "appsettings.json"),
            Path.Combine(root, "watch", "appsettings.json"));
        foreach (var checklist in Directory.GetFiles(
                     Path.Combine(CSharpRoot, "pack", "validation"),
                     "*.md"))
        {
            File.Copy(checklist, Path.Combine(root, "validation", Path.GetFileName(checklist)));
        }
        File.WriteAllText(
            Path.Combine(root, "RELEASE-EVIDENCE.json"),
            """{"schemaVersion":1,"rebuildDecision":"2026-08-09","oldVisualEvidenceAccepted":false,"tickets":{"11":{"generation":"2026-08-09-rebuild","xamlScenarioCount":19},"12":{"generation":"2026-08-09-rebuild","realWindowBaselineCount":5,"completeGateRuns":50},"13":{"generation":"2026-08-09-rebuild"}}}""");
        File.WriteAllText(Path.Combine(root, "VERSION.txt"), "version");
        File.Copy(
            Path.Combine(CSharpRoot, "MesIngest.Host", "appsettings.Local.json.example"),
            Path.Combine(root, "templates", "appsettings.Local.json.example"));
        File.Copy(
            Path.Combine(CSharpRoot, "MesIngest.Watch", "appsettings.Local.json.example"),
            Path.Combine(root, "templates", "watch.appsettings.Local.json.example"));
        return root;
    }

    private static async Task MutateOpenApiAsync(
        string path,
        Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject()
                   ?? throw new InvalidOperationException("Canonical V2 OpenAPI must be a JSON object.");
        mutate(root);
        await File.WriteAllTextAsync(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ComputeSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

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

    private static async Task<ValidationResult> RunReleaseSmokeAsync(string packageRoot)
    {
        var script = Path.Combine(packageRoot, "validation", "Invoke-ReleaseSmoke.ps1");
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
