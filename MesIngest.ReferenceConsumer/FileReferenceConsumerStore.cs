using System.Collections.Concurrent;
using System.Text.Json;

namespace MesIngest.ReferenceConsumer;

/// <summary>
/// A minimal consumer-owned durable store. Acceptance evidence and its order
/// intent are committed as one replaceable document; MesIngest never receives
/// this path and has no dependency on this assembly.
/// </summary>
public sealed class FileReferenceConsumerStore : IReferenceConsumerStore
{
    private const int CurrentFormatVersion = 1;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate;

    public FileReferenceConsumerStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(Path.GetFileName(_path)))
        {
            throw new ArgumentException("The consumer store path must name a file.", nameof(path));
        }

        _gate = PathGates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<StoredDemandAcceptance> GetOrCreateAcceptanceAsync(
        AcceptedDemandSnapshot acceptedDemand,
        OrderIntent orderIntent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acceptedDemand);
        ArgumentNullException.ThrowIfNull(orderIntent);
        ValidatePair(acceptedDemand, orderIntent);
        if (orderIntent.State != OrderIntentState.Pending || orderIntent.RemoteOrderId is not null)
        {
            throw new ArgumentException(
                "A newly accepted OrderIntent must be pending and have no remote identity.",
                nameof(orderIntent));
        }

        return await WithStateAsync(async state =>
        {
            var existing = state.Acceptances.SingleOrDefault(value =>
                string.Equals(
                    value.OrderIntent.IdempotencyKey,
                    orderIntent.IdempotencyKey,
                    StringComparison.Ordinal));
            if (existing is not null)
            {
                if (!HasSameImmutableIntent(existing, acceptedDemand, orderIntent))
                {
                    throw new IdempotencyKeyConflictException(orderIntent.IdempotencyKey);
                }

                return existing;
            }

            var stored = new StoredDemandAcceptance(
                acceptedDemand with
                {
                    Demand = acceptedDemand.Demand with
                    {
                        LiveMesFields = acceptedDemand.Demand.LiveMesFields with { },
                    },
                },
                orderIntent with { });
            state.Acceptances.Add(stored);
            await PersistAsync(state, cancellationToken).ConfigureAwait(false);
            return stored;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<OrderIntent?> GetOrderIntentAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return WithStateAsync(
            state => Task.FromResult(state.Acceptances.SingleOrDefault(value =>
                    string.Equals(
                        value.OrderIntent.IdempotencyKey,
                        idempotencyKey,
                        StringComparison.Ordinal))
                ?.OrderIntent),
            cancellationToken);
    }

    public Task<OrderIntent> MarkOrderIntentResultUnknownAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        UpdateIntentAsync(
            idempotencyKey,
            intent => intent.State switch
            {
                OrderIntentState.Pending or OrderIntentState.ResultUnknown => intent with
                {
                    State = OrderIntentState.ResultUnknown,
                },
                OrderIntentState.Confirmed => intent,
                _ => throw new InvalidOperationException(
                    "A rejected OrderIntent cannot become result-unknown."),
            },
            cancellationToken);

    public Task<OrderIntent> ConfirmOrderIntentAsync(
        string idempotencyKey,
        string remoteOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteOrderId);
        return UpdateIntentAsync(
            idempotencyKey,
            intent => intent.State switch
            {
                OrderIntentState.Confirmed when string.Equals(
                    intent.RemoteOrderId,
                    remoteOrderId,
                    StringComparison.Ordinal) => intent,
                OrderIntentState.Confirmed => throw new InvalidOperationException(
                    "A confirmed OrderIntent cannot be rebound to another remote order."),
                OrderIntentState.Rejected => throw new InvalidOperationException(
                    "A rejected OrderIntent cannot be confirmed."),
                _ => intent with
                {
                    State = OrderIntentState.Confirmed,
                    RemoteOrderId = remoteOrderId,
                },
            },
            cancellationToken);
    }

    private async Task<OrderIntent> UpdateIntentAsync(
        string idempotencyKey,
        Func<OrderIntent, OrderIntent> update,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return await WithStateAsync(async state =>
        {
            var index = state.Acceptances.FindIndex(value => string.Equals(
                value.OrderIntent.IdempotencyKey,
                idempotencyKey,
                StringComparison.Ordinal));
            if (index < 0)
            {
                throw new KeyNotFoundException(
                    $"OrderIntent '{idempotencyKey}' does not exist.");
            }

            var current = state.Acceptances[index];
            var updated = update(current.OrderIntent);
            if (updated != current.OrderIntent)
            {
                state.Acceptances[index] = current with { OrderIntent = updated };
                await PersistAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return updated;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> WithStateAsync<T>(
        Func<StoreDocument, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(await LoadAsync(cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoreDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new StoreDocument(CurrentFormatVersion, []);
        }

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<StoreDocument>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The reference-consumer store is empty.");
        if (document.FormatVersion != CurrentFormatVersion || document.Acceptances is null)
        {
            throw new InvalidDataException(
                "The reference-consumer store format is unsupported.");
        }

        if (document.Acceptances.Select(value => value.OrderIntent.IdempotencyKey)
            .Distinct(StringComparer.Ordinal).Count() != document.Acceptances.Count)
        {
            throw new InvalidDataException(
                "The reference-consumer store contains duplicate idempotency keys.");
        }

        foreach (var acceptance in document.Acceptances)
        {
            ValidatePair(acceptance.AcceptedDemand, acceptance.OrderIntent);
        }

        return document;
    }

    private async Task PersistAsync(
        StoreDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The store path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidatePair(
        AcceptedDemandSnapshot acceptedDemand,
        OrderIntent orderIntent)
    {
        if (!string.Equals(
                acceptedDemand.Demand.DemandId,
                orderIntent.DemandId,
                StringComparison.Ordinal)
            || acceptedDemand.Demand.DemandRevision != orderIntent.AcceptedDemandRevision
            || !acceptedDemand.AcceptedAt.EqualsExact(orderIntent.CreatedAt))
        {
            throw new ArgumentException(
                "AcceptedDemandSnapshot and OrderIntent must identify the same Demand revision.");
        }
    }

    private static bool HasSameImmutableIntent(
        StoredDemandAcceptance existing,
        AcceptedDemandSnapshot candidate,
        OrderIntent orderIntent) =>
        existing.AcceptedDemand == candidate
        && string.Equals(
            existing.OrderIntent.DemandId,
            orderIntent.DemandId,
            StringComparison.Ordinal)
        && existing.OrderIntent.AcceptedDemandRevision == orderIntent.AcceptedDemandRevision
        && existing.OrderIntent.CreatedAt.EqualsExact(orderIntent.CreatedAt);

    private sealed record StoreDocument(
        int FormatVersion,
        List<StoredDemandAcceptance> Acceptances);
}

public sealed class IdempotencyKeyConflictException : InvalidOperationException
{
    public IdempotencyKeyConflictException(string idempotencyKey)
        : base($"Idempotency key '{idempotencyKey}' is already bound to different content.")
    {
    }
}
