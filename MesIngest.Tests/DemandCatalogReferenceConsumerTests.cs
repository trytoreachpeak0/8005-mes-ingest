using MesIngest.Core.SeriesProjection;
using MesIngest.Infrastructure.SqlServer;
using MesIngest.ReferenceConsumer;

namespace MesIngest.Tests;

public sealed class DemandCatalogReferenceConsumerTests
{
    private static readonly HistoryEpoch TestHistoryEpoch = HistoryEpoch.FromGuid(
        Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public async Task New_consumer_rebuilds_discarded_cache_from_a_complete_catalog_without_a_revision_cursor()
    {
        var catalog = Catalog(revision: 7, Demand("demand-a", demandRevision: 3));
        var client = new ScriptedCatalogClient(
            ExternallyReadableDemandCatalogRead.Complete(catalog),
            ExternallyReadableDemandCatalogRead.Unchanged(
                new ExternallyReadableDemandCatalogIdentity(TestHistoryEpoch, 7)),
            ExternallyReadableDemandCatalogRead.Complete(catalog));

        var firstProcess = new DemandCatalogReferenceConsumer(client, new RecordingConsumerStore());
        var firstRead = await firstProcess.RefreshAsync();
        var unchangedRead = await firstProcess.RefreshAsync();

        var restartedProcess = new DemandCatalogReferenceConsumer(client, new RecordingConsumerStore());
        var restartRead = await restartedProcess.RefreshAsync();

        Assert.Equal(7, firstRead.CatalogRevision);
        Assert.Equal(7, unchangedRead.CatalogRevision);
        Assert.Equal(7, restartRead.CatalogRevision);
        Assert.Equal("demand-a", Assert.Single(restartRead.Items).DemandId);
        Assert.Equal(new long?[] { null, 7, null }, client.KnownRevisions);
    }

    [Fact]
    public async Task Old_epoch_signal_discards_the_conditional_cache_and_retries_once_unconditionally()
    {
        var oldCatalog = Catalog(revision: 7, Demand("demand-old", demandRevision: 3));
        var newEpoch = HistoryEpoch.FromGuid(
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var newCatalog = Catalog(revision: 1) with { HistoryEpoch = newEpoch };
        var client = new EpochMismatchCatalogClient(
            ExternallyReadableDemandCatalogRead.Complete(oldCatalog),
            new HistoryEpochMismatchException(newEpoch, TestHistoryEpoch),
            ExternallyReadableDemandCatalogRead.Complete(newCatalog));
        var consumer = new DemandCatalogReferenceConsumer(client, new RecordingConsumerStore());
        await consumer.RefreshAsync();

        var refreshed = await consumer.RefreshAsync();

        Assert.Equal(newEpoch, refreshed.HistoryEpoch);
        Assert.Equal(1, refreshed.CatalogRevision);
        Assert.Empty(refreshed.Items);
        Assert.Equal(
            new ExternallyReadableDemandCatalogIdentity?[]
            {
                null,
                new(TestHistoryEpoch, 7),
                null,
            },
            client.KnownIdentities);
    }

    [Fact]
    public async Task Second_old_epoch_signal_is_not_retried_and_the_next_refresh_stays_unconditional()
    {
        var oldCatalog = Catalog(revision: 7, Demand("demand-old", demandRevision: 3));
        var newEpoch = HistoryEpoch.FromGuid(
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var mismatch = new HistoryEpochMismatchException(newEpoch, TestHistoryEpoch);
        var newCatalog = Catalog(revision: 1) with { HistoryEpoch = newEpoch };
        var client = new EpochMismatchCatalogClient(
            ExternallyReadableDemandCatalogRead.Complete(oldCatalog),
            mismatch,
            mismatch,
            ExternallyReadableDemandCatalogRead.Complete(newCatalog));
        var consumer = new DemandCatalogReferenceConsumer(client, new RecordingConsumerStore());
        await consumer.RefreshAsync();

        await Assert.ThrowsAsync<HistoryEpochMismatchException>(() => consumer.RefreshAsync());
        var recovered = await consumer.RefreshAsync();

        Assert.Equal(newEpoch, recovered.HistoryEpoch);
        Assert.Equal(
            new ExternallyReadableDemandCatalogIdentity?[]
            {
                null,
                new(TestHistoryEpoch, 7),
                null,
                null,
            },
            client.KnownIdentities);
    }

    [Theory]
    [InlineData(4, "N3-3")]
    [InlineData(3, "N3-8")]
    public async Task Commit_point_unconditionally_rereads_and_rejects_a_changed_revision_or_value(
        long finalDemandRevision,
        string finalArea)
    {
        var candidate = Demand("demand-a", demandRevision: 3);
        var changed = Demand("demand-a", finalDemandRevision, finalArea);
        var client = new ScriptedCatalogClient(
            ExternallyReadableDemandCatalogRead.Complete(Catalog(7, candidate)),
            ExternallyReadableDemandCatalogRead.Complete(Catalog(8, changed)));
        var store = new RecordingConsumerStore();
        var consumer = new DemandCatalogReferenceConsumer(client, store);
        await consumer.RefreshAsync();

        var result = await consumer.AcceptAsync(candidate, "intent-a");

        Assert.Equal(DemandAcceptanceOutcome.CandidateChanged, result.Outcome);
        Assert.Equal(changed, result.CurrentDemand);
        Assert.Null(result.OrderIntent);
        Assert.Empty(store.CreatedIntents);
        Assert.Equal(new long?[] { null, null }, client.KnownRevisions);
    }

    [Fact]
    public async Task Commit_point_accepts_an_unchanged_candidate_when_only_the_global_catalog_revision_advances()
    {
        var candidate = Demand("demand-a", demandRevision: 3);
        var unchangedAtCommit = Demand("demand-a", demandRevision: 3);
        var unrelated = Demand("demand-b", demandRevision: 1);
        var client = new ScriptedCatalogClient(
            ExternallyReadableDemandCatalogRead.Complete(Catalog(7, candidate)),
            ExternallyReadableDemandCatalogRead.Complete(Catalog(8, unchangedAtCommit, unrelated)));
        var store = new RecordingConsumerStore();
        var consumer = new DemandCatalogReferenceConsumer(client, store);
        await consumer.RefreshAsync();

        var result = await consumer.AcceptAsync(candidate, "intent-a");

        Assert.Equal(DemandAcceptanceOutcome.Accepted, result.Outcome);
        Assert.Equal(8, result.AcceptedDemand!.CatalogRevision);
        Assert.Equal(candidate, result.AcceptedDemand.Demand);
        Assert.NotSame(candidate, result.CurrentDemand);
        Assert.Single(store.AtomicWrites);
        Assert.Equal(new long?[] { null, null }, client.KnownRevisions);
    }

    [Fact]
    public async Task Commit_point_rejects_a_candidate_that_left_the_readable_catalog()
    {
        var candidate = Demand("demand-a", demandRevision: 3);
        var client = new ScriptedCatalogClient(
            ExternallyReadableDemandCatalogRead.Complete(Catalog(7, candidate)),
            ExternallyReadableDemandCatalogRead.Complete(Catalog(8)));
        var store = new RecordingConsumerStore();
        var consumer = new DemandCatalogReferenceConsumer(client, store);
        await consumer.RefreshAsync();

        var result = await consumer.AcceptAsync(candidate, "intent-a");

        Assert.Equal(DemandAcceptanceOutcome.CandidateNoLongerReadable, result.Outcome);
        Assert.Null(result.CurrentDemand);
        Assert.Null(result.AcceptedDemand);
        Assert.Null(result.OrderIntent);
        Assert.Empty(store.AtomicWrites);
        Assert.Equal(new long?[] { null, null }, client.KnownRevisions);
    }

    [Fact]
    public async Task Commit_point_rejects_changed_catalog_value_provenance_even_when_revision_and_mes_fields_match()
    {
        var candidate = Demand("demand-a", demandRevision: 3);
        var changedProvenance = candidate with
        {
            ValueObservedAt = candidate.ValueObservedAt.AddMinutes(1),
            ValuePollTraceId = "poll-value-changed",
            ValueProjectionCommitId = "commit-value-changed",
        };
        var client = new ScriptedCatalogClient(
            ExternallyReadableDemandCatalogRead.Complete(Catalog(7, candidate)),
            ExternallyReadableDemandCatalogRead.Complete(Catalog(8, changedProvenance)));
        var store = new RecordingConsumerStore();
        var consumer = new DemandCatalogReferenceConsumer(client, store);
        await consumer.RefreshAsync();

        var result = await consumer.AcceptAsync(candidate, "intent-a");

        Assert.Equal(DemandAcceptanceOutcome.CandidateChanged, result.Outcome);
        Assert.Equal(changedProvenance, result.CurrentDemand);
        Assert.Empty(store.AtomicWrites);
        Assert.Equal(new long?[] { null, null }, client.KnownRevisions);
    }

    [Fact]
    public async Task Commit_point_freezes_the_unconditional_read_even_when_a_refresh_replaces_the_cache()
    {
        var candidate = Demand("demand-a", demandRevision: 3);
        var olderCatalog = Catalog(7, candidate);
        DemandCatalogReferenceConsumer? consumer = null;
        var finalItems = new CallbackReadOnlyList<ExternallyReadableDemandSnapshot>(
            [candidate],
            () => consumer!.RefreshAsync().GetAwaiter().GetResult());
        var finalCatalog = new ExternallyReadableDemandCatalogSnapshot(
            TestHistoryEpoch,
            CatalogRevision: 8,
            "commit-8",
            ProjectionSequence: 8,
            new DateTimeOffset(2026, 8, 13, 8, 8, 0, TimeSpan.Zero),
            finalItems);
        var client = new ScriptedCatalogClient(
            ExternallyReadableDemandCatalogRead.Complete(finalCatalog),
            ExternallyReadableDemandCatalogRead.Complete(olderCatalog));
        consumer = new DemandCatalogReferenceConsumer(client, new RecordingConsumerStore());

        var result = await consumer.AcceptAsync(candidate, "intent-a");

        Assert.Equal(DemandAcceptanceOutcome.Accepted, result.Outcome);
        Assert.Equal(8, result.AcceptedDemand!.CatalogRevision);
        Assert.Equal(new long?[] { null, 8 }, client.KnownRevisions);
    }

    [Fact]
    public async Task Accepted_snapshot_and_order_intent_are_atomically_frozen_and_ignore_later_catalog_refreshes()
    {
        var acceptedAt = new DateTimeOffset(2026, 8, 13, 9, 30, 0, TimeSpan.Zero);
        var candidate = Demand("demand-a", demandRevision: 3);
        var unchangedAtCommit = Demand("demand-a", demandRevision: 3);
        var changedLater = Demand("demand-a", demandRevision: 4, area: "N3-8");
        var client = new ScriptedCatalogClient(
            ExternallyReadableDemandCatalogRead.Complete(Catalog(7, candidate)),
            ExternallyReadableDemandCatalogRead.Complete(Catalog(7, unchangedAtCommit)),
            ExternallyReadableDemandCatalogRead.Complete(Catalog(8, changedLater)));
        var store = new RecordingConsumerStore();
        var consumer = new DemandCatalogReferenceConsumer(
            client,
            store,
            new FixedTimeProvider(acceptedAt));
        await consumer.RefreshAsync();

        var result = await consumer.AcceptAsync(candidate, "order-intent-a");
        await consumer.RefreshAsync();

        Assert.Equal(DemandAcceptanceOutcome.Accepted, result.Outcome);
        var atomicWrite = Assert.Single(store.AtomicWrites);
        Assert.Same(atomicWrite.AcceptedDemand, result.AcceptedDemand);
        Assert.Same(atomicWrite.OrderIntent, result.OrderIntent);
        Assert.Equal(7, atomicWrite.AcceptedDemand.CatalogRevision);
        Assert.Equal(acceptedAt, atomicWrite.AcceptedDemand.AcceptedAt);
        Assert.Equal(candidate, atomicWrite.AcceptedDemand.Demand);
        Assert.NotSame(candidate, atomicWrite.AcceptedDemand.Demand);
        Assert.Equal("N3-3", atomicWrite.AcceptedDemand.Demand.LiveMesFields.Area);
        Assert.Equal("order-intent-a", atomicWrite.OrderIntent.IdempotencyKey);
        Assert.Equal(candidate.DemandId, atomicWrite.OrderIntent.DemandId);
        Assert.Equal(candidate.DemandRevision, atomicWrite.OrderIntent.AcceptedDemandRevision);
        Assert.Equal(OrderIntentState.Pending, atomicWrite.OrderIntent.State);
    }

    [Fact]
    public async Task Unknown_remote_result_reconciles_the_same_stable_idempotency_key_without_a_second_order()
    {
        var intent = new OrderIntent(
            "order-intent-stable-a",
            "demand-a",
            AcceptedDemandRevision: 3,
            new DateTimeOffset(2026, 8, 13, 9, 30, 0, TimeSpan.Zero),
            OrderIntentState.Pending);
        var store = new RecordingConsumerStore(intent);
        var gateway = new TimeoutAfterCreateOrderGateway();
        var dispatcher = new ReferenceOrderIntentDispatcher(store, gateway);

        var unknown = await dispatcher.DispatchAsync(intent.IdempotencyKey);
        var reconciled = await dispatcher.DispatchAsync(intent.IdempotencyKey);

        Assert.Equal(OrderDispatchOutcome.ResultUnknown, unknown.Outcome);
        Assert.Equal(OrderIntentState.ResultUnknown, unknown.OrderIntent.State);
        Assert.Equal(OrderDispatchOutcome.Confirmed, reconciled.Outcome);
        Assert.Equal(OrderIntentState.Confirmed, reconciled.OrderIntent.State);
        Assert.Equal("riot-order-42", reconciled.OrderIntent.RemoteOrderId);
        Assert.Equal([intent.IdempotencyKey], gateway.CreateKeys);
        Assert.Equal([intent.IdempotencyKey, intent.IdempotencyKey], gateway.FindKeys);
        Assert.DoesNotContain(gateway.CreateKeys, key => key != intent.IdempotencyKey);
    }

    [Fact]
    public async Task Unknown_remote_result_retries_only_the_same_stable_key_when_lookup_finds_no_order()
    {
        var intent = new OrderIntent(
            "order-intent-stable-before-send",
            "demand-a",
            AcceptedDemandRevision: 3,
            new DateTimeOffset(2026, 8, 13, 9, 30, 0, TimeSpan.Zero),
            OrderIntentState.Pending);
        var store = new RecordingConsumerStore(intent);
        var gateway = new TimeoutBeforeCreateOrderGateway();
        var dispatcher = new ReferenceOrderIntentDispatcher(store, gateway);

        var unknown = await dispatcher.DispatchAsync(intent.IdempotencyKey);
        var retried = await dispatcher.DispatchAsync(intent.IdempotencyKey);

        Assert.Equal(OrderDispatchOutcome.ResultUnknown, unknown.Outcome);
        Assert.Equal(OrderDispatchOutcome.Confirmed, retried.Outcome);
        Assert.Equal("riot-order-after-retry", retried.OrderIntent.RemoteOrderId);
        Assert.Equal(
            [intent.IdempotencyKey, intent.IdempotencyKey],
            gateway.CreateKeys);
        Assert.Equal(
            [intent.IdempotencyKey, intent.IdempotencyKey],
            gateway.FindKeys);
    }

    [Fact]
    public async Task Rejected_order_intent_never_queries_or_creates_a_remote_order()
    {
        var intent = new OrderIntent(
            "order-intent-rejected-a",
            "demand-a",
            AcceptedDemandRevision: 3,
            new DateTimeOffset(2026, 8, 13, 9, 30, 0, TimeSpan.Zero),
            OrderIntentState.Rejected);
        var store = new RecordingConsumerStore(intent);
        var gateway = new TimeoutAfterCreateOrderGateway();
        var dispatcher = new ReferenceOrderIntentDispatcher(store, gateway);

        var result = await dispatcher.DispatchAsync(intent.IdempotencyKey);

        Assert.Equal(OrderDispatchOutcome.Rejected, result.Outcome);
        Assert.Same(intent, result.OrderIntent);
        Assert.Empty(gateway.FindKeys);
        Assert.Empty(gateway.CreateKeys);
    }

    [Fact]
    public void Mes_ingest_production_assemblies_do_not_reference_consumer_owned_dispatch_state()
    {
        var referenceConsumerAssemblyName = typeof(DemandCatalogReferenceConsumer).Assembly.GetName().Name;
        var mesIngestAssemblies = new[]
        {
            typeof(NewMesIngestContract).Assembly,
            typeof(SqlServerMesIngestProjection).Assembly,
            typeof(Program).Assembly,
        };

        Assert.All(
            mesIngestAssemblies,
            assembly => Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                reference => string.Equals(
                    reference.Name,
                    referenceConsumerAssemblyName,
                    StringComparison.Ordinal)));
        Assert.DoesNotContain(
            typeof(DemandCatalogReferenceConsumer).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "MesIngest.Infrastructure", StringComparison.Ordinal)
                         || string.Equals(reference.Name, "MesIngest.Host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task File_store_survives_restart_and_rejects_same_key_with_different_content()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"mes-ingest-reference-consumer-{Guid.NewGuid():N}.json");
        try
        {
            var acceptedAt = new DateTimeOffset(2026, 8, 13, 9, 30, 0, TimeSpan.Zero);
            var accepted = new AcceptedDemandSnapshot(7, acceptedAt, Demand("demand-a", 3));
            var intent = new OrderIntent(
                "durable-intent-a",
                accepted.Demand.DemandId,
                accepted.Demand.DemandRevision,
                acceptedAt,
                OrderIntentState.Pending);
            var firstProcess = new FileReferenceConsumerStore(path);

            var first = await firstProcess.GetOrCreateAcceptanceAsync(accepted, intent);
            var restartedProcess = new FileReferenceConsumerStore(path);
            var replay = await restartedProcess.GetOrCreateAcceptanceAsync(accepted, intent);
            var unknown = await restartedProcess.MarkOrderIntentResultUnknownAsync(intent.IdempotencyKey);
            var afterSecondRestart = await new FileReferenceConsumerStore(path)
                .GetOrderIntentAsync(intent.IdempotencyKey);

            Assert.Equal(first, replay);
            Assert.Equal(7, replay.AcceptedDemand.CatalogRevision);
            Assert.Equal(acceptedAt, replay.AcceptedDemand.AcceptedAt);
            Assert.Equal(OrderIntentState.ResultUnknown, unknown.State);
            Assert.Equal(unknown, afterSecondRestart);
            Assert.Contains("\"AcceptedDemand\"", await File.ReadAllTextAsync(path));

            await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() =>
                restartedProcess.GetOrCreateAcceptanceAsync(
                    accepted with
                    {
                        CatalogRevision = 8,
                        AcceptedAt = acceptedAt.AddMinutes(1),
                    },
                    intent with { CreatedAt = acceptedAt.AddMinutes(1) }));

            var differentDemand = Demand("demand-b", 1);
            var conflict = await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() =>
                restartedProcess.GetOrCreateAcceptanceAsync(
                    new AcceptedDemandSnapshot(9, acceptedAt, differentDemand),
                    intent with
                    {
                        DemandId = differentDemand.DemandId,
                        AcceptedDemandRevision = differentDemand.DemandRevision,
                    }));
            Assert.Contains(intent.IdempotencyKey, conflict.Message, StringComparison.Ordinal);
            Assert.Equal(unknown, await new FileReferenceConsumerStore(path)
                .GetOrderIntentAsync(intent.IdempotencyKey));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ExternallyReadableDemandCatalogSnapshot Catalog(
        long revision,
        params ExternallyReadableDemandSnapshot[] items) =>
        new(
            TestHistoryEpoch,
            revision,
            $"commit-{revision}",
            revision,
            new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero).AddMinutes(revision),
            items);

    private static ExternallyReadableDemandSnapshot Demand(
        string demandId,
        long demandRevision,
        string area = "N3-3") =>
        new(
            demandId,
            $"series-{demandId}",
            "CUT",
            $"SUBLOT-{demandId}",
            Generation: 1,
            demandRevision,
            new DateTimeOffset(2026, 8, 13, 7, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero),
            "poll-value",
            "commit-value",
            new LiveMesFieldSetSnapshot(
                area,
                "WB-03",
                "STEP-2",
                new DateTimeOffset(2026, 8, 12, 7, 0, 0, TimeSpan.Zero),
                "QFN-G1"));

    private sealed class ScriptedCatalogClient : IExternallyReadableDemandCatalogClient
    {
        private readonly Queue<ExternallyReadableDemandCatalogRead> _reads;

        public ScriptedCatalogClient(params ExternallyReadableDemandCatalogRead[] reads) =>
            _reads = new Queue<ExternallyReadableDemandCatalogRead>(reads);

        public List<ExternallyReadableDemandCatalogIdentity?> KnownIdentities { get; } = [];

        public IReadOnlyList<long?> KnownRevisions =>
            KnownIdentities.Select(identity => identity?.CatalogRevision).ToArray();

        public Task<ExternallyReadableDemandCatalogRead> ReadAsync(
            ExternallyReadableDemandCatalogIdentity? knownIdentity,
            CancellationToken cancellationToken = default)
        {
            KnownIdentities.Add(knownIdentity);
            return Task.FromResult(_reads.Dequeue());
        }
    }

    private sealed class EpochMismatchCatalogClient(params object[] responses)
        : IExternallyReadableDemandCatalogClient
    {
        private readonly Queue<object> _responses = new(responses);

        public List<ExternallyReadableDemandCatalogIdentity?> KnownIdentities { get; } = [];

        public Task<ExternallyReadableDemandCatalogRead> ReadAsync(
            ExternallyReadableDemandCatalogIdentity? knownIdentity,
            CancellationToken cancellationToken = default)
        {
            KnownIdentities.Add(knownIdentity);
            return _responses.Dequeue() switch
            {
                ExternallyReadableDemandCatalogRead read => Task.FromResult(read),
                Exception exception => Task.FromException<ExternallyReadableDemandCatalogRead>(exception),
                var unexpected => throw new InvalidOperationException(
                    $"Unexpected scripted catalog response '{unexpected.GetType().Name}'."),
            };
        }
    }

    private sealed class RecordingConsumerStore : IReferenceConsumerStore
    {
        private readonly Dictionary<string, OrderIntent> _intents = new(StringComparer.Ordinal);

        public RecordingConsumerStore(params OrderIntent[] intents)
        {
            foreach (var intent in intents)
            {
                _intents.Add(intent.IdempotencyKey, intent);
            }
        }

        public List<OrderIntent> CreatedIntents { get; } = [];
        public List<StoredDemandAcceptance> AtomicWrites { get; } = [];

        public Task<StoredDemandAcceptance> GetOrCreateAcceptanceAsync(
            AcceptedDemandSnapshot acceptedDemand,
            OrderIntent orderIntent,
            CancellationToken cancellationToken = default)
        {
            CreatedIntents.Add(orderIntent);
            _intents.TryAdd(orderIntent.IdempotencyKey, orderIntent);
            var stored = new StoredDemandAcceptance(acceptedDemand, orderIntent);
            AtomicWrites.Add(stored);
            return Task.FromResult(stored);
        }

        public Task<OrderIntent?> GetOrderIntentAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_intents.GetValueOrDefault(idempotencyKey));

        public Task<OrderIntent> MarkOrderIntentResultUnknownAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Update(idempotencyKey, intent => intent with
            {
                State = OrderIntentState.ResultUnknown,
            }));

        public Task<OrderIntent> ConfirmOrderIntentAsync(
            string idempotencyKey,
            string remoteOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Update(idempotencyKey, intent => intent with
            {
                State = OrderIntentState.Confirmed,
                RemoteOrderId = remoteOrderId,
            }));

        private OrderIntent Update(string key, Func<OrderIntent, OrderIntent> update)
        {
            var updated = update(_intents[key]);
            _intents[key] = updated;
            return updated;
        }
    }

    private sealed class TimeoutAfterCreateOrderGateway : IReferenceOrderGateway
    {
        private RemoteOrderReference? _createdOrder;

        public List<string> FindKeys { get; } = [];

        public List<string> CreateKeys { get; } = [];

        public Task<RemoteOrderReference?> FindByIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            FindKeys.Add(idempotencyKey);
            return Task.FromResult(_createdOrder);
        }

        public Task<RemoteOrderReference> CreateAsync(
            OrderIntent intent,
            CancellationToken cancellationToken = default)
        {
            CreateKeys.Add(intent.IdempotencyKey);
            _createdOrder = new RemoteOrderReference("riot-order-42", intent.IdempotencyKey);
            throw new OrderResultUnknownException("The create response was lost after remote acceptance.");
        }
    }

    private sealed class TimeoutBeforeCreateOrderGateway : IReferenceOrderGateway
    {
        private int _createAttempts;

        public List<string> FindKeys { get; } = [];

        public List<string> CreateKeys { get; } = [];

        public Task<RemoteOrderReference?> FindByIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            FindKeys.Add(idempotencyKey);
            return Task.FromResult<RemoteOrderReference?>(null);
        }

        public Task<RemoteOrderReference> CreateAsync(
            OrderIntent intent,
            CancellationToken cancellationToken = default)
        {
            CreateKeys.Add(intent.IdempotencyKey);
            _createAttempts++;
            if (_createAttempts == 1)
            {
                throw new OrderResultUnknownException(
                    "The transport timed out before the remote service observed the request.");
            }

            return Task.FromResult(new RemoteOrderReference(
                "riot-order-after-retry",
                intent.IdempotencyKey));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class CallbackReadOnlyList<T>(
        IReadOnlyList<T> items,
        Action onFirstEnumeration) : IReadOnlyList<T>
    {
        private bool _enumerated;

        public int Count => items.Count;

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            if (!_enumerated)
            {
                _enumerated = true;
                onFirstEnumeration();
            }

            return items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
