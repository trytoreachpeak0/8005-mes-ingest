namespace MesIngest.Watch;

internal enum WatchNotificationSeverity
{
    Information,
    Success,
    Warning,
    Error,
}

internal readonly record struct WatchNotificationScope(WatchWorkspacePage? Page)
{
    public static WatchNotificationScope Global { get; } = new(null);

    public static WatchNotificationScope ForPage(WatchWorkspacePage page) => new(page);

    public bool IsGlobal => Page is null;
}

internal readonly record struct WatchNotificationSource(
    string Category,
    WatchNotificationScope Scope,
    string BusinessIdentity)
{
    public string Key => string.Join(
        '|',
        NormalizeKeyPart(Category, nameof(Category)),
        Scope.IsGlobal ? "global" : $"page:{Scope.Page}",
        NormalizeKeyPart(BusinessIdentity, nameof(BusinessIdentity)));

    private static string NormalizeKeyPart(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

internal sealed record WatchNotificationEvent(
    WatchNotificationSource Source,
    WatchNotificationSeverity Severity,
    string SeverityText,
    string Title,
    string Message,
    string? ActionLabel = null,
    Action? Action = null)
{
    public TimeSpan DefaultLifetime => Severity switch
    {
        WatchNotificationSeverity.Warning => TimeSpan.FromSeconds(5),
        WatchNotificationSeverity.Error => TimeSpan.FromSeconds(8),
        _ => TimeSpan.FromSeconds(3),
    };
}

internal sealed record WatchNotificationSnapshot(
    string SourceKey,
    WatchNotificationSeverity Severity,
    string SeverityText,
    string Title,
    string Message,
    string? ActionLabel,
    int Occurrences,
    int RemainingSeconds,
    DateTimeOffset LastActivityAt)
{
    public string OccurrenceText => Occurrences > 1
        ? $"已合并 {Occurrences} 次"
        : "首次出现";

    public string TimerText => $"{RemainingSeconds} 秒";

    public string AutomationName =>
        $"{SeverityText}。{Title}。{Message}。{OccurrenceText}";
}

internal sealed class WatchNotificationSnapshotChangedEventArgs(
    IReadOnlyList<WatchNotificationSnapshot> items,
    string? announcement,
    bool shouldAnimate = false) : EventArgs
{
    public IReadOnlyList<WatchNotificationSnapshot> Items { get; } = items;

    public string? Announcement { get; } = announcement;

    public bool ShouldAnimate { get; } = shouldAnimate;
}

/// <summary>
/// Owns the transient notification lifecycle for one production Watch window.
/// WPF projects its immutable snapshots; it does not own expiry, ordering, or
/// coalescing decisions.
/// </summary>
internal sealed class WatchWindowNotificationCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly ITimer _timer;
    private readonly List<Entry> _entries = [];
    private long _activitySequence;
    private DateTimeOffset? _pausedAt;
    private bool _disposed;

    public WatchWindowNotificationCoordinator(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _timer = _clock.CreateTimer(
            static state => ((WatchWindowNotificationCoordinator)state!).OnTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public event EventHandler<WatchNotificationSnapshotChangedEventArgs>? SnapshotChanged;

    public void Present(WatchNotificationEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Validate(notification);

        WatchNotificationSnapshotChangedEventArgs change;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = EffectiveNow();
            RemoveExpired(now);
            var sourceKey = notification.Source.Key;
            var existing = _entries.FirstOrDefault(item => item.SourceKey == sourceKey);
            if (existing is null)
            {
                existing = new Entry(sourceKey, notification.Source.Scope);
                _entries.Add(existing);
            }

            existing.Severity = notification.Severity;
            existing.SeverityText = NormalizeText(notification.SeverityText, 24);
            existing.Title = NormalizeText(notification.Title, 80);
            existing.Message = NormalizeText(notification.Message, 240);
            existing.ActionLabel = notification.Action is null
                ? null
                : NormalizeText(notification.ActionLabel!, 32);
            existing.Action = notification.Action;
            existing.Occurrences = existing.Occurrences == 0
                ? 1
                : existing.Occurrences + 1;
            existing.LastActivityAt = now;
            existing.ActivitySequence = ++_activitySequence;
            var defaultExpiry = now + notification.DefaultLifetime;
            existing.ExpiresAt = existing.ExpiresAt > defaultExpiry
                ? existing.ExpiresAt
                : defaultExpiry;

            TrimToVisibleCapacity();
            ScheduleNextTimer(now);
            var retained = _entries.Contains(existing);
            var announcement = retained && existing.Occurrences == 1
                ? $"{existing.SeverityText}。{existing.Title}。{existing.Message}"
                : null;
            change = CreateChange(now, announcement, shouldAnimate: true);
        }

        RaiseSnapshotChanged(change);
    }

    public void Dismiss(string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        WatchNotificationSnapshotChangedEventArgs? change = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var removed = _entries.RemoveAll(item => item.SourceKey == sourceKey) > 0;
            if (removed)
            {
                ResetPauseWhenEmpty();
                ScheduleNextTimer(EffectiveNow());
                change = CreateChange(
                    EffectiveNow(),
                    announcement: null,
                    shouldAnimate: true);
            }
        }

        if (change is not null)
        {
            RaiseSnapshotChanged(change);
        }
    }

    public void ExecuteAction(string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        Action? action;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            action = _entries.FirstOrDefault(item => item.SourceKey == sourceKey)?.Action;
        }

        action?.Invoke();
    }

    public void ClearPage(WatchWorkspacePage page)
    {
        WatchNotificationSnapshotChangedEventArgs? change = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var removed = _entries.RemoveAll(item => item.Scope.Page == page) > 0;
            if (removed)
            {
                ResetPauseWhenEmpty();
                ScheduleNextTimer(EffectiveNow());
                change = CreateChange(
                    EffectiveNow(),
                    announcement: null,
                    shouldAnimate: true);
            }
        }

        if (change is not null)
        {
            RaiseSnapshotChanged(change);
        }
    }

    public void SetPaused(bool paused)
    {
        WatchNotificationSnapshotChangedEventArgs? change = null;
        lock (_gate)
        {
            if (_disposed || paused == (_pausedAt is not null))
            {
                return;
            }

            var now = _clock.GetUtcNow();
            if (paused)
            {
                _pausedAt = now;
            }
            else
            {
                var pausedFor = now - _pausedAt!.Value;
                foreach (var entry in _entries)
                {
                    entry.ExpiresAt += pausedFor;
                }

                _pausedAt = null;
            }

            ScheduleNextTimer(EffectiveNow());
            change = CreateChange(
                EffectiveNow(),
                announcement: null,
                shouldAnimate: false);
        }

        RaiseSnapshotChanged(change);
    }

    public IReadOnlyList<WatchNotificationSnapshot> GetSnapshot()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return [];
            }

            var now = EffectiveNow();
            RemoveExpired(now);
            return CreateSnapshots(now);
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
            _entries.Clear();
        }

        _timer.Dispose();
    }

    private void OnTimer()
    {
        WatchNotificationSnapshotChangedEventArgs? change = null;
        lock (_gate)
        {
            if (_disposed || _pausedAt is not null || _entries.Count == 0)
            {
                return;
            }

            var now = _clock.GetUtcNow();
            var removed = RemoveExpired(now);
            var after = CreateSnapshots(now);
            ScheduleNextTimer(now);
            change = new WatchNotificationSnapshotChangedEventArgs(
                after,
                announcement: null,
                shouldAnimate: removed);
        }

        if (change is not null)
        {
            RaiseSnapshotChanged(change);
        }
    }

    private DateTimeOffset EffectiveNow() => _pausedAt ?? _clock.GetUtcNow();

    private bool RemoveExpired(DateTimeOffset now)
    {
        var removed = _entries.RemoveAll(item => item.ExpiresAt <= now) > 0;
        ResetPauseWhenEmpty();
        return removed;
    }

    private void ResetPauseWhenEmpty()
    {
        if (_entries.Count == 0)
        {
            _pausedAt = null;
        }
    }

    private void ScheduleNextTimer(DateTimeOffset now)
    {
        if (_pausedAt is not null || _entries.Count == 0)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var remaining = _entries.Min(item => item.ExpiresAt - now);
        if (remaining <= TimeSpan.Zero)
        {
            remaining = TimeSpan.FromTicks(1);
        }

        var fractionalSeconds = remaining.TotalSeconds - Math.Floor(remaining.TotalSeconds);
        var untilDisplayedSecondChanges = fractionalSeconds > 0
            ? TimeSpan.FromSeconds(fractionalSeconds)
            : TimeSpan.FromSeconds(1);
        var dueTime = remaining < untilDisplayedSecondChanges
            ? remaining
            : untilDisplayedSecondChanges;
        _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
    }

    private void TrimToVisibleCapacity()
    {
        while (_entries.Count > 3)
        {
            var discard = _entries
                .OrderBy(item => Priority(item.Severity))
                .ThenBy(item => item.ActivitySequence)
                .First();
            _entries.Remove(discard);
        }
    }

    private WatchNotificationSnapshotChangedEventArgs CreateChange(
        DateTimeOffset now,
        string? announcement,
        bool shouldAnimate) =>
        new(CreateSnapshots(now), announcement, shouldAnimate);

    private IReadOnlyList<WatchNotificationSnapshot> CreateSnapshots(DateTimeOffset now) =>
        _entries
            .OrderByDescending(item => item.ActivitySequence)
            .Select(item => new WatchNotificationSnapshot(
                item.SourceKey,
                item.Severity,
                item.SeverityText,
                item.Title,
                item.Message,
                item.ActionLabel,
                item.Occurrences,
                Math.Max(0, (int)Math.Ceiling((item.ExpiresAt - now).TotalSeconds)),
                item.LastActivityAt))
            .ToArray();

    private void RaiseSnapshotChanged(WatchNotificationSnapshotChangedEventArgs change) =>
        SnapshotChanged?.Invoke(this, change);

    private static int Priority(WatchNotificationSeverity severity) => severity switch
    {
        WatchNotificationSeverity.Error => 3,
        WatchNotificationSeverity.Warning => 2,
        _ => 1,
    };

    private static void Validate(WatchNotificationEvent notification)
    {
        _ = notification.Source.Key;
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.SeverityText);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Message);
        if ((notification.Action is null) != string.IsNullOrWhiteSpace(notification.ActionLabel))
        {
            throw new ArgumentException(
                "Notification action and action label must either both be present or both be absent.",
                nameof(notification));
        }
    }

    private static string NormalizeText(string value, int maximumLength)
    {
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..(maximumLength - 1)] + "…";
    }

    private sealed class Entry(string sourceKey, WatchNotificationScope scope)
    {
        public string SourceKey { get; } = sourceKey;
        public WatchNotificationScope Scope { get; } = scope;
        public WatchNotificationSeverity Severity { get; set; }
        public string SeverityText { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? ActionLabel { get; set; }
        public Action? Action { get; set; }
        public int Occurrences { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset LastActivityAt { get; set; }
        public long ActivitySequence { get; set; }
    }
}
