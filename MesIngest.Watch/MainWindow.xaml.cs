using System.Windows.Threading;

namespace MesIngest.Watch;

internal partial class MainWindow : Window
{
    private readonly MesIngestApiClient _client;
    private readonly WatchOptions _options;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private IReadOnlyList<WatchDemandDto> _demands = [];
    private IReadOnlyList<WatchAlertDto> _alerts = [];
    private WatchPollHealthDto? _health;
    private string? _fetchError;
    private DemandSortField _sortBy = DemandSortField.Dates;
    private bool _sortAscending;

    public MainWindow(MesIngestApiClient client, WatchOptions options)
    {
        InitializeComponent();
        _client = client;
        _options = options;
        Title = $"MesIngest Watch — {_options.BaseUrl}";

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_options.RefreshSeconds),
        };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        Loaded += async (_, _) =>
        {
            await RefreshAsync().ConfigureAwait(true);
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _refreshGate.Dispose();
        };
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        // ComboBox IsSelected in XAML raises SelectionChanged during InitializeComponent,
        // before later-named controls (SortField, grids, …) are assigned.
        if (!IsLoaded)
        {
            return;
        }

        ApplyProjection();
    }

    private void OnDemandsSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var field = e.Column.Header?.ToString() switch
        {
            "TASK_TYPE" => DemandSortField.TaskType,
            "SUBLOT" => DemandSortField.Sublot,
            "status" => DemandSortField.Status,
            "last seen" => DemandSortField.MesLastSeenAt,
            "当前工序进入时间 (DATES)" => DemandSortField.Dates,
            _ => (DemandSortField?)null,
        };

        if (field is null)
        {
            return;
        }

        if (_sortBy == field)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortBy = field.Value;
            _sortAscending = field is not (DemandSortField.MesLastSeenAt or DemandSortField.Dates);
        }

        SyncSortControlsFromState();
        ApplyProjection();
    }

    private async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var snapshot = await _client.FetchSnapshotAsync().ConfigureAwait(true);
            if (snapshot.FetchError is null)
            {
                _demands = snapshot.Demands;
                _alerts = snapshot.Alerts;
                _health = snapshot.PollHealth;
                _fetchError = null;
            }
            else
            {
                // Keep last good projection; surface failure via banner.
                _fetchError = snapshot.FetchError;
            }

            ApplyProjection();
        }
        catch (Exception ex)
        {
            _fetchError = ex.Message;
            ApplyProjection();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplyProjection()
    {
        SyncSortStateFromControls();

        var statusFilter = (FilterStatus.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (string.Equals(statusFilter, "(all)", StringComparison.OrdinalIgnoreCase))
        {
            statusFilter = null;
        }

        var query = new DemandListQuery(
            TaskType: NullIfBlank(FilterTaskType.Text),
            Sublot: NullIfBlank(FilterSublot.Text),
            Status: statusFilter,
            SortBy: _sortBy,
            Ascending: _sortAscending);

        var rows = DemandListProjector.FilterSort(_demands, query);
        DemandsGrid.ItemsSource = rows;
        RowCountText.Text = $"{rows.Count} / {_demands.Count} rows";
        AlertsGrid.ItemsSource = _alerts;

        var banner = WatchBannerState.From(_health, _fetchError);
        FetchFailureBanner.Visibility = banner.ShowFetchFailure ? Visibility.Visible : Visibility.Collapsed;
        FetchFailureText.Text = banner.ShowFetchFailure
            ? banner.FetchFailureMessage ?? string.Empty
            : string.Empty;

        PausedBanner.Visibility = banner.ShowPausedZeroDrop ? Visibility.Visible : Visibility.Collapsed;
        PausedText.Text = banner.ShowPausedZeroDrop
            ? $"PAUSED_ZERO_DROP — types: {string.Join(", ", banner.PausedTaskTypes)}"
            : string.Empty;

        HealthText.Text = FormatHealth(_health, _options.BaseUrl);
    }

    private void SyncSortStateFromControls()
    {
        if (SortField.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && Enum.TryParse<DemandSortField>(tag, out var field))
        {
            _sortBy = field;
        }

        _sortAscending = SortAscending.IsChecked == true;
    }

    private void SyncSortControlsFromState()
    {
        foreach (ComboBoxItem item in SortField.Items)
        {
            if (item.Tag is string tag
                && Enum.TryParse<DemandSortField>(tag, out var field)
                && field == _sortBy)
            {
                SortField.SelectedItem = item;
                break;
            }
        }

        SortAscending.IsChecked = _sortAscending;
    }

    private static string FormatHealth(WatchPollHealthDto? health, string baseUrl)
    {
        var localTz = TimeZoneInfo.Local;
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, localTz);
        var tzLabel = $"{localTz.Id} (UTC{nowLocal:zzz})";

        if (health is null)
        {
            return $"API {baseUrl} — poll health: (none yet)  timezone={tzLabel}";
        }

        var paused = health.TaskTypePauses.Count(p => p.PausedZeroDrop);
        return $"API {baseUrl} — started {WatchTimeDisplay.Format(health.StartedAt)}  "
            + $"ended {WatchTimeDisplay.Format(health.EndedAt)}  "
            + $"durationMs={health.DurationMs:0}  rows={health.RowCount}  "
            + $"success={health.Success}  outcome={health.Outcome}  pausedTypes={paused}  "
            + $"timezone={tzLabel}";
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
