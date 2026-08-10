using System.Diagnostics;
using System.Text.Json;

namespace MesIngest.Tests;

public sealed class ReleasePackageValidationTests
{
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
                     "service", "watch", "queries", "templates", "scripts", "validation", "openapi",
                 })
        {
            Directory.CreateDirectory(Path.Combine(root, directory));
        }

        File.WriteAllText(Path.Combine(root, "service", "MesIngest.Host.exe"), "host");
        File.WriteAllText(Path.Combine(root, "watch", "MesIngest.Watch.exe"), "watch");
        File.WriteAllText(Path.Combine(root, "queries", "query.sql"), "select 1");
        File.WriteAllText(Path.Combine(root, "scripts", "install-service.ps1"), "# install");
        File.WriteAllText(Path.Combine(root, "scripts", "uninstall-service.ps1"), "# uninstall");
        File.WriteAllText(Path.Combine(root, "scripts", "Test-ReleasePackage.ps1"), "# validate");
        File.WriteAllText(Path.Combine(root, "validation", "Invoke-FactoryValidation.ps1"), "# factory");
        File.WriteAllText(Path.Combine(root, "validation", "Invoke-ReleaseSmoke.ps1"), "# smoke");
        File.WriteAllText(Path.Combine(root, "validation", "Invoke-WatchAcceptance.ps1"), "# acceptance");
        File.WriteAllText(Path.Combine(root, "INSTALL.md"), "install");
        File.WriteAllText(Path.Combine(root, "UPGRADE.md"), "upgrade");
        File.WriteAllText(Path.Combine(root, "FACTORY-VALIDATION.md"), "validate");
        File.WriteAllText(Path.Combine(root, "VERSION.txt"), "version");
        File.WriteAllText(
            Path.Combine(root, "templates", "appsettings.Local.json.example"),
            """{"MesIngest":{"SharedSecret":"","SqlServerConnectionString":"<SQL>"}}""");
        File.WriteAllText(
            Path.Combine(root, "templates", "watch.appsettings.Local.json.example"),
            """{"Watch":{"BaseUrl":"http://127.0.0.1:5088","RenderingMode":"SoftwareOnly","RequestTimeoutSeconds":30,"ConnectionLogRetentionDays":30,"ConnectionLogMaxSizeMb":100,"SharedSecret":""},"_comments":{"SharedSecret":"MesIngestWatch__SharedSecret external-configuration","preferences":"%LocalAppData%"}}""");
        File.WriteAllText(
            Path.Combine(root, "openapi", "v1.json"),
            """{"openapi":"3.0.1","paths":{"/api/contract":{"get":{}},"/api/demands":{"get":{}},"/api/demands/{demandId}":{"get":{}},"/api/alerts":{"get":{}},"/api/poll-health":{"get":{}},"/api/demand-changes":{"get":{}}}}""");
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

    private sealed record ValidationResult(int ExitCode, string Output);
}
