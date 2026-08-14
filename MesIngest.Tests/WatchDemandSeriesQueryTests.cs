using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchDemandSeriesQueryTests
{
    [Fact]
    public void Frozen_paging_is_rebuilt_from_the_visible_committed_snapshot()
    {
        var snapshot = Snapshot(
            filter: new DemandSeriesBrowseFilter
            {
                Lifecycles = ["TRACKING"],
                MesAreas = ["A1-1"],
            },
            pageNumber: 2,
            totalPages: 4,
            nextCursor: "cursor-page-three");
        var failedDraft = new DemandSeriesBrowseQuery(
            new DemandSeriesBrowseFilter
            {
                Lifecycles = ["ARCHIVED"],
                MesAreas = ["B2-2"],
            },
            PageSize: 50);

        var previous = WatchDemandSeriesQueries.OpenFrozenPage(snapshot, 1);
        var direct = WatchDemandSeriesQueries.OpenFrozenPage(snapshot, 4);
        var next = Assert.IsType<DemandSeriesBrowseQuery>(
            WatchDemandSeriesQueries.OpenNextFrozenPage(snapshot));

        Assert.Equal(["TRACKING"], previous.Filter.Lifecycles);
        Assert.Equal(["A1-1"], previous.Filter.MesAreas);
        Assert.Equal(snapshot.SnapshotReference, previous.SnapshotReference);
        Assert.Equal(snapshot.PageSize, previous.PageSize);
        Assert.Equal(1, previous.PageNumber);
        Assert.Null(previous.Cursor);

        Assert.Equal(4, direct.PageNumber);
        Assert.Equal(snapshot.SnapshotReference, direct.SnapshotReference);
        Assert.Equal(snapshot.Order, direct.Order);

        Assert.Equal(["TRACKING"], next.Filter.Lifecycles);
        Assert.Equal(["A1-1"], next.Filter.MesAreas);
        Assert.Equal(snapshot.SnapshotReference, next.SnapshotReference);
        Assert.Equal("cursor-page-three", next.Cursor);
        Assert.Equal(1, next.PageNumber);
        Assert.NotEqual(failedDraft.Filter, next.Filter);
    }

    [Fact]
    public void First_page_filters_never_reuse_a_frozen_snapshot_or_cursor()
    {
        var query = WatchDemandSeriesQueries.StartLatest(
            new DemandSeriesBrowseFilter
            {
                CurrentPresences = ["GONE"],
                SublotContains = "SL-20",
                DemandId = "demand-20",
                MesAreas = ["A1-1"],
            },
            pageSize: 200,
            pageNumber: 1);

        Assert.Equal(1, query.PageNumber);
        Assert.Equal(200, query.PageSize);
        Assert.Equal(["GONE"], query.Filter.CurrentPresences);
        Assert.Equal("SL-20", query.Filter.SublotContains);
        Assert.Equal("demand-20", query.Filter.DemandId);
        Assert.Equal(["A1-1"], query.Filter.MesAreas);
        Assert.Null(query.SnapshotReference);
        Assert.Null(query.Cursor);
        Assert.Equal(DemandSeriesBrowseOrder.Default, query.Order);
    }

    [Fact]
    public void Empty_or_last_pages_do_not_offer_an_impossible_next_request()
    {
        Assert.Null(WatchDemandSeriesQueries.OpenNextFrozenPage(
            Snapshot(new DemandSeriesBrowseFilter(), 1, 0, nextCursor: null)));
        Assert.Null(WatchDemandSeriesQueries.OpenNextFrozenPage(
            Snapshot(new DemandSeriesBrowseFilter(), 3, 3, nextCursor: null)));
    }

    private static DemandSeriesListSnapshot Snapshot(
        DemandSeriesBrowseFilter filter,
        int pageNumber,
        int totalPages,
        string? nextCursor) => new(
        new DemandSeriesSnapshotIdentity(
            "commit-visible",
            20,
            DateTimeOffset.Parse("2026-08-14T05:06:07Z"),
            "poll-visible"),
        "snapshot-visible",
        filter,
        DemandSeriesBrowseOrder.Default,
        ExactTotalCount: totalPages == 0 ? 0 : 350,
        new DemandSeriesFacets(300, 50, 250, 100, 10),
        PageSize: 100,
        PageNumber: pageNumber,
        TotalPages: totalPages,
        Items: [],
        NextCursor: nextCursor,
        HasMore: nextCursor is not null);
}
