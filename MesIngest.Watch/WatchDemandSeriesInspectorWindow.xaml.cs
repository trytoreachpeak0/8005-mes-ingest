using System.Windows.Controls;

namespace MesIngest.Watch;

internal partial class WatchDemandSeriesInspectorWindow : IWatchDemandSeriesInspectorWindow
{
    private bool _isRendering;

    internal WatchDemandSeriesInspectorWindow()
    {
        InitializeComponent();
        if (Application.Current is null)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);
        }
    }

    public event EventHandler<WatchDemandSeriesGenerationFocusRequestedEventArgs>?
        GenerationFocusRequested;

    public void Update(WatchDemandSeriesInspectorPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _isRendering = true;
        try
        {
            DataContext = presentation;
            InspectorSeriesContextText.Text =
                $"Series {presentation.SeriesId} · {presentation.Sublot} + {presentation.WorkType}";
            InspectorSnapshotContextText.Text =
                $"冻结快照 {presentation.FrozenSnapshot.SnapshotReference} · "
                + $"{WatchTimeDisplay.Format(presentation.FrozenSnapshot.ProjectionCommittedAt)} · "
                + $"Commit {presentation.FrozenSnapshot.ProjectionCommitId} · "
                + $"PollTrace {presentation.FrozenSnapshot.PollTraceId}";
            InspectorLifecycleText.Text = presentation.Lifecycle;
            InspectorPresenceText.Text = presentation.CurrentPresence;

            DemandSeriesInspectorGenerationList.ItemsSource = presentation.Generations;
            DemandSeriesInspectorGenerationList.SelectedItem = presentation.FocusedGeneration;
            RenderFocusedGeneration(presentation, presentation.FocusedGeneration);
        }
        finally
        {
            _isRendering = false;
        }
    }

    private void RenderFocusedGeneration(
        WatchDemandSeriesInspectorPresentation presentation,
        WatchDemandSeriesInspectorGenerationPresentation generation)
    {
        DemandSeriesInspectorGenerationIdentityText.Text =
            $"DemandId {generation.DemandId} · 第 {generation.Generation} 代 · "
            + generation.Status
            + (generation.PredecessorDemandId is { } predecessorDemandId
                ? $" · 前代 {predecessorDemandId}"
                : string.Empty);
        DemandSeriesInspectorFormationReasonText.Text = generation.FormationReason.ChineseLabel;
        DemandSeriesInspectorFormationReasonText.ToolTip = generation.FormationReason.RawCode;
        DemandSeriesInspectorFormationReasonCodeText.Text =
            $"原始原因码：{generation.FormationReason.RawCode}";
        DemandSeriesInspectorFormationFacts.ItemsSource = generation.FormationFacts;

        var before = generation.MesBoundary.Before;
        DemandSeriesInspectorBeforeEvidenceText.Visibility = before.State
            == WatchDemandMesBoundaryState.NotApplicable
                ? Visibility.Collapsed
                : Visibility.Visible;
        DemandSeriesInspectorBeforeEvidenceText.Text = FormatBoundaryState(before);
        var after = generation.MesBoundary.After;
        DemandSeriesInspectorAfterEvidenceText.Text = FormatBoundaryState(after);
        DemandSeriesInspectorAfterObservationGrid.ItemsSource = after.ObservationGroups
            .SelectMany(group => group.Rows)
            .ToArray();
        DemandSeriesInspectorMesExplanationText.Text = generation.MesBoundary.Explanation;
        var relatedEvents = presentation.EventsForDemand(generation.DemandId);
        DemandSeriesInspectorEventContextText.Text =
            $"DemandId {generation.DemandId} · {relatedEvents.Count:N0} / "
            + $"{presentation.Events.Count:N0} 个相关事件";
        DemandSeriesInspectorEventGrid.ItemsSource = relatedEvents;
    }

    private static string FormatBoundaryState(
        WatchDemandMesBoundarySidePresentation side) => side.State switch
        {
            WatchDemandMesBoundaryState.Missing => $"{side.Label}：缺失",
            WatchDemandMesBoundaryState.Conflict => $"{side.Label}：多行冲突",
            WatchDemandMesBoundaryState.Unique => $"{side.Label}：唯一可信原始行",
            _ => $"{side.Label}：不适用",
        };

    private void OnGenerationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRendering
            || DemandSeriesInspectorGenerationList.SelectedItem
                is not WatchDemandSeriesInspectorGenerationPresentation generation)
        {
            return;
        }

        GenerationFocusRequested?.Invoke(
            this,
            new WatchDemandSeriesGenerationFocusRequestedEventArgs(generation.DemandId));
    }
}
