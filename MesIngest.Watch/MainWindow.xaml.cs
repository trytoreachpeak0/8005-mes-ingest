using System.IO;
using System.Windows.Input;
using System.Windows.Threading;

namespace MesIngest.Watch;

internal partial class MainWindow : Window
{
    private static readonly TimeSpan DemandIdDebounce = TimeSpan.FromMilliseconds(300);

    private readonly MesIngestApiClient _client;
    private readonly WatchOptions _options;
    private readonly WatchConnectionEventRecorder _connectionRecorder;
    private readonly WatchConnectionEventJournal _connectionJournal;
    private readonly string _layoutPreferencesPath;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _demandIdDebounceTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, AlertDetailWindow> _openAlertDetails = new(StringComparer.Ordinal);
    private readonly List<WeakReference<AlertDetailWindow>> _openAlertDetailsWithoutId = [];

    private IReadOnlyList<WatchDemandDto> _demands = [];
    private IReadOnlyList<WatchAlertDto> _alerts = [];
    private WatchPollHealthDto? _health;
    private WatchRefreshState _refreshState = WatchRefreshState.Empty;
    private string _sortBy = "dates";
    private string _direction = "desc";
    private string? _nextCursor;
    private bool _hasMore;
    private string? _appliedDemandId;
    private string _alertSortColumn = "created";
    private bool _alertSortAscending;
    private UnifiedEventsWindow? _eventsWindow;

    public MainWindow(
        MesIngestApiClient client,
        WatchOptions options,
        WatchConnectionEventJournal? connectionJournal = null,
        WatchConnectionEventRecorder? connectionRecorder = null,
        string? layoutPreferencesPath = null)
    {
        InitializeComponent();
        WatchGridClipboardBehavior.Attach(DemandsGrid);
        WatchGridClipboardBehavior.Attach(AlertsGrid);
        _client = client;
        _options = options;
        _layoutPreferencesPath = layoutPreferencesPath ?? WatchLayoutPreferences.DefaultFilePath;
        _connectionJournal = connectionJournal ?? WatchConnectionEventJournal.FromOptions(options);
        _connectionRecorder = connectionRecorder
            ?? new WatchConnectionEventRecorder(TimeSpan.FromMinutes(5));
        Title = $"MesIngest Watch — {_options.BaseUrl}";

        ApplyPaneRatio(WatchLayoutPreferences.LoadDemandShare(_layoutPreferencesPath));
        PanesSplitter.DragCompleted += (_, _) => ConvertPaneHeightsToStars();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_options.RefreshSeconds),
        };
        _timer.Tick += async (_, _) => await RefreshAsync(resetPage: true).ConfigureAwait(true);

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
            await RefreshAsync(resetPage: true).ConfigureAwait(true);
        };

        Loaded += async (_, _) =>
        {
            ApplyDemandSortGlyphs();
            ApplyAlertSortGlyphs();
            await RefreshAsync(resetPage: true).ConfigureAwait(true);
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            SavePaneRatio();
            _timer.Stop();
            _demandIdDebounceTimer.Stop();
            _refreshGate.Dispose();
        };
    }

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
        var demand = DemandsRow.ActualHeight;
        var alert = AlertsRow.ActualHeight;
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
        if (!IsLoaded)
        {
            return;
        }

        UpdateGoneWindowVisibility();
        _ = RefreshAsync(resetPage: true);
    }

    private void OnDemandIdTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var typed = NullIfBlank(FilterDemandId.Text);
        if (typed is null)
        {
            _demandIdDebounceTimer.Stop();
            _appliedDemandId = null;
            _ = RefreshAsync(resetPage: true);
            return;
        }

        _demandIdDebounceTimer.Stop();
        _demandIdDebounceTimer.Start();
    }

    private void OnDemandsSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var sortToken = DemandSortToken(e.Column.Header?.ToString());
        if (sortToken is null)
        {
            return;
        }

        if (string.Equals(_sortBy, sortToken, StringComparison.Ordinal))
        {
            _direction = string.Equals(_direction, "asc", StringComparison.Ordinal) ? "desc" : "asc";
        }
        else
        {
            _sortBy = sortToken;
            _direction = "asc";
        }

        ApplyDemandSortGlyphs();
        _ = RefreshAsync(resetPage: true);
    }

    private void OnAlertsSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var header = e.Column.Header?.ToString();
        if (header is null)
        {
            return;
        }

        if (string.Equals(_alertSortColumn, header, StringComparison.Ordinal))
        {
            _alertSortAscending = !_alertSortAscending;
        }
        else
        {
            _alertSortColumn = header;
            _alertSortAscending = true;
        }

        ApplyProjection();
    }

    private async void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        if (!_hasMore || string.IsNullOrWhiteSpace(_nextCursor))
        {
            return;
        }

        await RefreshAsync(resetPage: false).ConfigureAwait(true);
    }

    private async Task RefreshAsync(bool resetPage)
    {
        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var query = BuildBrowseQuery(resetPage ? null : _nextCursor);
            var now = DateTimeOffset.UtcNow;

            if (!resetPage)
            {
                try
                {
                    var page = await _client.FetchDemandPageAsync(query).ConfigureAwait(true);
                    _demands = _demands.Concat(page.Items).ToList();
                    _nextCursor = page.NextCursor;
                    _hasMore = page.HasMore;
                    _refreshState = _refreshState.ApplySuccess(now);
                    RecordConnectionEvent(_connectionRecorder.ObserveSuccess(now));
                    ApplyProjection();
                    return;
                }
                catch (WatchEndpointFetchException ex) when (
                    ex.InnerException is HttpRequestException http
                    && http.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    // Fall through to first-page reload.
                    query = BuildBrowseQuery(cursor: null);
                }
                catch (WatchEndpointFetchException ex)
                {
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
                    return;
                }
            }

            var snapshot = await _client.FetchSnapshotAsync(query).ConfigureAwait(true);
            if (snapshot.FetchError is null)
            {
                _demands = snapshot.Demands;
                _alerts = snapshot.Alerts;
                _health = snapshot.PollHealth;
                _nextCursor = snapshot.DemandsNextCursor;
                _hasMore = snapshot.DemandsHasMore;
                _refreshState = _refreshState.ApplySuccess(now);
                RecordConnectionEvent(_connectionRecorder.ObserveSuccess(now));
            }
            else
            {
                _refreshState = _refreshState.ApplyFailure(snapshot.FetchError);
                RecordConnectionEvent(_connectionRecorder.ObserveFailure(
                    now,
                    endpoint: snapshot.FailedEndpoint ?? "(unknown)",
                    stage: snapshot.FailedStage ?? "HTTP_ERROR",
                    elapsed: snapshot.FailedElapsed ?? TimeSpan.Zero,
                    timeoutSeconds: _options.RequestTimeoutSeconds,
                    message: snapshot.FetchError,
                    correlationId: snapshot.CorrelationId));
            }

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
            _refreshGate.Release();
        }
    }

    private WatchDemandBrowseQuery BuildBrowseQuery(string? cursor)
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
            Cursor: cursor);
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
        catch
        {
            // Local journal must not break the watch loop.
        }
    }

    private void ApplyProjection()
    {
        UpdateGoneWindowVisibility();
        DemandsGrid.ItemsSource = _demands;
        AlertsGrid.ItemsSource = SortAlerts(_alerts);
        LoadMoreButton.IsEnabled = _hasMore && !string.IsNullOrWhiteSpace(_nextCursor);
        RowCountText.Text = _hasMore
            ? $"loaded {_demands.Count} · more available"
            : $"loaded {_demands.Count} · end of results";

        var banner = WatchBannerState.From(_health, _refreshState.FetchError);
        FetchFailureBanner.Visibility = banner.ShowFetchFailure ? Visibility.Visible : Visibility.Collapsed;
        FetchFailureText.Text = banner.ShowFetchFailure
            ? banner.FetchFailureMessage ?? string.Empty
            : string.Empty;

        PausedBanner.Visibility = banner.ShowPausedZeroDrop ? Visibility.Visible : Visibility.Collapsed;
        PausedText.Text = banner.ShowPausedZeroDrop
            ? $"PAUSED_ZERO_DROP — types: {string.Join(", ", banner.PausedTaskTypes)}"
            : string.Empty;

        HealthText.Text = FormatHealth(_health, _options.BaseUrl, _refreshState);
        ApplyDemandSortGlyphs();
        ApplyAlertSortGlyphs();
        SyncOpenAlertDetails();
        _eventsWindow?.Reload();
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
            locateDemand: demandId => LocateDemandFromAlert(demandId, window));
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
        var byId = _alerts
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

    private async void LocateDemandFromAlert(string demandId, AlertDetailWindow? window)
    {
        window?.SetLocateHint(null);
        try
        {
            var found = await _client.FetchDemandByIdAsync(demandId).ConfigureAwait(true);
            if (found is null)
            {
                var missing =
                    $"DemandId {demandId} was not found in Host TransportDemands. "
                    + "It may never have been projected, or filters/history retention do not apply to exact id lookup.";
                window?.SetLocateHint(missing);
                MessageBox.Show(this, missing, "Locate Demand", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SelectStatus(found.Status);
            FilterDemandId.Text = found.DemandId;
            _appliedDemandId = found.DemandId;
            _demandIdDebounceTimer.Stop();
            await RefreshAsync(resetPage: true).ConfigureAwait(true);

            var match = _demands.FirstOrDefault(d =>
                string.Equals(d.DemandId, found.DemandId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                DemandsGrid.SelectedItem = match;
                DemandsGrid.ScrollIntoView(match);
                window?.SetLocateHint(null);
                return;
            }

            var hint =
                $"DemandId {found.DemandId} exists (status={found.Status}) but is not in the current browse page. "
                + "Clear TASK_TYPE/SUBLOT filters or widen the GoneAt window if status=GONE.";
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

    private void OnOpenEventsClick(object sender, RoutedEventArgs e)
    {
        if (_eventsWindow is { IsVisible: true })
        {
            _eventsWindow.Reload();
            _eventsWindow.Activate();
            return;
        }

        _eventsWindow = new UnifiedEventsWindow(
            loadEvents: LoadUnifiedEvents,
            openAlertDetail: OpenAlertDetail,
            logDirectory: _connectionJournal.DirectoryPath)
        {
            Owner = this,
        };
        _eventsWindow.Closed += (_, _) => _eventsWindow = null;
        _eventsWindow.Show();
    }

    private IReadOnlyList<UnifiedWatchEvent> LoadUnifiedEvents()
    {
        var zone = TimeZoneInfo.Local;
        var host = _alerts.Select(a => UnifiedWatchEvent.FromAlert(a, zone));
        IEnumerable<UnifiedWatchEvent> watch;
        try
        {
            watch = _connectionJournal.ReadRecent(200)
                .Select(e => UnifiedWatchEvent.FromConnectionEvent(e, zone));
        }
        catch (IOException)
        {
            watch = [];
        }
        catch (UnauthorizedAccessException)
        {
            watch = [];
        }

        return host.Concat(watch)
            .OrderByDescending(e => e.At)
            .ToList();
    }

    private void OnOpenLogDirectoryClick(object sender, RoutedEventArgs e) =>
        WatchLogDirectory.Open(this, _connectionJournal.DirectoryPath);

    private IReadOnlyList<WatchAlertDto> SortAlerts(IReadOnlyList<WatchAlertDto> source)
    {
        IOrderedEnumerable<WatchAlertDto> ordered = _alertSortColumn switch
        {
            "Code" => OrderAlerts(source, a => a.Code ?? string.Empty),
            "created" => OrderAlerts(source, a => a.LastSeenAt ?? a.CreatedAt ?? DateTimeOffset.MinValue),
            "TASK_TYPE" => OrderAlerts(source, a => a.TaskType ?? string.Empty),
            "SUBLOT" => OrderAlerts(source, a => a.Sublot ?? string.Empty),
            "DemandId" => OrderAlerts(source, a => a.DemandId ?? string.Empty),
            "Message" => OrderAlerts(source, a => a.Message ?? string.Empty),
            _ => OrderAlerts(source, a => a.LastSeenAt ?? a.CreatedAt ?? DateTimeOffset.MinValue),
        };

        return ordered.ThenBy(a => a.DemandId ?? string.Empty, StringComparer.Ordinal).ToList();
    }

    private IOrderedEnumerable<WatchAlertDto> OrderAlerts<TKey>(
        IEnumerable<WatchAlertDto> source,
        Func<WatchAlertDto, TKey> keySelector) =>
        _alertSortAscending
            ? source.OrderBy(keySelector)
            : source.OrderByDescending(keySelector);

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
            var token = DemandSortToken(column.Header?.ToString());
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
        foreach (var column in AlertsGrid.Columns)
        {
            var header = column.Header?.ToString();
            column.SortDirection = string.Equals(header, _alertSortColumn, StringComparison.Ordinal)
                ? (_alertSortAscending
                    ? System.ComponentModel.ListSortDirection.Ascending
                    : System.ComponentModel.ListSortDirection.Descending)
                : null;
        }
    }

    private static string? DemandSortToken(string? header) =>
        header switch
        {
            "DemandId" => "demandId",
            "TASK_TYPE" => "taskType",
            "SUBLOT" => "sublot",
            "last seen" => "mesLastSeenAt",
            "当前工序进入时间 (DATES)" => "dates",
            "created" => "createdAt",
            "gone at" => "goneAt",
            _ => null,
        };

    private static string FormatHealth(
        WatchPollHealthDto? health,
        string baseUrl,
        WatchRefreshState refreshState)
    {
        var localTz = TimeZoneInfo.Local;
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, localTz);
        var tzLabel = $"{localTz.Id} (UTC{nowLocal:zzz})";
        var refreshLine = refreshState.FormatWatchRefreshLine(DateTimeOffset.UtcNow);

        if (health is null)
        {
            return $"API {baseUrl} — poll health: (none yet)  {refreshLine}  timezone={tzLabel}";
        }

        var paused = health.TaskTypePauses.Count(p => p.PausedZeroDrop);
        return $"API {baseUrl} — started {WatchTimeDisplay.Format(health.StartedAt)}  "
            + $"ended {WatchTimeDisplay.Format(health.EndedAt)}  "
            + $"durationMs={health.DurationMs:0}  rows={health.RowCount}  "
            + $"success={health.Success}  outcome={health.Outcome}  pausedTypes={paused}  "
            + $"{refreshLine}  timezone={tzLabel}";
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
