using MesIngest.Core;

namespace MesIngest.Tests;

public class TransportDemandStoreListOrderTests
{
    private static TransportDemand Demand(string id, DateTimeOffset dates) => new()
    {
        DemandId = id,
        TaskType = "DIE_TO_OVEN",
        Sublot = "S1",
        Area = null,
        Eqp = null,
        Step = null,
        Dates = dates,
        Package = null,
        Status = DemandStatus.Visible,
        MesLastSeenAt = dates.AddHours(1),
        DisappearCount = 0,
        LocationRisk = false,
        LocationRiskCode = null,
        CreatedAt = dates,
        GoneAt = null,
    };

    [Fact]
    public void In_memory_list_orders_by_dates_desc_then_demand_id_asc()
    {
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("b", new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)),
            Demand("a", new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)),
            Demand("c", new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero)),
        ]));

        var ids = store.List().Select(d => d.DemandId).ToArray();

        Assert.Equal(new[] { "c", "a", "b" }, ids);
    }

    [Fact]
    public void Append_alerts_without_created_at_stamps_utc()
    {
        var store = new InMemoryTransportDemandStore();
        store.AppendAlerts(
        [
            new IngestAlert(
                Code: "POLL_FAILURE",
                TaskType: null,
                Sublot: null,
                DemandId: null,
                Message: "boom",
                CreatedAt: null),
        ]);

        var alert = Assert.Single(store.ListAlerts());
        Assert.NotNull(alert.CreatedAt);
        Assert.Equal(TimeSpan.Zero, alert.CreatedAt!.Value.Offset);
    }

    [Fact]
    public void In_memory_alert_query_pages_new_sort_columns_on_the_shared_contract()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryTransportDemandStore(clock: () => now);
        store.ReplaceState(ProjectionState.Empty,
        [
            new IngestAlert(AlertCodes.FieldDrift, TaskType: "T-B", Sublot: "S-B"),
            new IngestAlert(AlertCodes.FieldDrift, TaskType: "T-A", Sublot: "S-A"),
            new IngestAlert(AlertCodes.FieldDrift, TaskType: "T-C", Sublot: "S-C"),
        ]);

        var first = store.QueryAlerts(new AlertListQuery
        {
            SortBy = AlertSortColumn.TaskType,
            Direction = SortDirection.Asc,
            Limit = 2,
            UseDefaultPrioritySort = false,
        });
        var second = store.QueryAlerts(new AlertListQuery
        {
            SortBy = AlertSortColumn.TaskType,
            Direction = SortDirection.Asc,
            Limit = 2,
            Cursor = first.NextCursor,
            UseDefaultPrioritySort = false,
        });

        Assert.Equal(new[] { "T-A", "T-B" }, first.Items.Select(alert => alert.TaskType));
        Assert.True(first.HasMore);
        Assert.Equal(new[] { "T-C" }, second.Items.Select(alert => alert.TaskType));
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task Default_runner_clock_stamps_created_at_and_poll_health_in_utc()
    {
        var baseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        var row = new MesSnapshotRow(
            "DIE_TO_OVEN",
            "Q1",
            "N01",
            "EQ1",
            "烘箱",
            baseline.AddHours(1),
            "PKG");
        var runner = new IngestRoundRunner(
            new FixedMesSnapshotSource(MesSnapshotOutcome.Success([row])),
            new TransportDemandReconciler(new SequentialDemandIdAllocator("d")),
            store,
            baseline);

        await runner.RunOnceAsync();

        var demand = Assert.Single(store.List());
        Assert.Equal(TimeSpan.Zero, demand.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, demand.MesLastSeenAt.Offset);

        var health = store.GetLatestPollHealth();
        Assert.NotNull(health);
        Assert.Equal(TimeSpan.Zero, health!.StartedAt.Offset);
        Assert.Equal(TimeSpan.Zero, health.EndedAt.Offset);
    }
}
