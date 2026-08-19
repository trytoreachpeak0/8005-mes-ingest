using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using MesIngest.Host;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 25 automation. The replaced contract is gone rather than disabled, database
/// deletion exists only in the attended cutover drill, and neither the repository nor
/// the release artifacts carry a real credential.
///
/// These read the tracked working tree, so they fail on a stale file the compiler never
/// looks at — a runbook, a template, a deployment script — which is exactly where a
/// retired entry point survives longest.
/// </summary>
public sealed partial class RetiredContractAndCutoverSafetyTests
{
    private static readonly string[] RetiredTypeNameFragments =
    [
        "IngestAlert",
        "DemandChangeFeed",
        "FrozenMesFieldSet",
        "TransportDemandReconciler",
        "SyncCursorExpired",
        "DemandListQuery",
        "AlertListQuery",
        "MesSnapshotSource",
        "TransportDemandStore",
    ];

    /// <summary>
    /// Executable, shippable artifacts. A DROP here is a path an operator can run; a DROP
    /// in a runbook is documentation, and a DROP in a test fixture runs only against that
    /// fixture's own disposable database on an explicitly opted-in server.
    /// </summary>
    private static readonly string[] ExecutableExtensions = [".ps1", ".psm1", ".cs", ".sql"];

    /// <summary>
    /// Where a real credential would actually ship: configuration, templates, deployment
    /// scripts, and operator documentation. Source files assign <c>Password</c> as a
    /// property, which is a name, not a secret.
    /// </summary>
    private static readonly string[] CredentialBearingExtensions =
    [
        ".json", ".example", ".ps1", ".psm1", ".md", ".txt", ".config", ".xml",
    ];

    private static readonly string[] TestProjectPrefixes =
    [
        "MesIngest.Tests/",
        "MesIngest.Watch.UiTests/",
    ];

    [Fact]
    public void Production_assemblies_expose_no_retired_contract_type()
    {
        var assemblies = new[]
        {
            typeof(MesIngest.Core.SeriesProjection.NewMesIngestContract).Assembly,
            typeof(MesIngestHostOptions).Assembly,
            typeof(MesIngest.Infrastructure.SqlServer.ProjectionCommitCheckpoint).Assembly,
            typeof(MesIngest.Watch.WatchOptionsLoader).Assembly,
        };

        var offenders = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Select(type => type.FullName ?? type.Name)
            .Where(name => RetiredTypeNameFragments.Any(
                fragment => name.Contains(fragment, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "The shipped assemblies still define retired-contract types: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void The_attended_cutover_drill_is_the_only_thing_that_deletes_a_database()
    {
        var cutoverDirectory = Path.Combine(RepositoryPaths.CSharpRoot, "pack", "cutover")
            + Path.DirectorySeparatorChar;

        var offenders = TrackedFiles(ExecutableExtensions, includeTestProjects: false)
            .Where(path => !path.StartsWith(cutoverDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(path => DropDatabaseRegex().IsMatch(File.ReadAllText(path)))
            .Select(RepositoryPaths.ToRelative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Only the attended cutover drill may delete a database; these can too: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// A retired key is rejected at startup, so any source that still injects one into a
    /// Host configuration breaks that Host. Most of those injections live in SQL-gated
    /// tests, which skip wherever no real SQL Server answers — so without this check the
    /// breakage only surfaces on the golden machine.
    /// </summary>
    [Fact]
    public void No_source_injects_a_retired_configuration_key_into_a_host()
    {
        var offenders = new List<string>();
        foreach (var path in TrackedFiles([".cs", ".ps1", ".psm1"], includeTestProjects: true))
        {
            var relative = RepositoryPaths.ToRelative(path);
            if (relative.EndsWith(nameof(RetiredContractAndCutoverSafetyTests) + ".cs", StringComparison.Ordinal)
                || relative.EndsWith("MesIngestHostOptions.cs", StringComparison.Ordinal)
                || relative.EndsWith("Invoke-ReleaseSmoke.ps1", StringComparison.Ordinal))
            {
                // These three define, document, or deliberately exercise the rejection.
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                foreach (var key in MesIngestHostOptions.RetiredConfigurationKeys)
                {
                    if (line.Contains($"__{key}\"", StringComparison.Ordinal)
                        || line.Contains($":{key}\"", StringComparison.Ordinal))
                    {
                        offenders.Add($"{relative}:{lineNumber} ({key})");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These still configure a Host with a retired key, which now fails startup: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Service_install_and_uninstall_never_touch_a_database()
    {
        foreach (var script in new[] { "install-service.ps1", "uninstall-service.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(RepositoryPaths.CSharpRoot, "pack", script));
            Assert.DoesNotContain("DROP DATABASE", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RESTORE DATABASE", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SqlConnection", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_cutover_and_rollback_drills_cannot_run_unattended()
    {
        foreach (var drill in new[]
                 {
                     "Invoke-EmptyDatabaseCutover.ps1",
                     "Invoke-CutoverRollback.ps1",
                 })
        {
            var text = File.ReadAllText(
                Path.Combine(RepositoryPaths.CSharpRoot, "pack", "cutover", drill));

            Assert.Contains("Assert-CutoverOperatorConfirmation", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(BypassSwitchRegex(), text);
        }

        var tools = File.ReadAllText(
            Path.Combine(RepositoryPaths.CSharpRoot, "pack", "cutover", "CutoverSqlTools.ps1"));

        // The confirmation is typed at the console and compared to the identity the
        // connection itself resolved. A redirected stdin cannot satisfy it, so an
        // unattended invocation stops before anything is dropped.
        Assert.Contains("Read-Host", tools, StringComparison.Ordinal);
        Assert.Contains("SERVERPROPERTY('MachineName')", tools, StringComparison.Ordinal);
        Assert.Contains("CUTOVER_TARGET_NOT_CONFIRMED", tools, StringComparison.Ordinal);
        Assert.DoesNotMatch(BypassSwitchRegex(), tools);
    }

    [Theory]
    [InlineData("Password=SuperSecret123;Data Source=plant", true)]
    [InlineData("    \"OraclePassword\": \"fwmes\",", true)]
    [InlineData("    \"SharedSecret\": \"plant-token-9f2\",", true)]
    [InlineData("Password=<MES_PASSWORD_FROM_SECRET_STORE>;", false)]
    [InlineData("    \"OraclePassword\": \"\",", false)]
    [InlineData("    \"SharedSecret\": \"Leave blank for the localhost default.\",", false)]
    public void The_credential_scan_recognizes_a_real_secret_and_ignores_a_placeholder(
        string line,
        bool expectedToBeFlagged)
    {
        Assert.Equal(expectedToBeFlagged, FindCredentialsOnLine(line).Any());
    }

    [Fact]
    public void No_tracked_file_carries_a_real_credential()
    {
        var offenders = new List<string>();
        foreach (var path in TrackedFiles(CredentialBearingExtensions, includeTestProjects: true))
        {
            var relative = RepositoryPaths.ToRelative(path);
            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                foreach (var _ in FindCredentialsOnLine(line))
                {
                    offenders.Add($"{relative}:{lineNumber}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Tracked files must not carry a real credential: " + string.Join(", ", offenders));
    }

    private static IEnumerable<string> FindCredentialsOnLine(string line)
    {
        foreach (Match match in CredentialAssignmentRegex().Matches(line))
        {
            var value = match.Groups["value"].Value.Trim().Trim('"', '\'', ',', ';');
            if (!IsPlaceholderCredential(value))
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// A placeholder is anything that cannot authenticate: empty, a bracketed token, an
    /// obvious mask, an integrated-security style value, or prose. A value containing
    /// whitespace is a sentence explaining the key, not a secret.
    /// </summary>
    private static bool IsPlaceholderCredential(string value) =>
        value.Length == 0
        || value.Any(char.IsWhiteSpace)
        || value.StartsWith('<')
        || value.StartsWith('$')
        || value.StartsWith('@')
        || value.Contains("***", StringComparison.Ordinal)
        || value.Contains("masked", StringComparison.OrdinalIgnoreCase)
        || value.Contains("redacted", StringComparison.OrdinalIgnoreCase)
        || value.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
        || value.Contains("invalid", StringComparison.OrdinalIgnoreCase)
        || value.Contains("example", StringComparison.OrdinalIgnoreCase)
        || value.Contains("retired-value", StringComparison.Ordinal)
        || value.Equals("True", StringComparison.OrdinalIgnoreCase)
        || value.Equals("False", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> TrackedFiles(
        IReadOnlyList<string> extensions,
        bool includeTestProjects)
    {
        foreach (var relative in GitTrackedRelativePaths())
        {
            var normalized = relative.Replace('\\', '/');
            if (!includeTestProjects
                && TestProjectPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            var extension = Path.GetExtension(relative);
            if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(RepositoryPaths.CSharpRoot, relative));
            if (File.Exists(full))
            {
                yield return full;
            }
        }
    }

    private static IReadOnlyList<string> GitTrackedRelativePaths()
    {
        var startInfo = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = RepositoryPaths.CSharpRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not run git ls-files.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "git ls-files failed; the scan cannot claim repository coverage.");

        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
    }

    [GeneratedRegex(@"DROP\s+DATABASE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DropDatabaseRegex();

    [GeneratedRegex(
        @"\[switch\]\s*\$(Force|Yes|NonInteractive|Unattended|SkipConfirmation)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BypassSwitchRegex();

    [GeneratedRegex(
        @"(?:\bPassword\s*=|\bPwd\s*=|""(?:OraclePassword|SharedSecret)""\s*:)\s*(?<value>[^;,\r\n}]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentRegex();
}

internal static class RepositoryPaths
{
    public static string CSharpRoot { get; } = Resolve();

    public static string ToRelative(string fullPath) =>
        Path.GetRelativePath(CSharpRoot, fullPath).Replace('\\', '/');

    private static string Resolve()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MesIngest.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate mes/ingest/csharp root from the test base directory.");
    }
}
