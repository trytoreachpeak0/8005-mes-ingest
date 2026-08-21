using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace MesIngest.Watch;

internal partial class WatchDemandSeriesInspectorWindow : IWatchDemandSeriesInspectorWindow
{
    private const double ResponsiveBreakpoint = 900;

    private bool _isRendering;
    private bool _filterSelectedGeneration;
    private WatchDemandSeriesInspectorStatePresentation? _state;
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

    public void Update(WatchDemandSeriesInspectorStatePresentation state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _isRendering = true;
        try
        {
            ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : Width);
            var targetChanged = _state is null
                || !string.Equals(
                    _state.SeriesId,
                    state.SeriesId,
                    StringComparison.Ordinal);
            var selectedEventId = targetChanged
                ? null
                : (DemandSeriesInspectorEventGrid.SelectedItem
                    as WatchDemandSeriesInspectorEventPresentation)?.EventId;
            var selectedRawEvidence = targetChanged
                ? null
                : DemandSeriesInspectorAfterObservationGrid.SelectedItem
                    as WatchDemandMesBoundaryRawRowPresentation;
            var viewportOffsets = targetChanged
                ? []
                : CaptureViewportOffsets();
            _state = state;
            var presentation = state.Detail;
            _presentation = presentation;
            DataContext = presentation;
            Title = $"DemandSeries Inspector · {state.SeriesId}";
            InspectorTitleBar.Title =
                $"MesIngest Watch · DemandSeries Inspector · {state.SeriesId}";
            InspectorSeriesContextText.Text = state.SeriesId;
            InspectorSnapshotContextText.Text =
                $"冻结快照 {state.FrozenSnapshot.SnapshotReference} · "
                + WatchTimeDisplay.Format(state.FrozenSnapshot.ProjectionCommittedAt);
            InspectorLifecycleText.Text =
                $"{state.Lifecycle} · {state.WorkType} · {state.Sublot}";
            InspectorPresenceText.Text = state.CurrentPresence;
            DemandSeriesInspectorStatusInfoBar.IsOpen =
                !string.IsNullOrWhiteSpace(state.StatusTitle)
                || !string.IsNullOrWhiteSpace(state.StatusMessage);
            DemandSeriesInspectorStatusInfoBar.Severity = state.StatusSeverity switch
            {
                WatchPresentationSeverity.Error => Wpf.Ui.Controls.InfoBarSeverity.Error,
                WatchPresentationSeverity.Warning => Wpf.Ui.Controls.InfoBarSeverity.Warning,
                WatchPresentationSeverity.Success => Wpf.Ui.Controls.InfoBarSeverity.Success,
                _ => Wpf.Ui.Controls.InfoBarSeverity.Informational,
            };
            DemandSeriesInspectorStatusInfoBar.Title = state.StatusTitle;
            DemandSeriesInspectorStatusInfoBar.Message = state.StatusMessage;
            AutomationProperties.SetName(
                DemandSeriesInspectorStatusInfoBar,
                DemandSeriesInspectorStatusInfoBar.IsOpen
                    ? $"{state.StatusTitle}。{state.StatusMessage}"
                    : "DemandSeries Inspector 读取状态：当前无通知");
            if (targetChanged)
            {
                _filterSelectedGeneration = false;
                DemandSeriesInspectorAllEventsRadio.IsChecked = true;
                DemandSeriesInspectorTabs.SelectedIndex = 0;
            }

            if (presentation is null)
            {
                ClearDetailBody();
                return;
            }

            DemandSeriesInspectorTabs.IsEnabled = true;
            DemandSeriesInspectorGenerationCountText.Text =
                $"{presentation.Generations.Count:N0} 个世代";

            var previousFocusedDemandId =
                DemandSeriesInspectorGenerationList.SelectedItem
                    is WatchDemandSeriesInspectorGenerationPresentation previous
                        ? previous.DemandId
                        : null;
            DemandSeriesInspectorGenerationList.ItemsSource = presentation.Generations;
            DemandSeriesInspectorGenerationList.SelectedItem = presentation.FocusedGeneration;
            if (targetChanged || !string.Equals(
                    previousFocusedDemandId,
                    presentation.FocusedGeneration.DemandId,
                    StringComparison.Ordinal))
            {
                DemandSeriesInspectorGenerationList.ScrollIntoView(presentation.FocusedGeneration);
            }

            RenderFocusedGeneration(presentation, presentation.FocusedGeneration);
            if (!targetChanged)
            {
                DemandSeriesInspectorEventGrid.SelectedItem =
                    DemandSeriesInspectorEventGrid.Items
                        .Cast<WatchDemandSeriesInspectorEventPresentation>()
                        .FirstOrDefault(item => string.Equals(
                            item.EventId,
                            selectedEventId,
                            StringComparison.Ordinal));
                DemandSeriesInspectorAfterObservationGrid.SelectedItem =
                    DemandSeriesInspectorAfterObservationGrid.Items
                        .Cast<WatchDemandMesBoundaryRawRowPresentation>()
                        .FirstOrDefault(item => selectedRawEvidence is not null
                            && string.Equals(
                                item.BoundaryLabel,
                                selectedRawEvidence.BoundaryLabel,
                                StringComparison.Ordinal)
                            && item.RawRow.Ordinal == selectedRawEvidence.RawRow.Ordinal
                            && string.Equals(
                                item.RawRow.PollTraceId,
                                selectedRawEvidence.RawRow.PollTraceId,
                                StringComparison.Ordinal));
                RestoreViewportOffsets(viewportOffsets);
            }
        }
        finally
        {
            _isRendering = false;
        }
    }

    internal void Update(WatchDemandSeriesInspectorPresentation presentation) =>
        Update(WatchDemandSeriesInspectorStatePresentation.Loaded(presentation));

    public void Clear()
    {
        _isRendering = true;
        try
        {
            _state = null;
            _presentation = null;
            DataContext = null;
            Title = "DemandSeries Inspector";
            InspectorTitleBar.Title = "MesIngest Watch · DemandSeries Inspector";
            InspectorSeriesContextText.Text = "尚未选择 DemandSeries";
            InspectorSnapshotContextText.Text = "尚无冻结快照";
            InspectorLifecycleText.Text = "—";
            InspectorPresenceText.Text = "—";
            DemandSeriesInspectorStatusInfoBar.IsOpen = true;
            DemandSeriesInspectorStatusInfoBar.Severity =
                Wpf.Ui.Controls.InfoBarSeverity.Informational;
            DemandSeriesInspectorStatusInfoBar.Title = "当前选择已清除";
            DemandSeriesInspectorStatusInfoBar.Message =
                "所选 Series 已离开最新结果；没有自动选择另一 Series。";
            _filterSelectedGeneration = false;
            DemandSeriesInspectorAllEventsRadio.IsChecked = true;
            DemandSeriesInspectorTabs.SelectedIndex = 0;
            ClearDetailBody();
        }
        finally
        {
            _isRendering = false;
        }
    }

    private void ClearDetailBody()
    {
        DemandSeriesInspectorTabs.IsEnabled = false;
        DemandSeriesInspectorGenerationCountText.Text = "正在读取世代";
        DemandSeriesInspectorGenerationList.ItemsSource = null;
        DemandSeriesInspectorFormationFacts.ItemsSource = null;
        DemandSeriesInspectorMesScalarFields.ItemsSource = null;
        DemandSeriesInspectorAfterObservationGrid.ItemsSource = null;
        DemandSeriesInspectorEventGrid.ItemsSource = null;
        DemandSeriesInspectorGenerationIdentityText.Text = "正在读取所选 Series 详情";
        DemandSeriesInspectorGenerationSummaryText.Text = string.Empty;
        DemandSeriesInspectorFormationReasonText.Text = "—";
        DemandSeriesInspectorFormationReasonCodeText.Text = string.Empty;
        DemandSeriesInspectorMesExplanationText.Text = string.Empty;
        DemandSeriesInspectorEventContextText.Text = "详情尚未提交";
    }

    private IReadOnlyList<InspectorViewportOffset> CaptureViewportOffsets()
    {
        var viewports = new ScrollViewer?[]
        {
            DemandSeriesInspectorGenerationScrollViewer,
            FindVisualDescendant<ScrollViewer>(DemandSeriesInspectorGenerationList),
            FindVisualDescendant<ScrollViewer>(DemandSeriesInspectorAfterObservationGrid),
            FindVisualDescendant<ScrollViewer>(DemandSeriesInspectorEventGrid),
        };
        return viewports
            .Where(viewport => viewport is not null)
            .Distinct()
            .Select(viewport => new InspectorViewportOffset(
                viewport!,
                viewport!.HorizontalOffset,
                viewport.VerticalOffset))
            .ToArray();
    }

    private static void RestoreViewportOffsets(
        IReadOnlyList<InspectorViewportOffset> offsets)
    {
        foreach (var offset in offsets)
        {
            offset.Viewport.ScrollToHorizontalOffset(offset.Horizontal);
            offset.Viewport.ScrollToVerticalOffset(offset.Vertical);
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private sealed record InspectorViewportOffset(
        ScrollViewer Viewport,
        double Horizontal,
        double Vertical);

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
