using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class RestartBarrierGoneAndPrearchiveReappearanceTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string WorkType = "WIRE_TO_NITROGEN";
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public RestartBarrierGoneAndPrearchiveReappearanceTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Ticket01SqlServerFact]
    public async Task Restarted_host_requires_two_successful_barrier_rounds_before_third_absence_marks_gone()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET05-RESTART-BOUNDARY";
        var seedCompletedAt = new DateTimeOffset(2026, 8, 13, 1, 0, 2, TimeSpan.Zero);
        string seriesId;
        string demandId;
        string firstHostSessionId;
        DateTimeOffset firstHostStartedAt;
        string[] firstHostAuthorityEvents;

        AssertNoCallerProvidedAbsenceAuthorityInput();

        await using (var initialFactory = CreateFactory())
        {
            using var initialClient = initialFactory.CreateClient();
            var initialAuthority = await ReadAuthorityAsync(initialClient);
            AssertAuthority(initialAuthority, "BARRIER", absenceAuthorityAvailable: false);
            firstHostSessionId = initialAuthority.GetProperty("hostSessionId").GetString()!;
            firstHostStartedAt = initialAuthority.GetProperty("startedAt").GetDateTimeOffset();
            AssertAuthorityEvent(
                Assert.Single(initialAuthority.GetProperty("events").EnumerateArray()),
                "RESTART_BARRIER_ENTERED",
                expectedPollTraceId: null,
                expectedProjectionCommitId: null,
                "NORMAL",
                "BARRIER");

            var seedReceipt = await initialFactory.Services.GetRequiredService<RoundIngestor>()
                .IngestAsync(SuccessRound(
                    "poll-ticket05-restart-seed",
                    seedCompletedAt,
                    ValidObservation(sublot, seedCompletedAt.AddMinutes(-4))));
            var seedTrace = await ReadTraceAsync(initialClient, seedReceipt.PollTraceId);
            AssertCommit(
                seedTrace,
                seedReceipt,
                firstHostSessionId,
                "BARRIER",
                "POST_BARRIER",
                absenceAuthority: false);

            var seeded = await ReadSeriesAsync(initialClient, WorkType, sublot);
            seriesId = seeded.GetProperty("seriesId").GetString()!;
            demandId = seeded.GetProperty("currentDemand").GetProperty("demandId").GetString()!;
            AssertVisible(seeded, demandId, seedCompletedAt);

            var initialRestoreReceipt = await initialFactory.Services.GetRequiredService<RoundIngestor>()
                .IngestAsync(SuccessRound(
                    "poll-ticket05-initial-restore-authority",
                    seedCompletedAt.AddMinutes(1)));
            AssertCommit(
                await ReadTraceAsync(initialClient, initialRestoreReceipt.PollTraceId),
                initialRestoreReceipt,
                firstHostSessionId,
                "POST_BARRIER",
                "NORMAL",
                absenceAuthority: false);
            AssertVisible(
                await ReadSeriesAsync(initialClient, WorkType, sublot),
                demandId,
                seedCompletedAt);
            var firstHostFinalAuthority = await ReadAuthorityAsync(initialClient);
            AssertAuthority(
                firstHostFinalAuthority,
                "NORMAL",
                absenceAuthorityAvailable: true);
            var firstHostEvents = firstHostFinalAuthority.GetProperty("events")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(3, firstHostEvents.Length);
            AssertAuthorityEvent(
                firstHostEvents[0],
                "RESTART_BARRIER_ENTERED",
                expectedPollTraceId: null,
                expectedProjectionCommitId: null,
                "NORMAL",
                "BARRIER");
            AssertAuthorityEvent(
                firstHostEvents[1],
                "RESTART_BASELINE_COMPLETED",
                seedReceipt.PollTraceId,
                seedReceipt.ProjectionCommitId,
                "BARRIER",
                "POST_BARRIER");
            AssertAuthorityEvent(
                firstHostEvents[2],
                "RESTART_ABSENCE_AUTHORITY_RESTORED",
                initialRestoreReceipt.PollTraceId,
                initialRestoreReceipt.ProjectionCommitId,
                "POST_BARRIER",
                "NORMAL");
            Assert.All(firstHostEvents, item =>
                Assert.Equal(firstHostSessionId, item.GetProperty("hostSessionId").GetString()));
            firstHostAuthorityEvents = firstHostEvents
                .Select(item => item.GetRawText())
                .ToArray();
        }

        await using (var restartedFactory = CreateFactory())
        {
            using var client = restartedFactory.CreateClient();
            var ingestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            var entered = await ReadAuthorityAsync(client);
            var restartedHostSessionId = entered.GetProperty("hostSessionId").GetString()!;
            Assert.NotEqual(firstHostSessionId, restartedHostSessionId);
            AssertAuthority(entered, "BARRIER", absenceAuthorityAvailable: false);
            var enteredEvent = Assert.Single(entered.GetProperty("events").EnumerateArray());
            AssertAuthorityEvent(
                enteredEvent,
                "RESTART_BARRIER_ENTERED",
                expectedPollTraceId: null,
                expectedProjectionCommitId: null,
                "NORMAL",
                "BARRIER");
            var enteredAt = enteredEvent.GetProperty("occurredAt").GetDateTimeOffset();

            var priorHostAuthority = await ReadAuthorityAsync(client, firstHostSessionId);
            Assert.Equal(firstHostSessionId, priorHostAuthority.GetProperty("hostSessionId").GetString());
            Assert.Equal(firstHostStartedAt, priorHostAuthority.GetProperty("startedAt").GetDateTimeOffset());
            Assert.False(priorHostAuthority.GetProperty("isCurrent").GetBoolean());
            AssertAuthority(priorHostAuthority, "NORMAL", absenceAuthorityAvailable: true);
            var persistedPriorEvents = priorHostAuthority.GetProperty("events")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(
                firstHostAuthorityEvents,
                persistedPriorEvents.Select(item => item.GetRawText()).ToArray());
            Assert.Equal(
                [
                    "RESTART_BARRIER_ENTERED",
                    "RESTART_BASELINE_COMPLETED",
                    "RESTART_ABSENCE_AUTHORITY_RESTORED",
                ],
                persistedPriorEvents
                    .Select(item => item.GetProperty("eventType").GetString())
                    .ToArray());
            Assert.All(persistedPriorEvents, item =>
                Assert.Equal(firstHostSessionId, item.GetProperty("hostSessionId").GetString()));

            var baselineCompletedAt = seedCompletedAt.AddMinutes(2);
            var baselineReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket05-restart-baseline",
                baselineCompletedAt));
            var afterBaseline = await ReadSeriesAsync(client, WorkType, sublot);
            Assert.Equal(seriesId, afterBaseline.GetProperty("seriesId").GetString());
            AssertVisible(afterBaseline, demandId, seedCompletedAt);
            AssertCommit(
                await ReadTraceAsync(client, baselineReceipt.PollTraceId),
                baselineReceipt,
                restartedHostSessionId,
                "BARRIER",
                "POST_BARRIER",
                absenceAuthority: false);
            var baselineAuthority = await ReadAuthorityAsync(client);
            AssertAuthority(baselineAuthority, "POST_BARRIER", absenceAuthorityAvailable: false);
            var baselineEvents = baselineAuthority.GetProperty("events").EnumerateArray().ToArray();
            Assert.Equal(2, baselineEvents.Length);
            Assert.Equal(enteredAt, baselineEvents[0].GetProperty("occurredAt").GetDateTimeOffset());
            AssertAuthorityEvent(
                baselineEvents[1],
                "RESTART_BASELINE_COMPLETED",
                baselineReceipt.PollTraceId,
                baselineReceipt.ProjectionCommitId,
                "BARRIER",
                "POST_BARRIER");

            var restoreCompletedAt = seedCompletedAt.AddMinutes(3);
            var restoreReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket05-restart-restore-authority",
                restoreCompletedAt));
            var afterRestore = await ReadSeriesAsync(client, WorkType, sublot);
            AssertVisible(afterRestore, demandId, seedCompletedAt);
            AssertCommit(
                await ReadTraceAsync(client, restoreReceipt.PollTraceId),
                restoreReceipt,
                restartedHostSessionId,
                "POST_BARRIER",
                "NORMAL",
                absenceAuthority: false);
            var restoredAuthority = await ReadAuthorityAsync(client);
            AssertAuthority(restoredAuthority, "NORMAL", absenceAuthorityAvailable: true);
            var restoredEvents = restoredAuthority.GetProperty("events").EnumerateArray().ToArray();
            Assert.Equal(3, restoredEvents.Length);
            AssertAuthorityEvent(
                restoredEvents[2],
                "RESTART_ABSENCE_AUTHORITY_RESTORED",
                restoreReceipt.PollTraceId,
                restoreReceipt.ProjectionCommitId,
                "POST_BARRIER",
                "NORMAL");

            var authoritativeCompletedAt = seedCompletedAt.AddMinutes(4);
            var authoritativeReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket05-restart-authoritative-absence",
                authoritativeCompletedAt));
            var gone = await ReadSeriesAsync(client, WorkType, sublot);
            Assert.Equal(seriesId, gone.GetProperty("seriesId").GetString());
            AssertGone(gone, demandId, seedCompletedAt, authoritativeCompletedAt);
            AssertCommit(
                await ReadTraceAsync(client, authoritativeReceipt.PollTraceId),
                authoritativeReceipt,
                restartedHostSessionId,
                "NORMAL",
                "NORMAL",
                absenceAuthority: true);
            AssertSeriesEventBoundToCommit(
                gone,
                "DEMAND_GONE",
                demandId,
                authoritativeReceipt);

            var finalAuthority = await ReadAuthorityAsync(client);
            AssertAuthority(finalAuthority, "NORMAL", absenceAuthorityAvailable: true);
            Assert.Equal(3, finalAuthority.GetProperty("events").GetArrayLength());
        }

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Failure_incomplete_replay_and_conflict_never_advance_restart_barrier()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET05-ISOLATION";
        var seedCompletedAt = new DateTimeOffset(2026, 8, 13, 2, 0, 2, TimeSpan.Zero);
        string demandId;

        await using (var initialFactory = CreateFactory())
        {
            using var initialClient = initialFactory.CreateClient();
            var initialIngestor = initialFactory.Services.GetRequiredService<RoundIngestor>();
            var seedReceipt = await initialIngestor
                .IngestAsync(SuccessRound(
                    "poll-ticket05-isolation-seed",
                    seedCompletedAt,
                    ValidObservation(sublot, seedCompletedAt.AddMinutes(-4))));
            var seeded = await ReadSeriesAsync(initialClient, WorkType, sublot);
            demandId = seeded.GetProperty("currentDemand").GetProperty("demandId").GetString()!;
            Assert.NotNull(seedReceipt.ProjectionCommitId);

            var restoreReceipt = await initialIngestor.IngestAsync(SuccessRound(
                "poll-ticket05-isolation-initial-restore",
                seedCompletedAt.AddMinutes(1),
                ValidObservation(sublot, seedCompletedAt.AddMinutes(-3))));
            Assert.NotNull(restoreReceipt.ProjectionCommitId);
            AssertAuthority(
                await ReadAuthorityAsync(initialClient),
                "NORMAL",
                absenceAuthorityAvailable: true);
        }

        await using (var restartedFactory = CreateFactory())
        {
            using var client = restartedFactory.CreateClient();
            var ingestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            var entered = await ReadAuthorityAsync(client);
            var hostSessionId = entered.GetProperty("hostSessionId").GetString()!;
            AssertAuthority(entered, "BARRIER", absenceAuthorityAvailable: false);
            Assert.Single(entered.GetProperty("events").EnumerateArray());

            var failureRound = NonSuccessRound(
                "poll-ticket05-barrier-failure",
                MesTaskUnionRoundOutcome.Failure,
                seedCompletedAt.AddMinutes(2));
            var failureReceipt = await ingestor.IngestAsync(failureRound);
            Assert.Null(failureReceipt.ProjectionCommitId);
            Assert.False(failureReceipt.IsReplay);

            var incompleteRound = NonSuccessRound(
                "poll-ticket05-barrier-incomplete",
                MesTaskUnionRoundOutcome.Incomplete,
                seedCompletedAt.AddMinutes(3),
                ValidObservation("SL-TICKET05-NOT-PROJECTED", seedCompletedAt));
            var incompleteReceipt = await ingestor.IngestAsync(incompleteRound);
            Assert.Null(incompleteReceipt.ProjectionCommitId);
            Assert.False(incompleteReceipt.IsReplay);

            var acceptedFailureTrace = await ReadTraceAsync(client, failureRound.PollTraceId);
            var acceptedFailureJson = acceptedFailureTrace.GetRawText();
            var conflict = await Assert.ThrowsAsync<PollTraceConflictException>(() =>
                ingestor.IngestAsync(failureRound with
                {
                    QueryVersion = "mes-task-union-ticket05-conflicting-version",
                }));
            Assert.Equal(failureRound.PollTraceId, conflict.PollTraceId);
            Assert.Equal(
                acceptedFailureJson,
                (await ReadTraceAsync(client, failureRound.PollTraceId)).GetRawText());

            var failureReplay = await ingestor.IngestAsync(failureRound with
            {
                StartedAt = failureRound.StartedAt.AddHours(1),
                CompletedAt = failureRound.CompletedAt.AddHours(1),
            });
            var incompleteReplay = await ingestor.IngestAsync(incompleteRound with
            {
                StartedAt = incompleteRound.StartedAt.AddHours(1),
                CompletedAt = incompleteRound.CompletedAt.AddHours(1),
            });
            Assert.True(failureReplay.IsReplay);
            Assert.True(incompleteReplay.IsReplay);
            Assert.Null(failureReplay.ProjectionCommitId);
            Assert.Null(incompleteReplay.ProjectionCommitId);
            Assert.Equal(
                JsonValueKind.Null,
                (await ReadTraceAsync(client, failureRound.PollTraceId))
                    .GetProperty("projectionCommit").ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                (await ReadTraceAsync(client, incompleteRound.PollTraceId))
                    .GetProperty("projectionCommit").ValueKind);

            var beforeSuccess = await ReadAuthorityAsync(client);
            AssertAuthority(beforeSuccess, "BARRIER", absenceAuthorityAvailable: false);
            Assert.Single(beforeSuccess.GetProperty("events").EnumerateArray());
            AssertVisible(
                await ReadSeriesAsync(client, WorkType, sublot),
                demandId,
                seedCompletedAt.AddMinutes(1));

            var baselineRound = SuccessRound(
                "poll-ticket05-isolation-baseline",
                seedCompletedAt.AddMinutes(4));
            var baselineReceipt = await ingestor.IngestAsync(baselineRound);
            AssertCommit(
                await ReadTraceAsync(client, baselineRound.PollTraceId),
                baselineReceipt,
                hostSessionId,
                "BARRIER",
                "POST_BARRIER",
                absenceAuthority: false);

            var baselineReplay = await ingestor.IngestAsync(baselineRound with
            {
                StartedAt = baselineRound.StartedAt.AddHours(1),
                CompletedAt = baselineRound.CompletedAt.AddHours(1),
            });
            Assert.True(baselineReplay.IsReplay);
            Assert.Equal(baselineReceipt.ProjectionCommitId, baselineReplay.ProjectionCommitId);
            var afterReplay = await ReadAuthorityAsync(client);
            AssertAuthority(afterReplay, "POST_BARRIER", absenceAuthorityAvailable: false);
            var events = afterReplay.GetProperty("events").EnumerateArray().ToArray();
            Assert.Equal(2, events.Length);
            AssertAuthorityEvent(
                events[1],
                "RESTART_BASELINE_COMPLETED",
                baselineReceipt.PollTraceId,
                baselineReceipt.ProjectionCommitId,
                "BARRIER",
                "POST_BARRIER");
            Assert.DoesNotContain(
                events,
                item => item.GetProperty("eventType").GetString()
                    == "RESTART_ABSENCE_AUTHORITY_RESTORED");
            AssertVisible(
                await ReadSeriesAsync(client, WorkType, sublot),
                demandId,
                seedCompletedAt.AddMinutes(1));
        }

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Restart_barrier_still_creates_and_updates_visible_demands_without_marking_absent_demands_gone()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string existingSublot = "SL-TICKET05-BARRIER-EXISTING";
        const string newSublot = "SL-TICKET05-BARRIER-NEW";
        var seedCompletedAt = new DateTimeOffset(2026, 8, 13, 3, 0, 2, TimeSpan.Zero);
        string existingDemandId;
        string seedProjectionCommitId;

        await using (var initialFactory = CreateFactory())
        {
            using var initialClient = initialFactory.CreateClient();
            var initialIngestor = initialFactory.Services.GetRequiredService<RoundIngestor>();
            var seedReceipt = await initialIngestor
                .IngestAsync(SuccessRound(
                    "poll-ticket05-barrier-existing-seed",
                    seedCompletedAt,
                    ValidObservation(existingSublot, seedCompletedAt.AddMinutes(-5))));
            var existing = await ReadSeriesAsync(initialClient, WorkType, existingSublot);
            existingDemandId = existing.GetProperty("currentDemand").GetProperty("demandId").GetString()!;
            Assert.NotNull(seedReceipt.ProjectionCommitId);

            var restoreReceipt = await initialIngestor.IngestAsync(SuccessRound(
                "poll-ticket05-barrier-existing-initial-restore",
                seedCompletedAt.AddMinutes(1),
                ValidObservation(existingSublot, seedCompletedAt.AddMinutes(-4))));
            seedProjectionCommitId = restoreReceipt.ProjectionCommitId!;
            AssertAuthority(
                await ReadAuthorityAsync(initialClient),
                "NORMAL",
                absenceAuthorityAvailable: true);
        }

        await using (var restartedFactory = CreateFactory())
        {
            using var client = restartedFactory.CreateClient();
            var ingestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            var entered = await ReadAuthorityAsync(client);
            var hostSessionId = entered.GetProperty("hostSessionId").GetString()!;
            AssertAuthority(entered, "BARRIER", absenceAuthorityAvailable: false);

            var createdCompletedAt = seedCompletedAt.AddMinutes(2);
            var createdObservation = ValidObservation(
                newSublot,
                createdCompletedAt.AddMinutes(-2),
                area: "N3-4",
                eqp: "WB-04",
                package: "QFN-A");
            var createdReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket05-barrier-create",
                createdCompletedAt,
                createdObservation));
            AssertCommit(
                await ReadTraceAsync(client, createdReceipt.PollTraceId),
                createdReceipt,
                hostSessionId,
                "BARRIER",
                "POST_BARRIER",
                absenceAuthority: false);

            var existingAfterCreate = await ReadSeriesAsync(client, WorkType, existingSublot);
            AssertVisible(existingAfterCreate, existingDemandId, seedCompletedAt.AddMinutes(1));
            Assert.Equal(seedProjectionCommitId, existingAfterCreate.GetProperty("latestProjectionCommitId").GetString());

            var created = await ReadSeriesAsync(client, WorkType, newSublot);
            var createdDemand = created.GetProperty("currentDemand");
            var createdDemandId = createdDemand.GetProperty("demandId").GetString()!;
            AssertVisible(created, createdDemandId, createdCompletedAt);
            Assert.Equal("N3-4", createdDemand.GetProperty("liveMesFields").GetProperty("area").GetString());
            Assert.Equal("WB-04", createdDemand.GetProperty("liveMesFields").GetProperty("eqp").GetString());
            Assert.Equal("QFN-A", createdDemand.GetProperty("liveMesFields").GetProperty("package").GetString());
            AssertSeriesEventBoundToCommit(
                created,
                "TRANSPORT_DEMAND_CREATED",
                createdDemandId,
                createdReceipt);

            var updatedCompletedAt = seedCompletedAt.AddMinutes(3);
            var updatedObservation = ValidObservation(
                newSublot,
                updatedCompletedAt.AddMinutes(-1),
                area: "N3-5",
                eqp: "WB-05",
                package: "QFN-B");
            var updatedReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket05-barrier-update",
                updatedCompletedAt,
                updatedObservation));
            AssertCommit(
                await ReadTraceAsync(client, updatedReceipt.PollTraceId),
                updatedReceipt,
                hostSessionId,
                "POST_BARRIER",
                "NORMAL",
                absenceAuthority: false);

            var existingAfterUpdate = await ReadSeriesAsync(client, WorkType, existingSublot);
            AssertVisible(existingAfterUpdate, existingDemandId, seedCompletedAt.AddMinutes(1));
            Assert.Equal(seedProjectionCommitId, existingAfterUpdate.GetProperty("latestProjectionCommitId").GetString());

            var updated = await ReadSeriesAsync(client, WorkType, newSublot);
            var current = updated.GetProperty("currentDemand");
            Assert.Equal(createdDemandId, current.GetProperty("demandId").GetString());
            Assert.Equal(1, current.GetProperty("generation").GetInt32());
            Assert.Equal(updatedCompletedAt, current.GetProperty("demandLastSeenAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, current.GetProperty("goneConfirmedAt").ValueKind);
            Assert.Equal(updatedReceipt.ProjectionCommitId, current.GetProperty("latestProjectionCommitId").GetString());
            Assert.Equal("N3-5", current.GetProperty("liveMesFields").GetProperty("area").GetString());
            Assert.Equal("WB-05", current.GetProperty("liveMesFields").GetProperty("eqp").GetString());
            Assert.Equal("QFN-B", current.GetProperty("liveMesFields").GetProperty("package").GetString());
            var changedEvents = updated.GetProperty("events").EnumerateArray()
                .Where(item => item.GetProperty("eventType").GetString() == "MES_FIELD_CHANGED"
                    && item.GetProperty("pollTraceId").GetString() == updatedReceipt.PollTraceId)
                .ToArray();
            Assert.NotEmpty(changedEvents);
            Assert.All(changedEvents, item =>
            {
                Assert.Equal(createdDemandId, item.GetProperty("subjectId").GetString());
                Assert.Equal(updatedReceipt.ProjectionCommitId, item.GetProperty("projectionCommitId").GetString());
            });

            var restored = await ReadAuthorityAsync(client);
            AssertAuthority(restored, "NORMAL", absenceAuthorityAvailable: true);
            Assert.Equal(
                ["RESTART_BARRIER_ENTERED", "RESTART_BASELINE_COMPLETED", "RESTART_ABSENCE_AUTHORITY_RESTORED"],
                restored.GetProperty("events").EnumerateArray()
                    .Select(item => item.GetProperty("eventType").GetString()).ToArray());
        }

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Same_completed_at_uses_latest_accepted_success_instead_of_poll_trace_lexical_order_for_current_raw_multiplicity()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET05-SAME-COMPLETED-AT";
        const string olderUniquePollTraceId = "poll-ticket05-z-same-time-unique";
        const string newerDuplicatePollTraceId = "poll-ticket05-a-same-time-duplicate";
        var sharedCompletedAt = new DateTimeOffset(2026, 8, 13, 4, 0, 2, TimeSpan.Zero);
        var olderUnique = ValidObservation(
            sublot,
            sharedCompletedAt.AddMinutes(-3),
            area: "N3-3",
            eqp: "WB-03",
            package: "QFN-OLD-UNIQUE");
        var newerDuplicateA = ValidObservation(
            sublot,
            sharedCompletedAt.AddMinutes(-2),
            area: "N3-8",
            eqp: "WB-08",
            package: "QFN-NEW-A");
        var newerDuplicateB = ValidObservation(
            sublot,
            sharedCompletedAt.AddMinutes(-1),
            area: "N3-9",
            eqp: "WB-09",
            package: "QFN-NEW-B");

        Assert.True(
            StringComparer.Ordinal.Compare(newerDuplicatePollTraceId, olderUniquePollTraceId) < 0,
            "The later accepted round must sort before the older round by PollTraceId to reproduce the tie-break regression.");

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        var olderReceipt = await ingestor.IngestAsync(SuccessRound(
            olderUniquePollTraceId,
            sharedCompletedAt,
            olderUnique));
        Assert.False(olderReceipt.IsReplay);
        Assert.NotNull(olderReceipt.ProjectionCommitId);
        var afterUnique = await ReadSeriesAsync(client, WorkType, sublot);
        var seriesId = afterUnique.GetProperty("seriesId").GetString()!;
        var demandId = afterUnique.GetProperty("currentDemand").GetProperty("demandId").GetString()!;
        Assert.Equal("QFN-OLD-UNIQUE", afterUnique.GetProperty("currentDemand")
            .GetProperty("liveMesFields").GetProperty("package").GetString());

        var newerReceipt = await ingestor.IngestAsync(SuccessRound(
            newerDuplicatePollTraceId,
            sharedCompletedAt,
            newerDuplicateA,
            newerDuplicateB));
        Assert.False(newerReceipt.IsReplay);
        Assert.NotEqual(olderReceipt.ProjectionCommitId, newerReceipt.ProjectionCommitId);
        Assert.Equal([seriesId], newerReceipt.SeriesIds);
        Assert.Equal([demandId], newerReceipt.DemandIds);

        var olderTrace = await ReadTraceAsync(client, olderUniquePollTraceId);
        var newerTrace = await ReadTraceAsync(client, newerDuplicatePollTraceId);
        Assert.Equal(sharedCompletedAt, olderTrace.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(sharedCompletedAt, newerTrace.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(1, olderTrace.GetProperty("observations").GetArrayLength());
        Assert.Equal(2, newerTrace.GetProperty("observations").GetArrayLength());
        Assert.All(newerTrace.GetProperty("observations").EnumerateArray(), observation =>
        {
            Assert.Equal(seriesId, observation.GetProperty("seriesId").GetString());
            Assert.Equal(demandId, observation.GetProperty("demandId").GetString());
            Assert.Equal(newerReceipt.ProjectionCommitId, observation.GetProperty("projectionCommitId").GetString());
        });

        var afterDuplicate = await ReadSeriesAsync(client, WorkType, sublot);
        Assert.Equal(seriesId, afterDuplicate.GetProperty("seriesId").GetString());
        Assert.Equal(newerReceipt.ProjectionCommitId, afterDuplicate.GetProperty("latestProjectionCommitId").GetString());
        var currentDemand = afterDuplicate.GetProperty("currentDemand");
        Assert.Equal(demandId, currentDemand.GetProperty("demandId").GetString());
        Assert.Equal(sharedCompletedAt, currentDemand.GetProperty("demandLastSeenAt").GetDateTimeOffset());
        Assert.Equal(newerReceipt.ProjectionCommitId, currentDemand.GetProperty("latestProjectionCommitId").GetString());
        Assert.Equal(JsonValueKind.Null, currentDemand.GetProperty("liveMesFields").ValueKind);
        Assert.Equal("NOT_READABLE", currentDemand.GetProperty("externalReadabilityState").GetString());
        Assert.Equal(
            ["DUPLICATE_TRANSPORT_DEMAND_KEY"],
            currentDemand.GetProperty("readabilityBlockers").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.Equal(currentDemand.GetRawText(), Assert.Single(
            afterDuplicate.GetProperty("demands").EnumerateArray()).GetRawText());

        var duplicateCondition = Assert.Single(
            afterDuplicate.GetProperty("currentConditions").EnumerateArray());
        Assert.Equal("DUPLICATE_TRANSPORT_DEMAND_KEY", duplicateCondition.GetProperty("code").GetString());
        Assert.Equal(demandId, duplicateCondition.GetProperty("demandId").GetString());
        Assert.Equal(newerDuplicatePollTraceId, duplicateCondition.GetProperty("latestPollTraceId").GetString());
        Assert.Equal(newerReceipt.ProjectionCommitId, duplicateCondition.GetProperty("latestProjectionCommitId").GetString());
        Assert.Equal(sharedCompletedAt, duplicateCondition.GetProperty("latestEvidenceAt").GetDateTimeOffset());

        var newerRoundRows = afterDuplicate.GetProperty("rawObservations").EnumerateArray()
            .Where(observation => observation.GetProperty("pollTraceId").GetString()
                == newerDuplicatePollTraceId)
            .OrderBy(observation => observation.GetProperty("ordinal").GetInt32())
            .ToArray();
        Assert.Equal(2, newerRoundRows.Length);
        Assert.Equal([0, 1], newerRoundRows.Select(row => row.GetProperty("ordinal").GetInt32()).ToArray());
        Assert.All(newerRoundRows, observation =>
            Assert.Equal(newerReceipt.ProjectionCommitId, observation.GetProperty("projectionCommitId").GetString()));

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task First_authoritative_absence_marks_visible_demand_gone_preserves_last_seen_and_closes_conditions_as_demand_gone()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET05-DIRECT-GONE";
        var localOffset = TimeSpan.FromHours(8);
        var seedCompletedAt = new DateTimeOffset(2026, 8, 13, 12, 0, 2, localOffset);
        var restoreCompletedAt = seedCompletedAt.AddMinutes(1);
        var absenceCompletedAt = seedCompletedAt.AddMinutes(2);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var authority = await ReadAuthorityAsync(client);
        var hostSessionId = authority.GetProperty("hostSessionId").GetString()!;
        AssertAuthority(authority, "BARRIER", absenceAuthorityAvailable: false);
        AssertAuthorityEvent(
            Assert.Single(authority.GetProperty("events").EnumerateArray()),
            "RESTART_BARRIER_ENTERED",
            expectedPollTraceId: null,
            expectedProjectionCommitId: null,
            "NORMAL",
            "BARRIER");

        var seedReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket05-direct-gone-seed",
            seedCompletedAt,
            new MesTaskUnionObservation(
                WorkType,
                sublot,
                Area: null,
                Eqp: "",
                Step: "焊线2",
                MesSourceDate: null,
                Package: " ")));
        AssertCommit(
            await ReadTraceAsync(client, seedReceipt.PollTraceId),
            seedReceipt,
            hostSessionId,
            "BARRIER",
            "POST_BARRIER",
            absenceAuthority: false);

        var restoreReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket05-direct-gone-restore-authority",
            restoreCompletedAt,
            new MesTaskUnionObservation(
                WorkType,
                sublot,
                Area: null,
                Eqp: "",
                Step: "焊线2",
                MesSourceDate: null,
                Package: " ")));
        AssertCommit(
            await ReadTraceAsync(client, restoreReceipt.PollTraceId),
            restoreReceipt,
            hostSessionId,
            "POST_BARRIER",
            "NORMAL",
            absenceAuthority: false);
        var before = await ReadSeriesAsync(client, WorkType, sublot);
        var demandId = before.GetProperty("currentDemand").GetProperty("demandId").GetString()!;
        var openPeriods = before.GetProperty("errorPeriods").EnumerateArray().ToArray();
        Assert.Equal(4, before.GetProperty("currentConditions").GetArrayLength());
        Assert.Equal(4, openPeriods.Length);
        Assert.All(openPeriods, period =>
        {
            Assert.Equal(JsonValueKind.Null, period.GetProperty("endedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, period.GetProperty("endReason").ValueKind);
        });

        var absenceReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket05-direct-gone-absence",
            absenceCompletedAt));
        AssertCommit(
            await ReadTraceAsync(client, absenceReceipt.PollTraceId),
            absenceReceipt,
            hostSessionId,
            "NORMAL",
            "NORMAL",
            absenceAuthority: true);
        Assert.Contains(before.GetProperty("seriesId").GetString(), absenceReceipt.SeriesIds);
        Assert.Contains(demandId, absenceReceipt.DemandIds);

        var gone = await ReadSeriesAsync(client, WorkType, sublot);
        AssertGone(
            gone,
            demandId,
            restoreCompletedAt.ToUniversalTime(),
            absenceCompletedAt.ToUniversalTime());
        var goneDemand = gone.GetProperty("currentDemand");
        Assert.Equal("NOT_READABLE", goneDemand.GetProperty("externalReadabilityState").GetString());
        Assert.Equal(
            ["DEMAND_GONE"],
            goneDemand.GetProperty("readabilityBlockers").EnumerateArray()
                .Select(item => item.GetString()).ToArray());
        Assert.Empty(gone.GetProperty("currentConditions").EnumerateArray());

        var endedPeriods = gone.GetProperty("errorPeriods").EnumerateArray().ToArray();
        Assert.Equal(openPeriods.Select(PeriodId).Order(StringComparer.Ordinal), endedPeriods.Select(PeriodId).Order(StringComparer.Ordinal));
        Assert.All(endedPeriods, period =>
        {
            Assert.Equal(absenceCompletedAt.ToUniversalTime(), period.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal("DEMAND_GONE", period.GetProperty("endReason").GetString());
            var closingEvidence = Assert.Single(period.GetProperty("evidence").EnumerateArray()
                .Where(item => item.GetProperty("evidenceKind").GetString() == "DEMAND_GONE"));
            Assert.Equal(absenceCompletedAt.ToUniversalTime(), closingEvidence.GetProperty("observedAt").GetDateTimeOffset());
            Assert.Equal(absenceReceipt.PollTraceId, closingEvidence.GetProperty("pollTraceId").GetString());
            Assert.Equal(absenceReceipt.ProjectionCommitId, closingEvidence.GetProperty("projectionCommitId").GetString());
            Assert.Equal(demandId, closingEvidence.GetProperty("demandId").GetString());
        });
        AssertSeriesEventBoundToCommit(gone, "DEMAND_GONE", demandId, absenceReceipt);
        Assert.All(
            gone.GetProperty("events").EnumerateArray()
                .Where(item => item.GetProperty("pollTraceId").GetString() == absenceReceipt.PollTraceId),
            item => Assert.Equal(
                absenceReceipt.ProjectionCommitId,
                item.GetProperty("projectionCommitId").GetString()));

        var unchangedAuthority = await ReadAuthorityAsync(client);
        AssertAuthority(unchangedAuthority, "NORMAL", absenceAuthorityAvailable: true);
        Assert.Equal(3, unchangedAuthority.GetProperty("events").GetArrayLength());
        Assert.NotNull(seedReceipt.ProjectionCommitId);
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Prearchive_reappearance_creates_persisted_successor_generation_without_rewriting_predecessor()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET05-PREARCHIVE-REAPPEAR";
        var firstCompletedAt = new DateTimeOffset(2026, 8, 13, 5, 0, 2, TimeSpan.Zero);
        var restoreCompletedAt = firstCompletedAt.AddMinutes(1);
        var goneCompletedAt = firstCompletedAt.AddMinutes(2);
        var reappearedCompletedAt = firstCompletedAt.AddMinutes(3);
        string seriesId;
        string predecessorId;
        string successorId;
        string projectionAfterReappearance;

        await using (var initialFactory = CreateFactory())
        {
            using var client = initialFactory.CreateClient();
            var ingestor = initialFactory.Services.GetRequiredService<RoundIngestor>();
            var authority = await ReadAuthorityAsync(client);
            var hostSessionId = authority.GetProperty("hostSessionId").GetString()!;
            AssertAuthority(authority, "BARRIER", absenceAuthorityAvailable: false);

            var firstReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket05-reappear-generation-1",
                firstCompletedAt,
                ValidObservation(
                    sublot,
                    firstCompletedAt.AddMinutes(-3),
                    area: "N3-3",
                    eqp: "WB-03",
                    package: "QFN-G1")));
            AssertCommit(
                await ReadTraceAsync(client, firstReceipt.PollTraceId),
                firstReceipt,
                hostSessionId,
                "BARRIER",
                "POST_BARRIER",
                absenceAuthority: false);
            var first = await ReadSeriesAsync(client, WorkType, sublot);
            seriesId = first.GetProperty("seriesId").GetString()!;
            predecessorId = first.GetProperty("currentDemand").GetProperty("demandId").GetString()!;

            var restoreReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket05-reappear-restore-authority",
                restoreCompletedAt,
                ValidObservation(
                    sublot,
                    firstCompletedAt.AddMinutes(-2),
                    area: "N3-3",
                    eqp: "WB-03",
                    package: "QFN-G1")));
            AssertCommit(
                await ReadTraceAsync(client, restoreReceipt.PollTraceId),
                restoreReceipt,
                hostSessionId,
                "POST_BARRIER",
                "NORMAL",
                absenceAuthority: false);

            var goneReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket05-reappear-gone",
                goneCompletedAt));
            AssertCommit(
                await ReadTraceAsync(client, goneReceipt.PollTraceId),
                goneReceipt,
                hostSessionId,
                "NORMAL",
                "NORMAL",
                absenceAuthority: true);
            var gone = await ReadSeriesAsync(client, WorkType, sublot);
            AssertGone(gone, predecessorId, restoreCompletedAt, goneCompletedAt);
            var predecessorBeforeReappearance = Assert.Single(
                gone.GetProperty("demands").EnumerateArray()).GetRawText();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ingestor.IngestAsync(SuccessRound(
                    "poll-ticket05-reappear-before-gone-confirmation",
                    goneCompletedAt.AddTicks(-1),
                    ValidObservation(
                        sublot,
                        goneCompletedAt.AddMinutes(-1),
                        area: "N3-8",
                        eqp: "WB-08",
                        package: "QFN-G2"))));
            Assert.Equal(
                gone.GetRawText(),
                (await ReadSeriesAsync(client, WorkType, sublot)).GetRawText());

            var reappearanceReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket05-reappear-generation-2",
                reappearedCompletedAt,
                ValidObservation(
                    sublot,
                    reappearedCompletedAt.AddMinutes(-1),
                    area: "N3-8",
                    eqp: "WB-08",
                    package: "QFN-G2")));
            AssertCommit(
                await ReadTraceAsync(client, reappearanceReceipt.PollTraceId),
                reappearanceReceipt,
                hostSessionId,
                "NORMAL",
                "NORMAL",
                absenceAuthority: true);

            var reappeared = await ReadSeriesAsync(client, WorkType, sublot);
            Assert.Equal(seriesId, reappeared.GetProperty("seriesId").GetString());
            Assert.Equal("TRACKING", reappeared.GetProperty("lifecycle").GetString());
            Assert.Equal("VISIBLE", reappeared.GetProperty("currentPresence").GetString());
            var generations = reappeared.GetProperty("demands").EnumerateArray().ToArray();
            Assert.Equal([1, 2], generations.Select(item => item.GetProperty("generation").GetInt32()).ToArray());
            Assert.Equal(predecessorBeforeReappearance, generations[0].GetRawText());

            var predecessor = generations[0];
            Assert.Equal(predecessorId, predecessor.GetProperty("demandId").GetString());
            Assert.Equal("GONE", predecessor.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, predecessor.GetProperty("predecessorDemandId").ValueKind);
            Assert.Equal(firstReceipt.ProjectionCommitId, predecessor.GetProperty("createdProjectionCommitId").GetString());
            Assert.Equal(goneReceipt.ProjectionCommitId, predecessor.GetProperty("latestProjectionCommitId").GetString());
            Assert.Equal(restoreCompletedAt, predecessor.GetProperty("demandLastSeenAt").GetDateTimeOffset());
            Assert.Equal(goneCompletedAt, predecessor.GetProperty("goneConfirmedAt").GetDateTimeOffset());

            var successor = generations[1];
            successorId = successor.GetProperty("demandId").GetString()!;
            Assert.NotEqual(predecessorId, successorId);
            Assert.Equal(seriesId, successor.GetProperty("seriesId").GetString());
            Assert.Equal(2, successor.GetProperty("generation").GetInt32());
            Assert.Equal(predecessorId, successor.GetProperty("predecessorDemandId").GetString());
            Assert.Equal("VISIBLE", successor.GetProperty("status").GetString());
            Assert.Equal(reappearedCompletedAt, successor.GetProperty("createdAt").GetDateTimeOffset());
            Assert.Equal(reappearedCompletedAt, successor.GetProperty("demandLastSeenAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, successor.GetProperty("goneConfirmedAt").ValueKind);
            Assert.Equal(reappearanceReceipt.PollTraceId, successor.GetProperty("createdPollTraceId").GetString());
            Assert.Equal(reappearanceReceipt.ProjectionCommitId, successor.GetProperty("createdProjectionCommitId").GetString());
            Assert.Equal(reappearanceReceipt.ProjectionCommitId, successor.GetProperty("latestProjectionCommitId").GetString());
            Assert.Equal("N3-8", successor.GetProperty("liveMesFields").GetProperty("area").GetString());
            Assert.Equal("WB-08", successor.GetProperty("liveMesFields").GetProperty("eqp").GetString());
            Assert.Equal("QFN-G2", successor.GetProperty("liveMesFields").GetProperty("package").GetString());

            var current = reappeared.GetProperty("currentDemand");
            Assert.Equal(successorId, current.GetProperty("demandId").GetString());
            Assert.Equal(successor.GetRawText(), current.GetRawText());
            Assert.Contains(seriesId, reappearanceReceipt.SeriesIds);
            Assert.Contains(successorId, reappearanceReceipt.DemandIds);
            var createdEvent = AssertSeriesEventBoundToCommit(
                reappeared,
                "TRANSPORT_DEMAND_CREATED",
                successorId,
                reappearanceReceipt);
            using (var payload = JsonDocument.Parse(createdEvent.GetProperty("payloadJson").GetString()!))
            {
                Assert.Equal(predecessorId, payload.RootElement.GetProperty("predecessorDemandId").GetString());
                Assert.Equal("PREARCHIVE_REAPPEARANCE", payload.RootElement.GetProperty("reason").GetString());
            }
            var reappearanceObservation = Assert.Single(
                reappeared.GetProperty("rawObservations").EnumerateArray()
                    .Where(item => item.GetProperty("pollTraceId").GetString()
                        == reappearanceReceipt.PollTraceId));
            Assert.Equal(successorId, reappearanceObservation.GetProperty("demandId").GetString());
            Assert.Equal(reappearanceReceipt.ProjectionCommitId, reappearanceObservation.GetProperty("projectionCommitId").GetString());
            projectionAfterReappearance = reappeared.GetRawText();
        }

        await using (var restartedFactory = CreateFactory())
        {
            using var restartedClient = restartedFactory.CreateClient();
            var persisted = await restartedClient.GetFromJsonAsync<JsonElement>(
                $"/api/v2/demand-series/{Uri.EscapeDataString(seriesId)}");
            Assert.Equal(projectionAfterReappearance, persisted.GetRawText());
            Assert.Equal(
                [predecessorId, successorId],
                persisted.GetProperty("demands").EnumerateArray()
                    .Select(item => item.GetProperty("demandId").GetString()).ToArray());
            Assert.Equal(successorId, persisted.GetProperty("currentDemand").GetProperty("demandId").GetString());

            var restartedAuthority = await ReadAuthorityAsync(restartedClient);
            AssertAuthority(restartedAuthority, "BARRIER", absenceAuthorityAvailable: false);
            AssertAuthorityEvent(
                Assert.Single(restartedAuthority.GetProperty("events").EnumerateArray()),
                "RESTART_BARRIER_ENTERED",
                expectedPollTraceId: null,
                expectedProjectionCommitId: null,
                "NORMAL",
                "BARRIER");
        }

        AssertDatabaseEvidence(database);
    }

    private static void AssertNoCallerProvidedAbsenceAuthorityInput()
    {
        Assert.DoesNotContain(
            typeof(MesTaskUnionRound).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.Name.Contains("absenceAuthority", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(MesTaskUnionRound).GetConstructors()
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.Name?.Contains(
                "absenceAuthority",
                StringComparison.OrdinalIgnoreCase) == true);

        var ingestMethod = Assert.Single(typeof(RoundIngestor).GetMethods(
                BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(RoundIngestor.IngestAsync)));
        Assert.Equal(
            [typeof(MesTaskUnionRound), typeof(CancellationToken)],
            ingestMethod.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.DoesNotContain(
            ingestMethod.GetParameters(),
            parameter => parameter.Name?.Contains(
                "absenceAuthority",
                StringComparison.OrdinalIgnoreCase) == true);
    }

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

    private static void AssertAuthorityEvent(
        JsonElement authorityEvent,
        string expectedEventType,
        string? expectedPollTraceId,
        string? expectedProjectionCommitId,
        string expectedPhaseBefore,
        string expectedPhaseAfter)
    {
        Assert.Equal(expectedEventType, authorityEvent.GetProperty("eventType").GetString());
        Assert.NotEqual(default, authorityEvent.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(expectedPollTraceId, ReadNullableString(authorityEvent, "pollTraceId"));
        Assert.Equal(expectedProjectionCommitId, ReadNullableString(authorityEvent, "projectionCommitId"));
        Assert.Equal(expectedPhaseBefore, authorityEvent.GetProperty("phaseBefore").GetString());
        Assert.Equal(expectedPhaseAfter, authorityEvent.GetProperty("phaseAfter").GetString());
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

    private static void AssertVisible(
        JsonElement series,
        string demandId,
        DateTimeOffset expectedLastSeenAt)
    {
        Assert.Equal("TRACKING", series.GetProperty("lifecycle").GetString());
        Assert.Equal("VISIBLE", series.GetProperty("currentPresence").GetString());
        var demand = series.GetProperty("currentDemand");
        Assert.Equal(demandId, demand.GetProperty("demandId").GetString());
        Assert.Equal("VISIBLE", demand.GetProperty("status").GetString());
        Assert.Equal(expectedLastSeenAt.ToUniversalTime(), demand.GetProperty("demandLastSeenAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, demand.GetProperty("goneConfirmedAt").ValueKind);
    }

    private static void AssertGone(
        JsonElement series,
        string demandId,
        DateTimeOffset expectedLastSeenAt,
        DateTimeOffset expectedGoneConfirmedAt)
    {
        Assert.Equal("TRACKING", series.GetProperty("lifecycle").GetString());
        Assert.Equal("GONE", series.GetProperty("currentPresence").GetString());
        var demand = series.GetProperty("currentDemand");
        Assert.Equal(demandId, demand.GetProperty("demandId").GetString());
        Assert.Equal("GONE", demand.GetProperty("status").GetString());
        Assert.Equal(expectedLastSeenAt.ToUniversalTime(), demand.GetProperty("demandLastSeenAt").GetDateTimeOffset());
        Assert.Equal(expectedGoneConfirmedAt.ToUniversalTime(), demand.GetProperty("goneConfirmedAt").GetDateTimeOffset());
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

    private static string PeriodId(JsonElement period) =>
        period.GetProperty("periodId").GetString()!;

    private static string? ReadNullableString(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Null ? null : property.GetString();
    }

    private static MesTaskUnionObservation ValidObservation(
        string sublot,
        DateTimeOffset mesSourceDate,
        string area = "N3-3",
        string eqp = "WB-03",
        string package = "QFN") =>
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
            "mes-task-union-ticket05-v1",
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
            "mes-task-union-ticket05-v1",
            outcome,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static async Task<JsonElement> ReadAuthorityAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/v2/absence-authority");

    private static async Task<JsonElement> ReadAuthorityAsync(
        HttpClient client,
        string hostSessionId) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/api/v2/absence-authority/{Uri.EscapeDataString(hostSessionId)}");

    private static async Task<JsonElement> ReadSeriesAsync(
        HttpClient client,
        string workType,
        string sublot) =>
        await client.GetFromJsonAsync<JsonElement>(
            "/api/v2/demand-series/by-key"
            + $"?workType={Uri.EscapeDataString(workType)}"
            + $"&sublot={Uri.EscapeDataString(sublot)}");

    private static async Task<JsonElement> ReadTraceAsync(HttpClient client, string pollTraceId) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/api/v2/poll-traces/{Uri.EscapeDataString(pollTraceId)}");

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder => builder.UseProductionSqlApiTestHost());

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
            [$"{MesIngestHostOptions.SectionName}__SnapshotSource"] = MesIngestHostOptions.OracleRoundSource,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "true",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
        });

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
