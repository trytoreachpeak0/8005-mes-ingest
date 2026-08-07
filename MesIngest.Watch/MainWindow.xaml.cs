using System.IO;
using System.Windows.Input;
using System.Windows.Threading;

namespace MesIngest.Watch;

internal partial class MainWindow : Window
{
    private static readonly TimeSpan DemandIdDebounce = TimeSpan.FromMilliseconds(300);

    private IWatchReadQueries _client;
    private WatchBrowseSession _browse;
    private WatchOverviewSession _overview;
    private readonly WatchHostSession _hostSession;
    private readonly WatchOptions _options;
    private readonly WatchConnectionEventRecorder _connectionRecorder;
    private readonly WatchConnectionEventJournal _connectionJournal;
    private readonly WatchTelemetryIoDiagnosticBuffer _telemetryIoDiagnostics;
    private readonly string _layoutPreferencesPath;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _demandIdDebounceTimer;
    private readonly DispatcherTimer _bannerHoldTimer;
    private readonly WatchRefreshAdmission _refreshAdmission = new();
    private readonly Dictionary<string, AlertDetailWindow> _openAlertDetails = new(StringComparer.Ordinal);
    private readonly List<WeakReference<AlertDetailWindow>> _openAlertDetailsWithoutId = [];

    private WatchPollHealthDto? _health;
    private bool _isOverviewRefreshing;
    private WatchRefreshState _refreshState = WatchRefreshState.Empty;
    private WatchBannerHoldState _bannerHold = WatchBannerHoldState.Empty;
    private string _sortBy = "dates";
    private string _direction = "desc";
    private string? _appliedDemandId;
    private CancellationTokenSource? _refreshCancellation;
    private long _hostGeneration;
    private bool _isApplyingHost;
    private MesIngestApiClient? _bootstrapClient;

    public MainWindow(
        MesIngestApiClient client,
        WatchOptions options,
        WatchConnectionEventJournal? connectionJournal = null,
        WatchConnectionEventRecorder? connectionRecorder = null,
        string? layoutPreferencesPath = null,
        WatchTelemetryIoDiagnosticBuffer? telemetryIoDiagnostics = null,
        Func<WatchHostSettings, IWatchHostQueryAdapter>? hostAdapterFactory = null)
    {
        InitializeComponent();
        WatchGridClipboardBehavior.Attach(DemandsGrid);
        WatchGridClipboardBehavior.Attach(AlertsGrid);
        _client = client;
        _bootstrapClient = client;
        _browse = new WatchBrowseSession(client);
        _overview = new WatchOverviewSession(client);
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

        ApplyPaneRatio(WatchLayoutPreferences.LoadDemandShare(_layoutPreferencesPath));
        PanesSplitter.DragCompleted += (_, _) => ConvertPaneHeightsToStars();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_options.RefreshSeconds),
        };
        _timer.Tick += async (_, _) => await RefreshAsync(WatchBrowseRefreshKind.PreserveWindow).ConfigureAwait(true);

        _bannerHoldTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _bannerHoldTimer.Tick += (_, _) => ApplyProjection();

        _demandIdDebounceTimer = new DispatcherTimer { Interval = DemandIdDebounce };
        _demandIdDebounceTimer.Tick += async (_, _) =>
        {
            _demandIdDebounceTimer.Stop();
            var typed = NullIfBlank(FilterDemandId.Text);
            if (!WatchDemandBrowseQuery.IsDemandIdFilterReady(typed))
            {
                // Incomplete hex — keep last applied DemandId; do not block the watch loop.
                return;
            }

            _appliedDemandId = typed;
            await RefreshAsync(WatchBrowseRefreshKind.Reset).ConfigureAwait(true);
        };

        Loaded += async (_, _) =>
        {
            ApplyDemandSortGlyphs();
            ApplyAlertSortGlyphs();
            await ApplyHostSessionAsync(
                    new WatchHostSettings(
                        _options.BaseUrl,
                        _options.SharedSecret,
                        _options.RequestTimeoutSeconds),
                    refreshOverview: true)
                .ConfigureAwait(true);
            if (_hostSession.State.Status == WatchHostConnectionStatus.Connected)
            {
                _timer.Start();
            }
        };
        Closed += (_, _) =>
        {
            SavePaneRatio();
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _hostSession.Dispose();
            _bootstrapClient?.Dispose();
            _bootstrapClient = null;
            _timer.Stop();
            _bannerHoldTimer.Stop();
            _demandIdDebounceTimer.Stop();
        };
    }

    private void OnPrimaryNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OverviewPage is null || DemandsPage is null || AlertsPage is null || SettingsPage is null)
        {
            return;
        }

        _refreshCancellation?.Cancel();
        var selected = PrimaryNavigation.SelectedIndex;
        OverviewPage.Visibility = selected == 0 ? Visibility.Visible : Visibility.Collapsed;
        DemandsPage.Visibility = selected == 1 ? Visibility.Visible : Visibility.Collapsed;
        AlertsPage.Visibility = selected == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = selected == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded && selected is 1 or 2)
        {
            _ = RefreshAsync(WatchBrowseRefreshKind.Reset);
        }
    }

    private void OnOverviewAlertsClick(object sender, RoutedEventArgs e)
    {
        _browse = new WatchBrowseSession(_hostSession);
        PrimaryNavigation.SelectedIndex = 2;
    }

    private void OnOverviewRefreshClick(object sender, RoutedEventArgs e) =>
        _ = RefreshAsync(WatchBrowseRefreshKind.Reset);

    private void OnOverviewCancelClick(object sender, RoutedEventArgs e) =>
        _refreshCancellation?.Cancel();

    private void OnOverviewDemandsClick(object sender, RoutedEventArgs e)
    {
        _browse = new WatchBrowseSession(_hostSession);
        _sortBy = "dates";
        _direction = "desc";
        _appliedDemandId = null;
        FilterStatus.SelectedIndex = 0;
        FilterTaskType.Clear();
        FilterSublot.Clear();
        FilterDemandId.Clear();
        PrimaryNavigation.SelectedIndex = 1;
    }

    private async void OnApplyHostClick(object sender, RoutedEventArgs e)
    {
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
            await ApplyHostSessionAsync(settings, refreshOverview: true).ConfigureAwait(true);
        }
        finally
        {
            ApplyHostButton.IsEnabled = true;
        }
    }

    private async Task ApplyHostSessionAsync(WatchHostSettings settings, bool refreshOverview)
    {
        _isApplyingHost = true;
        var generation = ++_hostGeneration;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;

        _health = null;
        _refreshState = WatchRefreshState.Empty;
        _bannerHold = WatchBannerHoldState.Empty;
        _sortBy = "dates";
        _direction = "desc";
        _appliedDemandId = null;
        _client = _hostSession;
        _browse = new WatchBrowseSession(_hostSession);
        _overview = new WatchOverviewSession(_hostSession);

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
        FilterTaskType.Clear();
        FilterSublot.Clear();
        FilterDemandId.Clear();
        FilterStatus.SelectedIndex = 0;
        GoneWindowHours.SelectedIndex = 0;
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

            if (refreshOverview)
            {
                await RefreshAsync(WatchBrowseRefreshKind.Reset).ConfigureAwait(true);
            }
        }
        finally
        {
            _isApplyingHost = false;
            ApplyProjection();
        }
    }

    private static string FormatHostFailure(WatchHostSessionState state) =>
        $"endpoint={state.Endpoint ?? "(unknown)"} kind={state.FailureKind} "
        + $"correlationId={state.CorrelationId ?? "(none)"} {state.ErrorMessage}";

    private void ApplyPaneRatio(double demandShare)
    {
        var (demandStar, alertStar) = WatchLayoutPreferences.ToStarHeights(demandShare);
        DemandsRow.Height = new GridLength(demandStar, GridUnitType.Star);
        AlertsRow.Height = new GridLength(alertStar, GridUnitType.Star);
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

    private void SavePaneRatio()
    {
        ConvertPaneHeightsToStars();
        var demand = DemandsRow.Height.Value;
        var alert = AlertsRow.Height.Value;
        var total = demand + alert;
        if (total <= 0 || !DemandsRow.Height.IsStar)
        {
            return;
        }

        WatchLayoutPreferences.SaveDemandShare(_layoutPreferencesPath, demand / total);
    }

    private void OnPanesSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ApplyPaneRatio(WatchLayoutPreferences.DefaultDemandShare);
        e.Handled = true;
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        // ComboBox IsSelected in XAML raises SelectionChanged during InitializeComponent,
        // before later-named controls exist.
        if (!IsLoaded || _isApplyingHost)
        {
            return;
        }

        UpdateGoneWindowVisibility();
        _ = RefreshAsync(WatchBrowseRefreshKind.Reset);
    }

    private void OnDemandIdTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _isApplyingHost)
        {
            return;
        }

        var typed = NullIfBlank(FilterDemandId.Text);
        if (typed is null)
        {
            _demandIdDebounceTimer.Stop();
            _appliedDemandId = null;
            _ = RefreshAsync(WatchBrowseRefreshKind.Reset);
            return;
        }

        _demandIdDebounceTimer.Stop();
        _demandIdDebounceTimer.Start();
    }

    private void OnDemandsSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var current = WatchDemandBrowseQuery.Default with
        {
            SortBy = _sortBy,
            Direction = _direction,
        };
        if (!current.TryApplySort(e.Column.Header?.ToString(), out var next))
        {
            return;
        }

        _sortBy = next.SortBy;
        _direction = next.Direction;

        ApplyDemandSortGlyphs();
        _ = RefreshAsync(WatchBrowseRefreshKind.Reset);
    }

    private void OnAlertsSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (!_browse.TryApplyAlertSort(e.Column.Header?.ToString()))
        {
            return;
        }

        ApplyAlertSortGlyphs();
        _ = RefreshAsync(WatchBrowseRefreshKind.Reset);
    }

    private async void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        if (!_browse.DemandsHasMore || string.IsNullOrWhiteSpace(_browse.DemandsNextCursor))
        {
            return;
        }

        await RefreshAsync(WatchBrowseRefreshKind.Append).ConfigureAwait(true);
    }

    private async void OnLoadMoreAlertsClick(object sender, RoutedEventArgs e)
    {
        if (!_browse.AlertsHasMore || string.IsNullOrWhiteSpace(_browse.AlertsNextCursor))
        {
            return;
        }

        await RefreshAsync(WatchBrowseRefreshKind.AppendAlerts).ConfigureAwait(true);
    }

    private async Task RefreshAsync(WatchBrowseRefreshKind kind)
    {
        var generation = _hostGeneration;
        var browse = _browse;
        var overview = _overview;
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _refreshCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        var admitted = false;
        var refreshingOverview = false;
        try
        {
            admitted = await _refreshAdmission.WaitAsync(kind, cancellation.Token).ConfigureAwait(true);
            if (!admitted)
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var query = BuildBrowseQuery();
            var now = DateTimeOffset.UtcNow;

            if (PrimaryNavigation.SelectedIndex == 0
                && kind is not WatchBrowseRefreshKind.Append
                && kind is not WatchBrowseRefreshKind.AppendAlerts)
            {
                refreshingOverview = true;
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
            var now = DateTimeOffset.UtcNow;
            var message = ex.FormatForBanner(
                _options.RequestTimeoutSeconds,
                Guid.NewGuid().ToString("N"));
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
            var now = DateTimeOffset.UtcNow;
            var message =
                $"endpoint=(unknown) stage=HTTP_ERROR timeoutSeconds={_options.RequestTimeoutSeconds} elapsedMs=0 {ex.Message}";
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

            Interlocked.CompareExchange(ref _refreshCancellation, null, cancellation);
            cancellation.Dispose();
            if (refreshingOverview
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
        _refreshState = snapshot.DemandsSucceeded || snapshot.AlertsSucceeded || snapshot.PollHealthSucceeded
            ? _refreshState.ApplyPartialSuccess(now).ApplyFailure(snapshot.FetchError)
            : _refreshState.ApplyFailure(snapshot.FetchError);
        RecordConnectionEvent(_connectionRecorder.ObserveFailure(
            now,
            endpoint: snapshot.FailedEndpoint ?? "(unknown)",
            stage: snapshot.FailedStage ?? "HTTP_ERROR",
            elapsed: snapshot.FailedElapsed ?? TimeSpan.Zero,
            timeoutSeconds: _options.RequestTimeoutSeconds,
            message: snapshot.FetchError,
            correlationId: snapshot.CorrelationId));
    }

    private WatchDemandBrowseQuery BuildBrowseQuery()
    {
        var status = (FilterStatus.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "VISIBLE";
        DateTimeOffset? goneAtFrom = null;
        if (string.Equals(status, "GONE", StringComparison.OrdinalIgnoreCase))
        {
            var hours = 24;
            if (GoneWindowHours.SelectedItem is ComboBoxItem item
                && item.Tag is string tag
                && int.TryParse(tag, out var parsed))
            {
                hours = parsed;
            }

            goneAtFrom = DateTimeOffset.UtcNow.AddHours(-hours);
        }

        return new WatchDemandBrowseQuery(
            Status: status,
            TaskType: NullIfBlank(FilterTaskType.Text),
            Sublot: NullIfBlank(FilterSublot.Text),
            DemandId: _appliedDemandId,
            GoneAtFrom: goneAtFrom,
            SortBy: _sortBy,
            Direction: _direction,
            Limit: 100,
            Cursor: null);
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
        UpdateGoneWindowVisibility();
        DemandsGrid.ItemsSource = _browse.Demands;
        AlertsGrid.ItemsSource = _browse.Alerts;
        LoadMoreButton.IsEnabled =
            _browse.DemandsHasMore && !string.IsNullOrWhiteSpace(_browse.DemandsNextCursor);
        LoadMoreAlertsButton.IsEnabled =
            _browse.AlertsHasMore && !string.IsNullOrWhiteSpace(_browse.AlertsNextCursor);
        RowCountText.Text = _browse.DemandsHasMore
            ? $"loaded {_browse.Demands.Count} · more available"
            : $"loaded {_browse.Demands.Count} · end of results";
        AlertCountText.Text = _browse.AlertsHasMore
            ? $"loaded {_browse.Alerts.Count} alerts · more available"
            : $"loaded {_browse.Alerts.Count} alerts · end of results";

        var now = DateTimeOffset.UtcNow;
        var banner = WatchBannerProjection.Project(
            _bannerHold,
            _health,
            _refreshState.FetchError,
            _browse.Alerts,
            now);
        _bannerHold = banner.HoldState;

        ErrorBanner.Visibility = banner.ShowError ? Visibility.Visible : Visibility.Collapsed;
        ErrorBannerText.Text = banner.ShowError ? banner.ErrorMessage ?? string.Empty : string.Empty;

        WarningBanner.Visibility = banner.ShowWarning ? Visibility.Visible : Visibility.Collapsed;
        WarningBannerText.Text = banner.ShowWarning ? banner.WarningMessage ?? string.Empty : string.Empty;

        var status = WatchStatusBarState.Project(
            _health,
            _refreshState,
            _browse.Alerts,
            _options.BaseUrl,
            now,
            recoveryMessage: banner.RecoveryMessage);
        StatusBarText.Text = status.CompactLine;
        StatusBarText.ToolTip = status.Tooltip;
        CurrentHostContextText.Text = $"当前 Host：{_options.BaseUrl}";

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
        var byId = _browse.Alerts
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
        var demandId = target.DemandId;
        window?.SetLocateHint(null);
        try
        {
            var found = await _client.FetchDemandByIdAsync(demandId).ConfigureAwait(true);
            if (found is null)
            {
                var missing = AlertDemandLocateHints.NotFound(target);
                window?.SetLocateHint(missing);
                MessageBox.Show(this, missing, "Locate Demand", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SelectStatus(found.Status);
            FilterDemandId.Text = found.DemandId;
            _appliedDemandId = found.DemandId;
            _demandIdDebounceTimer.Stop();
            await RefreshAsync(WatchBrowseRefreshKind.Reset).ConfigureAwait(true);

            var match = _browse.Demands.FirstOrDefault(d =>
                string.Equals(d.DemandId, found.DemandId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                DemandsGrid.SelectedItem = match;
                DemandsGrid.ScrollIntoView(match);
                window?.SetLocateHint(null);
                return;
            }

            var hint = AlertDemandLocateHints.OutsideCurrentBrowse(target, found.Status);
            window?.SetLocateHint(hint);
            MessageBox.Show(this, hint, "Locate Demand", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            var message = $"Locate Demand failed: {ex.Message}";
            window?.SetLocateHint(message);
            MessageBox.Show(this, message, "Locate Demand", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SelectStatus(string status)
    {
        foreach (ComboBoxItem item in FilterStatus.Items)
        {
            if (string.Equals(item.Content?.ToString(), status, StringComparison.OrdinalIgnoreCase))
            {
                FilterStatus.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateGoneWindowVisibility()
    {
        var status = (FilterStatus.SelectedItem as ComboBoxItem)?.Content?.ToString();
        GoneWindowPanel.Visibility = string.Equals(status, "GONE", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyDemandSortGlyphs()
    {
        foreach (var column in DemandsGrid.Columns)
        {
            var token = WatchDemandBrowseQuery.SortToken(column.Header?.ToString());
            if (token is null)
            {
                column.SortDirection = null;
                continue;
            }

            column.SortDirection = string.Equals(token, _sortBy, StringComparison.Ordinal)
                ? (string.Equals(_direction, "asc", StringComparison.Ordinal)
                    ? System.ComponentModel.ListSortDirection.Ascending
                    : System.ComponentModel.ListSortDirection.Descending)
                : null;
        }
    }

    private void ApplyAlertSortGlyphs()
    {
        var alertSort = _browse.AlertQuery.SortBy;
        var ascending = string.Equals(_browse.AlertQuery.Direction, "asc", StringComparison.Ordinal);
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

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
