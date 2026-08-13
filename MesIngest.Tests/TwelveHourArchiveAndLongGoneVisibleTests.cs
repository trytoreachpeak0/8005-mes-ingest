using System.Net.Http.Json;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class TwelveHourArchiveAndLongGoneVisibleTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string WorkType = "WIRE_TO_NITROGEN";
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public TwelveHourArchiveAndLongGoneVisibleTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Ticket01SqlServerFact]
    public async Task Authoritative_success_archives_at_exact_twelve_hour_boundary_but_not_one_tick_before()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET06-EXACT-BOUNDARY";
        var firstSeenAt = new DateTimeOffset(2026, 8, 13, 0, 0, 2, TimeSpan.Zero);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var gone = await SeedGoneAsync(factory, client, sublot, "ticket06-boundary", firstSeenAt);
        var oneTickBefore = gone.GoneConfirmedAt.AddHours(12).AddTicks(-1);
        var exactBoundary = gone.GoneConfirmedAt.AddHours(12);

        var beforeReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket06-boundary-before",
            oneTickBefore));
        AssertCommit(
            await ReadTraceAsync(client, beforeReceipt.PollTraceId),
            beforeReceipt,
            gone.HostSessionId,
            "NORMAL",
            "NORMAL",
            absenceAuthority: true);

        var before = await ReadSeriesAsync(client, WorkType, sublot);
        AssertTrackingGone(before, gone.DemandId, gone.LastSeenAt, gone.GoneConfirmedAt);
        Assert.Equal(gone.SeriesId, before.GetProperty("seriesId").GetString());
        Assert.DoesNotContain(
            before.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("eventType").GetString() == "GONE_TIMEOUT_ARCHIVED");

        var archiveReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket06-boundary-exact",
            exactBoundary));
        AssertCommit(
            await ReadTraceAsync(client, archiveReceipt.PollTraceId),
            archiveReceipt,
            gone.HostSessionId,
            "NORMAL",
            "NORMAL",
            absenceAuthority: true);

        var archived = await ReadSeriesAsync(client, WorkType, sublot);
        AssertArchivedGone(
            archived,
            gone.SeriesId,
            gone.DemandId,
            gone.LastSeenAt,
            gone.GoneConfirmedAt,
            exactBoundary,
            archiveReceipt);
        Assert.Contains(gone.SeriesId, archiveReceipt.SeriesIds);
        Assert.Contains(gone.DemandId, archiveReceipt.DemandIds);
        AssertArchiveEvent(
            archived,
            gone.SeriesId,
            gone.GoneConfirmedAt,
            exactBoundary,
            archiveReceipt);
        AssertStrictSeriesSequence(archived);

        const string boundaryReappearanceSublot = "SL-TICKET06-BOUNDARY-REAPPEAR";
        var boundarySeedAt = exactBoundary.AddHours(1);
        var seedBoundaryReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket06-boundary-reappear-seed",
            boundarySeedAt,
            ValidObservation(boundaryReappearanceSublot, firstSeenAt.AddYears(-9))));
        var boundaryReappearanceSeries = await ReadSeriesAsync(client, WorkType, boundaryReappearanceSublot);
        var boundaryPredecessorId = boundaryReappearanceSeries.GetProperty("currentDemand").GetProperty("demandId").GetString()!;
        var boundaryGoneAt = boundarySeedAt.AddMinutes(1);
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket06-boundary-reappear-gone",
            boundaryGoneAt));
        var reappearedAtBoundaryReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket06-boundary-reappearance-exact",
            boundaryGoneAt.AddHours(12),
            ValidObservation(boundaryReappearanceSublot, firstSeenAt.AddYears(-10))));
        var reappearedAtBoundary = await ReadSeriesAsync(client, WorkType, boundaryReappearanceSublot);
        Assert.Equal("TRACKING", reappearedAtBoundary.GetProperty("lifecycle").GetString());
        Assert.Equal("VISIBLE", reappearedAtBoundary.GetProperty("currentPresence").GetString());
        Assert.Equal(JsonValueKind.Null, reappearedAtBoundary.GetProperty("archivedAt").ValueKind);
        Assert.Equal(2, reappearedAtBoundary.GetProperty("currentDemand").GetProperty("generation").GetInt32());
        Assert.Equal(boundaryPredecessorId, reappearedAtBoundary.GetProperty("currentDemand").GetProperty("predecessorDemandId").GetString());
        Assert.Contains(boundaryReappearanceSeries.GetProperty("seriesId").GetString()!, seedBoundaryReceipt.SeriesIds);
        Assert.Contains(boundaryReappearanceSeries.GetProperty("seriesId").GetString()!, reappearedAtBoundaryReceipt.SeriesIds);
        Assert.DoesNotContain(
            reappearedAtBoundary.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("eventType").GetString() == "GONE_TIMEOUT_ARCHIVED");

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Failure_incomplete_and_restart_barrier_do_not_archive_an_overdue_gone_series()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET06-NO-AUTHORITY";
        var firstSeenAt = new DateTimeOffset(2026, 8, 13, 1, 0, 2, TimeSpan.Zero);
        GoneFixture gone;

        await using (var initialFactory = CreateFactory())
        {
            using var initialClient = initialFactory.CreateClient();
            gone = await SeedGoneAsync(
                initialFactory,
                initialClient,
                sublot,
                "ticket06-no-authority",
                firstSeenAt);
        }

        await using (var restartedFactory = CreateFactory())
        {
            using var client = restartedFactory.CreateClient();
            var ingestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            var entered = await ReadAuthorityAsync(client);
            var restartedHostSessionId = entered.GetProperty("hostSessionId").GetString()!;
            AssertAuthority(entered, "BARRIER", absenceAuthorityAvailable: false);

            var failureAt = gone.GoneConfirmedAt.AddHours(13);
            var failureReceipt = await ingestor.IngestAsync(NonSuccessRound(
                "poll-ticket06-overdue-failure",
                MesTaskUnionRoundOutcome.Failure,
                failureAt,
                ValidObservation(sublot, firstSeenAt.AddYears(-3), area: "N3-7")));
            var incompleteReceipt = await ingestor.IngestAsync(NonSuccessRound(
                "poll-ticket06-overdue-incomplete",
                MesTaskUnionRoundOutcome.Incomplete,
                failureAt.AddMinutes(1),
                ValidObservation(sublot, firstSeenAt.AddYears(-4), area: "N3-8")));
            Assert.Null(failureReceipt.ProjectionCommitId);
            Assert.Null(incompleteReceipt.ProjectionCommitId);
            Assert.Equal(
                JsonValueKind.Null,
                (await ReadTraceAsync(client, failureReceipt.PollTraceId))
                    .GetProperty("projectionCommit").ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                (await ReadTraceAsync(client, incompleteReceipt.PollTraceId))
                    .GetProperty("projectionCommit").ValueKind);
            AssertTrackingGone(
                await ReadSeriesAsync(client, WorkType, sublot),
                gone.DemandId,
                gone.LastSeenAt,
                gone.GoneConfirmedAt);

            var baselineReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-overdue-restart-baseline",
                failureAt.AddMinutes(2)));
            AssertCommit(
                await ReadTraceAsync(client, baselineReceipt.PollTraceId),
                baselineReceipt,
                restartedHostSessionId,
                "BARRIER",
                "POST_BARRIER",
                absenceAuthority: false);
            AssertTrackingGone(
                await ReadSeriesAsync(client, WorkType, sublot),
                gone.DemandId,
                gone.LastSeenAt,
                gone.GoneConfirmedAt);

            var restoreReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-overdue-restart-restore",
                failureAt.AddMinutes(3)));
            AssertCommit(
                await ReadTraceAsync(client, restoreReceipt.PollTraceId),
                restoreReceipt,
                restartedHostSessionId,
                "POST_BARRIER",
                "NORMAL",
                absenceAuthority: false);
            var stillGone = await ReadSeriesAsync(client, WorkType, sublot);
            AssertTrackingGone(
                stillGone,
                gone.DemandId,
                gone.LastSeenAt,
                gone.GoneConfirmedAt);
            Assert.Equal(1, stillGone.GetProperty("demands").GetArrayLength());

            var archiveAt = failureAt.AddMinutes(4);
            var archiveReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-overdue-first-authoritative",
                archiveAt));
            AssertCommit(
                await ReadTraceAsync(client, archiveReceipt.PollTraceId),
                archiveReceipt,
                restartedHostSessionId,
                "NORMAL",
                "NORMAL",
                absenceAuthority: true);

            var archived = await ReadSeriesAsync(client, WorkType, sublot);
            AssertArchivedGone(
                archived,
                gone.SeriesId,
                gone.DemandId,
                gone.LastSeenAt,
                gone.GoneConfirmedAt,
                archiveAt,
                archiveReceipt);
            AssertArchiveEvent(
                archived,
                gone.SeriesId,
                gone.GoneConfirmedAt,
                archiveAt,
                archiveReceipt);
            Assert.Equal(1, archived.GetProperty("demands").GetArrayLength());
        }

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Archived_series_is_irreversible_across_later_authoritative_absences_and_restart()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET06-IRREVERSIBLE";
        var firstSeenAt = new DateTimeOffset(2026, 8, 13, 2, 0, 2, TimeSpan.Zero);
        GoneFixture gone;
        RoundCommitReceipt archiveReceipt;
        DateTimeOffset archivedAt;
        string archiveEventJson;

        await using (var initialFactory = CreateFactory())
        {
            using var client = initialFactory.CreateClient();
            var ingestor = initialFactory.Services.GetRequiredService<RoundIngestor>();
            gone = await SeedGoneAsync(
                initialFactory,
                client,
                sublot,
                "ticket06-irreversible",
                firstSeenAt);
            archivedAt = gone.GoneConfirmedAt.AddHours(12);
            archiveReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-irreversible-archive",
                archivedAt));

            var archived = await ReadSeriesAsync(client, WorkType, sublot);
            AssertArchivedGone(
                archived,
                gone.SeriesId,
                gone.DemandId,
                gone.LastSeenAt,
                gone.GoneConfirmedAt,
                archivedAt,
                archiveReceipt);
            archiveEventJson = AssertArchiveEvent(
                archived,
                gone.SeriesId,
                gone.GoneConfirmedAt,
                archivedAt,
                archiveReceipt).GetRawText();

            var laterReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-irreversible-later-absence",
                archivedAt.AddHours(6)));
            AssertCommit(
                await ReadTraceAsync(client, laterReceipt.PollTraceId),
                laterReceipt,
                gone.HostSessionId,
                "NORMAL",
                "NORMAL",
                absenceAuthority: true);
            var afterLaterAbsence = await ReadSeriesAsync(client, WorkType, sublot);
            AssertArchivedGone(
                afterLaterAbsence,
                gone.SeriesId,
                gone.DemandId,
                gone.LastSeenAt,
                gone.GoneConfirmedAt,
                archivedAt,
                archiveReceipt);
            Assert.Equal(
                archiveEventJson,
                AssertArchiveEvent(
                    afterLaterAbsence,
                    gone.SeriesId,
                    gone.GoneConfirmedAt,
                    archivedAt,
                    archiveReceipt).GetRawText());
        }

        await using (var restartedFactory = CreateFactory())
        {
            using var client = restartedFactory.CreateClient();
            var ingestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            var entered = await ReadAuthorityAsync(client);
            var restartedHostSessionId = entered.GetProperty("hostSessionId").GetString()!;
            AssertAuthority(entered, "BARRIER", absenceAuthorityAvailable: false);

            var persisted = await ReadSeriesByIdAsync(client, gone.SeriesId);
            AssertArchivedGone(
                persisted,
                gone.SeriesId,
                gone.DemandId,
                gone.LastSeenAt,
                gone.GoneConfirmedAt,
                archivedAt,
                archiveReceipt);
            Assert.Equal(
                archiveEventJson,
                AssertArchiveEvent(
                    persisted,
                    gone.SeriesId,
                    gone.GoneConfirmedAt,
                    archivedAt,
                    archiveReceipt).GetRawText());

            var baselineReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-irreversible-restart-baseline",
                archivedAt.AddHours(7)));
            var restoreReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-irreversible-restart-restore",
                archivedAt.AddHours(8)));
            var authoritativeReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-irreversible-restart-authoritative",
                archivedAt.AddHours(9)));
            AssertCommit(
                await ReadTraceAsync(client, baselineReceipt.PollTraceId),
                baselineReceipt,
                restartedHostSessionId,
                "BARRIER",
                "POST_BARRIER",
                absenceAuthority: false);
            AssertCommit(
                await ReadTraceAsync(client, restoreReceipt.PollTraceId),
                restoreReceipt,
                restartedHostSessionId,
                "POST_BARRIER",
                "NORMAL",
                absenceAuthority: false);
            AssertCommit(
                await ReadTraceAsync(client, authoritativeReceipt.PollTraceId),
                authoritativeReceipt,
                restartedHostSessionId,
                "NORMAL",
                "NORMAL",
                absenceAuthority: true);

            var retained = await ReadSeriesByIdAsync(client, gone.SeriesId);
            AssertArchivedGone(
                retained,
                gone.SeriesId,
                gone.DemandId,
                gone.LastSeenAt,
                gone.GoneConfirmedAt,
                archivedAt,
                archiveReceipt);
            Assert.Equal(1, retained.GetProperty("demands").GetArrayLength());
            Assert.Equal(
                archiveEventJson,
                AssertArchiveEvent(
                    retained,
                    gone.SeriesId,
                    gone.GoneConfirmedAt,
                    archivedAt,
                    archiveReceipt).GetRawText());
            AssertStrictSeriesSequence(retained);
        }

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Postarchive_reappearance_preserves_series_and_creates_long_gone_visible_successor()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET06-POSTARCHIVE-REAPPEAR";
        var firstSeenAt = new DateTimeOffset(2026, 8, 13, 3, 0, 2, TimeSpan.Zero);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var gone = await SeedGoneAsync(
            factory,
            client,
            sublot,
            "ticket06-postarchive",
            firstSeenAt);
        var archivedAt = gone.GoneConfirmedAt.AddHours(12);
        var archiveReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket06-postarchive-archive",
            archivedAt));
        var archived = await ReadSeriesAsync(client, WorkType, sublot);
        var predecessorBeforeReappearance = archived.GetProperty("currentDemand").GetRawText();
        AssertArchivedGone(
            archived,
            gone.SeriesId,
            gone.DemandId,
            gone.LastSeenAt,
            gone.GoneConfirmedAt,
            archivedAt,
            archiveReceipt);

        var reappearedAt = archivedAt.AddMinutes(1);
        var reappearanceReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket06-postarchive-reappearance",
            reappearedAt,
            ValidObservation(
                sublot,
                firstSeenAt.AddYears(-5),
                area: "N3-8",
                eqp: "WB-08",
                package: "QFN-G2")));
        AssertCommit(
            await ReadTraceAsync(client, reappearanceReceipt.PollTraceId),
            reappearanceReceipt,
            gone.HostSessionId,
            "NORMAL",
            "NORMAL",
            absenceAuthority: true);

        var reappeared = await ReadSeriesAsync(client, WorkType, sublot);
        Assert.Equal(gone.SeriesId, reappeared.GetProperty("seriesId").GetString());
        Assert.Equal("ARCHIVED", reappeared.GetProperty("lifecycle").GetString());
        Assert.Equal("LONG_GONE_BUT_VISIBLE", reappeared.GetProperty("currentPresence").GetString());
        Assert.Equal(archivedAt, reappeared.GetProperty("archivedAt").GetDateTimeOffset());
        Assert.Equal(reappearanceReceipt.ProjectionCommitId, reappeared.GetProperty("latestProjectionCommitId").GetString());

        var generations = reappeared.GetProperty("demands").EnumerateArray().ToArray();
        Assert.Equal([1, 2], generations.Select(item => item.GetProperty("generation").GetInt32()).ToArray());
        Assert.Equal(predecessorBeforeReappearance, generations[0].GetRawText());

        var predecessor = generations[0];
        Assert.Equal(gone.DemandId, predecessor.GetProperty("demandId").GetString());
        Assert.Equal("GONE", predecessor.GetProperty("status").GetString());
        Assert.Equal(gone.GoneConfirmedAt, predecessor.GetProperty("goneConfirmedAt").GetDateTimeOffset());

        var successor = generations[1];
        var successorId = successor.GetProperty("demandId").GetString()!;
        Assert.NotEqual(gone.DemandId, successorId);
        Assert.Equal(gone.SeriesId, successor.GetProperty("seriesId").GetString());
        Assert.Equal(2, successor.GetProperty("generation").GetInt32());
        Assert.Equal(gone.DemandId, successor.GetProperty("predecessorDemandId").GetString());
        Assert.Equal("LONG_GONE_BUT_VISIBLE", successor.GetProperty("status").GetString());
        Assert.Equal(reappearedAt, successor.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(reappearedAt, successor.GetProperty("demandLastSeenAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, successor.GetProperty("goneConfirmedAt").ValueKind);
        Assert.Equal(reappearanceReceipt.PollTraceId, successor.GetProperty("createdPollTraceId").GetString());
        Assert.Equal(reappearanceReceipt.ProjectionCommitId, successor.GetProperty("createdProjectionCommitId").GetString());
        Assert.Equal(reappearanceReceipt.ProjectionCommitId, successor.GetProperty("latestProjectionCommitId").GetString());
        Assert.Equal("N3-8", successor.GetProperty("liveMesFields").GetProperty("area").GetString());
        Assert.Equal("WB-08", successor.GetProperty("liveMesFields").GetProperty("eqp").GetString());
        Assert.Equal("QFN-G2", successor.GetProperty("liveMesFields").GetProperty("package").GetString());
        Assert.Equal(successor.GetRawText(), reappeared.GetProperty("currentDemand").GetRawText());
        Assert.Equal("NOT_READABLE", successor.GetProperty("externalReadabilityState").GetString());
        Assert.Equal(
            ["LONG_GONE_BUT_VISIBLE", "SERIES_ARCHIVED"],
            ReadBlockers(successor).Order(StringComparer.Ordinal).ToArray());

        Assert.Contains(gone.SeriesId, reappearanceReceipt.SeriesIds);
        Assert.Contains(successorId, reappearanceReceipt.DemandIds);
        var createdEvent = AssertSeriesEventBoundToCommit(
            reappeared,
            "TRANSPORT_DEMAND_CREATED",
            successorId,
            reappearanceReceipt);
        Assert.Equal("DEMAND", createdEvent.GetProperty("subjectKind").GetString());
        using (var payload = JsonDocument.Parse(createdEvent.GetProperty("payloadJson").GetString()!))
        {
            Assert.Equal(gone.DemandId, payload.RootElement.GetProperty("predecessorDemandId").GetString());
            Assert.Equal("POSTARCHIVE_REAPPEARANCE", payload.RootElement.GetProperty("reason").GetString());
        }

        var rawObservation = Assert.Single(reappeared.GetProperty("rawObservations").EnumerateArray()
            .Where(item => item.GetProperty("pollTraceId").GetString() == reappearanceReceipt.PollTraceId));
        Assert.Equal(successorId, rawObservation.GetProperty("demandId").GetString());
        Assert.Equal(reappearanceReceipt.ProjectionCommitId, rawObservation.GetProperty("projectionCommitId").GetString());
        AssertArchiveEvent(
            reappeared,
            gone.SeriesId,
            gone.GoneConfirmedAt,
            archivedAt,
            archiveReceipt);
        AssertStrictSeriesSequence(reappeared);

        var archiveReplay = await ingestor.IngestAsync(SuccessRound(
            archiveReceipt.PollTraceId,
            archivedAt));
        Assert.True(archiveReplay.IsReplay);
        Assert.Equal(archiveReceipt.ProjectionCommitId, archiveReplay.ProjectionCommitId);
        Assert.Equal(archiveReceipt.SeriesIds, archiveReplay.SeriesIds);
        Assert.Equal(archiveReceipt.DemandIds, archiveReplay.DemandIds);
        Assert.Contains(gone.DemandId, archiveReplay.DemandIds);
        Assert.DoesNotContain(successorId, archiveReplay.DemandIds);

        var afterReplay = await ReadSeriesAsync(client, WorkType, sublot);
        Assert.Equal(reappeared.GetRawText(), afterReplay.GetRawText());

        const string secondSublot = "SL-TICKET06-POSTARCHIVE-REPLAY-SECOND";
        var secondGone = await SeedGoneInNormalPhaseAsync(
            ingestor,
            client,
            secondSublot,
            "ticket06-postarchive-replay-second",
            reappearedAt.AddHours(1));
        const string thirdSublot = "SL-TICKET06-POSTARCHIVE-REPLAY-THIRD";
        var thirdGone = await SeedGoneInNormalPhaseAsync(
            ingestor,
            client,
            thirdSublot,
            "ticket06-postarchive-replay-third",
            reappearedAt.AddHours(2));
        var multiArchiveAt = thirdGone.GoneConfirmedAt.AddHours(12);
        var multiArchiveRound = SuccessRound("poll-ticket06-multi-archive", multiArchiveAt);
        var multiArchive = await ingestor.IngestAsync(multiArchiveRound);
        Assert.Equal(
            new[] { secondGone.SeriesId, thirdGone.SeriesId }.Order(StringComparer.Ordinal),
            multiArchive.SeriesIds);
        Assert.Equal(
            new[] { secondGone.DemandId, thirdGone.DemandId }
                .Zip(new[] { secondGone.SeriesId, thirdGone.SeriesId })
                .OrderBy(pair => pair.Second, StringComparer.Ordinal)
                .Select(pair => pair.First),
            multiArchive.DemandIds);
        var multiReplay = await ingestor.IngestAsync(multiArchiveRound);
        Assert.True(multiReplay.IsReplay);
        Assert.Equal(multiArchive.SeriesIds, multiReplay.SeriesIds);
        Assert.Equal(multiArchive.DemandIds, multiReplay.DemandIds);

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Long_gone_visible_condition_and_readability_remain_permanent_for_valid_unique_observations_and_restart()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET06-LONG-GONE-PERMANENT";
        var firstSeenAt = new DateTimeOffset(2026, 8, 13, 4, 0, 2, TimeSpan.Zero);
        GoneFixture gone;
        DateTimeOffset archivedAt;
        string successorId;
        string periodId;
        string initialEvidenceJson;

        await using (var initialFactory = CreateFactory())
        {
            using var client = initialFactory.CreateClient();
            var ingestor = initialFactory.Services.GetRequiredService<RoundIngestor>();
            gone = await SeedGoneAsync(
                initialFactory,
                client,
                sublot,
                "ticket06-long-gone",
                firstSeenAt);
            archivedAt = gone.GoneConfirmedAt.AddHours(12);
            await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-long-gone-archive",
                archivedAt));

            var reappearedAt = archivedAt.AddMinutes(1);
            var reappearanceReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-long-gone-reappearance",
                reappearedAt,
                ValidObservation(
                    sublot,
                    firstSeenAt.AddYears(-6),
                    area: "N3-7",
                    eqp: "WB-07",
                    package: "QFN-G2")));
            var reappeared = await ReadSeriesAsync(client, WorkType, sublot);
            successorId = reappeared.GetProperty("currentDemand").GetProperty("demandId").GetString()!;
            AssertLongGoneVisible(reappeared, gone.SeriesId, successorId, archivedAt);

            var condition = AssertLongGoneCondition(
                reappeared,
                gone.SeriesId,
                successorId,
                reappearedAt,
                reappearanceReceipt);
            var period = Assert.Single(reappeared.GetProperty("errorPeriods").EnumerateArray());
            periodId = period.GetProperty("periodId").GetString()!;
            Assert.Equal(periodId, condition.GetProperty("periodId").GetString());
            Assert.Equal("LONG_GONE_BUT_VISIBLE", period.GetProperty("code").GetString());
            Assert.Equal("LIFECYCLE_CONFLICT", period.GetProperty("category").GetString());
            Assert.Equal("ERROR", period.GetProperty("severity").GetString());
            Assert.Equal($"SERIES:{gone.SeriesId}", period.GetProperty("target").GetString());
            Assert.Equal("ARCHIVED_SERIES_VISIBILITY", period.GetProperty("subjectKind").GetString());
            Assert.Equal("POSTARCHIVE_REAPPEARANCE", period.GetProperty("startReason").GetString());
            Assert.Equal(reappearedAt, period.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, period.GetProperty("endedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, period.GetProperty("endReason").ValueKind);
            var initialEvidence = Assert.Single(period.GetProperty("evidence").EnumerateArray());
            Assert.Equal("POSTARCHIVE_REAPPEARANCE", initialEvidence.GetProperty("evidenceKind").GetString());
            Assert.Equal(reappearedAt, initialEvidence.GetProperty("observedAt").GetDateTimeOffset());
            Assert.Equal(reappearanceReceipt.PollTraceId, initialEvidence.GetProperty("pollTraceId").GetString());
            Assert.Equal(reappearanceReceipt.ProjectionCommitId, initialEvidence.GetProperty("projectionCommitId").GetString());
            Assert.Equal(successorId, initialEvidence.GetProperty("demandId").GetString());
            initialEvidenceJson = initialEvidence.GetRawText();

            Assert.Equal(reappearanceReceipt.ProjectionCommitId, reappeared.GetProperty("latestProjectionCommitId").GetString());
            Assert.Equal(reappearanceReceipt.ProjectionCommitId, reappeared.GetProperty("currentDemand").GetProperty("latestProjectionCommitId").GetString());

            var laterAt = reappearedAt.AddMinutes(10);
            var laterReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-long-gone-later-valid",
                laterAt,
                ValidObservation(
                    sublot,
                    firstSeenAt.AddYears(-7),
                    area: "N3-8",
                    eqp: "WB-08",
                    package: "QFN-G3")));
            var later = await ReadSeriesAsync(client, WorkType, sublot);
            AssertLongGoneVisible(later, gone.SeriesId, successorId, archivedAt);
            Assert.Equal(2, later.GetProperty("demands").GetArrayLength());
            Assert.Equal(laterReceipt.ProjectionCommitId, later.GetProperty("latestProjectionCommitId").GetString());
            Assert.Equal(laterReceipt.ProjectionCommitId, later.GetProperty("currentDemand").GetProperty("latestProjectionCommitId").GetString());
            Assert.Equal("N3-8", later.GetProperty("currentDemand").GetProperty("liveMesFields").GetProperty("area").GetString());
            AssertStableOpenLongGonePeriod(
                later,
                gone.SeriesId,
                successorId,
                periodId,
                reappearedAt,
                initialEvidenceJson);
        }

        await using (var restartedFactory = CreateFactory())
        {
            using var client = restartedFactory.CreateClient();
            var ingestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            var authority = await ReadAuthorityAsync(client);
            var hostSessionId = authority.GetProperty("hostSessionId").GetString()!;
            AssertAuthority(authority, "BARRIER", absenceAuthorityAvailable: false);

            var persisted = await ReadSeriesByIdAsync(client, gone.SeriesId);
            AssertLongGoneVisible(persisted, gone.SeriesId, successorId, archivedAt);
            AssertStableOpenLongGonePeriod(
                persisted,
                gone.SeriesId,
                successorId,
                periodId,
                archivedAt.AddMinutes(1),
                initialEvidenceJson);

            var restartedObservationAt = archivedAt.AddMinutes(20);
            var restartedReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-long-gone-restart-valid",
                restartedObservationAt,
                ValidObservation(
                    sublot,
                    firstSeenAt.AddYears(-8),
                    area: "N3-9",
                    eqp: "WB-09",
                    package: "QFN-G4")));
            AssertCommit(
                await ReadTraceAsync(client, restartedReceipt.PollTraceId),
                restartedReceipt,
                hostSessionId,
                "BARRIER",
                "POST_BARRIER",
                absenceAuthority: false);

            var afterRestartObservation = await ReadSeriesByIdAsync(client, gone.SeriesId);
            AssertLongGoneVisible(afterRestartObservation, gone.SeriesId, successorId, archivedAt);
            Assert.Equal(2, afterRestartObservation.GetProperty("demands").GetArrayLength());
            Assert.Equal(restartedReceipt.ProjectionCommitId, afterRestartObservation.GetProperty("latestProjectionCommitId").GetString());
            Assert.Equal(restartedReceipt.ProjectionCommitId, afterRestartObservation.GetProperty("currentDemand").GetProperty("latestProjectionCommitId").GetString());
            Assert.Equal("N3-9", afterRestartObservation.GetProperty("currentDemand").GetProperty("liveMesFields").GetProperty("area").GetString());
            AssertStableOpenLongGonePeriod(
                afterRestartObservation,
                gone.SeriesId,
                successorId,
                periodId,
                archivedAt.AddMinutes(1),
                initialEvidenceJson);
            AssertStrictSeriesSequence(afterRestartObservation);

            var goneAgainAt = archivedAt.AddMinutes(30);
            var goneAgainReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-long-gone-authority-restored",
                archivedAt.AddMinutes(21)));
            AssertCommit(
                await ReadTraceAsync(client, goneAgainReceipt.PollTraceId),
                goneAgainReceipt,
                hostSessionId,
                "POST_BARRIER",
                "NORMAL",
                absenceAuthority: false);
            goneAgainReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-long-gone-gone-again",
                goneAgainAt));
            AssertCommit(
                await ReadTraceAsync(client, goneAgainReceipt.PollTraceId),
                goneAgainReceipt,
                hostSessionId,
                "NORMAL",
                "NORMAL",
                absenceAuthority: true);
            var goneAgain = await ReadSeriesByIdAsync(client, gone.SeriesId);
            Assert.Equal("ARCHIVED", goneAgain.GetProperty("lifecycle").GetString());
            Assert.Equal("GONE", goneAgain.GetProperty("currentPresence").GetString());
            Assert.Equal("GONE", goneAgain.GetProperty("currentDemand").GetProperty("status").GetString());
            Assert.Empty(goneAgain.GetProperty("currentConditions").EnumerateArray());
            var endedPeriod = Assert.Single(goneAgain.GetProperty("errorPeriods").EnumerateArray());
            Assert.Equal(goneAgainAt, endedPeriod.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal("DEMAND_GONE", endedPeriod.GetProperty("endReason").GetString());

            var reappearedAgainAt = goneAgainAt.AddMinutes(1);
            await ingestor.IngestAsync(SuccessRound(
                "poll-ticket06-long-gone-reappeared-again",
                reappearedAgainAt,
                ValidObservation(sublot, firstSeenAt.AddYears(-10), area: "N3-10")));
            var reappearedAgain = await ReadSeriesByIdAsync(client, gone.SeriesId);
            Assert.Equal(3, reappearedAgain.GetProperty("currentDemand").GetProperty("generation").GetInt32());
            Assert.Equal(successorId, reappearedAgain.GetProperty("currentDemand").GetProperty("predecessorDemandId").GetString());
            Assert.Equal("LONG_GONE_BUT_VISIBLE", reappearedAgain.GetProperty("currentDemand").GetProperty("status").GetString());
            Assert.Equal(2, reappearedAgain.GetProperty("errorPeriods").GetArrayLength());
            Assert.Single(reappearedAgain.GetProperty("currentConditions").EnumerateArray());
        }

        AssertDatabaseEvidence(database);
    }

    private static async Task<GoneFixture> SeedGoneAsync(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        string sublot,
        string pollPrefix,
        DateTimeOffset firstSeenAt)
    {
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var authority = await ReadAuthorityAsync(client);
        var hostSessionId = authority.GetProperty("hostSessionId").GetString()!;
        AssertAuthority(authority, "BARRIER", absenceAuthorityAvailable: false);

        var seedReceipt = await ingestor.IngestAsync(SuccessRound(
            $"poll-{pollPrefix}-seed",
            firstSeenAt,
            ValidObservation(sublot, firstSeenAt.AddYears(-1))));
        AssertCommit(
            await ReadTraceAsync(client, seedReceipt.PollTraceId),
            seedReceipt,
            hostSessionId,
            "BARRIER",
            "POST_BARRIER",
            absenceAuthority: false);
        var seeded = await ReadSeriesAsync(client, WorkType, sublot);
        var seriesId = seeded.GetProperty("seriesId").GetString()!;
        var demandId = seeded.GetProperty("currentDemand").GetProperty("demandId").GetString()!;

        var lastSeenAt = firstSeenAt.AddMinutes(1);
        var restoreReceipt = await ingestor.IngestAsync(SuccessRound(
            $"poll-{pollPrefix}-restore",
            lastSeenAt,
            ValidObservation(sublot, firstSeenAt.AddYears(-2))));
        AssertCommit(
            await ReadTraceAsync(client, restoreReceipt.PollTraceId),
            restoreReceipt,
            hostSessionId,
            "POST_BARRIER",
            "NORMAL",
            absenceAuthority: false);

        var goneConfirmedAt = firstSeenAt.AddMinutes(2);
        var goneReceipt = await ingestor.IngestAsync(SuccessRound(
            $"poll-{pollPrefix}-gone",
            goneConfirmedAt));
        AssertCommit(
            await ReadTraceAsync(client, goneReceipt.PollTraceId),
            goneReceipt,
            hostSessionId,
            "NORMAL",
            "NORMAL",
            absenceAuthority: true);
        var gone = await ReadSeriesAsync(client, WorkType, sublot);
        Assert.Equal(seriesId, gone.GetProperty("seriesId").GetString());
        AssertTrackingGone(gone, demandId, lastSeenAt, goneConfirmedAt);
        AssertSeriesEventBoundToCommit(gone, "DEMAND_GONE", demandId, goneReceipt);

        return new GoneFixture(
            hostSessionId,
            seriesId,
            demandId,
            lastSeenAt,
            goneConfirmedAt);
    }

    private static async Task<GoneFixture> SeedGoneInNormalPhaseAsync(
        RoundIngestor ingestor,
        HttpClient client,
        string sublot,
        string pollPrefix,
        DateTimeOffset firstSeenAt)
    {
        var seedReceipt = await ingestor.IngestAsync(SuccessRound(
            $"poll-{pollPrefix}-seed",
            firstSeenAt,
            ValidObservation(sublot, firstSeenAt.AddYears(-1))));
        var seeded = await ReadSeriesAsync(client, WorkType, sublot);
        var seriesId = seeded.GetProperty("seriesId").GetString()!;
        var demandId = seeded.GetProperty("currentDemand").GetProperty("demandId").GetString()!;
        Assert.Contains(seriesId, seedReceipt.SeriesIds);

        var goneConfirmedAt = firstSeenAt.AddMinutes(1);
        await ingestor.IngestAsync(SuccessRound($"poll-{pollPrefix}-gone", goneConfirmedAt));
        AssertTrackingGone(
            await ReadSeriesAsync(client, WorkType, sublot),
            demandId,
            firstSeenAt,
            goneConfirmedAt);
        return new GoneFixture(string.Empty, seriesId, demandId, firstSeenAt, goneConfirmedAt);
    }

    private static void AssertTrackingGone(
        JsonElement series,
        string demandId,
        DateTimeOffset expectedLastSeenAt,
        DateTimeOffset expectedGoneConfirmedAt)
    {
        Assert.Equal("TRACKING", series.GetProperty("lifecycle").GetString());
        Assert.Equal("GONE", series.GetProperty("currentPresence").GetString());
        Assert.Equal(JsonValueKind.Null, series.GetProperty("archivedAt").ValueKind);
        var demand = series.GetProperty("currentDemand");
        Assert.Equal(demandId, demand.GetProperty("demandId").GetString());
        Assert.Equal("GONE", demand.GetProperty("status").GetString());
        Assert.Equal(expectedLastSeenAt.ToUniversalTime(), demand.GetProperty("demandLastSeenAt").GetDateTimeOffset());
        Assert.Equal(expectedGoneConfirmedAt.ToUniversalTime(), demand.GetProperty("goneConfirmedAt").GetDateTimeOffset());
        Assert.Equal("NOT_READABLE", demand.GetProperty("externalReadabilityState").GetString());
        Assert.Equal(["DEMAND_GONE"], ReadBlockers(demand));
    }

    private static void AssertArchivedGone(
        JsonElement series,
        string seriesId,
        string demandId,
        DateTimeOffset expectedLastSeenAt,
        DateTimeOffset expectedGoneConfirmedAt,
        DateTimeOffset expectedArchivedAt,
        RoundCommitReceipt archiveReceipt)
    {
        Assert.Equal(seriesId, series.GetProperty("seriesId").GetString());
        Assert.Equal("ARCHIVED", series.GetProperty("lifecycle").GetString());
        Assert.Equal("GONE", series.GetProperty("currentPresence").GetString());
        Assert.Equal(expectedArchivedAt.ToUniversalTime(), series.GetProperty("archivedAt").GetDateTimeOffset());
        Assert.Equal(archiveReceipt.ProjectionCommitId, series.GetProperty("latestProjectionCommitId").GetString());
        var demand = series.GetProperty("currentDemand");
        Assert.Equal(demandId, demand.GetProperty("demandId").GetString());
        Assert.Equal(1, demand.GetProperty("generation").GetInt32());
        Assert.Equal("GONE", demand.GetProperty("status").GetString());
        Assert.Equal(expectedLastSeenAt.ToUniversalTime(), demand.GetProperty("demandLastSeenAt").GetDateTimeOffset());
        Assert.Equal(expectedGoneConfirmedAt.ToUniversalTime(), demand.GetProperty("goneConfirmedAt").GetDateTimeOffset());
        Assert.Equal(archiveReceipt.ProjectionCommitId, demand.GetProperty("latestProjectionCommitId").GetString());
        Assert.Equal("NOT_READABLE", demand.GetProperty("externalReadabilityState").GetString());
        Assert.Equal(
            ["DEMAND_GONE", "SERIES_ARCHIVED"],
            ReadBlockers(demand).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(series.GetProperty("currentConditions").EnumerateArray());
    }

    private static JsonElement AssertArchiveEvent(
        JsonElement series,
        string seriesId,
        DateTimeOffset goneConfirmedAt,
        DateTimeOffset archivedAt,
        RoundCommitReceipt receipt)
    {
        var archiveEvent = Assert.Single(series.GetProperty("events").EnumerateArray()
            .Where(item => item.GetProperty("eventType").GetString() == "GONE_TIMEOUT_ARCHIVED"));
        Assert.Equal("SERIES", archiveEvent.GetProperty("subjectKind").GetString());
        Assert.Equal(seriesId, archiveEvent.GetProperty("subjectId").GetString());
        Assert.Equal(archivedAt.ToUniversalTime(), archiveEvent.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(receipt.PollTraceId, archiveEvent.GetProperty("pollTraceId").GetString());
        Assert.Equal(receipt.ProjectionCommitId, archiveEvent.GetProperty("projectionCommitId").GetString());
        Assert.True(archiveEvent.GetProperty("payloadVersion").GetInt32() >= 1);
        using var payload = JsonDocument.Parse(archiveEvent.GetProperty("payloadJson").GetString()!);
        Assert.Equal(goneConfirmedAt.ToUniversalTime(), payload.RootElement.GetProperty("goneConfirmedAt").GetDateTimeOffset());
        Assert.Equal(archivedAt.ToUniversalTime(), payload.RootElement.GetProperty("archivedAt").GetDateTimeOffset());
        return archiveEvent;
    }

    private static void AssertLongGoneVisible(
        JsonElement series,
        string seriesId,
        string currentDemandId,
        DateTimeOffset archivedAt)
    {
        Assert.Equal(seriesId, series.GetProperty("seriesId").GetString());
        Assert.Equal("ARCHIVED", series.GetProperty("lifecycle").GetString());
        Assert.Equal("LONG_GONE_BUT_VISIBLE", series.GetProperty("currentPresence").GetString());
        Assert.Equal(archivedAt.ToUniversalTime(), series.GetProperty("archivedAt").GetDateTimeOffset());
        var currentDemand = series.GetProperty("currentDemand");
        Assert.Equal(currentDemandId, currentDemand.GetProperty("demandId").GetString());
        Assert.Equal(2, currentDemand.GetProperty("generation").GetInt32());
        Assert.Equal("LONG_GONE_BUT_VISIBLE", currentDemand.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, currentDemand.GetProperty("goneConfirmedAt").ValueKind);
        Assert.Equal("NOT_READABLE", currentDemand.GetProperty("externalReadabilityState").GetString());
        Assert.Equal(
            ["LONG_GONE_BUT_VISIBLE", "SERIES_ARCHIVED"],
            ReadBlockers(currentDemand).Order(StringComparer.Ordinal).ToArray());
    }

    private static JsonElement AssertLongGoneCondition(
        JsonElement series,
        string seriesId,
        string evidenceDemandId,
        DateTimeOffset observedAt,
        RoundCommitReceipt receipt)
    {
        var condition = Assert.Single(series.GetProperty("currentConditions").EnumerateArray());
        Assert.Equal("LONG_GONE_BUT_VISIBLE", condition.GetProperty("code").GetString());
        Assert.Equal("LIFECYCLE_CONFLICT", condition.GetProperty("category").GetString());
        Assert.Equal("ERROR", condition.GetProperty("severity").GetString());
        Assert.Equal($"SERIES:{seriesId}", condition.GetProperty("target").GetString());
        Assert.Equal("ARCHIVED_SERIES_VISIBILITY", condition.GetProperty("subjectKind").GetString());
        Assert.Equal(observedAt.ToUniversalTime(), condition.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(observedAt.ToUniversalTime(), condition.GetProperty("latestEvidenceAt").GetDateTimeOffset());
        Assert.Equal(receipt.PollTraceId, condition.GetProperty("latestPollTraceId").GetString());
        Assert.Equal(receipt.ProjectionCommitId, condition.GetProperty("latestProjectionCommitId").GetString());
        Assert.Equal(evidenceDemandId, condition.GetProperty("demandId").GetString());
        return condition;
    }

    private static void AssertStableOpenLongGonePeriod(
        JsonElement series,
        string seriesId,
        string evidenceDemandId,
        string periodId,
        DateTimeOffset startedAt,
        string initialEvidenceJson)
    {
        var condition = Assert.Single(series.GetProperty("currentConditions").EnumerateArray());
        Assert.Equal(periodId, condition.GetProperty("periodId").GetString());
        Assert.Equal("LONG_GONE_BUT_VISIBLE", condition.GetProperty("code").GetString());
        Assert.Equal($"SERIES:{seriesId}", condition.GetProperty("target").GetString());
        Assert.Equal("ARCHIVED_SERIES_VISIBILITY", condition.GetProperty("subjectKind").GetString());
        Assert.Equal(evidenceDemandId, condition.GetProperty("demandId").GetString());

        var period = Assert.Single(series.GetProperty("errorPeriods").EnumerateArray());
        Assert.Equal(periodId, period.GetProperty("periodId").GetString());
        Assert.Equal("LONG_GONE_BUT_VISIBLE", period.GetProperty("code").GetString());
        Assert.Equal($"SERIES:{seriesId}", period.GetProperty("target").GetString());
        Assert.Equal("ARCHIVED_SERIES_VISIBILITY", period.GetProperty("subjectKind").GetString());
        Assert.Equal(startedAt.ToUniversalTime(), period.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, period.GetProperty("endedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, period.GetProperty("endReason").ValueKind);
        var evidence = period.GetProperty("evidence").EnumerateArray().ToArray();
        Assert.NotEmpty(evidence);
        Assert.Equal(initialEvidenceJson, evidence[0].GetRawText());
    }

    private static void AssertStrictSeriesSequence(JsonElement series)
    {
        var sequences = series.GetProperty("events").EnumerateArray()
            .Select(item => item.GetProperty("seriesSequence").GetInt64())
            .ToArray();
        Assert.NotEmpty(sequences);
        Assert.Equal(
            Enumerable.Range(1, sequences.Length).Select(value => (long)value).ToArray(),
            sequences);
    }

    private static JsonElement AssertSeriesEventBoundToCommit(
        JsonElement series,
        string eventType,
        string subjectId,
        RoundCommitReceipt receipt)
    {
        var seriesEvent = Assert.Single(series.GetProperty("events").EnumerateArray()
            .Where(item => item.GetProperty("eventType").GetString() == eventType
                && item.GetProperty("subjectId").GetString() == subjectId
                && item.GetProperty("pollTraceId").GetString() == receipt.PollTraceId));
        Assert.Equal(receipt.ProjectionCommitId, seriesEvent.GetProperty("projectionCommitId").GetString());
        return seriesEvent;
    }

    private static string[] ReadBlockers(JsonElement demand) =>
        demand.GetProperty("readabilityBlockers").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

    private static void AssertAuthority(
        JsonElement authority,
        string expectedPhase,
        bool absenceAuthorityAvailable)
    {
        Assert.False(string.IsNullOrWhiteSpace(authority.GetProperty("hostSessionId").GetString()));
        Assert.Equal(expectedPhase, authority.GetProperty("phase").GetString());
        Assert.Equal(
            absenceAuthorityAvailable,
            authority.GetProperty("absenceAuthorityAvailable").GetBoolean());
    }

    private static void AssertCommit(
        JsonElement trace,
        RoundCommitReceipt receipt,
        string hostSessionId,
        string restartPhaseBefore,
        string restartPhaseAfter,
        bool absenceAuthority)
    {
        Assert.Equal(receipt.PollTraceId, trace.GetProperty("pollTraceId").GetString());
        Assert.Equal("SUCCESS", trace.GetProperty("outcome").GetString());
        Assert.NotNull(receipt.ProjectionCommitId);
        var commit = trace.GetProperty("projectionCommit");
        Assert.Equal(receipt.ProjectionCommitId, commit.GetProperty("projectionCommitId").GetString());
        Assert.Equal(receipt.PollTraceId, commit.GetProperty("pollTraceId").GetString());
        Assert.Equal(hostSessionId, commit.GetProperty("hostSessionId").GetString());
        Assert.Equal(restartPhaseBefore, commit.GetProperty("restartPhaseBefore").GetString());
        Assert.Equal(restartPhaseAfter, commit.GetProperty("restartPhaseAfter").GetString());
        Assert.Equal(absenceAuthority, commit.GetProperty("absenceAuthority").GetBoolean());
    }

    private static MesTaskUnionObservation ValidObservation(
        string sublot,
        DateTimeOffset mesSourceDate,
        string area = "N3-3",
        string eqp = "WB-03",
        string package = "QFN-G1") =>
        new(
            WorkType,
            sublot,
            area,
            eqp,
            Step: "焊线2",
            mesSourceDate,
            package);

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket06-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static MesTaskUnionRound NonSuccessRound(
        string pollTraceId,
        MesTaskUnionRoundOutcome outcome,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket06-v1",
            outcome,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static async Task<JsonElement> ReadAuthorityAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/v2/absence-authority");

    private static async Task<JsonElement> ReadSeriesAsync(
        HttpClient client,
        string workType,
        string sublot) =>
        await client.GetFromJsonAsync<JsonElement>(
            "/api/v2/demand-series/by-key"
            + $"?workType={Uri.EscapeDataString(workType)}"
            + $"&sublot={Uri.EscapeDataString(sublot)}");

    private static async Task<JsonElement> ReadSeriesByIdAsync(HttpClient client, string seriesId) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/api/v2/demand-series/{Uri.EscapeDataString(seriesId)}");

    private static async Task<JsonElement> ReadTraceAsync(HttpClient client, string pollTraceId) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/api/v2/poll-traces/{Uri.EscapeDataString(pollTraceId)}");

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Production));

    private void AssertDatabaseEvidence(Ticket01SqlServerDatabase database)
    {
        Assert.False(database.IsLocalDb);
        Assert.Equal(database.ExpectedProductMajor, database.ProductMajor);
        Assert.Equal(database.ExpectedCompatibilityLevel, database.CompatibilityLevel);
        Assert.InRange(database.EngineEdition, 1, 4);
        _output.WriteLine(
            $"Real SQL Server ProductVersion={database.ProductVersion}; "
            + $"ProductMajor={database.ProductMajor}; "
            + $"EngineEdition={database.EngineEdition}; "
            + $"CompatibilityLevel={database.CompatibilityLevel}");
    }

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__EnableLegacyDevelopmentEndpoints"] = "false",
            [$"{MesIngestHostOptions.SectionName}__SnapshotSource"] = "File",
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
        });

    private sealed record GoneFixture(
        string HostSessionId,
        string SeriesId,
        string DemandId,
        DateTimeOffset LastSeenAt,
        DateTimeOffset GoneConfirmedAt);

    private sealed class ProcessEnvironmentScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> _originalValues;
        private bool _disposed;

        public ProcessEnvironmentScope(IReadOnlyDictionary<string, string?> values)
        {
            _originalValues = values.Keys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in values)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var (key, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
