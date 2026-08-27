using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MesIngest.Watch;

namespace MesIngest.Tests;

[Collection("WpfDesktop")]
public sealed class WatchBilingualAreaFilterTests
{
    [Fact]
    public void Area_feedback_builders_are_enumerable_and_emit_controlled_English()
    {
        var text = WatchTextCatalog.For(WatchDisplayLanguage.English).AreaFilter;
        var contents = new[]
        {
            text.RestorePreviousFailed("中文诊断"),
            text.ReadProfilesFailed("中文诊断", preserveAppliedScope: false),
            text.ReadProfilesFailed("中文诊断", preserveAppliedScope: true),
            text.ReadAppliedProfileFailed("中文诊断"),
            text.DirectoryWatchDegraded("中文诊断"),
            text.DirectoryOpened("C:\\area-profiles", launchedFileManager: true),
            text.DirectoryOpened("C:\\area-profiles", launchedFileManager: false),
            text.ReadProfileFailed("中文诊断"),
            text.ConfirmAppliedScopeFailed("中文诊断"),
            text.PrepareOperationMissingVersion(),
            text.ProfileChangedOnDisk(),
            text.PrepareOperationFailed("中文诊断"),
            text.DraftNamed(),
            text.SavedAs("line-a"),
            text.Renamed("line-a", "line-b"),
            text.Deleted("line-a", appliedProfileWasDeleted: false),
            text.Deleted("line-a", appliedProfileWasDeleted: true),
            text.SavedButNotApplied("中文诊断"),
            text.ProfileApplied(),
            text.AllAreasApplied(),
            text.OperationFailed("中文诊断"),
        };
        var feedbackEntries = text.Entries
            .Where(entry => entry.SemanticId.StartsWith("area.feedback.", StringComparison.Ordinal)
                || entry.SemanticId.StartsWith("area.apply.", StringComparison.Ordinal)
                || entry.SemanticId.StartsWith("area.diagnostic.", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(feedbackEntries);
        Assert.All(feedbackEntries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.SimplifiedChinese));
            Assert.False(string.IsNullOrWhiteSpace(entry.English));
            Assert.False(ContainsHan(entry.English), $"{entry.SemanticId}: {entry.English}");
            Assert.Contains(
                WatchTextCatalog.AllEntries,
                candidate => candidate.SemanticId == entry.SemanticId);
        });
        Assert.All(contents, content =>
        {
            Assert.False(ContainsHan(content.SeverityText.English), content.SeverityText.English);
            Assert.False(ContainsHan(content.Title.English), content.Title.English);
            Assert.False(ContainsHan(content.Message.English), content.Message.English);
            if (content.ActionLabel is not null)
            {
                Assert.False(ContainsHan(content.ActionLabel.English), content.ActionLabel.English);
            }
        });
        Assert.Contains("中文诊断", contents[0].Message.SimplifiedChinese, StringComparison.Ordinal);
        Assert.DoesNotContain("中文诊断", contents[0].Message.English, StringComparison.Ordinal);
        Assert.False(ContainsHan(text.LocalizeApplyBlockedReason("未知阻塞原因")));
        Assert.False(ContainsHan(text.ApplyAutomation(
            WatchAreaProfileApplyAction.Reapply,
            "内容非法不可应用 · 当前显示范围保持不变")));
        Assert.False(ContainsHan(text.DiagnosticMessage(
            new WatchAreaFilterProfileDiagnostic("UNKNOWN_CODE", "中文诊断"))));
    }

    [Fact]
    public void Warning_info_bar_reprojects_typed_content_and_UIA_without_leaking_Chinese()
    {
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory();
            var areaDirectory = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaDirectory);
            File.WriteAllText(Path.Combine(areaDirectory, ".active-profile"), "不是 JSON");
            var preferences = WatchV2Preferences.Default with
            {
                DisplayLanguage = WatchDisplayLanguage.English,
            };
            var language = new WatchDisplayLanguageState(WatchDisplayLanguage.English);
            WatchWorkspaceWindow? window = null;
            try
            {
                window = new WatchWorkspaceWindow(
                    new WatchHostSettings("http://host-a", "secret", 30),
                    preferences,
                    Path.Combine(root, "connection.json"),
                    Path.Combine(root, "workspace.json"),
                    initializeOnLoaded: false,
                    areaFilterProfilesDirectoryPath: areaDirectory,
                    displayLanguageState: language);
                window.Show();
                Assert.IsAssignableFrom<ButtonBase>(window.FindName("AreaFilterNavigationItem"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.UpdateLayout();

                var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                    window.FindName("AreaProfileInfoBar"));
                var expected = WatchTextCatalog.For(WatchDisplayLanguage.English)
                    .AreaFilter.RestorePreviousFailed("ignored");
                Assert.True(infoBar.IsOpen);
                Assert.Equal(expected.Title.English, infoBar.Title);
                Assert.Equal(expected.Message.English, infoBar.Message);
                Assert.Equal(
                    $"{infoBar.Title}. {infoBar.Message}",
                    AutomationProperties.GetName(infoBar));
                Assert.False(ContainsHan(infoBar.Title));
                Assert.False(ContainsHan(infoBar.Message));
                Assert.False(ContainsHan(AutomationProperties.GetName(infoBar)));

                language.ApplyCommitted(WatchDisplayLanguage.SimplifiedChinese);

                Assert.Equal(expected.Title.SimplifiedChinese, infoBar.Title);
                Assert.Contains("已回退到全部 AREA", infoBar.Message, StringComparison.Ordinal);
                Assert.Equal(
                    $"{infoBar.Title}。{infoBar.Message}",
                    AutomationProperties.GetName(infoBar));
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });
    }

    [Fact]
    public void Directory_watch_degradation_uses_typed_English_content_and_matching_UIA()
    {
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory();
            var areaDirectory = Path.Combine(root, "area-filters");
            var events = new ManualAreaProfileDirectoryEventSource();
            var preferences = WatchV2Preferences.Default with
            {
                DisplayLanguage = WatchDisplayLanguage.English,
            };
            WatchWorkspaceWindow? window = null;
            try
            {
                window = new WatchWorkspaceWindow(
                    new WatchHostSettings("http://host-a", "secret", 30),
                    preferences,
                    Path.Combine(root, "connection.json"),
                    Path.Combine(root, "workspace.json"),
                    initializeOnLoaded: true,
                    areaFilterProfilesDirectoryPath: areaDirectory,
                    areaProfileDirectoryEventSource: events,
                    displayLanguageState: new WatchDisplayLanguageState(
                        WatchDisplayLanguage.English));
                window.Show();
                Assert.IsAssignableFrom<ButtonBase>(window.FindName("AreaFilterNavigationItem"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    static () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.True(events.IsStarted);

                events.FailNextStart = true;
                events.RaiseFailure(new IOException("中文目录异常"));
                window.Dispatcher.Invoke(() => { });

                var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                    window.FindName("AreaProfileDirectoryWatchInfoBar"));
                Assert.True(infoBar.IsOpen);
                Assert.Equal("AREA profile folder monitoring is degraded", infoBar.Title);
                Assert.False(ContainsHan(infoBar.Message));
                Assert.Equal(
                    $"{infoBar.Title}. {infoBar.Message}",
                    AutomationProperties.GetName(infoBar));
                Assert.False(ContainsHan(AutomationProperties.GetName(infoBar)));
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });
    }

    [Fact]
    public void Non_warning_area_feedback_reaches_notification_UIA_as_typed_English()
    {
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory();
            var areaDirectory = Path.Combine(root, "area-filters");
            var preferences = WatchV2Preferences.Default with
            {
                DisplayLanguage = WatchDisplayLanguage.English,
            };
            WatchWorkspaceWindow? window = null;
            try
            {
                window = new WatchWorkspaceWindow(
                    new WatchHostSettings("http://host-a", "secret", 30),
                    preferences,
                    Path.Combine(root, "connection.json"),
                    Path.Combine(root, "workspace.json"),
                    initializeOnLoaded: false,
                    areaFilterProfilesDirectoryPath: areaDirectory,
                    areaProfileDirectoryLauncher: new TestDirectoryLauncher(),
                    notificationReducedMotionProvider: static () => true,
                    displayLanguageState: new WatchDisplayLanguageState(
                        WatchDisplayLanguage.English));
                window.Show();
                Assert.IsAssignableFrom<ButtonBase>(window.FindName("AreaFilterNavigationItem"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.IsAssignableFrom<ButtonBase>(window.FindName("AreaProfileOpenDirectoryButton"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.UpdateLayout();

                var items = Assert.IsType<ItemsControl>(window.FindName("NotificationItemsControl"));
                var card = Assert.Single(items.Items.Cast<object>());
                var title = Assert.IsType<string>(card.GetType().GetProperty("Title")!.GetValue(card));
                var message = Assert.IsType<string>(card.GetType().GetProperty("Message")!.GetValue(card));
                var automationName = Assert.IsType<string>(
                    card.GetType().GetProperty("AutomationName")!.GetValue(card));
                Assert.Equal("AREA profiles folder is ready", title);
                Assert.Contains(areaDirectory, message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(title, automationName, StringComparison.Ordinal);
                Assert.Contains(message, automationName, StringComparison.Ordinal);
                Assert.False(ContainsHan(title));
                Assert.False(ContainsHan(message));
                Assert.False(ContainsHan(automationName));
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });
    }

    [Fact]
    public void Area_editor_language_reprojection_preserves_draft_selection_caret_and_disk()
    {
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory();
            var areaDirectory = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaDirectory);
            var profilePath = Path.Combine(areaDirectory, "line-a.txt");
            File.WriteAllText(profilePath, "# draft\nAREA-A\nAREA-B\n");
            var language = new WatchDisplayLanguageState(WatchDisplayLanguage.SimplifiedChinese);
            WatchWorkspaceWindow? window = null;
            try
            {
                window = new WatchWorkspaceWindow(
                    new WatchHostSettings("http://host-a", "secret", 30),
                    WatchV2Preferences.Default,
                    Path.Combine(root, "connection.json"),
                    Path.Combine(root, "workspace.json"),
                    initializeOnLoaded: false,
                    areaFilterProfilesDirectoryPath: areaDirectory,
                    displayLanguageState: language);
                window.Show();
                Assert.IsAssignableFrom<ButtonBase>(window.FindName("AreaFilterNavigationItem"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.UpdateLayout();

                var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
                list.SelectedItem = list.Items.Cast<WatchAreaFilterProfilePresentationRow>()
                    .Single(row => row.ProfileName == "line-a");
                var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
                editor.Text += "AREA-C\n";
                editor.Select(9, 4);
                var lastWriteBefore = File.GetLastWriteTimeUtc(profilePath);

                language.ApplyCommitted(WatchDisplayLanguage.English);
                window.UpdateLayout();

                Assert.Equal("line-a", Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem).ProfileName);
                Assert.Equal("# draft\nAREA-A\nAREA-B\nAREA-C\n", editor.Text);
                Assert.Equal(9, editor.SelectionStart);
                Assert.Equal(4, editor.SelectionLength);
                Assert.Equal(lastWriteBefore, File.GetLastWriteTimeUtc(profilePath));
                Assert.Equal(
                    "AREA filter profiles",
                    Assert.IsAssignableFrom<TextBlock>(window.FindName("AreaProfilePageTitleText")).Text);
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-bilingual-area-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static bool ContainsHan(string value) => value.Any(character =>
        character is >= '\u3400' and <= '\u9fff');

    private sealed class TestDirectoryLauncher : IWatchAreaProfileDirectoryLauncher
    {
        public WatchAreaProfileDirectoryOpenDisposition Open(string directoryPath) =>
            WatchAreaProfileDirectoryOpenDisposition.SuppressedForUiTest;
    }
}
