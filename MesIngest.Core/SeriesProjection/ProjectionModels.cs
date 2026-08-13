namespace MesIngest.Core.SeriesProjection;

public sealed record RoundCommitReceipt(
    string PollTraceId,
    MesTaskUnionRoundOutcome Outcome,
    string? ProjectionCommitId,
    IReadOnlyList<string> SeriesIds,
    IReadOnlyList<string> DemandIds,
    bool IsReplay);

public sealed record ProjectionCommitSnapshot(
    string ProjectionCommitId,
    string PollTraceId,
    DateTimeOffset CommittedAt,
    string HostSessionId,
    string RestartPhaseBefore,
    string RestartPhaseAfter,
    bool AbsenceAuthority,
    IReadOnlyList<TaskTypeProtectionDecisionSnapshot> TaskTypeProtectionDecisions);

public sealed record TaskTypeProtectionDecisionSnapshot(
    string WorkType,
    string PhaseBefore,
    string PhaseAfter,
    int ObservedCount,
    int LastHealthyNonZeroCount,
    int RecoveryStreakBefore,
    int RecoveryStreakAfter,
    bool ProtectionAllowsAbsenceAuthority,
    bool EffectiveAbsenceAuthorityAvailable,
    IReadOnlyList<string> EventIds);

public sealed record TaskTypeProtectionEventSnapshot(
    string EventId,
    string EpisodeId,
    string WorkType,
    long WorkTypeSequence,
    string EventType,
    DateTimeOffset OccurredAt,
    string PollTraceId,
    string ProjectionCommitId,
    string PhaseBefore,
    string PhaseAfter,
    int ObservedCount,
    int LastHealthyNonZeroCount,
    int RecoveryStreak,
    int RequiredRecoveryStreak,
    int EnterThreshold);

public sealed record TaskTypeProtectionSnapshot(
    string WorkType,
    string Phase,
    bool IsCurrentAttention,
    int LastHealthyNonZeroCount,
    int LatestObservedCount,
    int RecoveryStreak,
    int RequiredRecoveryStreak,
    int EnterThreshold,
    string? EpisodeId,
    DateTimeOffset? EnteredAt,
    bool ProtectionAllowsAbsenceAuthority,
    bool EffectiveAbsenceAuthorityAvailable,
    string LatestPollTraceId,
    string LatestProjectionCommitId,
    IReadOnlyList<TaskTypeProtectionEventSnapshot> Events);

public sealed record AbsenceAuthorityEventSnapshot(
    string EventId,
    string HostSessionId,
    string EventType,
    DateTimeOffset OccurredAt,
    string? PollTraceId,
    string? ProjectionCommitId,
    string PhaseBefore,
    string PhaseAfter);

public sealed record AbsenceAuthoritySnapshot(
    string HostSessionId,
    DateTimeOffset StartedAt,
    string Phase,
    bool IsCurrent,
    bool AbsenceAuthorityAvailable,
    IReadOnlyList<AbsenceAuthorityEventSnapshot> Events);

public sealed record LiveMesFieldSetSnapshot(
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package);

public sealed record SeriesErrorPeriodEvidenceSnapshot(
    string EvidenceId,
    string EvidenceKind,
    DateTimeOffset ObservedAt,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    string? ObservedValue,
    string ExpectedRule);

public sealed record DemandSeriesCurrentConditionSnapshot(
    string PeriodId,
    string Code,
    string Category,
    string Severity,
    string Target,
    string SubjectKind,
    DateTimeOffset StartedAt,
    DateTimeOffset LatestEvidenceAt,
    string LatestPollTraceId,
    string LatestProjectionCommitId,
    string DemandId,
    string? ObservedValue,
    string ExpectedRule);

public sealed record DemandSeriesErrorPeriodSnapshot(
    string PeriodId,
    string Code,
    string Category,
    string Severity,
    string Target,
    string SubjectKind,
    string StartReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    IReadOnlyList<SeriesErrorPeriodEvidenceSnapshot> Evidence);

public sealed record TransportDemandSnapshot(
    string DemandId,
    string SeriesId,
    int Generation,
    string? PredecessorDemandId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset DemandLastSeenAt,
    DateTimeOffset? GoneConfirmedAt,
    string CreatedPollTraceId,
    string CreatedProjectionCommitId,
    string LatestProjectionCommitId,
    LiveMesFieldSetSnapshot? LiveMesFields,
    string ExternalReadabilityState,
    IReadOnlyList<string> ReadabilityBlockers);

public enum MesObservationAssignment
{
    Assigned,
    Unassigned,
}

public sealed record DemandRawObservationSnapshot(
    int Ordinal,
    string PollTraceId,
    string ProjectionCommitId,
    MesObservationAssignment Assignment,
    string? SeriesId,
    string? DemandId,
    string? WorkType,
    string? Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package);

public sealed record DemandSeriesEventSnapshot(
    string EventId,
    string SeriesId,
    long SeriesSequence,
    string EventType,
    DateTimeOffset OccurredAt,
    string SubjectKind,
    string? SubjectId,
    string PollTraceId,
    string ProjectionCommitId,
    int PayloadVersion,
    string PayloadJson);

public sealed record DemandSeriesSnapshot(
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string CurrentPresence,
    DateTimeOffset StartedAt,
    string CreatedPollTraceId,
    string CreatedProjectionCommitId,
    string LatestProjectionCommitId,
    TransportDemandSnapshot CurrentDemand,
    IReadOnlyList<TransportDemandSnapshot> Demands,
    IReadOnlyList<DemandRawObservationSnapshot> RawObservations,
    IReadOnlyList<DemandSeriesEventSnapshot> Events,
    IReadOnlyList<DemandSeriesCurrentConditionSnapshot> CurrentConditions,
    IReadOnlyList<DemandSeriesErrorPeriodSnapshot> ErrorPeriods,
    DateTimeOffset? ArchivedAt = null);

public sealed record PollTraceSnapshot(
    string PollTraceId,
    string QueryVersion,
    string Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int RowCount,
    string ContentDigest,
    ProjectionCommitSnapshot? ProjectionCommit,
    IReadOnlyList<DemandRawObservationSnapshot> Observations);
