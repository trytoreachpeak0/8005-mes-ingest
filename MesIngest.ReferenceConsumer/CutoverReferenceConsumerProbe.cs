using MesIngest.Core.SeriesProjection;

namespace MesIngest.ReferenceConsumer;

/// <summary>
/// Runs the production reference consumer against the cutover Host and returns only
/// non-business evidence suitable for the external cutover record.
/// </summary>
public static class CutoverReferenceConsumerProbe
{
    public static async Task<CutoverReferenceConsumerProbeResult> VerifyAsync(
        HttpClient httpClient,
        HistoryEpoch expectedHistoryEpoch,
        IReadOnlySet<string> forbiddenTombstoneKeyTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(expectedHistoryEpoch);
        ArgumentNullException.ThrowIfNull(forbiddenTombstoneKeyTokens);

        var consumer = new DemandCatalogReferenceConsumer(
            new HttpExternallyReadableDemandCatalogClient(httpClient),
            RejectingProbeStore.Instance);
        var snapshot = await consumer.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.HistoryEpoch != expectedHistoryEpoch)
        {
            throw new InvalidDataException(
                "CUTOVER_REFERENCE_CONSUMER_HISTORY_EPOCH_MISMATCH: "
                + "the reference consumer read another HistoryEpoch.");
        }

        var exposedTombstone = snapshot.Items.FirstOrDefault(item =>
            forbiddenTombstoneKeyTokens.Contains(
                TransportDemandKeyIdentity.CreateToken(item.WorkType, item.Sublot)));
        if (exposedTombstone is not null)
        {
            throw new InvalidDataException(
                "CUTOVER_TOMBSTONE_KEY_EXTERNALLY_READABLE: "
                + "the reference consumer catalog contains an archived key.");
        }

        return new CutoverReferenceConsumerProbeResult(
            snapshot.HistoryEpoch.Value,
            snapshot.ProjectionCommitId,
            snapshot.ProjectionSequence,
            snapshot.Items.Count,
            TombstoneKeysExcluded: true);
    }

    private sealed class RejectingProbeStore : IReferenceConsumerStore
    {
        public static RejectingProbeStore Instance { get; } = new();

        public Task<StoredDemandAcceptance> GetOrCreateAcceptanceAsync(
            AcceptedDemandSnapshot acceptedDemand,
            OrderIntent orderIntent,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The cutover probe never accepts a demand.");

        public Task<OrderIntent?> GetOrderIntentAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The cutover probe never reads an order intent.");

        public Task<OrderIntent> MarkOrderIntentResultUnknownAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The cutover probe never mutates an order intent.");

        public Task<OrderIntent> ConfirmOrderIntentAsync(
            string idempotencyKey,
            string remoteOrderId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The cutover probe never confirms an order intent.");
    }
}

public sealed record CutoverReferenceConsumerProbeResult(
    Guid HistoryEpoch,
    string? ProjectionCommitId,
    long? ProjectionSequence,
    int ItemCount,
    bool TombstoneKeysExcluded);
