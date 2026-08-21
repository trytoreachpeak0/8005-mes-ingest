using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchDemandSeriesProductionIntegrationTests
{
    [Fact]
    public async Task Failed_area_b_refresh_keeps_area_a_snapshot_while_the_demand_header_shows_local_b_and_host_a()
    {
        const string credential = "demand-series-retained-area-secret";
        const string snapshotReference = "snapshot-demand-area-a";
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("demand-series-retained-area", credential)
            {
                Overview = FakeHostReply.Select<WatchOverviewQuery, WatchOverviewSnapshot>(query =>
                    FakeHostReply.Return(CreateOverview(query.MesAreas ?? []))),
                DemandSeries = FakeHostReply.Select<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>(query =>
                    query.Filter.MesAreas.SequenceEqual(["B2-2"], StringComparer.Ordinal)
                        ? FakeHostReply.Fail<DemandSeriesListSnapshot>(
                            WatchHostFailureKind.ServerQuery,
                            "/api/v2/demand-series",
                            "AREA B demand projection is unavailable")
                        : FakeHostReply.Return(CreateDemandSeriesList(
                            query,
                            "series-retained-area-a",
                            snapshotReference))),
                ReadabilityAudit = FakeHostReply.Select<ReadabilityAuditQuery, ReadabilityAuditListSnapshot>(query =>
                    FakeHostReply.Return(
                        WatchReadabilityAuditProductionIntegrationTests.CreateAuditList(
                            query,
                            "snapshot-audit-support"))),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl, credential),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 1440;
                window.Height = 900;
                window.Show();
                await window.InitializeAsync(timeout.Token);
                await window.ApplyAreaContextAsync(
                    new WatchAreaDisplayContext(
                        "AREA A 本机筛选",
                        ["A1-1"],
                        "本机已应用",
                        DateTimeOffset.Parse("2026-08-14T05:05:00Z")),
                    timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeries,
                    PageNumber: 1,
                    MesAreas: ["A1-1"],
                    Cursor: null));
                await window.DemandSeriesNavigationTask.WaitAsync(timeout.Token);

                await window.ApplyAreaContextAsync(
                    new WatchAreaDisplayContext(
                        "AREA B 本机筛选",
                        ["B2-2"],
                        "本机已应用",
                        DateTimeOffset.Parse("2026-08-14T05:10:00Z")),
                    timeout.Token);
                window.UpdateLayout();

                var retained = Assert.IsType<DemandSeriesListSnapshot>(
                    window.WorkspaceState.DemandSeries.Snapshot);
                Assert.True(window.WorkspaceState.DemandSeries.IsStale);
                Assert.Equal(snapshotReference, retained.SnapshotReference);
                Assert.Equal(["A1-1"], retained.Filter.MesAreas);
                Assert.Equal(["B2-2"], window.AreaContext.MesAreas);
                Assert.Contains(host.Timeline, entry =>
                    entry.Operation == FakeHostOperation.DemandSeriesV2
                    && entry.State == FakeHostRequestState.Failed
                    && entry.Endpoint.Contains("area=B2-2", StringComparison.Ordinal));

                var context = Find<TextBlock>(window, "DemandSeriesContextText");
                Assert.True(context.IsVisible);
                Assert.Equal(TextWrapping.NoWrap, context.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, context.TextTrimming);
                Assert.Contains("本机 AREA B 本机筛选", context.Text, StringComparison.Ordinal);
                Assert.Contains("Host A1-1", context.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("Host B2-2", context.Text, StringComparison.Ordinal);
                var fullContext = Assert.IsType<string>(context.ToolTip);
                Assert.Contains("本机 AREA：AREA B 本机筛选", fullContext, StringComparison.Ordinal);
                Assert.Contains("Host 已提交范围：A1-1", fullContext, StringComparison.Ordinal);
                Assert.Equal(fullContext, AutomationProperties.GetHelpText(context));
                Assert.Contains(
                    fullContext,
                    AutomationProperties.GetName(context),
                    StringComparison.Ordinal);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Overview_drill_loads_the_current_area_first_page_then_same_snapshot_detail_and_exposes_the_host_exact_total_to_uia()
    {
        const string credential = "demand-series-production-secret";
        const string seriesId = "series-current-area";
        const string snapshotReference = "snapshot-demand-series-20";
        var listGate = new FakeHostGate();
        var listQueryReceived = new TaskCompletionSource<DemandSeriesBrowseQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detailRequestReceived = new TaskCompletionSource<FakeHostV2DetailRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("demand-series-production", credential)
            {
                Overview = FakeHostReply.Select<WatchOverviewQuery, WatchOverviewSnapshot>(query =>
                    FakeHostReply.Return(CreateOverview(query.MesAreas ?? []))),
                DemandSeries = FakeHostReply.Select<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>(query =>
                {
                    if (query.Filter.Lifecycles.Contains(
                            DemandSeriesLifecycleContract.Tracking,
                            StringComparer.Ordinal))
                    {
                        listQueryReceived.TrySetResult(query);
                        return FakeHostReply.After(
                            listGate,
                            CreateDemandSeriesList(query, seriesId, snapshotReference));
                    }

                    return FakeHostReply.Return(
                        CreateDemandSeriesList(query, seriesId, "snapshot-area-refresh-21"));
                }),
                DemandSeriesDetail = FakeHostReply.Select<FakeHostV2DetailRequest, DemandSeriesDetailSnapshot>(request =>
                {
                    detailRequestReceived.TrySetResult(request);
                    return FakeHostReply.Return(CreateDemandSeriesDetail(
                        seriesId,
                        snapshotReference));
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl, credential),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 1440;
                window.Height = 900;
                window.Show();
                SetClientSizeInEpx(window, 1440, 900);
                await WaitForCompactNavigationAsync(window);
                await window.InitializeAsync(timeout.Token);
                await window.ApplyAreaContextAsync(
                    new WatchAreaDisplayContext(
                        "当前封装 AREA",
                        ["A1-1"],
                        "本机已应用",
                        DateTimeOffset.Parse("2026-08-14T05:05:00Z")),
                    timeout.Token);
                var navigationTimelineStart = host.Timeline.Count;
                var intent = new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeriesDetail,
                    PageNumber: 1,
                    MesAreas: ["A1-1"],
                    Lifecycles: [DemandSeriesLifecycleContract.Tracking],
                    SeriesId: seriesId,
                    PollTraceId: "poll-overview-drill",
                    Cursor: null);

                window.NavigateFromOverview(intent);

                var listQuery = await listQueryReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(1, listQuery.PageNumber);
                Assert.Null(listQuery.SnapshotReference);
                Assert.Null(listQuery.Cursor);
                Assert.Equal(["A1-1"], listQuery.Filter.MesAreas);
                Assert.Equal([DemandSeriesLifecycleContract.Tracking], listQuery.Filter.Lifecycles);
                Assert.Equal(seriesId, listQuery.Filter.SeriesId);
                Assert.Equal(WatchWorkspacePage.DemandSeries, window.ActivePage);
                Assert.DoesNotContain(host.Timeline, entry =>
                    entry.Operation == FakeHostOperation.DemandSeriesDetailV2);
                Assert.False(detailRequestReceived.Task.IsCompleted);

                listGate.Release();
                await window.DemandSeriesNavigationTask.WaitAsync(timeout.Token);

                var detailRequest = await detailRequestReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(seriesId, detailRequest.ObjectId);
                Assert.Equal(snapshotReference, detailRequest.SnapshotReference);
                Assert.Equal(
                    [
                        (FakeHostOperation.DemandSeriesV2, FakeHostRequestState.Started),
                        (FakeHostOperation.DemandSeriesV2, FakeHostRequestState.Completed),
                        (FakeHostOperation.DemandSeriesDetailV2, FakeHostRequestState.Started),
                        (FakeHostOperation.DemandSeriesDetailV2, FakeHostRequestState.Completed),
                    ],
                    host.Timeline
                        .Skip(navigationTimelineStart)
                        .Where(entry => entry.Operation is FakeHostOperation.DemandSeriesV2
                            or FakeHostOperation.DemandSeriesDetailV2)
                        .Select(entry => (entry.Operation, entry.State))
                        .ToArray());

                var committed = Assert.IsType<DemandSeriesListSnapshot>(
                    window.WorkspaceState.DemandSeries.Snapshot);
                var detail = Assert.IsType<DemandSeriesDetailSnapshot>(
                    window.WorkspaceState.DemandSeries.Detail);
                Assert.Equal(1, committed.ExactTotalCount);
                Assert.Equal(snapshotReference, committed.SnapshotReference);
                Assert.Equal(snapshotReference, detail.SnapshotReference);
                Assert.Equal(seriesId, window.WorkspaceState.DemandSeries.SelectedId);

                var pageSummary = Find<TextBlock>(window, "DemandSeriesPageSummaryText");
                Assert.Equal("精确 1 个 Series · 第 1 / 1 页", pageSummary.Text);
                Assert.Contains(
                    pageSummary.Text,
                    AutomationProperties.GetName(pageSummary),
                    StringComparison.Ordinal);
                var grid = Find<DataGrid>(window, "DemandSeriesGrid");
                Assert.Equal("需求系列列表", AutomationProperties.GetName(grid));
                Assert.Single(grid.Items);
                Assert.Equal(
                    "Tracking 1",
                    Find<TextBlock>(window, "DemandSeriesTrackingFacetText").Text);
                Assert.Equal(
                    "Archived 0",
                    Find<TextBlock>(window, "DemandSeriesArchivedFacetText").Text);

                // The source-comparison notice belongs to the drill transition. The settled
                // page keeps its compact summary visible while the detailed notice is folded.
                var infoExpander = Find<Wpf.Ui.Controls.CardExpander>(
                    window,
                    "DemandSeriesInfoExpander");
                infoExpander.IsExpanded = false;
                window.UpdateLayout();
                Assert.Equal(
                    Visibility.Visible,
                    Find<Wpf.Ui.Controls.TextBlock>(
                        window,
                        "DemandSeriesInfoHeaderSummary").Visibility);
                var context = Find<TextBlock>(window, "DemandSeriesContextText");
                Assert.Equal(TextWrapping.NoWrap, context.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, context.TextTrimming);
                Assert.InRange(context.ActualHeight, 1, 22);
                Assert.Contains("当前封装 AREA", context.Text, StringComparison.Ordinal);
                Assert.Contains("Host A1-1", context.Text, StringComparison.Ordinal);
                Assert.Contains("最近成功", context.Text, StringComparison.Ordinal);
                Assert.Matches(@"自动刷新 \d+ 秒$", context.Text);
                var fullContext = Assert.IsType<string>(context.ToolTip);
                Assert.Equal(fullContext, AutomationProperties.GetHelpText(context));
                Assert.Contains("commit-demand-series-20", fullContext, StringComparison.Ordinal);

                var master = Find<Border>(window, "DemandSeriesMasterPanel");
                var contextBottom = context.TranslatePoint(
                    new Point(0, context.ActualHeight),
                    window).Y;
                var masterTop = master.TranslatePoint(new Point(), window).Y;
                Assert.True(
                    contextBottom <= masterTop,
                    $"Demand source context must precede the full-height list; "
                    + $"contextBottom={contextBottom:0.##}, masterTop={masterTop:0.##}.");
                Assert.Equal(4, Grid.GetRow(master));
                Assert.Null(window.FindName("DemandSeriesDetailPanel"));
                Assert.Null(window.FindName("DemandSeriesMasterDetailSplitter"));
                Assert.Null(window.FindName("DemandSeriesDetailVisibilityToggle"));

                Assert.Equal(
                    [
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
                    ],
                    grid.Columns.Select(column => column.Header?.ToString() ?? string.Empty).ToArray());
                AssertHorizontalScrollRequired(
                    grid,
                    "Demand master must preserve readable column widths through horizontal scrolling in the 1440 viewport");

                var openInspector = Find<Button>(window, "DemandSeriesOpenInspectorButton");
                Assert.True(openInspector.IsEnabled);
                Assert.Equal("打开详情窗口", openInspector.Content);
                openInspector.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.Background);

                var inspectorWindow = Assert.IsType<WatchDemandSeriesInspectorWindow>(
                    window.DemandSeriesInspectorCoordinator.CurrentWindow);
                Assert.True(inspectorWindow.IsVisible);
                Assert.Null(inspectorWindow.Owner);
                Assert.False(inspectorWindow.Topmost);
                Assert.True(inspectorWindow.ShowInTaskbar);
                Assert.Equal("显示详情窗口", openInspector.Content);
                var inspectorPresentation = Assert.IsType<WatchDemandSeriesInspectorPresentation>(
                    inspectorWindow.DataContext);
                Assert.Equal(seriesId, inspectorPresentation.SeriesId);
                Assert.Equal(snapshotReference, inspectorPresentation.FrozenSnapshot.SnapshotReference);
                var tabs = Assert.IsType<TabControl>(
                    inspectorWindow.FindName("DemandSeriesInspectorTabs"));
                Assert.Equal(
                    ["世代分析", "事件"],
                    tabs.Items.Cast<TabItem>()
                        .Select(item => item.Header?.ToString() ?? string.Empty)
                        .ToArray());
                Assert.Equal(
                    "DemandSeriesInspectorGenerationList",
                    AutomationProperties.GetAutomationId(Assert.IsType<ListBox>(
                        inspectorWindow.FindName("DemandSeriesInspectorGenerationList"))));
                Assert.Equal(
                    "DemandSeriesInspectorFormationReason",
                    AutomationProperties.GetAutomationId(Assert.IsAssignableFrom<TextBlock>(
                        inspectorWindow.FindName("DemandSeriesInspectorFormationReasonText"))));
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Out_of_area_drill_requires_explicit_all_area_confirmation_before_detail_is_read()
    {
        const string credential = "demand-series-area-confirm-secret";
        const string seriesId = "series-outside-current-area";
        var scopedQueryReceived = new TaskCompletionSource<DemandSeriesBrowseQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allAreaQueryReceived = new TaskCompletionSource<DemandSeriesBrowseQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detailRequestReceived = new TaskCompletionSource<FakeHostV2DetailRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("demand-series-area-confirm", credential)
            {
                Overview = FakeHostReply.Select<WatchOverviewQuery, WatchOverviewSnapshot>(query =>
                    FakeHostReply.Return(CreateOverview(query.MesAreas ?? []))),
                DemandSeries = FakeHostReply.Select<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>(query =>
                {
                    if (query.Filter.MesAreas.Count > 0)
                    {
                        scopedQueryReceived.TrySetResult(query);
                        return FakeHostReply.Return(CreateEmptyDemandSeriesList(
                            query,
                            "snapshot-current-area-empty"));
                    }

                    allAreaQueryReceived.TrySetResult(query);
                    return FakeHostReply.Return(CreateDemandSeriesList(
                        query,
                        seriesId,
                        "snapshot-all-areas"));
                }),
                DemandSeriesDetail = FakeHostReply.Select<FakeHostV2DetailRequest, DemandSeriesDetailSnapshot>(request =>
                {
                    detailRequestReceived.TrySetResult(request);
                    return FakeHostReply.Return(CreateDemandSeriesDetail(
                        seriesId,
                        "snapshot-all-areas"));
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl, credential),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                await window.ApplyAreaContextAsync(
                    new WatchAreaDisplayContext(
                        "当前封装 AREA",
                        ["A1-1"],
                        "本机已应用",
                        DateTimeOffset.Parse("2026-08-14T05:05:00Z")),
                    timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeriesDetail,
                    MesAreas: ["A1-1"],
                    SeriesId: seriesId));

                await window.DemandSeriesNavigationTask.WaitAsync(timeout.Token);
                var scoped = await scopedQueryReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(["A1-1"], scoped.Filter.MesAreas);
                Assert.False(detailRequestReceived.Task.IsCompleted);
                Assert.Null(window.WorkspaceState.DemandSeries.Detail);
                Assert.Equal(
                    Visibility.Visible,
                    Find<Wpf.Ui.Controls.InfoBar>(window, "DemandSeriesEmptyState").Visibility);
                Assert.False(Find<TextBox>(window, "DemandSeriesPageNumberInput").IsEnabled);
                Assert.False(Find<ButtonBase>(window, "DemandSeriesGoToPageButton").IsEnabled);
                var confirmation = Find<Border>(
                    window,
                    "DemandSeriesAllAreasConfirmPanel");
                Assert.Equal(Visibility.Visible, confirmation.Visibility);

                await window.ConfirmDemandSeriesAllAreasAsync(timeout.Token);

                var allAreas = await allAreaQueryReceived.Task.WaitAsync(timeout.Token);
                Assert.Empty(allAreas.Filter.MesAreas);
                var detail = await detailRequestReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(seriesId, detail.ObjectId);
                Assert.Equal("snapshot-all-areas", detail.SnapshotReference);
                Assert.Empty(window.AreaContext.MesAreas);
                Assert.Equal("本机已应用", window.AreaContext.LocalState);
                Assert.NotNull(window.AreaContext.LastUpdatedAt);
                Assert.Equal(Visibility.Collapsed, confirmation.Visibility);
                Assert.Equal(seriesId, window.WorkspaceState.DemandSeries.SelectedId);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Filter_apply_and_next_page_use_host_queries_and_one_frozen_snapshot()
    {
        const string credential = "demand-series-filter-page-secret";
        var initialQueryReceived = new TaskCompletionSource<DemandSeriesBrowseQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var filteredQueryReceived = new TaskCompletionSource<DemandSeriesBrowseQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nextQueryReceived = new TaskCompletionSource<DemandSeriesBrowseQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("demand-series-filter-page", credential)
            {
                Overview = FakeHostReply.Select<WatchOverviewQuery, WatchOverviewSnapshot>(query =>
                    FakeHostReply.Return(CreateOverview(query.MesAreas ?? []))),
                DemandSeries = FakeHostReply.Select<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>(query =>
                {
                    if (query.Cursor is not null)
                    {
                        nextQueryReceived.TrySetResult(query);
                        return FakeHostReply.Return(CreatePagedDemandSeriesList(
                            query,
                            "snapshot-filtered",
                            pageNumber: 2,
                            totalPages: 2,
                            nextCursor: null,
                            seriesId: "series-filtered-page-two"));
                    }

                    if (query.Filter.Lifecycles.Contains(
                        DemandSeriesLifecycleContract.Archived,
                        StringComparer.Ordinal))
                    {
                        filteredQueryReceived.TrySetResult(query);
                        return FakeHostReply.Return(CreatePagedDemandSeriesList(
                            query,
                            "snapshot-filtered",
                            pageNumber: 1,
                            totalPages: 2,
                            nextCursor: "cursor-filtered-page-two",
                            seriesId: "series-filtered-page-one"));
                    }

                    initialQueryReceived.TrySetResult(query);
                    return FakeHostReply.Return(CreatePagedDemandSeriesList(
                        query,
                        "snapshot-initial",
                        pageNumber: 1,
                        totalPages: 1,
                        nextCursor: null,
                        seriesId: "series-initial"));
                }),
                DemandSeriesDetail = FakeHostReply.Select<FakeHostV2DetailRequest, DemandSeriesDetailSnapshot>(request =>
                    FakeHostReply.Return(CreateDemandSeriesDetail(
                        request.ObjectId,
                        request.SnapshotReference))),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl, credential),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                await window.ApplyAreaContextAsync(
                    new WatchAreaDisplayContext(
                        "当前封装 AREA",
                        ["A1-1"],
                        "本机已应用",
                        null),
                    timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeries,
                    MesAreas: ["A1-1"]));
                await window.DemandSeriesNavigationTask.WaitAsync(timeout.Token);
                await initialQueryReceived.Task.WaitAsync(timeout.Token);

                var lifecycleAll = Find<ButtonBase>(
                    window,
                    "DemandSeriesLifecycleAllButton");
                var lifecycleTracking = Find<ButtonBase>(
                    window,
                    "DemandSeriesLifecycleTrackingButton");
                var lifecycleArchived = Find<ButtonBase>(
                    window,
                    "DemandSeriesLifecycleArchivedButton");
                Assert.Equal("已选择", AutomationProperties.GetItemStatus(lifecycleAll));
                Assert.Equal("未选择", AutomationProperties.GetItemStatus(lifecycleTracking));
                Assert.Equal("未选择", AutomationProperties.GetItemStatus(lifecycleArchived));

                lifecycleArchived.RaiseEvent(
                    new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("未选择", AutomationProperties.GetItemStatus(lifecycleAll));
                Assert.Equal("未选择", AutomationProperties.GetItemStatus(lifecycleTracking));
                Assert.Equal("已选择", AutomationProperties.GetItemStatus(lifecycleArchived));
                Find<TextBox>(window, "DemandSeriesSublotFilter").Text = "SL-FILTER";
                var workType = Find<ComboBox>(window, "DemandSeriesWorkTypeFilter");
                workType.SelectedIndex = -1;
                workType.Text = "CUSTOM_WIRE_TYPE";
                Assert.True(Find<ButtonBase>(window, "DemandSeriesClearFiltersButton").IsEnabled);
                Find<ButtonBase>(window, "DemandSeriesApplyFiltersButton").RaiseEvent(
                    new RoutedEventArgs(ButtonBase.ClickEvent));

                var filtered = await filteredQueryReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal([DemandSeriesLifecycleContract.Archived], filtered.Filter.Lifecycles);
                Assert.Equal("SL-FILTER", filtered.Filter.SublotContains);
                Assert.Equal(["CUSTOM_WIRE_TYPE"], filtered.Filter.WorkTypes);
                Assert.Equal(["A1-1"], filtered.Filter.MesAreas);
                Assert.Equal(1, filtered.PageNumber);
                Assert.Null(filtered.SnapshotReference);
                Assert.Null(filtered.Cursor);
                await WaitUntilAsync(
                    () => string.Equals(
                        window.WorkspaceState.DemandSeries.Snapshot?.SnapshotReference,
                        "snapshot-filtered",
                        StringComparison.Ordinal),
                    timeout.Token);

                var next = Find<ButtonBase>(window, "DemandSeriesNextButton");
                await WaitUntilAsync(() => next.IsEnabled, timeout.Token);
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                var nextQuery = await nextQueryReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal("snapshot-filtered", nextQuery.SnapshotReference);
                Assert.Equal("cursor-filtered-page-two", nextQuery.Cursor);
                Assert.Equal(1, nextQuery.PageNumber);
                Assert.Equal(filtered.Filter.Lifecycles, nextQuery.Filter.Lifecycles);
                Assert.Equal(filtered.Filter.CurrentPresences, nextQuery.Filter.CurrentPresences);
                Assert.Equal(filtered.Filter.WorkTypes, nextQuery.Filter.WorkTypes);
                Assert.Equal(filtered.Filter.SublotContains, nextQuery.Filter.SublotContains);
                Assert.Equal(filtered.Filter.SeriesId, nextQuery.Filter.SeriesId);
                Assert.Equal(filtered.Filter.DemandId, nextQuery.Filter.DemandId);
                Assert.Equal(filtered.Filter.MesAreas, nextQuery.Filter.MesAreas);
                await WaitUntilAsync(
                    () => window.WorkspaceState.DemandSeries.Snapshot?.PageNumber == 2,
                    timeout.Token);
                await WaitUntilAsync(
                    () => string.Equals(
                        Find<TextBlock>(window, "DemandSeriesPageSummaryText").Text,
                        "精确 101 个 Series · 第 2 / 2 页",
                        StringComparison.Ordinal),
                    timeout.Token);
                Assert.Equal(
                    "精确 101 个 Series · 第 2 / 2 页",
                    Find<TextBlock>(window, "DemandSeriesPageSummaryText").Text);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Readability_audit_drill_carries_the_focused_demand_but_reads_a_current_demand_series_snapshot()
    {
        const string credential = "demand-series-audit-drill-secret";
        const string seriesId = "series-from-audit";
        const string demandId = "demand-current-area";
        var queryReceived = new TaskCompletionSource<DemandSeriesBrowseQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("demand-series-audit-drill", credential)
            {
                Overview = FakeHostReply.Select<WatchOverviewQuery, WatchOverviewSnapshot>(query =>
                    FakeHostReply.Return(CreateOverview(query.MesAreas ?? []))),
                DemandSeries = FakeHostReply.Select<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>(query =>
                {
                    queryReceived.TrySetResult(query);
                    return FakeHostReply.Return(CreateDemandSeriesList(
                        query,
                        seriesId,
                        "snapshot-audit-target"));
                }),
                DemandSeriesDetail = FakeHostReply.Select<FakeHostV2DetailRequest, DemandSeriesDetailSnapshot>(_ =>
                    FakeHostReply.Return(CreateDemandSeriesDetail(
                        seriesId,
                        "snapshot-audit-target"))),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl, credential),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                var context = WatchDemandSeriesNavigationContext.FromReadabilityAudit(
                    CreateReadabilityAuditSource(),
                    seriesId,
                    demandId);

                await window.NavigateToDemandSeriesAsync(context, timeout.Token);

                var query = await queryReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(seriesId, query.Filter.SeriesId);
                Assert.Equal(demandId, query.Filter.DemandId);
                Assert.Equal(["A1-1"], query.Filter.MesAreas);
                Assert.Null(query.SnapshotReference);
                Assert.Equal(seriesId, window.WorkspaceState.DemandSeries.SelectedId);
                var info = Find<Wpf.Ui.Controls.InfoBar>(window, "DemandSeriesInfoBar");
                Assert.Contains("来源 资格审计", info.Message, StringComparison.Ordinal);
                Assert.Contains("目标页快照较来源更新", info.Message, StringComparison.Ordinal);
                Assert.Contains("对象事实未变化", info.Message, StringComparison.Ordinal);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Superseded_navigation_cannot_overwrite_the_newer_default_selection()
    {
        using var files = new TemporaryWatchFiles();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var client = new IgnoringCancellationDemandClient();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions("http://window-operation.test", "window-operation-secret"),
                _ => client,
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeriesDetail,
                    SeriesId: IgnoringCancellationDemandClient.OldSeriesId));
                var superseded = window.DemandSeriesNavigationTask;
                await client.OldRequestStarted.Task.WaitAsync(timeout.Token);

                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeries));
                await window.DemandSeriesNavigationTask.WaitAsync(timeout.Token);
                Assert.Equal(
                    IgnoringCancellationDemandClient.OldSeriesId,
                    window.WorkspaceState.DemandSeries.SelectedId);
                Assert.Equal(
                    "snapshot-current-navigation",
                    window.WorkspaceState.DemandSeries.Snapshot?.SnapshotReference);

                client.ReleaseOldRequest();
                await superseded.WaitAsync(timeout.Token);

                Assert.Equal(
                    IgnoringCancellationDemandClient.OldSeriesId,
                    window.WorkspaceState.DemandSeries.SelectedId);
                Assert.Equal(
                    "snapshot-current-navigation",
                    window.WorkspaceState.DemandSeries.Snapshot?.SnapshotReference);
                Assert.Equal(
                    "snapshot-current-navigation",
                    window.WorkspaceState.DemandSeries.Detail?.SnapshotReference);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Closing_window_makes_a_late_demand_series_continuation_a_neutral_completion()
    {
        using var files = new TemporaryWatchFiles();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var client = new IgnoringCancellationDemandClient();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions("http://window-close.test", "window-close-secret"),
                _ => client,
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            await window.InitializeAsync(timeout.Token);
            window.NavigateFromOverview(new OverviewNavigationIntent(
                OverviewNavigationTargets.DemandSeriesDetail,
                SeriesId: IgnoringCancellationDemandClient.OldSeriesId));
            var closingOperation = window.DemandSeriesNavigationTask;
            await client.OldRequestStarted.Task.WaitAsync(timeout.Token);

            window.Dispose();
            client.ReleaseOldRequest();

            var failure = await Record.ExceptionAsync(() =>
                closingOperation.WaitAsync(timeout.Token));
            Assert.Null(failure);
        });
    }

    [Fact]
    public async Task Area_change_invalidates_the_old_demand_operation_before_waiting_for_overview()
    {
        using var files = new TemporaryWatchFiles();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var client = new IgnoringCancellationDemandClient { GateScopedOverview = true };

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions("http://area-operation.test", "area-operation-secret"),
                _ => client,
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeriesDetail,
                    SeriesId: IgnoringCancellationDemandClient.OldSeriesId));
                var oldNavigation = window.DemandSeriesNavigationTask;
                await client.OldRequestStarted.Task.WaitAsync(timeout.Token);

                var areaChange = window.ApplyAreaContextAsync(
                    new WatchAreaDisplayContext(
                        "新 AREA",
                        ["B2-2"],
                        "本机已应用",
                        DateTimeOffset.Parse("2026-08-14T05:10:00Z")),
                    timeout.Token);
                await client.ScopedOverviewStarted.Task.WaitAsync(timeout.Token);

                client.ReleaseOldRequest();
                await oldNavigation.WaitAsync(timeout.Token);
                client.ReleaseScopedOverview();
                await areaChange.WaitAsync(timeout.Token);

                var newAreaQuery = await client.NewAreaDemandReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(["B2-2"], newAreaQuery.Filter.MesAreas);
                Assert.Equal(["B2-2"], window.WorkspaceState.DemandSeries.Snapshot?.Filter.MesAreas);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Manual_filter_change_suspends_the_old_automatic_target_until_the_new_query_commits()
    {
        using var files = new TemporaryWatchFiles();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var clock = new ManualTimerTimeProvider(DateTimeOffset.Parse("2026-08-14T05:00:00Z"));
        var client = new AutomaticTargetRaceClient();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions("http://auto-target-race.test", "auto-target-secret"),
                _ => client,
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath,
                timeProvider: clock);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeriesDetail,
                    SeriesId: AutomaticTargetRaceClient.AutomaticSeriesId));
                await window.DemandSeriesNavigationTask.WaitAsync(timeout.Token);
                Assert.Equal(1, client.AutomaticSeriesCalls);

                Find<TextBox>(window, "DemandSeriesSeriesIdFilter").Text =
                    AutomaticTargetRaceClient.ManualSeriesId;
                Find<ButtonBase>(window, "DemandSeriesApplyFiltersButton").RaiseEvent(
                    new RoutedEventArgs(ButtonBase.ClickEvent));
                await client.ManualRequestStarted.Task.WaitAsync(timeout.Token);

                clock.Advance(TimeSpan.FromSeconds(10));
                await Dispatcher.Yield(DispatcherPriority.Background);

                Assert.Equal(1, client.AutomaticSeriesCalls);
                client.ReleaseManualRequest();
                await WaitUntilAsync(
                    () => string.Equals(
                        window.WorkspaceState.DemandSeries.Snapshot?.Filter.SeriesId,
                        AutomaticTargetRaceClient.ManualSeriesId,
                        StringComparison.Ordinal),
                    timeout.Token);
                await WaitUntilAsync(
                    () => string.Equals(
                        window.WorkspaceState.DemandSeries.SelectedId,
                        AutomaticTargetRaceClient.ManualSeriesId,
                        StringComparison.Ordinal),
                    timeout.Token);

                Assert.Equal(
                    AutomaticTargetRaceClient.ManualSeriesId,
                    window.WorkspaceState.DemandSeries.Snapshot?.Filter.SeriesId);
                Assert.Equal(
                    AutomaticTargetRaceClient.ManualSeriesId,
                    window.WorkspaceState.DemandSeries.SelectedId);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    private static WatchOptions CreateOptions(string baseUrl, string credential) => new()
    {
        BaseUrl = baseUrl,
        SharedSecret = credential,
        RequestTimeoutSeconds = 30,
        RenderingMode = WatchRenderingMode.SoftwareOnly,
    };

    private static WatchOverviewSnapshot CreateOverview(IReadOnlyList<string> areas)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:00:00Z");
        var identity = new OperationalSnapshotIdentity(
            "commit-overview-20",
            200,
            at,
            "poll-overview-20",
            200,
            20,
            at);
        var series = new OverviewNavigationIntent(
            OverviewNavigationTargets.DemandSeries,
            MesAreas: areas,
            Cursor: null);
        var audit = new OverviewNavigationIntent(
            OverviewNavigationTargets.ReadabilityAudit,
            MesAreas: areas,
            Cursor: null);
        var errors = new OverviewNavigationIntent(
            OverviewNavigationTargets.ErrorSearch,
            MesAreas: areas,
            ErrorWindow: ErrorSearchWindowKinds.Last7Days,
            Cursor: null);
        var attention = new OverviewNavigationIntent(
            OverviewNavigationTargets.CurrentIngestAttention,
            MesAreas: areas,
            Cursor: null);
        return new WatchOverviewSnapshot(
            identity,
            areas,
            new WatchOverviewSeriesSummary(1, 1, 0, 0, 0, series, series, series, series, series),
            new WatchOverviewReadabilitySummary(1, 1, 0, audit, audit, audit),
            new WatchOverviewErrorSummary(0, 0, errors, errors, errors),
            new WatchOverviewAttentionSummary(0, [], [], attention),
            [],
            WatchOverviewRecentActivityStates.NoRecentHighlights,
            WatchOverviewRecentActivityStates.NoRecentHighlightsMessage);
    }

    internal static DemandSeriesListSnapshot CreateDemandSeriesList(
        DemandSeriesBrowseQuery query,
        string seriesId,
        string snapshotReference)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        return new DemandSeriesListSnapshot(
            CreateDemandSeriesIdentity(at),
            snapshotReference,
            query.Filter,
            query.Order,
            ExactTotalCount: 1,
            new DemandSeriesFacets(1, 0, 1, 0, 0),
            query.PageSize,
            query.PageNumber,
            TotalPages: 1,
            [new DemandSeriesListItemSnapshot(
                seriesId,
                "WIRE_TO_GATE",
                "SL-CURRENT-AREA",
                DemandSeriesLifecycleContract.Tracking,
                "VISIBLE",
                at.AddHours(-2),
                ArchivedAt: null,
                "demand-current-area",
                CurrentGeneration: 2,
                CurrentDemandStatus: "VISIBLE",
                DemandLastSeenAt: at.AddMinutes(-1),
                GoneConfirmedAt: null,
                new LiveMesFieldSetSnapshot("A1-1", "EQP-20", "STEP-20", at.AddDays(-1), "PKG-20"),
                ExternalReadabilityState: "READABLE",
                ReadabilityBlockers: [],
                LastSeriesSequence: 5,
                LatestPollTraceId: "poll-demand-series-20",
                LatestProjectionCommitId: "commit-demand-series-20")],
            NextCursor: null,
            HasMore: false);
    }

    private static DemandSeriesListSnapshot CreateEmptyDemandSeriesList(
        DemandSeriesBrowseQuery query,
        string snapshotReference)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        return new DemandSeriesListSnapshot(
            CreateDemandSeriesIdentity(at),
            snapshotReference,
            query.Filter,
            query.Order,
            ExactTotalCount: 0,
            new DemandSeriesFacets(0, 0, 0, 0, 0),
            query.PageSize,
            PageNumber: 1,
            TotalPages: 0,
            Items: [],
            NextCursor: null,
            HasMore: false);
    }

    private static DemandSeriesListSnapshot CreatePagedDemandSeriesList(
        DemandSeriesBrowseQuery query,
        string snapshotReference,
        int pageNumber,
        int totalPages,
        string? nextCursor,
        string seriesId) => CreateDemandSeriesList(
            query,
            seriesId,
            snapshotReference) with
        {
            ExactTotalCount = 101,
            PageNumber = pageNumber,
            TotalPages = totalPages,
            NextCursor = nextCursor,
            HasMore = nextCursor is not null,
        };

    internal static DemandSeriesDetailSnapshot CreateDemandSeriesDetail(
        string seriesId,
        string snapshotReference)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var demand = new TransportDemandSnapshot(
            "demand-current-area",
            seriesId,
            Generation: 2,
            PredecessorDemandId: "demand-prior-area",
            Status: "VISIBLE",
            CreatedAt: at.AddMinutes(-30),
            DemandLastSeenAt: at.AddMinutes(-1),
            GoneConfirmedAt: null,
            CreatedPollTraceId: "poll-demand-created-20",
            CreatedProjectionCommitId: "commit-demand-created-20",
            LatestProjectionCommitId: "commit-demand-series-20",
            new LiveMesFieldSetSnapshot("A1-1", "EQP-20", "STEP-20", at.AddDays(-1), "PKG-20"),
            ExternalReadabilityState: "READABLE",
            ReadabilityBlockers: [],
            LatestObservationPollTraceId: "poll-demand-series-20",
            LatestObservationProjectionCommitId: "commit-demand-series-20",
            LatestObservationAt: at);
        return new DemandSeriesDetailSnapshot(
            CreateDemandSeriesIdentity(at),
            snapshotReference,
            new DemandSeriesSnapshot(
                seriesId,
                "WIRE_TO_GATE",
                "SL-CURRENT-AREA",
                DemandSeriesLifecycleContract.Tracking,
                "VISIBLE",
                at.AddHours(-2),
                "poll-demand-created-20",
                "commit-demand-created-20",
                "commit-demand-series-20",
                demand,
                [demand],
                [],
                [new DemandSeriesEventSnapshot(
                    "event-current-area",
                    seriesId,
                    5,
                    "DEMAND_OBSERVED",
                    at,
                    "DEMAND",
                    demand.DemandId,
                    "poll-demand-series-20",
                    "commit-demand-series-20",
                    1,
                    "{}")],
                [],
                [
                    CreateErrorPeriod(
                        "period-one",
                        "evidence-period-one",
                        at.AddMinutes(-5)),
                    CreateErrorPeriod(
                        "period-two",
                        "evidence-period-two",
                        at.AddMinutes(-2)),
                ],
                ArchivedAt: null,
                LastSeriesSequence: 5));
    }

    private static DemandSeriesErrorPeriodSnapshot CreateErrorPeriod(
        string periodId,
        string evidenceId,
        DateTimeOffset at) => new(
        periodId,
        "DUPLICATE_TRANSPORT_DEMAND_KEY",
        "READABILITY",
        "WARNING",
        "demand-current-area",
        "DEMAND",
        "OBSERVED",
        at,
        EndedAt: null,
        EndReason: null,
        [
            new SeriesErrorPeriodEvidenceSnapshot(
                evidenceId,
                "RAW_OBSERVATION",
                at,
                $"poll-{periodId}",
                $"commit-{periodId}",
                "demand-current-area",
                "2",
                "exactly one raw observation")
        ]);

    private static DemandSeriesSnapshotIdentity CreateDemandSeriesIdentity(DateTimeOffset at) => new(
        "commit-demand-series-20",
        201,
        at,
        "poll-demand-series-20");

    private static ReadabilityAuditListSnapshot CreateReadabilityAuditSource()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:00:00Z");
        return new ReadabilityAuditListSnapshot(
            new ReadabilityAuditSnapshotIdentity(
                "commit-audit-source",
                ProjectionSequence: 200,
                at,
                "poll-audit-source",
                CatalogRevision: 20),
            "snapshot-audit-source",
            new ReadabilityAuditFilter { MesAreas = ["A1-1"] },
            ReadabilityAuditOrder.Default,
            ExactTotalDemandCount: 1,
            new ReadabilityAuditFacets([], []),
            PageSize: 100,
            PageNumber: 1,
            TotalPages: 1,
            Items:
            [
                new ReadabilityAuditListItemSnapshot(
                    "demand-current-area",
                    "series-from-audit",
                    "WIRE_TO_GATE",
                    "SL-CURRENT-AREA",
                    Generation: 2,
                    PredecessorDemandId: "demand-prior-area",
                    DemandStatus: "VISIBLE",
                    SeriesLifecycle: DemandSeriesLifecycleContract.Tracking,
                    SeriesCurrentPresence: "VISIBLE",
                    IsCurrentGeneration: true,
                    DemandCreatedAt: at.AddMinutes(-30),
                    DemandLastSeenAt: at.AddMinutes(-1),
                    GoneConfirmedAt: null,
                    new LiveMesFieldSetSnapshot("A1-1", "EQP-20", "STEP-20", at.AddDays(-1), "PKG-20"),
                    CurrentRawObservationCount: 1,
                    ExternalReadabilityState: "READABLE",
                    LeadReadabilityBlocker: null,
                    ReadabilityBlockers: [],
                    LatestObservationPollTraceId: "poll-audit-source",
                    LatestObservationProjectionCommitId: "commit-audit-source",
                    LatestObservationAt: at)
            ],
            NextCursor: null,
            HasMore: false);
    }

    private static T Find<T>(WatchWorkspaceWindow window, string name)
        where T : class => Assert.IsAssignableFrom<T>(window.FindName(name));

    private static void AssertNoSignificantHorizontalScroll(DataGrid grid, string message)
    {
        grid.ApplyTemplate();
        grid.UpdateLayout();
        var scrollViewer = FindVisualChild<ScrollViewer>(grid);
        Assert.True(
            scrollViewer.ScrollableWidth <= 24,
            $"{message}; scrollableWidth={scrollViewer.ScrollableWidth:0.##}, viewportWidth={scrollViewer.ViewportWidth:0.##}, extentWidth={scrollViewer.ExtentWidth:0.##}.");
    }

    private static void AssertHorizontalScrollRequired(DataGrid grid, string message)
    {
        grid.ApplyTemplate();
        grid.UpdateLayout();
        var scrollViewer = FindVisualChild<ScrollViewer>(grid);
        Assert.True(
            scrollViewer.ScrollableWidth > 24,
            $"{message}; scrollableWidth={scrollViewer.ScrollableWidth:0.##}, viewportWidth={scrollViewer.ViewportWidth:0.##}, extentWidth={scrollViewer.ExtentWidth:0.##}.");
    }

    private static async Task WaitForCompactNavigationAsync(WatchWorkspaceWindow window)
    {
        var navigation = Find<Wpf.Ui.Controls.NavigationView>(
            window,
            "WorkspaceNavigation");
        navigation.ApplyTemplate();
        var paneGrid = Assert.IsAssignableFrom<FrameworkElement>(
            navigation.Template.FindName("PaneGrid", navigation));
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            await window.Dispatcher.InvokeAsync(
                window.UpdateLayout,
                DispatcherPriority.ApplicationIdle);
            if (paneGrid.ActualWidth is >= 39.5 and <= 40.5)
            {
                return;
            }

            await Task.Delay(16, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            $"The compact navigation pane did not settle within two seconds; "
            + $"actual width={paneGrid.ActualWidth:F2} epx.");
    }

    private static void SetClientSizeInEpx(Window window, int width, int height)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        WatchWindowNative.SetClientSize(
            new WindowInteropHelper(window).Handle,
            checked((int)Math.Round(width * dpi.DpiScaleX)),
            checked((int)Math.Round(height * dpi.DpiScaleY)));
    }

    private static T FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            try
            {
                return FindVisualChild<T>(child);
            }
            catch (Xunit.Sdk.XunitException)
            {
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Could not find visual child {typeof(T).Name} under {root.GetType().Name}.");
    }

    private sealed class IgnoringCancellationDemandClient : IWatchV2ApiClient
    {
        public const string OldSeriesId = "series-old-navigation";

        private readonly TaskCompletionSource<DemandSeriesListSnapshot> _oldRequestRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WatchOverviewSnapshot> _scopedOverviewRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _oldSeriesCalls;

        public bool GateScopedOverview { get; init; }

        public TaskCompletionSource OldRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ScopedOverviewStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<DemandSeriesBrowseQuery> NewAreaDemandReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseOldRequest()
        {
            var query = new DemandSeriesBrowseQuery(
                new DemandSeriesBrowseFilter { SeriesId = OldSeriesId });
            _oldRequestRelease.TrySetResult(CreateDemandSeriesList(
                query,
                OldSeriesId,
                "snapshot-old-navigation"));
        }

        public void ReleaseScopedOverview() => _scopedOverviewRelease.TrySetResult(
            CreateOverview(["B2-2"]));

        public Task VerifyContractAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<WatchOverviewSnapshot> FetchOverviewAsync(
            WatchOverviewQuery query,
            CancellationToken cancellationToken = default)
        {
            if (GateScopedOverview && query.MesAreas?.Count > 0)
            {
                ScopedOverviewStarted.TrySetResult();
                return _scopedOverviewRelease.Task;
            }

            return Task.FromResult(CreateOverview(query.MesAreas ?? []));
        }

        public Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
            DemandSeriesBrowseQuery query,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(query.Filter.SeriesId, OldSeriesId, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _oldSeriesCalls) == 1)
                {
                    OldRequestStarted.TrySetResult();
                    return _oldRequestRelease.Task;
                }

                NewAreaDemandReceived.TrySetResult(query);
                return Task.FromResult(CreateDemandSeriesList(
                    query,
                    OldSeriesId,
                    "snapshot-new-area"));
            }

            var oldItem = CreateDemandSeriesList(
                query,
                OldSeriesId,
                "snapshot-current-navigation").Items.Single();
            var currentItem = oldItem with
            {
                SeriesId = "series-current-navigation",
                Sublot = "SL-CURRENT-NAVIGATION",
                CurrentDemandId = "demand-current-navigation",
            };
            return Task.FromResult(CreateDemandSeriesList(
                query,
                OldSeriesId,
                "snapshot-current-navigation") with
            {
                ExactTotalCount = 2,
                Items = [oldItem, currentItem],
            });
        }

        public Task<DemandSeriesDetailSnapshot> FetchDemandSeriesDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDemandSeriesDetail(seriesId, snapshotReference));

        public Task<ReadabilityAuditListSnapshot> FetchReadabilityAuditAsync(
            ReadabilityAuditQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReadabilityAuditDetailSnapshot> FetchReadabilityAuditDetailAsync(
            string demandId,
            string snapshotReference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ErrorSearchListSnapshot> FetchErrorSearchAsync(
            ErrorSearchQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ErrorSearchDetailSnapshot> FetchErrorSearchDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ErrorSearchRawEvidenceSnapshot> FetchErrorRawEvidenceAsync(
            string seriesId,
            string evidenceId,
            string snapshotReference,
            ErrorSearchRawEvidenceQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CurrentIngestAttentionSnapshot> FetchCurrentAttentionAsync(
            CurrentIngestAttentionQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class AutomaticTargetRaceClient : IWatchV2ApiClient
    {
        public const string AutomaticSeriesId = "series-auto-target-a";
        public const string ManualSeriesId = "series-manual-target-b";

        private readonly TaskCompletionSource<DemandSeriesListSnapshot> _manualRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _automaticSeriesCalls;

        public int AutomaticSeriesCalls => Volatile.Read(ref _automaticSeriesCalls);

        public TaskCompletionSource ManualRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseManualRequest()
        {
            var query = new DemandSeriesBrowseQuery(
                new DemandSeriesBrowseFilter { SeriesId = ManualSeriesId });
            _manualRelease.TrySetResult(CreateDemandSeriesList(
                query,
                ManualSeriesId,
                "snapshot-manual-target-b"));
        }

        public Task VerifyContractAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<WatchOverviewSnapshot> FetchOverviewAsync(
            WatchOverviewQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateOverview(query.MesAreas ?? []));

        public Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
            DemandSeriesBrowseQuery query,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(query.Filter.SeriesId, ManualSeriesId, StringComparison.Ordinal))
            {
                ManualRequestStarted.TrySetResult();
                return _manualRelease.Task;
            }

            var call = Interlocked.Increment(ref _automaticSeriesCalls);
            return Task.FromResult(CreateDemandSeriesList(
                query,
                AutomaticSeriesId,
                $"snapshot-auto-target-a-{call}"));
        }

        public Task<DemandSeriesDetailSnapshot> FetchDemandSeriesDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDemandSeriesDetail(seriesId, snapshotReference));

        public Task<ReadabilityAuditListSnapshot> FetchReadabilityAuditAsync(
            ReadabilityAuditQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReadabilityAuditDetailSnapshot> FetchReadabilityAuditDetailAsync(
            string demandId,
            string snapshotReference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ErrorSearchListSnapshot> FetchErrorSearchAsync(
            ErrorSearchQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ErrorSearchDetailSnapshot> FetchErrorSearchDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ErrorSearchRawEvidenceSnapshot> FetchErrorRawEvidenceAsync(
            string seriesId,
            string evidenceId,
            string snapshotReference,
            ErrorSearchRawEvidenceQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CurrentIngestAttentionSnapshot> FetchCurrentAttentionAsync(
            CurrentIngestAttentionQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
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
            Name = "Watch DemandSeries production integration STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class TemporaryWatchFiles : IDisposable
    {
        public TemporaryWatchFiles()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"watch-demand-series-production-{Guid.NewGuid():N}");
            ConnectionPath = Path.Combine(Root, "connection.json");
            WorkspacePath = Path.Combine(Root, "workspace.json");
        }

        public string Root { get; }

        public string ConnectionPath { get; }

        public string WorkspacePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
