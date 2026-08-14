using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using MesIngest.Core.SeriesProjection;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace MesIngest.Watch;

internal partial class WatchWorkspaceWindow
{
    private WatchAreaFilterProfileStore _areaProfileStore = null!;
    private WatchAreaFilterProfile? _areaProfileDraft;
    private string? _selectedAreaProfileName;
    private string? _areaProfileStartupError;
    private bool _areaProfileDraftIsDirty;
    private bool _isRenderingAreaProfiles;
    private bool _isRenderingReadabilityAudit;
    private long _areaProfileOperationGeneration;
    private long _readabilityAuditOperationGeneration;

    internal Task ReadabilityAuditNavigationTask { get; private set; } = Task.CompletedTask;

    internal Task AreaProfileOperationTask { get; private set; } = Task.CompletedTask;

    private void InitializeAreaFilterProfiles(
        string? areaFilterProfilesDirectoryPath,
        TimeProvider? timeProvider)
    {
        _areaProfileStore = new WatchAreaFilterProfileStore(
            areaFilterProfilesDirectoryPath,
            timeProvider);
        try
        {
            var appliedState = _areaProfileStore.LoadAppliedState();
            _areaContext = appliedState.CurrentApplied.ToDisplayContext();
            _areaProfileStartupError = appliedState.Diagnostic?.Message;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            _areaContext = WatchAreaDisplayContext.AllAreas;
            _areaProfileStartupError = exception.Message;
        }

        _overviewQuery = new WatchOverviewQuery(_areaContext.MesAreas).NormalizeAndValidate();
        _demandSeriesQuery = WatchDemandSeriesQueries.StartLatest(
            new DemandSeriesBrowseFilter { MesAreas = _areaContext.MesAreas },
            _demandSeriesQuery.PageSize);
        _readabilityAuditQuery = WatchReadabilityAuditQueries.StartLatest(
            new ReadabilityAuditFilter { MesAreas = _areaContext.MesAreas },
            _readabilityAuditQuery.PageSize);
    }

    private void InitializeReadabilityAuditAndAreaProfiles()
    {
        WatchGridClipboardBehavior.Attach(ReadabilityStateFacetGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityBlockerFacetGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityAuditGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityQualificationGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityBlockerEvidenceGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityRawObservationGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(AreaProfileValidationGrid, preserveSelectionUnit: true);

        ReadabilityWorkTypeFilter.Items.Add(new ComboBoxItem
        {
            Content = "全部 WorkType",
            Tag = string.Empty,
        });
        ReadabilityBlockerFilter.Items.Add(new ComboBoxItem
        {
            Content = "全部阻断原因",
            Tag = string.Empty,
        });
        foreach (var definition in ReadabilityBlockerCatalog.Definitions)
        {
            ReadabilityBlockerFilter.Items.Add(new ComboBoxItem
            {
                Content = definition.Code,
                Tag = definition.Code,
                ToolTip = definition.Meaning,
            });
        }

        ReadabilityWorkTypeFilter.SelectedIndex = 0;
        ReadabilityBlockerFilter.SelectedIndex = 0;
        SyncReadabilityFilterControls(_readabilityAuditQuery);
        RenderAreaProfiles();
        if (_areaProfileStartupError is not null)
        {
            ShowAreaProfileInfo(
                InfoBarSeverity.Warning,
                "无法恢复上次 AREA 配置",
                $"已回退到全部 AREA。{_areaProfileStartupError}");
        }
    }

    private async Task LoadReadabilityAuditNavigationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshLatestReadabilityAuditAndRenderAsync(
                    _readabilityAuditQuery,
                    desiredDemandId: null,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || _lifetimeCancellation.IsCancellationRequested)
        {
            // Leaving the page or closing the window is a neutral end to navigation.
        }
    }

    private async Task RefreshLatestReadabilityAuditAndRenderAsync(
        ReadabilityAuditQuery query,
        string? desiredDemandId,
        CancellationToken cancellationToken)
    {
        var operation = BeginReadabilityAuditOperation();
        if (!await SuspendReadabilityAuditAutoRefreshAsync(operation, cancellationToken)
                .ConfigureAwait(true))
        {
            return;
        }

        _readabilityAuditQuery = query;
        var refresh = _session.RefreshLatestReadabilityAuditPageAsync(query, cancellationToken);
        RenderWorkspace();
        await refresh.ConfigureAwait(true);
        if (!IsCurrentReadabilityAuditOperation(operation, cancellationToken))
        {
            return;
        }

        RenderWorkspace();
        var view = _session.State.ReadabilityAudit;
        if (!view.IsStale && view.Snapshot is { } committed)
        {
            _readabilityAuditQuery = CanonicalReadabilityAuditAutoRefreshQuery(committed);
            if (!string.IsNullOrWhiteSpace(desiredDemandId)
                && committed.Items.Any(item => string.Equals(
                    item.DemandId,
                    desiredDemandId,
                    StringComparison.Ordinal)))
            {
                await SelectReadabilityDemandAndRenderAsync(
                        desiredDemandId,
                        operation,
                        cancellationToken,
                        manageAutoRefresh: false)
                    .ConfigureAwait(true);
                if (!IsCurrentReadabilityAuditOperation(operation, cancellationToken))
                {
                    return;
                }
            }
        }

        ReactivateReadabilityAuditIfCurrentPage();
    }

    private async Task RefreshFrozenReadabilityAuditAndRenderAsync(
        ReadabilityAuditQuery frozenRequest,
        ReadabilityAuditQuery automaticRequest,
        CancellationToken cancellationToken)
    {
        var operation = BeginReadabilityAuditOperation();
        if (!await SuspendReadabilityAuditAutoRefreshAsync(operation, cancellationToken)
                .ConfigureAwait(true))
        {
            return;
        }

        _readabilityAuditQuery = automaticRequest;
        var refresh = _session.RefreshReadabilityAuditAsync(frozenRequest, cancellationToken);
        RenderWorkspace();
        await refresh.ConfigureAwait(true);
        if (!IsCurrentReadabilityAuditOperation(operation, cancellationToken))
        {
            return;
        }

        RenderWorkspace();
        var view = _session.State.ReadabilityAudit;
        if (!view.IsStale && view.Snapshot is { } committed)
        {
            _readabilityAuditQuery = CanonicalReadabilityAuditAutoRefreshQuery(committed);
        }

        ReactivateReadabilityAuditIfCurrentPage();
    }

    internal Task SelectReadabilityDemandAndRenderAsync(
        string? demandId,
        CancellationToken cancellationToken = default) =>
        SelectReadabilityDemandAndRenderAsync(
            demandId,
            BeginReadabilityAuditOperation(),
            cancellationToken,
            manageAutoRefresh: true);

    private async Task SelectReadabilityDemandAndRenderAsync(
        string? demandId,
        long operation,
        CancellationToken cancellationToken,
        bool manageAutoRefresh)
    {
        if (manageAutoRefresh
            && !await SuspendReadabilityAuditAutoRefreshAsync(operation, cancellationToken)
                .ConfigureAwait(true))
        {
            return;
        }

        if (!IsCurrentReadabilityAuditOperation(operation, cancellationToken))
        {
            return;
        }

        var selection = _session.SelectReadabilityDemandAsync(demandId, cancellationToken);
        RenderWorkspace();
        await selection.ConfigureAwait(true);
        if (!IsCurrentReadabilityAuditOperation(operation, cancellationToken))
        {
            return;
        }

        RenderWorkspace();
        if (manageAutoRefresh)
        {
            ReactivateReadabilityAuditIfCurrentPage();
        }
    }

    private long BeginReadabilityAuditOperation() =>
        Interlocked.Increment(ref _readabilityAuditOperationGeneration);

    private bool IsCurrentReadabilityAuditOperation(
        long operation,
        CancellationToken cancellationToken) =>
        !_disposed
        && !cancellationToken.IsCancellationRequested
        && operation == Interlocked.Read(ref _readabilityAuditOperationGeneration);

    private async Task<bool> SuspendReadabilityAuditAutoRefreshAsync(
        long operation,
        CancellationToken cancellationToken)
    {
        if (_autoRefresh.ActiveView == WatchV2DataView.ReadabilityAudit)
        {
            _autoRefresh.Deactivate();
            await _autoRefresh.WaitForIdleAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(true);
        }

        return IsCurrentReadabilityAuditOperation(operation, cancellationToken);
    }

    private void ReactivateReadabilityAuditIfCurrentPage()
    {
        if (_activePage == WatchWorkspacePage.ReadabilityAudit
            && _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            _autoRefresh.ActivateReadabilityAudit(_readabilityAuditQuery);
        }
    }

    private static ReadabilityAuditQuery CanonicalReadabilityAuditAutoRefreshQuery(
        ReadabilityAuditListSnapshot snapshot) => snapshot.TotalPages == 0
        ? WatchReadabilityAuditQueries.StartLatest(snapshot.Filter, snapshot.PageSize)
        : WatchReadabilityAuditQueries.OpenFrozenPage(snapshot, snapshot.PageNumber);

    private ReadabilityAuditFilter ReadReadabilityAuditFilter()
    {
        var readabilityStates = ReadReadabilityChoices(ReadabilityStateFilter);
        if (readabilityStates.Any(state => state is not ExternalReadabilityStates.Readable
                and not ExternalReadabilityStates.NotReadable))
        {
            throw new ArgumentException("资格只能是 READABLE 或 NOT_READABLE；多个值请用逗号分隔。");
        }

        var blockers = ReadReadabilityChoices(ReadabilityBlockerFilter);
        var knownBlockers = ReadabilityBlockerCatalog.Definitions
            .Select(definition => definition.Code)
            .ToHashSet(StringComparer.Ordinal);
        if (blockers.Any(blocker => !knownBlockers.Contains(blocker)))
        {
            throw new ArgumentException("阻断原因必须使用页面列出的稳定 blocker code；多个值请用逗号分隔。");
        }

        return new ReadabilityAuditFilter
        {
            ReadabilityStates = readabilityStates,
            WorkTypes = ReadReadabilityChoices(ReadabilityWorkTypeFilter),
            Blockers = blockers,
            DemandId = ReadReadabilityText(ReadabilityDemandIdFilter.Text),
            SublotContains = string.IsNullOrWhiteSpace(ReadabilitySublotFilter.Text)
                ? null
                : ReadabilitySublotFilter.Text,
            MesAreas = _areaContext.MesAreas,
        };
    }

    private static IReadOnlyList<string> ReadReadabilityChoices(ComboBox comboBox)
    {
        var value = comboBox.IsEditable
            ? comboBox.Text
            : comboBox.SelectedItem is ComboBoxItem item
                ? item.Tag?.ToString() ?? item.Content?.ToString()
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

    private static string? ReadReadabilityText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private int ReadReadabilityPageSize()
    {
        var value = ReadabilityPageSizeInput.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? item.Content?.ToString()
            : ReadabilityPageSizeInput.Text;
        return int.TryParse(value, out var pageSize)
            && pageSize is ReadabilityAuditQuery.DefaultPageSize
                or ReadabilityAuditQuery.MaximumPageSize
            ? pageSize
            : throw new ArgumentException("每页数量必须是 100 或 200。");
    }

    private void SyncReadabilityFilterControls(ReadabilityAuditQuery query)
    {
        _isRenderingReadabilityAudit = true;
        try
        {
            SelectReadabilityChoice(ReadabilityStateFilter, query.Filter.ReadabilityStates);
            SelectReadabilityChoice(ReadabilityWorkTypeFilter, query.Filter.WorkTypes);
            SelectReadabilityChoice(ReadabilityBlockerFilter, query.Filter.Blockers);
            ReadabilityDemandIdFilter.Text = query.Filter.DemandId ?? string.Empty;
            ReadabilitySublotFilter.Text = query.Filter.SublotContains ?? string.Empty;
            SelectReadabilityChoice(ReadabilityPageSizeInput, [query.PageSize.ToString()]);
        }
        finally
        {
            _isRenderingReadabilityAudit = false;
        }

        UpdateReadabilityClearFilterState();
    }

    private void OnReadabilityFilterDraftChanged(object sender, RoutedEventArgs e)
    {
        if (!_isRenderingReadabilityAudit)
        {
            UpdateReadabilityClearFilterState();
        }
    }

    private void UpdateReadabilityClearFilterState()
    {
        if (ReadabilityClearFilterButton is null)
        {
            return;
        }

        try
        {
            var draft = ReadReadabilityAuditFilter();
            ReadabilityClearFilterButton.IsEnabled =
                draft.ReadabilityStates.Count > 0
                || draft.WorkTypes.Count > 0
                || draft.Blockers.Count > 0
                || draft.DemandId is not null
                || draft.SublotContains is not null
                || ReadReadabilityPageSize() != ReadabilityAuditQuery.DefaultPageSize;
        }
        catch (ArgumentException)
        {
            // An invalid non-empty draft is still clearable back to the default query.
            ReadabilityClearFilterButton.IsEnabled = true;
        }
    }

    private static void SelectReadabilityChoice(
        ComboBox comboBox,
        IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            comboBox.SelectedIndex = 0;
            return;
        }

        if (values.Count == 1)
        {
            foreach (var candidate in comboBox.Items.OfType<ComboBoxItem>())
            {
                var candidateValue = candidate.Tag?.ToString() ?? candidate.Content?.ToString();
                if (string.Equals(candidateValue, values[0], StringComparison.Ordinal))
                {
                    comboBox.SelectedItem = candidate;
                    return;
                }
            }
        }

        if (comboBox.IsEditable)
        {
            comboBox.SelectedIndex = -1;
            comboBox.Text = string.Join(", ", values);
        }
    }

    private void AddObservedReadabilityWorkTypes(IEnumerable<string> values)
    {
        var existing = ReadabilityWorkTypeFilter.Items
            .OfType<ComboBoxItem>()
            .Select(item => item.Content?.ToString())
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var value in values
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (existing.Add(value))
            {
                ReadabilityWorkTypeFilter.Items.Add(new ComboBoxItem
                {
                    Content = value,
                    Tag = value,
                });
            }
        }
    }

    private void RenderReadabilityAudit(WatchV2WorkspaceState state)
    {
        _isRenderingReadabilityAudit = true;
        try
        {
            var presentation = WatchReadabilityAuditPresentation.Project(
                state,
                _readabilityAuditQuery,
                _areaContext);
            ReadabilityRefreshPolicyText.Text = state.ReadabilityAudit.IsRefreshing
                ? "正在读取 · 自动刷新保持开启"
                : $"每 {_preferences.RefreshIntervals.ReadabilityAudit.IntervalSeconds} 秒自动刷新";
            ReadabilityAuditInfoBar.IsOpen = presentation.IsInfoOpen;
            ReadabilityAuditInfoBar.Severity = ToInfoBarSeverity(presentation.InfoSeverity);
            ReadabilityAuditInfoBar.Title = presentation.InfoTitle;
            ReadabilityAuditInfoBar.Message = presentation.InfoMessage;
            AutomationProperties.SetName(
                ReadabilityAuditInfoBar,
                presentation.IsInfoOpen
                    ? $"{presentation.InfoTitle}。{presentation.InfoMessage}"
                    : "资格审计读取状态：当前无活动通知");
            ReadabilitySnapshotFactsText.Text = presentation.SnapshotFacts;
            ReadabilityClientAttemptText.Text = presentation.ClientAttemptFacts;
            ReadabilityAreaScopeText.Text =
                $"{presentation.LocalAreaHeading} · {presentation.LocalAreaDetail} · {presentation.HostAreaScope}";
            ReadabilityOrderSummaryText.Text =
                $"{presentation.OrderSummary} · {presentation.HostFilterSummary}";
            ReadabilityPageSummaryText.Text = presentation.PageSummary;
            AutomationProperties.SetName(
                ReadabilityPageSummaryText,
                $"资格审计精确总数与页码：{presentation.PageSummary}");
            AutomationProperties.SetName(
                ReadabilityAreaScopeText,
                $"资格审计 AREA 范围：{ReadabilityAreaScopeText.Text}");

            ReadabilityStateFacetGrid.ItemsSource = presentation.StateFacets;
            ReadabilityBlockerFacetGrid.ItemsSource = presentation.BlockerFacets;
            AddObservedReadabilityWorkTypes(presentation.Rows.Select(row => row.WorkType));
            ReadabilityAuditGrid.ItemsSource = presentation.Rows;
            ReadabilityAuditGrid.SelectedItem = presentation.Rows.FirstOrDefault(row =>
                string.Equals(
                    row.DemandId,
                    state.ReadabilityAudit.SelectedId,
                    StringComparison.Ordinal));

            ReadabilityEmptyInfoBar.IsOpen = presentation.EmptyResultMessage.Length > 0;
            ReadabilityEmptyInfoBar.Title = presentation.EmptyResultMessage.Length > 0
                ? "当前已提交条件没有命中"
                : string.Empty;
            ReadabilityEmptyInfoBar.Message = presentation.EmptyResultMessage;
            AutomationProperties.SetName(
                ReadabilityEmptyInfoBar,
                presentation.EmptyResultMessage.Length > 0
                    ? $"{ReadabilityEmptyInfoBar.Title}。{presentation.EmptyResultMessage}"
                    : "资格审计结果不为空或尚未成功读取");
            ReadabilityPreviousPageButton.IsEnabled = presentation.CanGoPrevious
                && !presentation.IsRefreshing;
            ReadabilityNextPageButton.IsEnabled = presentation.CanGoNext
                && !presentation.IsRefreshing;
            ReadabilityGoToPageButton.IsEnabled = presentation.HasSnapshot
                && !presentation.IsRefreshing;
            ReadabilityPageNumberInput.Text = state.ReadabilityAudit.Snapshot?.PageNumber
                .ToString() ?? "1";

            var detail = presentation.Detail;
            ReadabilityDetailHeadingText.Text = detail?.Heading ?? "选择一个 Demand 世代";
            ReadabilityDetailFactsText.Text = detail is null
                ? presentation.SelectionNotice
                    ?? "详情必须与当前列表使用同一个冻结 snapshotReference。"
                : $"{detail.Facts} · 全部阻断 {detail.AllBlockersSummary}";
            ReadabilitySeriesFactsText.Text = detail?.SeriesFacts ?? "尚未选择 Series。";
            ReadabilityLiveMesFactsText.Text = detail is null
                ? "选择后显示可信 LiveMesFieldSet，或保留全部原始观测冲突证据。"
                : $"{detail.LiveMesFacts} · {detail.ObservationSummary} · {detail.PollTraceFacts}";
            ReadabilityQualificationGrid.ItemsSource = detail?.QualificationChecks;
            ReadabilityBlockerEvidenceGrid.ItemsSource = detail?.BlockerEvidence;
            ReadabilityRawObservationGrid.ItemsSource = detail?.RawObservations;
            ReadabilityOpenSeriesButton.IsEnabled = detail is not null;
            ReadabilityDetailInfoBar.IsOpen = detail is null;
            var selectedDemandId = state.ReadabilityAudit.SelectedId;
            var detailReadFailed = selectedDemandId is not null
                && state.ReadabilityAudit.LastFailureAt is not null;
            ReadabilityDetailInfoBar.Severity = presentation.SelectionNotice is not null
                || detailReadFailed
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Informational;
            ReadabilityDetailInfoBar.Title = presentation.SelectionNotice is not null
                ? "原选择已清除"
                : selectedDemandId is null
                    ? "尚未选择 Demand"
                    : detailReadFailed
                        ? "无法读取同快照详情"
                        : "正在读取同快照详情";
            ReadabilityDetailInfoBar.Message = presentation.SelectionNotice
                ?? (selectedDemandId is null
                    ? "从左侧列表选择 Demand 后读取同一审计快照的资格检查与全部证据。"
                    : detailReadFailed
                        ? $"Demand {selectedDemandId} 的详情读取失败；列表仍属于上方标明的冻结快照。"
                        : $"已选择 Demand {selectedDemandId}；正在读取当前 snapshotReference 的详情。");
            AutomationProperties.SetName(
                ReadabilityDetailInfoBar,
                $"{ReadabilityDetailInfoBar.Title}。{ReadabilityDetailInfoBar.Message}");
        }
        finally
        {
            _isRenderingReadabilityAudit = false;
        }
    }

    private async void OnReadabilityApplyFiltersClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var query = WatchReadabilityAuditQueries.StartLatest(
                ReadReadabilityAuditFilter(),
                ReadReadabilityPageSize());
            await RefreshLatestReadabilityAuditAndRenderAsync(
                    query,
                    desiredDemandId: null,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnReadabilityClearFiltersClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var query = WatchReadabilityAuditQueries.StartLatest(
                new ReadabilityAuditFilter { MesAreas = _areaContext.MesAreas },
                ReadabilityAuditQuery.DefaultPageSize);
            SyncReadabilityFilterControls(query);
            await RefreshLatestReadabilityAuditAndRenderAsync(
                    query,
                    desiredDemandId: null,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnReadabilityPreviousPageClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var snapshot = _session.State.ReadabilityAudit.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的资格审计快照。");
            var request = WatchReadabilityAuditQueries.OpenFrozenPage(
                snapshot,
                snapshot.PageNumber - 1);
            await RefreshFrozenReadabilityAuditAndRenderAsync(
                    request,
                    request,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnReadabilityNextPageClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var snapshot = _session.State.ReadabilityAudit.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的资格审计快照。");
            var request = WatchReadabilityAuditQueries.OpenNextFrozenPage(snapshot)
                ?? throw new InvalidOperationException("当前冻结审计快照没有下一页。");
            var automaticRequest = WatchReadabilityAuditQueries.OpenFrozenPage(
                snapshot,
                snapshot.PageNumber + 1);
            await RefreshFrozenReadabilityAuditAndRenderAsync(
                    request,
                    automaticRequest,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnReadabilityGoToPageClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var snapshot = _session.State.ReadabilityAudit.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的资格审计快照。");
            if (!int.TryParse(ReadabilityPageNumberInput.Text, out var pageNumber))
            {
                throw new ArgumentException("页码必须是整数。");
            }

            var request = WatchReadabilityAuditQueries.OpenFrozenPage(snapshot, pageNumber);
            await RefreshFrozenReadabilityAuditAndRenderAsync(
                    request,
                    request,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnReadabilitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingReadabilityAudit)
        {
            return;
        }

        await RunReadabilityUiActionAsync(() => SelectReadabilityDemandAndRenderAsync(
            (ReadabilityAuditGrid.SelectedItem as WatchReadabilityAuditRowPresentation)?.DemandId,
            _lifetimeCancellation.Token)).ConfigureAwait(true);
    }

    private async void OnReadabilityOpenSeriesClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var snapshot = _session.State.ReadabilityAudit.Snapshot
                ?? throw new InvalidOperationException("当前没有资格审计快照。");
            var selectedDemandId = _session.State.ReadabilityAudit.SelectedId
                ?? throw new InvalidOperationException("请先选择一个 Demand。");
            var detail = _session.State.ReadabilityAudit.Detail;
            if (detail is null
                || !string.Equals(
                    detail.SnapshotReference,
                    snapshot.SnapshotReference,
                    StringComparison.Ordinal)
                || !string.Equals(
                    detail.Demand.DemandId,
                    selectedDemandId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("当前没有与冻结资格审计快照一致的 Demand 详情。");
            }
            var navigation = WatchDemandSeriesNavigationContext.FromReadabilityAudit(
                snapshot,
                detail.Series.SeriesId,
                detail.Demand.DemandId);
            await NavigateToDemandSeriesAsync(navigation, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async Task RunReadabilityUiActionAsync(Func<Task> action)
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
            or ReadabilityAuditException)
        {
            ReadabilityAuditInfoBar.IsOpen = true;
            ReadabilityAuditInfoBar.Severity = InfoBarSeverity.Error;
            ReadabilityAuditInfoBar.Title = "无法执行资格审计操作";
            ReadabilityAuditInfoBar.Message = exception.Message;
            AutomationProperties.SetName(
                ReadabilityAuditInfoBar,
                $"{ReadabilityAuditInfoBar.Title}。{ReadabilityAuditInfoBar.Message}");
        }
    }

    private void RenderAreaProfiles()
    {
        if (AreaProfileList is null || _areaProfileStore is null)
        {
            return;
        }

        _isRenderingAreaProfiles = true;
        try
        {
            IReadOnlyList<WatchAreaFilterProfileSummary> profiles;
            WatchAppliedAreaFilterProfile applied;
            try
            {
                profiles = _areaProfileStore.EnumerateProfiles();
                var appliedState = _areaProfileStore.LoadAppliedState();
                applied = appliedState.CurrentApplied;
                if (appliedState.Diagnostic is { } diagnostic)
                {
                    _areaProfileStartupError = diagnostic.Message;
                    ShowAreaProfileInfo(
                        InfoBarSeverity.Warning,
                        "无法恢复上次 AREA 配置",
                        diagnostic.Message);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                profiles = [];
                applied = new WatchAppliedAreaFilterProfile(
                    _areaContext.MesAreas.Count == 0 ? null : _areaContext.ProfileName,
                    _areaContext.MesAreas,
                    _areaContext.LastUpdatedAt);
                _areaProfileStartupError ??= exception.Message;
                ShowAreaProfileInfo(
                    InfoBarSeverity.Warning,
                    "无法读取本机 AREA 配置",
                    $"当前已应用显示范围保持不变。{exception.Message}");
            }

            AreaProfileDirectoryText.Text = _areaProfileStore.DirectoryPath;
            AreaProfileListSummaryText.Text =
                $"{profiles.Count:N0} 个本机 TXT 配置 · 选择不等于应用";
            if (_selectedAreaProfileName is null
                && applied.ProfileName is { } appliedProfileName
                && profiles.Any(profile => string.Equals(
                    profile.ProfileName,
                    appliedProfileName,
                    StringComparison.Ordinal)))
            {
                _selectedAreaProfileName = appliedProfileName;
                try
                {
                    _areaProfileDraft = _areaProfileStore.Load(appliedProfileName);
                    _areaProfileDraftIsDirty = false;
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException)
                {
                    _selectedAreaProfileName = null;
                    ShowAreaProfileInfo(
                        InfoBarSeverity.Warning,
                        "无法读取当前应用配置的 TXT",
                        $"已应用 AREA 快照保持不变。{exception.Message}");
                }
            }

            AreaProfileAppliedStateText.Text = applied.AppliedAt is { } appliedAt
                ? $"当前应用：{applied.DisplaySummary} · {WatchTimeDisplay.Format(appliedAt)}"
                : $"当前应用：{applied.DisplaySummary}";
            AutomationProperties.SetName(
                AreaProfileAppliedStateText,
                applied.MesAreas.Count == 0
                    ? AreaProfileAppliedStateText.Text
                    : $"{AreaProfileAppliedStateText.Text}；AREA {string.Join('、', applied.MesAreas)}");
            AreaProfileList.ItemsSource = profiles;
            AreaProfileList.SelectedItem = profiles.FirstOrDefault(profile => string.Equals(
                profile.ProfileName,
                _selectedAreaProfileName,
                StringComparison.Ordinal));

            _areaProfileDraft ??= WatchAreaFilterProfileParser.Parse(
                string.Empty,
                string.Empty);
            if (!string.Equals(AreaProfileNameInput.Text, _areaProfileDraft.ProfileName, StringComparison.Ordinal))
            {
                AreaProfileNameInput.Text = _areaProfileDraft.ProfileName;
            }
            if (!string.Equals(AreaProfileEditor.Text, _areaProfileDraft.Content, StringComparison.Ordinal))
            {
                AreaProfileEditor.Text = _areaProfileDraft.Content;
            }

            AreaProfileValidationGrid.ItemsSource = _areaProfileDraft.Diagnostics;
            AreaProfileValidationSummaryText.Text = _areaProfileDraft.IsValid
                ? $"{_areaProfileDraft.MesAreas.Count:N0} 个合法 AREA · 可保存并应用"
                : $"{_areaProfileDraft.Diagnostics.Count:N0} 项问题 · 非法草稿不可应用";
            AreaProfileDiskStateText.Text = _areaProfileDraftIsDirty
                ? "草稿未保存"
                : _selectedAreaProfileName is null
                    ? "尚未保存"
                    : "已从 TXT 读取";
            AreaProfileSaveButton.IsEnabled = _areaProfileDraft.IsValid;
            AreaProfileApplyButton.IsEnabled = _areaProfileDraft.IsValid;
            AutomationProperties.SetName(
                AreaProfileValidationSummaryText,
                $"AREA 配置校验：{AreaProfileValidationSummaryText.Text}");
            AutomationProperties.SetName(
                AreaProfileDiskStateText,
                $"AREA 配置保存状态：{AreaProfileDiskStateText.Text}");
        }
        finally
        {
            _isRenderingAreaProfiles = false;
        }
    }

    private void OnAreaProfileNewClick(object sender, RoutedEventArgs e)
    {
        _selectedAreaProfileName = null;
        _areaProfileDraftIsDirty = true;
        _areaProfileDraft = WatchAreaFilterProfileParser.Parse("new-area-filter", string.Empty);
        RenderAreaProfiles();
        AreaProfileNameInput.Focus();
        AreaProfileNameInput.SelectAll();
    }

    private void OnAreaProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingAreaProfiles
            || AreaProfileList.SelectedItem is not WatchAreaFilterProfileSummary summary)
        {
            return;
        }

        try
        {
            _selectedAreaProfileName = summary.ProfileName;
            _areaProfileDraft = _areaProfileStore.Load(summary.ProfileName);
            _areaProfileDraftIsDirty = false;
            RenderAreaProfiles();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            ShowAreaProfileInfo(
                InfoBarSeverity.Error,
                "无法读取 AREA TXT 配置",
                exception.Message);
        }
    }

    private void OnAreaProfileDraftChanged(object sender, TextChangedEventArgs e)
    {
        if (_isRenderingAreaProfiles)
        {
            return;
        }

        _areaProfileDraft = WatchAreaFilterProfileParser.Parse(
            AreaProfileNameInput.Text,
            AreaProfileEditor.Text);
        if (_selectedAreaProfileName is not null
            && !string.Equals(
                _selectedAreaProfileName,
                _areaProfileDraft.ProfileName,
                StringComparison.Ordinal))
        {
            _selectedAreaProfileName = null;
        }
        _areaProfileDraftIsDirty = true;
        RenderAreaProfiles();
    }

    private void OnAreaProfileSaveClick(object sender, RoutedEventArgs e)
    {
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(() =>
        {
            var draft = CurrentAreaProfileDraft();
            var result = _areaProfileStore.Save(draft.ProfileName, draft.Content);
            _areaProfileDraft = result.Draft;
            if (!result.Saved)
            {
                throw new InvalidOperationException(ProjectAreaDiagnostics(result.Diagnostics));
            }

            _selectedAreaProfileName = result.Draft.ProfileName;
            _areaProfileDraftIsDirty = false;
            ShowAreaProfileInfo(
                InfoBarSeverity.Success,
                "AREA 配置已保存",
                $"{result.Draft.ProfileName}.txt 已以 UTF-8 原子写入；当前应用范围未静默改变。");
            RenderAreaProfiles();
            return Task.CompletedTask;
        });
    }

    private void OnAreaProfileApplyClick(object sender, RoutedEventArgs e)
    {
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(async () =>
        {
            var draft = CurrentAreaProfileDraft();
            var result = _areaProfileStore.Apply(draft.ProfileName, draft.Content);
            _areaProfileDraft = result.Draft;
            if (!result.Applied)
            {
                throw new InvalidOperationException(ProjectAreaDiagnostics(result.Diagnostics));
            }

            _selectedAreaProfileName = result.Draft.ProfileName;
            _areaProfileDraftIsDirty = false;
            await ApplyAreaContextAsync(
                    result.CurrentApplied.ToDisplayContext(),
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            ShowAreaProfileInfo(
                InfoBarSeverity.Success,
                "AREA 配置已应用",
                "概览、需求系列和资格审计已清除冻结游标并从第一页重新读取；错误检索与接入告警未改变。");
            RenderAreaProfiles();
        });
    }

    private void OnAreaApplyAllAreasClick(object sender, RoutedEventArgs e)
    {
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(async () =>
        {
            var result = _areaProfileStore.ApplyAllAreas();
            await ApplyAreaContextAsync(
                    result.CurrentApplied.ToDisplayContext(),
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            ShowAreaProfileInfo(
                InfoBarSeverity.Success,
                "已应用全部 AREA",
                "本机范围标记已持久化；三个 AREA 相关只读视图已从第一页重新读取。");
            RenderAreaProfiles();
        });
    }

    private WatchAreaFilterProfile CurrentAreaProfileDraft() =>
        _areaProfileDraft = WatchAreaFilterProfileParser.Parse(
            AreaProfileNameInput.Text,
            AreaProfileEditor.Text);

    private async Task RunAreaProfileUiActionAsync(Func<Task> action)
    {
        var operation = Interlocked.Increment(ref _areaProfileOperationGeneration);
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the window is a neutral end to an in-flight action.
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or WatchOverviewException
            or DemandSeriesBrowseException
            or ReadabilityAuditException)
        {
            if (operation == Interlocked.Read(ref _areaProfileOperationGeneration))
            {
                ShowAreaProfileInfo(
                    InfoBarSeverity.Error,
                    "无法完成 AREA 配置操作",
                    exception.Message);
                RenderAreaProfiles();
            }
        }
    }

    private void ShowAreaProfileInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        AreaProfileInfoBar.Severity = severity;
        AreaProfileInfoBar.Title = title;
        AreaProfileInfoBar.Message = message;
        AreaProfileInfoBar.IsOpen = true;
        AutomationProperties.SetName(AreaProfileInfoBar, $"{title}。{message}");
    }

    private static string ProjectAreaDiagnostics(
        IReadOnlyList<WatchAreaFilterProfileDiagnostic> diagnostics) => diagnostics.Count == 0
        ? "AREA 配置未通过校验。"
        : string.Join("；", diagnostics.Select(diagnostic => diagnostic.LineNumber is { } line
            ? $"第 {line} 行 {diagnostic.Code}: {diagnostic.Message}"
            : $"{diagnostic.Code}: {diagnostic.Message}"));
}
