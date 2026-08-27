namespace MesIngest.Core;

/// <summary>
/// Authoritative scheduling state calculated by <see cref="SingleFlightPollLoop"/>.
/// </summary>
public sealed record PollSchedulerStateSnapshot(
    int ConsecutiveFailures,
    int BackoffLevel,
    DateTimeOffset? NextAllowedStart,
    DateTimeOffset? LastSuccessAt,
    string? PollTraceId)
{
    public static PollSchedulerStateSnapshot NotStarted { get; } = new(
        ConsecutiveFailures: 0,
        BackoffLevel: 0,
        NextAllowedStart: null,
        LastSuccessAt: null,
        PollTraceId: null);
}

/// <summary>
/// Receives immutable scheduler snapshots after the coordinator calculates the next start.
/// </summary>
public interface IPollSchedulerStateObserver
{
    void OnStateChanged(PollSchedulerStateSnapshot snapshot);
}
