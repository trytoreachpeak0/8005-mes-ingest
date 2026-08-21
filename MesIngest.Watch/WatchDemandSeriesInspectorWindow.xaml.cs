using System.Windows.Automation;
using System.Windows.Controls;

namespace MesIngest.Watch;

internal partial class WatchDemandSeriesInspectorWindow : IWatchDemandSeriesInspectorWindow
{
    private const double ResponsiveBreakpoint = 900;

    private bool _isRendering;
    private bool _filterSelectedGeneration;
    private WatchDemandSeriesInspectorPresentation? _presentation;

    internal WatchDemandSeriesInspectorWindow()
    {
        InitializeComponent();
        if (Application.Current is null)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);
        }

        Loaded += OnInspectorLoaded;
        ApplyResponsiveLayout(Width);
    }

    public event EventHandler<WatchDemandSeriesGenerationFocusRequestedEventArgs>?
        GenerationFocusRequested;

    public void Update(WatchDemandSeriesInspectorPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _isRendering = true;
        try
        {
            ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : Width);
            var targetChanged = _presentation is null
                || !string.Equals(
                    _presentation.SeriesId,
                    presentation.SeriesId,
                    StringComparison.Ordinal);
            _presentation = presentation;
            DataContext = presentation;
            Title = $"DemandSeries Inspector · {presentation.SeriesId}";
            InspectorTitleBar.Title =
                $"MesIngest Watch · DemandSeries Inspector · {presentation.SeriesId}";
            InspectorSeriesContextText.Text = presentation.SeriesId;
            InspectorSnapshotContextText.Text =
                $"冻结快照 {presentation.FrozenSnapshot.SnapshotReference} · "
                + WatchTimeDisplay.Format(presentation.FrozenSnapshot.ProjectionCommittedAt);
            InspectorLifecycleText.Text =
                $"{presentation.Lifecycle} · {presentation.WorkType} · {presentation.Sublot}";
            InspectorPresenceText.Text = presentation.CurrentPresence;
            DemandSeriesInspectorGenerationCountText.Text =
                $"{presentation.Generations.Count:N0} 个世代";

            DemandSeriesInspectorGenerationList.ItemsSource = presentation.Generations;
            DemandSeriesInspectorGenerationList.SelectedItem = presentation.FocusedGeneration;
            DemandSeriesInspectorGenerationList.ScrollIntoView(presentation.FocusedGeneration);
            if (targetChanged)
            {
                _filterSelectedGeneration = false;
                DemandSeriesInspectorAllEventsRadio.IsChecked = true;
                DemandSeriesInspectorTabs.SelectedIndex = 0;
            }

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
            + generation.Status;
        DemandSeriesInspectorGenerationSummaryText.Text =
            generation.PredecessorDemandId is { } predecessorDemandId
                ? $"前驱 {predecessorDemandId}"
                : "Series 首个 Demand 世代";
        DemandSeriesInspectorFormationReasonText.Text = generation.FormationReason.ChineseLabel;
        DemandSeriesInspectorFormationReasonText.ToolTip =
            $"内部原因码：{generation.FormationReason.RawCode}";
        AutomationProperties.SetHelpText(
            DemandSeriesInspectorFormationReasonText,
            $"形成原因：{generation.FormationReason.ChineseLabel}；"
            + $"原始原因码：{generation.FormationReason.RawCode}");
        DemandSeriesInspectorFormationReasonCodeText.Text =
            $"原始原因码：{generation.FormationReason.RawCode}";
        DemandSeriesInspectorFormationFacts.ItemsSource = generation.FormationFacts;
        AutomationProperties.SetName(
            DemandSeriesInspectorFormationFacts,
            $"Demand 形成事实，共 {generation.FormationFacts.Count:N0} 项");
        AutomationProperties.SetHelpText(
            DemandSeriesInspectorFormationFacts,
            string.Join("；", generation.FormationFacts.Select(fact => fact.AutomationName)));

        var before = generation.MesBoundary.Before;
        DemandSeriesInspectorBeforeEvidenceText.Visibility = before.State
            == WatchDemandMesBoundaryState.NotApplicable
                ? Visibility.Collapsed
                : Visibility.Visible;
        DemandSeriesInspectorBeforeEvidenceText.Text = FormatBoundaryState(before);
        AutomationProperties.SetName(
            DemandSeriesInspectorBeforeEvidenceText,
            FormatBoundaryState(before));
        var after = generation.MesBoundary.After;
        DemandSeriesInspectorAfterEvidenceText.Text = FormatBoundaryState(after);
        AutomationProperties.SetName(
            DemandSeriesInspectorAfterEvidenceText,
            FormatBoundaryState(after));
        DemandSeriesInspectorScalarBoundaryEvidenceText.Text = string.Join(
            "；",
            new[] { before, after }
                .Where(side => side.State != WatchDemandMesBoundaryState.NotApplicable)
                .Select(FormatBoundaryState));
        AutomationProperties.SetName(
            DemandSeriesInspectorScalarBoundaryEvidenceText,
            DemandSeriesInspectorScalarBoundaryEvidenceText.Text);
        var rawRows = ProjectBoundaryRows(before)
            .Concat(ProjectBoundaryRows(after))
            .ToArray();
        DemandSeriesInspectorAfterObservationGrid.ItemsSource = rawRows;
        AutomationProperties.SetName(
            DemandSeriesInspectorAfterObservationGrid,
            $"MES 边界原始行；{FormatBoundaryState(before)}；"
            + $"{FormatBoundaryState(after)}；共 {rawRows.Length:N0} 行");
        AutomationProperties.SetHelpText(
            DemandSeriesInspectorAfterObservationGrid,
            "保留边界原始行的 Assignment、SeriesId、DemandId、七个 MES 原生字段、"
            + "PollTrace 与 ProjectionCommit；不挑选 canonical row。");
        DemandSeriesInspectorMesScalarFields.ItemsSource =
            generation.MesBoundary.ScalarFields;
        DemandSeriesInspectorMesFieldCountText.Text =
            $"{generation.MesBoundary.ScalarFields.Count:N0} 个原生字段";
        DemandSeriesInspectorScalarEvidencePanel.Visibility =
            generation.MesBoundary.CanProjectScalarFields
                ? Visibility.Visible
                : Visibility.Collapsed;
        DemandSeriesInspectorRawEvidencePanel.Visibility =
            generation.MesBoundary.CanProjectScalarFields
                ? Visibility.Collapsed
                : Visibility.Visible;
        DemandSeriesInspectorMesExplanationText.Text = generation.Generation == 1
            ? $"{generation.DemandId} 是该 Series 的首个 Demand 世代；"
                + generation.MesBoundary.Explanation
            : $"{generation.FormationReason.ChineseLabel}形成第 {generation.Generation} 代；"
                + generation.MesBoundary.Explanation;
        ApplyEventFilter();
    }

    private static string FormatBoundaryState(
        WatchDemandMesBoundarySidePresentation side)
    {
        var state = side.State switch
        {
            WatchDemandMesBoundaryState.Missing => $"{side.Label}：缺失",
            WatchDemandMesBoundaryState.Conflict => $"{side.Label}：多行冲突",
            WatchDemandMesBoundaryState.Unique => $"{side.Label}：唯一可信原始行",
            _ => $"{side.Label}：不适用",
        };
        return string.IsNullOrWhiteSpace(side.PollTraceId)
            || string.IsNullOrWhiteSpace(side.ProjectionCommitId)
            ? state
            : $"{state} · PollTrace {side.PollTraceId} · "
                + $"ProjectionCommit {side.ProjectionCommitId}";
    }

    private static IEnumerable<WatchDemandMesBoundaryRawRowPresentation> ProjectBoundaryRows(
        WatchDemandMesBoundarySidePresentation side) =>
        side.RawRowsInOrdinalOrder.Select(row =>
            new WatchDemandMesBoundaryRawRowPresentation(side.Label, row));

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

    private void OnRelatedEventsClick(object sender, RoutedEventArgs e)
    {
        _filterSelectedGeneration = true;
        DemandSeriesInspectorSelectedEventsRadio.IsChecked = true;
        DemandSeriesInspectorTabs.SelectedIndex = 1;
        ApplyEventFilter();
    }

    private void OnAllEventsChecked(object sender, RoutedEventArgs e)
    {
        if (_isRendering)
        {
            return;
        }

        _filterSelectedGeneration = false;
        ApplyEventFilter();
    }

    private void OnSelectedEventsChecked(object sender, RoutedEventArgs e)
    {
        if (_isRendering)
        {
            return;
        }

        _filterSelectedGeneration = true;
        ApplyEventFilter();
    }

    private void ApplyEventFilter()
    {
        if (_presentation is null)
        {
            return;
        }

        var generation = _presentation.FocusedGeneration;
        var relatedEvents = _presentation.EventsForDemand(generation.DemandId);
        var visibleEvents = _filterSelectedGeneration
            ? relatedEvents
            : _presentation.Events;
        DemandSeriesInspectorEventGrid.ItemsSource = visibleEvents;
        var context = _filterSelectedGeneration
            ? $"当前 Demand 相关事件 · DemandId {generation.DemandId} · "
                + $"{relatedEvents.Count:N0} / {_presentation.Events.Count:N0} 条"
            : $"全部 Series 事件 · DemandId {generation.DemandId} · "
                + $"冻结快照内按 SeriesSequence 展示 {_presentation.Events.Count:N0} 条";
        DemandSeriesInspectorEventContextText.Text = context;
        AutomationProperties.SetName(DemandSeriesInspectorEventContextText, context);
        AutomationProperties.SetName(
            DemandSeriesInspectorEventGrid,
            $"DemandSeries 永久事件；{context}");
        AutomationProperties.SetHelpText(
            DemandSeriesInspectorEventGrid,
            "事件字段：SeriesSequence、EventId、SeriesId、OccurredAt、EventType、"
            + "SubjectKind、SubjectId、PollTraceId、ProjectionCommitId、PayloadVersion、PayloadJson。"
            + "过滤只改变同一冻结事件集合的本地视图。");
    }

    private void OnInspectorSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void OnInspectorLoaded(object sender, RoutedEventArgs e)
    {
        if (_presentation is not null)
        {
            DemandSeriesInspectorGenerationList.ScrollIntoView(
                _presentation.FocusedGeneration);
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (DemandSeriesInspectorGenerationWorkbench is null)
        {
            return;
        }

        var isNarrow = width < ResponsiveBreakpoint;
        var columns = DemandSeriesInspectorGenerationWorkbench.ColumnDefinitions;
        var rows = DemandSeriesInspectorGenerationWorkbench.RowDefinitions;
        columns[0].Width = isNarrow
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(300);
        columns[1].Width = isNarrow ? new GridLength(0) : new GridLength(12);
        columns[2].Width = isNarrow
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        rows[0].Height = isNarrow
            ? new GridLength(240)
            : new GridLength(1, GridUnitType.Star);
        rows[1].Height = isNarrow ? new GridLength(12) : new GridLength(0);
        rows[2].Height = isNarrow ? GridLength.Auto : new GridLength(0);

        Grid.SetColumn(DemandSeriesInspectorGenerationNavigator, 0);
        Grid.SetRow(DemandSeriesInspectorGenerationNavigator, 0);
        Grid.SetColumn(DemandSeriesInspectorGenerationDetail, isNarrow ? 0 : 2);
        Grid.SetRow(DemandSeriesInspectorGenerationDetail, isNarrow ? 2 : 0);
        DemandSeriesInspectorGenerationDetail.MinHeight = isNarrow ? 480 : 0;
        DemandSeriesInspectorGenerationScrollViewer.VerticalScrollBarVisibility =
            isNarrow
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
    }
}
