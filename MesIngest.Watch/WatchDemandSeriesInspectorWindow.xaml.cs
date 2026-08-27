using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MesIngest.Watch;

internal partial class WatchDemandSeriesInspectorWindow : IWatchDemandSeriesInspectorWindow
{
    private const double ResponsiveBreakpoint = 900;

    private bool _isRendering;
    private bool _filterSelectedGeneration;
    private WatchDemandSeriesInspectorStatePresentation? _state;
    private WatchDemandSeriesInspectorPresentation? _presentation;
    private WindowState _lastNonMinimizedState = WindowState.Normal;
    private WatchWindowLayout? _capturedLayout;
    private readonly WatchDisplayLanguageState _displayLanguageState;
    private WatchInspectorText _text;

    internal WatchDemandSeriesInspectorWindow(
        WatchDisplayLanguageState? displayLanguageState = null)
    {
        _displayLanguageState = displayLanguageState
            ?? new WatchDisplayLanguageState(WatchDisplayLanguage.SimplifiedChinese);
        _text = _displayLanguageState.Catalog.Inspector;
        InitializeComponent();
        WatchGridClipboardBehavior.Attach(
            DemandSeriesInspectorAfterObservationGrid,
            _displayLanguageState,
            preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(
            DemandSeriesInspectorEventGrid,
            _displayLanguageState,
            preserveSelectionUnit: true);
        Owner = null;
        if (Application.Current is null)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);
        }

        Loaded += OnInspectorLoaded;
        Closed += OnInspectorClosed;
        _displayLanguageState.Changed += OnDisplayLanguageChanged;
        ApplyLocalizedText();
        ApplyResponsiveLayout(Width);
    }

    private void OnInspectorClosed(object? sender, EventArgs e)
    {
        Closed -= OnInspectorClosed;
        _displayLanguageState.Changed -= OnDisplayLanguageChanged;
    }

    private void OnDisplayLanguageChanged(object? sender, EventArgs e)
    {
        var focusedElement = Keyboard.FocusedElement;
        var offsets = CaptureViewportOffsets();
        _text = _displayLanguageState.Catalog.Inspector;
        ApplyLocalizedText();
        if (_state is not null)
        {
            Update(_state);
        }

        RestoreViewportOffsets(offsets);
        if (focusedElement is IInputElement focusTarget && focusTarget.Focusable)
        {
            Keyboard.Focus(focusTarget);
        }
    }

    private void ApplyLocalizedText()
    {
        var demandText = _displayLanguageState.Catalog.DemandSeries;
        Title = _state is null ? _text.WindowTitle : _text.FormatWindowTitle(_state.SeriesId);
        InspectorTitleBar.Title = _state is null
            ? $"MesIngest Watch · {_text.WindowTitle}"
            : _text.FormatAppTitle(_state.SeriesId);
        AutomationProperties.SetName(InspectorTitleBar, _text.TitleBarAutomationName);
        AutomationProperties.SetName(DemandSeriesInspectorContext, _text.ContextAutomationName);
        AutomationProperties.SetName(DemandSeriesInspectorStatusInfoBar, _text.ReadState);
        AutomationProperties.SetName(DemandSeriesInspectorTabs, _text.InvestigationTasks);
        DemandSeriesInspectorGenerationTab.Header = _text.GenerationAnalysis;
        AutomationProperties.SetName(DemandSeriesInspectorGenerationTab, _text.GenerationAnalysis);
        DemandSeriesInspectorEventsTab.Header = _text.Events;
        AutomationProperties.SetName(DemandSeriesInspectorEventsTab, _text.Events);
        DemandSeriesInspectorGenerationHeadingText.Text = _text.DemandGenerations;
        DemandSeriesInspectorGenerationHelpText.Text = _text.GenerationHelp;
        AutomationProperties.SetName(DemandSeriesInspectorGenerationList, _text.GenerationNavigation);
        DemandSeriesInspectorRelatedEventsButton.Content = _text.RelatedEvents;
        AutomationProperties.SetName(
            DemandSeriesInspectorRelatedEventsButton,
            _text.RelatedEventsAutomationName);
        AutomationProperties.SetName(
            DemandSeriesInspectorFormationReasonText,
            _text.FormationReason);
        AutomationProperties.SetName(
            DemandSeriesInspectorFormationFacts,
            _text.FormationFacts);
        DemandSeriesInspectorMesHeadingText.Text = _text.MesDiffHeading;
        DemandSeriesInspectorMesHelpText.Text = _text.MesDiffHelp;
        DemandSeriesInspectorNativeFieldHeaderText.Text = _text.NativeField;
        DemandSeriesInspectorBeforeValueHeaderText.Text = _text.BeforeValue;
        DemandSeriesInspectorAfterValueHeaderText.Text = _text.AfterValue;
        DemandSeriesInspectorChangeHeaderText.Text = _text.Change;
        AutomationProperties.SetName(DemandSeriesInspectorMesScalarFields, _text.MesDiffHeading);
        DemandSeriesInspectorAfterObservationGrid.Columns[0].Header = _text.BoundaryColumn;
        AutomationProperties.SetName(
            DemandSeriesInspectorAfterObservationGrid,
            _text.RawRows);
        DemandSeriesInspectorEventHeadingText.Text = _text.PermanentEvents;
        DemandSeriesInspectorAllEventsRadio.Content = _text.AllEvents;
        DemandSeriesInspectorSelectedEventsRadio.Content = _text.SelectedEvents;
        AutomationProperties.SetName(DemandSeriesInspectorAllEventsRadio, _text.ShowAllEvents);
        AutomationProperties.SetName(DemandSeriesInspectorSelectedEventsRadio, _text.ShowSelectedEvents);
        DemandSeriesInspectorEventLogHeadingText.Text = _text.EventLog;
        DemandSeriesInspectorEventLogHelpText.Text = _text.EventLogHelp;
        AutomationProperties.SetName(DemandSeriesInspectorEventGrid, _text.EventGrid);

        if (_state is not null)
        {
            InspectorLifecycleText.Text = _text.FormatStableIdentity(
                demandText.DescribeLifecycle(_state.Lifecycle),
                _state.WorkType,
                _state.Sublot);
            InspectorPresenceText.Text = demandText.DescribePresence(_state.CurrentPresence);
        }
    }

    public event EventHandler<WatchDemandSeriesGenerationFocusRequestedEventArgs>?
        GenerationFocusRequested;

    public bool IsMinimized => WindowState == WindowState.Minimized;

    public void Restore() => WindowState = _lastNonMinimizedState;

    public void ApplyLayout(WatchWindowLayout? layout)
    {
        var applied = WatchWindowLayoutService.Apply(
            this,
            layout,
            WatchWindowLayoutTokens.Inspector);
        _lastNonMinimizedState = applied.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        _capturedLayout = null;
    }

    public WatchWindowLayout CaptureLayout() => _capturedLayout
        ?? WatchWindowLayoutService.Capture(
            this,
            restoreToMaximized: _lastNonMinimizedState == WindowState.Maximized);

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _lastNonMinimizedState = WindowState;
        }

        base.OnStateChanged(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _capturedLayout = WatchWindowLayoutService.Capture(
            this,
            restoreToMaximized: _lastNonMinimizedState == WindowState.Maximized);
        base.OnClosing(e);
    }

    public void Update(WatchDemandSeriesInspectorStatePresentation state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var focusedElement = Keyboard.FocusedElement;
        var restoreInspectorFocus = focusedElement is DependencyObject focusedObject
            && ReferenceEquals(GetWindow(focusedObject), this);
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
            Title = _text.FormatWindowTitle(state.SeriesId);
            InspectorTitleBar.Title = _text.FormatAppTitle(state.SeriesId);
            InspectorSeriesContextText.Text = state.SeriesId;
            InspectorSnapshotContextText.Text =
                _text.FormatFrozenSnapshot(
                    state.FrozenSnapshot.SnapshotReference,
                    WatchTimeDisplay.Format(state.FrozenSnapshot.ProjectionCommittedAt));
            InspectorSnapshotContextText.ToolTip = InspectorSnapshotContextText.Text;
            var demandText = _displayLanguageState.Catalog.DemandSeries;
            InspectorLifecycleText.Text = _text.FormatStableIdentity(
                demandText.DescribeLifecycle(state.Lifecycle),
                state.WorkType,
                state.Sublot);
            InspectorPresenceText.Text = demandText.DescribePresence(state.CurrentPresence);
            UpdateContextAutomationNames();
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
                    ? _text.FormatStatusName(state.StatusTitle, state.StatusMessage)
                    : $"{_text.ReadState}: {_text.NoNotice}");
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
                _text.FormatGenerationCount(presentation.Generations.Count);

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

            if (restoreInspectorFocus
                && focusedElement is IInputElement focusTarget
                && focusTarget.Focusable)
            {
                Keyboard.Focus(focusTarget);
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
            Title = _text.WindowTitle;
            InspectorTitleBar.Title = $"MesIngest Watch · {_text.WindowTitle}";
            InspectorSeriesContextText.Text = _text.NoSelection;
            InspectorSnapshotContextText.Text = _text.NoSnapshot;
            InspectorSnapshotContextText.ToolTip = null;
            InspectorLifecycleText.Text = _text.NoSelection;
            InspectorPresenceText.Text = _text.NoSelection;
            UpdateContextAutomationNames();
            DemandSeriesInspectorStatusInfoBar.IsOpen = true;
            DemandSeriesInspectorStatusInfoBar.Severity =
                Wpf.Ui.Controls.InfoBarSeverity.Informational;
            DemandSeriesInspectorStatusInfoBar.Title = _text.ClearedTitle;
            DemandSeriesInspectorStatusInfoBar.Message = _text.ClearedMessage;
            AutomationProperties.SetName(
                DemandSeriesInspectorStatusInfoBar,
                _text.FormatStatusName(
                    DemandSeriesInspectorStatusInfoBar.Title,
                    DemandSeriesInspectorStatusInfoBar.Message));
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
        DemandSeriesInspectorGenerationCountText.Text = _text.LoadingGenerations;
        DemandSeriesInspectorGenerationList.ItemsSource = null;
        DemandSeriesInspectorFormationFacts.ItemsSource = null;
        DemandSeriesInspectorMesScalarFields.ItemsSource = null;
        DemandSeriesInspectorAfterObservationGrid.ItemsSource = null;
        DemandSeriesInspectorEventGrid.ItemsSource = null;
        DemandSeriesInspectorGenerationIdentityText.Text = _text.LoadingDetail;
        DemandSeriesInspectorGenerationSummaryText.Text = string.Empty;
        DemandSeriesInspectorFormationReasonText.Text = _state?.IsLoading == true
            ? _text.Loading
            : _text.Unavailable;
        DemandSeriesInspectorFormationReasonText.ToolTip = null;
        DemandSeriesInspectorFormationReasonCodeText.Text = string.Empty;
        DemandSeriesInspectorScalarBoundaryEvidenceText.Text = string.Empty;
        DemandSeriesInspectorBeforeEvidenceText.Text = string.Empty;
        DemandSeriesInspectorAfterEvidenceText.Text = string.Empty;
        DemandSeriesInspectorMesFieldCountText.Text = _text.FormatNativeFieldCount(0);
        DemandSeriesInspectorScalarEvidencePanel.Visibility = Visibility.Visible;
        DemandSeriesInspectorRawEvidencePanel.Visibility = Visibility.Collapsed;
        DemandSeriesInspectorMesExplanationText.Text = string.Empty;
        DemandSeriesInspectorEventContextText.Text = _text.LoadingDetail;
        ClearDetailAutomationNames();
    }

    private void ClearDetailAutomationNames()
    {
        var stateLabel = _state?.IsLoading == true ? _text.Loading : _text.Unavailable;
        AutomationProperties.SetName(
            DemandSeriesInspectorGenerationIdentityText,
            _text.FormatDetailStateName(stateLabel));
        AutomationProperties.SetName(
            DemandSeriesInspectorFormationReasonText,
            _text.FormatFormationStateName(stateLabel));
        AutomationProperties.SetHelpText(
            DemandSeriesInspectorFormationReasonText,
            string.Empty);
        AutomationProperties.SetName(
            DemandSeriesInspectorFormationFacts,
            _text.FormatFormationFactsStateName(stateLabel));
        AutomationProperties.SetHelpText(
            DemandSeriesInspectorFormationFacts,
            string.Empty);
        AutomationProperties.SetName(
            DemandSeriesInspectorScalarBoundaryEvidenceText,
            _text.FormatScalarBoundaryStateName(stateLabel));
        AutomationProperties.SetName(
            DemandSeriesInspectorAfterObservationGrid,
            _text.FormatRawRowsStateName(stateLabel));
        AutomationProperties.SetHelpText(
            DemandSeriesInspectorAfterObservationGrid,
            string.Empty);
        AutomationProperties.SetName(
            DemandSeriesInspectorEventContextText,
            _text.FormatEventContextStateName(stateLabel));
        AutomationProperties.SetName(
            DemandSeriesInspectorEventGrid,
            _text.FormatEventGridStateName(stateLabel));
        AutomationProperties.SetHelpText(
            DemandSeriesInspectorEventGrid,
            string.Empty);
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
            _text.FormatGenerationIdentity(
                generation.DemandId,
                generation.Generation,
                generation.Status);
        AutomationProperties.SetName(
            DemandSeriesInspectorGenerationIdentityText,
            _text.FormatGenerationIdentityName(
                generation.DemandId,
                generation.Generation,
                generation.Status,
                generation.CurrentMarker));
        DemandSeriesInspectorGenerationSummaryText.Text =
            generation.PredecessorDemandId is { } predecessorDemandId
                ? _text.FormatPredecessor(predecessorDemandId)
                : _text.FirstGeneration;
        DemandSeriesInspectorFormationReasonText.Text = generation.FormationReason.ChineseLabel;
        DemandSeriesInspectorFormationReasonText.ToolTip =
            _text.FormatRawReasonCode(generation.FormationReason.RawCode);
        var formationReasonAutomationName = _text.FormatFormationReasonName(
            generation.FormationReason.ChineseLabel,
            generation.FormationReason.RawCode);
        AutomationProperties.SetName(
            DemandSeriesInspectorFormationReasonText,
            formationReasonAutomationName);
        AutomationProperties.SetHelpText(
            DemandSeriesInspectorFormationReasonText,
            formationReasonAutomationName);
        DemandSeriesInspectorFormationReasonCodeText.Text =
            _text.FormatRawReasonCode(generation.FormationReason.RawCode);
        AutomationProperties.SetName(
            DemandSeriesInspectorFormationReasonCodeText,
            DemandSeriesInspectorFormationReasonCodeText.Text);
        DemandSeriesInspectorFormationFacts.ItemsSource = generation.FormationFacts;
        AutomationProperties.SetName(
            DemandSeriesInspectorFormationFacts,
            _text.FormatFormationFactsCount(generation.FormationFacts.Count));
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
            _text.FormatRawRowsCount(
                FormatBoundaryState(before),
                FormatBoundaryState(after),
                rawRows.Length));
        AutomationProperties.SetHelpText(
            DemandSeriesInspectorAfterObservationGrid,
            _text.RawRowsHelp);
        DemandSeriesInspectorMesScalarFields.ItemsSource =
            generation.MesBoundary.ScalarFields;
        DemandSeriesInspectorMesFieldCountText.Text =
            _text.FormatNativeFieldCount(generation.MesBoundary.ScalarFields.Count);
        DemandSeriesInspectorScalarEvidencePanel.Visibility =
            generation.MesBoundary.CanProjectScalarFields
                ? Visibility.Visible
                : Visibility.Collapsed;
        DemandSeriesInspectorRawEvidencePanel.Visibility =
            generation.MesBoundary.CanProjectScalarFields
                ? Visibility.Collapsed
                : Visibility.Visible;
        DemandSeriesInspectorMesExplanationText.Text = generation.Generation == 1
            ? _text.FormatFirstConclusion(
                generation.DemandId,
                generation.MesBoundary.Explanation)
            : _text.FormatLaterConclusion(
                generation.FormationReason.ChineseLabel,
                generation.Generation,
                generation.MesBoundary.Explanation);
        ApplyEventFilter();
    }

    private string FormatBoundaryState(
        WatchDemandMesBoundarySidePresentation side)
    {
        var state = _text.FormatBoundaryState(side.Label, side.State);
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
            ? _text.FormatSelectedEventContext(
                generation.DemandId,
                relatedEvents.Count,
                _presentation.Events.Count)
            : _text.FormatAllEventContext(
                generation.DemandId,
                _presentation.Events.Count);
        DemandSeriesInspectorEventContextText.Text = context;
        AutomationProperties.SetName(DemandSeriesInspectorEventContextText, context);
        AutomationProperties.SetName(
            DemandSeriesInspectorEventGrid,
            _text.FormatEventGridContext(_text.EventGrid, context));
        AutomationProperties.SetHelpText(
            DemandSeriesInspectorEventGrid,
            _text.EventHelp);
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

    private void OnScrollableEvidenceGotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => BringIntoGenerationViewport(element));
        }
    }

    private void BringIntoGenerationViewport(FrameworkElement element)
    {
        DemandSeriesInspectorGenerationScrollViewer.UpdateLayout();
        var top = element.TranslatePoint(
            new Point(),
            DemandSeriesInspectorGenerationScrollViewer).Y;
        var bottom = top + element.ActualHeight;
        if (top < 0)
        {
            DemandSeriesInspectorGenerationScrollViewer.ScrollToVerticalOffset(
                DemandSeriesInspectorGenerationScrollViewer.VerticalOffset + top);
        }
        else if (bottom > DemandSeriesInspectorGenerationScrollViewer.ViewportHeight)
        {
            DemandSeriesInspectorGenerationScrollViewer.ScrollToVerticalOffset(
                DemandSeriesInspectorGenerationScrollViewer.VerticalOffset
                + bottom
                - DemandSeriesInspectorGenerationScrollViewer.ViewportHeight);
        }

        DemandSeriesInspectorGenerationScrollViewer.UpdateLayout();
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (DemandSeriesInspectorGenerationWorkbench is null)
        {
            return;
        }

        var isNarrow = width < ResponsiveBreakpoint;
        Grid.SetColumn(DemandSeriesInspectorPrimaryContext, 0);
        Grid.SetColumnSpan(DemandSeriesInspectorPrimaryContext, isNarrow ? 2 : 1);
        ReflowToSecondRow(
            DemandSeriesInspectorPrimaryContext,
            InspectorLifecycleText,
            isNarrow,
            wideColumn: 2,
            narrowColumnSpan: 3,
            narrowMargin: new Thickness(0, 8, 0, 0),
            wideMargin: new Thickness(12, 0, 0, 0));
        ReflowToSecondRow(
            DemandSeriesInspectorContext,
            InspectorSnapshotContextText,
            isNarrow,
            wideColumn: 1,
            narrowColumnSpan: 2,
            narrowMargin: new Thickness(0, 8, 0, 0),
            wideMargin: new Thickness(12, 0, 0, 0));

        Grid.SetColumnSpan(
            DemandSeriesInspectorGenerationIdentityText,
            isNarrow ? 2 : 1);
        ReflowToSecondRow(
            DemandSeriesInspectorGenerationIdentityContext,
            DemandSeriesInspectorGenerationSummaryText,
            isNarrow,
            wideColumn: 1,
            narrowColumnSpan: 2,
            narrowMargin: new Thickness(0, 8, 0, 0),
            wideMargin: new Thickness(12, 0, 0, 0));
        ReflowToSecondRow(
            DemandSeriesInspectorGenerationHeader,
            DemandSeriesInspectorGenerationActions,
            isNarrow,
            wideColumn: 1,
            narrowColumnSpan: 1,
            narrowMargin: new Thickness(0, 8, 0, 0),
            wideMargin: new Thickness(0));
        ReflowToSecondRow(
            DemandSeriesInspectorEventHeader,
            DemandSeriesInspectorEventFilters,
            isNarrow,
            wideColumn: 1,
            narrowColumnSpan: 1,
            narrowMargin: new Thickness(0, 8, 0, 0),
            wideMargin: new Thickness(0));

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

    private static void ReflowToSecondRow(
        Grid container,
        FrameworkElement element,
        bool isNarrow,
        int wideColumn,
        int narrowColumnSpan,
        Thickness narrowMargin,
        Thickness wideMargin)
    {
        container.RowDefinitions[1].Height = isNarrow
            ? GridLength.Auto
            : new GridLength(0);
        Grid.SetRow(element, isNarrow ? 1 : 0);
        Grid.SetColumn(element, isNarrow ? 0 : wideColumn);
        Grid.SetColumnSpan(element, isNarrow ? narrowColumnSpan : 1);
        element.Margin = isNarrow ? narrowMargin : wideMargin;
    }

    private void UpdateContextAutomationNames()
    {
        var series = _text.FormatSeriesContext(InspectorSeriesContextText.Text);
        var presence = _text.FormatPresenceContext(
            _state?.CurrentPresence ?? InspectorPresenceText.Text);
        var identity = InspectorLifecycleText.Text;
        AutomationProperties.SetName(
            InspectorSeriesContextText,
            series);
        AutomationProperties.SetName(
            InspectorPresenceText,
            presence);
        AutomationProperties.SetName(
            InspectorLifecycleText,
            identity);
        AutomationProperties.SetName(
            InspectorSnapshotContextText,
            InspectorSnapshotContextText.Text);
        AutomationProperties.SetName(
            DemandSeriesInspectorContext,
            _text.FormatContextName(
                series,
                presence,
                identity,
                InspectorSnapshotContextText.Text));
    }
}
