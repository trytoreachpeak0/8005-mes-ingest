using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using MesIngest.Watch;

namespace MesIngest.Tests;

[Collection("WpfDesktop")]
public sealed class WatchWindowLayoutPersistenceTests
{
    [Fact]
    public void Settings_names_the_backward_compatible_choice_remember_window_layout() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("settings-name");
            try
            {
                using var window = CreateWorkspace(root, WatchV2Preferences.Default);
                var preference = Assert.IsAssignableFrom<UIElement>(
                    window.FindName("RememberWindowSizeCheckBox"));

                Assert.Equal("记住窗口布局", AutomationProperties.GetName(preference));

                window.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Remembered_layout_round_trips_main_and_inspector_normal_bounds_and_monitor_identity() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("normal-roundtrip");
            try
            {
                var expected = CaptureLayouts(root, maximizeInspector: false);
                var path = PreferencesPath(root);
                var inspectorDocument = FindLayoutObject(
                    JsonNode.Parse(File.ReadAllText(path))!,
                    expected.Inspector.Width,
                    expected.Inspector.Height);
                var monitor = inspectorDocument
                    .Single(property => property.Key.Contains("monitor", StringComparison.OrdinalIgnoreCase))
                    .Value?.GetValue<string>();
                Assert.False(string.IsNullOrWhiteSpace(monitor));

                var loaded = WatchV2PreferencesStore.Load(path);
                using var main = CreateWorkspace(root, loaded);
                main.Show();
                main.UpdateLayout();
                AssertBounds(expected.Main, NormalBounds(main));

                var inspector = OpenInspector(main, "series-b");
                Assert.Equal(WindowState.Normal, inspector.WindowState);
                AssertBounds(expected.Inspector, NormalBounds(inspector));

                main.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Maximized_inspector_restores_safe_normal_bounds_then_maximized_state() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("maximized-roundtrip");
            try
            {
                var expected = CaptureLayouts(root, maximizeInspector: true);
                var loaded = WatchV2PreferencesStore.Load(PreferencesPath(root));
                using var main = CreateWorkspace(root, loaded);
                main.Show();

                var inspector = OpenInspector(main, "series-b");

                Assert.Equal(WindowState.Maximized, inspector.WindowState);
                AssertBounds(expected.Inspector, inspector.RestoreBounds);
                AssertContained(inspector.RestoreBounds, SystemParameters.WorkArea);

                main.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Invalid_inspector_coordinates_restore_the_clamped_1200_by_800_default() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("invalid-coordinates");
            try
            {
                var expected = CaptureLayouts(root, maximizeInspector: false);
                var path = PreferencesPath(root);
                var rootNode = JsonNode.Parse(File.ReadAllText(path))!;
                var inspectorLayout = FindLayoutObject(
                    rootNode,
                    expected.Inspector.Width,
                    expected.Inspector.Height);
                SetNumber(inspectorLayout, "left", 1_000_000_000);
                SetNumber(inspectorLayout, "top", -1_000_000_000);
                File.WriteAllText(path, rootNode.ToJsonString(JsonOptions));

                using var main = CreateWorkspace(root, WatchV2PreferencesStore.Load(path));
                main.Show();
                var inspector = OpenInspector(main, "series-b");
                var workArea = SystemParameters.WorkArea;
                var restored = NormalBounds(inspector);

                AssertClose(Math.Min(1200, workArea.Width), restored.Width);
                AssertClose(Math.Min(800, workArea.Height), restored.Height);
                Assert.True(restored.Width >= 720);
                Assert.True(restored.Height >= 600);
                AssertContained(restored, workArea);

                main.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Missing_monitor_falls_back_to_a_visible_work_area() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("missing-monitor");
            try
            {
                var expected = CaptureLayouts(root, maximizeInspector: false);
                var path = PreferencesPath(root);
                var rootNode = JsonNode.Parse(File.ReadAllText(path))!;
                var inspectorLayout = FindLayoutObject(
                    rootNode,
                    expected.Inspector.Width,
                    expected.Inspector.Height);
                SetString(inspectorLayout, "monitor", @"\\.\DISPLAY-DOES-NOT-EXIST");
                File.WriteAllText(path, rootNode.ToJsonString(JsonOptions));

                using var main = CreateWorkspace(root, WatchV2PreferencesStore.Load(path));
                main.Show();
                var inspector = OpenInspector(main, "series-b");
                var restored = NormalBounds(inspector);

                AssertClose(expected.Inspector.Width, restored.Width);
                AssertClose(expected.Inspector.Height, restored.Height);
                AssertContained(restored, SystemParameters.WorkArea);

                main.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Changed_work_area_clamps_the_complete_inspector_normal_bounds() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("work-area-clamp");
            try
            {
                var expected = CaptureLayouts(root, maximizeInspector: false);
                var path = PreferencesPath(root);
                var rootNode = JsonNode.Parse(File.ReadAllText(path))!;
                var inspectorLayout = FindLayoutObject(
                    rootNode,
                    expected.Inspector.Width,
                    expected.Inspector.Height);
                var workArea = SystemParameters.WorkArea;
                SetNumber(inspectorLayout, "left", workArea.Right - 40);
                SetNumber(inspectorLayout, "top", workArea.Bottom - 40);
                File.WriteAllText(path, rootNode.ToJsonString(JsonOptions));

                using var main = CreateWorkspace(root, WatchV2PreferencesStore.Load(path));
                main.Show();
                var inspector = OpenInspector(main, "series-b");
                var restored = NormalBounds(inspector);

                AssertClose(expected.Inspector.Width, restored.Width);
                AssertClose(expected.Inspector.Height, restored.Height);
                AssertContained(restored, workArea);

                main.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static (Rect Main, Rect Inspector) CaptureLayouts(
        string root,
        bool maximizeInspector)
    {
        var workArea = SystemParameters.WorkArea;
        Assert.True(workArea.Width >= 1280 && workArea.Height >= 760,
            "Ticket 06 geometry tests require a desktop work area of at least 1280x760 epx.");
        var mainBounds = new Rect(workArea.Left + 24, workArea.Top + 20, 1040, 700);
        var inspectorBounds = new Rect(workArea.Left + 128, workArea.Top + 48, 920, 680);

        using var main = CreateWorkspace(root, WatchV2Preferences.Default);
        ApplyNormalBounds(main, mainBounds);
        main.Show();
        main.UpdateLayout();
        var inspector = OpenInspector(main, "series-a");
        ApplyNormalBounds(inspector, inspectorBounds);
        inspector.UpdateLayout();
        if (maximizeInspector)
        {
            inspector.WindowState = WindowState.Maximized;
        }

        inspector.Close();
        main.Close();

        Assert.True(File.Exists(PreferencesPath(root)));
        return (mainBounds, inspectorBounds);
    }

    private static WatchWorkspaceWindow CreateWorkspace(
        string root,
        WatchV2Preferences preferences) => new(
        new WatchHostSettings("http://host-a", "secret", 30),
        preferences,
        Path.Combine(root, "connection.json"),
        PreferencesPath(root),
        initializeOnLoaded: false);

    private static WatchDemandSeriesInspectorWindow OpenInspector(
        WatchWorkspaceWindow main,
        string seriesId)
    {
        main.DemandSeriesInspectorCoordinator.OpenOrShow(LoadingState(seriesId));
        var inspector = Assert.IsType<WatchDemandSeriesInspectorWindow>(
            main.DemandSeriesInspectorCoordinator.CurrentWindow);
        inspector.UpdateLayout();
        return inspector;
    }

    private static WatchDemandSeriesInspectorStatePresentation LoadingState(string seriesId) => new(
        seriesId,
        "WIRE_TO_GATE",
        $"SUB-{seriesId[^1]}",
        "TRACKING",
        "VISIBLE",
        new WatchDemandSeriesFrozenSnapshotPresentation(
            "snapshot-layout",
            "commit-layout",
            1,
            DateTimeOffset.Parse("2026-08-21T01:00:00+08:00"),
            "poll-layout"),
        Detail: null,
        IsLoading: true,
        IsStale: false,
        IsPaused: false,
        WatchPresentationSeverity.Informational,
        "正在读取详情",
        "正在读取当前列表选择。");

    private static void ApplyNormalBounds(Window window, Rect bounds)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.WindowState = WindowState.Normal;
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
    }

    private static Rect NormalBounds(Window window) =>
        window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

    private static JsonObject FindLayoutObject(JsonNode root, double width, double height)
    {
        var matches = Descendants(root)
            .OfType<JsonObject>()
            .Where(candidate => TryGetNumber(candidate, "width", out var candidateWidth)
                && TryGetNumber(candidate, "height", out var candidateHeight)
                && Math.Abs(candidateWidth - width) <= 1
                && Math.Abs(candidateHeight - height) <= 1)
            .ToArray();
        return Assert.Single(matches);
    }

    private static IEnumerable<JsonNode> Descendants(JsonNode node)
    {
        yield return node;
        if (node is JsonObject jsonObject)
        {
            foreach (var child in jsonObject.Select(property => property.Value).OfType<JsonNode>())
            {
                foreach (var descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var child in jsonArray.OfType<JsonNode>())
            {
                foreach (var descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static bool TryGetNumber(JsonObject value, string namePart, out double number)
    {
        var property = value.FirstOrDefault(candidate =>
            candidate.Key.Contains(namePart, StringComparison.OrdinalIgnoreCase));
        if (property.Value is JsonValue jsonValue
            && jsonValue.TryGetValue<double>(out number))
        {
            return true;
        }

        number = 0;
        return false;
    }

    private static void SetNumber(JsonObject value, string namePart, double number)
    {
        var property = Assert.Single(value.Where(candidate =>
            candidate.Key.Contains(namePart, StringComparison.OrdinalIgnoreCase)));
        value[property.Key] = number;
    }

    private static void SetString(JsonObject value, string namePart, string text)
    {
        var property = Assert.Single(value.Where(candidate =>
            candidate.Key.Contains(namePart, StringComparison.OrdinalIgnoreCase)));
        value[property.Key] = text;
    }

    private static void AssertBounds(Rect expected, Rect actual)
    {
        AssertClose(expected.Left, actual.Left);
        AssertClose(expected.Top, actual.Top);
        AssertClose(expected.Width, actual.Width);
        AssertClose(expected.Height, actual.Height);
    }

    private static void AssertContained(Rect bounds, Rect workArea)
    {
        Assert.InRange(bounds.Left, workArea.Left - 1, workArea.Right - bounds.Width + 1);
        Assert.InRange(bounds.Top, workArea.Top - 1, workArea.Bottom - bounds.Height + 1);
        Assert.True(bounds.Right <= workArea.Right + 1);
        Assert.True(bounds.Bottom <= workArea.Bottom + 1);
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - 1, expected + 1);

    private static string PreferencesPath(string root) =>
        Path.Combine(root, "watch-v2-preferences.json");

    private static string NewTempDirectory(string scenario) =>
        Path.Combine(Path.GetTempPath(), $"watch-layout-{scenario}-{Guid.NewGuid():N}");

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
