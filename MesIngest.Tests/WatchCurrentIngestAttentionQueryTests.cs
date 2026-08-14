using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchCurrentIngestAttentionQueryTests
{
    [Fact]
    public void Latest_and_overview_queries_normalize_filters_pages_and_ignore_area_scope()
    {
        var latest = WatchCurrentIngestAttentionQueries.StartLatest(
            kinds:
            [
                $" {CurrentIngestAttentionKinds.UnassignedMesObservation} ",
                CurrentIngestAttentionKinds.SeriesError,
                CurrentIngestAttentionKinds.SeriesError,
            ],
            severities:
            [
                CurrentIngestAttentionSeverities.Warning,
                $" {CurrentIngestAttentionSeverities.Error} ",
            ],
            pageSize: 200,
            pageNumber: 3);
        var fromOverview = WatchCurrentIngestAttentionQueries.FromNavigation(
            new OverviewNavigationIntent(
                OverviewNavigationTargets.CurrentIngestAttention,
                PageNumber: 4,
                MesAreas: ["A1-1", "B2-2"],
                AttentionKinds:
                [
                    CurrentIngestAttentionKinds.TaskTypeProtection,
                    CurrentIngestAttentionKinds.PollRunFailure,
                ],
                AttentionSeverities: [CurrentIngestAttentionSeverities.Warning]),
            pageSize: 100);

        Assert.Equal(
            [
                CurrentIngestAttentionKinds.SeriesError,
                CurrentIngestAttentionKinds.UnassignedMesObservation,
            ],
            latest.Kinds);
        Assert.Equal(
            [
                CurrentIngestAttentionSeverities.Error,
                CurrentIngestAttentionSeverities.Warning,
            ],
            latest.Severities);
        Assert.Equal(200, latest.PageSize);
        Assert.Equal(3, latest.PageNumber);
        Assert.Equal(CurrentIngestAttentionOrder.Default, latest.Order);

        Assert.Equal(
            [
                CurrentIngestAttentionKinds.PollRunFailure,
                CurrentIngestAttentionKinds.TaskTypeProtection,
            ],
            fromOverview.Kinds);
        Assert.Equal([CurrentIngestAttentionSeverities.Warning], fromOverview.Severities);
        Assert.Equal(100, fromOverview.PageSize);
        Assert.Equal(4, fromOverview.PageNumber);
        Assert.DoesNotContain(
            typeof(CurrentIngestAttentionQuery).GetProperties(),
            property => property.Name.Contains("Area", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Page_navigation_uses_the_visible_host_filters_page_size_and_order()
    {
        var snapshot = Snapshot(
            pageNumber: 2,
            totalPages: 4,
            kinds: [CurrentIngestAttentionKinds.SeriesError],
            severities: [CurrentIngestAttentionSeverities.Error]);

        var previous = Assert.IsType<CurrentIngestAttentionQuery>(
            WatchCurrentIngestAttentionQueries.OpenPreviousPage(snapshot));
        var direct = WatchCurrentIngestAttentionQueries.OpenPage(snapshot, 4);
        var next = Assert.IsType<CurrentIngestAttentionQuery>(
            WatchCurrentIngestAttentionQueries.OpenNextPage(snapshot));

        Assert.Equal(1, previous.PageNumber);
        Assert.Equal(4, direct.PageNumber);
        Assert.Equal(3, next.PageNumber);
        Assert.All([previous, direct, next], query =>
        {
            Assert.Equal([CurrentIngestAttentionKinds.SeriesError], query.Kinds);
            Assert.Equal([CurrentIngestAttentionSeverities.Error], query.Severities);
            Assert.Equal(snapshot.PageSize, query.PageSize);
            Assert.Equal(snapshot.Order, query.Order);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WatchCurrentIngestAttentionQueries.OpenPage(snapshot, 5));
    }

    [Fact]
    public void Empty_and_edge_pages_do_not_offer_impossible_navigation()
    {
        var empty = Snapshot(pageNumber: 1, totalPages: 0);
        var first = Snapshot(pageNumber: 1, totalPages: 3);
        var last = Snapshot(pageNumber: 3, totalPages: 3);

        Assert.Null(WatchCurrentIngestAttentionQueries.OpenPreviousPage(empty));
        Assert.Null(WatchCurrentIngestAttentionQueries.OpenNextPage(empty));
        Assert.Null(WatchCurrentIngestAttentionQueries.OpenPreviousPage(first));
        Assert.Null(WatchCurrentIngestAttentionQueries.OpenNextPage(last));
    }

    [Fact]
    public void Series_error_drill_is_explicit_active_identity_on_a_fresh_first_page()
    {
        var item = Item(
            kind: CurrentIngestAttentionKinds.SeriesError,
            stableIdentity:
                "series-22:REQUIRED_MES_FIELD_MISSING:DEMAND:demand-22:EQP",
            seriesId: "series-22",
            errorCode: "REQUIRED_MES_FIELD_MISSING",
            target: "DEMAND:demand-22",
            subjectKind: "EQP",
            navigation: new OverviewNavigationIntent(
                OverviewNavigationTargets.ErrorSearch,
                PageNumber: 1,
                ErrorActivityStates: [ErrorSearchActivityStates.Active],
                ErrorWindow: ErrorSearchWindowKinds.AllHistory,
                SeriesId: "series-22"));

        var drill = Assert.IsType<ErrorSearchQuery>(
            WatchCurrentIngestAttentionQueries.CreateErrorSearchDrill(item));

        Assert.Equal(["DATA_COMPLETENESS"], drill.Filter.Categories);
        Assert.Equal(["REQUIRED_MES_FIELD_MISSING"], drill.Filter.ErrorCodes);
        Assert.Equal([ErrorSearchActivityStates.Active], drill.Filter.ActivityStates);
        Assert.Equal("SERIES-22", drill.Filter.SeriesId);
        Assert.Null(drill.Filter.DemandId);
        Assert.Null(drill.Filter.SublotContains);
        Assert.Equal(ErrorSearchWindowKinds.AllHistory, drill.Window.Kind);
        Assert.Equal(ErrorSearchQuery.DefaultPageSize, drill.PageSize);
        Assert.Null(drill.SnapshotReference);
        Assert.Null(drill.Cursor);
        Assert.Equal(ErrorSearchOrder.Default, drill.Order);
    }

    [Fact]
    public void Series_error_drill_uses_the_error_search_default_window_when_intent_omits_it()
    {
        var item = Item(
            kind: CurrentIngestAttentionKinds.SeriesError,
            stableIdentity:
                "series-22:REQUIRED_MES_FIELD_MISSING:DEMAND:demand-22:EQP",
            seriesId: "series-22",
            errorCode: "REQUIRED_MES_FIELD_MISSING",
            target: "DEMAND:demand-22",
            subjectKind: "EQP",
            navigation: new OverviewNavigationIntent(
                OverviewNavigationTargets.ErrorSearch,
                SeriesId: "series-22"));

        var drill = Assert.IsType<ErrorSearchQuery>(
            WatchCurrentIngestAttentionQueries.CreateErrorSearchDrill(item));

        Assert.Equal(ErrorSearchWindowKinds.Last7Days, drill.Window.Kind);
        Assert.Null(drill.SnapshotReference);
        Assert.Null(drill.Cursor);
    }

    [Fact]
    public void Series_error_drill_uses_the_current_item_identity_not_a_stale_navigation_identity()
    {
        var item = Item(
            kind: CurrentIngestAttentionKinds.SeriesError,
            stableIdentity:
                "authoritative-series:REQUIRED_MES_FIELD_MISSING:DEMAND:demand-22:EQP",
            seriesId: "authoritative-series",
            errorCode: "REQUIRED_MES_FIELD_MISSING",
            target: "DEMAND:demand-22",
            subjectKind: "EQP",
            navigation: new OverviewNavigationIntent(
                OverviewNavigationTargets.ErrorSearch,
                SeriesId: "stale-navigation-series"));

        var drill = Assert.IsType<ErrorSearchQuery>(
            WatchCurrentIngestAttentionQueries.CreateErrorSearchDrill(item));

        Assert.Equal("AUTHORITATIVE-SERIES", drill.Filter.SeriesId);
        Assert.NotEqual("STALE-NAVIGATION-SERIES", drill.Filter.SeriesId);
        Assert.Equal([ErrorSearchActivityStates.Active], drill.Filter.ActivityStates);
        Assert.Null(drill.SnapshotReference);
        Assert.Null(drill.Cursor);
    }

    [Fact]
    public void Global_attention_items_do_not_offer_a_series_error_drill()
    {
        var pollFailure = Item(
            kind: CurrentIngestAttentionKinds.PollRunFailure,
            stableIdentity: "POLL_RUN_FAILURE",
            seriesId: null,
            errorCode: null,
            target: "POLL_TRACE:poll-22",
            subjectKind: "POLL_TRACE",
            navigation: new OverviewNavigationIntent(
                OverviewNavigationTargets.PollTrace,
                PollTraceId: "poll-22"));

        Assert.Null(WatchCurrentIngestAttentionQueries.CreateErrorSearchDrill(pollFailure));
    }

    private static CurrentIngestAttentionSnapshot Snapshot(
        int pageNumber,
        int totalPages,
        IReadOnlyList<string>? kinds = null,
        IReadOnlyList<string>? severities = null) => new(
        Identity(),
        ExactTotalItemCount: totalPages == 0 ? 0 : 350,
        new CurrentIngestAttentionFacets([], []),
        CurrentIngestAttentionOrder.Default,
        PageSize: 100,
        PageNumber: pageNumber,
        TotalPages: totalPages,
        Kinds: kinds ?? [],
        Severities: severities ?? [],
        Items: []);

    private static CurrentIngestAttentionItemSnapshot Item(
        string kind,
        string stableIdentity,
        string? seriesId,
        string? errorCode,
        string? target,
        string? subjectKind,
        OverviewNavigationIntent navigation) => new(
        kind,
        CurrentIngestAttentionSeverities.Error,
        DateTimeOffset.Parse("2026-08-14T05:06:07Z"),
        stableIdentity,
        seriesId,
        WorkType: null,
        errorCode,
        target,
        subjectKind,
        new CurrentIngestAttentionEvidenceSnapshot(
            PollTraceId: "poll-22",
            SeriesId: seriesId,
            DemandId: "demand-22"),
        navigation);

    private static OperationalSnapshotIdentity Identity() => new(
        "commit-attention-22",
        ProjectionSequence: 422,
        DateTimeOffset.Parse("2026-08-14T05:06:07Z"),
        "poll-attention-22",
        PollTraceHighWater: 33,
        CatalogRevision: 9,
        DateTimeOffset.Parse("2026-08-14T05:06:08Z"));
}
