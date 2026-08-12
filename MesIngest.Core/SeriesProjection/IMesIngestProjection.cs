namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Deep seam for the new projection: every round result leaves immutable evidence,
/// only SUCCESS mutates business projections, and reads expose committed state.
/// </summary>
public interface IMesIngestProjection
{
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
}
