namespace MesIngest.Watch;

internal sealed record WatchDemandDto(
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
    string? LocationRiskCode);

internal sealed record WatchAlertDto(
    string Code,
    string? TaskType,
    string? Sublot,
    string? DemandId,
    string? Message);

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
    IReadOnlyList<WatchTaskTypePauseDto> TaskTypePauses);
