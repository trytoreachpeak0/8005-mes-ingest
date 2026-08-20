using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// UI composition seam for the rare path where the editor buffer and the file
/// on disk both changed. Auto-save keeps the dirty window near a second, so
/// these cases are unlikely — but skipping them would mean silently swallowing
/// whatever the other writer produced.
/// </summary>
[Collection("WpfDesktop")]
public sealed class WatchAreaProfileWriteConflictTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_dirty_buffer_meeting_a_changed_file_offers_the_choice_instead_of_overwriting() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            Editor(window).Text = "B2-2";

            RaiseExternalWrite(directoryPath, events, clock, "C3-3");
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal(Visibility.Visible, ConflictPanel(window).Visibility);
            Assert.True(KeepLocalButton(window).IsEnabled);
            Assert.True(UseDiskButton(window).IsEnabled);
            Assert.Equal("C3-3", ReadProfile(directoryPath, "西区"));
            Assert.Equal("B2-2", Editor(window).Text);
        });

    [Fact]
    public void Keeping_the_local_edit_writes_it_over_the_file_and_rebases_the_fingerprint() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            Editor(window).Text = "B2-2";
            RaiseExternalWrite(directoryPath, events, clock, "C3-3");
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            KeepLocalButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("B2-2", ReadProfile(directoryPath, "西区"));
            Assert.Equal(Visibility.Collapsed, ConflictPanel(window).Visibility);
            Assert.Equal("已自动保存", DiskState(window).Text);

            // The rebased fingerprint is what lets the next ordinary auto-save
            // land without a second conflict over the same file.
            Editor(window).Text = "B2-2\nB2-4";
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);
            Assert.Equal("B2-2\nB2-4", ReadProfile(directoryPath, "西区"));
            Assert.Equal(Visibility.Collapsed, ConflictPanel(window).Visibility);
        });

    [Fact]
    public void Taking_the_disk_version_drops_the_local_edit_and_reloads_the_editor() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            Editor(window).Text = "B2-2";
            RaiseExternalWrite(directoryPath, events, clock, "C3-3");
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            UseDiskButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("C3-3", Editor(window).Text);
            Assert.Equal("C3-3", ReadProfile(directoryPath, "西区"));
            Assert.Equal(Visibility.Collapsed, ConflictPanel(window).Visibility);
            Assert.Equal("已自动保存", DiskState(window).Text);
        });

    [Fact]
    public void Auto_save_stays_paused_while_the_choice_is_on_screen() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            Editor(window).Text = "B2-2";
            RaiseExternalWrite(directoryPath, events, clock, "C3-3");
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Editor(window).Text = "B2-2\nB2-4";
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("C3-3", ReadProfile(directoryPath, "西区"));
            Assert.Equal(Visibility.Visible, ConflictPanel(window).Visibility);
        });

    [Fact]
    public void A_deleted_file_under_a_dirty_buffer_keeps_the_text_as_an_unnamed_draft() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            Editor(window).Text = "B2-2";

            File.Delete(Path.Combine(directoryPath, "西区.txt"));
            events.RaiseDeleted("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("B2-2", Editor(window).Text);
            Assert.Equal("新建 AREA 配置", FileTitle(window).Text);
            Assert.Equal("文件已删除 · 未命名草稿", DiskState(window).Text);
            Assert.True(SaveDraftAsButton(window).IsEnabled);
            Assert.False(File.Exists(Path.Combine(directoryPath, "西区.txt")));
        });

    [Fact]
    public void An_unnamed_draft_reaches_the_disk_again_through_save_as() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            Editor(window).Text = "B2-2";
            File.Delete(Path.Combine(directoryPath, "西区.txt"));
            events.RaiseDeleted("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);

            SaveDraftAsButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            TargetNameInput(window).Text = "西区";
            ConfirmButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainDispatcher(window.Dispatcher);

            Assert.Equal("B2-2", ReadProfile(directoryPath, "西区"));
            Assert.Equal("西区.txt", FileTitle(window).Text);
        });

    [Fact]
    public void A_deleted_file_under_a_clean_buffer_moves_the_selection_to_the_neighbour() =>
        RunWithAreaProfileWindow(
            (window, directoryPath, events, clock) =>
            {
                WriteProfile(directoryPath, "b-line", "B2-2");
                WriteProfile(directoryPath, "c-line", "C3-3");
                events.RaiseCreated("b-line.txt");
                events.RaiseCreated("c-line.txt");
                clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
                var list = ProfileList(window);
                SelectProfile(list, "b-line");

                File.Delete(Path.Combine(directoryPath, "b-line.txt"));
                events.RaiseDeleted("b-line.txt");
                clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);

                Assert.Equal(
                    ["a-line", "c-line"],
                    Rows(list).Select(row => row.ProfileName));
                Assert.Equal(
                    "c-line",
                    Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
                        .ProfileName);
                Assert.Equal("C3-3", Editor(window).Text);
            },
            selectedProfileName: "a-line");

    [Fact]
    public void Keeping_the_local_edit_does_not_overwrite_a_writer_that_landed_after_the_prompt() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            Editor(window).Text = "B2-2";
            RaiseExternalWrite(directoryPath, events, clock, "C3-3");
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            // A third write lands while the prompt is up. Keeping the local
            // edit must rebase onto that version, not onto the stale one the
            // conflict was raised against.
            WriteProfile(directoryPath, "西区", "D4-4");
            KeepLocalButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("B2-2", ReadProfile(directoryPath, "西区"));
            Assert.Equal(Visibility.Collapsed, ConflictPanel(window).Visibility);
        });

    [Fact]
    public void Saving_the_buffer_elsewhere_settles_the_conflict_and_resumes_auto_save() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            Editor(window).Text = "B2-2";
            RaiseExternalWrite(directoryPath, events, clock, "C3-3");
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            var saveAs = FileCommand(window, "AreaProfileSaveAsMenuItem");
            Assert.True(saveAs.IsEnabled);
            Assert.False(FileCommand(window, "AreaProfileRenameMenuItem").IsEnabled);
            Assert.False(FileCommand(window, "AreaProfileDeleteMenuItem").IsEnabled);
            saveAs.RaiseEvent(
                new RoutedEventArgs(MenuItem.ClickEvent));
            TargetNameInput(window).Text = "东区";
            ConfirmButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainDispatcher(window.Dispatcher);

            Assert.Equal(Visibility.Collapsed, ConflictPanel(window).Visibility);
            Assert.Equal("B2-2", ReadProfile(directoryPath, "东区"));
            Assert.Equal("C3-3", ReadProfile(directoryPath, "西区"));

            Editor(window).Text = "B2-2\nB2-4";
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);
            Assert.Equal("B2-2\nB2-4", ReadProfile(directoryPath, "东区"));
        });

    [Fact]
    public void Ctrl_S_says_why_it_refused_instead_of_doing_nothing() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            Editor(window).Text = "B2-2";
            RaiseExternalWrite(directoryPath, events, clock, "C3-3");
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            ApplicationCommands.Save.Execute(
                null,
                Assert.IsType<ScrollViewer>(window.FindName("AreaFilterPage")));

            var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("AreaProfileInfoBar"));
            Assert.True(infoBar.IsOpen);
            Assert.Contains("冲突", infoBar.Title, StringComparison.Ordinal);
            Assert.Equal("C3-3", ReadProfile(directoryPath, "西区"));
        });

    [Fact]
    public void Leaving_the_page_and_coming_back_keeps_both_versions_and_the_prompt() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            Editor(window).Text = "B2-2";
            RaiseExternalWrite(directoryPath, events, clock, "C3-3");
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            NavigateTo(window, "OverviewNavigationItem");
            NavigateTo(window, "AreaFilterNavigationItem");

            Assert.Equal(Visibility.Visible, ConflictPanel(window).Visibility);
            Assert.Equal("B2-2", Editor(window).Text);
            Assert.Equal("C3-3", ReadProfile(directoryPath, "西区"));
        });

    [Fact]
    public void The_neighbour_the_selection_moves_to_is_one_the_search_still_shows() =>
        RunWithAreaProfileWindow(
            (window, directoryPath, events, clock) =>
            {
                WriteProfile(directoryPath, "a-keep", "B2-2");
                WriteProfile(directoryPath, "a-drop", "C3-3");
                events.RaiseCreated("a-keep.txt");
                events.RaiseCreated("a-drop.txt");
                clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
                var list = ProfileList(window);
                Assert.IsType<TextBox>(window.FindName("AreaProfileSearchInput")).Text = "a-";
                SelectProfile(list, "a-drop");

                File.Delete(Path.Combine(directoryPath, "a-drop.txt"));
                events.RaiseDeleted("a-drop.txt");
                clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);

                Assert.Equal(
                    "a-keep",
                    Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
                        .ProfileName);
                Assert.Equal("B2-2", Editor(window).Text);
            },
            selectedProfileName: "z-hidden");

    [Fact]
    public void Selecting_another_profile_is_refused_until_the_choice_is_made() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            WriteProfile(directoryPath, "东区", "D4-4");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            var list = ProfileList(window);
            Editor(window).Text = "B2-2";
            RaiseExternalWrite(directoryPath, events, clock, "C3-3");
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            SelectProfile(list, "东区");

            Assert.Equal(
                "西区",
                Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
                    .ProfileName);
            Assert.Equal("B2-2", Editor(window).Text);
            Assert.Equal(Visibility.Visible, ConflictPanel(window).Visibility);
        });

    private static void RaiseExternalWrite(
        string directoryPath,
        ManualAreaProfileDirectoryEventSource events,
        ManualTimerTimeProvider clock,
        string content)
    {
        WriteProfile(directoryPath, "西区", content);
        events.RaiseChanged("西区.txt");
        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
    }

    private static void NavigateTo(WatchWorkspaceWindow window, string navigationItemName) =>
        Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(window.FindName(navigationItemName))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static TextBox Editor(WatchWorkspaceWindow window) =>
        Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));

    private static ListBox ProfileList(WatchWorkspaceWindow window) =>
        Assert.IsType<ListBox>(window.FindName("AreaProfileList"));

    private static Border ConflictPanel(WatchWorkspaceWindow window) =>
        Assert.IsType<Border>(window.FindName("AreaProfileWriteConflictPanel"));

    private static Wpf.Ui.Controls.Button KeepLocalButton(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.Button>(
            window.FindName("AreaProfileKeepLocalEditButton"));

    private static Wpf.Ui.Controls.Button UseDiskButton(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.Button>(
            window.FindName("AreaProfileUseDiskVersionButton"));

    private static Wpf.Ui.Controls.Button SaveDraftAsButton(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.Button>(
            window.FindName("AreaProfileSaveDraftAsButton"));

    private static MenuItem FileCommand(WatchWorkspaceWindow window, string automationId) =>
        WatchAreaProfileFileCommandTestHelper.FindSelected(window, automationId);

    private static Wpf.Ui.Controls.Button ConfirmButton(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.Button>(
            window.FindName("AreaProfileFileOperationConfirmButton"));

    private static TextBox TargetNameInput(WatchWorkspaceWindow window) =>
        Assert.IsType<TextBox>(window.FindName("AreaProfileTargetNameInput"));

    private static Wpf.Ui.Controls.TextBlock DiskState(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.TextBlock>(
            window.FindName("AreaProfileDiskStateText"));

    private static Wpf.Ui.Controls.TextBlock FileTitle(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.TextBlock>(
            window.FindName("AreaProfileFileTitleText"));

    private static IReadOnlyList<WatchAreaFilterProfilePresentationRow> Rows(ListBox list) =>
        list.Items
            .Cast<WatchAreaFilterProfilePresentationRow>()
            .Where(row => !row.IsAllAreas)
            .ToArray();

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
        $"watch-area-conflict-{Guid.NewGuid():N}");

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
        string selectedProfileName = "西区") =>
        StaTestRunner.Run(() =>
        {
            var root = NewRoot();
            var areaProfilesPath = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaProfilesPath);
            WriteProfile(areaProfilesPath, selectedProfileName, "A1-1\nA1-2");
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
                    Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(
                        window.FindName("AreaFilterNavigationItem"))
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    SelectProfile(ProfileList(window), selectedProfileName);
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
