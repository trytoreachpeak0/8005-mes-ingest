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
            Assert.Equal(1440, window.Width);
            Assert.Equal(900, window.Height);
            Assert.Equal(720, window.MinWidth);
            Assert.Equal(WindowCornerPreference.Round, window.WindowCornerPreference);

            var titleBar = Assert.IsType<TitleBar>(window.FindName("WindowTitleBar"));
            var navigation = Assert.IsType<NavigationView>(window.FindName("WorkspaceNavigation"));
            Assert.Equal("主导航", AutomationProperties.GetName(navigation));
            Assert.Equal(
                new[] { "概览", "需求系列", "资格审计", "错误检索", "AREA 筛选", "接入告警" },
                navigation.MenuItems
                    .OfType<NavigationViewItem>()
                    .Select(item => item.Content?.ToString()));
            Assert.Equal(
                new[] { "Host 未连接", "设置" },
                navigation.FooterMenuItems
                    .OfType<NavigationViewItem>()
                    .Select(item => item.Content?.ToString()));

            Assert.Null(window.FindName("OverviewRefreshButton"));
            Assert.Null(window.FindName("CancelRefreshButton"));
            Assert.Null(window.FindName("AutoRefreshEnabled"));
            Assert.Null(window.FindName("WatchStatusBar"));
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
            Assert.Equal("需求系列快照与 AREA 范围", AutomationProperties.GetName(context));
            Assert.Contains("AREA", context.Text, StringComparison.Ordinal);
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
                ["DemandSeriesLifecycleTrackingButton"] = "生命周期：Tracking",
                ["DemandSeriesLifecycleArchivedButton"] = "生命周期：Archived",
                ["DemandSeriesPresenceFilter"] = "当前出现状态筛选",
                ["DemandSeriesWorkTypeFilter"] = "WorkType 筛选",
                ["DemandSeriesSublotFilter"] = "SUBLOT 筛选",
                ["DemandSeriesSeriesIdFilter"] = "SeriesId 筛选",
                ["DemandSeriesDemandIdFilter"] = "DemandId 筛选",
                ["DemandSeriesAreaProfileSelector"] = "需求系列 AREA 配置选择器",
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
                "应用需求系列筛选",
                AutomationProperties.GetName(applyFilters));

            var masterPanel = Assert.IsType<Border>(
                window.FindName("DemandSeriesMasterPanel"));
            Assert.Equal("需求系列主列表", AutomationProperties.GetName(masterPanel));
            Assert.Equal(4, Grid.GetRow(masterPanel));
            Assert.True(masterPanel.ActualHeight > 300);

            var openInspector = Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesOpenInspectorButton"));
            Assert.Equal("打开详情窗口", openInspector.Content);
            Assert.Equal(
                "DemandSeriesOpenInspectorButton",
                AutomationProperties.GetAutomationId(openInspector));
            Assert.Equal(
                "打开 DemandSeries 详情窗口",
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
                    "SeriesId",
                    "WorkType",
                    "SUBLOT",
                    "生命周期 / 当前出现",
                    "当前 AREA",
                    "当前 Demand",
                    "世代",
                    "事件",
                    "开始",
                    "LAST SEEN",
                    "GONE SINCE",
                    "ARCHIVED",
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

            Assert.Equal(
                "1",
                Assert.IsAssignableFrom<TextBlock>(window.FindName("SeriesSummaryValue")).Text);

            clock.Advance(TimeSpan.FromSeconds(10));

            Assert.Equal(2, client.OverviewCallCount);
            Assert.Equal(
                "2",
                Assert.IsAssignableFrom<TextBlock>(window.FindName("SeriesSummaryValue")).Text);
            Assert.False(window.WorkspaceState.Overview.IsRefreshing);

            window.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        });

    private sealed class ChangingOverviewClient : IWatchV2ApiClient
    {
        public int OverviewCallCount { get; private set; }

        public Task VerifyContractAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<WatchOverviewSnapshot> FetchOverviewAsync(
            WatchOverviewQuery query,
            CancellationToken cancellationToken = default)
        {
            var count = ++OverviewCallCount;
            var at = DateTimeOffset.Parse("2026-08-14T08:00:00Z").AddSeconds(count);
            var identity = new OperationalSnapshotIdentity(
                $"commit-{count}",
                count,
                at,
                $"poll-{count}",
                count,
                count,
                at);
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
                new WatchOverviewAttentionSummary(0, [], [], attention),
                [],
                WatchOverviewRecentActivityStates.NoRecentHighlights,
                WatchOverviewRecentActivityStates.NoRecentHighlightsMessage));
        }

        public Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
            DemandSeriesBrowseQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
