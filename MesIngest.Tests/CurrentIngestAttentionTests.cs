using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class CurrentIngestAttentionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ProtectedWorkType = "WIRE_TO_NITROGEN";
    private const int ProtectionEnterThreshold = 10;
    private const int ProtectionRecoveryStreak = 2;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public CurrentIngestAttentionTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Ticket01SqlServerFact]
    public async Task Four_sources_are_stable_and_only_complete_success_clears_current_items_while_history_remains()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var baselineAt = new DateTimeOffset(2026, 8, 14, 1, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(baselineAt.AddMinutes(30));
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        var baselineRows = BaselineObservations(baselineAt);
        var baseline = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-baseline",
            baselineAt,
            baselineRows));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-restart-authority",
            baselineAt.AddMinutes(1),
            baselineRows));

        var unassigned = new MesTaskUnionObservation(
            WorkType: null,
            Sublot: "SL-TICKET13-UNASSIGNED",
            Area: "A1-1",
            Eqp: "EQP-TICKET13-UNASSIGNED",
            Step: "STEP-TICKET13",
            MesSourceDate: baselineAt.AddMinutes(2),
            Package: "PKG-TICKET13");
        var zeroDrop = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-zero-drop-unassigned",
            baselineAt.AddMinutes(2),
            unassigned));
        var stableUnassigned = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-stable-unassigned",
            baselineAt.AddMinutes(2).AddSeconds(30),
            unassigned));

        await ingestor.IngestAsync(NonSuccessRound(
            "poll-ticket13-failure",
            MesTaskUnionRoundOutcome.Failure,
            baselineAt.AddMinutes(3)));
        var afterFailure = await GetJsonAsync(client, "/api/v2/current-ingest-attention");
        AssertSnapshotFence(afterFailure, stableUnassigned);
        Assert.Equal(5L, afterFailure.GetProperty("exactTotalItemCount").GetInt64());
        AssertAllKindsPresent(afterFailure);
        Assert.Equal(
            "FAILURE",
            FindItem(afterFailure, CurrentIngestAttentionKinds.PollRunFailure)
                .GetProperty("evidence").GetProperty("outcome").GetString());

        var incompleteRound = NonSuccessRound(
            "poll-ticket13-incomplete",
            MesTaskUnionRoundOutcome.Incomplete,
            baselineAt.AddMinutes(4));
        await ingestor.IngestAsync(incompleteRound);
        var evidenceBeforeReplay = await ReadProjectionEvidenceAsync(database.ConnectionString);
        var replay = await ingestor.IngestAsync(incompleteRound);
        Assert.True(replay.IsReplay);
        await Assert.ThrowsAsync<PollTraceConflictException>(() => ingestor.IngestAsync(
            incompleteRound with
            {
                QueryVersion = "mes-task-union-ticket13-conflict",
            }));
        Assert.Equal(
            evidenceBeforeReplay,
            await ReadProjectionEvidenceAsync(database.ConnectionString));
        var current = await GetJsonAsync(client, "/api/v2/current-ingest-attention");
        AssertSnapshotFence(current, stableUnassigned);
        Assert.Equal(
            stableUnassigned.HistoryEpoch!.Value.ToString("D"),
            current.GetProperty("snapshot").GetProperty("historyEpoch").GetString());
        Assert.Equal(5L, current.GetProperty("exactTotalItemCount").GetInt64());
        Assert.Equal(CurrentIngestAttentionOrder.Default, current.GetProperty("order").GetString());
        Assert.Equal(100, current.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, current.GetProperty("pageNumber").GetInt32());
        Assert.Equal(1, current.GetProperty("totalPages").GetInt32());
        AssertAllKindsPresent(current);

        var typeFacets = ReadFacets(current, "types");
        Assert.Equal(2L, typeFacets[CurrentIngestAttentionKinds.SeriesError]);
        Assert.Equal(1L, typeFacets[CurrentIngestAttentionKinds.PollRunFailure]);
        Assert.Equal(1L, typeFacets[CurrentIngestAttentionKinds.TaskTypeProtection]);
        Assert.Equal(1L, typeFacets[CurrentIngestAttentionKinds.UnassignedMesObservation]);
        Assert.Equal(0L, typeFacets[CurrentIngestAttentionKinds.HistoryCleanupFailure]);
        Assert.Equal(0L, typeFacets[CurrentIngestAttentionKinds.StoragePressure]);
        var severityFacets = ReadFacets(current, "severities");
        Assert.Equal(4L, severityFacets[CurrentIngestAttentionSeverities.Error]);
        Assert.Equal(1L, severityFacets[CurrentIngestAttentionSeverities.Warning]);

        var items = current.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(5, items.Length);
        AssertStableAttentionOrder(items);
        Assert.Equal(
            CurrentIngestAttentionKinds.PollRunFailure,
            items[0].GetProperty("kind").GetString());
        var tiedSeriesErrors = items
            .Where(item => item.GetProperty("kind").GetString() == CurrentIngestAttentionKinds.SeriesError)
            .ToArray();
        Assert.Equal(2, tiedSeriesErrors.Length);
        Assert.Equal(
            tiedSeriesErrors[0].GetProperty("occurredAt").GetDateTimeOffset(),
            tiedSeriesErrors[1].GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(
            tiedSeriesErrors.Select(StableIdentity).Order(StringComparer.Ordinal),
            tiedSeriesErrors.Select(StableIdentity));

        AssertSeriesErrorEvidence(tiedSeriesErrors, baseline);
        AssertPollFailureEvidence(
            FindItem(current, CurrentIngestAttentionKinds.PollRunFailure),
            stableUnassigned,
            "poll-ticket13-incomplete",
            "INCOMPLETE");
        AssertTaskProtectionEvidence(
            FindItem(current, CurrentIngestAttentionKinds.TaskTypeProtection),
            stableUnassigned);
        AssertUnassignedEvidence(
            FindItem(current, CurrentIngestAttentionKinds.UnassignedMesObservation),
            stableUnassigned,
            baselineAt.AddMinutes(2));

        Assert.Equal(
            afterFailure.GetProperty("items").EnumerateArray().Select(StableIdentity),
            items.Select(StableIdentity));
        Assert.Equal(
            "poll-ticket13-incomplete",
            FindItem(current, CurrentIngestAttentionKinds.PollRunFailure)
                .GetProperty("evidence").GetProperty("pollTraceId").GetString());

        var page1 = await GetJsonAsync(
            client,
            "/api/v2/current-ingest-attention?pageSize=2&pageNumber=1");
        var page2 = await GetJsonAsync(
            client,
            "/api/v2/current-ingest-attention?pageSize=2&pageNumber=2");
        var page3 = await GetJsonAsync(
            client,
            "/api/v2/current-ingest-attention?pageSize=2&pageNumber=3");
        AssertPage(page1, pageNumber: 1, itemCount: 2);
        AssertPage(page2, pageNumber: 2, itemCount: 2);
        AssertPage(page3, pageNumber: 3, itemCount: 1);
        Assert.Equal(
            items.Select(StableIdentity),
            page1.GetProperty("items").EnumerateArray()
                .Concat(page2.GetProperty("items").EnumerateArray())
                .Concat(page3.GetProperty("items").EnumerateArray())
                .Select(StableIdentity));

        var seriesOnly = await GetJsonAsync(
            client,
            "/api/v2/current-ingest-attention?kind=SERIES_ERROR&severity=ERROR&pageSize=10");
        Assert.Equal(2L, seriesOnly.GetProperty("exactTotalItemCount").GetInt64());
        Assert.Equal(
            [CurrentIngestAttentionKinds.SeriesError],
            seriesOnly.GetProperty("kinds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            [CurrentIngestAttentionSeverities.Error],
            seriesOnly.GetProperty("severities").EnumerateArray().Select(value => value.GetString()));
        Assert.All(
            seriesOnly.GetProperty("items").EnumerateArray(),
            item => Assert.Equal(
                CurrentIngestAttentionKinds.SeriesError,
                item.GetProperty("kind").GetString()));
        var warningOnly = await GetJsonAsync(
            client,
            "/api/v2/current-ingest-attention?severity=WARNING&pageSize=10");
        Assert.Equal(1L, warningOnly.GetProperty("exactTotalItemCount").GetInt64());
        var warningItem = Assert.Single(warningOnly.GetProperty("items").EnumerateArray());
        Assert.Equal(
            CurrentIngestAttentionKinds.TaskTypeProtection,
            warningItem.GetProperty("kind").GetString());
        Assert.Equal(
            CurrentIngestAttentionSeverities.Warning,
            warningItem.GetProperty("severity").GetString());

        var evidenceBeforeReads = await ReadProjectionEvidenceAsync(database.ConnectionString);
        var repeated = await GetJsonAsync(client, "/api/v2/current-ingest-attention");
        Assert.Equal(
            items.Select(StableIdentity),
            repeated.GetProperty("items").EnumerateArray().Select(StableIdentity));
        Assert.Equal(
            items.Select(item => item.GetProperty("evidence").GetRawText()),
            repeated.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("evidence").GetRawText()));
        Assert.Equal(
            evidenceBeforeReads,
            await ReadProjectionEvidenceAsync(database.ConnectionString));

        var healthyRows = baselineRows
            .Select(row => row with { Eqp = "EQP-TICKET13-RECOVERED" })
            .ToArray();
        var recovery = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-complete-recovery",
            baselineAt.AddMinutes(5),
            healthyRows));
        var recovered = await GetJsonAsync(client, "/api/v2/current-ingest-attention");
        AssertSnapshotFence(recovered, recovery);
        Assert.Equal(1L, recovered.GetProperty("exactTotalItemCount").GetInt64());
        var remaining = Assert.Single(recovered.GetProperty("items").EnumerateArray());
        Assert.Equal(CurrentIngestAttentionKinds.TaskTypeProtection, remaining.GetProperty("kind").GetString());
        Assert.Equal("RECOVERING", remaining.GetProperty("evidence").GetProperty("phase").GetString());
        var recoveredTypes = ReadFacets(recovered, "types");
        Assert.Equal(0L, recoveredTypes[CurrentIngestAttentionKinds.SeriesError]);
        Assert.Equal(0L, recoveredTypes[CurrentIngestAttentionKinds.PollRunFailure]);
        Assert.Equal(1L, recoveredTypes[CurrentIngestAttentionKinds.TaskTypeProtection]);
        Assert.Equal(0L, recoveredTypes[CurrentIngestAttentionKinds.UnassignedMesObservation]);
        Assert.Equal(0L, recoveredTypes[CurrentIngestAttentionKinds.HistoryCleanupFailure]);
        Assert.Equal(0L, recoveredTypes[CurrentIngestAttentionKinds.StoragePressure]);
        var recoveredSeverities = ReadFacets(recovered, "severities");
        Assert.Equal(0L, recoveredSeverities[CurrentIngestAttentionSeverities.Error]);
        Assert.Equal(1L, recoveredSeverities[CurrentIngestAttentionSeverities.Warning]);

        var authorityPending = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-protection-authority-pending",
            baselineAt.AddMinutes(6),
            healthyRows));
        var pending = await GetJsonAsync(client, "/api/v2/current-ingest-attention");
        AssertSnapshotFence(pending, authorityPending);
        var pendingItem = Assert.Single(pending.GetProperty("items").EnumerateArray());
        Assert.Equal(
            TaskTypeProtectionPhaseContract.AuthorityPending,
            pendingItem.GetProperty("evidence").GetProperty("phase").GetString());

        var clearedProtection = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-protection-cleared",
            baselineAt.AddMinutes(7),
            healthyRows));
        var cleared = await GetJsonAsync(client, "/api/v2/current-ingest-attention");
        AssertSnapshotFence(cleared, clearedProtection);
        Assert.Equal(0L, cleared.GetProperty("exactTotalItemCount").GetInt64());
        Assert.Empty(cleared.GetProperty("items").EnumerateArray());
        using (var protectionResponse = await client.GetAsync(
                   $"/api/v2/task-type-protections/{ProtectedWorkType}"))
        {
            protectionResponse.EnsureSuccessStatusCode();
            using var protection = JsonDocument.Parse(
                await protectionResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                TaskTypeProtectionPhaseContract.Monitoring,
                protection.RootElement.GetProperty("phase").GetString());
            Assert.False(protection.RootElement.GetProperty("isCurrentAttention").GetBoolean());
            Assert.Contains(
                protection.RootElement.GetProperty("events").EnumerateArray(),
                item => item.GetProperty("eventType").GetString()
                    == TaskTypeProtectionEventCode.Cleared);
        }

        var history = await GetJsonAsync(
            client,
            "/api/v2/error-search?state=ENDED&window=ALL_HISTORY&pageSize=10");
        Assert.Equal(2L, history.GetProperty("totalSeriesCount").GetInt64());
        var historicalSeries = history.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, historicalSeries.Length);
        Assert.All(historicalSeries, item =>
        {
            Assert.Equal(ErrorSearchActivityStates.Ended, item.GetProperty("activityState").GetString());
            Assert.Contains(
                item.GetProperty("matchedErrors").EnumerateArray(),
                error => error.GetProperty("code").GetString() == "REQUIRED_MES_FIELD_MISSING");
        });

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Routes_return_stable_validation_and_projection_unavailable_errors()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(new AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 14, 4, 0, 0, TimeSpan.Zero)));
        using var client = factory.CreateClient();

        await AssertErrorAsync(
            client,
            "/api/v2/current-ingest-attention",
            System.Net.HttpStatusCode.Conflict,
            CurrentIngestAttentionErrorCodes.ProjectionNotAvailable);
        await AssertErrorAsync(
            client,
            "/api/v2/watch-overview",
            System.Net.HttpStatusCode.Conflict,
            WatchOverviewErrorCodes.ProjectionNotAvailable);
        await AssertErrorAsync(
            client,
            "/api/v2/current-ingest-attention?pageSize=0",
            System.Net.HttpStatusCode.BadRequest,
            CurrentIngestAttentionErrorCodes.InvalidQuery);
        await AssertErrorAsync(
            client,
            "/api/v2/current-ingest-attention?kind=UNKNOWN",
            System.Net.HttpStatusCode.BadRequest,
            CurrentIngestAttentionErrorCodes.InvalidQuery);
        await AssertErrorAsync(
            client,
            "/api/v2/watch-overview?area=",
            System.Net.HttpStatusCode.BadRequest,
            WatchOverviewErrorCodes.InvalidQuery);
    }

    private static MesTaskUnionObservation[] BaselineObservations(DateTimeOffset completedAt) =>
        Enumerable.Range(1, ProtectionEnterThreshold)
            .Select(index => new MesTaskUnionObservation(
                ProtectedWorkType,
                $"SL-TICKET13-{index:00}",
                Area: "A1-1",
                Eqp: index <= 2 ? null : "EQP-TICKET13",
                Step: "STEP-TICKET13",
                MesSourceDate: completedAt.AddMinutes(-index),
                Package: "PKG-TICKET13"))
            .ToArray();

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket13-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static MesTaskUnionRound NonSuccessRound(
        string pollTraceId,
        MesTaskUnionRoundOutcome outcome,
        DateTimeOffset completedAt) =>
        new(
            pollTraceId,
            "mes-task-union-ticket13-v1",
            outcome,
            completedAt.AddSeconds(-2),
            completedAt,
            Array.Empty<MesTaskUnionObservation>());

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string uri)
    {
        using var response = await client.GetAsync(uri);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private static async Task AssertErrorAsync(
        HttpClient client,
        string uri,
        System.Net.HttpStatusCode status,
        string code)
    {
        using var response = await client.GetAsync(uri);
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
    }

    private static async Task<string> ReadProjectionEvidenceAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONCAT(
                (SELECT COUNT_BIG(*) FROM mesingest.PollTraces), N'|',
                (SELECT COUNT_BIG(*) FROM mesingest.ProjectionCommits), N'|',
                (SELECT COUNT_BIG(*) FROM mesingest.DemandSeriesErrorPeriods), N'|',
                (SELECT COUNT_BIG(*) FROM mesingest.ProjectionCommitUnassignedObservationFacts), N'|',
                (SELECT COUNT_BIG(*) FROM mesingest.UnassignedMesObservationEvents), N'|',
                (SELECT COUNT_BIG(*) FROM mesingest.TaskTypeProtectionEvents));
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static void AssertSnapshotFence(JsonElement root, RoundCommitReceipt receipt)
    {
        var snapshot = root.GetProperty("snapshot");
        Assert.Equal(receipt.ProjectionCommitId, snapshot.GetProperty("projectionCommitId").GetString());
        Assert.Equal(receipt.ProjectionSequence, snapshot.GetProperty("projectionSequence").GetInt64());
        Assert.Equal(receipt.PollTraceId, snapshot.GetProperty("pollTraceId").GetString());
        Assert.True(snapshot.GetProperty("pollTraceHighWater").GetInt64() > 0);
        _ = snapshot.GetProperty("snapshotAsOf").GetDateTimeOffset();
    }

    private static void AssertAllKindsPresent(JsonElement root)
    {
        var kinds = root.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("kind").GetString())
            .ToHashSet(StringComparer.Ordinal);
        var activeKinds = ReadFacets(root, "types")
            .Where(item => item.Value > 0)
            .Select(item => item.Key);
        Assert.Equal(
            activeKinds.Order(StringComparer.Ordinal),
            kinds.Order(StringComparer.Ordinal));
    }

    private static IReadOnlyDictionary<string, long> ReadFacets(JsonElement root, string name) =>
        root.GetProperty("facets").GetProperty(name).EnumerateArray().ToDictionary(
            facet => facet.GetProperty("value").GetString()!,
            facet => facet.GetProperty("itemCount").GetInt64(),
            StringComparer.Ordinal);

    private static JsonElement FindItem(JsonElement root, string kind) =>
        Assert.Single(root.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == kind));

    private static string StableIdentity(JsonElement item) =>
        item.GetProperty("stableIdentity").GetString()!;

    private static void AssertStableAttentionOrder(IReadOnlyList<JsonElement> items)
    {
        var actual = items.Select(item => new AttentionOrderKey(
            StableIdentity(item),
            item.GetProperty("severity").GetString() == CurrentIngestAttentionSeverities.Error ? 0 : 1,
            item.GetProperty("occurredAt").GetDateTimeOffset())).ToArray();
        Assert.Equal(
            actual
                .OrderBy(item => item.SeverityRank)
                .ThenByDescending(item => item.OccurredAt)
                .ThenBy(item => item.StableIdentity, StringComparer.Ordinal),
            actual);
    }

    private static void AssertSeriesErrorEvidence(
        IReadOnlyList<JsonElement> items,
        RoundCommitReceipt baseline)
    {
        Assert.All(items, item =>
        {
            Assert.Equal(CurrentIngestAttentionSeverities.Error, item.GetProperty("severity").GetString());
            Assert.Equal("REQUIRED_MES_FIELD_MISSING", item.GetProperty("errorCode").GetString());
            Assert.Equal("EQP", item.GetProperty("subjectKind").GetString());
            var seriesId = item.GetProperty("seriesId").GetString()!;
            var evidence = item.GetProperty("evidence");
            var demandId = evidence.GetProperty("demandId").GetString()!;
            Assert.Equal($"DEMAND:{demandId}", item.GetProperty("target").GetString());
            Assert.Equal(
                $"{seriesId}:REQUIRED_MES_FIELD_MISSING:DEMAND:{demandId}:EQP",
                StableIdentity(item));
            Assert.Equal(baseline.ProjectionCommitId, evidence.GetProperty("projectionCommitId").GetString());
            Assert.Equal(baseline.ProjectionSequence, evidence.GetProperty("projectionSequence").GetInt64());
            Assert.Equal(baseline.PollTraceId, evidence.GetProperty("pollTraceId").GetString());
            Assert.Equal(seriesId, evidence.GetProperty("seriesId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(evidence.GetProperty("evidenceId").GetString()));

            var navigation = item.GetProperty("navigation");
            AssertNavigation(navigation, OverviewNavigationTargets.ErrorSearch);
            Assert.Equal(seriesId, navigation.GetProperty("seriesId").GetString());
            Assert.Equal(ErrorSearchWindowKinds.AllHistory, navigation.GetProperty("errorWindow").GetString());
            Assert.Equal(
                [ErrorSearchActivityStates.Active],
                navigation.GetProperty("errorActivityStates").EnumerateArray()
                    .Select(value => value.GetString()));
        });
    }

    private static void AssertPollFailureEvidence(
        JsonElement item,
        RoundCommitReceipt fence,
        string pollTraceId,
        string outcome)
    {
        Assert.Equal(CurrentIngestAttentionSeverities.Error, item.GetProperty("severity").GetString());
        Assert.Equal("POLL_RUN_FAILURE", StableIdentity(item));
        Assert.Equal("POLL_TRACE", item.GetProperty("subjectKind").GetString());
        var evidence = item.GetProperty("evidence");
        Assert.Equal(fence.ProjectionCommitId, evidence.GetProperty("projectionCommitId").GetString());
        Assert.Equal(fence.ProjectionSequence, evidence.GetProperty("projectionSequence").GetInt64());
        Assert.Equal(pollTraceId, evidence.GetProperty("pollTraceId").GetString());
        Assert.True(evidence.GetProperty("pollTraceSequence").GetInt64() > 0);
        Assert.Equal(outcome, evidence.GetProperty("outcome").GetString());
        var navigation = item.GetProperty("navigation");
        AssertNavigation(navigation, OverviewNavigationTargets.PollTrace);
        Assert.Equal(pollTraceId, navigation.GetProperty("pollTraceId").GetString());
    }

    private static void AssertTaskProtectionEvidence(JsonElement item, RoundCommitReceipt fence)
    {
        Assert.Equal(CurrentIngestAttentionSeverities.Warning, item.GetProperty("severity").GetString());
        Assert.Equal($"TASK_TYPE_PROTECTION:{ProtectedWorkType}", StableIdentity(item));
        Assert.Equal(ProtectedWorkType, item.GetProperty("workType").GetString());
        Assert.Equal("WORK_TYPE", item.GetProperty("subjectKind").GetString());
        var evidence = item.GetProperty("evidence");
        Assert.Equal(fence.ProjectionCommitId, evidence.GetProperty("projectionCommitId").GetString());
        Assert.Equal(fence.ProjectionSequence, evidence.GetProperty("projectionSequence").GetInt64());
        Assert.Equal(fence.PollTraceId, evidence.GetProperty("pollTraceId").GetString());
        Assert.Equal("PAUSED_ZERO_DROP", evidence.GetProperty("phase").GetString());
        Assert.False(string.IsNullOrWhiteSpace(evidence.GetProperty("evidenceId").GetString()));
        var navigation = item.GetProperty("navigation");
        AssertNavigation(navigation, OverviewNavigationTargets.TaskTypeProtection);
        Assert.Equal(ProtectedWorkType, navigation.GetProperty("workType").GetString());
    }

    private static void AssertUnassignedEvidence(
        JsonElement item,
        RoundCommitReceipt fence,
        DateTimeOffset episodeOccurredAt)
    {
        Assert.Equal(CurrentIngestAttentionSeverities.Error, item.GetProperty("severity").GetString());
        Assert.StartsWith("UNASSIGNED_MES_OBSERVATION:", StableIdentity(item));
        Assert.Equal("UNASSIGNED_OBSERVATION_SET", item.GetProperty("subjectKind").GetString());
        Assert.Equal(episodeOccurredAt, item.GetProperty("occurredAt").GetDateTimeOffset());
        var evidence = item.GetProperty("evidence");
        Assert.Equal(fence.ProjectionCommitId, evidence.GetProperty("projectionCommitId").GetString());
        Assert.Equal(fence.ProjectionSequence, evidence.GetProperty("projectionSequence").GetInt64());
        Assert.Equal(fence.PollTraceId, evidence.GetProperty("pollTraceId").GetString());
        Assert.Equal(1, evidence.GetProperty("observationCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, evidence.GetProperty("observationOrdinal").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(evidence.GetProperty("contentDigest").GetString()));
        var navigation = item.GetProperty("navigation");
        AssertNavigation(navigation, OverviewNavigationTargets.PollTrace);
        Assert.Equal(fence.PollTraceId, navigation.GetProperty("pollTraceId").GetString());
    }

    private static void AssertNavigation(JsonElement navigation, string target)
    {
        Assert.Equal(target, navigation.GetProperty("target").GetString());
        Assert.Equal(1, navigation.GetProperty("pageNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, navigation.GetProperty("cursor").ValueKind);
    }

    private static void AssertPage(JsonElement page, int pageNumber, int itemCount)
    {
        Assert.Equal(5L, page.GetProperty("exactTotalItemCount").GetInt64());
        Assert.Equal(2, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(pageNumber, page.GetProperty("pageNumber").GetInt32());
        Assert.Equal(3, page.GetProperty("totalPages").GetInt32());
        Assert.Equal(itemCount, page.GetProperty("items").GetArrayLength());
    }

    private WebApplicationFactory<Program> CreateFactory(AdjustableTimeProvider clock) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseProductionSqlApiTestHost();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__SnapshotSource"] = MesIngestHostOptions.OracleRoundSource,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "true",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
            [$"{MesIngestHostOptions.SectionName}__ZeroDropEnterThreshold"] = ProtectionEnterThreshold.ToString(),
            [$"{MesIngestHostOptions.SectionName}__ZeroDropClearStreak"] = ProtectionRecoveryStreak.ToString(),
        });

    private void AssertDatabaseEvidence(Ticket01SqlServerDatabase database)
    {
        Assert.False(database.IsLocalDb);
        Assert.Equal(database.ExpectedProductMajor, database.ProductMajor);
        Assert.Equal(database.ExpectedCompatibilityLevel, database.CompatibilityLevel);
        Assert.InRange(database.EngineEdition, 1, 4);
        _output.WriteLine(
            $"SQL Server {database.ProductVersion}; compatibility {database.CompatibilityLevel}");
    }

    private sealed record AttentionOrderKey(
        string StableIdentity,
        int SeverityRank,
        DateTimeOffset OccurredAt);
}
