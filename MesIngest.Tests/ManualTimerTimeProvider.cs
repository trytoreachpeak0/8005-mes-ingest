namespace MesIngest.Tests;

/// <summary>
/// Clock whose timers only fire from <see cref="Advance"/>, so time dependent
/// behaviour is driven by the test instead of by a real wait.
/// </summary>
internal sealed class ManualTimerTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        lock (_gate)
        {
            _utcNow += elapsed;
        }

        while (TryTakeDueCallback(out var callback))
        {
            callback();
        }
    }

    private bool TryTakeDueCallback(out Action callback)
    {
        lock (_gate)
        {
            var timer = _timers.FirstOrDefault(value => value.IsDue(_utcNow));
            if (timer is null)
            {
                callback = null!;
                return false;
            }

            callback = timer.TakeCallback(_utcNow);
            return true;
        }
    }

    private sealed class ManualTimer(
        ManualTimerTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private DateTimeOffset? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public bool IsDue(DateTimeOffset utcNow) =>
            !_disposed && _dueAt is { } dueAt && dueAt <= utcNow;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner._utcNow + dueTime;
                return true;
            }
        }

        public Action TakeCallback(DateTimeOffset utcNow)
        {
            _dueAt = _period == Timeout.InfiniteTimeSpan
                ? null
                : utcNow + _period;
            return () => callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                _dueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
