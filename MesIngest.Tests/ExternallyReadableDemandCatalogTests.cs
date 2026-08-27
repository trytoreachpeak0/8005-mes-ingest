using System.Net;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.ReferenceConsumer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class ExternallyReadableDemandCatalogTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CatalogUri = "/api/v2/externally-readable-demand-catalog";
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public ExternallyReadableDemandCatalogTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Theory]
    [InlineData(DemandSeriesLifecycleContract.Tracking, DemandSeriesLifecycleContract.Visible, 1, true, false, true)]
    [InlineData(DemandSeriesLifecycleContract.Tracking, DemandSeriesLifecycleContract.Gone, 1, true, false, false)]
    [InlineData(DemandSeriesLifecycleContract.Archived, DemandSeriesLifecycleContract.LongGoneButVisible, 1, true, false, false)]
    [InlineData(DemandSeriesLifecycleContract.Tracking, DemandSeriesLifecycleContract.Visible, 2, false, true, false)]
    [InlineData(DemandSeriesLifecycleContract.Tracking, DemandSeriesLifecycleContract.Visible, 1, true, true, false)]
    public void Central_policy_requires_visible_unique_valid_condition_free_unarchived_demand(
        string lifecycle,
        string status,
        int observationCount,
        bool hasFields,
        bool hasCondition,
        bool expected)
    {
        var fields = hasFields
            ? new LiveMesFieldSetSnapshot(
                "N3-3",
                "WB-03",
                "焊线2",
                new DateTimeOffset(2026, 8, 13, 9, 58, 0, TimeSpan.FromHours(8)),
                "QFN")
            : null;

        var actual = ExternallyReadableDemandPolicy.IsEligible(
            lifecycle,
            status,
            observationCount,
            fields,
            hasCondition ? ["DUPLICATE_TRANSPORT_DEMAND_KEY"] : []);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Central_policy_rejects_missing_or_invalid_required_fields()
    {
        Assert.False(ExternallyReadableDemandPolicy.IsEligible(
            DemandSeriesLifecycleContract.Tracking,
            DemandSeriesLifecycleContract.Visible,
            1,
            new LiveMesFieldSetSnapshot(
                "N03-03",
                "WB-03",
                "焊线2",
                new DateTimeOffset(2026, 8, 13, 9, 58, 0, TimeSpan.Zero),
                "QFN"),
            []));
        Assert.False(ExternallyReadableDemandPolicy.IsEligible(
            DemandSeriesLifecycleContract.Tracking,
            DemandSeriesLifecycleContract.Visible,
            1,
            new LiveMesFieldSetSnapshot(
                "N3-3",
                null,
                "焊线2",
                new DateTimeOffset(2026, 8, 13, 9, 58, 0, TimeSpan.Zero),
                "QFN"),
            []));
    }

    [Ticket01SqlServerFact]
    public async Task Initial_empty_catalog_is_a_complete_stable_revision_zero_resource()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var initial = await GetCatalogAsync(client);

        Assert.Equal(0L, initial.Body.GetProperty("catalogRevision").GetInt64());
        Assert.Equal(JsonValueKind.Null, initial.Body.GetProperty("projectionCommitId").ValueKind);
        Assert.Equal(JsonValueKind.Null, initial.Body.GetProperty("projectionSequence").ValueKind);
        Assert.Equal(JsonValueKind.Null, initial.Body.GetProperty("projectionCommittedAt").ValueKind);
        Assert.Equal(0, initial.Body.GetProperty("count").GetInt32());
        Assert.Empty(initial.Body.GetProperty("items").EnumerateArray());
        Assert.Equal(
            $"W/\"catalog-h{Guid.Parse(initial.Body.GetProperty("historyEpoch").GetString()!):N}-r0\"",
            initial.ETag);

        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, CatalogUri);
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", initial.ETag);
        using var unchanged = await client.SendAsync(conditionalRequest);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(string.Empty, await unchanged.Content.ReadAsStringAsync());
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Conditional_catalog_identity_from_an_old_history_epoch_is_rejected()
    {
        string oldEtag;
        Guid oldHistoryEpoch;
        await using (var oldDatabase = await Ticket01SqlServerDatabase.CreateAsync())
        {
            using var oldEnvironment = ConfigureProductionV2Environment(oldDatabase.ConnectionString);
            await using var oldFactory = CreateFactory();
            using var oldClient = oldFactory.CreateClient();

            var oldCatalog = await GetCatalogAsync(oldClient);
            oldEtag = oldCatalog.ETag;
            oldHistoryEpoch = Guid.Parse(oldCatalog.Body.GetProperty("historyEpoch").GetString()!);
            AssertDatabaseEvidence(oldDatabase);
        }

        await using var newDatabase = await Ticket01SqlServerDatabase.CreateAsync();
        using var newEnvironment = ConfigureProductionV2Environment(newDatabase.ConnectionString);
        await using var newFactory = CreateFactory();
        using var newClient = newFactory.CreateClient();
        var currentCatalog = await GetCatalogAsync(newClient);
        var currentHistoryEpoch = Guid.Parse(
            currentCatalog.Body.GetProperty("historyEpoch").GetString()!);
        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUri);
        request.Headers.TryAddWithoutValidation("If-None-Match", oldEtag);

        using var response = await newClient.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            HistoryEpochMismatchException.ErrorCode,
            body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
        Assert.Equal(
            currentHistoryEpoch,
            body.RootElement.GetProperty("currentHistoryEpoch").GetGuid());
        Assert.Equal(
            oldHistoryEpoch,
            body.RootElement.GetProperty("suppliedHistoryEpoch").GetGuid());
        AssertDatabaseEvidence(newDatabase);
    }

    [Ticket01SqlServerFact]
    public async Task Catalog_revision_changes_once_only_for_member_or_member_value_changes()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var time = new DateTimeOffset(2026, 8, 13, 18, 0, 0, TimeSpan.Zero);

        var initial = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-initial",
            time,
            Observation("SL-T09-B", "N3-3", "WB-03", "QFN-A"),
            Observation("SL-T09-A", "N3-8", "WB-08", "QFN-B")));
        var catalog1 = await GetCatalogAsync(client);
        Assert.Equal(1L, catalog1.Body.GetProperty("catalogRevision").GetInt64());
        Assert.Equal(2, catalog1.Body.GetProperty("count").GetInt32());
        Assert.Equal(initial.ProjectionCommitId, catalog1.Body.GetProperty("projectionCommitId").GetString());
        var ids1 = catalog1.Body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("demandId").GetString()!)
            .ToArray();
        Assert.Equal(ids1.OrderBy(value => value, StringComparer.Ordinal), ids1);
        Assert.All(catalog1.Body.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal(1L, item.GetProperty("demandRevision").GetInt64());
            Assert.Equal(JsonValueKind.Object, item.GetProperty("transportDemandKey").ValueKind);
            Assert.Equal(JsonValueKind.Object, item.GetProperty("liveMesFields").ValueKind);
        });

        var unchanged = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-unchanged",
            time.AddMinutes(1),
            Observation("SL-T09-B", "N3-3", "WB-03", "QFN-A"),
            Observation("SL-T09-A", "N3-8", "WB-08", "QFN-B")));
        var catalogUnchanged = await GetCatalogAsync(client);
        Assert.Equal(1L, catalogUnchanged.Body.GetProperty("catalogRevision").GetInt64());
        Assert.Equal(initial.ProjectionCommitId, catalogUnchanged.Body.GetProperty("projectionCommitId").GetString());
        Assert.NotEqual(unchanged.ProjectionCommitId, catalogUnchanged.Body.GetProperty("projectionCommitId").GetString());
        Assert.Equal(catalog1.ETag, catalogUnchanged.ETag);

        var changed = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-two-values-change",
            time.AddMinutes(2),
            Observation("SL-T09-B", "N3-9", "WB-09", "QFN-C"),
            Observation("SL-T09-A", "N3-7", "WB-07", "QFN-D")));
        var catalog2 = await GetCatalogAsync(client);
        Assert.Equal(2L, catalog2.Body.GetProperty("catalogRevision").GetInt64());
        Assert.Equal(changed.ProjectionCommitId, catalog2.Body.GetProperty("projectionCommitId").GetString());
        Assert.All(
            catalog2.Body.GetProperty("items").EnumerateArray(),
            item => Assert.Equal(2L, item.GetProperty("demandRevision").GetInt64()));
        Assert.NotEqual(catalog1.ETag, catalog2.ETag);
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Failure_incomplete_and_replay_leave_the_committed_catalog_unchanged()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var time = new DateTimeOffset(2026, 8, 13, 18, 30, 0, TimeSpan.Zero);
        var acceptedRound = SuccessRound(
            "poll-ticket09-nonsuccess-baseline",
            time,
            Observation("SL-T09-STABLE", "N3-3", "WB-03", "QFN-A"));

        var accepted = await ingestor.IngestAsync(acceptedRound);
        var baseline = await GetCatalogAsync(client);

        var failure = await ingestor.IngestAsync(NonSuccessRound(
            "poll-ticket09-failure",
            time.AddMinutes(1),
            MesTaskUnionRoundOutcome.Failure,
            Observation("SL-T09-STABLE", "N3-8", "WB-08", "QFN-B")));
        var afterFailure = await GetCatalogAsync(client);

        var incomplete = await ingestor.IngestAsync(NonSuccessRound(
            "poll-ticket09-incomplete",
            time.AddMinutes(2),
            MesTaskUnionRoundOutcome.Incomplete,
            Observation("SL-T09-STABLE", "N3-9", "WB-09", "QFN-C")));
        var afterIncomplete = await GetCatalogAsync(client);

        var replay = await ingestor.IngestAsync(acceptedRound);
        var afterReplay = await GetCatalogAsync(client);

        Assert.Equal(MesTaskUnionRoundOutcome.Failure, failure.Outcome);
        Assert.Equal(MesTaskUnionRoundOutcome.Incomplete, incomplete.Outcome);
        Assert.True(replay.IsReplay);
        Assert.Equal(accepted.ProjectionCommitId, baseline.Body.GetProperty("projectionCommitId").GetString());
        AssertCatalogExactlyEqual(baseline, afterFailure);
        AssertCatalogExactlyEqual(baseline, afterIncomplete);
        AssertCatalogExactlyEqual(baseline, afterReplay);
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Conditional_catalog_read_returns_bodyless_304_or_one_atomically_committed_full_revision()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var time = new DateTimeOffset(2026, 8, 13, 19, 0, 0, TimeSpan.Zero);

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-condition-a",
            time,
            Observation("SL-T09-C", "N3-3", "WB-03", "QFN-A")));
        var first = await GetCatalogAsync(client);

        using (var request = new HttpRequestMessage(HttpMethod.Get, CatalogUri))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", first.ETag);
            using var notModified = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
            Assert.Equal(string.Empty, await notModified.Content.ReadAsStringAsync());
            Assert.Equal(first.ETag, notModified.Headers.ETag?.ToString());
        }

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-condition-b",
            time.AddMinutes(1),
            Observation("SL-T09-C", "N3-8", "WB-08", "QFN-B")));
        using var changedRequest = new HttpRequestMessage(HttpMethod.Get, CatalogUri);
        changedRequest.Headers.TryAddWithoutValidation("If-None-Match", first.ETag);
        using var changed = await client.SendAsync(changedRequest);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var changedBodyText = await changed.Content.ReadAsStringAsync();
        using var changedJson = JsonDocument.Parse(changedBodyText);
        var body = changedJson.RootElement;
        Assert.Equal(2L, body.GetProperty("catalogRevision").GetInt64());
        Assert.Equal(
            $"W/\"catalog-h{Guid.Parse(body.GetProperty("historyEpoch").GetString()!):N}-r{body.GetProperty("catalogRevision").GetInt64()}\"",
            changed.Headers.ETag?.ToString());
        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal("N3-8", item.GetProperty("liveMesFields").GetProperty("area").GetString());
    }

    [Ticket01SqlServerFact]
    public async Task Catalog_only_contains_centrally_eligible_demands_while_operations_detail_keeps_every_rejected_demand()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var time = new DateTimeOffset(2026, 8, 13, 20, 0, 0, TimeSpan.Zero);

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-qualification",
            time,
            Observation("SL-T09-GOOD", "N3-3", "WB-03", "QFN"),
            Observation("SL-T09-MISSING", "N3-8", null, "QFN"),
            Observation("SL-T09-DUP", "N3-4", "WB-04", "A"),
            Observation("SL-T09-DUP", "N3-5", "WB-05", "B"),
            Observation("SL-T09-MULTI", "N3-6", "WB-06", "QFN"),
            Observation("SL-T09-MULTI", "N3-6", "WB-06", "QFN", "DIE_ATTACH")));

        var catalog = await GetCatalogAsync(client);
        var only = Assert.Single(catalog.Body.GetProperty("items").EnumerateArray());
        Assert.Equal("SL-T09-GOOD", only.GetProperty("transportDemandKey").GetProperty("sublot").GetString());

        var operations = await GetJsonAsync(client, "/api/v2/demand-series?pageSize=20");
        Assert.Equal(5L, operations.GetProperty("exactTotalCount").GetInt64());
        Assert.Contains(
            operations.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("sublot").GetString() == "SL-T09-MISSING"
                    && item.GetProperty("externalReadabilityState").GetString() == "NOT_READABLE");
        Assert.Contains(
            operations.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("sublot").GetString() == "SL-T09-DUP"
                    && item.GetProperty("readabilityBlockers").EnumerateArray()
                        .Any(blocker => blocker.GetString() == "DUPLICATE_TRANSPORT_DEMAND_KEY"));
    }

    [Ticket01SqlServerFact]
    public async Task Field_duplicate_and_multiple_work_type_recovery_reenter_catalog_in_one_revision()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var time = new DateTimeOffset(2026, 8, 13, 20, 30, 0, TimeSpan.Zero);

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-recovery-blocked",
            time,
            Observation("SL-T09-REC-GOOD", "N3-3", "WB-03", "QFN"),
            Observation("SL-T09-REC-FIELD", "N3-8", null, "QFN"),
            Observation("SL-T09-REC-DUP", "N3-4", "WB-04", "A"),
            Observation("SL-T09-REC-DUP", "N3-5", "WB-05", "B"),
            Observation("SL-T09-REC-MULTI", "N3-6", "WB-06", "QFN"),
            Observation("SL-T09-REC-MULTI", "N3-6", "WB-06", "QFN", "DIE_ATTACH")));
        var blocked = await GetCatalogAsync(client);
        Assert.Equal(1L, blocked.Body.GetProperty("catalogRevision").GetInt64());
        Assert.Equal(
            "SL-T09-REC-GOOD",
            Assert.Single(blocked.Body.GetProperty("items").EnumerateArray())
                .GetProperty("transportDemandKey")
                .GetProperty("sublot")
                .GetString());

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-recovery-readable",
            time.AddMinutes(1),
            Observation("SL-T09-REC-GOOD", "N3-3", "WB-03", "QFN"),
            Observation("SL-T09-REC-FIELD", "N3-8", "WB-08", "QFN"),
            Observation("SL-T09-REC-DUP", "N3-4", "WB-04", "A"),
            Observation("SL-T09-REC-MULTI", "N3-6", "WB-06", "QFN")));
        var recovered = await GetCatalogAsync(client);

        Assert.Equal(2L, recovered.Body.GetProperty("catalogRevision").GetInt64());
        var recoveredItems = recovered.Body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(4, recoveredItems.Length);
        Assert.Equal(
            ["SL-T09-REC-DUP", "SL-T09-REC-FIELD", "SL-T09-REC-GOOD", "SL-T09-REC-MULTI"],
            recoveredItems
                .Select(item => item.GetProperty("transportDemandKey").GetProperty("sublot").GetString())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
        Assert.All(
            recoveredItems.Where(item => item.GetProperty("transportDemandKey").GetProperty("sublot").GetString() != "SL-T09-REC-GOOD"),
            item => Assert.True(item.GetProperty("demandRevision").GetInt64() > 1));
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Gone_demand_exits_and_postarchive_visible_successor_never_enters_catalog()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var time = new DateTimeOffset(2026, 8, 13, 22, 0, 0, TimeSpan.Zero);
        const string sublot = "SL-T09-ARCHIVED-VISIBLE";

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-archive-seed",
            time,
            Observation(sublot, "N3-3", "WB-03", "QFN-G1")));
        var visible = await GetCatalogAsync(client);
        Assert.Equal(1L, visible.Body.GetProperty("catalogRevision").GetInt64());
        Assert.Single(visible.Body.GetProperty("items").EnumerateArray());

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-archive-restore-authority",
            time.AddMinutes(1),
            Observation(sublot, "N3-3", "WB-03", "QFN-G1")));
        var restored = await GetCatalogAsync(client);
        Assert.Equal(visible.ETag, restored.ETag);

        var goneAt = time.AddMinutes(2);
        await ingestor.IngestAsync(SuccessRound("poll-ticket09-archive-gone", goneAt));
        var gone = await GetCatalogAsync(client);
        Assert.Equal(2L, gone.Body.GetProperty("catalogRevision").GetInt64());
        Assert.Empty(gone.Body.GetProperty("items").EnumerateArray());

        var archivedAt = goneAt.AddHours(12);
        await ingestor.IngestAsync(SuccessRound("poll-ticket09-archive-expired", archivedAt));
        var archived = await GetCatalogAsync(client);
        Assert.Equal(gone.ETag, archived.ETag);
        Assert.Empty(archived.Body.GetProperty("items").EnumerateArray());

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-archive-reappeared",
            archivedAt.AddMinutes(1),
            Observation(sublot, "N3-8", "WB-08", "QFN-G2")));
        var longGoneVisible = await GetCatalogAsync(client);
        Assert.Equal(gone.ETag, longGoneVisible.ETag);
        Assert.Empty(longGoneVisible.Body.GetProperty("items").EnumerateArray());

        var operations = await GetJsonAsync(
            client,
            $"/api/v2/demand-series/by-key?workType=WIRE_TO_NITROGEN&sublot={Uri.EscapeDataString(sublot)}");
        Assert.Equal("ARCHIVED", operations.GetProperty("lifecycle").GetString());
        Assert.Equal("LONG_GONE_BUT_VISIBLE", operations.GetProperty("currentPresence").GetString());
        Assert.Equal(2, operations.GetProperty("currentDemand").GetProperty("generation").GetInt32());
        Assert.Equal("NOT_READABLE", operations.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Catalog_returns_complete_stable_items_in_demand_id_order_and_rejects_dispatch_scope_queries()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-full-range",
            new DateTimeOffset(2026, 8, 13, 21, 0, 0, TimeSpan.Zero),
            Observation("SL-T09-ONE", "N3-3", "WB-03", "QFN", "WIRE_TO_NITROGEN"),
            Observation("SL-T09-TWO", "N3-8", "WB-08", "BGA", "DIE_ATTACH")));

        var full = await GetCatalogAsync(client);
        Assert.Equal(2, full.Body.GetProperty("count").GetInt32());
        Assert.Equal(
            ["DIE_ATTACH", "WIRE_TO_NITROGEN"],
            full.Body.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("transportDemandKey").GetProperty("workType").GetString())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());

        foreach (var query in new[] { "workType=DIE_ATTACH", "area=N3-3", "vehicle=AGV-01", "map=M1", "station=S1", "cursor=opaque", "page=1" })
        {
            using var rejected = await client.GetAsync($"{CatalogUri}?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal("CATALOG_QUERY_NOT_SUPPORTED", error.RootElement.GetProperty("code").GetString());
        }
    }

    [Ticket01SqlServerFact]
    public async Task Production_host_catalog_updates_leave_consumer_cancellation_and_dispatch_canaries_untouched()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var consumerStateDirectory = Path.Combine(
            Path.GetTempPath(),
            $"mes-ingest-ticket09-consumer-canary-{Guid.NewGuid():N}");
        var cancellationCanaryPath = Path.Combine(
            consumerStateDirectory,
            "cancellation-suppression.json");
        var intentPath = Path.Combine(consumerStateDirectory, "order-intents.json");

        try
        {
            Directory.CreateDirectory(consumerStateDirectory);
            var cancellationCanary = System.Text.Encoding.UTF8.GetBytes(
                """{"transportDemandKey":"WIRE_TO_NITROGEN|SL-CANCELLED","suppressed":true}""");
            await File.WriteAllBytesAsync(cancellationCanaryPath, cancellationCanary);
            var acceptedAt = new DateTimeOffset(2026, 8, 13, 21, 30, 0, TimeSpan.Zero);
            var acceptedDemand = new ExternallyReadableDemandSnapshot(
                "consumer-owned-demand",
                "consumer-owned-series",
                "WIRE_TO_NITROGEN",
                "SL-CONSUMER-OWNED",
                Generation: 1,
                DemandRevision: 4,
                acceptedAt.AddHours(-1),
                acceptedAt.AddMinutes(-1),
                "consumer-poll",
                "consumer-commit",
                new LiveMesFieldSetSnapshot(
                    "N3-3",
                    "WB-03",
                    "焊线2",
                    acceptedAt.AddHours(-2),
                    "QFN"));
            var accepted = new AcceptedDemandSnapshot(12, acceptedAt, acceptedDemand);
            var intent = new OrderIntent(
                "consumer-owned-intent",
                acceptedDemand.DemandId,
                acceptedDemand.DemandRevision,
                acceptedAt,
                OrderIntentState.Pending);
            var consumerStore = new FileReferenceConsumerStore(intentPath);
            await consumerStore.GetOrCreateAcceptanceAsync(accepted, intent);
            await consumerStore.MarkOrderIntentResultUnknownAsync(intent.IdempotencyKey);
            var dispatchCanary = await File.ReadAllBytesAsync(intentPath);

            await ingestor.IngestAsync(SuccessRound(
                "poll-ticket09-consumer-isolation-a",
                acceptedAt.AddMinutes(1),
                Observation("SL-T09-ISOLATED", "N3-3", "WB-03", "QFN-A")));
            var firstCatalog = await GetCatalogAsync(client);
            await ingestor.IngestAsync(SuccessRound(
                "poll-ticket09-consumer-isolation-b",
                acceptedAt.AddMinutes(2),
                Observation("SL-T09-ISOLATED", "N3-8", "WB-08", "QFN-B")));
            var changedCatalog = await GetCatalogAsync(client);

            Assert.NotEqual(firstCatalog.ETag, changedCatalog.ETag);
            Assert.Equal(cancellationCanary, await File.ReadAllBytesAsync(cancellationCanaryPath));
            Assert.Equal(dispatchCanary, await File.ReadAllBytesAsync(intentPath));
            Assert.Equal(
                OrderIntentState.ResultUnknown,
                (await new FileReferenceConsumerStore(intentPath)
                    .GetOrderIntentAsync(intent.IdempotencyKey))!.State);
            AssertDatabaseEvidence(database);
        }
        finally
        {
            if (Directory.Exists(consumerStateDirectory))
            {
                Directory.Delete(consumerStateDirectory, recursive: true);
            }
        }
    }

    private static MesTaskUnionObservation Observation(
        string sublot,
        string area,
        string? eqp,
        string package,
        string workType = "WIRE_TO_NITROGEN") =>
        new(
            workType,
            sublot,
            area,
            eqp,
            "焊线2",
            new DateTimeOffset(2026, 8, 13, 9, 58, 0, TimeSpan.FromHours(8)),
            package);

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket09-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static MesTaskUnionRound NonSuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        MesTaskUnionRoundOutcome outcome,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket09-v1",
            outcome,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static void AssertCatalogExactlyEqual(
        (JsonElement Body, string ETag) expected,
        (JsonElement Body, string ETag) actual)
    {
        Assert.Equal(expected.ETag, actual.ETag);
        Assert.Equal(expected.Body.GetRawText(), actual.Body.GetRawText());
    }

    private async Task<(JsonElement Body, string ETag)> GetCatalogAsync(HttpClient client)
    {
        using var response = await client.GetAsync(CatalogUri);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return (json.RootElement.Clone(), response.Headers.ETag?.ToString() ?? string.Empty);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string uri)
    {
        using var response = await client.GetAsync(uri);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Production));

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
        });

    private void AssertDatabaseEvidence(Ticket01SqlServerDatabase database)
    {
        Assert.False(database.IsLocalDb);
        Assert.Equal(database.ExpectedProductMajor, database.ProductMajor);
        Assert.Equal(database.ExpectedCompatibilityLevel, database.CompatibilityLevel);
        _output.WriteLine(
            $"SQL Server {database.ProductVersion}; compatibility {database.CompatibilityLevel}");
    }
}
