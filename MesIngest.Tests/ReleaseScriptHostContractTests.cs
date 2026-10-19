using System.Diagnostics;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

/// <summary>
/// The release smoke and the factory acceptance run start the packaged Host and judge
/// what it serves, but the repository tests only reach the part of those scripts that
/// runs before a database exists. Two Host changes went past them unseen for two
/// months (#12): the catalog ETag gained a HistoryEpoch (55b427f), and a production
/// Host started refusing to run without an explicit producer mode (f8c7141). Both
/// surfaced only on a release-day smoke. These checks hold the scripts to the Host
/// without a SQL Server.
/// </summary>
public sealed class ReleaseScriptHostContractTests
{
    public static TheoryData<string> CatalogEtagScripts { get; } = new()
    {
        "Invoke-ReleaseSmoke.ps1",
        "Invoke-FactoryAcceptance.ps1",
    };

    [Theory]
    [MemberData(nameof(CatalogEtagScripts))]
    public void Expected_catalog_etag_is_the_one_the_host_codec_formats(string scriptName)
    {
        var historyEpoch = Guid.Parse("5f0c3a1e-9b7d-4e21-8c44-1d2e3f405162");
        const long catalogRevision = 42;
        var hostEtag = "W/\"" + ExternallyReadableDemandCatalogEtagCodec.FormatOpaqueTag(
            new ExternallyReadableDemandCatalogIdentity(
                HistoryEpoch.FromGuid(historyEpoch),
                catalogRevision)) + "\"";

        // The body carries HistoryEpoch in "D" format, so that is what the script gets.
        var scriptEtag = RunPowerShell(
            ScriptPath(scriptName),
            $$"""
            $function = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -ceq 'Get-ExpectedCatalogEtag'
            }, $true)
            if ($null -eq $function) { throw 'Get-ExpectedCatalogEtag is not defined.' }
            Invoke-Expression $function.Extent.Text
            Get-ExpectedCatalogEtag -HistoryEpoch '{{historyEpoch:D}}' -CatalogRevision {{catalogRevision}}
            """).Trim();

        Assert.Equal(hostEtag, scriptEtag);
    }

    [Theory]
    [MemberData(nameof(CatalogEtagScripts))]
    public void Catalog_etag_is_only_compared_through_the_shared_expectation(string scriptName)
    {
        var text = File.ReadAllText(ScriptPath(scriptName));

        Assert.DoesNotContain("catalog-r", text, StringComparison.Ordinal);
        Assert.Contains("= Get-ExpectedCatalogEtag `", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A start the smoke means to succeed must name a producer mode the Host accepts:
    /// a poll switch, or the recording. A start that is expected to refuse reads stderr
    /// (-DrainStreams $false) and asserts its own refusal reason instead.
    /// </summary>
    [Fact]
    public void Every_release_smoke_host_start_names_a_producer_mode_or_expects_a_refusal()
    {
        var output = RunPowerShell(
            ScriptPath("Invoke-ReleaseSmoke.ps1"),
            """
            $starts = $ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -ceq 'Start-PackagedHostProcess'
            }, $true)
            @($starts | ForEach-Object {
                $arguments = @{}
                $elements = $_.CommandElements
                for ($index = 1; $index -lt $elements.Count; $index++) {
                    $element = $elements[$index]
                    if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
                        $value = if ($null -ne $element.Argument) {
                            $element.Argument.Extent.Text
                        } elseif ($index + 1 -lt $elements.Count -and
                            $elements[$index + 1] -isnot [System.Management.Automation.Language.CommandParameterAst]) {
                            $index++
                            $elements[$index].Extent.Text
                        } else {
                            '$true'
                        }
                        $arguments[$element.ParameterName] = $value
                    }
                }
                [ordered]@{ line = $_.Extent.StartLineNumber; arguments = $arguments }
            }) | ConvertTo-Json -Depth 4 -AsArray
            """);

        using var starts = JsonDocument.Parse(output);
        var calls = starts.RootElement.EnumerateArray().ToArray();
        Assert.True(calls.Length >= 2, $"Expected several packaged Host starts; found {calls.Length}.");
        foreach (var call in calls)
        {
            var arguments = call.GetProperty("arguments");
            string? Argument(string name) =>
                arguments.TryGetProperty(name, out var value) ? value.GetString() : null;

            var producerMode = Argument("RecordingPath") is not null
                               || Argument("ContinuousPoll") == "$true"
                               || Argument("OneShotOnStartup") == "$true";
            var expectsRefusal = Argument("DrainStreams") == "$false";
            Assert.True(
                producerMode || expectsRefusal,
                $"Invoke-ReleaseSmoke.ps1:{call.GetProperty("line").GetInt32()} starts a Host that "
                + "ProductionHostStartupPolicy refuses: no -RecordingPath, no poll switch, and it "
                + "is not a start that expects a refusal.");
        }
    }

    private static string ScriptPath(string scriptName) =>
        Path.Combine(RepositoryPaths.CSharpRoot, "pack", "validation", scriptName);

    private static string RunPowerShell(string scriptPath, string body)
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
        startInfo.ArgumentList.Add(
            "$ErrorActionPreference = 'Stop'\n"
            + "$parseErrors = $null\n"
            + "$ast = [System.Management.Automation.Language.Parser]::ParseFile("
            + "$env:MES_INGEST_TEST_SCRIPT_PATH, [ref]$null, [ref]$parseErrors)\n"
            + "if ($parseErrors.Count -gt 0) { throw \"Parse errors: $($parseErrors -join '; ')\" }\n"
            + body);
        startInfo.Environment["MES_INGEST_TEST_SCRIPT_PATH"] = scriptPath;

        using var process = Process.Start(startInfo);
        Assert.True(process is not null, "pwsh did not start; the shipped scripts cannot be inspected.");

        var standardOutput = process!.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "pwsh did not finish inspecting the script.");
        Assert.True(
            process.ExitCode == 0,
            $"pwsh exited {process.ExitCode}: {standardError.Result}");
        return standardOutput.Result;
    }
}
