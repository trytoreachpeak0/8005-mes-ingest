using MesIngest.Core;

namespace MesIngest.Watch;

/// <summary>
/// Records Watch latency telemetry IO failures into the local connection-event journal
/// without affecting the Host refresh path.
/// </summary>
internal static class WatchLatencyWriteFailureJournal
{
    public const string Endpoint = "watch-latency";
    public const string Stage = "TELEMETRY_IO";

    public static void Append(
        WatchConnectionEventJournal journal,
        Exception ex,
        DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(ex);

        journal.Append(new WatchConnectionEvent(
            Kind: WatchConnectionEventKind.Failure,
            At: at ?? DateTimeOffset.UtcNow,
            Endpoint: Endpoint,
            Stage: Stage,
            ElapsedMs: null,
            TimeoutSeconds: null,
            Message: LatencyLogFormatter.Sanitize(ex.Message),
            FailureCount: 1,
            OutageDurationMs: null));
    }
}
