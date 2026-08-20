using System.Text;
using System.Windows.Controls;
using System.Windows.Input;
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
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(
                ["西区"],
                Rows(list).Select(row => row.ProfileName));
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
    public void A_live_list_refresh_leaves_the_editor_buffer_and_the_caret_alone() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            var loadedContent = editor.Text;
            editor.CaretIndex = 3;

            // Another editor rewrote the selected file. Following it into the
            // editor is the live reload ticket; the list follows it here.
            WriteProfile(directoryPath, "西区", "C3-1\nC3-2\nC3-3");
            events.RaiseChanged("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(3, Assert.Single(Rows(list), row => row.ProfileName == "西区").MesAreaCount);
            Assert.Equal(loadedContent, editor.Text);
            Assert.Equal(3, editor.CaretIndex);
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

    private static void RunWithAreaProfileWindow(
        Action<WatchWorkspaceWindow,
            string,
            ManualAreaProfileDirectoryEventSource,
            ManualTimerTimeProvider> assert) =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-area-live-list-{Guid.NewGuid():N}");
            var areaProfilesPath = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaProfilesPath);
            WriteProfile(areaProfilesPath, "西区", "A1-1\nA1-2");
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
