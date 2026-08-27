namespace MesIngest.Watch;

internal sealed record WatchContinuingFault(
    string SourceKey,
    WatchNotificationScope Scope,
    WatchNotificationSeverity Severity,
    string Title,
    string Message,
    string ActionLabel,
    WatchLocalizedNotificationContent? LocalizedContent = null)
{
    internal (string Title, string Message, string ActionLabel) Project(
        WatchDisplayLanguage language) => LocalizedContent is null
        ? (Title, Message, ActionLabel)
        : (
            LocalizedContent.Title.In(language),
            LocalizedContent.Message.In(language),
            LocalizedContent.ActionLabel?.In(language) ?? ActionLabel);
}

internal sealed record WatchFeedbackLifecycleChange(
    IReadOnlyList<WatchContinuingFault> Started,
    IReadOnlyList<WatchContinuingFault> Recovered,
    IReadOnlyList<WatchContinuingFault> Active,
    IReadOnlyList<WatchContinuingFault> ForegroundSummary)
{
    public static WatchFeedbackLifecycleChange Empty(
        IReadOnlyList<WatchContinuingFault> active) => new([], [], active, []);
}

/// <summary>
/// Remembers only the notification boundary of a continuing fault. The Host
/// workspace remains the authority for whether the fault is active.
/// </summary>
internal sealed class WatchFeedbackLifecycle
{
    private readonly Dictionary<string, WatchContinuingFault> _active =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _newWhileBackground = new(StringComparer.Ordinal);
    private bool _isForeground = true;

    public IReadOnlyList<WatchContinuingFault> Active => [.. _active.Values];

    public WatchFeedbackLifecycleChange Reset(
        IEnumerable<WatchContinuingFault> currentFaults)
    {
        ArgumentNullException.ThrowIfNull(currentFaults);
        _active.Clear();
        foreach (var fault in currentFaults)
        {
            _active[fault.SourceKey] = fault;
        }

        _newWhileBackground.Clear();
        return WatchFeedbackLifecycleChange.Empty(Active);
    }

    public WatchFeedbackLifecycleChange Update(
        IEnumerable<WatchContinuingFault> currentFaults)
    {
        ArgumentNullException.ThrowIfNull(currentFaults);
        var current = currentFaults.ToDictionary(item => item.SourceKey, StringComparer.Ordinal);
        var started = new List<WatchContinuingFault>();
        var recovered = new List<WatchContinuingFault>();

        foreach (var prior in _active.Values)
        {
            if (current.ContainsKey(prior.SourceKey))
            {
                continue;
            }

            _newWhileBackground.Remove(prior.SourceKey);
            if (_isForeground)
            {
                recovered.Add(prior);
            }
        }

        foreach (var fault in current.Values)
        {
            if (_active.ContainsKey(fault.SourceKey))
            {
                continue;
            }

            if (_isForeground)
            {
                started.Add(fault);
            }
            else
            {
                _newWhileBackground.Add(fault.SourceKey);
            }
        }

        _active.Clear();
        foreach (var fault in current.Values)
        {
            _active.Add(fault.SourceKey, fault);
        }

        return new WatchFeedbackLifecycleChange(started, recovered, Active, []);
    }

    public WatchFeedbackLifecycleChange SetForeground(bool isForeground)
    {
        if (_isForeground == isForeground)
        {
            return WatchFeedbackLifecycleChange.Empty(Active);
        }

        _isForeground = isForeground;
        if (!isForeground)
        {
            return WatchFeedbackLifecycleChange.Empty(Active);
        }

        var summary = _newWhileBackground
            .Select(key => _active.GetValueOrDefault(key))
            .Where(fault => fault is not null)
            .Cast<WatchContinuingFault>()
            .OrderByDescending(fault => fault.Severity)
            .ToArray();
        _newWhileBackground.Clear();
        return new WatchFeedbackLifecycleChange([], [], Active, summary);
    }
}
