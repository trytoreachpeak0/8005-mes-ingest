using MesIngest.Core;
using MesIngest.Core.SeriesProjection;

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
}
