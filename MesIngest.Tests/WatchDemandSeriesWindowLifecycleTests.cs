using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MesIngest.Watch;

namespace MesIngest.Tests;

[Collection("WpfDesktop")]
public sealed class WatchDemandSeriesWindowLifecycleTests
{
    [Fact]
    public void Background_update_keeps_a_minimized_inspector_minimized_until_explicit_show_restores_it() =>
        StaTestRunner.Run(() =>
        {
            using var coordinator = new WatchDemandSeriesInspectorCoordinator();
            var first = LoadingState("series-a");
            var refreshed = LoadingState("series-b");

            coordinator.OpenOrShow(first);
            var inspector = Assert.IsType<WatchDemandSeriesInspectorWindow>(
                coordinator.CurrentWindow);
            inspector.WindowState = WindowState.Minimized;
            DrainDispatcher();

            coordinator.Update(refreshed);

            Assert.Equal(WindowState.Minimized, inspector.WindowState);
            Assert.Equal("series-b", inspector.Title.Split('·').Last().Trim());

            coordinator.OpenOrShow(refreshed);
            DrainDispatcher();

            Assert.NotEqual(WindowState.Minimized, inspector.WindowState);
            Assert.Same(inspector, coordinator.CurrentWindow);
        });

    [Fact]
    public void Main_and_inspector_minimize_independently_and_escape_does_not_close_inspector() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("independent-minimize");
            try
            {
                using var main = CreateWorkspace(root, WatchV2Preferences.Default);
                main.Show();
                main.DemandSeriesInspectorCoordinator.OpenOrShow(LoadingState("series-a"));
                var inspector = Assert.IsType<WatchDemandSeriesInspectorWindow>(
                    main.DemandSeriesInspectorCoordinator.CurrentWindow);

                main.WindowState = WindowState.Minimized;
                DrainDispatcher();
                Assert.NotEqual(WindowState.Minimized, inspector.WindowState);

                main.WindowState = WindowState.Normal;
                inspector.WindowState = WindowState.Minimized;
                DrainDispatcher();
                Assert.NotEqual(WindowState.Minimized, main.WindowState);

                inspector.WindowState = WindowState.Normal;
                DrainDispatcher();
                RaiseKey(inspector, Key.Escape);
                DrainDispatcher();

                Assert.True(main.DemandSeriesInspectorCoordinator.IsOpen);
                Assert.True(inspector.IsVisible);

                main.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void System_close_command_closes_the_inspector_and_reopen_creates_a_new_window() =>
        StaTestRunner.Run(() =>
        {
            using var coordinator = new WatchDemandSeriesInspectorCoordinator();
            var state = LoadingState("series-a");
            coordinator.OpenOrShow(state);
            var first = Assert.IsType<WatchDemandSeriesInspectorWindow>(coordinator.CurrentWindow);

            SystemCommands.CloseWindow(first);
            DrainDispatcher();

            Assert.False(coordinator.IsOpen);
            Assert.False(first.IsVisible);

            coordinator.OpenOrShow(state);
            var reopened = Assert.IsType<WatchDemandSeriesInspectorWindow>(coordinator.CurrentWindow);

            Assert.NotSame(first, reopened);
            Assert.True(reopened.IsVisible);
        });

    private static WatchWorkspaceWindow CreateWorkspace(
        string root,
        WatchV2Preferences preferences) => new(
        new WatchHostSettings("http://host-a", "secret", 30),
        preferences,
        Path.Combine(root, "connection.json"),
        Path.Combine(root, "watch-v2-preferences.json"),
        initializeOnLoaded: false);

    private static WatchDemandSeriesInspectorStatePresentation LoadingState(string seriesId) => new(
        seriesId,
        "WIRE_TO_GATE",
        $"SUB-{seriesId[^1]}",
        "TRACKING",
        "VISIBLE",
        new WatchDemandSeriesFrozenSnapshotPresentation(
            "snapshot-1",
            "commit-1",
            1,
            DateTimeOffset.Parse("2026-08-21T01:00:00+08:00"),
            "poll-1"),
        Detail: null,
        IsLoading: true,
        IsStale: false,
        IsPaused: false,
        WatchPresentationSeverity.Informational,
        "正在读取详情",
        "正在读取当前列表选择。");

    private static void RaiseKey(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("The window must be connected to a presentation source.");
        window.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        });
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static string NewTempDirectory(string scenario) =>
        Path.Combine(Path.GetTempPath(), $"watch-inspector-{scenario}-{Guid.NewGuid():N}");

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
