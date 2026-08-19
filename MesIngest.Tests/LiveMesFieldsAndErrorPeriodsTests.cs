using System.Net.Http.Json;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class LiveMesFieldsAndErrorPeriodsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string WorkType = "WIRE_TO_NITROGEN";
    private readonly WebApplicationFactory<Program> _factory;

    public LiveMesFieldsAndErrorPeriodsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Ticket01SqlServerFact]
    public async Task Five_live_field_changes_update_the_same_demand_and_emit_exactly_five_auditable_events()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var firstCompletedAt = new DateTimeOffset(2026, 8, 13, 1, 0, 2, TimeSpan.Zero);
        var changedCompletedAt = firstCompletedAt.AddMinutes(1);
        var unchangedCompletedAt = changedCompletedAt.AddMinutes(1);
        var initial = ValidObservation("SL-TICKET03-LIVE") with
        {
            Area = "A1-1",
            Eqp = "WB-01",
            Step = "焊线",
            MesSourceDate = new DateTimeOffset(2026, 8, 13, 8, 45, 0, TimeSpan.FromHours(8)),
            Package = "QFN48",
        };
        var changed = initial with
        {
            Area = "A11-11",
            Eqp = "WB-02",
            Step = "焊线2",
            MesSourceDate = new DateTimeOffset(2026, 8, 13, 9, 15, 0, TimeSpan.FromHours(8)),
            Package = "QFN64",
        };

        var firstReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket03-live-first",
            firstCompletedAt,
            initial));
        var changedReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket03-live-changed",
            changedCompletedAt,
            changed));
        var afterChange = await GetSeriesByKeyAsync(client, initial.Sublot!);
        var eventsAfterChange = afterChange.GetProperty("events");
        var eventIdsAfterChange = eventsAfterChange
            .EnumerateArray()
            .Select(item => item.GetProperty("eventId").GetString())
            .ToArray();

        var unchangedReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket03-live-unchanged",
            unchangedCompletedAt,
            changed));
        var afterUnchanged = await GetSeriesByKeyAsync(client, initial.Sublot!);

        Assert.Equal(firstReceipt.SeriesIds, changedReceipt.SeriesIds);
        Assert.Equal(firstReceipt.SeriesIds, unchangedReceipt.SeriesIds);
        Assert.Equal(firstReceipt.DemandIds, changedReceipt.DemandIds);
        Assert.Equal(firstReceipt.DemandIds, unchangedReceipt.DemandIds);
        Assert.NotEqual(firstReceipt.ProjectionCommitId, changedReceipt.ProjectionCommitId);
        Assert.NotEqual(changedReceipt.ProjectionCommitId, unchangedReceipt.ProjectionCommitId);

        var demand = afterUnchanged.GetProperty("currentDemand");
        Assert.Equal(firstReceipt.DemandIds.Single(), demand.GetProperty("demandId").GetString());
        Assert.Equal(1, demand.GetProperty("generation").GetInt32());
        Assert.Equal("VISIBLE", demand.GetProperty("status").GetString());
        Assert.Equal(unchangedReceipt.ProjectionCommitId, demand.GetProperty("latestProjectionCommitId").GetString());
        AssertLiveFields(demand.GetProperty("liveMesFields"), changed);

        var fieldChanges = eventsAfterChange
            .EnumerateArray()
            .Where(item => item.GetProperty("eventType").GetString() == "MES_FIELD_CHANGED")
            .ToArray();
        Assert.Equal(5, fieldChanges.Length);
        var changesByField = fieldChanges.ToDictionary(
            item => ReadEventPayload(item).GetProperty("field").GetString()!,
            StringComparer.Ordinal);
        Assert.Equal(RequiredSubjects.OrderBy(value => value), changesByField.Keys.OrderBy(value => value));

        AssertStringChange(changesByField["AREA"], "A1-1", "A11-11");
        AssertStringChange(changesByField["EQP"], "WB-01", "WB-02");
        AssertStringChange(changesByField["STEP"], "焊线", "焊线2");
        AssertDateChange(
            changesByField["DATES"],
            initial.MesSourceDate!.Value,
            changed.MesSourceDate!.Value);
        AssertStringChange(changesByField["PACKAGE"], "QFN48", "QFN64");
        Assert.All(fieldChanges, item =>
        {
            var payload = ReadEventPayload(item);
            Assert.Equal("poll-ticket03-live-changed", item.GetProperty("pollTraceId").GetString());
            Assert.Equal(changedReceipt.ProjectionCommitId, item.GetProperty("projectionCommitId").GetString());
            Assert.Equal(changedCompletedAt, item.GetProperty("occurredAt").GetDateTimeOffset());
            Assert.Equal(payload.GetProperty("field").GetString(), item.GetProperty("subjectKind").GetString());
            Assert.Equal(firstReceipt.DemandIds.Single(), item.GetProperty("subjectId").GetString());
        });

        AssertStrictSeriesSequence(eventsAfterChange);
        Assert.Equal(
            eventIdsAfterChange,
            afterUnchanged.GetProperty("events")
                .EnumerateArray()
                .Select(item => item.GetProperty("eventId").GetString())
                .ToArray());
        Assert.Equal(3, afterUnchanged.GetProperty("rawObservations").GetArrayLength());
    }

    [Ticket01SqlServerFact]
    public async Task Bootstrapped_missing_fields_remain_visible_extend_the_same_periods_and_clear_on_complete_success()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var firstCompletedAt = new DateTimeOffset(2026, 8, 13, 2, 0, 2, TimeSpan.Zero);
        var evidenceChangedAt = firstCompletedAt.AddMinutes(1);
        var clearedAt = evidenceChangedAt.AddMinutes(1);
        var missing = new MesTaskUnionObservation(
            WorkType,
            "SL-TICKET03-MISSING",
            Area: null,
            Eqp: "",
            Step: " ",
            MesSourceDate: null,
            Package: "\t");
        var stillMissing = missing with
        {
            Area = "",
            Eqp = " ",
            Step = "\t",
            Package = "   ",
        };
        var recovered = ValidObservation(missing.Sublot!);

        var firstReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket03-missing-bootstrap",
            firstCompletedAt,
            missing));
        var bootstrapped = await GetSeriesByKeyAsync(client, missing.Sublot!);

        var demand = bootstrapped.GetProperty("currentDemand");
        Assert.Equal(firstReceipt.DemandIds.Single(), demand.GetProperty("demandId").GetString());
        Assert.Equal(1, demand.GetProperty("generation").GetInt32());
        Assert.Equal("VISIBLE", demand.GetProperty("status").GetString());
        Assert.Equal("NOT_READABLE", demand.GetProperty("externalReadabilityState").GetString());
        Assert.Equal(
            ["REQUIRED_MES_FIELD_MISSING"],
            demand.GetProperty("readabilityBlockers")
                .EnumerateArray()
                .Select(blocker => blocker.GetString()));
        AssertLiveFields(demand.GetProperty("liveMesFields"), missing);
        AssertRawObservation(bootstrapped.GetProperty("rawObservations")[0], missing);

        var initialConditions = bootstrapped.GetProperty("currentConditions");
        var initialPeriods = bootstrapped.GetProperty("errorPeriods");
        Assert.Equal(5, initialConditions.GetArrayLength());
        Assert.Equal(5, initialPeriods.GetArrayLength());
        var periodIdsBySubject = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var subject in RequiredSubjects)
        {
            var condition = FindBySubject(initialConditions, subject);
            var period = FindBySubject(initialPeriods, subject);
            var periodId = period.GetProperty("periodId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(periodId));
            periodIdsBySubject.Add(subject, periodId!);

            Assert.Equal(periodId, condition.GetProperty("periodId").GetString());
            AssertFieldErrorIdentity(condition, firstReceipt.DemandIds.Single(), subject, "REQUIRED_MES_FIELD_MISSING", "DATA_COMPLETENESS");
            AssertFieldErrorIdentity(period, firstReceipt.DemandIds.Single(), subject, "REQUIRED_MES_FIELD_MISSING", "DATA_COMPLETENESS");
            Assert.Equal(firstCompletedAt, condition.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(firstCompletedAt, condition.GetProperty("latestEvidenceAt").GetDateTimeOffset());
            Assert.Equal("poll-ticket03-missing-bootstrap", condition.GetProperty("latestPollTraceId").GetString());
            Assert.Equal(firstReceipt.ProjectionCommitId, condition.GetProperty("latestProjectionCommitId").GetString());
            AssertObservedValue(condition, GetFieldValue(missing, subject));
            Assert.Equal("NON_WHITESPACE_VALUE_REQUIRED", condition.GetProperty("expectedRule").GetString());
            Assert.Equal("BOOTSTRAPPED_CURRENT_CONDITION", period.GetProperty("startReason").GetString());
            Assert.Equal(firstCompletedAt, period.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, period.GetProperty("endedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, period.GetProperty("endReason").ValueKind);

            var evidence = Assert.Single(period.GetProperty("evidence").EnumerateArray());
            Assert.Equal("BOOTSTRAPPED_CURRENT_CONDITION", evidence.GetProperty("evidenceKind").GetString());
            AssertEvidenceLink(
                evidence,
                firstCompletedAt,
                "poll-ticket03-missing-bootstrap",
                firstReceipt.ProjectionCommitId!);
            AssertObservedValue(evidence, GetFieldValue(missing, subject));
        }

        var changedReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket03-missing-evidence-change",
            evidenceChangedAt,
            stillMissing));
        var afterEvidenceChange = await GetSeriesByKeyAsync(client, missing.Sublot!);
        Assert.Equal(firstReceipt.SeriesIds, changedReceipt.SeriesIds);
        Assert.Equal(firstReceipt.DemandIds, changedReceipt.DemandIds);
        Assert.Equal("NOT_READABLE", afterEvidenceChange.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
        Assert.Equal(5, afterEvidenceChange.GetProperty("currentConditions").GetArrayLength());
        Assert.Equal(5, afterEvidenceChange.GetProperty("errorPeriods").GetArrayLength());
        Assert.Equal(2, afterEvidenceChange.GetProperty("rawObservations").GetArrayLength());
        AssertRawObservation(afterEvidenceChange.GetProperty("rawObservations")[1], stillMissing);

        foreach (var subject in RequiredSubjects)
        {
            var condition = FindBySubject(afterEvidenceChange.GetProperty("currentConditions"), subject);
            var period = FindBySubject(afterEvidenceChange.GetProperty("errorPeriods"), subject);
            Assert.Equal(periodIdsBySubject[subject], condition.GetProperty("periodId").GetString());
            Assert.Equal(periodIdsBySubject[subject], period.GetProperty("periodId").GetString());
            var evidence = period.GetProperty("evidence");
            if (subject == "DATES")
            {
                Assert.Equal(1, evidence.GetArrayLength());
                Assert.Equal(firstCompletedAt, condition.GetProperty("latestEvidenceAt").GetDateTimeOffset());
                Assert.Equal("poll-ticket03-missing-bootstrap", condition.GetProperty("latestPollTraceId").GetString());
                Assert.Equal(firstReceipt.ProjectionCommitId, condition.GetProperty("latestProjectionCommitId").GetString());
                AssertObservedValue(condition, GetFieldValue(missing, subject));
                continue;
            }

            Assert.Equal(2, evidence.GetArrayLength());
            Assert.Equal(evidenceChangedAt, condition.GetProperty("latestEvidenceAt").GetDateTimeOffset());
            Assert.Equal("poll-ticket03-missing-evidence-change", condition.GetProperty("latestPollTraceId").GetString());
            Assert.Equal(changedReceipt.ProjectionCommitId, condition.GetProperty("latestProjectionCommitId").GetString());
            AssertObservedValue(condition, GetFieldValue(stillMissing, subject));
            Assert.Equal("NON_WHITESPACE_VALUE_REQUIRED", condition.GetProperty("expectedRule").GetString());
            var changedEvidence = evidence[1];
            Assert.Equal("CONDITION_EVIDENCE_CHANGED", changedEvidence.GetProperty("evidenceKind").GetString());
            AssertEvidenceLink(
                changedEvidence,
                evidenceChangedAt,
                "poll-ticket03-missing-evidence-change",
                changedReceipt.ProjectionCommitId!);
            AssertObservedValue(changedEvidence, GetFieldValue(stillMissing, subject));
        }

        var recoveredReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket03-missing-cleared",
            clearedAt,
            recovered));
        var afterRecovery = await GetSeriesByKeyAsync(client, missing.Sublot!);

        Assert.Equal(firstReceipt.SeriesIds, recoveredReceipt.SeriesIds);
        Assert.Equal(firstReceipt.DemandIds, recoveredReceipt.DemandIds);
        Assert.Equal(1, afterRecovery.GetProperty("currentDemand").GetProperty("generation").GetInt32());
        Assert.Equal("READABLE", afterRecovery.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
        Assert.Empty(afterRecovery.GetProperty("currentDemand").GetProperty("readabilityBlockers").EnumerateArray());
        AssertLiveFields(afterRecovery.GetProperty("currentDemand").GetProperty("liveMesFields"), recovered);
        Assert.Equal(0, afterRecovery.GetProperty("currentConditions").GetArrayLength());
        Assert.Equal(5, afterRecovery.GetProperty("errorPeriods").GetArrayLength());
        Assert.Equal(3, afterRecovery.GetProperty("rawObservations").GetArrayLength());

        foreach (var subject in RequiredSubjects)
        {
            var period = FindBySubject(afterRecovery.GetProperty("errorPeriods"), subject);
            Assert.Equal(periodIdsBySubject[subject], period.GetProperty("periodId").GetString());
            Assert.Equal(clearedAt, period.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal("CONDITION_CLEARED", period.GetProperty("endReason").GetString());
            var periodEvidence = period.GetProperty("evidence");
            var endingEvidence = periodEvidence[periodEvidence.GetArrayLength() - 1];
            Assert.Equal("CONDITION_CLEARED", endingEvidence.GetProperty("evidenceKind").GetString());
            AssertObservedValue(endingEvidence, GetFieldValue(recovered, subject));
            Assert.Equal("NON_WHITESPACE_VALUE_REQUIRED", endingEvidence.GetProperty("expectedRule").GetString());
            AssertEvidenceLink(
                endingEvidence,
                clearedAt,
                "poll-ticket03-missing-cleared",
                recoveredReceipt.ProjectionCommitId!);
        }
    }

    [Ticket01SqlServerFact]
    public async Task Invalid_area_uses_one_persistent_period_non_success_rounds_cannot_clear_and_contract_publishes_catalog()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var firstCompletedAt = new DateTimeOffset(2026, 8, 13, 3, 0, 2, TimeSpan.Zero);
        var invalidStartedAt = firstCompletedAt.AddMinutes(1);
        var failureAt = invalidStartedAt.AddMinutes(1);
        var incompleteAt = failureAt.AddMinutes(1);
        var invalidChangedAt = incompleteAt.AddMinutes(1);
        var clearedAt = invalidChangedAt.AddMinutes(1);
        var recurredAt = clearedAt.AddMinutes(1);
        var valid = ValidObservation("SL-TICKET03-INVALID-AREA") with { Area = "A1-1" };
        var firstInvalid = valid with { Area = "A01-01" };
        var secondInvalid = valid with { Area = "A00-1" };
        var recovered = valid with { Area = "A11-11" };
        string seriesId;
        string demandId;
        string invalidPeriodId;
        string stateBeforeRestart;

        await using (var firstFactory = CreateFactory())
        {
            var firstClient = firstFactory.CreateClient();
            var ingestor = firstFactory.Services.GetRequiredService<RoundIngestor>();
            var firstReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket03-area-valid",
                firstCompletedAt,
                valid));
            var invalidReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket03-area-invalid-first",
                invalidStartedAt,
                firstInvalid));
            var active = await GetSeriesByKeyAsync(firstClient, valid.Sublot!);

            seriesId = firstReceipt.SeriesIds.Single();
            demandId = firstReceipt.DemandIds.Single();
            Assert.Equal(firstReceipt.SeriesIds, invalidReceipt.SeriesIds);
            Assert.Equal(firstReceipt.DemandIds, invalidReceipt.DemandIds);
            Assert.Equal("A01-01", active.GetProperty("currentDemand").GetProperty("liveMesFields").GetProperty("area").GetString());
            Assert.Equal("NOT_READABLE", active.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
            Assert.Equal(
                ["INVALID_MES_FIELD_FORMAT"],
                active.GetProperty("currentDemand").GetProperty("readabilityBlockers")
                    .EnumerateArray()
                    .Select(blocker => blocker.GetString()));
            var activeCondition = Assert.Single(active.GetProperty("currentConditions").EnumerateArray());
            var activePeriod = Assert.Single(active.GetProperty("errorPeriods").EnumerateArray());
            AssertFieldErrorIdentity(activeCondition, demandId, "AREA", "INVALID_MES_FIELD_FORMAT", "DATA_FORMAT");
            AssertFieldErrorIdentity(activePeriod, demandId, "AREA", "INVALID_MES_FIELD_FORMAT", "DATA_FORMAT");
            invalidPeriodId = activePeriod.GetProperty("periodId").GetString()!;
            Assert.Equal(invalidPeriodId, activeCondition.GetProperty("periodId").GetString());
            Assert.Equal("CONDITION_DETECTED", activePeriod.GetProperty("startReason").GetString());
            Assert.Equal(invalidStartedAt, activePeriod.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, activePeriod.GetProperty("endedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, activePeriod.GetProperty("endReason").ValueKind);
            var initialEvidence = Assert.Single(activePeriod.GetProperty("evidence").EnumerateArray());
            Assert.Equal("CONDITION_DETECTED", initialEvidence.GetProperty("evidenceKind").GetString());
            AssertObservedValue(initialEvidence, "A01-01");
            AssertEvidenceLink(
                initialEvidence,
                invalidStartedAt,
                "poll-ticket03-area-invalid-first",
                invalidReceipt.ProjectionCommitId!);

            var failureReceipt = await ingestor.IngestAsync(NonSuccessRound(
                "poll-ticket03-area-failure",
                failureAt,
                MesTaskUnionRoundOutcome.Failure,
                recovered));
            var incompleteReceipt = await ingestor.IngestAsync(NonSuccessRound(
                "poll-ticket03-area-incomplete",
                incompleteAt,
                MesTaskUnionRoundOutcome.Incomplete,
                recovered));
            Assert.Null(failureReceipt.ProjectionCommitId);
            Assert.Null(incompleteReceipt.ProjectionCommitId);

            var failureTrace = await firstClient.GetFromJsonAsync<JsonElement>(
                "/api/v2/poll-traces/poll-ticket03-area-failure");
            var incompleteTrace = await firstClient.GetFromJsonAsync<JsonElement>(
                "/api/v2/poll-traces/poll-ticket03-area-incomplete");
            Assert.Equal("FAILURE", failureTrace.GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Null, failureTrace.GetProperty("projectionCommit").ValueKind);
            Assert.Equal("INCOMPLETE", incompleteTrace.GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Null, incompleteTrace.GetProperty("projectionCommit").ValueKind);

            var afterNonSuccess = await GetSeriesByKeyAsync(firstClient, valid.Sublot!);
            Assert.Equal(active.GetRawText(), afterNonSuccess.GetRawText());
            stateBeforeRestart = afterNonSuccess.GetRawText();
        }

        await using (var restartedFactory = CreateFactory())
        {
            var restartedClient = restartedFactory.CreateClient();
            var ingestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            var afterRestart = await restartedClient.GetFromJsonAsync<JsonElement>(
                $"/api/v2/demand-series/{Uri.EscapeDataString(seriesId)}");
            Assert.Equal(stateBeforeRestart, afterRestart.GetRawText());

            var changedReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket03-area-invalid-changed",
                invalidChangedAt,
                secondInvalid));
            var afterEvidenceChange = await GetSeriesByKeyAsync(restartedClient, valid.Sublot!);
            Assert.Equal(seriesId, changedReceipt.SeriesIds.Single());
            Assert.Equal(demandId, changedReceipt.DemandIds.Single());
            Assert.Equal(1, afterEvidenceChange.GetProperty("currentConditions").GetArrayLength());
            Assert.Equal(1, afterEvidenceChange.GetProperty("errorPeriods").GetArrayLength());
            var continuedPeriod = afterEvidenceChange.GetProperty("errorPeriods")[0];
            Assert.Equal(invalidPeriodId, continuedPeriod.GetProperty("periodId").GetString());
            Assert.Equal(2, continuedPeriod.GetProperty("evidence").GetArrayLength());
            AssertObservedValue(continuedPeriod.GetProperty("evidence")[0], "A01-01");
            AssertObservedValue(continuedPeriod.GetProperty("evidence")[1], "A00-1");
            Assert.Equal(
                "CONDITION_EVIDENCE_CHANGED",
                continuedPeriod.GetProperty("evidence")[1].GetProperty("evidenceKind").GetString());
            AssertEvidenceLink(
                continuedPeriod.GetProperty("evidence")[1],
                invalidChangedAt,
                "poll-ticket03-area-invalid-changed",
                changedReceipt.ProjectionCommitId!);

            var recoveredReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket03-area-recovered",
                clearedAt,
                recovered));
            var afterRecovery = await GetSeriesByKeyAsync(restartedClient, valid.Sublot!);
            Assert.Equal(seriesId, recoveredReceipt.SeriesIds.Single());
            Assert.Equal(demandId, recoveredReceipt.DemandIds.Single());
            Assert.Equal(1, afterRecovery.GetProperty("currentDemand").GetProperty("generation").GetInt32());
            Assert.Equal("A11-11", afterRecovery.GetProperty("currentDemand").GetProperty("liveMesFields").GetProperty("area").GetString());
            Assert.Equal("READABLE", afterRecovery.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
            Assert.Empty(afterRecovery.GetProperty("currentDemand").GetProperty("readabilityBlockers").EnumerateArray());
            Assert.Equal(0, afterRecovery.GetProperty("currentConditions").GetArrayLength());
            var endedPeriod = Assert.Single(afterRecovery.GetProperty("errorPeriods").EnumerateArray());
            Assert.Equal(invalidPeriodId, endedPeriod.GetProperty("periodId").GetString());
            Assert.Equal(clearedAt, endedPeriod.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal("CONDITION_CLEARED", endedPeriod.GetProperty("endReason").GetString());
            var endedEvidence = endedPeriod.GetProperty("evidence");
            Assert.Equal(
                "CONDITION_CLEARED",
                endedEvidence[endedEvidence.GetArrayLength() - 1].GetProperty("evidenceKind").GetString());
            AssertStrictSeriesSequence(afterRecovery.GetProperty("events"));

            var recurredReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket03-area-recurred",
                recurredAt,
                firstInvalid));
            var afterRecurrence = await GetSeriesByKeyAsync(restartedClient, valid.Sublot!);
            Assert.Equal(seriesId, recurredReceipt.SeriesIds.Single());
            Assert.Equal(demandId, recurredReceipt.DemandIds.Single());
            Assert.Equal("NOT_READABLE", afterRecurrence.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
            Assert.Equal(1, afterRecurrence.GetProperty("currentConditions").GetArrayLength());
            Assert.Equal(2, afterRecurrence.GetProperty("errorPeriods").GetArrayLength());
            Assert.Equal(5, afterRecurrence.GetProperty("rawObservations").GetArrayLength());
            var recurredCondition = afterRecurrence.GetProperty("currentConditions")[0];
            var recurredPeriod = afterRecurrence.GetProperty("errorPeriods")
                .EnumerateArray()
                .Single(period => period.GetProperty("periodId").GetString() != invalidPeriodId);
            var retainedEndedPeriod = afterRecurrence.GetProperty("errorPeriods")
                .EnumerateArray()
                .Single(period => period.GetProperty("periodId").GetString() == invalidPeriodId);
            var recurredPeriodId = recurredPeriod.GetProperty("periodId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(recurredPeriodId));
            Assert.NotEqual(invalidPeriodId, recurredPeriodId);
            Assert.Equal(recurredPeriodId, recurredCondition.GetProperty("periodId").GetString());
            Assert.Equal("CONDITION_CLEARED", retainedEndedPeriod.GetProperty("endReason").GetString());
            Assert.Equal("CONDITION_DETECTED", recurredPeriod.GetProperty("startReason").GetString());
            Assert.Equal(recurredAt, recurredPeriod.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, recurredPeriod.GetProperty("endedAt").ValueKind);
            var recurredEvidence = Assert.Single(recurredPeriod.GetProperty("evidence").EnumerateArray());
            Assert.Equal("CONDITION_DETECTED", recurredEvidence.GetProperty("evidenceKind").GetString());
            AssertObservedValue(recurredEvidence, "A01-01");
            AssertEvidenceLink(
                recurredEvidence,
                recurredAt,
                "poll-ticket03-area-recurred",
                recurredReceipt.ProjectionCommitId!);
            AssertStrictSeriesSequence(afterRecurrence.GetProperty("events"));

            var contract = await restartedClient.GetFromJsonAsync<JsonElement>("/api/v2/contract");
            AssertSeriesErrorCatalog(contract.GetProperty("seriesErrorCatalog"));
        }
    }

    [Ticket01SqlServerFact]
    public async Task First_round_bootstraps_every_erroneous_series_and_exact_repeats_add_no_error_noise()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var firstCompletedAt = new DateTimeOffset(2026, 8, 13, 6, 0, 2, TimeSpan.Zero);
        var repeatedCompletedAt = firstCompletedAt.AddMinutes(1);
        var overlongValue = new string('X', 300);
        var overlongInvalidArea = ValidObservation("SL-TICKET03-MULTI-AREA") with
        {
            Area = overlongValue,
            Eqp = overlongValue,
            Step = overlongValue,
            Package = overlongValue,
        };
        var missingEqp = ValidObservation("SL-TICKET03-MULTI-EQP") with { Eqp = " " };

        var firstReceipt = await ingestor.IngestAsync(new MesTaskUnionRound(
            "poll-ticket03-multi-bootstrap",
            "mes-task-union-ticket03-v1",
            MesTaskUnionRoundOutcome.Success,
            firstCompletedAt.AddSeconds(-2),
            firstCompletedAt,
            [overlongInvalidArea, missingEqp]));
        var areaSeries = await GetSeriesByKeyAsync(client, overlongInvalidArea.Sublot!);
        var eqpSeries = await GetSeriesByKeyAsync(client, missingEqp.Sublot!);
        var areaEventIds = ReadEventIds(areaSeries);
        var eqpEventIds = ReadEventIds(eqpSeries);

        Assert.Equal(2, firstReceipt.SeriesIds.Count);
        Assert.Equal(2, firstReceipt.DemandIds.Count);
        AssertLiveFields(areaSeries.GetProperty("currentDemand").GetProperty("liveMesFields"), overlongInvalidArea);
        AssertRawObservation(areaSeries.GetProperty("rawObservations")[0], overlongInvalidArea);
        AssertLiveFields(eqpSeries.GetProperty("currentDemand").GetProperty("liveMesFields"), missingEqp);
        AssertRawObservation(eqpSeries.GetProperty("rawObservations")[0], missingEqp);
        AssertBootstrappedSingleFieldError(
            areaSeries,
            "AREA",
            "INVALID_MES_FIELD_FORMAT",
            firstCompletedAt,
            "poll-ticket03-multi-bootstrap",
            firstReceipt.ProjectionCommitId!);
        AssertBootstrappedSingleFieldError(
            eqpSeries,
            "EQP",
            "REQUIRED_MES_FIELD_MISSING",
            firstCompletedAt,
            "poll-ticket03-multi-bootstrap",
            firstReceipt.ProjectionCommitId!);

        var repeatedReceipt = await ingestor.IngestAsync(new MesTaskUnionRound(
            "poll-ticket03-multi-repeated",
            "mes-task-union-ticket03-v1",
            MesTaskUnionRoundOutcome.Success,
            repeatedCompletedAt.AddSeconds(-2),
            repeatedCompletedAt,
            [overlongInvalidArea, missingEqp]));
        var repeatedAreaSeries = await GetSeriesByKeyAsync(client, overlongInvalidArea.Sublot!);
        var repeatedEqpSeries = await GetSeriesByKeyAsync(client, missingEqp.Sublot!);

        Assert.Equal(firstReceipt.SeriesIds.Order(), repeatedReceipt.SeriesIds.Order());
        Assert.Equal(firstReceipt.DemandIds.Order(), repeatedReceipt.DemandIds.Order());
        Assert.Equal(areaEventIds, ReadEventIds(repeatedAreaSeries));
        Assert.Equal(eqpEventIds, ReadEventIds(repeatedEqpSeries));
        Assert.Equal(2, repeatedAreaSeries.GetProperty("rawObservations").GetArrayLength());
        Assert.Equal(2, repeatedEqpSeries.GetProperty("rawObservations").GetArrayLength());
        Assert.Equal(
            repeatedReceipt.ProjectionCommitId,
            repeatedAreaSeries.GetProperty("currentDemand").GetProperty("latestProjectionCommitId").GetString());
        Assert.Equal(
            repeatedReceipt.ProjectionCommitId,
            repeatedEqpSeries.GetProperty("currentDemand").GetProperty("latestProjectionCommitId").GetString());
        Assert.Single(repeatedAreaSeries.GetProperty("errorPeriods")[0].GetProperty("evidence").EnumerateArray());
        Assert.Single(repeatedEqpSeries.GetProperty("errorPeriods")[0].GetProperty("evidence").EnumerateArray());
    }

    private static readonly string[] RequiredSubjects = ["AREA", "EQP", "STEP", "DATES", "PACKAGE"];

    private static string?[] ReadEventIds(JsonElement series) =>
        series.GetProperty("events")
            .EnumerateArray()
            .Select(item => item.GetProperty("eventId").GetString())
            .ToArray();

    private static void AssertBootstrappedSingleFieldError(
        JsonElement series,
        string subjectKind,
        string code,
        DateTimeOffset startedAt,
        string pollTraceId,
        string projectionCommitId)
    {
        var condition = Assert.Single(series.GetProperty("currentConditions").EnumerateArray());
        var period = Assert.Single(series.GetProperty("errorPeriods").EnumerateArray());
        Assert.Equal(subjectKind, condition.GetProperty("subjectKind").GetString());
        Assert.Equal(code, condition.GetProperty("code").GetString());
        Assert.Equal(period.GetProperty("periodId").GetString(), condition.GetProperty("periodId").GetString());
        Assert.Equal("BOOTSTRAPPED_CURRENT_CONDITION", period.GetProperty("startReason").GetString());
        Assert.Equal(startedAt, period.GetProperty("startedAt").GetDateTimeOffset());
        var evidence = Assert.Single(period.GetProperty("evidence").EnumerateArray());
        Assert.Equal("BOOTSTRAPPED_CURRENT_CONDITION", evidence.GetProperty("evidenceKind").GetString());
        AssertEvidenceLink(evidence, startedAt, pollTraceId, projectionCommitId);
    }

    private static MesTaskUnionObservation ValidObservation(string sublot) =>
        new(
            WorkType,
            sublot,
            Area: "A1-1",
            Eqp: "WB-01",
            Step: "焊线2",
            MesSourceDate: new DateTimeOffset(2026, 8, 13, 8, 45, 0, TimeSpan.FromHours(8)),
            Package: "QFN48");

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        MesTaskUnionObservation observation) =>
        new(
            pollTraceId,
            "mes-task-union-ticket03-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            [observation]);

    private static MesTaskUnionRound NonSuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        MesTaskUnionRoundOutcome outcome,
        MesTaskUnionObservation observation) =>
        new(
            pollTraceId,
            "mes-task-union-ticket03-v1",
            outcome,
            completedAt.AddSeconds(-2),
            completedAt,
            [observation]);

    private static async Task<JsonElement> GetSeriesByKeyAsync(HttpClient client, string sublot) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/api/v2/demand-series/by-key?workType={Uri.EscapeDataString(WorkType)}&sublot={Uri.EscapeDataString(sublot)}");

    private static void AssertLiveFields(JsonElement fields, MesTaskUnionObservation expected)
    {
        AssertJsonString(fields.GetProperty("area"), expected.Area);
        AssertJsonString(fields.GetProperty("eqp"), expected.Eqp);
        AssertJsonString(fields.GetProperty("step"), expected.Step);
        if (expected.MesSourceDate is null)
        {
            Assert.Equal(JsonValueKind.Null, fields.GetProperty("mesSourceDate").ValueKind);
        }
        else
        {
            Assert.Equal(expected.MesSourceDate.Value, fields.GetProperty("mesSourceDate").GetDateTimeOffset());
        }
        AssertJsonString(fields.GetProperty("package"), expected.Package);
    }

    private static void AssertRawObservation(JsonElement raw, MesTaskUnionObservation expected)
    {
        Assert.Equal("ASSIGNED", raw.GetProperty("assignment").GetString());
        AssertJsonString(raw.GetProperty("area"), expected.Area);
        AssertJsonString(raw.GetProperty("eqp"), expected.Eqp);
        AssertJsonString(raw.GetProperty("step"), expected.Step);
        if (expected.MesSourceDate is null)
        {
            Assert.Equal(JsonValueKind.Null, raw.GetProperty("mesSourceDate").ValueKind);
        }
        else
        {
            Assert.Equal(expected.MesSourceDate.Value, raw.GetProperty("mesSourceDate").GetDateTimeOffset());
        }
        AssertJsonString(raw.GetProperty("package"), expected.Package);
    }

    private static void AssertStrictSeriesSequence(JsonElement events)
    {
        var sequences = events.EnumerateArray()
            .Select(item => item.GetProperty("seriesSequence").GetInt64())
            .ToArray();
        Assert.All(
            sequences.Zip(sequences.Skip(1)),
            pair => Assert.True(
                pair.First < pair.Second,
                $"SeriesSequence must be strictly increasing, but observed {pair.First} then {pair.Second}."));
    }

    private static JsonElement ReadEventPayload(JsonElement item)
    {
        using var document = JsonDocument.Parse(item.GetProperty("payloadJson").GetString()!);
        return document.RootElement.Clone();
    }

    private static void AssertStringChange(JsonElement item, string before, string after)
    {
        var payload = ReadEventPayload(item);
        Assert.Equal(before, payload.GetProperty("before").GetString());
        Assert.Equal(after, payload.GetProperty("after").GetString());
    }

    private static void AssertDateChange(
        JsonElement item,
        DateTimeOffset before,
        DateTimeOffset after)
    {
        var payload = ReadEventPayload(item);
        Assert.Equal(before, payload.GetProperty("before").GetDateTimeOffset());
        Assert.Equal(after, payload.GetProperty("after").GetDateTimeOffset());
    }

    private static JsonElement FindBySubject(JsonElement items, string subject) =>
        items.EnumerateArray().Single(item =>
            item.GetProperty("subjectKind").GetString() == subject);

    private static void AssertFieldErrorIdentity(
        JsonElement item,
        string demandId,
        string subject,
        string code,
        string category)
    {
        Assert.Equal(code, item.GetProperty("code").GetString());
        Assert.Equal(category, item.GetProperty("category").GetString());
        Assert.Equal("ERROR", item.GetProperty("severity").GetString());
        Assert.Equal($"DEMAND:{demandId}", item.GetProperty("target").GetString());
        Assert.Equal(subject, item.GetProperty("subjectKind").GetString());
    }

    private static void AssertEvidenceLink(
        JsonElement evidence,
        DateTimeOffset observedAt,
        string pollTraceId,
        string projectionCommitId)
    {
        Assert.Equal(observedAt, evidence.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(pollTraceId, evidence.GetProperty("pollTraceId").GetString());
        Assert.Equal(projectionCommitId, evidence.GetProperty("projectionCommitId").GetString());
    }

    private static void AssertObservedValue(JsonElement evidence, string? expected)
    {
        AssertJsonString(evidence.GetProperty("observedValue"), expected);
    }

    private static string? GetFieldValue(MesTaskUnionObservation observation, string subject) =>
        subject switch
        {
            "AREA" => observation.Area,
            "EQP" => observation.Eqp,
            "STEP" => observation.Step,
            "DATES" => observation.MesSourceDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            "PACKAGE" => observation.Package,
            _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, "Unknown MES field subject."),
        };

    private static void AssertJsonString(JsonElement actual, string? expected)
    {
        if (expected is null)
        {
            Assert.Equal(JsonValueKind.Null, actual.ValueKind);
            return;
        }

        Assert.Equal(JsonValueKind.String, actual.ValueKind);
        Assert.Equal(expected, actual.GetString());
    }

    private static void AssertSeriesErrorCatalog(JsonElement catalog)
    {
        Assert.Equal(5, catalog.GetArrayLength());
        var definitions = catalog.EnumerateArray().ToDictionary(
            item => item.GetProperty("code").GetString()!,
            StringComparer.Ordinal);
        AssertDefinition(definitions, "REQUIRED_MES_FIELD_MISSING", "DATA_COMPLETENESS", "DEMAND", "A required MES field is null, empty, or whitespace.");
        AssertDefinition(definitions, "INVALID_MES_FIELD_FORMAT", "DATA_FORMAT", "DEMAND", "A present MES field does not match its domain format.");
        AssertDefinition(definitions, "DUPLICATE_TRANSPORT_DEMAND_KEY", "OBSERVATION_CONFLICT", "DEMAND", "One round contains multiple raw observations for a TransportDemandKey.");
        AssertDefinition(definitions, "SUBLOT_MULTIPLE_WORK_TYPES", "OBSERVATION_CONFLICT", "DEMAND", "One SUBLOT appears in multiple WorkTypes in the same round.");
        AssertDefinition(definitions, "LONG_GONE_BUT_VISIBLE", "LIFECYCLE_CONFLICT", "SERIES", "An archived DemandSeries became visible in MES again.");
    }

    private static void AssertDefinition(
        IReadOnlyDictionary<string, JsonElement> definitions,
        string code,
        string category,
        string scope,
        string meaning)
    {
        var definition = definitions[code];
        Assert.Equal(category, definition.GetProperty("category").GetString());
        Assert.Equal("ERROR", definition.GetProperty("severity").GetString());
        Assert.Equal(scope, definition.GetProperty("scope").GetString());
        Assert.Equal(meaning, definition.GetProperty("meaning").GetString());
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Production));

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__EnableLegacyDevelopmentEndpoints"] = "false",
            [$"{MesIngestHostOptions.SectionName}__SnapshotSource"] = MesIngestHostOptions.NoRoundSource,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
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
