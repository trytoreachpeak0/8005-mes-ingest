namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Deep seam for the new projection: one successful round is committed atomically,
/// while all reads return only committed evidence.
/// </summary>
public interface IMesIngestProjection
{
    Task<SuccessRoundCommitReceipt> CommitSuccessRoundAsync(
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
