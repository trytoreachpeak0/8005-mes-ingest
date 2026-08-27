using System.Net;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class WatchOverviewSnapshotTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WatchOverviewSnapshotTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Ticket01SqlServerFact]
    public async Task Overview_returns_exact_same_commit_summaries_and_explicit_drill_intents()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var completedAt = new DateTimeOffset(2026, 8, 13, 2, 0, 2, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(completedAt.AddMinutes(1));
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        var receipt = await ingestor.IngestAsync(new MesTaskUnionRound(
            "poll-ticket14-basic",
            "mes-task-union-ticket14-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            [ValidObservation("WIRE_TO_NITROGEN", "SL-TICKET14-A", "A1-1", completedAt)]));

        using var response = await client.GetAsync("/api/v2/watch-overview?area=A1-1");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        Assert.Equal(receipt.ProjectionCommitId, root.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(receipt.ProjectionSequence, root.GetProperty("snapshot").GetProperty("projectionSequence").GetInt64());
        Assert.Equal(
            receipt.HistoryEpoch!.Value.ToString("D"),
            root.GetProperty("snapshot").GetProperty("historyEpoch").GetString());
        Assert.Equal(1, root.GetProperty("series").GetProperty("exactTotalSeriesCount").GetInt64());
        Assert.Equal(1, root.GetProperty("readability").GetProperty("exactTotalDemandGenerationCount").GetInt64());
        Assert.Equal(1, root.GetProperty("readability").GetProperty("readableCount").GetInt64());
        Assert.Equal(0, root.GetProperty("errors").GetProperty("activeSeriesCount").GetInt64());
        Assert.Equal(0, root.GetProperty("attention").GetProperty("exactTotalItemCount").GetInt64());

        Assert.Equal(1, root.GetProperty("series").GetProperty("trackingCount").GetInt64());
        Assert.Equal(0, root.GetProperty("series").GetProperty("archivedCount").GetInt64());
        Assert.Equal(0, root.GetProperty("series").GetProperty("goneCount").GetInt64());
        Assert.Equal(0, root.GetProperty("series").GetProperty("longGoneButVisibleCount").GetInt64());
        Assert.Equal(0, root.GetProperty("readability").GetProperty("notReadableCount").GetInt64());
        Assert.Equal(0, root.GetProperty("errors").GetProperty("prior7DaysSeriesCount").GetInt64());
        Assert.Equal(completedAt, root.GetProperty("snapshot").GetProperty("projectionCommittedAt").GetDateTimeOffset());
        Assert.Equal(receipt.PollTraceId, root.GetProperty("snapshot").GetProperty("pollTraceId").GetString());
        Assert.Equal(clock.GetUtcNow(), root.GetProperty("snapshot").GetProperty("snapshotAsOf").GetDateTimeOffset());
        Assert.Equal(NewMesIngestContract.Version, root.GetProperty("snapshot").GetProperty("contractVersion").GetString());

        var seriesIntent = root.GetProperty("series").GetProperty("navigation");
        Assert.Equal("DEMAND_SERIES", seriesIntent.GetProperty("target").GetString());
        Assert.Equal(1, seriesIntent.GetProperty("pageNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, seriesIntent.GetProperty("cursor").ValueKind);
        Assert.Equal("A1-1", Assert.Single(seriesIntent.GetProperty("mesAreas").EnumerateArray()).GetString());
        AssertExplicitFirstPageIntent(
            root.GetProperty("readability").GetProperty("notReadableNavigation"),
            OverviewNavigationTargets.ReadabilityAudit);
        AssertExplicitFirstPageIntent(
            root.GetProperty("errors").GetProperty("activeNavigation"),
            OverviewNavigationTargets.ErrorSearch);
        Assert.Equal(
            [ErrorSearchActivityStates.Active],
            root.GetProperty("errors").GetProperty("activeNavigation")
                .GetProperty("errorActivityStates").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            ErrorSearchWindowKinds.Last7Days,
            root.GetProperty("errors").GetProperty("activeNavigation").GetProperty("errorWindow").GetString());
        AssertExplicitFirstPageIntent(
            root.GetProperty("attention").GetProperty("navigation"),
            OverviewNavigationTargets.CurrentIngestAttention);
    }

    [Ticket01SqlServerFact]
    public async Task Area_scope_changes_only_series_and_readability_and_never_leaks_profile_state()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var completedAt = new DateTimeOffset(2026, 8, 14, 1, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(completedAt.AddMinutes(1));
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-area-scope",
            completedAt,
            ValidObservation("WIRE_TO_NITROGEN", "SL-TICKET14-AREA-A", "A1-1", completedAt),
            ValidObservation("WIRE_TO_NITROGEN", "SL-TICKET14-AREA-B", "B2-2", completedAt) with
            {
                Eqp = null,
            },
            ValidObservation("WIRE_TO_NITROGEN", "SL-TICKET07-AREA-CASE", "a1-1", completedAt)));

        using var all = await ReadOverviewAsync(client, "/api/v2/watch-overview");
        using var areaA = await ReadOverviewAsync(client, "/api/v2/watch-overview?area=A1-1");
        using var areaB = await ReadOverviewAsync(client, "/api/v2/watch-overview?area=B2-2");

        AssertSummaryCounts(all.RootElement, 3, 3, 1, 2);
        AssertSummaryCounts(areaA.RootElement, 1, 1, 1, 0);
        AssertSummaryCounts(areaB.RootElement, 1, 1, 0, 1);

        var allRevision = all.RootElement.GetProperty("snapshot").GetProperty("catalogRevision").GetInt64();
        Assert.Equal(
            allRevision,
            areaA.RootElement.GetProperty("snapshot").GetProperty("catalogRevision").GetInt64());
        Assert.Equal(
            allRevision,
            areaB.RootElement.GetProperty("snapshot").GetProperty("catalogRevision").GetInt64());

        var globalErrors = all.RootElement.GetProperty("errors").GetRawText();
        var globalAttention = all.RootElement.GetProperty("attention").GetRawText();
        Assert.Equal(globalErrors, areaA.RootElement.GetProperty("errors").GetRawText());
        Assert.Equal(globalErrors, areaB.RootElement.GetProperty("errors").GetRawText());
        Assert.Equal(globalAttention, areaA.RootElement.GetProperty("attention").GetRawText());
        Assert.Equal(globalAttention, areaB.RootElement.GetProperty("attention").GetRawText());
        Assert.Equal(2, all.RootElement.GetProperty("errors").GetProperty("activeSeriesCount").GetInt64());
        Assert.Equal(2, all.RootElement.GetProperty("attention").GetProperty("exactTotalItemCount").GetInt64());

        foreach (var document in new[] { all, areaA, areaB })
        {
            var propertyNames = EnumeratePropertyNames(document.RootElement).ToArray();
            Assert.DoesNotContain(
                propertyNames,
                name => name.Contains("profile", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("fileStatus", propertyNames, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("fileState", propertyNames, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("updatedAt", propertyNames, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("localUpdatedAt", propertyNames, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Ticket01SqlServerFact]
    public async Task Readability_summary_counts_every_retained_demand_generation_in_its_trusted_area()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var firstSeenAt = new DateTimeOffset(2026, 8, 14, 2, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(firstSeenAt.AddHours(13));
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var areaAObservation = ValidObservation(
            "WIRE_TO_NITROGEN",
            "SL-TICKET14-GENERATIONS",
            "A1-1",
            firstSeenAt);

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-generations-seed",
            firstSeenAt,
            areaAObservation));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-generations-authority",
            firstSeenAt.AddMinutes(1),
            areaAObservation));
        var goneAt = firstSeenAt.AddMinutes(2);
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-generations-gone",
            goneAt));
        var archivedAt = goneAt.AddHours(12);
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-generations-archive",
            archivedAt));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-generations-reappear",
            archivedAt.AddMinutes(1),
            ValidObservation(
                "WIRE_TO_NITROGEN",
                "SL-TICKET14-GENERATIONS",
                "B2-2",
                archivedAt.AddMinutes(1)),
            ValidObservation(
                "WIRE_TO_NITROGEN",
                "SL-TICKET14-UNTRUSTED-AREA",
                "A1-1",
                archivedAt.AddMinutes(1)),
            ValidObservation(
                "WIRE_TO_NITROGEN",
                "SL-TICKET14-UNTRUSTED-AREA",
                "A1-1",
                archivedAt.AddMinutes(1))));

        using var all = await ReadOverviewAsync(client, "/api/v2/watch-overview");
        using var areaA = await ReadOverviewAsync(client, "/api/v2/watch-overview?area=A1-1");
        using var areaB = await ReadOverviewAsync(client, "/api/v2/watch-overview?area=B2-2");
        using var auditAll = await ReadJsonAsync(client, "/api/v2/readability-audit?pageSize=20");
        using var auditAreaA = await ReadJsonAsync(
            client,
            "/api/v2/readability-audit?area=A1-1&pageSize=20");
        using var auditAreaB = await ReadJsonAsync(
            client,
            "/api/v2/readability-audit?area=B2-2&pageSize=20");

        AssertSummaryCounts(all.RootElement, 2, 3, 0, 3);
        AssertSummaryCounts(areaA.RootElement, 0, 1, 0, 1);
        AssertSummaryCounts(areaB.RootElement, 1, 1, 0, 1);
        Assert.Equal(
            auditAll.RootElement.GetProperty("exactTotalDemandCount").GetInt64(),
            all.RootElement.GetProperty("readability")
                .GetProperty("exactTotalDemandGenerationCount").GetInt64());
        Assert.Equal(
            auditAreaA.RootElement.GetProperty("exactTotalDemandCount").GetInt64(),
            areaA.RootElement.GetProperty("readability")
                .GetProperty("exactTotalDemandGenerationCount").GetInt64());
        Assert.Equal(
            auditAreaB.RootElement.GetProperty("exactTotalDemandCount").GetInt64(),
            areaB.RootElement.GetProperty("readability")
                .GetProperty("exactTotalDemandGenerationCount").GetInt64());
    }

    [Ticket01SqlServerFact]
    public async Task Recent_error_summary_uses_read_time_and_a_half_open_seven_day_window()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var firstErrorAt = new DateTimeOffset(2026, 8, 14, 3, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(firstErrorAt.AddMinutes(2));
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var recovered = ValidObservation(
            "WIRE_TO_NITROGEN",
            "SL-TICKET14-OLD-ERROR",
            "A1-1",
            firstErrorAt);

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-old-error-open",
            firstErrorAt,
            recovered with { Eqp = null }));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-old-error-close",
            firstErrorAt.AddMinutes(1),
            recovered));

        clock.SetUtcNow(firstErrorAt.AddDays(7).AddMinutes(1));
        using (var aged = await ReadOverviewAsync(client, "/api/v2/watch-overview"))
        {
            Assert.Equal(
                0,
                aged.RootElement.GetProperty("errors").GetProperty("activeSeriesCount").GetInt64());
            Assert.Equal(
                0,
                aged.RootElement.GetProperty("errors").GetProperty("prior7DaysSeriesCount").GetInt64());
        }

        var upperBoundary = clock.GetUtcNow();
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-upper-boundary-error",
            upperBoundary,
            recovered,
            ValidObservation(
                "WIRE_TO_NITROGEN",
                "SL-TICKET14-UPPER-BOUNDARY-ERROR",
                "A1-1",
                upperBoundary) with { Eqp = null }));

        using var atUpperBoundary = await ReadOverviewAsync(client, "/api/v2/watch-overview");
        Assert.Equal(
            1,
            atUpperBoundary.RootElement.GetProperty("errors").GetProperty("activeSeriesCount").GetInt64());
        Assert.Equal(
            0,
            atUpperBoundary.RootElement.GetProperty("errors")
                .GetProperty("prior7DaysSeriesCount").GetInt64());
    }

    [Ticket01SqlServerFact]
    public async Task Recent_activity_uses_real_transitions_a_strict_24_hour_window_and_stable_top_five()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var fromUtc = now.AddHours(-24);
        var clock = new AdjustableTimeProvider(now);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        var oldObservation = ValidObservation(
            "WIRE_TO_NITROGEN",
            "SL-TICKET14-ACTIVITY-OLD",
            "A1-1",
            fromUtc.AddTicks(-1));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-activity-old",
            fromUtc.AddTicks(-1),
            oldObservation));

        using (var empty = await ReadOverviewAsync(client, "/api/v2/watch-overview"))
        {
            Assert.Empty(empty.RootElement.GetProperty("recentActivity").EnumerateArray());
            Assert.Equal(
                WatchOverviewRecentActivityStates.NoRecentHighlights,
                empty.RootElement.GetProperty("recentActivityState").GetString());
            Assert.Equal(
                WatchOverviewRecentActivityStates.NoRecentHighlightsMessage,
                empty.RootElement.GetProperty("emptyStateMessage").GetString());
            Assert.DoesNotContain(
                EnumeratePropertyNames(empty.RootElement),
                name => name.Contains("health", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("健康", empty.RootElement.GetRawText(), StringComparison.Ordinal);
        }

        var boundaryObservation = ValidObservation(
            "WIRE_TO_NITROGEN",
            "SL-TICKET14-ACTIVITY-BOUNDARY",
            "A1-1",
            fromUtc);
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-activity-boundary",
            fromUtc,
            oldObservation,
            boundaryObservation));

        using (var boundary = await ReadOverviewAsync(client, "/api/v2/watch-overview"))
        {
            var boundaryActivity = Assert.Single(
                boundary.RootElement.GetProperty("recentActivity").EnumerateArray());
            Assert.Equal("DEMAND_SERIES_STARTED", boundaryActivity.GetProperty("eventType").GetString());
            Assert.Equal(fromUtc, boundaryActivity.GetProperty("occurredAt").GetDateTimeOffset());
        }

        var batchAt = now.AddHours(-2);
        var batch = Enumerable.Range(0, 6)
            .Select(index => ValidObservation(
                "WIRE_TO_NITROGEN",
                $"SL-TICKET14-ACTIVITY-{index}",
                "A1-1",
                batchAt))
            .ToArray();
        var current = new[] { oldObservation, boundaryObservation }
            .Concat(batch)
            .ToArray();
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-activity-batch",
            batchAt,
            current));

        var noiseAt = now.AddHours(-1);
        var noisy = current.ToArray();
        noisy[2] = noisy[2] with { Eqp = "EQP-NOISE" };
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-activity-noise",
            noiseAt,
            noisy));

        var upperBoundaryObservation = ValidObservation(
            "WIRE_TO_NITROGEN",
            "SL-TICKET14-ACTIVITY-UPPER-BOUNDARY",
            "A1-1",
            now);
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-activity-upper-boundary",
            now,
            noisy.Concat([upperBoundaryObservation]).ToArray()));

        var expectedStartIds = await ReadSeriesStartedEventIdsAsync(
            database.ConnectionString,
            batchAt);
        Assert.Equal(6, expectedStartIds.Count);
        Assert.True(await EventExistsAsync(
            database.ConnectionString,
            "MES_FIELD_CHANGED",
            noiseAt));

        using var overview = await ReadOverviewAsync(client, "/api/v2/watch-overview");
        var activities = overview.RootElement.GetProperty("recentActivity")
            .EnumerateArray()
            .Select(item => new
            {
                EventId = item.GetProperty("eventId").GetString()!,
                EventType = item.GetProperty("eventType").GetString()!,
                OccurredAt = item.GetProperty("occurredAt").GetDateTimeOffset(),
            })
            .ToArray();

        Assert.Equal(5, activities.Length);
        Assert.Equal(expectedStartIds.Take(5), activities.Select(item => item.EventId));
        Assert.All(activities, item =>
        {
            Assert.Equal("DEMAND_SERIES_STARTED", item.EventType);
            Assert.Equal(batchAt, item.OccurredAt);
        });
        Assert.DoesNotContain(activities, item => item.EventType == "MES_FIELD_CHANGED");
        Assert.DoesNotContain(activities, item => item.EventType == "TRANSPORT_DEMAND_CREATED");
        Assert.DoesNotContain(activities, item => item.OccurredAt == now);
        Assert.Equal(
            WatchOverviewRecentActivityStates.HasRecentHighlights,
            overview.RootElement.GetProperty("recentActivityState").GetString());
        Assert.Equal(JsonValueKind.Null, overview.RootElement.GetProperty("emptyStateMessage").ValueKind);
    }

    [Ticket01SqlServerFact]
    public async Task Concurrent_commit_does_not_wait_for_overview_and_the_response_is_wholly_old()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var now = new DateTimeOffset(2026, 8, 14, 15, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var observer = new GatedOverviewReadBoundaryObserver();
        await using var factory = CreateFactory(clock, observer);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var observationA = ValidObservation(
            "WIRE_TO_NITROGEN",
            "SL-TICKET14-CONCURRENT-A",
            "A1-1",
            now.AddMinutes(-2));

        var receiptA = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket14-concurrent-a",
            now.AddMinutes(-2),
            observationA));
        var pendingRead = client.GetAsync("/api/v2/watch-overview");
        var selected = await observer.FenceSelected.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(receiptA.ProjectionCommitId, selected.ProjectionCommitId);
        Assert.Equal(receiptA.ProjectionSequence, selected.ProjectionSequence);

        RoundCommitReceipt receiptB;
        try
        {
            var observationB = ValidObservation(
                "WIRE_TO_NITROGEN",
                "SL-TICKET14-CONCURRENT-B",
                "B2-2",
                now.AddMinutes(-1)) with
            {
                Eqp = null,
            };
            var pendingWrite = ingestor.IngestAsync(SuccessRound(
                    "poll-ticket14-concurrent-b",
                    now.AddMinutes(-1),
                    observationA,
                    observationB));
            receiptB = await pendingWrite.WaitAsync(TimeSpan.FromSeconds(10));
            observer.Release();
        }
        finally
        {
            observer.Release();
        }

        using var firstResponse = await pendingRead.WaitAsync(TimeSpan.FromSeconds(10));
        using var concurrentOverview = await ReadOverviewResponseAsync(firstResponse);
        Assert.Equal(
            receiptA.ProjectionCommitId,
            concurrentOverview.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(
            receiptA.ProjectionSequence,
            concurrentOverview.RootElement.GetProperty("snapshot").GetProperty("projectionSequence").GetInt64());
        Assert.Equal(
            1,
            concurrentOverview.RootElement.GetProperty("snapshot").GetProperty("pollTraceHighWater").GetInt64());
        AssertSummaryCounts(concurrentOverview.RootElement, 1, 1, 1, 0);
        Assert.Equal(
            0,
            concurrentOverview.RootElement.GetProperty("errors").GetProperty("activeSeriesCount").GetInt64());
        Assert.Equal(
            0,
            concurrentOverview.RootElement.GetProperty("errors")
                .GetProperty("prior7DaysSeriesCount").GetInt64());
        Assert.Equal(
            0,
            concurrentOverview.RootElement.GetProperty("attention")
                .GetProperty("exactTotalItemCount").GetInt64());
        Assert.All(
            concurrentOverview.RootElement.GetProperty("recentActivity").EnumerateArray(),
            item => Assert.Equal(
                receiptA.ProjectionCommitId,
                item.GetProperty("projectionCommitId").GetString()));

        using var newOverview = await ReadOverviewAsync(client, "/api/v2/watch-overview");
        Assert.Equal(
            receiptB.ProjectionCommitId,
            newOverview.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(
            receiptB.ProjectionSequence,
            newOverview.RootElement.GetProperty("snapshot").GetProperty("projectionSequence").GetInt64());
        Assert.Equal(2, newOverview.RootElement.GetProperty("snapshot").GetProperty("pollTraceHighWater").GetInt64());
        AssertSummaryCounts(newOverview.RootElement, 2, 2, 1, 1);
        Assert.Equal(1, newOverview.RootElement.GetProperty("errors").GetProperty("activeSeriesCount").GetInt64());
        Assert.Equal(1, newOverview.RootElement.GetProperty("errors").GetProperty("prior7DaysSeriesCount").GetInt64());
        Assert.Equal(1, newOverview.RootElement.GetProperty("attention").GetProperty("exactTotalItemCount").GetInt64());
    }

    [Ticket01SqlServerFact]
    public async Task Recent_activity_current_projection_tracks_poll_failure_and_recovery_without_a_history_rebuild()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var now = new DateTimeOffset(2026, 8, 14, 18, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var observation = ValidObservation(
            "WIRE_TO_NITROGEN",
            "SL-TICKET07-POLL-ACTIVITY",
            "A1-1",
            now.AddMinutes(-3));

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-activity-baseline",
            now.AddMinutes(-3),
            observation));
        await ingestor.IngestAsync(new MesTaskUnionRound(
            "poll-ticket07-activity-failure",
            "mes-task-union-ticket14-v1",
            MesTaskUnionRoundOutcome.Failure,
            now.AddMinutes(-2).AddSeconds(-2),
            now.AddMinutes(-2),
            Array.Empty<MesTaskUnionObservation>()));

        using (var failedOverview = await ReadOverviewAsync(client, "/api/v2/watch-overview"))
        {
            var failure = Assert.Single(
                failedOverview.RootElement.GetProperty("recentActivity").EnumerateArray(),
                item => item.GetProperty("eventType").GetString() == "POLL_RUN_FAILED");
            Assert.Equal(
                "poll-ticket07-activity-failure",
                failure.GetProperty("pollTraceId").GetString());
            Assert.Equal(JsonValueKind.Null, failure.GetProperty("projectionCommitId").ValueKind);
        }

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-activity-recovery",
            now.AddMinutes(-1),
            observation));

        using var recoveredOverview = await ReadOverviewAsync(client, "/api/v2/watch-overview");
        var recovery = Assert.Single(
            recoveredOverview.RootElement.GetProperty("recentActivity").EnumerateArray(),
            item => item.GetProperty("eventType").GetString() == "POLL_RUN_RECOVERED");
        Assert.Equal(
            "poll-ticket07-activity-recovery",
            recovery.GetProperty("pollTraceId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            recovery.GetProperty("projectionCommitId").GetString()));
    }

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket14-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static void AssertSummaryCounts(
        JsonElement root,
        long seriesCount,
        long demandCount,
        long readableCount,
        long notReadableCount)
    {
        Assert.Equal(seriesCount, root.GetProperty("series").GetProperty("exactTotalSeriesCount").GetInt64());
        Assert.Equal(
            demandCount,
            root.GetProperty("readability").GetProperty("exactTotalDemandGenerationCount").GetInt64());
        Assert.Equal(readableCount, root.GetProperty("readability").GetProperty("readableCount").GetInt64());
        Assert.Equal(notReadableCount, root.GetProperty("readability").GetProperty("notReadableCount").GetInt64());
    }

    private static void AssertExplicitFirstPageIntent(JsonElement intent, string target)
    {
        Assert.Equal(target, intent.GetProperty("target").GetString());
        Assert.Equal(1, intent.GetProperty("pageNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, intent.GetProperty("cursor").ValueKind);
    }

    private static async Task<JsonDocument> ReadOverviewAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return await ReadOverviewResponseAsync(response);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected JSON success but received {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static async Task<JsonDocument> ReadOverviewResponseAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected watch overview success but received {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumeratePropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ReadSeriesStartedEventIdsAsync(
        string connectionString,
        DateTimeOffset occurredAt)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId
            FROM mesingest.DemandSeriesEvents
            WHERE EventType = N'DEMAND_SERIES_STARTED' AND OccurredAt = @occurredAt
            ORDER BY EventId;
            """;
        command.Parameters.AddWithValue("@occurredAt", occurredAt);
        await using var reader = await command.ExecuteReaderAsync();
        var eventIds = new List<string>();
        while (await reader.ReadAsync())
        {
            eventIds.Add(reader.GetString(0));
        }
        return eventIds;
    }

    private static async Task<bool> EventExistsAsync(
        string connectionString,
        string eventType,
        DateTimeOffset occurredAt)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN EXISTS
            (
                SELECT 1 FROM mesingest.DemandSeriesEvents
                WHERE EventType = @eventType AND OccurredAt = @occurredAt
            ) THEN 1 ELSE 0 END;
            """;
        command.Parameters.AddWithValue("@eventType", eventType);
        command.Parameters.AddWithValue("@occurredAt", occurredAt);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static MesTaskUnionObservation ValidObservation(
        string workType,
        string sublot,
        string area,
        DateTimeOffset observedAt) =>
        new(workType, sublot, area, "EQP-14", "STEP-14", observedAt, "PKG-14");

    private WebApplicationFactory<Program> CreateFactory(
        AdjustableTimeProvider clock,
        IWatchOverviewReadBoundaryObserver? observer = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseProductionSqlApiTestHost();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                if (observer is not null)
                {
                    services.RemoveAll<IWatchOverviewReadBoundaryObserver>();
                    services.AddSingleton(observer);
                }
            });
        });

    private sealed class GatedOverviewReadBoundaryObserver : IWatchOverviewReadBoundaryObserver
    {
        private readonly TaskCompletionSource<OperationalSnapshotIdentity> _fenceSelected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;

        public Task<OperationalSnapshotIdentity> FenceSelected => _fenceSelected.Task;

        public Task OnFenceSelectedAsync(
            OperationalSnapshotIdentity snapshot,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _invocationCount) != 1)
            {
                return Task.CompletedTask;
            }

            _fenceSelected.TrySetResult(snapshot);
            return _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult(true);
    }

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__SnapshotSource"] = MesIngestHostOptions.OracleRoundSource,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "true",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
        });
}
