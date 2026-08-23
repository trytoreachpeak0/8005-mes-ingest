using MesIngest.Core.SeriesProjection;
using System.Text.Json;

namespace MesIngest.Watch;

internal sealed record WatchV2TransportDemandKeyWire(string WorkType, string Sublot);

internal sealed record WatchV2LiveMesFieldSetWire(
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package)
{
    public LiveMesFieldSetSnapshot ToCore() => new(Area, Eqp, Step, MesSourceDate, Package);
}

internal sealed record WatchV2DemandSeriesSnapshotIdentityWire(
    string HistoryEpoch,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    string ContractVersion)
{
    public DemandSeriesSnapshotIdentity ToCore() => new(
        MesIngest.Core.SeriesProjection.HistoryEpoch.FromGuid(Guid.Parse(HistoryEpoch)),
        ProjectionCommitId,
        ProjectionSequence,
        ProjectionCommittedAt,
        PollTraceId,
        ContractVersion);
}

internal sealed record WatchV2DemandSeriesFacetsWire(
    long TrackingCount,
    long ArchivedCount,
    long VisibleCount,
    long GoneCount,
    long LongGoneButVisibleCount)
{
    public DemandSeriesFacets ToCore() => new(
        TrackingCount,
        ArchivedCount,
        VisibleCount,
        GoneCount,
        LongGoneButVisibleCount);
}

internal sealed record WatchV2DemandSeriesListItemWire(
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string CurrentPresence,
    DateTimeOffset StartedAt,
    DateTimeOffset? ArchivedAt,
    string CurrentDemandId,
    int CurrentGeneration,
    string CurrentDemandStatus,
    DateTimeOffset DemandLastSeenAt,
    DateTimeOffset? GoneConfirmedAt,
    WatchV2LiveMesFieldSetWire? LiveMesFields,
    string ExternalReadabilityState,
    IReadOnlyList<string> ReadabilityBlockers,
    long LastSeriesSequence,
    string LatestPollTraceId,
    string LatestProjectionCommitId)
{
    public DemandSeriesListItemSnapshot ToCore() => new(
        SeriesId,
        WorkType,
        Sublot,
        Lifecycle,
        CurrentPresence,
        StartedAt,
        ArchivedAt,
        CurrentDemandId,
        CurrentGeneration,
        CurrentDemandStatus,
        DemandLastSeenAt,
        GoneConfirmedAt,
        LiveMesFields?.ToCore(),
        ExternalReadabilityState,
        ReadabilityBlockers,
        LastSeriesSequence,
        LatestPollTraceId,
        LatestProjectionCommitId);
}

internal sealed record WatchV2DemandSeriesListWire(
    string SnapshotReference,
    WatchV2DemandSeriesSnapshotIdentityWire Snapshot,
    long ExactTotalCount,
    WatchV2DemandSeriesFacetsWire Facets,
    string Order,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<WatchV2DemandSeriesListItemWire> Items,
    string? NextCursor,
    bool HasMore)
{
    public DemandSeriesListSnapshot ToCore(DemandSeriesBrowseFilter filter) => new(
        Snapshot.ToCore(),
        SnapshotReference,
        filter,
        Order,
        ExactTotalCount,
        Facets.ToCore(),
        PageSize,
        PageNumber,
        TotalPages,
        Items.Select(item => item.ToCore()).ToArray(),
        NextCursor,
        HasMore);
}

internal sealed record WatchV2TransportDemandWire(
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
    WatchV2LiveMesFieldSetWire? LiveMesFields,
    string ExternalReadabilityState,
    IReadOnlyList<string> ReadabilityBlockers,
    string? LatestObservationPollTraceId,
    string? LatestObservationProjectionCommitId,
    DateTimeOffset? LatestObservationAt)
{
    public TransportDemandSnapshot ToCore() => new(
        DemandId,
        SeriesId,
        Generation,
        PredecessorDemandId,
        Status,
        CreatedAt,
        DemandLastSeenAt,
        GoneConfirmedAt,
        CreatedPollTraceId,
        CreatedProjectionCommitId,
        LatestProjectionCommitId,
        LiveMesFields?.ToCore(),
        ExternalReadabilityState,
        ReadabilityBlockers,
        LatestObservationPollTraceId,
        LatestObservationProjectionCommitId,
        LatestObservationAt);
}

internal sealed record WatchV2DemandRawObservationWire(
    int Ordinal,
    string PollTraceId,
    string ProjectionCommitId,
    DateTimeOffset ObservedAt,
    string Assignment,
    string? SeriesId,
    string? DemandId,
    string? WorkType,
    string? Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package,
    string? MesSourceDateRaw)
{
    public DemandRawObservationSnapshot ToCore() => new(
        Ordinal,
        PollTraceId,
        ProjectionCommitId,
        Assignment switch
        {
            "ASSIGNED" => MesObservationAssignment.Assigned,
            "UNASSIGNED" => MesObservationAssignment.Unassigned,
            _ => throw new JsonException($"Unknown MES observation assignment '{Assignment}'."),
        },
        SeriesId,
        DemandId,
        WorkType,
        Sublot,
        Area,
        Eqp,
        Step,
        MesSourceDate,
        Package,
        ObservedAt,
        MesSourceDateRaw);
}

internal sealed record WatchV2DemandSeriesEventWire(
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
    string PayloadJson)
{
    public DemandSeriesEventSnapshot ToCore() => new(
        EventId,
        SeriesId,
        SeriesSequence,
        EventType,
        OccurredAt,
        SubjectKind,
        SubjectId,
        PollTraceId,
        ProjectionCommitId,
        PayloadVersion,
        PayloadJson);
}

internal sealed record WatchV2SeriesErrorPeriodEvidenceWire(
    string EvidenceId,
    string EvidenceKind,
    DateTimeOffset ObservedAt,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    string? ObservedValue,
    string ExpectedRule)
{
    public SeriesErrorPeriodEvidenceSnapshot ToCore() => new(
        EvidenceId,
        EvidenceKind,
        ObservedAt,
        PollTraceId,
        ProjectionCommitId,
        DemandId,
        ObservedValue,
        ExpectedRule);
}

internal sealed record WatchV2DemandSeriesCurrentConditionWire(
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
    string ExpectedRule)
{
    public DemandSeriesCurrentConditionSnapshot ToCore() => new(
        PeriodId,
        Code,
        Category,
        Severity,
        Target,
        SubjectKind,
        StartedAt,
        LatestEvidenceAt,
        LatestPollTraceId,
        LatestProjectionCommitId,
        DemandId,
        ObservedValue,
        ExpectedRule);
}

internal sealed record WatchV2DemandSeriesErrorPeriodWire(
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
    IReadOnlyList<WatchV2SeriesErrorPeriodEvidenceWire> Evidence)
{
    public DemandSeriesErrorPeriodSnapshot ToCore() => new(
        PeriodId,
        Code,
        Category,
        Severity,
        Target,
        SubjectKind,
        StartReason,
        StartedAt,
        EndedAt,
        EndReason,
        Evidence.Select(item => item.ToCore()).ToArray());
}

internal sealed record WatchV2FrozenDemandSeriesWire(
    string SnapshotReference,
    WatchV2DemandSeriesSnapshotIdentityWire Snapshot,
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string CurrentPresence,
    DateTimeOffset StartedAt,
    DateTimeOffset? ArchivedAt,
    string CreatedPollTraceId,
    string CreatedProjectionCommitId,
    string LatestProjectionCommitId,
    long LastSeriesSequence,
    WatchV2TransportDemandWire CurrentDemand,
    IReadOnlyList<WatchV2TransportDemandWire> Demands,
    IReadOnlyList<WatchV2DemandRawObservationWire> RawObservations,
    IReadOnlyList<WatchV2DemandSeriesEventWire> Events,
    IReadOnlyList<WatchV2DemandSeriesCurrentConditionWire> CurrentConditions,
    IReadOnlyList<WatchV2DemandSeriesErrorPeriodWire> ErrorPeriods)
{
    public DemandSeriesDetailSnapshot ToCore() => new(
        Snapshot.ToCore(),
        SnapshotReference,
        new DemandSeriesSnapshot(
            SeriesId,
            WorkType,
            Sublot,
            Lifecycle,
            CurrentPresence,
            StartedAt,
            CreatedPollTraceId,
            CreatedProjectionCommitId,
            LatestProjectionCommitId,
            CurrentDemand.ToCore(),
            Demands.Select(item => item.ToCore()).ToArray(),
            RawObservations.Select(item => item.ToCore()).ToArray(),
            Events.Select(item => item.ToCore()).ToArray(),
            CurrentConditions.Select(item => item.ToCore()).ToArray(),
            ErrorPeriods.Select(item => item.ToCore()).ToArray(),
            ArchivedAt,
            LastSeriesSequence));
}

internal sealed record WatchV2ReadabilityAuditSnapshotIdentityWire(
    string HistoryEpoch,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    long CatalogRevision,
    string ContractVersion)
{
    public ReadabilityAuditSnapshotIdentity ToCore() => new(
        MesIngest.Core.SeriesProjection.HistoryEpoch.FromGuid(Guid.Parse(HistoryEpoch)),
        ProjectionCommitId,
        ProjectionSequence,
        ProjectionCommittedAt,
        PollTraceId,
        CatalogRevision,
        ContractVersion);
}

internal sealed record WatchV2ReadabilityAuditFilterWire(
    IReadOnlyList<string> ReadabilityStates,
    IReadOnlyList<string> WorkTypes,
    IReadOnlyList<string> Blockers,
    string? DemandId,
    string? SublotContains,
    IReadOnlyList<string> MesAreas)
{
    public ReadabilityAuditFilter ToCore() => new()
    {
        ReadabilityStates = ReadabilityStates,
        WorkTypes = WorkTypes,
        Blockers = Blockers,
        DemandId = DemandId,
        SublotContains = SublotContains,
        MesAreas = MesAreas,
    };
}

internal sealed record WatchV2ReadabilityStateFacetWire(string State, long DemandCount)
{
    public ReadabilityStateFacetSnapshot ToCore() => new(State, DemandCount);
}

internal sealed record WatchV2ReadabilityBlockerFacetWire(string Code, long DemandCount)
{
    public ReadabilityBlockerFacetSnapshot ToCore() => new(Code, DemandCount);
}

internal sealed record WatchV2ReadabilityAuditFacetsWire(
    IReadOnlyList<WatchV2ReadabilityStateFacetWire> ReadabilityStates,
    IReadOnlyList<WatchV2ReadabilityBlockerFacetWire> Blockers)
{
    public ReadabilityAuditFacets ToCore() => new(
        ReadabilityStates.Select(item => item.ToCore()).ToArray(),
        Blockers.Select(item => item.ToCore()).ToArray());
}

internal sealed record WatchV2ReadabilityAuditListItemWire(
    string DemandId,
    string SeriesId,
    WatchV2TransportDemandKeyWire TransportDemandKey,
    int Generation,
    string? PredecessorDemandId,
    string DemandStatus,
    string SeriesLifecycle,
    string SeriesCurrentPresence,
    bool IsCurrentGeneration,
    DateTimeOffset DemandCreatedAt,
    DateTimeOffset DemandLastSeenAt,
    DateTimeOffset? GoneConfirmedAt,
    WatchV2LiveMesFieldSetWire? LiveMesFields,
    int CurrentRawObservationCount,
    string ExternalReadabilityState,
    string? LeadReadabilityBlocker,
    IReadOnlyList<string> ReadabilityBlockers,
    string LatestObservationPollTraceId,
    string LatestObservationProjectionCommitId,
    DateTimeOffset LatestObservationAt)
{
    public ReadabilityAuditListItemSnapshot ToCore() => new(
        DemandId,
        SeriesId,
        TransportDemandKey.WorkType,
        TransportDemandKey.Sublot,
        Generation,
        PredecessorDemandId,
        DemandStatus,
        SeriesLifecycle,
        SeriesCurrentPresence,
        IsCurrentGeneration,
        DemandCreatedAt,
        DemandLastSeenAt,
        GoneConfirmedAt,
        LiveMesFields?.ToCore(),
        CurrentRawObservationCount,
        ExternalReadabilityState,
        LeadReadabilityBlocker,
        ReadabilityBlockers,
        LatestObservationPollTraceId,
        LatestObservationProjectionCommitId,
        LatestObservationAt);
}

internal sealed record WatchV2ReadabilityAuditListWire(
    string SnapshotReference,
    WatchV2ReadabilityAuditSnapshotIdentityWire Snapshot,
    WatchV2ReadabilityAuditFilterWire Filter,
    long ExactTotalDemandCount,
    WatchV2ReadabilityAuditFacetsWire Facets,
    string Order,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<WatchV2ReadabilityAuditListItemWire> Items,
    string? NextCursor,
    bool HasMore)
{
    public ReadabilityAuditListSnapshot ToCore() => new(
        Snapshot.ToCore(),
        SnapshotReference,
        Filter.ToCore(),
        Order,
        ExactTotalDemandCount,
        Facets.ToCore(),
        PageSize,
        PageNumber,
        TotalPages,
        Items.Select(item => item.ToCore()).ToArray(),
        NextCursor,
        HasMore);
}

internal sealed record WatchV2ReadabilityAuditSeriesWire(
    string SeriesId,
    WatchV2TransportDemandKeyWire TransportDemandKey,
    string Lifecycle,
    string CurrentPresence,
    DateTimeOffset StartedAt,
    DateTimeOffset? ArchivedAt,
    string CurrentDemandId)
{
    public ReadabilityAuditSeriesSnapshot ToCore() => new(
        SeriesId,
        TransportDemandKey.WorkType,
        TransportDemandKey.Sublot,
        Lifecycle,
        CurrentPresence,
        StartedAt,
        ArchivedAt,
        CurrentDemandId);
}

internal sealed record WatchV2ReadabilityQualificationCheckWire(
    string Code,
    string BlockingCode,
    string Result)
{
    public ReadabilityQualificationCheckSnapshot ToCore() => new(Code, BlockingCode, Result);
}

internal sealed record WatchV2ReadabilityEvidenceItemWire(
    string SubjectKind,
    string? ObservedValue,
    string ExpectedRule,
    DateTimeOffset ObservedAt,
    string PollTraceId,
    string ProjectionCommitId)
{
    public ReadabilityEvidenceItemSnapshot ToCore() => new(
        SubjectKind,
        ObservedValue,
        ExpectedRule,
        ObservedAt,
        PollTraceId,
        ProjectionCommitId);
}

internal sealed record WatchV2ReadabilityBlockerEvidenceWire(
    string Code,
    int Priority,
    IReadOnlyList<WatchV2ReadabilityEvidenceItemWire> Evidence)
{
    public ReadabilityBlockerEvidenceSnapshot ToCore() => new(
        Code,
        Priority,
        Evidence.Select(item => item.ToCore()).ToArray());
}

internal sealed record WatchV2ReadabilityAuditPollTraceWire(
    string PollTraceId,
    string QueryVersion,
    string Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int RowCount,
    string ContentDigest,
    string ProjectionCommitId,
    long ProjectionSequence)
{
    public ReadabilityAuditPollTraceSnapshot ToCore() => new(
        PollTraceId,
        QueryVersion,
        Outcome,
        StartedAt,
        CompletedAt,
        RowCount,
        ContentDigest,
        ProjectionCommitId,
        ProjectionSequence);
}

internal sealed record WatchV2ReadabilityAuditDetailWire(
    string SnapshotReference,
    WatchV2ReadabilityAuditSnapshotIdentityWire Snapshot,
    WatchV2ReadabilityAuditListItemWire Demand,
    WatchV2ReadabilityAuditSeriesWire Series,
    IReadOnlyList<WatchV2ReadabilityQualificationCheckWire> QualificationChecks,
    IReadOnlyList<WatchV2ReadabilityBlockerEvidenceWire> Blockers,
    IReadOnlyList<WatchV2DemandRawObservationWire> LatestRawObservations,
    WatchV2ReadabilityAuditPollTraceWire LatestObservationPollTrace)
{
    public ReadabilityAuditDetailSnapshot ToCore() => new(
        Snapshot.ToCore(),
        SnapshotReference,
        Demand.ToCore(),
        Series.ToCore(),
        QualificationChecks.Select(item => item.ToCore()).ToArray(),
        Blockers.Select(item => item.ToCore()).ToArray(),
        LatestRawObservations.Select(item => item.ToCore()).ToArray(),
        LatestObservationPollTrace.ToCore());
}

internal sealed record WatchV2ErrorSearchSnapshotIdentityWire(
    string HistoryEpoch,
    DateTimeOffset ErrorSearchAsOf,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    string ContractVersion)
{
    public ErrorSearchSnapshotIdentity ToCore() => new(
        MesIngest.Core.SeriesProjection.HistoryEpoch.FromGuid(Guid.Parse(HistoryEpoch)),
        ErrorSearchAsOf,
        ProjectionCommitId,
        ProjectionSequence,
        ProjectionCommittedAt,
        PollTraceId,
        ContractVersion);
}

internal sealed record WatchV2ErrorSearchFilterWire(
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> ErrorCodes,
    IReadOnlyList<string> ActivityStates,
    string? SeriesId,
    string? DemandId,
    string? SublotContains)
{
    public ErrorSearchFilter ToCore() => new()
    {
        Categories = Categories,
        ErrorCodes = ErrorCodes,
        ActivityStates = ActivityStates,
        SeriesId = SeriesId,
        DemandId = DemandId,
        SublotContains = SublotContains,
    };
}

internal sealed record WatchV2ErrorSearchWindowWire(
    string Kind,
    DateTimeOffset? FromUtc,
    DateTimeOffset ToUtc)
{
    public ErrorSearchResolvedWindow ToCore() => new(Kind, FromUtc, ToUtc);
}

internal sealed record WatchV2ErrorSearchCategoryFacetWire(string Category, long SeriesCount)
{
    public ErrorSearchCategoryFacetSnapshot ToCore() => new(Category, SeriesCount);
}

internal sealed record WatchV2ErrorSearchActivityStateFacetWire(string State, long SeriesCount)
{
    public ErrorSearchActivityStateFacetSnapshot ToCore() => new(State, SeriesCount);
}

internal sealed record WatchV2ErrorSearchFacetsWire(
    IReadOnlyList<WatchV2ErrorSearchCategoryFacetWire> Categories,
    IReadOnlyList<WatchV2ErrorSearchActivityStateFacetWire> ActivityStates)
{
    public ErrorSearchFacets ToCore() => new(
        Categories.Select(item => item.ToCore()).ToArray(),
        ActivityStates.Select(item => item.ToCore()).ToArray());
}

internal sealed record WatchV2ErrorSearchMatchedErrorWire(
    string Code,
    string Category,
    string Severity)
{
    public ErrorSearchMatchedErrorSnapshot ToCore() => new(Code, Category, Severity);
}

internal sealed record WatchV2ErrorSearchListItemWire(
    string SeriesId,
    WatchV2TransportDemandKeyWire TransportDemandKey,
    string ActivityState,
    IReadOnlyList<WatchV2ErrorSearchMatchedErrorWire> MatchedErrors,
    DateTimeOffset LatestMatchedEvidenceAt,
    int MatchedPeriodCount,
    int MatchedDemandGenerationCount,
    string? MesArea,
    string MesAreaAvailability)
{
    public ErrorSearchListItemSnapshot ToCore() => new(
        SeriesId,
        TransportDemandKey.WorkType,
        TransportDemandKey.Sublot,
        ActivityState,
        MatchedErrors.Select(item => item.ToCore()).ToArray(),
        LatestMatchedEvidenceAt,
        MatchedPeriodCount,
        MatchedDemandGenerationCount,
        MesArea,
        MesAreaAvailability);
}

internal sealed record WatchV2ErrorSearchListWire(
    string SnapshotReference,
    WatchV2ErrorSearchSnapshotIdentityWire Snapshot,
    WatchV2ErrorSearchFilterWire Filter,
    WatchV2ErrorSearchWindowWire Window,
    string Order,
    long TotalSeriesCount,
    WatchV2ErrorSearchFacetsWire Facets,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<WatchV2ErrorSearchListItemWire> Items,
    string? NextCursor,
    bool HasMore)
{
    public ErrorSearchListSnapshot ToCore() => new(
        SnapshotReference,
        Snapshot.ToCore(),
        Filter.ToCore(),
        Window.ToCore(),
        Order,
        TotalSeriesCount,
        Facets.ToCore(),
        PageSize,
        PageNumber,
        TotalPages,
        Items.Select(item => item.ToCore()).ToArray(),
        NextCursor,
        HasMore);
}

internal sealed record WatchV2ErrorSearchDiagnosticValueWire(
    string Kind,
    string? ScalarValue,
    int? ObservationCount,
    string? Sha256Digest)
{
    public ErrorSearchDiagnosticValueSnapshot ToCore() => new(
        Kind,
        ScalarValue,
        ObservationCount,
        Sha256Digest);
}

internal sealed record WatchV2ErrorSearchDetailEvidenceWire(
    string EvidenceId,
    string EvidenceKind,
    string SubjectKind,
    DateTimeOffset ObservedAt,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    IReadOnlyList<string> RelatedWorkTypes,
    WatchV2ErrorSearchDiagnosticValueWire DiagnosticValue,
    string ExpectedRule,
    bool RawEvidenceAvailable)
{
    public ErrorSearchDetailEvidenceSnapshot ToCore() => new(
        EvidenceId,
        EvidenceKind,
        SubjectKind,
        ObservedAt,
        PollTraceId,
        ProjectionCommitId,
        DemandId,
        RelatedWorkTypes,
        DiagnosticValue.ToCore(),
        ExpectedRule,
        RawEvidenceAvailable);
}

internal sealed record WatchV2ErrorSearchDetailPeriodWire(
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
    bool StartsBeforeWindow,
    bool EndsAfterWindow,
    bool ActiveAtAsOf,
    IReadOnlyList<WatchV2ErrorSearchDetailEvidenceWire> Evidence)
{
    public ErrorSearchDetailPeriodSnapshot ToCore() => new(
        PeriodId,
        Code,
        Category,
        Severity,
        Target,
        SubjectKind,
        StartReason,
        StartedAt,
        EndedAt,
        EndReason,
        StartsBeforeWindow,
        EndsAfterWindow,
        ActiveAtAsOf,
        Evidence.Select(item => item.ToCore()).ToArray());
}

internal sealed record WatchV2ErrorSearchDetailWire(
    string SnapshotReference,
    WatchV2ErrorSearchSnapshotIdentityWire Snapshot,
    WatchV2ErrorSearchFilterWire Filter,
    WatchV2ErrorSearchWindowWire Window,
    string Order,
    WatchV2ErrorSearchListItemWire Series,
    IReadOnlyList<WatchV2ErrorSearchDetailPeriodWire> Periods)
{
    public ErrorSearchDetailSnapshot ToCore() => new(
        SnapshotReference,
        Snapshot.ToCore(),
        Filter.ToCore(),
        Window.ToCore(),
        Order,
        Series.ToCore(),
        Periods.Select(item => item.ToCore()).ToArray());
}

internal sealed record WatchV2ErrorSearchRawEvidenceLimitsWire(
    int MaxItems,
    int MaxItemBytes,
    int MaxTotalBytes)
{
    public ErrorSearchRawEvidenceLimitsSnapshot ToCore() => new(
        MaxItems,
        MaxItemBytes,
        MaxTotalBytes);
}

internal sealed record WatchV2ErrorSearchRawEvidenceItemWire(
    int Ordinal,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    DateTimeOffset ObservedAt,
    IReadOnlyDictionary<string, string?> Fields)
{
    public ErrorSearchRawEvidenceItemSnapshot ToCore() => new(
        Ordinal,
        PollTraceId,
        ProjectionCommitId,
        DemandId,
        ObservedAt,
        Fields);
}

internal sealed record WatchV2ErrorSearchRawEvidenceWire(
    string SnapshotReference,
    WatchV2ErrorSearchSnapshotIdentityWire Snapshot,
    string SeriesId,
    string PeriodId,
    string EvidenceId,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    IReadOnlyList<string> IncludedFields,
    int ItemCount,
    WatchV2ErrorSearchRawEvidenceLimitsWire Limits,
    int PayloadBytes,
    IReadOnlyList<WatchV2ErrorSearchRawEvidenceItemWire> Items)
{
    public ErrorSearchRawEvidenceSnapshot ToCore() => new(
        SnapshotReference,
        Snapshot.ToCore(),
        SeriesId,
        PeriodId,
        EvidenceId,
        PollTraceId,
        ProjectionCommitId,
        DemandId,
        IncludedFields,
        ItemCount,
        Limits.ToCore(),
        PayloadBytes,
        Items.Select(item => item.ToCore()).ToArray());
}

internal sealed record WatchV2OperationalSnapshotIdentityWire(
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    long PollTraceHighWater,
    long CatalogRevision,
    DateTimeOffset SnapshotAsOf,
    string ContractVersion,
    string? HistoryEpoch = null)
{
    public OperationalSnapshotIdentity ToCore() => new(
        ProjectionCommitId,
        ProjectionSequence,
        ProjectionCommittedAt,
        PollTraceId,
        PollTraceHighWater,
        CatalogRevision,
        SnapshotAsOf,
        ContractVersion,
        string.IsNullOrWhiteSpace(HistoryEpoch)
            ? null
            : MesIngest.Core.SeriesProjection.HistoryEpoch.FromGuid(Guid.Parse(HistoryEpoch)));
}

internal sealed record WatchV2OverviewNavigationIntentWire(
    string Target,
    int PageNumber,
    IReadOnlyList<string>? MesAreas,
    IReadOnlyList<string>? Lifecycles,
    IReadOnlyList<string>? CurrentPresences,
    IReadOnlyList<string>? ReadabilityStates,
    IReadOnlyList<string>? ErrorActivityStates,
    string? ErrorWindow,
    IReadOnlyList<string>? AttentionKinds,
    IReadOnlyList<string>? AttentionSeverities,
    string? SeriesId,
    string? WorkType,
    string? PollTraceId,
    string? Cursor)
{
    public OverviewNavigationIntent ToCore() => new(
        Target,
        PageNumber,
        MesAreas,
        Lifecycles,
        CurrentPresences,
        ReadabilityStates,
        ErrorActivityStates,
        ErrorWindow,
        AttentionKinds,
        AttentionSeverities,
        SeriesId,
        WorkType,
        PollTraceId,
        Cursor);
}

internal sealed record WatchV2CurrentAttentionFacetWire(string Value, long ItemCount)
{
    public CurrentIngestAttentionFacetSnapshot ToCore() => new(Value, ItemCount);
}

internal sealed record WatchV2CurrentAttentionFacetsWire(
    IReadOnlyList<WatchV2CurrentAttentionFacetWire> Types,
    IReadOnlyList<WatchV2CurrentAttentionFacetWire> Severities)
{
    public CurrentIngestAttentionFacets ToCore() => new(
        Types.Select(item => item.ToCore()).ToArray(),
        Severities.Select(item => item.ToCore()).ToArray());
}

internal sealed record WatchV2CurrentAttentionEvidenceWire(
    string? ProjectionCommitId,
    long? ProjectionSequence,
    string? PollTraceId,
    long? PollTraceSequence,
    string? SeriesId,
    string? DemandId,
    string? WorkType,
    int? ObservationOrdinal,
    string? EvidenceId,
    string? ContentDigest,
    string? Phase,
    string? Outcome,
    int? ObservationCount)
{
    public CurrentIngestAttentionEvidenceSnapshot ToCore() => new(
        ProjectionCommitId,
        ProjectionSequence,
        PollTraceId,
        PollTraceSequence,
        SeriesId,
        DemandId,
        WorkType,
        ObservationOrdinal,
        EvidenceId,
        ContentDigest,
        Phase,
        Outcome,
        ObservationCount);
}

internal sealed record WatchV2CurrentAttentionItemWire(
    string Kind,
    string Severity,
    DateTimeOffset OccurredAt,
    string StableIdentity,
    string? SeriesId,
    string? WorkType,
    string? ErrorCode,
    string? Target,
    string? SubjectKind,
    WatchV2CurrentAttentionEvidenceWire Evidence,
    WatchV2OverviewNavigationIntentWire Navigation)
{
    public CurrentIngestAttentionItemSnapshot ToCore() => new(
        Kind,
        Severity,
        OccurredAt,
        StableIdentity,
        SeriesId,
        WorkType,
        ErrorCode,
        Target,
        SubjectKind,
        Evidence.ToCore(),
        Navigation.ToCore());
}

internal sealed record WatchV2CurrentAttentionWire(
    WatchV2OperationalSnapshotIdentityWire Snapshot,
    long ExactTotalItemCount,
    WatchV2CurrentAttentionFacetsWire Facets,
    string Order,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Severities,
    IReadOnlyList<WatchV2CurrentAttentionItemWire> Items)
{
    public CurrentIngestAttentionSnapshot ToCore() => new(
        Snapshot.ToCore(),
        ExactTotalItemCount,
        Facets.ToCore(),
        Order,
        PageSize,
        PageNumber,
        TotalPages,
        Kinds,
        Severities,
        Items.Select(item => item.ToCore()).ToArray());
}
