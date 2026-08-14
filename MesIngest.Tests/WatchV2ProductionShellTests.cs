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
        RunInSta(() =>
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
        RunInSta(() =>
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
                Grid.GetRow(Assert.IsType<Border>(window.FindName("RefreshSettingsCard"))));
            Assert.Equal("Segoe UI Variable, Microsoft YaHei UI", window.FontFamily.Source);

            window.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        });

    [Fact]
    public void Demand_series_page_keeps_filter_list_exact_paging_and_evidence_journeys_accessible_at_720_epx() =>
        RunInSta(() =>
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
            Assert.Equal(ScrollBarVisibility.Auto, scrollViewer.VerticalScrollBarVisibility);
            Assert.Equal("需求系列页面", AutomationProperties.GetName(page));
            Assert.Equal("需求系列工作区", AutomationProperties.GetName(scrollViewer));
            var context = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesContextText"));
            Assert.Equal("需求系列快照与 AREA 范围", AutomationProperties.GetName(context));
            Assert.Contains("AREA", context.Text, StringComparison.Ordinal);
            var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("DemandSeriesInfoBar"));
            Assert.Equal("需求系列读取状态", AutomationProperties.GetName(infoBar));

            var filters = new (string Name, Type Type, string AutomationName)[]
            {
                ("DemandSeriesLifecycleFilter", typeof(ComboBox), "生命周期筛选"),
                ("DemandSeriesPresenceFilter", typeof(ComboBox), "当前出现状态筛选"),
                ("DemandSeriesWorkTypeFilter", typeof(ComboBox), "WorkType 筛选"),
                ("DemandSeriesSublotFilter", typeof(TextBox), "SUBLOT 筛选"),
                ("DemandSeriesSeriesIdFilter", typeof(TextBox), "SeriesId 筛选"),
                ("DemandSeriesDemandIdFilter", typeof(TextBox), "DemandId 筛选"),
                ("DemandSeriesPageSizeFilter", typeof(ComboBox), "每页数量"),
            };
            foreach (var (name, type, automationName) in filters)
            {
                var control = Assert.IsAssignableFrom<Control>(window.FindName(name));
                Assert.True(type.IsInstanceOfType(control), $"{name} must be a {type.Name}");
                Assert.Equal(automationName, AutomationProperties.GetName(control));
                Assert.Equal(name, AutomationProperties.GetAutomationId(control));
                Assert.True(control.Focusable, $"{name} must accept keyboard focus");
                Assert.True(
                    KeyboardNavigation.GetIsTabStop(control),
                    $"{name} must participate in tab navigation");
            }

            var commandNames = new Dictionary<string, string>
            {
                ["DemandSeriesApplyFiltersButton"] = "应用需求系列筛选",
                ["DemandSeriesClearFiltersButton"] = "清空需求系列筛选条件",
                ["DemandSeriesPreviousButton"] = "需求系列上一页",
                ["DemandSeriesNextButton"] = "需求系列下一页",
                ["DemandSeriesGoToPageButton"] = "跳转到需求系列页码",
                ["DemandSeriesAllAreasConfirmButton"] = "确认切换到全部 AREA",
                ["DemandSeriesCopyTimeButton"] = "复制需求系列时间",
                ["DemandSeriesCopyEvidenceButton"] = "复制需求系列证据",
            };
            foreach (var (name, automationName) in commandNames)
            {
                var button = Assert.IsAssignableFrom<ButtonBase>(window.FindName(name));
                Assert.Equal(automationName, AutomationProperties.GetName(button));
                Assert.Equal(name, AutomationProperties.GetAutomationId(button));
                Assert.True(button.Focusable, $"{name} must accept keyboard focus");
                Assert.True(
                    KeyboardNavigation.GetIsTabStop(button),
                    $"{name} must participate in tab navigation");
            }

            var applyFilters = Assert.IsType<Wpf.Ui.Controls.Button>(
                window.FindName("DemandSeriesApplyFiltersButton"));
            var clearFilters = Assert.IsType<Wpf.Ui.Controls.Button>(
                window.FindName("DemandSeriesClearFiltersButton"));
            Assert.Equal(ControlAppearance.Secondary, applyFilters.Appearance);
            Assert.False(clearFilters.IsEnabled);
            var sublotDraft = Assert.IsType<TextBox>(
                window.FindName("DemandSeriesSublotFilter"));
            sublotDraft.Text = "SL-DRAFT";
            Assert.True(clearFilters.IsEnabled);
            sublotDraft.Clear();
            Assert.False(clearFilters.IsEnabled);

            var seriesGrid = Assert.IsType<DataGrid>(window.FindName("DemandSeriesGrid"));
            Assert.Equal("需求系列列表", AutomationProperties.GetName(seriesGrid));
            Assert.Equal("DemandSeriesGrid", AutomationProperties.GetAutomationId(seriesGrid));
            Assert.True(seriesGrid.IsReadOnly);
            Assert.False(seriesGrid.AutoGenerateColumns);
            Assert.False(seriesGrid.CanUserSortColumns);
            Assert.True(seriesGrid.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(seriesGrid));
            Assert.Equal(DataGridSelectionUnit.FullRow, seriesGrid.SelectionUnit);
            Assert.Equal(
                new[]
                {
                    "SeriesId",
                    "SUBLOT",
                    "WorkType",
                    "开始时间",
                    "生命周期 / 当前出现",
                    "当前 Demand",
                    "世代",
                    "Demand 状态",
                    "DemandLastSeenAt",
                    "GoneConfirmedAt",
                    "ArchivedAt",
                    "外部可读",
                    "最后序列",
                    "相关关注",
                    "PollTrace",
                    "ProjectionCommit",
                },
                seriesGrid.Columns.Select(column => column.Header?.ToString()));
            var orderSummary = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesOrderText"));
            Assert.Equal("需求系列固定排序", AutomationProperties.GetName(orderSummary));
            Assert.Contains("Host 固定排序", orderSummary.Text, StringComparison.Ordinal);

            var pageSummary = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesPageSummaryText"));
            Assert.Equal("需求系列精确分页摘要", AutomationProperties.GetName(pageSummary));
            Assert.Equal("尚无需求系列快照", pageSummary.Text);
            var pageNumber = Assert.IsType<TextBox>(
                window.FindName("DemandSeriesPageNumberInput"));
            Assert.Equal("需求系列页码", AutomationProperties.GetName(pageNumber));
            Assert.Equal(
                "DemandSeriesPageNumberInput",
                AutomationProperties.GetAutomationId(pageNumber));
            Assert.True(pageNumber.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(pageNumber));
            Assert.False(pageNumber.IsEnabled);
            Assert.False(Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesGoToPageButton")).IsEnabled);
            var emptyState = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("DemandSeriesEmptyState"));
            Assert.Equal(Visibility.Collapsed, emptyState.Visibility);
            Assert.Equal("需求系列空结果", AutomationProperties.GetName(emptyState));

            var masterPanel = Assert.IsType<Border>(
                window.FindName("DemandSeriesMasterPanel"));
            var detailPanel = Assert.IsType<Border>(
                window.FindName("DemandSeriesDetailPanel"));
            Assert.Equal("需求系列主列表", AutomationProperties.GetName(masterPanel));
            Assert.Equal("需求系列详情与证据", AutomationProperties.GetName(detailPanel));
            Assert.Equal(0, Grid.GetRow(masterPanel));
            Assert.Equal(2, Grid.GetRow(detailPanel));
            Assert.InRange(masterPanel.ActualWidth, 1, scrollViewer.ActualWidth);
            Assert.InRange(detailPanel.ActualWidth, 1, scrollViewer.ActualWidth);

            var detailHeading = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesDetailHeadingText"));
            Assert.Equal("选中需求系列详情", AutomationProperties.GetName(detailHeading));
            Assert.Contains("选择", detailHeading.Text, StringComparison.Ordinal);
            var evidenceTabs = Assert.IsType<TabControl>(
                window.FindName("DemandSeriesEvidenceTabs"));
            Assert.Equal("需求系列详情证据类别", AutomationProperties.GetName(evidenceTabs));
            Assert.Equal(
                "DemandSeriesEvidenceTabs",
                AutomationProperties.GetAutomationId(evidenceTabs));
            Assert.True(evidenceTabs.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(evidenceTabs));

            var evidenceGrids = new Dictionary<string, string>
            {
                ["DemandSeriesGenerationGrid"] = "Demand 世代关系",
                ["DemandSeriesRawObservationGrid"] = "MES 原始观测",
                ["DemandSeriesConditionGrid"] = "需求系列当前条件",
                ["DemandSeriesErrorPeriodGrid"] = "需求系列错误期间",
                ["DemandSeriesErrorEvidenceGrid"] = "需求系列错误期间永久证据",
                ["DemandSeriesEventGrid"] = "需求系列永久事件与轮次证据",
            };
            foreach (var (name, automationName) in evidenceGrids)
            {
                var grid = Assert.IsType<DataGrid>(window.FindName(name));
                Assert.Equal(automationName, AutomationProperties.GetName(grid));
                Assert.Equal(name, AutomationProperties.GetAutomationId(grid));
                Assert.True(grid.IsReadOnly);
                Assert.False(grid.AutoGenerateColumns);
                Assert.Equal(DataGridSelectionUnit.FullRow, grid.SelectionUnit);
                Assert.True(grid.Focusable, $"{name} must accept keyboard focus");
                Assert.True(
                    KeyboardNavigation.GetIsTabStop(grid),
                    $"{name} must participate in tab navigation");
            }

            var generationGrid = Assert.IsType<DataGrid>(
                window.FindName("DemandSeriesGenerationGrid"));
            Assert.Contains(generationGrid.Columns, column =>
                string.Equals(column.Header?.ToString(), "资格阻断", StringComparison.Ordinal));
            Assert.Contains(generationGrid.Columns, column =>
                string.Equals(column.Header?.ToString(), "可信 MES", StringComparison.Ordinal));

            var rawGrid = Assert.IsType<DataGrid>(
                window.FindName("DemandSeriesRawObservationGrid"));
            Assert.Equal(
                new[]
                {
                    "#",
                    "Assignment",
                    "SeriesId",
                    "DemandId",
                    "WorkType",
                    "SUBLOT",
                    "AREA",
                    "EQP",
                    "STEP",
                    "DATES / MesSourceDate",
                    "MesSourceDateRaw",
                    "PACKAGE",
                    "ObservedAt",
                    "PollTrace",
                    "ProjectionCommit",
                },
                rawGrid.Columns.Select(column => column.Header?.ToString()));
            Assert.StartsWith(
                "可信 LiveMesFieldSet",
                AutomationProperties.GetName(Assert.IsAssignableFrom<TextBlock>(
                    window.FindName("DemandSeriesLiveMesFieldsText"))),
                StringComparison.Ordinal);
            Assert.StartsWith(
                "当前 Demand 资格阻断",
                AutomationProperties.GetName(Assert.IsAssignableFrom<TextBlock>(
                    window.FindName("DemandSeriesReadabilityBlockersText"))),
                StringComparison.Ordinal);

            var confirmationPanel = Assert.IsType<Border>(
                window.FindName("DemandSeriesAllAreasConfirmPanel"));
            Assert.Equal(Visibility.Collapsed, confirmationPanel.Visibility);
            Assert.Equal("范围外对象切换确认", AutomationProperties.GetName(confirmationPanel));
            var confirmationInfo = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("DemandSeriesAllAreasConfirmInfo"));
            Assert.Equal("范围外对象切换警告", AutomationProperties.GetName(confirmationInfo));

            window.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        });

    [Fact]
    public void Overview_navigation_preserves_the_exact_first_page_intent_and_routes_to_the_v2_host_page() =>
        RunInSta(() =>
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
        RunInSta(() =>
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

    private static void RunInSta(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                caught = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA test did not finish");
        Assert.Null(caught);
    }

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

    private sealed class ManualTimerTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            }

            lock (_gate)
            {
                _utcNow += elapsed;
            }

            while (TryTakeDueCallback(out var callback))
            {
                callback();
            }
        }

        private bool TryTakeDueCallback(out Action callback)
        {
            lock (_gate)
            {
                var timer = _timers.FirstOrDefault(value => value.IsDue(_utcNow));
                if (timer is null)
                {
                    callback = null!;
                    return false;
                }

                callback = timer.TakeCallback(_utcNow);
                return true;
            }
        }

        private sealed class ManualTimer(
            ManualTimerTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private DateTimeOffset? _dueAt;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public bool IsDue(DateTimeOffset utcNow) =>
                !_disposed && _dueAt is { } dueAt && dueAt <= utcNow;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _period = period;
                    _dueAt = dueTime == Timeout.InfiniteTimeSpan
                        ? null
                        : owner._utcNow + dueTime;
                    return true;
                }
            }

            public Action TakeCallback(DateTimeOffset utcNow)
            {
                _dueAt = _period == Timeout.InfiniteTimeSpan
                    ? null
                    : utcNow + _period;
                return () => callback(state);
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    _disposed = true;
                    _dueAt = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
