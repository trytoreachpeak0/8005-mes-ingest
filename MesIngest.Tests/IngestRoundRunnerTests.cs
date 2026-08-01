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
        Assert.Equal(
            AlertDetailsBuilder.Reappear("old", "new"),
            alert.Details);
        Assert.Contains(store.List(), d => d.DemandId == "new" && d.Status == DemandStatus.Visible);
        Assert.Contains(store.List(), d => d.DemandId == "old" && d.Status == DemandStatus.Gone);
    }

    [Fact]
    public async Task RunOnce_reappear_details_use_store_lookup_when_hot_state_omits_gone()
    {
        var now = Baseline.AddHours(12);
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "old-gone",
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
                GoneAt = now,
            },
        ]));

        // Hot GetState() excludes GONE; runner must resolve previous id via store lookup.
        Assert.Empty(store.GetState().Demands);
        Assert.Equal("old-gone", store.GetLatestGoneDemandId("WIRE_TO_GATE", "Q1"));

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
            new TransportDemandReconciler(new SequentialDemandIdAllocator("brand-new")),
            store,
            Baseline,
            clock: () => now.AddMinutes(1));

        await runner.RunOnceAsync();

        var alert = Assert.Single(store.ListAlerts());
        Assert.Equal("REAPPEAR_AFTER_GONE", alert.Code);
        Assert.Equal(AlertDetailsBuilder.Reappear("old-gone", "brand-new"), alert.Details);
    }

    [Fact]
    public async Task Restart_barrier_valid_post_baseline_rows_still_seed_pause_when_next_round_empty()
    {
        var now = Baseline.AddHours(12);
        var store = new InMemoryTransportDemandStore();
        var validRows = Enumerable.Range(1, 10)
            .Select(i => new MesSnapshotRow(
                "DIE_TO_OVEN",
                $"Q-{i}",
                "N01-01",
                "EQ1",
                "烘箱",
                Baseline.AddHours(1),
                "PKG"))
            .ToList();

        var source = new QueueMesSnapshotSource(
            MesSnapshotOutcome.Success(validRows),
            MesSnapshotOutcome.Success([]));
        var runner = new IngestRoundRunner(
            source,
            new TransportDemandReconciler(new SequentialDemandIdAllocator(
                Enumerable.Range(1, 10).Select(i => $"d{i}").ToArray())),
            store,
            Baseline,
            zeroDropEnterThreshold: 10,
            clock: () => now);

        await runner.RunOnceAsync();
        Assert.Equal(10, store.List().Count);
        Assert.Equal(10, Assert.Single(store.GetState().TaskTypePauses).LastHealthyNonZeroCount);
        Assert.False(Assert.Single(store.GetState().TaskTypePauses).PausedZeroDrop);

        await runner.RunOnceAsync();
        Assert.True(Assert.Single(store.GetState().TaskTypePauses).PausedZeroDrop);
        Assert.Equal(10, Assert.Single(store.GetState().TaskTypePauses).LastHealthyNonZeroCount);
        Assert.Contains(store.ListAlerts(), a => a.Code == "PAUSED_ZERO_DROP");
    }

    [Fact]
    public async Task Restart_barrier_duplicate_keys_only_do_not_seed_healthy_baseline_or_pause_on_next_empty()
    {
        var now = Baseline.AddHours(12);
        var store = new InMemoryTransportDemandStore();
        var duplicateOnly = Enumerable.Range(1, 12)
            .Select(_ => new MesSnapshotRow(
                "DIE_TO_OVEN",
                "SAME-KEY",
                "N01-01",
                "EQ1",
                "烘箱",
                Baseline.AddHours(1),
                "PKG"))
            .ToList();

        var source = new QueueMesSnapshotSource(
            MesSnapshotOutcome.Success(duplicateOnly),
            MesSnapshotOutcome.Success([]));
        var runner = new IngestRoundRunner(
            source,
            new TransportDemandReconciler(new SequentialDemandIdAllocator()),
            store,
            Baseline,
            zeroDropEnterThreshold: 10,
            clock: () => now);

        await runner.RunOnceAsync();
        Assert.Empty(store.List());
        Assert.DoesNotContain(
            store.GetState().TaskTypePauses,
            p => p.LastHealthyNonZeroCount > 0);

        await runner.RunOnceAsync();
        Assert.DoesNotContain(store.GetState().TaskTypePauses, p => p.PausedZeroDrop);
        Assert.DoesNotContain(store.ListAlerts(), a => a.Code == "PAUSED_ZERO_DROP");
    }

    [Fact]
    public async Task Restart_barrier_pre_baseline_only_rows_do_not_seed_healthy_baseline_or_pause_on_next_empty()
    {
        var now = Baseline.AddHours(12);
        var store = new InMemoryTransportDemandStore();
        var preBaselineOnly = Enumerable.Range(1, 12)
            .Select(i => new MesSnapshotRow(
                "DIE_TO_OVEN",
                $"Q-{i}",
                "N01-01",
                "EQ1",
                "烘箱",
                Baseline.AddDays(-1),
                "PKG"))
            .ToList();

        var source = new QueueMesSnapshotSource(
            MesSnapshotOutcome.Success(preBaselineOnly),
            MesSnapshotOutcome.Success([]));
        var runner = new IngestRoundRunner(
            source,
            new TransportDemandReconciler(new SequentialDemandIdAllocator()),
            store,
            Baseline,
            zeroDropEnterThreshold: 10,
            clock: () => now);

        await runner.RunOnceAsync();
        Assert.Empty(store.List());
        Assert.DoesNotContain(
            store.GetState().TaskTypePauses,
            p => p.LastHealthyNonZeroCount > 0);

        await runner.RunOnceAsync();
        Assert.DoesNotContain(store.GetState().TaskTypePauses, p => p.PausedZeroDrop);
        Assert.DoesNotContain(store.ListAlerts(), a => a.Code == "PAUSED_ZERO_DROP");
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
    public async Task Csv_row_parse_failure_is_poll_incomplete_not_failure()
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

        var csv = """
            TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE
            DIE_TO_OVEN,Q1,N01-01,EQ1,烘箱,not-a-date,PKG
            """;
        var path = Path.Combine(Path.GetTempPath(), $"mes-runner-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, csv);

        try
        {
            var runner = new IngestRoundRunner(
                new CsvFileMesSnapshotSource(path),
                new TransportDemandReconciler(new SequentialDemandIdAllocator("new")),
                store,
                Baseline,
                clock: () => now.AddMinutes(1));

            await runner.RunOnceAsync();

            var demand = Assert.Single(store.List());
            Assert.Equal("d1", demand.DemandId);
            Assert.Equal(DemandStatus.Visible, demand.Status);
            Assert.Equal(1, demand.DisappearCount);

            var alert = Assert.Single(store.ListAlerts());
            Assert.Equal("POLL_INCOMPLETE", alert.Code);
            Assert.Equal("INCOMPLETE", store.GetLatestPollHealth()!.Outcome);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Csv_empty_task_type_is_poll_incomplete_not_failure()
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

        var csv = """
            TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE
            ,Q1,N01-01,EQ1,烘箱,2026-08-01T10:00:00,PKG
            """;
        var path = Path.Combine(Path.GetTempPath(), $"mes-runner-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, csv);

        try
        {
            var runner = new IngestRoundRunner(
                new CsvFileMesSnapshotSource(path),
                new TransportDemandReconciler(new SequentialDemandIdAllocator("new")),
                store,
                Baseline,
                clock: () => now.AddMinutes(1));

            await runner.RunOnceAsync();

            var demand = Assert.Single(store.List());
            Assert.Equal(DemandStatus.Visible, demand.Status);
            Assert.Equal(1, demand.DisappearCount);
            Assert.Equal("POLL_INCOMPLETE", Assert.Single(store.ListAlerts()).Code);
            Assert.Equal("INCOMPLETE", store.GetLatestPollHealth()!.Outcome);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Csv_zero_byte_file_is_poll_incomplete_without_mutating_presence()
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

        var path = Path.Combine(Path.GetTempPath(), $"mes-runner-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());

        try
        {
            var runner = new IngestRoundRunner(
                new CsvFileMesSnapshotSource(path),
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
            Assert.Equal("POLL_INCOMPLETE", Assert.Single(store.ListAlerts()).Code);
            Assert.Equal("INCOMPLETE", store.GetLatestPollHealth()!.Outcome);
        }
        finally
        {
            File.Delete(path);
        }
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

    [Fact]
    public async Task Failed_round_does_not_rewrite_projection_store()
    {
        var now = Baseline.AddHours(12);
        var store = new CountingReplaceStore();
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
        store.ReplaceCount = 0;

        var runner = new IngestRoundRunner(
            new FixedMesSnapshotSource(MesSnapshotOutcome.Failure()),
            new TransportDemandReconciler(new SequentialDemandIdAllocator("new")),
            store,
            Baseline,
            clock: () => now.AddMinutes(1));

        await runner.RunOnceAsync();

        Assert.Equal(0, store.ReplaceCount);
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

    private sealed class CountingReplaceStore : ITransportDemandStore
    {
        private readonly InMemoryTransportDemandStore _inner = new();
        public int ReplaceCount { get; set; }

        public ProjectionState GetState() => _inner.GetState();

        public void ReplaceState(ProjectionState state, IReadOnlyList<IngestAlert>? alerts = null)
        {
            ReplaceCount++;
            _inner.ReplaceState(state, alerts);
        }

        public bool HasGoneTransportDemandKey(string taskType, string sublot) =>
            _inner.HasGoneTransportDemandKey(taskType, sublot);

        public string? GetLatestGoneDemandId(string taskType, string sublot) =>
            _inner.GetLatestGoneDemandId(taskType, sublot);

        public TransportDemand? GetById(string demandId) => _inner.GetById(demandId);

        public IReadOnlyList<TransportDemand> List(
            DemandStatus? status = null,
            string? taskType = null,
            string? sublot = null,
            string? demandId = null) =>
            _inner.List(status, taskType, sublot, demandId);

        public DemandListPage QueryPage(DemandListQuery query) => _inner.QueryPage(query);

        public DemandChangeFeedPage QueryChangeFeed(DemandChangeFeedQuery query) =>
            _inner.QueryChangeFeed(query);

        public void AppendAlerts(IReadOnlyList<IngestAlert> alerts) => _inner.AppendAlerts(alerts);

        public IReadOnlyList<IngestAlert> ListAlerts(int? limit = null) => _inner.ListAlerts(limit);
        public AlertListPage QueryAlerts(AlertListQuery query) => _inner.QueryAlerts(query);

        public void SetLatestPollHealth(PollHealth health) => _inner.SetLatestPollHealth(health);

        public PollHealth? GetLatestPollHealth() => _inner.GetLatestPollHealth();
    }
}
