namespace MesIngest.Core;

/// <summary>
/// Sequential poll loop: at most one round is in flight, successful rounds use fixed
/// start-to-start slots without catch-up, and consecutive failures use bounded backoff.
/// </summary>
public static class SingleFlightPollLoop
{
    public static IReadOnlyList<TimeSpan> FailureBackoffDelays { get; } =
    [
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(300),
    ];

    public static async Task RunAsync(
        Func<CancellationToken, Task> runRound,
        TimeSpan pollStartInterval,
        CancellationToken cancellationToken,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(runRound);
        await RunAsync(
            async ct =>
            {
                await runRound(ct).ConfigureAwait(false);
                return true;
            },
            pollStartInterval,
            cancellationToken,
            utcNow,
            delay).ConfigureAwait(false);
    }

    public static async Task RunAsync(
        Func<CancellationToken, Task<bool>> runRound,
        TimeSpan pollStartInterval,
        CancellationToken cancellationToken,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(runRound);
        if (pollStartInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollStartInterval));
        }

        utcNow ??= static () => DateTimeOffset.UtcNow;
        delay ??= static (wait, ct) => Task.Delay(wait, ct);
        var nextScheduledStart = utcNow();
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var scheduledStart = nextScheduledStart;
            var succeeded = false;
            try
            {
                succeeded = await runRound(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Bad rounds (store outage, unexpected faults) must not stop continuous poll.
                // Health/alerts are the caller's responsibility inside runRound when possible.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var completedAt = utcNow();
            if (succeeded)
            {
                consecutiveFailures = 0;
                nextScheduledStart = scheduledStart + pollStartInterval;
                while (nextScheduledStart <= completedAt)
                {
                    nextScheduledStart += pollStartInterval;
                }
            }
            else
            {
                consecutiveFailures++;
                var backoffIndex = Math.Min(
                    consecutiveFailures - 1,
                    FailureBackoffDelays.Count - 1);
                nextScheduledStart = completedAt + FailureBackoffDelays[backoffIndex];
            }

            var wait = nextScheduledStart - utcNow();
            if (wait < TimeSpan.Zero)
            {
                wait = TimeSpan.Zero;
            }

            try
            {
                await delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
