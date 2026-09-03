using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class SqlServerMemoryProfileTests
{
    [Fact]
    public void Memory_profile_entry_is_part_of_the_release_package_and_documents_fail_closed_operation()
    {
        var scriptPath = MemoryProfileScriptPath();
        Assert.True(File.Exists(scriptPath), $"Missing memory profile entry: {scriptPath}");
        var script = File.ReadAllText(scriptPath);
        var publish = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "Publish-MesIngest.ps1"));
        var packageValidator = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "Test-ReleasePackage.ps1"));
        var install = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "INSTALL.md"));

        Assert.Contains("Invoke-SqlServerMemoryProfile.ps1", publish, StringComparison.Ordinal);
        Assert.Contains(@"scripts\maintenance\Invoke-SqlServerMemoryProfile.ps1", packageValidator, StringComparison.Ordinal);
        Assert.Contains("Invoke-SqlServerMemoryProfile.ps1", install, StringComparison.Ordinal);
        Assert.Contains("MES_INGEST_SQLSERVER_ADMIN", install, StringComparison.Ordinal);
        Assert.Contains("1536", install, StringComparison.Ordinal);
        Assert.Contains("2048", install, StringComparison.Ordinal);
        Assert.Contains("800", install, StringComparison.Ordinal);
        Assert.Contains("SQL_ADMIN_PERMISSION_REQUIRED", script, StringComparison.Ordinal);
        Assert.Contains("SQL_INSTANCE_IDENTITY_MISMATCH", script, StringComparison.Ordinal);
        Assert.Contains("SQL_CONFIGURATION_READ_FAILED", script, StringComparison.Ordinal);
        Assert.Contains("NORMAL_PROFILE_RESTORE_FAILED", script, StringComparison.Ordinal);
        Assert.Contains("partialApplicationReportedAsSuccess = $false", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(800)]
    [InlineData(512)]
    public void Configuration_validation_rejects_the_known_unrunnable_memory_envelope_before_sql(
        int requestedMaxServerMemoryMb)
    {
        var script = MemoryProfileScriptPath();
        var outputRoot = Path.Combine(
            Path.GetTempPath(),
            $"mesingest-memory-profile-reject-{Guid.NewGuid():N}");

        try
        {
            var start = NewPowerShellStart(script);
            AddArguments(
                start,
                "-Action", "Validate",
                "-RequestedMaxServerMemoryMb", requestedMaxServerMemoryMb.ToString(),
                "-OutputRoot", outputRoot);
            start.Environment["MES_INGEST_SQLSERVER_ADMIN"] =
                "Server=127.0.0.1,1;Database=master;User ID=secret-user;Password=secret-password;Encrypt=False;TrustServerCertificate=True";

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("pwsh did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "Memory profile validator did not finish.");

            var output = stdout + stderr;
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("UNSUPPORTED_MEMORY_PROFILE", output, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-user", output, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-password", output, StringComparison.Ordinal);
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

    [Ticket01SqlServerFact]
    public void Normal_profile_is_applied_and_emits_the_required_memory_diagnostics()
    {
        var connectionString = Environment.GetEnvironmentVariable("MES_INGEST_TICKET01_SQLSERVER")
                               ?? throw new InvalidOperationException("Real SQL Server connection is missing.");
        var identity = ReadServerIdentity(connectionString);
        var outputRoot = Path.Combine(
            Path.GetTempPath(),
            $"mesingest-memory-profile-normal-{Guid.NewGuid():N}");

        try
        {
            var start = NewPowerShellStart(MemoryProfileScriptPath());
            AddArguments(
                start,
                "-Action", "ApplyNormal",
                "-RequestedMaxServerMemoryMb", "1536",
                "-ExpectedMachineName", identity.MachineName,
                "-ExpectedInstanceName", identity.InstanceName,
                "-ConfirmInstance", $"{identity.MachineName}\\{identity.InstanceName}",
                "-StabilitySampleCount", "3",
                "-StabilitySampleIntervalSeconds", "1",
                "-OutputRoot", outputRoot);
            start.Environment["MES_INGEST_SQLSERVER_ADMIN"] = connectionString;

            var result = Run(start, TimeSpan.FromMinutes(1));
            Assert.True(
                result.ExitCode == 0,
                $"Normal profile failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");

            var runDirectory = Assert.Single(Directory.GetDirectories(outputRoot));
            var reportPath = Path.Combine(runDirectory, "sql-memory-profile.json");
            Assert.True(File.Exists(reportPath), $"Missing memory profile report: {reportPath}");
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            var report = document.RootElement;

            Assert.Equal("SUCCEEDED", report.GetProperty("status").GetString());
            Assert.Equal("ApplyNormal", report.GetProperty("action").GetString());
            Assert.Equal(1536, report.GetProperty("configuration").GetProperty("requestedMb").GetInt32());
            Assert.Equal(1536, report.GetProperty("configuration").GetProperty("afterMb").GetInt32());
            Assert.Equal(1536, report.GetProperty("configuration").GetProperty("valueInUseMb").GetInt32());
            Assert.True(report.GetProperty("diagnostics").GetProperty("committedMemory").GetProperty("available").GetBoolean());
            Assert.True(report.GetProperty("diagnostics").GetProperty("workspaceMemory").GetProperty("available").GetBoolean());
            Assert.True(report.GetProperty("diagnostics").GetProperty("grantWaits").GetProperty("available").GetBoolean());
            Assert.True(report.GetProperty("diagnostics").GetProperty("resourceSemaphore").GetProperty("available").GetBoolean());
            Assert.True(report.GetProperty("diagnostics").GetProperty("error701").GetProperty("available").GetBoolean());
            Assert.True(report.GetProperty("diagnostics").GetProperty("spills").GetProperty("available").GetBoolean());
            Assert.Equal(3, report.GetProperty("diagnostics").GetProperty("processEnvelope").GetProperty("sampleCount").GetInt32());
            Assert.Equal(2048, report.GetProperty("diagnostics").GetProperty("processEnvelope").GetProperty("targetMb").GetInt32());
            Assert.True(report.GetProperty("diagnostics").GetProperty("processEnvelope").GetProperty("satisfied").GetBoolean());
            Assert.False(report.GetProperty("safety").GetProperty("credentialsWritten").GetBoolean());
            Assert.False(report.GetProperty("safety").GetProperty("connectionStringWritten").GetBoolean());

            var json = File.ReadAllText(reportPath);
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                Assert.DoesNotContain(builder.Password, json, StringComparison.Ordinal);
            }
            Assert.True(File.Exists(Path.Combine(runDirectory, "sql-memory-profile.md")));
            Assert.True(File.Exists(Path.Combine(runDirectory, "sha256-inventory.json")));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Ticket01SqlServerTheory]
    [InlineData(0, "SUCCEEDED", true)]
    [InlineData(9, "FAILED", false)]
    [InlineData(1223, "CANCELLED", false)]
    public void Maintenance_profile_runs_only_at_2048_mb_and_always_restores_normal(
        int maintenanceExitCode,
        string expectedOutcome,
        bool expectedSuccess)
    {
        var connectionString = Environment.GetEnvironmentVariable("MES_INGEST_TICKET01_SQLSERVER")
                               ?? throw new InvalidOperationException("Real SQL Server connection is missing.");
        var identity = ReadServerIdentity(connectionString);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"mesingest-memory-maintenance-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(root, "evidence");
        Directory.CreateDirectory(root);
        var maintenanceScript = Path.Combine(root, "assert-maintenance-profile.ps1");
        File.WriteAllText(
            maintenanceScript,
            """
            Add-Type -AssemblyName System.Data
            $connectionString = [Environment]::GetEnvironmentVariable('MES_INGEST_SQLSERVER_ADMIN')
            $connection = [System.Data.SqlClient.SqlConnection]::new($connectionString)
            try {
                $connection.Open()
                $command = $connection.CreateCommand()
                try {
                    $command.CommandText = "SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = N'max server memory (MB)';"
                    $value = [int]$command.ExecuteScalar()
                    if ($value -ne 2048) { [Environment]::Exit(42) }
                } finally { $command.Dispose() }
            } finally { $connection.Dispose() }
            [Environment]::Exit([int]$args[0])
            """);

        try
        {
            var start = NewPowerShellStart(MemoryProfileScriptPath());
            AddArguments(
                start,
                "-Action", "RunMaintenance",
                "-RequestedMaxServerMemoryMb", "2048",
                "-ExpectedMachineName", identity.MachineName,
                "-ExpectedInstanceName", identity.InstanceName,
                "-ConfirmInstance", $"{identity.MachineName}\\{identity.InstanceName}",
                "-Reason", "ticket-03 isolated maintenance profile verification",
                "-MaintenanceScriptPath", maintenanceScript,
                "-MaintenanceArgument", maintenanceExitCode.ToString(),
                "-StabilitySampleCount", "1",
                "-StabilitySampleIntervalSeconds", "0",
                "-OutputRoot", outputRoot);
            start.Environment["MES_INGEST_SQLSERVER_ADMIN"] = connectionString;

            var result = Run(start, TimeSpan.FromMinutes(1));
            var runDirectory = Assert.Single(Directory.GetDirectories(outputRoot));
            var reportJson = File.ReadAllText(Path.Combine(runDirectory, "sql-memory-profile.json"));
            Assert.True(
                expectedSuccess == (result.ExitCode == 0),
                $"Unexpected wrapper exit.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}{Environment.NewLine}{reportJson}");
            using var document = JsonDocument.Parse(reportJson);
            var report = document.RootElement;
            Assert.Equal(expectedSuccess ? "SUCCEEDED" : expectedOutcome, report.GetProperty("status").GetString());
            Assert.True(
                string.Equals(
                    expectedOutcome,
                    report.GetProperty("maintenance").GetProperty("outcome").GetString(),
                    StringComparison.Ordinal),
                reportJson);
            Assert.Equal(2048, report.GetProperty("configuration").GetProperty("afterMb").GetInt32());
            Assert.True(report.GetProperty("configuration").GetProperty("restoreAttempted").GetBoolean());
            Assert.True(report.GetProperty("configuration").GetProperty("restoreSucceeded").GetBoolean());
            Assert.Equal(1536, report.GetProperty("configuration").GetProperty("restoredMb").GetInt32());
            Assert.Equal(
                "ticket-03 isolated maintenance profile verification",
                report.GetProperty("reason").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                report.GetProperty("operator").GetProperty("windowsIdentity").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(
                report.GetProperty("maintenance").GetProperty("startedAt").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(
                report.GetProperty("maintenance").GetProperty("completedAt").GetString()));
            Assert.Equal(1536, ReadMaxServerMemory(connectionString));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Ticket01SqlServerFact]
    public void Timed_out_maintenance_is_terminated_and_restores_normal()
    {
        var connectionString = Environment.GetEnvironmentVariable("MES_INGEST_TICKET01_SQLSERVER")
                               ?? throw new InvalidOperationException("Real SQL Server connection is missing.");
        var identity = ReadServerIdentity(connectionString);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"mesingest-memory-timeout-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(root, "evidence");
        Directory.CreateDirectory(root);
        var maintenanceScript = Path.Combine(root, "wait-past-timeout.ps1");
        File.WriteAllText(maintenanceScript, "Start-Sleep -Seconds 5");

        try
        {
            var start = NewPowerShellStart(MemoryProfileScriptPath());
            AddArguments(
                start,
                "-Action", "RunMaintenance",
                "-RequestedMaxServerMemoryMb", "2048",
                "-ExpectedMachineName", identity.MachineName,
                "-ExpectedInstanceName", identity.InstanceName,
                "-ConfirmInstance", $"{identity.MachineName}\\{identity.InstanceName}",
                "-Reason", "ticket-03 timeout restoration verification",
                "-MaintenanceScriptPath", maintenanceScript,
                "-MaintenanceTimeoutSeconds", "1",
                "-StabilitySampleCount", "1",
                "-StabilitySampleIntervalSeconds", "0",
                "-OutputRoot", outputRoot);
            start.Environment["MES_INGEST_SQLSERVER_ADMIN"] = connectionString;

            var result = Run(start, TimeSpan.FromMinutes(1));
            Assert.NotEqual(0, result.ExitCode);
            var runDirectory = Assert.Single(Directory.GetDirectories(outputRoot));
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(runDirectory, "sql-memory-profile.json")));
            var report = document.RootElement;
            Assert.Equal("FAILED", report.GetProperty("status").GetString());
            Assert.Equal("MAINTENANCE_TIMED_OUT", report.GetProperty("diagnosticCode").GetString());
            Assert.Equal("TIMED_OUT", report.GetProperty("maintenance").GetProperty("outcome").GetString());
            Assert.True(report.GetProperty("configuration").GetProperty("restoreSucceeded").GetBoolean());
            Assert.Equal(1536, ReadMaxServerMemory(connectionString));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Ticket01SqlServerFact]
    public void Wrong_instance_confirmation_fails_before_changing_the_current_profile()
    {
        var connectionString = Environment.GetEnvironmentVariable("MES_INGEST_TICKET01_SQLSERVER")
                               ?? throw new InvalidOperationException("Real SQL Server connection is missing.");
        var outputRoot = Path.Combine(
            Path.GetTempPath(),
            $"mesingest-memory-wrong-instance-{Guid.NewGuid():N}");
        var before = ReadMaxServerMemory(connectionString);

        try
        {
            var start = NewPowerShellStart(MemoryProfileScriptPath());
            AddArguments(
                start,
                "-Action", "ApplyNormal",
                "-RequestedMaxServerMemoryMb", "1536",
                "-ExpectedMachineName", "definitely-not-this-machine",
                "-ExpectedInstanceName", "MSSQLSERVER",
                "-ConfirmInstance", @"definitely-not-this-machine\MSSQLSERVER",
                "-StabilitySampleCount", "1",
                "-StabilitySampleIntervalSeconds", "0",
                "-OutputRoot", outputRoot);
            start.Environment["MES_INGEST_SQLSERVER_ADMIN"] = connectionString;

            var result = Run(start, TimeSpan.FromMinutes(1));
            Assert.NotEqual(0, result.ExitCode);
            var runDirectory = Assert.Single(Directory.GetDirectories(outputRoot));
            var json = File.ReadAllText(Path.Combine(runDirectory, "sql-memory-profile.json"));
            using var document = JsonDocument.Parse(json);
            Assert.Equal("FAILED", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "SQL_INSTANCE_IDENTITY_MISMATCH",
                document.RootElement.GetProperty("diagnosticCode").GetString());
            Assert.False(
                document.RootElement.GetProperty("safety").GetProperty("exactInstanceConfirmed").GetBoolean());
            Assert.Equal(before, ReadMaxServerMemory(connectionString));
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
    public void Administrative_connection_failure_is_audited_without_credentials_or_success()
    {
        const string sqlUser = "ticket03-secret-user";
        const string sqlPassword = "ticket03-secret-password";
        var outputRoot = Path.Combine(
            Path.GetTempPath(),
            $"mesingest-memory-connection-failure-{Guid.NewGuid():N}");

        try
        {
            var start = NewPowerShellStart(MemoryProfileScriptPath());
            AddArguments(
                start,
                "-Action", "ApplyNormal",
                "-RequestedMaxServerMemoryMb", "1536",
                "-ExpectedMachineName", "unreachable",
                "-ExpectedInstanceName", "MSSQLSERVER",
                "-ConfirmInstance", @"unreachable\MSSQLSERVER",
                "-SqlConnectionTimeoutSeconds", "1",
                "-OutputRoot", outputRoot);
            start.Environment["MES_INGEST_SQLSERVER_ADMIN"] =
                $"Server=127.0.0.1,1;Database=master;User ID={sqlUser};Password={sqlPassword};Encrypt=False;TrustServerCertificate=True";

            var result = Run(start, TimeSpan.FromSeconds(30));
            Assert.NotEqual(0, result.ExitCode);
            var runDirectory = Assert.Single(Directory.GetDirectories(outputRoot));
            var json = File.ReadAllText(Path.Combine(runDirectory, "sql-memory-profile.json"));
            using var document = JsonDocument.Parse(json);
            var report = document.RootElement;
            Assert.Equal("FAILED", report.GetProperty("status").GetString());
            Assert.Equal("SQL_CONNECTION_FAILED", report.GetProperty("diagnosticCode").GetString());
            Assert.False(report.GetProperty("safety").GetProperty("exactInstanceConfirmed").GetBoolean());
            Assert.False(report.GetProperty("safety").GetProperty("partialApplicationReportedAsSuccess").GetBoolean());
            Assert.DoesNotContain(sqlUser, json, StringComparison.Ordinal);
            Assert.DoesNotContain(sqlPassword, json, StringComparison.Ordinal);
            Assert.DoesNotContain(sqlUser, result.Stdout + result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(sqlPassword, result.Stdout + result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    private static ProcessStartInfo NewPowerShellStart(string script)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.CSharpRoot,
        };
        AddArguments(
            start,
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy", "Bypass",
            "-File", script);
        return start;
    }

    private static string MemoryProfileScriptPath() =>
        Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "maintenance",
            "Invoke-SqlServerMemoryProfile.ps1");

    private static (string MachineName, string InstanceName) ReadServerIdentity(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
            ConnectTimeout = 5,
        };
        using var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')),
                   ISNULL(CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName')), N'MSSQLSERVER');
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), "SQL Server identity query returned no row.");
        return (reader.GetString(0), reader.GetString(1));
    }

    private static int ReadMaxServerMemory(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
            ConnectTimeout = 5,
        };
        using var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = N'max server memory (MB)';";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ProcessResult Run(ProcessStartInfo start, TimeSpan timeout)
    {
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("pwsh did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(timeout), "Memory profile command did not finish.");
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static void AddArguments(ProcessStartInfo start, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class Ticket01SqlServerTheoryAttribute : TheoryAttribute
{
    public Ticket01SqlServerTheoryAttribute()
    {
        if (!Ticket01SqlServerDatabase.IsAvailable)
        {
            Skip = "Real SQL Server unavailable for Ticket 03 memory-profile gate";
        }
    }
}
