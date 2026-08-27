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
        WatchGridClipboardBehavior.Attach(ReadabilityStateFacetGrid, _displayLanguageState, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityBlockerFacetGrid, _displayLanguageState, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityAuditGrid, _displayLanguageState, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityQualificationGrid, _displayLanguageState, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityBlockerEvidenceGrid, _displayLanguageState, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityRawObservationGrid, _displayLanguageState, preserveSelectionUnit: true);

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
        var value = comboBox.SelectedItem is ComboBoxItem item
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
                _areaContext,
                _displayLanguageState.Catalog);
            var text = _displayLanguageState.Catalog.ReadabilityAudit;
            ReadabilityRefreshPolicyText.Text = text.RefreshPolicy(
                _preferences.RefreshIntervals.ReadabilityAudit.IntervalSeconds,
                state.ReadabilityAudit.IsRefreshing);
            ReadabilityAuditInfoBar.IsOpen = false;
            ReadabilityAuditInfoBar.Severity = ToInfoBarSeverity(presentation.InfoSeverity);
            ReadabilityAuditInfoBar.Title = presentation.InfoTitle;
            ReadabilityAuditInfoBar.Message = presentation.InfoMessage;
            AutomationProperties.SetName(
                ReadabilityAuditInfoBar,
                text.ReadStateAutomation(
                    presentation.InfoTitle,
                    presentation.InfoMessage,
                    presentation.IsInfoOpen));
            ReadabilitySnapshotFactsText.Text = presentation.SnapshotFacts;
            ReadabilityClientAttemptText.Text = presentation.ClientAttemptFacts;
            ReadabilityAreaScopeText.Text =
                $"{presentation.LocalAreaHeading} · {presentation.LocalAreaDetail} · {presentation.HostAreaScope}";
            ReadabilityOrderSummaryText.Text =
                $"{presentation.OrderSummary} · {presentation.HostFilterSummary}";
            ReadabilityPageSummaryText.Text = presentation.PageSummary;
            AutomationProperties.SetName(
                ReadabilityPageSummaryText,
                text.PageSummaryAutomation(presentation.PageSummary));
            AutomationProperties.SetName(
                ReadabilityAreaScopeText,
                text.AreaScopeAutomation(ReadabilityAreaScopeText.Text));

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
                ? text.ValueSemantics.Single(item => item.Kind == WatchDisplayValueKind.EmptyResult).Heading
                : string.Empty;
            ReadabilityEmptyInfoBar.Message = presentation.EmptyResultMessage;
            AutomationProperties.SetName(
                ReadabilityEmptyInfoBar,
                presentation.EmptyResultMessage.Length > 0
                    ? text.ReadStateAutomation(
                        ReadabilityEmptyInfoBar.Title,
                        presentation.EmptyResultMessage,
                        isOpen: true)
                    : text.NonEmptyOrUnread);
            ReadabilityPreviousPageButton.IsEnabled = presentation.CanGoPrevious
                && !presentation.IsRefreshing;
            ReadabilityNextPageButton.IsEnabled = presentation.CanGoNext
                && !presentation.IsRefreshing;
            ReadabilityGoToPageButton.IsEnabled = presentation.HasSnapshot
                && !presentation.IsRefreshing;
            ReadabilityPageNumberInput.Text = state.ReadabilityAudit.Snapshot?.PageNumber
                .ToString() ?? "1";

            var detail = presentation.Detail;
            ReadabilityDetailHeadingText.Text = detail?.Heading ?? text.NotSelected;
            var detailEvidence = detail is null
                ? presentation.SelectionNotice
                    ?? text.SelectDemand
                : text.DetailEvidence(detail.Facts, detail.AllBlockersSummary);
            ReadabilityDetailFactsText.Text = detail?.BusinessIdentity ?? detailEvidence;
            ReadabilityDetailFactsText.ToolTip = detailEvidence;
            ReadabilitySeriesFactsText.Text = detail?.SeriesFacts ?? text.NotSelected;
            ReadabilityLiveMesFactsText.Text = detail is null
                ? text.SelectDemand
                : detail.LiveMesFields is null
                    ? $"{detail.LiveMesFacts} {detail.ObservationSummary}"
                    : detail.ObservationSummary;
            var liveMesFields = detail?.LiveMesFields;
            var absentLiveValue = detail is null
                ? _displayLanguageState.Catalog.Common.NotLoaded
                : _displayLanguageState.Catalog.Common.SystemUnknown;
            ReadabilityLiveMesAreaText.Text = liveMesFields?.Area ?? absentLiveValue;
            ReadabilityLiveMesEqpText.Text = liveMesFields?.Eqp ?? absentLiveValue;
            ReadabilityLiveMesStepText.Text = liveMesFields?.Step ?? absentLiveValue;
            ReadabilityLiveMesDateText.Text = liveMesFields?.MesSourceDate ?? absentLiveValue;
            ReadabilityLiveMesPackageText.Text = liveMesFields?.Package ?? absentLiveValue;
            var liveMesEvidence = detail is null
                ? ReadabilityLiveMesFactsText.Text
                : $"{detail.LiveMesFacts} · {detail.ObservationSummary} · {detail.PollTraceFacts}";
            ReadabilityLiveMesFieldsGrid.ToolTip = liveMesEvidence;
            AutomationProperties.SetName(
                ReadabilityLiveMesFieldsGrid,
                text.LiveMesFieldsAutomation(
                    ReadabilityLiveMesAreaText.Text,
                    ReadabilityLiveMesEqpText.Text,
                    ReadabilityLiveMesStepText.Text,
                    ReadabilityLiveMesDateText.Text,
                    ReadabilityLiveMesPackageText.Text));
            AutomationProperties.SetHelpText(ReadabilityLiveMesFieldsGrid, liveMesEvidence);
            AutomationProperties.SetName(
                ReadabilityLiveMesAreaText,
                text.FieldAutomation("AREA", ReadabilityLiveMesAreaText.Text));
            AutomationProperties.SetName(
                ReadabilityLiveMesEqpText,
                text.FieldAutomation("EQP", ReadabilityLiveMesEqpText.Text));
            AutomationProperties.SetName(
                ReadabilityLiveMesStepText,
                text.FieldAutomation("STEP", ReadabilityLiveMesStepText.Text));
            AutomationProperties.SetName(
                ReadabilityLiveMesDateText,
                text.FieldAutomation("DATES", ReadabilityLiveMesDateText.Text));
            AutomationProperties.SetName(
                ReadabilityLiveMesPackageText,
                text.FieldAutomation("PACKAGE", ReadabilityLiveMesPackageText.Text));
            AutomationProperties.SetName(
                ReadabilityDetailFactsText,
                text.BusinessIdentityAutomation(ReadabilityDetailFactsText.Text));
            AutomationProperties.SetHelpText(ReadabilityDetailFactsText, detailEvidence);
            AutomationProperties.SetName(
                ReadabilitySeriesFactsText,
                text.SeriesFactsAutomation(ReadabilitySeriesFactsText.Text));
            AutomationProperties.SetName(
                ReadabilityLiveMesFactsText,
                text.ObservationAutomation(ReadabilityLiveMesFactsText.Text));
            AutomationProperties.SetHelpText(ReadabilityLiveMesFactsText, liveMesEvidence);
            var primaryBlocker = detail?.PrimaryBlockerEvidence;
            var readabilitySemanticState = detail?.SemanticState ?? "Neutral";
            ReadabilityPrimaryBlockerCard.Tag = readabilitySemanticState;
            ReadabilityPrimaryBlockerCodeText.Text = detail is null
                ? text.NoBlocker
                : detail.LeadReadabilityBlocker is { Length: > 0 } blockerCode
                    ? $"{text.DescribeBlocker(blockerCode).Description} · {blockerCode}"
                    : text.NoBlocker;
            ReadabilityPrimaryBlockerEvidenceText.Text = primaryBlocker is null
                ? detail is null
                    ? text.SelectPrimaryEvidence
                    : string.IsNullOrWhiteSpace(detail.LeadReadabilityBlocker)
                        ? text.NoBlockerEvidence
                        : text.MissingStructuredEvidence
                : text.ObservedValue(
                    primaryBlocker.SubjectKind,
                    primaryBlocker.ObservedValue,
                    primaryBlocker.ObservedAt);
            ReadabilityPrimaryBlockerRuleText.Text = primaryBlocker is null
                ? string.Empty
                : text.Rule(primaryBlocker.ExpectedRule);
            AutomationProperties.SetName(
                ReadabilityPrimaryBlockerCard,
                text.PrimaryBlockerAutomation(
                    ReadabilityPrimaryBlockerCodeText.Text,
                    ReadabilityPrimaryBlockerEvidenceText.Text,
                    ReadabilityPrimaryBlockerRuleText.Text));
            ReadabilityQualificationChecklist.ItemsSource = detail?.QualificationChecks;
            ReadabilityQualificationConclusionCard.Tag = readabilitySemanticState;
            ReadabilityQualificationConclusionText.Text = detail is null
                ? text.NotSelected
                : text.QualificationConclusion(detail.ExternalReadabilityState);
            ReadabilityValueSemanticsItems.ItemsSource = text.ValueSemantics;
            var revisionEvidence = detail is null
                ? text.RevisionPrompt
                : $"Catalog Revision {detail.CatalogRevision:N0} · Snapshot {detail.SnapshotReference} · Projection {detail.ProjectionSequence:N0} · {detail.ProjectionCommitId} · PollTrace {detail.PollTraceId}";
            ReadabilityRevisionFactsText.Text = detail is null
                ? text.RevisionNotLoaded
                : $"Catalog Revision {detail.CatalogRevision:N0}";
            ReadabilityRevisionFactsText.ToolTip = revisionEvidence;
            AutomationProperties.SetName(
                ReadabilityRevisionFactsText,
                text.RevisionAutomation(ReadabilityRevisionFactsText.Text));
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
            ReadabilityDetailInfoBar.Title = text.DetailStatusTitle(
                presentation.SelectionNotice is not null,
                selectedDemandId is not null,
                detailReadFailed);
            ReadabilityDetailInfoBar.Message = presentation.SelectionNotice
                ?? (selectedDemandId is null
                    ? text.SelectDemand
                    : text.DetailStatusMessage(selectedDemandId, detailReadFailed));
            AutomationProperties.SetName(
                ReadabilityDetailInfoBar,
                text.ReadStateAutomation(
                    ReadabilityDetailInfoBar.Title,
                    ReadabilityDetailInfoBar.Message,
                    isOpen: true));
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
            PresentOperationFailure(
                WatchWorkspacePage.ReadabilityAudit,
                "readability.operation",
                "无法执行资格审计操作",
                "请检查输入或当前快照后重试。",
                "返回资格审计");
        }
    }
}
