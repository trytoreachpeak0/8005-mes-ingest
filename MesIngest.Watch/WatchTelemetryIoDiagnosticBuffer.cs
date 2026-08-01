using MesIngest.Core;

namespace MesIngest.Watch;

/// <summary>
/// Bounded in-memory fallback for local log failures. It deliberately has no dependency
/// on the primary log directory so operators can still inspect TELEMETRY_IO events.
/// </summary>
internal sealed class WatchTelemetryIoDiagnosticBuffer
{
    public const string Stage = "TELEMETRY_IO";

    private readonly int _capacity;
    private readonly Queue<WatchConnectionEvent> _events = new();
    private readonly object _gate = new();

    public WatchTelemetryIoDiagnosticBuffer(int capacity = 200)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public void Record(string endpoint, Exception ex, DateTimeOffset? at = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(ex);

        var diagnostic = new WatchConnectionEvent(
            Kind: WatchConnectionEventKind.Failure,
            At: at ?? DateTimeOffset.UtcNow,
            Endpoint: endpoint,
            Stage: Stage,
            ElapsedMs: null,
            TimeoutSeconds: null,
            Message: LatencyLogFormatter.Sanitize(ex.Message),
            FailureCount: 1,
            OutageDurationMs: null);

        lock (_gate)
        {
            _events.Enqueue(diagnostic);
            while (_events.Count > _capacity)
            {
                _events.Dequeue();
            }
        }
    }

    public IReadOnlyList<WatchConnectionEvent> ReadRecent(int maxCount = 200)
    {
        if (maxCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        }

        lock (_gate)
        {
            return _events
                .Reverse()
                .Take(maxCount)
                .ToList();
        }
    }
}
