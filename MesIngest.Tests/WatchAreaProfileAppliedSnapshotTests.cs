using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// The display scope is the AREA snapshot taken when the user last pressed
/// apply. Files keep moving underneath it — the editor writes on its own, other
/// programs write too — and none of that may shift what the data pages show
/// until the user says so. These cases pin that boundary and the way the page
/// admits when the applied file has drifted away from the snapshot.
/// </summary>
[Collection("WpfDesktop")]
public sealed class WatchAreaProfileAppliedSnapshotTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private const string AppliedContent = "A1-1\nA1-2";

    [Fact]
    public void Area_filter_layout_gives_redundant_banner_and_field_strip_space_to_the_editor() =>
        RunWithAppliedProfile((window, _, _, _) =>
        {
            window.Width = 1440;
            window.Height = 900;
            window.UpdateLayout();
            DrainDispatcher(window.Dispatcher);

            Assert.DoesNotContain(
                VisualDescendants<Wpf.Ui.Controls.InfoBar>(window),
                infoBar => string.Equals(
                    AutomationProperties.GetName(infoBar),
                    "当前应用 AREA 显示范围",
                    StringComparison.Ordinal));
            Assert.Null(window.FindName("AreaProfileRulesText"));

            var fileTitle = Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                window.FindName("AreaProfileFileTitleText"));
            var validCount = Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                window.FindName("AreaProfileValidCountText"));
            var titleCenter = fileTitle.TranslatePoint(
                new Point(0, fileTitle.ActualHeight / 2),
                window);
            var countCenter = validCount.TranslatePoint(
                new Point(0, validCount.ActualHeight / 2),
                window);
            Assert.InRange(Math.Abs(titleCenter.Y - countCenter.Y), 0, 2);

            var pathAndFormat = Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                window.FindName("AreaProfileDirectoryText"));
            Assert.Contains("每行一个 AREA", pathAndFormat.Text, StringComparison.Ordinal);
            Assert.Contains("A1-1", pathAndFormat.Text, StringComparison.Ordinal);
            Assert.Contains("# 开头忽略", pathAndFormat.Text, StringComparison.Ordinal);

            var editorFrame = Assert.IsType<Border>(
                window.FindName("AreaProfileEditorFrame"));
            Assert.True(
                editorFrame.ActualHeight >= 500,
                $"AREA editor height was only {editorFrame.ActualHeight:N0} epx.");
        });

    // One row per line of the ticket's state table. The file condition travels
    // as a string because the enum is internal to MesIngest.Watch and a public
    // xUnit theory method cannot take a less accessible parameter type.
    [Theory]
    // not the applied one, valid -> offers to apply
    [InlineData(false, "Valid", false, "应用此配置", true, false)]
    // not the applied one, invalid -> offers to apply, refuses
    [InlineData(false, "Invalid", false, "应用此配置", false, false)]
    // the applied one, valid, still matches the snapshot -> nothing to do
    [InlineData(true, "Valid", true, "已应用", false, false)]
    // the applied one, valid, drifted -> offers to re-apply
    [InlineData(true, "Valid", false, "重新应用", true, false)]
    // the applied one, invalid -> refuses, and says why in the status bar
    [InlineData(true, "Invalid", false, "重新应用", false, true)]
    // the applied one, file deleted -> refuses, and says the scope still holds
    [InlineData(true, "Missing", false, "重新应用", false, true)]
    public void The_apply_button_states_match_the_agreed_table(
        bool isCurrentApplied,
        string condition,
        bool matchesAppliedSnapshot,
        string expectedContent,
        bool expectedEnabled,
        bool expectsBlockedReason)
    {
        var state = WatchAreaProfileApplyButtonState.Evaluate(
            isCurrentApplied,
            Enum.Parse<WatchAreaProfileFileCondition>(condition),
            matchesAppliedSnapshot);

        Assert.Equal(expectedContent, state.Content);
        Assert.Equal(expectedEnabled, state.IsEnabled);
        Assert.Equal(expectsBlockedReason, state.BlockedReason is not null);
    }

    [Fact]
    public void A_profile_deleted_underneath_the_scope_still_says_the_scope_is_in_force()
    {
        var state = WatchAreaProfileApplyButtonState.Evaluate(
            isCurrentApplied: true,
            WatchAreaProfileFileCondition.Missing,
            matchesAppliedSnapshot: false);

        Assert.Contains("范围仍生效", state.BlockedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_externally_deleted_applied_profile_stays_as_a_recoverable_snapshot_row() =>
        RunWithAppliedProfile((window, directoryPath, events, clock) =>
        {
            File.Delete(Path.Combine(directoryPath, "西区.txt"));
            events.RaiseDeleted("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);

            var row = AppliedRow(window);
            Assert.Equal("西区", row.ProfileName);
            Assert.Equal(2, row.MesAreaCount);
            Assert.Equal("文件已删除 · 范围仍生效", row.AttentionText);
            Assert.Contains(
                "AREA A1-1、A1-2",
                AutomationProperties.GetName(AppliedState(window)),
                StringComparison.Ordinal);
            Assert.Equal(["A1-1", "A1-2"], window.AreaContext.MesAreas);
            Assert.Equal("重新应用", ApplyButton(window).Content);
            Assert.False(ApplyButton(window).IsEnabled);
            Assert.Contains(
                "文件已删除",
                AutomationProperties.GetName(ApplyButton(window)),
                StringComparison.Ordinal);
            Assert.True(SaveAsButton(window).IsEnabled);
            Assert.False(RenameButton(window).IsEnabled);
            Assert.False(DeleteButton(window).IsEnabled);

            SaveAsButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("西区", TargetNameInput(window).Text);
        });

    [Fact]
    public void Deleting_the_applied_profile_in_the_page_keeps_the_same_scope_and_recovery_row() =>
        RunWithAppliedProfile((window, directoryPath, _, _) =>
        {
            DeleteButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Contains(
                "范围仍生效",
                FileOperationPrompt(window).Text,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "回退为全部 AREA",
                FileOperationPrompt(window).Text,
                StringComparison.Ordinal);

            ConfirmFileOperationButton(window).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntilCompleted(window.Dispatcher, window.AreaProfileOperationTask);

            Assert.False(File.Exists(Path.Combine(directoryPath, "西区.txt")));
            Assert.Equal("文件已删除 · 范围仍生效", AppliedRow(window).AttentionText);
            Assert.Contains(
                "AREA A1-1、A1-2",
                AutomationProperties.GetName(AppliedState(window)),
                StringComparison.Ordinal);
            Assert.Equal(["A1-1", "A1-2"], window.AreaContext.MesAreas);
            Assert.True(SaveAsButton(window).IsEnabled);
        });

    [Fact]
    public void A_deleted_applied_profile_can_be_restored_under_the_same_name_without_moving_scope() =>
        RunWithAppliedProfile((window, directoryPath, events, clock) =>
        {
            File.Delete(Path.Combine(directoryPath, "西区.txt"));
            events.RaiseDeleted("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);

            SaveAsButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("西区", TargetNameInput(window).Text);
            ConfirmFileOperationButton(window).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntilCompleted(window.Dispatcher, window.AreaProfileOperationTask);

            Assert.Equal(AppliedContent, ReadProfile(directoryPath, "西区"));
            Assert.Null(AppliedRow(window).AttentionText);
            Assert.Equal("已应用", ApplyButton(window).Content);
            Assert.Contains(
                "AREA A1-1、A1-2",
                AutomationProperties.GetName(AppliedState(window)),
                StringComparison.Ordinal);
            Assert.Equal(["A1-1", "A1-2"], window.AreaContext.MesAreas);
        });

    [Fact]
    public void Selecting_an_unselected_missing_applied_row_restores_from_its_snapshot_not_the_other_editor() =>
        RunWithAppliedProfile((window, directoryPath, events, clock) =>
        {
            WriteProfile(directoryPath, "东区", "B2-2");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            SelectProfile(ProfileList(window), "东区");
            Assert.Equal("B2-2", Editor(window).Text);

            File.Delete(Path.Combine(directoryPath, "西区.txt"));
            events.RaiseDeleted("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);
            Assert.Equal("B2-2", Editor(window).Text);

            SelectProfile(ProfileList(window), "西区");

            Assert.Equal(AppliedContent, Editor(window).Text);
            Assert.True(SaveAsButton(window).IsEnabled);
            SaveAsButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("西区", TargetNameInput(window).Text);
        });

    [Fact]
    public void Only_applying_all_areas_releases_the_scope_after_its_profile_file_is_deleted() =>
        RunWithAppliedProfile((window, directoryPath, events, clock) =>
        {
            File.Delete(Path.Combine(directoryPath, "西区.txt"));
            events.RaiseDeleted("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);
            Assert.Contains(
                "AREA A1-1、A1-2",
                AutomationProperties.GetName(AppliedState(window)),
                StringComparison.Ordinal);

            ApplyAllAreasButton(window).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntilCompleted(window.Dispatcher, window.AreaProfileOperationTask);

            Assert.Contains("当前应用：全部 AREA", AppliedState(window).Text, StringComparison.Ordinal);
            Assert.Empty(window.AreaContext.MesAreas);
            Assert.DoesNotContain(Rows(ProfileList(window)), row => row.IsApplied);
        });

    [Fact]
    public void Editing_the_applied_profile_leaves_the_display_scope_where_the_snapshot_put_it() =>
        RunWithAppliedProfile((window, directoryPath, _, clock) =>
        {
            Editor(window).Text = "B2-2\nB2-4\nB2-6";
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("B2-2\nB2-4\nB2-6", ReadProfile(directoryPath, "西区"));
            Assert.Contains(
                "西区 · 2 个 AREA",
                AppliedState(window).Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "AREA A1-1、A1-2",
                AutomationProperties.GetName(AppliedState(window)),
                StringComparison.Ordinal);
        });

    [Fact]
    public void An_external_write_to_the_applied_profile_asks_for_a_re_apply_without_moving_the_scope() =>
        RunWithAppliedProfile((window, directoryPath, events, clock) =>
        {
            WriteProfile(directoryPath, "西区", "C3-3");
            events.RaiseChanged("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal("重新应用", ApplyButton(window).Content);
            Assert.True(ApplyButton(window).IsEnabled);
            Assert.Equal("待重新应用", AppliedRow(window).AttentionText);
            Assert.Contains(
                "AREA A1-1、A1-2",
                AutomationProperties.GetName(AppliedState(window)),
                StringComparison.Ordinal);
        });

    [Fact]
    public void A_comment_or_blank_line_edit_is_not_drift() =>
        RunWithAppliedProfile((window, _, _, clock) =>
        {
            Editor(window).Text = "# 白班\nA1-1\n\nA1-2\n";
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("已应用", ApplyButton(window).Content);
            Assert.False(ApplyButton(window).IsEnabled);
            Assert.Null(AppliedRow(window).AttentionText);
        });

    [Fact]
    public void Reordering_the_areas_is_not_drift_because_the_parsed_sequence_is_ordered() =>
        RunWithAppliedProfile((window, _, _, clock) =>
        {
            Editor(window).Text = "A1-2\nA1-1";
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("已应用", ApplyButton(window).Content);
            Assert.Null(AppliedRow(window).AttentionText);
        });

    [Fact]
    public void Adding_an_area_in_the_editor_is_drift() =>
        RunWithAppliedProfile((window, _, _, clock) =>
        {
            Editor(window).Text = AppliedContent + "\nA1-3";
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("重新应用", ApplyButton(window).Content);
            Assert.True(ApplyButton(window).IsEnabled);
            Assert.Equal("待重新应用", AppliedRow(window).AttentionText);
        });

    [Fact]
    public void Invalid_content_cannot_be_applied_and_the_scope_stays_where_it_was() =>
        RunWithAppliedProfile((window, _, _, clock) =>
        {
            Editor(window).Text = "还没打完";
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Equal("重新应用", ApplyButton(window).Content);
            Assert.False(ApplyButton(window).IsEnabled);
            Assert.Contains(
                "AREA A1-1、A1-2",
                AutomationProperties.GetName(AppliedState(window)),
                StringComparison.Ordinal);
        });

    [Fact]
    public void An_invalid_applied_profile_says_so_in_all_three_places() =>
        RunWithAppliedProfile((window, _, _, clock) =>
        {
            Editor(window).Text = "还没打完";
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            Assert.Contains(
                "非法",
                ValidationSummary(window).Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "无效",
                ValidCount(window).Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "内容非法",
                AppliedRow(window).AttentionText!,
                StringComparison.Ordinal);
        });

    [Fact]
    public void The_applied_badge_is_not_displaced_by_invalid_content_or_drift() =>
        RunWithAppliedProfile((window, _, _, clock) =>
        {
            Editor(window).Text = "还没打完";
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            // The accessible name is where both dimensions have to survive
            // together: the badge says one of them and the subtitle the other,
            // and a screen reader only ever hears this one string.
            var row = AppliedRow(window);
            Assert.Equal("内容非法 · 待重新应用", row.AttentionText);
            Assert.Contains("当前应用", row.AutomationName, StringComparison.Ordinal);
            Assert.Contains("内容非法", row.AutomationName, StringComparison.Ordinal);
        });

    [Fact]
    public void The_list_shows_the_directory_and_not_the_unwritten_buffer() =>
        RunWithAppliedProfile((window, _, _, _) =>
        {
            // No clock advance: nothing has reached the disk yet.
            Editor(window).Text = "还没打完";

            var row = AppliedRow(window);
            Assert.True(row.IsValid);
            Assert.Equal(2, row.MesAreaCount);
            Assert.Contains(
                "1 个文件 · 0 个需要修复",
                ListSummary(window).Text,
                StringComparison.Ordinal);
        });

    [Fact]
    public void Drift_shows_on_the_row_before_the_buffer_reaches_the_disk() =>
        RunWithAppliedProfile((window, _, _, _) =>
        {
            // No clock advance either: the row still describes the file, but it
            // may not claim the scope matches something the user has typed away.
            Editor(window).Text = AppliedContent + "\nA1-3";

            Assert.Equal("待重新应用", AppliedRow(window).AttentionText);
            Assert.Equal("重新应用", ApplyButton(window).Content);
        });

    [Fact]
    public void A_profile_that_is_not_the_applied_one_offers_to_apply_itself() =>
        RunWithAppliedProfile((window, directoryPath, events, clock) =>
        {
            WriteProfile(directoryPath, "东区", "D4-4");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            SelectProfile(ProfileList(window), "东区");

            Assert.Equal("应用此配置", ApplyButton(window).Content);
            Assert.True(ApplyButton(window).IsEnabled);
            Assert.Null(Assert.Single(
                    Rows(ProfileList(window)),
                    row => row.ProfileName == "东区")
                .AttentionText);
        });

    [Fact]
    public void A_profile_that_is_not_the_applied_one_cannot_be_applied_while_invalid() =>
        RunWithAppliedProfile((window, directoryPath, events, clock) =>
        {
            WriteProfile(directoryPath, "东区", "还没打完");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            SelectProfile(ProfileList(window), "东区");

            Assert.Equal("应用此配置", ApplyButton(window).Content);
            Assert.False(ApplyButton(window).IsEnabled);
            Assert.Equal(
                "内容非法 · 需修复",
                Assert.Single(Rows(ProfileList(window)), row => row.ProfileName == "东区")
                    .AttentionText);
        });

    [Fact]
    public void Re_applying_a_drifted_profile_moves_the_snapshot_to_what_the_file_now_says() =>
        RunWithAppliedProfile((window, _, _, clock) =>
        {
            Editor(window).Text = "C3-3\nC3-5";
            clock.Advance(WatchAreaFilterProfileStore.EditorAutoSaveDelay);

            ApplyButton(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainDispatcher(window.Dispatcher);

            Assert.Contains(
                "AREA C3-3、C3-5",
                AutomationProperties.GetName(AppliedState(window)),
                StringComparison.Ordinal);
            Assert.Equal("已应用", ApplyButton(window).Content);
            Assert.False(ApplyButton(window).IsEnabled);
            Assert.Null(AppliedRow(window).AttentionText);
        });

    [Fact]
    public void The_snapshot_taken_before_the_window_existed_is_still_the_display_scope() =>
        RunWithAppliedProfile((window, directoryPath, _, _) =>
        {
            // The applied marker was written by a store instance that is gone
            // by the time the window opens, which is what a restart looks like.
            Assert.Contains(
                "西区 · 2 个 AREA",
                AppliedState(window).Text,
                StringComparison.Ordinal);
            Assert.Equal(AppliedContent, ReadProfile(directoryPath, "西区"));
            Assert.Equal("已应用", ApplyButton(window).Content);
        });

    private static Wpf.Ui.Controls.Button ApplyButton(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.Button>(window.FindName("AreaProfileApplyButton"));

    private static Wpf.Ui.Controls.Button SaveAsButton(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.Button>(window.FindName("AreaProfileSaveAsButton"));

    private static Wpf.Ui.Controls.Button RenameButton(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.Button>(window.FindName("AreaProfileRenameButton"));

    private static Wpf.Ui.Controls.Button DeleteButton(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.Button>(window.FindName("AreaProfileDeleteButton"));

    private static Wpf.Ui.Controls.Button ApplyAllAreasButton(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.Button>(window.FindName("AreaApplyAllAreasButton"));

    private static TextBox TargetNameInput(WatchWorkspaceWindow window) =>
        Assert.IsType<TextBox>(window.FindName("AreaProfileTargetNameInput"));

    private static Wpf.Ui.Controls.Button ConfirmFileOperationButton(
        WatchWorkspaceWindow window) => Assert.IsType<Wpf.Ui.Controls.Button>(
        window.FindName("AreaProfileFileOperationConfirmButton"));

    private static Wpf.Ui.Controls.TextBlock FileOperationPrompt(
        WatchWorkspaceWindow window) => Assert.IsType<Wpf.Ui.Controls.TextBlock>(
        window.FindName("AreaProfileFileOperationPromptText"));

    private static TextBox Editor(WatchWorkspaceWindow window) =>
        Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));

    private static ListBox ProfileList(WatchWorkspaceWindow window) =>
        Assert.IsType<ListBox>(window.FindName("AreaProfileList"));

    private static Wpf.Ui.Controls.TextBlock AppliedState(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.TextBlock>(
            window.FindName("AreaProfileAppliedStateText"));

    private static Wpf.Ui.Controls.TextBlock ValidationSummary(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.TextBlock>(
            window.FindName("AreaProfileValidationSummaryText"));

    private static Wpf.Ui.Controls.TextBlock ListSummary(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.TextBlock>(
            window.FindName("AreaProfileListSummaryText"));

    private static Wpf.Ui.Controls.TextBlock ValidCount(WatchWorkspaceWindow window) =>
        Assert.IsType<Wpf.Ui.Controls.TextBlock>(
            window.FindName("AreaProfileValidCountText"));

    private static WatchAreaFilterProfilePresentationRow AppliedRow(
        WatchWorkspaceWindow window) => Assert.Single(
        Rows(ProfileList(window)),
        row => row.IsApplied);

    private static IReadOnlyList<WatchAreaFilterProfilePresentationRow> Rows(ListBox list) =>
        [.. list.Items.Cast<WatchAreaFilterProfilePresentationRow>()];

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
        $"watch-area-snapshot-{Guid.NewGuid():N}");

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

    private static void PumpUntilCompleted(Dispatcher dispatcher, Task task)
    {
        while (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in VisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Opens the page with 西区 already applied, and with the applying store
    /// disposed before the window exists — the snapshot the window reads is one
    /// that outlived the process that took it.
    /// </summary>
    private static void RunWithAppliedProfile(
        Action<WatchWorkspaceWindow,
            string,
            ManualAreaProfileDirectoryEventSource,
            ManualTimerTimeProvider> assert) =>
        StaTestRunner.Run(() =>
        {
            var root = NewRoot();
            var areaProfilesPath = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaProfilesPath);
            WriteProfile(areaProfilesPath, "西区", AppliedContent);
            var clock = new ManualTimerTimeProvider(StartedAt);
            var events = new ManualAreaProfileDirectoryEventSource();
            try
            {
                using (var seedStore = new WatchAreaFilterProfileStore(areaProfilesPath, clock))
                {
                    Assert.True(seedStore.Apply("西区").Applied);
                }

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
