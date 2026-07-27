namespace MesIngest.Core;

/// <summary>
/// Sequential poll loop: at most one round in flight; waits <paramref name="postPollDelay"/>
/// after each completed round before starting the next.
/// </summary>
public static class SingleFlightPollLoop
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> runRound,
        TimeSpan postPollDelay,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= static (wait, ct) => Task.Delay(wait, ct);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await runRound(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await delay(postPollDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
