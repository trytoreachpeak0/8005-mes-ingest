using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MesIngest.Core.SeriesProjection;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace MesIngest.Watch;

internal sealed record WatchErrorSearchCategoryNavigationItem(
    string Category,
    string CategoryLabel,
    string CodesSummary,
    long? SeriesCount,
    string SeriesCountText,
    string SeriesCountCaption,
    string AutomationName)
{
    public string AutomationId => $"ErrorSearchCategory_{Category}";
}
internal partial class WatchWorkspaceWindow
{
    private readonly Dictionary<int, string?> _errorSearchCursorsByPage = new();
    private readonly HashSet<string> _selectedErrorSearchCategories = new(StringComparer.Ordinal);
    private WatchErrorRawEvidenceState _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
    private IReadOnlyList<WatchErrorSearchCategoryNavigationItem> _errorSearchCategoryNavigationItems = [];
    private string? _selectedErrorPeriodId;
    private string? _selectedErrorEvidenceId;

    private bool _isRenderingErrorSearch;

    private bool _isSyncingErrorSearchCategoryList;
    private long _errorSearchOperationGeneration;

    internal Task ErrorSearchNavigationTask { get; private set; } = Task.CompletedTask;

    internal Task ErrorSearchOperationTask { get; private set; } = Task.CompletedTask;

    private void InitializeErrorSearchPage()
    {
        WatchGridClipboardBehavior.Attach(ErrorSearchActivityStateFacetGrid, _displayLanguageState, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ErrorSearchSeriesGrid, _displayLanguageState, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ErrorSearchPeriodGrid, _displayLanguageState, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ErrorSearchEvidenceGrid, _displayLanguageState, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ErrorSearchRawEvidenceGrid, _displayLanguageState, preserveSelectionUnit: true);

        PopulateErrorSearchCatalogChoices();

        ResetErrorSearchCursorHistory();
        SyncErrorSearchFilterControls(_errorSearchQuery);
    }

    private void ApplyLocalizedErrorSearchText()
    {
        var text = _displayLanguageState.Catalog.ErrorSearch;
        var bodyOffset = ErrorSearchBodyScrollViewer.VerticalOffset;
        var activityValues = ReadChoiceValues(ErrorSearchActivityStateFilter);
        var windowValue = ReadSingleChoiceValue(ErrorSearchWindowFilter);

        ErrorSearchPageTitleText.Text = text.PageTitle;
        ErrorSearchCategoryTitleText.Text = text.CategoryTitle;
        ErrorSearchCategoryHelpText.Text = text.CategoryHelp;
        ErrorSearchCategorySearchLabel.Text = text.CategorySearch;
        ErrorSearchActivityFacetTitleText.Text = text.ActivityFacetTitle;
        ErrorSearchResultsTitleText.Text = text.ResultsTitle;
        ErrorSearchClearFilterButton.Content = text.ClearFilters;
        ErrorSearchCodeFilterLabel.Text = text.ErrorCode;
        ErrorSearchActivityFilterLabel.Text = text.ActivityState;
        ErrorSearchWindowFilterLabel.Text = text.TimeRange;
        ErrorSearchSeriesIdFilterLabel.Text = text.Pick("SeriesId（精确）", "SeriesId (exact)");
        ErrorSearchApplyFilterButton.Content = text.ApplyFilters;
        ErrorSearchMoreFiltersExpander.Header = text.MoreFilters;
        ErrorSearchDemandIdFilterLabel.Text = text.Pick("DemandId（精确）", "DemandId (exact)");
        ErrorSearchSublotFilterLabel.Text = text.Pick("SUBLOT（包含）", "SUBLOT (contains)");
        ErrorSearchPageSizeLabel.Text = text.PerPage;
        ErrorSearchPreviousPageButton.Content = text.PreviousPage;
        ErrorSearchNextPageButton.Content = text.NextPage;
        ErrorSearchGoToPageButton.Content = text.GoToPage;
        ErrorSearchDetailTitleText.Text = text.DetailTitle;
        ErrorSearchPeriodsTitleText.Text = text.PeriodsTitle;
        ErrorSearchEvidenceTitleText.Text = text.EvidenceTitle;
        ErrorSearchRawEvidenceTitleText.Text = text.RawEvidenceTitle;
        ErrorSearchRawEvidenceHelpText.Text = text.RawEvidenceHelp;
        ErrorSearchLoadRawEvidenceButton.Content = text.LoadRawEvidence;
        ErrorSearchOpenSeriesButton.Content = text.OpenSeries;
        ErrorSearchScopeText.Text = text.Pick(
            "按 Host 冻结快照检索真正命中的错误期间；本机 AREA 配置不参与条件、分面或分页。",
            "Search actually matched error periods in a frozen Host snapshot. Local AREA configuration does not affect filters, facets, or paging.");
        ErrorSearchHistoryInitialText(text);

        PopulateErrorSearchCatalogChoices();
        ErrorSearchActivityStateFilter.Items.Clear();
        foreach (var state in new[] { ErrorSearchActivityStates.Active, ErrorSearchActivityStates.Ended })
        {
            ErrorSearchActivityStateFilter.Items.Add(new ComboBoxItem
            {
                Content = text.CodeWithMeaning(text.DescribeActivityState(state)),
                Tag = state,
            });
        }
        ErrorSearchActivityStateFilter.Tag = text.Pick("全部状态", "All states");
        SelectChoice(ErrorSearchActivityStateFilter, activityValues);

        foreach (var item in ErrorSearchWindowFilter.Items.OfType<ComboBoxItem>())
        {
            var code = item.Tag?.ToString();
            if (!string.IsNullOrWhiteSpace(code))
            {
                item.Content = text.CodeWithMeaning(new WatchCodeMeaning(text.WindowLabel(code), code, true));
            }
        }
        SelectChoice(ErrorSearchWindowFilter, [windowValue]);
        ErrorSearchCodeFilter.Tag = text.Pick("全部错误码", "All error codes");
        if (ReadChoiceValues(ErrorSearchCodeFilter).Count == 0)
        {
            SelectChoice(ErrorSearchCodeFilter, []);
        }

        ErrorSearchActivityFacetStateColumn.Header = text.ActivityState;
        ErrorSearchActivityFacetCountColumn.Header = text.Pick("需求系列", "Demand series");
        ErrorSearchMatchedErrorsColumn.Header = text.ErrorCode;
        ErrorSearchActivityColumn.Header = text.ActivityState;
        ErrorSearchMatchedPeriodsColumn.Header = text.Pick("命中期间", "Matched periods");
        ErrorSearchDemandGenerationsColumn.Header = text.Pick("需求代次", "Demand generations");
        ErrorSearchLatestEvidenceColumn.Header = text.Pick("最新证据", "Latest evidence");

        var periodHeaders = new[]
        {
            text.ErrorCode, text.Pick("分类", "Category"), text.Pick("状态边界", "State boundary"),
            text.Pick("开始", "Started"), text.Pick("结束", "Ended"), text.Pick("结束原因", "End reason"),
            text.Pick("主体种类", "Subject kind"), "Target",
        };
        for (var index = 0; index < periodHeaders.Length; index++)
        {
            ErrorSearchPeriodGrid.Columns[index].Header = periodHeaders[index];
        }
        var evidenceHeaders = new[]
        {
            text.Pick("证据", "Evidence"), text.Pick("证据类型", "Evidence kind"),
            text.Pick("字段 / 主体", "Field / subject"), text.Pick("观测值", "Observed value"),
            text.Pick("规则", "Rule"), "DemandId / WorkType", text.Pick("时间", "Time"), "PollTrace",
        };
        for (var index = 0; index < evidenceHeaders.Length; index++)
        {
            ErrorSearchEvidenceGrid.Columns[index].Header = evidenceHeaders[index];
        }
        ErrorSearchRawEvidenceGrid.Columns[2].Header = text.Pick("时间", "Time");
        ErrorSearchRawEvidenceGrid.Columns[4].Header = text.Pick("白名单字段与值", "Allow-listed fields and values");

        AutomationProperties.SetName(ErrorSearchPage, text.Pick("错误检索页面", "Error search page"));
        AutomationProperties.SetName(ErrorSearchBodyScrollViewer, text.Pick("错误检索三列工作区滚动区域", "Error-search three-column workspace scroller"));
        AutomationProperties.SetName(ErrorSearchCategorySearchInput, text.Pick("错误分类搜索", "Error-category search"));
        AutomationProperties.SetHelpText(ErrorSearchCategorySearchInput, text.Pick("输入分类或错误码的一部分；搜索只缩小左侧导航，不会清除已选择分类。", "Enter part of a category or error code. Search narrows the left navigation without clearing selected categories."));
        AutomationProperties.SetName(ErrorSearchCategoryList, text.Pick("错误分类导航（可多选）", "Error-category navigation (multi-select)"));
        AutomationProperties.SetHelpText(ErrorSearchCategoryList, text.CategoryHelp);
        AutomationProperties.SetName(ErrorSearchActivityStateFacetGrid, text.Pick("错误活动状态 Host 精确分面", "Exact Host facets for error activity state"));
        AutomationProperties.SetName(ErrorSearchSeriesGrid, text.Pick("错误检索去重需求系列结果", "Distinct demand-series Error Search results"));
        AutomationProperties.SetName(ErrorSearchPeriodGrid, text.Pick("错误检索真正命中期间", "Actually matched Error Search periods"));
        AutomationProperties.SetName(ErrorSearchEvidenceGrid, text.Pick("错误检索可解释证据", "Diagnostic Error Search evidence"));
        AutomationProperties.SetName(ErrorSearchRawEvidenceGrid, text.Pick("错误检索受限原始证据", "Restricted Error Search raw evidence"));
        AutomationProperties.SetName(ErrorSearchCodeFilter, text.Pick("错误码筛选", "Error-code filter"));
        AutomationProperties.SetName(ErrorSearchActivityStateFilter, text.Pick("错误活动状态筛选", "Error activity-state filter"));
        AutomationProperties.SetName(ErrorSearchWindowFilter, text.Pick("错误检索时间范围", "Error Search time range"));
        AutomationProperties.SetName(ErrorSearchSeriesIdFilter, text.Pick("错误检索 SeriesId 精确筛选", "Exact SeriesId filter for Error Search"));
        AutomationProperties.SetName(ErrorSearchDemandIdFilter, text.Pick("错误检索 DemandId 精确筛选", "Exact DemandId filter for Error Search"));
        AutomationProperties.SetName(ErrorSearchSublotFilter, text.Pick("错误检索 SUBLOT 包含筛选", "SUBLOT-contains filter for Error Search"));
        AutomationProperties.SetName(ErrorSearchPageSizeInput, text.Pick("错误检索每页数量", "Error Search page size"));
        AutomationProperties.SetName(ErrorSearchPageNumberInput, text.Pick("错误检索目标页码", "Error Search target page"));
        AutomationProperties.SetName(ErrorSearchApplyFilterButton, text.ApplyFilters);
        AutomationProperties.SetName(ErrorSearchClearFilterButton, text.ClearFilters);
        AutomationProperties.SetName(ErrorSearchPreviousPageButton, text.PreviousPage);
        AutomationProperties.SetName(ErrorSearchNextPageButton, text.NextPage);
        AutomationProperties.SetName(ErrorSearchGoToPageButton, text.GoToPage);
        AutomationProperties.SetName(ErrorSearchLoadRawEvidenceButton, text.LoadRawEvidence);
        AutomationProperties.SetName(ErrorSearchOpenSeriesButton, text.OpenSeries);

        RenderErrorSearch(_session.State);
        ErrorSearchBodyScrollViewer.ScrollToVerticalOffset(bodyOffset);
    }

    private void ErrorSearchHistoryInitialText(WatchErrorSearchText text)
    {
        if (_selectedErrorPeriodId is null || _selectedErrorEvidenceId is null)
        {
            ErrorSearchRawEvidenceLimitsText.Text = BuildErrorRawEvidenceLimitsText(text);
        }
    }

    private void PopulateErrorSearchCatalogChoices()
    {
        UpdateErrorSearchCategoryNavigation([]);

        var selectedCodes = ReadChoiceValues(ErrorSearchCodeFilter);
        ErrorSearchCodeFilter.Items.Clear();
        var text = _displayLanguageState.Catalog.ErrorSearch;
        foreach (var definition in SeriesErrorCatalog.Definitions.OrderBy(
                     definition => definition.Code,
                     StringComparer.Ordinal))
        {
            ErrorSearchCodeFilter.Items.Add(new ComboBoxItem
            {
                Content = text.CodeWithMeaning(text.DescribeErrorCode(definition.Code)),
                Tag = definition.Code,
                ToolTip = text.DescribeErrorCode(definition.Code).Description,
            });
        }
        SelectChoice(ErrorSearchCodeFilter, selectedCodes);
    }

    private void UpdateErrorSearchCategoryNavigation(
        IReadOnlyList<WatchErrorSearchCategoryFacetPresentation> facets)
    {
        var counts = facets.ToDictionary(
            facet => facet.Category,
            facet => facet.SeriesCount,
            StringComparer.Ordinal);
        var text = _displayLanguageState.Catalog.ErrorSearch;
        _errorSearchCategoryNavigationItems = SeriesErrorCatalog.Definitions
            .GroupBy(definition => definition.Category, StringComparer.Ordinal)
            .Select(group => new WatchErrorSearchCategoryNavigationItem(
                group.Key,
                text.CodeWithMeaning(text.DescribeCategory(group.Key)),
                string.Join(
                    " · ",
                    group.Select(definition => text.CodeWithMeaning(text.DescribeErrorCode(definition.Code))).Order(StringComparer.Ordinal)),
                counts.TryGetValue(group.Key, out var count) ? count : null,
                counts.TryGetValue(group.Key, out count)
                    ? count.ToString("N0", _displayLanguageState.Catalog.Language == WatchDisplayLanguage.SimplifiedChinese
                        ? CultureInfo.GetCultureInfo("zh-CN")
                        : CultureInfo.GetCultureInfo("en-US"))
                    : _displayLanguageState.Catalog.Common.NotLoaded,
                counts.ContainsKey(group.Key) ? text.Pick("个需求系列", "demand series") : text.Pick("尚无快照", "No snapshot"),
                counts.TryGetValue(group.Key, out count)
                    ? text.Pick($"错误分类 {group.Key}，Host 精确 {count:N0} 个需求系列，可多选", $"Error category {group.Key}; exact Host count {count:N0} demand series; multi-select")
                    : text.Pick($"错误分类 {group.Key}，尚无 Host 快照计数，可多选", $"Error category {group.Key}; no Host snapshot count; multi-select")))
            .ToArray();
        RefreshErrorSearchCategoryNavigation();
    }

    private void RefreshErrorSearchCategoryNavigation()
    {
        var search = ErrorSearchCategorySearchInput.Text.Trim();
        var visibleItems = _errorSearchCategoryNavigationItems
            .Where(item => search.Length == 0
                || item.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.CodesSummary.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        _isSyncingErrorSearchCategoryList = true;
        try
        {
            ErrorSearchCategoryList.UnselectAll();
            ErrorSearchCategoryList.ItemsSource = visibleItems;
            foreach (var item in visibleItems.Where(item =>
                         _selectedErrorSearchCategories.Contains(item.Category)))
            {
                ErrorSearchCategoryList.SelectedItems.Add(item);
            }
        }
        finally
        {
            _isSyncingErrorSearchCategoryList = false;
        }
    }

    private async Task LoadErrorSearchNavigationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshLatestErrorSearchAndRenderAsync(_errorSearchQuery, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || _lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the window or superseding navigation is a neutral end state.
        }
    }


    private async Task RefreshLatestErrorSearchAndRenderAsync(
        ErrorSearchQuery query,
        CancellationToken cancellationToken)
    {
        var operation = BeginErrorSearchOperation();
        try
        {
            if (!await SuspendErrorSearchAutoRefreshAsync(operation, cancellationToken)
                    .ConfigureAwait(true))
            {
                return;
            }

            _errorSearchQuery = WatchErrorSearchQueries.StartLatest(
                query.Filter,
                query.Window,
                query.PageSize);
            var previousSnapshotReference = _session.State.ErrorSearch.Snapshot?.SnapshotReference;
            var refresh = _session.RefreshErrorSearchAsync(_errorSearchQuery, cancellationToken);
            RenderWorkspace();
            await refresh.ConfigureAwait(true);
            if (!IsCurrentErrorSearchOperation(operation, cancellationToken))
            {
                return;
            }

            var view = _session.State.ErrorSearch;
            if (!view.IsStale && view.Snapshot is { } committed)
            {
                ResetErrorSearchCursorHistory();
                TrackErrorSearchPage(committed, cursorUsed: null);
                if (!string.Equals(
                        previousSnapshotReference,
                        committed.SnapshotReference,
                        StringComparison.Ordinal))
                {
                    _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
                    _selectedErrorPeriodId = null;
                    _selectedErrorEvidenceId = null;
                }
            }

            RenderWorkspace();
        }
        finally
        {
            ReactivateErrorSearchIfLatestOperation(operation);
        }
    }

    private async Task RefreshFrozenErrorSearchAndRenderAsync(
        ErrorSearchQuery request,
        int targetPageNumber,
        CancellationToken cancellationToken)
    {
        var operation = BeginErrorSearchOperation();
        try
        {
            if (!await SuspendErrorSearchAutoRefreshAsync(operation, cancellationToken)
                    .ConfigureAwait(true))
            {
                return;
            }

            await RefreshFrozenErrorSearchAndRenderAsync(
                    request,
                    targetPageNumber,
                    operation,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            ReactivateErrorSearchIfLatestOperation(operation);
        }
    }

    private async Task RefreshFrozenErrorSearchAndRenderAsync(
        ErrorSearchQuery request,
        int targetPageNumber,
        long operation,
        CancellationToken cancellationToken)
    {
        _errorSearchQuery = WatchErrorSearchQueries.StartLatest(
            request.Filter,
            request.Window,
            request.PageSize);
        _errorSearchCursorsByPage[targetPageNumber] = request.Cursor;
        var refresh = _session.RefreshErrorSearchAsync(request, cancellationToken);
        RenderWorkspace();
        await refresh.ConfigureAwait(true);
        if (!IsCurrentErrorSearchOperation(operation, cancellationToken))
        {
            return;
        }

        var view = _session.State.ErrorSearch;
        if (!view.IsStale
            && view.Snapshot is { } committed
            && committed.PageNumber == targetPageNumber)
        {
            TrackErrorSearchPage(committed, request.Cursor);
            _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
            _selectedErrorPeriodId = null;
            _selectedErrorEvidenceId = null;
        }

        RenderWorkspace();
    }

    private async Task GoToErrorSearchPageAndRenderAsync(
        int targetPageNumber,
        CancellationToken cancellationToken)
    {
        var operation = BeginErrorSearchOperation();
        try
        {
            if (!await SuspendErrorSearchAutoRefreshAsync(operation, cancellationToken)
                    .ConfigureAwait(true))
            {
                return;
            }

            var snapshot = _session.State.ErrorSearch.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的错误检索快照。");
            if (targetPageNumber < 1 || targetPageNumber > snapshot.TotalPages)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetPageNumber),
                    targetPageNumber,
                    "目标页必须位于 Host 返回的总页数范围内。");
            }

            if (targetPageNumber < snapshot.PageNumber)
            {
                if (!_errorSearchCursorsByPage.TryGetValue(targetPageNumber, out var previousCursor))
                {
                    throw new InvalidOperationException("Host 尚未发出目标上一页所需的不透明游标。");
                }

                var request = WatchErrorSearchQueries.OpenFrozenPage(
                    snapshot,
                    targetPageNumber,
                    previousCursor);
                await RefreshFrozenErrorSearchAndRenderAsync(
                        request,
                        targetPageNumber,
                        operation,
                        cancellationToken)
                    .ConfigureAwait(true);
            }
            else
            {
                while (snapshot.PageNumber < targetPageNumber)
                {
                    if (!snapshot.HasMore || string.IsNullOrWhiteSpace(snapshot.NextCursor))
                    {
                        throw new InvalidOperationException("Host 未发出到达目标页所需的下一页游标。");
                    }

                    var nextPage = snapshot.PageNumber + 1;
                    var request = WatchErrorSearchQueries.OpenFrozenPage(
                        snapshot,
                        nextPage,
                        snapshot.NextCursor);
                    await RefreshFrozenErrorSearchAndRenderAsync(
                            request,
                            nextPage,
                            operation,
                            cancellationToken)
                        .ConfigureAwait(true);
                    if (!IsCurrentErrorSearchOperation(operation, cancellationToken))
                    {
                        return;
                    }

                    var refreshed = _session.State.ErrorSearch;
                    if (refreshed.IsStale || refreshed.Snapshot is null)
                    {
                        return;
                    }

                    snapshot = refreshed.Snapshot;
                }
            }

            RenderWorkspace();
        }
        finally
        {
            ReactivateErrorSearchIfLatestOperation(operation);
        }
    }

    internal Task SelectErrorSeriesAndRenderAsync(
        string? seriesId,
        CancellationToken cancellationToken = default) =>
        SelectErrorSeriesAndRenderAsync(
            seriesId,
            BeginErrorSearchOperation(),
            cancellationToken,
            manageAutoRefresh: true);

    private async Task SelectErrorSeriesAndRenderAsync(
        string? seriesId,
        long operation,
        CancellationToken cancellationToken,
        bool manageAutoRefresh)
    {
        try
        {
            if (manageAutoRefresh
                && !await SuspendErrorSearchAutoRefreshAsync(operation, cancellationToken)
                    .ConfigureAwait(true))
            {
                return;
            }

            if (!IsCurrentErrorSearchOperation(operation, cancellationToken))
            {
                return;
            }

            _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
            _selectedErrorPeriodId = null;
            _selectedErrorEvidenceId = null;
            var selection = _session.SelectErrorSeriesAsync(seriesId, cancellationToken);
            RenderWorkspace();
            await selection.ConfigureAwait(true);
            if (!IsCurrentErrorSearchOperation(operation, cancellationToken))
            {
                return;
            }

            RenderWorkspace();
        }
        finally
        {
            if (manageAutoRefresh)
            {
                ReactivateErrorSearchIfLatestOperation(operation);
            }
        }
    }

    internal async Task LoadErrorRawEvidenceAndRenderAsync(
        string periodId,
        string evidenceId,
        ErrorSearchRawEvidenceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        var operation = BeginErrorSearchOperation();
        try
        {
            if (!await SuspendErrorSearchAutoRefreshAsync(operation, cancellationToken)
                    .ConfigureAwait(true))
            {
                return;
            }

            var view = _session.State.ErrorSearch;
            var snapshot = view.Snapshot
                ?? throw new InvalidOperationException("当前没有错误检索快照。");
            var seriesId = view.SelectedId
                ?? throw new InvalidOperationException("请先选择一个错误 Series。");
            var detail = view.Detail;
            if (detail is null
                || !string.Equals(detail.SnapshotReference, snapshot.SnapshotReference, StringComparison.Ordinal)
                || !detail.Periods.Any(period =>
                    string.Equals(period.PeriodId, periodId, StringComparison.Ordinal)
                    && period.Evidence.Any(evidence => string.Equals(
                        evidence.EvidenceId,
                        evidenceId,
                        StringComparison.Ordinal))))
            {
                throw new InvalidOperationException("所选证据不属于当前冻结错误详情。");
            }

            var retained = _errorRawEvidence;
            _selectedErrorPeriodId = periodId;
            _selectedErrorEvidenceId = evidenceId;
            _errorRawEvidence = WatchErrorRawEvidenceState.Begin(
                snapshot.SnapshotReference,
                seriesId,
                evidenceId,
                retained);
            RenderWorkspace();

            try
            {
                var raw = await _session.ReadErrorRawEvidenceAsync(
                        seriesId,
                        evidenceId,
                        normalized,
                        cancellationToken)
                    .ConfigureAwait(true);
                if (!IsCurrentErrorSearchOperation(operation, cancellationToken))
                {
                    return;
                }

                _errorRawEvidence = WatchErrorRawEvidenceState.Loaded(raw);
            }
            catch (OperationCanceledException) when (
                !IsCurrentErrorSearchOperation(operation, cancellationToken)
                || cancellationToken.IsCancellationRequested
                || _lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (WatchHostQueryException exception)
            {
                if (!IsCurrentErrorSearchOperation(operation, cancellationToken))
                {
                    return;
                }

                _errorRawEvidence = WatchErrorRawEvidenceState.Failed(
                    snapshot.SnapshotReference,
                    seriesId,
                    evidenceId,
                    DateTimeOffset.UtcNow,
                    exception.ErrorCode,
                    exception.Message,
                    exception.CorrelationId,
                    retained);
            }

            RenderWorkspace();
        }
        finally
        {
            ReactivateErrorSearchIfLatestOperation(operation);
        }
    }


    private long BeginErrorSearchOperation() =>
        Interlocked.Increment(ref _errorSearchOperationGeneration);


    private bool IsCurrentErrorSearchOperation(
        long operation,
        CancellationToken cancellationToken) =>
        !_disposed
        && !cancellationToken.IsCancellationRequested
        && operation == Interlocked.Read(ref _errorSearchOperationGeneration);


    private bool IsLatestErrorSearchOperation(long operation) =>
        !_disposed
        && operation == Interlocked.Read(ref _errorSearchOperationGeneration);


    private async Task<bool> SuspendErrorSearchAutoRefreshAsync(
        long operation,
        CancellationToken cancellationToken)
    {
        if (_autoRefresh.ActiveView == WatchV2DataView.ErrorSearch)
        {
            _autoRefresh.Deactivate();
            await _autoRefresh.WaitForIdleAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(true);
        }

        return IsCurrentErrorSearchOperation(operation, cancellationToken);
    }


    private void ReactivateErrorSearchIfCurrentPage()
    {
        if (_activePage == WatchWorkspacePage.ErrorSearch
            && _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            _autoRefresh.ActivateErrorSearch(_errorSearchQuery);
        }
    }

    private void ReactivateErrorSearchIfLatestOperation(long operation)
    {
        if (IsLatestErrorSearchOperation(operation))
        {
            ReactivateErrorSearchIfCurrentPage();
        }
    }


    private void ResetErrorSearchCursorHistory()
    {
        _errorSearchCursorsByPage.Clear();
        _errorSearchCursorsByPage[1] = null;
    }

    private void TrackErrorSearchPage(ErrorSearchListSnapshot snapshot, string? cursorUsed)
    {
        _errorSearchCursorsByPage[snapshot.PageNumber] = cursorUsed;
        if (snapshot.HasMore && !string.IsNullOrWhiteSpace(snapshot.NextCursor))
        {
            _errorSearchCursorsByPage[snapshot.PageNumber + 1] = snapshot.NextCursor;
        }
    }

    private ErrorSearchFilter ReadErrorSearchFilter() => new ErrorSearchFilter
    {
        Categories = _selectedErrorSearchCategories.Order(StringComparer.Ordinal).ToArray(),
        ErrorCodes = ReadChoiceValues(ErrorSearchCodeFilter),
        ActivityStates = ReadChoiceValues(ErrorSearchActivityStateFilter),
        SeriesId = ReadOptionalText(ErrorSearchSeriesIdFilter.Text),
        DemandId = ReadOptionalText(ErrorSearchDemandIdFilter.Text),
        SublotContains = ReadOptionalText(ErrorSearchSublotFilter.Text),
    };

    private ErrorSearchWindowSelection ReadErrorSearchWindow()
    {
        var value = ReadSingleChoiceValue(ErrorSearchWindowFilter);
        return value switch
        {
            ErrorSearchWindowKinds.Last24Hours => ErrorSearchWindowSelection.Last24Hours,
            ErrorSearchWindowKinds.Last7Days => ErrorSearchWindowSelection.Last7Days,
            ErrorSearchWindowKinds.Last15Days => ErrorSearchWindowSelection.Last15Days,
            ErrorSearchWindowKinds.AllHistory => ErrorSearchWindowSelection.AllHistory,
            _ => throw new ArgumentException("时间范围必须是最近 24 小时、7 天、15 天或全部历史。"),
        };
    }

    private int ReadErrorSearchPageSize() => ReadPageSize(
        ErrorSearchPageSizeInput,
        ErrorSearchQuery.MaximumPageSize,
        "错误检索");


    private static string? ReadOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void SyncErrorSearchFilterControls(ErrorSearchQuery query)
    {
        _isRenderingErrorSearch = true;
        try
        {
            _selectedErrorSearchCategories.Clear();
            foreach (var category in query.Filter.Categories)
            {
                _selectedErrorSearchCategories.Add(category);
            }
            ErrorSearchCategorySearchInput.Clear();
            RefreshErrorSearchCategoryNavigation();
            SelectChoice(ErrorSearchCodeFilter, query.Filter.ErrorCodes);
            SelectChoice(ErrorSearchActivityStateFilter, query.Filter.ActivityStates);
            SelectChoice(ErrorSearchWindowFilter, [query.Window.Kind]);
            ErrorSearchSeriesIdFilter.Text = query.Filter.SeriesId ?? string.Empty;
            ErrorSearchDemandIdFilter.Text = query.Filter.DemandId ?? string.Empty;
            ErrorSearchSublotFilter.Text = query.Filter.SublotContains ?? string.Empty;
            SelectChoice(ErrorSearchPageSizeInput, [query.PageSize.ToString(CultureInfo.InvariantCulture)]);
            ErrorSearchPageNumberInput.Text = "1";
        }
        finally
        {
            _isRenderingErrorSearch = false;
        }
    }


    private void RenderErrorSearchHeader(
        WatchV2WorkspaceState state,
        WatchErrorSearchPresentation presentation)
    {
        var text = _displayLanguageState.Catalog.ErrorSearch;
        ErrorSearchFreshnessText.Text =
            $"Endpoint {BuildPageEndpoint(state, "/api/v2/error-search")} · "
            + $"{presentation.ClientAttemptFacts} · "
            + text.Pick("自动刷新 ", "Auto-refresh ")
            + $"{_preferences.RefreshIntervals.ErrorSearch.IntervalSeconds} "
            + text.Pick("秒", "seconds");
        SetTextAutomationName(
            ErrorSearchFreshnessText,
            text.Pick("错误检索 Endpoint、最近成功与自动刷新", "Error Search endpoint, last success, and auto-refresh"),
            ErrorSearchFreshnessText.Text);

        var activeCount = presentation.ActivityFacets
            .FirstOrDefault(facet => string.Equals(
                facet.RawState,
                ErrorSearchActivityStates.Active,
                StringComparison.Ordinal))
            ?.SeriesCount ?? 0;
        var (status, styleKey) = !presentation.HasSnapshot
            ? (presentation.IsRefreshing ? text.Pick("正在读取", "Loading") : text.Pick("尚无快照", "No snapshot"),
                presentation.IsRefreshing ? "StatusPillAccent" : "StatusPill")
            : presentation.IsStale
                ? (text.Pick("快照已陈旧", "Snapshot is stale"), "StatusPillCaution")
                : activeCount > 0
                    ? (text.Pick($"活动错误 {activeCount:N0}", $"Active errors {activeCount:N0}"), "StatusPillCritical")
                    : (text.Pick("无活动错误", "No active errors"), "StatusPillSuccess");
        SetHeaderStatus(
            ErrorSearchHeaderStatusPill,
            ErrorSearchHeaderStatusText,
            text.Pick("错误检索状态", "Error Search status"),
            status,
            styleKey);
    }


    private void RenderErrorSearch(WatchV2WorkspaceState state)
    {
        _isRenderingErrorSearch = true;
        try
        {
            var text = _displayLanguageState.Catalog.ErrorSearch;
            var presentation = WatchErrorSearchPresentation.Project(
                state,
                _errorSearchQuery,
                _errorRawEvidence,
                _displayLanguageState.Catalog);
            RenderErrorSearchHeader(state, presentation);
            ErrorSearchSnapshotText.Text = presentation.SnapshotFacts;
            ErrorSearchWindowText.Text = presentation.CommittedWindow;
            ErrorSearchNormalizedFilterText.Text = presentation.CommittedConditions;
            var fullContractFacts = string.Join(
                Environment.NewLine,
                ErrorSearchScopeText.Text,
                presentation.SnapshotFacts,
                presentation.CommittedWindow,
                presentation.CommittedConditions);
            ErrorSearchCompactFactsText.Text = presentation.HasSnapshot
                ? text.Pick("Host 冻结快照 · ", "Frozen Host snapshot · ") + presentation.SnapshotFacts
                : text.Pick("Host 尚无冻结快照 · 条件待提交", "No frozen Host snapshot · filters not committed");
            ErrorSearchCompactFactsText.ToolTip = fullContractFacts;
            SetTextAutomationName(
                ErrorSearchCompactFactsText,
                text.Pick("错误检索 Host 冻结快照、窗口与已提交条件", "Error Search frozen Host snapshot, window, and committed filters"),
                fullContractFacts);
            AutomationProperties.SetHelpText(ErrorSearchCompactFactsText, fullContractFacts);
            ErrorSearchPageSummaryText.Text =
                $"{presentation.PageSummary} · {presentation.OrderSummary}";
            ErrorSearchEmptyResultText.Text = presentation.EmptyResultMessage;
            SetTextAutomationName(
                ErrorSearchEmptyResultText,
                text.Pick("错误检索空结果说明", "Error Search empty-result explanation"),
                string.IsNullOrEmpty(presentation.EmptyResultMessage)
                    ? text.Pick("当前非成功零结果", "Current state is not a successful empty result")
                    : presentation.EmptyResultMessage);
            SetTextAutomationName(ErrorSearchSnapshotText, text.Pick("错误检索快照", "Error Search snapshot"), presentation.SnapshotFacts);
            SetTextAutomationName(ErrorSearchWindowText, text.Pick("错误检索窗口", "Error Search window"), presentation.CommittedWindow);
            SetTextAutomationName(ErrorSearchNormalizedFilterText, text.Pick("错误检索规范化条件", "Normalized Error Search filters"), presentation.CommittedConditions);
            SetTextAutomationName(
                ErrorSearchPageSummaryText,
                text.Pick("错误检索精确总数、页码与排序", "Error Search exact total, page, and order"),
                ErrorSearchPageSummaryText.Text);

            var showEmpty = presentation.EmptyResultMessage.Length > 0;
            ErrorSearchStatusInfoBar.IsOpen = showEmpty;
            ErrorSearchStatusInfoBar.Severity = InfoBarSeverity.Informational;
            ErrorSearchStatusInfoBar.Title = showEmpty ? text.Pick("当前条件没有历史", "No history for current filters") : string.Empty;
            ErrorSearchStatusInfoBar.Message = showEmpty
                ? text.Pick("当前条件查询成功；没有历史。", "The current query succeeded with no history. ") + presentation.EmptyResultMessage
                : string.Empty;
            AutomationProperties.SetName(
                ErrorSearchStatusInfoBar,
                ErrorSearchStatusInfoBar.IsOpen
                    ? $"{ErrorSearchStatusInfoBar.Title}。{ErrorSearchStatusInfoBar.Message}"
                    : text.Pick("错误检索状态：当前无活动通知", "Error Search status: no active notification"));

            UpdateErrorSearchCategoryNavigation(presentation.CategoryFacets);
            ErrorSearchActivityStateFacetGrid.ItemsSource = presentation.ActivityFacets;
            ErrorSearchSeriesGrid.ItemsSource = presentation.Rows;
            ErrorSearchSeriesGrid.SelectedItem = presentation.Rows.FirstOrDefault(row =>
                string.Equals(row.SeriesId, state.ErrorSearch.SelectedId, StringComparison.Ordinal));

            ErrorSearchPreviousPageButton.IsEnabled = presentation.CanGoPrevious;
            ErrorSearchNextPageButton.IsEnabled = presentation.CanGoNext;
            if (state.ErrorSearch.Snapshot is { } snapshot)
            {
                ErrorSearchPageNumberInput.Text = snapshot.PageNumber.ToString(CultureInfo.InvariantCulture);
                TrackErrorSearchPage(
                    snapshot,
                    _errorSearchCursorsByPage.GetValueOrDefault(snapshot.PageNumber));
            }

            var detail = presentation.Detail;
            var selectedPeriod = detail?.Periods.FirstOrDefault(period =>
                string.Equals(period.PeriodId, _selectedErrorPeriodId, StringComparison.Ordinal));
            if (_selectedErrorPeriodId is not null && selectedPeriod is null)
            {
                _selectedErrorPeriodId = null;
                _selectedErrorEvidenceId = null;
                _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
            }
            ErrorSearchPeriodGrid.ItemsSource = detail?.Periods ?? [];
            ErrorSearchPeriodGrid.SelectedItem = selectedPeriod;
            var selectedPeriodEvidence = selectedPeriod?.Evidence ?? [];
            var selectedEvidence = selectedPeriodEvidence.FirstOrDefault(evidence =>
                string.Equals(evidence.EvidenceId, _selectedErrorEvidenceId, StringComparison.Ordinal));
            if (_selectedErrorEvidenceId is not null && selectedEvidence is null)
            {
                _selectedErrorEvidenceId = null;
                _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
            }
            ErrorSearchEvidenceGrid.ItemsSource = selectedPeriodEvidence;
            ErrorSearchEvidenceGrid.SelectedItem = selectedEvidence;
            ErrorSearchDetailContextText.Text = detail is null
                ? $"{presentation.DetailStatus.Title}：{presentation.DetailStatus.Message}"
                : detail.Heading + text.Pick(" · 窗口命中边界：", " · Window match boundaries: ") + string.Join(text.Pick("；", "; "), detail.Periods.Select(period => period.BoundarySummary).Distinct(StringComparer.Ordinal)) + " · " + detail.GenerationSummary;
            SetTextAutomationName(
                ErrorSearchDetailContextText,
                text.Pick("错误检索窗口边界与需求代次语义", "Error Search window-boundary and demand-generation semantics"),
                ErrorSearchDetailContextText.Text);

            var raw = _selectedErrorPeriodId is null || _selectedErrorEvidenceId is null
                ? WatchErrorRawEvidencePresentation.Hidden
                : presentation.RawEvidence;
            ErrorSearchRawEvidenceInfoBar.IsOpen = raw.IsVisible;
            ErrorSearchRawEvidenceInfoBar.Severity = raw.StatusSeverity is WatchPresentationSeverity.Warning
                && _errorRawEvidence.LastFailureAt is not null
                    ? InfoBarSeverity.Error
                    : ToInfoBarSeverity(raw.StatusSeverity);
            ErrorSearchRawEvidenceInfoBar.Title = raw.StatusTitle;
            ErrorSearchRawEvidenceInfoBar.Message = raw.StatusMessage;
            AutomationProperties.SetName(
                ErrorSearchRawEvidenceInfoBar,
                raw.IsVisible
                    ? $"{raw.StatusTitle}。{raw.StatusMessage}"
                    : text.Pick("原始证据状态：尚未显式请求", "Raw-evidence status: not explicitly requested"));
            ErrorSearchRawEvidenceGrid.ItemsSource = raw.Items;
            ErrorSearchRawEvidenceLimitsText.Text = raw.IsVisible && raw.IncludedFields.Count > 0
                ? text.Pick("白名单字段 ", "Allow-listed fields ") + string.Join(", ", raw.IncludedFields) + " · " + raw.LimitsSummary + text.Pick(" · 已返回 ", " · returned ") + $"{raw.ItemCount:N0}" + text.Pick(" 条 / ", " items / ") + $"{raw.PayloadBytes:N0}" + text.Pick(" 字节", " bytes")
                : BuildErrorRawEvidenceLimitsText(text);
            SetTextAutomationName(
                ErrorSearchRawEvidenceLimitsText,
                text.Pick("错误检索原始证据白名单与上限", "Error Search raw-evidence allow-list and limits"),
                ErrorSearchRawEvidenceLimitsText.Text);
            ErrorSearchLoadRawEvidenceButton.IsEnabled =
                ErrorSearchEvidenceGrid.SelectedItem is WatchErrorSearchEvidencePresentation evidence
                && evidence.CanReadRawEvidence;
            ErrorSearchOpenSeriesButton.IsEnabled =
                WatchDemandSeriesNavigationContext.FromErrorSearch(
                    state.ErrorSearch.Snapshot,
                    state.ErrorSearch.SelectedId,
                    state.ErrorSearch.Detail) is not null;
        }
        finally
        {
            _isRenderingErrorSearch = false;
        }
    }

    private void OnErrorSearchCategorySearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isRenderingErrorSearch)
        {
            RefreshErrorSearchCategoryNavigation();
        }
    }

    private void OnErrorSearchCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingErrorSearch || _isSyncingErrorSearchCategoryList)
        {
            return;
        }

        foreach (var item in e.RemovedItems.OfType<WatchErrorSearchCategoryNavigationItem>())
        {
            _selectedErrorSearchCategories.Remove(item.Category);
        }
        foreach (var item in e.AddedItems.OfType<WatchErrorSearchCategoryNavigationItem>())
        {
            _selectedErrorSearchCategories.Add(item.Category);
        }
    }

    private void OnErrorSearchApplyFiltersClick(object sender, RoutedEventArgs e)
    {
        ErrorSearchOperationTask = RunErrorSearchUiActionAsync(async () =>
        {
            var query = WatchErrorSearchQueries.StartLatest(
                ReadErrorSearchFilter(),
                ReadErrorSearchWindow(),
                ReadErrorSearchPageSize());
            SyncErrorSearchFilterControls(query);
            await RefreshLatestErrorSearchAndRenderAsync(query, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
    }

    private void OnErrorSearchClearFiltersClick(object sender, RoutedEventArgs e)
    {
        ErrorSearchOperationTask = RunErrorSearchUiActionAsync(async () =>
        {
            var query = WatchErrorSearchQueries.StartLatest();
            SyncErrorSearchFilterControls(query);
            await RefreshLatestErrorSearchAndRenderAsync(query, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
    }

    private void OnErrorSearchPreviousPageClick(object sender, RoutedEventArgs e)
    {
        ErrorSearchOperationTask = RunErrorSearchUiActionAsync(async () =>
        {
            var snapshot = _session.State.ErrorSearch.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的错误检索快照。");
            var target = snapshot.PageNumber - 1;
            if (!_errorSearchCursorsByPage.TryGetValue(target, out var cursor))
            {
                throw new InvalidOperationException("Host 尚未发出上一页所需的不透明游标。");
            }

            var request = WatchErrorSearchQueries.OpenFrozenPage(snapshot, target, cursor);
            await RefreshFrozenErrorSearchAndRenderAsync(
                    request,
                    target,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
    }

    private void OnErrorSearchNextPageClick(object sender, RoutedEventArgs e)
    {
        ErrorSearchOperationTask = RunErrorSearchUiActionAsync(async () =>
        {
            var snapshot = _session.State.ErrorSearch.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的错误检索快照。");
            var request = WatchErrorSearchQueries.OpenNextFrozenPage(snapshot)
                ?? throw new InvalidOperationException("当前冻结错误快照没有下一页。");
            await RefreshFrozenErrorSearchAndRenderAsync(
                    request,
                    snapshot.PageNumber + 1,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
    }

    private void OnErrorSearchGoToPageClick(object sender, RoutedEventArgs e)
    {
        ErrorSearchOperationTask = RunErrorSearchUiActionAsync(async () =>
        {
            if (!int.TryParse(
                    ErrorSearchPageNumberInput.Text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var pageNumber))
            {
                throw new ArgumentException("页码必须是整数。");
            }

            await GoToErrorSearchPageAndRenderAsync(pageNumber, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
    }

    private void OnErrorSearchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingErrorSearch)
        {
            return;
        }

        ErrorSearchOperationTask = RunErrorSearchUiActionAsync(() =>
            SelectErrorSeriesAndRenderAsync(
                (ErrorSearchSeriesGrid.SelectedItem as WatchErrorSearchRowPresentation)?.SeriesId,
                _lifetimeCancellation.Token));
    }

    private void OnErrorSearchPeriodSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingErrorSearch)
        {
            return;
        }

        _selectedErrorPeriodId =
            (ErrorSearchPeriodGrid.SelectedItem as WatchErrorSearchPeriodPresentation)?.PeriodId;
        ClearDisplayedErrorRawEvidence();
        var evidence =
            (ErrorSearchPeriodGrid.SelectedItem as WatchErrorSearchPeriodPresentation)?.Evidence
            ?? [];
        ErrorSearchEvidenceGrid.ItemsSource = evidence;
        ErrorSearchEvidenceGrid.SelectedIndex = -1;
        _selectedErrorEvidenceId = null;
        ErrorSearchLoadRawEvidenceButton.IsEnabled = false;
    }

    private void OnErrorSearchEvidenceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingErrorSearch)
        {
            return;
        }

        var evidence = ErrorSearchEvidenceGrid.SelectedItem as WatchErrorSearchEvidencePresentation;
        if (!string.Equals(
                _selectedErrorEvidenceId,
                evidence?.EvidenceId,
                StringComparison.Ordinal))
        {
            ClearDisplayedErrorRawEvidence();
        }
        _selectedErrorEvidenceId = evidence?.EvidenceId;
        ErrorSearchLoadRawEvidenceButton.IsEnabled = evidence?.CanReadRawEvidence == true;
    }

    private void ClearDisplayedErrorRawEvidence()
    {
        var text = _displayLanguageState.Catalog.ErrorSearch;
        _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
        ErrorSearchRawEvidenceInfoBar.IsOpen = false;
        AutomationProperties.SetName(
            ErrorSearchRawEvidenceInfoBar,
            text.Pick("原始证据状态：尚未显式请求", "Raw-evidence status: not explicitly requested"));
        ErrorSearchRawEvidenceGrid.ItemsSource = Array.Empty<WatchErrorRawItemPresentation>();
        ErrorSearchRawEvidenceLimitsText.Text = BuildErrorRawEvidenceLimitsText(text);
        SetTextAutomationName(
            ErrorSearchRawEvidenceLimitsText,
            text.Pick("错误检索原始证据白名单与上限", "Error Search raw-evidence allow-list and limits"),
            ErrorSearchRawEvidenceLimitsText.Text);
    }

    private static string BuildErrorRawEvidenceLimitsText(WatchErrorSearchText text) =>
        text.Pick("显式按需；允许字段 ", "Explicit on demand; allowed fields ")
        + string.Join(", ", ErrorSearchRawEvidenceFields.All)
        + text.Pick(" · 最多 ", " · maximum ")
        + ErrorSearchRawEvidenceLimits.MaximumItems
        + text.Pick(" 条 · 单条 ", " items · ")
        + ErrorSearchRawEvidenceLimits.MaximumItemBytes
        + text.Pick(" 字节/条 · 总计 ", " bytes/item · ")
        + ErrorSearchRawEvidenceLimits.MaximumTotalBytes
        + text.Pick(" 字节", " bytes total");

    private void OnErrorSearchLoadRawEvidenceClick(object sender, RoutedEventArgs e)
    {
        ErrorSearchOperationTask = RunErrorSearchUiActionAsync(async () =>
        {
            var periodId = _selectedErrorPeriodId
                ?? throw new InvalidOperationException("请先选择一个命中期间。");
            var evidence = ErrorSearchEvidenceGrid.SelectedItem as WatchErrorSearchEvidencePresentation
                ?? throw new InvalidOperationException("请先选择一条可展开的证据。");
            await LoadErrorRawEvidenceAndRenderAsync(
                    periodId,
                    evidence.EvidenceId,
                    new ErrorSearchRawEvidenceQuery(ErrorSearchRawEvidenceFields.All),
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
    }

    private void OnErrorSearchOpenSeriesClick(object sender, RoutedEventArgs e)
    {
        ErrorSearchOperationTask = RunErrorSearchUiActionAsync(async () =>
        {
            var errorSearch = _session.State.ErrorSearch;
            var navigation = WatchDemandSeriesNavigationContext.FromErrorSearch(
                    errorSearch.Snapshot,
                    errorSearch.SelectedId,
                    errorSearch.Detail)
                ?? throw new InvalidOperationException(
                    "当前选择没有与冻结错误检索快照一致的详情，无法打开 DemandSeries。");
            await NavigateToDemandSeriesAsync(navigation, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
    }

    private async Task RunErrorSearchUiActionAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the window is a neutral end to an in-flight action.
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or ErrorSearchException)
        {
            var text = _displayLanguageState.Catalog.ErrorSearch;
            PresentOperationFailure(
                WatchWorkspacePage.ErrorSearch,
                "error-search.operation",
                text.Pick("无法执行错误检索操作", "Unable to complete the Error Search operation"),
                text.Pick("请检查输入或当前快照后重试。", "Check the input or current snapshot and try again."),
                text.Pick("返回错误检索", "Return to Error Search"));
        }
    }

    private void ReflowErrorSearch(bool stack, bool useOuterScrolling)
    {
        if (stack || useOuterScrolling)
        {
            BindingOperations.ClearBinding(ErrorSearchBodyGrid, FrameworkElement.HeightProperty);
            ErrorSearchBodyGrid.Height = double.NaN;
            ErrorSearchFirstRow.Height = GridLength.Auto;
            ErrorSearchSeriesGrid.Height = (double)FindResource("Ticket22ResultsGridMinHeight");
        }
        else
        {
            ErrorSearchBodyGrid.SetBinding(
                FrameworkElement.HeightProperty,
                new Binding(nameof(FrameworkElement.ActualHeight))
                {
                    Source = ErrorSearchBodyScrollViewer,
                    Mode = BindingMode.OneWay,
                });
            ErrorSearchFirstRow.Height = new GridLength(1, GridUnitType.Star);
            ErrorSearchSeriesGrid.Height = double.NaN;
        }

        ReflowTicket22ThreeCards(
            stack,
            ErrorSearchCategoryCard,
            ErrorSearchResultsCard,
            ErrorSearchDetailCard,
            ErrorSearchCategoryColumn,
            ErrorSearchCategoryGapColumn,
            ErrorSearchResultsColumn,
            ErrorSearchResultsGapColumn,
            ErrorSearchDetailColumn,
            ErrorSearchFirstGapRow,
            ErrorSearchResultsRow,
            ErrorSearchSecondGapRow,
            ErrorSearchDetailRow,
            (GridLength)FindResource("ErrorSearchCategoryColumnWidth"),
            (GridLength)FindResource("ErrorSearchDetailColumnWidth"),
            (GridLength)FindResource("Ticket22CardGap"));
    }

    private void OnErrorSearchFilterGridSizeChanged(object sender, SizeChangedEventArgs e) =>
        ReflowErrorSearchFilterGrid(e.NewSize.Width);

    private void ReflowErrorSearchFilterGrid(double filterWidth)
    {
        var stackSeriesFilter = filterWidth > 0
            && filterWidth < (double)FindResource("ErrorSearchInlineFilterMinimumWidth");

        Grid.SetRow(ErrorSearchSeriesIdFilterField, stackSeriesFilter ? 2 : 0);
        Grid.SetColumn(ErrorSearchSeriesIdFilterField, stackSeriesFilter ? 0 : 6);
        Grid.SetColumnSpan(ErrorSearchSeriesIdFilterField, stackSeriesFilter ? 7 : 1);
        Grid.SetRow(ErrorSearchApplyFilterButton, stackSeriesFilter ? 2 : 0);
        ErrorSearchFilterGapRow.Height = stackSeriesFilter
            ? new GridLength(8)
            : new GridLength(0);
        ErrorSearchFilterSecondRow.Height = stackSeriesFilter
            ? GridLength.Auto
            : new GridLength(0);
    }

    private static void ReflowTicket22ThreeCards(
        bool stack,
        UIElement firstCard,
        UIElement secondCard,
        UIElement thirdCard,
        ColumnDefinition firstColumn,
        ColumnDefinition firstGapColumn,
        ColumnDefinition secondColumn,
        ColumnDefinition secondGapColumn,
        ColumnDefinition thirdColumn,
        RowDefinition firstGapRow,
        RowDefinition secondRow,
        RowDefinition secondGapRow,
        RowDefinition thirdRow,
        GridLength firstWidth,
        GridLength thirdWidth,
        GridLength gap)
    {
        Grid.SetColumn(firstCard, 0);
        Grid.SetRow(firstCard, 0);
        Grid.SetColumn(secondCard, stack ? 0 : 2);
        Grid.SetRow(secondCard, stack ? 2 : 0);
        Grid.SetColumn(thirdCard, stack ? 0 : 4);
        Grid.SetRow(thirdCard, stack ? 4 : 0);

        firstColumn.Width = stack ? new GridLength(1, GridUnitType.Star) : firstWidth;
        firstGapColumn.Width = stack ? new GridLength(0) : gap;
        secondColumn.Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        secondGapColumn.Width = stack ? new GridLength(0) : gap;
        thirdColumn.Width = stack ? new GridLength(0) : thirdWidth;
        firstGapRow.Height = stack ? gap : new GridLength(0);
        secondRow.Height = stack ? GridLength.Auto : new GridLength(0);
        secondGapRow.Height = stack ? gap : new GridLength(0);
        thirdRow.Height = stack ? GridLength.Auto : new GridLength(0);
    }

}
