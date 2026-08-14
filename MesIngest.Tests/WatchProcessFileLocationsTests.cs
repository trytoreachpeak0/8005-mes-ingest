using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchProcessFileLocationsTests
{
    [Fact]
    public void Production_process_keeps_the_existing_default_file_locations()
    {
        var locations = WatchProcessFileLocations.Resolve(_ => null);

        Assert.Null(locations.ConnectionPreferencesPath);
        Assert.Null(locations.WorkspacePreferencesPath);
        Assert.Null(locations.AreaFilterProfilesDirectoryPath);
    }

    [Fact]
    public void Ui_test_process_maps_all_mutable_files_below_the_explicit_isolated_root()
    {
        var root = Path.GetFullPath(Path.Combine("fixtures", "watch-local-app-data"));
        var locations = WatchProcessFileLocations.Resolve(name => name switch
        {
            "MESINGEST_WATCH_UI_TEST_MODE" => "1",
            "LOCALAPPDATA" => root,
            _ => null,
        });
        var watchRoot = Path.Combine(root, "MesIngest.Watch");

        Assert.Equal(
            Path.Combine(watchRoot, "connection-preferences.json"),
            locations.ConnectionPreferencesPath);
        Assert.Equal(
            Path.Combine(watchRoot, "watch-v2-preferences.json"),
            locations.WorkspacePreferencesPath);
        Assert.Equal(
            Path.Combine(watchRoot, "area-filters"),
            locations.AreaFilterProfilesDirectoryPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative-path")]
    public void Ui_test_process_rejects_a_missing_or_relative_isolation_root(string? root)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WatchProcessFileLocations.Resolve(name => name switch
            {
                "MESINGEST_WATCH_UI_TEST_MODE" => "1",
                "LOCALAPPDATA" => root,
                _ => null,
            }));

        Assert.Contains("LOCALAPPDATA", exception.Message, StringComparison.Ordinal);
    }
}
