namespace MesIngest.Watch;

/// <summary>
/// Builds the operator-facing Events feed from Host alerts, persisted Watch events,
/// and the independent in-memory fallback for local logging failures.
/// </summary>
internal sealed class WatchUnifiedEventFeed
{
    private readonly WatchConnectionEventJournal _journal;
    private readonly WatchTelemetryIoDiagnosticBuffer _diagnostics;

    public WatchUnifiedEventFeed(
        WatchConnectionEventJournal journal,
        WatchTelemetryIoDiagnosticBuffer diagnostics)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public IReadOnlyList<UnifiedWatchEvent> Load(
        IEnumerable<WatchAlertDto> alerts,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        var zone = timeZone ?? TimeZoneInfo.Local;
        var host = alerts.Select(alert => UnifiedWatchEvent.FromAlert(alert, zone));

        var watch = _journal.ReadRecent(200)
            .Concat(_diagnostics.ReadRecent(200))
            .Select(connectionEvent => UnifiedWatchEvent.FromConnectionEvent(connectionEvent, zone));

        return host.Concat(watch)
            .OrderByDescending(evt => evt.At)
            .ToList();
    }
}
