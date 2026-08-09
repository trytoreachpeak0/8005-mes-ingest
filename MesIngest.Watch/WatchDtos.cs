namespace MesIngest.Watch;

public sealed record WatchDemandDto(
    string DemandId,
    string TaskType,
    string Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset Dates,
    string? Package,
    string Status,
    DateTimeOffset MesLastSeenAt,
    int DisappearCount,
    bool LocationRisk,
    string? LocationRiskCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? GoneAt);

public sealed record WatchAlertDto(
    string? AlertId,
    string Code,
    string? Severity,
    string? TaskType,
    string? Sublot,
    string? DemandId,
    string? Message,
    string? Details,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    int OccurrenceCount,
    bool IsActive,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? CreatedAt);

internal sealed record WatchAlertPage(
    IReadOnlyList<WatchAlertDto> Items,
    string? NextCursor,
    bool HasMore);

internal sealed record WatchTaskTypePauseDto(
    string TaskType,
    bool PausedZeroDrop,
    int LastHealthyNonZeroCount,
    int RecoveryStreak);

internal sealed record WatchPollHealthDto(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double DurationMs,
    int RowCount,
    bool Success,
    string Outcome,
    IReadOnlyList<WatchTaskTypePauseDto> TaskTypePauses,
    string? FailureStage = null,
    double? OracleDurationMs = null);
