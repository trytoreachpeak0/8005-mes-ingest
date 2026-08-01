namespace MesIngest.Core;

public enum DemandStatus
{
    Visible,
    Gone,
}

public enum SnapshotOutcomeKind
{
    Success,
    Failure,
    Incomplete,
}

/// <summary>
/// Business identity of a transport demand. Components are compared ordinally and
/// case-sensitively; non-blank values are preserved without normalization.
/// </summary>
public sealed record TransportDemandKey
{
    public TransportDemandKey(string taskType, string sublot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sublot);

        TaskType = taskType;
        Sublot = sublot;
    }

    public string TaskType { get; }
    public string Sublot { get; }
}

public sealed record MesSnapshotRow(
    string TaskType,
    string Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset Dates,
    string? Package);

public sealed class MesSnapshotOutcome
{
    private MesSnapshotOutcome(
        SnapshotOutcomeKind kind,
        IReadOnlyList<MesSnapshotRow> rows,
        string? failureStage = null,
        double? oracleDurationMs = null)
    {
        Kind = kind;
        Rows = rows;
        FailureStage = failureStage;
        OracleDurationMs = oracleDurationMs;
    }

    public SnapshotOutcomeKind Kind { get; }
    public IReadOnlyList<MesSnapshotRow> Rows { get; }
    public string? FailureStage { get; }
    public double? OracleDurationMs { get; }

    public static MesSnapshotOutcome Success(
        IReadOnlyList<MesSnapshotRow> rows,
        double? oracleDurationMs = null) =>
        new(SnapshotOutcomeKind.Success, rows, oracleDurationMs: oracleDurationMs);

    public static MesSnapshotOutcome Failure(
        string? failureStage = null,
        double? oracleDurationMs = null) =>
        new(
            SnapshotOutcomeKind.Failure,
            Array.Empty<MesSnapshotRow>(),
            failureStage,
            oracleDurationMs);

    public static MesSnapshotOutcome Incomplete(double? oracleDurationMs = null) =>
        new(SnapshotOutcomeKind.Incomplete, Array.Empty<MesSnapshotRow>(), oracleDurationMs: oracleDurationMs);
}

public sealed record TransportDemand
{
    public required string DemandId { get; init; }
    public required string TaskType { get; init; }
    public required string Sublot { get; init; }
    public string? Area { get; init; }
    public string? Eqp { get; init; }
    public string? Step { get; init; }
    public DateTimeOffset Dates { get; init; }
    public string? Package { get; init; }
    public required DemandStatus Status { get; init; }
    public required DateTimeOffset MesLastSeenAt { get; init; }
    public int DisappearCount { get; init; }
    public bool LocationRisk { get; init; }
    public string? LocationRiskCode { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? GoneAt { get; init; }
}

public sealed record TaskTypePauseState(
    string TaskType,
    bool PausedZeroDrop,
    int LastHealthyNonZeroCount,
    int RecoveryStreak);

public sealed class ProjectionState
{
    public static ProjectionState Empty { get; } = new(Array.Empty<TransportDemand>());

    public ProjectionState(
        IReadOnlyList<TransportDemand> demands,
        IReadOnlyList<TaskTypePauseState>? taskTypePauses = null)
    {
        Demands = demands;
        TaskTypePauses = taskTypePauses ?? Array.Empty<TaskTypePauseState>();
    }

    public IReadOnlyList<TransportDemand> Demands { get; }
    public IReadOnlyList<TaskTypePauseState> TaskTypePauses { get; }
}

public sealed class ReconcileResult
{
    public ReconcileResult(ProjectionState state, IReadOnlyList<IngestAlert>? alerts = null)
    {
        State = state;
        Alerts = alerts ?? Array.Empty<IngestAlert>();
    }

    public ProjectionState State { get; }
    public IReadOnlyList<IngestAlert> Alerts { get; }
}

public static class AlertCodes
{
    public const string PollFailure = "POLL_FAILURE";
    public const string PollIncomplete = "POLL_INCOMPLETE";
    public const string DuplicateReconcileKey = "DUPLICATE_RECONCILE_KEY";
    public const string PausedZeroDrop = "PAUSED_ZERO_DROP";
    public const string FieldDrift = "FIELD_DRIFT";
    public const string ReappearAfterGone = "REAPPEAR_AFTER_GONE";
}

public static class AlertSeverities
{
    public const string Error = "ERROR";
    public const string Warning = "WARNING";
}

public sealed record IngestAlert(
    string Code,
    string? TaskType = null,
    string? Sublot = null,
    string? DemandId = null,
    string? Message = null,
    DateTimeOffset? CreatedAt = null,
    string? AlertId = null,
    string? Severity = null,
    string? Details = null,
    string? DetailsFingerprint = null,
    DateTimeOffset? FirstSeenAt = null,
    DateTimeOffset? LastSeenAt = null,
    int OccurrenceCount = 1,
    bool IsActive = true,
    DateTimeOffset? ResolvedAt = null)
{
    public DateTimeOffset EffectiveFirstSeenAt =>
        FirstSeenAt ?? CreatedAt ?? DateTimeOffset.MinValue;

    public DateTimeOffset EffectiveLastSeenAt =>
        LastSeenAt ?? FirstSeenAt ?? CreatedAt ?? DateTimeOffset.MinValue;
}

/// <summary>
/// Process-start recovery semantics (business model §6.4).
/// BarrierRound: first successful full poll after start — create/refresh only.
/// PostBarrierRound: second successful poll — adopt barrier counts then normal rules.
/// </summary>
public sealed class RestartRecovery
{
    public static RestartRecovery Normal { get; } = new(RestartRecoveryPhase.Normal, null);

    private RestartRecovery(
        RestartRecoveryPhase phase,
        IReadOnlyDictionary<string, int>? barrierRoundCountsByType)
    {
        Phase = phase;
        BarrierRoundCountsByType = barrierRoundCountsByType;
    }

    public RestartRecoveryPhase Phase { get; }
    public IReadOnlyDictionary<string, int>? BarrierRoundCountsByType { get; }

    public static RestartRecovery BarrierRound() =>
        new(RestartRecoveryPhase.BarrierRound, null);

    public static RestartRecovery PostBarrierRound(IReadOnlyDictionary<string, int> barrierRoundCountsByType) =>
        new(RestartRecoveryPhase.PostBarrierRound, barrierRoundCountsByType);
}

public enum RestartRecoveryPhase
{
    Normal,
    BarrierRound,
    PostBarrierRound,
}

public interface IDemandIdAllocator
{
    string Next();
}

public sealed class SequentialDemandIdAllocator : IDemandIdAllocator
{
    private readonly Queue<string> _ids;

    public SequentialDemandIdAllocator(params string[] ids)
    {
        _ids = new Queue<string>(ids);
    }

    public string Next() =>
        _ids.Count > 0 ? _ids.Dequeue() : Guid.NewGuid().ToString("N");
}

public sealed class GuidDemandIdAllocator : IDemandIdAllocator
{
    public string Next() => Guid.NewGuid().ToString("N");
}
