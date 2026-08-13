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
