namespace MesIngest.Core.SeriesProjection;

public enum RestartBarrierPhase
{
    Barrier,
    PostBarrier,
    Normal,
}

public static class RestartBarrierPhaseContract
{
    public const string Barrier = "BARRIER";
    public const string PostBarrier = "POST_BARRIER";
    public const string Normal = "NORMAL";

    public static string ToContractValue(this RestartBarrierPhase phase) => phase switch
    {
        RestartBarrierPhase.Barrier => Barrier,
        RestartBarrierPhase.PostBarrier => PostBarrier,
        RestartBarrierPhase.Normal => Normal,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown restart phase."),
    };

    public static RestartBarrierPhase Parse(string value) => value switch
    {
        Barrier => RestartBarrierPhase.Barrier,
        PostBarrier => RestartBarrierPhase.PostBarrier,
        Normal => RestartBarrierPhase.Normal,
        _ => throw new InvalidOperationException($"Stored restart phase '{value}' is not supported."),
    };
}

public static class RestartBarrierEventCode
{
    public const string Entered = "RESTART_BARRIER_ENTERED";
    public const string BaselineCompleted = "RESTART_BASELINE_COMPLETED";
    public const string AbsenceAuthorityRestored = "RESTART_ABSENCE_AUTHORITY_RESTORED";

    public static int GetLifecycleOrder(string eventCode) => eventCode switch
    {
        Entered => 0,
        BaselineCompleted => 1,
        AbsenceAuthorityRestored => 2,
        _ => 3,
    };
}

public readonly record struct RestartBarrierTransition(
    RestartBarrierPhase Before,
    RestartBarrierPhase After,
    bool AbsenceAuthority,
    string? EventCode)
{
    public static RestartBarrierTransition AcceptSuccess(RestartBarrierPhase phase) => phase switch
    {
        RestartBarrierPhase.Barrier => new(
            phase,
            RestartBarrierPhase.PostBarrier,
            AbsenceAuthority: false,
            RestartBarrierEventCode.BaselineCompleted),
        RestartBarrierPhase.PostBarrier => new(
            phase,
            RestartBarrierPhase.Normal,
            AbsenceAuthority: false,
            RestartBarrierEventCode.AbsenceAuthorityRestored),
        RestartBarrierPhase.Normal => new(
            phase,
            RestartBarrierPhase.Normal,
            AbsenceAuthority: true,
            EventCode: null),
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown restart phase."),
    };
}
