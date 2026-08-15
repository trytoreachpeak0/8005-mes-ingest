using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchTicket21AreaAndResponsiveIntegrationTests
{
    [Fact]
    public void Area_directory_launcher_creates_only_the_requested_directory_and_suppresses_shell_in_ui_test_mode()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"watch-ticket-21-directory-launcher-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "exact-area-filters");
        var shellStartCount = 0;
        try
        {
            var launcher = new WatchAreaProfileDirectoryLauncher(
                variable => variable == "MESINGEST_WATCH_UI_TEST_MODE" ? "1" : null,
                _ => shellStartCount++);

            var disposition = launcher.Open(directory);

            Assert.Equal(
                WatchAreaProfileDirectoryOpenDisposition.SuppressedForUiTest,
                disposition);
            Assert.True(Directory.Exists(directory));
            Assert.Equal(0, shellStartCount);
            Assert.Single(Directory.GetDirectories(root));
            Assert.Equal(
                Path.GetFullPath(directory),
                Path.GetFullPath(Directory.GetDirectories(root)[0]));
            Assert.Equal(
                $"1{Environment.NewLine}2{Environment.NewLine}3",
                WatchWorkspaceWindow.FormatAreaProfileLineNumbers("A1-1\r\nA1-2\rA1-3"));

            System.Diagnostics.ProcessStartInfo? startInfo = null;
            var platformDirectory = Path.Combine(root, "platform-shell-area-filters");
            var platformLauncher = new WatchAreaProfileDirectoryLauncher(
                _ => null,
                requestedStartInfo => startInfo = requestedStartInfo);
            Assert.Equal(
                WatchAreaProfileDirectoryOpenDisposition.Opened,
                platformLauncher.Open(platformDirectory));
            Assert.NotNull(startInfo);
            Assert.Equal(Path.GetFullPath(platformDirectory), startInfo.FileName);
            Assert.True(startInfo.UseShellExecute);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Corrupt_applied_marker_falls_back_with_an_accessible_warning_instead_of_silent_scope_widening()
    {
        using var files = new TemporaryWatchFiles("损坏标记", "A1-1\n");
        File.WriteAllText(files.ActiveMarkerPath, "not json", new UTF8Encoding(false));

        await RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath,
                areaFilterProfilesDirectoryPath: files.AreaProfilesPath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                Assert.Empty(window.AreaContext.MesAreas);
                var warning = Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar");
                Assert.True(warning.IsOpen);
                Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Warning, warning.Severity);
                Assert.Equal("无法恢复上次 AREA 配置", warning.Title);
                Assert.Contains("已回退为全部 AREA", warning.Message, StringComparison.Ordinal);
                Assert.Contains(
                    "回退",
                    AutomationProperties.GetName(warning),
                    StringComparison.Ordinal);
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Selected_and_saved_area_draft_stays_local_until_apply_refreshes_only_the_three_area_scoped_views()
    {
        const string credential = "ticket-21-area-secret";
        var initialOverviewReceived = new TaskCompletionSource<WatchOverviewQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var overviewReceived = new TaskCompletionSource<WatchOverviewQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var demandSeriesReceived = new TaskCompletionSource<DemandSeriesBrowseQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readabilityReceived = new TaskCompletionSource<ReadabilityAuditQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-21-area", credential)
            {
                Overview = FakeHostReply.Select<WatchOverviewQuery, WatchOverviewSnapshot>(query =>
                {
                    if (query.MesAreas is ["A1-1"])
                    {
                        initialOverviewReceived.TrySetResult(query);
                    }
                    else if (query.MesAreas is ["B2-2", "C3-3"])
                    {
                        overviewReceived.TrySetResult(query);
                    }

                    return FakeHostReply.Return(CreateOverview(query.MesAreas ?? []));
                }),
                DemandSeries = FakeHostReply.Select<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>(query =>
                {
                    demandSeriesReceived.TrySetResult(query);
                    return FakeHostReply.Return(CreateEmptyDemandSeries(query));
                }),
                ReadabilityAudit = FakeHostReply.Select<ReadabilityAuditQuery, ReadabilityAuditListSnapshot>(query =>
                {
                    readabilityReceived.TrySetResult(query);
                    return FakeHostReply.Return(CreateEmptyReadabilityAudit(query));
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles("封装东区", "A1-1\n");
        Assert.True(new WatchAreaFilterProfileStore(files.AreaProfilesPath)
            .Apply("封装东区")
            .Applied);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl, credential),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath,
                areaFilterProfilesDirectoryPath: files.AreaProfilesPath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                Assert.Equal(
                    ["A1-1"],
                    (await initialOverviewReceived.Task.WaitAsync(timeout.Token)).MesAreas);
                var demandAreaSelector = Find<ComboBox>(
                    window,
                    "DemandSeriesAreaProfileSelector");
                var auditAreaSelector = Find<ComboBox>(
                    window,
                    "ReadabilityAreaProfileSelector");
                Assert.Same(demandAreaSelector.ItemsSource, auditAreaSelector.ItemsSource);
                Assert.Equal(1, demandAreaSelector.SelectedIndex);
                Assert.Equal(1, auditAreaSelector.SelectedIndex);
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));

                var profiles = Find<ListBox>(window, "AreaProfileList");
                profiles.SelectedIndex = 0;
                Assert.Equal(WatchWorkspacePage.AreaFilter, window.ActivePage);
                Assert.Equal(["A1-1"], window.AreaContext.MesAreas);
                Assert.Equal("封装东区", window.AreaContext.ProfileName);

                var editor = Find<TextBox>(window, "AreaProfileEditor");
                const string savedContent = "C3-3\nB2-2\n";
                editor.Text = savedContent;
                Assert.Equal(["A1-1"], window.AreaContext.MesAreas);
                Assert.Equal("草稿未保存", Find<TextBlock>(window, "AreaProfileDiskStateText").Text);

                var beforeSave = host.Timeline.Count;
                Click(Find<ButtonBase>(window, "AreaProfileSaveButton"));
                await window.AreaProfileOperationTask.WaitAsync(timeout.Token);

                Assert.Equal(savedContent, File.ReadAllText(files.ProfilePath, Encoding.UTF8));
                Assert.True(File.Exists(files.ActiveMarkerPath));
                Assert.Equal(
                    ["A1-1"],
                    new WatchAreaFilterProfileStore(files.AreaProfilesPath)
                        .LoadApplied()
                        .MesAreas);
                Assert.Equal(["A1-1"], window.AreaContext.MesAreas);
                Assert.Contains(
                    "当前应用：封装东区 · 1 个 AREA",
                    Find<TextBlock>(window, "AreaProfileAppliedStateText").Text,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    host.Timeline.Skip(beforeSave),
                    entry => IsAreaSensitiveOperation(entry.Operation));

                var beforeApply = host.Timeline.Count;
                Click(Find<ButtonBase>(window, "AreaProfileApplyButton"));
                await window.AreaProfileOperationTask.WaitAsync(timeout.Token);

                var overviewQuery = await overviewReceived.Task.WaitAsync(timeout.Token);
                var demandSeriesQuery = await demandSeriesReceived.Task.WaitAsync(timeout.Token);
                var readabilityQuery = await readabilityReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(["B2-2", "C3-3"], overviewQuery.MesAreas);
                Assert.Equal(1, demandSeriesQuery.PageNumber);
                Assert.Null(demandSeriesQuery.SnapshotReference);
                Assert.Null(demandSeriesQuery.Cursor);
                Assert.Equal(["B2-2", "C3-3"], demandSeriesQuery.Filter.MesAreas);
                Assert.Equal(1, readabilityQuery.PageNumber);
                Assert.Null(readabilityQuery.SnapshotReference);
                Assert.Null(readabilityQuery.Cursor);
                Assert.Equal(["B2-2", "C3-3"], readabilityQuery.Filter.MesAreas);
                Assert.Equal(["B2-2", "C3-3"], window.AreaContext.MesAreas);
                Assert.Equal("封装东区", window.AreaContext.ProfileName);
                Assert.True(File.Exists(files.ActiveMarkerPath));

                var appliedTimeline = host.Timeline.Skip(beforeApply).ToArray();
                foreach (var operation in new[]
                         {
                             FakeHostOperation.OverviewV2,
                             FakeHostOperation.DemandSeriesV2,
                             FakeHostOperation.ReadabilityAuditV2,
                         })
                {
                    Assert.Single(appliedTimeline, entry =>
                        entry.Operation == operation
                        && entry.State == FakeHostRequestState.Started);
                    Assert.Single(appliedTimeline, entry =>
                        entry.Operation == operation
                        && entry.State == FakeHostRequestState.Completed);
                }

                Assert.DoesNotContain(appliedTimeline, entry =>
                    entry.Operation is FakeHostOperation.ErrorSearchV2
                        or FakeHostOperation.CurrentAttentionV2);

                editor.Text = "A1-1\nA1-1\na1-1\n";
                var apply = Find<ButtonBase>(window, "AreaProfileApplyButton");
                var diagnostics = Find<DataGrid>(window, "AreaProfileValidationGrid")
                    .Items
                    .Cast<WatchAreaFilterProfileDiagnostic>()
                    .ToArray();
                Assert.False(apply.IsEnabled);
                Assert.Contains(
                    diagnostics,
                    diagnostic => diagnostic.Code
                        == WatchAreaFilterProfileDiagnosticCodes.DuplicateMesArea);
                Assert.Contains(
                    diagnostics,
                    diagnostic => diagnostic.Code
                        == WatchAreaFilterProfileDiagnosticCodes.InvalidMesArea);
                Assert.Contains(
                    "非法草稿不可应用",
                    Find<TextBlock>(window, "AreaProfileValidationSummaryText").Text,
                    StringComparison.Ordinal);
                Assert.Equal(["B2-2", "C3-3"], window.AreaContext.MesAreas);

                var beforeSelectorApply = host.Timeline.Count;
                demandAreaSelector.SelectedIndex = 0;
                await window.AreaProfileOperationTask.WaitAsync(timeout.Token);

                Assert.Empty(window.AreaContext.MesAreas);
                Assert.Equal("全部 AREA", window.AreaContext.ProfileName);
                Assert.Equal(0, demandAreaSelector.SelectedIndex);
                Assert.Equal(0, auditAreaSelector.SelectedIndex);
                Assert.Empty(
                    new WatchAreaFilterProfileStore(files.AreaProfilesPath)
                        .LoadApplied()
                        .MesAreas);
                var selectorTimeline = host.Timeline.Skip(beforeSelectorApply).ToArray();
                foreach (var operation in new[]
                         {
                             FakeHostOperation.OverviewV2,
                             FakeHostOperation.DemandSeriesV2,
                             FakeHostOperation.ReadabilityAuditV2,
                         })
                {
                    Assert.Single(selectorTimeline, entry =>
                        entry.Operation == operation
                        && entry.State == FakeHostRequestState.Started);
                    Assert.Single(selectorTimeline, entry =>
                        entry.Operation == operation
                        && entry.State == FakeHostRequestState.Completed);
                }

                Assert.DoesNotContain(selectorTimeline, entry =>
                    entry.Operation is FakeHostOperation.ErrorSearchV2
                        or FakeHostOperation.CurrentAttentionV2);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Area_variant_a_commands_search_real_profile_rows_and_preserve_the_selected_draft_on_reload()
    {
        var weldingAreas = string.Join(
            '\n',
            Enumerable.Range(1, 24).Select(index =>
                $"W{((index - 1) / 8) + 1}-{((index - 1) % 8) + 1}"));
        using var files = new TemporaryWatchFiles("东区", "A1-1\nA1-2");
        files.WriteProfile("西区", "D1-1");
        files.WriteProfile("焊线区域", weldingAreas);
        files.WriteProfile("临时范围", "A1-1\nAREA-INVALID");
        Assert.True(new WatchAreaFilterProfileStore(files.AreaProfilesPath)
            .Apply("东区")
            .Applied);
        var directoryLauncher = new RecordingAreaProfileDirectoryLauncher();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath,
                areaFilterProfilesDirectoryPath: files.AreaProfilesPath,
                areaProfileDirectoryLauncher: directoryLauncher);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));
                var profileList = Find<ListBox>(window, "AreaProfileList");
                var rows = profileList.Items
                    .Cast<WatchAreaFilterProfilePresentationRow>()
                    .ToArray();
                Assert.Equal(4, rows.Length);
                Assert.Contains(
                    "4 个文件 · 1 个需要修复",
                    Find<TextBlock>(window, "AreaProfileListSummaryText").Text,
                    StringComparison.Ordinal);

                var east = Assert.Single(rows, row => row.ProfileName == "东区");
                Assert.Equal(2, east.MesAreaCount);
                Assert.True(east.IsValid);
                Assert.True(east.IsApplied);
                Assert.Equal("当前应用", east.StatusText);
                Assert.NotEqual(default, east.LastModifiedAt);
                Assert.Contains("2 个 AREA", east.MetadataText, StringComparison.Ordinal);

                var west = Assert.Single(rows, row => row.ProfileName == "西区");
                Assert.Equal(1, west.MesAreaCount);
                Assert.True(west.IsValid);
                Assert.False(west.IsApplied);
                Assert.Equal("有效", west.StatusText);

                var invalid = Assert.Single(rows, row => row.ProfileName == "临时范围");
                Assert.Equal(1, invalid.MesAreaCount);
                Assert.Equal(1, invalid.DiagnosticCount);
                Assert.False(invalid.IsValid);
                Assert.False(invalid.IsApplied);
                Assert.Equal("无效", invalid.StatusText);

                var welding = Assert.Single(rows, row => row.ProfileName == "焊线区域");
                Assert.Equal(24, welding.MesAreaCount);
                profileList.SelectedItem = welding;
                var expectedLineNumbers = string.Join(
                    Environment.NewLine,
                    Enumerable.Range(1, 24));
                Assert.Equal(
                    expectedLineNumbers,
                    Find<TextBlock>(window, "AreaProfileLineNumbersText").Text);

                var editor = Find<TextBox>(window, "AreaProfileEditor");
                editor.Text += "\n# 未保存的本机草稿";
                var unsavedDraft = editor.Text;
                Assert.Equal(
                    "草稿未保存",
                    Find<TextBlock>(window, "AreaProfileDiskStateText").Text);

                var search = Find<TextBox>(window, "AreaProfileSearchInput");
                search.Text = "西区";
                var searchedValid = Assert.Single(
                    profileList.Items.Cast<WatchAreaFilterProfilePresentationRow>());
                Assert.Equal("西区", searchedValid.ProfileName);
                Assert.True(searchedValid.IsValid);
                Assert.Equal(unsavedDraft, editor.Text);

                search.Text = "临时";
                var searchedInvalid = Assert.Single(
                    profileList.Items.Cast<WatchAreaFilterProfilePresentationRow>());
                Assert.Equal("临时范围", searchedInvalid.ProfileName);
                Assert.False(searchedInvalid.IsValid);
                Assert.Equal(unsavedDraft, editor.Text);

                files.WriteProfile("西区", "D1-1\nD1-2");
                Click(Find<ButtonBase>(window, "AreaProfileReloadButton"));
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Assert.Equal(unsavedDraft, editor.Text);
                Assert.Equal(
                    "AREA 配置已重新加载",
                    Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar").Title);

                search.Clear();
                rows = profileList.Items
                    .Cast<WatchAreaFilterProfilePresentationRow>()
                    .ToArray();
                Assert.Equal(4, rows.Length);
                Assert.Equal(
                    "焊线区域",
                    Assert.IsType<WatchAreaFilterProfilePresentationRow>(
                        profileList.SelectedItem).ProfileName);
                Assert.Equal(2, Assert.Single(rows, row => row.ProfileName == "西区").MesAreaCount);
                Assert.Equal(unsavedDraft, editor.Text);

                Click(Find<ButtonBase>(window, "AreaProfileOpenDirectoryButton"));
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Assert.Equal(
                    [Path.GetFullPath(files.AreaProfilesPath)],
                    directoryLauncher.RequestedDirectories);
                var info = Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar");
                Assert.Equal("AREA 配置目录已准备", info.Title);
                Assert.Contains("未启动文件管理器", info.Message, StringComparison.Ordinal);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Area_variant_a_real_window_restores_editor_hierarchy_and_discards_only_the_unsaved_draft()
    {
        const string appliedContent = "A1-1\nA1-2\n";
        const string savedOtherContent = "B2-2\n";
        using var files = new TemporaryWatchFiles("东区", appliedContent);
        files.WriteProfile("西区", savedOtherContent);
        files.WriteProfile("临时范围", "AREA-INVALID\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Apply("东区").Applied);

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath,
                areaFilterProfilesDirectoryPath: files.AreaProfilesPath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Show();
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));
                window.UpdateLayout();

                Assert.Equal(new Thickness(0), Find<Grid>(window, "AreaFilterLayoutGrid").Margin);
                Assert.Equal("东区.txt", Find<TextBlock>(window, "AreaProfileFileTitleText").Text);
                Assert.Contains(
                    "UTF-8",
                    Find<TextBlock>(window, "AreaProfileDirectoryText").Text,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "每行一个",
                    Find<TextBlock>(window, "AreaProfileRulesText").Text,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "2 个有效 AREA",
                    Find<TextBlock>(window, "AreaProfileValidCountText").Text,
                    StringComparison.Ordinal);
                Assert.Same(
                    window.FindResource("StatusPillSuccess"),
                    Find<Border>(window, "AreaProfileValidCountPill").Style);
                Assert.Same(
                    window.FindResource("CaptionText"),
                    Find<Wpf.Ui.Controls.TextBlock>(window, "AreaProfileDirectoryText").Style);
                Assert.Equal(
                    2,
                    Grid.GetRow(Find<Border>(window, "AreaProfileEditorFrame")));
                Assert.Equal(
                    4,
                    Grid.GetRow(Find<Grid>(window, "AreaProfileEditorStatusGrid")));
                Assert.Equal(
                    4,
                    Grid.GetRow(Find<Grid>(window, "AreaProfileAppliedCommandRow")));

                var openFileDirectory = Find<ButtonBase>(window, "AreaProfileFileOpenDirectoryButton");
                var reloadFile = Find<ButtonBase>(window, "AreaProfileFileReloadButton");
                AssertInteractiveAutomation(
                    openFileDirectory,
                    "AreaProfileFileOpenDirectoryButton",
                    "打开当前 AREA TXT 所在目录");
                AssertInteractiveAutomation(
                    reloadFile,
                    "AreaProfileFileReloadButton",
                    "从磁盘重新加载当前 AREA TXT");

                var discard = Find<ButtonBase>(window, "AreaProfileDiscardButton");
                var save = Find<ButtonBase>(window, "AreaProfileSaveButton");
                var apply = Find<ButtonBase>(window, "AreaProfileApplyButton");
                Assert.False(discard.IsEnabled);
                Assert.False(save.IsEnabled);
                Assert.False(apply.IsEnabled);

                var rows = Find<ListBox>(window, "AreaProfileList")
                    .Items
                    .Cast<WatchAreaFilterProfilePresentationRow>()
                    .ToArray();
                var profileList = Find<ListBox>(window, "AreaProfileList");
                var appliedRow = Assert.Single(rows, row => row.ProfileName == "东区");
                var invalidRow = Assert.Single(rows, row => row.ProfileName == "临时范围");
                var appliedContainer = Assert.IsType<ListBoxItem>(
                    profileList.ItemContainerGenerator.ContainerFromItem(appliedRow));
                var invalidContainer = Assert.IsType<ListBoxItem>(
                    profileList.ItemContainerGenerator.ContainerFromItem(invalidRow));
                Assert.NotNull(FindVisualDescendant<Border>(
                    appliedContainer,
                    border => border.Visibility == Visibility.Visible
                        && ReferenceEquals(
                            border.Style,
                            window.FindResource("StatusPillAccent"))));
                Assert.NotNull(FindVisualDescendant<Border>(
                    invalidContainer,
                    border => border.Visibility == Visibility.Visible
                        && ReferenceEquals(
                            border.Style,
                            window.FindResource("StatusPillCritical"))));
                Assert.NotNull(FindVisualDescendant<Wpf.Ui.Controls.TextBlock>(
                    appliedContainer,
                    text => ReferenceEquals(
                            text.Style,
                            window.FindResource("CaptionText"))
                        && text.Text.Contains("AREA", StringComparison.Ordinal)));
                profileList.SelectedItem = Assert.Single(
                    rows,
                    row => row.ProfileName == "西区");
                Assert.Equal("西区.txt", Find<TextBlock>(window, "AreaProfileFileTitleText").Text);
                Assert.False(discard.IsEnabled);
                Assert.False(save.IsEnabled);
                Assert.True(apply.IsEnabled);

                var editor = Find<TextBox>(window, "AreaProfileEditor");
                editor.Text = "C3-3\n";
                Assert.True(discard.IsEnabled);
                Assert.True(save.IsEnabled);
                Assert.True(apply.IsEnabled);
                Assert.Equal("草稿未保存", Find<TextBlock>(window, "AreaProfileDiskStateText").Text);

                var markerBeforeDiscard = File.ReadAllText(files.ActiveMarkerPath, Encoding.UTF8);
                Click(discard);
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);

                Assert.Equal(savedOtherContent, editor.Text);
                Assert.Equal(markerBeforeDiscard, File.ReadAllText(files.ActiveMarkerPath, Encoding.UTF8));
                Assert.Equal("东区", store.LoadApplied().ProfileName);
                Assert.False(discard.IsEnabled);
                Assert.False(save.IsEnabled);
                Assert.True(apply.IsEnabled);

                editor.Text = "AREA-INVALID\n";
                Assert.True(discard.IsEnabled);
                Assert.False(save.IsEnabled);
                Assert.False(apply.IsEnabled);
                Assert.Same(
                    window.FindResource("StatusPillCritical"),
                    Find<Border>(window, "AreaProfileValidCountPill").Style);
                Assert.Contains(
                    "无效",
                    Find<TextBlock>(window, "AreaProfileValidCountText").Text,
                    StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Readability_and_area_pages_expose_stable_automation_landmarks_and_stack_their_detail_work_below_master_at_720_epx()
    {
        using var files = new TemporaryWatchFiles("窄屏配置", "A1-1\n");

        await RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath,
                areaFilterProfilesDirectoryPath: files.AreaProfilesPath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ReadabilityAudit,
                    PageNumber: 1,
                    Cursor: null));
                window.Show();
                window.Width = 720;
                window.Height = 900;
                window.UpdateLayout();

                var readabilityPage = Find<ScrollViewer>(window, "ReadabilityAuditPage");
                Assert.Equal(Visibility.Visible, readabilityPage.Visibility);
                Assert.Equal(ScrollBarVisibility.Disabled, readabilityPage.HorizontalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Auto, readabilityPage.VerticalScrollBarVisibility);
                AssertAutomation(
                    readabilityPage,
                    "ReadabilityAuditPage",
                    "资格审计页面");

                var readabilityMaster = Find<Wpf.Ui.Controls.Card>(window, "ReadabilityMasterCard");
                var readabilityDetail = Find<Wpf.Ui.Controls.Card>(window, "ReadabilityDetailCard");
                var readabilityDetailRegion = Find<Grid>(window, "ReadabilityDetailRegion");
                AssertAutomation(
                    readabilityMaster,
                    "ReadabilityMasterCard",
                    "资格审计主列表与精确分面");
                AssertAutomation(
                    readabilityDetail,
                    "ReadabilityDetailCard",
                    "资格审计详情与证据");
                Assert.Equal(0, Grid.GetColumn(readabilityMaster));
                Assert.Equal(0, Grid.GetRow(readabilityMaster));
                Assert.Equal(0, Grid.GetColumn(readabilityDetailRegion));
                Assert.Equal(2, Grid.GetRow(readabilityDetailRegion));
                Assert.True(Find<RowDefinition>(window, "ReadabilityBodyBottomRow").Height.IsAuto);
                Assert.Equal(0, Find<ColumnDefinition>(window, "ReadabilityDetailColumn").Width.Value);
                Assert.InRange(readabilityMaster.ActualWidth, 1, readabilityPage.ActualWidth);
                Assert.InRange(readabilityDetail.ActualWidth, 1, readabilityPage.ActualWidth);

                var readabilityInputs = new (string Name, Type Type, string AutomationName)[]
                {
                    ("ReadabilityStateAllButton", typeof(Wpf.Ui.Controls.Button), "资格：全部"),
                    ("ReadabilityStateReadableButton", typeof(Wpf.Ui.Controls.Button), "资格：外部可见"),
                    ("ReadabilityStateNotReadableButton", typeof(Wpf.Ui.Controls.Button), "资格：外部不可见"),
                    ("ReadabilityWorkTypeFilter", typeof(ComboBox), "资格审计 WorkType 筛选"),
                    ("ReadabilityBlockerFilter", typeof(ComboBox), "资格阻断原因筛选"),
                    ("ReadabilityDemandIdFilter", typeof(TextBox), "资格审计 DemandId 精确筛选"),
                    ("ReadabilitySublotFilter", typeof(TextBox), "资格审计 SUBLOT 包含筛选"),
                    ("ReadabilityPageSizeInput", typeof(ComboBox), "资格审计每页数量"),
                    ("ReadabilityPageNumberInput", typeof(TextBox), "资格审计目标页码"),
                };
                foreach (var (name, type, automationName) in readabilityInputs)
                {
                    var control = Assert.IsAssignableFrom<Control>(window.FindName(name));
                    Assert.True(type.IsInstanceOfType(control), $"{name} must be a {type.Name}");
                    AssertInteractiveAutomation(control, name, automationName);
                }

                var readabilityCommands = new Dictionary<string, string>
                {
                    ["ReadabilityApplyFilterButton"] = "应用资格审计条件",
                    ["ReadabilityClearFilterButton"] = "清除资格审计条件",
                    ["ReadabilityPreviousPageButton"] = "资格审计上一页",
                    ["ReadabilityNextPageButton"] = "资格审计下一页",
                    ["ReadabilityGoToPageButton"] = "资格审计直接页码跳转",
                    ["ReadabilityOpenSeriesButton"] = "从资格审计查看所属需求系列",
                };
                foreach (var (name, automationName) in readabilityCommands)
                {
                    var command = Find<ButtonBase>(window, name);
                    AssertInteractiveAutomation(command, name, automationName);
                    AssertHorizontallyDiscoverable(command, readabilityPage);
                }

                var clearFilters = Find<ButtonBase>(window, "ReadabilityClearFilterButton");
                Assert.False(clearFilters.IsEnabled);
                var demandIdDraft = Find<TextBox>(window, "ReadabilityDemandIdFilter");
                demandIdDraft.Text = "demand-draft";
                Assert.True(clearFilters.IsEnabled);
                demandIdDraft.Clear();
                Assert.False(clearFilters.IsEnabled);

                var readabilityGrids = new Dictionary<string, string>
                {
                    ["ReadabilityStateFacetGrid"] = "资格状态 Host 精确分面",
                    ["ReadabilityBlockerFacetGrid"] = "阻断原因 Host 精确去重分面",
                    ["ReadabilityAuditGrid"] = "资格审计 Demand 世代列表",
                    ["ReadabilityQualificationGrid"] = "资格审计全部资格检查",
                    ["ReadabilityBlockerEvidenceGrid"] = "资格审计全部阻断证据",
                    ["ReadabilityRawObservationGrid"] = "资格审计全部原始观测",
                };
                foreach (var (name, automationName) in readabilityGrids)
                {
                    var grid = Find<DataGrid>(window, name);
                    AssertInteractiveAutomation(grid, name, automationName);
                    Assert.True(grid.IsReadOnly);
                    Assert.False(grid.AutoGenerateColumns);
                }

                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));
                window.UpdateLayout();

                var areaPage = Find<ScrollViewer>(window, "AreaFilterPage");
                Assert.Equal(Visibility.Visible, areaPage.Visibility);
                Assert.Equal(ScrollBarVisibility.Disabled, areaPage.HorizontalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Auto, areaPage.VerticalScrollBarVisibility);
                AssertAutomation(areaPage, "AreaFilterPage", "AREA 筛选页面");

                var areaMaster = Find<Wpf.Ui.Controls.Card>(window, "AreaProfileMasterCard");
                var areaEditor = Find<Wpf.Ui.Controls.Card>(window, "AreaProfileEditorCard");
                AssertAutomation(areaMaster, "AreaProfileMasterCard", "AREA 配置主列表");
                AssertAutomation(areaEditor, "AreaProfileEditorCard", "AREA 配置编辑器");
                Assert.Equal(0, Grid.GetColumn(areaMaster));
                Assert.Equal(0, Grid.GetRow(areaMaster));
                Assert.Equal(0, Grid.GetColumn(areaEditor));
                Assert.Equal(2, Grid.GetRow(areaEditor));
                Assert.True(Find<RowDefinition>(window, "AreaProfileBottomRow").Height.IsAuto);
                Assert.Equal(0, Find<ColumnDefinition>(window, "AreaProfileEditorColumn").Width.Value);
                Assert.InRange(areaMaster.ActualWidth, 1, areaPage.ActualWidth);
                Assert.InRange(areaEditor.ActualWidth, 1, areaPage.ActualWidth);

                var areaInputs = new (string Name, Type Type, string AutomationName)[]
                {
                    ("AreaProfileList", typeof(ListBox), "本机命名 AREA 配置列表"),
                    ("AreaProfileNameInput", typeof(TextBox), "AREA 配置名称"),
                    ("AreaProfileEditor", typeof(TextBox), "AREA 配置 TXT 内容编辑器"),
                    ("AreaProfileValidationGrid", typeof(DataGrid), "AREA 配置逐项校验"),
                };
                foreach (var (name, type, automationName) in areaInputs)
                {
                    var control = Assert.IsAssignableFrom<Control>(window.FindName(name));
                    Assert.True(type.IsInstanceOfType(control), $"{name} must be a {type.Name}");
                    AssertInteractiveAutomation(control, name, automationName);
                }

                Find<TextBox>(window, "AreaProfileNameInput").Text = "窄屏配置";
                Find<TextBox>(window, "AreaProfileEditor").Text = "A1-1\nB2-2\n";
                window.UpdateLayout();
                var areaCommands = new Dictionary<string, string>
                {
                    ["AreaProfileNewButton"] = "新建 AREA 配置",
                    ["AreaApplyAllAreasButton"] = "应用全部 AREA 显示范围",
                    ["AreaProfileSaveButton"] = "保存 AREA TXT 配置",
                    ["AreaProfileApplyButton"] = "应用选中 AREA 配置",
                };
                foreach (var (name, automationName) in areaCommands)
                {
                    var command = Find<ButtonBase>(window, name);
                    AssertInteractiveAutomation(command, name, automationName);
                    Assert.True(command.IsEnabled, $"{name} should be operable for a valid draft");
                    AssertHorizontallyDiscoverable(command, areaPage);
                }
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    private static bool IsAreaSensitiveOperation(FakeHostOperation operation) =>
        operation is FakeHostOperation.OverviewV2
            or FakeHostOperation.DemandSeriesV2
            or FakeHostOperation.ReadabilityAuditV2
            or FakeHostOperation.ErrorSearchV2
            or FakeHostOperation.CurrentAttentionV2;

    private static WatchOptions CreateOptions(string baseUrl, string credential) => new()
    {
        BaseUrl = baseUrl,
        SharedSecret = credential,
        RequestTimeoutSeconds = 30,
        RenderingMode = WatchRenderingMode.SoftwareOnly,
    };

    private static WatchOverviewSnapshot CreateOverview(IReadOnlyList<string> areas)
    {
        var at = DateTimeOffset.Parse("2026-08-14T06:00:00Z");
        var identity = new OperationalSnapshotIdentity(
            "commit-ticket-21-overview",
            21,
            at,
            "poll-ticket-21-overview",
            21,
            21,
            at);
        var series = new OverviewNavigationIntent(
            OverviewNavigationTargets.DemandSeries,
            MesAreas: areas);
        var audit = new OverviewNavigationIntent(
            OverviewNavigationTargets.ReadabilityAudit,
            MesAreas: areas);
        var errors = new OverviewNavigationIntent(
            OverviewNavigationTargets.ErrorSearch,
            ErrorWindow: ErrorSearchWindowKinds.Last7Days);
        var attention = new OverviewNavigationIntent(
            OverviewNavigationTargets.CurrentIngestAttention);
        return new WatchOverviewSnapshot(
            identity,
            areas,
            new WatchOverviewSeriesSummary(0, 0, 0, 0, 0, series, series, series, series, series),
            new WatchOverviewReadabilitySummary(0, 0, 0, audit, audit, audit),
            new WatchOverviewErrorSummary(0, 0, errors, errors, errors),
            new WatchOverviewAttentionSummary(0, [], [], attention),
            [],
            WatchOverviewRecentActivityStates.NoRecentHighlights,
            WatchOverviewRecentActivityStates.NoRecentHighlightsMessage);
    }

    private static DemandSeriesListSnapshot CreateEmptyDemandSeries(
        DemandSeriesBrowseQuery query)
    {
        var at = DateTimeOffset.Parse("2026-08-14T06:01:00Z");
        return new DemandSeriesListSnapshot(
            new DemandSeriesSnapshotIdentity(
                "commit-ticket-21-series",
                22,
                at,
                "poll-ticket-21-series"),
            "snapshot-ticket-21-series",
            query.Filter,
            query.Order,
            ExactTotalCount: 0,
            new DemandSeriesFacets(0, 0, 0, 0, 0),
            query.PageSize,
            query.PageNumber,
            TotalPages: 0,
            Items: [],
            NextCursor: null,
            HasMore: false);
    }

    private static ReadabilityAuditListSnapshot CreateEmptyReadabilityAudit(
        ReadabilityAuditQuery query)
    {
        var at = DateTimeOffset.Parse("2026-08-14T06:02:00Z");
        return new ReadabilityAuditListSnapshot(
            new ReadabilityAuditSnapshotIdentity(
                "commit-ticket-21-audit",
                23,
                at,
                "poll-ticket-21-audit",
                CatalogRevision: 21),
            "snapshot-ticket-21-audit",
            query.Filter,
            query.Order,
            ExactTotalDemandCount: 0,
            new ReadabilityAuditFacets([], []),
            query.PageSize,
            query.PageNumber,
            TotalPages: 0,
            Items: [],
            NextCursor: null,
            HasMore: false);
    }

    private static void Click(UIElement element) =>
        element.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void AssertAutomation(
        FrameworkElement element,
        string automationId,
        string automationName)
    {
        Assert.Equal(automationId, AutomationProperties.GetAutomationId(element));
        Assert.Equal(automationName, AutomationProperties.GetName(element));
    }

    private static void AssertInteractiveAutomation(
        Control control,
        string automationId,
        string automationName)
    {
        AssertAutomation(control, automationId, automationName);
        Assert.True(control.Focusable, $"{automationId} must accept keyboard focus");
        Assert.True(
            KeyboardNavigation.GetIsTabStop(control),
            $"{automationId} must participate in tab navigation");
    }

    private static void AssertHorizontallyDiscoverable(
        FrameworkElement element,
        FrameworkElement page)
    {
        Assert.True(element.IsVisible, $"{element.Name} must remain visible at 720 epx");
        Assert.True(element.ActualWidth > 0, $"{element.Name} must be measured at 720 epx");
        var origin = element.TranslatePoint(new Point(0, 0), page);
        if (origin.X < -0.5 || origin.X + element.ActualWidth > page.ActualWidth + 0.5)
        {
            element.BringIntoView();
            page.UpdateLayout();
            origin = element.TranslatePoint(new Point(0, 0), page);
        }

        Assert.True(origin.X >= -0.5, $"{element.Name} starts outside the page at {origin.X}");
        Assert.True(
            origin.X + element.ActualWidth <= page.ActualWidth + 0.5,
            $"{element.Name} ends outside the page at {origin.X + element.ActualWidth}");
    }

    private static T Find<T>(FrameworkElement root, string name)
        where T : class => Assert.IsAssignableFrom<T>(root.FindName(name));

    private static T? FindVisualDescendant<T>(
        DependencyObject root,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            if (FindVisualDescendant<T>(child, predicate) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static Task RunInStaDispatcherAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            _ = ExecuteAsync();
            Dispatcher.Run();
            if (failure is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(failure);
            }

            async Task ExecuteAsync()
            {
                try
                {
                    await action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Watch Ticket 21 AREA integration STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class RecordingAreaProfileDirectoryLauncher
        : IWatchAreaProfileDirectoryLauncher
    {
        public List<string> RequestedDirectories { get; } = [];

        public WatchAreaProfileDirectoryOpenDisposition Open(string directoryPath)
        {
            RequestedDirectories.Add(Path.GetFullPath(directoryPath));
            return WatchAreaProfileDirectoryOpenDisposition.SuppressedForUiTest;
        }
    }

    private sealed class TemporaryWatchFiles : IDisposable
    {
        public TemporaryWatchFiles(string profileName, string profileContent)
        {
            Root = Path.Combine(Path.GetTempPath(), $"watch-ticket-21-area-{Guid.NewGuid():N}");
            ConnectionPath = Path.Combine(Root, "connection.json");
            WorkspacePath = Path.Combine(Root, "workspace.json");
            AreaProfilesPath = Path.Combine(Root, "area-filters");
            ProfilePath = Path.Combine(AreaProfilesPath, $"{profileName}.txt");
            ActiveMarkerPath = Path.Combine(AreaProfilesPath, ".active-profile");
            Directory.CreateDirectory(AreaProfilesPath);
            WriteProfile(profileName, profileContent);
        }

        public string Root { get; }

        public string ConnectionPath { get; }

        public string WorkspacePath { get; }

        public string AreaProfilesPath { get; }

        public string ProfilePath { get; }

        public string ActiveMarkerPath { get; }

        public string WriteProfile(string profileName, string profileContent)
        {
            var path = Path.Combine(AreaProfilesPath, $"{profileName}.txt");
            File.WriteAllText(path, profileContent, new UTF8Encoding(false));
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
