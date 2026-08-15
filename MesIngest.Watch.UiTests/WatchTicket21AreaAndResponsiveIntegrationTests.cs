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
    public void Area_directory_caption_abbreviates_the_real_local_application_data_root()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        Assert.False(string.IsNullOrWhiteSpace(localApplicationData));
        var directory = Path.Combine(
            localApplicationData,
            "MesIngest.Watch",
            "area-filters");

        Assert.Equal(
            "%LocalAppData%\\MesIngest.Watch\\area-filters · UTF-8",
            WatchWorkspaceWindow.FormatAreaProfileDirectoryCaption(directory));
    }

    [Fact]
    public void Area_directory_caption_does_not_abbreviate_a_custom_path_that_only_has_the_canonical_suffix()
    {
        var nonLocalRoot = Path.Combine(
            Path.GetPathRoot(Environment.CurrentDirectory)
                ?? throw new InvalidOperationException("当前目录没有路径根。"),
            $"watch-ticket-21-non-local-{Guid.NewGuid():N}");
        var directory = Path.Combine(
            nonLocalRoot,
            "MesIngest.Watch",
            "area-filters");

        Assert.Equal(
            "area-filters · 本机 TXT · UTF-8",
            WatchWorkspaceWindow.FormatAreaProfileDirectoryCaption(directory));
    }

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

                var saveAsFile = Find<ButtonBase>(window, "AreaProfileSaveAsButton");
                var renameFile = Find<ButtonBase>(window, "AreaProfileRenameButton");
                var deleteFile = Find<ButtonBase>(window, "AreaProfileDeleteButton");
                var reloadFile = Find<ButtonBase>(window, "AreaProfileFileReloadButton");
                AssertInteractiveAutomation(
                    saveAsFile,
                    "AreaProfileSaveAsButton",
                    "另存当前 AREA TXT 配置");
                AssertInteractiveAutomation(
                    renameFile,
                    "AreaProfileRenameButton",
                    "重命名当前 AREA TXT 配置");
                AssertInteractiveAutomation(
                    deleteFile,
                    "AreaProfileDeleteButton",
                    "删除当前 AREA TXT 配置");
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
    public async Task Area_variant_a_real_window_exposes_real_file_commands_with_conflict_safety_and_delete_confirmation()
    {
        const string appliedContent = "A1-1\nA1-2\n";
        using var files = new TemporaryWatchFiles("东区", appliedContent);
        files.WriteProfile("西区", "B2-2\n");
        files.WriteProfile("焊线区域", "C3-3\n");
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
                window.Width = 1440;
                window.Height = 900;
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));
                window.UpdateLayout();

                var master = Find<Wpf.Ui.Controls.Card>(window, "AreaProfileMasterCard");
                var editor = Find<Wpf.Ui.Controls.Card>(window, "AreaProfileEditorCard");
                var body = Find<Grid>(window, "AreaProfileBodyGrid");
                Assert.Equal(318, master.ActualWidth, precision: 1);
                Assert.Equal(0, Grid.GetColumn(master));
                Assert.Equal(2, Grid.GetColumn(editor));
                var masterOrigin = master.TranslatePoint(new Point(0, 0), body);
                var editorOrigin = editor.TranslatePoint(new Point(0, 0), body);
                Assert.Equal(16, editorOrigin.X - masterOrigin.X - master.ActualWidth, precision: 1);

                var localScope = Find<TextBlock>(window, "AreaProfileLocalScopeText");
                Assert.Equal("仅影响本机当前用户的显示", localScope.Text);
                Assert.Equal(TextWrapping.NoWrap, localScope.TextWrapping);
                var allAreas = Find<Wpf.Ui.Controls.Button>(window, "AreaApplyAllAreasButton");
                Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Transparent, allAreas.Appearance);
                Assert.True(allAreas.ActualWidth < master.ActualWidth / 2);

                var saveAs = Find<Wpf.Ui.Controls.Button>(window, "AreaProfileSaveAsButton");
                var rename = Find<Wpf.Ui.Controls.Button>(window, "AreaProfileRenameButton");
                var delete = Find<Wpf.Ui.Controls.Button>(window, "AreaProfileDeleteButton");
                Assert.Equal("另存为", saveAs.Content);
                Assert.Equal("重命名", rename.Content);
                Assert.Equal("删除", delete.Content);
                Assert.True(saveAs.IsEnabled);
                Assert.True(rename.IsEnabled);
                Assert.True(delete.IsEnabled);
                var fileCommandLayer = Find<StackPanel>(
                    window,
                    "AreaProfileFileCommandLayer");
                Assert.Equal(1, Grid.GetColumn(fileCommandLayer));
                Assert.Equal(
                    new UIElement[] { saveAs, rename, delete },
                    fileCommandLayer.Children.Cast<UIElement>());
                Assert.Equal(
                    Visibility.Collapsed,
                    Find<FrameworkElement>(window, "AreaProfileFileOperationPanel").Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    Find<TextBox>(window, "AreaProfileNameInput").Visibility);

                var reloadFromDisk = Find<Wpf.Ui.Controls.Button>(
                    window,
                    "AreaProfileFileReloadButton");
                var saveAndApply = Find<Wpf.Ui.Controls.Button>(window, "AreaProfileApplyButton");
                Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Transparent, reloadFromDisk.Appearance);
                Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Secondary, saveAndApply.Appearance);
                Assert.Equal(
                    4,
                    Grid.GetRow(Find<Grid>(window, "AreaProfileEditorStatusGrid")));
                var discard = Find<Wpf.Ui.Controls.Button>(window, "AreaProfileDiscardButton");
                var save = Find<Wpf.Ui.Controls.Button>(window, "AreaProfileSaveButton");
                var bottomCommandLayer = Assert.IsType<StackPanel>(discard.Parent);
                Assert.Equal(
                    new UIElement[] { discard, save },
                    bottomCommandLayer.Children.Cast<UIElement>());

                Click(saveAs);
                var operationPanel = Find<FrameworkElement>(
                    window,
                    "AreaProfileFileOperationPanel");
                var operationTarget = Find<TextBox>(window, "AreaProfileTargetNameInput");
                var operationConfirm = Find<ButtonBase>(
                    window,
                    "AreaProfileFileOperationConfirmButton");
                Assert.Equal(Visibility.Visible, operationPanel.Visibility);
                Assert.Equal("另存为", Find<TextBlock>(
                    window,
                    "AreaProfileFileOperationPromptText").Text);

                operationTarget.Text = "西区";
                Click(operationConfirm);
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                var conflictInfo = Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar");
                Assert.Equal("无法完成 AREA 配置操作", conflictInfo.Title);
                Assert.Contains(
                    WatchAreaFilterProfileDiagnosticCodes.ProfileAlreadyExists,
                    conflictInfo.Message,
                    StringComparison.Ordinal);
                Assert.Equal("B2-2\n", store.Load("西区").Content);
                Assert.Equal(Visibility.Visible, operationPanel.Visibility);

                operationTarget.Text = "东区副本";
                Click(operationConfirm);
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Assert.Equal(appliedContent, store.Load("东区副本").Content);
                Assert.Equal("东区副本.txt", Find<TextBlock>(window, "AreaProfileFileTitleText").Text);
                Assert.Equal(Visibility.Collapsed, operationPanel.Visibility);

                Click(rename);
                operationTarget.Text = "东区副本重命名";
                Click(operationConfirm);
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Assert.False(File.Exists(Path.Combine(files.AreaProfilesPath, "东区副本.txt")));
                Assert.Equal(appliedContent, store.Load("东区副本重命名").Content);
                Assert.Equal(
                    "东区副本重命名.txt",
                    Find<TextBlock>(window, "AreaProfileFileTitleText").Text);

                Click(delete);
                Assert.True(File.Exists(Path.Combine(files.AreaProfilesPath, "东区副本重命名.txt")));
                Assert.Equal(Visibility.Visible, operationPanel.Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    operationTarget.Visibility);
                Assert.Contains(
                    "再次确认",
                    Find<TextBlock>(window, "AreaProfileFileOperationPromptText").Text,
                    StringComparison.Ordinal);

                Click(operationConfirm);
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Assert.False(File.Exists(Path.Combine(files.AreaProfilesPath, "东区副本重命名.txt")));
                Assert.Equal(Visibility.Collapsed, operationPanel.Visibility);
                Assert.Equal("东区", store.LoadApplied().ProfileName);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Area_file_confirmation_freezes_its_source_and_closes_on_selector_draft_or_page_changes()
    {
        using var files = new TemporaryWatchFiles("ProfileA", "A1-1\n");
        files.WriteProfile("ProfileB", "B2-2\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Apply("ProfileA").Applied);

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
                var profileList = Find<ListBox>(window, "AreaProfileList");
                profileList.SelectedItem = Assert.Single(
                    profileList.Items.Cast<WatchAreaFilterProfilePresentationRow>(),
                    row => row.ProfileName == "ProfileA");

                Click(Find<ButtonBase>(window, "AreaProfileRenameButton"));
                var panel = Find<FrameworkElement>(window, "AreaProfileFileOperationPanel");
                var prompt = Find<TextBlock>(window, "AreaProfileFileOperationPromptText");
                var target = Find<TextBox>(window, "AreaProfileTargetNameInput");
                var confirm = Find<Wpf.Ui.Controls.Button>(
                    window,
                    "AreaProfileFileOperationConfirmButton");
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.Contains("ProfileA.txt", prompt.Text, StringComparison.Ordinal);
                Assert.Contains(
                    "ProfileA.txt",
                    AutomationProperties.GetName(panel),
                    StringComparison.Ordinal);
                Assert.Equal(
                    Wpf.Ui.Controls.ControlAppearance.Secondary,
                    confirm.Appearance);
                Assert.Equal(
                    "确认重命名 ProfileA.txt",
                    AutomationProperties.GetName(confirm));
                target.Text = "RenamedA";

                var demandSelector = Find<ComboBox>(
                    window,
                    "DemandSeriesAreaProfileSelector");
                demandSelector.SelectedItem = Assert.Single(
                    demandSelector.Items.Cast<WatchAreaProfileSelectorOption>(),
                    option => option.ProfileName == "ProfileB");
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Click(confirm);
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                Assert.True(File.Exists(Path.Combine(files.AreaProfilesPath, "ProfileA.txt")));
                Assert.True(File.Exists(Path.Combine(files.AreaProfilesPath, "ProfileB.txt")));
                Assert.False(File.Exists(Path.Combine(files.AreaProfilesPath, "RenamedA.txt")));

                profileList.SelectedItem = Assert.Single(
                    profileList.Items.Cast<WatchAreaFilterProfilePresentationRow>(),
                    row => row.ProfileName == "ProfileB");
                Click(Find<ButtonBase>(window, "AreaProfileDeleteButton"));
                Assert.Contains("ProfileB.txt", prompt.Text, StringComparison.Ordinal);
                Assert.Contains("概览、需求系列和资格审计", prompt.Text, StringComparison.Ordinal);
                Assert.Contains("全部 AREA", prompt.Text, StringComparison.Ordinal);
                Assert.Equal(
                    "确认删除 ProfileB.txt",
                    AutomationProperties.GetName(confirm));
                Assert.Equal(prompt.Text, AutomationProperties.GetHelpText(confirm));

                var editor = Find<TextBox>(window, "AreaProfileEditor");
                editor.Text = "B2-2\nC3-3\n";
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                Assert.Equal(string.Empty, AutomationProperties.GetHelpText(confirm));
                Click(confirm);
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Assert.Equal("B2-2\n", store.Load("ProfileB").Content);
                Assert.Equal("ProfileB", store.LoadApplied().ProfileName);

                Click(Find<ButtonBase>(window, "AreaProfileDiscardButton"));
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Click(Find<ButtonBase>(window, "AreaProfileDeleteButton"));
                Assert.Equal(Visibility.Visible, panel.Visibility);
                var settingsNavigation = Find<Wpf.Ui.Controls.NavigationViewItem>(
                    window,
                    "SettingsNavigationItem");
                settingsNavigation.Focus();
                Click(settingsNavigation);
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                Click(confirm);
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Assert.True(File.Exists(Path.Combine(files.AreaProfilesPath, "ProfileB.txt")));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task External_case_only_profile_rename_keeps_the_actual_filename_selected_and_current()
    {
        using var files = new TemporaryWatchFiles("ActiveScope", "A1-1\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Apply("ActiveScope").Applied);
        var transitPath = Path.Combine(files.AreaProfilesPath, "case-change.tmp");
        var actualPath = Path.Combine(files.AreaProfilesPath, "ACTIVESCOPE.txt");
        File.Move(files.ProfilePath, transitPath);
        File.Move(transitPath, actualPath);
        var markerBefore = File.ReadAllText(files.ActiveMarkerPath, Encoding.UTF8);

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

                var profileList = Find<ListBox>(window, "AreaProfileList");
                var row = Assert.Single(
                    profileList.Items.Cast<WatchAreaFilterProfilePresentationRow>());
                Assert.Equal("ACTIVESCOPE", row.ProfileName);
                Assert.True(row.IsApplied);
                Assert.Equal(
                    "ACTIVESCOPE",
                    Assert.IsType<WatchAreaFilterProfilePresentationRow>(
                        profileList.SelectedItem).ProfileName);
                Assert.Equal(
                    "ACTIVESCOPE.txt",
                    Find<TextBlock>(window, "AreaProfileFileTitleText").Text);

                var selector = Find<ComboBox>(window, "DemandSeriesAreaProfileSelector");
                var current = Assert.Single(
                    selector.Items.Cast<WatchAreaProfileSelectorOption>(),
                    option => option.ProfileName == "ACTIVESCOPE");
                Assert.Equal(
                    "ACTIVESCOPE",
                    Assert.IsType<WatchAreaProfileSelectorOption>(
                        selector.SelectedItem).ProfileName);
                selector.SelectedItem = null;
                selector.SelectedItem = current;
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);

                Assert.Equal(markerBefore, File.ReadAllText(files.ActiveMarkerPath, Encoding.UTF8));
                Assert.Equal("ActiveScope", store.LoadApplied().ProfileName);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("AreaProfileRenameButton")]
    [InlineData("AreaProfileDeleteButton")]
    public async Task Destructive_confirmation_refuses_an_unreadable_applied_marker(
        string commandName)
    {
        using var files = new TemporaryWatchFiles("ProtectedScope", "A1-1\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Apply("ProtectedScope").Applied);

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
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));
                File.WriteAllText(files.ActiveMarkerPath, "not json", new UTF8Encoding(false));

                Click(Find<ButtonBase>(window, commandName));

                Assert.Equal(
                    Visibility.Collapsed,
                    Find<FrameworkElement>(window, "AreaProfileFileOperationPanel").Visibility);
                var info = Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar");
                Assert.True(info.IsOpen);
                Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Error, info.Severity);
                Assert.Contains("无法确认", info.Title, StringComparison.Ordinal);
                Assert.Contains("明确应用全部 AREA", info.Message, StringComparison.Ordinal);
                Assert.True(File.Exists(files.ProfilePath));
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Inline_file_confirmation_restores_focus_after_cancel_and_success()
    {
        using var files = new TemporaryWatchFiles("FocusScope", "A1-1\n");

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
                Find<ListBox>(window, "AreaProfileList").SelectedIndex = 0;
                var rename = Find<Wpf.Ui.Controls.Button>(window, "AreaProfileRenameButton");
                var cancel = Find<Wpf.Ui.Controls.Button>(
                    window,
                    "AreaProfileFileOperationCancelButton");
                var confirm = Find<Wpf.Ui.Controls.Button>(
                    window,
                    "AreaProfileFileOperationConfirmButton");
                var target = Find<TextBox>(window, "AreaProfileTargetNameInput");

                var create = Find<Wpf.Ui.Controls.Button>(window, "AreaProfileNewButton");
                create.Focus();
                Click(create);
                cancel.Focus();
                Click(cancel);
                Assert.Same(create, Keyboard.FocusedElement);

                rename.Focus();
                Click(rename);
                cancel.Focus();
                Click(cancel);
                Assert.Same(rename, Keyboard.FocusedElement);

                Click(rename);
                target.Text = "RenamedFocusScope";
                confirm.Focus();
                Click(confirm);
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);

                Assert.Same(rename, Keyboard.FocusedElement);
                Assert.True(File.Exists(Path.Combine(
                    files.AreaProfilesPath,
                    "RenamedFocusScope.txt")));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Destructive_confirmation_reports_a_busy_profile_store_without_dispatcher_escape()
    {
        using var files = new TemporaryWatchFiles("BusyScope", "A1-1\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Apply("BusyScope").Applied);

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
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));
                using var transactionLock = new FileStream(
                    Path.Combine(files.AreaProfilesPath, ".area-profiles.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                Click(Find<ButtonBase>(window, "AreaProfileDeleteButton"));

                Assert.Equal(
                    Visibility.Collapsed,
                    Find<FrameworkElement>(window, "AreaProfileFileOperationPanel").Visibility);
                var info = Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar");
                Assert.True(info.IsOpen);
                Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Error, info.Severity);
                Assert.Contains("稍后重试", info.Message, StringComparison.Ordinal);
                Assert.True(File.Exists(files.ProfilePath));
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Delete_confirmation_discloses_and_honors_a_profile_applied_by_another_instance()
    {
        using var files = new TemporaryWatchFiles("DeleteTarget", "A1-1\n");
        files.WriteProfile("InitiallyApplied", "B2-2\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Apply("InitiallyApplied").Applied);

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
                var profiles = Find<ListBox>(window, "AreaProfileList");
                profiles.SelectedItem = Assert.Single(
                    profiles.Items.Cast<WatchAreaFilterProfilePresentationRow>(),
                    row => row.ProfileName == "DeleteTarget");
                Click(Find<ButtonBase>(window, "AreaProfileDeleteButton"));
                var prompt = Find<TextBlock>(window, "AreaProfileFileOperationPromptText");
                var confirm = Find<Wpf.Ui.Controls.Button>(
                    window,
                    "AreaProfileFileOperationConfirmButton");
                Assert.Contains("若确认时", prompt.Text, StringComparison.Ordinal);
                Assert.Contains("概览、需求系列和资格审计", prompt.Text, StringComparison.Ordinal);
                Assert.Equal(prompt.Text, AutomationProperties.GetHelpText(confirm));

                Assert.True(new WatchAreaFilterProfileStore(files.AreaProfilesPath)
                    .Apply("DeleteTarget")
                    .Applied);
                confirm.Focus();
                Click(confirm);
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);

                Assert.False(File.Exists(files.ProfilePath));
                Assert.True(store.LoadApplied().IsAllAreas);
                var focus = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
                Assert.Same(Find<ListBox>(window, "AreaProfileList"), focus);
                Assert.True(focus.IsVisible);
                Assert.True(focus.IsEnabled);
                Assert.Contains(
                    "回退到全部 AREA",
                    Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar").Message,
                    StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task New_profile_remains_bound_and_retryable_when_marker_apply_fails_after_save()
    {
        using var files = new TemporaryWatchFiles("ExistingScope", "A1-1\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Apply("ExistingScope").Applied);

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
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));
                Click(Find<ButtonBase>(window, "AreaProfileNewButton"));
                var target = Find<TextBox>(window, "AreaProfileTargetNameInput");
                target.Text = "RecoverableScope";
                Click(Find<ButtonBase>(window, "AreaProfileFileOperationConfirmButton"));
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Find<TextBox>(window, "AreaProfileEditor").Text = "B2-2\n";

                using (var markerLock = new FileStream(
                           files.ActiveMarkerPath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read))
                {
                    Click(Find<ButtonBase>(window, "AreaProfileApplyButton"));
                    await window.AreaProfileOperationTask.WaitAsync(
                        TimeSpan.FromSeconds(5),
                        TestContext.Current.CancellationToken);
                }

                Assert.True(File.Exists(Path.Combine(
                    files.AreaProfilesPath,
                    "RecoverableScope.txt")));
                Assert.Equal(
                    "RecoverableScope.txt",
                    Find<TextBlock>(window, "AreaProfileFileTitleText").Text);
                Assert.Equal(
                    "磁盘版本未变化",
                    Find<TextBlock>(window, "AreaProfileDiskStateText").Text);
                var failure = Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar");
                Assert.Contains("已保存", failure.Message, StringComparison.Ordinal);
                Assert.Contains("未应用", failure.Message, StringComparison.Ordinal);
                Assert.Equal("ExistingScope", store.LoadApplied().ProfileName);

                Click(Find<ButtonBase>(window, "AreaProfileApplyButton"));
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Assert.Equal("RecoverableScope", store.LoadApplied().ProfileName);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Selected_profile_remains_clean_and_retryable_when_marker_apply_fails_after_save()
    {
        using var files = new TemporaryWatchFiles("AppliedScope", "A1-1\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Save("EditableScope", "B2-2\n").Saved);
        Assert.True(store.Apply("AppliedScope").Applied);

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
                var profiles = Find<ListBox>(window, "AreaProfileList");
                profiles.SelectedItem = Assert.Single(
                    profiles.Items.Cast<WatchAreaFilterProfilePresentationRow>(),
                    row => row.ProfileName == "EditableScope");
                Find<TextBox>(window, "AreaProfileEditor").Text = "C3-3\nC3-4\n";

                using (var markerLock = new FileStream(
                           files.ActiveMarkerPath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read))
                {
                    Click(Find<ButtonBase>(window, "AreaProfileApplyButton"));
                    await window.AreaProfileOperationTask.WaitAsync(
                        TimeSpan.FromSeconds(5),
                        TestContext.Current.CancellationToken);
                }

                Assert.Equal("C3-3\nC3-4\n", store.Load("EditableScope").Content);
                Assert.Equal("AppliedScope", store.LoadApplied().ProfileName);
                Assert.Equal(
                    "EditableScope.txt",
                    Find<TextBlock>(window, "AreaProfileFileTitleText").Text);
                Assert.Equal(
                    "磁盘版本未变化",
                    Find<TextBlock>(window, "AreaProfileDiskStateText").Text);
                var failure = Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar");
                Assert.Equal("AREA 配置已保存但范围未应用", failure.Title);
                Assert.Contains("EditableScope.txt 已保存", failure.Message, StringComparison.Ordinal);
                Assert.Contains("范围未应用", failure.Message, StringComparison.Ordinal);
                Assert.True(Find<ButtonBase>(window, "AreaProfileApplyButton").IsEnabled);

                Click(Find<ButtonBase>(window, "AreaProfileApplyButton"));
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);

                Assert.Equal("EditableScope", store.LoadApplied().ProfileName);
                Assert.Equal(
                    "AREA 配置已应用",
                    Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar").Title);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_windows_preserve_the_external_txt_and_dirty_draft_on_stale_save_or_apply(
        bool saveAndApply)
    {
        using var files = new TemporaryWatchFiles("AppliedScope", "A1-1\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Save("EditableScope", "B2-2\n").Saved);
        Assert.True(store.Apply("AppliedScope").Applied);

        await RunInStaDispatcherAsync(async () =>
        {
            using var firstComposition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath,
                areaFilterProfilesDirectoryPath: files.AreaProfilesPath);
            using var secondComposition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath,
                areaFilterProfilesDirectoryPath: files.AreaProfilesPath);
            var firstWindow = firstComposition.CreateMainWindow(initializeOnLoaded: false);
            var secondWindow = secondComposition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                firstWindow.Show();
                secondWindow.Show();
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(
                    firstWindow,
                    "AreaFilterNavigationItem"));
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(
                    secondWindow,
                    "AreaFilterNavigationItem"));
                SelectProfile(firstWindow, "EditableScope");
                SelectProfile(secondWindow, "EditableScope");

                Find<TextBox>(firstWindow, "AreaProfileEditor").Text = "D4-4\n";
                Find<TextBox>(secondWindow, "AreaProfileEditor").Text = "C3-3\n";
                Click(Find<ButtonBase>(secondWindow, "AreaProfileSaveButton"));
                await secondWindow.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);

                Click(Find<ButtonBase>(
                    firstWindow,
                    saveAndApply ? "AreaProfileApplyButton" : "AreaProfileSaveButton"));
                await firstWindow.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);

                Assert.Equal("C3-3\n", store.Load("EditableScope").Content);
                Assert.Equal("AppliedScope", store.LoadApplied().ProfileName);
                Assert.Equal(
                    "D4-4\n",
                    Find<TextBox>(firstWindow, "AreaProfileEditor").Text);
                Assert.Equal(
                    "草稿未保存",
                    Find<TextBlock>(firstWindow, "AreaProfileDiskStateText").Text);
                Assert.True(Find<ButtonBase>(
                    firstWindow,
                    "AreaProfileDiscardButton").IsEnabled);
                Assert.True(Find<ButtonBase>(
                    firstWindow,
                    "AreaProfileSaveAsButton").IsEnabled);
                var failure = Find<Wpf.Ui.Controls.InfoBar>(
                    firstWindow,
                    "AreaProfileInfoBar");
                Assert.Contains(
                    WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk,
                    failure.Message,
                    StringComparison.Ordinal);
                Assert.Contains("磁盘", failure.Message, StringComparison.Ordinal);
            }
            finally
            {
                secondWindow.Dispose();
                firstWindow.Dispose();
            }
        });
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("delete")]
    public async Task File_confirmation_refuses_a_source_replaced_after_the_panel_opens(
        string operation)
    {
        using var files = new TemporaryWatchFiles("AppliedScope", "A1-1\n");
        var externalStore = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(externalStore.Save("SourceScope", "B2-2\n").Saved);
        Assert.True(externalStore.Apply("AppliedScope").Applied);

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
                SelectProfile(window, "SourceScope");
                Click(Find<ButtonBase>(
                    window,
                    operation == "rename"
                        ? "AreaProfileRenameButton"
                        : "AreaProfileDeleteButton"));
                var panel = Find<FrameworkElement>(window, "AreaProfileFileOperationPanel");
                Assert.Equal(Visibility.Visible, panel.Visibility);
                if (operation == "rename")
                {
                    Find<TextBox>(window, "AreaProfileTargetNameInput").Text = "RenamedScope";
                }

                var loadedByExternalStore = externalStore.Load("SourceScope");
                Assert.True(externalStore.Save(
                    "SourceScope",
                    "C3-3\n",
                    loadedByExternalStore.FileFingerprint!).Saved);
                Click(Find<ButtonBase>(window, "AreaProfileFileOperationConfirmButton"));
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);

                Assert.Equal("C3-3\n", externalStore.Load("SourceScope").Content);
                Assert.False(File.Exists(Path.Combine(
                    files.AreaProfilesPath,
                    "RenamedScope.txt")));
                Assert.Equal("AppliedScope", externalStore.LoadApplied().ProfileName);
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                var failure = Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar");
                Assert.Contains(
                    WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk,
                    failure.Message,
                    StringComparison.Ordinal);
                Assert.Contains("重新选择", failure.Message, StringComparison.Ordinal);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("delete")]
    public async Task File_confirmation_does_not_adopt_an_unseen_external_version_when_opening(
        string operation)
    {
        using var files = new TemporaryWatchFiles("AppliedScope", "A1-1\n");
        var externalStore = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(externalStore.Save("SourceScope", "B2-2\n").Saved);
        Assert.True(externalStore.Apply("AppliedScope").Applied);

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
                SelectProfile(window, "SourceScope");
                var loadedByExternalStore = externalStore.Load("SourceScope");
                Assert.True(externalStore.Save(
                    "SourceScope",
                    "C3-3\n",
                    loadedByExternalStore.FileFingerprint!).Saved);

                Click(Find<ButtonBase>(
                    window,
                    operation == "rename"
                        ? "AreaProfileRenameButton"
                        : "AreaProfileDeleteButton"));
                await window.AreaProfileOperationTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);

                Assert.Equal(
                    Visibility.Collapsed,
                    Find<FrameworkElement>(window, "AreaProfileFileOperationPanel").Visibility);
                Assert.Equal("C3-3\n", externalStore.Load("SourceScope").Content);
                Assert.Equal("AppliedScope", externalStore.LoadApplied().ProfileName);
                var failure = Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar");
                Assert.Contains(
                    WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk,
                    failure.Message,
                    StringComparison.Ordinal);
                Assert.Contains("重新加载", failure.Message, StringComparison.Ordinal);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Late_area_apply_completion_cannot_replace_a_newer_all_areas_success_notice()
    {
        const string credential = "ticket-21-area-generation-secret";
        var slowGate = new FakeHostGate();
        var slowOverviewReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allAreasDemandReceived = new TaskCompletionSource<DemandSeriesBrowseQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-21-area-generation", credential)
            {
                Overview = FakeHostReply.Select<WatchOverviewQuery, WatchOverviewSnapshot>(query =>
                {
                    if (query.MesAreas is ["B2-2"])
                    {
                        slowOverviewReceived.TrySetResult();
                        return FakeHostReply.After(
                            slowGate,
                            CreateOverview(query.MesAreas),
                            completeAfterCancellation: true);
                    }

                    return FakeHostReply.Return(CreateOverview(query.MesAreas ?? []));
                }),
                DemandSeries = FakeHostReply.Select<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>(
                    query =>
                    {
                        if (query.Filter.MesAreas.Count == 0)
                        {
                            allAreasDemandReceived.TrySetResult(query);
                        }

                        return query.Filter.MesAreas is ["B2-2"]
                            ? FakeHostReply.After(
                                slowGate,
                                CreateEmptyDemandSeries(query),
                                completeAfterCancellation: true)
                            : FakeHostReply.Return(CreateEmptyDemandSeries(query));
                    }),
                ReadabilityAudit = FakeHostReply.Select<ReadabilityAuditQuery, ReadabilityAuditListSnapshot>(
                    query => query.Filter.MesAreas is ["B2-2"]
                        ? FakeHostReply.After(
                            slowGate,
                            CreateEmptyReadabilityAudit(query),
                            completeAfterCancellation: true)
                        : FakeHostReply.Return(CreateEmptyReadabilityAudit(query))),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles("InitialScope", "A1-1\n");
        files.WriteProfile("SlowScope", "B2-2\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Apply("InitialScope").Applied);
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
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));
                var profiles = Find<ListBox>(window, "AreaProfileList");
                profiles.SelectedItem = Assert.Single(
                    profiles.Items.Cast<WatchAreaFilterProfilePresentationRow>(),
                    row => row.ProfileName == "SlowScope");

                Click(Find<ButtonBase>(window, "AreaProfileApplyButton"));
                var slowOperation = window.AreaProfileOperationTask;
                await slowOverviewReceived.Task.WaitAsync(timeout.Token);

                Click(Find<ButtonBase>(window, "AreaApplyAllAreasButton"));
                var newerOperation = window.AreaProfileOperationTask;
                Assert.Empty(window.AreaContext.MesAreas);
                Assert.True(store.LoadApplied().IsAllAreas);
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(
                    window,
                    "DemandSeriesNavigationItem"));
                var navigatedDemandQuery = await allAreasDemandReceived.Task.WaitAsync(timeout.Token);
                Assert.Empty(navigatedDemandQuery.Filter.MesAreas);
                await newerOperation.WaitAsync(timeout.Token);
                var info = Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar");
                Assert.Equal("已应用全部 AREA", info.Title);
                Assert.Empty(window.AreaContext.MesAreas);

                slowGate.Release();
                await slowOperation.WaitAsync(timeout.Token);

                Assert.Equal("已应用全部 AREA", info.Title);
                Assert.Empty(window.AreaContext.MesAreas);
                Assert.True(store.LoadApplied().IsAllAreas);
            }
            finally
            {
                slowGate.Release();
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Late_all_areas_completion_cannot_replace_a_newer_profile_apply_success()
    {
        var client = new ReverseAreaGenerationClient();
        using var files = new TemporaryWatchFiles("InitialScope", "A1-1\n");
        files.WriteProfile("NewScope", "B2-2\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Apply("InitialScope").Applied);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                clientFactory: _ => client,
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath,
                areaFilterProfilesDirectoryPath: files.AreaProfilesPath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));

                Click(Find<ButtonBase>(window, "AreaApplyAllAreasButton"));
                var slowAllAreasOperation = window.AreaProfileOperationTask;
                await client.AllAreasOverviewStarted.WaitAsync(timeout.Token);

                SelectProfile(window, "NewScope");
                Click(Find<ButtonBase>(window, "AreaProfileApplyButton"));
                var newerProfileOperation = window.AreaProfileOperationTask;
                await newerProfileOperation.WaitAsync(timeout.Token);
                var info = Find<Wpf.Ui.Controls.InfoBar>(window, "AreaProfileInfoBar");
                Assert.Equal("AREA 配置已应用", info.Title);
                Assert.Equal(["B2-2"], window.AreaContext.MesAreas);
                Assert.Equal("NewScope", store.LoadApplied().ProfileName);
                Assert.False(
                    slowAllAreasOperation.IsCompleted,
                    "The older all-areas operation must still be pending so its generation guard is observable.");

                client.ReleaseAllAreas();
                await slowAllAreasOperation.WaitAsync(timeout.Token);

                Assert.Equal("AREA 配置已应用", info.Title);
                Assert.Equal(["B2-2"], window.AreaContext.MesAreas);
                Assert.Equal("NewScope", store.LoadApplied().ProfileName);
                Assert.Equal(
                    "NewScope.txt",
                    Find<TextBlock>(window, "AreaProfileFileTitleText").Text);
            }
            finally
            {
                client.ReleaseAllAreas();
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Applied_profile_that_is_now_invalid_keeps_critical_text_and_automation_when_selected()
    {
        using var files = new TemporaryWatchFiles("已损坏", "A1-1\n");
        var store = new WatchAreaFilterProfileStore(files.AreaProfilesPath);
        Assert.True(store.Apply("已损坏").Applied);
        File.WriteAllText(files.ProfilePath, "AREA-INVALID\n", new UTF8Encoding(false));

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
                window.Show();
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));
                window.UpdateLayout();

                var profileList = Find<ListBox>(window, "AreaProfileList");
                var row = Assert.Single(
                    profileList.Items.Cast<WatchAreaFilterProfilePresentationRow>());
                Assert.True(row.IsApplied);
                Assert.False(row.IsValid);
                Assert.Equal("当前应用 · 无效", row.StatusText);
                Assert.Contains("当前应用 · 无效", row.AutomationName, StringComparison.Ordinal);

                profileList.SelectedItem = row;
                window.UpdateLayout();
                var container = Assert.IsType<ListBoxItem>(
                    profileList.ItemContainerGenerator.ContainerFromItem(row));
                Assert.Equal(row.AutomationName, AutomationProperties.GetName(container));

                var statusPill = Assert.IsType<Border>(FindVisualDescendant<Border>(
                    container,
                    border => border.Visibility == Visibility.Visible
                        && ReferenceEquals(
                            border.Style,
                            window.FindResource("StatusPillCritical"))));
                var statusText = Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                    FindVisualDescendant<Wpf.Ui.Controls.TextBlock>(
                        statusPill,
                        text => text.Text == "当前应用 · 无效"));
                Assert.Same(
                    window.FindResource("SystemFillColorCriticalBackgroundBrush"),
                    statusPill.Background);
                Assert.Same(
                    window.FindResource("SystemFillColorCriticalBrush"),
                    statusText.Foreground);
                Assert.Same(
                    window.FindResource("AccentFillColorDefaultBrush"),
                    container.Background);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Readability_and_area_pages_expose_stable_automation_landmarks_and_stack_their_detail_work_below_master_at_720_epx()
    {
        var maximumLengthProfileName = new string('A', 80);
        using var files = new TemporaryWatchFiles(maximumLengthProfileName, "A1-1\n");

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

                Find<ListBox>(window, "AreaProfileList").SelectedIndex = 0;
                Click(Find<ButtonBase>(window, "AreaProfileRenameButton"));
                window.UpdateLayout();
                var prompt = Find<TextBlock>(window, "AreaProfileFileOperationPromptText");
                var target = Find<TextBox>(window, "AreaProfileTargetNameInput");
                var confirm = Find<ButtonBase>(
                    window,
                    "AreaProfileFileOperationConfirmButton");
                var cancel = Find<ButtonBase>(
                    window,
                    "AreaProfileFileOperationCancelButton");
                Assert.Equal(TextTrimming.CharacterEllipsis, prompt.TextTrimming);
                Assert.Contains(maximumLengthProfileName, prompt.ToolTip?.ToString(), StringComparison.Ordinal);
                Assert.Equal(0, Grid.GetRow(prompt));
                Assert.Equal(2, Grid.GetRow(target));
                Assert.Equal(2, Grid.GetRow(confirm));
                Assert.Equal(2, Grid.GetRow(cancel));
                foreach (var control in new Control[] { target, confirm, cancel })
                {
                    AssertHorizontallyDiscoverable(control, areaPage);
                    Assert.True(control.Focus());
                    Assert.Same(control, Keyboard.FocusedElement);
                }
                Click(cancel);

                var areaInputs = new (string Name, Type Type, string AutomationName)[]
                {
                    ("AreaProfileList", typeof(ListBox), "本机命名 AREA 配置列表"),
                    ("AreaProfileEditor", typeof(TextBox), "AREA 配置 TXT 内容编辑器"),
                    ("AreaProfileValidationGrid", typeof(DataGrid), "AREA 配置逐项校验"),
                };
                foreach (var (name, type, automationName) in areaInputs)
                {
                    var control = Assert.IsAssignableFrom<Control>(window.FindName(name));
                    Assert.True(type.IsInstanceOfType(control), $"{name} must be a {type.Name}");
                    AssertInteractiveAutomation(control, name, automationName);
                }

                Find<ListBox>(window, "AreaProfileList").SelectedIndex = 0;
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

    private static void SelectProfile(WatchWorkspaceWindow window, string profileName)
    {
        var profiles = Find<ListBox>(window, "AreaProfileList");
        profiles.SelectedItem = Assert.Single(
            profiles.Items.Cast<WatchAreaFilterProfilePresentationRow>(),
            row => row.ProfileName == profileName);
    }

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

    private sealed class ReverseAreaGenerationClient : IWatchV2ApiClient
    {
        private readonly TaskCompletionSource _allAreasOverviewStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseAllAreas = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AllAreasOverviewStarted => _allAreasOverviewStarted.Task;

        public void ReleaseAllAreas() => _releaseAllAreas.TrySetResult();

        public Task VerifyContractAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task<WatchOverviewSnapshot> FetchOverviewAsync(
            WatchOverviewQuery query,
            CancellationToken cancellationToken = default)
        {
            var areas = query.MesAreas ?? [];
            if (areas.Count == 0)
            {
                _allAreasOverviewStarted.TrySetResult();
                await _releaseAllAreas.Task.ConfigureAwait(false);
            }

            return CreateOverview(areas);
        }

        public async Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
            DemandSeriesBrowseQuery query,
            CancellationToken cancellationToken = default)
        {
            if (query.Filter.MesAreas.Count == 0)
            {
                await _releaseAllAreas.Task.ConfigureAwait(false);
            }

            return CreateEmptyDemandSeries(query);
        }

        public async Task<ReadabilityAuditListSnapshot> FetchReadabilityAuditAsync(
            ReadabilityAuditQuery query,
            CancellationToken cancellationToken = default)
        {
            if (query.Filter.MesAreas.Count == 0)
            {
                await _releaseAllAreas.Task.ConfigureAwait(false);
            }

            return CreateEmptyReadabilityAudit(query);
        }

        public Task<DemandSeriesDetailSnapshot> FetchDemandSeriesDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) => Missing<DemandSeriesDetailSnapshot>();

        public Task<ReadabilityAuditDetailSnapshot> FetchReadabilityAuditDetailAsync(
            string demandId,
            string snapshotReference,
            CancellationToken cancellationToken = default) => Missing<ReadabilityAuditDetailSnapshot>();

        public Task<ErrorSearchListSnapshot> FetchErrorSearchAsync(
            ErrorSearchQuery query,
            CancellationToken cancellationToken = default) => Missing<ErrorSearchListSnapshot>();

        public Task<ErrorSearchDetailSnapshot> FetchErrorSearchDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) => Missing<ErrorSearchDetailSnapshot>();

        public Task<ErrorSearchRawEvidenceSnapshot> FetchErrorRawEvidenceAsync(
            string seriesId,
            string evidenceId,
            string snapshotReference,
            ErrorSearchRawEvidenceQuery query,
            CancellationToken cancellationToken = default) => Missing<ErrorSearchRawEvidenceSnapshot>();

        public Task<CurrentIngestAttentionSnapshot> FetchCurrentAttentionAsync(
            CurrentIngestAttentionQuery query,
            CancellationToken cancellationToken = default) => Missing<CurrentIngestAttentionSnapshot>();

        public void Dispose() => ReleaseAllAreas();

        private static Task<T> Missing<T>() =>
            Task.FromException<T>(new NotSupportedException());
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
