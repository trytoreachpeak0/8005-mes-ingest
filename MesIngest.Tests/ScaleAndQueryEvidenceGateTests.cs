using System.Diagnostics;
using System.Text.Json;

namespace MesIngest.Tests;

public sealed class ScaleAndQueryEvidenceGateTests
{
    [Fact]
    public void Packaged_scale_gate_declares_every_ticket_02_evidence_surface()
    {
        var csharpRoot = RepositoryPaths.CSharpRoot;
        var gatePath = Path.Combine(
            csharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");

        Assert.True(File.Exists(gatePath), $"Missing scale evidence gate: {gatePath}");

        var gate = File.ReadAllText(gatePath);
        var tier1Runner = File.ReadAllText(Path.Combine(csharpRoot, "Invoke-RuntimeFeedbackTier1.ps1"));
        var install = File.ReadAllText(Path.Combine(csharpRoot, "pack", "INSTALL.md"));
        foreach (var profile in new[] { "0", "7", "30" })
        {
            Assert.Contains($"historyDays = {profile}", gate, StringComparison.Ordinal);
        }

        foreach (var path in new[]
                 {
                     "/api/v2/demand-series",
                     "/api/v2/externally-readable-demand-catalog",
                     "/api/v2/current-ingest-attention",
                     "/api/v2/watch-overview",
                     "/api/v2/readability-audit",
                     "/api/v2/error-search",
                     "/raw-observations",
                 })
        {
            Assert.Contains(path, gate, StringComparison.Ordinal);
        }

        foreach (var evidence in new[]
                 {
                     "query_post_execution_showplan",
                     "sql_statement_completed",
                     "logical_reads",
                     "duration",
                     "granted_memory_kb",
                     "SpillToTempDb",
                     "sys.dm_db_partition_stats",
                     "sys.database_files",
                     "data_compression_desc",
                     "schemaVersion",
                     "contractVersion",
                     "sourceCommit",
                     "sqlSkippedTests",
                     "MISSING_ACTUAL_PLAN",
                     "EMPTY_SCALE_DATABASE",
                     "actualPlanCount",
                     "statementCount",
                     "logicalReads",
                     "maxGrantedMemoryKb",
                     "spillCount",
                     "physicalFiles",
                     "allocations",
                     "CLUSTERED",
                     "NONCLUSTERED",
                     "NON_CANONICAL_SCALE_PROFILE",
                     "hostSha256",
                     "sourceDirty",
                     "maxServerMemoryMb",
                     "compatibilityLevel",
                     "recoveryModel",
                     "xp_delete_files",
                 })
        {
            Assert.Contains(evidence, gate, StringComparison.Ordinal);
        }

        Assert.Contains("Invoke-ScaleAndQueryEvidence.ps1", install, StringComparison.Ordinal);
        Assert.Contains("MESINGEST_SCALE_EVIDENCE_ONLY", install, StringComparison.Ordinal);
        Assert.Contains("hostAssemblySha256", tier1Runner, StringComparison.Ordinal);
        Assert.Contains("testAssemblySha256", tier1Runner, StringComparison.Ordinal);
        Assert.Contains("sourceCommit", tier1Runner, StringComparison.Ordinal);
        Assert.Contains("trxVerified", gate, StringComparison.Ordinal);
        Assert.Contains("SQL_TIER1_BUILD_MISMATCH", gate, StringComparison.Ordinal);
    }

    [Fact]
    public void Scale_gate_rejects_a_system_database_before_opening_sql()
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"mesingest-scale-reject-{Guid.NewGuid():N}");

        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryPaths.CSharpRoot,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-DatabaseName", "master",
                         "-ProfileDays", "0",
                         "-OutputRoot", outputRoot,
                         "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                     })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment["MES_INGEST_SCALE_EVIDENCE_SQLSERVER"] =
                "Server=127.0.0.1,1;Database=master;User ID=secret-user;Password=secret-password;Encrypt=False;TrustServerCertificate=True";

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "Scale evidence gate did not finish.");
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("Refusing unsafe scale database name", stdout + stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-user", stdout + stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-password", stdout + stderr, StringComparison.Ordinal);
            Assert.False(Directory.Exists(outputRoot));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Evidence_validator_rejects_a_missing_query_surface_plan_without_sql()
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var root = Path.Combine(Path.GetTempPath(), $"mesingest-scale-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixturePath = Path.Combine(root, "fixture.json");
        var surfaces = new[]
        {
            "DemandSeries",
            "ExternallyReadableDemandCatalog",
            "CurrentIngestAttention",
            "Overview",
            "ReadabilityAudit",
            "ErrorSearch",
            "RawEvidence",
        };
        File.WriteAllText(
            fixturePath,
            JsonSerializer.Serialize(new
            {
                queries = surfaces.Select(name => new
                {
                    name,
                    statementCount = 1,
                    actualPlanCount = name == "Overview" ? 0 : 1,
                }),
                statementMetrics = new[] { new { logical_reads = 1 } },
                actualPlans = new[] { new { planSha256 = new string('a', 64) } },
                data = new { series_count = 600, raw_observations = 600 },
                tests = new { satisfied = true },
                build = new { sourceCommit = new string('b', 40) },
                profile = new { canonical = true },
            }));

        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryPaths.CSharpRoot,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-ProfileDays", "0",
                         "-DatabaseName", "MesIngest_Scale_FixtureOnly",
                         "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                         "-ValidateEvidenceFixturePath", fixturePath,
                     })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "Evidence fixture validation did not finish.");
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(
                "MISSING_QUERY_SURFACE_EVIDENCE:Overview",
                stdout + stderr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Set MES_INGEST_SCALE_EVIDENCE_SQLSERVER",
                stdout + stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Percentile_validator_uses_nearest_rank_for_small_tail_samples()
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.CSharpRoot,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-ProfileDays", "0",
                     "-DatabaseName", "MesIngest_Scale_PercentileOnly",
                     "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                     "-ValidatePercentileFixture", "1,2,3,4,100",
                 })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Windows PowerShell did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Percentile fixture validation did not finish.");
        Assert.True(process.ExitCode == 0, stdout + Environment.NewLine + stderr);
        Assert.Contains("p50=3", stdout, StringComparison.Ordinal);
        Assert.Contains("p95=100", stdout, StringComparison.Ordinal);
        Assert.Contains("p99=100", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Set MES_INGEST_SCALE_EVIDENCE_SQLSERVER", stderr, StringComparison.Ordinal);
    }
}
