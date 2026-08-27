using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchDemandSeriesBilingualPresentationTests
{
    [Fact]
    public void Demand_series_catalog_localizes_canonical_meanings_and_preserves_codes()
    {
        var chinese = WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).DemandSeries;
        var english = WatchTextCatalog.For(WatchDisplayLanguage.English).DemandSeries;

        Assert.Equal("跟踪中 (TRACKING)", chinese.DescribeLifecycle("TRACKING"));
        Assert.Equal("Tracking (TRACKING)", english.DescribeLifecycle("TRACKING"));
        Assert.Equal("当前可见 (VISIBLE)", chinese.DescribePresence("VISIBLE"));
        Assert.Equal("Visible now (VISIBLE)", english.DescribePresence("VISIBLE"));
        Assert.Equal("未知生命周期 (FUTURE_LIFECYCLE)", chinese.DescribeLifecycle("FUTURE_LIFECYCLE"));
        Assert.Equal("Unknown lifecycle (FUTURE_LIFECYCLE)", english.DescribeLifecycle("FUTURE_LIFECYCLE"));
        Assert.EndsWith("(FUTURE_LIFECYCLE)", english.DescribeLifecycle("FUTURE_LIFECYCLE"), StringComparison.Ordinal);
    }

    [Fact]
    public void Demand_series_presenter_localizes_distinct_query_states_and_keeps_raw_facts_invariant()
    {
        var at = DateTimeOffset.Parse("2026-08-27T14:05:06+08:00");
        var item = new DemandSeriesListItemSnapshot(
            "series-04",
            "WIRE_TO_GATE",
            "SL-04",
            "TRACKING",
            "VISIBLE",
            at.AddHours(-2),
            ArchivedAt: null,
            "demand-04",
            CurrentGeneration: 2,
            CurrentDemandStatus: "VISIBLE",
            at.AddMinutes(-1),
            GoneConfirmedAt: null,
            new LiveMesFieldSetSnapshot("A1-1", "EQP-04", "STEP-04", at, "PKG-04"),
            "READABLE",
            [],
            LastSeriesSequence: 42,
            LatestPollTraceId: "poll-04",
            LatestProjectionCommitId: "commit-04");
        var snapshot = new DemandSeriesListSnapshot(
            new DemandSeriesSnapshotIdentity(
                HistoryEpoch.CreateNew(), "commit-04", 42, at, "poll-04"),
            "snapshot-04",
            new DemandSeriesBrowseFilter
            {
                Lifecycles = ["TRACKING"],
                CurrentPresences = ["VISIBLE"],
                WorkTypes = ["WIRE_TO_GATE"],
                MesAreas = ["A1-1"],
            },
            DemandSeriesBrowseOrder.Default,
            ExactTotalCount: 1,
            new DemandSeriesFacets(1, 0, 1, 0, 0),
            PageSize: 100,
            PageNumber: 1,
            TotalPages: 1,
            Items: [item],
            NextCursor: null,
            HasMore: false);
        var state = WatchV2WorkspaceState.Reset(
            4,
            "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            DemandSeries = WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot>
                .Empty(4) with
            {
                Snapshot = snapshot,
                LastSuccessfulAt = at,
                PendingQueryKey = "canonical",
                CommittedQueryKey = "canonical",
            },
        };
        var query = new DemandSeriesBrowseQuery(snapshot.Filter);

        var chinese = WatchDemandSeriesPresentation.Project(
            state,
            query,
            new WatchAreaDisplayContext("封装车间", ["A1-1"], "本机已应用", at),
            navigation: null,
            focusedDemandId: null,
            WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).DemandSeries);
        var english = WatchDemandSeriesPresentation.Project(
            state,
            query,
            new WatchAreaDisplayContext("Packaging", ["A1-1"], "Applied locally", at),
            navigation: null,
            focusedDemandId: null,
            WatchTextCatalog.For(WatchDisplayLanguage.English).DemandSeries);

        Assert.Equal("精确 1 个需求系列 · 第 1 / 1 页", chinese.PageSummary);
        Assert.Equal("Exactly 1 demand series · page 1 of 1", english.PageSummary);
        Assert.Equal("跟踪中 (TRACKING) · 当前可见 (VISIBLE)", chinese.Rows[0].LifecycleAndPresence);
        Assert.Equal("Tracking (TRACKING) · Visible now (VISIBLE)", english.Rows[0].LifecycleAndPresence);
        Assert.Equal(chinese.Rows[0].SeriesId, english.Rows[0].SeriesId);
        Assert.Equal(chinese.Rows[0].CurrentDemandId, english.Rows[0].CurrentDemandId);
        Assert.Equal(chinese.Rows[0].WorkType, english.Rows[0].WorkType);
        Assert.Equal(chinese.Rows[0].DemandLastSeenAt, english.Rows[0].DemandLastSeenAt);
        Assert.EndsWith("+08:00", english.Rows[0].DemandLastSeenAt, StringComparison.Ordinal);

        var loading = WatchDemandSeriesPresentation.Project(
            state with
            {
                DemandSeries = state.DemandSeries with { IsRefreshing = true, Snapshot = null },
            },
            query,
            WatchAreaDisplayContext.AllAreas,
            navigation: null,
            focusedDemandId: null,
            WatchTextCatalog.For(WatchDisplayLanguage.English).DemandSeries);
        var failed = WatchDemandSeriesPresentation.Project(
            state with
            {
                DemandSeries = state.DemandSeries with
                {
                    Snapshot = null,
                    LastFailureAt = at,
                    ErrorMessage = "timeout",
                },
            },
            query,
            WatchAreaDisplayContext.AllAreas,
            navigation: null,
            focusedDemandId: null,
            WatchTextCatalog.For(WatchDisplayLanguage.English).DemandSeries);

        Assert.Equal("Loading demand series", loading.InfoTitle);
        Assert.Equal("Demand series read failed", failed.InfoTitle);
        Assert.NotEqual(loading.InfoTitle, failed.InfoTitle);
        Assert.NotEqual(english.PageSummary, loading.PageSummary);
    }
}

[Collection("WpfDesktop")]
public sealed class WatchDemandSeriesBilingualProductionTests
{
    [Fact]
    public void Demand_series_page_switches_all_primary_surfaces_and_uia_to_english() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-demand-series-bilingual-{Guid.NewGuid():N}");
            try
            {
                using var composition = WatchV2ApplicationComposition.Create(
                    new WatchOptions { BaseUrl = "http://host-a" },
                    connectionPreferencesPath: Path.Combine(root, "connection.json"),
                    workspacePreferencesPath: Path.Combine(root, "workspace.json"));
                var window = composition.CreateMainWindow(initializeOnLoaded: false);

                composition.DisplayLanguageState.ApplyCommitted(WatchDisplayLanguage.English);

                Assert.Equal(
                    "Demand series",
                    Assert.IsType<Wpf.Ui.Controls.TextBlock>(window.FindName("DemandSeriesPageTitleText")).Text);
                Assert.Equal(
                    "Lifecycle",
                    Assert.IsType<Wpf.Ui.Controls.TextBlock>(window.FindName("DemandSeriesLifecycleFilterLabel")).Text);
                Assert.Equal(
                    "Apply filters",
                    Assert.IsType<Wpf.Ui.Controls.Button>(window.FindName("DemandSeriesApplyFiltersButton")).Content);
                Assert.Equal(
                    "Open Inspector",
                    Assert.IsType<Wpf.Ui.Controls.Button>(window.FindName("DemandSeriesOpenInspectorButton")).Content);
                Assert.Equal(
                    "Demand series page",
                    AutomationProperties.GetName(Assert.IsType<Grid>(window.FindName("DemandSeriesPage"))));
                var grid = Assert.IsType<DataGrid>(window.FindName("DemandSeriesGrid"));
                Assert.Equal("Lifecycle / current presence", grid.Columns[3].Header);
                Assert.Equal("Previous", Assert.IsType<Wpf.Ui.Controls.Button>(window.FindName("DemandSeriesPreviousButton")).Content);
                Assert.Equal("Next", Assert.IsType<Wpf.Ui.Controls.Button>(window.FindName("DemandSeriesNextButton")).Content);

                window.Close();
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });

    [Fact]
    public void Demand_series_language_switch_preserves_canonical_filter_scroll_and_focus() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-demand-series-state-{Guid.NewGuid():N}");
            try
            {
                using var composition = WatchV2ApplicationComposition.Create(
                    new WatchOptions { BaseUrl = "http://host-a" },
                    connectionPreferencesPath: Path.Combine(root, "connection.json"),
                    workspacePreferencesPath: Path.Combine(root, "workspace.json"));
                var window = composition.CreateMainWindow(initializeOnLoaded: false);
                Assert.IsAssignableFrom<System.Windows.Controls.Primitives.ButtonBase>(
                        window.FindName("DemandSeriesNavigationItem"))
                    .RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var lifecycle = Assert.IsType<Wpf.Ui.Controls.Button>(window.FindName("DemandSeriesLifecycleTrackingButton"));
                lifecycle.RaiseEvent(new RoutedEventArgs(Wpf.Ui.Controls.Button.ClickEvent));
                var presence = Assert.IsType<ComboBox>(window.FindName("DemandSeriesPresenceFilter"));
                presence.SelectedValue = "VISIBLE";
                var identity = Assert.IsType<TextBox>(window.FindName("DemandSeriesSeriesIdFilter"));
                identity.Text = "series-04";
                var scroll = Assert.IsType<ScrollViewer>(window.FindName("DemandSeriesScrollViewer"));
                window.Show();
                window.UpdateLayout();
                scroll.ScrollToVerticalOffset(37);
                var verticalOffset = scroll.VerticalOffset;
                Assert.Same(identity, System.Windows.Input.Keyboard.Focus(identity));
                var workspaceState = window.WorkspaceState;

                composition.DisplayLanguageState.ApplyCommitted(WatchDisplayLanguage.English);

                Assert.Same(workspaceState, window.WorkspaceState);
                Assert.Equal("TRACKING", lifecycle.Tag);
                Assert.Equal("VISIBLE", presence.SelectedValue);
                Assert.Equal("series-04", identity.Text);
                Assert.Same(identity, System.Windows.Input.Keyboard.FocusedElement);
                Assert.Equal(verticalOffset, scroll.VerticalOffset);

                window.Close();
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });
}
