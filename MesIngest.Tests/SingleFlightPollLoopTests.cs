using System.Text.Json;
using MesIngest.Core;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;

namespace MesIngest.Tests;

public class SingleFlightPollLoopTests
{
    [Fact]
    public void Observed_start_extension_preserves_the_existing_public_signatures()
    {
        var receiptParameters = new[]
        {
            typeof(string),
            typeof(MesTaskUnionRoundOutcome),
            typeof(string),
            typeof(IReadOnlyList<string>),
            typeof(IReadOnlyList<string>),
            typeof(bool),
            typeof(long?),
            typeof(HistoryEpoch),
        };

        Assert.NotNull(typeof(RoundCommitReceipt).GetConstructor(receiptParameters));
        Assert.Contains(
            typeof(RoundCommitReceipt).GetMethods(),
            method => method.Name == "Deconstruct"
                && method.GetParameters().Length == receiptParameters.Length);

        var legacyRunMethods = typeof(SingleFlightPollLoop)
            .GetMethods()
            .Where(method => method.Name == nameof(SingleFlightPollLoop.RunAsync))
            .Where(method => method.GetParameters().Length == 5)
            .Select(method => method.GetParameters()[0].ParameterType)
            .ToArray();
        Assert.Contains(typeof(Func<CancellationToken, Task>), legacyRunMethods);
        Assert.Contains(typeof(Func<CancellationToken, Task<bool>>), legacyRunMethods);
    }

    [Fact]
    public void Production_defaults_are_sixty_second_start_slots_with_60_120_300_failure_backoff()
    {
        Assert.Equal(60, new MesIngest.Host.MesIngestHostOptions().PollStartIntervalSeconds);
        Assert.Equal(
            [60, 120, 300],
            SingleFlightPollLoop.FailureBackoffDelays.Select(value => (int)value.TotalSeconds));
    }

    [Fact]
    public async Task Uses_sixty_second_start_to_start_slots_and_skips_missed_slots_without_catch_up()
    {
        var now = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
        var starts = new List<DateTimeOffset>();
        var delays = new List<TimeSpan>();
        var durations = new Queue<TimeSpan>(
            [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(75), TimeSpan.Zero]);
        using var cts = new CancellationTokenSource();

        await SingleFlightPollLoop.RunAsync(
            runRound: _ =>
            {
                starts.Add(now);
                now += durations.Dequeue();
                if (starts.Count == 3)
                {
                    cts.Cancel();
                }

                return Task.FromResult(true);
            },
            pollStartInterval: TimeSpan.FromSeconds(60),
            cancellationToken: cts.Token,
            utcNow: () => now,
            delay: (wait, _) =>
            {
                delays.Add(wait);
                now += wait;
                return Task.CompletedTask;
            });

        Assert.Equal(
            [
                DateTimeOffset.Parse("2026-08-25T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-25T00:01:00Z"),
                DateTimeOffset.Parse("2026-08-25T00:03:00Z"),
            ],
            starts);
        Assert.Equal([TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(45)], delays);
    }

    [Fact]
    public async Task Priority_gate_delay_uses_the_observed_source_start_without_a_catch_up_burst()
    {
        var now = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
        var observedStart = (DateTimeOffset?)null;
        var sourceStarts = new List<DateTimeOffset>();
        var delays = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();

        await SingleFlightPollLoop.RunAsync(
            runRound: _ =>
            {
                if (sourceStarts.Count == 0)
                {
                    now += TimeSpan.FromSeconds(40);
                }
                observedStart = now;
                sourceStarts.Add(now);
                now += TimeSpan.FromSeconds(5);
                if (sourceStarts.Count == 2)
                {
                    cts.Cancel();
                }
                return Task.FromResult(true);
            },
            pollStartInterval: TimeSpan.FromSeconds(60),
            cancellationToken: cts.Token,
            utcNow: () => now,
            delay: (wait, _) =>
            {
                delays.Add(wait);
                now += wait;
                return Task.CompletedTask;
            },
            roundStartedAt: () => observedStart);

        Assert.Equal(TimeSpan.FromSeconds(60), sourceStarts[1] - sourceStarts[0]);
        Assert.Equal([TimeSpan.FromSeconds(55)], delays);
    }

    [Fact]
    public async Task Consecutive_failures_back_off_60_120_300_then_success_resets_normal_cadence()
    {
        var now = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
        var starts = new List<DateTimeOffset>();
        var delays = new List<TimeSpan>();
        var outcomes = new Queue<bool>([false, false, false, false, true, true]);
        using var cts = new CancellationTokenSource();

        await SingleFlightPollLoop.RunAsync(
            runRound: _ =>
            {
                starts.Add(now);
                var succeeded = outcomes.Dequeue();
                if (starts.Count == 6)
                {
                    cts.Cancel();
                }

                return Task.FromResult(succeeded);
            },
            pollStartInterval: TimeSpan.FromSeconds(60),
            cancellationToken: cts.Token,
            utcNow: () => now,
            delay: (wait, _) =>
            {
                delays.Add(wait);
                now += wait;
                return Task.CompletedTask;
            });

        Assert.Equal(
            [60, 120, 300, 300, 60],
            delays.Select(value => (int)value.TotalSeconds));
        Assert.Equal(
            [0, 60, 180, 480, 780, 840],
            starts.Select(value => (int)(value - starts[0]).TotalSeconds));
    }

    [Fact]
    public async Task First_failure_publishes_level_one_next_start_and_poll_trace_identity()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-27T01:00:00Z");
        var now = startedAt;
        var pollTraceId = "poll-scheduler-first-failure";
        var observer = new RecordingPollSchedulerStateObserver();
        using var cancellation = new CancellationTokenSource();

        await SingleFlightPollLoop.RunAsync(
            runRound: _ => Task.FromResult(false),
            pollStartInterval: TimeSpan.FromSeconds(60),
            cancellationToken: cancellation.Token,
            utcNow: () => now,
            delay: (wait, _) =>
            {
                now += wait;
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            roundStartedAt: null,
            pollTraceId: () => pollTraceId,
            stateObserver: observer);

        var state = Assert.Single(observer.Snapshots);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.Equal(1, state.BackoffLevel);
        Assert.Equal(startedAt.AddSeconds(60), state.NextAllowedStart);
        Assert.Null(state.LastSuccessAt);
        Assert.Equal(pollTraceId, state.PollTraceId);
    }

    [Fact]
    public async Task Consecutive_failures_publish_exact_capped_backoff_levels_without_real_waits()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-27T02:00:00Z");
        var now = startedAt;
        var round = 0;
        string? pollTraceId = null;
        var observer = new RecordingPollSchedulerStateObserver();
        using var cancellation = new CancellationTokenSource();

        await SingleFlightPollLoop.RunAsync(
            runRound: _ =>
            {
                round++;
                pollTraceId = $"poll-scheduler-failure-{round}";
                return Task.FromResult(false);
            },
            pollStartInterval: TimeSpan.FromSeconds(60),
            cancellationToken: cancellation.Token,
            utcNow: () => now,
            delay: (wait, _) =>
            {
                now += wait;
                if (round == 4)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            },
            roundStartedAt: null,
            pollTraceId: () => pollTraceId,
            stateObserver: observer);

        Assert.Equal([1, 2, 3, 4], observer.Snapshots.Select(state => state.ConsecutiveFailures));
        Assert.Equal([1, 2, 3, 3], observer.Snapshots.Select(state => state.BackoffLevel));
        Assert.Equal(
            [60, 180, 480, 780],
            observer.Snapshots.Select(state =>
                (int)(state.NextAllowedStart!.Value - startedAt).TotalSeconds));
        Assert.Equal(
            [
                "poll-scheduler-failure-1",
                "poll-scheduler-failure-2",
                "poll-scheduler-failure-3",
                "poll-scheduler-failure-4",
            ],
            observer.Snapshots.Select(state => state.PollTraceId));
        Assert.All(observer.Snapshots, state => Assert.Null(state.LastSuccessAt));
    }

    [Fact]
    public async Task Success_resets_failure_state_and_last_success_survives_the_next_failure()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-27T03:00:00Z");
        var now = startedAt;
        var outcomes = new Queue<bool>([false, false, true, false]);
        var round = 0;
        string? pollTraceId = null;
        var observer = new RecordingPollSchedulerStateObserver();
        using var cancellation = new CancellationTokenSource();

        await SingleFlightPollLoop.RunAsync(
            runRound: _ =>
            {
                round++;
                pollTraceId = $"poll-scheduler-round-{round}";
                return Task.FromResult(outcomes.Dequeue());
            },
            pollStartInterval: TimeSpan.FromSeconds(60),
            cancellationToken: cancellation.Token,
            utcNow: () => now,
            delay: (wait, _) =>
            {
                now += wait;
                if (round == 4)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            },
            roundStartedAt: null,
            pollTraceId: () => pollTraceId,
            stateObserver: observer);

        var success = observer.Snapshots[2];
        Assert.Equal(0, success.ConsecutiveFailures);
        Assert.Equal(0, success.BackoffLevel);
        Assert.Equal(startedAt.AddMinutes(4), success.NextAllowedStart);
        Assert.Equal(startedAt.AddMinutes(3), success.LastSuccessAt);
        Assert.Equal("poll-scheduler-round-3", success.PollTraceId);

        var failureAfterSuccess = observer.Snapshots[3];
        Assert.Equal(1, failureAfterSuccess.ConsecutiveFailures);
        Assert.Equal(1, failureAfterSuccess.BackoffLevel);
        Assert.Equal(startedAt.AddMinutes(5), failureAfterSuccess.NextAllowedStart);
        Assert.Equal(success.LastSuccessAt, failureAfterSuccess.LastSuccessAt);
        Assert.Equal("poll-scheduler-round-4", failureAfterSuccess.PollTraceId);
    }

    [Fact]
    public void Host_singleton_snapshot_is_exposed_as_a_minimal_serializable_attention_field()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-27T04:00:00Z");
        var schedulerState = new PollSchedulerState();
        schedulerState.OnStateChanged(new PollSchedulerStateSnapshot(
            ConsecutiveFailures: 2,
            BackoffLevel: 2,
            NextAllowedStart: observedAt.AddMinutes(2),
            LastSuccessAt: observedAt.AddMinutes(-3),
            PollTraceId: "poll-scheduler-dto"));
        var epoch = HistoryEpoch.FromGuid(Guid.Parse("7a8bd026-c1da-4f76-aa99-0f58eed36d4d"));
        var attention = new CurrentIngestAttentionSnapshot(
            Snapshot: new OperationalSnapshotIdentity(
                "commit-scheduler-dto",
                7,
                observedAt,
                "poll-scheduler-dto",
                9,
                11,
                observedAt,
                HistoryEpoch: epoch),
            ExactTotalItemCount: 0,
            Facets: new CurrentIngestAttentionFacets([], []),
            Order: CurrentIngestAttentionOrder.Default,
            PageSize: 100,
            PageNumber: 1,
            TotalPages: 0,
            Kinds: [],
            Severities: [],
            Items: [],
            HistoryCleanup: HistoryCleanupStateSnapshot.NotRun,
            StoragePressure: new StoragePressureStateSnapshot(
                StoragePressureStatuses.Healthy,
                epoch,
                "MesIngest",
                @"C:\MesIngest\MesIngest.mdf",
                new VolumeSpaceSample(@"C:\", 100, 50),
                observedAt,
                PausedAt: null,
                PauseId: null,
                PauseReason: null,
                RecoveryAuditId: null));

        var dto = CurrentIngestAttentionOperationalDto.From(attention, schedulerState.Current);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var scheduler = document.RootElement.GetProperty("pollScheduler");
        Assert.Equal(5, scheduler.EnumerateObject().Count());
        Assert.Equal(2, scheduler.GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal(2, scheduler.GetProperty("backoffLevel").GetInt32());
        Assert.Equal(observedAt.AddMinutes(2), scheduler.GetProperty("nextAllowedStart").GetDateTimeOffset());
        Assert.Equal(observedAt.AddMinutes(-3), scheduler.GetProperty("lastSuccessAt").GetDateTimeOffset());
        Assert.Equal("poll-scheduler-dto", scheduler.GetProperty("pollTraceId").GetString());
    }

    [Fact]
    public async Task Runs_rounds_sequentially_never_overlapping()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        var rounds = 0;
        var delayCalls = 0;
        using var cts = new CancellationTokenSource();

        await SingleFlightPollLoop.RunAsync(
            runRound: async ct =>
            {
                var current = Interlocked.Increment(ref inFlight);
                Interlocked.Exchange(ref maxInFlight, Math.Max(maxInFlight, current));
                await Task.Delay(20, ct);
                Interlocked.Decrement(ref inFlight);
                if (Interlocked.Increment(ref rounds) >= 3)
                {
                    cts.Cancel();
                }
            },
            delay: async (wait, ct) =>
            {
                Interlocked.Increment(ref delayCalls);
                await Task.Delay(1, ct);
            },
            pollStartInterval: TimeSpan.FromMilliseconds(5),
            cancellationToken: cts.Token);

        Assert.Equal(3, rounds);
        Assert.Equal(1, maxInFlight);
        Assert.Equal(2, delayCalls);
    }

    [Fact]
    public async Task Waits_until_the_next_start_slot_after_each_completed_round()
    {
        var events = new List<string>();
        var now = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
        using var cts = new CancellationTokenSource();
        var rounds = 0;

        await SingleFlightPollLoop.RunAsync(
            runRound: async _ =>
            {
                events.Add("round");
                await Task.Yield();
                if (Interlocked.Increment(ref rounds) >= 2)
                {
                    cts.Cancel();
                }
            },
            delay: async (wait, ct) =>
            {
                events.Add($"delay:{wait.TotalMilliseconds}");
                now += wait;
                await Task.Yield();
            },
            pollStartInterval: TimeSpan.FromMilliseconds(10),
            utcNow: () => now,
            cancellationToken: cts.Token);

        Assert.Equal(["round", "delay:10", "round"], events);
    }

    [Fact]
    public async Task Cancellation_stops_before_starting_another_round()
    {
        var rounds = 0;
        using var cts = new CancellationTokenSource();

        await SingleFlightPollLoop.RunAsync(
            runRound: async _ =>
            {
                Interlocked.Increment(ref rounds);
                cts.Cancel();
                await Task.Yield();
            },
            delay: async (_, ct) => await Task.Delay(Timeout.Infinite, ct),
            pollStartInterval: TimeSpan.FromHours(1),
            cancellationToken: cts.Token);

        Assert.Equal(1, rounds);
    }

    [Fact]
    public async Task Round_exception_does_not_stop_the_loop()
    {
        var rounds = 0;
        using var cts = new CancellationTokenSource();

        await SingleFlightPollLoop.RunAsync(
            runRound: _ =>
            {
                var n = Interlocked.Increment(ref rounds);
                if (n == 1)
                {
                    throw new InvalidOperationException("store unavailable");
                }

                if (n >= 3)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
            delay: async (_, ct) => await Task.Yield(),
            pollStartInterval: TimeSpan.FromMilliseconds(1),
            cancellationToken: cts.Token);

        Assert.Equal(3, rounds);
    }

    private sealed class RecordingPollSchedulerStateObserver : IPollSchedulerStateObserver
    {
        public List<PollSchedulerStateSnapshot> Snapshots { get; } = [];

        public void OnStateChanged(PollSchedulerStateSnapshot snapshot) => Snapshots.Add(snapshot);
    }
}
