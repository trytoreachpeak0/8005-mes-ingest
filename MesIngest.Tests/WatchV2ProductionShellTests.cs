using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Reflection;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;
using NavigationView = Wpf.Ui.Controls.NavigationView;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using TitleBar = Wpf.Ui.Controls.TitleBar;
using TitleBarButton = Wpf.Ui.Controls.TitleBarButton;
using WindowCornerPreference = Wpf.Ui.Controls.WindowCornerPreference;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace MesIngest.Tests;

[Collection("WpfDesktop")]
public sealed class WatchV2ProductionShellTests
{
    private static void Click(WatchWorkspaceWindow window, string name) =>
        Assert.IsAssignableFrom<ButtonBase>(window.FindName(name))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [Fact]
    public void Production_app_owns_only_the_v2_composition_root()
    {
        var compositionField = Assert.IsAssignableFrom<FieldInfo>(
            typeof(App).GetField("_composition", BindingFlags.Instance | BindingFlags.NonPublic));

        Assert.Equal(typeof(WatchV2ApplicationComposition), compositionField.FieldType);
    }

    [Fact]
    public void Production_composition_creates_the_six_page_fluent_v2_shell_without_forbidden_refresh_controls() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-v2-shell-{Guid.NewGuid():N}");
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    SharedSecret = "external-only",
                    RequestTimeoutSeconds = 30,
                },
                connectionPreferencesPath: Path.Combine(root, "connection.json"),
                workspacePreferencesPath: Path.Combine(root, "workspace.json"));
            var window = composition.CreateMainWindow(initializeOnLoaded: false);

            Assert.IsType<WatchWorkspaceWindow>(window);
            Assert.True(window.ExtendsContentIntoTitleBar);
            Assert.Equal(Math.Min(1440, SystemParameters.WorkArea.Width), window.Width);
            Assert.Equal(Math.Min(900, SystemParameters.WorkArea.Height), window.Height);
            Assert.Equal(720, window.MinWidth);
            Assert.Equal(WindowCornerPreference.Round, window.WindowCornerPreference);

            var titleBar = Assert.IsType<TitleBar>(window.FindName("WindowTitleBar"));
            var navigation = Assert.IsType<NavigationView>(window.FindName("WorkspaceNavigation"));
            Assert.Equal("主导航", AutomationProperties.GetName(navigation));
            Assert.Equal(
                new[] { "概览", "需求系列", "资格审计", "错误检索", "区域筛选", "接入告警" },
                navigation.MenuItems
                    .OfType<NavigationViewItem>()
                    .Select(item => item.Content?.ToString()));
            Assert.Equal(
                new[] { "服务端未连接", "设置" },
                navigation.FooterMenuItems
                    .OfType<NavigationViewItem>()
                    .Select(item => item.Content?.ToString()));

            Assert.Null(window.FindName("OverviewRefreshButton"));
            Assert.Null(window.FindName("CancelRefreshButton"));
            Assert.Null(window.FindName("AutoRefreshEnabled"));
            Assert.Null(window.FindName("WatchStatusBar"));
            Assert.Null(window.FindName("ResumeStoragePressureButton"));
            Assert.Null(window.FindName("AcknowledgeHistoryResetButton"));
            Assert.Null(window.FindName("DeleteDatabaseButton"));
            Assert.True(titleBar.ShowMinimize);
            Assert.True(titleBar.ShowMaximize);
            Assert.True(titleBar.ShowClose);

            window.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        });

    [Fact]
    public void Protection_state_updates_existing_overview_footer_and_read_only_evidence_surfaces() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-v2-protection-{Guid.NewGuid():N}");
            var client = new ChangingOverviewClient(includeProtectionState: true);
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://host-a",
                    SharedSecret = "external-only",
                },
                _ => client,
                connectionPreferencesPath: Path.Combine(root, "connection.json"),
                workspacePreferencesPath: Path.Combine(root, "workspace.json"));
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            window.InitializeAsync().GetAwaiter().GetResult();

            var hostFooter = Assert.IsType<NavigationViewItem>(
                window.FindName("HostNavigationItem"));
            Assert.Contains("StoragePressurePause", hostFooter.Content?.ToString(), StringComparison.Ordinal);
            Assert.Contains("制造执行系统轮询暂停", AutomationProperties.GetName(hostFooter), StringComparison.Ordinal);

            window.NavigateFromOverview(new OverviewNavigationIntent(
                OverviewNavigationTargets.CurrentIngestAttention,
                AttentionKinds: [CurrentIngestAttentionKinds.StoragePressure]));
            window.CurrentAttentionNavigationTask.GetAwaiter().GetResult();

            Assert.Contains("StoragePressurePause", hostFooter.Content?.ToString(), StringComparison.Ordinal);
            var protectionSummary = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("AttentionSummaryFacetText"));
            Assert.Contains("StoragePressurePause", protectionSummary.Text, StringComparison.Ordinal);
            Assert.Contains(
                "制造执行系统轮询暂停",
                AutomationProperties.GetName(protectionSummary),
                StringComparison.Ordinal);
            var evidence = Assert.IsType<DataGrid>(
                    window.FindName("CurrentAttentionEvidenceGrid"))
                .ItemsSource
                .Cast<object>()
                .Select(item => (
                    Name: Assert.IsType<string>(item.GetType().GetProperty("Name")!.GetValue(item)),
                    Value: Assert.IsType<string>(item.GetType().GetProperty("Value")!.GetValue(item))))
                .ToArray();
            Assert.Contains(evidence, row => row.Name == "原因" && row.Value.Contains(
                "below pause threshold",
                StringComparison.Ordinal));
            Assert.Contains(evidence, row => row.Name == "服务端最早可用时间");
            Assert.Contains(evidence, row => row.Name == "本地管理" && row.Value.Contains(
                "resume-storage-pressure",
                StringComparison.Ordinal));
            Assert.Null(window.FindName("ResumeStoragePressureButton"));
            Assert.Null(window.FindName("AcknowledgeHistoryResetButton"));

            window.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        });

    [Fact]
    public void Production_shell_keeps_native_caption_commands_accessible_and_reflows_at_720_epx() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-v2-adaptive-{Guid.NewGuid():N}");
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions { BaseUrl = "http://127.0.0.1:5088" },
                connectionPreferencesPath: Path.Combine(root, "connection.json"),
                workspacePreferencesPath: Path.Combine(root, "workspace.json"));
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            window.Show();
            window.Width = 720;
            window.UpdateLayout();

            var titleBar = Assert.IsType<TitleBar>(window.FindName("WindowTitleBar"));
            var expectedButtons = new Dictionary<string, string>
            {
                ["PART_MinimizeButton"] = "最小化窗口",
                ["PART_MaximizeButton"] = "最大化窗口",
                ["PART_CloseButton"] = "关闭窗口",
            };
            foreach (var (partName, automationName) in expectedButtons)
            {
                var button = Assert.IsType<TitleBarButton>(
                    titleBar.Template.FindName(partName, titleBar));
                Assert.True(button.Focusable);
                Assert.True(KeyboardNavigation.GetIsTabStop(button));
                Assert.Equal(automationName, AutomationProperties.GetName(button));
            }

            Assert.Equal(
                2,
                Assert.IsType<UniformGrid>(window.FindName("OverviewSummaryCards")).Columns);
            Assert.Equal(
                2,
                Grid.GetRow(Assert.IsType<Border>(window.FindName("OverviewFactsCard"))));
            Assert.Equal(
                2,
                Grid.GetRow(Assert.IsType<StackPanel>(window.FindName("RefreshSettingsCard"))));
            Assert.Equal("Segoe UI Variable, Microsoft YaHei UI", window.FontFamily.Source);

            window.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        });

    [Fact]
    public void Demand_series_page_keeps_filters_full_height_list_and_exact_paging_accessible_at_720_epx() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-v2-demand-series-{Guid.NewGuid():N}");
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions { BaseUrl = "http://127.0.0.1:5088" },
                connectionPreferencesPath: Path.Combine(root, "connection.json"),
                workspacePreferencesPath: Path.Combine(root, "workspace.json"));
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            window.NavigateFromOverview(
                new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
            window.Show();
            window.Width = 720;
            window.Height = 900;
            window.UpdateLayout();

            var page = Assert.IsType<Grid>(window.FindName("DemandSeriesPage"));
            var scrollViewer = Assert.IsType<ScrollViewer>(
                window.FindName("DemandSeriesScrollViewer"));
            Assert.Equal(Visibility.Visible, page.Visibility);
            Assert.Equal(ScrollBarVisibility.Disabled, scrollViewer.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Disabled, scrollViewer.VerticalScrollBarVisibility);
            Assert.Equal("需求系列页面", AutomationProperties.GetName(page));
            Assert.Equal("需求系列工作区", AutomationProperties.GetName(scrollViewer));

            var context = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesContextText"));
            Assert.Equal("需求系列快照与区域范围", AutomationProperties.GetName(context));
            Assert.Contains("区域", context.Text, StringComparison.Ordinal);
            Assert.Equal(
                "需求系列读取状态",
                AutomationProperties.GetName(Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                    window.FindName("DemandSeriesInfoBar"))));
            var infoExpander = Assert.IsType<Wpf.Ui.Controls.CardExpander>(
                window.FindName("DemandSeriesInfoExpander"));
            Assert.True(infoExpander.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(infoExpander));

            var filters = new Dictionary<string, string>
            {
                ["DemandSeriesLifecycleAllButton"] = "生命周期：全部",
                ["DemandSeriesLifecycleTrackingButton"] = "生命周期：跟踪中",
                ["DemandSeriesLifecycleArchivedButton"] = "生命周期：已归档",
                ["DemandSeriesPresenceFilter"] = "当前出现状态筛选",
                ["DemandSeriesWorkTypeFilter"] = "工序类型筛选",
                ["DemandSeriesSublotFilter"] = "子批次筛选",
                ["DemandSeriesSeriesIdFilter"] = "需求系列标识筛选",
                ["DemandSeriesDemandIdFilter"] = "运输需求标识筛选",
                ["DemandSeriesAreaProfileSelector"] = "需求系列区域配置选择器",
                ["DemandSeriesPageSizeFilter"] = "每页数量",
            };
            foreach (var (name, automationName) in filters)
            {
                var control = Assert.IsAssignableFrom<Control>(window.FindName(name));
                Assert.Equal(automationName, AutomationProperties.GetName(control));
                Assert.True(KeyboardNavigation.GetIsTabStop(control));
            }

            var applyFilters = Assert.IsType<Wpf.Ui.Controls.Button>(
                window.FindName("DemandSeriesApplyFiltersButton"));
            Assert.Equal(ControlAppearance.Secondary, applyFilters.Appearance);
            Assert.Equal(
                "应用条件",
                AutomationProperties.GetName(applyFilters));

            var masterPanel = Assert.IsType<Border>(
                window.FindName("DemandSeriesMasterPanel"));
            Assert.Equal("需求系列主列表", AutomationProperties.GetName(masterPanel));
            Assert.Equal(4, Grid.GetRow(masterPanel));
            Assert.True(masterPanel.ActualHeight > 300);

            var openInspector = Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesOpenInspectorButton"));
            Assert.Equal("打开调查窗口", openInspector.Content);
            Assert.Equal(
                "DemandSeriesOpenInspectorButton",
                AutomationProperties.GetAutomationId(openInspector));
            Assert.Equal(
                "打开调查窗口",
                AutomationProperties.GetName(openInspector));
            Assert.False(openInspector.IsEnabled);

            var seriesGrid = Assert.IsType<DataGrid>(window.FindName("DemandSeriesGrid"));
            Assert.Equal("DemandSeriesGrid", AutomationProperties.GetAutomationId(seriesGrid));
            Assert.Equal("需求系列列表", AutomationProperties.GetName(seriesGrid));
            Assert.Equal(ScrollBarVisibility.Auto, seriesGrid.VerticalScrollBarVisibility);
            Assert.True(seriesGrid.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(seriesGrid));
            Assert.Equal(
                new[]
                {
                    "需求系列标识",
                    "工序类型",
                    "子批次",
                    "生命周期 / 当前出现",
                    "当前区域",
                    "当前运输需求",
                    "世代",
                    "事件",
                    "开始时间",
                    "最后观测",
                    "确认消失",
                    "归档时间",
                },
                seriesGrid.Columns.Select(column => column.Header?.ToString()));

            var emptyState = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("DemandSeriesEmptyState"));
            Assert.Equal("需求系列空结果", AutomationProperties.GetName(emptyState));
            Assert.Equal(Visibility.Collapsed, emptyState.Visibility);
            Assert.True(Assert.IsType<RowDefinition>(
                window.FindName("DemandSeriesPagingRow")).Height.IsAuto);
            foreach (var name in new[]
            {
                "DemandSeriesPreviousButton",
                "DemandSeriesGoToPageButton",
                "DemandSeriesNextButton",
            })
            {
                var pagingButton = Assert.IsAssignableFrom<Control>(window.FindName(name));
                Assert.True(KeyboardNavigation.GetIsTabStop(pagingButton));
            }

            Assert.Null(window.FindName("DemandSeriesMasterDetailGrid"));
            Assert.Null(window.FindName("DemandSeriesDetailPanel"));
            Assert.Null(window.FindName("DemandSeriesDetailVisibilityToggle"));
            Assert.Null(window.FindName("DemandSeriesMasterDetailSplitter"));
            Assert.Null(window.FindName("DemandSeriesGenerationGrid"));
            Assert.Null(window.FindName("DemandSeriesFullEvidenceTabs"));

            window.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        });

    [Fact]
    public void Overview_navigation_preserves_the_exact_first_page_intent_and_routes_to_the_v2_host_page() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-v2-route-{Guid.NewGuid():N}");
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions { BaseUrl = "http://127.0.0.1:5088" },
                connectionPreferencesPath: Path.Combine(root, "connection.json"),
                workspacePreferencesPath: Path.Combine(root, "workspace.json"));
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            var intent = new OverviewNavigationIntent(
                OverviewNavigationTargets.DemandSeries,
                PageNumber: 1,
                MesAreas: ["A1-1"],
                Lifecycles: ["TRACKING"],
                CurrentPresences: ["VISIBLE"],
                SeriesId: "series-19",
                Cursor: null);
            OverviewNavigationIntent? announced = null;
            window.OverviewNavigationRequested += (_, args) => announced = args.Intent;

            window.NavigateFromOverview(intent);

            Assert.Same(intent, window.LastOverviewNavigationIntent);
            Assert.Same(intent, announced);
            Assert.Equal(WatchWorkspacePage.DemandSeries, window.ActivePage);
            Assert.Equal(
                Visibility.Visible,
                Assert.IsType<Grid>(window.FindName("DemandSeriesPage")).Visibility);
            Assert.Throws<ArgumentException>(() => window.NavigateFromOverview(
                intent with { PageNumber = 2 }));
            Assert.Throws<ArgumentException>(() => window.NavigateFromOverview(
                intent with { Cursor = "must-not-survive" }));

            window.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        });

    [Fact]
    public void Automatic_overview_completion_reprojects_the_window_without_a_manual_command() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-v2-auto-ui-{Guid.NewGuid():N}");
            var clock = new ManualTimerTimeProvider(
                DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
            var client = new ChangingOverviewClient();
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://host-a",
                    SharedSecret = "external-only",
                },
                _ => client,
                connectionPreferencesPath: Path.Combine(root, "connection.json"),
                workspacePreferencesPath: Path.Combine(root, "workspace.json"),
                timeProvider: clock);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            window.InitializeAsync().GetAwaiter().GetResult();
            var notifications = Assert.IsType<ItemsControl>(
                window.FindName("NotificationItemsControl"));

            Assert.Equal(
                "1",
                Assert.IsAssignableFrom<TextBlock>(window.FindName("SeriesSummaryValue")).Text);
            Assert.Empty(notifications.Items);

            clock.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(2, client.OverviewCallCount);
            Assert.Equal(
                "2",
                Assert.IsAssignableFrom<TextBlock>(window.FindName("SeriesSummaryValue")).Text);
            Assert.False(window.WorkspaceState.Overview.IsRefreshing);
            Assert.Empty(notifications.Items);

            window.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        });

    [Fact]
    public void Returning_to_a_scrollable_page_starts_at_its_representative_top() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-v2-navigation-scroll-{Guid.NewGuid():N}");
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions { BaseUrl = "http://127.0.0.1:5088" },
                connectionPreferencesPath: Path.Combine(root, "connection.json"),
                workspacePreferencesPath: Path.Combine(root, "workspace.json"));
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            window.Show();
            window.Width = 960;
            window.Height = 600;
            window.UpdateLayout();

            Click(window, "CurrentAttentionNavigationItem");
            window.UpdateLayout();
            var viewport = Assert.IsType<ScrollViewer>(window.FindName("CurrentAttentionPage"));
            viewport.ScrollToBottom();
            window.UpdateLayout();
            Assert.True(viewport.VerticalOffset > 0);

            Click(window, "SettingsNavigationItem");
            Click(window, "CurrentAttentionNavigationItem");
            window.UpdateLayout();

            Assert.Equal(0, viewport.VerticalOffset);

            window.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        });

    [Fact]
    public void Continuing_overview_fault_is_announced_once_stays_in_the_header_and_recovers_as_a_new_cycle() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-v2-feedback-{Guid.NewGuid():N}");
            var clock = new ManualTimerTimeProvider(
                DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
            var client = new ChangingOverviewClient(
                failedOverviewCalls: new HashSet<int> { 2, 3, 5 });
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions { BaseUrl = "http://host-a", SharedSecret = "must-not-leak" },
                _ => client,
                connectionPreferencesPath: Path.Combine(root, "connection.json"),
                workspacePreferencesPath: Path.Combine(root, "workspace.json"),
                timeProvider: clock);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            window.InitializeAsync().GetAwaiter().GetResult();
            var items = Assert.IsType<ItemsControl>(
                window.FindName("NotificationItemsControl"));
            var header = Assert.IsType<Wpf.Ui.Controls.Button>(
                window.FindName("OverviewFaultStatusButton"));

            clock.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(Visibility.Visible, header.Visibility);
            Assert.Single(items.Items);
            Assert.Equal("概览读取持续失败", NotificationProperty(items, "Title"));
            Assert.DoesNotContain("must-not-leak", NotificationProperty(items, "Message"), StringComparison.Ordinal);
            Assert.DoesNotContain("http://", NotificationProperty(items, "Message"), StringComparison.Ordinal);

            clock.Advance(TimeSpan.FromSeconds(30));

            Assert.Empty(items.Items);
            Assert.Equal(Visibility.Visible, header.Visibility);

            clock.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(Visibility.Collapsed, header.Visibility);
            Assert.Single(items.Items);
            Assert.Equal("读取已恢复", NotificationProperty(items, "Title"));

            clock.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(Visibility.Visible, header.Visibility);
            Assert.Single(items.Items);
            Assert.Equal("概览读取持续失败", NotificationProperty(items, "Title"));

            window.Close();
            Directory.Delete(root, recursive: true);
        });

    [Fact]
    public void Host_contract_failure_is_global_controlled_and_its_action_opens_settings() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-v2-host-feedback-{Guid.NewGuid():N}");
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions { BaseUrl = "http://host-a", SharedSecret = "must-not-leak" },
                _ => new ChangingOverviewClient(verifyContractFailure: true),
                connectionPreferencesPath: Path.Combine(root, "connection.json"),
                workspacePreferencesPath: Path.Combine(root, "workspace.json"));
            var window = composition.CreateMainWindow(initializeOnLoaded: false);

            window.InitializeAsync().GetAwaiter().GetResult();
            var items = Assert.IsType<ItemsControl>(
                window.FindName("NotificationItemsControl"));
            var item = Assert.Single(items.Items)!;
            Assert.Equal("服务端连接持续失败", NotificationProperty(items, "Title"));
            Assert.Equal("打开设置", NotificationProperty(items, "ActionLabel"));
            Assert.DoesNotContain("must-not-leak", NotificationProperty(items, "Message"), StringComparison.Ordinal);
            Assert.Equal(
                WatchHostConnectionStatus.Failed,
                window.WorkspaceState.ConnectionStatus);

            var action = Assert.IsAssignableFrom<System.Windows.Input.ICommand>(
                item.GetType().GetProperty("ActionCommand")!.GetValue(item));
            action.Execute(null);

            Assert.Equal(WatchWorkspacePage.Settings, window.ActivePage);
            Assert.Single(items.Items);

            window.Close();
            Directory.Delete(root, recursive: true);
        });

    private static string NotificationProperty(ItemsControl items, string propertyName)
    {
        var item = Assert.Single(items.Items);
        var property = item!.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<string>(property!.GetValue(item));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Out_of_scope_DemandSeries_uses_a_modal_all_AREA_confirmation_without_changing_state_on_cancel(
        bool confirm) =>
        StaTestRunner.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(
                    System.Windows.Threading.Dispatcher.CurrentDispatcher));
            var root = Path.Combine(Path.GetTempPath(), $"watch-v2-all-area-{Guid.NewGuid():N}");
            var profiles = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(profiles);
            File.WriteAllText(Path.Combine(profiles, "东区.txt"), "A1-1");
            using (var store = new WatchAreaFilterProfileStore(profiles))
            {
                Assert.True(store.Apply("东区").Applied);
            }

            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions { BaseUrl = "http://host-a" },
                _ => new ChangingOverviewClient(),
                connectionPreferencesPath: Path.Combine(root, "connection.json"),
                workspacePreferencesPath: Path.Combine(root, "workspace.json"),
                areaFilterProfilesDirectoryPath: profiles);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            window.InitializeAsync().GetAwaiter().GetResult();
            Assert.Equal("东区", window.AreaContext.ProfileName);
            var intent = new OverviewNavigationIntent(
                OverviewNavigationTargets.DemandSeries,
                PageNumber: 1,
                MesAreas: ["A1-1"],
                SeriesId: "missing-series");

            window.NavigateFromOverview(intent);
            window.DemandSeriesNavigationTask.GetAwaiter().GetResult();
            DrainDispatcher(window.Dispatcher);

            Assert.Equal("all-areas", window.ActiveWorkspaceDialogKind);
            var dialog = Assert.IsType<Wpf.Ui.Controls.ContentDialog>(
                window.ActiveWorkspaceDialog);
            Assert.Equal("切换到全部区域", dialog.PrimaryButtonText);
            Assert.Equal("取消", dialog.CloseButtonText);
            Assert.Equal(Wpf.Ui.Controls.ContentDialogButton.Close, dialog.DefaultButton);

            dialog.Hide(confirm
                ? Wpf.Ui.Controls.ContentDialogResult.Primary
                : Wpf.Ui.Controls.ContentDialogResult.None);
            PumpUntilCompleted(window.Dispatcher, window.ActiveWorkspaceDialogTask);

            Assert.Null(window.ActiveWorkspaceDialog);
            Assert.Equal(confirm ? "全部区域" : "东区", window.AreaContext.ProfileName);
            Assert.Same(intent, window.LastOverviewNavigationIntent);
            Assert.Null(window.WorkspaceState.DemandSeries.SelectedId);

            window.Close();
            Directory.Delete(root, recursive: true);
        });

    private static void DrainDispatcher(System.Windows.Threading.Dispatcher dispatcher)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void PumpUntilCompleted(
        System.Windows.Threading.Dispatcher dispatcher,
        Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    () => frame.Continue = false),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
        DrainDispatcher(dispatcher);
    }

    private sealed class ChangingOverviewClient(
        bool includeProtectionState = false,
        IReadOnlySet<int>? failedOverviewCalls = null,
        bool verifyContractFailure = false)
        : IWatchV2ApiClient
    {
        public int OverviewCallCount { get; private set; }

        public Task VerifyContractAsync(CancellationToken cancellationToken) =>
            verifyContractFailure
                ? Task.FromException(new WatchHostQueryException(
                    WatchHostFailureKind.Contract,
                    "http://host-a/api/v2/contract?credential=must-not-leak",
                    "contract-correlation",
                    "must-not-leak contract detail",
                    errorCode: "CONTRACT_MISMATCH"))
                : Task.CompletedTask;

        public Task<WatchOverviewSnapshot> FetchOverviewAsync(
            WatchOverviewQuery query,
            CancellationToken cancellationToken = default)
        {
            var count = ++OverviewCallCount;
            if (failedOverviewCalls?.Contains(count) == true)
            {
                throw new WatchHostQueryException(
                    WatchHostFailureKind.Timeout,
                    "http://host-a/api/v2/overview?credential=must-not-leak",
                    $"correlation-{count}",
                    "must-not-leak simulated timeout with a long internal exception detail");
            }

            var at = DateTimeOffset.Parse("2026-08-14T08:00:00Z").AddSeconds(count);
            var identity = new OperationalSnapshotIdentity(
                $"commit-{count}",
                count,
                at,
                $"poll-{count}",
                count,
                count,
                at,
                HistoryEpoch: includeProtectionState
                    ? HistoryEpoch.FromGuid(Guid.Parse("99999999-9999-9999-9999-999999999999"))
                    : null);
            var series = new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries);
            var audit = new OverviewNavigationIntent(OverviewNavigationTargets.ReadabilityAudit);
            var errors = new OverviewNavigationIntent(OverviewNavigationTargets.ErrorSearch);
            var attention = new OverviewNavigationIntent(OverviewNavigationTargets.CurrentIngestAttention);
            return Task.FromResult(new WatchOverviewSnapshot(
                identity,
                query.MesAreas ?? [],
                new WatchOverviewSeriesSummary(
                    count,
                    count,
                    0,
                    0,
                    0,
                    series,
                    series,
                    series,
                    series,
                    series),
                new WatchOverviewReadabilitySummary(count, count, 0, audit, audit, audit),
                new WatchOverviewErrorSummary(0, 0, errors, errors, errors),
                new WatchOverviewAttentionSummary(
                    includeProtectionState ? 1 : 0,
                    includeProtectionState
                        ? [new OverviewFacetSnapshot(
                            CurrentIngestAttentionKinds.StoragePressure,
                            1,
                            attention with
                            {
                                AttentionKinds = [CurrentIngestAttentionKinds.StoragePressure],
                            })]
                        : [],
                    includeProtectionState
                        ? [new OverviewFacetSnapshot(
                            CurrentIngestAttentionSeverities.Error,
                            1,
                            attention)]
                        : [],
                    attention),
                [],
                WatchOverviewRecentActivityStates.NoRecentHighlights,
                WatchOverviewRecentActivityStates.NoRecentHighlightsMessage));
        }

        public Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
            DemandSeriesBrowseQuery query,
            CancellationToken cancellationToken = default)
        {
            var at = DateTimeOffset.Parse("2026-08-14T08:00:30Z");
            return Task.FromResult(new DemandSeriesListSnapshot(
                new DemandSeriesSnapshotIdentity(
                    HistoryEpoch.CreateNew(),
                    "commit-demand-empty",
                    1,
                    at,
                    "poll-demand-empty"),
                "snapshot-demand-empty",
                query.Filter,
                query.Order,
                0,
                new DemandSeriesFacets(0, 0, 0, 0, 0),
                query.PageSize,
                query.PageNumber,
                0,
                [],
                null,
                false));
        }

        public Task<DemandSeriesDetailSnapshot> FetchDemandSeriesDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReadabilityAuditListSnapshot> FetchReadabilityAuditAsync(
            ReadabilityAuditQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReadabilityAuditDetailSnapshot> FetchReadabilityAuditDetailAsync(
            string demandId,
            string snapshotReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ErrorSearchListSnapshot> FetchErrorSearchAsync(
            ErrorSearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ErrorSearchDetailSnapshot> FetchErrorSearchDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ErrorSearchRawEvidenceSnapshot> FetchErrorRawEvidenceAsync(
            string seriesId,
            string evidenceId,
            string snapshotReference,
            ErrorSearchRawEvidenceQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CurrentIngestAttentionSnapshot> FetchCurrentAttentionAsync(
            CurrentIngestAttentionQuery query,
            CancellationToken cancellationToken = default)
        {
            if (!includeProtectionState)
            {
                throw new NotSupportedException();
            }

            var at = DateTimeOffset.Parse("2026-08-14T08:00:30Z");
            var epoch = HistoryEpoch.FromGuid(
                Guid.Parse("99999999-9999-9999-9999-999999999999"));
            var item = new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.StoragePressure,
                CurrentIngestAttentionSeverities.Error,
                at,
                "STORAGE_PRESSURE",
                SeriesId: null,
                WorkType: null,
                ErrorCode: StoragePressureStatuses.Paused,
                Target: "MesIngest",
                SubjectKind: "DATABASE_VOLUME",
                new CurrentIngestAttentionEvidenceSnapshot(
                    EvidenceId: "pause-21",
                    Phase: StoragePressureStatuses.Paused,
                    FailureReason: "database volume below pause threshold",
                    DatabaseName: "MesIngest",
                    VolumeRoot: @"D:\",
                    AvailablePercent: 9.5m),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.CurrentIngestAttention,
                    AttentionKinds: [CurrentIngestAttentionKinds.StoragePressure]));
            return Task.FromResult(new CurrentIngestAttentionSnapshot(
                new OperationalSnapshotIdentity(
                    "commit-protection-21",
                    21,
                    at,
                    "poll-protection-21",
                    21,
                    21,
                    at,
                    HistoryEpoch: epoch),
                1,
                new CurrentIngestAttentionFacets(
                    [new(CurrentIngestAttentionKinds.StoragePressure, 1)],
                    [new(CurrentIngestAttentionSeverities.Error, 1)]),
                query.Order,
                query.PageSize,
                query.PageNumber,
                1,
                query.Kinds ?? [],
                query.Severities ?? [],
                [item],
                HistoryCleanupStateSnapshot.NotRun with
                {
                    EarliestAvailableHostUtc = at.AddDays(-15),
                },
                new StoragePressureStateSnapshot(
                    StoragePressureStatuses.Paused,
                    epoch,
                    "MesIngest",
                    @"D:\SqlData\MesIngest.mdf",
                    VolumeSpaceSample.FromPercent(@"D:\", 1_000_000, 9.5m),
                    at,
                    at,
                    "pause-21",
                    "database volume below pause threshold",
                    RecoveryAuditId: null)));
        }

        public void Dispose()
        {
        }
    }
}
