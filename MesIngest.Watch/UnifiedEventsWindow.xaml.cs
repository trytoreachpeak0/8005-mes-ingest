using System.Windows;
using System.Windows.Input;

namespace MesIngest.Watch;

internal partial class UnifiedEventsWindow : Window
{
    private readonly Func<IReadOnlyList<UnifiedWatchEvent>> _loadEvents;
    private readonly Action<WatchAlertDto> _openAlertDetail;
    private readonly string _logDirectory;
    private IReadOnlyList<UnifiedWatchEvent> _all = [];

    public UnifiedEventsWindow(
        Func<IReadOnlyList<UnifiedWatchEvent>> loadEvents,
        Action<WatchAlertDto> openAlertDetail,
        string logDirectory)
    {
        InitializeComponent();
        _loadEvents = loadEvents;
        _openAlertDetail = openAlertDetail;
        _logDirectory = logDirectory;
        Reload();
    }

    public void Reload()
    {
        _all = _loadEvents();
        ApplyFilter();
    }

    private void OnFilterChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyFilter();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Reload();

    private void ApplyFilter()
    {
        var filter = ResolveFilter();
        EventsGrid.ItemsSource = UnifiedWatchEvent.Filter(_all, filter);
    }

    private UnifiedEventSourceFilter ResolveFilter()
    {
        var tag = (SourceFilter.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "HostIngestAlert" => UnifiedEventSourceFilter.HostIngestAlert,
            "WatchConnectionEvent" => UnifiedEventSourceFilter.WatchConnectionEvent,
            _ => UnifiedEventSourceFilter.All,
        };
    }

    private void OnOpenLogDirectory(object sender, RoutedEventArgs e) =>
        WatchLogDirectory.Open(this, _logDirectory);

    private void OnEventsDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedDetail();

    private void OnEventsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenSelectedDetail();
        }
    }

    private void OnViewDetailsMenu(object sender, RoutedEventArgs e) => OpenSelectedDetail();

    private void OpenSelectedDetail()
    {
        if (EventsGrid.SelectedItem is not UnifiedWatchEvent selected)
        {
            return;
        }

        if (selected.Alert is { } alert)
        {
            _openAlertDetail(alert);
            return;
        }

        if (selected.ConnectionEvent is { } connection)
        {
            var text =
                $"Source=Watch Connection Event{Environment.NewLine}"
                + $"Kind={connection.Kind}{Environment.NewLine}"
                + $"At={WatchTimeDisplay.Format(connection.At)}{Environment.NewLine}"
                + $"Endpoint={connection.Endpoint ?? "null"}{Environment.NewLine}"
                + $"Stage={connection.Stage ?? "null"}{Environment.NewLine}"
                + $"ElapsedMs={connection.ElapsedMs?.ToString() ?? "null"}{Environment.NewLine}"
                + $"TimeoutSeconds={connection.TimeoutSeconds?.ToString() ?? "null"}{Environment.NewLine}"
                + $"FailureCount={connection.FailureCount}{Environment.NewLine}"
                + $"OutageDurationMs={connection.OutageDurationMs?.ToString() ?? "null"}{Environment.NewLine}"
                + $"CorrelationId={connection.CorrelationId ?? "null"}{Environment.NewLine}"
                + $"Message={connection.Message ?? "null"}";

            MessageBox.Show(this, text, "Watch Connection Event", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
