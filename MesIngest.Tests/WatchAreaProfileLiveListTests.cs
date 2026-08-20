using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// UI composition seam for the live directory sync: the real production window,
/// a manually driven directory event source, and an injected clock.
/// </summary>
[Collection("WpfDesktop")]
public sealed class WatchAreaProfileLiveListTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_area_page_no_longer_offers_a_manual_reload_command() =>
        RunWithAreaProfileWindow((window, _, _, _) =>
        {
            Assert.Null(window.FindName("AreaProfileReloadButton"));
            Assert.Null(window.FindName("AreaProfileFileReloadButton"));
            Assert.NotNull(window.FindName("AreaProfileOpenDirectoryButton"));
        });

    [Fact]
    public void A_profile_dropped_into_the_directory_joins_the_list_without_any_user_action() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            Assert.Equal(
                ["西区"],
                Rows(list).Select(row => row.ProfileName));

            WriteProfile(directoryPath, "东区", "B2-2\nB2-3");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(
                ["东区", "西区"],
                Rows(list).Select(row => row.ProfileName));
            Assert.Equal(2, Assert.Single(Rows(list), row => row.ProfileName == "东区").MesAreaCount);
        });

    [Fact]
    public void A_profile_removed_from_the_directory_leaves_the_list_without_any_user_action() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            WriteProfile(directoryPath, "东区", "B2-2");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            Assert.Equal(2, Rows(list).Count);

            File.Delete(Path.Combine(directoryPath, "东区.txt"));
            events.RaiseDeleted("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);

            Assert.Equal(
                ["西区"],
                Rows(list).Select(row => row.ProfileName));
        });

    [Fact]
    public void An_externally_renamed_selected_profile_follows_the_new_name_and_resorts_without_a_deleted_warning() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            var title = Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                window.FindName("AreaProfileFileTitleText"));
            var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("AreaProfileInfoBar"));
            WriteProfile(directoryPath, "北区", "B2-2");
            events.RaiseCreated("北区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            var originalContent = editor.Text;

            File.Move(
                Path.Combine(directoryPath, "西区.txt"),
                Path.Combine(directoryPath, "东区.txt"));
            events.RaiseRenamed("东区.txt", "西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(
                ["东区", "北区"],
                Rows(list).Select(row => row.ProfileName));
            Assert.Equal(
                "东区",
                Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
                    .ProfileName);
            Assert.Equal("东区.txt", title.Text);
            Assert.Equal(originalContent, editor.Text);
            Assert.False(infoBar.IsOpen && infoBar.Title.Contains("删除", StringComparison.Ordinal));
        });

    [Fact]
    public void An_editor_atomic_replace_keeps_the_selected_row_visible_and_reloads_the_clean_editor() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("AreaProfileInfoBar"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");

            File.Delete(Path.Combine(directoryPath, "西区.txt"));
            events.RaiseDeleted("西区.txt");
            clock.Advance(
                WatchAreaFilterProfileStore.DeleteConfirmationWindow
                    - TimeSpan.FromMilliseconds(50));

            Assert.Equal(
                ["西区"],
                Rows(list).Select(row => row.ProfileName));
            Assert.Equal("A1-1\nA1-2", editor.Text);

            WriteProfile(directoryPath, "西区", "B2-2\nB2-3");
            events.RaiseCreated("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(
                ["西区"],
                Rows(list).Select(row => row.ProfileName));
            Assert.Equal("B2-2\nB2-3", editor.Text);
            Assert.False(infoBar.IsOpen && infoBar.Title.Contains("删除", StringComparison.Ordinal));
        });

    [Fact]
    public void An_externally_renamed_profile_keeps_the_local_dirty_buffer_under_the_new_name() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            var title = Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                window.FindName("AreaProfileFileTitleText"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            editor.Text += "\n# 本机尚未保存";
            var dirtyBuffer = editor.Text;

            File.Move(
                Path.Combine(directoryPath, "西区.txt"),
                Path.Combine(directoryPath, "东区.txt"));
            events.RaiseRenamed("东区.txt", "西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(
                "东区",
                Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
                    .ProfileName);
            Assert.Equal("东区.txt", title.Text);
            Assert.Equal(dirtyBuffer, editor.Text);
        });

    [Fact]
    public void A_live_list_refresh_keeps_the_selected_profile_the_unsaved_draft_and_the_focus() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            editor.Text += "\n# 未保存的本机草稿";
            var unsavedDraft = editor.Text;
            editor.Focus();
            Assert.Same(editor, FocusManager.GetFocusedElement(window));

            WriteProfile(directoryPath, "东区", "B2-2");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(2, Rows(list).Count);
            Assert.Equal(
                "西区",
                Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
                    .ProfileName);
            Assert.Equal(unsavedDraft, editor.Text);
            Assert.Same(editor, FocusManager.GetFocusedElement(window));
        });

    [Fact]
    public void Companion_files_dropped_next_to_a_profile_never_join_the_list() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));

            File.WriteAllText(
                Path.Combine(directoryPath, ".西区.txt.swp"),
                "B2-2",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(directoryPath, "西区.txtbackup"),
                "B2-2",
                new UTF8Encoding(false));
            events.RaiseCreated(".西区.txt.swp");
            events.RaiseCreated("西区.txtbackup");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(
                ["西区"],
                Rows(list).Select(row => row.ProfileName));
        });

    [Fact]
    public void An_external_rewrite_refreshes_a_clean_editor_on_the_ui_thread_without_losing_its_view() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(
                    window.FindName("AreaFilterNavigationItem"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.Height = 720;
            editor.Height = 120;
            editor.Width = 500;
            editor.ApplyTemplate();
            window.UpdateLayout();
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            editor.Focus();
            editor.CaretIndex = editor.GetCharacterIndexFromLineIndex(50) + 84;
            for (var index = 0; index < 4; index++)
            {
                EditingCommands.SelectLeftByCharacter.Execute(null, editor);
            }
            Assert.Equal(4, editor.SelectionLength);
            editor.ScrollToLine(45);
            editor.UpdateLayout();
            var editorScrollViewer = Assert.IsAssignableFrom<ScrollViewer>(
                FindVisualDescendant<ScrollViewer>(editor));
            editorScrollViewer.ScrollToHorizontalOffset(160);
            editor.UpdateLayout();
            var expectedCaret = editor.CaretIndex;
            var expectedSelectionStart = editor.SelectionStart;
            var expectedSelectionLength = editor.SelectionLength;
            var expectedHorizontalOffset = editorScrollViewer.HorizontalOffset;
            var expectedVerticalOffset = editorScrollViewer.VerticalOffset;
            Assert.True(expectedHorizontalOffset > 0);
            Assert.True(expectedVerticalOffset > 0);
            var textChangeCount = 0;
            var everyTextChangeWasOnTheUiThread = true;
            editor.TextChanged += (_, _) =>
            {
                textChangeCount++;
                everyTextChangeWasOnTheUiThread &= window.Dispatcher.CheckAccess();
            };
            var externalContent = ProfileContent("B2", 80);

            WriteProfile(directoryPath, "西区", externalContent);
            Task.Run(() =>
            {
                events.RaiseChanged("西区.txt");
                clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            }).GetAwaiter().GetResult();
            DrainDispatcher(window.Dispatcher);

            Assert.Equal(externalContent, editor.Text);
            Assert.Equal(80, Assert.Single(Rows(list), row => row.ProfileName == "西区").MesAreaCount);
            Assert.Equal(expectedCaret, editor.CaretIndex);
            Assert.Equal(expectedSelectionStart, editor.SelectionStart);
            Assert.Equal(expectedSelectionLength, editor.SelectionLength);
            Assert.Equal(expectedHorizontalOffset, editorScrollViewer.HorizontalOffset, precision: 3);
            Assert.Equal(expectedVerticalOffset, editorScrollViewer.VerticalOffset, precision: 3);
            Assert.Equal(1, textChangeCount);
            Assert.True(everyTextChangeWasOnTheUiThread);
            Assert.Same(editor, FocusManager.GetFocusedElement(window));
        }, ProfileContent("A1", 80));

    [Fact]
    public void An_external_save_with_identical_bytes_does_not_reload_the_editor() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            editor.Select(3, 4);
            var expectedCaret = editor.CaretIndex;
            var expectedSelectionStart = editor.SelectionStart;
            var expectedSelectionLength = editor.SelectionLength;
            var textChangeCount = 0;
            editor.TextChanged += (_, _) => textChangeCount++;

            WriteProfile(directoryPath, "西区", editor.Text);
            events.RaiseChanged("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(0, textChangeCount);
            Assert.Equal(expectedCaret, editor.CaretIndex);
            Assert.Equal(expectedSelectionStart, editor.SelectionStart);
            Assert.Equal(expectedSelectionLength, editor.SelectionLength);
        });

    [Fact]
    public void A_locked_external_write_reports_an_error_only_after_the_editor_retries_are_exhausted() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("AreaProfileInfoBar"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            var originalContent = editor.Text;
            using var writer = ExclusiveFileWriter.WriteUtf8AndHold(
                Path.Combine(directoryPath, "西区.txt"),
                "B2-2");

            events.RaiseChanged("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            Assert.False(window.AreaProfileOperationTask.IsCompleted);
            for (var index = 0;
                 index < WatchAreaFilterProfileStore.ExternalReadRetryDelays.Count;
                 index++)
            {
                clock.Advance(WatchAreaFilterProfileStore.ExternalReadRetryDelays[index]);
                if (index + 1 < WatchAreaFilterProfileStore.ExternalReadRetryDelays.Count)
                {
                    Assert.NotEqual("无法完成 AREA 配置操作", infoBar.Title);
                }
            }

            PumpUntilCompleted(window.Dispatcher, window.AreaProfileOperationTask);

            Assert.Equal(originalContent, editor.Text);
            Assert.True(infoBar.IsOpen);
            Assert.Equal("无法完成 AREA 配置操作", infoBar.Title);
        });

    [Fact]
    public void An_unwatchable_directory_is_reported_instead_of_pretending_the_list_is_live() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-area-live-list-{Guid.NewGuid():N}");
            var areaProfilesPath = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaProfilesPath);
            WriteProfile(areaProfilesPath, "西区", "A1-1\nA1-2");
            var events = new ManualAreaProfileDirectoryEventSource { FailNextStart = true };
            try
            {
                using var composition = WatchV2ApplicationComposition.Create(
                    new WatchOptions
                    {
                        BaseUrl = "http://127.0.0.1:5088",
                        RenderingMode = WatchRenderingMode.SoftwareOnly,
                    },
                    connectionPreferencesPath: Path.Combine(root, "connection.json"),
                    workspacePreferencesPath: Path.Combine(root, "workspace.json"),
                    timeProvider: new ManualTimerTimeProvider(StartedAt),
                    areaFilterProfilesDirectoryPath: areaProfilesPath,
                    areaProfileDirectoryEventSource: events);
                var window = composition.CreateMainWindow(initializeOnLoaded: false);
                try
                {
                    var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                        window.FindName("AreaProfileInfoBar"));
                    Assert.True(infoBar.IsOpen);
                    Assert.Equal("无法监视 AREA 配置目录", infoBar.Title);
                    Assert.Equal(
                        ["西区"],
                        Rows(Assert.IsType<ListBox>(window.FindName("AreaProfileList")))
                            .Select(row => row.ProfileName));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });

    private static IReadOnlyList<WatchAreaFilterProfilePresentationRow> Rows(ListBox list) =>
        [.. list.Items.Cast<WatchAreaFilterProfilePresentationRow>()];

    private static void WriteProfile(string directoryPath, string profileName, string content) =>
        File.WriteAllText(
            Path.Combine(directoryPath, $"{profileName}.txt"),
            content,
            new UTF8Encoding(false));

    private static string ProfileContent(string areaPrefix, int lineCount) =>
        string.Join(
            '\n',
            [
                .. Enumerable.Range(1, lineCount).Select(index =>
                    $"# {index:D2} {new string('x', 160)}"),
                .. Enumerable.Range(1, lineCount).Select(index => $"{areaPrefix}-{index}"),
            ]);

    private static void DrainDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntilCompleted(Dispatcher dispatcher, Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    () => frame.Continue = false),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
        DrainDispatcher(dispatcher);
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate)
            {
                return candidate;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void RunWithAreaProfileWindow(
        Action<WatchWorkspaceWindow,
            string,
            ManualAreaProfileDirectoryEventSource,
            ManualTimerTimeProvider> assert,
        string initialContent = "A1-1\nA1-2") =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-area-live-list-{Guid.NewGuid():N}");
            var areaProfilesPath = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaProfilesPath);
            WriteProfile(areaProfilesPath, "西区", initialContent);
            var clock = new ManualTimerTimeProvider(StartedAt);
            var events = new ManualAreaProfileDirectoryEventSource();
            try
            {
                using var composition = WatchV2ApplicationComposition.Create(
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
                var window = composition.CreateMainWindow(initializeOnLoaded: false);
                try
                {
                    window.Show();
                    DrainDispatcher(window.Dispatcher);
                    Assert.Equal(areaProfilesPath, events.StartedDirectoryPath);
                    assert(window, areaProfilesPath, events, clock);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });
}
