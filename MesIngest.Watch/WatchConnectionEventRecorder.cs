namespace MesIngest.Watch;

internal enum WatchConnectionEventKind
{
    Failure,
    Summary,
    Recovered,
}

internal sealed record WatchConnectionEvent(
    WatchConnectionEventKind Kind,
    DateTimeOffset At,
    string? Endpoint,
    string? Stage,
    long? ElapsedMs,
    int? TimeoutSeconds,
    string? Message,
    int FailureCount,
    long? OutageDurationMs,
    string? CorrelationId = null);

/// <summary>
/// Deduplicates Watch HTTP outage observations: first failure, 5-minute summaries, recovery.
/// Does not write to disk — callers persist emitted events.
/// </summary>
internal sealed class WatchConnectionEventRecorder
{
    private readonly TimeSpan _summaryInterval;
    private DateTimeOffset? _outageStartedAt;
    private DateTimeOffset? _lastEmittedAt;
    private int _failureCount;
    private string? _lastEndpoint;
    private string? _lastStage;
    private long? _lastElapsedMs;
    private int? _lastTimeoutSeconds;
    private string? _lastMessage;
    private string? _lastCorrelationId;

    public WatchConnectionEventRecorder(TimeSpan summaryInterval)
    {
        if (summaryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(summaryInterval));
        }

        _summaryInterval = summaryInterval;
    }

    public WatchConnectionEvent? ObserveFailure(
        DateTimeOffset at,
        string endpoint,
        string stage,
        TimeSpan elapsed,
        int timeoutSeconds,
        string message,
        string? correlationId = null)
    {
        _failureCount++;
        _lastEndpoint = endpoint;
        _lastStage = stage;
        _lastElapsedMs = (long)elapsed.TotalMilliseconds;
        _lastTimeoutSeconds = timeoutSeconds;
        _lastMessage = message;
        _lastCorrelationId = correlationId;

        if (_outageStartedAt is null)
        {
            _outageStartedAt = at;
            _lastEmittedAt = at;
            return new WatchConnectionEvent(
                Kind: WatchConnectionEventKind.Failure,
                At: at,
                Endpoint: endpoint,
                Stage: stage,
                ElapsedMs: _lastElapsedMs,
                TimeoutSeconds: timeoutSeconds,
                Message: message,
                FailureCount: _failureCount,
                OutageDurationMs: null,
                CorrelationId: correlationId);
        }

        if (_lastEmittedAt is { } lastEmitted && at - lastEmitted >= _summaryInterval)
        {
            _lastEmittedAt = at;
            return new WatchConnectionEvent(
                Kind: WatchConnectionEventKind.Summary,
                At: at,
                Endpoint: endpoint,
                Stage: stage,
                ElapsedMs: _lastElapsedMs,
                TimeoutSeconds: timeoutSeconds,
                Message: message,
                FailureCount: _failureCount,
                OutageDurationMs: (long)(at - _outageStartedAt.Value).TotalMilliseconds,
                CorrelationId: correlationId);
        }

        return null;
    }

    public WatchConnectionEvent? ObserveSuccess(DateTimeOffset at)
    {
        if (_outageStartedAt is null)
        {
            return null;
        }

        var started = _outageStartedAt.Value;
        var count = _failureCount;
        var endpoint = _lastEndpoint;
        var stage = _lastStage;
        var elapsedMs = _lastElapsedMs;
        var timeoutSeconds = _lastTimeoutSeconds;
        var message = _lastMessage;
        var correlationId = _lastCorrelationId;

        _outageStartedAt = null;
        _lastEmittedAt = null;
        _failureCount = 0;
        _lastEndpoint = null;
        _lastStage = null;
        _lastElapsedMs = null;
        _lastTimeoutSeconds = null;
        _lastMessage = null;
        _lastCorrelationId = null;

        return new WatchConnectionEvent(
            Kind: WatchConnectionEventKind.Recovered,
            At: at,
            Endpoint: endpoint,
            Stage: stage,
            ElapsedMs: elapsedMs,
            TimeoutSeconds: timeoutSeconds,
            Message: message,
            FailureCount: count,
            OutageDurationMs: (long)(at - started).TotalMilliseconds,
            CorrelationId: correlationId);
    }
}
