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
    string CodesSummary,
    long? SeriesCount)
{
    public string SeriesCountText => SeriesCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";

    public string SeriesCountCaption => SeriesCount is null ? "尚无快照" : "Series";

    public string AutomationId => $"ErrorSearchCategory_{Category}";

    public string AutomationName => SeriesCount is { } count
        ? $"错误分类 {Category}，Host 精确 {count.ToString("N0", CultureInfo.CurrentCulture)} 个 DemandSeries，可多选"
        : $"错误分类 {Category}，尚无 Host 快照计数，可多选";
}

internal partial class WatchWorkspaceWindow
{
    private readonly Dictionary<int, string?> _errorSearchCursorsByPage = new();
    private readonly HashSet<string> _selectedErrorSearchCategories = new(StringComparer.Ordinal);
    private WatchErrorRawEvidenceState _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
    private IReadOnlyList<WatchErrorSearchCategoryNavigationItem> _errorSearchCategoryNavigationItems = [];
    private string? _selectedErrorPeriodId;
    private string? _selectedErrorEvidenceId;
    private string? _selectedCurrentAttentionIdentity;
    private string? _currentAttentionSelectionNotice;
    private bool _isRenderingErrorSearch;
    private bool _isRenderingCurrentAttention;
    private bool _isSyncingErrorSearchCategoryList;
    private long _errorSearchOperationGeneration;
    private long _currentAttentionOperationGeneration;

    internal Task ErrorSearchNavigationTask { get; private set; } = Task.CompletedTask;

    internal Task CurrentAttentionNavigationTask { get; private set; } = Task.CompletedTask;

    internal Task ErrorSearchOperationTask { get; private set; } = Task.CompletedTask;

    internal Task CurrentAttentionOperationTask { get; private set; } = Task.CompletedTask;

    internal WatchV2DataView? ActiveAutoRefreshView => _autoRefresh.ActiveView;

    private void InitializeTicket22Pages()
    {
        WatchGridClipboardBehavior.Attach(ErrorSearchActivityStateFacetGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ErrorSearchSeriesGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ErrorSearchPeriodGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ErrorSearchEvidenceGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ErrorSearchRawEvidenceGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(CurrentAttentionKindFacetGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(CurrentAttentionSeverityFacetGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(CurrentAttentionGrid, preserveSelectionUnit: true);
        CurrentAttentionGrid.ClearValue(DataGrid.HeadersVisibilityProperty);
        WatchGridClipboardBehavior.Attach(CurrentAttentionEvidenceGrid, preserveSelectionUnit: true);

        PopulateErrorSearchCatalogChoices();

        ResetErrorSearchCursorHistory();
        SyncErrorSearchFilterControls(_errorSearchQuery);
        SyncCurrentAttentionFilterControls(_currentAttentionQuery);
    }

    private void PopulateErrorSearchCatalogChoices()
    {
        UpdateErrorSearchCategoryNavigation([]);

        ErrorSearchCodeFilter.Items.Clear();
        foreach (var definition in SeriesErrorCatalog.Definitions.OrderBy(
                     definition => definition.Code,
                     StringComparer.Ordinal))
        {
            ErrorSearchCodeFilter.Items.Add(new ComboBoxItem
            {
                Content = definition.Code,
                Tag = definition.Code,
                ToolTip = definition.Meaning,
            });
        }
    }

    private void UpdateErrorSearchCategoryNavigation(
        IReadOnlyList<WatchErrorSearchCategoryFacetPresentation> facets)
    {
        var counts = facets.ToDictionary(
            facet => facet.Category,
            facet => facet.SeriesCount,
            StringComparer.Ordinal);
        _errorSearchCategoryNavigationItems = SeriesErrorCatalog.Definitions
            .GroupBy(definition => definition.Category, StringComparer.Ordinal)
            .Select(group => new WatchErrorSearchCategoryNavigationItem(
                group.Key,
                string.Join(
                    " · ",
                    group.Select(definition => definition.Code).Order(StringComparer.Ordinal)),
                counts.TryGetValue(group.Key, out var count) ? count : null))
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

    private async Task LoadCurrentAttentionNavigationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshCurrentAttentionAndRenderAsync(_currentAttentionQuery, cancellationToken)
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

    private async Task RefreshCurrentAttentionAndRenderAsync(
        CurrentIngestAttentionQuery query,
        CancellationToken cancellationToken)
    {
        var operation = BeginCurrentAttentionOperation();
        try
        {
            if (!await SuspendCurrentAttentionAutoRefreshAsync(operation, cancellationToken)
                    .ConfigureAwait(true))
            {
                return;
            }

            _currentAttentionQuery = query.NormalizeAndValidate();
            var refresh = _session.RefreshCurrentAttentionAsync(_currentAttentionQuery, cancellationToken);
            RenderWorkspace();
            await refresh.ConfigureAwait(true);
            if (!IsCurrentAttentionOperation(operation, cancellationToken))
            {
                return;
            }

            RenderWorkspace();
        }
        finally
        {
            ReactivateCurrentAttentionIfLatestOperation(operation);
        }
    }

    private long BeginErrorSearchOperation() =>
        Interlocked.Increment(ref _errorSearchOperationGeneration);

    private long BeginCurrentAttentionOperation() =>
        Interlocked.Increment(ref _currentAttentionOperationGeneration);

    private bool IsCurrentErrorSearchOperation(
        long operation,
        CancellationToken cancellationToken) =>
        !_disposed
        && !cancellationToken.IsCancellationRequested
        && operation == Interlocked.Read(ref _errorSearchOperationGeneration);

    private bool IsCurrentAttentionOperation(
        long operation,
        CancellationToken cancellationToken) =>
        !_disposed
        && !cancellationToken.IsCancellationRequested
        && operation == Interlocked.Read(ref _currentAttentionOperationGeneration);

    private bool IsLatestErrorSearchOperation(long operation) =>
        !_disposed
        && operation == Interlocked.Read(ref _errorSearchOperationGeneration);

    private bool IsLatestCurrentAttentionOperation(long operation) =>
        !_disposed
        && operation == Interlocked.Read(ref _currentAttentionOperationGeneration);

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

    private async Task<bool> SuspendCurrentAttentionAutoRefreshAsync(
        long operation,
        CancellationToken cancellationToken)
    {
        if (_autoRefresh.ActiveView == WatchV2DataView.CurrentIngestAttention)
        {
            _autoRefresh.Deactivate();
            await _autoRefresh.WaitForIdleAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(true);
        }

        return IsCurrentAttentionOperation(operation, cancellationToken);
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

    private void ReactivateCurrentAttentionIfCurrentPage()
    {
        if (_activePage == WatchWorkspacePage.CurrentAttention
            && _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            _autoRefresh.ActivateCurrentAttention(_currentAttentionQuery);
        }
    }

    private void ReactivateCurrentAttentionIfLatestOperation(long operation)
    {
        if (IsLatestCurrentAttentionOperation(operation))
        {
            ReactivateCurrentAttentionIfCurrentPage();
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
            ErrorSearchWindowKinds.Last30Days => ErrorSearchWindowSelection.Last30Days,
            ErrorSearchWindowKinds.AllHistory => ErrorSearchWindowSelection.AllHistory,
            _ => throw new ArgumentException("时间范围必须是最近 24 小时、7 天、30 天或全部历史。"),
        };
    }

    private int ReadErrorSearchPageSize() => ReadPageSize(
        ErrorSearchPageSizeInput,
        ErrorSearchQuery.MaximumPageSize,
        "错误检索");

    private int ReadCurrentAttentionPageSize() => ReadPageSize(
        CurrentAttentionPageSizeInput,
        CurrentIngestAttentionQuery.MaximumPageSize,
        "接入告警");

    private static int ReadPageSize(ComboBox comboBox, int maximum, string pageName)
    {
        var value = ReadSingleChoiceValue(comboBox);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize)
            && pageSize is >= 1
            && pageSize <= maximum
            ? pageSize
            : throw new ArgumentException($"{pageName}每页数量必须介于 1 和 {maximum}。", nameof(comboBox));
    }

    private static IReadOnlyList<string> ReadChoiceValues(ComboBox comboBox)
    {
        var selectedItem = comboBox.SelectedItem as ComboBoxItem;
        var selectedContent = selectedItem?.Content?.ToString();
        var value = !comboBox.IsEditable
            || (selectedItem is not null
                && string.Equals(comboBox.Text, selectedContent, StringComparison.Ordinal))
                    ? selectedItem?.Tag?.ToString()
                        ?? selectedContent
                        ?? comboBox.Text
                    : comboBox.Text;
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith("全部", StringComparison.Ordinal))
        {
            return [];
        }

        return value
            .Split([',', ';', '，'], StringSplitOptions.RemoveEmptyEntries)
            .Select(candidate => candidate.Trim())
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadSingleChoiceValue(ComboBox comboBox)
    {
        if (comboBox.IsEditable && !string.IsNullOrWhiteSpace(comboBox.Text))
        {
            return comboBox.Text.Trim();
        }

        return comboBox.SelectedValue?.ToString()
            ?? (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? comboBox.Text;
    }

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

    private void SyncCurrentAttentionFilterControls(CurrentIngestAttentionQuery query)
    {
        _isRenderingCurrentAttention = true;
        try
        {
            SelectChoice(CurrentAttentionKindFilter, query.Kinds ?? []);
            SelectChoice(CurrentAttentionSeverityFilter, query.Severities ?? []);
            SelectChoice(CurrentAttentionPageSizeInput, [query.PageSize.ToString(CultureInfo.InvariantCulture)]);
            CurrentAttentionPageNumberInput.Text = query.PageNumber.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isRenderingCurrentAttention = false;
        }
    }

    private static void SelectChoice(ComboBox comboBox, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            if (comboBox.IsEditable)
            {
                comboBox.SelectedIndex = -1;
                comboBox.Text = comboBox.Tag?.ToString() ?? string.Empty;
            }
            else
            {
                comboBox.SelectedIndex = 0;
            }
            return;
        }

        if (values.Count == 1)
        {
            foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
            {
                var candidate = item.Tag?.ToString() ?? item.Content?.ToString();
                if (string.Equals(candidate, values[0], StringComparison.Ordinal))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }
        }

        comboBox.SelectedIndex = -1;
        if (comboBox.IsEditable)
        {
            comboBox.Text = string.Join(", ", values);
        }
    }

    private void RenderErrorSearchHeader(
        WatchV2WorkspaceState state,
        WatchErrorSearchPresentation presentation)
    {
        ErrorSearchFreshnessText.Text =
            $"Endpoint {BuildPageEndpoint(state, "/api/v2/error-search")} · "
            + $"{presentation.ClientAttemptFacts} · "
            + $"自动刷新 {_preferences.RefreshIntervals.ErrorSearch.IntervalSeconds} 秒";
        SetTextAutomationName(
            ErrorSearchFreshnessText,
            "错误检索 Endpoint、最近成功与自动刷新",
            ErrorSearchFreshnessText.Text);

        var activeCount = presentation.ActivityFacets
            .FirstOrDefault(facet => string.Equals(
                facet.State,
                ErrorSearchActivityStates.Active,
                StringComparison.Ordinal))
            ?.SeriesCount ?? 0;
        var (status, styleKey) = !presentation.HasSnapshot
            ? (presentation.IsRefreshing ? "正在读取" : "尚无快照",
                presentation.IsRefreshing ? "StatusPillAccent" : "StatusPill")
            : presentation.IsStale
                ? ("快照已陈旧", "StatusPillCaution")
                : activeCount > 0
                    ? ($"活动错误 {activeCount:N0}", "StatusPillCritical")
                    : ("无活动错误", "StatusPillSuccess");
        SetHeaderStatus(
            ErrorSearchHeaderStatusPill,
            ErrorSearchHeaderStatusText,
            "错误检索状态",
            status,
            styleKey);
    }

    private void RenderCurrentAttentionHeader(
        WatchV2WorkspaceState state,
        WatchCurrentIngestAttentionPresentation presentation)
    {
        CurrentAttentionFreshnessText.Text =
            $"Endpoint {BuildPageEndpoint(state, "/api/v2/current-ingest-attention")} · "
            + $"{presentation.ClientAttemptFacts} · "
            + $"自动刷新 {_preferences.RefreshIntervals.CurrentIngestAttention.IntervalSeconds} 秒";
        SetTextAutomationName(
            CurrentAttentionFreshnessText,
            "接入告警 Endpoint、最近成功与自动刷新",
            CurrentAttentionFreshnessText.Text);

        var exactTotal = state.CurrentAttention.Snapshot?.ExactTotalItemCount ?? 0;
        var errorCount = presentation.SeverityFacets
            .FirstOrDefault(facet => string.Equals(
                facet.Value,
                CurrentIngestAttentionSeverities.Error,
                StringComparison.Ordinal))
            ?.ItemCount ?? 0;
        var (status, styleKey) = !presentation.HasSnapshot
            ? (presentation.IsRefreshing ? "正在读取" : "尚无快照",
                presentation.IsRefreshing ? "StatusPillAccent" : "StatusPill")
            : presentation.IsStale
                ? ("快照已陈旧", "StatusPillCaution")
                : exactTotal == 0
                    ? ("当前无关注", "StatusPillSuccess")
                    : ($"当前关注 {exactTotal:N0}",
                        errorCount > 0 ? "StatusPillCritical" : "StatusPillCaution");
        SetHeaderStatus(
            CurrentAttentionHeaderStatusPill,
            CurrentAttentionHeaderStatusText,
            "接入告警状态",
            status,
            styleKey);
    }

    private string BuildPageEndpoint(WatchV2WorkspaceState state, string endpointPath)
    {
        var baseUrl = string.IsNullOrWhiteSpace(state.BaseUrl)
            ? _currentHostSettings.BaseUrl
            : state.BaseUrl;
        return $"{baseUrl.TrimEnd('/')}{endpointPath}";
    }

    private void SetHeaderStatus(
        Border pill,
        TextBlock text,
        string automationLabel,
        string value,
        string styleKey)
    {
        pill.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        text.Text = value;
        var automationName = $"{automationLabel}：{value}";
        AutomationProperties.SetName(pill, automationName);
        AutomationProperties.SetName(text, automationName);
        pill.ToolTip = automationName;
    }

    private void RenderErrorSearch(WatchV2WorkspaceState state)
    {
        _isRenderingErrorSearch = true;
        try
        {
            var presentation = WatchErrorSearchPresentation.Project(
                state,
                _errorSearchQuery,
                _errorRawEvidence);
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
                ? $"Host 冻结快照 · {presentation.SnapshotFacts}"
                : "Host 尚无冻结快照 · 条件待提交";
            ErrorSearchCompactFactsText.ToolTip = fullContractFacts;
            SetTextAutomationName(
                ErrorSearchCompactFactsText,
                "错误检索 Host 冻结快照、窗口与已提交条件",
                fullContractFacts);
            AutomationProperties.SetHelpText(ErrorSearchCompactFactsText, fullContractFacts);
            ErrorSearchPageSummaryText.Text =
                $"{presentation.PageSummary} · {presentation.OrderSummary}";
            ErrorSearchEmptyResultText.Text = presentation.EmptyResultMessage;
            SetTextAutomationName(
                ErrorSearchEmptyResultText,
                "错误检索空结果说明",
                string.IsNullOrEmpty(presentation.EmptyResultMessage)
                    ? "当前非成功零结果"
                    : presentation.EmptyResultMessage);
            SetTextAutomationName(ErrorSearchSnapshotText, "错误检索快照", presentation.SnapshotFacts);
            SetTextAutomationName(ErrorSearchWindowText, "错误检索窗口", presentation.CommittedWindow);
            SetTextAutomationName(ErrorSearchNormalizedFilterText, "错误检索规范化条件", presentation.CommittedConditions);
            SetTextAutomationName(
                ErrorSearchPageSummaryText,
                "错误检索精确总数、页码与排序",
                ErrorSearchPageSummaryText.Text);

            var showEmpty = presentation.EmptyResultMessage.Length > 0;
            ErrorSearchStatusInfoBar.IsOpen = presentation.IsInfoOpen || showEmpty;
            ErrorSearchStatusInfoBar.Severity = presentation.IsInfoOpen
                ? presentation.InfoSeverity is WatchPresentationSeverity.Warning
                    && state.ErrorSearch.LastFailureAt is not null
                        ? InfoBarSeverity.Error
                        : ToInfoBarSeverity(presentation.InfoSeverity)
                : InfoBarSeverity.Informational;
            ErrorSearchStatusInfoBar.Title = presentation.IsInfoOpen
                ? presentation.InfoTitle
                : showEmpty ? "当前条件没有历史" : string.Empty;
            ErrorSearchStatusInfoBar.Message = presentation.IsInfoOpen
                ? presentation.InfoMessage
                : showEmpty
                    ? $"当前条件查询成功；没有历史。{presentation.EmptyResultMessage}"
                    : string.Empty;
            AutomationProperties.SetName(
                ErrorSearchStatusInfoBar,
                ErrorSearchStatusInfoBar.IsOpen
                    ? $"{ErrorSearchStatusInfoBar.Title}。{ErrorSearchStatusInfoBar.Message}"
                    : "错误检索状态：当前无活动通知");

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
                : $"{detail.Heading} · 窗口命中边界：{string.Join("；", detail.Periods.Select(period => period.BoundarySummary).Distinct(StringComparer.Ordinal))} · {detail.GenerationSummary}";
            SetTextAutomationName(
                ErrorSearchDetailContextText,
                "错误检索窗口边界与 Demand 世代语义",
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
                    : "原始证据状态：尚未显式请求");
            ErrorSearchRawEvidenceGrid.ItemsSource = raw.Items;
            ErrorSearchRawEvidenceLimitsText.Text = raw.IsVisible && raw.IncludedFields.Count > 0
                ? $"白名单字段 {string.Join("、", raw.IncludedFields)} · {raw.LimitsSummary.Replace(",", string.Empty, StringComparison.Ordinal)} · 已返回 {raw.ItemCount:N0} 条 / {raw.PayloadBytes:N0} 字节"
                : $"显式按需；允许字段 {string.Join("、", ErrorSearchRawEvidenceFields.All)} · 最多 {ErrorSearchRawEvidenceLimits.MaximumItems} 条 · 单条 {ErrorSearchRawEvidenceLimits.MaximumItemBytes} 字节 · 总计 {ErrorSearchRawEvidenceLimits.MaximumTotalBytes} 字节";
            SetTextAutomationName(
                ErrorSearchRawEvidenceLimitsText,
                "错误检索原始证据白名单与上限",
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

    private void RenderCurrentAttention(WatchV2WorkspaceState state)
    {
        _isRenderingCurrentAttention = true;
        try
        {
            var presentation = WatchCurrentIngestAttentionPresentation.Project(
                state,
                _currentAttentionQuery);
            RenderCurrentAttentionHeader(state, presentation);
            var hadSelection = !string.IsNullOrWhiteSpace(_selectedCurrentAttentionIdentity);
            var selected = presentation.Rows.FirstOrDefault(row => string.Equals(
                row.StableIdentity,
                _selectedCurrentAttentionIdentity,
                StringComparison.Ordinal));
            var selectionNoLongerMatches = hadSelection
                && selected is null
                && presentation.HasSnapshot
                && !presentation.IsRefreshing
                && !presentation.IsStale
                && state.CurrentAttention.LastFailureAt is null;
            if (selectionNoLongerMatches)
            {
                _currentAttentionSelectionNotice =
                    $"原关注项 {_selectedCurrentAttentionIdentity} 已不在最新当前关注结果中；已清除选择与结构化证据，请重新选择。";
                _selectedCurrentAttentionIdentity = null;
            }
            else if (!hadSelection && string.IsNullOrEmpty(_currentAttentionSelectionNotice))
            {
                selected = presentation.Rows.FirstOrDefault();
            }

            CurrentAttentionSnapshotText.Text = presentation.SnapshotFacts;
            CurrentAttentionScopeText.Text =
                $"{presentation.AreaIsolationNotice} · {presentation.SemanticsNotice} · {presentation.HostFilterSummary}";
            CurrentAttentionPageSummaryText.Text =
                $"{presentation.PageSummary} · {presentation.OrderSummary}";
            CurrentAttentionEmptyResultText.Text = presentation.EmptyResultMessage;
            SetTextAutomationName(
                CurrentAttentionEmptyResultText,
                "接入告警空结果说明",
                string.IsNullOrEmpty(presentation.EmptyResultMessage)
                    ? "当前非成功零结果"
                    : presentation.EmptyResultMessage);
            SetTextAutomationName(CurrentAttentionSnapshotText, "接入告警快照", CurrentAttentionSnapshotText.Text);
            SetTextAutomationName(CurrentAttentionScopeText, "接入告警范围与语义", CurrentAttentionScopeText.Text);
            SetTextAutomationName(CurrentAttentionPageSummaryText, "接入告警精确总数与页码", CurrentAttentionPageSummaryText.Text);

            var showEmpty = presentation.EmptyResultMessage.Length > 0;
            var showSelectionNotice = !string.IsNullOrEmpty(_currentAttentionSelectionNotice);
            CurrentAttentionStatusInfoBar.IsOpen =
                presentation.IsInfoOpen || showSelectionNotice || showEmpty;
            CurrentAttentionStatusInfoBar.Severity = presentation.IsInfoOpen
                ? ToInfoBarSeverity(presentation.InfoSeverity)
                : showSelectionNotice
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Informational;
            CurrentAttentionStatusInfoBar.Title = presentation.IsInfoOpen
                ? presentation.InfoTitle
                : showSelectionNotice
                    ? "原关注项已不在刷新结果中"
                    : showEmpty ? "当前没有接入告警" : string.Empty;
            CurrentAttentionStatusInfoBar.Message = presentation.IsInfoOpen
                ? presentation.InfoMessage
                : showSelectionNotice
                    ? _currentAttentionSelectionNotice ?? string.Empty
                    : presentation.EmptyResultMessage;
            AutomationProperties.SetName(
                CurrentAttentionStatusInfoBar,
                CurrentAttentionStatusInfoBar.IsOpen
                    ? $"{CurrentAttentionStatusInfoBar.Title}。{CurrentAttentionStatusInfoBar.Message}"
                    : "接入告警状态：当前无活动通知");

            CurrentAttentionKindFacetGrid.ItemsSource = presentation.TypeFacets;
            CurrentAttentionSeverityFacetGrid.ItemsSource = presentation.SeverityFacets;
            CurrentAttentionGrid.ItemsSource = presentation.Rows;
            CurrentAttentionGrid.SelectedItem = selected;
            _selectedCurrentAttentionIdentity = selected?.StableIdentity;
            RenderCurrentAttentionEvidence(selected);
            CurrentAttentionPreviousPageButton.IsEnabled = presentation.CanGoPrevious;
            CurrentAttentionNextPageButton.IsEnabled = presentation.CanGoNext;
            if (state.CurrentAttention.Snapshot is { } snapshot)
            {
                CurrentAttentionPageNumberInput.Text = snapshot.PageNumber.ToString(CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _isRenderingCurrentAttention = false;
        }
    }

    private void RenderCurrentAttentionEvidence(
        WatchCurrentIngestAttentionRowPresentation? selected)
    {
        CurrentAttentionSelectedContextText.Text = selected is null
            ? "选择一项查看其稳定标识与 Host 结构化证据。"
            : $"{selected.KindLabel} · 稳定标识 {selected.StableIdentity} · {selected.SubjectSummary}";
        SetTextAutomationName(
            CurrentAttentionSelectedContextText,
            "接入告警选中项上下文",
            CurrentAttentionSelectedContextText.Text);
        CurrentAttentionEvidenceGrid.ItemsSource = selected is null
            ? []
            : ProjectEvidenceFacts(selected.Evidence);
        CurrentAttentionOpenErrorSearchButton.IsEnabled = selected?.ErrorSearchDrill is not null;
    }

    private static IReadOnlyList<WatchEvidenceFactPresentation> ProjectEvidenceFacts(
        WatchCurrentIngestAttentionEvidencePresentation evidence)
    {
        var rows = new List<WatchEvidenceFactPresentation>();
        Add("ProjectionCommitId", evidence.ProjectionCommitId);
        Add("ProjectionSequence", evidence.ProjectionSequence);
        Add("PollTraceId", evidence.PollTraceId);
        Add("PollTraceSequence", evidence.PollTraceSequence);
        Add("SeriesId", evidence.SeriesId);
        Add("DemandId", evidence.DemandId);
        Add("WorkType", evidence.WorkType);
        Add("ObservationOrdinal", evidence.ObservationOrdinal);
        Add("EvidenceId", evidence.EvidenceId);
        Add("ContentDigest", evidence.ContentDigest);
        Add("Phase", evidence.Phase);
        Add("Outcome", evidence.Outcome);
        return rows;

        void Add(string name, object? value)
        {
            if (value is not null)
            {
                rows.Add(new WatchEvidenceFactPresentation(
                    name,
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
            }
        }
    }

    private static void SetTextAutomationName(TextBlock control, string label, string value) =>
        AutomationProperties.SetName(control, $"{label}：{value}");

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
        _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
        ErrorSearchRawEvidenceInfoBar.IsOpen = false;
        AutomationProperties.SetName(
            ErrorSearchRawEvidenceInfoBar,
            "原始证据状态：尚未显式请求");
        ErrorSearchRawEvidenceGrid.ItemsSource = Array.Empty<WatchErrorRawItemPresentation>();
        ErrorSearchRawEvidenceLimitsText.Text =
            $"显式按需；允许字段 {string.Join("、", ErrorSearchRawEvidenceFields.All)} · 最多 {ErrorSearchRawEvidenceLimits.MaximumItems} 条 · 单条 {ErrorSearchRawEvidenceLimits.MaximumItemBytes} 字节 · 总计 {ErrorSearchRawEvidenceLimits.MaximumTotalBytes} 字节";
        SetTextAutomationName(
            ErrorSearchRawEvidenceLimitsText,
            "错误检索原始证据白名单与上限",
            ErrorSearchRawEvidenceLimitsText.Text);
    }

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

    private void OnCurrentAttentionApplyFiltersClick(object sender, RoutedEventArgs e)
    {
        CurrentAttentionOperationTask = RunCurrentAttentionUiActionAsync(async () =>
        {
            var query = WatchCurrentIngestAttentionQueries.StartLatest(
                ReadChoiceValues(CurrentAttentionKindFilter),
                ReadChoiceValues(CurrentAttentionSeverityFilter),
                ReadCurrentAttentionPageSize());
            SyncCurrentAttentionFilterControls(query);
            await RefreshCurrentAttentionAndRenderAsync(query, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
    }

    private void OnCurrentAttentionClearFiltersClick(object sender, RoutedEventArgs e)
    {
        CurrentAttentionOperationTask = RunCurrentAttentionUiActionAsync(async () =>
        {
            var query = WatchCurrentIngestAttentionQueries.StartLatest();
            SyncCurrentAttentionFilterControls(query);
            await RefreshCurrentAttentionAndRenderAsync(query, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
    }

    private void OnCurrentAttentionPreviousPageClick(object sender, RoutedEventArgs e)
    {
        CurrentAttentionOperationTask = RunCurrentAttentionUiActionAsync(async () =>
        {
            var snapshot = _session.State.CurrentAttention.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的接入告警快照。");
            var query = WatchCurrentIngestAttentionQueries.OpenPreviousPage(snapshot)
                ?? throw new InvalidOperationException("当前接入告警已经是第一页。");
            await RefreshCurrentAttentionAndRenderAsync(query, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
    }

    private void OnCurrentAttentionNextPageClick(object sender, RoutedEventArgs e)
    {
        CurrentAttentionOperationTask = RunCurrentAttentionUiActionAsync(async () =>
        {
            var snapshot = _session.State.CurrentAttention.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的接入告警快照。");
            var query = WatchCurrentIngestAttentionQueries.OpenNextPage(snapshot)
                ?? throw new InvalidOperationException("当前接入告警已经是最后一页。");
            await RefreshCurrentAttentionAndRenderAsync(query, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
    }

    private void OnCurrentAttentionGoToPageClick(object sender, RoutedEventArgs e)
    {
        LogCurrentAttentionGoToPageRenderingState("handler-entry");
        CurrentAttentionOperationTask = RunCurrentAttentionUiActionAsync(async () =>
        {
            var snapshot = _session.State.CurrentAttention.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的接入告警快照。");
            if (!int.TryParse(
                    CurrentAttentionPageNumberInput.Text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var pageNumber))
            {
                throw new ArgumentException("页码必须是整数。");
            }

            var query = WatchCurrentIngestAttentionQueries.OpenPage(snapshot, pageNumber);
            await RefreshCurrentAttentionAndRenderAsync(query, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            _ = LogCurrentAttentionGoToPageRenderingTimelineAsync();
        });
    }

    private async Task LogCurrentAttentionGoToPageRenderingTimelineAsync()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MESINGEST_WATCH_UI_TEST_MODE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var delays = new[] { 0, 250, 500, 750, 1000, 1500, 2000 };
        var elapsed = 0;
        foreach (var delay in delays)
        {
            if (delay > 0)
            {
                await Task.Delay(delay).ConfigureAwait(true);
                elapsed += delay;
            }

            await Dispatcher.InvokeAsync(
                () =>
                {
                    UpdateLayout();
                    LogCurrentAttentionGoToPageRenderingState($"after-refresh+{elapsed}ms");
                },
                DispatcherPriority.Render);
        }
    }

    private void LogCurrentAttentionGoToPageRenderingState(string stage)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MESINGEST_WATCH_UI_TEST_MODE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var button = CurrentAttentionGoToPageButton;
        button.ApplyTemplate();
        var presenter = button.Template.FindName("ContentPresenter", button)
            as ContentPresenter;
        var text = FindVisualDescendant<FrameworkElement>(
            button,
            element => element is TextBlock or AccessText);
        var focused = Keyboard.FocusedElement as FrameworkElement;

        Console.WriteLine(
            "[DEBUG-T23FONT] "
            + $"stage={stage} "
            + $"windowActive={IsActive} "
            + $"buttonPressed={button.IsPressed} "
            + $"buttonKeyboardFocused={button.IsKeyboardFocused} "
            + $"buttonKeyboardFocusWithin={button.IsKeyboardFocusWithin} "
            + $"buttonMouseOver={button.IsMouseOver} "
            + $"buttonMouseCaptured={button.IsMouseCaptured} "
            + $"buttonEnabled={button.IsEnabled} "
            + $"focused={focused?.GetType().Name}:{focused?.Name} "
            + $"button={FormatTextRenderingElement(button)} "
            + $"foreground={FormatBrush(button.Foreground)} "
            + $"pressedForeground={FormatBrush(button.PressedForeground)} "
            + $"presenter={FormatTextRenderingElement(presenter)} "
            + $"presenterForeground={FormatBrush(
                presenter is null ? null : TextElement.GetForeground(presenter))} "
            + $"text={FormatTextRenderingElement(text)}");
    }

    private string FormatTextRenderingElement(FrameworkElement? element)
    {
        if (element is null)
        {
            return "(null)";
        }

        var point = element.TranslatePoint(new Point(0, 0), this);
        return $"{element.GetType().Name}"
            + $"@{point.X:R},{point.Y:R}"
            + $"/{element.ActualWidth:R}x{element.ActualHeight:R}"
            + $"/opacity={element.Opacity:R}"
            + $"/render={TextOptions.GetTextRenderingMode(element)}"
            + $"/format={TextOptions.GetTextFormattingMode(element)}"
            + $"/hint={TextOptions.GetTextHintingMode(element)}"
            + $"/snaps={element.SnapsToDevicePixels}"
            + $"/rounds={element.UseLayoutRounding}";
    }

    private static string FormatBrush(Brush? brush) => brush switch
    {
        SolidColorBrush solid =>
            $"Solid({solid.Color})/opacity={solid.Opacity:R}/frozen={solid.IsFrozen}",
        null => "(null)",
        _ => $"{brush.GetType().Name}/opacity={brush.Opacity:R}/frozen={brush.IsFrozen}",
    };

    private void OnCurrentAttentionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingCurrentAttention)
        {
            return;
        }

        var selected = CurrentAttentionGrid.SelectedItem
            as WatchCurrentIngestAttentionRowPresentation;
        _selectedCurrentAttentionIdentity = selected?.StableIdentity;
        _currentAttentionSelectionNotice = null;
        RenderCurrentAttentionEvidence(selected);
        if (CurrentAttentionStatusInfoBar.IsOpen)
        {
            RenderWorkspace();
        }
    }

    private void OnCurrentAttentionOpenErrorSearchClick(object sender, RoutedEventArgs e)
    {
        var selected = CurrentAttentionGrid.SelectedItem
            as WatchCurrentIngestAttentionRowPresentation;
        if (selected?.ErrorSearchDrill is not { } query)
        {
            CurrentAttentionOperationTask = RunCurrentAttentionUiActionAsync(() =>
                throw new InvalidOperationException("请选择一条活动 Series 错误后再下钻。"));
            return;
        }

        _errorSearchQuery = query;
        _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
        SyncErrorSearchFilterControls(query);
        NavigateTo(WatchWorkspacePage.ErrorSearch);
        ErrorSearchNavigationTask = LoadErrorSearchNavigationAsync(_lifetimeCancellation.Token);
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
            ErrorSearchStatusInfoBar.IsOpen = true;
            ErrorSearchStatusInfoBar.Severity = InfoBarSeverity.Error;
            ErrorSearchStatusInfoBar.Title = "无法执行错误检索操作";
            ErrorSearchStatusInfoBar.Message = exception.Message;
            AutomationProperties.SetName(
                ErrorSearchStatusInfoBar,
                $"{ErrorSearchStatusInfoBar.Title}。{ErrorSearchStatusInfoBar.Message}");
        }
    }

    private async Task RunCurrentAttentionUiActionAsync(Func<Task> action)
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
            or CurrentIngestAttentionException)
        {
            CurrentAttentionStatusInfoBar.IsOpen = true;
            CurrentAttentionStatusInfoBar.Severity = InfoBarSeverity.Error;
            CurrentAttentionStatusInfoBar.Title = "无法执行接入告警操作";
            CurrentAttentionStatusInfoBar.Message = exception.Message;
            AutomationProperties.SetName(
                CurrentAttentionStatusInfoBar,
                $"{CurrentAttentionStatusInfoBar.Title}。{CurrentAttentionStatusInfoBar.Message}");
        }
    }

    private void ReflowTicket22Pages(double contentWidth, bool useOuterScrolling)
    {
        var stack = contentWidth < (double)FindResource("Ticket22ThreeCardStackBreakpoint");
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
        ReflowCurrentAttention(stack, useOuterScrolling);
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

    private void ReflowCurrentAttention(bool stack, bool useOuterScrolling)
    {
        ConfigureResponsivePageViewport(
            CurrentAttentionLayoutGrid,
            CurrentAttentionPage,
            stack || useOuterScrolling);

        Grid.SetColumn(CurrentAttentionFacetCard, 0);
        Grid.SetRow(CurrentAttentionFacetCard, 0);
        Grid.SetColumnSpan(CurrentAttentionFacetCard, stack ? 1 : 3);
        Grid.SetColumn(CurrentAttentionResultsCard, 0);
        Grid.SetRow(CurrentAttentionResultsCard, 2);
        Grid.SetColumn(CurrentAttentionEvidenceCard, stack ? 0 : 2);
        Grid.SetRow(CurrentAttentionEvidenceCard, stack ? 4 : 2);

        CurrentAttentionMasterColumn.Width = stack
            ? new GridLength(1, GridUnitType.Star)
            : (GridLength)FindResource("CurrentAttentionMasterColumnWidth");
        CurrentAttentionMasterDetailGapColumn.Width = stack
            ? new GridLength(0)
            : new GridLength(16);
        CurrentAttentionDetailColumn.Width = stack
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        CurrentAttentionFirstGapRow.Height = new GridLength(16);
        CurrentAttentionResultsRow.Height = stack
            ? GridLength.Auto
            : new GridLength(1, GridUnitType.Star);
        CurrentAttentionSecondGapRow.Height = stack
            ? new GridLength(16)
            : new GridLength(0);
        CurrentAttentionEvidenceRow.Height = stack
            ? GridLength.Auto
            : new GridLength(0);
        CurrentAttentionGrid.Height = stack
            ? (double)FindResource("Ticket22ResultsGridMinHeight")
            : double.NaN;
        CurrentAttentionKindFilterColumn.Width = new GridLength(stack ? 170 : 200);
        CurrentAttentionSeverityFilterColumn.Width = new GridLength(stack ? 160 : 210);
        CurrentAttentionPageSizeFilterColumn.Width = new GridLength(stack ? 70 : 90);
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

    private sealed record WatchEvidenceFactPresentation(string Name, string Value);
}
