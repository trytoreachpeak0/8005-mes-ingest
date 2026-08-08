using System.Windows;
using System.Windows.Media;

namespace MesIngest.Watch;

internal partial class AlertDetailWindow : Window
{
    private readonly Action<AlertDemandTarget>? _locateDemand;
    private readonly Action<string> _copyText;
    private readonly Func<string, CancellationToken, Task<WatchDemandDto?>>? _loadDemand;
    private CancellationTokenSource? _relatedDemandLoadCts;
    private AlertDetailViewModel _viewModel;

    public AlertDetailWindow(
        AlertDetailViewModel viewModel,
        Action<AlertDemandTarget>? locateDemand = null,
        Action<string>? copyText = null,
        Func<string, CancellationToken, Task<WatchDemandDto?>>? loadDemand = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _locateDemand = locateDemand;
        _copyText = copyText ?? Clipboard.SetText;
        _loadDemand = loadDemand;
        Loaded += OnLoaded;
        Closed += OnClosed;
        ApplyViewModel();
    }

    public string? AlertId => _viewModel.AlertId;

    public void ApplyUpdate(WatchAlertDto alert)
    {
        _viewModel = _viewModel.ApplyUpdate(alert);
        ApplyViewModel();
        if (IsLoaded)
        {
            _ = RefreshRelatedDemandsAsync();
        }
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

        AlertIdText.Text = _viewModel.AlertId ?? string.Empty;
        CodeText.Text = _viewModel.Code;
        SeverityText.Text = _viewModel.Severity ?? string.Empty;
        SourceText.Text = _viewModel.SourceLabel;
        LifecycleText.Text = _viewModel.LifecycleLabel;
        FirstSeenText.Text = _viewModel.FirstSeenAtText;
        LastSeenText.Text = _viewModel.LastSeenAtText;
        ResolvedAtText.Text = _viewModel.ResolvedAtText;
        CreatedAtText.Text = _viewModel.CreatedAtText;
        CountText.Text = _viewModel.OccurrenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TaskTypeText.Text = _viewModel.TaskType ?? string.Empty;
        SublotText.Text = _viewModel.Sublot ?? string.Empty;
        DemandIdText.Text = _viewModel.DemandId ?? string.Empty;
        MessageText.Text = _viewModel.Message ?? string.Empty;

        CopyDemandIdButton.IsEnabled = !string.IsNullOrWhiteSpace(_viewModel.DemandId);
        LocateDemandButton.IsEnabled = !string.IsNullOrWhiteSpace(_viewModel.DemandId) && _locateDemand is not null;

        var showReappearTargets = _viewModel.HasReappearDemandTargets;
        GenericDemandActionsPanel.Visibility = showReappearTargets ? Visibility.Collapsed : Visibility.Visible;
        ReappearDemandActionsPanel.Visibility = showReappearTargets ? Visibility.Visible : Visibility.Collapsed;
        ApplyDemandTarget(
            _viewModel.ReappearTargets.Previous,
            PreviousDemandActionsPanel,
            PreviousDemandIdText,
            CopyPreviousDemandIdButton,
            LocatePreviousDemandButton);
        ApplyDemandTarget(
            _viewModel.ReappearTargets.New,
            NewDemandActionsPanel,
            NewDemandIdText,
            CopyNewDemandIdButton,
            LocateNewDemandButton);

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

    private async void OnLoaded(object sender, RoutedEventArgs e) =>
        await RefreshRelatedDemandsAsync().ConfigureAwait(true);

    private void OnClosed(object? sender, EventArgs e)
    {
        _relatedDemandLoadCts?.Cancel();
        _relatedDemandLoadCts?.Dispose();
        _relatedDemandLoadCts = null;
    }

    private async Task RefreshRelatedDemandsAsync()
    {
        _relatedDemandLoadCts?.Cancel();
        _relatedDemandLoadCts?.Dispose();
        _relatedDemandLoadCts = new CancellationTokenSource();
        var cancellationToken = _relatedDemandLoadCts.Token;

        RelatedDemandGrid.Visibility = Visibility.Collapsed;
        RelatedDemandGrid.ItemsSource = null;

        var isComparison = _viewModel.HasReappearDemandTargets;
        var previousId = isComparison ? _viewModel.ReappearTargets.Previous?.DemandId : null;
        var currentId = isComparison
            ? _viewModel.ReappearTargets.New?.DemandId
            : _viewModel.DemandId;

        if (string.IsNullOrWhiteSpace(previousId) && string.IsNullOrWhiteSpace(currentId))
        {
            RelatedDemandStatusText.Text = "This alert code has no related DemandId.";
            return;
        }

        if (_loadDemand is null)
        {
            RelatedDemandStatusText.Text = "Related Demand lookup is unavailable.";
            return;
        }

        RelatedDemandStatusText.Text = isComparison
            ? "Loading complete previous and current Demand snapshots…"
            : "Loading complete related Demand snapshot…";

        try
        {
            var previousTask = LoadDemandAsync(previousId, cancellationToken);
            var currentTask = LoadDemandAsync(currentId, cancellationToken);
            await Task.WhenAll(previousTask, currentTask).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            var previous = await previousTask.ConfigureAwait(true);
            var current = await currentTask.ConfigureAwait(true);
            IReadOnlyList<AlertDemandComparisonRow> rows;
            if (isComparison)
            {
                rows = AlertDemandComparisonProjection.Compare(previous, current);
                RelatedDemandGrid.Columns[1].Visibility = Visibility.Visible;
                RelatedDemandGrid.Columns[1].Header = "Previous (GONE)";
                RelatedDemandGrid.Columns[2].Header = "New (VISIBLE)";
                RelatedDemandStatusText.Text = DemandComparisonStatus(previousId, previous, currentId, current);
            }
            else if (current is not null)
            {
                rows = AlertDemandComparisonProjection.Single(current);
                RelatedDemandGrid.Columns[1].Visibility = Visibility.Collapsed;
                RelatedDemandGrid.Columns[2].Header = "Value";
                RelatedDemandStatusText.Text = $"Complete snapshot for DemandId {current.DemandId}.";
            }
            else
            {
                RelatedDemandStatusText.Text = $"DemandId {currentId} was not found.";
                return;
            }

            RelatedDemandGrid.ItemsSource = rows;
            RelatedDemandGrid.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RelatedDemandStatusText.Text = $"Unable to load related Demand snapshot: {ex.Message}";
        }
    }

    private Task<WatchDemandDto?> LoadDemandAsync(string? demandId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(demandId)
            ? Task.FromResult<WatchDemandDto?>(null)
            : _loadDemand!(demandId, cancellationToken);

    private static string DemandComparisonStatus(
        string? previousId,
        WatchDemandDto? previous,
        string? currentId,
        WatchDemandDto? current)
    {
        var previousStatus = previous is null ? $"previous {previousId ?? "(missing ID)"} not found" : "previous loaded";
        var currentStatus = current is null ? $"new {currentId ?? "(missing ID)"} not found" : "new loaded";
        return $"Complete comparison: {previousStatus}; {currentStatus}. Highlighted rows differ.";
    }

    private void ApplyDemandTarget(
        AlertDemandTarget? target,
        FrameworkElement panel,
        TextBlock text,
        Button copyButton,
        Button locateButton)
    {
        var available = target is not null;
        panel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        text.Text = target?.DemandId ?? string.Empty;
        copyButton.IsEnabled = available;
        locateButton.IsEnabled = available && _locateDemand is not null;
    }

    private void OnCopySummary(object sender, RoutedEventArgs e) =>
        _copyText(_viewModel.BuildSummary());

    private void OnCopyDetails(object sender, RoutedEventArgs e) =>
        _copyText(_viewModel.DetailsJson);

    private void OnCopyDemandId(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.DemandId))
        {
            _copyText(_viewModel.DemandId);
        }
    }

    private void OnCopyPreviousDemandId(object sender, RoutedEventArgs e) =>
        CopyDemandId(_viewModel.ReappearTargets.Previous?.DemandId);

    private void OnCopyNewDemandId(object sender, RoutedEventArgs e) =>
        CopyDemandId(_viewModel.ReappearTargets.New?.DemandId);

    private void OnLocateDemand(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.DemandId) || _locateDemand is null)
        {
            return;
        }

        _locateDemand(new AlertDemandTarget(_viewModel.DemandId, AlertDemandTargetKind.Generic));
    }

    private void OnLocatePreviousDemand(object sender, RoutedEventArgs e) =>
        LocateDemand(_viewModel.ReappearTargets.Previous);

    private void OnLocateNewDemand(object sender, RoutedEventArgs e) =>
        LocateDemand(_viewModel.ReappearTargets.New);

    private void CopyDemandId(string? demandId)
    {
        if (!string.IsNullOrWhiteSpace(demandId))
        {
            _copyText(demandId);
        }
    }

    private void LocateDemand(AlertDemandTarget? target)
    {
        if (target is not null && _locateDemand is not null)
        {
            _locateDemand(target);
        }
    }
}
