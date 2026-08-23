using MesIngest.Core.SeriesProjection;

namespace MesIngest.ReferenceConsumer;

public interface IExternallyReadableDemandCatalogClient
{
    Task<ExternallyReadableDemandCatalogRead> ReadAsync(
        ExternallyReadableDemandCatalogIdentity? knownIdentity,
        CancellationToken cancellationToken = default);
}

public interface IReferenceConsumerStore
{
    /// <summary>
    /// Atomically persists the immutable decision evidence and its reliable
    /// remote-order intent. A repeated idempotency key with the same acceptance
    /// returns the existing pair; the store must reject different content.
    /// </summary>
    Task<StoredDemandAcceptance> GetOrCreateAcceptanceAsync(
        AcceptedDemandSnapshot acceptedDemand,
        OrderIntent orderIntent,
        CancellationToken cancellationToken = default);

    Task<OrderIntent?> GetOrderIntentAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<OrderIntent> MarkOrderIntentResultUnknownAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<OrderIntent> ConfirmOrderIntentAsync(
        string idempotencyKey,
        string remoteOrderId,
        CancellationToken cancellationToken = default);
}

public sealed record AcceptedDemandSnapshot(
    long CatalogRevision,
    DateTimeOffset AcceptedAt,
    ExternallyReadableDemandSnapshot Demand);

public enum OrderIntentState
{
    Pending,
    ResultUnknown,
    Confirmed,
    Rejected,
}

public sealed record OrderIntent(
    string IdempotencyKey,
    string DemandId,
    long AcceptedDemandRevision,
    DateTimeOffset CreatedAt,
    OrderIntentState State,
    string? RemoteOrderId = null);

public sealed record StoredDemandAcceptance(
    AcceptedDemandSnapshot AcceptedDemand,
    OrderIntent OrderIntent);

public enum DemandAcceptanceOutcome
{
    Accepted,
    CandidateNoLongerReadable,
    CandidateChanged,
}

public sealed record DemandAcceptanceResult(
    DemandAcceptanceOutcome Outcome,
    ExternallyReadableDemandSnapshot? CurrentDemand,
    AcceptedDemandSnapshot? AcceptedDemand,
    OrderIntent? OrderIntent);

public sealed record RemoteOrderReference(
    string RemoteOrderId,
    string IdempotencyKey);

public interface IReferenceOrderGateway
{
    Task<RemoteOrderReference?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or returns the order bound to <see cref="OrderIntent.IdempotencyKey"/>.
    /// Repeating the same key and content must not create another remote order;
    /// reusing a key with different content must be rejected by the adapter.
    /// </summary>
    Task<RemoteOrderReference> CreateAsync(
        OrderIntent intent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Signals that a remote create may already have committed, so the caller must
/// reconcile with the same idempotency key rather than infer failure or retry
/// under a new identity.
/// </summary>
public sealed class OrderResultUnknownException : Exception
{
    public OrderResultUnknownException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public enum OrderDispatchOutcome
{
    Confirmed,
    ResultUnknown,
    Rejected,
}

public sealed record OrderDispatchResult(
    OrderDispatchOutcome Outcome,
    OrderIntent OrderIntent);

public sealed class ReferenceOrderIntentDispatcher
{
    private readonly IReferenceConsumerStore _store;
    private readonly IReferenceOrderGateway _gateway;

    public ReferenceOrderIntentDispatcher(
        IReferenceConsumerStore store,
        IReferenceOrderGateway gateway)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public async Task<OrderDispatchResult> DispatchAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var intent = await _store.GetOrderIntentAsync(
            idempotencyKey,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"OrderIntent '{idempotencyKey}' does not exist.");

        if (intent.State == OrderIntentState.Confirmed)
        {
            return new OrderDispatchResult(OrderDispatchOutcome.Confirmed, intent);
        }

        if (intent.State == OrderIntentState.Rejected)
        {
            return new OrderDispatchResult(OrderDispatchOutcome.Rejected, intent);
        }

        var existing = await _gateway.FindByIdempotencyKeyAsync(
            intent.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return await ConfirmAsync(intent, existing, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // A timeout may have happened before the remote service observed the
            // request. Retrying is safe only because CreateAsync is contractually
            // bound to this unchanged, durable idempotency key.
            var created = await _gateway.CreateAsync(intent, cancellationToken).ConfigureAwait(false);
            return await ConfirmAsync(intent, created, cancellationToken).ConfigureAwait(false);
        }
        catch (OrderResultUnknownException)
        {
            var unknown = await _store.MarkOrderIntentResultUnknownAsync(
                intent.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            return new OrderDispatchResult(OrderDispatchOutcome.ResultUnknown, unknown);
        }
    }

    private async Task<OrderDispatchResult> ConfirmAsync(
        OrderIntent intent,
        RemoteOrderReference remote,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                intent.IdempotencyKey,
                remote.IdempotencyKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The remote order identity does not match the stored OrderIntent.");
        }

        var confirmed = await _store.ConfirmOrderIntentAsync(
            intent.IdempotencyKey,
            remote.RemoteOrderId,
            cancellationToken).ConfigureAwait(false);
        return new OrderDispatchResult(OrderDispatchOutcome.Confirmed, confirmed);
    }
}

/// <summary>
/// Minimal external consumer proving that the MesIngest catalog is a complete,
/// disposable view rather than a durable synchronization ledger.
/// </summary>
public sealed class DemandCatalogReferenceConsumer
{
    private readonly IExternallyReadableDemandCatalogClient _catalogClient;
    private readonly IReferenceConsumerStore _store;
    private readonly TimeProvider _timeProvider;
    private ExternallyReadableDemandCatalogSnapshot? _cache;

    public DemandCatalogReferenceConsumer(
        IExternallyReadableDemandCatalogClient catalogClient,
        IReferenceConsumerStore store,
        TimeProvider? timeProvider = null)
    {
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExternallyReadableDemandCatalogSnapshot> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        // A reference consumer may call Refresh and Accept concurrently. Publish
        // only whole immutable snapshots; the local final-snapshot variable in
        // AcceptAsync remains the commit-point decision evidence.
        var read = await _catalogClient.ReadAsync(
            Volatile.Read(ref _cache) is { } cached
                ? new ExternallyReadableDemandCatalogIdentity(
                    cached.HistoryEpoch,
                    cached.CatalogRevision)
                : null,
            cancellationToken).ConfigureAwait(false);
        if (read.NotModified)
        {
            return Volatile.Read(ref _cache) ?? throw new InvalidOperationException(
                "The catalog client returned Not Modified before this consumer had a complete catalog.");
        }

        var complete = read.Snapshot ?? throw new InvalidOperationException(
            "A modified catalog read must carry the complete catalog snapshot.");
        Volatile.Write(ref _cache, complete);
        return complete;
    }

    public async Task<DemandAcceptanceResult> AcceptAsync(
        ExternallyReadableDemandSnapshot candidate,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // The execution commitment is intentionally based on an unconditional
        // final complete read. A 304 against the discovery cache would not prove
        // which exact facts were accepted at this instant.
        var finalRead = await _catalogClient.ReadAsync(
            knownIdentity: null,
            cancellationToken).ConfigureAwait(false);
        if (finalRead.NotModified || finalRead.Snapshot is null)
        {
            throw new InvalidOperationException(
                "An unconditional commit-point catalog read must return the complete catalog.");
        }

        var finalSnapshot = finalRead.Snapshot;
        Volatile.Write(ref _cache, finalSnapshot);
        var current = finalSnapshot.Items.SingleOrDefault(item =>
            string.Equals(item.DemandId, candidate.DemandId, StringComparison.Ordinal));
        if (current is null)
        {
            return new DemandAcceptanceResult(
                DemandAcceptanceOutcome.CandidateNoLongerReadable,
                CurrentDemand: null,
                AcceptedDemand: null,
                OrderIntent: null);
        }

        if (!HasSameDecisionFacts(candidate, current))
        {
            return new DemandAcceptanceResult(
                DemandAcceptanceOutcome.CandidateChanged,
                current,
                AcceptedDemand: null,
                OrderIntent: null);
        }

        var acceptedAt = _timeProvider.GetUtcNow();
        var acceptedDemand = new AcceptedDemandSnapshot(
            finalSnapshot.CatalogRevision,
            acceptedAt,
            current with { LiveMesFields = current.LiveMesFields with { } });
        var orderIntent = new OrderIntent(
            idempotencyKey,
            current.DemandId,
            current.DemandRevision,
            acceptedAt,
            OrderIntentState.Pending);
        var stored = await _store.GetOrCreateAcceptanceAsync(
            acceptedDemand,
            orderIntent,
            cancellationToken).ConfigureAwait(false);
        return new DemandAcceptanceResult(
            DemandAcceptanceOutcome.Accepted,
            current,
            stored.AcceptedDemand,
            stored.OrderIntent);
    }

    private static bool HasSameDecisionFacts(
        ExternallyReadableDemandSnapshot discovered,
        ExternallyReadableDemandSnapshot current) =>
        string.Equals(discovered.DemandId, current.DemandId, StringComparison.Ordinal)
        && string.Equals(discovered.SeriesId, current.SeriesId, StringComparison.Ordinal)
        && string.Equals(discovered.WorkType, current.WorkType, StringComparison.Ordinal)
        && string.Equals(discovered.Sublot, current.Sublot, StringComparison.Ordinal)
        && discovered.Generation == current.Generation
        && discovered.DemandRevision == current.DemandRevision
        && discovered.CreatedAt.Equals(current.CreatedAt)
        && discovered.ValueObservedAt.Equals(current.ValueObservedAt)
        && string.Equals(
            discovered.ValuePollTraceId,
            current.ValuePollTraceId,
            StringComparison.Ordinal)
        && string.Equals(
            discovered.ValueProjectionCommitId,
            current.ValueProjectionCommitId,
            StringComparison.Ordinal)
        && Equals(discovered.LiveMesFields, current.LiveMesFields);
}
