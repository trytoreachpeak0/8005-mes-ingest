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

    [Fact]
    public async Task Failed_round_appends_poll_failure_alert_without_mutating_projection()
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
        ]));

        var runner = new IngestRoundRunner(
            new FixedMesSnapshotSource(MesSnapshotOutcome.Failure()),
            new TransportDemandReconciler(new SequentialDemandIdAllocator("new")),
            store,
            Baseline,
            clock: () => now.AddMinutes(1));

        await runner.RunOnceAsync();

        var demand = Assert.Single(store.List());
        Assert.Equal("d1", demand.DemandId);
        Assert.Equal(DemandStatus.Visible, demand.Status);
        Assert.Equal(1, demand.DisappearCount);
        Assert.Equal(now, demand.MesLastSeenAt);

        var alert = Assert.Single(store.ListAlerts());
        Assert.Equal("POLL_FAILURE", alert.Code);
        var health = store.GetLatestPollHealth();
        Assert.NotNull(health);
        Assert.False(health!.Success);
        Assert.Equal("FAILURE", health.Outcome);
    }

    [Fact]
    public async Task Incomplete_round_appends_poll_incomplete_alert_without_creating_visible()
    {
        var now = Baseline.AddHours(12);
        var store = new InMemoryTransportDemandStore();
        var runner = new IngestRoundRunner(
            new FixedMesSnapshotSource(MesSnapshotOutcome.Incomplete()),
            new TransportDemandReconciler(new SequentialDemandIdAllocator("new")),
            store,
            Baseline,
            clock: () => now);

        await runner.RunOnceAsync();

        Assert.Empty(store.List());
        var alert = Assert.Single(store.ListAlerts());
        Assert.Equal("POLL_INCOMPLETE", alert.Code);
        Assert.Equal("INCOMPLETE", store.GetLatestPollHealth()!.Outcome);
    }

    [Fact]
    public async Task Query_timeout_is_recorded_as_poll_failure_without_mutating_projection()
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
        ]));

        var runner = new IngestRoundRunner(
            new HangingMesSnapshotSource(),
            new TransportDemandReconciler(new SequentialDemandIdAllocator("new")),
            store,
            Baseline,
            queryTimeout: TimeSpan.FromMilliseconds(30),
            clock: () => now.AddMinutes(1));

        await runner.RunOnceAsync();

        var demand = Assert.Single(store.List());
        Assert.Equal(1, demand.DisappearCount);
        Assert.Equal(DemandStatus.Visible, demand.Status);
        Assert.Equal("POLL_FAILURE", Assert.Single(store.ListAlerts()).Code);
        Assert.Equal("FAILURE", store.GetLatestPollHealth()!.Outcome);
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

    private sealed class HangingMesSnapshotSource : IMesSnapshotSource
    {
        public async Task<MesSnapshotOutcome> ReadAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return MesSnapshotOutcome.Success([]);
        }
    }
}
