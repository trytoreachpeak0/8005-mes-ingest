namespace MesIngest.Core.SeriesProjection;

public sealed record SuccessRoundCommitReceipt(
    string PollTraceId,
    string ProjectionCommitId,
    IReadOnlyList<string> SeriesIds,
    IReadOnlyList<string> DemandIds);

public sealed record ProjectionCommitSnapshot(
    string ProjectionCommitId,
    string PollTraceId,
    DateTimeOffset CommittedAt);

public sealed record LiveMesFieldSetSnapshot(
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package);

public sealed record TransportDemandSnapshot(
    string DemandId,
    string SeriesId,
    int Generation,
    string? PredecessorDemandId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset DemandLastSeenAt,
    string CreatedPollTraceId,
    string CreatedProjectionCommitId,
    string LatestProjectionCommitId,
    LiveMesFieldSetSnapshot LiveMesFields);

public sealed record DemandRawObservationSnapshot(
    int Ordinal,
    string PollTraceId,
    string ProjectionCommitId,
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
    IReadOnlyList<DemandRawObservationSnapshot> RawObservations,
    IReadOnlyList<DemandSeriesEventSnapshot> Events);

public sealed record PollTraceSnapshot(
    string PollTraceId,
    string QueryVersion,
    string Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int RowCount,
    string ContentDigest,
    ProjectionCommitSnapshot ProjectionCommit,
    IReadOnlyList<DemandRawObservationSnapshot> Observations);
