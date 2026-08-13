namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Per-WorkType protection phase. Stored and transport values must use
/// <see cref="TaskTypeProtectionPhaseContract"/> rather than enum names.
/// </summary>
public enum TaskTypeProtectionPhase
{
    Monitoring,
    PausedZeroDrop,
    Recovering,
    AuthorityPending,
}

public static class TaskTypeProtectionPhaseContract
{
    public const string Monitoring = "MONITORING";
    public const string PausedZeroDrop = "PAUSED_ZERO_DROP";
    public const string Recovering = "RECOVERING";
    public const string AuthorityPending = "AUTHORITY_PENDING";

    public static string ToContractValue(this TaskTypeProtectionPhase phase) => phase switch
    {
        TaskTypeProtectionPhase.Monitoring => Monitoring,
        TaskTypeProtectionPhase.PausedZeroDrop => PausedZeroDrop,
        TaskTypeProtectionPhase.Recovering => Recovering,
        TaskTypeProtectionPhase.AuthorityPending => AuthorityPending,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown task-type protection phase."),
    };

    public static TaskTypeProtectionPhase Parse(string value) => value switch
    {
        Monitoring => TaskTypeProtectionPhase.Monitoring,
        PausedZeroDrop => TaskTypeProtectionPhase.PausedZeroDrop,
        Recovering => TaskTypeProtectionPhase.Recovering,
        AuthorityPending => TaskTypeProtectionPhase.AuthorityPending,
        _ => throw new InvalidOperationException($"Stored task-type protection phase '{value}' is not supported."),
    };
}

/// <summary>
/// Immutable event vocabulary for a protection episode.
/// </summary>
public static class TaskTypeProtectionEventCode
{
    public const string Entered = "TASK_TYPE_PROTECTION_ENTERED";
    public const string RecoveryProgress = "TASK_TYPE_PROTECTION_RECOVERY_PROGRESS";
    public const string Cleared = "TASK_TYPE_PROTECTION_CLEARED";
    public const string AbsenceAuthorityRestored = "TASK_TYPE_ABSENCE_AUTHORITY_RESTORED";
}

/// <summary>
/// Minimal persisted input to the pure transition policy. Episode identity and
/// evidence timestamps belong to the projection store, not to the policy.
/// </summary>
public readonly record struct TaskTypeProtectionState(
    TaskTypeProtectionPhase Phase,
    int LastHealthyNonZeroCount,
    int LatestObservedCount,
    int RecoveryStreak)
{
    public static TaskTypeProtectionState Initial { get; } = new(
        TaskTypeProtectionPhase.Monitoring,
        LastHealthyNonZeroCount: 0,
        LatestObservedCount: 0,
        RecoveryStreak: 0);
}

/// <summary>
/// Result for one newly accepted complete SUCCESS round. Event codes are in
/// their required append order; the projection assigns stable event identities.
/// </summary>
public sealed record TaskTypeProtectionTransition(
    TaskTypeProtectionState Before,
    TaskTypeProtectionState After,
    bool ProtectionAllowsAbsenceAuthority,
    bool EffectiveAbsenceAuthorityAvailable,
    IReadOnlyList<string> EventCodes);

/// <summary>
/// Pure, per-WorkType zero-drop protection policy. FAILURE, INCOMPLETE,
/// replay, and conflict rounds must not call this policy.
/// </summary>
public static class TaskTypeProtectionPolicy
{
    public const int RequiredRecoveryStreak = 2;

    public static TaskTypeProtectionTransition AcceptSuccess(
        TaskTypeProtectionState state,
        int observedCount,
        int enterThreshold,
        bool restartAbsenceAuthority)
    {
        if (observedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedCount), observedCount, "Observed count cannot be negative.");
        }

        if (enterThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(enterThreshold), enterThreshold, "Enter threshold must be positive.");
        }

        Validate(state);

        var lastHealthy = observedCount > 0
            ? observedCount
            : state.LastHealthyNonZeroCount;

        return state.Phase switch
        {
            TaskTypeProtectionPhase.Monitoring => FromMonitoring(
                state,
                observedCount,
                enterThreshold,
                restartAbsenceAuthority,
                lastHealthy),
            TaskTypeProtectionPhase.PausedZeroDrop => FromPaused(
                state,
                observedCount,
                lastHealthy),
            TaskTypeProtectionPhase.Recovering => FromRecovering(
                state,
                observedCount,
                lastHealthy),
            TaskTypeProtectionPhase.AuthorityPending => FromAuthorityPending(
                state,
                observedCount,
                restartAbsenceAuthority,
                lastHealthy),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state.Phase, "Unknown task-type protection phase."),
        };
    }

    private static TaskTypeProtectionTransition FromMonitoring(
        TaskTypeProtectionState before,
        int observedCount,
        int enterThreshold,
        bool restartAbsenceAuthority,
        int lastHealthy)
    {
        var enters = observedCount == 0
            && before.LastHealthyNonZeroCount >= enterThreshold;
        var after = new TaskTypeProtectionState(
            enters ? TaskTypeProtectionPhase.PausedZeroDrop : TaskTypeProtectionPhase.Monitoring,
            lastHealthy,
            observedCount,
            RecoveryStreak: 0);

        return enters
            ? Transition(before, after, protectionAllowsAuthority: false, restartAbsenceAuthority, TaskTypeProtectionEventCode.Entered)
            : Transition(before, after, protectionAllowsAuthority: true, restartAbsenceAuthority);
    }

    private static TaskTypeProtectionTransition FromPaused(
        TaskTypeProtectionState before,
        int observedCount,
        int lastHealthy)
    {
        if (observedCount == 0)
        {
            var paused = new TaskTypeProtectionState(
                TaskTypeProtectionPhase.PausedZeroDrop,
                lastHealthy,
                observedCount,
                RecoveryStreak: 0);
            return Transition(before, paused, protectionAllowsAuthority: false, restartAbsenceAuthority: false);
        }

        var recovering = new TaskTypeProtectionState(
            TaskTypeProtectionPhase.Recovering,
            lastHealthy,
            observedCount,
            RecoveryStreak: 1);
        return Transition(
            before,
            recovering,
            protectionAllowsAuthority: false,
            restartAbsenceAuthority: false,
            TaskTypeProtectionEventCode.RecoveryProgress);
    }

    private static TaskTypeProtectionTransition FromRecovering(
        TaskTypeProtectionState before,
        int observedCount,
        int lastHealthy)
    {
        if (observedCount == 0)
        {
            var paused = new TaskTypeProtectionState(
                TaskTypeProtectionPhase.PausedZeroDrop,
                lastHealthy,
                observedCount,
                RecoveryStreak: 0);
            return Transition(before, paused, protectionAllowsAuthority: false, restartAbsenceAuthority: false);
        }

        var recoveryStreak = before.RecoveryStreak + 1;
        if (recoveryStreak < RequiredRecoveryStreak)
        {
            var recovering = new TaskTypeProtectionState(
                TaskTypeProtectionPhase.Recovering,
                lastHealthy,
                observedCount,
                recoveryStreak);
            return Transition(
                before,
                recovering,
                protectionAllowsAuthority: false,
                restartAbsenceAuthority: false,
                TaskTypeProtectionEventCode.RecoveryProgress);
        }

        var pending = new TaskTypeProtectionState(
            TaskTypeProtectionPhase.AuthorityPending,
            lastHealthy,
            observedCount,
            RequiredRecoveryStreak);
        return Transition(
            before,
            pending,
            protectionAllowsAuthority: false,
            restartAbsenceAuthority: false,
            TaskTypeProtectionEventCode.RecoveryProgress,
            TaskTypeProtectionEventCode.Cleared);
    }

    private static TaskTypeProtectionTransition FromAuthorityPending(
        TaskTypeProtectionState before,
        int observedCount,
        bool restartAbsenceAuthority,
        int lastHealthy)
    {
        if (!restartAbsenceAuthority)
        {
            var pending = before with
            {
                LastHealthyNonZeroCount = lastHealthy,
                LatestObservedCount = observedCount,
            };
            return Transition(before, pending, protectionAllowsAuthority: false, restartAbsenceAuthority: false);
        }

        var monitoring = new TaskTypeProtectionState(
            TaskTypeProtectionPhase.Monitoring,
            lastHealthy,
            observedCount,
            RecoveryStreak: 0);
        return Transition(
            before,
            monitoring,
            protectionAllowsAuthority: true,
            restartAbsenceAuthority: true,
            TaskTypeProtectionEventCode.AbsenceAuthorityRestored);
    }

    private static TaskTypeProtectionTransition Transition(
        TaskTypeProtectionState before,
        TaskTypeProtectionState after,
        bool protectionAllowsAuthority,
        bool restartAbsenceAuthority,
        params string[] eventCodes) => new(
            before,
            after,
            protectionAllowsAuthority,
            protectionAllowsAuthority && restartAbsenceAuthority,
            eventCodes);

    private static void Validate(TaskTypeProtectionState state)
    {
        if (state.LastHealthyNonZeroCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state.LastHealthyNonZeroCount, "Last healthy count cannot be negative.");
        }

        if (state.LatestObservedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state.LatestObservedCount, "Latest observed count cannot be negative.");
        }

        if (state.RecoveryStreak < 0 || state.RecoveryStreak > RequiredRecoveryStreak)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state.RecoveryStreak, "Recovery streak is outside the supported range.");
        }
    }
}
