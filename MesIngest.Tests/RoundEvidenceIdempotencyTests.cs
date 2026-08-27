using System.Net;
using System.Net.Http.Json;
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

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class RoundEvidenceIdempotencyTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset FixtureUtcNow =
        new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    private const string WorkType = "WIRE_TO_NITROGEN";
    private const string Sublot = "SL-TICKET02-EVIDENCE";
    private const string SeriesByKeyUri =
        "/api/v2/demand-series/by-key?workType=WIRE_TO_NITROGEN&sublot=SL-TICKET02-EVIDENCE";

    private readonly WebApplicationFactory<Program> _factory;

    public RoundEvidenceIdempotencyTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Ticket01SqlServerFact]
    public async Task All_outcomes_expose_canonical_utc_round_evidence_without_projecting_unsuccessful_results()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var localOffset = TimeSpan.FromHours(8);
        var successStartedAt = new DateTimeOffset(2026, 8, 12, 9, 0, 0, localOffset);
        var successCompletedAt = successStartedAt.AddSeconds(3);

        var successReceipt = await ingestor.IngestAsync(new MesTaskUnionRound(
            PollTraceId: "poll-ticket02-success",
            QueryVersion: "mes-task-union-ticket02-v1",
            Outcome: MesTaskUnionRoundOutcome.Success,
            StartedAt: successStartedAt,
            CompletedAt: successCompletedAt,
            Observations:
            [
                new MesTaskUnionObservation(
                    WorkType,
                    Sublot,
                    Area: null,
                    Eqp: "",
                    Step: "not-a-known-step",
                    MesSourceDate: null,
                    Package: " "),
            ]));

        var beforeProblems = await client.GetFromJsonAsync<JsonElement>(SeriesByKeyUri);
        var beforeProjection = beforeProblems.GetRawText();
        Assert.Equal(successReceipt.ProjectionCommitId, beforeProblems.GetProperty("latestProjectionCommitId").GetString());
        Assert.Equal(
            "NOT_READABLE",
            beforeProblems.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
        var initialConditions = beforeProblems.GetProperty("currentConditions").EnumerateArray().ToArray();
        Assert.Equal(4, initialConditions.Length);
        Assert.Contains(initialConditions, condition =>
            condition.GetProperty("code").GetString() == "REQUIRED_MES_FIELD_MISSING"
            && condition.GetProperty("subjectKind").GetString() == "AREA");
        Assert.Contains(initialConditions, condition =>
            condition.GetProperty("code").GetString() == "REQUIRED_MES_FIELD_MISSING"
            && condition.GetProperty("subjectKind").GetString() == "EQP");
        Assert.Contains(initialConditions, condition =>
            condition.GetProperty("code").GetString() == "REQUIRED_MES_FIELD_MISSING"
            && condition.GetProperty("subjectKind").GetString() == "DATES");
        Assert.Contains(initialConditions, condition =>
            condition.GetProperty("code").GetString() == "REQUIRED_MES_FIELD_MISSING"
            && condition.GetProperty("subjectKind").GetString() == "PACKAGE");
        var initialPeriods = beforeProblems.GetProperty("errorPeriods").EnumerateArray().ToArray();
        Assert.Equal(4, initialPeriods.Length);
        Assert.All(initialPeriods, period =>
            Assert.Equal("BOOTSTRAPPED_CURRENT_CONDITION", period.GetProperty("startReason").GetString()));

        var failureStartedAt = successStartedAt.AddMinutes(1);
        var failureReceipt = await ingestor.IngestAsync(new MesTaskUnionRound(
            PollTraceId: "poll-ticket02-failure",
            QueryVersion: "mes-task-union-ticket02-v1",
            Outcome: MesTaskUnionRoundOutcome.Failure,
            StartedAt: failureStartedAt,
            CompletedAt: failureStartedAt.AddSeconds(5),
            Observations: []));

        var incompleteStartedAt = successStartedAt.AddMinutes(2);
        var incompleteReceipt = await ingestor.IngestAsync(new MesTaskUnionRound(
            PollTraceId: "poll-ticket02-incomplete",
            QueryVersion: "mes-task-union-ticket02-v2",
            Outcome: MesTaskUnionRoundOutcome.Incomplete,
            StartedAt: incompleteStartedAt,
            CompletedAt: incompleteStartedAt.AddSeconds(4),
            Observations:
            [
                new MesTaskUnionObservation(
                    WorkType: "LOADPORT_TO_OVEN",
                    Sublot: "SL-STRUCTURALLY-INCOMPLETE",
                    Area: null,
                    Eqp: null,
                    Step: null,
                    MesSourceDate: null,
                    Package: null),
            ]));

        Assert.Null(failureReceipt.ProjectionCommitId);
        Assert.Equal(MesTaskUnionRoundOutcome.Failure, failureReceipt.Outcome);
        Assert.False(failureReceipt.IsReplay);
        Assert.Empty(failureReceipt.SeriesIds);
        Assert.Empty(failureReceipt.DemandIds);
        Assert.Null(incompleteReceipt.ProjectionCommitId);
        Assert.Equal(MesTaskUnionRoundOutcome.Incomplete, incompleteReceipt.Outcome);
        Assert.False(incompleteReceipt.IsReplay);
        Assert.Empty(incompleteReceipt.SeriesIds);
        Assert.Empty(incompleteReceipt.DemandIds);

        var successTrace = await ReadTraceAsync(client, "poll-ticket02-success");
        AssertTraceEvidence(
            successTrace,
            "mes-task-union-ticket02-v1",
            "SUCCESS",
            successStartedAt.ToUniversalTime(),
            successCompletedAt.ToUniversalTime(),
            expectedRowCount: 1,
            hasProjectionCommit: true);
        Assert.Equal(1, successTrace.GetProperty("observations").GetArrayLength());

        var failureTrace = await ReadTraceAsync(client, "poll-ticket02-failure");
        AssertTraceEvidence(
            failureTrace,
            "mes-task-union-ticket02-v1",
            "FAILURE",
            failureStartedAt.ToUniversalTime(),
            failureStartedAt.AddSeconds(5).ToUniversalTime(),
            expectedRowCount: 0,
            hasProjectionCommit: false);
        Assert.Equal(0, failureTrace.GetProperty("observations").GetArrayLength());

        var incompleteTrace = await ReadTraceAsync(client, "poll-ticket02-incomplete");
        AssertTraceEvidence(
            incompleteTrace,
            "mes-task-union-ticket02-v2",
            "INCOMPLETE",
            incompleteStartedAt.ToUniversalTime(),
            incompleteStartedAt.AddSeconds(4).ToUniversalTime(),
            expectedRowCount: 1,
            hasProjectionCommit: false);
        Assert.Equal(0, incompleteTrace.GetProperty("observations").GetArrayLength());
        Assert.NotEqual(
            failureTrace.GetProperty("contentDigest").GetString(),
            incompleteTrace.GetProperty("contentDigest").GetString());

        var failureReplay = await ingestor.IngestAsync(new MesTaskUnionRound(
            PollTraceId: "poll-ticket02-failure",
            QueryVersion: "mes-task-union-ticket02-v1",
            Outcome: MesTaskUnionRoundOutcome.Failure,
            StartedAt: failureStartedAt.AddHours(2),
            CompletedAt: failureStartedAt.AddHours(2).AddSeconds(5),
            Observations: []));
        var incompleteReplay = await ingestor.IngestAsync(new MesTaskUnionRound(
            PollTraceId: "poll-ticket02-incomplete",
            QueryVersion: "mes-task-union-ticket02-v2",
            Outcome: MesTaskUnionRoundOutcome.Incomplete,
            StartedAt: incompleteStartedAt.AddHours(2),
            CompletedAt: incompleteStartedAt.AddHours(2).AddSeconds(4),
            Observations:
            [
                new MesTaskUnionObservation(
                    WorkType: "LOADPORT_TO_OVEN",
                    Sublot: "SL-STRUCTURALLY-INCOMPLETE",
                    Area: null,
                    Eqp: null,
                    Step: null,
                    MesSourceDate: null,
                    Package: null),
            ]));
        Assert.True(failureReplay.IsReplay);
        Assert.True(incompleteReplay.IsReplay);
        Assert.Equal(failureTrace.GetRawText(), (await ReadTraceAsync(client, "poll-ticket02-failure")).GetRawText());
        Assert.Equal(incompleteTrace.GetRawText(), (await ReadTraceAsync(client, "poll-ticket02-incomplete")).GetRawText());
        Assert.Equal(new DatabaseCounts(3, 1, 1, 1, 1, 6), await ReadDatabaseCountsAsync(database.ConnectionString));

        var afterProblems = await client.GetFromJsonAsync<JsonElement>(SeriesByKeyUri);
        Assert.Equal(beforeProjection, afterProblems.GetRawText());
        using var incompleteSeries = await client.GetAsync(
            "/api/v2/demand-series/by-key?workType=LOADPORT_TO_OVEN&sublot=SL-STRUCTURALLY-INCOMPLETE");
        Assert.Equal(HttpStatusCode.NotFound, incompleteSeries.StatusCode);
    }

    [Ticket01SqlServerFact]
    public async Task Same_poll_trace_and_canonical_content_replays_the_original_accepted_result_without_duplicates()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        const string pollTraceId = "poll-ticket02-replay";
        const string queryVersion = "mes-task-union-ticket02-v1";
        var firstStartedAt = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.FromHours(8));
        var firstCompletedAt = firstStartedAt.AddSeconds(2);
        var firstRows = new[]
        {
            CreateObservation(
                "WIRE_TO_NITROGEN",
                "SL-TICKET02-REPLAY-A",
                new DateTimeOffset(2026, 8, 12, 9, 15, 0, TimeSpan.FromHours(8))),
            CreateObservation(
                "LOADPORT_TO_OVEN",
                "SL-TICKET02-REPLAY-B",
                new DateTimeOffset(2026, 8, 12, 9, 20, 0, TimeSpan.FromHours(8))),
        };

        var firstReceipt = await ingestor.IngestAsync(new MesTaskUnionRound(
            pollTraceId,
            queryVersion,
            MesTaskUnionRoundOutcome.Success,
            firstStartedAt,
            firstCompletedAt,
            firstRows));
        var firstTrace = await ReadTraceAsync(client, pollTraceId);
        var firstSeriesA = await ReadSeriesAsync(client, "WIRE_TO_NITROGEN", "SL-TICKET02-REPLAY-A");
        var firstSeriesB = await ReadSeriesAsync(client, "LOADPORT_TO_OVEN", "SL-TICKET02-REPLAY-B");

        var replayRows = new[]
        {
            firstRows[1] with { MesSourceDate = firstRows[1].MesSourceDate!.Value.ToUniversalTime() },
            firstRows[0] with { MesSourceDate = firstRows[0].MesSourceDate!.Value.ToUniversalTime() },
        };
        var replayReceipt = await ingestor.IngestAsync(new MesTaskUnionRound(
            pollTraceId,
            queryVersion,
            MesTaskUnionRoundOutcome.Success,
            StartedAt: firstStartedAt.AddHours(4).ToUniversalTime(),
            CompletedAt: firstCompletedAt.AddHours(4).ToUniversalTime(),
            Observations: replayRows));

        Assert.False(firstReceipt.IsReplay);
        Assert.True(replayReceipt.IsReplay);
        Assert.Equal(firstReceipt.PollTraceId, replayReceipt.PollTraceId);
        Assert.Equal(firstReceipt.Outcome, replayReceipt.Outcome);
        Assert.Equal(firstReceipt.ProjectionCommitId, replayReceipt.ProjectionCommitId);
        Assert.Equal(firstReceipt.SeriesIds, replayReceipt.SeriesIds);
        Assert.Equal(firstReceipt.DemandIds, replayReceipt.DemandIds);

        var replayTrace = await ReadTraceAsync(client, pollTraceId);
        Assert.Equal(firstTrace.GetRawText(), replayTrace.GetRawText());
        Assert.Equal(firstStartedAt.ToUniversalTime(), replayTrace.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(firstCompletedAt.ToUniversalTime(), replayTrace.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(2, replayTrace.GetProperty("observations").GetArrayLength());

        var replaySeriesA = await ReadSeriesAsync(client, "WIRE_TO_NITROGEN", "SL-TICKET02-REPLAY-A");
        var replaySeriesB = await ReadSeriesAsync(client, "LOADPORT_TO_OVEN", "SL-TICKET02-REPLAY-B");
        Assert.Equal(firstSeriesA.GetRawText(), replaySeriesA.GetRawText());
        Assert.Equal(firstSeriesB.GetRawText(), replaySeriesB.GetRawText());
        Assert.Equal(1, replaySeriesA.GetProperty("rawObservations").GetArrayLength());
        Assert.Equal(1, replaySeriesB.GetProperty("rawObservations").GetArrayLength());
        Assert.Equal(2, replaySeriesA.GetProperty("events").GetArrayLength());
        Assert.Equal(2, replaySeriesB.GetProperty("events").GetArrayLength());
        Assert.Equal(
            new DatabaseCounts(1, 1, 2, 2, 2, 4),
            await ReadDatabaseCountsAsync(database.ConnectionString));
    }

    [Ticket01SqlServerFact]
    public async Task Same_poll_trace_with_different_content_returns_versioned_conflict_without_partial_writes()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        const string pollTraceId = "poll-ticket02-conflict";
        var startedAt = new DateTimeOffset(2026, 8, 12, 3, 0, 0, TimeSpan.Zero);
        var observation = CreateObservation(
            "WIRE_TO_NITROGEN",
            "SL-TICKET02-CONFLICT",
            startedAt.AddMinutes(-5));
        var acceptedRound = new MesTaskUnionRound(
            pollTraceId,
            "mes-task-union-ticket02-v1",
            MesTaskUnionRoundOutcome.Success,
            startedAt,
            startedAt.AddSeconds(2),
            [observation]);

        var accepted = await ingestor.IngestAsync(acceptedRound);
        var acceptedTrace = await ReadTraceAsync(client, pollTraceId);
        var acceptedSeries = await ReadSeriesAsync(
            client,
            "WIRE_TO_NITROGEN",
            "SL-TICKET02-CONFLICT");
        var beforeCounts = await ReadDatabaseCountsAsync(database.ConnectionString);
        Assert.Equal(new DatabaseCounts(1, 1, 1, 1, 1, 2), beforeCounts);

        var duplicateCountConflict = await Assert.ThrowsAsync<PollTraceConflictException>(() =>
            ingestor.IngestAsync(acceptedRound with
            {
                StartedAt = startedAt.AddMinutes(1),
                CompletedAt = startedAt.AddMinutes(1).AddSeconds(2),
                Observations = [observation, observation],
            }));
        AssertConflict(duplicateCountConflict, pollTraceId);

        var outcomeConflict = await Assert.ThrowsAsync<PollTraceConflictException>(() =>
            ingestor.IngestAsync(acceptedRound with
            {
                Outcome = MesTaskUnionRoundOutcome.Incomplete,
                Observations = [observation],
            }));
        AssertConflict(outcomeConflict, pollTraceId);

        var queryVersionConflict = await Assert.ThrowsAsync<PollTraceConflictException>(() =>
            ingestor.IngestAsync(acceptedRound with
            {
                QueryVersion = "mes-task-union-ticket02-v2",
            }));
        AssertConflict(queryVersionConflict, pollTraceId);

        Assert.Equal(beforeCounts, await ReadDatabaseCountsAsync(database.ConnectionString));
        Assert.Equal(acceptedTrace.GetRawText(), (await ReadTraceAsync(client, pollTraceId)).GetRawText());
        Assert.Equal(
            acceptedSeries.GetRawText(),
            (await ReadSeriesAsync(client, "WIRE_TO_NITROGEN", "SL-TICKET02-CONFLICT")).GetRawText());
        Assert.Equal(accepted.ProjectionCommitId, acceptedTrace.GetProperty("projectionCommit").GetProperty("projectionCommitId").GetString());
    }

    [Ticket01SqlServerFact]
    public async Task Success_preserves_unassigned_rows_while_projecting_every_assignable_key()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var completedAt = new DateTimeOffset(2026, 8, 12, 4, 0, 2, TimeSpan.Zero);
        var assigned = CreateObservation(
            "WIRE_TO_NITROGEN",
            "SL-TICKET02-ASSIGNED",
            completedAt.AddMinutes(-8));
        var conflictingWorkType = assigned with
        {
            WorkType = "LOADPORT_TO_OVEN",
            Area = "invalid-area",
            Eqp = "",
        };
        var missingSublot = new MesTaskUnionObservation(
            WorkType: "LOADPORT_TO_OVEN",
            Sublot: null,
            Area: "A1-1",
            Eqp: "EQP-NULL-SUBLOT",
            Step: "STEP-NULL-SUBLOT",
            MesSourceDate: null,
            Package: "PKG-NULL-SUBLOT");
        var missingWorkType = new MesTaskUnionObservation(
            WorkType: " ",
            Sublot: "SL-BLANK-TASK-TYPE",
            Area: "B2-2",
            Eqp: null,
            Step: "",
            MesSourceDate: completedAt.AddMinutes(-4),
            Package: null);

        var receipt = await ingestor.IngestAsync(new MesTaskUnionRound(
            PollTraceId: "poll-ticket02-unassigned",
            QueryVersion: "mes-task-union-ticket02-v1",
            Outcome: MesTaskUnionRoundOutcome.Success,
            StartedAt: completedAt.AddSeconds(-2),
            CompletedAt: completedAt,
            Observations: [missingWorkType, assigned, missingSublot, conflictingWorkType]));

        Assert.Equal(MesTaskUnionRoundOutcome.Success, receipt.Outcome);
        Assert.Equal(2, receipt.SeriesIds.Count);
        Assert.Equal(2, receipt.DemandIds.Count);
        var trace = await ReadTraceAsync(client, "poll-ticket02-unassigned");
        Assert.Equal("SUCCESS", trace.GetProperty("outcome").GetString());
        Assert.Equal(4, trace.GetProperty("rowCount").GetInt32());
        var observations = trace.GetProperty("observations");
        Assert.Equal(4, observations.GetArrayLength());
        Assert.Equal([0, 1, 2, 3], observations.EnumerateArray().Select(item => item.GetProperty("ordinal").GetInt32()).ToArray());

        var unassigned = observations.EnumerateArray()
            .Where(item => item.GetProperty("assignment").GetString() == "UNASSIGNED")
            .ToArray();
        Assert.Equal(2, unassigned.Length);
        Assert.All(unassigned, item =>
        {
            Assert.Equal(JsonValueKind.Null, item.GetProperty("seriesId").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("demandId").ValueKind);
            Assert.Equal(receipt.ProjectionCommitId, item.GetProperty("projectionCommitId").GetString());
        });
        Assert.Contains(unassigned, item =>
            item.GetProperty("workType").GetString() == "LOADPORT_TO_OVEN"
            && item.GetProperty("sublot").ValueKind == JsonValueKind.Null
            && item.GetProperty("eqp").GetString() == "EQP-NULL-SUBLOT");
        Assert.Contains(unassigned, item =>
            item.GetProperty("workType").GetString() == " "
            && item.GetProperty("sublot").GetString() == "SL-BLANK-TASK-TYPE"
            && item.GetProperty("step").GetString() == "");

        var assignedEvidence = observations.EnumerateArray()
            .Where(item => item.GetProperty("assignment").GetString() == "ASSIGNED")
            .ToArray();
        Assert.Equal(2, assignedEvidence.Length);
        Assert.Equal(
            receipt.SeriesIds.Order(StringComparer.Ordinal),
            assignedEvidence.Select(item => item.GetProperty("seriesId").GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(
            receipt.DemandIds.Order(StringComparer.Ordinal),
            assignedEvidence.Select(item => item.GetProperty("demandId").GetString()!).Order(StringComparer.Ordinal));
        var series = await ReadSeriesAsync(client, "WIRE_TO_NITROGEN", "SL-TICKET02-ASSIGNED");
        Assert.Contains(series.GetProperty("seriesId").GetString(), receipt.SeriesIds);
        Assert.Equal(1, series.GetProperty("rawObservations").GetArrayLength());
        AssertWorkTypeMembershipCondition(
            series,
            "LOADPORT_TO_OVEN",
            "WIRE_TO_NITROGEN");

        var conflictingSeries = await ReadSeriesAsync(client, "LOADPORT_TO_OVEN", "SL-TICKET02-ASSIGNED");
        Assert.Contains(conflictingSeries.GetProperty("seriesId").GetString(), receipt.SeriesIds);
        Assert.Equal("invalid-area", conflictingSeries.GetProperty("currentDemand").GetProperty("liveMesFields").GetProperty("area").GetString());
        Assert.Equal("", conflictingSeries.GetProperty("currentDemand").GetProperty("liveMesFields").GetProperty("eqp").GetString());
        Assert.Equal(
            "NOT_READABLE",
            conflictingSeries.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
        var fieldConditions = conflictingSeries.GetProperty("currentConditions").EnumerateArray().ToArray();
        Assert.Equal(3, fieldConditions.Length);
        Assert.Contains(fieldConditions, condition =>
            condition.GetProperty("code").GetString() == "INVALID_MES_FIELD_FORMAT"
            && condition.GetProperty("category").GetString() == "DATA_FORMAT"
            && condition.GetProperty("subjectKind").GetString() == "AREA");
        Assert.Contains(fieldConditions, condition =>
            condition.GetProperty("code").GetString() == "REQUIRED_MES_FIELD_MISSING"
            && condition.GetProperty("category").GetString() == "DATA_COMPLETENESS"
            && condition.GetProperty("subjectKind").GetString() == "EQP");
        AssertWorkTypeMembershipCondition(
            conflictingSeries,
            "LOADPORT_TO_OVEN",
            "WIRE_TO_NITROGEN");

        using var guessedFromSublot = await client.GetAsync(
            "/api/v2/demand-series/by-key?workType=LOADPORT_TO_OVEN&sublot=SL-GUESSED-FROM-MISSING-SUBLOT");
        Assert.Equal(HttpStatusCode.NotFound, guessedFromSublot.StatusCode);
        using var guessedFromWorkType = await client.GetAsync(
            "/api/v2/demand-series/by-key?workType=WIRE_TO_NITROGEN&sublot=SL-BLANK-TASK-TYPE");
        Assert.Equal(HttpStatusCode.NotFound, guessedFromWorkType.StatusCode);
        Assert.Equal(new DatabaseCounts(1, 1, 2, 2, 4, 8), await ReadDatabaseCountsAsync(database.ConnectionString));
    }

    [Ticket01SqlServerFact]
    public async Task Restarted_host_preserves_round_evidence_replay_and_projection_isolation()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string successId = "poll-ticket02-journey-success";
        const string failureId = "poll-ticket02-journey-failure";
        const string incompleteId = "poll-ticket02-journey-incomplete";
        var startedAt = new DateTimeOffset(2026, 8, 12, 5, 0, 0, TimeSpan.Zero);
        var observation = CreateObservation(
            "WIRE_TO_NITROGEN",
            "SL-TICKET02-JOURNEY",
            startedAt.AddMinutes(-3));
        var successRound = new MesTaskUnionRound(
            successId,
            "mes-task-union-ticket02-v1",
            MesTaskUnionRoundOutcome.Success,
            startedAt,
            startedAt.AddSeconds(2),
            [observation]);

        RoundCommitReceipt accepted;
        string acceptedTraceJson;
        string acceptedSeriesJson;
        string failureTraceJson;
        string incompleteTraceJson;
        DatabaseCounts acceptedCounts;

        await using (var firstFactory = CreateFactory())
        {
            var firstClient = firstFactory.CreateClient();
            var firstIngestor = firstFactory.Services.GetRequiredService<RoundIngestor>();
            accepted = await firstIngestor.IngestAsync(successRound);
            acceptedTraceJson = (await ReadTraceAsync(firstClient, successId)).GetRawText();
            acceptedSeriesJson = (await ReadSeriesAsync(
                firstClient,
                "WIRE_TO_NITROGEN",
                "SL-TICKET02-JOURNEY")).GetRawText();

            await firstIngestor.IngestAsync(new MesTaskUnionRound(
                failureId,
                "mes-task-union-ticket02-v1",
                MesTaskUnionRoundOutcome.Failure,
                startedAt.AddMinutes(1),
                startedAt.AddMinutes(1).AddSeconds(8),
                []));
            await firstIngestor.IngestAsync(new MesTaskUnionRound(
                incompleteId,
                "mes-task-union-ticket02-v1",
                MesTaskUnionRoundOutcome.Incomplete,
                startedAt.AddMinutes(2),
                startedAt.AddMinutes(2).AddSeconds(4),
                [observation with { WorkType = null }]));

            failureTraceJson = (await ReadTraceAsync(firstClient, failureId)).GetRawText();
            incompleteTraceJson = (await ReadTraceAsync(firstClient, incompleteId)).GetRawText();
            Assert.Equal(
                acceptedSeriesJson,
                (await ReadSeriesAsync(firstClient, "WIRE_TO_NITROGEN", "SL-TICKET02-JOURNEY")).GetRawText());
            acceptedCounts = await ReadDatabaseCountsAsync(database.ConnectionString);
            Assert.Equal(new DatabaseCounts(3, 1, 1, 1, 1, 2), acceptedCounts);
        }

        await using (var restartedFactory = CreateFactory())
        {
            var restartedClient = restartedFactory.CreateClient();
            var restartedIngestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            Assert.Equal(acceptedTraceJson, (await ReadTraceAsync(restartedClient, successId)).GetRawText());
            Assert.Equal(failureTraceJson, (await ReadTraceAsync(restartedClient, failureId)).GetRawText());
            Assert.Equal(incompleteTraceJson, (await ReadTraceAsync(restartedClient, incompleteId)).GetRawText());
            Assert.Equal(
                acceptedSeriesJson,
                (await ReadSeriesAsync(restartedClient, "WIRE_TO_NITROGEN", "SL-TICKET02-JOURNEY")).GetRawText());

            var replay = await restartedIngestor.IngestAsync(successRound with
            {
                StartedAt = startedAt.AddHours(1),
                CompletedAt = startedAt.AddHours(1).AddSeconds(2),
                Observations = [observation with { MesSourceDate = observation.MesSourceDate!.Value.ToOffset(TimeSpan.FromHours(8)) }],
            });
            Assert.True(replay.IsReplay);
            Assert.Equal(accepted.ProjectionCommitId, replay.ProjectionCommitId);

            var conflict = await Assert.ThrowsAsync<PollTraceConflictException>(() =>
                restartedIngestor.IngestAsync(successRound with
                {
                    Observations = [observation with { Package = "CONFLICT-AFTER-RESTART" }],
                }));
            AssertConflict(conflict, successId);
            Assert.Equal(acceptedCounts, await ReadDatabaseCountsAsync(database.ConnectionString));
            Assert.Equal(acceptedTraceJson, (await ReadTraceAsync(restartedClient, successId)).GetRawText());
            Assert.Equal(
                acceptedSeriesJson,
                (await ReadSeriesAsync(restartedClient, "WIRE_TO_NITROGEN", "SL-TICKET02-JOURNEY")).GetRawText());
        }
    }

    private static async Task<JsonElement> ReadTraceAsync(HttpClient client, string pollTraceId) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/api/v2/poll-traces/{Uri.EscapeDataString(pollTraceId)}");

    private static async Task<JsonElement> ReadSeriesAsync(
        HttpClient client,
        string workType,
        string sublot) =>
        await client.GetFromJsonAsync<JsonElement>(
            "/api/v2/demand-series/by-key"
            + $"?workType={Uri.EscapeDataString(workType)}"
            + $"&sublot={Uri.EscapeDataString(sublot)}");

    private static MesTaskUnionObservation CreateObservation(
        string workType,
        string sublot,
        DateTimeOffset mesSourceDate) =>
        new(
            workType,
            sublot,
            Area: "N3-3",
            Eqp: "EQP-02",
            Step: "STEP-02",
            mesSourceDate,
            Package: "PKG-02");

    private static void AssertTraceEvidence(
        JsonElement trace,
        string expectedQueryVersion,
        string expectedOutcome,
        DateTimeOffset expectedStartedAt,
        DateTimeOffset expectedCompletedAt,
        int expectedRowCount,
        bool hasProjectionCommit)
    {
        Assert.Equal(expectedQueryVersion, trace.GetProperty("queryVersion").GetString());
        Assert.Equal(expectedOutcome, trace.GetProperty("outcome").GetString());
        Assert.Equal(expectedStartedAt, trace.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, trace.GetProperty("startedAt").GetDateTimeOffset().Offset);
        Assert.Equal(expectedCompletedAt, trace.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, trace.GetProperty("completedAt").GetDateTimeOffset().Offset);
        Assert.Equal(expectedRowCount, trace.GetProperty("rowCount").GetInt32());
        Assert.Matches("^[0-9a-f]{64}$", trace.GetProperty("contentDigest").GetString()!);
        Assert.Equal(
            hasProjectionCommit ? JsonValueKind.Object : JsonValueKind.Null,
            trace.GetProperty("projectionCommit").ValueKind);
    }

    private static void AssertConflict(PollTraceConflictException conflict, string pollTraceId)
    {
        Assert.Equal("POLL_TRACE_CONTENT_CONFLICT", conflict.Code);
        Assert.Equal(NewMesIngestContract.Version, conflict.ContractVersion);
        Assert.Equal(pollTraceId, conflict.PollTraceId);
        Assert.Contains(pollTraceId, conflict.Message, StringComparison.Ordinal);
    }

    private static void AssertWorkTypeMembershipCondition(
        JsonElement series,
        params string[] expectedWorkTypes)
    {
        var demandId = series.GetProperty("currentDemand").GetProperty("demandId").GetString();
        var condition = Assert.Single(series.GetProperty("currentConditions").EnumerateArray()
            .Where(value => value.GetProperty("code").GetString() == "SUBLOT_MULTIPLE_WORK_TYPES"));
        Assert.Equal("OBSERVATION_CONFLICT", condition.GetProperty("category").GetString());
        Assert.Equal("WORK_TYPE_MEMBERSHIP", condition.GetProperty("subjectKind").GetString());
        Assert.Equal($"DEMAND:{demandId}", condition.GetProperty("target").GetString());
        Assert.Equal("EXACTLY_ONE_WORK_TYPE_PER_SUBLOT", condition.GetProperty("expectedRule").GetString());
        using var observedValue = JsonDocument.Parse(condition.GetProperty("observedValue").GetString()!);
        Assert.Equal(
            expectedWorkTypes.Order(StringComparer.Ordinal),
            observedValue.RootElement.EnumerateArray()
                .Select(value => value.GetString())
                .Order(StringComparer.Ordinal));
    }

    private static async Task<DatabaseCounts> ReadDatabaseCountsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM mesingest.PollTraces),
                (SELECT COUNT(*) FROM mesingest.ProjectionCommits),
                (SELECT COUNT(*) FROM mesingest.DemandSeries),
                (SELECT COUNT(*) FROM mesingest.TransportDemands),
                (SELECT COUNT(*) FROM mesingest.DemandRawObservations),
                (SELECT COUNT(*) FROM mesingest.DemandSeriesEvents);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DatabaseCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5));
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseProductionSqlApiTestHost();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new AdjustableTimeProvider(FixtureUtcNow));
            });
        });

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

    private sealed record DatabaseCounts(
        int PollTraces,
        int ProjectionCommits,
        int DemandSeries,
        int TransportDemands,
        int RawObservations,
        int SeriesEvents);
}
