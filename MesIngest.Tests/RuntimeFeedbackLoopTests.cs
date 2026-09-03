using System.Diagnostics;
using System.Text.Json;

namespace MesIngest.Tests;

public sealed class RuntimeFeedbackLoopTests
{
    [Fact]
    public void Packaged_runtime_feedback_collector_covers_the_read_only_ticket_01_contract()
    {
        var csharpRoot = RepositoryPaths.CSharpRoot;
        var collectorPath = Path.Combine(
            csharpRoot,
            "pack",
            "validation",
            "Invoke-RuntimeFeedbackLoop.ps1");

        Assert.True(File.Exists(collectorPath), $"Missing runtime feedback collector: {collectorPath}");

        var collector = File.ReadAllText(collectorPath);
        var tier1Runner = File.ReadAllText(Path.Combine(csharpRoot, "Invoke-RuntimeFeedbackTier1.ps1"));
        var publish = File.ReadAllText(Path.Combine(csharpRoot, "pack", "Publish-MesIngest.ps1"));
        var install = File.ReadAllText(Path.Combine(csharpRoot, "pack", "INSTALL.md"));

        Assert.Contains("Get-CimInstance Win32_Service", collector, StringComparison.Ordinal);
        Assert.Contains("Get-NetTCPConnection", collector, StringComparison.Ordinal);
        Assert.Contains("/api/v2/contract", collector, StringComparison.Ordinal);
        Assert.Contains("/api/v2/watch-overview", collector, StringComparison.Ordinal);
        Assert.Contains("/api/v2/current-ingest-attention", collector, StringComparison.Ordinal);
        Assert.Contains("/api/v2/externally-readable-demand-catalog", collector, StringComparison.Ordinal);
        Assert.Contains("/api/v2/demand-series", collector, StringComparison.Ordinal);
        Assert.Contains("HOST_UNREACHABLE", collector, StringComparison.Ordinal);
        Assert.Contains("SQL_UNAVAILABLE", collector, StringComparison.Ordinal);
        Assert.Contains("QUERY_TIMEOUT", collector, StringComparison.Ordinal);
        Assert.Contains("CONTRACT_MISMATCH", collector, StringComparison.Ordinal);
        Assert.Contains("expectedCapabilityIdentitySha256", collector, StringComparison.Ordinal);
        Assert.Contains("max server memory (MB)", collector, StringComparison.Ordinal);
        Assert.Contains("recovery_model_desc", collector, StringComparison.Ordinal);
        Assert.Contains("sys.database_files", collector, StringComparison.Ordinal);
        Assert.Contains("RESOURCE_SEMAPHORE", collector, StringComparison.Ordinal);
        Assert.Contains("total_spills", collector, StringComparison.Ordinal);
        Assert.Contains("Error: 701", collector, StringComparison.Ordinal);
        Assert.Contains("Error: 17300", collector, StringComparison.Ordinal);
        Assert.Contains("Error: 17312", collector, StringComparison.Ordinal);
        Assert.Contains("notExecuted", collector, StringComparison.Ordinal);
        Assert.Contains("SQL_TIER1_ATTESTATION_NOT_PROVIDED", collector, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(csharpRoot, "Invoke-RuntimeFeedbackTier1.ps1")));
        Assert.Contains("dotnet test MesIngest.Tests", tier1Runner, StringComparison.Ordinal);
        Assert.Contains("LocalDB is rejected", tier1Runner, StringComparison.Ordinal);
        Assert.Contains("runtime-feedback-tier1-attestation.json", tier1Runner, StringComparison.Ordinal);
        Assert.Contains("Convert]::ToInt32($reader.GetValue(1)", tier1Runner, StringComparison.Ordinal);
        Assert.Contains("runtime-feedback.json", collector, StringComparison.Ordinal);
        Assert.Contains("runtime-feedback.md", collector, StringComparison.Ordinal);
        Assert.Contains("Invoke-RuntimeFeedbackLoop.ps1", publish, StringComparison.Ordinal);
        Assert.Contains("Invoke-RuntimeFeedbackLoop.ps1", install, StringComparison.Ordinal);

        Assert.DoesNotContain("Start-Service", collector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Service", collector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Restart-Service", collector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-Service", collector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER DATABASE", collector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER SERVER CONFIGURATION", collector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RECONFIGURE", collector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP DATABASE", collector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pathName =", collector, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Offline_runtime_feedback_run_classifies_host_unreachable_and_never_writes_credentials()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mesingest-runtime-feedback-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(root, "install");
        var serviceRoot = Path.Combine(installRoot, "service");
        var outputRoot = Path.Combine(root, "evidence");
        Directory.CreateDirectory(serviceRoot);
        var trxPath = Path.Combine(root, "tier1.trx");

        const string sqlUser = "ticket01-secret-user";
        const string sqlPassword = "ticket01-secret-password";
        const string urlUser = "ticket01-url-user";
        const string urlPassword = "ticket01-url-password";
        File.WriteAllText(
            Path.Combine(serviceRoot, "appsettings.Local.json"),
            $$"""
            {
              "MesIngest": {
                "Urls": "http://{{urlUser}}:{{urlPassword}}@127.0.0.1:1",
                "SnapshotSource": "Oracle",
                "NewSqlServerConnectionString": "Server=127.0.0.1,1;Database=MesIngestUnavailable;User ID={{sqlUser}};Password={{sqlPassword}};Encrypt=False;TrustServerCertificate=True"
              }
            }
            """);
        File.WriteAllText(
            trxPath,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="725" executed="725" passed="725" failed="0" notExecuted="0" />
              </ResultSummary>
              <TestDefinitions>
                <UnitTest name="synthetic" storage="C:\fake\MesIngest.Tests.dll" />
              </TestDefinitions>
            </TestRun>
            """);

        try
        {
            var script = Path.Combine(
                RepositoryPaths.CSharpRoot,
                "pack",
                "validation",
                "Invoke-RuntimeFeedbackLoop.ps1");
            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryPaths.CSharpRoot,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-InstallRoot", installRoot,
                         "-OutputRoot", outputRoot,
                         "-ServiceName", $"MissingMesIngest{Guid.NewGuid():N}",
                         "-SqlServiceName", $"MissingSql{Guid.NewGuid():N}",
                         "-RequestTimeoutSeconds", "1",
                         "-SqlConnectionTimeoutSeconds", "1",
                         "-EventLookbackHours", "1",
                         "-SqlTestTrxPath", trxPath,
                     })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment["MES_INGEST_TICKET01_SQLSERVER"] =
                $"Server=127.0.0.1,1;Database=master;User ID={sqlUser};Password={sqlPassword};Encrypt=False;TrustServerCertificate=True";
            start.Environment["MES_INGEST_TICKET01_EXPECTED_PRODUCT_MAJOR"] = "16";
            start.Environment["MES_INGEST_TICKET01_EXPECTED_COMPATIBILITY_LEVEL"] = "160";

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("pwsh did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(60_000), "Runtime feedback collector did not finish.");
            Assert.True(process.ExitCode == 0, $"Collector failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");

            var run = Assert.Single(Directory.GetDirectories(outputRoot));
            var json = File.ReadAllText(Path.Combine(run, "runtime-feedback.json"));
            var markdown = File.ReadAllText(Path.Combine(run, "runtime-feedback.md"));
            using var document = JsonDocument.Parse(json);
            var report = document.RootElement;

            Assert.Equal("HOST_UNREACHABLE", report.GetProperty("diagnosis").GetProperty("primaryState").GetString());
            Assert.False(report.GetProperty("sqlServer").GetProperty("connection").GetProperty("connected").GetBoolean());
            Assert.Equal("SQL_UNAVAILABLE", report.GetProperty("sqlServer").GetProperty("connection").GetProperty("classification").GetString());
            Assert.True(report.GetProperty("safety").GetProperty("readOnly").GetBoolean());
            Assert.False(report.GetProperty("safety").GetProperty("serviceStateChanged").GetBoolean());
            Assert.False(report.GetProperty("safety").GetProperty("databaseChanged").GetBoolean());
            Assert.False(report.GetProperty("safety").GetProperty("productionConfigurationChanged").GetBoolean());
            Assert.Equal(725, report.GetProperty("tests").GetProperty("total").GetInt32());
            Assert.Equal(725, report.GetProperty("tests").GetProperty("passed").GetInt32());
            Assert.Equal(0, report.GetProperty("tests").GetProperty("skipped").GetInt32());
            Assert.False(report.GetProperty("tests").GetProperty("realSqlTier1Satisfied").GetBoolean());
            Assert.Equal(
                "SQL_TIER1_ATTESTATION_NOT_PROVIDED",
                report.GetProperty("tests").GetProperty("diagnosticCode").GetString());
            Assert.True(File.Exists(Path.Combine(run, "sha256-inventory.json")));
            Assert.DoesNotContain(sqlUser, json, StringComparison.Ordinal);
            Assert.DoesNotContain(sqlPassword, json, StringComparison.Ordinal);
            Assert.DoesNotContain(sqlUser, markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(sqlPassword, markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(urlUser, json, StringComparison.Ordinal);
            Assert.DoesNotContain(urlPassword, json, StringComparison.Ordinal);
            Assert.DoesNotContain(urlUser, markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(urlPassword, markdown, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
