using System.Text.Json;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchV2PreferencesTests
{
    [Fact]
    public void Version_2_remember_window_size_document_loads_as_the_shared_layout_preference()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"watch-v2-legacy-layout-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """
            {
              "version": 2,
              "intervalSeconds": {
                "overview": 10,
                "demandSeries": 30,
                "readabilityAudit": 60,
                "errorSearch": 300,
                "currentIngestAttention": 10
              },
              "display": {
                "rememberWindowSize": true,
                "windowWidth": 1680,
                "windowHeight": 1000,
                "isNavigationPaneOpen": true
              }
            }
            """);

        try
        {
            var preferences = WatchV2PreferencesStore.Load(path);

            Assert.True(preferences.Display.RememberWindowLayout);
            Assert.Equal(1680, preferences.Display.WindowWidth);
            Assert.Equal(1000, preferences.Display.WindowHeight);
            Assert.True(preferences.Display.IsNavigationPaneOpen);
            Assert.Equal(30, preferences.RefreshIntervals.DemandSeries.IntervalSeconds);

            WatchV2PreferencesStore.Save(path, preferences);
            Assert.Equal(preferences, WatchV2PreferencesStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Layout_opt_out_does_not_persist_main_or_inspector_geometry()
    {
        var directory = NewTempDirectory();
        var path = Path.Combine(directory, "watch-v2-preferences.json");
        var preferences = WatchV2Preferences.Default with
        {
            Display = new WatchV2DisplayPreferences(
                rememberWindowLayout: false,
                windowWidth: 1777,
                windowHeight: 1111,
                isNavigationPaneOpen: true),
        };

        try
        {
            WatchV2PreferencesStore.Save(path, preferences);

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("1777", json, StringComparison.Ordinal);
            Assert.DoesNotContain("1111", json, StringComparison.Ordinal);
            Assert.DoesNotContain("monitor", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("maximized", json, StringComparison.OrdinalIgnoreCase);

            var loaded = WatchV2PreferencesStore.Load(path);
            Assert.False(loaded.Display.RememberWindowLayout);
            Assert.Equal(
                WatchV2DisplayPreferences.Default.WindowWidth,
                loaded.Display.WindowWidth);
            Assert.Equal(
                WatchV2DisplayPreferences.Default.WindowHeight,
                loaded.Display.WindowHeight);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Defaults_cover_five_always_on_views_and_the_1440_by_900_shell_baseline()
    {
        var preferences = WatchV2Preferences.Default;

        Assert.Equal(30, preferences.RefreshIntervals.Overview.IntervalSeconds);
        Assert.Equal(60, preferences.RefreshIntervals.DemandSeries.IntervalSeconds);
        Assert.Equal(60, preferences.RefreshIntervals.ReadabilityAudit.IntervalSeconds);
        Assert.Equal(60, preferences.RefreshIntervals.ErrorSearch.IntervalSeconds);
        Assert.Equal(30, preferences.RefreshIntervals.CurrentIngestAttention.IntervalSeconds);

        Assert.Equal(5, Enum.GetValues<WatchV2DataView>().Length);
        Assert.Null(typeof(WatchV2AutoRefreshSetting).GetProperty("Enabled"));
        Assert.True(preferences.Display.RememberWindowLayout);
        Assert.Equal(1440, preferences.Display.WindowWidth);
        Assert.Equal(900, preferences.Display.WindowHeight);
        Assert.False(preferences.Display.IsNavigationPaneOpen);
        Assert.Equal(720, WatchV2DisplayPreferences.MinimumWindowWidth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_path_round_trips_each_interval_and_allowed_local_display_preference(
        bool isNavigationPaneOpen)
    {
        var directory = NewTempDirectory();
        var path = Path.Combine(directory, "nested", "watch-v2-preferences.json");
        var expected = new WatchV2Preferences(
            WatchV2AutoRefreshSettings.Default
                .With(WatchV2DataView.Overview, new WatchV2AutoRefreshSetting(10))
                .With(WatchV2DataView.DemandSeries, new WatchV2AutoRefreshSetting(30))
                .With(WatchV2DataView.ReadabilityAudit, new WatchV2AutoRefreshSetting(60))
                .With(WatchV2DataView.ErrorSearch, new WatchV2AutoRefreshSetting(300))
                .With(WatchV2DataView.CurrentIngestAttention, new WatchV2AutoRefreshSetting(30)),
            new WatchV2DisplayPreferences(
                rememberWindowLayout: true,
                windowWidth: 1680,
                windowHeight: 1050,
                isNavigationPaneOpen));

        try
        {
            WatchV2PreferencesStore.Save(path, expected);

            Assert.True(File.Exists(path));
            Assert.Equal(expected, WatchV2PreferencesStore.Load(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Saved_document_contains_only_interval_seconds_and_local_display_values()
    {
        var directory = NewTempDirectory();
        var path = Path.Combine(directory, "watch-v2-preferences.json");
        var preferences = WatchV2Preferences.Default with
        {
            RefreshIntervals = WatchV2AutoRefreshSettings.Default
                .With(WatchV2DataView.DemandSeries, new WatchV2AutoRefreshSetting(30))
                .With(WatchV2DataView.CurrentIngestAttention, new WatchV2AutoRefreshSetting(300)),
        };

        try
        {
            WatchV2PreferencesStore.Save(path, preferences);

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("enabled", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("baseUrl", json, StringComparison.OrdinalIgnoreCase);

            using var document = JsonDocument.Parse(json);
            Assert.Equal(
                ["display", "intervalSeconds", "version"],
                PropertyNames(document.RootElement));

            var intervals = document.RootElement.GetProperty("intervalSeconds");
            Assert.Equal(
                ["currentIngestAttention", "demandSeries", "errorSearch", "overview", "readabilityAudit"],
                PropertyNames(intervals));
            Assert.All(
                intervals.EnumerateObject(),
                property => Assert.Equal(JsonValueKind.Number, property.Value.ValueKind));

            var display = document.RootElement.GetProperty("display");
            Assert.Equal(
                ["isNavigationPaneOpen", "rememberWindowSize", "windowHeight", "windowWidth"],
                PropertyNames(display));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"version\":1,\"overview\":{\"enabled\":true,\"intervalSeconds\":10}}")]
    [InlineData("{\"version\":99,\"intervalSeconds\":{\"overview\":10,\"demandSeries\":10,\"readabilityAudit\":10,\"errorSearch\":10,\"currentIngestAttention\":10},\"display\":{\"rememberWindowSize\":true,\"windowWidth\":1440,\"windowHeight\":900,\"isNavigationPaneOpen\":true}}")]
    [InlineData("{\"version\":2,\"intervalSeconds\":{\"overview\":11,\"demandSeries\":10,\"readabilityAudit\":10,\"errorSearch\":10,\"currentIngestAttention\":10},\"display\":{\"rememberWindowSize\":true,\"windowWidth\":1440,\"windowHeight\":900,\"isNavigationPaneOpen\":true}}")]
    [InlineData("{\"version\":2,\"intervalSeconds\":{\"overview\":10,\"demandSeries\":10,\"readabilityAudit\":10,\"errorSearch\":10,\"currentIngestAttention\":10},\"display\":{\"rememberWindowSize\":true,\"windowWidth\":719,\"windowHeight\":900,\"isNavigationPaneOpen\":true}}")]
    [InlineData("{\"version\":2,\"intervalSeconds\":{\"overview\":10,\"demandSeries\":10,\"readabilityAudit\":10,\"errorSearch\":10},\"display\":{\"rememberWindowSize\":true,\"windowWidth\":1440,\"windowHeight\":900,\"isNavigationPaneOpen\":true}}")]
    public void Corrupt_legacy_incompatible_or_partial_documents_fall_back_as_one_unit(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-v2-preferences-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        try
        {
            Assert.Equal(WatchV2Preferences.Default, WatchV2PreferencesStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Replacing_existing_preferences_leaves_one_complete_document_and_no_temporary_file()
    {
        var directory = NewTempDirectory();
        var path = Path.Combine(directory, "watch-v2-preferences.json");
        var expected = WatchV2Preferences.Default with
        {
            RefreshIntervals = WatchV2AutoRefreshSettings.Default.With(
                WatchV2DataView.ErrorSearch,
                new WatchV2AutoRefreshSetting(60)),
            Display = new WatchV2DisplayPreferences(
                rememberWindowLayout: true,
                windowWidth: 1920,
                windowHeight: 1080,
                isNavigationPaneOpen: false),
        };

        try
        {
            WatchV2PreferencesStore.Save(path, WatchV2Preferences.Default);
            WatchV2PreferencesStore.Save(path, expected);

            Assert.Equal(expected, WatchV2PreferencesStore.Load(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(WatchV2PreferencesStore.CurrentVersion, document.RootElement.GetProperty("version").GetInt32());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Default_path_is_local_application_data_and_separate_from_host_configuration()
    {
        var path = WatchV2PreferencesStore.DefaultFilePath;

        Assert.Contains("MesIngest.Watch", path, StringComparison.Ordinal);
        Assert.EndsWith("watch-v2-preferences.json", path, StringComparison.Ordinal);
        Assert.DoesNotContain("appsettings", path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection", path, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(719, 900)]
    [InlineData(1440, 599)]
    [InlineData(double.NaN, 900)]
    [InlineData(1440, double.PositiveInfinity)]
    public void Invalid_window_geometry_is_rejected_before_it_can_be_persisted(double width, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WatchV2DisplayPreferences(
                rememberWindowLayout: true,
                windowWidth: width,
                windowHeight: height,
                isNavigationPaneOpen: true));
    }

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string NewTempDirectory() =>
        Path.Combine(Path.GetTempPath(), $"watch-v2-preferences-{Guid.NewGuid():N}");

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
