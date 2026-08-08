using System.IO;
using System.Windows.Input;
using System.Windows.Threading;

namespace MesIngest.Watch;

internal partial class MainWindow : Window
{
    private IWatchReadQueries _client;
    private WatchBrowseSession _browse;
    private WatchDemandSession _visibleDemands;
    private WatchDemandSession _goneDemands;
    private WatchOverviewSession _overview;
    private readonly WatchHostSession _hostSession;
    private readonly WatchOptions _options;
    private readonly WatchConnectionEventRecorder _connectionRecorder;
    private readonly WatchConnectionEventJournal _connectionJournal;
    private readonly WatchTelemetryIoDiagnosticBuffer _telemetryIoDiagnostics;
    private readonly string _layoutPreferencesPath;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _bannerHoldTimer;
    private readonly WatchRefreshAdmission _refreshAdmission = new();
    private readonly Dictionary<string, AlertDetailWindow> _openAlertDetails = new(StringComparer.Ordinal);
    private readonly List<WeakReference<AlertDetailWindow>> _openAlertDetailsWithoutId = [];

    private WatchPollHealthDto? _health;
    private bool _isOverviewRefreshing;
    private bool _isApplyingDemandProjection;
    private bool _isResettingDemandViews;
    private WatchRefreshState _refreshState = WatchRefreshState.Empty;
    private WatchRefreshState _visibleRefreshState = WatchRefreshState.Empty;
    private WatchRefreshState _goneRefreshState = WatchRefreshState.Empty;
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
        WatchTelemetryIoDiagnosticBuffer? telemetryIoDiagnostics = null,
        Func<WatchHostSettings, IWatchHostQueryAdapter>? hostAdapterFactory = null)
    {
        InitializeComponent();
        DemandTaskTypeFilter.ItemsSource =
            new[] { string.Empty }.Concat(WatchDemandDraft.ProductionTaskTypes).ToArray();
        DemandTaskTypeFilter.SelectedIndex = 0;
        WatchGridClipboardBehavior.Attach(DemandsGrid);
        WatchGridClipboardBehavior.Attach(AlertsGrid);
        _client = client;
        _bootstrapClient = client;
        _browse = new WatchBrowseSession(client);
        _visibleDemands = new WatchDemandSession(client);
        _goneDemands = new WatchDemandSession(client, WatchDemandViewKind.Gone);
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
            _visibleDemands.Dispose();
            _goneDemands.Dispose();
            _hostSession.Dispose();
            _bootstrapClient?.Dispose();
            _bootstrapClient = null;
            _timer.Stop();
            _bannerHoldTimer.Stop();
        };
    }

    private void OnPrimaryNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OverviewPage is null || DemandsPage is null || AlertsPage is null || SettingsPage is null)
        {
            return;
        }

        _refreshCancellation?.Cancel();
        ActiveDemandSession.CancelActive(userInitiated: false);
        var selected = PrimaryNavigation.SelectedIndex;
        OverviewPage.Visibility = selected == 0 ? Visibility.Visible : Visibility.Collapsed;
        DemandsPage.Visibility = selected == 1 ? Visibility.Visible : Visibility.Collapsed;
        AlertsPage.Visibility = selected == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = selected == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded && selected == 1)
        {
            if (ActiveDemandSession.State.LastSuccessfulAt is null)
            {
                _ = RunDemandOperationAsync(ActiveDemandSession.LoadInitialAsync);
            }
        }
        else if (IsLoaded && selected == 2)
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
        _visibleDemands.Dispose();
        _visibleDemands = new WatchDemandSession(_hostSession);
        ResetDemandTabsToVisible();
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
        var generation = ++_hostGeneration;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;

        _health = null;
        _refreshState = WatchRefreshState.Empty;
        _visibleRefreshState = WatchRefreshState.Empty;
        _goneRefreshState = WatchRefreshState.Empty;
        _bannerHold = WatchBannerHoldState.Empty;
        _client = _hostSession;
        _browse = new WatchBrowseSession(_hostSession);
        _visibleDemands.Dispose();
        _visibleDemands = new WatchDemandSession(_hostSession);
        _goneDemands.Dispose();
        _goneDemands = new WatchDemandSession(_hostSession, WatchDemandViewKind.Gone);
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
        ResetDemandTabsToVisible();
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

        try
        {
            WatchLayoutPreferences.SaveDemandShare(_layoutPreferencesPath, demand / total);
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
        ApplyProjection();
        if (IsLoaded && DemandsPage.Visibility == Visibility.Visible
            && current.State.LastSuccessfulAt is null)
        {
            _ = RunDemandOperationAsync(current.LoadInitialAsync);
        }
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
        if (!_browse.TryApplyAlertSort(e.Column.Header?.ToString()))
        {
            return;
        }

        ApplyAlertSortGlyphs();
        _ = RefreshAsync(WatchBrowseRefreshKind.Reset);
    }

    private async void OnLoadMoreAlertsClick(object sender, RoutedEventArgs e)
    {
        if (!_browse.AlertsHasMore || string.IsNullOrWhiteSpace(_browse.AlertsNextCursor))
        {
            return;
        }

        await RefreshAsync(WatchBrowseRefreshKind.AppendAlerts).ConfigureAwait(true);
    }

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
        var generation = _hostGeneration;
        var session = ActiveDemandSession;
        var pending = operation(CancellationToken.None);
        ApplyProjection();
        var outcome = await pending.ConfigureAwait(true);
        if (generation != _hostGeneration || !ReferenceEquals(session, ActiveDemandSession))
        {
            return WatchDemandBrowseOutcome.Superseded;
        }

        var now = DateTimeOffset.UtcNow;
        if (outcome == WatchDemandBrowseOutcome.Succeeded)
        {
            SetActiveDemandRefreshState(ActiveDemandRefreshState.ApplySuccess(now));
            RecordConnectionEvent(_connectionRecorder.ObserveSuccess(now));
        }
        else if (outcome == WatchDemandBrowseOutcome.Failed && session.State.Failure is { } failure)
        {
            var (message, endpoint, stage, elapsed, correlationId) = FormatVisibleDemandFailure(failure);
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

    private (string Message, string Endpoint, string Stage, TimeSpan Elapsed, string? CorrelationId)
        FormatVisibleDemandFailure(Exception failure)
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
            var message = $"endpoint={hostFailure.Endpoint} kind={hostFailure.Kind} "
                + $"correlationId={hostFailure.CorrelationId} {hostFailure.Message}";
            return (
                message,
                hostFailure.Endpoint,
                hostFailure.Kind.ToString(),
                TimeSpan.Zero,
                hostFailure.CorrelationId);
        }

        return (
            $"endpoint=/api/demands stage=HTTP_ERROR timeoutSeconds={_options.RequestTimeoutSeconds} "
            + $"elapsedMs=0 {failure.Message}",
            "/api/demands",
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
            var query = WatchDemandBrowseQuery.Default;
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
        AlertsGrid.ItemsSource = _browse.Alerts;
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
        LoadMoreAlertsButton.IsEnabled =
            _browse.AlertsHasMore && !string.IsNullOrWhiteSpace(_browse.AlertsNextCursor);
        RowCountText.Text = demand.LastSuccessfulAt is null
            ? "尚无成功窗口"
            : demand.Items.Count == 0
                ? $"当前查询无结果 · 最近成功 {demand.LastSuccessfulAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : demand.HasMore
                    ? $"当前页 {demand.Items.Count} 行 · 还有下一页 · 最近成功 {demand.LastSuccessfulAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                    : $"当前页 {demand.Items.Count} 行 · 已到末页 · 最近成功 {demand.LastSuccessfulAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        AlertCountText.Text = _browse.AlertsHasMore
            ? $"loaded {_browse.Alerts.Count} alerts · more available"
            : $"loaded {_browse.Alerts.Count} alerts · end of results";

        var now = DateTimeOffset.UtcNow;
        var pageRefreshState = PrimaryNavigation.SelectedIndex == 1
            ? ActiveDemandRefreshState
            : _refreshState;
        var banner = WatchBannerProjection.Project(
            _bannerHold,
            _health,
            pageRefreshState.FetchError,
            _browse.Alerts,
            now);
        _bannerHold = banner.HoldState;

        ErrorBanner.Visibility = banner.ShowError ? Visibility.Visible : Visibility.Collapsed;
        ErrorBannerText.Text = banner.ShowError ? banner.ErrorMessage ?? string.Empty : string.Empty;

        WarningBanner.Visibility = banner.ShowWarning ? Visibility.Visible : Visibility.Collapsed;
        WarningBannerText.Text = banner.ShowWarning ? banner.WarningMessage ?? string.Empty : string.Empty;

        var status = WatchStatusBarState.Project(
            _health,
            pageRefreshState,
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

            var locateQuery = new WatchDemandBrowseQuery(
                Status: found.Status,
                DemandId: found.DemandId,
                SortBy: string.Equals(found.Status, "GONE", StringComparison.OrdinalIgnoreCase)
                    ? "goneAt"
                    : "dates",
                Direction: "desc",
                Limit: 100);
            await _browse.RefreshAsync(
                    WatchBrowseRefreshKind.Reset,
                    locateQuery)
                .ConfigureAwait(true);

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
