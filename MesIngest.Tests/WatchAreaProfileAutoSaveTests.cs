using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// UI composition seam for editor auto-save: the real production window, a
/// manually driven directory event source, and an injected clock. Every delay
/// is advanced by the test, so no case waits on real time.
/// </summary>
[Collection("WpfDesktop")]
public sealed class WatchAreaProfileAutoSaveTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_editor_no_longer_offers_a_save_or_a_discard_command() =>
        RunWithAreaProfileWindow((window, _, _, _) =>
        {
            Assert.Null(window.FindName("AreaProfileSaveButton"));
            Assert.Null(window.FindName("AreaProfileDiscardButton"));
        });

    [Fact]
    public void Typing_reaches_the_disk_once_the_editor_has_been_idle_for_the_auto_save_delay() =>
        RunWithAreaProfileWindow((window, directoryPath, _, clock) =>
        {
            Editor(window).Text = "B2-2\nB2-3";
            Assert.Equal("A1-1\nA1-2", ReadProfile(directoryPath, "西区"));

            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("B2-2\nB2-3", ReadProfile(directoryPath, "西区"));
        });

    [Fact]
    public void Continued_typing_restarts_the_delay_so_only_the_last_buffer_is_written() =>
        RunWithAreaProfileWindow((window, directoryPath, _, clock) =>
        {
            var editor = Editor(window);
            var half = WatchAreaFilterProfileStore.EditorAutoSaveDelay / 2;
            editor.Text = "B2-2";
            clock.Advance(half);
            editor.Text = "B2-2\nB2-3";
            clock.Advance(half);
            Assert.Equal("A1-1\nA1-2", ReadProfile(directoryPath, "西区"));

            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("B2-2\nB2-3", ReadProfile(directoryPath, "西区"));
        });

    [Fact]
    public void The_dirty_state_reports_the_unwritten_buffer_and_clears_once_it_reaches_the_disk() =>
        RunWithAreaProfileWindow((window, _, _, clock) =>
        {
            var diskState = Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                window.FindName("AreaProfileDiskStateText"));
            Assert.Equal("已自动保存", diskState.Text);

            Editor(window).Text = "B2-2";
            Assert.Equal("未落盘 · 即将自动保存", diskState.Text);

            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("已自动保存", diskState.Text);
        });

    [Fact]
    public void Ctrl_S_writes_the_buffer_without_waiting_for_the_delay() =>
        RunWithAreaProfileWindow((window, directoryPath, _, _) =>
        {
            Assert.Contains(
                ApplicationCommands.Save.InputGestures.Cast<InputGesture>(),
                gesture => gesture is KeyGesture
                {
                    Key: Key.S,
                    Modifiers: ModifierKeys.Control,
                });

            Editor(window).Text = "B2-2";
            ApplicationCommands.Save.Execute(null, AreaFilterPage(window));

            Assert.Equal("B2-2", ReadProfile(directoryPath, "西区"));
        });

    [Fact]
    public void Selecting_another_profile_writes_the_outgoing_buffer_first() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            WriteProfile(directoryPath, "东区", "C3-3");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            var list = ProfileList(window);
            Editor(window).Text = "B2-2";

            SelectProfile(list, "东区");

            Assert.Equal("B2-2", ReadProfile(directoryPath, "西区"));
            Assert.Equal("C3-3", Editor(window).Text);
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);
            Assert.Equal("C3-3", ReadProfile(directoryPath, "东区"));
        });

    [Fact]
    public void Leaving_the_area_page_writes_the_pending_buffer() =>
        RunWithAreaProfileWindow((window, directoryPath, _, _) =>
        {
            Editor(window).Text = "B2-2";

            NavigateTo(window, "OverviewNavigationItem");

            Assert.Equal(WatchWorkspacePage.Overview, window.ActivePage);
            Assert.Equal("B2-2", ReadProfile(directoryPath, "西区"));
        });

    [Fact]
    public void An_unfinished_buffer_still_reaches_the_disk_so_leaving_it_never_drops_what_was_typed() =>
        RunWithAreaProfileWindow((window, directoryPath, _, clock) =>
        {
            Editor(window).Text = "B2-2\n还没打完";

            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("B2-2\n还没打完", ReadProfile(directoryPath, "西区"));
            Assert.Contains(
                "非法内容不可应用",
                Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                    window.FindName("AreaProfileValidationSummaryText")).Text,
                StringComparison.Ordinal);
        });

    [Fact]
    public void An_auto_save_leaves_the_caret_and_the_selection_where_the_user_left_them() =>
        RunWithAreaProfileWindow((window, _, _, clock) =>
        {
            var editor = Editor(window);
            editor.Text = "B2-2\nB2-3";
            editor.Select(2, 3);

            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("B2-2\nB2-3", editor.Text);
            Assert.Equal(2, editor.SelectionStart);
            Assert.Equal(3, editor.SelectionLength);
        });

    [Fact]
    public void An_externally_deleted_profile_is_not_recreated_by_the_pending_auto_save() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var editor = Editor(window);
            editor.Text = "B2-2";
            File.Delete(Path.Combine(directoryPath, "西区.txt"));
            events.RaiseDeleted("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);

            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.False(File.Exists(Path.Combine(directoryPath, "西区.txt")));
            Assert.Equal("B2-2", editor.Text);
            Assert.Equal(
                "文件已删除 · 未命名草稿",
                Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                    window.FindName("AreaProfileDiskStateText")).Text);
        });

    [Fact]
    public void Closing_the_window_writes_the_pending_buffer() =>
        StaTestRunner.Run(() =>
        {
            var root = NewRoot();
            var areaProfilesPath = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaProfilesPath);
            WriteProfile(areaProfilesPath, "西区", "A1-1");
            try
            {
                using var composition = CreateComposition(
                    root,
                    areaProfilesPath,
                    new ManualTimerTimeProvider(StartedAt),
                    new ManualAreaProfileDirectoryEventSource());
                var window = composition.CreateMainWindow(initializeOnLoaded: false);
                window.Show();
                DrainDispatcher(window.Dispatcher);
                NavigateTo(window, "AreaFilterNavigationItem");
                SelectProfile(ProfileList(window), "西区");
                Editor(window).Text = "B2-2";

                window.Close();

                Assert.Equal("B2-2", ReadProfile(areaProfilesPath, "西区"));
            }
            finally
            {
                DeleteRoot(root);
            }
        });

    private static TextBox Editor(WatchWorkspaceWindow window) =>
        Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));

    private static ListBox ProfileList(WatchWorkspaceWindow window) =>
        Assert.IsType<ListBox>(window.FindName("AreaProfileList"));

    private static ScrollViewer AreaFilterPage(WatchWorkspaceWindow window) =>
        Assert.IsType<ScrollViewer>(window.FindName("AreaFilterPage"));

    private static void NavigateTo(WatchWorkspaceWindow window, string navigationItemName) =>
        Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(window.FindName(navigationItemName))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void SelectProfile(ListBox list, string profileName) =>
        list.SelectedItem = Assert.Single(
            list.Items.Cast<WatchAreaFilterProfilePresentationRow>(),
            row => row.ProfileName == profileName);

    private static void WriteProfile(string directoryPath, string profileName, string content) =>
        File.WriteAllText(
            Path.Combine(directoryPath, $"{profileName}.txt"),
            content,
            new UTF8Encoding(false));

    private static string ReadProfile(string directoryPath, string profileName) =>
        File.ReadAllText(
            Path.Combine(directoryPath, $"{profileName}.txt"),
            new UTF8Encoding(false));

    private static string NewRoot() => Path.Combine(
        Path.GetTempPath(),
        $"watch-area-autosave-{Guid.NewGuid():N}");

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static WatchV2ApplicationComposition CreateComposition(
        string root,
        string areaProfilesPath,
        TimeProvider clock,
        ManualAreaProfileDirectoryEventSource events) =>
        WatchV2ApplicationComposition.Create(
            new WatchOptions
            {
                BaseUrl = "http://127.0.0.1:5088",
                SharedSecret = "external-only",
                RequestTimeoutSeconds = 30,
                RenderingMode = WatchRenderingMode.SoftwareOnly,
            },
            connectionPreferencesPath: Path.Combine(root, "connection.json"),
            workspacePreferencesPath: Path.Combine(root, "workspace.json"),
            timeProvider: clock,
            areaFilterProfilesDirectoryPath: areaProfilesPath,
            areaProfileDirectoryEventSource: events);

    private static void DrainDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void RunWithAreaProfileWindow(
        Action<WatchWorkspaceWindow,
            string,
            ManualAreaProfileDirectoryEventSource,
            ManualTimerTimeProvider> assert,
        string initialContent = "A1-1\nA1-2") =>
        StaTestRunner.Run(() =>
        {
            var root = NewRoot();
            var areaProfilesPath = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaProfilesPath);
            WriteProfile(areaProfilesPath, "西区", initialContent);
            var clock = new ManualTimerTimeProvider(StartedAt);
            var events = new ManualAreaProfileDirectoryEventSource();
            try
            {
                using var composition = CreateComposition(
                    root,
                    areaProfilesPath,
                    clock,
                    events);
                var window = composition.CreateMainWindow(initializeOnLoaded: false);
                try
                {
                    window.Show();
                    DrainDispatcher(window.Dispatcher);
                    NavigateTo(window, "AreaFilterNavigationItem");
                    SelectProfile(ProfileList(window), "西区");
                    assert(window, areaProfilesPath, events, clock);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        });
}
