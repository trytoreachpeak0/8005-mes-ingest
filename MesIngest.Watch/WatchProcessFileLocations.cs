using System.IO;

namespace MesIngest.Watch;

internal sealed record WatchProcessFileLocations(
    string? ConnectionPreferencesPath,
    string? WorkspacePreferencesPath,
    string? AreaFilterProfilesDirectoryPath)
{
    private const string UiTestModeVariable = "MESINGEST_WATCH_UI_TEST_MODE";
    private const string LocalAppDataVariable = "LOCALAPPDATA";

    internal static WatchProcessFileLocations Resolve() =>
        Resolve(Environment.GetEnvironmentVariable);

    internal static WatchProcessFileLocations Resolve(
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);
        if (!string.Equals(
                readEnvironmentVariable(UiTestModeVariable),
                "1",
                StringComparison.Ordinal))
        {
            return new(null, null, null);
        }

        var localAppData = readEnvironmentVariable(LocalAppDataVariable);
        if (string.IsNullOrWhiteSpace(localAppData)
            || !Path.IsPathFullyQualified(localAppData))
        {
            throw new InvalidOperationException(
                $"{UiTestModeVariable}=1 requires an absolute {LocalAppDataVariable} path.");
        }

        var watchDirectory = Path.Combine(
            Path.GetFullPath(localAppData),
            "MesIngest.Watch");
        return new(
            Path.Combine(watchDirectory, "connection-preferences.json"),
            Path.Combine(watchDirectory, "watch-v2-preferences.json"),
            Path.Combine(watchDirectory, "area-filters"));
    }
}
