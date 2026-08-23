using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchErrorSearchQueryTests
{
    [Fact]
    public void Default_latest_query_starts_on_the_unfrozen_first_page()
    {
        var query = WatchErrorSearchQueries.StartLatest();

        Assert.Empty(query.Filter.Categories);
        Assert.Empty(query.Filter.ErrorCodes);
        Assert.Empty(query.Filter.ActivityStates);
        Assert.Null(query.Filter.SeriesId);
        Assert.Null(query.Filter.DemandId);
        Assert.Null(query.Filter.SublotContains);
        Assert.Equal(ErrorSearchWindowSelection.Last7Days, query.Window);
        Assert.Equal(ErrorSearchQuery.DefaultPageSize, query.PageSize);
        Assert.Null(query.SnapshotReference);
        Assert.Null(query.Cursor);
        Assert.Equal(ErrorSearchOrder.Default, query.Order);
    }

    [Fact]
    public void Latest_query_normalizes_every_supported_condition_and_range_without_an_area_dimension()
    {
        var query = WatchErrorSearchQueries.StartLatest(
            new ErrorSearchFilter
            {
                Categories = [" data_format ", "DATA_FORMAT"],
                ErrorCodes = [" invalid_mes_field_format ", "INVALID_MES_FIELD_FORMAT"],
                ActivityStates = [" ended ", "ACTIVE", "ENDED"],
                SeriesId = " series-ticket-22 ",
                DemandId = " demand-ticket-22 ",
                SublotContains = " sl-ticket-22 ",
            },
            ErrorSearchWindowSelection.Last24Hours,
            pageSize: ErrorSearchQuery.MaximumPageSize);

        Assert.Equal(["DATA_FORMAT"], query.Filter.Categories);
        Assert.Equal(["INVALID_MES_FIELD_FORMAT"], query.Filter.ErrorCodes);
        Assert.Equal(["ACTIVE", "ENDED"], query.Filter.ActivityStates);
        Assert.Equal("SERIES-TICKET-22", query.Filter.SeriesId);
        Assert.Equal("DEMAND-TICKET-22", query.Filter.DemandId);
        Assert.Equal("SL-TICKET-22", query.Filter.SublotContains);
        Assert.Equal(ErrorSearchWindowSelection.Last24Hours, query.Window);
        Assert.Equal(200, query.PageSize);
        Assert.DoesNotContain(
            typeof(ErrorSearchFilter).GetProperties(),
            property => property.Name.Contains("Area", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(
            ErrorSearchWindowSelection.Last7Days,
            WatchErrorSearchQueries.StartLatest(window: ErrorSearchWindowSelection.Last7Days).Window);
        Assert.Equal(
            ErrorSearchWindowSelection.Last30Days,
            WatchErrorSearchQueries.StartLatest(window: ErrorSearchWindowSelection.Last30Days).Window);
        Assert.Equal(
            ErrorSearchWindowSelection.AllHistory,
            WatchErrorSearchQueries.StartLatest(window: ErrorSearchWindowSelection.AllHistory).Window);
        Assert.Equal(
            ErrorSearchQuery.DefaultPageSize,
            WatchErrorSearchQueries.StartLatest(pageSize: 100).PageSize);
    }

    [Fact]
    public void Frozen_cursor_paging_is_rebuilt_from_the_visible_committed_snapshot()
    {
        var snapshot = Snapshot(
            pageNumber: 2,
            totalPages: 4,
            nextCursor: "cursor-page-three");
        var failedDraft = WatchErrorSearchQueries.StartLatest(
            new ErrorSearchFilter
            {
                Categories = ["DATA_COMPLETENESS"],
                ErrorCodes = ["REQUIRED_MES_FIELD_MISSING"],
                SeriesId = "series-failed-draft",
            },
            ErrorSearchWindowSelection.Last30Days,
            pageSize: 200);

        var first = WatchErrorSearchQueries.OpenFrozenPage(snapshot, 1, cursor: null);
        var next = Assert.IsType<ErrorSearchQuery>(
            WatchErrorSearchQueries.OpenNextFrozenPage(snapshot));
        var direct = WatchErrorSearchQueries.OpenFrozenPage(
            snapshot,
            targetPageNumber: 4,
            cursor: "cursor-page-four");

        AssertFrozen(first, snapshot, expectedCursor: null);
        AssertFrozen(next, snapshot, "cursor-page-three");
        AssertFrozen(direct, snapshot, "cursor-page-four");
        Assert.NotEqual(failedDraft.Filter, next.Filter);
        Assert.NotEqual(failedDraft.Window, next.Window);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WatchErrorSearchQueries.OpenFrozenPage(snapshot, 5, "cursor-page-five"));
        Assert.Throws<ArgumentException>(() =>
            WatchErrorSearchQueries.OpenFrozenPage(snapshot, 3, cursor: null));
    }

    [Fact]
    public void Frozen_rolling_window_preserves_its_kind_for_the_snapshot_hash()
    {
        var snapshot = Snapshot(
            pageNumber: 1,
            totalPages: 2,
            nextCursor: "cursor-page-two",
            windowKind: ErrorSearchWindowKinds.Last7Days);

        var next = Assert.IsType<ErrorSearchQuery>(
            WatchErrorSearchQueries.OpenNextFrozenPage(snapshot));

        Assert.Equal(ErrorSearchWindowKinds.Last7Days, next.Window.Kind);
        Assert.Null(next.Window.FromUtc);
        Assert.Null(next.Window.ToUtc);
        Assert.Equal(snapshot.SnapshotReference, next.SnapshotReference);
        Assert.Equal("cursor-page-two", next.Cursor);
    }

    [Fact]
    public void Empty_or_last_pages_do_not_offer_an_impossible_next_request()
    {
        Assert.Null(WatchErrorSearchQueries.OpenNextFrozenPage(
            Snapshot(pageNumber: 1, totalPages: 0, nextCursor: null)));
        Assert.Null(WatchErrorSearchQueries.OpenNextFrozenPage(
            Snapshot(pageNumber: 3, totalPages: 3, nextCursor: null)));
    }

    private static void AssertFrozen(
        ErrorSearchQuery actual,
        ErrorSearchListSnapshot snapshot,
        string? expectedCursor)
    {
        Assert.Equal(snapshot.Filter.Categories, actual.Filter.Categories);
        Assert.Equal(snapshot.Filter.ErrorCodes, actual.Filter.ErrorCodes);
        Assert.Equal(snapshot.Filter.ActivityStates, actual.Filter.ActivityStates);
        Assert.Equal(snapshot.Filter.SeriesId, actual.Filter.SeriesId);
        Assert.Equal(snapshot.Filter.DemandId, actual.Filter.DemandId);
        Assert.Equal(snapshot.Filter.SublotContains, actual.Filter.SublotContains);
        Assert.Equal(snapshot.PageSize, actual.PageSize);
        Assert.Equal(snapshot.SnapshotReference, actual.SnapshotReference);
        Assert.Equal(expectedCursor, actual.Cursor);
        Assert.Equal(snapshot.Order, actual.Order);
        Assert.Equal(ErrorSearchWindowKinds.Custom, actual.Window.Kind);
        Assert.Equal(snapshot.Window.FromUtc, actual.Window.FromUtc);
        Assert.Equal(snapshot.Window.ToUtc, actual.Window.ToUtc);
    }

    private static ErrorSearchListSnapshot Snapshot(
        int pageNumber,
        int totalPages,
        string? nextCursor,
        string windowKind = ErrorSearchWindowKinds.Custom)
    {
        var asOf = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        return new ErrorSearchListSnapshot(
            "snapshot-visible-ticket-22",
            new ErrorSearchSnapshotIdentity(
                HistoryEpoch.FromGuid(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                asOf,
                "commit-visible-ticket-22",
                ProjectionSequence: 220,
                asOf.AddMinutes(-1),
                "poll-visible-ticket-22"),
            new ErrorSearchFilter
            {
                Categories = ["DATA_FORMAT"],
                ErrorCodes = ["INVALID_MES_FIELD_FORMAT"],
                ActivityStates = [ErrorSearchActivityStates.Ended],
                SeriesId = "SERIES-VISIBLE-TICKET-22",
                DemandId = "DEMAND-VISIBLE-TICKET-22",
                SublotContains = "SL-VISIBLE-TICKET-22",
            },
            windowKind == ErrorSearchWindowKinds.Custom
                ? new ErrorSearchResolvedWindow(
                    ErrorSearchWindowKinds.Custom,
                    asOf.AddDays(-2),
                    asOf.AddDays(-1))
                : new ErrorSearchResolvedWindow(
                    windowKind,
                    asOf.AddDays(-7),
                    asOf),
            ErrorSearchOrder.Default,
            TotalSeriesCount: totalPages == 0 ? 0 : 350,
            new ErrorSearchFacets([], []),
            PageSize: 100,
            PageNumber: pageNumber,
            TotalPages: totalPages,
            Items: [],
            NextCursor: nextCursor,
            HasMore: nextCursor is not null);
    }
}
