using MesIngest.Watch;

namespace MesIngest.Tests;

public class DemandListProjectorTests
{
    private static WatchDemandDto Demand(
        string demandId,
        string taskType,
        string sublot,
        string status,
        DateTimeOffset lastSeen,
        DateTimeOffset? dates = null) =>
        new(
            DemandId: demandId,
            TaskType: taskType,
            Sublot: sublot,
            Area: null,
            Eqp: null,
            Step: null,
            Dates: dates ?? lastSeen,
            Package: null,
            Status: status,
            MesLastSeenAt: lastSeen,
            DisappearCount: 0,
            LocationRisk: false,
            LocationRiskCode: null,
            CreatedAt: lastSeen,
            GoneAt: null);

    [Fact]
    public void Filters_by_task_type_sublot_and_status_case_insensitive_contains()
    {
        var rows = new[]
        {
            Demand("d1", "DIE_TO_OVEN", "Q-100", "VISIBLE", new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero)),
            Demand("d2", "DIE_TO_WIRE_STAGING", "Q-200", "VISIBLE", new DateTimeOffset(2026, 8, 2, 11, 0, 0, TimeSpan.Zero)),
            Demand("d3", "DIE_TO_OVEN", "Q-100", "GONE", new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero)),
        };

        var filtered = DemandListProjector.FilterSort(
            rows,
            new DemandListQuery(TaskType: "oven", Sublot: "100", Status: "gone", SortBy: DemandSortField.MesLastSeenAt, Ascending: true));

        Assert.Single(filtered);
        Assert.Equal("d3", filtered[0].DemandId);
    }

    [Fact]
    public void Default_query_sorts_by_dates_descending_then_demand_id_ascending()
    {
        var rows = new[]
        {
            Demand(
                "b",
                "DIE_TO_OVEN",
                "A",
                "VISIBLE",
                lastSeen: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
                dates: new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)),
            Demand(
                "a",
                "DIE_TO_OVEN",
                "B",
                "VISIBLE",
                lastSeen: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                dates: new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)),
            Demand(
                "c",
                "DIE_TO_OVEN",
                "C",
                "VISIBLE",
                lastSeen: new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero),
                dates: new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero)),
        };

        var sorted = DemandListProjector.FilterSort(rows, new DemandListQuery());

        Assert.Equal(DemandSortField.Dates, new DemandListQuery().SortBy);
        Assert.False(new DemandListQuery().Ascending);
        Assert.Equal(new[] { "c", "a", "b" }, sorted.Select(d => d.DemandId));
    }

    [Fact]
    public void Sorts_by_mes_last_seen_when_explicitly_requested()
    {
        var rows = new[]
        {
            Demand("old", "DIE_TO_OVEN", "A", "VISIBLE", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            Demand("new", "DIE_TO_OVEN", "B", "VISIBLE", new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero)),
            Demand("mid", "DIE_TO_OVEN", "C", "VISIBLE", new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)),
        };

        var sorted = DemandListProjector.FilterSort(
            rows,
            new DemandListQuery(SortBy: DemandSortField.MesLastSeenAt, Ascending: false));

        Assert.Equal(new[] { "new", "mid", "old" }, sorted.Select(d => d.DemandId));
    }

    [Fact]
    public void Sorts_by_task_type_then_keeps_stable_relative_order_for_ties_via_demand_id()
    {
        var rows = new[]
        {
            Demand("b", "WIRE_TO_NITROGEN", "1", "VISIBLE", new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)),
            Demand("a", "DIE_TO_OVEN", "1", "VISIBLE", new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)),
            Demand("c", "DIE_TO_OVEN", "2", "VISIBLE", new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)),
        };

        var sorted = DemandListProjector.FilterSort(
            rows,
            new DemandListQuery(SortBy: DemandSortField.TaskType, Ascending: true));

        Assert.Equal(new[] { "a", "c", "b" }, sorted.Select(d => d.DemandId));
    }

    [Fact]
    public void Same_dates_instant_with_different_offsets_sort_by_instant_then_demand_id()
    {
        // Same UTC instant expressed with +00 and +08 offsets.
        var instant = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero);
        var asBeijing = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(instant.UtcTicks, asBeijing.UtcTicks);

        var rows = new[]
        {
            Demand("b", "T", "1", "VISIBLE", instant, dates: asBeijing),
            Demand("a", "T", "1", "VISIBLE", instant, dates: instant),
        };

        var sorted = DemandListProjector.FilterSort(
            rows,
            new DemandListQuery(SortBy: DemandSortField.Dates, Ascending: false));

        Assert.Equal(new[] { "a", "b" }, sorted.Select(d => d.DemandId));
    }
}
