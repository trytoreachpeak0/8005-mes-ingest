using System.Diagnostics;

namespace MesIngest.Tests;

/// <summary>
/// The cutover drill is run once, under downtime, against a database that is about to be
/// destroyed. A defect that only shows up when it first connects is discovered at the
/// worst possible moment, so these exercise the shipped script itself rather than its text.
/// </summary>
public sealed class CutoverDrillScriptTests
{
    /// <summary>
    /// Points the real <c>Open-CutoverConnection</c> at an address that cannot answer. The
    /// connection must fail as a network failure, which proves the connection string it
    /// built was well formed. A malformed one fails earlier and differently — that is how
    /// the shipped script once rejected its own <c>Initial Catalog</c> keyword.
    /// </summary>
    [Fact]
    public void Open_cutover_connection_builds_a_valid_connection_string()
    {
        var tools = Path.Combine(
            RepositoryPaths.CSharpRoot, "pack", "cutover", "CutoverSqlTools.ps1");
        Assert.True(File.Exists(tools), $"Missing cutover tools: {tools}");

        var script =
            $". '{tools}'; " +
            "$b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new(); " +
            "$b['Server'] = '127.0.0.1,59999'; " +
            "$b['Database'] = 'MesIngestUnreachable'; " +
            "$b['Integrated Security'] = $true; " +
            "$b['Connect Timeout'] = 2; " +
            "try { " +
            "  $c = Open-CutoverConnection -ConnectionString $b.ConnectionString " +
            "        -ForbiddenDatabase 'SomethingElse'; " +
            "  $c.Dispose(); Write-Output 'UNEXPECTED_CONNECT' " +
            "} catch { Write-Output $_.Exception.Message }";

        var result = RunPowerShell(script);

        // A malformed connection string is rejected by the builder before any socket is
        // opened, so reaching a network error is the proof that it was well formed.
        Assert.DoesNotContain("Keyword not supported", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UNEXPECTED_CONNECT", result, StringComparison.Ordinal);
        Assert.Contains(
            "establishing a connection to SQL Server",
            result,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A connection that is already inside the target database cannot drop or restore it,
    /// so the tools refuse that input before anything opens.
    /// </summary>
    [Fact]
    public void Open_cutover_connection_refuses_a_connection_aimed_at_the_target_database()
    {
        var tools = Path.Combine(
            RepositoryPaths.CSharpRoot, "pack", "cutover", "CutoverSqlTools.ps1");

        var script =
            $". '{tools}'; " +
            "$b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new(); " +
            "$b['Server'] = '127.0.0.1,59999'; " +
            "$b['Database'] = 'MesIngestTarget'; " +
            "$b['Integrated Security'] = $true; " +
            "$b['Connect Timeout'] = 2; " +
            "try { " +
            "  $c = Open-CutoverConnection -ConnectionString $b.ConnectionString " +
            "        -ForbiddenDatabase 'MesIngestTarget'; " +
            "  $c.Dispose(); Write-Output 'UNEXPECTED_CONNECT' " +
            "} catch { Write-Output $_.Exception.Message }";

        var result = RunPowerShell(script);

        Assert.Contains(
            "CUTOVER_CONNECTION_TARGETS_THE_DATABASE_ITSELF",
            result,
            StringComparison.Ordinal);
    }

    private static string RunPowerShell(string script)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.CSharpRoot,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo);
        Assert.True(process is not null, "pwsh did not start; the shipped drill cannot be exercised.");

        var output = process!.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return output;
    }
}
