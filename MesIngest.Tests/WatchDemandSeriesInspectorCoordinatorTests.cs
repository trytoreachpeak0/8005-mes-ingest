using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
