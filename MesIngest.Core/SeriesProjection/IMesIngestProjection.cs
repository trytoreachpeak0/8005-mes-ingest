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

    Task<DemandSeriesSnapshot?> GetDemandSeriesByKeyAsync(
        string workType,
        string sublot,
        CancellationToken cancellationToken = default);

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
    /// Reads the complete, full-scope externally readable catalog at one
    /// committed CatalogRevision. A matching known revision transfers no body.
    /// </summary>
    Task<ExternallyReadableDemandCatalogRead> ReadExternallyReadableDemandCatalogAsync(
        long? knownRevision = null,
        CancellationToken cancellationToken = default);

    Task<PollTraceSnapshot?> GetPollTraceAsync(
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
