namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Deep seam for the new projection: every round result leaves immutable evidence,
/// only SUCCESS mutates business projections, and reads expose committed state.
/// </summary>
public interface IMesIngestProjection
{
    Task BeginHostSessionAsync(CancellationToken cancellationToken = default);

    Task<RoundCommitReceipt> CommitRoundAsync(
        MesTaskUnionRound round,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads only the maintained current Series, current Demand, and active
    /// conditions for one exact business key. Historical collections are empty.
    /// </summary>
    Task<DemandSeriesSnapshot?> GetDemandSeriesByKeyAsync(
        string workType,
        string sublot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads only the maintained current Series, current Demand, and active
    /// conditions for one SeriesId. Historical collections are empty.
    /// </summary>
    Task<DemandSeriesSnapshot?> GetDemandSeriesAsync(
        string seriesId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one server-filtered, exactly counted page from a frozen
    /// ProjectionCommit. When the query omits a snapshot reference, the
    /// implementation freezes the latest successful commit atomically.
    /// </summary>
    Task<DemandSeriesListSnapshot> ListDemandSeriesAsync(
        DemandSeriesBrowseQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconstructs every detail fact as it was at the referenced frozen
    /// ProjectionCommit. A later successful round must not affect this result.
    /// </summary>
    Task<DemandSeriesDetailSnapshot?> GetDemandSeriesAtSnapshotAsync(
        string seriesId,
        string snapshotReference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every generated Demand generation from one frozen ProjectionCommit.
    /// Filtering, exact facets, ordering, and bounded paging are performed by
    /// the Host before any rows are returned to Watch.
    /// </summary>
    Task<ReadabilityAuditListSnapshot> ListReadabilityAuditAsync(
        ReadabilityAuditQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explains one Demand's complete qualification decision and evidence at
    /// the same signed audit snapshot used by its list result.
    /// </summary>
    Task<ReadabilityAuditDetailSnapshot?> GetReadabilityAuditDetailAsync(
        string demandId,
        string snapshotReference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches permanent Series error periods through one Host-frozen UTC
    /// ErrorSearchAsOf and ProjectionCommit high-water. Exact totals, facets,
    /// activity state, fixed ordering, and subsequent pages share that fence.
    /// </summary>
    Task<ErrorSearchListSnapshot> ListErrorSearchAsync(
        ErrorSearchQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explains only the periods and evidence that belonged to one Series match
    /// in the referenced signed Error Search snapshot.
    /// </summary>
    Task<ErrorSearchDetailSnapshot?> GetErrorSearchDetailAsync(
        string seriesId,
        string snapshotReference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a bounded, whitelisted raw observation set only after the evidence
    /// is proven to belong to the referenced Error Search match.
    /// </summary>
    Task<ErrorSearchRawEvidenceSnapshot?> GetErrorSearchRawEvidenceAsync(
        string seriesId,
        string evidenceId,
        string snapshotReference,
        ErrorSearchRawEvidenceQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the bounded current union of every operational attention source
    /// through one composite projection/poll fence.
    /// </summary>
    Task<CurrentIngestAttentionSnapshot> ReadCurrentIngestAttentionAsync(
        CurrentIngestAttentionQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically returns all Watch overview summaries and recent transitions
    /// from one identifiable operational fence.
    /// </summary>
    Task<WatchOverviewSnapshot> ReadWatchOverviewAsync(
        WatchOverviewQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the complete, full-scope externally readable catalog at one
    /// committed CatalogRevision. A matching known revision transfers no body.
    /// </summary>
    Task<ExternallyReadableDemandCatalogRead> ReadExternallyReadableDemandCatalogAsync(
        ExternallyReadableDemandCatalogIdentity? knownIdentity = null,
        CancellationToken cancellationToken = default);

    Task<HistoricalObjectReadResult<PollTraceSnapshot>> GetPollTraceAsync(
        string pollTraceId,
        CancellationToken cancellationToken = default);

    Task<AbsenceAuthoritySnapshot> GetAbsenceAuthorityAsync(
        CancellationToken cancellationToken = default);

    Task<AbsenceAuthoritySnapshot?> GetAbsenceAuthorityAsync(
        string hostSessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskTypeProtectionSnapshot>> ListTaskTypeProtectionsAsync(
        CancellationToken cancellationToken = default);

    Task<TaskTypeProtectionSnapshot?> GetTaskTypeProtectionAsync(
        string workType,
        CancellationToken cancellationToken = default);
}
