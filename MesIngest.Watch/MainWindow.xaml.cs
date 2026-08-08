using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using MesIngest.Core;

namespace MesIngest.Watch;

internal partial class MainWindow : Window
{
    private IWatchReadQueries _client;
    private WatchBrowseSession _browse;
    private WatchDemandSession _visibleDemands;
    private WatchDemandSession _goneDemands;
    private WatchAlertSession _alerts;
    private WatchOverviewSession _overview;
    private readonly WatchHostSession _hostSession;
    private readonly WatchOptions _options;
    private readonly WatchConnectionEventRecorder _connectionRecorder;
    private readonly WatchConnectionEventJournal _connectionJournal;
    private readonly WatchTelemetryIoDiagnosticBuffer _telemetryIoDiagnostics;
    private readonly string _layoutPreferencesPath;
    private readonly string _autoRefreshPreferencesPath;
    private readonly string _connectionPreferencesPath;
    private readonly WatchAutoRefreshSchedule _autoRefresh;
    private readonly TimeProvider _timeProvider;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _bannerHoldTimer;
    private readonly WatchRefreshAdmission _refreshAdmission = new();
    private readonly Dictionary<string, AlertDetailWindow> _openAlertDetails = new(StringComparer.Ordinal);
    private readonly List<WeakReference<AlertDetailWindow>> _openAlertDetailsWithoutId = [];

    private WatchPollHealthDto? _health;
    private bool _isOverviewRefreshing;
    private bool _isApplyingDemandProjection;
    private bool _isApplyingAlertProjection;
    private bool _isResettingDemandViews;
    private bool _isSyncingAutoRefreshControls;
    private bool _isApplyingHostSession;
    private string? _overviewNotice;
    private WatchRefreshState _refreshState = WatchRefreshState.Empty;
    private WatchRefreshState _visibleRefreshState = WatchRefreshState.Empty;
    private WatchRefreshState _goneRefreshState = WatchRefreshState.Empty;
    private WatchRefreshState _alertRefreshState = WatchRefreshState.Empty;
    private WatchBannerHoldState _bannerHold = WatchBannerHoldState.Empty;
    private CancellationTokenSource? _refreshCancellation;
    private long _hostGeneration;
    private WatchDemandViewKind _activeDemandViewKind = WatchDemandViewKind.Visible;
    private MesIngestApiClient? _bootstrapClient;

    public MainWindow(
        MesIngestApiClient client,
        WatchOptions options,
        WatchConnectionEventJournal? connectionJournal = null,
        WatchConnectionEventRecorder? connectionRecorder = null,
        string? layoutPreferencesPath = null,
        string? autoRefreshPreferencesPath = null,
        string? connectionPreferencesPath = null,
        WatchTelemetryIoDiagnosticBuffer? telemetryIoDiagnostics = null,
        Func<WatchHostSettings, IWatchHostQueryAdapter>? hostAdapterFactory = null,
        TimeProvider? timeProvider = null)
    {
        InitializeComponent();
        _timeProvider = timeProvider ?? TimeProvider.System;
        DemandTaskTypeFilter.ItemsSource =
            new[] { string.Empty }.Concat(WatchDemandDraft.ProductionTaskTypes).ToArray();
        DemandTaskTypeFilter.SelectedIndex = 0;
        AlertActivityFilter.ItemsSource = new[] { "活动", "已解除" };
        AlertActivityFilter.SelectedIndex = 0;
        AlertCodeFilter.ItemsSource =
            new[] { string.Empty }.Concat(WatchAlertDraft.ProductionCodes).ToArray();
        AlertCodeFilter.SelectedIndex = 0;
        AlertSeverityFilter.ItemsSource =
            new[] { string.Empty }.Concat(WatchAlertDraft.Severities).ToArray();
        AlertSeverityFilter.SelectedIndex = 0;
        WatchGridClipboardBehavior.Attach(DemandsGrid);
        WatchGridClipboardBehavior.Attach(AlertsGrid);
        _client = client;
        _bootstrapClient = client;
        _browse = new WatchBrowseSession(client);
        _visibleDemands = new WatchDemandSession(client);
        _goneDemands = new WatchDemandSession(client, WatchDemandViewKind.Gone);
        _alerts = new WatchAlertSession(client);
        _overview = new WatchOverviewSession(client, () => _timeProvider.GetUtcNow());
        if (hostAdapterFactory is null)
        {
            var bootstrapAvailable = true;
            _hostSession = new WatchHostSession(settings =>
            {
                if (bootstrapAvailable)
                {
                    bootstrapAvailable = false;
                    _bootstrapClient = null;
                    return client;
                }

                return MesIngestApiClient.CreateForHost(settings);
            });
        }
        else
        {
            _hostSession = new WatchHostSession(hostAdapterFactory);
        }
        _options = options;
        _layoutPreferencesPath = layoutPreferencesPath ?? WatchLayoutPreferences.DefaultFilePath;
        _autoRefreshPreferencesPath = autoRefreshPreferencesPath
            ?? (layoutPreferencesPath is null
                ? WatchAutoRefreshPreferencesStore.DefaultFilePath
                : Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(layoutPreferencesPath))!,
                    "auto-refresh.json"));
        _connectionPreferencesPath = connectionPreferencesPath
            ?? (layoutPreferencesPath is null
                ? WatchConnectionPreferencesStore.DefaultFilePath
                : Path.ChangeExtension(layoutPreferencesPath, ".connection.json"));
        _autoRefresh = new WatchAutoRefreshSchedule(
            WatchAutoRefreshPreferencesStore.Load(_autoRefreshPreferencesPath),
            _timeProvider);
        _telemetryIoDiagnostics = telemetryIoDiagnostics ?? new WatchTelemetryIoDiagnosticBuffer();
        _connectionJournal = connectionJournal ?? WatchConnectionEventJournal.FromOptions(
            options,
            onWriteFailure: ex => _telemetryIoDiagnostics.Record("watch-connection", ex));
        _connectionRecorder = connectionRecorder
            ?? new WatchConnectionEventRecorder(TimeSpan.FromMinutes(5));
        Title = $"MesIngest Watch — {_options.BaseUrl}";
        HostBaseUrlInput.Text = _options.BaseUrl;
        HostCredentialInput.Password = _options.SharedSecret;
        RequestTimeoutInput.Text = _options.RequestTimeoutSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        InitializeAutoRefreshControls();

        ApplyWindowLayout(WatchLayoutPreferences.Load(_layoutPreferencesPath));
        PanesSplitter.DragCompleted += (_, _) => ConvertPaneHeightsToStars();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timer.Tick += OnAutoRefreshTick;

        _bannerHoldTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _bannerHoldTimer.Tick += (_, _) => ApplyProjection();

        Loaded += async (_, _) =>
        {
            ApplyDemandSortGlyphs();
            ApplyAlertSortGlyphs();
            _autoRefresh.Activate(WatchRefreshView.Overview);
            await ApplyHostSessionAsync(
                    new WatchHostSettings(
                        _options.BaseUrl,
                        _options.SharedSecret,
                        _options.RequestTimeoutSeconds),
                    refreshOverview: true)
                .ConfigureAwait(true);
        };
        Closed += (_, _) =>
        {
            SaveWindowLayout();
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _visibleDemands.Dispose();
            _goneDemands.Dispose();
            _alerts.Dispose();
            _hostSession.Dispose();
            _bootstrapClient?.Dispose();
            _bootstrapClient = null;
            _timer.Stop();
            _bannerHoldTimer.Stop();
        };
    }

    private void InitializeAutoRefreshControls()
    {
        var intervals = WatchAutoRefreshSetting.AllowedIntervals;
        foreach (var combo in new[]
                 {
                     OverviewAutoRefreshInterval,
                     DemandAutoRefreshInterval,
                     AlertAutoRefreshInterval,
                     SettingsOverviewAutoRefreshInterval,
                     SettingsVisibleAutoRefreshInterval,
                     SettingsGoneAutoRefreshInterval,
                     SettingsAlertAutoRefreshInterval,
                 })
        {
            combo.ItemsSource = intervals;
        }

        SyncAutoRefreshControls();
    }

    private void SyncAutoRefreshControls()
    {
        _isSyncingAutoRefreshControls = true;
        try
        {
            ApplyAutoRefreshSetting(
                OverviewAutoRefreshCheckBox,
                OverviewAutoRefreshInterval,
                WatchRefreshView.Overview);
            ApplyAutoRefreshSetting(
                DemandAutoRefreshCheckBox,
                DemandAutoRefreshInterval,
                DemandRefreshView);
            ApplyAutoRefreshSetting(
                AlertAutoRefreshCheckBox,
                AlertAutoRefreshInterval,
                WatchRefreshView.Alerts);
            ApplyAutoRefreshSetting(
                SettingsOverviewAutoRefreshCheckBox,
                SettingsOverviewAutoRefreshInterval,
                WatchRefreshView.Overview);
            ApplyAutoRefreshSetting(
                SettingsVisibleAutoRefreshCheckBox,
                SettingsVisibleAutoRefreshInterval,
                WatchRefreshView.Visible);
            ApplyAutoRefreshSetting(
                SettingsGoneAutoRefreshCheckBox,
                SettingsGoneAutoRefreshInterval,
                WatchRefreshView.Gone);
            ApplyAutoRefreshSetting(
                SettingsAlertAutoRefreshCheckBox,
                SettingsAlertAutoRefreshInterval,
                WatchRefreshView.Alerts);
        }
        finally
        {
            _isSyncingAutoRefreshControls = false;
        }
    }

    private void ApplyAutoRefreshSetting(
        CheckBox checkBox,
        ComboBox interval,
        WatchRefreshView view)
    {
        var setting = _autoRefresh.Preferences.For(view);
        checkBox.IsChecked = setting.Enabled;
        interval.SelectedItem = setting.IntervalSeconds;
        interval.IsEnabled = setting.Enabled;
    }

    private void OnAutoRefreshEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingAutoRefreshControls || sender is not CheckBox checkBox)
        {
            return;
        }

        var view = AutoRefreshViewForControl(checkBox);
        var previous = _autoRefresh.Preferences.For(view);
        _autoRefresh.Update(
            view,
            new WatchAutoRefreshSetting(checkBox.IsChecked == true, previous.IntervalSeconds));
        SaveAutoRefreshPreferences();
        SyncAutoRefreshControls();
        ApplyProjection();
    }

    private void OnAutoRefreshIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingAutoRefreshControls
            || sender is not ComboBox { SelectedItem: int interval } combo)
        {
            return;
        }

        var view = AutoRefreshViewForControl(combo);
        var previous = _autoRefresh.Preferences.For(view);
        _autoRefresh.Update(view, new WatchAutoRefreshSetting(previous.Enabled, interval));
        SaveAutoRefreshPreferences();
        SyncAutoRefreshControls();
        ApplyProjection();
    }

    private WatchRefreshView AutoRefreshViewForControl(FrameworkElement control)
    {
        if (ReferenceEquals(control, DemandAutoRefreshCheckBox)
            || ReferenceEquals(control, DemandAutoRefreshInterval))
        {
            return DemandRefreshView;
        }

        return control.Tag is WatchRefreshView view
            ? view
            : throw new InvalidOperationException(
                $"Auto-refresh control '{control.Name}' has no view.");
    }

    private void SaveAutoRefreshPreferences()
    {
        try
        {
            WatchAutoRefreshPreferencesStore.Save(
                _autoRefreshPreferencesPath,
                _autoRefresh.Preferences);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _telemetryIoDiagnostics.Record("watch-preferences", ex);
        }
    }

    private async void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        var view = CurrentRefreshView;
        if (view is null
            || !_autoRefresh.TryTakeDue(view.Value, IsRefreshInProgress(view.Value)))
        {
            return;
        }

        await RefreshViewAsync(view.Value).ConfigureAwait(true);
    }

    private Task RefreshViewAsync(WatchRefreshView view) => view switch
    {
        WatchRefreshView.Overview => RefreshAsync(WatchBrowseRefreshKind.PreserveWindow),
        WatchRefreshView.Visible when DemandRefreshView == WatchRefreshView.Visible =>
            RunDemandOperationAsync(_visibleDemands.RefreshCurrentAsync),
        WatchRefreshView.Gone when DemandRefreshView == WatchRefreshView.Gone =>
            RunDemandOperationAsync(_goneDemands.RefreshCurrentAsync),
        WatchRefreshView.Alerts => RunAlertOperationAsync(_alerts.RefreshCurrentAsync),
        _ => Task.CompletedTask,
    };

    private bool IsRefreshInProgress(WatchRefreshView view) => view switch
    {
        WatchRefreshView.Overview => _isOverviewRefreshing,
        WatchRefreshView.Visible => _visibleDemands.State.IsRefreshing,
        WatchRefreshView.Gone => _goneDemands.State.IsRefreshing,
        WatchRefreshView.Alerts => _alerts.State.IsRefreshing,
        _ => false,
    };

    private WatchRefreshView? CurrentRefreshView => PrimaryNavigation.SelectedIndex switch
    {
        0 => WatchRefreshView.Overview,
        1 => DemandRefreshView,
        2 => WatchRefreshView.Alerts,
        _ => null,
    };

    private WatchRefreshView DemandRefreshView =>
        _activeDemandViewKind == WatchDemandViewKind.Gone
            ? WatchRefreshView.Gone
            : WatchRefreshView.Visible;

    private void OnPrimaryNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OverviewPage is null || DemandsPage is null || AlertsPage is null || SettingsPage is null)
        {
            return;
        }

        _refreshCancellation?.Cancel();
        _overview.CancelActive();
        ActiveDemandSession.CancelActive(userInitiated: false);
        _alerts.CancelActive(userInitiated: false);
        var selected = PrimaryNavigation.SelectedIndex;
        OverviewPage.Visibility = selected == 0 ? Visibility.Visible : Visibility.Collapsed;
        DemandsPage.Visibility = selected == 1 ? Visibility.Visible : Visibility.Collapsed;
        AlertsPage.Visibility = selected == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = selected == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (selected == 3)
        {
            _autoRefresh.Deactivate();
            SyncAutoRefreshControls();
            return;
        }

        var view = selected switch
        {
            0 => WatchRefreshView.Overview,
            1 => DemandRefreshView,
            2 => WatchRefreshView.Alerts,
            _ => WatchRefreshView.Overview,
        };
        var refreshImmediately = _autoRefresh.Activate(view);
        SyncAutoRefreshControls();

        if (_isApplyingHostSession)
        {
            return;
        }

        if (IsLoaded && selected == 0 && refreshImmediately)
        {
            _ = RefreshAsync(WatchBrowseRefreshKind.PreserveWindow);
        }
        else if (IsLoaded && selected == 1)
        {
            if (ActiveDemandSession.State.LastSuccessfulAt is null)
            {
                _ = RunDemandOperationAsync(ActiveDemandSession.LoadInitialAsync);
            }
            else if (refreshImmediately)
            {
                _ = RunDemandOperationAsync(ActiveDemandSession.RefreshCurrentAsync);
            }
        }
        else if (IsLoaded && selected == 2)
        {
            if (_alerts.State.LastSuccessfulAt is null)
            {
                _ = RunAlertOperationAsync(_alerts.LoadInitialAsync);
            }
            else if (refreshImmediately)
            {
                _ = RunAlertOperationAsync(_alerts.RefreshCurrentAsync);
            }
        }
    }

    private void OnOverviewAlertsClick(object sender, RoutedEventArgs e)
    {
        _alerts.Dispose();
        _alerts = new WatchAlertSession(_hostSession);
        _alertRefreshState = WatchRefreshState.Empty;
        ApplyAlertDraftToControls(_alerts.State.Draft);
        PrimaryNavigation.SelectedIndex = 2;
    }

    private void OnOverviewRefreshClick(object sender, RoutedEventArgs e) =>
        _ = RefreshAsync(WatchBrowseRefreshKind.Reset);

    private void OnOverviewCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isOverviewRefreshing)
        {
            _overviewNotice = "已取消";
        }
        _autoRefresh.CancelCurrentRequest(WatchRefreshView.Overview);
        _overview.CancelActive();
        _refreshCancellation?.Cancel();
        _isOverviewRefreshing = false;
        ApplyProjection();
    }

    private void OnOverviewDemandsClick(object sender, RoutedEventArgs e)
    {
        _visibleDemands.Dispose();
        _visibleDemands = new WatchDemandSession(_hostSession);
        ResetDemandTabsToVisible();
        PrimaryNavigation.SelectedIndex = 1;
    }

    private async void OnApplyHostClick(object sender, RoutedEventArgs e)
    {
        SettingsValidationText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "WatchErrorBrush");
        SettingsValidationText.Text = string.Empty;
        if (!int.TryParse(
                RequestTimeoutInput.Text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var timeoutSeconds))
        {
            SettingsValidationText.Text = "请求超时必须是 1–300 之间的整数。";
            return;
        }

        WatchHostSettings settings;
        try
        {
            settings = new WatchHostSettings(
                HostBaseUrlInput.Text,
                HostCredentialInput.Password,
                timeoutSeconds);
        }
        catch (ArgumentException ex)
        {
            SettingsValidationText.Text = ex.Message;
            return;
        }

        ApplyHostButton.IsEnabled = false;
        try
        {
            SaveConnectionPreferences(settings);
            await ApplyHostSessionAsync(settings, refreshOverview: true).ConfigureAwait(true);
        }
        finally
        {
            ApplyHostButton.IsEnabled = true;
        }
    }

    private async Task ApplyHostSessionAsync(WatchHostSettings settings, bool refreshOverview)
    {
        _isApplyingHostSession = true;
        var generation = ++_hostGeneration;
        _timer.Stop();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        _overview.CancelActive();

        _health = null;
        _refreshState = WatchRefreshState.Empty;
        _visibleRefreshState = WatchRefreshState.Empty;
        _goneRefreshState = WatchRefreshState.Empty;
        _alertRefreshState = WatchRefreshState.Empty;
        _overviewNotice = null;
        _bannerHold = WatchBannerHoldState.Empty;
        _client = _hostSession;
        _browse = new WatchBrowseSession(_hostSession);
        _visibleDemands.Dispose();
        _visibleDemands = new WatchDemandSession(_hostSession);
        _goneDemands.Dispose();
        _goneDemands = new WatchDemandSession(_hostSession, WatchDemandViewKind.Gone);
        _alerts.Dispose();
        _alerts = new WatchAlertSession(_hostSession);
        _overview = new WatchOverviewSession(_hostSession, () => _timeProvider.GetUtcNow());

        foreach (var detail in _openAlertDetails.Values.ToArray())
        {
            detail.Close();
        }
        _openAlertDetails.Clear();
        foreach (var weak in _openAlertDetailsWithoutId.ToArray())
        {
            if (weak.TryGetTarget(out var detail))
            {
                detail.Close();
            }
        }
        _openAlertDetailsWithoutId.Clear();
        ResetDemandTabsToVisible();
        ApplyAlertDraftToControls(_alerts.State.Draft);
        DemandsGrid.SelectedItem = null;
        AlertsGrid.SelectedItem = null;
        PrimaryNavigation.SelectedIndex = 0;

        _options.BaseUrl = settings.BaseUrl;
        _options.SharedSecret = settings.Credential;
        _options.RequestTimeoutSeconds = settings.RequestTimeoutSeconds;
        Title = $"MesIngest Watch — {settings.BaseUrl}";
        ApplyProjection();

        try
        {
            await _hostSession.ApplyAsync(settings).ConfigureAwait(true);
            _bootstrapClient?.Dispose();
            _bootstrapClient = null;
            if (generation != _hostGeneration)
            {
                return;
            }

            var state = _hostSession.State;
            _health = state.PollHealth;
            _overview.SeedPollHealth(state.PollHealth, state.LastSuccessfulAt);
            if (state.Status == WatchHostConnectionStatus.Failed)
            {
                _refreshState = _refreshState.ApplyFailure(FormatHostFailure(state));
                ApplyProjection();
                return;
            }

            _isApplyingHostSession = false;
            _timer.Start();

            if (refreshOverview)
            {
                await RefreshAsync(WatchBrowseRefreshKind.Reset).ConfigureAwait(true);
            }
        }
        finally
        {
            _isApplyingHostSession = false;
            ApplyProjection();
        }
    }

    private string FormatHostFailure(WatchHostSessionState state) =>
        $"endpoint={state.Endpoint ?? "(unknown)"} kind={state.FailureKind} "
        + $"stage={state.FailureStage ?? state.FailureKind.ToString()} "
        + $"timeoutSeconds={_options.RequestTimeoutSeconds} "
        + $"elapsedMs={(long)(state.FailureElapsed ?? TimeSpan.Zero).TotalMilliseconds} "
        + $"correlationId={state.CorrelationId ?? "(none)"} {state.ErrorMessage}";

    private void ApplyPaneRatio(double demandShare)
    {
        var (demandStar, alertStar) = WatchLayoutPreferences.ToStarHeights(demandShare);
        DemandsRow.Height = new GridLength(demandStar, GridUnitType.Star);
        AlertsRow.Height = new GridLength(alertStar, GridUnitType.Star);
    }

    private void ApplyWindowLayout(WatchWindowLayout layout)
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(layout.WindowWidth, workArea.Width));
        Height = Math.Max(MinHeight, Math.Min(layout.WindowHeight, workArea.Height));
        ApplyPaneRatio(layout.DemandShare);
    }

    /// <summary>
    /// GridSplitter leaves Absolute row heights after a drag; convert back to Star
    /// so window resize/maximize keeps the same Demand/Alert fill ratio.
    /// </summary>
    private void ConvertPaneHeightsToStars()
    {
        var demand = DemandsRow.ActualHeight > 0
            ? DemandsRow.ActualHeight
            : DemandsRow.Height.Value;
        var alert = AlertsRow.ActualHeight > 0
            ? AlertsRow.ActualHeight
            : AlertsRow.Height.Value;
        var total = demand + alert;
        if (total <= 0)
        {
            return;
        }

        ApplyPaneRatio(demand / total);
    }

    private void SaveWindowLayout()
    {
        ConvertPaneHeightsToStars();
        var demand = DemandsRow.Height.Value;
        var alert = AlertsRow.Height.Value;
        var total = demand + alert;
        if (total <= 0 || !DemandsRow.Height.IsStar)
        {
            ApplyPaneRatio(WatchLayoutPreferences.DefaultDemandShare);
            demand = DemandsRow.Height.Value;
            alert = AlertsRow.Height.Value;
            total = demand + alert;
        }

        try
        {
            var restoreBounds = RestoreBounds;
            var width = WindowState == WindowState.Normal
                ? Width
                : restoreBounds.Width;
            var height = WindowState == WindowState.Normal
                ? Height
                : restoreBounds.Height;
            width = double.IsFinite(width) && width > 0 ? width : WatchWindowLayout.Default.WindowWidth;
            height = double.IsFinite(height) && height > 0 ? height : WatchWindowLayout.Default.WindowHeight;
            WatchLayoutPreferences.Save(
                _layoutPreferencesPath,
                new WatchWindowLayout(width, height, demand / total));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _telemetryIoDiagnostics.Record("watch-layout-preferences", ex);
        }
    }

    private void OnPanesSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ApplyPaneRatio(WatchLayoutPreferences.DefaultDemandShare);
        e.Handled = true;
    }

    private void OnDemandStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isResettingDemandViews
            || _visibleDemands is null
            || _goneDemands is null
            || DemandStatusTabs is null)
        {
            return;
        }

        var previous = SessionFor(_activeDemandViewKind);
        previous.UpdateDraft(ReadDemandDraft(_activeDemandViewKind));
        previous.CancelActive(userInitiated: false);
        _activeDemandViewKind = DemandStatusTabs.SelectedIndex == 1
            ? WatchDemandViewKind.Gone
            : WatchDemandViewKind.Visible;
        var current = ActiveDemandSession;
        ApplyDemandDraftToControls(current.State.Draft);
        var refreshImmediately = _autoRefresh.Activate(DemandRefreshView);
        SyncAutoRefreshControls();
        ApplyProjection();
        if (IsLoaded && DemandsPage.Visibility == Visibility.Visible
            && current.State.LastSuccessfulAt is null)
        {
            _ = RunDemandOperationAsync(current.LoadInitialAsync);
        }
        else if (IsLoaded
                 && DemandsPage.Visibility == Visibility.Visible
                 && refreshImmediately)
        {
            _ = RunDemandOperationAsync(current.RefreshCurrentAsync);
        }
    }

    private void SaveConnectionPreferences(WatchHostSettings settings)
    {
        try
        {
            WatchConnectionPreferencesStore.Save(
                _connectionPreferencesPath,
                new WatchConnectionPreferences(
                    settings.BaseUrl,
                    settings.RequestTimeoutSeconds,
                    WatchCredentialReference.ExternalConfiguration));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _telemetryIoDiagnostics.Record("watch-connection-preferences", ex);
        }
    }

    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        ApplyWindowLayout(WatchWindowLayout.Default);
        SaveWindowLayout();
        SettingsValidationText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "WatchSecondaryTextBrush");
        SettingsValidationText.Text = "已恢复默认窗口大小和详情分隔位置。";
    }

    private void ResetDemandTabsToVisible()
    {
        _isResettingDemandViews = true;
        try
        {
            _activeDemandViewKind = WatchDemandViewKind.Visible;
            DemandStatusTabs.SelectedIndex = 0;
        }
        finally
        {
            _isResettingDemandViews = false;
        }

        ApplyDemandDraftToControls(_visibleDemands.State.Draft);
    }

    private void OnDemandsSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (WatchDemandBrowseQuery.SortToken(e.Column.Header?.ToString()) is null)
        {
            return;
        }

        _ = RunDemandOperationAsync(
            token => ActiveDemandSession.ApplySortAsync(e.Column.Header?.ToString(), token));
    }

    private void OnDemandSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncDemandSelectionFromGrid();
    }

    private void OnDemandSelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        SyncDemandSelectionFromGrid();
    }

    private WatchDemandDto? SyncDemandSelectionFromGrid()
    {
        if (_isApplyingDemandProjection)
        {
            return null;
        }

        var selected = ResolveCurrentDemand();
        ActiveDemandSession.SelectDemand(selected?.DemandId);
        ApplyDemandDetails(selected);
        return selected;
    }

    private WatchDemandDto? ResolveCurrentDemand() =>
        DemandsGrid.CurrentCell.Item as WatchDemandDto
        ?? DemandsGrid.SelectedItem as WatchDemandDto
        ?? DemandsGrid.SelectedCells.FirstOrDefault().Item as WatchDemandDto;

    private void OnDemandsDoubleClick(object sender, MouseButtonEventArgs e) =>
        ShowSelectedDemandDetails();

    private void OnDemandsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ShowSelectedDemandDetails();
        }
    }

    private void OnDemandViewDetailsClick(object sender, RoutedEventArgs e) =>
        ShowSelectedDemandDetails();

    private void OnDemandViewDetailsMenu(object sender, RoutedEventArgs e) =>
        ShowSelectedDemandDetails();

    private void ShowSelectedDemandDetails()
    {
        if (SyncDemandSelectionFromGrid() is null)
        {
            return;
        }

        DemandDetailsPanel.BringIntoView();
    }

    private void OnDemandCopyIdMenu(object sender, RoutedEventArgs e)
    {
        if (ResolveCurrentDemand() is { } demand)
        {
            WatchGridClipboardBehavior.TrySetClipboardText(demand.DemandId);
        }
    }

    private void ApplyDemandDetails(WatchDemandDto? demand)
    {
        DemandViewDetailsButton.IsEnabled = demand is not null;
        DemandDetailsPanel.DataContext = demand is null
            ? null
            : WatchDemandDetails.From(demand);
        DemandDetailsPanel.Visibility = demand is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        DemandDetailsPlaceholder.Visibility = demand is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnDemandQueryClick(object sender, RoutedEventArgs e)
    {
        ActiveDemandSession.UpdateDraft(ReadDemandDraft(_activeDemandViewKind));
        await RunDemandOperationAsync(ActiveDemandSession.SubmitDraftAsync).ConfigureAwait(true);
    }

    private async void OnDemandResetClick(object sender, RoutedEventArgs e)
    {
        var outcome = await RunDemandOperationAsync(ActiveDemandSession.ResetAsync).ConfigureAwait(true);
        if (outcome == WatchDemandBrowseOutcome.Succeeded)
        {
            ApplyDemandDraftToControls(ActiveDemandSession.State.Draft);
        }
    }

    private async void OnDemandRefreshClick(object sender, RoutedEventArgs e) =>
        await RunDemandOperationAsync(ActiveDemandSession.RefreshCurrentAsync).ConfigureAwait(true);

    private void OnDemandCancelClick(object sender, RoutedEventArgs e)
    {
        _autoRefresh.CancelCurrentRequest(DemandRefreshView);
        ActiveDemandSession.CancelActive(userInitiated: true);
        ApplyProjection();
    }

    private async void OnDemandPreviousClick(object sender, RoutedEventArgs e) =>
        await RunDemandOperationAsync(ActiveDemandSession.MovePreviousAsync).ConfigureAwait(true);

    private async void OnDemandNextClick(object sender, RoutedEventArgs e) =>
        await RunDemandOperationAsync(ActiveDemandSession.MoveNextAsync).ConfigureAwait(true);

    private void OnAlertsSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (WatchAlertBrowseQuery.SortToken(e.Column.Header?.ToString()) is null)
        {
            return;
        }

        _ = RunAlertOperationAsync(
            token => _alerts.ApplySortAsync(e.Column.Header?.ToString(), token));
    }

    private void OnAlertSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingAlertProjection)
        {
            return;
        }

        _alerts.SelectAlert((AlertsGrid.SelectedItem as WatchAlertDto)?.AlertId);
    }

    private WatchAlertDraft ReadAlertDraft() => new(
        Active: AlertActivityFilter.SelectedIndex != 1,
        Code: NullIfBlank(AlertCodeFilter.SelectedValue?.ToString()),
        Severity: NullIfBlank(AlertSeverityFilter.SelectedValue?.ToString()),
        LastSeenAtFrom: AlertRangeFromFilter.Text,
        LastSeenAtTo: AlertRangeToFilter.Text);

    private void ApplyAlertDraftToControls(WatchAlertDraft draft)
    {
        AlertActivityFilter.SelectedIndex = draft.Active ? 0 : 1;
        AlertCodeFilter.SelectedValue = draft.Code ?? string.Empty;
        AlertSeverityFilter.SelectedValue = draft.Severity ?? string.Empty;
        AlertRangeFromFilter.Text = draft.LastSeenAtFrom ?? string.Empty;
        AlertRangeToFilter.Text = draft.LastSeenAtTo ?? string.Empty;
    }

    private async void OnAlertQueryClick(object sender, RoutedEventArgs e)
    {
        _alerts.UpdateDraft(ReadAlertDraft());
        await RunAlertOperationAsync(_alerts.SubmitDraftAsync).ConfigureAwait(true);
    }

    private async void OnAlertResetClick(object sender, RoutedEventArgs e)
    {
        var outcome = await RunAlertOperationAsync(_alerts.ResetAsync).ConfigureAwait(true);
        if (outcome == WatchAlertBrowseOutcome.Succeeded)
        {
            ApplyAlertDraftToControls(_alerts.State.Draft);
        }
    }

    private async void OnAlertRefreshClick(object sender, RoutedEventArgs e) =>
        await RunAlertOperationAsync(_alerts.RefreshCurrentAsync).ConfigureAwait(true);

    private void OnAlertCancelClick(object sender, RoutedEventArgs e)
    {
        _autoRefresh.CancelCurrentRequest(WatchRefreshView.Alerts);
        _alerts.CancelActive(userInitiated: true);
        ApplyProjection();
    }

    private async void OnAlertPreviousClick(object sender, RoutedEventArgs e) =>
        await RunAlertOperationAsync(_alerts.MovePreviousAsync).ConfigureAwait(true);

    private async void OnAlertNextClick(object sender, RoutedEventArgs e) =>
        await RunAlertOperationAsync(_alerts.MoveNextAsync).ConfigureAwait(true);

    private WatchDemandDraft ReadDemandDraft(WatchDemandViewKind viewKind) => new(
        TaskType: NullIfBlank(DemandTaskTypeFilter.SelectedValue?.ToString()),
        Sublot: DemandSublotFilter.Text,
        DemandId: DemandIdFilter.Text,
        DatesFrom: viewKind == WatchDemandViewKind.Visible ? DemandRangeFromFilter.Text : null,
        DatesTo: viewKind == WatchDemandViewKind.Visible ? DemandRangeToFilter.Text : null,
        GoneAtFrom: viewKind == WatchDemandViewKind.Gone ? DemandRangeFromFilter.Text : null,
        GoneAtTo: viewKind == WatchDemandViewKind.Gone ? DemandRangeToFilter.Text : null);

    private void ApplyDemandDraftToControls(WatchDemandDraft draft)
    {
        DemandTaskTypeFilter.SelectedValue = draft.TaskType ?? string.Empty;
        DemandSublotFilter.Text = draft.Sublot ?? string.Empty;
        DemandIdFilter.Text = draft.DemandId ?? string.Empty;
        DemandRangeFromFilter.Text = _activeDemandViewKind == WatchDemandViewKind.Gone
            ? draft.GoneAtFrom ?? string.Empty
            : draft.DatesFrom ?? string.Empty;
        DemandRangeToFilter.Text = _activeDemandViewKind == WatchDemandViewKind.Gone
            ? draft.GoneAtTo ?? string.Empty
            : draft.DatesTo ?? string.Empty;
    }

    private async Task<WatchDemandBrowseOutcome> RunDemandOperationAsync(
        Func<CancellationToken, Task<WatchDemandBrowseOutcome>> operation)
    {
        var refreshView = DemandRefreshView;
        var refreshRequest = _autoRefresh.BeginRequest(refreshView);
        try
        {
            var generation = _hostGeneration;
            var session = ActiveDemandSession;
            var pending = operation(CancellationToken.None);
            ApplyProjection();
            var outcome = await pending.ConfigureAwait(true);
            if (generation != _hostGeneration || !ReferenceEquals(session, ActiveDemandSession))
            {
                return WatchDemandBrowseOutcome.Superseded;
            }

            var now = UtcNow();
            if (outcome == WatchDemandBrowseOutcome.Succeeded)
            {
                SetActiveDemandRefreshState(ActiveDemandRefreshState.ApplySuccess(now));
                RecordConnectionEvent(_connectionRecorder.ObserveSuccess(now));
            }
            else if (outcome == WatchDemandBrowseOutcome.Failed && session.State.Failure is { } failure)
            {
                var (failureMessage, endpoint, stage, elapsed, correlationId) = FormatBrowseFailure(
                    failure,
                    "/api/demands");
                var message = failureMessage;
                SetActiveDemandRefreshState(ActiveDemandRefreshState.ApplyFailure(message));
                RecordConnectionEvent(_connectionRecorder.ObserveFailure(
                    now,
                    endpoint,
                    stage,
                    elapsed,
                    _options.RequestTimeoutSeconds,
                    message,
                    correlationId));
            }

            ApplyProjection();
            return outcome;
        }
        finally
        {
            _autoRefresh.EndRequest(refreshView, refreshRequest);
        }
    }

    private async Task<WatchAlertBrowseOutcome> RunAlertOperationAsync(
        Func<CancellationToken, Task<WatchAlertBrowseOutcome>> operation)
    {
        var refreshRequest = _autoRefresh.BeginRequest(WatchRefreshView.Alerts);
        try
        {
            var generation = _hostGeneration;
            var session = _alerts;
            var pending = operation(CancellationToken.None);
            ApplyProjection();
            var outcome = await pending.ConfigureAwait(true);
            if (generation != _hostGeneration || !ReferenceEquals(session, _alerts))
            {
                return WatchAlertBrowseOutcome.Superseded;
            }

            var now = UtcNow();
            if (outcome == WatchAlertBrowseOutcome.Succeeded)
            {
                _alertRefreshState = _alertRefreshState.ApplySuccess(now);
                RecordConnectionEvent(_connectionRecorder.ObserveSuccess(now));
            }
            else if (outcome == WatchAlertBrowseOutcome.Failed && session.State.Failure is { } failure)
            {
                var (failureMessage, endpoint, stage, elapsed, correlationId) = FormatBrowseFailure(
                    failure,
                    "/api/alerts");
                var message = failureMessage;
                _alertRefreshState = _alertRefreshState.ApplyFailure(message);
                RecordConnectionEvent(_connectionRecorder.ObserveFailure(
                    now,
                    endpoint,
                    stage,
                    elapsed,
                    _options.RequestTimeoutSeconds,
                    message,
                    correlationId));
            }

            ApplyProjection();
            return outcome;
        }
        finally
        {
            _autoRefresh.EndRequest(WatchRefreshView.Alerts, refreshRequest);
        }
    }

    private (string Message, string Endpoint, string Stage, TimeSpan Elapsed, string? CorrelationId)
        FormatBrowseFailure(Exception failure, string defaultEndpoint)
    {
        if (failure is WatchEndpointFetchException endpointFailure)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            return (
                endpointFailure.FormatForBanner(_options.RequestTimeoutSeconds, correlationId),
                endpointFailure.Endpoint,
                endpointFailure.Stage,
                endpointFailure.Elapsed,
                correlationId);
        }

        if (failure is WatchHostQueryException hostFailure)
        {
            var stage = hostFailure.Stage ?? hostFailure.Kind.ToString();
            var message = $"endpoint={hostFailure.Endpoint} kind={hostFailure.Kind} "
                + $"stage={stage} timeoutSeconds={_options.RequestTimeoutSeconds} "
                + $"elapsedMs={(long)hostFailure.Elapsed.TotalMilliseconds} "
                + $"correlationId={hostFailure.CorrelationId} {hostFailure.Message}";
            return (
                message,
                hostFailure.Endpoint,
                stage,
                hostFailure.Elapsed,
                hostFailure.CorrelationId);
        }

        return (
            $"endpoint={defaultEndpoint} stage=HTTP_ERROR timeoutSeconds={_options.RequestTimeoutSeconds} "
            + $"elapsedMs=0 {failure.Message}",
            defaultEndpoint,
            "HTTP_ERROR",
            TimeSpan.Zero,
            null);
    }

    private async Task RefreshAsync(WatchBrowseRefreshKind kind)
    {
        if (PrimaryNavigation.SelectedIndex == 1)
        {
            await RunDemandOperationAsync(ActiveDemandSession.RefreshCurrentAsync).ConfigureAwait(true);
            return;
        }

        if (PrimaryNavigation.SelectedIndex == 2)
        {
            await RunAlertOperationAsync(_alerts.RefreshCurrentAsync).ConfigureAwait(true);
            return;
        }

        if (PrimaryNavigation.SelectedIndex != 0)
        {
            return;
        }

        var generation = _hostGeneration;
        var browse = _browse;
        var overview = _overview;
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _refreshCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        var admitted = false;
        var refreshingOverview = false;
        long? autoRefreshRequest = null;
        try
        {
            admitted = await _refreshAdmission.WaitAsync(kind, cancellation.Token).ConfigureAwait(true);
            if (!admitted)
            {
                return;
            }

            autoRefreshRequest = _autoRefresh.BeginRequest(WatchRefreshView.Overview);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var query = WatchDemandBrowseQuery.Default;
            var now = UtcNow();

            if (PrimaryNavigation.SelectedIndex == 0
                && kind is not WatchBrowseRefreshKind.Append
                && kind is not WatchBrowseRefreshKind.AppendAlerts)
            {
                refreshingOverview = true;
                _overviewNotice = null;
                _isOverviewRefreshing = true;
                ApplyProjection();
                await overview.RefreshAsync(
                        _ => Dispatcher.InvokeAsync(
                                ApplyProjection,
                                DispatcherPriority.DataBind)
                            .Task,
                        cancellation.Token)
                    .ConfigureAwait(true);
                if (generation != _hostGeneration || !ReferenceEquals(overview, _overview))
                {
                    return;
                }

                var overviewSnapshot = overview.LastSnapshot;
                if (overviewSnapshot is not null)
                {
                    if (overviewSnapshot.PollHealthSucceeded)
                    {
                        _health = overviewSnapshot.PollHealth;
                    }

                    ApplySnapshotRefreshState(overviewSnapshot, now);
                }

                ApplyProjection();
                return;
            }

            await browse.RefreshAsync(kind, query, cancellation.Token).ConfigureAwait(true);
            if (generation != _hostGeneration || !ReferenceEquals(browse, _browse))
            {
                return;
            }

            var snapshot = browse.LastSnapshot;

            if (kind is WatchBrowseRefreshKind.Append or WatchBrowseRefreshKind.AppendAlerts
                && !browse.LastRefreshIncludedSnapshot)
            {
                // Partial page success must not clear a still-failing endpoint banner.
                _refreshState = _refreshState.ApplyPartialSuccess(now);
                if (_refreshState.FetchError is null)
                {
                    RecordConnectionEvent(_connectionRecorder.ObserveSuccess(now));
                }

                ApplyProjection();
                return;
            }

            if (snapshot is null)
            {
                ApplyProjection();
                return;
            }

            if (snapshot.PollHealthSucceeded)
            {
                _health = snapshot.PollHealth;
            }

            ApplySnapshotRefreshState(snapshot, now);

            ApplyProjection();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A navigation, newer refresh, explicit cancel, or Host replacement owns the UI now.
        }
        catch (WatchEndpointFetchException ex)
        {
            var now = UtcNow();
            var failureMessage = ex.FormatForBanner(
                _options.RequestTimeoutSeconds,
                Guid.NewGuid().ToString("N"));
            var message = failureMessage;
            _refreshState = _refreshState.ApplyFailure(message);
            RecordConnectionEvent(_connectionRecorder.ObserveFailure(
                now,
                endpoint: ex.Endpoint,
                stage: ex.Stage,
                elapsed: ex.Elapsed,
                timeoutSeconds: _options.RequestTimeoutSeconds,
                message: message));
            ApplyProjection();
        }
        catch (Exception ex)
        {
            var now = UtcNow();
            var failureMessage =
                $"endpoint=(unknown) stage=HTTP_ERROR timeoutSeconds={_options.RequestTimeoutSeconds} elapsedMs=0 {ex.Message}";
            var message = failureMessage;
            _refreshState = _refreshState.ApplyFailure(message);
            RecordConnectionEvent(_connectionRecorder.ObserveFailure(
                now,
                endpoint: "(unknown)",
                stage: "HTTP_ERROR",
                elapsed: TimeSpan.Zero,
                timeoutSeconds: _options.RequestTimeoutSeconds,
                message: message));
            ApplyProjection();
        }
        finally
        {
            if (admitted)
            {
                _refreshAdmission.Release();
            }

            if (autoRefreshRequest is { } refreshRequest)
            {
                _autoRefresh.EndRequest(WatchRefreshView.Overview, refreshRequest);
            }

            var ownedCurrentRequest = ReferenceEquals(
                Interlocked.CompareExchange(ref _refreshCancellation, null, cancellation),
                cancellation);
            cancellation.Dispose();
            if (refreshingOverview
                && ownedCurrentRequest
                && generation == _hostGeneration
                && ReferenceEquals(overview, _overview))
            {
                _isOverviewRefreshing = false;
                ApplyProjection();
            }
        }
    }

    private void ApplySnapshotRefreshState(WatchSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.AllEndpointsSucceeded)
        {
            _refreshState = _refreshState.ApplySuccess(now);
            RecordConnectionEvent(_connectionRecorder.ObserveSuccess(now));
            return;
        }

        if (snapshot.FetchError is null)
        {
            return;
        }

        // Keep last-success clock when any endpoint still works.
        var failureMessage = snapshot.FetchError.Contains("timeoutSeconds=", StringComparison.Ordinal)
            ? snapshot.FetchError
            : $"{snapshot.FetchError} timeoutSeconds={_options.RequestTimeoutSeconds}";
        _refreshState = snapshot.DemandsSucceeded || snapshot.AlertsSucceeded || snapshot.PollHealthSucceeded
            ? _refreshState.ApplyPartialSuccess(now).ApplyFailure(failureMessage)
            : _refreshState.ApplyFailure(failureMessage);
        RecordConnectionEvent(_connectionRecorder.ObserveFailure(
            now,
            endpoint: snapshot.FailedEndpoint ?? "(unknown)",
            stage: snapshot.FailedStage ?? "HTTP_ERROR",
            elapsed: snapshot.FailedElapsed ?? TimeSpan.Zero,
            timeoutSeconds: _options.RequestTimeoutSeconds,
            message: failureMessage,
            correlationId: snapshot.CorrelationId));
    }

    private void RecordConnectionEvent(WatchConnectionEvent? connectionEvent)
    {
        if (connectionEvent is null)
        {
            return;
        }

        try
        {
            _connectionJournal.Append(connectionEvent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _telemetryIoDiagnostics.Record("watch-connection", ex);
        }
    }

    private void ApplyProjection()
    {
        var demand = ActiveDemandSession.State;
        var alert = _alerts.State;
        _isApplyingDemandProjection = true;
        try
        {
            DemandsGrid.ItemsSource = demand.Items;
            DemandsGrid.SelectedItem = demand.SelectedDemandId is null
                ? null
                : demand.Items.FirstOrDefault(item => string.Equals(
                    item.DemandId,
                    demand.SelectedDemandId,
                    StringComparison.Ordinal));
        }
        finally
        {
            _isApplyingDemandProjection = false;
        }
        ApplyDemandDetails(demand.SelectedDemand);
        _isApplyingAlertProjection = true;
        try
        {
            AlertsGrid.ItemsSource = alert.Items;
            AlertsGrid.SelectedItem = alert.SelectedAlertId is null
                ? null
                : alert.Items.FirstOrDefault(item => string.Equals(
                    item.AlertId,
                    alert.SelectedAlertId,
                    StringComparison.Ordinal));
        }
        finally
        {
            _isApplyingAlertProjection = false;
        }
        DemandPreviousButton.IsEnabled = demand.CanMovePrevious;
        DemandNextButton.IsEnabled = demand.CanMoveNext;
        DemandRefreshButton.IsEnabled = demand.LastSuccessfulAt is not null;
        DemandCancelButton.IsEnabled = demand.IsRefreshing;
        DemandQueryButton.IsEnabled = true;
        DemandResetButton.IsEnabled = true;
        DemandBusyText.Visibility = demand.IsRefreshing ? Visibility.Visible : Visibility.Collapsed;
        DemandValidationText.Text = demand.ValidationError ?? string.Empty;
        DemandNoticeText.Text = demand.Notice ?? string.Empty;
        DemandPageText.Text = $"第 {demand.PageNumber} 页";
        DemandCommittedQueryText.Text = FormatCommittedDemandQuery(demand.CommittedQuery);
        var isGone = _activeDemandViewKind == WatchDemandViewKind.Gone;
        DemandModeText.Text = isGone
            ? "GONE · 独立服务端单页窗口 · 每页固定 100 行"
            : "VISIBLE · 独立服务端单页窗口 · 每页固定 100 行";
        DemandRangeFromLabel.Text = isGone ? "GoneAt 起始（含时区）" : "DATES 起始（含时区）";
        DemandRangeToLabel.Text = isGone ? "GoneAt 结束（含时区）" : "DATES 结束（含时区）";
        AlertPreviousButton.IsEnabled = alert.CanMovePrevious;
        AlertNextButton.IsEnabled = alert.CanMoveNext;
        AlertRefreshButton.IsEnabled = alert.LastSuccessfulAt is not null;
        AlertCancelButton.IsEnabled = alert.IsRefreshing;
        AlertQueryButton.IsEnabled = true;
        AlertResetButton.IsEnabled = true;
        AlertBusyText.Visibility = alert.IsRefreshing ? Visibility.Visible : Visibility.Collapsed;
        AlertValidationText.Text = alert.ValidationError ?? string.Empty;
        AlertNoticeText.Text = alert.Notice ?? string.Empty;
        AlertPageText.Text = $"第 {alert.PageNumber} 页";
        AlertCommittedQueryText.Text = FormatCommittedAlertQuery(alert.CommittedQuery);
        RowCountText.Text = demand.LastSuccessfulAt is null
            ? "尚无成功窗口"
            : demand.Items.Count == 0
                ? $"当前查询无结果 · 最近成功 {demand.LastSuccessfulAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : demand.HasMore
                    ? $"当前页 {demand.Items.Count} 行 · 还有下一页 · 最近成功 {demand.LastSuccessfulAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                    : $"当前页 {demand.Items.Count} 行 · 已到末页 · 最近成功 {demand.LastSuccessfulAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        AlertCountText.Text = alert.LastSuccessfulAt is null
            ? "尚无成功窗口"
            : alert.Items.Count == 0
                ? $"当前查询无结果 · 最近成功 {alert.LastSuccessfulAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : alert.HasMore
                    ? $"当前页 {alert.Items.Count} 行 · 还有下一页 · 最近成功 {alert.LastSuccessfulAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                    : $"当前页 {alert.Items.Count} 行 · 已到末页 · 最近成功 {alert.LastSuccessfulAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

        var now = UtcNow();
        var pageRefreshState = PrimaryNavigation.SelectedIndex switch
        {
            1 => ActiveDemandRefreshState,
            2 => _alertRefreshState,
            _ => _refreshState,
        };
        var activeAlertEvidence = _overview.State.Alerts.Items;
        var banner = WatchBannerProjection.Project(
            _bannerHold,
            _health,
            pageRefreshState.FetchError,
            activeAlertEvidence,
            now);
        _bannerHold = banner.HoldState;

        ErrorBanner.Visibility = banner.ShowError ? Visibility.Visible : Visibility.Collapsed;
        var bannerError = banner.ErrorMessage ?? string.Empty;
        ErrorBannerText.Text = banner.ShowError
            ? pageRefreshState.FetchError is null
                ? bannerError
                : pageRefreshState.FormatFailure(bannerError, now)
            : string.Empty;

        WarningBanner.Visibility = banner.ShowWarning ? Visibility.Visible : Visibility.Collapsed;
        WarningBannerText.Text = banner.ShowWarning ? banner.WarningMessage ?? string.Empty : string.Empty;

        var status = WatchStatusBarState.Project(
            _health,
            pageRefreshState,
            activeAlertEvidence,
            _options.BaseUrl,
            now,
            recoveryMessage: banner.RecoveryMessage);
        StatusBarText.Text = status.CompactLine;
        StatusBarText.ToolTip = status.Tooltip;
        CurrentHostContextText.Text = $"当前 Host：{_options.BaseUrl}";
        PageRefreshContextText.Text = CurrentRefreshView is { } currentView
            ? FormatPageRefreshContext(currentView, pageRefreshState, now)
            : "设置页 · 自动刷新已暂停";

        var hostState = _hostSession.State;
        var overview = WatchOverviewProjection.Project(hostState, _overview.State);
        OverviewConclusionText.Text = _isOverviewRefreshing ? "○ 刷新中" : overview.ConclusionText;
        OverviewHostText.Text = overview.HostText;
        OverviewPollHealthText.Text = overview.PollHealthText;
        OverviewAlertText.Text = overview.AlertsText;
        OverviewAlertText.Foreground = overview.HasActiveError
            ? System.Windows.Media.Brushes.DarkRed
            : System.Windows.Media.Brushes.Black;
        OverviewDemandText.Text = overview.DemandsText;
        OverviewBusyText.Visibility = _isOverviewRefreshing ? Visibility.Visible : Visibility.Collapsed;
        OverviewNoticeText.Text = _overviewNotice ?? string.Empty;
        OverviewRefreshButton.IsEnabled = !_isOverviewRefreshing;
        OverviewCancelButton.IsEnabled = _isOverviewRefreshing;

        var needsHoldTick = banner.ShowError
            || banner.ShowWarning
            || banner.HoldState.RecoveredAt is not null
            || banner.HoldState.ErrorClearedAt is not null
            || banner.HoldState.WarningClearedAt is not null;
        if (needsHoldTick)
        {
            if (!_bannerHoldTimer.IsEnabled)
            {
                _bannerHoldTimer.Start();
            }
        }
        else if (_bannerHoldTimer.IsEnabled)
        {
            _bannerHoldTimer.Stop();
        }

        ApplyDemandSortGlyphs();
        ApplyAlertSortGlyphs();
        SyncOpenAlertDetails();
    }

    private string FormatPageRefreshContext(
        WatchRefreshView view,
        WatchRefreshState refreshState,
        DateTimeOffset now)
    {
        var setting = _autoRefresh.Preferences.For(view);
        var viewName = view switch
        {
            WatchRefreshView.Overview => "概览",
            WatchRefreshView.Visible => "VISIBLE",
            WatchRefreshView.Gone => "GONE",
            WatchRefreshView.Alerts => "IngestAlert",
            _ => view.ToString(),
        };
        var auto = setting.Enabled
            ? $"自动刷新=开启({setting.IntervalSeconds}s)"
            : $"自动刷新=关闭({setting.IntervalSeconds}s)";
        return $"当前视图={viewName} · {auto} · {refreshState.FormatWatchRefreshLine(now)}";
    }

    private static string FormatCommittedDemandQuery(WatchDemandBrowseQuery query)
    {
        var filters = new List<string>
        {
            $"已提交：status={query.Status}",
            $"sortBy={query.SortBy}",
            $"direction={query.Direction}",
            "limit=100",
        };
        if (!string.IsNullOrWhiteSpace(query.TaskType))
        {
            filters.Add($"TASK_TYPE={query.TaskType}");
        }

        if (!string.IsNullOrWhiteSpace(query.Sublot))
        {
            filters.Add($"SUBLOT={query.Sublot}");
        }

        if (!string.IsNullOrWhiteSpace(query.DemandId))
        {
            filters.Add($"DemandId={query.DemandId}");
        }

        if (query.DatesFrom is not null)
        {
            filters.Add($"DATES from {query.DatesFrom:O}");
        }

        if (query.DatesTo is not null)
        {
            filters.Add($"DATES to {query.DatesTo:O}");
        }

        if (query.GoneAtFrom is not null)
        {
            filters.Add($"GoneAt from {query.GoneAtFrom:O}");
        }

        if (query.GoneAtTo is not null)
        {
            filters.Add($"GoneAt to {query.GoneAtTo:O}");
        }

        return string.Join(" · ", filters);
    }

    private static string FormatCommittedAlertQuery(WatchAlertBrowseQuery query)
    {
        var filters = new List<string>
        {
            $"已提交：active={query.Active?.ToString().ToLowerInvariant()}",
            "limit=100",
        };
        if (!string.IsNullOrWhiteSpace(query.Code))
        {
            filters.Add($"Code={query.Code}");
        }

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            filters.Add($"Severity={query.Severity}");
        }

        if (query.From is not null)
        {
            filters.Add($"LastSeenAt from {query.From:O}");
        }

        if (query.To is not null)
        {
            filters.Add($"LastSeenAt to {query.To:O}");
        }

        filters.Add(query.SortBy is null
            ? "Host 默认优先级排序"
            : $"sortBy={query.SortBy} · direction={query.Direction}");
        return string.Join(" · ", filters);
    }

    private void OnAlertsDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedAlertDetail();

    private void OnAlertsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenSelectedAlertDetail();
        }
    }

    private void OnAlertsPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject origin
            && ItemsControl.ContainerFromElement(AlertsGrid, origin) is DataGridRow row)
        {
            AlertsGrid.SelectedItem = row.Item;
            row.Focus();
        }
    }

    private void OnAlertViewDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WatchAlertDto alert })
        {
            OpenAlertDetail(alert);
        }
    }

    private void OnAlertViewDetailsMenu(object sender, RoutedEventArgs e) => OpenSelectedAlertDetail();

    private void OpenSelectedAlertDetail()
    {
        if (AlertsGrid.SelectedItem is WatchAlertDto alert)
        {
            OpenAlertDetail(alert);
        }
    }

    private void OpenAlertDetail(WatchAlertDto alert)
    {
        if (!string.IsNullOrWhiteSpace(alert.AlertId)
            && _openAlertDetails.TryGetValue(alert.AlertId, out var existing)
            && existing.IsVisible)
        {
            existing.ApplyUpdate(alert);
            existing.Activate();
            return;
        }

        AlertDetailWindow? window = null;
        window = new AlertDetailWindow(
            AlertDetailViewModel.From(alert),
            locateDemand: target => LocateDemandFromAlert(target, window),
            searchBusinessKey: businessKey => SearchDemandFromAlert(businessKey, window),
            loadDemand: _client.FetchDemandByIdAsync);
        window.Owner = this;
        window.Closed += (_, _) => UnregisterAlertDetail(window);

        if (!string.IsNullOrWhiteSpace(alert.AlertId))
        {
            _openAlertDetails[alert.AlertId] = window;
        }
        else
        {
            _openAlertDetailsWithoutId.Add(new WeakReference<AlertDetailWindow>(window));
        }

        window.Show();
    }

    private void UnregisterAlertDetail(AlertDetailWindow window)
    {
        if (!string.IsNullOrWhiteSpace(window.AlertId)
            && _openAlertDetails.TryGetValue(window.AlertId, out var mapped)
            && ReferenceEquals(mapped, window))
        {
            _openAlertDetails.Remove(window.AlertId);
        }

        _openAlertDetailsWithoutId.RemoveAll(reference =>
            !reference.TryGetTarget(out var target) || ReferenceEquals(target, window));
    }

    private void SyncOpenAlertDetails()
    {
        var byId = _alerts.State.Items
            .Where(a => !string.IsNullOrWhiteSpace(a.AlertId))
            .GroupBy(a => a.AlertId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var (alertId, window) in _openAlertDetails.ToList())
        {
            if (!window.IsVisible)
            {
                _openAlertDetails.Remove(alertId);
                continue;
            }

            if (byId.TryGetValue(alertId, out var live))
            {
                window.ApplyUpdate(live);
            }
            else
            {
                window.MarkHistorical();
            }
        }
    }

    private async void LocateDemandFromAlert(AlertDemandTarget target, AlertDetailWindow? window)
    {
        window?.SetLocateHint(null);
        var navigator = new AlertDemandNavigator(_client, _visibleDemands, _goneDemands);
        var result = await navigator.LocateExactAsync(target).ConfigureAwait(true);
        CompleteAlertDemandNavigation(result, window);
    }

    private async void SearchDemandFromAlert(
        TransportDemandKey businessKey,
        AlertDetailWindow? window)
    {
        window?.SetLocateHint(null);
        var navigator = new AlertDemandNavigator(_client, _visibleDemands, _goneDemands);
        var result = await navigator.SearchBusinessKeyAsync(businessKey).ConfigureAwait(true);
        CompleteAlertDemandNavigation(result, window);
    }

    private void CompleteAlertDemandNavigation(
        AlertDemandNavigationResult result,
        AlertDetailWindow? window)
    {
        if (result.Outcome is AlertDemandNavigationOutcome.Canceled
            or AlertDemandNavigationOutcome.Superseded)
        {
            return;
        }

        if (result.Outcome != AlertDemandNavigationOutcome.Succeeded
            || result.ViewKind is null)
        {
            var message = result.Message ?? "TransportDemand 导航失败。";
            window?.SetLocateHint(message);
            MessageBox.Show(
                this,
                message,
                "TransportDemand 导航",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var targetViewKind = result.ViewKind.Value;
        var targetSession = SessionFor(targetViewKind);
        var previousSession = ActiveDemandSession;
        if (!ReferenceEquals(previousSession, targetSession))
        {
            previousSession.UpdateDraft(ReadDemandDraft(_activeDemandViewKind));
            previousSession.CancelActive(userInitiated: false);
        }

        _isResettingDemandViews = true;
        try
        {
            _activeDemandViewKind = targetViewKind;
            DemandStatusTabs.SelectedIndex = targetViewKind == WatchDemandViewKind.Gone ? 1 : 0;
        }
        finally
        {
            _isResettingDemandViews = false;
        }

        PrimaryNavigation.SelectedIndex = 1;
        ApplyDemandDraftToControls(targetSession.State.Draft);
        SetActiveDemandRefreshState(ActiveDemandRefreshState.ApplySuccess(UtcNow()));
        ApplyProjection();
        if (result.Demand is { } demand)
        {
            var selected = targetSession.State.SelectedDemand ?? demand;
            DemandsGrid.SelectedItem = selected;
            DemandsGrid.ScrollIntoView(selected);
            DemandDetailsPanel.BringIntoView();
        }

        window?.SetLocateHint(result.Message);
    }

    private void ApplyDemandSortGlyphs()
    {
        var committed = ActiveDemandSession.State.CommittedQuery;
        foreach (var column in DemandsGrid.Columns)
        {
            var token = WatchDemandBrowseQuery.SortToken(column.Header?.ToString());
            if (token is null)
            {
                column.SortDirection = null;
                continue;
            }

            column.SortDirection = string.Equals(token, committed.SortBy, StringComparison.Ordinal)
                ? (string.Equals(committed.Direction, "asc", StringComparison.Ordinal)
                    ? System.ComponentModel.ListSortDirection.Ascending
                    : System.ComponentModel.ListSortDirection.Descending)
                : null;
        }
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();

    private void ApplyAlertSortGlyphs()
    {
        var committed = _alerts.State.CommittedQuery;
        var alertSort = committed.SortBy;
        var ascending = string.Equals(committed.Direction, "asc", StringComparison.Ordinal);
        foreach (var column in AlertsGrid.Columns)
        {
            var token = WatchAlertBrowseQuery.SortToken(column.Header?.ToString());
            column.SortDirection = token is not null
                && string.Equals(token, alertSort, StringComparison.Ordinal)
                ? (ascending
                    ? System.ComponentModel.ListSortDirection.Ascending
                    : System.ComponentModel.ListSortDirection.Descending)
                : null;
        }
    }

    private WatchDemandSession ActiveDemandSession =>
        SessionFor(_activeDemandViewKind);

    private WatchDemandSession SessionFor(WatchDemandViewKind viewKind) =>
        viewKind == WatchDemandViewKind.Gone ? _goneDemands : _visibleDemands;

    private WatchRefreshState ActiveDemandRefreshState =>
        _activeDemandViewKind == WatchDemandViewKind.Gone
            ? _goneRefreshState
            : _visibleRefreshState;

    private void SetActiveDemandRefreshState(WatchRefreshState state)
    {
        if (_activeDemandViewKind == WatchDemandViewKind.Gone)
        {
            _goneRefreshState = state;
        }
        else
        {
            _visibleRefreshState = state;
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
