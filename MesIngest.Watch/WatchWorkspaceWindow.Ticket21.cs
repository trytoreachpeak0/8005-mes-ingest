using System.Windows.Automation;
using System.Windows.Controls;
using MesIngest.Core.SeriesProjection;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace MesIngest.Watch;

internal partial class WatchWorkspaceWindow
{
    private bool _isRenderingReadabilityAudit;
    private long _readabilityAuditOperationGeneration;

    internal Task ReadabilityAuditNavigationTask { get; private set; } = Task.CompletedTask;

    private void InitializeReadabilityAuditPage()
    {
        WatchGridClipboardBehavior.Attach(ReadabilityStateFacetGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityBlockerFacetGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityAuditGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityQualificationGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityBlockerEvidenceGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityRawObservationGrid, preserveSelectionUnit: true);

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
        var readabilityStates = ReadReadabilityStateDraft();
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
            SetReadabilityStateDraft(query.Filter.ReadabilityStates);
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
            var detailEvidence = detail is null
                ? presentation.SelectionNotice
                    ?? "详情必须与当前列表使用同一个冻结 snapshotReference。"
                : $"{detail.Facts} · 全部阻断 {detail.AllBlockersSummary}";
            ReadabilityDetailFactsText.Text = detail?.BusinessIdentity ?? detailEvidence;
            ReadabilityDetailFactsText.ToolTip = detailEvidence;
            ReadabilitySeriesFactsText.Text = detail?.SeriesFacts ?? "尚未选择 Series。";
            ReadabilityLiveMesFactsText.Text = detail is null
                ? "选择后显示可信 LiveMesFieldSet，或保留全部原始观测冲突证据。"
                : detail.LiveMesFields is null
                    ? $"{detail.LiveMesFacts} {detail.ObservationSummary}"
                    : detail.ObservationSummary;
            var liveMesFields = detail?.LiveMesFields;
            ReadabilityLiveMesAreaText.Text = liveMesFields?.Area ?? "—";
            ReadabilityLiveMesEqpText.Text = liveMesFields?.Eqp ?? "—";
            ReadabilityLiveMesStepText.Text = liveMesFields?.Step ?? "—";
            ReadabilityLiveMesDateText.Text = liveMesFields?.MesSourceDate ?? "—";
            ReadabilityLiveMesPackageText.Text = liveMesFields?.Package ?? "—";
            var liveMesEvidence = detail is null
                ? ReadabilityLiveMesFactsText.Text
                : $"{detail.LiveMesFacts} · {detail.ObservationSummary} · {detail.PollTraceFacts}";
            ReadabilityLiveMesFieldsGrid.ToolTip = liveMesEvidence;
            AutomationProperties.SetName(
                ReadabilityLiveMesFieldsGrid,
                $"资格审计最后可信 MES 字段：AREA {ReadabilityLiveMesAreaText.Text}；EQP {ReadabilityLiveMesEqpText.Text}；STEP {ReadabilityLiveMesStepText.Text}；DATES {ReadabilityLiveMesDateText.Text}；PACKAGE {ReadabilityLiveMesPackageText.Text}");
            AutomationProperties.SetHelpText(ReadabilityLiveMesFieldsGrid, liveMesEvidence);
            AutomationProperties.SetName(
                ReadabilityLiveMesAreaText,
                $"资格审计 MES AREA {ReadabilityLiveMesAreaText.Text}");
            AutomationProperties.SetName(
                ReadabilityLiveMesEqpText,
                $"资格审计 MES EQP {ReadabilityLiveMesEqpText.Text}");
            AutomationProperties.SetName(
                ReadabilityLiveMesStepText,
                $"资格审计 MES STEP {ReadabilityLiveMesStepText.Text}");
            AutomationProperties.SetName(
                ReadabilityLiveMesDateText,
                $"资格审计 MES DATES {ReadabilityLiveMesDateText.Text}");
            AutomationProperties.SetName(
                ReadabilityLiveMesPackageText,
                $"资格审计 MES PACKAGE {ReadabilityLiveMesPackageText.Text}");
            AutomationProperties.SetName(
                ReadabilityDetailFactsText,
                $"资格审计业务身份：{ReadabilityDetailFactsText.Text}");
            AutomationProperties.SetHelpText(ReadabilityDetailFactsText, detailEvidence);
            AutomationProperties.SetName(
                ReadabilitySeriesFactsText,
                $"资格审计所属 Series 事实：{ReadabilitySeriesFactsText.Text}");
            AutomationProperties.SetName(
                ReadabilityLiveMesFactsText,
                $"资格审计 MES 观测说明：{ReadabilityLiveMesFactsText.Text}");
            AutomationProperties.SetHelpText(ReadabilityLiveMesFactsText, liveMesEvidence);
            var primaryBlocker = detail?.PrimaryBlockerEvidence;
            var readabilitySemanticState = detail?.SemanticState ?? "Neutral";
            ReadabilityPrimaryBlockerCard.Tag = readabilitySemanticState;
            ReadabilityPrimaryBlockerCodeText.Text = detail is null
                ? "尚无阻断"
                : detail.LeadReadabilityBlocker ?? "当前无阻断";
            ReadabilityPrimaryBlockerEvidenceText.Text = primaryBlocker is null
                ? detail is null
                    ? "选择后显示首要阻断证据。"
                    : string.IsNullOrWhiteSpace(detail.LeadReadabilityBlocker)
                        ? "当前资格检查未返回阻断证据。"
                        : "Host 未返回首要阻断的结构化证据。"
                : $"{primaryBlocker.SubjectKind} · 观测值 {primaryBlocker.ObservedValue} · {primaryBlocker.ObservedAt}";
            ReadabilityPrimaryBlockerRuleText.Text = primaryBlocker is null
                ? string.Empty
                : $"规则：{primaryBlocker.ExpectedRule}";
            AutomationProperties.SetName(
                ReadabilityPrimaryBlockerCard,
                $"首要阻断：{ReadabilityPrimaryBlockerCodeText.Text}；{ReadabilityPrimaryBlockerEvidenceText.Text}；{ReadabilityPrimaryBlockerRuleText.Text}");
            ReadabilityQualificationChecklist.ItemsSource = detail?.QualificationChecks;
            ReadabilityQualificationConclusionCard.Tag = readabilitySemanticState;
            ReadabilityQualificationConclusionText.Text = detail is null
                ? "结论：尚未选择 Demand"
                : $"结论：{detail.ExternalReadabilityState}";
            var revisionEvidence = detail is null
                ? "选择 Demand 后显示完整 Catalog 修订与冻结快照链。"
                : $"Catalog Revision {detail.CatalogRevision:N0} · Snapshot {detail.SnapshotReference} · Projection {detail.ProjectionSequence:N0} · {detail.ProjectionCommitId} · PollTrace {detail.PollTraceId}";
            ReadabilityRevisionFactsText.Text = detail is null
                ? "Catalog Revision —"
                : $"Catalog Revision {detail.CatalogRevision:N0}";
            ReadabilityRevisionFactsText.ToolTip = revisionEvidence;
            AutomationProperties.SetName(
                ReadabilityRevisionFactsText,
                $"资格审计修订目录事实：{ReadabilityRevisionFactsText.Text}");
            AutomationProperties.SetHelpText(ReadabilityRevisionFactsText, revisionEvidence);
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
            RenderSelectedReadabilityAudit(presentation, state);
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
}
