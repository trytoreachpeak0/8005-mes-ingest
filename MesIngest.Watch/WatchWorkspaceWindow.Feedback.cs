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
            "原需求系列已不在刷新结果中");
        PublishSelectionFeedback(
            WatchWorkspacePage.ReadabilityAudit,
            state.ReadabilityAudit.SelectionNotice,
            $"{state.HostGeneration}:{state.ReadabilityAudit.SelectionGeneration}",
            "原 Demand 已不在刷新结果中");
        PublishSelectionFeedback(
            WatchWorkspacePage.ErrorSearch,
            state.ErrorSearch.SelectionNotice,
            $"{state.HostGeneration}:{state.ErrorSearch.SelectionGeneration}",
            "原错误项已不在刷新结果中");
        PublishSelectionFeedback(
            WatchWorkspacePage.CurrentAttention,
            _currentAttentionSelectionNotice,
            _currentAttentionSelectionNotice ?? string.Empty,
            "原关注项已不在刷新结果中");
    }

    private void PublishSelectionFeedback(
        WatchWorkspacePage page,
        string? notice,
        string token,
        string title)
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
        PresentNotification(new WatchNotificationEvent(
            new WatchNotificationSource(
                "refresh.selection-cleared",
                WatchNotificationScope.ForPage(page),
                page.ToString()),
            WatchNotificationSeverity.Information,
            "信息",
            title,
            "刷新已清除原选择；详情保持未选择，请重新选择一项。"));
    }

    private IReadOnlyList<WatchContinuingFault> ProjectContinuingFaults(
        WatchV2WorkspaceState state)
    {
        var faults = new List<WatchContinuingFault>();
        if (state.ConnectionStatus == WatchHostConnectionStatus.Failed)
        {
            faults.Add(new WatchContinuingFault(
                "host.connection",
                WatchNotificationScope.Global,
                WatchNotificationSeverity.Error,
                "Host 连接持续失败",
                ControlledHostFailureMessage(state.FailureKind),
                "打开设置"));
        }

        AddViewFault(faults, "overview.refresh", WatchWorkspacePage.Overview,
            "概览读取持续失败", state.Overview.LastFailureAt, state.Overview.FailureKind);
        AddViewFault(faults, "overview.protection", WatchWorkspacePage.Overview,
            "保护状态读取持续失败", state.Protection.LastFailureAt, state.Protection.FailureKind);
        AddViewFault(faults, "demand-series.refresh", WatchWorkspacePage.DemandSeries,
            "DemandSeries 读取持续失败", state.DemandSeries.LastFailureAt, state.DemandSeries.FailureKind);
        AddViewFault(faults, "demand-series.detail", WatchWorkspacePage.DemandSeries,
            "DemandSeries 详情读取持续失败", state.DemandSeries.DetailLastFailureAt, state.DemandSeries.DetailFailureKind);
        AddViewFault(faults, "readability.refresh", WatchWorkspacePage.ReadabilityAudit,
            "资格审计读取持续失败", state.ReadabilityAudit.LastFailureAt, state.ReadabilityAudit.FailureKind);
        AddViewFault(faults, "readability.detail", WatchWorkspacePage.ReadabilityAudit,
            "资格审计详情读取持续失败", state.ReadabilityAudit.DetailLastFailureAt, state.ReadabilityAudit.DetailFailureKind);
        AddViewFault(faults, "error-search.refresh", WatchWorkspacePage.ErrorSearch,
            "错误检索读取持续失败", state.ErrorSearch.LastFailureAt, state.ErrorSearch.FailureKind);
        AddViewFault(faults, "error-search.detail", WatchWorkspacePage.ErrorSearch,
            "错误详情读取持续失败", state.ErrorSearch.DetailLastFailureAt, state.ErrorSearch.DetailFailureKind);
        AddViewFault(faults, "attention.refresh", WatchWorkspacePage.CurrentAttention,
            "接入告警读取持续失败", state.CurrentAttention.LastFailureAt, state.CurrentAttention.FailureKind);
        return faults;
    }

    private static void AddViewFault(
        ICollection<WatchContinuingFault> faults,
        string sourceKey,
        WatchWorkspacePage page,
        string title,
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
            failureKind,
            title,
            "查看页面"));
    }

    private static WatchContinuingFault CreateFault(
        string sourceKey,
        WatchNotificationScope scope,
        WatchHostFailureKind failureKind,
        string title,
        string actionLabel) => new(
            sourceKey,
            scope,
            WatchNotificationSeverity.Error,
            title,
            ControlledFailureMessage(failureKind),
            actionLabel);

    private static string ControlledFailureMessage(WatchHostFailureKind failureKind) =>
        failureKind switch
        {
            WatchHostFailureKind.Authentication =>
                "Host 拒绝了当前凭据；请在设置中更新凭据后重试。",
            WatchHostFailureKind.Contract or WatchHostFailureKind.Decode =>
                "Host 返回内容与当前 Watch 不兼容；请核对 Host 版本。",
            WatchHostFailureKind.Timeout =>
                "请求超时；页面保持当前稳定状态，自动刷新仍会继续。",
            WatchHostFailureKind.Network or WatchHostFailureKind.Http =>
                "Host 暂时不可达；页面保持当前稳定状态，自动刷新仍会继续。",
            WatchHostFailureKind.ServerQuery =>
                "Host 查询失败；页面保持当前稳定状态，自动刷新仍会继续。",
            _ => "读取暂时失败；页面保持当前稳定状态，自动刷新仍会继续。",
        };

    private static string ControlledHostFailureMessage(WatchHostFailureKind failureKind) =>
        failureKind switch
        {
            WatchHostFailureKind.Authentication =>
                "Host 拒绝了当前凭据；请在设置中更新凭据后重试。",
            WatchHostFailureKind.Contract or WatchHostFailureKind.Decode =>
                "Host 返回内容与当前 Watch 不兼容；请核对 Host 版本。",
            WatchHostFailureKind.Timeout =>
                "Host 连接验证超时；请检查连接设置后重试。",
            WatchHostFailureKind.Network or WatchHostFailureKind.Http =>
                "Host 暂时不可达；请检查连接设置后重试。",
            _ => "Host 连接验证失败；请检查连接设置后重试。",
        };

    private void ApplyFeedbackLifecycleChange(WatchFeedbackLifecycleChange change)
    {
        foreach (var fault in change.Started)
        {
            PresentFaultNotification(fault);
        }

        foreach (var fault in change.Recovered)
        {
            _notificationCoordinator.Dismiss(FaultNotificationSource(fault).Key);
            PresentNotification(new WatchNotificationEvent(
                new WatchNotificationSource(
                    "continuing-fault.recovered",
                    fault.Scope,
                    fault.SourceKey),
                WatchNotificationSeverity.Success,
                "恢复",
                "读取已恢复",
                $"{fault.Title}已恢复；稳定故障状态已清除。"));
        }

        if (change.ForegroundSummary.Count > 0)
        {
            var mostSevere = change.ForegroundSummary[0];
            PresentNotification(new WatchNotificationEvent(
                new WatchNotificationSource(
                    "continuing-fault.background-summary",
                    WatchNotificationScope.Global,
                    "active-new-faults"),
                mostSevere.Severity,
                "错误",
                "后台期间出现新的持续故障",
                change.ForegroundSummary.Count == 1
                    ? mostSevere.Title
                    : $"仍有 {change.ForegroundSummary.Count} 个新故障活动；请查看页面标题状态。",
                "查看故障",
                () => NavigateToFault(mostSevere)));
        }

        RenderFaultHeaders(change.Active);
    }

    private void PresentFaultNotification(WatchContinuingFault fault)
    {
        var source = FaultNotificationSource(fault);
        PresentNotification(new WatchNotificationEvent(
            source,
            fault.Severity,
            "错误",
            fault.Title,
            fault.Message,
            fault.ActionLabel,
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
        text.Text = $"错误 · {faults.Length} 个故障";
        var detail = string.Join("；", faults.Select(item => item.Title));
        button.Tag = page;
        button.ToolTip = detail;
        AutomationProperties.SetName(button, $"{text.Text}。{detail}。打开故障详情");
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
        PresentNotification(new WatchNotificationEvent(
            new WatchNotificationSource(
                "continuing-fault.details",
                WatchNotificationScope.ForPage(page),
                page.ToString()),
            fault.Severity,
            "错误",
            fault.Title,
            faults.Length == 1
                ? fault.Message
                : $"当前共有 {faults.Length} 个持续故障。{fault.Message}"));
    }

    private void PresentOperationFailure(
        WatchWorkspacePage page,
        string sourceIdentity,
        string title,
        string controlledMessage,
        string actionLabel)
    {
        PresentNotification(new WatchNotificationEvent(
            new WatchNotificationSource(
                "operation.failed",
                WatchNotificationScope.ForPage(page),
                sourceIdentity),
            WatchNotificationSeverity.Error,
            "错误",
            title,
            controlledMessage,
            actionLabel,
            () => NavigateTo(page)));
    }
}
