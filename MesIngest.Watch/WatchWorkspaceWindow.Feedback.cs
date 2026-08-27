using System.Windows.Automation;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace MesIngest.Watch;

internal partial class WatchWorkspaceWindow
{
    private readonly WatchFeedbackLifecycle _feedbackLifecycle = new();
    private readonly Dictionary<WatchWorkspacePage, string> _selectionFeedbackTokens = [];
    private long? _feedbackHostGeneration;

    private void InitializeFeedbackLifecycle()
    {
        Activated += OnWindowActivatedForFeedback;
        Deactivated += OnWindowDeactivatedForFeedback;
        StateChanged += OnWindowStateChangedForFeedback;
    }

    private void DisposeFeedbackLifecycle()
    {
        Activated -= OnWindowActivatedForFeedback;
        Deactivated -= OnWindowDeactivatedForFeedback;
        StateChanged -= OnWindowStateChangedForFeedback;
    }

    private void OnWindowActivatedForFeedback(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            ApplyFeedbackLifecycleChange(_feedbackLifecycle.SetForeground(true));
        }
    }

    private void OnWindowDeactivatedForFeedback(object? sender, EventArgs e) =>
        ApplyFeedbackLifecycleChange(_feedbackLifecycle.SetForeground(false));

    private void OnWindowStateChangedForFeedback(object? sender, EventArgs e) =>
        ApplyFeedbackLifecycleChange(
            _feedbackLifecycle.SetForeground(WindowState != WindowState.Minimized && IsActive));

    private void SynchronizeContinuingFeedback(WatchV2WorkspaceState state)
    {
        var faults = ProjectContinuingFaults(state);
        if (_feedbackHostGeneration != state.HostGeneration)
        {
            _feedbackHostGeneration = state.HostGeneration;
            _selectionFeedbackTokens.Clear();
            ApplyFeedbackLifecycleChange(_feedbackLifecycle.Reset([]));
            ApplyFeedbackLifecycleChange(_feedbackLifecycle.Update(faults));
            return;
        }

        ApplyFeedbackLifecycleChange(
            _feedbackLifecycle.Update(faults));
    }

    private void PublishSelectionFeedback(WatchV2WorkspaceState state)
    {
        PublishSelectionFeedback(
            WatchWorkspacePage.DemandSeries,
            state.DemandSeries.SelectionNotice,
            $"{state.HostGeneration}:{state.DemandSeries.SelectionGeneration}",
            WatchFeedbackText.Localized("feedback.selection.demand-series"));
        PublishSelectionFeedback(
            WatchWorkspacePage.ReadabilityAudit,
            state.ReadabilityAudit.SelectionNotice,
            $"{state.HostGeneration}:{state.ReadabilityAudit.SelectionGeneration}",
            WatchFeedbackText.Localized("feedback.selection.transport-demand"));
        PublishSelectionFeedback(
            WatchWorkspacePage.ErrorSearch,
            state.ErrorSearch.SelectionNotice,
            $"{state.HostGeneration}:{state.ErrorSearch.SelectionGeneration}",
            WatchFeedbackText.Localized("feedback.selection.error"));
        PublishSelectionFeedback(
            WatchWorkspacePage.CurrentAttention,
            _currentAttentionSelectionNotice,
            _currentAttentionSelectionNotice ?? string.Empty,
            WatchFeedbackText.Localized("feedback.selection.attention"));
    }

    private void PublishSelectionFeedback(
        WatchWorkspacePage page,
        string? notice,
        string token,
        WatchLocalizedText title)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        if (_selectionFeedbackTokens.TryGetValue(page, out var prior)
            && string.Equals(prior, token, StringComparison.Ordinal))
        {
            return;
        }

        _selectionFeedbackTokens[page] = token;
        var localized = new WatchLocalizedNotificationContent(
            WatchFeedbackText.Localized("feedback.severity.information"),
            title,
            WatchFeedbackText.Localized("feedback.selection-cleared"));
        PresentNotification(WatchNotificationEvent.CreateLocalized(
            new WatchNotificationSource(
                "refresh.selection-cleared",
                WatchNotificationScope.ForPage(page),
                page.ToString()),
            WatchNotificationSeverity.Information,
            localized));
    }

    private IReadOnlyList<WatchContinuingFault> ProjectContinuingFaults(
        WatchV2WorkspaceState state)
    {
        var faults = new List<WatchContinuingFault>();
        if (state.ConnectionStatus == WatchHostConnectionStatus.Failed)
        {
            faults.Add(CreateFault(
                "host.connection",
                WatchNotificationScope.Global,
                state.FailureKind));
        }

        AddViewFault(faults, "overview.refresh", WatchWorkspacePage.Overview,
            state.Overview.LastFailureAt, state.Overview.FailureKind);
        AddViewFault(faults, "overview.protection", WatchWorkspacePage.Overview,
            state.Protection.LastFailureAt, state.Protection.FailureKind);
        AddViewFault(faults, "demand-series.refresh", WatchWorkspacePage.DemandSeries,
            state.DemandSeries.LastFailureAt, state.DemandSeries.FailureKind);
        AddViewFault(faults, "demand-series.detail", WatchWorkspacePage.DemandSeries,
            state.DemandSeries.DetailLastFailureAt, state.DemandSeries.DetailFailureKind);
        AddViewFault(faults, "readability.refresh", WatchWorkspacePage.ReadabilityAudit,
            state.ReadabilityAudit.LastFailureAt, state.ReadabilityAudit.FailureKind);
        AddViewFault(faults, "readability.detail", WatchWorkspacePage.ReadabilityAudit,
            state.ReadabilityAudit.DetailLastFailureAt, state.ReadabilityAudit.DetailFailureKind);
        AddViewFault(faults, "error-search.refresh", WatchWorkspacePage.ErrorSearch,
            state.ErrorSearch.LastFailureAt, state.ErrorSearch.FailureKind);
        AddViewFault(faults, "error-search.detail", WatchWorkspacePage.ErrorSearch,
            state.ErrorSearch.DetailLastFailureAt, state.ErrorSearch.DetailFailureKind);
        AddViewFault(faults, "attention.refresh", WatchWorkspacePage.CurrentAttention,
            state.CurrentAttention.LastFailureAt, state.CurrentAttention.FailureKind);
        return faults;
    }

    private static void AddViewFault(
        ICollection<WatchContinuingFault> faults,
        string sourceKey,
        WatchWorkspacePage page,
        DateTimeOffset? failedAt,
        WatchHostFailureKind failureKind)
    {
        if (failedAt is null || failureKind == WatchHostFailureKind.Canceled)
        {
            return;
        }

        faults.Add(CreateFault(
            sourceKey,
            WatchNotificationScope.ForPage(page),
            failureKind));
    }

    private static WatchContinuingFault CreateFault(
        string sourceKey,
        WatchNotificationScope scope,
        WatchHostFailureKind failureKind)
    {
        var content = WatchFeedbackText.ContinuingFault(sourceKey, failureKind);
        return new WatchContinuingFault(
            sourceKey,
            scope,
            WatchNotificationSeverity.Error,
            content.Title.SimplifiedChinese,
            content.Message.SimplifiedChinese,
            content.ActionLabel!.SimplifiedChinese,
            content);
    }

    private void ApplyFeedbackLifecycleChange(WatchFeedbackLifecycleChange change)
    {
        foreach (var fault in change.Started)
        {
            PresentFaultNotification(fault);
        }

        foreach (var fault in change.Recovered)
        {
            _notificationCoordinator.Dismiss(FaultNotificationSource(fault).Key);
            var localizedTitle = fault.LocalizedContent?.Title
                ?? new WatchLocalizedText(fault.Title, fault.Title);
            var localized = WatchFeedbackText.Recovered(localizedTitle);
            PresentNotification(WatchNotificationEvent.CreateLocalized(
                new WatchNotificationSource(
                    "continuing-fault.recovered",
                    fault.Scope,
                    fault.SourceKey),
                WatchNotificationSeverity.Success,
                localized));
        }

        if (change.ForegroundSummary.Count > 0)
        {
            var mostSevere = change.ForegroundSummary[0];
            var mostSevereTitle = mostSevere.LocalizedContent?.Title
                ?? new WatchLocalizedText(mostSevere.Title, mostSevere.Title);
            var localized = WatchFeedbackText.BackgroundFaultSummary(
                change.ForegroundSummary.Count,
                mostSevereTitle);
            PresentNotification(WatchNotificationEvent.CreateLocalized(
                new WatchNotificationSource(
                    "continuing-fault.background-summary",
                    WatchNotificationScope.Global,
                    "active-new-faults"),
                mostSevere.Severity,
                localized,
                () => NavigateToFault(mostSevere)));
        }

        RenderFaultHeaders(change.Active);
    }

    private void PresentFaultNotification(WatchContinuingFault fault)
    {
        var source = FaultNotificationSource(fault);
        var localized = fault.LocalizedContent
            ?? throw new InvalidOperationException(
                "Production continuing faults require bilingual localized content.");
        PresentNotification(WatchNotificationEvent.CreateLocalized(
            source,
            fault.Severity,
            localized,
            () => NavigateToFault(fault)));
    }

    private static WatchNotificationSource FaultNotificationSource(
        WatchContinuingFault fault) => new(
            "continuing-fault.started",
            fault.Scope,
            fault.SourceKey);

    private void NavigateToFault(WatchContinuingFault fault) =>
        NavigateTo(fault.Scope.Page ?? WatchWorkspacePage.Settings);

    private void RenderFaultHeaders(IReadOnlyList<WatchContinuingFault> active)
    {
        RenderFaultHeader(OverviewFaultStatusButton, OverviewFaultStatusPill,
            OverviewFaultStatusText, WatchWorkspacePage.Overview, active);
        RenderFaultHeader(DemandSeriesFaultStatusButton, DemandSeriesFaultStatusPill,
            DemandSeriesFaultStatusText, WatchWorkspacePage.DemandSeries, active);
        RenderFaultHeader(ReadabilityFaultStatusButton, ReadabilityFaultStatusPill,
            ReadabilityFaultStatusText, WatchWorkspacePage.ReadabilityAudit, active);
        RenderFaultHeader(ErrorSearchFaultStatusButton, ErrorSearchFaultStatusPill,
            ErrorSearchFaultStatusText, WatchWorkspacePage.ErrorSearch, active);
        RenderFaultHeader(CurrentAttentionFaultStatusButton, CurrentAttentionFaultStatusPill,
            CurrentAttentionFaultStatusText, WatchWorkspacePage.CurrentAttention, active);
    }

    private void RenderFaultHeader(
        Wpf.Ui.Controls.Button button,
        Border pill,
        Wpf.Ui.Controls.TextBlock text,
        WatchWorkspacePage page,
        IReadOnlyList<WatchContinuingFault> active)
    {
        var faults = active.Where(item => item.Scope.Page == page).ToArray();
        button.Visibility = faults.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (faults.Length == 0)
        {
            return;
        }

        pill.Style = (Style)FindResource("StatusPillCritical");
        var header = WatchFeedbackText.FaultHeader(
            _displayLanguageState.Current,
            faults);
        text.Text = header.Text;
        var detail = header.Detail;
        button.Tag = page;
        button.ToolTip = detail;
        AutomationProperties.SetName(button, header.Automation);
        AutomationProperties.SetHelpText(button, detail);
    }

    private void OnFaultStatusClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WatchWorkspacePage page })
        {
            return;
        }

        var faults = _feedbackLifecycle.Active.Where(item => item.Scope.Page == page).ToArray();
        if (faults.Length == 0)
        {
            return;
        }

        var fault = faults[0];
        var localizedFault = fault.LocalizedContent
            ?? throw new InvalidOperationException(
                "Production continuing faults require bilingual localized content.");
        var localized = WatchFeedbackText.FaultDetails(faults.Length, localizedFault);
        PresentNotification(WatchNotificationEvent.CreateLocalized(
            new WatchNotificationSource(
                "continuing-fault.details",
                WatchNotificationScope.ForPage(page),
                page.ToString()),
            fault.Severity,
            localized));
    }

    private void PresentOperationFailure(
        WatchWorkspacePage page,
        string sourceIdentity,
        WatchLocalizedText title,
        WatchLocalizedText controlledMessage,
        WatchLocalizedText actionLabel)
    {
        var localized = new WatchLocalizedNotificationContent(
            WatchFeedbackText.Localized("feedback.severity.error"),
            title,
            controlledMessage,
            actionLabel);
        PresentNotification(WatchNotificationEvent.CreateLocalized(
            new WatchNotificationSource(
                "operation.failed",
                WatchNotificationScope.ForPage(page),
                sourceIdentity),
            WatchNotificationSeverity.Error,
            localized,
            () => NavigateTo(page)));
    }
}
