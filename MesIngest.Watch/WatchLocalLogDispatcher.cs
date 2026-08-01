using System.IO;
using System.Threading.Channels;

namespace MesIngest.Watch;

/// <summary>
/// Runs local log filesystem work on one bounded background lane. Callers only enqueue,
/// so a stalled disk operation cannot stall a successful Watch refresh or the UI thread.
/// </summary>
internal sealed class WatchLocalLogDispatcher : IDisposable
{
    private readonly Channel<QueueMessage> _queue;
    private readonly Task _worker;

    public WatchLocalLogDispatcher(int capacity = 256)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _queue = Channel.CreateBounded<QueueMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = Task.Run(ProcessAsync);
    }

    public bool TryEnqueue(Action action, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_queue.Writer.TryWrite(new LogActionMessage(action, onFailure)))
        {
            return true;
        }

        WatchIoFailureReporter.TryReport(
            onFailure,
            new IOException("Watch local log queue is full or unavailable; log work was dropped."));
        return false;
    }

    public Task DrainAsync()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new DrainBarrierMessage(completion)))
        {
            completion.SetException(
                new IOException("Watch local log queue is full or unavailable; cannot observe drain."));
        }

        return completion.Task;
    }

    public void Dispose() => _queue.Writer.TryComplete();

    private async Task ProcessAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            switch (item)
            {
                case DrainBarrierMessage barrier:
                    barrier.Completion.TrySetResult();
                    break;

                case LogActionMessage action:
                    try
                    {
                        action.Execute();
                    }
                    catch (Exception ex)
                    {
                        WatchIoFailureReporter.TryReport(action.OnFailure, ex);
                    }

                    break;
            }
        }
    }

    private abstract record QueueMessage;

    private sealed record LogActionMessage(
        Action Execute,
        Action<Exception>? OnFailure) : QueueMessage;

    private sealed record DrainBarrierMessage(
        TaskCompletionSource Completion) : QueueMessage;
}

internal static class WatchIoFailureReporter
{
    public static void TryReport(Action<Exception>? onFailure, Exception ex)
    {
        try
        {
            onFailure?.Invoke(ex);
        }
        catch
        {
            // Diagnostics must remain outside every local logging failure path.
        }
    }
}
