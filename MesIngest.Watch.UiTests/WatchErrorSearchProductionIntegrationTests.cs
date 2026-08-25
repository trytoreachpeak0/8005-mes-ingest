using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchErrorSearchProductionIntegrationTests
{
    private const string Credential = "ticket-22-error-search-secret";
    private const string SnapshotReference = "error-search-snapshot-22";
    private const string SeriesId = "SERIES-ERROR-22";
    private static readonly DateTimeOffset ErrorSearchAsOf =
        DateTimeOffset.Parse("2026-08-14T06:00:00Z");

    [Fact]
    public async Task Matching_error_detail_enables_open_series_and_click_reads_a_fresh_demand_series_snapshot()
    {
        var demandQueryReceived = new TaskCompletionSource<DemandSeriesBrowseQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var filter = new ErrorSearchFilter().Normalize();
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-22-error-open-series", Credential)
            {
                Overview = FakeHostReply.Return(CreateOverview()),
                ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
                    FakeHostReply.Return(CreateErrorPage(
                        query,
                        SnapshotReference,
                        pageNumber: 1,
                        totalPages: 1,
                        totalSeriesCount: 1))),
                ErrorSearchDetail = FakeHostReply.Return(CreateErrorDetail(
                    filter,
                    ErrorSearchWindowKinds.Last7Days)),
                DemandSeries = FakeHostReply.Select<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>(query =>
                {
                    demandQueryReceived.TrySetResult(query);
                    return FakeHostReply.Return(
                        WatchDemandSeriesProductionIntegrationTests.CreateDemandSeriesList(
                            query,
                            SeriesId,
                            "demand-series-target-snapshot-22"));
                }),
                DemandSeriesDetail = FakeHostReply.Select<FakeHostV2DetailRequest, DemandSeriesDetailSnapshot>(request =>
                    FakeHostReply.Return(
                        WatchDemandSeriesProductionIntegrationTests.CreateDemandSeriesDetail(
                            request.ObjectId,
                            request.SnapshotReference))),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CreateTimeout();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ErrorSearch,
                    ErrorWindow: ErrorSearchWindowKinds.Last7Days,
                    Cursor: null));
                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);

                var openSeries = Find<ButtonBase>(window, "ErrorSearchOpenSeriesButton");
                Assert.False(openSeries.IsEnabled);

                await window.SelectErrorSeriesAndRenderAsync(SeriesId, timeout.Token);
                Assert.True(openSeries.IsEnabled);

                Click(openSeries);
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);

                var demandQuery = await demandQueryReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(SeriesId, demandQuery.Filter.SeriesId);
                Assert.Null(demandQuery.Filter.DemandId);
                Assert.Empty(demandQuery.Filter.MesAreas);
                Assert.Null(demandQuery.SnapshotReference);
                Assert.Null(demandQuery.Cursor);
                Assert.Equal(WatchWorkspacePage.DemandSeries, window.ActivePage);
                Assert.Equal(SeriesId, window.WorkspaceState.DemandSeries.SelectedId);
                Assert.Contains(
                    "来源 错误检索",
                    Find<Wpf.Ui.Controls.InfoBar>(window, "DemandSeriesInfoBar").Message,
                    StringComparison.Ordinal);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Category_navigation_renders_selected_accent_surface_preserves_hidden_multi_selection_and_applies_host_exact_categories()
    {
        var receivedQueries = new ConcurrentQueue<ErrorSearchQuery>();
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-22-error-category-navigation", Credential)
            {
                Overview = FakeHostReply.Return(CreateOverview()),
                ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
                {
                    receivedQueries.Enqueue(query);
                    return FakeHostReply.Return(CreateErrorPage(
                        query,
                        SnapshotReference,
                        pageNumber: 1,
                        totalPages: 1,
                        totalSeriesCount: 8));
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CreateTimeout();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.Show();
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "ErrorSearchNavigationItem"));
                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);
                window.UpdateLayout();

                var search = Find<TextBox>(window, "ErrorSearchCategorySearchInput");
                var categories = Find<ListBox>(window, "ErrorSearchCategoryList");
                Assert.Equal(SelectionMode.Multiple, categories.SelectionMode);
                Assert.Equal("错误分类搜索", AutomationProperties.GetName(search));
                Assert.Equal("错误分类导航（可多选）", AutomationProperties.GetName(categories));
                Assert.Collection(
                    categories.Items.Cast<object>(),
                    item => Assert.Contains(
                        "DATA_COMPLETENESS",
                        CategoryAutomationName(categories, item),
                        StringComparison.Ordinal),
                    item => Assert.Contains(
                        "DATA_FORMAT",
                        CategoryAutomationName(categories, item),
                        StringComparison.Ordinal),
                    item => Assert.Contains(
                        "OBSERVATION_CONFLICT",
                        CategoryAutomationName(categories, item),
                        StringComparison.Ordinal),
                    item => Assert.Contains(
                        "LIFECYCLE_CONFLICT",
                        CategoryAutomationName(categories, item),
                        StringComparison.Ordinal));

                var completeness = FindCategoryItem(categories, "DATA_COMPLETENESS", "Host 精确 5 个 DemandSeries");
                var format = FindCategoryItem(categories, "DATA_FORMAT", "Host 精确 3 个 DemandSeries");
                categories.SelectedItems.Add(completeness);
                categories.SelectedItems.Add(format);
                Assert.Equal(2, categories.SelectedItems.Count);

                var focusedCategory = Assert.IsType<ListBoxItem>(
                    categories.ItemContainerGenerator.ContainerFromItem(completeness));
                Assert.True(focusedCategory.Focus());
                Assert.True(focusedCategory.IsKeyboardFocusWithin);
                var results = Find<DataGrid>(window, "ErrorSearchSeriesGrid");
                Assert.True(results.Focus());
                await window.Dispatcher.InvokeAsync(
                    window.UpdateLayout,
                    DispatcherPriority.ApplicationIdle);
                Assert.Same(results, Keyboard.FocusedElement);
                Assert.False(focusedCategory.IsKeyboardFocusWithin);
                AssertSelectedCategorySurface(window, categories, completeness);
                AssertSelectedCategorySurface(window, categories, format);

                var compactFacts = Find<TextBlock>(window, "ErrorSearchCompactFactsText");
                Assert.Contains("Host 冻结快照", compactFacts.Text, StringComparison.Ordinal);
                var fullFacts = Assert.IsType<string>(compactFacts.ToolTip);
                Assert.Contains("ErrorSearchAsOf", fullFacts, StringComparison.Ordinal);
                Assert.Contains("UTC 半开窗口", fullFacts, StringComparison.Ordinal);
                Assert.Contains("Host 已提交条件", fullFacts, StringComparison.Ordinal);
                Assert.Contains(
                    "ErrorSearchAsOf",
                    AutomationProperties.GetHelpText(compactFacts),
                    StringComparison.Ordinal);

                search.Text = "FORMAT";
                window.UpdateLayout();
                Assert.Single(categories.Items);
                Assert.Contains(
                    "DATA_FORMAT",
                    CategoryAutomationName(categories, categories.Items[0]),
                    StringComparison.Ordinal);

                Click(Find<ButtonBase>(window, "ErrorSearchApplyFilterButton"));
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);
                Assert.Equal(
                    ["DATA_COMPLETENESS", "DATA_FORMAT"],
                    receivedQueries.Last().Filter.Categories);

                Click(Find<ButtonBase>(window, "ErrorSearchClearFilterButton"));
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);
                Assert.Empty(receivedQueries.Last().Filter.Categories);
                Assert.Empty(categories.SelectedItems);
                Assert.Equal(string.Empty, search.Text);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Overview_and_navigation_load_first_page_then_normalized_filters_render_host_exact_facets_and_same_snapshot_detail()
    {
        var navigationQueries = new ConcurrentQueue<ErrorSearchQuery>();
        var filteredQueryReceived = new TaskCompletionSource<ErrorSearchQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detailReceived = new TaskCompletionSource<FakeHostV2DetailRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedFilter = ExpectedMultiFilter();
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-22-error-search-production", Credential)
            {
                Overview = FakeHostReply.Return(CreateOverview()),
                ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
                {
                    if (query.Filter.Categories.Count == 0)
                    {
                        navigationQueries.Enqueue(query);
                        return FakeHostReply.Return(CreateErrorPage(
                            query,
                            SnapshotReference,
                            pageNumber: 1,
                            totalPages: 1,
                            totalSeriesCount: 1));
                    }

                    filteredQueryReceived.TrySetResult(query);
                    return FakeHostReply.Return(CreateErrorPage(
                        query,
                        SnapshotReference,
                        pageNumber: 1,
                        totalPages: 3,
                        totalSeriesCount: 8,
                        nextCursor: "opaque-error-page-2"));
                }),
                ErrorSearchDetail = FakeHostReply.Select<FakeHostV2DetailRequest, ErrorSearchDetailSnapshot>(request =>
                {
                    detailReceived.TrySetResult(request);
                    return FakeHostReply.Return(CreateErrorDetail(expectedFilter));
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CreateTimeout();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ErrorSearch,
                    PageNumber: 1,
                    ErrorActivityStates: [ErrorSearchActivityStates.Active],
                    ErrorWindow: ErrorSearchWindowKinds.Last15Days,
                    SeriesId: " series-error-22 ",
                    Cursor: null));

                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);
                var overviewQuery = navigationQueries.Last();
                Assert.Equal([ErrorSearchActivityStates.Active], overviewQuery.Filter.ActivityStates);
                Assert.Equal(SeriesId, overviewQuery.Filter.SeriesId);
                Assert.Equal(ErrorSearchWindowKinds.Last15Days, overviewQuery.Window.Kind);
                Assert.Null(overviewQuery.SnapshotReference);
                Assert.Null(overviewQuery.Cursor);
                Assert.Equal(WatchWorkspacePage.ErrorSearch, window.ActivePage);

                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "OverviewNavigationItem"));
                var beforeNavigation = navigationQueries.Count;
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "ErrorSearchNavigationItem"));
                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);
                Assert.True(
                    navigationQueries.Count > beforeNavigation,
                    "The primary navigation item must issue an immediate first-page request.");
                var navigationQuery = navigationQueries.Last();
                Assert.Null(navigationQuery.SnapshotReference);
                Assert.Null(navigationQuery.Cursor);

                SelectErrorCategories(window, "DATA_FORMAT", "DATA_COMPLETENESS");
                Find<ComboBox>(window, "ErrorSearchCodeFilter").Text =
                    " invalid_mes_field_format, required_mes_field_missing ";
                Find<ComboBox>(window, "ErrorSearchActivityStateFilter").Text =
                    " ended, active, ACTIVE ";
                Find<ComboBox>(window, "ErrorSearchWindowFilter").SelectedValue =
                    ErrorSearchWindowKinds.Last15Days;
                Find<TextBox>(window, "ErrorSearchSeriesIdFilter").Text =
                    " series-error-22 ";
                Find<TextBox>(window, "ErrorSearchDemandIdFilter").Text =
                    " demand-error-22 ";
                Find<TextBox>(window, "ErrorSearchSublotFilter").Text =
                    " sublot-error-22 ";
                Find<ComboBox>(window, "ErrorSearchPageSizeInput").SelectedValue =
                    ErrorSearchQuery.MaximumPageSize;
                Click(Find<ButtonBase>(window, "ErrorSearchApplyFilterButton"));

                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);
                var filteredQuery = await filteredQueryReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(
                    ["DATA_COMPLETENESS", "DATA_FORMAT"],
                    filteredQuery.Filter.Categories);
                Assert.Equal(
                    ["INVALID_MES_FIELD_FORMAT", "REQUIRED_MES_FIELD_MISSING"],
                    filteredQuery.Filter.ErrorCodes);
                Assert.Equal(
                    [ErrorSearchActivityStates.Active, ErrorSearchActivityStates.Ended],
                    filteredQuery.Filter.ActivityStates);
                Assert.Equal(SeriesId, filteredQuery.Filter.SeriesId);
                Assert.Equal("DEMAND-ERROR-22", filteredQuery.Filter.DemandId);
                Assert.Equal("SUBLOT-ERROR-22", filteredQuery.Filter.SublotContains);
                Assert.Equal(ErrorSearchWindowKinds.Last15Days, filteredQuery.Window.Kind);
                Assert.Equal(ErrorSearchQuery.MaximumPageSize, filteredQuery.PageSize);
                Assert.Equal(ErrorSearchOrder.Default, filteredQuery.Order);
                Assert.Null(filteredQuery.SnapshotReference);
                Assert.Null(filteredQuery.Cursor);

                var committed = Assert.IsType<ErrorSearchListSnapshot>(
                    window.WorkspaceState.ErrorSearch.Snapshot);
                Assert.Equal(SnapshotReference, committed.SnapshotReference);
                Assert.Equal(ErrorSearchAsOf, committed.Snapshot.ErrorSearchAsOf);
                Assert.Equal(expectedFilter.Categories, committed.Filter.Categories);
                Assert.Equal(expectedFilter.ErrorCodes, committed.Filter.ErrorCodes);
                Assert.Equal(expectedFilter.ActivityStates, committed.Filter.ActivityStates);
                Assert.Equal(expectedFilter.SeriesId, committed.Filter.SeriesId);
                Assert.Equal(expectedFilter.DemandId, committed.Filter.DemandId);
                Assert.Equal(expectedFilter.SublotContains, committed.Filter.SublotContains);
                Assert.Equal(8, committed.TotalSeriesCount);
                Assert.Equal(2, committed.Facets.Categories.Count);
                Assert.Equal(2, committed.Facets.ActivityStates.Count);
                Assert.Single(committed.Items);
                Assert.Equal(SeriesId, committed.Items[0].SeriesId);
                Assert.Equal(2, committed.Items[0].MatchedDemandGenerationCount);

                var categoryNavigation = Find<ListBox>(window, "ErrorSearchCategoryList");
                Assert.Equal(4, categoryNavigation.Items.Count);
                FindCategoryItem(categoryNavigation, "DATA_COMPLETENESS", "Host 精确 5 个 DemandSeries");
                FindCategoryItem(categoryNavigation, "DATA_FORMAT", "Host 精确 3 个 DemandSeries");
                Assert.Equal(2, Find<DataGrid>(window, "ErrorSearchActivityStateFacetGrid").Items.Count);
                Assert.Single(Find<DataGrid>(window, "ErrorSearchSeriesGrid").Items);
                var pageSummary = Find<TextBlock>(window, "ErrorSearchPageSummaryText");
                Assert.Contains("精确 8", pageSummary.Text, StringComparison.Ordinal);
                Assert.Contains("第 1 / 3 页", pageSummary.Text, StringComparison.Ordinal);
                Assert.Contains("2026-08-14", Find<TextBlock>(window, "ErrorSearchSnapshotText").Text, StringComparison.Ordinal);
                var windowText = Find<TextBlock>(window, "ErrorSearchWindowText").Text;
                Assert.Contains("UTC", windowText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("[", windowText, StringComparison.Ordinal);
                Assert.Contains(")", windowText, StringComparison.Ordinal);

                await window.SelectErrorSeriesAndRenderAsync(SeriesId, timeout.Token);
                var detailRequest = await detailReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(SeriesId, detailRequest.ObjectId);
                Assert.Equal(SnapshotReference, detailRequest.SnapshotReference);

                var detail = Assert.IsType<ErrorSearchDetailSnapshot>(
                    window.WorkspaceState.ErrorSearch.Detail);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    window.LoadErrorRawEvidenceAndRenderAsync(
                        "not-current-period",
                        "not-current-evidence",
                        new ErrorSearchRawEvidenceQuery([ErrorSearchRawEvidenceFields.Package]),
                        timeout.Token));
                Assert.Equal(WatchV2DataView.ErrorSearch, window.ActiveAutoRefreshView);
                Assert.Equal(committed.SnapshotReference, detail.SnapshotReference);
                Assert.Equal(committed.Snapshot, detail.Snapshot);
                Assert.Equal(committed.Filter.Categories, detail.Filter.Categories);
                Assert.Equal(committed.Filter.ErrorCodes, detail.Filter.ErrorCodes);
                Assert.Equal(committed.Filter.ActivityStates, detail.Filter.ActivityStates);
                Assert.Equal(committed.Filter.SeriesId, detail.Filter.SeriesId);
                Assert.Equal(committed.Filter.DemandId, detail.Filter.DemandId);
                Assert.Equal(committed.Filter.SublotContains, detail.Filter.SublotContains);
                Assert.Equal(committed.Window, detail.Window);
                Assert.Equal(2, detail.Periods.Count);
                Assert.Contains(detail.Periods, period => period.StartsBeforeWindow);
                Assert.Contains(detail.Periods, period => period.EndsAfterWindow);
                Assert.Contains(detail.Periods, period => period.ActiveAtAsOf);
                Assert.Equal(
                    ["DEMAND-ERROR-21", "DEMAND-ERROR-22"],
                    detail.Periods
                        .SelectMany(period => period.Evidence)
                        .Select(evidence => evidence.DemandId)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal));
                Assert.Contains(
                    detail.Periods.SelectMany(period => period.Evidence),
                    evidence => evidence.RelatedWorkTypes.Count == 2
                        && evidence.DiagnosticValue.Kind
                            == ErrorSearchDiagnosticValueKinds.RawObservationSet
                        && evidence.DiagnosticValue.ObservationCount == 2
                        && evidence.RawEvidenceAvailable);
                Assert.Equal(2, Find<DataGrid>(window, "ErrorSearchPeriodGrid").Items.Count);
                Assert.Empty(Find<DataGrid>(window, "ErrorSearchEvidenceGrid").Items);
                var detailContext = Find<TextBlock>(window, "ErrorSearchDetailContextText").Text;
                Assert.Contains(SeriesId, detailContext, StringComparison.Ordinal);
                Assert.Contains("窗口", detailContext, StringComparison.Ordinal);
                Assert.Contains("Demand", detailContext, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Detail_failure_is_shown_in_the_detail_card_and_retry_recovers_without_staling_the_list()
    {
        var filter = new ErrorSearchFilter().Normalize();
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-22-error-detail-recovery", Credential)
            {
                Overview = FakeHostReply.Return(CreateOverview()),
                ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
                    FakeHostReply.Return(CreateErrorPage(
                        query,
                        SnapshotReference,
                        pageNumber: 1,
                        totalPages: 1,
                        totalSeriesCount: 1))),
                ErrorSearchDetail = FakeHostReply.Sequence<
                    FakeHostV2DetailRequest,
                    ErrorSearchDetailSnapshot>(
                    FakeHostReply.Fail<ErrorSearchDetailSnapshot>(
                        WatchHostFailureKind.ServerQuery,
                        "/api/v2/error-search/{seriesId}",
                        "ticket 22 detail failure"),
                    FakeHostReply.Return(CreateErrorDetail(
                        filter,
                        ErrorSearchWindowKinds.Last7Days))),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CreateTimeout();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ErrorSearch,
                    ErrorWindow: ErrorSearchWindowKinds.Last7Days,
                    Cursor: null));
                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);

                await window.SelectErrorSeriesAndRenderAsync(SeriesId, timeout.Token);
                var failed = window.WorkspaceState.ErrorSearch;
                Assert.Null(failed.Detail);
                Assert.NotNull(failed.DetailLastFailureAt);
                Assert.Equal(WatchHostFailureKind.ServerQuery, failed.DetailFailureKind);
                Assert.Null(failed.LastFailureAt);
                Assert.False(failed.IsStale);
                Assert.False(Find<Wpf.Ui.Controls.InfoBar>(
                    window,
                    "ErrorSearchStatusInfoBar").IsOpen);
                Assert.Contains(
                    "详情读取失败",
                    Find<TextBlock>(window, "ErrorSearchDetailContextText").Text,
                    StringComparison.Ordinal);

                await window.SelectErrorSeriesAndRenderAsync(SeriesId, timeout.Token);
                var recovered = window.WorkspaceState.ErrorSearch;
                Assert.NotNull(recovered.Detail);
                Assert.Null(recovered.DetailLastFailureAt);
                Assert.Equal(WatchHostFailureKind.None, recovered.DetailFailureKind);
                Assert.Null(recovered.DetailFailureCode);
                Assert.False(recovered.IsStale);
                Assert.Contains(
                    "窗口",
                    Find<TextBlock>(window, "ErrorSearchDetailContextText").Text,
                    StringComparison.Ordinal);
                Assert.Equal(WatchV2DataView.ErrorSearch, window.ActiveAutoRefreshView);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Host_opaque_cursor_history_drives_next_previous_and_direct_page_without_fabricated_cursor()
    {
        const string cursor2 = "opaque:ticket-22:page-2";
        const string cursor3 = "opaque:ticket-22:page-3";
        var queries = new ConcurrentQueue<ErrorSearchQuery>();
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-22-error-cursors", Credential)
            {
                Overview = FakeHostReply.Return(CreateOverview()),
                ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
                {
                    queries.Enqueue(query);
                    if (string.Equals(
                            query.Filter.SeriesId,
                            "FAIL-CURSOR-22",
                            StringComparison.Ordinal))
                    {
                        return FakeHostReply.Fail<ErrorSearchListSnapshot>(
                            WatchHostFailureKind.ServerQuery,
                            "/api/v2/error-search",
                            "ticket 22 cursor refresh failure");
                    }

                    return query.Cursor switch
                    {
                        cursor2 => FakeHostReply.Return(CreateErrorPage(
                            query,
                            SnapshotReference,
                            pageNumber: 2,
                            totalPages: 3,
                            totalSeriesCount: 3,
                            nextCursor: cursor3)),
                        cursor3 => FakeHostReply.Return(CreateErrorPage(
                            query,
                            SnapshotReference,
                            pageNumber: 3,
                            totalPages: 3,
                            totalSeriesCount: 3)),
                        null => FakeHostReply.Return(CreateErrorPage(
                            query,
                            SnapshotReference,
                            pageNumber: 1,
                            totalPages: 3,
                            totalSeriesCount: 3,
                            nextCursor: cursor2)),
                        _ => throw new Xunit.Sdk.XunitException(
                            $"The Watch fabricated an unknown cursor '{query.Cursor}'."),
                    };
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CreateTimeout();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ErrorSearch,
                    ErrorWindow: ErrorSearchWindowKinds.Last7Days,
                    Cursor: null));
                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);
                Assert.Equal(1, window.WorkspaceState.ErrorSearch.Snapshot?.PageNumber);
                Assert.Equal(WatchV2DataView.ErrorSearch, window.ActiveAutoRefreshView);

                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    try
                    {
                        await window.SelectErrorSeriesAndRenderAsync(SeriesId, canceled.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation is neutral; the current page still resumes its clock.
                    }
                }
                Assert.Equal(WatchV2DataView.ErrorSearch, window.ActiveAutoRefreshView);

                Click(Find<ButtonBase>(window, "ErrorSearchNextPageButton"));
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);
                var page2Query = queries.Last();
                Assert.Equal(SnapshotReference, page2Query.SnapshotReference);
                Assert.Equal(cursor2, page2Query.Cursor);
                Assert.Equal(2, window.WorkspaceState.ErrorSearch.Snapshot?.PageNumber);

                Click(Find<ButtonBase>(window, "ErrorSearchPreviousPageButton"));
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);
                var previousQuery = queries.Last();
                Assert.Equal(SnapshotReference, previousQuery.SnapshotReference);
                Assert.Null(previousQuery.Cursor);
                Assert.Equal(1, window.WorkspaceState.ErrorSearch.Snapshot?.PageNumber);

                var beforeDirectJump = queries.Count;
                Find<TextBox>(window, "ErrorSearchPageNumberInput").Text = "3";
                Click(Find<ButtonBase>(window, "ErrorSearchGoToPageButton"));
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);

                Assert.Equal(3, window.WorkspaceState.ErrorSearch.Snapshot?.PageNumber);
                var directJumpQueries = queries.Skip(beforeDirectJump).ToArray();
                Assert.Equal(2, directJumpQueries.Length);
                Assert.Equal([cursor2, cursor3], directJumpQueries.Select(query => query.Cursor));
                Assert.All(directJumpQueries, query =>
                    Assert.Equal(SnapshotReference, query.SnapshotReference));
                Assert.Contains(
                    "第 3 / 3 页",
                    Find<TextBlock>(window, "ErrorSearchPageSummaryText").Text,
                    StringComparison.Ordinal);
                var status = Find<Wpf.Ui.Controls.InfoBar>(
                    window,
                    "ErrorSearchStatusInfoBar");
                Assert.False(status.IsOpen);
                Assert.DoesNotContain(
                    "无法执行错误检索操作",
                    status.Title,
                    StringComparison.Ordinal);

                Find<TextBox>(window, "ErrorSearchPageNumberInput").Text = "4";
                Click(Find<ButtonBase>(window, "ErrorSearchGoToPageButton"));
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);
                Assert.Equal(3, window.WorkspaceState.ErrorSearch.Snapshot?.PageNumber);
                Assert.Equal(WatchV2DataView.ErrorSearch, window.ActiveAutoRefreshView);

                Find<TextBox>(window, "ErrorSearchSeriesIdFilter").Text = "fail-cursor-22";
                Click(Find<ButtonBase>(window, "ErrorSearchApplyFilterButton"));
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);
                Assert.Equal(3, window.WorkspaceState.ErrorSearch.Snapshot?.PageNumber);
                Assert.True(window.WorkspaceState.ErrorSearch.IsStale);

                Click(Find<ButtonBase>(window, "ErrorSearchPreviousPageButton"));
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);
                Assert.Equal(2, window.WorkspaceState.ErrorSearch.Snapshot?.PageNumber);
                Assert.Equal(cursor2, queries.Last().Cursor);
                Assert.False(window.WorkspaceState.ErrorSearch.IsStale);
                Assert.False(Find<Wpf.Ui.Controls.InfoBar>(
                    window,
                    "ErrorSearchStatusInfoBar").IsOpen);
                Assert.Equal(WatchV2DataView.ErrorSearch, window.ActiveAutoRefreshView);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Successful_empty_is_distinct_from_refresh_failure_which_retains_the_last_successful_snapshot()
    {
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-22-error-empty-failure", Credential)
            {
                Overview = FakeHostReply.Return(CreateOverview()),
                ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
                {
                    return query.Filter.SeriesId switch
                    {
                        "EMPTY-22" => FakeHostReply.Return(CreateErrorPage(
                            query,
                            "error-empty-snapshot-22",
                            pageNumber: 1,
                            totalPages: 0,
                            totalSeriesCount: 0,
                            includeItem: false)),
                        "RETAIN-22" => FakeHostReply.Return(CreateErrorPage(
                            query,
                            "error-retained-snapshot-22",
                            pageNumber: 1,
                            totalPages: 1,
                            totalSeriesCount: 1)),
                        "FAIL-22" => FakeHostReply.Fail<ErrorSearchListSnapshot>(
                            WatchHostFailureKind.ServerQuery,
                            "/api/v2/error-search",
                            "ticket 22 retained refresh failure"),
                        _ => FakeHostReply.Return(CreateErrorPage(
                            query,
                            SnapshotReference,
                            pageNumber: 1,
                            totalPages: 1,
                            totalSeriesCount: 1)),
                    };
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CreateTimeout();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ErrorSearch,
                    Cursor: null));
                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);
                window.Show();
                window.UpdateLayout();

                await ApplySeriesFilterAsync(window, "empty-22", timeout.Token);
                var empty = window.WorkspaceState.ErrorSearch;
                Assert.Equal("error-empty-snapshot-22", empty.Snapshot?.SnapshotReference);
                Assert.Empty(Assert.IsType<ErrorSearchListSnapshot>(empty.Snapshot).Items);
                Assert.Null(empty.LastFailureAt);
                Assert.False(empty.IsStale);
                Assert.Empty(Find<DataGrid>(window, "ErrorSearchSeriesGrid").Items);
                Assert.Contains(
                    "当前条件",
                    Find<Wpf.Ui.Controls.InfoBar>(window, "ErrorSearchStatusInfoBar").Message,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "没有历史",
                    Find<Wpf.Ui.Controls.InfoBar>(window, "ErrorSearchStatusInfoBar").Message,
                    StringComparison.Ordinal);

                await ApplySeriesFilterAsync(window, "retain-22", timeout.Token);
                var retainedSnapshot = Assert.IsType<ErrorSearchListSnapshot>(
                    window.WorkspaceState.ErrorSearch.Snapshot);
                Assert.Equal("error-retained-snapshot-22", retainedSnapshot.SnapshotReference);
                Assert.Single(retainedSnapshot.Items);

                await ApplySeriesFilterAsync(window, "fail-22", timeout.Token);
                var failed = window.WorkspaceState.ErrorSearch;
                Assert.Same(retainedSnapshot, failed.Snapshot);
                Assert.True(failed.IsStale);
                Assert.NotNull(failed.LastFailureAt);
                Assert.Equal(WatchHostFailureKind.ServerQuery, failed.FailureKind);
                Assert.Single(Find<DataGrid>(window, "ErrorSearchSeriesGrid").Items);
                var retainedCategories = Find<ListBox>(window, "ErrorSearchCategoryList");
                FindCategoryItem(retainedCategories, "DATA_COMPLETENESS", "Host 精确 5 个 DemandSeries");
                FindCategoryItem(retainedCategories, "DATA_FORMAT", "Host 精确 3 个 DemandSeries");
                var failure = Find<Wpf.Ui.Controls.InfoBar>(window, "ErrorSearchStatusInfoBar");
                Assert.True(failure.IsOpen);
                Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Error, failure.Severity);
                Assert.Contains("失败", failure.Message, StringComparison.Ordinal);
                Assert.Contains("保留", failure.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("没有历史", failure.Message, StringComparison.Ordinal);
                Assert.Contains(
                    ErrorSearchAsOf.ToString("yyyy-MM-dd"),
                    Find<TextBlock>(window, "ErrorSearchSnapshotText").Text,
                    StringComparison.Ordinal);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Raw_evidence_grid_tracks_the_selected_period_across_real_selection_and_button_render_cycles()
    {
        var rawRequests = new ConcurrentQueue<FakeHostV2RawEvidenceRequest>();
        var filter = new ErrorSearchFilter().Normalize();
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-22-error-raw-period-selection", Credential)
            {
                Overview = FakeHostReply.Return(CreateOverview()),
                ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
                    FakeHostReply.Return(CreateErrorPage(
                        query,
                        SnapshotReference,
                        pageNumber: 1,
                        totalPages: 1,
                        totalSeriesCount: 1))),
                ErrorSearchDetail = FakeHostReply.Return(CreateErrorDetail(
                    filter,
                    ErrorSearchWindowKinds.Last7Days)),
                ErrorSearchRawEvidence = FakeHostReply.Select<
                    FakeHostV2RawEvidenceRequest,
                    ErrorSearchRawEvidenceSnapshot>(request =>
                {
                    rawRequests.Enqueue(request);
                    return FakeHostReply.Return(CreateRawEvidence());
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CreateTimeout();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ErrorSearch,
                    Cursor: null));
                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);

                var seriesGrid = Find<DataGrid>(window, "ErrorSearchSeriesGrid");
                seriesGrid.SelectedIndex = 0;
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);

                var periodGrid = Find<DataGrid>(window, "ErrorSearchPeriodGrid");
                var evidenceGrid = Find<DataGrid>(window, "ErrorSearchEvidenceGrid");
                var loadRawButton = Find<ButtonBase>(window, "ErrorSearchLoadRawEvidenceButton");
                Assert.Equal(2, periodGrid.Items.Count);
                Assert.Empty(evidenceGrid.Items);
                Assert.False(loadRawButton.IsEnabled);

                periodGrid.SelectedIndex = 0;
                var boundaryEvidence = Assert.IsType<WatchErrorSearchEvidencePresentation>(
                    Assert.Single(evidenceGrid.Items));
                Assert.Equal("EVIDENCE-BOUNDARY-22", boundaryEvidence.EvidenceId);
                evidenceGrid.SelectedIndex = 0;
                Assert.True(loadRawButton.IsEnabled);

                Click(loadRawButton);
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);

                var rawRequest = Assert.Single(rawRequests);
                Assert.Equal("EVIDENCE-BOUNDARY-22", rawRequest.EvidenceId);
                var retainedBoundaryEvidence = Assert.IsType<WatchErrorSearchEvidencePresentation>(
                    Assert.Single(evidenceGrid.Items));
                Assert.Equal("EVIDENCE-BOUNDARY-22", retainedBoundaryEvidence.EvidenceId);

                periodGrid.SelectedIndex = 1;
                var activeEvidence = Assert.IsType<WatchErrorSearchEvidencePresentation>(
                    Assert.Single(evidenceGrid.Items));
                Assert.Equal("EVIDENCE-ACTIVE-22", activeEvidence.EvidenceId);
                evidenceGrid.SelectedIndex = 0;
                Assert.False(loadRawButton.IsEnabled);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Raw_evidence_requires_explicit_allowlisted_bounded_expansion_and_a_raw_failure_is_isolated()
    {
        var rawRequests = new ConcurrentQueue<FakeHostV2RawEvidenceRequest>();
        var rawCall = 0;
        var filter = new ErrorSearchFilter().Normalize();
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-22-error-raw", Credential)
            {
                Overview = FakeHostReply.Return(CreateOverview()),
                ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
                    FakeHostReply.Return(CreateErrorPage(
                        query,
                        SnapshotReference,
                        pageNumber: 1,
                        totalPages: 1,
                        totalSeriesCount: 1))),
                ErrorSearchDetail = FakeHostReply.Return(CreateErrorDetail(
                    filter,
                    ErrorSearchWindowKinds.Last7Days)),
                ErrorSearchRawEvidence = FakeHostReply.Select<FakeHostV2RawEvidenceRequest, ErrorSearchRawEvidenceSnapshot>(request =>
                {
                    rawRequests.Enqueue(request);
                    return Interlocked.Increment(ref rawCall) == 1
                        ? FakeHostReply.Return(CreateRawEvidence())
                        : FakeHostReply.HttpFailure<ErrorSearchRawEvidenceSnapshot>(
                            HttpStatusCode.Forbidden,
                            ErrorSearchErrorCodes.RawAccessDenied,
                            "raw evidence access denied for ticket 22");
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CreateTimeout();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ErrorSearch,
                    Cursor: null));
                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);
                await window.SelectErrorSeriesAndRenderAsync(SeriesId, timeout.Token);
                var committed = Assert.IsType<ErrorSearchListSnapshot>(
                    window.WorkspaceState.ErrorSearch.Snapshot);
                var detail = Assert.IsType<ErrorSearchDetailSnapshot>(
                    window.WorkspaceState.ErrorSearch.Detail);

                var rawQuery = new ErrorSearchRawEvidenceQuery(
                    [
                        ErrorSearchRawEvidenceFields.Area,
                        ErrorSearchRawEvidenceFields.Sublot,
                        ErrorSearchRawEvidenceFields.Area,
                        ErrorSearchRawEvidenceFields.Package,
                    ],
                    MaxItems: 2);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    window.LoadErrorRawEvidenceAndRenderAsync(
                        "PERIOD-NOT-IN-SNAPSHOT-22",
                        "EVIDENCE-NOT-IN-SNAPSHOT-22",
                        rawQuery,
                        timeout.Token));
                Assert.Empty(rawRequests);
                Assert.Equal(WatchV2DataView.ErrorSearch, window.ActiveAutoRefreshView);

                await window.LoadErrorRawEvidenceAndRenderAsync(
                    "PERIOD-BOUNDARY-22",
                    "EVIDENCE-BOUNDARY-22",
                    rawQuery,
                    timeout.Token);

                var request = rawRequests.Single();
                Assert.Equal(SeriesId, request.SeriesId);
                Assert.Equal("EVIDENCE-BOUNDARY-22", request.EvidenceId);
                Assert.Equal(SnapshotReference, request.SnapshotReference);
                Assert.Equal(
                    [
                        ErrorSearchRawEvidenceFields.Area,
                        ErrorSearchRawEvidenceFields.Sublot,
                        ErrorSearchRawEvidenceFields.Package,
                    ],
                    request.Query.Fields);
                Assert.Equal(2, request.Query.MaxItems);
                Assert.Equal(2, Find<DataGrid>(window, "ErrorSearchRawEvidenceGrid").Items.Count);
                var limits = Find<TextBlock>(window, "ErrorSearchRawEvidenceLimitsText").Text;
                Assert.Contains("area", limits, StringComparison.Ordinal);
                Assert.Contains("sublot", limits, StringComparison.Ordinal);
                Assert.Contains("package", limits, StringComparison.Ordinal);
                Assert.Contains("2", limits, StringComparison.Ordinal);
                Assert.Contains("2048", limits, StringComparison.Ordinal);
                Assert.Contains("65536", limits, StringComparison.Ordinal);

                await window.LoadErrorRawEvidenceAndRenderAsync(
                    "PERIOD-BOUNDARY-22",
                    "EVIDENCE-BOUNDARY-22",
                    rawQuery,
                    timeout.Token);

                Assert.Equal(2, rawRequests.Count);
                Assert.Same(committed, window.WorkspaceState.ErrorSearch.Snapshot);
                Assert.Same(detail, window.WorkspaceState.ErrorSearch.Detail);
                Assert.Equal(2, Find<DataGrid>(window, "ErrorSearchRawEvidenceGrid").Items.Count);
                var rawFailure = Find<Wpf.Ui.Controls.InfoBar>(window, "ErrorSearchRawEvidenceInfoBar");
                Assert.True(rawFailure.IsOpen);
                Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Error, rawFailure.Severity);
                Assert.Contains(
                    ErrorSearchErrorCodes.RawAccessDenied,
                    rawFailure.Message,
                    StringComparison.Ordinal);
                Assert.False(window.WorkspaceState.ErrorSearch.IsStale);
                Assert.Equal(WatchV2DataView.ErrorSearch, window.ActiveAutoRefreshView);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    private static async Task ApplySeriesFilterAsync(
        WatchWorkspaceWindow window,
        string seriesId,
        CancellationToken cancellationToken)
    {
        Find<TextBox>(window, "ErrorSearchSeriesIdFilter").Text = seriesId;
        Click(Find<ButtonBase>(window, "ErrorSearchApplyFilterButton"));
        await window.ErrorSearchOperationTask.WaitAsync(cancellationToken);
    }

    internal static ErrorSearchFilter ExpectedMultiFilter() => new ErrorSearchFilter
    {
        Categories = ["DATA_COMPLETENESS", "DATA_FORMAT"],
        ErrorCodes = ["INVALID_MES_FIELD_FORMAT", "REQUIRED_MES_FIELD_MISSING"],
        ActivityStates = [ErrorSearchActivityStates.Active, ErrorSearchActivityStates.Ended],
        SeriesId = SeriesId,
        DemandId = "DEMAND-ERROR-22",
        SublotContains = "SUBLOT-ERROR-22",
    }.Normalize();

    internal static WatchOverviewSnapshot CreateOverview()
    {
        var identity = new OperationalSnapshotIdentity(
            "overview-commit-22",
            220,
            ErrorSearchAsOf,
            "overview-poll-22",
            220,
            22,
            ErrorSearchAsOf);
        var series = new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries);
        var audit = new OverviewNavigationIntent(OverviewNavigationTargets.ReadabilityAudit);
        var errors = new OverviewNavigationIntent(
            OverviewNavigationTargets.ErrorSearch,
            PageNumber: 1,
            ErrorActivityStates: [ErrorSearchActivityStates.Active],
            ErrorWindow: ErrorSearchWindowKinds.Last15Days,
            SeriesId: SeriesId,
            Cursor: null);
        var attention = new OverviewNavigationIntent(
            OverviewNavigationTargets.CurrentIngestAttention,
            PageNumber: 1,
            Cursor: null);
        return new WatchOverviewSnapshot(
            identity,
            ["A1-1"],
            new WatchOverviewSeriesSummary(0, 0, 0, 0, 0, series, series, series, series, series),
            new WatchOverviewReadabilitySummary(0, 0, 0, audit, audit, audit),
            new WatchOverviewErrorSummary(8, 3, errors, errors, errors),
            new WatchOverviewAttentionSummary(4, [], [], attention),
            [],
            WatchOverviewRecentActivityStates.NoRecentHighlights,
            WatchOverviewRecentActivityStates.NoRecentHighlightsMessage);
    }

    internal static ErrorSearchListSnapshot CreateErrorPage(
        ErrorSearchQuery query,
        string snapshotReference,
        int pageNumber,
        int totalPages,
        long totalSeriesCount,
        string? nextCursor = null,
        bool includeItem = true,
        ErrorSearchListItemSnapshot? item = null)
    {
        var normalized = query.NormalizeAndValidate();
        var identity = ErrorIdentity();
        var resolvedWindow = normalized.Window.Resolve(ErrorSearchAsOf);
        var pageItem = item ?? CreateErrorItem(pageNumber);
        return new ErrorSearchListSnapshot(
            snapshotReference,
            identity,
            normalized.Filter,
            resolvedWindow,
            normalized.Order,
            totalSeriesCount,
            new ErrorSearchFacets(
                [
                    new ErrorSearchCategoryFacetSnapshot("DATA_COMPLETENESS", 5),
                    new ErrorSearchCategoryFacetSnapshot("DATA_FORMAT", 3),
                ],
                [
                    new ErrorSearchActivityStateFacetSnapshot(ErrorSearchActivityStates.Active, 3),
                    new ErrorSearchActivityStateFacetSnapshot(ErrorSearchActivityStates.Ended, 5),
                ]),
            normalized.PageSize,
            pageNumber,
            totalPages,
            includeItem ? [pageItem] : [],
            nextCursor,
            nextCursor is not null);
    }

    internal static ErrorSearchListItemSnapshot CreateErrorItem(int pageNumber = 1) => new(
        pageNumber == 1 ? SeriesId : $"SERIES-ERROR-22-PAGE-{pageNumber}",
        "WIRE_TO_GATE",
        "SUBLOT-ERROR-22",
        pageNumber == 3
            ? ErrorSearchActivityStates.Ended
            : ErrorSearchActivityStates.Active,
        [
            new ErrorSearchMatchedErrorSnapshot(
                "REQUIRED_MES_FIELD_MISSING",
                "DATA_COMPLETENESS",
                "ERROR"),
            new ErrorSearchMatchedErrorSnapshot(
                "INVALID_MES_FIELD_FORMAT",
                "DATA_FORMAT",
                "ERROR"),
        ],
        ErrorSearchAsOf.AddMinutes(-pageNumber),
        MatchedPeriodCount: 2,
        MatchedDemandGenerationCount: 2,
        MesArea: "A1-1",
        ErrorSearchMesAreaAvailability.CurrentTrusted);

    internal static ErrorSearchDetailSnapshot CreateErrorDetail(
        ErrorSearchFilter filter,
        string windowKind = ErrorSearchWindowKinds.Last15Days)
    {
        var window = new ErrorSearchWindowSelection(windowKind).Resolve(ErrorSearchAsOf);
        return new ErrorSearchDetailSnapshot(
            SnapshotReference,
            ErrorIdentity(),
            filter.Normalize(),
            window,
            ErrorSearchOrder.Default,
            CreateErrorItem(),
            [
                new ErrorSearchDetailPeriodSnapshot(
                    "PERIOD-BOUNDARY-22",
                    "REQUIRED_MES_FIELD_MISSING",
                    "DATA_COMPLETENESS",
                    "ERROR",
                    "AREA",
                    "DEMAND",
                    "FIRST_OBSERVED",
                    window.FromUtc!.Value.AddHours(-2),
                    ErrorSearchAsOf.AddDays(-20),
                    "RESOLVED",
                    StartsBeforeWindow: true,
                    EndsAfterWindow: false,
                    ActiveAtAsOf: false,
                    [
                        CreateEvidence(
                            "EVIDENCE-BOUNDARY-22",
                            "DEMAND-ERROR-21",
                            ErrorSearchAsOf.AddDays(-29),
                            rawAvailable: true),
                    ]),
                new ErrorSearchDetailPeriodSnapshot(
                    "PERIOD-ACTIVE-22",
                    "INVALID_MES_FIELD_FORMAT",
                    "DATA_FORMAT",
                    "ERROR",
                    "SUBLOT",
                    "DEMAND",
                    "REOPENED_IN_NEXT_DEMAND_GENERATION",
                    ErrorSearchAsOf.AddHours(-2),
                    EndedAt: null,
                    EndReason: null,
                    StartsBeforeWindow: false,
                    EndsAfterWindow: true,
                    ActiveAtAsOf: true,
                    [
                        CreateEvidence(
                            "EVIDENCE-ACTIVE-22",
                            "DEMAND-ERROR-22",
                            ErrorSearchAsOf.AddMinutes(-3),
                            rawAvailable: false),
                    ]),
            ]);
    }

    private static ErrorSearchDetailEvidenceSnapshot CreateEvidence(
        string evidenceId,
        string demandId,
        DateTimeOffset observedAt,
        bool rawAvailable) => new(
            evidenceId,
            "MES_FIELD_DIAGNOSTIC",
            "DEMAND",
            observedAt,
            "error-poll-22",
            "error-commit-22",
            demandId,
            ["WIRE_TO_GATE", "WIRE_TO_NITROGEN"],
            new ErrorSearchDiagnosticValueSnapshot(
                ErrorSearchDiagnosticValueKinds.RawObservationSet,
                ObservationCount: 2,
                Sha256Digest: "AABBCCDD22"),
            "canonical value must be non-empty and match its domain",
            rawAvailable);

    private static ErrorSearchRawEvidenceSnapshot CreateRawEvidence() =>
        WithMeasuredPayloadBytes(new ErrorSearchRawEvidenceSnapshot(
            SnapshotReference,
            ErrorIdentity(),
            SeriesId,
            "PERIOD-BOUNDARY-22",
            "EVIDENCE-BOUNDARY-22",
            "error-poll-22",
            "error-commit-22",
            "DEMAND-ERROR-21",
            [
                ErrorSearchRawEvidenceFields.Area,
                ErrorSearchRawEvidenceFields.Sublot,
                ErrorSearchRawEvidenceFields.Package,
            ],
            ItemCount: 2,
            new ErrorSearchRawEvidenceLimitsSnapshot(
                MaxItems: 2,
                MaxItemBytes: ErrorSearchRawEvidenceLimits.MaximumItemBytes,
                MaxTotalBytes: ErrorSearchRawEvidenceLimits.MaximumTotalBytes),
            PayloadBytes: 0,
            [
                CreateRawItem(1, "A1-1", "PKG-22-A"),
                CreateRawItem(2, "A1-2", "PKG-22-B"),
            ]));

    private static ErrorSearchRawEvidenceSnapshot WithMeasuredPayloadBytes(
        ErrorSearchRawEvidenceSnapshot snapshot)
    {
        var current = snapshot;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var measuredBytes = JsonSerializer.SerializeToUtf8Bytes(current).Length;
            if (measuredBytes == current.PayloadBytes)
            {
                return current;
            }
            current = current with { PayloadBytes = measuredBytes };
        }

        return current with
        {
            PayloadBytes = JsonSerializer.SerializeToUtf8Bytes(current).Length,
        };
    }

    private static ErrorSearchRawEvidenceItemSnapshot CreateRawItem(
        int ordinal,
        string area,
        string package) => new(
            ordinal,
            "error-poll-22",
            "error-commit-22",
            "DEMAND-ERROR-21",
            ErrorSearchAsOf.AddMinutes(-ordinal),
            new Dictionary<string, string?>
            {
                [ErrorSearchRawEvidenceFields.Area] = area,
                [ErrorSearchRawEvidenceFields.Sublot] = "SUBLOT-ERROR-22",
                [ErrorSearchRawEvidenceFields.Package] = package,
            });

    private static ErrorSearchSnapshotIdentity ErrorIdentity() => new(
        HistoryEpoch.FromGuid(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
        ErrorSearchAsOf,
        "error-commit-22",
        222,
        ErrorSearchAsOf,
        "error-poll-22");

    internal static WatchOptions CreateOptions(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        SharedSecret = Credential,
        RequestTimeoutSeconds = 30,
        RenderingMode = WatchRenderingMode.SoftwareOnly,
    };

    internal static CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return timeout;
    }

    internal static void Click(UIElement element) =>
        element.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    internal static void SelectErrorCategories(FrameworkElement window, params string[] values)
    {
        if (window is Window actualWindow && !actualWindow.IsVisible)
        {
            actualWindow.Show();
        }
        window.UpdateLayout();
        var categories = Find<ListBox>(window, "ErrorSearchCategoryList");
        foreach (var value in values)
        {
            categories.SelectedItems.Add(FindCategoryItem(categories, value));
        }
    }

    private static object FindCategoryItem(
        ListBox categories,
        string category,
        string? expectedCount = null)
    {
        var item = categories.Items.Cast<object>().Single(candidate =>
            CategoryAutomationName(categories, candidate).Contains(category, StringComparison.Ordinal));
        if (expectedCount is not null)
        {
            Assert.Contains(
                expectedCount,
                CategoryAutomationName(categories, item),
                StringComparison.Ordinal);
        }
        return item;
    }

    private static string CategoryAutomationName(ListBox categories, object item)
    {
        categories.UpdateLayout();
        var container = Assert.IsType<ListBoxItem>(
            categories.ItemContainerGenerator.ContainerFromItem(item));
        var automationId = AutomationProperties.GetAutomationId(container);
        Assert.StartsWith("ErrorSearchCategory_", automationId, StringComparison.Ordinal);
        return AutomationProperties.GetName(container);
    }

    private static void AssertSelectedCategorySurface(
        FrameworkElement window,
        ListBox categories,
        object item)
    {
        window.UpdateLayout();
        var container = Assert.IsType<ListBoxItem>(
            categories.ItemContainerGenerator.ContainerFromItem(item));
        var selectedBackground = Assert.IsType<SolidColorBrush>(
            window.FindResource("ListBoxItemSelectedBackgroundThemeBrush"));
        var selectedForeground = Assert.IsType<SolidColorBrush>(
            window.FindResource("ListBoxItemSelectedForegroundThemeBrush"));
        Assert.Equal(Color.FromRgb(0x00, 0x67, 0xC0), selectedBackground.Color);
        Assert.Equal(Colors.White, selectedForeground.Color);
        Assert.True(
            ContrastRatio(selectedBackground.Color, selectedForeground.Color) >= 4.5,
            $"Selected category contrast was {ContrastRatio(selectedBackground.Color, selectedForeground.Color):F2}:1.");
        Assert.Same(selectedBackground, container.Background);
        Assert.Same(selectedForeground, container.Foreground);

        var categoryText = VisualDescendants<Wpf.Ui.Controls.TextBlock>(container)
            .Where(text => text.Name.StartsWith("ErrorSearchCategory", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(4, categoryText.Length);
        Assert.All(
            categoryText,
            text => Assert.Equal(
                selectedForeground.Color,
                Assert.IsType<SolidColorBrush>(text.Foreground).Color));

        var surface = Assert.Single(
            VisualDescendants<Border>(container),
            border => string.Equals(
                border.Name,
                "ErrorSearchCategorySurface",
                StringComparison.Ordinal));
        Assert.Equal(
            selectedBackground.Color,
            Assert.IsType<SolidColorBrush>(surface.Background).Color);
        Assert.InRange(surface.ActualWidth, container.ActualWidth - 8, container.ActualWidth);
        Assert.InRange(surface.ActualHeight, container.ActualHeight - 8, container.ActualHeight);

        var width = (int)Math.Round(surface.ActualWidth);
        var height = (int)Math.Round(surface.ActualHeight);
        Assert.True(width > 4 && height > 0, $"Selected category was not arranged: {width}x{height}.");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixel = new byte[4];
        bitmap.CopyPixels(
            new Int32Rect(2, height / 2, 1, 1),
            pixel,
            stride: 4,
            offset: 0);
        var renderedBackplate = Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        Assert.Equal(selectedBackground.Color, renderedBackplate);
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * Linearize(color.R)
        + 0.7152 * Linearize(color.G)
        + 0.0722 * Linearize(color.B);

    private static double Linearize(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
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

    internal static T Find<T>(FrameworkElement root, string name)
        where T : class => Assert.IsAssignableFrom<T>(root.FindName(name));

    internal static Task RunInStaDispatcherAsync(Func<Task> action)
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
            Name = "Watch Ticket 22 Error Search integration STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal sealed class TemporaryWatchFiles : IDisposable
    {
        public TemporaryWatchFiles()
        {
            Root = Path.Combine(Path.GetTempPath(), $"watch-ticket-22-{Guid.NewGuid():N}");
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
