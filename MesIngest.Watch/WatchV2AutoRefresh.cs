using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal enum WatchV2DataView
{
    Overview,
    DemandSeries,
    ReadabilityAudit,
    ErrorSearch,
    CurrentIngestAttention,
}

internal enum WatchV2AutoRefreshPhase
{
    Started,
    Completed,
}

internal sealed class WatchV2AutoRefreshEventArgs(
    WatchV2DataView view,
    WatchV2AutoRefreshPhase phase) : EventArgs
{
    public WatchV2DataView View { get; } = view;

    public WatchV2AutoRefreshPhase Phase { get; } = phase;
}

internal sealed record WatchV2AutoRefreshSetting
{
    public static IReadOnlyList<int> AllowedIntervals { get; } = [10, 30, 60, 300];

    public WatchV2AutoRefreshSetting(int intervalSeconds = 10)
    {
        if (!AllowedIntervals.Contains(intervalSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervalSeconds),
                intervalSeconds,
                "Auto-refresh interval must be 10, 30, 60, or 300 seconds.");
        }

        IntervalSeconds = intervalSeconds;
    }

    public int IntervalSeconds { get; }
}

internal sealed record WatchV2AutoRefreshSettings(
    WatchV2AutoRefreshSetting Overview,
    WatchV2AutoRefreshSetting DemandSeries,
    WatchV2AutoRefreshSetting ReadabilityAudit,
    WatchV2AutoRefreshSetting ErrorSearch,
    WatchV2AutoRefreshSetting CurrentIngestAttention)
{
    public static WatchV2AutoRefreshSettings Default { get; } = new(
        new(),
        new(),
        new(),
        new(),
        new());

    public WatchV2AutoRefreshSetting For(WatchV2DataView view) => view switch
    {
        WatchV2DataView.Overview => Overview,
        WatchV2DataView.DemandSeries => DemandSeries,
        WatchV2DataView.ReadabilityAudit => ReadabilityAudit,
        WatchV2DataView.ErrorSearch => ErrorSearch,
        WatchV2DataView.CurrentIngestAttention => CurrentIngestAttention,
        _ => throw new ArgumentOutOfRangeException(nameof(view), view, null),
    };

    public WatchV2AutoRefreshSettings With(
        WatchV2DataView view,
        WatchV2AutoRefreshSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return view switch
        {
            WatchV2DataView.Overview => this with { Overview = setting },
            WatchV2DataView.DemandSeries => this with { DemandSeries = setting },
            WatchV2DataView.ReadabilityAudit => this with { ReadabilityAudit = setting },
            WatchV2DataView.ErrorSearch => this with { ErrorSearch = setting },
            WatchV2DataView.CurrentIngestAttention => this with { CurrentIngestAttention = setting },
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, null),
        };
    }
}

/// <summary>
/// Owns the always-on refresh clock for the currently visible V2 Host data
/// view. A due tick is consumed even while a request is busy, so timer work
/// cannot queue behind that request; completion starts a fresh full interval.
/// </summary>
internal sealed class WatchV2AutoRefreshSchedule
{
    private readonly TimeProvider _timeProvider;
    private WatchV2AutoRefreshSettings _settings;
    private DateTimeOffset? _nextDueAt;

    public WatchV2AutoRefreshSchedule(
        WatchV2AutoRefreshSettings settings,
        TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public WatchV2DataView? ActiveView { get; private set; }

    public WatchV2AutoRefreshSettings Settings => _settings;

    public void Activate(WatchV2DataView view)
    {
        _ = _settings.For(view);
        ActiveView = view;
        ScheduleNext();
    }

    public void Deactivate()
    {
        ActiveView = null;
        _nextDueAt = null;
    }

    public void Update(WatchV2DataView view, WatchV2AutoRefreshSetting setting)
    {
        _settings = _settings.With(view, setting);
        if (ActiveView == view)
        {
            ScheduleNext();
        }
    }

    public WatchV2DataView? TryTakeDue(bool refreshInProgress)
    {
        if (ActiveView is not { } activeView
            || _nextDueAt is not { } dueAt
            || _timeProvider.GetUtcNow() < dueAt)
        {
            return null;
        }

        _nextDueAt = null;
        return refreshInProgress ? null : activeView;
    }

    public void CompleteRefresh(WatchV2DataView view)
    {
        if (ActiveView == view)
        {
            ScheduleNext();
        }
    }

    public TimeSpan? GetDelayUntilNextDue()
    {
        if (_nextDueAt is not { } dueAt)
        {
            return null;
        }

        var delay = dueAt - _timeProvider.GetUtcNow();
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private void ScheduleNext()
    {
        _nextDueAt = ActiveView is { } activeView
            ? _timeProvider.GetUtcNow().AddSeconds(_settings.For(activeView).IntervalSeconds)
            : null;
    }
}

/// <summary>
/// Drives the active Host-data view through <see cref="WatchV2WorkspaceSession"/>.
/// The timer is one-shot: a refresh owns the single-flight slot until it
/// completes, and a tick that arrives while that slot is occupied is dropped.
/// </summary>
internal sealed class WatchV2AutoRefreshCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly WatchV2WorkspaceSession _session;
    private readonly WatchV2AutoRefreshSchedule _schedule;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly ITimer _timer;
    private RefreshTarget? _activeTarget;
    private TaskCompletionSource? _idleCompletion;
    private Exception? _lastUnhandledException;
    private bool _refreshInProgress;
    private bool _disposed;

    public WatchV2AutoRefreshCoordinator(
        WatchV2WorkspaceSession session,
        WatchV2AutoRefreshSettings settings,
        TimeProvider? timeProvider = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        var clock = timeProvider ?? TimeProvider.System;
        _schedule = new WatchV2AutoRefreshSchedule(settings, clock);
        _timer = clock.CreateTimer(
            static state => ((WatchV2AutoRefreshCoordinator)state!).OnTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public WatchV2DataView? ActiveView
    {
        get
        {
            lock (_gate)
            {
                return _schedule.ActiveView;
            }
        }
    }

    public WatchV2AutoRefreshSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _schedule.Settings;
            }
        }
    }

    public Exception? LastUnhandledException
    {
        get
        {
            lock (_gate)
            {
                return _lastUnhandledException;
            }
        }
    }

    /// <summary>
    /// Announces automatic refresh state transitions after the workspace has
    /// committed the corresponding state. Observer failures are isolated from
    /// the coordinator and from other observers.
    /// </summary>
    public event EventHandler<WatchV2AutoRefreshEventArgs>? RefreshStateChanged;

    public void ActivateOverview(WatchOverviewQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Activate(new RefreshTarget(
            WatchV2DataView.Overview,
            query.NormalizeAndValidate()));
    }

    public void ActivateDemandSeries(DemandSeriesBrowseQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Activate(new RefreshTarget(
            WatchV2DataView.DemandSeries,
            query.NormalizeAndValidate()));
    }

    public void ActivateReadabilityAudit(ReadabilityAuditQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Activate(new RefreshTarget(
            WatchV2DataView.ReadabilityAudit,
            query.NormalizeAndValidate()));
    }

    public void ActivateErrorSearch(ErrorSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Activate(new RefreshTarget(
            WatchV2DataView.ErrorSearch,
            query.NormalizeAndValidate()));
    }

    public void ActivateCurrentAttention(CurrentIngestAttentionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Activate(new RefreshTarget(
            WatchV2DataView.CurrentIngestAttention,
            query.NormalizeAndValidate()));
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeTarget = null;
            _schedule.Deactivate();
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Update(
        WatchV2DataView view,
        WatchV2AutoRefreshSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _schedule.Update(view, setting);
            if (_schedule.ActiveView == view)
            {
                ArmTimerForScheduleLocked();
            }
        }
    }

    /// <summary>
    /// Completes when the currently running refresh leaves the single-flight
    /// slot. This does not enqueue work and is suitable for graceful shutdown
    /// and deterministic callers.
    /// </summary>
    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            return _refreshInProgress
                ? _idleCompletion!.Task
                : Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeTarget = null;
            _schedule.Deactivate();
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _disposeCancellation.Cancel();
        _timer.Dispose();
        _disposeCancellation.Dispose();
    }

    private void Activate(RefreshTarget target)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeTarget = target;
            _schedule.Activate(target.View);
            ArmTimerForScheduleLocked();
        }
    }

    private void OnTimer()
    {
        RefreshTarget? target = null;
        TaskCompletionSource? completion = null;
        CancellationToken cancellationToken = default;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var dueView = _schedule.TryTakeDue(_refreshInProgress);
            if (dueView is null)
            {
                ArmTimerForScheduleLocked();
                return;
            }

            if (_activeTarget is not { } activeTarget
                || activeTarget.View != dueView.Value)
            {
                _schedule.CompleteRefresh(dueView.Value);
                ArmTimerForScheduleLocked();
                return;
            }

            _refreshInProgress = true;
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _idleCompletion = completion;
            target = activeTarget;
            cancellationToken = _disposeCancellation.Token;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _ = RunRefreshAsync(target, completion, cancellationToken);
    }

    private async Task RunRefreshAsync(
        RefreshTarget target,
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        try
        {
            var refresh = RefreshAsync(target, cancellationToken);
            PublishRefreshStateChanged(
                target.View,
                WatchV2AutoRefreshPhase.Started);
            await refresh.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal is a neutral end to an in-flight automatic refresh.
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _lastUnhandledException = exception;
            }
        }
        finally
        {
            var publishCompleted = false;
            lock (_gate)
            {
                _refreshInProgress = false;
                if (!_disposed
                    && _schedule.ActiveView is { } activeView
                    && _schedule.GetDelayUntilNextDue() is null)
                {
                    _schedule.CompleteRefresh(activeView);
                }

                if (!_disposed)
                {
                    ArmTimerForScheduleLocked();
                    publishCompleted = true;
                }
            }

            if (publishCompleted)
            {
                PublishRefreshStateChanged(
                    target.View,
                    WatchV2AutoRefreshPhase.Completed);
            }

            completion.TrySetResult();
        }
    }

    private void PublishRefreshStateChanged(
        WatchV2DataView view,
        WatchV2AutoRefreshPhase phase)
    {
        EventHandler<WatchV2AutoRefreshEventArgs>? subscribers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            subscribers = RefreshStateChanged;
        }

        if (subscribers is null)
        {
            return;
        }

        var args = new WatchV2AutoRefreshEventArgs(view, phase);
        foreach (EventHandler<WatchV2AutoRefreshEventArgs> subscriber
                 in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch
            {
                // Rendering observers must not own the automatic-refresh
                // single-flight slot or prevent another observer from running.
            }
        }
    }

    private Task RefreshAsync(RefreshTarget target, CancellationToken cancellationToken) =>
        target.View switch
        {
            WatchV2DataView.Overview => _session.RefreshOverviewAsync(
                (WatchOverviewQuery)target.Query,
                cancellationToken),
            WatchV2DataView.DemandSeries => _session.RefreshLatestDemandSeriesPageAsync(
                (DemandSeriesBrowseQuery)target.Query,
                cancellationToken),
            WatchV2DataView.ReadabilityAudit => _session.RefreshReadabilityAuditAsync(
                (ReadabilityAuditQuery)target.Query,
                cancellationToken),
            WatchV2DataView.ErrorSearch => _session.RefreshErrorSearchAsync(
                (ErrorSearchQuery)target.Query,
                cancellationToken),
            WatchV2DataView.CurrentIngestAttention => _session.RefreshCurrentAttentionAsync(
                (CurrentIngestAttentionQuery)target.Query,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.View,
                null),
        };

    private void ArmTimerForScheduleLocked()
    {
        var delay = _schedule.GetDelayUntilNextDue();
        _timer.Change(
            delay ?? Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    private sealed record RefreshTarget(WatchV2DataView View, object Query);
}
