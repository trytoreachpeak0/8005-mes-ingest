using System.Windows;
using System.Windows.Media;

namespace MesIngest.Watch;

internal partial class AlertDetailWindow : Window
{
    private readonly Action<string>? _locateDemand;
    private AlertDetailViewModel _viewModel;

    public AlertDetailWindow(AlertDetailViewModel viewModel, Action<string>? locateDemand = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _locateDemand = locateDemand;
        ApplyViewModel();
    }

    public string? AlertId => _viewModel.AlertId;

    public void ApplyUpdate(WatchAlertDto alert)
    {
        _viewModel = _viewModel.ApplyUpdate(alert);
        ApplyViewModel();
    }

    public void MarkHistorical()
    {
        _viewModel = _viewModel.MarkHistorical();
        ApplyViewModel();
    }

    public void SetLocateHint(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            LocateHintText.Visibility = Visibility.Collapsed;
            LocateHintText.Text = string.Empty;
            return;
        }

        LocateHintText.Visibility = Visibility.Visible;
        LocateHintText.Text = message;
    }

    private void ApplyViewModel()
    {
        Title = string.IsNullOrWhiteSpace(_viewModel.AlertId)
            ? $"Alert detail — {_viewModel.Code}"
            : $"Alert detail — {_viewModel.AlertId}";

        SnapshotStatusText.Text = _viewModel.IsHistoricalSnapshot
            ? _viewModel.SnapshotStatusText
            : $"Status: {_viewModel.SnapshotStatusText}";
        SnapshotStatusText.Foreground = _viewModel.IsHistoricalSnapshot
            ? new SolidColorBrush(Color.FromRgb(0xBF, 0x36, 0x0C))
            : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

        CodeText.Text = _viewModel.Code;
        SeverityText.Text = _viewModel.Severity ?? string.Empty;
        SourceText.Text = _viewModel.SourceLabel;
        LifecycleText.Text = _viewModel.LifecycleLabel;
        FirstSeenText.Text = _viewModel.FirstSeenAtText;
        LastSeenText.Text = _viewModel.LastSeenAtText;
        ResolvedAtText.Text = _viewModel.ResolvedAtText;
        CountText.Text = _viewModel.OccurrenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TaskTypeText.Text = _viewModel.TaskType ?? string.Empty;
        SublotText.Text = _viewModel.Sublot ?? string.Empty;
        DemandIdText.Text = _viewModel.DemandId ?? string.Empty;
        MessageText.Text = _viewModel.Message ?? string.Empty;

        CopyDemandIdButton.IsEnabled = !string.IsNullOrWhiteSpace(_viewModel.DemandId);
        LocateDemandButton.IsEnabled = !string.IsNullOrWhiteSpace(_viewModel.DemandId) && _locateDemand is not null;

        FieldDriftGrid.Visibility = Visibility.Collapsed;
        KeyValueGrid.Visibility = Visibility.Collapsed;
        LegacyDetailsText.Visibility = Visibility.Collapsed;
        EmptyDetailsText.Visibility = Visibility.Collapsed;

        switch (_viewModel.Projection.Kind)
        {
            case AlertDetailsProjectionKind.FieldDrift:
                FieldDriftGrid.ItemsSource = _viewModel.Projection.FieldRows;
                FieldDriftGrid.Visibility = Visibility.Visible;
                break;
            case AlertDetailsProjectionKind.KeyValue:
                KeyValueGrid.ItemsSource = _viewModel.Projection.KeyValues;
                KeyValueGrid.Visibility = Visibility.Visible;
                break;
            case AlertDetailsProjectionKind.Legacy:
                LegacyDetailsText.Text = _viewModel.Projection.LegacySummary ?? string.Empty;
                LegacyDetailsText.Visibility = Visibility.Visible;
                break;
            default:
                EmptyDetailsText.Visibility = Visibility.Visible;
                break;
        }
    }

    private void OnCopySummary(object sender, RoutedEventArgs e) =>
        Clipboard.SetText(_viewModel.BuildSummary());

    private void OnCopyDetails(object sender, RoutedEventArgs e) =>
        Clipboard.SetText(_viewModel.DetailsJson);

    private void OnCopyDemandId(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.DemandId))
        {
            Clipboard.SetText(_viewModel.DemandId);
        }
    }

    private void OnLocateDemand(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.DemandId) || _locateDemand is null)
        {
            return;
        }

        _locateDemand(_viewModel.DemandId);
    }
}
