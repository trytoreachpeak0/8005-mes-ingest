using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchReadabilityAuditQueryTests
{
    [Fact]
    public void Frozen_paging_is_rebuilt_from_the_visible_committed_audit_snapshot()
    {
        var snapshot = Snapshot(
            filter: new ReadabilityAuditFilter
            {
                ReadabilityStates = [ExternalReadabilityStates.NotReadable],
                Blockers = ["INVALID_MES_FIELD_FORMAT"],
                MesAreas = ["A1-1"],
            },
            pageNumber: 2,
            totalPages: 4,
            nextCursor: "cursor-page-three");
        var failedDraft = new ReadabilityAuditQuery(
            new ReadabilityAuditFilter
            {
                ReadabilityStates = [ExternalReadabilityStates.Readable],
                MesAreas = ["B2-2"],
            },
            PageSize: 50);

        var previous = WatchReadabilityAuditQueries.OpenFrozenPage(snapshot, 1);
        var direct = WatchReadabilityAuditQueries.OpenFrozenPage(snapshot, 4);
        var next = Assert.IsType<ReadabilityAuditQuery>(
            WatchReadabilityAuditQueries.OpenNextFrozenPage(snapshot));

        Assert.Equal([ExternalReadabilityStates.NotReadable], previous.Filter.ReadabilityStates);
        Assert.Equal(["INVALID_MES_FIELD_FORMAT"], previous.Filter.Blockers);
        Assert.Equal(["A1-1"], previous.Filter.MesAreas);
        Assert.Equal(snapshot.SnapshotReference, previous.SnapshotReference);
        Assert.Equal(snapshot.PageSize, previous.PageSize);
        Assert.Equal(1, previous.PageNumber);
        Assert.Null(previous.Cursor);

        Assert.Equal(4, direct.PageNumber);
        Assert.Equal(snapshot.SnapshotReference, direct.SnapshotReference);
        Assert.Equal(snapshot.Order, direct.Order);

        Assert.Equal([ExternalReadabilityStates.NotReadable], next.Filter.ReadabilityStates);
        Assert.Equal(["A1-1"], next.Filter.MesAreas);
        Assert.Equal(snapshot.SnapshotReference, next.SnapshotReference);
        Assert.Equal("cursor-page-three", next.Cursor);
        Assert.Equal(1, next.PageNumber);
        Assert.NotEqual(failedDraft.Filter, next.Filter);
    }

    [Fact]
    public void Latest_audit_requests_never_reuse_a_frozen_snapshot_or_cursor()
    {
        var query = WatchReadabilityAuditQueries.StartLatest(
            new ReadabilityAuditFilter
            {
                ReadabilityStates = [ExternalReadabilityStates.NotReadable],
                WorkTypes = ["WIRE_TO_NITROGEN"],
                Blockers = ["DEMAND_GONE"],
                SublotContains = "SL-20",
                DemandId = "demand-20",
                MesAreas = ["A1-1"],
            },
            pageSize: 200,
            pageNumber: 3);

        Assert.Equal(3, query.PageNumber);
        Assert.Equal(200, query.PageSize);
        Assert.Equal([ExternalReadabilityStates.NotReadable], query.Filter.ReadabilityStates);
        Assert.Equal(["WIRE_TO_NITROGEN"], query.Filter.WorkTypes);
        Assert.Equal(["DEMAND_GONE"], query.Filter.Blockers);
        Assert.Equal("SL-20", query.Filter.SublotContains);
        Assert.Equal("demand-20", query.Filter.DemandId);
        Assert.Equal(["A1-1"], query.Filter.MesAreas);
        Assert.Null(query.SnapshotReference);
        Assert.Null(query.Cursor);
        Assert.Equal(ReadabilityAuditOrder.Default, query.Order);
    }

    [Fact]
    public void Empty_or_last_audit_pages_do_not_offer_an_impossible_next_request()
    {
        Assert.Null(WatchReadabilityAuditQueries.OpenNextFrozenPage(
            Snapshot(new ReadabilityAuditFilter(), 1, 0, nextCursor: null)));
        Assert.Null(WatchReadabilityAuditQueries.OpenNextFrozenPage(
            Snapshot(new ReadabilityAuditFilter(), 3, 3, nextCursor: null)));
    }

    private static ReadabilityAuditListSnapshot Snapshot(
        ReadabilityAuditFilter filter,
        int pageNumber,
        int totalPages,
        string? nextCursor) => new(
        new ReadabilityAuditSnapshotIdentity(
            "commit-visible",
            20,
            DateTimeOffset.Parse("2026-08-14T05:06:07Z"),
            "poll-visible",
            CatalogRevision: 7),
        "snapshot-visible",
        filter,
        ReadabilityAuditOrder.Default,
        ExactTotalDemandCount: totalPages == 0 ? 0 : 350,
        new ReadabilityAuditFacets([], []),
        PageSize: 100,
        PageNumber: pageNumber,
        TotalPages: totalPages,
        Items: [],
        NextCursor: nextCursor,
        HasMore: nextCursor is not null);
}
