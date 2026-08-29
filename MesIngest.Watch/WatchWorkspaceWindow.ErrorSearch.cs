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
        ErrorSearchSeriesIdFilterLabel.Text = text.SeriesIdLabel;
        ErrorSearchApplyFilterButton.Content = text.ApplyFilters;
        ErrorSearchMoreFiltersExpander.Header = text.MoreFilters;
        ErrorSearchDemandIdFilterLabel.Text = text.Select(WatchGeneratedText.ErrorSearchUi194);
        ErrorSearchSublotFilterLabel.Text = text.Select(WatchGeneratedText.ErrorSearchUi195);
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
        ErrorSearchScopeText.Text = text.Select(WatchGeneratedText.ErrorSearchUi196);
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
        ErrorSearchActivityStateFilter.Tag = text.Select(WatchGeneratedText.ErrorSearchUi197);
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
        ErrorSearchCodeFilter.Tag = text.Select(WatchGeneratedText.ErrorSearchUi198);
        if (ReadChoiceValues(ErrorSearchCodeFilter).Count == 0)
        {
            SelectChoice(ErrorSearchCodeFilter, []);
        }

        ErrorSearchActivityFacetStateColumn.Header = text.ActivityState;
        ErrorSearchActivityFacetCountColumn.Header = text.Select(WatchGeneratedText.ErrorSearchUi199);
        ErrorSearchMatchedErrorsColumn.Header = text.ErrorCode;
        ErrorSearchActivityColumn.Header = text.ActivityState;
        ErrorSearchMatchedPeriodsColumn.Header = text.Select(WatchGeneratedText.ErrorSearchUi200);
        ErrorSearchDemandGenerationsColumn.Header = text.Select(WatchGeneratedText.ErrorSearchUi201);
        ErrorSearchLatestEvidenceColumn.Header = text.Select(WatchGeneratedText.ErrorSearchUi202);

        var periodHeaders = new[]
        {
            text.ErrorCode, text.Select(WatchGeneratedText.ErrorSearchUi203), text.Select(WatchGeneratedText.ErrorSearchUi204),
            text.Select(WatchGeneratedText.ErrorSearchUi205), text.Select(WatchGeneratedText.ErrorSearchUi206), text.Select(WatchGeneratedText.ErrorSearchUi207),
            text.Select(WatchGeneratedText.ErrorSearchUi208), _displayLanguageState.Catalog.Columns.Target,
        };
        for (var index = 0; index < periodHeaders.Length; index++)
        {
            ErrorSearchPeriodGrid.Columns[index].Header = periodHeaders[index];
        }
        var evidenceHeaders = new[]
        {
            text.Select(WatchGeneratedText.ErrorSearchUi209), text.Select(WatchGeneratedText.ErrorSearchUi210),
            text.Select(WatchGeneratedText.ErrorSearchUi211), text.Select(WatchGeneratedText.ErrorSearchUi212),
            text.Select(WatchGeneratedText.ErrorSearchUi213), _displayLanguageState.Catalog.Columns.DemandWorkType, text.Select(WatchGeneratedText.ErrorSearchUi214), _displayLanguageState.Catalog.Columns.PollTrace,
        };
        for (var index = 0; index < evidenceHeaders.Length; index++)
        {
            ErrorSearchEvidenceGrid.Columns[index].Header = evidenceHeaders[index];
        }
        ErrorSearchRawEvidenceGrid.Columns[2].Header = text.Select(WatchGeneratedText.ErrorSearchUi214);
        ErrorSearchRawEvidenceGrid.Columns[4].Header = text.Select(WatchGeneratedText.ErrorSearchUi215);

        AutomationProperties.SetName(ErrorSearchPage, text.Select(WatchGeneratedText.ErrorSearchUi216));
        AutomationProperties.SetName(ErrorSearchBodyScrollViewer, text.Select(WatchGeneratedText.ErrorSearchUi217));
        AutomationProperties.SetName(ErrorSearchCategorySearchInput, text.Select(WatchGeneratedText.ErrorSearchUi218));
        AutomationProperties.SetHelpText(ErrorSearchCategorySearchInput, text.Select(WatchGeneratedText.ErrorSearchUi219));
        AutomationProperties.SetName(ErrorSearchCategoryList, text.Select(WatchGeneratedText.ErrorSearchUi220));
        AutomationProperties.SetHelpText(ErrorSearchCategoryList, text.CategoryHelp);
        AutomationProperties.SetName(ErrorSearchActivityStateFacetGrid, text.Select(WatchGeneratedText.ErrorSearchUi221));
        AutomationProperties.SetName(ErrorSearchSeriesGrid, text.Select(WatchGeneratedText.ErrorSearchUi222));
        AutomationProperties.SetName(ErrorSearchPeriodGrid, text.Select(WatchGeneratedText.ErrorSearchUi223));
        AutomationProperties.SetName(ErrorSearchEvidenceGrid, text.Select(WatchGeneratedText.ErrorSearchUi224));
        AutomationProperties.SetName(ErrorSearchRawEvidenceGrid, text.Select(WatchGeneratedText.ErrorSearchUi225));
        AutomationProperties.SetName(ErrorSearchCodeFilter, text.Select(WatchGeneratedText.ErrorSearchUi226));
        AutomationProperties.SetName(ErrorSearchActivityStateFilter, text.Select(WatchGeneratedText.ErrorSearchUi227));
        AutomationProperties.SetName(ErrorSearchWindowFilter, text.Select(WatchGeneratedText.ErrorSearchUi228));
        AutomationProperties.SetName(ErrorSearchSeriesIdFilter, text.Select(WatchGeneratedText.ErrorSearchUi229));
        AutomationProperties.SetName(ErrorSearchDemandIdFilter, text.Select(WatchGeneratedText.ErrorSearchUi230));
        AutomationProperties.SetName(ErrorSearchSublotFilter, text.Select(WatchGeneratedText.ErrorSearchUi231));
        AutomationProperties.SetName(ErrorSearchPageSizeInput, text.Select(WatchGeneratedText.ErrorSearchUi232));
        AutomationProperties.SetName(ErrorSearchPageNumberInput, text.Select(WatchGeneratedText.ErrorSearchUi233));
        AutomationProperties.SetName(ErrorSearchApplyFilterButton, text.ApplyFiltersAutomationName);
        AutomationProperties.SetName(ErrorSearchClearFilterButton, text.ClearFiltersAutomationName);
        AutomationProperties.SetName(ErrorSearchPreviousPageButton, text.PreviousPageAutomationName);
        AutomationProperties.SetName(ErrorSearchNextPageButton, text.NextPageAutomationName);
        AutomationProperties.SetName(ErrorSearchGoToPageButton, text.GoToPageAutomationName);
        AutomationProperties.SetName(ErrorSearchLoadRawEvidenceButton, text.LoadRawEvidenceAutomationName);
        AutomationProperties.SetName(ErrorSearchOpenSeriesButton, text.OpenSeries);
        AutomationProperties.SetName(ErrorSearchCategoryCard, text.CategoryCardAutomationName);
        AutomationProperties.SetName(ErrorSearchResultsCard, text.ResultsCardAutomationName);
        AutomationProperties.SetName(ErrorSearchDetailCard, text.DetailCardAutomationName);
        AutomationProperties.SetName(ErrorSearchContractFactsPanel, text.PageTitle);
        AutomationProperties.SetName(ErrorSearchScopeText, text.PageTitle);

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
            .Select(group =>
            {
                var categoryLabel = text.CodeWithMeaning(text.DescribeCategory(group.Key));
                return new WatchErrorSearchCategoryNavigationItem(
                    group.Key,
                    categoryLabel,
                    string.Join(
                        " · ",
                        group.Select(definition => text.CodeWithMeaning(text.DescribeErrorCode(definition.Code))).Order(StringComparer.Ordinal)),
                    counts.TryGetValue(group.Key, out var count) ? count : null,
                    counts.TryGetValue(group.Key, out count)
                        ? count.ToString("N0", _displayLanguageState.Catalog.Language == WatchDisplayLanguage.SimplifiedChinese
                            ? CultureInfo.GetCultureInfo("zh-CN")
                            : CultureInfo.GetCultureInfo("en-US"))
                        : _displayLanguageState.Catalog.Common.NotLoaded,
                    counts.ContainsKey(group.Key) ? text.Select(WatchGeneratedText.ErrorSearchUi234) : text.Select(WatchGeneratedText.ErrorSearchUi235),
                    counts.TryGetValue(group.Key, out count)
                        ? text.Format(WatchGeneratedText.ErrorSearchUi236, new object?[] { categoryLabel, count }, new object?[] { categoryLabel, count })
                        : text.Format(WatchGeneratedText.ErrorSearchUi237, new object?[] { categoryLabel }, new object?[] { categoryLabel }));
            })
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
                    "目标页必须位于服务端返回的总页数范围内。");
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
            + text.Select(WatchGeneratedText.ErrorSearchUi238)
            + $"{_preferences.RefreshIntervals.ErrorSearch.IntervalSeconds} "
            + text.Select(WatchGeneratedText.ErrorSearchUi239);
        SetTextAutomationName(
            ErrorSearchFreshnessText,
            text.Select(WatchGeneratedText.ErrorSearchUi240),
            ErrorSearchFreshnessText.Text);

        var activeCount = presentation.ActivityFacets
            .FirstOrDefault(facet => string.Equals(
                facet.RawState,
                ErrorSearchActivityStates.Active,
                StringComparison.Ordinal))
            ?.SeriesCount ?? 0;
        var (status, styleKey) = !presentation.HasSnapshot
            ? (presentation.IsRefreshing ? text.Select(WatchGeneratedText.ErrorSearchUi241) : text.Select(WatchGeneratedText.ErrorSearchUi235),
                "StatusPill")
            : presentation.IsStale
                ? (text.Select(WatchGeneratedText.ErrorSearchUi242), "StatusPillCaution")
                : activeCount > 0
                    ? (text.Format(WatchGeneratedText.ErrorSearchUi243, new object?[] { activeCount }, new object?[] { activeCount }), "StatusPillCritical")
                    : (text.Select(WatchGeneratedText.ErrorSearchUi244), "StatusPillSuccess");
        SetHeaderStatus(
            ErrorSearchHeaderStatusPill,
            ErrorSearchHeaderStatusText,
            text.Select(WatchGeneratedText.ErrorSearchUi245),
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
            SetTextAutomationName(
                ErrorSearchScopeText,
                text.PageTitle,
                ErrorSearchScopeText.Text);
            var fullContractFacts = string.Join(
                Environment.NewLine,
                ErrorSearchScopeText.Text,
                presentation.SnapshotFacts,
                presentation.CommittedWindow,
                presentation.CommittedConditions);
            ErrorSearchCompactFactsText.Text = presentation.HasSnapshot
                ? text.Select(WatchGeneratedText.ErrorSearchUi246) + presentation.SnapshotFacts
                : text.Select(WatchGeneratedText.ErrorSearchUi247);
            ErrorSearchCompactFactsText.ToolTip = fullContractFacts;
            SetTextAutomationName(
                ErrorSearchCompactFactsText,
                text.Select(WatchGeneratedText.ErrorSearchUi248),
                fullContractFacts);
            AutomationProperties.SetHelpText(ErrorSearchCompactFactsText, fullContractFacts);
            ErrorSearchPageSummaryText.Text =
                $"{presentation.PageSummary} · {presentation.OrderSummary}";
            ErrorSearchEmptyResultText.Text = presentation.EmptyResultMessage;
            SetTextAutomationName(
                ErrorSearchEmptyResultText,
                text.Select(WatchGeneratedText.ErrorSearchUi249),
                string.IsNullOrEmpty(presentation.EmptyResultMessage)
                    ? text.Select(WatchGeneratedText.ErrorSearchUi250)
                    : presentation.EmptyResultMessage);
            SetTextAutomationName(ErrorSearchSnapshotText, text.Select(WatchGeneratedText.ErrorSearchUi251), presentation.SnapshotFacts);
            SetTextAutomationName(ErrorSearchWindowText, text.Select(WatchGeneratedText.ErrorSearchUi252), presentation.CommittedWindow);
            SetTextAutomationName(ErrorSearchNormalizedFilterText, text.Select(WatchGeneratedText.ErrorSearchUi253), presentation.CommittedConditions);
            SetTextAutomationName(
                ErrorSearchPageSummaryText,
                text.Select(WatchGeneratedText.ErrorSearchUi254),
                ErrorSearchPageSummaryText.Text);

            var showEmpty = presentation.EmptyResultMessage.Length > 0;
            ErrorSearchStatusInfoBar.IsOpen = showEmpty;
            ErrorSearchStatusInfoBar.Severity = InfoBarSeverity.Informational;
            ErrorSearchStatusInfoBar.Title = showEmpty ? text.Select(WatchGeneratedText.ErrorSearchUi255) : string.Empty;
            ErrorSearchStatusInfoBar.Message = showEmpty
                ? text.Select(WatchGeneratedText.ErrorSearchUi256) + presentation.EmptyResultMessage
                : string.Empty;
            AutomationProperties.SetName(
                ErrorSearchStatusInfoBar,
                ErrorSearchStatusInfoBar.IsOpen
                    ? $"{ErrorSearchStatusInfoBar.Title}。{ErrorSearchStatusInfoBar.Message}"
                    : text.Select(WatchGeneratedText.ErrorSearchUi257));

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
                : detail.Heading + text.Select(WatchGeneratedText.ErrorSearchUi258) + string.Join(text.Select(WatchGeneratedText.ErrorSearchUi259), detail.Periods.Select(period => period.BoundarySummary).Distinct(StringComparer.Ordinal)) + " · " + detail.GenerationSummary;
            SetTextAutomationName(
                ErrorSearchDetailContextText,
                text.Select(WatchGeneratedText.ErrorSearchUi260),
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
                    : text.Select(WatchGeneratedText.ErrorSearchUi261));
            ErrorSearchRawEvidenceGrid.ItemsSource = raw.Items;
            ErrorSearchRawEvidenceLimitsText.Text = raw.IsVisible && raw.IncludedFields.Count > 0
                ? text.Select(WatchGeneratedText.ErrorSearchUi262) + string.Join(", ", raw.IncludedFields) + " · " + raw.LimitsSummary + text.Select(WatchGeneratedText.ErrorSearchUi263) + $"{raw.ItemCount:N0}" + text.Select(WatchGeneratedText.ErrorSearchUi264) + $"{raw.PayloadBytes:N0}" + text.Select(WatchGeneratedText.ErrorSearchUi265)
                : BuildErrorRawEvidenceLimitsText(text);
            SetTextAutomationName(
                ErrorSearchRawEvidenceLimitsText,
                text.Select(WatchGeneratedText.ErrorSearchUi266),
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
            text.Select(WatchGeneratedText.ErrorSearchUi261));
        ErrorSearchRawEvidenceGrid.ItemsSource = Array.Empty<WatchErrorRawItemPresentation>();
        ErrorSearchRawEvidenceLimitsText.Text = BuildErrorRawEvidenceLimitsText(text);
        SetTextAutomationName(
            ErrorSearchRawEvidenceLimitsText,
            text.Select(WatchGeneratedText.ErrorSearchUi266),
            ErrorSearchRawEvidenceLimitsText.Text);
    }

    private static string BuildErrorRawEvidenceLimitsText(WatchErrorSearchText text) =>
        text.Select(WatchGeneratedText.ErrorSearchUi267)
        + string.Join(", ", ErrorSearchRawEvidenceFields.All)
        + text.Select(WatchGeneratedText.ErrorSearchUi268)
        + ErrorSearchRawEvidenceLimits.MaximumItems
        + text.Select(WatchGeneratedText.ErrorSearchUi269)
        + ErrorSearchRawEvidenceLimits.MaximumItemBytes
        + text.Select(WatchGeneratedText.ErrorSearchUi270)
        + ErrorSearchRawEvidenceLimits.MaximumTotalBytes
        + text.Select(WatchGeneratedText.ErrorSearchUi271);

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
                    "当前选择没有与冻结错误检索快照一致的详情，无法打开需求系列。");
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
                WatchLocalizedText.FromEntry(WatchGeneratedText.ErrorSearchUi272),
                WatchLocalizedText.FromEntry(WatchGeneratedText.ErrorSearchUi273),
                WatchLocalizedText.FromEntry(WatchGeneratedText.ErrorSearchUi274));
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
