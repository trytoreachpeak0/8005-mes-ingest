namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Production domain entry point for one complete MES_TASK_UNION result. The
/// projection module owns outcome isolation, idempotency, and persistence.
/// </summary>
public sealed class RoundIngestor
{
    private readonly IMesIngestProjection _projection;

    public RoundIngestor(IMesIngestProjection projection)
    {
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
    }

    public Task<RoundCommitReceipt> IngestAsync(
        MesTaskUnionRound round,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(round);
        return _projection.CommitRoundAsync(round, cancellationToken);
    }
}
