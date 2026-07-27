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

    [Fact]
    public async Task Process_restart_first_successful_round_is_barrier_second_resumes_disappear()
    {
        var now = Baseline.AddHours(12);
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d1",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-1",
                    Area = "N01-01",
                    Eqp = "EQ1",
                    Step = "烘箱",
                    Dates = Baseline.AddHours(1),
                    Package = "PKG",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = now,
                    DisappearCount = 1,
                },
            ],
            [
                new TaskTypePauseState("DIE_TO_OVEN", PausedZeroDrop: false, LastHealthyNonZeroCount: 3, RecoveryStreak: 0),
            ]));

        var source = new QueueMesSnapshotSource(
            MesSnapshotOutcome.Success([]),
            MesSnapshotOutcome.Success([]));
        var runner = new IngestRoundRunner(
            source,
            new TransportDemandReconciler(new SequentialDemandIdAllocator()),
            store,
            Baseline,
            disappearThreshold: 2,
            clock: () => now.AddMinutes(1));

        await runner.RunOnceAsync();
        var afterBarrier = Assert.Single(store.List());
        Assert.Equal(DemandStatus.Visible, afterBarrier.Status);
        Assert.Equal(1, afterBarrier.DisappearCount);

        await runner.RunOnceAsync();
        var afterSecond = Assert.Single(store.List());
        Assert.Equal(DemandStatus.Gone, afterSecond.Status);
        Assert.Equal(2, afterSecond.DisappearCount);
    }

    [Fact]
    public async Task Failed_or_incomplete_round_does_not_consume_restart_barrier()
    {
        var now = Baseline.AddHours(12);
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "d1",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q-1",
                Area = "N01-01",
                Eqp = "EQ1",
                Step = "烘箱",
                Dates = Baseline.AddHours(1),
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                DisappearCount = 0,
            },
        ]));

        var source = new QueueMesSnapshotSource(
            MesSnapshotOutcome.Failure(),
            MesSnapshotOutcome.Incomplete(),
            MesSnapshotOutcome.Success([]),
            MesSnapshotOutcome.Success([]));
        var runner = new IngestRoundRunner(
            source,
            new TransportDemandReconciler(new SequentialDemandIdAllocator()),
            store,
            Baseline,
            disappearThreshold: 2,
            clock: () => now.AddMinutes(1));

        await runner.RunOnceAsync();
        Assert.Equal(0, Assert.Single(store.List()).DisappearCount);

        await runner.RunOnceAsync();
        Assert.Equal(0, Assert.Single(store.List()).DisappearCount);

        await runner.RunOnceAsync();
        Assert.Equal(0, Assert.Single(store.List()).DisappearCount);

        await runner.RunOnceAsync();
        Assert.Equal(1, Assert.Single(store.List()).DisappearCount);
    }

    private sealed class QueueMesSnapshotSource : IMesSnapshotSource
    {
        private readonly Queue<MesSnapshotOutcome> _outcomes;

        public QueueMesSnapshotSource(params MesSnapshotOutcome[] outcomes)
        {
            _outcomes = new Queue<MesSnapshotOutcome>(outcomes);
        }

        public Task<MesSnapshotOutcome> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_outcomes.Dequeue());
    }
}
