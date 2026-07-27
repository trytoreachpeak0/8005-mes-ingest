using MesIngest.Core;

namespace MesIngest.Tests;

public class IngestRoundRunnerTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task RunOnce_appends_reappear_alerts_to_store()
    {
        var now = Baseline.AddHours(12);
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "old",
                TaskType = "WIRE_TO_GATE",
                Sublot = "Q1",
                Area = "N01",
                Eqp = "EQ",
                Step = "关卡",
                Dates = Baseline.AddHours(1),
                Package = "PKG",
                Status = DemandStatus.Gone,
                MesLastSeenAt = now,
                DisappearCount = 2,
            },
        ]));

        var row = new MesSnapshotRow(
            "WIRE_TO_GATE",
            "Q1",
            "N01",
            "EQ",
            "关卡",
            Baseline.AddHours(2),
            "PKG2");
        var runner = new IngestRoundRunner(
            new FixedMesSnapshotSource(MesSnapshotOutcome.Success([row])),
            new TransportDemandReconciler(new SequentialDemandIdAllocator("new")),
            store,
            Baseline,
            clock: () => now.AddMinutes(1));

        await runner.RunOnceAsync();

        var alert = Assert.Single(store.ListAlerts());
        Assert.Equal("REAPPEAR_AFTER_GONE", alert.Code);
        Assert.Equal("new", alert.DemandId);
        Assert.Contains(store.List(), d => d.DemandId == "new" && d.Status == DemandStatus.Visible);
        Assert.Contains(store.List(), d => d.DemandId == "old" && d.Status == DemandStatus.Gone);
    }
}
