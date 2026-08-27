using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using MesIngest.Host;

namespace MesIngest.Tests;

public sealed class HostStartupSecurityProcessTests
{
    [Fact]
    public async Task Process_rejects_Kestrel_endpoint_before_it_can_override_loopback_MesIngest_urls()
    {
        var configuredPort = GetFreeTcpPort();
        var overridingPort = GetFreeTcpPort();
        using var process = StartHost(new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["MesIngest__Urls"] = $"http://127.0.0.1:{configuredPort}",
            ["MesIngest__SnapshotSource"] = "None",
            ["MesIngest__ContinuousPollEnabled"] = "false",
            ["MesIngest__RunOneShotOnStartup"] = "false",
            ["Kestrel__Endpoints__Remote__Url"] = $"http://0.0.0.0:{overridingPort}",
        });

        var result = await WaitForExitAsync(process, TimeSpan.FromSeconds(15));

        Assert.False(result.TimedOut, result.Output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Kestrel:Endpoints", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MesIngest:Urls", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Process_remote_MesIngest_binding_requires_the_startup_policy_secret()
    {
        var port = GetFreeTcpPort();
        using var process = StartHost(new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["MesIngest__Urls"] = $"http://0.0.0.0:{port}",
            ["MesIngest__SharedSecret"] = "process-secret",
            ["MesIngest__SnapshotSource"] = "None",
            ["MesIngest__ContinuousPollEnabled"] = "false",
            ["MesIngest__RunOneShotOnStartup"] = "false",
        });
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(2),
        };

        using var unauthorized = await WaitForResponseAsync(process, client, "/security-policy-probe");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "process-secret");
        using var authorized = await client.GetAsync("/security-policy-probe");
        Assert.Equal(HttpStatusCode.NotFound, authorized.StatusCode);

        await StopAsync(process);
    }

    [Theory]
    [InlineData("None", "MesIngest:SnapshotSource must be Oracle")]
    [InlineData("Oracle", "producer mode")]
    public async Task Production_process_rejects_missing_current_MES_producer(
        string snapshotSource,
        string expectedMessage)
    {
        var port = GetFreeTcpPort();
        using var process = StartHost(new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["MesIngest__Urls"] = $"http://127.0.0.1:{port}",
            ["MesIngest__NewSqlServerConnectionString"] =
                "Server=startup.invalid;Database=startup;Integrated Security=true;Encrypt=false",
            ["MesIngest__SnapshotSource"] = snapshotSource,
            ["MesIngest__ContinuousPollEnabled"] = "false",
            ["MesIngest__RunOneShotOnStartup"] = "false",
        });

        var result = await WaitForExitAsync(process, TimeSpan.FromSeconds(15));

        Assert.False(result.TimedOut, result.Output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedMessage, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_policy_preserves_the_explicit_live_Oracle_probe_mode()
    {
        var options = new MesIngestHostOptions
        {
            SnapshotSource = MesIngestHostOptions.OracleRoundSource,
            ContinuousPollEnabled = false,
            RunOneShotOnStartup = false,
        };

        ProductionHostStartupPolicy.Validate(
            options,
            isDevelopment: false,
            probeOracle: true,
            projectionEnabled: false);
    }

    [Fact]
    public void Production_policy_preserves_the_explicit_recorded_round_release_smoke_mode()
    {
        var options = new MesIngestHostOptions
        {
            SnapshotSource = MesIngestHostOptions.OracleRoundSource,
            ReplayRoundsFromRecordingPath = "explicit-release-smoke-rounds.json",
            ContinuousPollEnabled = false,
            RunOneShotOnStartup = false,
        };

        ProductionHostStartupPolicy.Validate(
            options,
            isDevelopment: false,
            probeOracle: false,
            projectionEnabled: true);
    }

    private static Process StartHost(IReadOnlyDictionary<string, string?> environment)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.CSharpRoot,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--no-launch-profile");
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(configuration);
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "MesIngest.Host",
            "MesIngest.Host.csproj"));

        foreach (var key in start.Environment.Keys
                     .Where(key => key.StartsWith("MesIngest__", StringComparison.OrdinalIgnoreCase)
                                   || key.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            start.Environment.Remove(key);
        }

        foreach (var key in new[]
                 {
                     "ASPNETCORE_URLS",
                     "ASPNETCORE_HTTP_PORTS",
                     "ASPNETCORE_HTTPS_PORTS",
                     "DOTNET_URLS",
                     "DOTNET_HTTP_PORTS",
                     "DOTNET_HTTPS_PORTS",
                 })
        {
            start.Environment.Remove(key);
        }

        foreach (var (key, value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(key);
            }
            else
            {
                start.Environment[key] = value;
            }
        }

        return Process.Start(start) ?? throw new InvalidOperationException("MesIngest.Host did not start.");
    }

    private static async Task<HttpResponseMessage> WaitForResponseAsync(
        Process process,
        HttpClient client,
        string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var output = await ReadCompletedOutputAsync(process);
                throw new InvalidOperationException(
                    $"MesIngest.Host exited with {process.ExitCode} before listening. {output}");
            }

            try
            {
                return await client.GetAsync(path);
            }
            catch (HttpRequestException error)
            {
                lastError = error;
                await Task.Delay(50);
            }

            catch (TaskCanceledException error)
            {
                lastError = error;
            }
        }

        throw new TimeoutException("MesIngest.Host did not accept HTTP requests in time.", lastError);
    }

    private static async Task<ProcessResult> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return new ProcessResult(-1, await stdout + await stderr, TimedOut: true);
        }

        return new ProcessResult(process.ExitCode, await stdout + await stderr, TimedOut: false);
    }

    private static async Task<string> ReadCompletedOutputAsync(Process process)
    {
        await process.WaitForExitAsync();
        return await process.StandardOutput.ReadToEndAsync()
               + await process.StandardError.ReadToEndAsync();
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, bool TimedOut);
}
