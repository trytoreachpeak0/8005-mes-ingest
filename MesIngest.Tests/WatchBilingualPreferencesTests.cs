using System.Globalization;
using System.Text.Json;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchBilingualPreferencesTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void Missing_preferences_default_to_simplified_chinese_without_consulting_process_culture(
        string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var path = Path.Combine(
            Path.GetTempPath(),
            $"watch-bilingual-missing-{Guid.NewGuid():N}.json");

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);

            var loaded = WatchV2PreferencesStore.Load(path);

            Assert.Equal(WatchDisplayLanguage.SimplifiedChinese, loaded.DisplayLanguage);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            File.Delete(path);
        }
    }

    [Fact]
    public void English_round_trips_with_refresh_main_and_inspector_layout_preferences()
    {
        var directory = NewTempDirectory();
        var path = Path.Combine(directory, "watch-v2-preferences.json");
        var mainLayout = new WatchWindowLayout(20, 30, 1680, 1000, "DISPLAY-A", Maximized: true);
        var inspectorLayout = new WatchWindowLayout(60, 70, 1180, 760, "DISPLAY-B", Maximized: false);
        var expected = WatchV2Preferences.Default with
        {
            DisplayLanguage = WatchDisplayLanguage.English,
            RefreshIntervals = WatchV2AutoRefreshSettings.Default
                .With(WatchV2DataView.Overview, new WatchV2AutoRefreshSetting(10))
                .With(WatchV2DataView.DemandSeries, new WatchV2AutoRefreshSetting(30))
                .With(WatchV2DataView.ReadabilityAudit, new WatchV2AutoRefreshSetting(60))
                .With(WatchV2DataView.ErrorSearch, new WatchV2AutoRefreshSetting(300))
                .With(WatchV2DataView.CurrentIngestAttention, new WatchV2AutoRefreshSetting(10)),
            Display = new WatchV2DisplayPreferences(
                rememberWindowLayout: true,
                windowWidth: 1680,
                windowHeight: 1000,
                isNavigationPaneOpen: true,
                mainWindowLayout: mainLayout,
                inspectorWindowLayout: inspectorLayout),
        };

        try
        {
            WatchV2PreferencesStore.Save(path, expected);

            Assert.Equal(expected, WatchV2PreferencesStore.Load(path));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var display = document.RootElement.GetProperty("display");
            Assert.Equal("en-US", display.GetProperty("language").GetString());
            Assert.False(document.RootElement.TryGetProperty("host", out _));
            Assert.False(document.RootElement.TryGetProperty("credential", out _));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Version_2_without_language_migrates_to_chinese_and_preserves_refresh_main_and_inspector_layout()
    {
        var directory = NewTempDirectory();
        var path = Path.Combine(directory, "watch-v2-preferences.json");
        Directory.CreateDirectory(directory);
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
                "isNavigationPaneOpen": true,
                "mainWindowLayout": {
                  "left": 20,
                  "top": 30,
                  "width": 1680,
                  "height": 1000,
                  "monitorDeviceName": "DISPLAY-A",
                  "maximized": true
                },
                "inspectorWindowLayout": {
                  "left": 60,
                  "top": 70,
                  "width": 1180,
                  "height": 760,
                  "monitorDeviceName": "DISPLAY-B",
                  "maximized": false
                }
              }
            }
            """);

        try
        {
            var loaded = WatchV2PreferencesStore.Load(path);

            Assert.Equal(WatchDisplayLanguage.SimplifiedChinese, loaded.DisplayLanguage);
            Assert.Equal(10, loaded.RefreshIntervals.Overview.IntervalSeconds);
            Assert.Equal(30, loaded.RefreshIntervals.DemandSeries.IntervalSeconds);
            Assert.Equal(60, loaded.RefreshIntervals.ReadabilityAudit.IntervalSeconds);
            Assert.Equal(300, loaded.RefreshIntervals.ErrorSearch.IntervalSeconds);
            Assert.Equal(10, loaded.RefreshIntervals.CurrentIngestAttention.IntervalSeconds);
            Assert.True(loaded.Display.RememberWindowLayout);
            Assert.True(loaded.Display.IsNavigationPaneOpen);
            Assert.Equal(new WatchWindowLayout(20, 30, 1680, 1000, "DISPLAY-A", true), loaded.Display.MainWindowLayout);
            Assert.Equal(new WatchWindowLayout(60, 70, 1180, 760, "DISPLAY-B", false), loaded.Display.InspectorWindowLayout);

            WatchV2PreferencesStore.Save(path, loaded);
            using var migratedDocument = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(
                WatchV2PreferencesStore.CurrentVersion,
                migratedDocument.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(
                "zh-CN",
                migratedDocument.RootElement.GetProperty("display").GetProperty("language").GetString());
            Assert.Equal(loaded, WatchV2PreferencesStore.Load(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("\"future-language\"")]
    [InlineData("42")]
    [InlineData("{}")]
    public void Unknown_or_malformed_language_falls_back_to_chinese_without_discarding_other_valid_preferences(
        string languageJson)
    {
        var directory = NewTempDirectory();
        var path = Path.Combine(directory, "watch-v2-preferences.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            path,
            $$"""
            {
              "version": 3,
              "intervalSeconds": {
                "overview": 10,
                "demandSeries": 30,
                "readabilityAudit": 60,
                "errorSearch": 300,
                "currentIngestAttention": 10
              },
              "display": {
                "language": {{languageJson}},
                "rememberWindowSize": true,
                "windowWidth": 1680,
                "windowHeight": 1000,
                "isNavigationPaneOpen": true,
                "mainWindowLayout": {
                  "left": 20,
                  "top": 30,
                  "width": 1680,
                  "height": 1000,
                  "monitorDeviceName": "DISPLAY-A",
                  "maximized": false
                },
                "inspectorWindowLayout": {
                  "left": 60,
                  "top": 70,
                  "width": 1180,
                  "height": 760,
                  "monitorDeviceName": "DISPLAY-B",
                  "maximized": true
                }
              }
            }
            """);

        try
        {
            var loaded = WatchV2PreferencesStore.Load(path);

            Assert.Equal(WatchDisplayLanguage.SimplifiedChinese, loaded.DisplayLanguage);
            Assert.Equal(10, loaded.RefreshIntervals.Overview.IntervalSeconds);
            Assert.Equal(30, loaded.RefreshIntervals.DemandSeries.IntervalSeconds);
            Assert.Equal(60, loaded.RefreshIntervals.ReadabilityAudit.IntervalSeconds);
            Assert.Equal(300, loaded.RefreshIntervals.ErrorSearch.IntervalSeconds);
            Assert.Equal(10, loaded.RefreshIntervals.CurrentIngestAttention.IntervalSeconds);
            Assert.True(loaded.Display.RememberWindowLayout);
            Assert.True(loaded.Display.IsNavigationPaneOpen);
            Assert.Equal(new WatchWindowLayout(20, 30, 1680, 1000, "DISPLAY-A", false), loaded.Display.MainWindowLayout);
            Assert.Equal(new WatchWindowLayout(60, 70, 1180, 760, "DISPLAY-B", true), loaded.Display.InspectorWindowLayout);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string NewTempDirectory() =>
        Path.Combine(Path.GetTempPath(), $"watch-bilingual-preferences-{Guid.NewGuid():N}");

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
