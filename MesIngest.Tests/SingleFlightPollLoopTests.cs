using MesIngest.Core;

namespace MesIngest.Tests;

public class SingleFlightPollLoopTests
{
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
            postPollDelay: TimeSpan.FromMilliseconds(5),
            cancellationToken: cts.Token);

        Assert.Equal(3, rounds);
        Assert.Equal(1, maxInFlight);
        Assert.Equal(3, delayCalls);
    }

    [Fact]
    public async Task Waits_post_poll_delay_after_each_completed_round()
    {
        var events = new List<string>();
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
                await Task.Yield();
            },
            postPollDelay: TimeSpan.FromMilliseconds(10),
            cancellationToken: cts.Token);

        Assert.Equal(["round", "delay:10", "round", "delay:10"], events);
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
            postPollDelay: TimeSpan.FromHours(1),
            cancellationToken: cts.Token);

        Assert.Equal(1, rounds);
    }
}
