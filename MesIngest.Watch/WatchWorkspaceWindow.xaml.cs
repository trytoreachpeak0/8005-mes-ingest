using System.IO;
using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MesIngest.Core.SeriesProjection;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using TitleBar = Wpf.Ui.Controls.TitleBar;
using TitleBarButton = Wpf.Ui.Controls.TitleBarButton;

namespace MesIngest.Watch;

internal enum WatchWorkspacePage
{
    Overview,
    DemandSeries,
    ReadabilityAudit,
    ErrorSearch,
    AreaFilter,
    CurrentAttention,
    Settings,
}

internal sealed class WatchOverviewNavigationEventArgs(
    OverviewNavigationIntent intent) : EventArgs
{
    public OverviewNavigationIntent Intent { get; } = intent;
}

/// <summary>
/// Production ticket-19 shell. The window is a thin WPF projection over the
/// immutable V2 workspace state; all Host admission, cancellation, and stale
/// snapshot rules remain owned by <see cref="WatchV2WorkspaceSession"/>.
/// </summary>
internal partial class WatchWorkspaceWindow : IDisposable
{
    private readonly WatchV2WorkspaceSession _session;
    private readonly WatchV2AutoRefreshCoordinator _autoRefresh;
    private readonly string _connectionPreferencesPath;
    private readonly string _workspacePreferencesPath;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private WatchHostSettings _currentHostSettings;
    private WatchV2Preferences _preferences;
    private WatchAreaDisplayContext _areaContext = WatchAreaDisplayContext.AllAreas;
    private WatchOverviewQuery _overviewQuery = new();
    private DemandSeriesBrowseQuery _demandSeriesQuery = new(new DemandSeriesBrowseFilter());
    private ReadabilityAuditQuery _readabilityAuditQuery = new(new ReadabilityAuditFilter());
    private ErrorSearchQuery _errorSearchQuery = new(
        new ErrorSearchFilter(),
        ErrorSearchWindowSelection.Last7Days);
    private CurrentIngestAttentionQuery _currentAttentionQuery = new();
    private WatchWorkspacePage _activePage = WatchWorkspacePage.Overview;
    private bool _initialized;
    private bool _isWatchingSystemTheme;
    private bool _disposed;

    internal WatchWorkspaceWindow(
        WatchHostSettings initialHostSettings,
        WatchV2Preferences preferences,
        string connectionPreferencesPath,
        string workspacePreferencesPath,
        Func<WatchHostSettings, IWatchV2ApiClient>? clientFactory = null,
        TimeProvider? timeProvider = null,
        bool initializeOnLoaded = true)
    {
        _currentHostSettings = initialHostSettings
            ?? throw new ArgumentNullException(nameof(initialHostSettings));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _connectionPreferencesPath = Path.GetFullPath(connectionPreferencesPath);
        _workspacePreferencesPath = Path.GetFullPath(workspacePreferencesPath);
        _session = new WatchV2WorkspaceSession(clientFactory, timeProvider);
        _autoRefresh = new WatchV2AutoRefreshCoordinator(
            _session,
            preferences.RefreshIntervals,
            timeProvider);

        InitializeComponent();
        if (Application.Current is null)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);
        }
        InitializeIntervalInputs();
        ApplyDisplayPreferences(preferences.Display);
        PopulateSettingsInputs();
        _autoRefresh.RefreshStateChanged += OnAutoRefreshStateChanged;
        SourceInitialized += OnWindowSourceInitialized;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        if (initializeOnLoaded)
        {
            Loaded += OnWindowLoaded;
        }

        NavigateTo(WatchWorkspacePage.Overview, activateRefresh: false);
        RenderWorkspace();
    }

    internal WatchV2WorkspaceState WorkspaceState => _session.State;

    internal WatchV2AutoRefreshSettings AutoRefreshSettings => _autoRefresh.Settings;

    internal WatchAreaDisplayContext AreaContext => _areaContext;

    internal WatchWorkspacePage ActivePage => _activePage;

    internal OverviewNavigationIntent? LastOverviewNavigationIntent { get; private set; }

    internal Task InitializationTask { get; private set; } = Task.CompletedTask;

    internal event EventHandler<WatchOverviewNavigationEventArgs>? OverviewNavigationRequested;

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            await InitializationTask.ConfigureAwait(true);
            return;
        }

        _initialized = true;
        var initialization = InitializeCoreAsync(cancellationToken);
        InitializationTask = initialization;
        await initialization.ConfigureAwait(true);
    }

    internal async Task<bool> ApplyHostAsync(
        WatchHostSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _currentHostSettings = settings;
        _autoRefresh.Deactivate();
        NavigateTo(WatchWorkspacePage.Overview, activateRefresh: false);
        var apply = _session.ApplyAsync(settings, cancellationToken);
        var expectedHostGeneration = _session.State.HostGeneration;
        RenderWorkspace();
        await apply.ConfigureAwait(true);
        RenderWorkspace();
        if (_session.State.HostGeneration != expectedHostGeneration)
        {
            return false;
        }

        WatchConnectionPreferencesStore.Save(
            _connectionPreferencesPath,
            new WatchConnectionPreferences(
                settings.BaseUrl,
                settings.RequestTimeoutSeconds,
                WatchCredentialReference.ExternalConfiguration));

        if (_session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            await RefreshOverviewAndRenderAsync(cancellationToken).ConfigureAwait(true);
            if (_session.State.HostGeneration != expectedHostGeneration)
            {
                return false;
            }

            _autoRefresh.ActivateOverview(_overviewQuery);
        }

        return true;
    }

    internal async Task ApplyAreaContextAsync(
        WatchAreaDisplayContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _areaContext = (context ?? throw new ArgumentNullException(nameof(context)))
            .NormalizeAndValidate();
        _overviewQuery = new WatchOverviewQuery(_areaContext.MesAreas).NormalizeAndValidate();
        _demandSeriesQuery = _demandSeriesQuery with
        {
            Filter = _demandSeriesQuery.Filter with { MesAreas = _areaContext.MesAreas },
            PageNumber = 1,
            SnapshotReference = null,
            Cursor = null,
        };
        _readabilityAuditQuery = _readabilityAuditQuery with
        {
            Filter = _readabilityAuditQuery.Filter with { MesAreas = _areaContext.MesAreas },
            PageNumber = 1,
            SnapshotReference = null,
            Cursor = null,
        };
        RenderWorkspace();

        if (_session.State.ConnectionStatus != WatchHostConnectionStatus.Connected)
        {
            return;
        }

        await RefreshOverviewAndRenderAsync(cancellationToken).ConfigureAwait(true);
        if (_activePage == WatchWorkspacePage.Overview)
        {
            _autoRefresh.ActivateOverview(_overviewQuery);
        }
    }

    internal void ApplyLocalPreferences(
        WatchV2AutoRefreshSettings refreshIntervals,
        WatchV2DisplayPreferences display)
    {
        ArgumentNullException.ThrowIfNull(refreshIntervals);
        ArgumentNullException.ThrowIfNull(display);
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var view in Enum.GetValues<WatchV2DataView>())
        {
            _autoRefresh.Update(view, refreshIntervals.For(view));
        }

        _preferences = new WatchV2Preferences(_autoRefresh.Settings, display);
        WatchV2PreferencesStore.Save(_workspacePreferencesPath, _preferences);
        ApplyDisplayPreferences(display, restoreGeometry: false);
    }

    internal void NavigateFromOverview(OverviewNavigationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.PageNumber != 1 || intent.Cursor is not null)
        {
            throw new ArgumentException(
                "Overview navigation must start on page one with a null cursor.",
                nameof(intent));
        }

        LastOverviewNavigationIntent = intent;
        ApplyNavigationIntent(intent);
        OverviewNavigationRequested?.Invoke(
            this,
            new WatchOverviewNavigationEventArgs(intent));
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var apply = _session.ApplyAsync(_currentHostSettings, cancellationToken);
        RenderWorkspace();
        await apply.ConfigureAwait(true);
        RenderWorkspace();
        if (_session.State.ConnectionStatus != WatchHostConnectionStatus.Connected)
        {
            return;
        }

        await RefreshOverviewAndRenderAsync(cancellationToken).ConfigureAwait(true);
        _autoRefresh.ActivateOverview(_overviewQuery);
    }

    private async Task RefreshOverviewAndRenderAsync(CancellationToken cancellationToken)
    {
        var refresh = _session.RefreshOverviewAsync(_overviewQuery, cancellationToken);
        RenderWorkspace();
        await refresh.ConfigureAwait(true);
        RenderWorkspace();
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;
        try
        {
            await InitializeAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the window is a neutral end to startup.
        }
    }

    private void OnAutoRefreshStateChanged(
        object? sender,
        WatchV2AutoRefreshEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            RenderWorkspace();
        }
        else
        {
            _ = Dispatcher.BeginInvoke((Action)RenderWorkspace);
        }
    }

    private void RenderWorkspace()
    {
        if (_disposed)
        {
            return;
        }

        var state = _session.State;
        var presentation = WatchOverviewPresentation.Project(state, _areaContext);
        OverviewContextText.Text = $"{presentation.SnapshotFacts} · {presentation.ClientAttemptFacts}";
        OverviewInfoBar.IsOpen = presentation.IsInfoOpen;
        OverviewInfoBar.Severity = ToInfoBarSeverity(presentation.InfoSeverity);
        OverviewInfoBar.Title = presentation.InfoTitle;
        OverviewInfoBar.Message = presentation.InfoMessage;
        SeriesSummaryValue.Text = presentation.SeriesValue;
        SeriesSummaryDetail.Text = presentation.SeriesDetail;
        ReadabilitySummaryValue.Text = presentation.ReadabilityValue;
        ReadabilitySummaryDetail.Text = presentation.ReadabilityDetail;
        ErrorsSummaryValue.Text = presentation.ErrorsValue;
        ErrorsSummaryDetail.Text = presentation.ErrorsDetail;
        AttentionSummaryValue.Text = presentation.AttentionValue;
        AttentionSummaryDetail.Text = presentation.AttentionDetail;
        LocalAreaHeadingText.Text = presentation.LocalAreaHeading;
        LocalAreaDetailText.Text = presentation.LocalAreaDetail;
        HostAreaScopeText.Text = presentation.HostAreaScope;
        RecentActivityHeadingText.Text = presentation.RecentActivityHeading;
        SnapshotFactsText.Text = presentation.SnapshotFacts;
        ClientAttemptFactsText.Text = presentation.ClientAttemptFacts;
        AutomationProperties.SetName(
            OverviewContextText,
            $"概览快照与客户端读取时间：{OverviewContextText.Text}");
        AutomationProperties.SetName(
            SnapshotFactsText,
            $"Host 快照事实：{SnapshotFactsText.Text}");
        AutomationProperties.SetName(
            ClientAttemptFactsText,
            $"Watch 客户端读取事实：{ClientAttemptFactsText.Text}");
        AutomationProperties.SetName(
            OverviewInfoBar,
            presentation.IsInfoOpen
                ? $"{presentation.InfoTitle}。{presentation.InfoMessage}"
                : "概览读取状态：当前无活动通知");
        StaleNoticeText.Text = presentation.IsStale
            ? "数据可能已过期；卡片仍属于上方标明的 Host 已提交范围。"
            : string.Empty;
        AutomationProperties.SetName(
            StaleNoticeText,
            presentation.IsStale ? StaleNoticeText.Text : "概览数据未标记为陈旧");
        SetNavigationAction(SeriesSummaryAction, presentation.SeriesNavigation);
        SetNavigationAction(ReadabilitySummaryAction, presentation.ReadabilityNavigation);
        SetNavigationAction(ErrorsSummaryAction, presentation.ErrorsNavigation);
        SetNavigationAction(AttentionSummaryAction, presentation.AttentionNavigation);
        SetNavigationAction(SeriesTrackingAction, state.Overview.Snapshot?.Series.TrackingNavigation);
        SetNavigationAction(SeriesArchivedAction, state.Overview.Snapshot?.Series.ArchivedNavigation);
        SetNavigationAction(SeriesGoneAction, state.Overview.Snapshot?.Series.GoneNavigation);
        SetNavigationAction(
            SeriesLongGoneVisibleAction,
            state.Overview.Snapshot?.Series.LongGoneButVisibleNavigation);
        SetNavigationAction(ReadableSummaryAction, state.Overview.Snapshot?.Readability.ReadableNavigation);
        SetNavigationAction(
            NotReadableSummaryAction,
            state.Overview.Snapshot?.Readability.NotReadableNavigation);
        SetNavigationAction(ActiveErrorsSummaryAction, state.Overview.Snapshot?.Errors.ActiveNavigation);
        SetNavigationAction(PriorErrorsSummaryAction, state.Overview.Snapshot?.Errors.Prior7DaysNavigation);
        RenderAttentionFacetActions(state.Overview.Snapshot?.Attention);
        RenderRecentActivity(presentation);
        RenderHostFooter(state, presentation);
    }

    private void RenderAttentionFacetActions(WatchOverviewAttentionSummary? attention)
    {
        while (AttentionSummaryActions.Children.Count > 1)
        {
            AttentionSummaryActions.Children.RemoveAt(1);
        }

        if (attention is null)
        {
            return;
        }

        foreach (var facet in attention.Types.Concat(attention.Severities))
        {
            var action = new Wpf.Ui.Controls.Button
            {
                Content = $"{AttentionFacetLabel(facet.Value)} · {facet.Count:N0}",
                Tag = facet.Navigation,
                Margin = new Thickness(0, 0, 8, 8),
                Appearance = ControlAppearance.Secondary,
            };
            AutomationProperties.SetName(
                action,
                $"查看接入告警 {AttentionFacetLabel(facet.Value)} {facet.Count:N0} 项第一页");
            action.Click += OnOverviewIntentClick;
            AttentionSummaryActions.Children.Add(action);
        }
    }

    private void RenderRecentActivity(WatchOverviewPresentation presentation)
    {
        RecentActivityItems.Children.Clear();
        if (!presentation.HasSnapshot)
        {
            RecentActivityItems.Children.Add(new Wpf.Ui.Controls.TextBlock
            {
                Text = "等待 Host 概览快照。",
                FontTypography = Wpf.Ui.Controls.FontTypography.Body,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        if (presentation.RecentActivity.Count == 0)
        {
            RecentActivityItems.Children.Add(new Wpf.Ui.Controls.TextBlock
            {
                Text = "Host 在该快照窗口内没有报告重点转换；这不是健康结论。",
                FontTypography = Wpf.Ui.Controls.FontTypography.Body,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var activity in presentation.RecentActivity)
        {
            var content = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new Wpf.Ui.Controls.TextBlock
            {
                Text = activity.Heading,
                FontTypography = Wpf.Ui.Controls.FontTypography.BodyStrong,
                TextWrapping = TextWrapping.Wrap,
            });
            var detail = new Wpf.Ui.Controls.TextBlock
            {
                Text = activity.Detail,
                FontTypography = Wpf.Ui.Controls.FontTypography.Caption,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 8, 0),
            };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            text.Children.Add(detail);
            content.Children.Add(text);
            var occurredAt = new Wpf.Ui.Controls.TextBlock
            {
                Text = activity.OccurredAt,
                FontTypography = Wpf.Ui.Controls.FontTypography.Caption,
                VerticalAlignment = VerticalAlignment.Top,
            };
            occurredAt.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Grid.SetColumn(occurredAt, 1);
            content.Children.Add(occurredAt);

            var action = new Wpf.Ui.Controls.Button
            {
                Content = content,
                Tag = activity.Navigation,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12),
                Appearance = ControlAppearance.Secondary,
            };
            AutomationProperties.SetName(action, $"打开重点动态 {activity.Heading}");
            action.Click += OnOverviewIntentClick;
            RecentActivityItems.Children.Add(action);
        }
    }

    private void RenderHostFooter(
        WatchV2WorkspaceState state,
        WatchOverviewPresentation overview)
    {
        var latestViewFailure = new[]
            {
                state.Overview.LastFailureAt,
                state.DemandSeries.LastFailureAt,
                state.ReadabilityAudit.LastFailureAt,
                state.ErrorSearch.LastFailureAt,
                state.CurrentAttention.LastFailureAt,
            }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();
        var hasViewFailure = latestViewFailure != default;
        HostNavigationItem.Content = state.ConnectionStatus switch
        {
            WatchHostConnectionStatus.Connected when hasViewFailure => "Host 已连接 · 读取失败",
            WatchHostConnectionStatus.Connected => "Host 已连接",
            WatchHostConnectionStatus.Connecting => "Host 连接中",
            WatchHostConnectionStatus.Failed => "Host 连接失败",
            _ => "Host 未连接",
        };
        HostNavigationIcon.Symbol = state.ConnectionStatus switch
        {
            WatchHostConnectionStatus.Connected when hasViewFailure => SymbolRegular.CloudError24,
            WatchHostConnectionStatus.Connected => SymbolRegular.CloudCheckmark24,
            WatchHostConnectionStatus.Connecting => SymbolRegular.CloudSync24,
            WatchHostConnectionStatus.Failed => SymbolRegular.CloudDismiss24,
            _ => SymbolRegular.CloudOff24,
        };
        AutomationProperties.SetName(
            HostNavigationItem,
            $"Host 状态：{HostNavigationItem.Content}；打开连接设置");
        HostNavigationItem.ToolTip = hasViewFailure
            ? $"{overview.HostDetail} · 最近页面读取失败 {latestViewFailure:yyyy-MM-dd HH:mm:ss}"
            : overview.HostDetail;
        SettingsHostStateText.Text = $"{HostNavigationItem.Content} · {overview.HostDetail}";
    }

    private static void SetNavigationAction(
        Wpf.Ui.Controls.Button button,
        OverviewNavigationIntent? intent)
    {
        button.Tag = intent;
        button.IsEnabled = intent is not null;
    }

    private static string AttentionFacetLabel(string value) => value switch
    {
        CurrentIngestAttentionKinds.SeriesError => "Series 错误",
        CurrentIngestAttentionKinds.PollRunFailure => "轮询失败",
        CurrentIngestAttentionKinds.TaskTypeProtection => "任务类型保护",
        CurrentIngestAttentionKinds.UnassignedMesObservation => "未分配观测",
        CurrentIngestAttentionSeverities.Error => "ERROR",
        CurrentIngestAttentionSeverities.Warning => "WARNING",
        _ => value,
    };

    private void ApplyNavigationIntent(OverviewNavigationIntent intent)
    {
        switch (intent.Target)
        {
            case OverviewNavigationTargets.DemandSeries:
            case OverviewNavigationTargets.DemandSeriesDetail:
                _demandSeriesQuery = new DemandSeriesBrowseQuery(
                    new DemandSeriesBrowseFilter
                    {
                        Lifecycles = intent.Lifecycles ?? [],
                        CurrentPresences = intent.CurrentPresences ?? [],
                        WorkTypes = intent.WorkType is null ? [] : [intent.WorkType],
                        MesAreas = intent.MesAreas ?? [],
                        SeriesId = intent.SeriesId,
                    },
                    PageNumber: intent.PageNumber,
                    Cursor: intent.Cursor).NormalizeAndValidate();
                NavigateTo(WatchWorkspacePage.DemandSeries);
                break;
            case OverviewNavigationTargets.ReadabilityAudit:
                _readabilityAuditQuery = new ReadabilityAuditQuery(
                    new ReadabilityAuditFilter
                    {
                        ReadabilityStates = intent.ReadabilityStates ?? [],
                        WorkTypes = intent.WorkType is null ? [] : [intent.WorkType],
                        MesAreas = intent.MesAreas ?? [],
                    },
                    PageNumber: intent.PageNumber,
                    Cursor: intent.Cursor).NormalizeAndValidate();
                NavigateTo(WatchWorkspacePage.ReadabilityAudit);
                break;
            case OverviewNavigationTargets.ErrorSearch:
                _errorSearchQuery = new ErrorSearchQuery(
                    new ErrorSearchFilter
                    {
                        ActivityStates = intent.ErrorActivityStates ?? [],
                        SeriesId = intent.SeriesId,
                    },
                    intent.ErrorWindow is null
                        ? ErrorSearchWindowSelection.Last7Days
                        : new ErrorSearchWindowSelection(intent.ErrorWindow),
                    Cursor: intent.Cursor).NormalizeAndValidate();
                NavigateTo(WatchWorkspacePage.ErrorSearch);
                break;
            case OverviewNavigationTargets.CurrentIngestAttention:
                _currentAttentionQuery = new CurrentIngestAttentionQuery(
                    PageNumber: intent.PageNumber,
                    Kinds: intent.AttentionKinds,
                    Severities: intent.AttentionSeverities).NormalizeAndValidate();
                NavigateTo(WatchWorkspacePage.CurrentAttention);
                break;
            case OverviewNavigationTargets.TaskTypeProtection:
                _currentAttentionQuery = new CurrentIngestAttentionQuery(
                    PageNumber: intent.PageNumber,
                    Kinds: [CurrentIngestAttentionKinds.TaskTypeProtection],
                    Severities: intent.AttentionSeverities).NormalizeAndValidate();
                NavigateTo(WatchWorkspacePage.CurrentAttention);
                break;
            case OverviewNavigationTargets.PollTrace:
                _currentAttentionQuery = new CurrentIngestAttentionQuery(
                    PageNumber: intent.PageNumber,
                    Kinds: [CurrentIngestAttentionKinds.PollRunFailure],
                    Severities: intent.AttentionSeverities).NormalizeAndValidate();
                NavigateTo(WatchWorkspacePage.CurrentAttention);
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported overview navigation target '{intent.Target}'.",
                    nameof(intent));
        }
    }

    private void NavigateTo(
        WatchWorkspacePage page,
        bool activateRefresh = true)
    {
        _activePage = page;
        OverviewPage.Visibility = page == WatchWorkspacePage.Overview ? Visibility.Visible : Visibility.Collapsed;
        DemandSeriesPage.Visibility = page == WatchWorkspacePage.DemandSeries ? Visibility.Visible : Visibility.Collapsed;
        ReadabilityAuditPage.Visibility = page == WatchWorkspacePage.ReadabilityAudit ? Visibility.Visible : Visibility.Collapsed;
        ErrorSearchPage.Visibility = page == WatchWorkspacePage.ErrorSearch ? Visibility.Visible : Visibility.Collapsed;
        AreaFilterPage.Visibility = page == WatchWorkspacePage.AreaFilter ? Visibility.Visible : Visibility.Collapsed;
        CurrentAttentionPage.Visibility = page == WatchWorkspacePage.CurrentAttention ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == WatchWorkspacePage.Settings ? Visibility.Visible : Visibility.Collapsed;
        OverviewNavigationItem.IsActive = page == WatchWorkspacePage.Overview;
        DemandSeriesNavigationItem.IsActive = page == WatchWorkspacePage.DemandSeries;
        ReadabilityAuditNavigationItem.IsActive = page == WatchWorkspacePage.ReadabilityAudit;
        ErrorSearchNavigationItem.IsActive = page == WatchWorkspacePage.ErrorSearch;
        AreaFilterNavigationItem.IsActive = page == WatchWorkspacePage.AreaFilter;
        CurrentAttentionNavigationItem.IsActive = page == WatchWorkspacePage.CurrentAttention;
        SettingsNavigationItem.IsActive = page == WatchWorkspacePage.Settings;
        HostNavigationItem.IsActive = false;

        if (!activateRefresh
            || _session.State.ConnectionStatus != WatchHostConnectionStatus.Connected)
        {
            return;
        }

        switch (page)
        {
            case WatchWorkspacePage.Overview:
                _autoRefresh.ActivateOverview(_overviewQuery);
                break;
            case WatchWorkspacePage.DemandSeries:
                _autoRefresh.ActivateDemandSeries(_demandSeriesQuery);
                break;
            case WatchWorkspacePage.ReadabilityAudit:
                _autoRefresh.ActivateReadabilityAudit(_readabilityAuditQuery);
                break;
            case WatchWorkspacePage.ErrorSearch:
                _autoRefresh.ActivateErrorSearch(_errorSearchQuery);
                break;
            case WatchWorkspacePage.CurrentAttention:
                _autoRefresh.ActivateCurrentAttention(_currentAttentionQuery);
                break;
            case WatchWorkspacePage.AreaFilter:
            case WatchWorkspacePage.Settings:
                _autoRefresh.Deactivate();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(page), page, null);
        }
    }

    private void InitializeIntervalInputs()
    {
        ConfigureIntervalInput(OverviewIntervalInput, _preferences.RefreshIntervals.Overview.IntervalSeconds);
        ConfigureIntervalInput(DemandSeriesIntervalInput, _preferences.RefreshIntervals.DemandSeries.IntervalSeconds);
        ConfigureIntervalInput(ReadabilityAuditIntervalInput, _preferences.RefreshIntervals.ReadabilityAudit.IntervalSeconds);
        ConfigureIntervalInput(ErrorSearchIntervalInput, _preferences.RefreshIntervals.ErrorSearch.IntervalSeconds);
        ConfigureIntervalInput(CurrentAttentionIntervalInput, _preferences.RefreshIntervals.CurrentIngestAttention.IntervalSeconds);
    }

    private static void ConfigureIntervalInput(ComboBox comboBox, int selectedSeconds)
    {
        comboBox.ItemsSource = WatchV2AutoRefreshSetting.AllowedIntervals
            .Select(seconds => new RefreshIntervalChoice(seconds, $"{seconds} 秒"))
            .ToArray();
        comboBox.DisplayMemberPath = nameof(RefreshIntervalChoice.Label);
        comboBox.SelectedValuePath = nameof(RefreshIntervalChoice.Seconds);
        comboBox.SelectedValue = selectedSeconds;
    }

    private void PopulateSettingsInputs()
    {
        HostBaseUrlInput.Text = _currentHostSettings.BaseUrl;
        HostCredentialInput.Password = string.Empty;
        RequestTimeoutInput.Text = _currentHostSettings.RequestTimeoutSeconds.ToString();
        RememberWindowSizeCheckBox.IsChecked = _preferences.Display.RememberWindowSize;
        KeepNavigationPaneOpenCheckBox.IsChecked = _preferences.Display.IsNavigationPaneOpen;
    }

    private void ApplyDisplayPreferences(
        WatchV2DisplayPreferences display,
        bool restoreGeometry = true)
    {
        if (restoreGeometry)
        {
            var preferred = display.RememberWindowSize
                ? display
                : WatchV2DisplayPreferences.Default;
            var workArea = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, Math.Min(preferred.WindowWidth, workArea.Width));
            Height = Math.Max(MinHeight, Math.Min(preferred.WindowHeight, workArea.Height));
        }

        WorkspaceNavigation.IsPaneOpen = display.IsNavigationPaneOpen;
    }

    private async void OnApplyHostClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var credential = string.IsNullOrWhiteSpace(HostCredentialInput.Password)
                ? _currentHostSettings.Credential
                : HostCredentialInput.Password;
            if (!int.TryParse(RequestTimeoutInput.Text, out var timeoutSeconds))
            {
                throw new ArgumentException("请求超时必须是 1–300 秒之间的整数。");
            }

            var settings = new WatchHostSettings(
                HostBaseUrlInput.Text,
                credential,
                timeoutSeconds);
            ShowSettingsInfo(
                InfoBarSeverity.Informational,
                "正在应用 Host 设置",
                "旧 Host 业务状态已立即清空；正在验证新 Host 契约。");
            if (!await ApplyHostAsync(settings, _lifetimeCancellation.Token).ConfigureAwait(true))
            {
                return;
            }

            PopulateSettingsInputs();
            ShowSettingsInfo(
                _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Error,
                _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected
                    ? "Host 设置已应用"
                    : "Host 连接失败",
                _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected
                    ? "契约兼容，概览已读取并恢复自动刷新。"
                    : "旧 Host 数据不会恢复；请检查地址、凭据、超时和契约版本。");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the window is a neutral end to an in-flight apply.
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowSettingsInfo(InfoBarSeverity.Error, "无法应用 Host 设置", exception.Message);
        }
        finally
        {
            HostCredentialInput.Password = string.Empty;
        }
    }

    private void OnSaveLocalPreferencesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var refresh = new WatchV2AutoRefreshSettings(
                ReadInterval(OverviewIntervalInput),
                ReadInterval(DemandSeriesIntervalInput),
                ReadInterval(ReadabilityAuditIntervalInput),
                ReadInterval(ErrorSearchIntervalInput),
                ReadInterval(CurrentAttentionIntervalInput));
            var display = new WatchV2DisplayPreferences(
                RememberWindowSizeCheckBox.IsChecked == true,
                Math.Max(MinWidth, Width),
                Math.Max(MinHeight, Height),
                KeepNavigationPaneOpenCheckBox.IsChecked == true);
            var hostGeneration = _session.State.HostGeneration;
            ApplyLocalPreferences(refresh, display);
            if (_session.State.HostGeneration != hostGeneration)
            {
                throw new InvalidOperationException("Local preferences must not replace the Host session.");
            }

            ShowSettingsInfo(
                InfoBarSeverity.Success,
                "本机设置已保存",
                "自动刷新保持开启；当前 Host 会话未重建。");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowSettingsInfo(InfoBarSeverity.Error, "无法保存本机设置", exception.Message);
        }
    }

    private static WatchV2AutoRefreshSetting ReadInterval(ComboBox comboBox) =>
        comboBox.SelectedValue is int seconds
            ? new WatchV2AutoRefreshSetting(seconds)
            : throw new ArgumentException("请选择 10、30、60 或 300 秒。");

    private void ShowSettingsInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.Title = title;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
        AutomationProperties.SetName(SettingsInfoBar, $"{title}。{message}");
    }

    private void OnOverviewIntentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OverviewNavigationIntent intent })
        {
            NavigateFromOverview(intent);
        }
    }

    private void OnOverviewNavigationClick(object sender, RoutedEventArgs e) =>
        NavigateTo(WatchWorkspacePage.Overview);

    private void OnDemandSeriesNavigationClick(object sender, RoutedEventArgs e) =>
        NavigateTo(WatchWorkspacePage.DemandSeries);

    private void OnReadabilityAuditNavigationClick(object sender, RoutedEventArgs e) =>
        NavigateTo(WatchWorkspacePage.ReadabilityAudit);

    private void OnErrorSearchNavigationClick(object sender, RoutedEventArgs e) =>
        NavigateTo(WatchWorkspacePage.ErrorSearch);

    private void OnAreaFilterNavigationClick(object sender, RoutedEventArgs e) =>
        NavigateTo(WatchWorkspacePage.AreaFilter);

    private void OnCurrentAttentionNavigationClick(object sender, RoutedEventArgs e) =>
        NavigateTo(WatchWorkspacePage.CurrentAttention);

    private void OnSettingsNavigationClick(object sender, RoutedEventArgs e) =>
        NavigateTo(WatchWorkspacePage.Settings);

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ActualWidth < 900 && WorkspaceNavigation.IsPaneOpen)
        {
            WorkspaceNavigation.IsPaneOpen = false;
        }

        var contentWidth = Math.Max(0, ActualWidth - (WorkspaceNavigation.IsPaneOpen ? 232 : 48) - 48);
        OverviewSummaryCards.Columns = contentWidth >= 1160 ? 5 : contentWidth >= 760 ? 3 : 2;
        var stackBody = contentWidth < 900;
        Grid.SetColumn(OverviewFactsCard, stackBody ? 0 : 2);
        Grid.SetRow(OverviewFactsCard, stackBody ? 2 : 0);
        OverviewBodyGap.Width = stackBody ? new GridLength(0) : new GridLength(12);
        FactsColumn.Width = stackBody ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        OverviewBodyVerticalGap.Height = stackBody ? new GridLength(16) : new GridLength(0);
        OverviewFactsRow.Height = stackBody ? GridLength.Auto : new GridLength(0);

        var stackSettings = contentWidth < 940;
        Grid.SetColumn(RefreshSettingsCard, stackSettings ? 0 : 2);
        Grid.SetRow(RefreshSettingsCard, stackSettings ? 2 : 0);
        SettingsGapColumn.Width = stackSettings ? new GridLength(0) : new GridLength(16);
        SettingsRightColumn.Width = stackSettings ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SettingsVerticalGap.Height = stackSettings ? new GridLength(16) : new GridLength(0);
        SettingsBottomRow.Height = stackSettings ? GridLength.Auto : new GridLength(0);
        WorkspaceContent.Margin = contentWidth < 760
            ? new Thickness(12)
            : new Thickness(24, 16, 24, 16);
    }

    private void OnWindowTitleBarLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TitleBar titleBar)
        {
            return;
        }

        titleBar.ApplyTemplate();
        ConfigureTitleBarButton(titleBar, "PART_MinimizeButton", "最小化窗口");
        ConfigureTitleBarButton(titleBar, "PART_CloseButton", "关闭窗口");
        UpdateMaximizeButtonAccessibility(titleBar);
        StateChanged -= OnWindowStateChangedForTitleBar;
        StateChanged += OnWindowStateChangedForTitleBar;
    }

    private void OnWindowStateChangedForTitleBar(object? sender, EventArgs e) =>
        UpdateMaximizeButtonAccessibility(WindowTitleBar);

    private void UpdateMaximizeButtonAccessibility(TitleBar titleBar) =>
        ConfigureTitleBarButton(
            titleBar,
            "PART_MaximizeButton",
            WindowState == WindowState.Maximized ? "还原窗口" : "最大化窗口");

    private static void ConfigureTitleBarButton(
        TitleBar titleBar,
        string partName,
        string automationName)
    {
        if (titleBar.Template?.FindName(partName, titleBar) is not TitleBarButton button)
        {
            return;
        }

        button.Focusable = true;
        KeyboardNavigation.SetIsTabStop(button, true);
        AutomationProperties.SetName(button, automationName);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        SaveWindowGeometry();
        DisposeCore();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isWatchingSystemTheme)
        {
            Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(this);
            _isWatchingSystemTheme = false;
        }
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(
            this,
            Wpf.Ui.Controls.WindowBackdropType.Mica,
            updateAccents: true);
        _isWatchingSystemTheme = true;
    }

    private void SaveWindowGeometry()
    {
        if (!_preferences.Display.RememberWindowSize)
        {
            return;
        }

        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(0, 0, Width, Height) : RestoreBounds;
            var display = new WatchV2DisplayPreferences(
                rememberWindowSize: true,
                windowWidth: Math.Max(MinWidth, bounds.Width),
                windowHeight: Math.Max(MinHeight, bounds.Height),
                isNavigationPaneOpen: WorkspaceNavigation.IsPaneOpen);
            _preferences = _preferences with { Display = display };
            WatchV2PreferencesStore.Save(_workspacePreferencesPath, _preferences);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            // A display preference failure must not block a clean window close.
        }
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _autoRefresh.RefreshStateChanged -= OnAutoRefreshStateChanged;
        _lifetimeCancellation.Cancel();
        _autoRefresh.Dispose();
        _session.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private static InfoBarSeverity ToInfoBarSeverity(WatchPresentationSeverity severity) =>
        severity switch
        {
            WatchPresentationSeverity.Success => InfoBarSeverity.Success,
            WatchPresentationSeverity.Warning => InfoBarSeverity.Warning,
            WatchPresentationSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };

    private sealed record RefreshIntervalChoice(int Seconds, string Label);
}
