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
public sealed class DuplicateKeyAndMultipleWorkTypesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DuplicateWorkType = "WIRE_TO_NITROGEN";
    private const string DuplicateSublot = "SL-TICKET04-DUPLICATE";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public DuplicateKeyAndMultipleWorkTypesTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Ticket01SqlServerFact]
    public async Task Duplicate_multiset_reorders_without_noise_changes_evidence_in_place_and_recovers_the_same_demand_after_restart()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        AssertDatabaseEvidence(database);
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var firstCompletedAt = new DateTimeOffset(2026, 8, 13, 8, 0, 2, TimeSpan.Zero);
        var rowA = Observation(
            DuplicateWorkType,
            DuplicateSublot,
            area: "A1-1",
            eqp: "EQP-DUP-A",
            step: "STEP-DUP-A",
            sourceDate: firstCompletedAt.AddMinutes(-5),
            package: "PKG-DUP-A");
        var rowB = Observation(
            DuplicateWorkType,
            DuplicateSublot,
            area: "B2-2",
            eqp: "EQP-DUP-B",
            step: "STEP-DUP-B",
            sourceDate: firstCompletedAt.AddMinutes(-4),
            package: "PKG-DUP-B");
        var rowBChanged = rowB with { Package = "PKG-DUP-B-CHANGED" };
        var rowC = Observation(
            DuplicateWorkType,
            DuplicateSublot,
            area: "C3-3",
            eqp: "EQP-DUP-C",
            step: "STEP-DUP-C",
            sourceDate: firstCompletedAt.AddMinutes(-3),
            package: "PKG-DUP-C");

        string seriesId;
        string demandId;
        string periodId;
        string beforeRestartJson;
        string countChangeTraceJson;
        RoundCommitReceipt firstReceipt;
        RoundCommitReceipt countChangeReceipt;

        await using (var firstFactory = CreateFactory())
        {
            using var client = firstFactory.CreateClient();
            var ingestor = firstFactory.Services.GetRequiredService<RoundIngestor>();

            firstReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket04-duplicate-first",
                firstCompletedAt,
                rowA,
                rowB));
            Assert.Single(firstReceipt.SeriesIds);
            Assert.Single(firstReceipt.DemandIds);
            seriesId = firstReceipt.SeriesIds.Single();
            demandId = firstReceipt.DemandIds.Single();

            var first = await ReadSeriesByKeyAsync(client, DuplicateWorkType, DuplicateSublot);
            Assert.Equal(seriesId, first.GetProperty("seriesId").GetString());
            Assert.Equal(demandId, first.GetProperty("currentDemand").GetProperty("demandId").GetString());
            Assert.Equal(1, first.GetProperty("currentDemand").GetProperty("generation").GetInt32());
            Assert.Equal(JsonValueKind.Null, first.GetProperty("currentDemand").GetProperty("liveMesFields").ValueKind);
            Assert.Equal("NOT_READABLE", first.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
            Assert.Equal(
                ["DUPLICATE_TRANSPORT_DEMAND_KEY"],
                first.GetProperty("currentDemand").GetProperty("readabilityBlockers")
                    .EnumerateArray().Select(value => value.GetString()).ToArray());
            AssertCurrentDemandAdvance(first, firstReceipt, firstCompletedAt);
            AssertRoundRows(first, "poll-ticket04-duplicate-first", firstReceipt.ProjectionCommitId!, rowA, rowB);

            var firstCondition = AssertSingleDuplicateCondition(first, demandId);
            periodId = firstCondition.GetProperty("periodId").GetString()!;
            AssertCanonicalDuplicateEvidence(firstCondition.GetProperty("observedValue"), rowA, rowB);
            var firstPeriod = AssertSingleDuplicatePeriod(first, periodId, "BOOTSTRAPPED_CURRENT_CONDITION");
            Assert.Single(firstPeriod.GetProperty("evidence").EnumerateArray());
            var eventIdsBeforeReorder = ReadEventIds(first);
            var evidenceIdsBeforeReorder = ReadEvidenceIds(firstPeriod);

            var firstTraceJson = (await ReadTraceAsync(client, "poll-ticket04-duplicate-first")).GetRawText();
            var replayReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket04-duplicate-first",
                firstCompletedAt.AddHours(4),
                rowB with { MesSourceDate = rowB.MesSourceDate!.Value.ToOffset(TimeSpan.FromHours(8)) },
                rowA with { MesSourceDate = rowA.MesSourceDate!.Value.ToOffset(TimeSpan.FromHours(8)) }));
            Assert.True(replayReceipt.IsReplay);
            Assert.Equal(firstReceipt.PollTraceId, replayReceipt.PollTraceId);
            Assert.Equal(firstReceipt.Outcome, replayReceipt.Outcome);
            Assert.Equal(firstReceipt.ProjectionCommitId, replayReceipt.ProjectionCommitId);
            Assert.Equal(firstReceipt.SeriesIds, replayReceipt.SeriesIds);
            Assert.Equal(firstReceipt.DemandIds, replayReceipt.DemandIds);
            Assert.Equal(first.GetRawText(), (await ReadSeriesByKeyAsync(client, DuplicateWorkType, DuplicateSublot)).GetRawText());
            Assert.Equal(firstTraceJson, (await ReadTraceAsync(client, "poll-ticket04-duplicate-first")).GetRawText());

            var reorderReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket04-duplicate-reordered",
                firstCompletedAt.AddMinutes(1),
                rowB,
                rowA));
            Assert.Equal(firstReceipt.SeriesIds, reorderReceipt.SeriesIds);
            Assert.Equal(firstReceipt.DemandIds, reorderReceipt.DemandIds);
            var reordered = await ReadSeriesByKeyAsync(client, DuplicateWorkType, DuplicateSublot);
            Assert.Equal(eventIdsBeforeReorder, ReadEventIds(reordered));
            var reorderedPeriod = AssertSingleDuplicatePeriod(reordered, periodId, "BOOTSTRAPPED_CURRENT_CONDITION");
            Assert.Equal(evidenceIdsBeforeReorder, ReadEvidenceIds(reorderedPeriod));
            AssertCurrentDemandAdvance(reordered, reorderReceipt, firstCompletedAt.AddMinutes(1));
            AssertRoundRows(reordered, "poll-ticket04-duplicate-reordered", reorderReceipt.ProjectionCommitId!, rowB, rowA);

            var contentChangeReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket04-duplicate-content-change",
                firstCompletedAt.AddMinutes(2),
                rowA,
                rowBChanged));
            var contentChanged = await ReadSeriesByKeyAsync(client, DuplicateWorkType, DuplicateSublot);
            Assert.Equal(seriesId, contentChanged.GetProperty("seriesId").GetString());
            Assert.Equal(demandId, contentChanged.GetProperty("currentDemand").GetProperty("demandId").GetString());
            var contentCondition = AssertSingleDuplicateCondition(contentChanged, demandId);
            Assert.Equal(periodId, contentCondition.GetProperty("periodId").GetString());
            AssertCanonicalDuplicateEvidence(contentCondition.GetProperty("observedValue"), rowA, rowBChanged);
            var contentPeriod = AssertSingleDuplicatePeriod(contentChanged, periodId, "BOOTSTRAPPED_CURRENT_CONDITION");
            Assert.Equal(2, contentPeriod.GetProperty("evidence").GetArrayLength());
            Assert.Equal(
                ["BOOTSTRAPPED_CURRENT_CONDITION", "CONDITION_EVIDENCE_CHANGED"],
                ReadEvidenceKinds(contentPeriod));
            AssertCurrentDemandAdvance(contentChanged, contentChangeReceipt, firstCompletedAt.AddMinutes(2));
            AssertRoundRows(contentChanged, "poll-ticket04-duplicate-content-change", contentChangeReceipt.ProjectionCommitId!, rowA, rowBChanged);

            countChangeReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket04-duplicate-count-change",
                firstCompletedAt.AddMinutes(3),
                rowA,
                rowBChanged,
                rowC));
            var countChanged = await ReadSeriesByKeyAsync(client, DuplicateWorkType, DuplicateSublot);
            Assert.Equal(seriesId, countChanged.GetProperty("seriesId").GetString());
            Assert.Equal(demandId, countChanged.GetProperty("currentDemand").GetProperty("demandId").GetString());
            Assert.Equal(1, countChanged.GetProperty("currentDemand").GetProperty("generation").GetInt32());
            Assert.Equal(JsonValueKind.Null, countChanged.GetProperty("currentDemand").GetProperty("liveMesFields").ValueKind);
            var countCondition = AssertSingleDuplicateCondition(countChanged, demandId);
            Assert.Equal(periodId, countCondition.GetProperty("periodId").GetString());
            AssertCanonicalDuplicateEvidence(countCondition.GetProperty("observedValue"), rowA, rowBChanged, rowC);
            var countPeriod = AssertSingleDuplicatePeriod(countChanged, periodId, "BOOTSTRAPPED_CURRENT_CONDITION");
            Assert.Equal(3, countPeriod.GetProperty("evidence").GetArrayLength());
            Assert.Equal(
                [
                    "BOOTSTRAPPED_CURRENT_CONDITION",
                    "CONDITION_EVIDENCE_CHANGED",
                    "CONDITION_EVIDENCE_CHANGED",
                ],
                ReadEvidenceKinds(countPeriod));
            AssertCurrentDemandAdvance(countChanged, countChangeReceipt, firstCompletedAt.AddMinutes(3));
            AssertRoundRows(countChanged, "poll-ticket04-duplicate-count-change", countChangeReceipt.ProjectionCommitId!, rowA, rowBChanged, rowC);

            var countTrace = await ReadTraceAsync(client, "poll-ticket04-duplicate-count-change");
            Assert.Equal(countChangeReceipt.ProjectionCommitId, countTrace.GetProperty("projectionCommit").GetProperty("projectionCommitId").GetString());
            Assert.Equal(3, countTrace.GetProperty("observations").GetArrayLength());
            Assert.All(countTrace.GetProperty("observations").EnumerateArray(), raw =>
            {
                Assert.Equal("ASSIGNED", raw.GetProperty("assignment").GetString());
                Assert.Equal(seriesId, raw.GetProperty("seriesId").GetString());
                Assert.Equal(demandId, raw.GetProperty("demandId").GetString());
                Assert.Equal(countChangeReceipt.ProjectionCommitId, raw.GetProperty("projectionCommitId").GetString());
            });

            beforeRestartJson = countChanged.GetRawText();
            countChangeTraceJson = countTrace.GetRawText();
        }

        await using (var restartedFactory = CreateFactory())
        {
            using var restartedClient = restartedFactory.CreateClient();
            var restartedIngestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            Assert.Equal(
                beforeRestartJson,
                (await ReadSeriesByKeyAsync(restartedClient, DuplicateWorkType, DuplicateSublot)).GetRawText());
            Assert.Equal(
                countChangeTraceJson,
                (await ReadTraceAsync(restartedClient, "poll-ticket04-duplicate-count-change")).GetRawText());

            var recoveryCompletedAt = firstCompletedAt.AddMinutes(4);
            var recoveryReceipt = await restartedIngestor.IngestAsync(SuccessRound(
                "poll-ticket04-duplicate-recovery",
                recoveryCompletedAt,
                rowBChanged));
            Assert.Equal([seriesId], recoveryReceipt.SeriesIds);
            Assert.Equal([demandId], recoveryReceipt.DemandIds);

            var recovered = await ReadSeriesByKeyAsync(restartedClient, DuplicateWorkType, DuplicateSublot);
            Assert.Equal(seriesId, recovered.GetProperty("seriesId").GetString());
            var recoveredDemand = recovered.GetProperty("currentDemand");
            Assert.Equal(demandId, recoveredDemand.GetProperty("demandId").GetString());
            Assert.Equal(1, recoveredDemand.GetProperty("generation").GetInt32());
            Assert.Equal("READABLE", recoveredDemand.GetProperty("externalReadabilityState").GetString());
            Assert.Empty(recoveredDemand.GetProperty("readabilityBlockers").EnumerateArray());
            AssertObservationFields(recoveredDemand.GetProperty("liveMesFields"), rowBChanged);
            Assert.Empty(recovered.GetProperty("currentConditions").EnumerateArray());
            AssertCurrentDemandAdvance(recovered, recoveryReceipt, recoveryCompletedAt);
            AssertRoundRows(recovered, "poll-ticket04-duplicate-recovery", recoveryReceipt.ProjectionCommitId!, rowBChanged);

            var recoveredPeriod = AssertSingleDuplicatePeriod(recovered, periodId, "BOOTSTRAPPED_CURRENT_CONDITION");
            Assert.Equal(recoveryCompletedAt, recoveredPeriod.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal("CONDITION_CLEARED", recoveredPeriod.GetProperty("endReason").GetString());
            Assert.Equal(4, recoveredPeriod.GetProperty("evidence").GetArrayLength());
            Assert.Equal(
                [
                    "BOOTSTRAPPED_CURRENT_CONDITION",
                    "CONDITION_EVIDENCE_CHANGED",
                    "CONDITION_EVIDENCE_CHANGED",
                    "CONDITION_CLEARED",
                ],
                ReadEvidenceKinds(recoveredPeriod));
            var closingEvidence = recoveredPeriod.GetProperty("evidence")[3];
            Assert.Equal("poll-ticket04-duplicate-recovery", closingEvidence.GetProperty("pollTraceId").GetString());
            Assert.Equal(recoveryReceipt.ProjectionCommitId, closingEvidence.GetProperty("projectionCommitId").GetString());
            Assert.Equal(demandId, closingEvidence.GetProperty("demandId").GetString());
            AssertCanonicalDuplicateEvidence(closingEvidence.GetProperty("observedValue"), rowBChanged);
        }
    }

    [Ticket01SqlServerFact]
    public async Task Exact_duplicate_observations_preserve_multiplicity_and_cannot_clear_a_field_error_without_unique_counterevidence()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        AssertDatabaseEvidence(database);
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var firstCompletedAt = new DateTimeOffset(2026, 8, 13, 8, 30, 2, TimeSpan.Zero);
        var invalid = Observation(
            DuplicateWorkType,
            "SL-TICKET04-FIELD-CONFLICT",
            area: "A01-01",
            eqp: "EQP-FIELD-CONFLICT-A",
            step: "STEP-FIELD-CONFLICT-A",
            sourceDate: firstCompletedAt.AddMinutes(-2),
            package: "PKG-FIELD-CONFLICT-A");
        var valid = Observation(
            DuplicateWorkType,
            invalid.Sublot!,
            area: "A1-1",
            eqp: "EQP-FIELD-CONFLICT-B",
            step: "STEP-FIELD-CONFLICT-B",
            sourceDate: firstCompletedAt.AddMinutes(-1),
            package: "PKG-FIELD-CONFLICT-B");

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        var firstReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket04-field-conflict-first",
            firstCompletedAt,
            invalid));
        var first = await ReadSeriesByKeyAsync(client, invalid.WorkType!, invalid.Sublot!);
        var seriesId = first.GetProperty("seriesId").GetString()!;
        var demandId = first.GetProperty("currentDemand").GetProperty("demandId").GetString()!;
        Assert.Equal(firstReceipt.SeriesIds.Single(), seriesId);
        Assert.Equal(firstReceipt.DemandIds.Single(), demandId);
        var invalidCondition = Assert.Single(first.GetProperty("currentConditions").EnumerateArray());
        Assert.Equal("INVALID_MES_FIELD_FORMAT", invalidCondition.GetProperty("code").GetString());
        Assert.Equal("AREA", invalidCondition.GetProperty("subjectKind").GetString());
        var invalidPeriodId = invalidCondition.GetProperty("periodId").GetString()!;

        var duplicateReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket04-field-conflict-duplicate",
            firstCompletedAt.AddMinutes(1),
            invalid,
            invalid));
        Assert.Equal([seriesId], duplicateReceipt.SeriesIds);
        Assert.Equal([demandId], duplicateReceipt.DemandIds);
        var duplicate = await ReadSeriesByKeyAsync(client, invalid.WorkType!, invalid.Sublot!);
        Assert.Equal(JsonValueKind.Null, duplicate.GetProperty("currentDemand").GetProperty("liveMesFields").ValueKind);
        Assert.Equal("NOT_READABLE", duplicate.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
        Assert.Equal(
            ["DUPLICATE_TRANSPORT_DEMAND_KEY", "INVALID_MES_FIELD_FORMAT"],
            duplicate.GetProperty("currentDemand").GetProperty("readabilityBlockers")
                .EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        var duplicateConditions = duplicate.GetProperty("currentConditions").EnumerateArray().ToArray();
        Assert.Equal(2, duplicateConditions.Length);
        var retainedInvalidCondition = Assert.Single(duplicateConditions.Where(value =>
            value.GetProperty("code").GetString() == "INVALID_MES_FIELD_FORMAT"));
        Assert.Equal(invalidPeriodId, retainedInvalidCondition.GetProperty("periodId").GetString());
        Assert.Equal(
            "poll-ticket04-field-conflict-first",
            retainedInvalidCondition.GetProperty("latestPollTraceId").GetString());
        var duplicateCondition = Assert.Single(duplicateConditions.Where(value =>
            value.GetProperty("code").GetString() == "DUPLICATE_TRANSPORT_DEMAND_KEY"));
        var duplicatePeriodId = duplicateCondition.GetProperty("periodId").GetString()!;
        var retainedInvalidPeriod = Assert.Single(duplicate.GetProperty("errorPeriods").EnumerateArray().Where(value =>
            value.GetProperty("periodId").GetString() == invalidPeriodId));
        Assert.Equal(JsonValueKind.Null, retainedInvalidPeriod.GetProperty("endedAt").ValueKind);
        Assert.Equal(["BOOTSTRAPPED_CURRENT_CONDITION"], ReadEvidenceKinds(retainedInvalidPeriod));
        AssertCanonicalDuplicateEvidence(duplicateCondition.GetProperty("observedValue"), invalid, invalid);
        AssertRoundRows(
            duplicate,
            "poll-ticket04-field-conflict-duplicate",
            duplicateReceipt.ProjectionCommitId!,
            invalid,
            invalid);

        var recoveryCompletedAt = firstCompletedAt.AddMinutes(2);
        var recoveryReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket04-field-conflict-recovery",
            recoveryCompletedAt,
            valid));
        Assert.Equal([seriesId], recoveryReceipt.SeriesIds);
        Assert.Equal([demandId], recoveryReceipt.DemandIds);
        var recovered = await ReadSeriesByKeyAsync(client, invalid.WorkType!, invalid.Sublot!);
        Assert.Equal(1, recovered.GetProperty("currentDemand").GetProperty("generation").GetInt32());
        Assert.Equal("READABLE", recovered.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
        AssertObservationFields(recovered.GetProperty("currentDemand").GetProperty("liveMesFields"), valid);
        Assert.Empty(recovered.GetProperty("currentConditions").EnumerateArray());

        var recoveredInvalidPeriod = Assert.Single(recovered.GetProperty("errorPeriods").EnumerateArray().Where(value =>
            value.GetProperty("periodId").GetString() == invalidPeriodId));
        var recoveredDuplicatePeriod = Assert.Single(recovered.GetProperty("errorPeriods").EnumerateArray().Where(value =>
            value.GetProperty("periodId").GetString() == duplicatePeriodId));
        Assert.Equal(recoveryCompletedAt, recoveredInvalidPeriod.GetProperty("endedAt").GetDateTimeOffset());
        Assert.Equal(recoveryCompletedAt, recoveredDuplicatePeriod.GetProperty("endedAt").GetDateTimeOffset());
        Assert.Equal(
            ["BOOTSTRAPPED_CURRENT_CONDITION", "CONDITION_CLEARED"],
            ReadEvidenceKinds(recoveredInvalidPeriod));
        Assert.Equal(
            ["CONDITION_DETECTED", "CONDITION_CLEARED"],
            ReadEvidenceKinds(recoveredDuplicatePeriod));
        Assert.All(
            new[] { recoveredInvalidPeriod, recoveredDuplicatePeriod },
            period =>
            {
                var closingEvidence = period.GetProperty("evidence")[1];
                Assert.Equal("poll-ticket04-field-conflict-recovery", closingEvidence.GetProperty("pollTraceId").GetString());
                Assert.Equal(recoveryReceipt.ProjectionCommitId, closingEvidence.GetProperty("projectionCommitId").GetString());
                Assert.Equal(demandId, closingEvidence.GetProperty("demandId").GetString());
            });
    }

    [Ticket01SqlServerFact]
    public async Task Multiple_work_types_create_independent_series_update_complete_membership_evidence_and_clear_the_still_visible_demand_after_restart()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        AssertDatabaseEvidence(database);
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET04-MULTI-WORK-TYPE";
        const string workTypeA = "LOADPORT_TO_OVEN";
        const string workTypeB = "STAGING_TO_WIRE";
        const string workTypeC = "WIRE_TO_NITROGEN";
        var firstCompletedAt = new DateTimeOffset(2026, 8, 13, 9, 0, 2, TimeSpan.Zero);
        var rowA = Observation(workTypeA, sublot, "A1-1", "EQP-MULTI-A", "STEP-A", firstCompletedAt.AddMinutes(-3), "PKG-A");
        var rowB = Observation(workTypeB, sublot, "B2-2", "EQP-MULTI-B", "STEP-B", firstCompletedAt.AddMinutes(-2), "PKG-B");
        var rowC = Observation(workTypeC, sublot, "C3-3", "EQP-MULTI-C", "STEP-C", firstCompletedAt.AddMinutes(-1), "PKG-C");
        var identities = new Dictionary<string, (string SeriesId, string DemandId, string PeriodId)>(StringComparer.Ordinal);
        string beforeRestartA;
        string beforeRestartB;
        string beforeRestartC;

        await using (var firstFactory = CreateFactory())
        {
            using var client = firstFactory.CreateClient();
            var ingestor = firstFactory.Services.GetRequiredService<RoundIngestor>();
            var firstReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket04-multi-first",
                firstCompletedAt,
                rowB,
                rowA));
            Assert.Equal(2, firstReceipt.SeriesIds.Count);
            Assert.Equal(2, firstReceipt.DemandIds.Count);

            foreach (var (workType, expectedRow, ordinal) in new[]
                     {
                         (workTypeA, rowA, 1),
                         (workTypeB, rowB, 0),
                     })
            {
                var series = await ReadSeriesByKeyAsync(client, workType, sublot);
                var demand = series.GetProperty("currentDemand");
                var seriesId = series.GetProperty("seriesId").GetString()!;
                var demandId = demand.GetProperty("demandId").GetString()!;
                Assert.Contains(seriesId, firstReceipt.SeriesIds);
                Assert.Contains(demandId, firstReceipt.DemandIds);
                Assert.Equal(1, demand.GetProperty("generation").GetInt32());
                AssertObservationFields(demand.GetProperty("liveMesFields"), expectedRow);
                Assert.Equal("NOT_READABLE", demand.GetProperty("externalReadabilityState").GetString());
                Assert.Equal(
                    ["SUBLOT_MULTIPLE_WORK_TYPES"],
                    demand.GetProperty("readabilityBlockers").EnumerateArray()
                        .Select(value => value.GetString()).ToArray());
                var condition = AssertSingleMembershipCondition(series, demandId, workTypeA, workTypeB);
                var periodId = condition.GetProperty("periodId").GetString()!;
                var period = AssertSingleMembershipPeriod(series, periodId, "BOOTSTRAPPED_CURRENT_CONDITION");
                Assert.Single(period.GetProperty("evidence").EnumerateArray());
                Assert.Equal("poll-ticket04-multi-first", period.GetProperty("evidence")[0].GetProperty("pollTraceId").GetString());
                Assert.Equal(firstReceipt.ProjectionCommitId, period.GetProperty("evidence")[0].GetProperty("projectionCommitId").GetString());
                AssertMembershipEvidenceLink(
                    period.GetProperty("evidence")[0],
                    "BOOTSTRAPPED_CURRENT_CONDITION",
                    firstCompletedAt,
                    "poll-ticket04-multi-first",
                    firstReceipt.ProjectionCommitId!,
                    demandId,
                    workTypeA,
                    workTypeB);
                AssertAssignedRows(
                    series,
                    "poll-ticket04-multi-first",
                    firstReceipt.ProjectionCommitId!,
                    (ordinal, expectedRow));
                identities.Add(workType, (seriesId, demandId, periodId));
            }

            AssertTraceAssignments(
                await ReadTraceAsync(client, "poll-ticket04-multi-first"),
                firstReceipt,
                (0, rowB, identities[workTypeB].SeriesId, identities[workTypeB].DemandId),
                (1, rowA, identities[workTypeA].SeriesId, identities[workTypeA].DemandId));

            var changedReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket04-multi-membership-change",
                firstCompletedAt.AddMinutes(1),
                rowC,
                rowA,
                rowB));
            Assert.Equal(3, changedReceipt.SeriesIds.Count);
            Assert.Equal(3, changedReceipt.DemandIds.Count);

            foreach (var (workType, expectedRow, ordinal) in new[]
                     {
                         (workTypeA, rowA, 1),
                         (workTypeB, rowB, 2),
                     })
            {
                var changed = await ReadSeriesByKeyAsync(client, workType, sublot);
                var identity = identities[workType];
                Assert.Equal(identity.SeriesId, changed.GetProperty("seriesId").GetString());
                Assert.Equal(identity.DemandId, changed.GetProperty("currentDemand").GetProperty("demandId").GetString());
                Assert.Equal(1, changed.GetProperty("currentDemand").GetProperty("generation").GetInt32());
                AssertObservationFields(changed.GetProperty("currentDemand").GetProperty("liveMesFields"), expectedRow);
                var condition = AssertSingleMembershipCondition(changed, identity.DemandId, workTypeA, workTypeB, workTypeC);
                Assert.Equal(identity.PeriodId, condition.GetProperty("periodId").GetString());
                var period = AssertSingleMembershipPeriod(changed, identity.PeriodId, "BOOTSTRAPPED_CURRENT_CONDITION");
                Assert.Equal(
                    ["BOOTSTRAPPED_CURRENT_CONDITION", "CONDITION_EVIDENCE_CHANGED"],
                    ReadEvidenceKinds(period));
                AssertMembershipEvidenceLink(
                    period.GetProperty("evidence")[1],
                    "CONDITION_EVIDENCE_CHANGED",
                    firstCompletedAt.AddMinutes(1),
                    "poll-ticket04-multi-membership-change",
                    changedReceipt.ProjectionCommitId!,
                    identity.DemandId,
                    workTypeA,
                    workTypeB,
                    workTypeC);
                AssertAssignedRows(
                    changed,
                    "poll-ticket04-multi-membership-change",
                    changedReceipt.ProjectionCommitId!,
                    (ordinal, expectedRow));
            }

            var seriesC = await ReadSeriesByKeyAsync(client, workTypeC, sublot);
            var demandC = seriesC.GetProperty("currentDemand");
            var conditionC = AssertSingleMembershipCondition(
                seriesC,
                demandC.GetProperty("demandId").GetString()!,
                workTypeA,
                workTypeB,
                workTypeC);
            var identityC = (
                SeriesId: seriesC.GetProperty("seriesId").GetString()!,
                DemandId: demandC.GetProperty("demandId").GetString()!,
                PeriodId: conditionC.GetProperty("periodId").GetString()!);
            Assert.Equal(workTypeC, seriesC.GetProperty("workType").GetString());
            Assert.Equal(sublot, seriesC.GetProperty("sublot").GetString());
            Assert.Contains(identityC.SeriesId, changedReceipt.SeriesIds);
            Assert.Contains(identityC.DemandId, changedReceipt.DemandIds);
            Assert.Equal(1, demandC.GetProperty("generation").GetInt32());
            AssertObservationFields(demandC.GetProperty("liveMesFields"), rowC);
            Assert.Equal("NOT_READABLE", demandC.GetProperty("externalReadabilityState").GetString());
            Assert.Equal(
                ["SUBLOT_MULTIPLE_WORK_TYPES"],
                demandC.GetProperty("readabilityBlockers").EnumerateArray()
                    .Select(value => value.GetString()).ToArray());
            Assert.DoesNotContain(identityC.SeriesId, identities.Values.Select(value => value.SeriesId));
            Assert.DoesNotContain(identityC.DemandId, identities.Values.Select(value => value.DemandId));
            var periodC = AssertSingleMembershipPeriod(seriesC, identityC.PeriodId, "CONDITION_DETECTED");
            Assert.Single(periodC.GetProperty("evidence").EnumerateArray());
            AssertMembershipEvidenceLink(
                periodC.GetProperty("evidence")[0],
                "CONDITION_DETECTED",
                firstCompletedAt.AddMinutes(1),
                "poll-ticket04-multi-membership-change",
                changedReceipt.ProjectionCommitId!,
                identityC.DemandId,
                workTypeA,
                workTypeB,
                workTypeC);
            AssertAssignedRows(
                seriesC,
                "poll-ticket04-multi-membership-change",
                changedReceipt.ProjectionCommitId!,
                (0, rowC));
            identities.Add(workTypeC, identityC);

            AssertTraceAssignments(
                await ReadTraceAsync(client, "poll-ticket04-multi-membership-change"),
                changedReceipt,
                (0, rowC, identities[workTypeC].SeriesId, identities[workTypeC].DemandId),
                (1, rowA, identities[workTypeA].SeriesId, identities[workTypeA].DemandId),
                (2, rowB, identities[workTypeB].SeriesId, identities[workTypeB].DemandId));

            beforeRestartA = (await ReadSeriesByKeyAsync(client, workTypeA, sublot)).GetRawText();
            beforeRestartB = (await ReadSeriesByKeyAsync(client, workTypeB, sublot)).GetRawText();
            beforeRestartC = seriesC.GetRawText();
        }

        await using (var restartedFactory = CreateFactory())
        {
            using var restartedClient = restartedFactory.CreateClient();
            var restartedIngestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            Assert.Equal(beforeRestartA, (await ReadSeriesByKeyAsync(restartedClient, workTypeA, sublot)).GetRawText());
            Assert.Equal(beforeRestartB, (await ReadSeriesByKeyAsync(restartedClient, workTypeB, sublot)).GetRawText());
            Assert.Equal(beforeRestartC, (await ReadSeriesByKeyAsync(restartedClient, workTypeC, sublot)).GetRawText());

            var recoveryCompletedAt = firstCompletedAt.AddMinutes(2);
            var recoveryReceipt = await restartedIngestor.IngestAsync(SuccessRound(
                "poll-ticket04-multi-recovery",
                recoveryCompletedAt,
                rowA));
            Assert.Equal([identities[workTypeA].SeriesId], recoveryReceipt.SeriesIds);
            Assert.Equal([identities[workTypeA].DemandId], recoveryReceipt.DemandIds);

            var recoveredA = await ReadSeriesByKeyAsync(restartedClient, workTypeA, sublot);
            Assert.Equal(identities[workTypeA].SeriesId, recoveredA.GetProperty("seriesId").GetString());
            Assert.Equal(workTypeA, recoveredA.GetProperty("workType").GetString());
            Assert.Equal(sublot, recoveredA.GetProperty("sublot").GetString());
            Assert.Equal(identities[workTypeA].DemandId, recoveredA.GetProperty("currentDemand").GetProperty("demandId").GetString());
            Assert.Equal(1, recoveredA.GetProperty("currentDemand").GetProperty("generation").GetInt32());
            Assert.Equal("READABLE", recoveredA.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
            Assert.Empty(recoveredA.GetProperty("currentConditions").EnumerateArray());
            var endedA = AssertSingleMembershipPeriod(
                recoveredA,
                identities[workTypeA].PeriodId,
                "BOOTSTRAPPED_CURRENT_CONDITION");
            Assert.Equal(recoveryCompletedAt, endedA.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal("CONDITION_CLEARED", endedA.GetProperty("endReason").GetString());
            Assert.Equal(
                [
                    "BOOTSTRAPPED_CURRENT_CONDITION",
                    "CONDITION_EVIDENCE_CHANGED",
                    "CONDITION_CLEARED",
                ],
                ReadEvidenceKinds(endedA));
            AssertMembershipEvidenceLink(
                endedA.GetProperty("evidence")[2],
                "CONDITION_CLEARED",
                recoveryCompletedAt,
                "poll-ticket04-multi-recovery",
                recoveryReceipt.ProjectionCommitId!,
                identities[workTypeA].DemandId,
                workTypeA);
            AssertAssignedRows(
                recoveredA,
                "poll-ticket04-multi-recovery",
                recoveryReceipt.ProjectionCommitId!,
                (0, rowA));
            AssertTraceAssignments(
                await ReadTraceAsync(restartedClient, "poll-ticket04-multi-recovery"),
                recoveryReceipt,
                (0, rowA, identities[workTypeA].SeriesId, identities[workTypeA].DemandId));

            var persistedB = await ReadSeriesByKeyAsync(restartedClient, workTypeB, sublot);
            var persistedC = await ReadSeriesByKeyAsync(restartedClient, workTypeC, sublot);
            Assert.Equal(identities[workTypeB].SeriesId, persistedB.GetProperty("seriesId").GetString());
            Assert.Equal(identities[workTypeB].DemandId, persistedB.GetProperty("currentDemand").GetProperty("demandId").GetString());
            Assert.Equal(workTypeB, persistedB.GetProperty("workType").GetString());
            Assert.Equal(identities[workTypeC].SeriesId, persistedC.GetProperty("seriesId").GetString());
            Assert.Equal(identities[workTypeC].DemandId, persistedC.GetProperty("currentDemand").GetProperty("demandId").GetString());
            Assert.Equal(workTypeC, persistedC.GetProperty("workType").GetString());
            Assert.Equal(
                "NOT_READABLE",
                persistedB.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
            Assert.Equal(
                "NOT_READABLE",
                persistedC.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
            AssertSingleMembershipCondition(
                persistedB,
                identities[workTypeB].DemandId,
                workTypeA,
                workTypeB,
                workTypeC);
            AssertSingleMembershipCondition(
                persistedC,
                identities[workTypeC].DemandId,
                workTypeA,
                workTypeB,
                workTypeC);
            var activeB = AssertSingleMembershipPeriod(
                persistedB,
                identities[workTypeB].PeriodId,
                "BOOTSTRAPPED_CURRENT_CONDITION");
            var activeC = AssertSingleMembershipPeriod(
                persistedC,
                identities[workTypeC].PeriodId,
                "CONDITION_DETECTED");
            Assert.Equal(JsonValueKind.Null, activeB.GetProperty("endedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, activeC.GetProperty("endedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, activeB.GetProperty("endReason").ValueKind);
            Assert.Equal(JsonValueKind.Null, activeC.GetProperty("endReason").ValueKind);
            Assert.Equal(
                ["BOOTSTRAPPED_CURRENT_CONDITION", "CONDITION_EVIDENCE_CHANGED"],
                ReadEvidenceKinds(activeB));
            Assert.Equal(
                ["CONDITION_DETECTED"],
                ReadEvidenceKinds(activeC));
        }
    }

    [Ticket01SqlServerFact]
    public async Task Duplicate_key_and_multiple_work_type_conditions_coexist_with_complete_raw_assignments()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        AssertDatabaseEvidence(database);
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        const string sublot = "SL-TICKET04-COMBINED-CONFLICT";
        const string workTypeA = "LOADPORT_TO_OVEN";
        const string workTypeB = "STAGING_TO_WIRE";
        var completedAt = new DateTimeOffset(2026, 8, 13, 10, 0, 2, TimeSpan.Zero);
        var rowA1 = Observation(workTypeA, sublot, "A1-1", "EQP-COMBINED-A1", "STEP-A1", completedAt.AddMinutes(-3), "PKG-A1");
        var rowA2 = Observation(workTypeA, sublot, "A1-2", "EQP-COMBINED-A2", "STEP-A2", completedAt.AddMinutes(-2), "PKG-A2");
        var rowB = Observation(workTypeB, sublot, "B2-2", "EQP-COMBINED-B", "STEP-B", completedAt.AddMinutes(-1), "PKG-B");

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var receipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket04-combined-conflict",
            completedAt,
            rowA1,
            rowB,
            rowA2));
        Assert.Equal(2, receipt.SeriesIds.Count);
        Assert.Equal(2, receipt.DemandIds.Count);

        var seriesA = await ReadSeriesByKeyAsync(client, workTypeA, sublot);
        var demandA = seriesA.GetProperty("currentDemand");
        var seriesAId = seriesA.GetProperty("seriesId").GetString()!;
        var demandAId = demandA.GetProperty("demandId").GetString()!;
        Assert.Contains(seriesAId, receipt.SeriesIds);
        Assert.Contains(demandAId, receipt.DemandIds);
        Assert.Equal(JsonValueKind.Null, demandA.GetProperty("liveMesFields").ValueKind);
        Assert.Equal("NOT_READABLE", demandA.GetProperty("externalReadabilityState").GetString());
        Assert.Equal(
            ["DUPLICATE_TRANSPORT_DEMAND_KEY", "SUBLOT_MULTIPLE_WORK_TYPES"],
            demandA.GetProperty("readabilityBlockers").EnumerateArray()
                .Select(value => value.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(2, seriesA.GetProperty("currentConditions").GetArrayLength());
        var duplicateCondition = AssertSingleDuplicateCondition(seriesA, demandAId);
        AssertCanonicalDuplicateEvidence(duplicateCondition.GetProperty("observedValue"), rowA1, rowA2);
        AssertSingleMembershipCondition(seriesA, demandAId, workTypeA, workTypeB);
        AssertAssignedRows(
            seriesA,
            receipt.PollTraceId,
            receipt.ProjectionCommitId!,
            (0, rowA1),
            (2, rowA2));

        var seriesB = await ReadSeriesByKeyAsync(client, workTypeB, sublot);
        var demandB = seriesB.GetProperty("currentDemand");
        var seriesBId = seriesB.GetProperty("seriesId").GetString()!;
        var demandBId = demandB.GetProperty("demandId").GetString()!;
        Assert.Contains(seriesBId, receipt.SeriesIds);
        Assert.Contains(demandBId, receipt.DemandIds);
        AssertObservationFields(demandB.GetProperty("liveMesFields"), rowB);
        Assert.Equal("NOT_READABLE", demandB.GetProperty("externalReadabilityState").GetString());
        Assert.Equal(
            ["SUBLOT_MULTIPLE_WORK_TYPES"],
            demandB.GetProperty("readabilityBlockers").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        AssertSingleMembershipCondition(seriesB, demandBId, workTypeA, workTypeB);
        AssertAssignedRows(
            seriesB,
            receipt.PollTraceId,
            receipt.ProjectionCommitId!,
            (1, rowB));

        AssertTraceAssignments(
            await ReadTraceAsync(client, receipt.PollTraceId),
            receipt,
            (0, rowA1, seriesAId, demandAId),
            (1, rowB, seriesBId, demandBId),
            (2, rowA2, seriesAId, demandAId));
    }

    private static JsonElement AssertSingleDuplicateCondition(JsonElement series, string demandId)
    {
        var condition = Assert.Single(series.GetProperty("currentConditions").EnumerateArray()
            .Where(value => value.GetProperty("code").GetString() == "DUPLICATE_TRANSPORT_DEMAND_KEY"));
        Assert.Equal("DUPLICATE_TRANSPORT_DEMAND_KEY", condition.GetProperty("code").GetString());
        Assert.Equal("OBSERVATION_CONFLICT", condition.GetProperty("category").GetString());
        Assert.Equal("ERROR", condition.GetProperty("severity").GetString());
        Assert.Equal($"DEMAND:{demandId}", condition.GetProperty("target").GetString());
        Assert.Equal("RAW_OBSERVATION_SET", condition.GetProperty("subjectKind").GetString());
        Assert.Equal(demandId, condition.GetProperty("demandId").GetString());
        Assert.Equal(
            "EXACTLY_ONE_RAW_OBSERVATION_PER_TRANSPORT_DEMAND_KEY",
            condition.GetProperty("expectedRule").GetString());
        return condition;
    }

    private static JsonElement AssertSingleMembershipCondition(
        JsonElement series,
        string demandId,
        params string[] expectedWorkTypes)
    {
        var condition = Assert.Single(series.GetProperty("currentConditions").EnumerateArray()
            .Where(value => value.GetProperty("code").GetString() == "SUBLOT_MULTIPLE_WORK_TYPES"));
        Assert.Equal("OBSERVATION_CONFLICT", condition.GetProperty("category").GetString());
        Assert.Equal("ERROR", condition.GetProperty("severity").GetString());
        Assert.Equal($"DEMAND:{demandId}", condition.GetProperty("target").GetString());
        Assert.Equal("WORK_TYPE_MEMBERSHIP", condition.GetProperty("subjectKind").GetString());
        Assert.Equal(demandId, condition.GetProperty("demandId").GetString());
        Assert.Equal(
            "EXACTLY_ONE_WORK_TYPE_PER_SUBLOT",
            condition.GetProperty("expectedRule").GetString());
        AssertMembershipEvidence(condition.GetProperty("observedValue"), expectedWorkTypes);
        return condition;
    }

    private static JsonElement AssertSingleMembershipPeriod(
        JsonElement series,
        string periodId,
        string startReason)
    {
        var period = Assert.Single(series.GetProperty("errorPeriods").EnumerateArray()
            .Where(value => value.GetProperty("code").GetString() == "SUBLOT_MULTIPLE_WORK_TYPES"));
        Assert.Equal(periodId, period.GetProperty("periodId").GetString());
        Assert.Equal("OBSERVATION_CONFLICT", period.GetProperty("category").GetString());
        Assert.Equal("WORK_TYPE_MEMBERSHIP", period.GetProperty("subjectKind").GetString());
        Assert.Equal(startReason, period.GetProperty("startReason").GetString());
        return period;
    }

    private static void AssertMembershipEvidence(JsonElement observedValue, params string[] expectedWorkTypes)
    {
        using var document = JsonDocument.Parse(observedValue.GetString()!);
        Assert.Equal(
            expectedWorkTypes.Order(StringComparer.Ordinal),
            document.RootElement.EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
    }

    private static void AssertMembershipEvidenceLink(
        JsonElement evidence,
        string evidenceKind,
        DateTimeOffset observedAt,
        string pollTraceId,
        string projectionCommitId,
        string demandId,
        params string[] expectedWorkTypes)
    {
        Assert.Equal(evidenceKind, evidence.GetProperty("evidenceKind").GetString());
        Assert.Equal(observedAt, evidence.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(pollTraceId, evidence.GetProperty("pollTraceId").GetString());
        Assert.Equal(projectionCommitId, evidence.GetProperty("projectionCommitId").GetString());
        Assert.Equal(demandId, evidence.GetProperty("demandId").GetString());
        Assert.Equal("EXACTLY_ONE_WORK_TYPE_PER_SUBLOT", evidence.GetProperty("expectedRule").GetString());
        AssertMembershipEvidence(evidence.GetProperty("observedValue"), expectedWorkTypes);
    }

    private static JsonElement AssertSingleDuplicatePeriod(
        JsonElement series,
        string periodId,
        string startReason)
    {
        var period = Assert.Single(series.GetProperty("errorPeriods").EnumerateArray()
            .Where(value => value.GetProperty("code").GetString() == "DUPLICATE_TRANSPORT_DEMAND_KEY"));
        Assert.Equal(periodId, period.GetProperty("periodId").GetString());
        Assert.Equal("OBSERVATION_CONFLICT", period.GetProperty("category").GetString());
        Assert.Equal("RAW_OBSERVATION_SET", period.GetProperty("subjectKind").GetString());
        Assert.Equal(startReason, period.GetProperty("startReason").GetString());
        return period;
    }

    private static void AssertCanonicalDuplicateEvidence(
        JsonElement observedValue,
        params MesTaskUnionObservation[] expected)
    {
        using var document = JsonDocument.Parse(observedValue.GetString()!);
        var actualRows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(expected.Length, actualRows.Length);
        Assert.Equal(
            expected.Select(ObservationEvidenceKey).Order(StringComparer.Ordinal),
            actualRows.Select(ObservationEvidenceKey).Order(StringComparer.Ordinal));
    }

    private static string ObservationEvidenceKey(MesTaskUnionObservation observation) =>
        JsonSerializer.Serialize(new object?[]
        {
            observation.WorkType,
            observation.Sublot,
            observation.Area,
            observation.Eqp,
            observation.Step,
            observation.MesSourceDate?.ToUniversalTime(),
            observation.Package,
        });

    private static string ObservationEvidenceKey(JsonElement observation) =>
        JsonSerializer.Serialize(new object?[]
        {
            observation.GetProperty("workType").GetString(),
            observation.GetProperty("sublot").GetString(),
            observation.GetProperty("area").GetString(),
            observation.GetProperty("eqp").GetString(),
            observation.GetProperty("step").GetString(),
            observation.GetProperty("mesSourceDate").ValueKind == JsonValueKind.Null
                ? null
                : observation.GetProperty("mesSourceDate").GetDateTimeOffset().ToUniversalTime(),
            observation.GetProperty("package").GetString(),
        });

    private static void AssertCurrentDemandAdvance(
        JsonElement series,
        RoundCommitReceipt receipt,
        DateTimeOffset completedAt)
    {
        Assert.Equal(receipt.ProjectionCommitId, series.GetProperty("latestProjectionCommitId").GetString());
        var demand = series.GetProperty("currentDemand");
        Assert.Equal(receipt.ProjectionCommitId, demand.GetProperty("latestProjectionCommitId").GetString());
        Assert.Equal(completedAt, demand.GetProperty("demandLastSeenAt").GetDateTimeOffset());
    }

    private static void AssertRoundRows(
        JsonElement series,
        string pollTraceId,
        string projectionCommitId,
        params MesTaskUnionObservation[] expected)
    {
        var actual = series.GetProperty("rawObservations").EnumerateArray()
            .Where(value => value.GetProperty("pollTraceId").GetString() == pollTraceId)
            .OrderBy(value => value.GetProperty("ordinal").GetInt32())
            .ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(index, actual[index].GetProperty("ordinal").GetInt32());
            Assert.Equal(projectionCommitId, actual[index].GetProperty("projectionCommitId").GetString());
            Assert.Equal("ASSIGNED", actual[index].GetProperty("assignment").GetString());
            AssertObservationFields(actual[index], expected[index]);
            Assert.Equal(expected[index].WorkType, actual[index].GetProperty("workType").GetString());
            Assert.Equal(expected[index].Sublot, actual[index].GetProperty("sublot").GetString());
        }
    }

    private static void AssertAssignedRows(
        JsonElement series,
        string pollTraceId,
        string projectionCommitId,
        params (int Ordinal, MesTaskUnionObservation Observation)[] expected)
    {
        var actual = series.GetProperty("rawObservations").EnumerateArray()
            .Where(value => value.GetProperty("pollTraceId").GetString() == pollTraceId)
            .ToArray();
        Assert.Equal(expected.Length, actual.Length);
        var seriesId = series.GetProperty("seriesId").GetString();
        var demandId = series.GetProperty("currentDemand").GetProperty("demandId").GetString();
        foreach (var expectedRow in expected)
        {
            var row = Assert.Single(actual.Where(value =>
                value.GetProperty("ordinal").GetInt32() == expectedRow.Ordinal));
            Assert.Equal(projectionCommitId, row.GetProperty("projectionCommitId").GetString());
            Assert.Equal("ASSIGNED", row.GetProperty("assignment").GetString());
            Assert.Equal(seriesId, row.GetProperty("seriesId").GetString());
            Assert.Equal(demandId, row.GetProperty("demandId").GetString());
            Assert.Equal(expectedRow.Observation.WorkType, row.GetProperty("workType").GetString());
            Assert.Equal(expectedRow.Observation.Sublot, row.GetProperty("sublot").GetString());
            AssertObservationFields(row, expectedRow.Observation);
        }
    }

    private static void AssertTraceAssignments(
        JsonElement trace,
        RoundCommitReceipt receipt,
        params (int Ordinal, MesTaskUnionObservation Observation, string SeriesId, string DemandId)[] expected)
    {
        Assert.Equal(receipt.PollTraceId, trace.GetProperty("pollTraceId").GetString());
        Assert.Equal("SUCCESS", trace.GetProperty("outcome").GetString());
        Assert.Equal(expected.Length, trace.GetProperty("rowCount").GetInt32());
        Assert.Equal(
            receipt.ProjectionCommitId,
            trace.GetProperty("projectionCommit").GetProperty("projectionCommitId").GetString());
        var actual = trace.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, actual.Length);
        foreach (var expectedRow in expected)
        {
            var row = Assert.Single(actual.Where(value =>
                value.GetProperty("ordinal").GetInt32() == expectedRow.Ordinal));
            Assert.Equal("ASSIGNED", row.GetProperty("assignment").GetString());
            Assert.Equal(expectedRow.SeriesId, row.GetProperty("seriesId").GetString());
            Assert.Equal(expectedRow.DemandId, row.GetProperty("demandId").GetString());
            Assert.Equal(receipt.ProjectionCommitId, row.GetProperty("projectionCommitId").GetString());
            Assert.Equal(expectedRow.Observation.WorkType, row.GetProperty("workType").GetString());
            Assert.Equal(expectedRow.Observation.Sublot, row.GetProperty("sublot").GetString());
            AssertObservationFields(row, expectedRow.Observation);
        }
    }

    private static void AssertObservationFields(JsonElement actual, MesTaskUnionObservation expected)
    {
        Assert.Equal(expected.Area, actual.GetProperty("area").GetString());
        Assert.Equal(expected.Eqp, actual.GetProperty("eqp").GetString());
        Assert.Equal(expected.Step, actual.GetProperty("step").GetString());
        Assert.Equal(expected.MesSourceDate, actual.GetProperty("mesSourceDate").GetDateTimeOffset());
        Assert.Equal(expected.Package, actual.GetProperty("package").GetString());
    }

    private static string?[] ReadEventIds(JsonElement series) =>
        series.GetProperty("events").EnumerateArray()
            .Select(value => value.GetProperty("eventId").GetString())
            .ToArray();

    private static string?[] ReadEvidenceIds(JsonElement period) =>
        period.GetProperty("evidence").EnumerateArray()
            .Select(value => value.GetProperty("evidenceId").GetString())
            .ToArray();

    private static string?[] ReadEvidenceKinds(JsonElement period) =>
        period.GetProperty("evidence").EnumerateArray()
            .Select(value => value.GetProperty("evidenceKind").GetString())
            .ToArray();

    private static MesTaskUnionObservation Observation(
        string workType,
        string sublot,
        string area,
        string eqp,
        string step,
        DateTimeOffset sourceDate,
        string package) =>
        new(workType, sublot, area, eqp, step, sourceDate, package);

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket04-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static async Task<JsonElement> ReadSeriesByKeyAsync(
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
