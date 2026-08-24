namespace MesIngest.Core.SeriesProjection;

public interface IMesTaskUnionPollRunner
{
    Task<RoundCommitReceipt> RunOnceAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Production entry for one formal Oracle poll: read one complete causal round,
/// then hand exactly that round to the V2 projection transaction boundary.
/// </summary>
public sealed class MesTaskUnionPollRunner : IMesTaskUnionPollRunner
{
    private readonly IMesTaskUnionRoundSource _source;
    private readonly RoundIngestor _ingestor;

    public MesTaskUnionPollRunner(
        IMesTaskUnionRoundSource source,
        RoundIngestor ingestor)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _ingestor = ingestor ?? throw new ArgumentNullException(nameof(ingestor));
    }

    public async Task<RoundCommitReceipt> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var round = await _source.ReadRoundAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await _ingestor.IngestAsync(round, cancellationToken).ConfigureAwait(false);
    }
}
