using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchDemandSeriesInspectorCoordinatorTests
{
    [Fact]
    public void Explicit_open_reuses_one_window_and_activates_it_each_time()
    {
        var created = new List<RecordingDemandSeriesInspectorWindow>();
        using var coordinator = new WatchDemandSeriesInspectorCoordinator(() =>
        {
            var window = new RecordingDemandSeriesInspectorWindow();
            created.Add(window);
            return window;
        });
        var first = FirstObservedPresentation("series-a", "demand-a");
        var refreshed = FirstObservedPresentation("series-a", "demand-a-refreshed");

        coordinator.OpenOrShow(first);
        coordinator.OpenOrShow(refreshed);

        var window = Assert.Single(created);
        Assert.Equal(1, window.ShowCount);
        Assert.Equal(2, window.ActivateCount);
        Assert.Same(refreshed, window.Presentation);
        Assert.True(coordinator.IsOpen);
    }

    [Fact]
    public void Selection_update_changes_open_content_without_showing_or_activating_the_window()
    {
        var window = new RecordingDemandSeriesInspectorWindow();
        using var coordinator = new WatchDemandSeriesInspectorCoordinator(() => window);
        var first = FirstObservedPresentation("series-a", "demand-a");
        var next = FirstObservedPresentation("series-b", "demand-b");
        coordinator.OpenOrShow(first);

        coordinator.Update(next);

        Assert.Same(next, window.Presentation);
        Assert.Equal(1, window.ShowCount);
        Assert.Equal(1, window.ActivateCount);
    }

    [Fact]
    public void Explicit_show_can_activate_an_existing_window_before_new_content_is_ready()
    {
        var window = new RecordingDemandSeriesInspectorWindow();
        using var coordinator = new WatchDemandSeriesInspectorCoordinator(() => window);
        coordinator.OpenOrShow(FirstObservedPresentation("series-a", "demand-a"));

        var shown = coordinator.ShowExisting();

        Assert.True(shown);
        Assert.Equal(1, window.ShowCount);
        Assert.Equal(2, window.ActivateCount);
    }

    [Fact]
    public void Closing_forgets_the_instance_and_the_next_explicit_open_recreates_it()
    {
        var created = new List<RecordingDemandSeriesInspectorWindow>();
        using var coordinator = new WatchDemandSeriesInspectorCoordinator(() =>
        {
            var window = new RecordingDemandSeriesInspectorWindow();
            created.Add(window);
            return window;
        });
        var presentation = FirstObservedPresentation("series-a", "demand-a");
        coordinator.OpenOrShow(presentation);

        created[0].SimulateClose();
        coordinator.OpenOrShow(presentation);

        Assert.Equal(2, created.Count);
        Assert.Equal(1, created[1].ShowCount);
        Assert.Equal(1, created[1].ActivateCount);
    }

    [Fact]
    public void Generation_focus_is_reported_upward_as_user_intent()
    {
        var window = new RecordingDemandSeriesInspectorWindow();
        using var coordinator = new WatchDemandSeriesInspectorCoordinator(() => window);
        string? requestedDemandId = null;
        coordinator.GenerationFocusRequested += (_, args) => requestedDemandId = args.DemandId;
        coordinator.OpenOrShow(FirstObservedPresentation("series-a", "demand-a"));

        window.RequestGeneration("demand-2");

        Assert.Equal("demand-2", requestedDemandId);
    }

    [Fact]
    public void Fluent_window_exposes_normal_top_level_semantics_and_first_observation_evidence() =>
        StaTestRunner.Run(() =>
        {
            var presentation = FirstObservedPresentation("series-a", "demand-a");
            var window = new WatchDemandSeriesInspectorWindow();

            window.Update(presentation);
            window.UpdateLayout();

            Assert.Null(window.Owner);
            Assert.False(window.Topmost);
            Assert.True(window.ShowInTaskbar);
            Assert.Equal(1200, window.Width);
            Assert.Equal(800, window.Height);
            Assert.Equal(720, window.MinWidth);
            Assert.Equal(600, window.MinHeight);
            Assert.Equal("DemandSeries Inspector", AutomationProperties.GetName(window));

            var tabs = Assert.IsType<TabControl>(window.FindName("DemandSeriesInspectorTabs"));
            Assert.Equal("DemandSeriesInspectorTabs", AutomationProperties.GetAutomationId(tabs));
            Assert.Equal(
                new[] { "世代分析", "事件" },
                tabs.Items.Cast<TabItem>().Select(item => item.Header?.ToString()));

            var generations = Assert.IsType<ListBox>(window.FindName("DemandSeriesInspectorGenerationList"));
            Assert.Equal("DemandSeriesInspectorGenerationList", AutomationProperties.GetAutomationId(generations));
            Assert.True(VirtualizingStackPanel.GetIsVirtualizing(generations));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingStackPanel.GetVirtualizationMode(generations));
            Assert.Same(presentation.FocusedGeneration, generations.SelectedItem);
            Assert.Equal("首次观察到", Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorFormationReasonText")).Text);
            Assert.Equal(
                "形成原因：首次观察到；原始原因码：FIRST_OBSERVED",
                AutomationProperties.GetHelpText(
                    Assert.IsAssignableFrom<TextBlock>(
                        window.FindName("DemandSeriesInspectorFormationReasonText"))));
            var identity = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorGenerationIdentityText"));
            Assert.DoesNotContain("前代", identity.Text, StringComparison.Ordinal);
            Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorBeforeEvidenceText")).Visibility);
            Assert.Single(Assert.IsType<DataGrid>(
                window.FindName("DemandSeriesInspectorAfterObservationGrid")).Items);
            Assert.Single(Assert.IsType<ItemsControl>(
                window.FindName("DemandSeriesInspectorFormationFacts")).Items);

            window.Close();
        });

    [Fact]
    public void Generation_selection_reports_intent_then_one_presentation_update_replaces_all_focused_sections() =>
        StaTestRunner.Run(() =>
        {
            var firstObserved = FirstObservedPresentation("series-a", "demand-1");
            var first = firstObserved.FocusedGeneration with { IsCurrent = false };
            var second = first with
            {
                Generation = 2,
                DemandId = "demand-2",
                PredecessorDemandId = first.DemandId,
                Status = "VISIBLE",
                IsCurrent = true,
                FormationReason = new WatchDemandFormationReasonPresentation(
                    "PREARCHIVE_REAPPEARANCE",
                    "归档前消失后再现",
                    IsKnown: true),
                FormationFacts = [new WatchDemandFormationFactPresentation(
                    WatchDemandFormationFactKind.FirstObservation,
                    "新世代首次匹配观测",
                    "demand-2",
                    DateTimeOffset.Parse("2026-08-21T02:00:01+08:00"),
                    "poll-2",
                    "commit-2",
                    2)],
                MesBoundary = first.MesBoundary with
                {
                    After = first.MesBoundary.After with
                    {
                        Label = "新世代首次匹配观测",
                        DemandId = "demand-2",
                    },
                },
            };
            var initial = firstObserved with
            {
                Generations = [first, second],
                FocusedGeneration = first,
            };
            var focusedSecond = initial with { FocusedGeneration = second };
            var window = new WatchDemandSeriesInspectorWindow();
            string? requested = null;
            window.GenerationFocusRequested += (_, args) => requested = args.DemandId;
            window.Update(initial);

            var list = Assert.IsType<ListBox>(
                window.FindName("DemandSeriesInspectorGenerationList"));
            Assert.Equal("历史世代", first.CurrentMarker);
            Assert.Equal("当前世代", second.CurrentMarker);
            list.SelectedItem = second;
            Assert.Equal("demand-2", requested);

            window.Update(focusedSecond);

            Assert.Same(second, list.SelectedItem);
            Assert.Contains("DemandId demand-2", Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorGenerationIdentityText")).Text,
                StringComparison.Ordinal);
            Assert.Equal("归档前消失后再现", Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorFormationReasonText")).Text);
            Assert.Equal("新世代首次匹配观测", Assert.IsType<WatchDemandFormationFactPresentation>(
                Assert.IsType<ItemsControl>(
                    window.FindName("DemandSeriesInspectorFormationFacts")).Items[0]).Label);
            Assert.Contains("新世代首次匹配观测", Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorAfterEvidenceText")).Text,
                StringComparison.Ordinal);
            Assert.Contains("DemandId demand-2", Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorEventContextText")).Text,
                StringComparison.Ordinal);

            window.Close();
        });

    [Fact]
    public void Minimum_width_reflows_the_generation_workbench_into_a_scrollable_vertical_layout() =>
        StaTestRunner.Run(() =>
        {
            var window = new WatchDemandSeriesInspectorWindow
            {
                Width = 720,
                Height = 600,
            };

            window.Update(FirstObservedPresentation("series-a", "demand-a"));
            window.UpdateLayout();

            var workbench = Assert.IsType<Grid>(
                window.FindName("DemandSeriesInspectorGenerationWorkbench"));
            var navigator = Assert.IsType<Border>(
                window.FindName("DemandSeriesInspectorGenerationNavigator"));
            var detail = Assert.IsType<Grid>(
                window.FindName("DemandSeriesInspectorGenerationDetail"));
            var scrollViewer = Assert.IsType<ScrollViewer>(
                window.FindName("DemandSeriesInspectorGenerationScrollViewer"));

            Assert.Equal(0, Grid.GetColumn(navigator));
            Assert.Equal(0, Grid.GetRow(navigator));
            Assert.Equal(0, Grid.GetColumn(detail));
            Assert.Equal(2, Grid.GetRow(detail));
            Assert.Equal(0, workbench.ColumnDefinitions[2].Width.Value);
            Assert.Equal(ScrollBarVisibility.Auto, scrollViewer.VerticalScrollBarVisibility);
            Assert.True(detail.MinHeight >= 480);

            window.Close();
        });

    [Fact]
    public void Event_workspace_exposes_exact_filter_labels_and_accessible_keyboard_contract() =>
        StaTestRunner.Run(() =>
        {
            var initial = FirstObservedPresentation("series-a", "demand-a");
            var evidence = InspectorEvent(
                initial.SeriesId,
                sequence: 17,
                initial.FocusedGeneration.DemandId,
                [initial.FocusedGeneration.DemandId],
                payloadVersion: 7);
            var window = new WatchDemandSeriesInspectorWindow();

            window.Update(initial with { Events = [evidence] });

            var tabs = Assert.IsType<TabControl>(
                window.FindName("DemandSeriesInspectorTabs"));
            var eventTab = Assert.IsType<TabItem>(tabs.Items[1]);
            Assert.Equal("事件", eventTab.Header);
            Assert.Equal(
                "DemandSeriesInspectorEventsTab",
                AutomationProperties.GetAutomationId(eventTab));
            Assert.Equal("事件", AutomationProperties.GetName(eventTab));
            Assert.True(tabs.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(tabs));
            Assert.NotNull(eventTab.HeaderTemplate);

            var relatedAction = Assert.IsType<Wpf.Ui.Controls.Button>(
                window.FindName("DemandSeriesInspectorRelatedEventsButton"));
            Assert.Equal(
                "DemandSeriesInspectorRelatedEventsButton",
                AutomationProperties.GetAutomationId(relatedAction));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(relatedAction)));
            Assert.True(relatedAction.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(relatedAction));

            var allEvents = Assert.IsType<RadioButton>(
                window.FindName("DemandSeriesInspectorAllEventsRadio"));
            var relatedEvents = Assert.IsType<RadioButton>(
                window.FindName("DemandSeriesInspectorSelectedEventsRadio"));
            Assert.Equal("全部 Series 事件", allEvents.Content);
            Assert.Equal("当前 Demand 相关事件", relatedEvents.Content);
            Assert.Equal(
                "DemandSeriesInspectorAllEventsFilter",
                AutomationProperties.GetAutomationId(allEvents));
            Assert.Equal(
                "DemandSeriesInspectorSelectedEventsFilter",
                AutomationProperties.GetAutomationId(relatedEvents));
            Assert.Equal("显示全部 Series 事件", AutomationProperties.GetName(allEvents));
            Assert.Equal("显示当前 Demand 相关事件", AutomationProperties.GetName(relatedEvents));
            Assert.True(allEvents.Focusable);
            Assert.True(relatedEvents.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(allEvents));
            Assert.True(KeyboardNavigation.GetIsTabStop(relatedEvents));

            var eventGrid = Assert.IsType<DataGrid>(
                window.FindName("DemandSeriesInspectorEventGrid"));
            Assert.Equal(
                "DemandSeriesInspectorEventGrid",
                AutomationProperties.GetAutomationId(eventGrid));
            Assert.Contains(
                "DemandSeries",
                AutomationProperties.GetName(eventGrid),
                StringComparison.Ordinal);
            Assert.True(eventGrid.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(eventGrid));
            window.Show();
            window.UpdateLayout();
            var eventCount = FindVisualDescendantByAutomationId<TextBlock>(
                eventTab,
                "DemandSeriesInspectorEventTabCount");
            Assert.Equal("1", eventCount.Text);
            Assert.Equal(
                "冻结事件总数 1",
                AutomationProperties.GetName(eventCount));
            window.Close();
        });

    [Fact]
    public void Event_workspace_keyboard_actions_select_invoke_filter_and_navigate_the_grid() =>
        StaTestRunner.Run(() =>
        {
            var initial = FirstObservedPresentation("series-a", "demand-a");
            var related = InspectorEvent(
                initial.SeriesId,
                sequence: 1,
                initial.FocusedGeneration.DemandId,
                [initial.FocusedGeneration.DemandId]);
            var unrelated = InspectorEvent(
                initial.SeriesId,
                sequence: 2,
                "demand-other",
                ["demand-other"]);
            var window = new WatchDemandSeriesInspectorWindow();
            window.Update(initial with { Events = [related, unrelated] });
            window.Show();
            window.Activate();
            window.UpdateLayout();

            var tabs = Assert.IsType<TabControl>(
                window.FindName("DemandSeriesInspectorTabs"));
            var eventTab = Assert.IsType<TabItem>(tabs.Items[1]);
            tabs.SelectedIndex = 0;
            Assert.True(eventTab.Focus());
            PressKey(eventTab, Key.Space);
            Assert.Equal(1, tabs.SelectedIndex);

            tabs.SelectedIndex = 0;
            var relatedAction = Assert.IsType<Wpf.Ui.Controls.Button>(
                window.FindName("DemandSeriesInspectorRelatedEventsButton"));
            window.UpdateLayout();
            Assert.True(relatedAction.Focus());
            PressKey(relatedAction, Key.Space);
            Assert.Equal(1, tabs.SelectedIndex);
            var eventGrid = Assert.IsType<DataGrid>(
                window.FindName("DemandSeriesInspectorEventGrid"));
            Assert.Equal([1L], EventSequences(eventGrid));

            var allEvents = Assert.IsType<RadioButton>(
                window.FindName("DemandSeriesInspectorAllEventsRadio"));
            Assert.True(allEvents.Focus());
            PressKey(allEvents, Key.Space);
            Assert.Equal([1L, 2L], EventSequences(eventGrid));

            var currentDemandEvents = Assert.IsType<RadioButton>(
                window.FindName("DemandSeriesInspectorSelectedEventsRadio"));
            Assert.True(currentDemandEvents.Focus());
            PressKey(currentDemandEvents, Key.Space);
            Assert.Equal([1L], EventSequences(eventGrid));

            Assert.True(allEvents.Focus());
            PressKey(allEvents, Key.Space);
            eventGrid.SelectedIndex = 0;
            eventGrid.CurrentCell = new DataGridCellInfo(
                eventGrid.Items[0],
                eventGrid.Columns[0]);
            eventGrid.ScrollIntoView(eventGrid.Items[0]);
            window.UpdateLayout();
            var firstCellContent = Assert.IsAssignableFrom<FrameworkElement>(
                eventGrid.Columns[0].GetCellContent(eventGrid.Items[0]));
            var firstCell = FindVisualAncestor<DataGridCell>(firstCellContent);
            Assert.True(firstCell.Focus());
            PressKey(firstCell, Key.Down);
            Assert.Equal(1, eventGrid.SelectedIndex);

            window.Close();
        });

    [Fact]
    public void Event_workspace_exposes_complete_auditable_production_evidence() =>
        StaTestRunner.Run(() =>
        {
            var initial = FirstObservedPresentation("series-a", "demand-a");
            var evidence = InspectorEvent(
                initial.SeriesId,
                sequence: 17,
                initial.FocusedGeneration.DemandId,
                [initial.FocusedGeneration.DemandId],
                payloadVersion: 7);
            var window = new WatchDemandSeriesInspectorWindow();

            window.Update(initial with { Events = [evidence] });
            Assert.IsType<TabControl>(window.FindName("DemandSeriesInspectorTabs"))
                .SelectedIndex = 1;
            window.Show();
            window.UpdateLayout();

            var eventGrid = Assert.IsType<DataGrid>(
                window.FindName("DemandSeriesInspectorEventGrid"));
            Assert.Equal(
                [
                    "SeriesSequence",
                    "EventId",
                    "SeriesId",
                    "OccurredAt",
                    "EventType",
                    "SubjectKind",
                    "SubjectId",
                    "PollTraceId",
                    "ProjectionCommitId",
                    "PayloadVersion",
                    "PayloadJson",
                ],
                eventGrid.Columns
                    .Cast<DataGridBoundColumn>()
                    .Select(column => Assert.IsType<Binding>(column.Binding).Path.Path)
                    .ToArray());
            Assert.Equal(
                [140, 180, 180, 190, 230, 150, 180, 180, 210, 150, 360],
                eventGrid.Columns
                    .Select(column => (int)Math.Round(column.ActualWidth))
                    .ToArray());
            Assert.Equal(
                [140, 180, 180, 190, 230, 150, 180, 180, 210, 150, 360],
                eventGrid.Columns
                    .Select(column => (int)Math.Round(column.MinWidth))
                    .ToArray());
            var firstHeader = FindVisualDescendants<DataGridColumnHeader>(eventGrid)
                .Single(header => string.Equals(
                    header.Content as string,
                    "SeriesSequence",
                    StringComparison.Ordinal));
            var firstHeaderText = Assert.Single(
                FindVisualDescendants<TextBlock>(firstHeader));
            var firstCellText = Assert.IsType<TextBlock>(
                eventGrid.Columns[0].GetCellContent(eventGrid.Items[0]));
            Assert.InRange(
                firstHeaderText.TranslatePoint(new Point(), eventGrid).X
                    - firstCellText.TranslatePoint(new Point(), eventGrid).X,
                -1,
                1);
            var rendered = Assert.IsType<WatchDemandSeriesInspectorEventPresentation>(
                Assert.Single(eventGrid.Items));
            Assert.Same(evidence, rendered);
            Assert.Equal("event-17", rendered.EventId);
            Assert.Equal("series-a", rendered.SeriesId);
            Assert.Equal("DEMAND", rendered.SubjectKind);
            Assert.Equal("demand-a", rendered.SubjectId);
            Assert.Equal("poll-17", rendered.PollTraceId);
            Assert.Equal("commit-17", rendered.ProjectionCommitId);
            Assert.Equal(7, rendered.PayloadVersion);
            Assert.Equal(
                "{\"demandId\":\"demand-a\",\"sequence\":17}",
                rendered.PayloadJson);

            window.Close();
        });

    [Fact]
    public void Related_event_action_and_filter_roundtrip_stay_local_preserve_order_and_emit_no_request() =>
        StaTestRunner.Run(() =>
        {
            var initial = FirstObservedPresentation("series-a", "demand-a");
            var relatedFirst = InspectorEvent(
                initial.SeriesId,
                sequence: 1,
                initial.FocusedGeneration.DemandId,
                [initial.FocusedGeneration.DemandId]);
            var unrelated = InspectorEvent(
                initial.SeriesId,
                sequence: 2,
                "demand-other",
                ["demand-other"]);
            var relatedLast = InspectorEvent(
                initial.SeriesId,
                sequence: 3,
                initial.FocusedGeneration.DemandId,
                [initial.FocusedGeneration.DemandId]);
            var presentation = initial with
            {
                Events = [relatedFirst, unrelated, relatedLast],
            };
            var window = new WatchDemandSeriesInspectorWindow();
            var outwardRequestCount = 0;
            window.GenerationFocusRequested += (_, _) => outwardRequestCount++;

            window.Update(presentation);
            var eventGrid = Assert.IsType<DataGrid>(
                window.FindName("DemandSeriesInspectorEventGrid"));
            Assert.Equal([1L, 2L, 3L], EventSequences(eventGrid));

            var relatedEvents = Assert.IsType<Wpf.Ui.Controls.Button>(
                window.FindName("DemandSeriesInspectorRelatedEventsButton"));
            relatedEvents.RaiseEvent(new RoutedEventArgs(Wpf.Ui.Controls.Button.ClickEvent));

            Assert.Equal(1, Assert.IsType<TabControl>(
                window.FindName("DemandSeriesInspectorTabs")).SelectedIndex);
            Assert.True(Assert.IsType<RadioButton>(
                window.FindName("DemandSeriesInspectorSelectedEventsRadio")).IsChecked);
            Assert.Equal([1L, 3L], EventSequences(eventGrid));
            Assert.Same(relatedFirst, eventGrid.Items[0]);
            Assert.Same(relatedLast, eventGrid.Items[1]);
            var context = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorEventContextText"));
            Assert.Contains("当前 Demand 相关事件", context.Text, StringComparison.Ordinal);
            Assert.Contains("DemandId demand-a", context.Text, StringComparison.Ordinal);
            Assert.Contains("2 / 3", context.Text, StringComparison.Ordinal);

            var allEvents = Assert.IsType<RadioButton>(
                window.FindName("DemandSeriesInspectorAllEventsRadio"));
            allEvents.IsChecked = true;
            Assert.Equal([1L, 2L, 3L], EventSequences(eventGrid));
            Assert.Same(relatedFirst, eventGrid.Items[0]);
            Assert.Same(unrelated, eventGrid.Items[1]);
            Assert.Same(relatedLast, eventGrid.Items[2]);
            Assert.Contains("全部 Series 事件", context.Text, StringComparison.Ordinal);
            Assert.Contains("DemandId demand-a", context.Text, StringComparison.Ordinal);
            Assert.Contains("冻结快照", context.Text, StringComparison.Ordinal);
            Assert.Contains("3 条", context.Text, StringComparison.Ordinal);

            var currentDemandEvents = Assert.IsType<RadioButton>(
                window.FindName("DemandSeriesInspectorSelectedEventsRadio"));
            currentDemandEvents.IsChecked = true;
            Assert.Equal([1L, 3L], EventSequences(eventGrid));
            Assert.Same(relatedFirst, eventGrid.Items[0]);
            Assert.Same(relatedLast, eventGrid.Items[1]);
            eventGrid.SelectedItem = relatedLast;
            eventGrid.Columns[0].Width = new DataGridLength(143);

            var refreshedRelatedFirst = relatedFirst with { PayloadJson = "{\"refresh\":1}" };
            var refreshedRelatedLast = relatedLast with { PayloadJson = "{\"refresh\":2}" };
            window.Update(presentation with
            {
                FrozenSnapshot = presentation.FrozenSnapshot with
                {
                    SnapshotReference = "snapshot-2",
                },
                Events = [refreshedRelatedFirst, unrelated, refreshedRelatedLast],
            });

            Assert.Equal(1, Assert.IsType<TabControl>(
                window.FindName("DemandSeriesInspectorTabs")).SelectedIndex);
            Assert.True(currentDemandEvents.IsChecked);
            Assert.Equal(refreshedRelatedLast, eventGrid.SelectedItem);
            Assert.Equal(143, eventGrid.Columns[0].Width.Value);
            Assert.Equal(0, outwardRequestCount);

            window.Close();
        });

    [Fact]
    public void Related_filter_tracks_the_new_focused_generation_after_one_atomic_update() =>
        StaTestRunner.Run(() =>
        {
            var seed = FirstObservedPresentation("series-a", "demand-1");
            var first = seed.FocusedGeneration with { IsCurrent = false };
            var second = first with
            {
                Generation = 2,
                DemandId = "demand-2",
                PredecessorDemandId = first.DemandId,
                IsCurrent = true,
            };
            var firstEvent = InspectorEvent(seed.SeriesId, 1, first.DemandId, [first.DemandId]);
            var secondEvent = InspectorEvent(seed.SeriesId, 2, second.DemandId, [second.DemandId]);
            var presentation = seed with
            {
                Generations = [first, second],
                FocusedGeneration = first,
                Events = [firstEvent, secondEvent],
            };
            var window = new WatchDemandSeriesInspectorWindow();
            string? requestedDemandId = null;
            window.GenerationFocusRequested += (_, args) => requestedDemandId = args.DemandId;

            window.Update(presentation);
            Assert.IsType<Wpf.Ui.Controls.Button>(
                    window.FindName("DemandSeriesInspectorRelatedEventsButton"))
                .RaiseEvent(new RoutedEventArgs(Wpf.Ui.Controls.Button.ClickEvent));
            var eventGrid = Assert.IsType<DataGrid>(
                window.FindName("DemandSeriesInspectorEventGrid"));
            Assert.Equal([1L], EventSequences(eventGrid));

            var generationList = Assert.IsType<ListBox>(
                window.FindName("DemandSeriesInspectorGenerationList"));
            generationList.SelectedItem = second;
            Assert.Equal(second.DemandId, requestedDemandId);
            window.Update(presentation with { FocusedGeneration = second });

            Assert.True(Assert.IsType<RadioButton>(
                window.FindName("DemandSeriesInspectorSelectedEventsRadio")).IsChecked);
            Assert.Equal([2L], EventSequences(eventGrid));
            Assert.Same(secondEvent, eventGrid.Items[0]);
            var context = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorEventContextText")).Text;
            Assert.Contains("当前 Demand 相关事件", context, StringComparison.Ordinal);
            Assert.Contains("DemandId demand-2", context, StringComparison.Ordinal);
            Assert.DoesNotContain("DemandId demand-1", context, StringComparison.Ordinal);

            window.Close();
        });

    [Fact]
    public void Series_switch_clears_event_filter_and_returns_to_generation_analysis_without_request() =>
        StaTestRunner.Run(() =>
        {
            var firstSeed = FirstObservedPresentation("series-a", "demand-a");
            var firstEvent = InspectorEvent(
                firstSeed.SeriesId,
                1,
                firstSeed.FocusedGeneration.DemandId,
                [firstSeed.FocusedGeneration.DemandId]);
            var secondSeed = FirstObservedPresentation("series-b", "demand-b");
            var secondEvent = InspectorEvent(
                secondSeed.SeriesId,
                9,
                secondSeed.FocusedGeneration.DemandId,
                [secondSeed.FocusedGeneration.DemandId]);
            var window = new WatchDemandSeriesInspectorWindow();
            var outwardRequestCount = 0;
            window.GenerationFocusRequested += (_, _) => outwardRequestCount++;

            window.Update(firstSeed with { Events = [firstEvent] });
            Assert.IsType<Wpf.Ui.Controls.Button>(
                    window.FindName("DemandSeriesInspectorRelatedEventsButton"))
                .RaiseEvent(new RoutedEventArgs(Wpf.Ui.Controls.Button.ClickEvent));
            Assert.Equal(1, Assert.IsType<TabControl>(
                window.FindName("DemandSeriesInspectorTabs")).SelectedIndex);

            window.Update(secondSeed with { Events = [secondEvent] });

            Assert.Equal(0, Assert.IsType<TabControl>(
                window.FindName("DemandSeriesInspectorTabs")).SelectedIndex);
            Assert.True(Assert.IsType<RadioButton>(
                window.FindName("DemandSeriesInspectorAllEventsRadio")).IsChecked);
            Assert.False(Assert.IsType<RadioButton>(
                window.FindName("DemandSeriesInspectorSelectedEventsRadio")).IsChecked);
            var eventGrid = Assert.IsType<DataGrid>(
                window.FindName("DemandSeriesInspectorEventGrid"));
            Assert.Equal([9L], EventSequences(eventGrid));
            Assert.Same(secondEvent, eventGrid.Items[0]);
            var context = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorEventContextText")).Text;
            Assert.Contains("全部 Series 事件", context, StringComparison.Ordinal);
            Assert.Contains("DemandId demand-b", context, StringComparison.Ordinal);
            Assert.DoesNotContain("demand-a", context, StringComparison.Ordinal);
            Assert.Equal(0, outwardRequestCount);

            window.Close();
        });

    [Fact]
    public void Update_Reappearance_facts_expose_trace_commit_or_explicit_unavailable_state_to_automation() =>
        StaTestRunner.Run(() =>
        {
            var seed = FirstObservedPresentation("series-a", "demand-a");
            var generation = seed.FocusedGeneration with
            {
                Generation = 2,
                DemandId = "demand-b",
                PredecessorDemandId = "demand-a",
                FormationReason = new WatchDemandFormationReasonPresentation(
                    "POSTARCHIVE_REAPPEARANCE",
                    "归档后再次出现",
                    IsKnown: true),
                FormationFacts =
                [
                    new WatchDemandFormationFactPresentation(
                        WatchDemandFormationFactKind.Archive,
                        "Series 归档",
                        "series-a",
                        DateTimeOffset.Parse("2026-08-21T01:00:00+08:00"),
                        "poll-archive",
                        "commit-archive",
                        9),
                    new WatchDemandFormationFactPresentation(
                        WatchDemandFormationFactKind.AuthoritativeGone,
                        "权威缺失 / GONE",
                        "冻结快照中未找到",
                        OccurredAt: null,
                        PollTraceId: null,
                        ProjectionCommitId: null,
                        SeriesSequence: null,
                        IsAvailable: false),
                ],
            };
            var window = RenderInspector(FocusedPresentation(seed, generation));

            var reason = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorFormationReasonText"));
            Assert.Equal("归档后再次出现", reason.Text);
            Assert.Contains("POSTARCHIVE_REAPPEARANCE", reason.ToolTip?.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                "POSTARCHIVE_REAPPEARANCE",
                AutomationProperties.GetHelpText(reason),
                StringComparison.Ordinal);
            var facts = Assert.IsType<ItemsControl>(
                window.FindName("DemandSeriesInspectorFormationFacts"));
            Assert.Contains(
                "PollTrace poll-archive",
                AutomationProperties.GetHelpText(facts),
                StringComparison.Ordinal);
            Assert.Contains(
                "ProjectionCommit commit-archive",
                AutomationProperties.GetHelpText(facts),
                StringComparison.Ordinal);
            Assert.Contains(
                "冻结快照中未找到此项事实",
                AutomationProperties.GetHelpText(facts),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "PollTrace  · ProjectionCommit ",
                AutomationProperties.GetHelpText(facts),
                StringComparison.Ordinal);

            window.Close();
        });

    [Fact]
    public void Update_Unknown_reason_keeps_neutral_primary_text_and_secondary_raw_code() =>
        StaTestRunner.Run(() =>
        {
            var seed = FirstObservedPresentation("series-a", "demand-a");
            var generation = seed.FocusedGeneration with
            {
                FormationReason = new WatchDemandFormationReasonPresentation(
                    "FUTURE_REASON",
                    "形成原因暂无法确认",
                    IsKnown: false),
            };
            var window = RenderInspector(FocusedPresentation(seed, generation));

            var reason = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorFormationReasonText"));
            Assert.Equal("形成原因暂无法确认", reason.Text);
            Assert.Equal("内部原因码：FUTURE_REASON", reason.ToolTip);
            Assert.Contains(
                "原始原因码：FUTURE_REASON",
                AutomationProperties.GetHelpText(reason),
                StringComparison.Ordinal);

            window.Close();
        });

    [Fact]
    public void Update_Unique_boundaries_show_sources_all_seven_fields_change_states_and_noncausal_explanation() =>
        StaTestRunner.Run(() =>
        {
            var seed = FirstObservedPresentation("series-a", "demand-a");
            var afterRow = seed.FocusedGeneration.MesBoundary.After.RawRowsInOrdinalOrder[0] with
            {
                Ordinal = 2,
                PollTraceId = "poll-after",
                ProjectionCommitId = "commit-after",
                DemandId = "demand-b",
                Eqp = "EQP-NEW",
                Package = "PKG-NEW",
            };
            var beforeRow = afterRow with
            {
                Ordinal = 1,
                PollTraceId = "poll-before",
                ProjectionCommitId = "commit-before",
                DemandId = "demand-a",
                Eqp = "EQP-OLD",
                Package = "PKG-OLD",
            };
            var fields = new[]
            {
                new WatchDemandMesScalarFieldPresentation("TASK_TYPE", "WIRE_TO_GATE", "WIRE_TO_GATE", IsChanged: false),
                new WatchDemandMesScalarFieldPresentation("SUBLOT", "SUB-1", "SUB-1", IsChanged: false),
                new WatchDemandMesScalarFieldPresentation("AREA", "A1", "A1", IsChanged: false),
                new WatchDemandMesScalarFieldPresentation("EQP", "EQP-OLD", "EQP-NEW", IsChanged: true),
                new WatchDemandMesScalarFieldPresentation("STEP", "焊线", "焊线", IsChanged: false),
                new WatchDemandMesScalarFieldPresentation("DATES / MesSourceDate", "2026-08-20", "2026-08-21", IsChanged: true),
                new WatchDemandMesScalarFieldPresentation("PACKAGE", "PKG-OLD", "PKG-NEW", IsChanged: true),
            };
            var boundary = new WatchDemandMesBoundaryPresentation(
                new WatchDemandMesBoundarySidePresentation(
                    "前代最后匹配观测",
                    "demand-a",
                    "poll-before",
                    "commit-before",
                    WatchDemandMesBoundaryState.Unique,
                    [new WatchDemandMesObservationGroupPresentation(
                        "poll-before",
                        "commit-before",
                        MesObservationAssignment.Assigned,
                        [beforeRow])]),
                new WatchDemandMesBoundarySidePresentation(
                    "新世代首次匹配观测",
                    "demand-b",
                    "poll-after",
                    "commit-after",
                    WatchDemandMesBoundaryState.Unique,
                    [new WatchDemandMesObservationGroupPresentation(
                        "poll-after",
                        "commit-after",
                        MesObservationAssignment.Assigned,
                        [afterRow])]),
                CanProjectScalarFields: true,
                fields,
                "MES 字段差异只是边界两侧的观察证据，不是 TransportDemand/DemandId 形成原因。");
            var generation = seed.FocusedGeneration with
            {
                Generation = 2,
                DemandId = "demand-b",
                PredecessorDemandId = "demand-a",
                MesBoundary = boundary,
            };
            var window = RenderInspector(FocusedPresentation(seed, generation));

            var sources = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorScalarBoundaryEvidenceText"));
            Assert.Contains("PollTrace poll-before", sources.Text, StringComparison.Ordinal);
            Assert.Contains("ProjectionCommit commit-before", sources.Text, StringComparison.Ordinal);
            Assert.Contains("PollTrace poll-after", sources.Text, StringComparison.Ordinal);
            Assert.Contains("ProjectionCommit commit-after", sources.Text, StringComparison.Ordinal);
            Assert.Equal(
                "DemandSeriesInspectorScalarBoundaryEvidence",
                AutomationProperties.GetAutomationId(sources));
            var scalarFields = Assert.IsType<ItemsControl>(
                window.FindName("DemandSeriesInspectorMesScalarFields"));
            Assert.Equal(7, scalarFields.Items.Count);
            Assert.Contains(
                scalarFields.Items.Cast<WatchDemandMesScalarFieldPresentation>(),
                field => field.ChangeLabel == "已变化");
            Assert.Contains(
                scalarFields.Items.Cast<WatchDemandMesScalarFieldPresentation>(),
                field => field.ChangeLabel == "保持不变");
            Assert.Contains(
                "MES 字段差异只是边界两侧的观察证据，不是 TransportDemand/DemandId 形成原因。",
                Assert.IsAssignableFrom<TextBlock>(
                    window.FindName("DemandSeriesInspectorMesExplanationText")).Text,
                StringComparison.Ordinal);

            window.Close();
        });

    [Fact]
    public void Update_Zero_and_conflict_boundaries_name_absence_and_render_every_raw_row_in_global_ordinal_order() =>
        StaTestRunner.Run(() =>
        {
            var seed = FirstObservedPresentation("series-a", "demand-a");
            var template = seed.FocusedGeneration.MesBoundary.After.RawRowsInOrdinalOrder[0];
            var unassigned = template with
            {
                Ordinal = 1,
                Assignment = MesObservationAssignment.Unassigned,
                SeriesId = null,
                DemandId = null,
                PollTraceId = "poll-conflict",
                ProjectionCommitId = "commit-conflict",
                Eqp = "EQP-U",
            };
            var assignedA = template with
            {
                Ordinal = 2,
                PollTraceId = "poll-conflict",
                ProjectionCommitId = "commit-conflict",
                DemandId = "demand-b",
                Eqp = "EQP-A",
            };
            var assignedB = assignedA with
            {
                Ordinal = 3,
                Eqp = "EQP-B",
            };
            var boundary = new WatchDemandMesBoundaryPresentation(
                new WatchDemandMesBoundarySidePresentation(
                    "前代最后匹配观测",
                    "demand-a",
                    "poll-absence",
                    "commit-absence",
                    WatchDemandMesBoundaryState.Missing,
                    ObservationGroups: []),
                new WatchDemandMesBoundarySidePresentation(
                    "新世代首次匹配观测",
                    "demand-b",
                    "poll-conflict",
                    "commit-conflict",
                    WatchDemandMesBoundaryState.Conflict,
                    [
                        new WatchDemandMesObservationGroupPresentation(
                            "poll-conflict",
                            "commit-conflict",
                            MesObservationAssignment.Assigned,
                            [assignedA, assignedB]),
                        new WatchDemandMesObservationGroupPresentation(
                            "poll-conflict",
                            "commit-conflict",
                            MesObservationAssignment.Unassigned,
                            [unassigned]),
                    ]),
                CanProjectScalarFields: false,
                ScalarFields: [],
                "MES 字段差异只是边界两侧的观察证据，不是 TransportDemand/DemandId 形成原因。");
            var generation = seed.FocusedGeneration with
            {
                Generation = 2,
                DemandId = "demand-b",
                PredecessorDemandId = "demand-a",
                MesBoundary = boundary,
            };
            var window = RenderInspector(FocusedPresentation(seed, generation));

            Assert.Equal(
                Visibility.Collapsed,
                Assert.IsType<Grid>(
                    window.FindName("DemandSeriesInspectorScalarEvidencePanel")).Visibility);
            Assert.Equal(
                Visibility.Visible,
                Assert.IsType<Grid>(
                    window.FindName("DemandSeriesInspectorRawEvidencePanel")).Visibility);
            var before = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorBeforeEvidenceText"));
            Assert.Contains("缺失", before.Text, StringComparison.Ordinal);
            Assert.Contains("poll-absence", before.Text, StringComparison.Ordinal);
            Assert.Contains("缺失", AutomationProperties.GetName(before), StringComparison.Ordinal);
            var after = Assert.IsAssignableFrom<TextBlock>(
                window.FindName("DemandSeriesInspectorAfterEvidenceText"));
            Assert.Contains("多行冲突", after.Text, StringComparison.Ordinal);
            Assert.Equal(
                "DemandSeriesInspectorAfterEvidence",
                AutomationProperties.GetAutomationId(after));
            Assert.Contains("多行冲突", AutomationProperties.GetName(after), StringComparison.Ordinal);
            var rawGrid = Assert.IsType<DataGrid>(
                window.FindName("DemandSeriesInspectorAfterObservationGrid"));
            var rows = rawGrid.Items.Cast<WatchDemandMesBoundaryRawRowPresentation>().ToArray();
            Assert.Equal([1, 2, 3], rows.Select(row => row.RawRow.Ordinal).ToArray());
            Assert.Equal(
                [
                    MesObservationAssignment.Unassigned,
                    MesObservationAssignment.Assigned,
                    MesObservationAssignment.Assigned,
                ],
                rows.Select(row => row.RawRow.Assignment).ToArray());
            Assert.All(rows, row => Assert.Equal("新世代首次匹配观测", row.BoundaryLabel));
            Assert.Null(rows[0].RawRow.SeriesId);
            Assert.Null(rows[0].RawRow.DemandId);
            Assert.Equal(
                ["EQP-U", "EQP-A", "EQP-B"],
                rows.Select(row => row.RawRow.Eqp).ToArray());
            Assert.Equal(
                [
                    "边界",
                    "#",
                    "Assignment",
                    "SeriesId",
                    "DemandId",
                    "TASK_TYPE",
                    "SUBLOT",
                    "AREA",
                    "EQP",
                    "STEP",
                    "DATES / MesSourceDate",
                    "PACKAGE",
                    "PollTrace",
                    "ProjectionCommit",
                ],
                rawGrid.Columns.Select(column => column.Header?.ToString()).ToArray());
            Assert.Contains(
                "前代最后匹配观测：缺失",
                AutomationProperties.GetName(rawGrid),
                StringComparison.Ordinal);
            Assert.Contains(
                "新世代首次匹配观测：多行冲突",
                AutomationProperties.GetName(rawGrid),
                StringComparison.Ordinal);

            window.Close();
        });

    [Fact]
    public void Update_Many_generations_virtualizes_focused_history_and_preserves_a_distinct_current_marker() =>
        StaTestRunner.Run(() =>
        {
            var seed = FirstObservedPresentation("series-a", "demand-1");
            var generations = Enumerable.Range(1, 80)
                .Select(index => seed.FocusedGeneration with
                {
                    Generation = index,
                    DemandId = $"demand-{index}",
                    PredecessorDemandId = index == 1 ? null : $"demand-{index - 1}",
                    IsCurrent = index == 80,
                })
                .ToArray();
            var focused = generations[59];
            var presentation = seed with
            {
                Generations = generations,
                FocusedGeneration = focused,
            };
            var window = RenderInspector(presentation);
            window.Show();
            window.UpdateLayout();

            var list = Assert.IsType<ListBox>(
                window.FindName("DemandSeriesInspectorGenerationList"));
            Assert.Equal(80, list.Items.Count);
            Assert.Same(focused, list.SelectedItem);
            Assert.False(focused.IsCurrent);
            Assert.True(generations[^1].IsCurrent);
            Assert.Equal("历史世代", focused.CurrentMarker);
            Assert.Equal("当前世代", generations[^1].CurrentMarker);
            Assert.Contains("第 60 代", focused.NavigationAutomationName, StringComparison.Ordinal);
            Assert.Contains("历史世代", focused.NavigationAutomationName, StringComparison.Ordinal);
            Assert.Contains("当前世代", generations[^1].NavigationAutomationName, StringComparison.Ordinal);
            Assert.True(VirtualizingStackPanel.GetIsVirtualizing(list));
            Assert.Equal(
                VirtualizationMode.Recycling,
                VirtualizingStackPanel.GetVirtualizationMode(list));
            Assert.True(ScrollViewer.GetCanContentScroll(list));
            var selectedContainer = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromItem(focused));
            Assert.Contains(
                "第 60 代",
                AutomationProperties.GetName(selectedContainer),
                StringComparison.Ordinal);

            window.Close();
        });

    private static long[] EventSequences(DataGrid grid) => grid.Items
        .Cast<WatchDemandSeriesInspectorEventPresentation>()
        .Select(item => item.SeriesSequence)
        .ToArray();

    private static WatchDemandSeriesInspectorEventPresentation InspectorEvent(
        string seriesId,
        long sequence,
        string demandId,
        IReadOnlyList<string> relatedDemandIds,
        int payloadVersion = 1) => new(
        $"event-{sequence}",
        seriesId,
        sequence,
        "TRANSPORT_DEMAND_CREATED",
        DateTimeOffset.Parse("2026-08-21T01:00:01+08:00").AddMinutes(sequence),
        "DEMAND",
        demandId,
        $"poll-{sequence}",
        $"commit-{sequence}",
        payloadVersion,
        $"{{\"demandId\":\"{demandId}\",\"sequence\":{sequence}}}",
        relatedDemandIds);

    private static T FindVisualDescendantByAutomationId<T>(
        DependencyObject root,
        string automationId)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate
                && string.Equals(
                    AutomationProperties.GetAutomationId(candidate),
                    automationId,
                    StringComparison.Ordinal))
            {
                return candidate;
            }

            try
            {
                return FindVisualDescendantByAutomationId<T>(child, automationId);
            }
            catch (InvalidOperationException)
            {
                // Continue searching sibling branches.
            }
        }

        throw new InvalidOperationException(
            $"Automation element '{automationId}' was not found.");
    }

    private static T FindVisualAncestor<T>(DependencyObject start)
        where T : DependencyObject
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        throw new InvalidOperationException(
            $"Visual ancestor '{typeof(T).Name}' was not found.");
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void PressKey(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(element);
        Assert.NotNull(source);
        element.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        });
        element.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.KeyUpEvent,
        });
    }

    private static WatchDemandSeriesInspectorPresentation FocusedPresentation(
        WatchDemandSeriesInspectorPresentation seed,
        WatchDemandSeriesInspectorGenerationPresentation generation) => seed with
        {
            Generations = [generation],
            FocusedGeneration = generation,
        };

    private static WatchDemandSeriesInspectorWindow RenderInspector(
        WatchDemandSeriesInspectorPresentation presentation)
    {
        var window = new WatchDemandSeriesInspectorWindow();
        window.Update(presentation);
        return window;
    }

    private static WatchDemandSeriesInspectorPresentation FirstObservedPresentation(
        string seriesId,
        string demandId)
    {
        var firstRow = new WatchDemandMesRawRowPresentation(
            1,
            "poll-1",
            "commit-1",
            MesObservationAssignment.Assigned,
            seriesId,
            demandId,
            "WIRE_TO_GATE",
            "SUB-1",
            "A1",
            "EQP-1",
            "焊线",
            DateTimeOffset.Parse("2026-08-21T01:00:00+08:00"),
            "PKG-1",
            DateTimeOffset.Parse("2026-08-21T01:00:01+08:00"),
            "2026-08-21 01:00:00");
        var generation = new WatchDemandSeriesInspectorGenerationPresentation(
            1,
            demandId,
            PredecessorDemandId: null,
            "VISIBLE",
            IsCurrent: true,
            new WatchDemandFormationReasonPresentation("FIRST_OBSERVED", "首次观察到", IsKnown: true),
            [new WatchDemandFormationFactPresentation(
                WatchDemandFormationFactKind.FirstObservation,
                "首次匹配观测",
                demandId,
                DateTimeOffset.Parse("2026-08-21T01:00:01+08:00"),
                "poll-1",
                "commit-1",
                1)],
            new WatchDemandMesBoundaryPresentation(
                new WatchDemandMesBoundarySidePresentation(
                    "前代最后匹配观测",
                    DemandId: null,
                    PollTraceId: null,
                    ProjectionCommitId: null,
                    WatchDemandMesBoundaryState.NotApplicable,
                    ObservationGroups: []),
                new WatchDemandMesBoundarySidePresentation(
                    "首次匹配观测",
                    demandId,
                    "poll-1",
                    "commit-1",
                    WatchDemandMesBoundaryState.Unique,
                    [new WatchDemandMesObservationGroupPresentation(
                        "poll-1",
                        "commit-1",
                        MesObservationAssignment.Assigned,
                        [firstRow])]),
                CanProjectScalarFields: true,
                [new WatchDemandMesScalarFieldPresentation("TASK_TYPE", "不适用", "WIRE_TO_GATE", IsChanged: false)],
                "MES 字段变化只是边界两侧的观察证据，不是 DemandId 形成原因。"));

        return new WatchDemandSeriesInspectorPresentation(
            seriesId,
            "WIRE_TO_GATE",
            "SUB-1",
            "TRACKING",
            "VISIBLE",
            new WatchDemandSeriesFrozenSnapshotPresentation(
                "snapshot-1",
                "commit-1",
                1,
                DateTimeOffset.Parse("2026-08-21T01:00:02+08:00"),
                "poll-1"),
            [generation],
            generation,
            []);
    }

}
