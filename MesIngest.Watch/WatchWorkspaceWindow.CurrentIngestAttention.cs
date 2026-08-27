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

internal partial class WatchWorkspaceWindow
{
    private string? _selectedCurrentAttentionIdentity;
    private string? _currentAttentionSelectionNotice;

    private bool _isRenderingCurrentAttention;

    private long _currentAttentionOperationGeneration;

    internal Task CurrentAttentionNavigationTask { get; private set; } = Task.CompletedTask;

    internal Task CurrentAttentionOperationTask { get; private set; } = Task.CompletedTask;

    private void InitializeCurrentIngestAttentionPage()
    {
        WatchGridClipboardBehavior.Attach(CurrentAttentionKindFacetGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(CurrentAttentionSeverityFacetGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(CurrentAttentionGrid, preserveSelectionUnit: true);
        CurrentAttentionGrid.ClearValue(DataGrid.HeadersVisibilityProperty);
        WatchGridClipboardBehavior.Attach(CurrentAttentionEvidenceGrid, preserveSelectionUnit: true);

        SyncCurrentAttentionFilterControls(_currentAttentionQuery);
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

    private long BeginCurrentAttentionOperation() =>
        Interlocked.Increment(ref _currentAttentionOperationGeneration);

    private bool IsCurrentAttentionOperation(
        long operation,
        CancellationToken cancellationToken) =>
        !_disposed
        && !cancellationToken.IsCancellationRequested
        && operation == Interlocked.Read(ref _currentAttentionOperationGeneration);

    private bool IsLatestCurrentAttentionOperation(long operation) =>
        !_disposed
        && operation == Interlocked.Read(ref _currentAttentionOperationGeneration);

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

    private int ReadCurrentAttentionPageSize() => ReadPageSize(
        CurrentAttentionPageSizeInput,
        CurrentIngestAttentionQuery.MaximumPageSize,
        "接入告警");

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
            CurrentAttentionStatusInfoBar.IsOpen = showEmpty;
            CurrentAttentionStatusInfoBar.Severity = InfoBarSeverity.Informational;
            CurrentAttentionStatusInfoBar.Title = showEmpty ? "当前没有接入告警" : string.Empty;
            CurrentAttentionStatusInfoBar.Message = presentation.EmptyResultMessage;
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
            : ProjectEvidenceFacts(selected);
        CurrentAttentionOpenErrorSearchButton.IsEnabled = selected?.ErrorSearchDrill is not null;
        CurrentAttentionOpenDemandSeriesButton.IsEnabled =
            WatchDemandSeriesNavigationContext.FromCurrentAttention(
                _session.State.CurrentAttention.Snapshot,
                selected) is not null;
    }

    private static IReadOnlyList<WatchEvidenceFactPresentation> ProjectEvidenceFacts(
        WatchCurrentIngestAttentionRowPresentation selected)
    {
        var evidence = selected.Evidence;
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
        Add("ProtectionStatus", selected.Protection?.Status);
        Add("Reason", selected.Protection?.Reason);
        Add("LastSuccessfulWindow", selected.Protection?.LastSuccessfulWindow);
        Add("EarliestAvailableHostUtc", selected.Protection?.EarliestAvailable);
        Add("HistoryEpochProgress", selected.Protection?.RebuildProgress);
        Add("CurrentReadRestriction", selected.Protection?.CurrentReadRestriction);
        Add("LocalAdministration", selected.Protection?.LocalAdministrationGuidance);
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

    private void OnCurrentAttentionOpenDemandSeriesClick(object sender, RoutedEventArgs e)
    {
        CurrentAttentionOperationTask = RunCurrentAttentionUiActionAsync(async () =>
        {
            var navigation = WatchDemandSeriesNavigationContext.FromCurrentAttention(
                    _session.State.CurrentAttention.Snapshot,
                    CurrentAttentionGrid.SelectedItem
                        as WatchCurrentIngestAttentionRowPresentation)
                ?? throw new InvalidOperationException(
                    "当前关注项没有可精确定位的 DemandSeries。" );
            await NavigateToDemandSeriesAsync(navigation, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        });
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
            PresentOperationFailure(
                WatchWorkspacePage.CurrentAttention,
                "attention.operation",
                "无法执行接入告警操作",
                "请检查当前选择或快照后重试。",
                "返回接入告警");
        }
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

    private sealed record WatchEvidenceFactPresentation(string Name, string Value);

}
