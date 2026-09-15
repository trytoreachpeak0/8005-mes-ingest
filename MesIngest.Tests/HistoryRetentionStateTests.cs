using System.Data;
using System.Net;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.Infrastructure.SqlServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class HistoryRetentionStateTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string WorkType = "WIRE_TO_NITROGEN";
    private readonly WebApplicationFactory<Program> _factory;

    public HistoryRetentionStateTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Retention_clocks_use_exact_fifteen_day_boundaries()
    {
        var completedAt = new DateTimeOffset(2026, 7, 1, 8, 15, 30, TimeSpan.Zero);
        var rawBoundary = completedAt.AddDays(15);
        var seriesBoundary = completedAt.AddDays(15);

        Assert.Equal(
            TimeSpan.FromDays(15),
            HistoryRetentionPolicy.RawObservationAvailabilityWindow);
        Assert.Equal(
            TimeSpan.FromDays(15),
            HistoryRetentionPolicy.RetentionEligibleDemandSeriesWindow);
        Assert.Equal(rawBoundary, HistoryRetentionPolicy.RawObservationExpiresAt(completedAt));
        Assert.False(HistoryRetentionPolicy.IsRawObservationExpired(
            completedAt,
            rawBoundary.AddTicks(-1)));
        Assert.True(HistoryRetentionPolicy.IsRawObservationExpired(completedAt, rawBoundary));
        Assert.True(HistoryRetentionPolicy.IsRawObservationExpired(
            completedAt,
            rawBoundary.AddTicks(1)));

        Assert.Equal(
            seriesBoundary,
            HistoryRetentionPolicy.RetentionEligibleDemandSeriesCleanupDueAt(completedAt));
    }

    [Ticket01SqlServerFact]
    public async Task Frozen_demand_detail_is_commit_consistent_while_cleanup_commits_then_expires_as_structured_410()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var completedAt = new DateTimeOffset(2026, 8, 1, 4, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(completedAt.AddHours(1));
        var observer = new GatedDemandSeriesReadObserver();
        await using var factory = CreateFactory(clock, readBoundaryObserver: observer);
        var cleanupClock = new AdjustableTimeProvider(completedAt.AddDays(15));
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket28-frozen-cleanup-race",
            completedAt,
            ValidObservation("SL-TICKET28-FROZEN-CLEANUP", completedAt.AddYears(-1))));
        var discovery = await ReadSeriesAsync(client, "SL-TICKET28-FROZEN-CLEANUP");
        var seriesId = discovery.GetProperty("seriesId").GetString()!;
        var snapshotReference = discovery.GetProperty("snapshotReference").GetString()!;
        var detailUri = $"/api/v2/demand-series/{Uri.EscapeDataString(seriesId)}?snapshot="
            + Uri.EscapeDataString(snapshotReference);
        await using var cleanupFactory = CreateFactory(cleanupClock);
        var cleanupProjection = cleanupFactory.Services.GetRequiredService<IMesIngestProjection>();

        var gate = observer.Arm();
        var pendingFrozenRead = client.GetAsync(detailUri);
        await gate.Selected.WaitAsync(TimeSpan.FromSeconds(10));
        var cleanup = await cleanupProjection.AdvanceHistoryRetentionAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, cleanup.DeletedRawObservationCount);
        gate.Release();

        using (var frozenResponse = await pendingFrozenRead.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Assert.Equal(HttpStatusCode.OK, frozenResponse.StatusCode);
            var frozen = await ReadJsonAsync(frozenResponse);
            Assert.Single(frozen.GetProperty("rawObservations").EnumerateArray());
        }

        await AssertHistoricalUnavailableAsync(
            client,
            detailUri,
            HttpStatusCode.Gone,
            PollEvidenceErrorCodes.MesIngestHistoryExpired,
            completedAt);
    }

    [Ticket01SqlServerFact]
    public async Task Raw_observation_multiset_expires_atomically_at_the_Host_UTC_boundary()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var completedAt = new DateTimeOffset(2026, 7, 1, 8, 15, 30, TimeSpan.Zero);
        var retainedAt = completedAt.AddDays(1);
        var clock = new AdjustableTimeProvider(completedAt.AddDays(15).AddTicks(-1));
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var projection = factory.Services.GetRequiredService<IMesIngestProjection>();

        var expiring = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-expiring",
            completedAt,
            InvalidObservation("SL-TICKET13-RETAINED-GRAPH"),
            InvalidObservation("SL-TICKET13-RETAINED-GRAPH"),
            ValidObservation("SL-TICKET13-OLD-ONLY", completedAt.AddYears(-12)),
            new MesTaskUnionObservation(null, null, null, null, null, null, null, null)));
        var oldOnlyDemand = await projection.GetDemandSeriesByKeyAsync(
            WorkType,
            "SL-TICKET13-OLD-ONLY");
        Assert.NotNull(oldOnlyDemand);
        var oldSeriesSnapshot = await ReadSeriesAsync(client, "SL-TICKET13-RETAINED-GRAPH");
        var oldSeriesId = oldSeriesSnapshot.GetProperty("seriesId").GetString()!;
        var oldSnapshotReference = oldSeriesSnapshot.GetProperty("snapshotReference").GetString()!;
        var emptyExpired = await ingestor.IngestAsync(FailureRound(
            "poll-ticket13-expiring-empty",
            completedAt));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-retained",
            retainedAt,
            ValidObservation("SL-TICKET13-RETAINED-GRAPH", completedAt.AddYears(-12))));

        var beforeSeries = await ReadSeriesAsync(client, "SL-TICKET13-RETAINED-GRAPH");
        var retainedSnapshotReference = beforeSeries.GetProperty("snapshotReference").GetString()!;
        var beforeCurrent = await projection.GetDemandSeriesByKeyAsync(
            WorkType,
            "SL-TICKET13-RETAINED-GRAPH");
        var beforeGraph = await ReadSeriesGraphCountsAsync(
            database.ConnectionString,
            oldSeriesId);
        var before = await projection.AdvanceHistoryRetentionAsync();
        Assert.Equal(0, before.ExpiredPollTraceCount);
        Assert.Equal(0, before.DeletedRawObservationCount);
        using (var response = await client.GetAsync($"/api/v2/poll-traces/{expiring.PollTraceId}"))
        {
            var body = await ReadJsonAsync(response);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(4, body.GetProperty("observations").GetArrayLength());
        }

        clock.SetUtcNow(completedAt.AddDays(15));
        await AssertHistoricalUnavailableAsync(
            client,
            $"/api/v2/poll-traces/{expiring.PollTraceId}",
            HttpStatusCode.Gone,
            PollEvidenceErrorCodes.MesIngestHistoryExpired,
            completedAt);
        await AssertHistoricalUnavailableAsync(
            client,
            $"/api/v2/demand-series/{oldSeriesId}?snapshot="
            + Uri.EscapeDataString(oldSnapshotReference),
            HttpStatusCode.Gone,
            PollEvidenceErrorCodes.MesIngestHistoryExpired,
            completedAt);
        await AssertHistoricalUnavailableAsync(
            client,
            $"/api/v2/demand-series/{oldSeriesId}?snapshot="
            + Uri.EscapeDataString(retainedSnapshotReference),
            HttpStatusCode.Gone,
            PollEvidenceErrorCodes.MesIngestHistoryExpired,
            completedAt);
        await AssertHistoricalUnavailableAsync(
            client,
            "/api/v2/demand-series?snapshot="
            + Uri.EscapeDataString(retainedSnapshotReference),
            HttpStatusCode.Gone,
            PollEvidenceErrorCodes.MesIngestHistoryExpired,
            completedAt);
        string freshDemandSnapshotReference;
        using (var freshListResponse = await client.GetAsync("/api/v2/demand-series"))
        {
            Assert.Equal(HttpStatusCode.OK, freshListResponse.StatusCode);
            var freshList = await ReadJsonAsync(freshListResponse);
            freshDemandSnapshotReference = freshList.GetProperty("snapshotReference").GetString()!;
            using var freshSnapshotResponse = await client.GetAsync(
                "/api/v2/demand-series?snapshot="
                + Uri.EscapeDataString(freshDemandSnapshotReference));
            Assert.Equal(HttpStatusCode.OK, freshSnapshotResponse.StatusCode);
            using var freshDetailResponse = await client.GetAsync(
                $"/api/v2/demand-series/{oldSeriesId}?snapshot="
                + Uri.EscapeDataString(freshDemandSnapshotReference));
            Assert.Equal(HttpStatusCode.OK, freshDetailResponse.StatusCode);
            var freshDetail = await ReadJsonAsync(freshDetailResponse);
            Assert.Single(freshDetail.GetProperty("rawObservations").EnumerateArray());
        }
        string freshAuditSnapshotReference;
        using (var freshAuditResponse = await client.GetAsync("/api/v2/readability-audit"))
        {
            Assert.Equal(HttpStatusCode.OK, freshAuditResponse.StatusCode);
            var freshAudit = await ReadJsonAsync(freshAuditResponse);
            freshAuditSnapshotReference = freshAudit.GetProperty("snapshotReference").GetString()!;
        }
        using (var freshAuditDetailResponse = await client.GetAsync(
            $"/api/v2/readability-audit/{oldOnlyDemand.CurrentDemand.DemandId}?snapshot="
            + Uri.EscapeDataString(freshAuditSnapshotReference)))
        {
            Assert.Equal(HttpStatusCode.OK, freshAuditDetailResponse.StatusCode);
        }

        var expired = await projection.AdvanceHistoryRetentionAsync();
        Assert.Equal(clock.GetUtcNow(), expired.AdvancedAt);
        Assert.Equal(completedAt, expired.RawObservationCutoff);
        Assert.Equal(2, expired.ExpiredPollTraceCount);
        Assert.Equal(4, expired.DeletedRawObservationCount);

        await AssertHistoricalUnavailableAsync(
            client,
            $"/api/v2/poll-traces/{expiring.PollTraceId}",
            HttpStatusCode.Gone,
            PollEvidenceErrorCodes.MesIngestHistoryExpired,
            retainedAt);
        await AssertHistoricalUnavailableAsync(
            client,
            $"/api/v2/poll-traces/{emptyExpired.PollTraceId}",
            HttpStatusCode.Gone,
            PollEvidenceErrorCodes.MesIngestHistoryExpired,
            retainedAt);
        await AssertHistoricalUnavailableAsync(
            client,
            "/api/v2/poll-traces/poll-ticket13-never-existed",
            HttpStatusCode.NotFound,
            PollEvidenceErrorCodes.PollTraceNotFound,
            retainedAt);

        var afterCurrent = await projection.GetDemandSeriesByKeyAsync(
            WorkType,
            "SL-TICKET13-RETAINED-GRAPH");
        using var currentListResponse = await client.GetAsync("/api/v2/demand-series");
        Assert.Equal(HttpStatusCode.OK, currentListResponse.StatusCode);
        Assert.NotNull(beforeCurrent);
        Assert.NotNull(afterCurrent);
        Assert.Equal(beforeCurrent.SeriesId, afterCurrent.SeriesId);
        Assert.Equal(beforeCurrent.Lifecycle, afterCurrent.Lifecycle);
        Assert.Equal(beforeCurrent.CurrentPresence, afterCurrent.CurrentPresence);
        Assert.Equal(
            JsonSerializer.Serialize(beforeCurrent.CurrentDemand),
            JsonSerializer.Serialize(afterCurrent.CurrentDemand));
        Assert.Equal(
            beforeGraph,
            await ReadSeriesGraphCountsAsync(database.ConnectionString, oldSeriesId));
        Assert.Equal(0, await CountRawObservationsAsync(
            database.ConnectionString,
            expiring.PollTraceId));
        Assert.Equal(1, await CountRawObservationsAsync(
            database.ConnectionString,
            "poll-ticket13-retained"));
        using var retainedDemandDetailResponse = await client.GetAsync(
            $"/api/v2/demand-series/{oldOnlyDemand.SeriesId}?snapshot="
            + Uri.EscapeDataString(freshDemandSnapshotReference));
        Assert.Equal(HttpStatusCode.OK, retainedDemandDetailResponse.StatusCode);
        var retainedDemandDetail = await ReadJsonAsync(retainedDemandDetailResponse);
        var retainedOldOnlyCurrent = await projection.GetDemandSeriesAsync(oldOnlyDemand.SeriesId);
        Assert.NotNull(retainedOldOnlyCurrent);
        Assert.Equal(
            retainedOldOnlyCurrent.LatestProjectionCommitId,
            retainedDemandDetail.GetProperty("latestProjectionCommitId").GetString());
        Assert.Equal(
            "N3-3",
            retainedDemandDetail.GetProperty("currentDemand")
                .GetProperty("liveMesFields")
                .GetProperty("area")
                .GetString());
        Assert.Empty(retainedDemandDetail.GetProperty("rawObservations").EnumerateArray());
        using var retainedAuditDetailResponse = await client.GetAsync(
            $"/api/v2/readability-audit/{oldOnlyDemand.CurrentDemand.DemandId}?snapshot="
            + Uri.EscapeDataString(freshAuditSnapshotReference));
        Assert.Equal(HttpStatusCode.OK, retainedAuditDetailResponse.StatusCode);
    }

    [Ticket01SqlServerFact]
    public async Task Series_eligibility_is_created_cancelled_and_restarted_from_each_qualifying_Host_UTC()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var firstSeenAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(firstSeenAt);
        var eventObserver = new SeriesActivityCheckpointObserver();
        await using var factory = CreateFactory(clock, eventObserver);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-seed",
            firstSeenAt,
            ValidObservation("SL-TICKET13-ELIGIBILITY", firstSeenAt.AddYears(-15))));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-normal",
            firstSeenAt.AddMinutes(1),
            ValidObservation("SL-TICKET13-ELIGIBILITY", firstSeenAt.AddYears(8))));
        var goneAt = firstSeenAt.AddMinutes(2);
        await ingestor.IngestAsync(SuccessRound("poll-ticket13-gone", goneAt));
        var seriesId = (await ReadSeriesAsync(client, "SL-TICKET13-ELIGIBILITY"))
            .GetProperty("seriesId").GetString()!;

        var trackingGone = await ReadRetentionStateAsync(database.ConnectionString, seriesId);
        Assert.Equal(DemandSeriesLifecycleContract.Tracking, trackingGone.Lifecycle);
        Assert.Equal(DemandSeriesLifecycleContract.Gone, trackingGone.CurrentPresence);
        Assert.Null(trackingGone.EligibilityAt);

        var archiveAt = goneAt.Add(DemandSeriesArchivePolicy.MinimumGoneDuration);
        await ingestor.IngestAsync(SuccessRound("poll-ticket13-archive", archiveAt));
        var eligible = await ReadRetentionStateAsync(database.ConnectionString, seriesId);
        Assert.Equal(DemandSeriesLifecycleContract.Archived, eligible.Lifecycle);
        Assert.Equal(DemandSeriesLifecycleContract.Gone, eligible.CurrentPresence);
        Assert.Equal(archiveAt, eligible.EligibilityAt);
        Assert.Equal(archiveAt.AddDays(15),
            HistoryRetentionPolicy.RetentionEligibleDemandSeriesCleanupDueAt(
                eligible.EligibilityAt!.Value));
        Assert.Equal(0, eligible.CurrentConditionCount);
        Assert.Equal(0, eligible.OpenErrorPeriodCount);

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-still-eligible",
            archiveAt.AddMinutes(1)));
        var stillEligible = await ReadRetentionStateAsync(database.ConnectionString, seriesId);
        Assert.Equal(archiveAt, stillEligible.EligibilityAt);

        // The round projects nothing for this Series; only the test checkpoint writes an
        // event for it. Eligibility is refreshed for the Series a round projects, so
        // activity that no projection path produces does not restart the clock.
        var foreignActivityAt = archiveAt.AddMinutes(2);
        eventObserver.Arm("poll-ticket13-event-only-activity", seriesId, foreignActivityAt);
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-event-only-activity",
            foreignActivityAt));
        var eligibleAfterForeignEvent = await ReadRetentionStateAsync(
            database.ConnectionString,
            seriesId);
        Assert.Equal(archiveAt, eligibleAfterForeignEvent.EligibilityAt);
        Assert.True(eligibleAfterForeignEvent.EventCount > stillEligible.EventCount);
        Assert.Equal(0, eligibleAfterForeignEvent.CurrentConditionCount);
        Assert.Equal(0, eligibleAfterForeignEvent.OpenErrorPeriodCount);

        var reappearedAt = archiveAt.AddHours(1);
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-reappeared-invalid",
            reappearedAt,
            InvalidObservation("SL-TICKET13-ELIGIBILITY")));
        var cancelled = await ReadRetentionStateAsync(database.ConnectionString, seriesId);
        Assert.Equal(DemandSeriesLifecycleContract.Archived, cancelled.Lifecycle);
        Assert.Equal(DemandSeriesLifecycleContract.LongGoneButVisible, cancelled.CurrentPresence);
        Assert.Null(cancelled.EligibilityAt);
        Assert.True(cancelled.CurrentConditionCount > 0);
        Assert.True(cancelled.OpenErrorPeriodCount > 0);
        Assert.True(cancelled.EventCount > eligible.EventCount);
        Assert.True(cancelled.RawObservationCount > eligible.RawObservationCount);

        var eligibleAgainAt = reappearedAt.AddMinutes(1);
        await ingestor.IngestAsync(SuccessRound("poll-ticket13-gone-again", eligibleAgainAt));
        var eligibleAgain = await ReadRetentionStateAsync(database.ConnectionString, seriesId);
        Assert.Equal(DemandSeriesLifecycleContract.Archived, eligibleAgain.Lifecycle);
        Assert.Equal(DemandSeriesLifecycleContract.Gone, eligibleAgain.CurrentPresence);
        Assert.Equal(eligibleAgainAt, eligibleAgain.EligibilityAt);
        Assert.NotEqual(eligible.EligibilityAt, eligibleAgain.EligibilityAt);
        Assert.Equal(0, eligibleAgain.CurrentConditionCount);
        Assert.Equal(0, eligibleAgain.OpenErrorPeriodCount);
    }

    [Ticket01SqlServerFact]
    public async Task Retention_eligibility_refresh_reevaluates_only_the_Series_the_round_projected()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var firstSeenAt = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(firstSeenAt);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var anchor = ValidObservation("SL-SCOPE-ANCHOR", firstSeenAt.AddYears(-1));

        await ingestor.IngestAsync(SuccessRound(
            "poll-scope-seed",
            firstSeenAt,
            anchor,
            ValidObservation("SL-SCOPE-ARCHIVED", firstSeenAt.AddYears(-1))));
        await ingestor.IngestAsync(SuccessRound(
            "poll-scope-normal",
            firstSeenAt.AddMinutes(1),
            anchor,
            ValidObservation("SL-SCOPE-ARCHIVED", firstSeenAt.AddYears(-1))));
        var goneAt = firstSeenAt.AddMinutes(2);
        await ingestor.IngestAsync(SuccessRound("poll-scope-gone", goneAt, anchor));
        var archiveAt = goneAt.Add(DemandSeriesArchivePolicy.MinimumGoneDuration);
        await ingestor.IngestAsync(SuccessRound("poll-scope-archive", archiveAt, anchor));

        var archivedSeriesId = (await ReadSeriesAsync(client, "SL-SCOPE-ARCHIVED"))
            .GetProperty("seriesId").GetString()!;
        var anchorSeriesId = (await ReadSeriesAsync(client, "SL-SCOPE-ANCHOR"))
            .GetProperty("seriesId").GetString()!;
        Assert.Equal(archiveAt, (await ReadRetentionStateAsync(
            database.ConnectionString,
            archivedSeriesId)).EligibilityAt);
        Assert.Null((await ReadRetentionStateAsync(
            database.ConnectionString,
            anchorSeriesId)).EligibilityAt);

        // Drift both Series out of band. The next round projects only the anchor.
        await SetRetentionEligibilityAtAsync(database.ConnectionString, archivedSeriesId, null);
        await SetRetentionEligibilityAtAsync(
            database.ConnectionString,
            anchorSeriesId,
            archiveAt.AddDays(-30));
        await ingestor.IngestAsync(SuccessRound(
            "poll-scope-anchor-only",
            archiveAt.AddMinutes(1),
            anchor));

        var untouched = await ReadRetentionStateAsync(database.ConnectionString, archivedSeriesId);
        Assert.Equal(DemandSeriesLifecycleContract.Archived, untouched.Lifecycle);
        Assert.Equal(DemandSeriesLifecycleContract.Gone, untouched.CurrentPresence);
        Assert.Null(untouched.EligibilityAt);
        var projected = await ReadRetentionStateAsync(database.ConnectionString, anchorSeriesId);
        Assert.Equal(DemandSeriesLifecycleContract.Tracking, projected.Lifecycle);
        Assert.Null(projected.EligibilityAt);
    }

    [Ticket01SqlServerFact]
    public async Task Round_scoped_eligibility_refresh_leaves_nothing_for_the_legacy_full_scan_to_change()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var t0 = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(t0);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var anchor = ValidObservation("SL-EQ-ANCHOR", t0.AddYears(-1));
        var valid = ValidObservation("SL-EQ-VALID", t0.AddYears(-1));
        var invalid = InvalidObservation("SL-EQ-INVALID");
        var duplicate = ValidObservation("SL-EQ-DUPLICATE", t0.AddYears(-1));
        var late = ValidObservation("SL-EQ-LATE", t0.AddYears(-1));

        async Task IngestAndCompareAsync(
            string pollTraceId,
            DateTimeOffset completedAt,
            params MesTaskUnionObservation[] observations)
        {
            var receipt = await ingestor.IngestAsync(
                SuccessRound(pollTraceId, completedAt, observations));
            await AssertLegacyFullScanRefreshChangesNothingAsync(
                database.ConnectionString,
                receipt,
                completedAt);
        }

        await IngestAndCompareAsync("poll-eq-1", t0,
            anchor, valid, invalid, duplicate, duplicate, late);
        await IngestAndCompareAsync("poll-eq-2", t0.AddMinutes(1),
            anchor, ValidObservation("SL-EQ-VALID", t0.AddYears(2)), invalid,
            duplicate, duplicate, late);
        var goneAt = t0.AddMinutes(2);
        await IngestAndCompareAsync("poll-eq-3-gone", goneAt, anchor, late);
        var archiveAt = goneAt.Add(DemandSeriesArchivePolicy.MinimumGoneDuration);
        await IngestAndCompareAsync("poll-eq-4-archive", archiveAt, anchor, late);
        await IngestAndCompareAsync("poll-eq-5-quiet", archiveAt.AddMinutes(1), anchor, late);
        var reappearedAt = archiveAt.AddHours(1);
        await IngestAndCompareAsync("poll-eq-6-reappear", reappearedAt, anchor, invalid);
        var invalidGoneAgainAt = reappearedAt.AddMinutes(1);
        await IngestAndCompareAsync("poll-eq-7-gone-again", invalidGoneAgainAt, anchor);
        var lateArchiveAt = reappearedAt.Add(DemandSeriesArchivePolicy.MinimumGoneDuration);
        await IngestAndCompareAsync("poll-eq-8-late-archive", lateArchiveAt, anchor);

        async Task<RetentionState> ReadBySublotAsync(string sublot) =>
            await ReadRetentionStateAsync(
                database.ConnectionString,
                (await ReadSeriesAsync(client, sublot)).GetProperty("seriesId").GetString()!);

        // The dataset must actually walk every eligibility transition, or the
        // comparison above proves nothing.
        Assert.Null((await ReadBySublotAsync("SL-EQ-ANCHOR")).EligibilityAt);
        Assert.Equal(archiveAt, (await ReadBySublotAsync("SL-EQ-VALID")).EligibilityAt);
        Assert.Equal(archiveAt, (await ReadBySublotAsync("SL-EQ-DUPLICATE")).EligibilityAt);
        var invalidState = await ReadBySublotAsync("SL-EQ-INVALID");
        Assert.Equal(DemandSeriesLifecycleContract.Archived, invalidState.Lifecycle);
        Assert.Equal(invalidGoneAgainAt, invalidState.EligibilityAt);
        var lateState = await ReadBySublotAsync("SL-EQ-LATE");
        Assert.Equal(DemandSeriesLifecycleContract.Archived, lateState.Lifecycle);
        Assert.Equal(lateArchiveAt, lateState.EligibilityAt);
    }

    [Ticket01SqlServerFact]
    public async Task Empty_expired_PollTrace_is_410_when_the_cutoff_is_the_earliest_boundary()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var completedAt = new DateTimeOffset(2026, 7, 2, 6, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(completedAt.AddDays(15));
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var projection = factory.Services.GetRequiredService<IMesIngestProjection>();
        var receipt = await ingestor.IngestAsync(FailureRound(
            "poll-ticket13-only-empty-expired",
            completedAt));

        var advance = await projection.AdvanceHistoryRetentionAsync();
        Assert.Equal(1, advance.ExpiredPollTraceCount);
        Assert.Equal(0, advance.DeletedRawObservationCount);
        Assert.Equal(completedAt, advance.EarliestAvailableHostUtc);

        await AssertHistoricalUnavailableAsync(
            client,
            $"/api/v2/poll-traces/{receipt.PollTraceId}",
            HttpStatusCode.Gone,
            PollEvidenceErrorCodes.MesIngestHistoryExpired,
            completedAt);
    }

    private WebApplicationFactory<Program> CreateFactory(
        AdjustableTimeProvider clock,
        IProjectionCommitCheckpointObserver? checkpointObserver = null,
        IProjectionReadBoundaryObserver? readBoundaryObserver = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseProductionSqlApiTestHost();
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                if (checkpointObserver is not null)
                {
                    services.RemoveAll<IProjectionCommitCheckpointObserver>();
                    services.AddSingleton(checkpointObserver);
                }
                if (readBoundaryObserver is not null)
                {
                    services.RemoveAll<IProjectionReadBoundaryObserver>();
                    services.AddSingleton(readBoundaryObserver);
                }
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
        });

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket13-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-1),
            completedAt,
            observations);

    private static MesTaskUnionRound FailureRound(
        string pollTraceId,
        DateTimeOffset completedAt) =>
        new(
            pollTraceId,
            "mes-task-union-ticket13-v1",
            MesTaskUnionRoundOutcome.Failure,
            completedAt.AddSeconds(-1),
            completedAt,
            [],
            new MesTaskUnionRoundDiagnostic(
                "ORACLE_EXECUTE",
                "TICKET13_CONTROLLED_FAILURE",
                "Controlled failure with no raw observation multiset."));

    private static MesTaskUnionObservation ValidObservation(
        string sublot,
        DateTimeOffset mesSourceDate) =>
        new(WorkType, sublot, "N3-3", "WB-03", "焊线2", mesSourceDate, "QFN");

    private static MesTaskUnionObservation InvalidObservation(string sublot) =>
        new(WorkType, sublot, "A01-01", null, "焊线2", null, null, "not-a-date");

    private static async Task<JsonElement> ReadSeriesAsync(HttpClient client, string sublot)
    {
        using var response = await client.GetAsync(
            "/api/v2/demand-series/by-key"
            + $"?workType={Uri.EscapeDataString(WorkType)}"
            + $"&sublot={Uri.EscapeDataString(sublot)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private static async Task AssertHistoricalUnavailableAsync(
        HttpClient client,
        string uri,
        HttpStatusCode expectedStatus,
        string expectedCode,
        DateTimeOffset expectedBoundary)
    {
        using var response = await client.GetAsync(uri);
        var body = await ReadJsonAsync(response);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, body.GetProperty("code").GetString());
        Assert.Equal(
            expectedBoundary,
            body.GetProperty("earliestAvailableHostUtc").GetDateTimeOffset());
    }

    private static async Task<int> CountRawObservationsAsync(
        string connectionString,
        string pollTraceId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM mesingest.DemandRawObservations
            WHERE PollTraceId = @pollTraceId;
            """;
        command.Parameters.AddWithValue("@pollTraceId", pollTraceId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<RetentionState> ReadRetentionStateAsync(
        string connectionString,
        string seriesId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                s.Lifecycle,
                s.CurrentPresence,
                s.RetentionEligibilityAt,
                (SELECT COUNT(*)
                 FROM mesingest.DemandSeriesCurrentConditions AS c
                 WHERE c.SeriesId = s.SeriesId),
                (SELECT COUNT(*)
                 FROM mesingest.DemandSeriesErrorPeriods AS p
                 WHERE p.SeriesId = s.SeriesId AND p.EndedAt IS NULL),
                (SELECT COUNT(*)
                 FROM mesingest.DemandSeriesEvents AS e
                 WHERE e.SeriesId = s.SeriesId),
                (SELECT COUNT(*)
                 FROM mesingest.DemandRawObservations AS o
                 WHERE o.SeriesId = s.SeriesId)
            FROM mesingest.DemandSeries AS s
            WHERE s.SeriesId = @seriesId;
            """;
        command.Parameters.AddWithValue("@seriesId", seriesId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new RetentionState(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6));
    }

    private static async Task<SeriesGraphCounts> ReadSeriesGraphCountsAsync(
        string connectionString,
        string seriesId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM mesingest.TransportDemands WHERE SeriesId = @seriesId),
                (SELECT COUNT(*) FROM mesingest.DemandSeriesEvents WHERE SeriesId = @seriesId),
                (SELECT COUNT(*) FROM mesingest.DemandSeriesErrorPeriods WHERE SeriesId = @seriesId),
                (SELECT COUNT(*) FROM mesingest.DemandSeriesCurrentConditions WHERE SeriesId = @seriesId);
            """;
        command.Parameters.AddWithValue("@seriesId", seriesId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new SeriesGraphCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    // The whole-table statement every commit ran before eligibility refresh was scoped
    // to the round's projected Series. Kept verbatim as the equivalence oracle.
    private const string LegacyFullScanRetentionEligibilityRefreshSql = """
        ;WITH RetentionState AS
        (
            SELECT
                series.SeriesId,
                CONVERT(BIT, CASE
                    WHEN series.Lifecycle = N'ARCHIVED'
                         AND series.CurrentPresence NOT IN (N'VISIBLE', N'LONG_GONE_BUT_VISIBLE')
                         AND demand.Status NOT IN (N'VISIBLE', N'LONG_GONE_BUT_VISIBLE')
                         AND NOT EXISTS
                         (
                             SELECT 1
                             FROM mesingest.DemandSeriesCurrentConditions AS currentCondition
                             WHERE currentCondition.SeriesId = series.SeriesId
                         )
                         AND NOT EXISTS
                         (
                             SELECT 1
                             FROM mesingest.DemandSeriesErrorPeriods AS errorPeriod
                             WHERE errorPeriod.SeriesId = series.SeriesId
                               AND errorPeriod.EndedAt IS NULL
                         )
                    THEN 1
                    ELSE 0
                END) AS IsEligible,
                CONVERT(BIT, CASE
                    WHEN EXISTS
                         (
                             SELECT 1
                             FROM mesingest.DemandRawObservations AS observation
                             WHERE observation.SeriesId = series.SeriesId
                               AND observation.ProjectionCommitId = @projectionCommitId
                         )
                         OR EXISTS
                         (
                             SELECT 1
                             FROM mesingest.DemandSeriesEvents AS seriesEvent
                             WHERE seriesEvent.SeriesId = series.SeriesId
                               AND seriesEvent.ProjectionCommitId = @projectionCommitId
                         )
                    THEN 1
                    ELSE 0
                END) AS HadActivityThisCommit
            FROM mesingest.DemandSeries AS series
            INNER JOIN mesingest.TransportDemands AS demand
                ON demand.DemandId = series.CurrentDemandId
        )
        UPDATE series
        SET RetentionEligibilityAt =
            CASE
                WHEN state.IsEligible = 1 THEN @eligibilityAt
                ELSE NULL
            END
        FROM mesingest.DemandSeries AS series
        INNER JOIN RetentionState AS state ON state.SeriesId = series.SeriesId
        WHERE (state.IsEligible = 1
               AND (series.RetentionEligibilityAt IS NULL
                    OR state.HadActivityThisCommit = 1))
           OR (state.IsEligible = 0 AND series.RetentionEligibilityAt IS NOT NULL);
        """;

    private static async Task AssertLegacyFullScanRefreshChangesNothingAsync(
        string connectionString,
        RoundCommitReceipt receipt,
        DateTimeOffset completedAt)
    {
        Assert.NotNull(receipt.ProjectionCommitId);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable);
        var committed = await ReadAllRetentionEligibilityAsync(connection, transaction);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = LegacyFullScanRetentionEligibilityRefreshSql;
            command.Parameters.Add("@eligibilityAt", SqlDbType.DateTimeOffset).Value =
                completedAt.ToUniversalTime();
            command.Parameters.Add("@projectionCommitId", SqlDbType.NVarChar, 64).Value =
                receipt.ProjectionCommitId;
            await command.ExecuteNonQueryAsync();
        }

        var legacy = await ReadAllRetentionEligibilityAsync(connection, transaction);
        await transaction.RollbackAsync();
        Assert.Equal(legacy, committed);
    }

    private static async Task<string> ReadAllRetentionEligibilityAsync(
        SqlConnection connection,
        SqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SeriesId, RetentionEligibilityAt
            FROM mesingest.DemandSeries
            ORDER BY SeriesId;
            """;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0) + "="
                + (reader.IsDBNull(1)
                    ? "NULL"
                    : reader.GetFieldValue<DateTimeOffset>(1).ToString("O")));
        }

        return string.Join(Environment.NewLine, rows);
    }

    private static async Task SetRetentionEligibilityAtAsync(
        string connectionString,
        string seriesId,
        DateTimeOffset? eligibilityAt)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mesingest.DemandSeries
            SET RetentionEligibilityAt = @eligibilityAt
            WHERE SeriesId = @seriesId;
            """;
        command.Parameters.Add("@eligibilityAt", SqlDbType.DateTimeOffset).Value =
            (object?)eligibilityAt ?? DBNull.Value;
        command.Parameters.AddWithValue("@seriesId", seriesId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private sealed record RetentionState(
        string Lifecycle,
        string CurrentPresence,
        DateTimeOffset? EligibilityAt,
        int CurrentConditionCount,
        int OpenErrorPeriodCount,
        int EventCount,
        int RawObservationCount);

    private sealed record SeriesGraphCounts(
        int DemandCount,
        int EventCount,
        int ErrorPeriodCount,
        int CurrentConditionCount);

    private sealed class GatedDemandSeriesReadObserver : IProjectionReadBoundaryObserver
    {
        private ReadGate? _gate;

        public ReadGate Arm()
        {
            Assert.Null(_gate);
            _gate = new ReadGate();
            return _gate;
        }

        public Task OnFenceSelectedAsync(
            ProjectionReadSurface surface,
            ProjectionReadFence fence,
            CancellationToken cancellationToken)
        {
            if (surface != ProjectionReadSurface.DemandSeries)
            {
                return Task.CompletedTask;
            }
            var gate = Interlocked.Exchange(ref _gate, null);
            return gate is null ? Task.CompletedTask : gate.SelectAsync(cancellationToken);
        }
    }

    private sealed class ReadGate
    {
        private readonly TaskCompletionSource<bool> _selected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Selected => _selected.Task;

        public async Task SelectAsync(CancellationToken cancellationToken)
        {
            _selected.TrySetResult(true);
            await _released.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _released.TrySetResult(true);
    }

    private sealed class SeriesActivityCheckpointObserver
        : IProjectionCommitCheckpointObserver
    {
        private string? _pollTraceId;
        private string? _seriesId;
        private DateTimeOffset? _occurredAt;

        public void Arm(
            string pollTraceId,
            string seriesId,
            DateTimeOffset occurredAt)
        {
            Assert.Null(_pollTraceId);
            _pollTraceId = pollTraceId;
            _seriesId = seriesId;
            _occurredAt = occurredAt;
        }

        public async Task OnCheckpointAsync(
            ProjectionCommitCheckpoint checkpoint,
            ProjectionCommitCheckpointContext context,
            SqlConnection connection,
            SqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            if (checkpoint is not ProjectionCommitCheckpoint.DemandProjectionPersisted
                || !string.Equals(context.PollTraceId, _pollTraceId, StringComparison.Ordinal))
            {
                return;
            }

            var seriesId = _seriesId!;
            var occurredAt = _occurredAt!.Value;
            _pollTraceId = null;
            _seriesId = null;
            _occurredAt = null;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DECLARE @nextSequence BIGINT =
                    (SELECT LastSeriesSequence + 1
                     FROM mesingest.DemandSeries WITH (UPDLOCK, HOLDLOCK)
                     WHERE SeriesId = @seriesId);

                INSERT INTO mesingest.DemandSeriesEvents
                    (EventId, SeriesId, SeriesSequence, EventType, OccurredAt,
                     SubjectKind, SubjectId, PollTraceId, ProjectionCommitId,
                     PayloadVersion, Payload)
                VALUES
                    (@eventId, @seriesId, @nextSequence, N'RETENTION_ACTIVITY_TEST',
                     @occurredAt, N'SERIES', @seriesId, @pollTraceId,
                     @projectionCommitId, 1, N'{"source":"checkpoint"}');

                UPDATE mesingest.DemandSeries
                SET LastSeriesSequence = @nextSequence,
                    LatestProjectionCommitId = @projectionCommitId
                WHERE SeriesId = @seriesId;
                """;
            command.Parameters.AddWithValue("@eventId", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("@seriesId", seriesId);
            command.Parameters.AddWithValue("@occurredAt", occurredAt);
            command.Parameters.AddWithValue("@pollTraceId", context.PollTraceId);
            command.Parameters.AddWithValue("@projectionCommitId", context.ProjectionCommitId);
            Assert.Equal(2, await command.ExecuteNonQueryAsync(cancellationToken));
        }
    }
}
