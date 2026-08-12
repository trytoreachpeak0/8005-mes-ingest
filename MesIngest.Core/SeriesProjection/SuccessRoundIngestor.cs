namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Production domain entry point for one complete successful MES_TASK_UNION round.
/// SQL transaction details remain hidden behind <see cref="IMesIngestProjection"/>.
/// </summary>
public sealed class SuccessRoundIngestor
{
    private readonly IMesIngestProjection _projection;

    public SuccessRoundIngestor(IMesIngestProjection projection)
    {
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
    }

    public Task<SuccessRoundCommitReceipt> IngestAsync(
        MesTaskUnionRound round,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.Outcome is not MesTaskUnionRoundOutcome.Success)
        {
            throw new ArgumentException(
                "SuccessRoundIngestor accepts only complete SUCCESS rounds.",
                nameof(round));
        }

        return _projection.CommitSuccessRoundAsync(round, cancellationToken);
    }
}
