using MesIngest.Core;

namespace MesIngest.Tests;

/// <summary>
/// Keyset pagination must keep DemandId as ascending tie-break under desc primary sort.
/// </summary>
public class DemandListPagingTieBreakTests
{
    [Fact]
    public void QueryPage_desc_with_identical_primary_values_pages_all_ids_without_gap_or_dup()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("id-00", now),
            Demand("id-01", now),
            Demand("id-02", now),
            Demand("id-03", now),
            Demand("id-04", now),
        ]));

        var collected = CollectAllIds(store, DemandSortColumn.Dates, SortDirection.Desc, limit: 2, asOf: now);

        Assert.Equal(new[] { "id-00", "id-01", "id-02", "id-03", "id-04" }, collected);
        Assert.Equal(collected.Count, collected.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void QueryPage_desc_page_boundary_inside_tie_group_has_no_gap_or_dup()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var earlier = now.AddHours(-1);
        var later = now.AddHours(1);
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("early", earlier),
            Demand("tie-a", now),
            Demand("tie-b", now),
            Demand("tie-c", now),
            Demand("late", later),
        ]));

        // dates desc, DemandId asc within ties → late, tie-a, tie-b, tie-c, early
        // limit=2 puts the cursor on tie-a (inside the tie group).
        var collected = CollectAllIds(store, DemandSortColumn.Dates, SortDirection.Desc, limit: 2, asOf: now);

        Assert.Equal(new[] { "late", "tie-a", "tie-b", "tie-c", "early" }, collected);
        Assert.Equal(collected.Count, collected.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void QueryPage_asc_and_desc_cover_same_ids_for_dates_and_taskType()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", now, taskType: "ALPHA"),
            Demand("b", now, taskType: "ALPHA"),
            Demand("c", now.AddHours(1), taskType: "BRAVO"),
            Demand("d", now.AddHours(-1), taskType: "CHARLIE"),
        ]));

        var datesDesc = CollectAllIds(store, DemandSortColumn.Dates, SortDirection.Desc, limit: 2, asOf: now);
        var datesAsc = CollectAllIds(store, DemandSortColumn.Dates, SortDirection.Asc, limit: 2, asOf: now);
        Assert.Equal(new[] { "c", "a", "b", "d" }, datesDesc);
        Assert.Equal(new[] { "d", "a", "b", "c" }, datesAsc);
        Assert.Equal(
            datesDesc.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            datesAsc.OrderBy(id => id, StringComparer.Ordinal).ToArray());

        var taskDesc = CollectAllIds(store, DemandSortColumn.TaskType, SortDirection.Desc, limit: 2, asOf: now);
        var taskAsc = CollectAllIds(store, DemandSortColumn.TaskType, SortDirection.Asc, limit: 2, asOf: now);
        Assert.Equal(new[] { "d", "c", "a", "b" }, taskDesc);
        Assert.Equal(new[] { "a", "b", "c", "d" }, taskAsc);
        Assert.Equal(
            taskDesc.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            taskAsc.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData(DemandSortColumn.Dates)]
    [InlineData(DemandSortColumn.CreatedAt)]
    [InlineData(DemandSortColumn.MesLastSeenAt)]
    [InlineData(DemandSortColumn.TaskType)]
    [InlineData(DemandSortColumn.Sublot)]
    [InlineData(DemandSortColumn.GoneAt)]
    [InlineData(DemandSortColumn.Status)]
    [InlineData(DemandSortColumn.Area)]
    [InlineData(DemandSortColumn.Eqp)]
    [InlineData(DemandSortColumn.Step)]
    [InlineData(DemandSortColumn.Package)]
    [InlineData(DemandSortColumn.LocationRisk)]
    [InlineData(DemandSortColumn.DisappearCount)]
    public void QueryPage_desc_identical_primary_covers_all_allow_list_columns(DemandSortColumn sortBy)
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        var status = sortBy == DemandSortColumn.GoneAt ? DemandStatus.Gone : DemandStatus.Visible;
        store.ReplaceState(new ProjectionState(
        [
            TiedDemand("id-00", now, status),
            TiedDemand("id-01", now, status),
            TiedDemand("id-02", now, status),
            TiedDemand("id-03", now, status),
            TiedDemand("id-04", now, status),
        ]));

        var collected = CollectAllIds(store, sortBy, SortDirection.Desc, limit: 2, asOf: now, status: status);

        Assert.Equal(new[] { "id-00", "id-01", "id-02", "id-03", "id-04" }, collected);
        Assert.Equal(collected.Count, collected.Distinct(StringComparer.Ordinal).Count());
    }

    private static List<string> CollectAllIds(
        InMemoryTransportDemandStore store,
        DemandSortColumn sortBy,
        SortDirection direction,
        int limit,
        DateTimeOffset asOf,
        DemandStatus status = DemandStatus.Visible)
    {
        var collected = new List<string>();
        string? cursor = null;
        for (var pages = 0; pages < 20; pages++)
        {
            var page = store.QueryPage(new DemandListQuery
            {
                Status = status,
                SortBy = sortBy,
                Direction = direction,
                Limit = limit,
                Cursor = cursor,
                AsOf = asOf,
                GoneAtFrom = status == DemandStatus.Gone ? asOf.AddDays(-30) : null,
            });
            collected.AddRange(page.Items.Select(d => d.DemandId));
            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        return collected;
    }

    private static TransportDemand Demand(
        string id,
        DateTimeOffset dates,
        string taskType = "DIE_TO_OVEN") =>
        new()
        {
            DemandId = id,
            TaskType = taskType,
            Sublot = "Q1",
            Dates = dates,
            Status = DemandStatus.Visible,
            MesLastSeenAt = dates,
            CreatedAt = dates,
        };

    private static TransportDemand TiedDemand(string id, DateTimeOffset stamp, DemandStatus status) =>
        new()
        {
            DemandId = id,
            TaskType = "SAME_TYPE",
            Sublot = "SAME_SUBLOT",
            Dates = stamp,
            Status = status,
            MesLastSeenAt = stamp,
            CreatedAt = stamp,
            GoneAt = status == DemandStatus.Gone ? stamp : null,
        };
}
