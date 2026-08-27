using MesIngest.Core;

namespace MesIngest.Host;

/// <summary>
/// Process-lifetime holder for the latest coordinator-owned scheduler snapshot.
/// </summary>
public sealed class PollSchedulerState : IPollSchedulerStateObserver
{
    private PollSchedulerStateSnapshot _current = PollSchedulerStateSnapshot.NotStarted;

    public PollSchedulerStateSnapshot Current => Volatile.Read(ref _current);

    public void OnStateChanged(PollSchedulerStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
