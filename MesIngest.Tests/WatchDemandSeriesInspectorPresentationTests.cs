using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchDemandSeriesInspectorPresentationTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-20T04:05:06Z");

    [Fact]
    public void Project_Preserves_the_frozen_snapshot_reference_identity_and_time()
    {
        var demand = Demand(
            generation: 1,
            demandId: "demand-1",
            predecessorDemandId: null,
            status: DemandSeriesLifecycleContract.Visible,
            createdAt: At,
            createdPollTraceId: "poll-create-1",
            createdProjectionCommitId: "commit-create-1",
            latestObservationAt: At,
            latestObservationPollTraceId: "poll-create-1",
            latestObservationProjectionCommitId: "commit-create-1");
        var detail = Detail(
            [demand],
            [Event(1, "TRANSPORT_DEMAND_CREATED", "DEMAND", demand.DemandId, "poll-create-1", "commit-create-1", "{\"generation\":1}", At)],
            observations: []);

        var presentation = WatchDemandSeriesInspectorPresentation.Project(detail, demand.DemandId);

        Assert.Equal("snapshot-e", presentation.FrozenSnapshot.SnapshotReference);
        Assert.Equal("commit-snapshot", presentation.FrozenSnapshot.ProjectionCommitId);
        Assert.Equal(40, presentation.FrozenSnapshot.ProjectionSequence);
        Assert.Equal(At, presentation.FrozenSnapshot.ProjectionCommittedAt);
        Assert.Equal("poll-snapshot", presentation.FrozenSnapshot.PollTraceId);
    }

    [Fact]
    public void Project_Production_creation_events_translate_reasons_and_select_reason_specific_facts()
    {
        var first = Demand(
            generation: 1,
            demandId: "demand-1",
            predecessorDemandId: null,
            status: DemandSeriesLifecycleContract.Gone,
            createdAt: At.AddHours(-4),
            createdPollTraceId: "poll-create-1",
            createdProjectionCommitId: "commit-create-1",
            latestObservationAt: At.AddHours(-3),
            latestObservationPollTraceId: "poll-seen-1",
            latestObservationProjectionCommitId: "commit-seen-1",
            goneConfirmedAt: At.AddHours(-2));
        var second = Demand(
            generation: 2,
            demandId: "demand-2",
            predecessorDemandId: first.DemandId,
            status: DemandSeriesLifecycleContract.Visible,
            createdAt: At,
            createdPollTraceId: "poll-create-2",
            createdProjectionCommitId: "commit-create-2",
            latestObservationAt: At,
            latestObservationPollTraceId: "poll-create-2",
            latestObservationProjectionCommitId: "commit-create-2");
        var detail = Detail(
            [first, second],
            [
                Event(1, "DEMAND_SERIES_STARTED", "SERIES", "series-e", "poll-create-1", "commit-create-1", "{}", At.AddHours(-4)),
                Event(2, "TRANSPORT_DEMAND_CREATED", "DEMAND", first.DemandId, "poll-create-1", "commit-create-1", "{\"demandId\":\"demand-1\",\"generation\":1}", At.AddHours(-4)),
                Event(3, "DEMAND_GONE", "DEMAND", first.DemandId, "poll-gone-1", "commit-gone-1", "{\"demandId\":\"demand-1\"}", At.AddHours(-2)),
                Event(4, "TRANSPORT_DEMAND_CREATED", "DEMAND", second.DemandId, "poll-create-2", "commit-create-2", "{\"demandId\":\"demand-2\",\"generation\":2,\"predecessorDemandId\":\"demand-1\",\"reason\":\"PREARCHIVE_REAPPEARANCE\"}", At),
            ],
            observations: []);

        var presentation = WatchDemandSeriesInspectorPresentation.Project(detail, second.DemandId);

        var firstPresentation = presentation.Generations[0];
        Assert.Equal("FIRST_OBSERVED", firstPresentation.FormationReason.RawCode);
        Assert.Equal("首次观察到", firstPresentation.FormationReason.ChineseLabel);
        Assert.True(firstPresentation.FormationReason.IsKnown);
        Assert.Equal(
            [WatchDemandFormationFactKind.FirstObservation],
            firstPresentation.FormationFacts.Select(fact => fact.Kind).ToArray());

        var focused = presentation.FocusedGeneration;
        Assert.Equal(second.DemandId, focused.DemandId);
        Assert.Equal(DemandSeriesLifecycleContract.Visible, focused.Status);
        Assert.Equal("PREARCHIVE_REAPPEARANCE", focused.FormationReason.RawCode);
        Assert.Equal("归档前消失后再现", focused.FormationReason.ChineseLabel);
        Assert.Equal(
            [
                WatchDemandFormationFactKind.PredecessorIdentity,
                WatchDemandFormationFactKind.PredecessorLastObservation,
                WatchDemandFormationFactKind.AuthoritativeGone,
                WatchDemandFormationFactKind.FirstObservation,
            ],
            focused.FormationFacts.Select(fact => fact.Kind).ToArray());
        Assert.DoesNotContain(
            focused.FormationFacts,
            fact => fact.Kind == WatchDemandFormationFactKind.Archive);
        Assert.All(focused.FormationFacts, fact =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fact.PollTraceId));
            Assert.False(string.IsNullOrWhiteSpace(fact.ProjectionCommitId));
        });
        Assert.Equal("poll-gone-1", focused.FormationFacts[2].PollTraceId);
        Assert.Equal("commit-gone-1", focused.FormationFacts[2].ProjectionCommitId);
    }

    [Fact]
    public void Project_Postarchive_reappearance_keeps_production_status_and_archive_evidence()
    {
        var first = Demand(
            generation: 1,
            demandId: "demand-1",
            predecessorDemandId: null,
            status: DemandSeriesLifecycleContract.Gone,
            createdAt: At.AddHours(-16),
            createdPollTraceId: "poll-create-1",
            createdProjectionCommitId: "commit-create-1",
            latestObservationAt: At.AddHours(-15),
            latestObservationPollTraceId: "poll-seen-1",
            latestObservationProjectionCommitId: "commit-seen-1",
            goneConfirmedAt: At.AddHours(-14));
        var second = Demand(
            generation: 2,
            demandId: "demand-2",
            predecessorDemandId: first.DemandId,
            status: DemandSeriesLifecycleContract.LongGoneButVisible,
            createdAt: At,
            createdPollTraceId: "poll-create-2",
            createdProjectionCommitId: "commit-create-2",
            latestObservationAt: At,
            latestObservationPollTraceId: "poll-create-2",
            latestObservationProjectionCommitId: "commit-create-2");
        var detail = Detail(
            [first, second],
            [
                Event(1, "DEMAND_SERIES_STARTED", "SERIES", "series-e", "poll-create-1", "commit-create-1", "{}", At.AddHours(-16)),
                Event(2, "TRANSPORT_DEMAND_CREATED", "DEMAND", first.DemandId, "poll-create-1", "commit-create-1", "{\"demandId\":\"demand-1\",\"generation\":1}", At.AddHours(-16)),
                Event(3, "DEMAND_GONE", "DEMAND", first.DemandId, "poll-gone-1", "commit-gone-1", "{\"demandId\":\"demand-1\"}", At.AddHours(-14)),
                Event(4, "GONE_TIMEOUT_ARCHIVED", "SERIES", "series-e", "poll-archive", "commit-archive", "{\"seriesId\":\"series-e\",\"demandId\":\"demand-1\"}", At.AddHours(-1)),
                Event(5, "TRANSPORT_DEMAND_CREATED", "DEMAND", second.DemandId, "poll-create-2", "commit-create-2", "{\"demandId\":\"demand-2\",\"generation\":2,\"predecessorDemandId\":\"demand-1\",\"reason\":\"POSTARCHIVE_REAPPEARANCE\"}", At),
            ],
            observations: [],
            lifecycle: DemandSeriesLifecycleContract.Archived,
            currentPresence: DemandSeriesLifecycleContract.LongGoneButVisible,
            archivedAt: At.AddHours(-1));

        var presentation = WatchDemandSeriesInspectorPresentation.Project(detail, second.DemandId);

        var focused = presentation.FocusedGeneration;
        Assert.Equal(DemandSeriesLifecycleContract.LongGoneButVisible, presentation.CurrentPresence);
        Assert.Equal(DemandSeriesLifecycleContract.LongGoneButVisible, focused.Status);
        Assert.Equal("POSTARCHIVE_REAPPEARANCE", focused.FormationReason.RawCode);
        Assert.Equal("归档后再次出现", focused.FormationReason.ChineseLabel);
        Assert.Equal(
            [
                WatchDemandFormationFactKind.PredecessorIdentity,
                WatchDemandFormationFactKind.PredecessorLastObservation,
                WatchDemandFormationFactKind.AuthoritativeGone,
                WatchDemandFormationFactKind.Archive,
                WatchDemandFormationFactKind.FirstObservation,
            ],
            focused.FormationFacts.Select(fact => fact.Kind).ToArray());
        var archive = focused.FormationFacts[3];
        Assert.Equal("poll-archive", archive.PollTraceId);
        Assert.Equal("commit-archive", archive.ProjectionCommitId);
        Assert.Equal(4, archive.SeriesSequence);
    }

    [Fact]
    public void Project_Known_reappearance_marks_missing_expected_facts_unavailable_without_placeholder_evidence()
    {
        var first = Demand(
            generation: 1,
            demandId: "demand-1",
            predecessorDemandId: null,
            status: DemandSeriesLifecycleContract.Gone,
            createdAt: At.AddHours(-16),
            createdPollTraceId: "poll-create-1",
            createdProjectionCommitId: "commit-create-1",
            latestObservationAt: At.AddHours(-15),
            latestObservationPollTraceId: "poll-seen-1",
            latestObservationProjectionCommitId: "commit-seen-1",
            goneConfirmedAt: At.AddHours(-14)) with
        {
            LatestObservationAt = null,
            LatestObservationPollTraceId = null,
            LatestObservationProjectionCommitId = null,
        };
        var second = Demand(
            generation: 2,
            demandId: "demand-2",
            predecessorDemandId: first.DemandId,
            status: DemandSeriesLifecycleContract.LongGoneButVisible,
            createdAt: At,
            createdPollTraceId: "poll-create-2",
            createdProjectionCommitId: "commit-create-2",
            latestObservationAt: At,
            latestObservationPollTraceId: "poll-create-2",
            latestObservationProjectionCommitId: "commit-create-2");
        var detail = Detail(
            [first, second],
            [
                Event(1, "TRANSPORT_DEMAND_CREATED", "DEMAND", first.DemandId, "poll-create-1", "commit-create-1", "{\"demandId\":\"demand-1\",\"generation\":1}", At.AddHours(-16)),
                Event(2, "TRANSPORT_DEMAND_CREATED", "DEMAND", second.DemandId, "poll-create-2", "commit-create-2", "{\"demandId\":\"demand-2\",\"generation\":2,\"predecessorDemandId\":\"demand-1\",\"reason\":\"POSTARCHIVE_REAPPEARANCE\"}", At),
            ],
            observations: [],
            lifecycle: DemandSeriesLifecycleContract.Archived,
            currentPresence: DemandSeriesLifecycleContract.LongGoneButVisible,
            archivedAt: At.AddHours(-1));

        var facts = WatchDemandSeriesInspectorPresentation
            .Project(detail, second.DemandId)
            .FocusedGeneration
            .FormationFacts;

        Assert.Equal(
            [
                WatchDemandFormationFactKind.PredecessorIdentity,
                WatchDemandFormationFactKind.PredecessorLastObservation,
                WatchDemandFormationFactKind.AuthoritativeGone,
                WatchDemandFormationFactKind.Archive,
                WatchDemandFormationFactKind.FirstObservation,
            ],
            facts.Select(fact => fact.Kind).ToArray());
        Assert.True(facts[0].IsAvailable);
        Assert.All(facts.Skip(1).Take(3), fact =>
        {
            Assert.False(fact.IsAvailable);
            Assert.Null(fact.OccurredAt);
            Assert.Null(fact.PollTraceId);
            Assert.Null(fact.ProjectionCommitId);
            Assert.Null(fact.SeriesSequence);
        });
        Assert.True(facts[4].IsAvailable);
        Assert.Equal("poll-create-2", facts[4].PollTraceId);
        Assert.Equal("commit-create-2", facts[4].ProjectionCommitId);
        Assert.Equal(2, facts[4].SeriesSequence);
    }

    [Theory]
    [InlineData("{\"reason\":\"FUTURE_REASON\"}", "FUTURE_REASON")]
    [InlineData("{\"generation\":2}", "（原因码缺失）")]
    [InlineData("{not-json", "（原因载荷无效）")]
    public void Project_Unknown_or_malformed_reason_uses_neutral_fallback_without_borrowed_narrative(
        string creationPayload,
        string expectedRawCode)
    {
        var first = Demand(
            generation: 1,
            demandId: "demand-1",
            predecessorDemandId: null,
            status: DemandSeriesLifecycleContract.Gone,
            createdAt: At.AddHours(-4),
            createdPollTraceId: "poll-create-1",
            createdProjectionCommitId: "commit-create-1",
            latestObservationAt: At.AddHours(-3),
            latestObservationPollTraceId: "poll-seen-1",
            latestObservationProjectionCommitId: "commit-seen-1",
            goneConfirmedAt: At.AddHours(-2));
        var second = Demand(
            generation: 2,
            demandId: "demand-2",
            predecessorDemandId: first.DemandId,
            status: DemandSeriesLifecycleContract.Visible,
            createdAt: At,
            createdPollTraceId: "poll-create-2",
            createdProjectionCommitId: "commit-create-2",
            latestObservationAt: At,
            latestObservationPollTraceId: "poll-create-2",
            latestObservationProjectionCommitId: "commit-create-2");
        var detail = Detail(
            [first, second],
            [
                Event(1, "TRANSPORT_DEMAND_CREATED", "DEMAND", first.DemandId, "poll-before", "commit-before", "{\"demandId\":\"demand-1\",\"generation\":1}", At.AddHours(-4)),
                Event(2, "DEMAND_GONE", "DEMAND", first.DemandId, "poll-gone-1", "commit-gone-1", "{\"demandId\":\"demand-1\"}", At.AddHours(-2)),
                Event(3, "TRANSPORT_DEMAND_CREATED", "DEMAND", second.DemandId, "poll-create-2", "commit-create-2", creationPayload, At),
            ],
            observations: []);

        var focused = WatchDemandSeriesInspectorPresentation
            .Project(detail, second.DemandId)
            .FocusedGeneration;

        Assert.Equal(expectedRawCode, focused.FormationReason.RawCode);
        Assert.Equal("形成原因暂无法确认", focused.FormationReason.ChineseLabel);
        Assert.False(focused.FormationReason.IsKnown);
        var onlyFact = Assert.Single(focused.FormationFacts);
        Assert.Equal(WatchDemandFormationFactKind.FirstObservation, onlyFact.Kind);
        Assert.Equal("创建事件", onlyFact.Label);
        Assert.DoesNotContain(
            focused.FormationFacts,
            fact => fact.Kind is WatchDemandFormationFactKind.PredecessorIdentity
                or WatchDemandFormationFactKind.AuthoritativeGone
                or WatchDemandFormationFactKind.Archive);
    }

    [Fact]
    public void Project_Unique_assigned_boundary_rows_enable_all_seven_scalar_mes_fields()
    {
        var first = Demand(
            generation: 1,
            demandId: "demand-1",
            predecessorDemandId: null,
            status: DemandSeriesLifecycleContract.Gone,
            createdAt: At.AddHours(-4),
            createdPollTraceId: "poll-before",
            createdProjectionCommitId: "commit-before",
            latestObservationAt: At.AddHours(-3),
            latestObservationPollTraceId: "poll-before",
            latestObservationProjectionCommitId: "commit-before",
            goneConfirmedAt: At.AddHours(-2));
        var second = Demand(
            generation: 2,
            demandId: "demand-2",
            predecessorDemandId: first.DemandId,
            status: DemandSeriesLifecycleContract.Visible,
            createdAt: At,
            createdPollTraceId: "poll-after",
            createdProjectionCommitId: "commit-after",
            latestObservationAt: At,
            latestObservationPollTraceId: "poll-after",
            latestObservationProjectionCommitId: "commit-after");
        var beforeDate = At.AddDays(-2);
        var afterDate = At.AddDays(-1);
        var detail = Detail(
            [first, second],
            [
                Event(1, "TRANSPORT_DEMAND_CREATED", "DEMAND", first.DemandId, "poll-before", "commit-before", "{\"demandId\":\"demand-1\",\"generation\":1}", At.AddHours(-4)),
                Event(2, "DEMAND_GONE", "DEMAND", first.DemandId, "poll-gone-1", "commit-gone-1", "{\"demandId\":\"demand-1\"}", At.AddHours(-2)),
                Event(3, "TRANSPORT_DEMAND_CREATED", "DEMAND", second.DemandId, "poll-after", "commit-after", "{\"reason\":\"PREARCHIVE_REAPPEARANCE\"}", At),
            ],
            observations:
            [
                Observation(1, "poll-before", "commit-before", first.DemandId, "WIRE_TO_GATE", "SL-E", "A1", "EQP-OLD", "STEP-1", beforeDate, "PKG-OLD"),
                Observation(1, "poll-after", "commit-after", second.DemandId, "WIRE_TO_GATE", "SL-E", "A1", "EQP-NEW", "STEP-1", afterDate, "PKG-NEW"),
            ]);

        var presentation = WatchDemandSeriesInspectorPresentation.Project(detail, second.DemandId);

        var boundary = presentation.FocusedGeneration.MesBoundary;
        Assert.Equal(WatchDemandMesBoundaryState.Unique, boundary.Before.State);
        Assert.Equal(WatchDemandMesBoundaryState.Unique, boundary.After.State);
        Assert.True(boundary.CanProjectScalarFields);
        Assert.Equal(
            ["TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "DATES / MesSourceDate", "PACKAGE"],
            boundary.ScalarFields.Select(field => field.FieldName).ToArray());
        Assert.Equal(
            ["EQP", "DATES / MesSourceDate", "PACKAGE"],
            boundary.ScalarFields.Where(field => field.IsChanged).Select(field => field.FieldName).ToArray());
        Assert.Equal("EQP-OLD", boundary.ScalarFields[3].BeforeValue);
        Assert.Equal("EQP-NEW", boundary.ScalarFields[3].AfterValue);
        Assert.Equal(beforeDate, boundary.ScalarFields[5].BeforeMesSourceDate);
        Assert.Equal(afterDate, boundary.ScalarFields[5].AfterMesSourceDate);
        Assert.Contains("观察证据", boundary.Explanation, StringComparison.Ordinal);
        Assert.Equal(
            "MES 字段差异只是边界两侧的观察证据，不是 TransportDemand/DemandId 形成原因。",
            boundary.Explanation);

        var firstBoundary = WatchDemandSeriesInspectorPresentation
            .Project(detail, first.DemandId)
            .FocusedGeneration
            .MesBoundary;
        Assert.Equal(WatchDemandMesBoundaryState.NotApplicable, firstBoundary.Before.State);
        Assert.Equal(WatchDemandMesBoundaryState.Unique, firstBoundary.After.State);
        Assert.True(firstBoundary.CanProjectScalarFields);
        Assert.All(firstBoundary.ScalarFields, field => Assert.Equal("不适用", field.BeforeValue));
    }

    [Fact]
    public void Project_Zero_and_multiple_assigned_boundaries_suppress_scalars_and_preserve_global_ordinal_evidence()
    {
        var first = Demand(
            generation: 1,
            demandId: "demand-1",
            predecessorDemandId: null,
            status: DemandSeriesLifecycleContract.Gone,
            createdAt: At.AddHours(-4),
            createdPollTraceId: "poll-create-1",
            createdProjectionCommitId: "commit-create-1",
            latestObservationAt: At.AddHours(-3),
            latestObservationPollTraceId: "poll-missing",
            latestObservationProjectionCommitId: "commit-missing",
            goneConfirmedAt: At.AddHours(-2));
        var second = Demand(
            generation: 2,
            demandId: "demand-2",
            predecessorDemandId: first.DemandId,
            status: DemandSeriesLifecycleContract.Visible,
            createdAt: At,
            createdPollTraceId: "poll-conflict",
            createdProjectionCommitId: "commit-conflict",
            latestObservationAt: At,
            latestObservationPollTraceId: "poll-conflict",
            latestObservationProjectionCommitId: "commit-conflict");
        var detail = Detail(
            [first, second],
            [
                Event(1, "TRANSPORT_DEMAND_CREATED", "DEMAND", first.DemandId, "poll-create-1", "commit-create-1", "{\"generation\":1}", At.AddHours(-4)),
                Event(2, "DEMAND_GONE", "DEMAND", first.DemandId, "poll-missing", "commit-missing", "{\"demandId\":\"demand-1\"}", At.AddHours(-2)),
                Event(3, "TRANSPORT_DEMAND_CREATED", "DEMAND", second.DemandId, "poll-conflict", "commit-conflict", "{\"reason\":\"PREARCHIVE_REAPPEARANCE\"}", At),
            ],
            observations:
            [
                Observation(3, "poll-conflict", "commit-conflict", second.DemandId, "WIRE_TO_GATE", "SL-E", "A1", "EQP-B", "STEP-1", At, "PKG-B"),
                Observation(1, "poll-conflict", "commit-conflict", second.DemandId, "WIRE_TO_GATE", "SL-E", "A1", "EQP-UNASSIGNED", "STEP-1", At, "PKG-U", MesObservationAssignment.Unassigned),
                Observation(2, "poll-conflict", "commit-conflict", second.DemandId, "WIRE_TO_GATE", "SL-E", "A1", "EQP-A", "STEP-1", At, "PKG-A"),
            ]);

        var boundary = WatchDemandSeriesInspectorPresentation
            .Project(detail, second.DemandId)
            .FocusedGeneration
            .MesBoundary;

        Assert.Equal(WatchDemandMesBoundaryState.Missing, boundary.Before.State);
        Assert.Empty(boundary.Before.ObservationGroups);
        Assert.Equal(WatchDemandMesBoundaryState.Conflict, boundary.After.State);
        Assert.False(boundary.CanProjectScalarFields);
        Assert.Empty(boundary.ScalarFields);
        Assert.Equal(
            [MesObservationAssignment.Assigned, MesObservationAssignment.Unassigned],
            boundary.After.ObservationGroups.Select(group => group.Assignment).ToArray());
        Assert.Equal(
            [2, 3],
            boundary.After.ObservationGroups[0].Rows.Select(row => row.Ordinal).ToArray());
        Assert.Equal(
            ["EQP-A", "EQP-B"],
            boundary.After.ObservationGroups[0].Rows.Select(row => row.Eqp).ToArray());
        var unassigned = Assert.Single(boundary.After.ObservationGroups[1].Rows);
        Assert.Equal(1, unassigned.Ordinal);
        Assert.Null(unassigned.SeriesId);
        Assert.Null(unassigned.DemandId);
        Assert.Equal(
            [1, 2, 3],
            boundary.After.RawRowsInOrdinalOrder.Select(row => row.Ordinal).ToArray());
        Assert.All(boundary.After.RawRowsInOrdinalOrder, row =>
        {
            Assert.Equal("poll-conflict", row.PollTraceId);
            Assert.Equal("commit-conflict", row.ProjectionCommitId);
        });
    }

    [Fact]
    public void Project_Events_remain_real_immutable_sequence_and_support_local_demand_filtering()
    {
        var first = Demand(
            generation: 1,
            demandId: "demand-1",
            predecessorDemandId: null,
            status: DemandSeriesLifecycleContract.Gone,
            createdAt: At.AddHours(-4),
            createdPollTraceId: "poll-create-1",
            createdProjectionCommitId: "commit-create-1",
            latestObservationAt: At.AddHours(-3),
            latestObservationPollTraceId: "poll-seen-1",
            latestObservationProjectionCommitId: "commit-seen-1",
            goneConfirmedAt: At.AddHours(-2));
        var second = Demand(
            generation: 2,
            demandId: "demand-2",
            predecessorDemandId: first.DemandId,
            status: DemandSeriesLifecycleContract.LongGoneButVisible,
            createdAt: At,
            createdPollTraceId: "poll-create-2",
            createdProjectionCommitId: "commit-create-2",
            latestObservationAt: At,
            latestObservationPollTraceId: "poll-create-2",
            latestObservationProjectionCommitId: "commit-create-2");
        var events = new[]
        {
            Event(5, "SERIES_ERROR_PERIOD_STARTED", "ARCHIVED_SERIES_VISIBILITY", second.DemandId, "poll-create-2", "commit-create-2", "{\"demandId\":\"demand-2\"}", At),
            Event(2, "TRANSPORT_DEMAND_CREATED", "DEMAND", first.DemandId, "poll-create-1", "commit-create-1", "{\"demandId\":\"demand-1\",\"generation\":1}", At.AddHours(-4)),
            Event(4, "TRANSPORT_DEMAND_CREATED", "DEMAND", second.DemandId, "poll-create-2", "commit-create-2", "{\"demandId\":\"demand-2\",\"predecessorDemandId\":\"demand-1\",\"reason\":\"POSTARCHIVE_REAPPEARANCE\"}", At),
            Event(1, "DEMAND_SERIES_STARTED", "SERIES", "series-e", "poll-create-1", "commit-create-1", "{\"seriesId\":\"series-e\"}", At.AddHours(-4)),
            Event(3, "GONE_TIMEOUT_ARCHIVED", "SERIES", "series-e", "poll-archive", "commit-archive", "{\"seriesId\":\"series-e\",\"demandId\":\"demand-1\"}", At.AddHours(-1)),
        };
        var detail = Detail(
            [first, second],
            events,
            observations: [],
            lifecycle: DemandSeriesLifecycleContract.Archived,
            currentPresence: DemandSeriesLifecycleContract.LongGoneButVisible,
            archivedAt: At.AddHours(-1));

        var presentation = WatchDemandSeriesInspectorPresentation.Project(detail, second.DemandId);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], presentation.Events.Select(item => item.SeriesSequence).ToArray());
        Assert.Equal(
            ["DEMAND_SERIES_STARTED", "TRANSPORT_DEMAND_CREATED", "GONE_TIMEOUT_ARCHIVED", "TRANSPORT_DEMAND_CREATED", "SERIES_ERROR_PERIOD_STARTED"],
            presentation.Events.Select(item => item.EventType).ToArray());
        Assert.DoesNotContain(
            presentation.Events,
            item => item.EventType is "DEMAND_REAPPEARED" or "SNAPSHOT_COMMITTED");
        Assert.Equal(
            [4L, 5L],
            presentation.EventsForDemand(second.DemandId).Select(item => item.SeriesSequence).ToArray());
        Assert.Equal(
            [2L, 3L, 4L],
            presentation.EventsForDemand(first.DemandId).Select(item => item.SeriesSequence).ToArray());
        Assert.Equal(
            [1L, 2L, 3L, 4L, 5L],
            presentation.EventsForDemand(demandId: null).Select(item => item.SeriesSequence).ToArray());
        Assert.Equal(events[2].PayloadJson, presentation.Events[3].PayloadJson);
        Assert.Equal("poll-create-2", presentation.Events[3].PollTraceId);
        Assert.Equal("commit-create-2", presentation.Events[3].ProjectionCommitId);
    }

    private static DemandSeriesDetailSnapshot Detail(
        IReadOnlyList<TransportDemandSnapshot> demands,
        IReadOnlyList<DemandSeriesEventSnapshot> events,
        IReadOnlyList<DemandRawObservationSnapshot> observations,
        string lifecycle = DemandSeriesLifecycleContract.Tracking,
        string currentPresence = DemandSeriesLifecycleContract.Visible,
        DateTimeOffset? archivedAt = null)
    {
        var current = demands.OrderBy(demand => demand.Generation).Last();
        var identity = new DemandSeriesSnapshotIdentity(
            "commit-snapshot",
            40,
            At,
            "poll-snapshot");
        return new DemandSeriesDetailSnapshot(
            identity,
            "snapshot-e",
            new DemandSeriesSnapshot(
                "series-e",
                "WIRE_TO_GATE",
                "SL-E",
                lifecycle,
                currentPresence,
                demands.Min(demand => demand.CreatedAt),
                demands[0].CreatedPollTraceId,
                demands[0].CreatedProjectionCommitId,
                identity.ProjectionCommitId,
                current,
                demands,
                observations,
                events,
                CurrentConditions: [],
                ErrorPeriods: [],
                archivedAt,
                LastSeriesSequence: events.Max(seriesEvent => seriesEvent.SeriesSequence)));
    }

    private static TransportDemandSnapshot Demand(
        int generation,
        string demandId,
        string? predecessorDemandId,
        string status,
        DateTimeOffset createdAt,
        string createdPollTraceId,
        string createdProjectionCommitId,
        DateTimeOffset latestObservationAt,
        string latestObservationPollTraceId,
        string latestObservationProjectionCommitId,
        DateTimeOffset? goneConfirmedAt = null) => new(
        demandId,
        "series-e",
        generation,
        predecessorDemandId,
        status,
        createdAt,
        latestObservationAt,
        goneConfirmedAt,
        createdPollTraceId,
        createdProjectionCommitId,
        latestObservationProjectionCommitId,
        LiveMesFields: null,
        ExternalReadabilityState: ExternalReadabilityStates.NotReadable,
        ReadabilityBlockers: [],
        latestObservationPollTraceId,
        latestObservationProjectionCommitId,
        latestObservationAt);

    private static DemandSeriesEventSnapshot Event(
        long sequence,
        string eventType,
        string subjectKind,
        string subjectId,
        string pollTraceId,
        string projectionCommitId,
        string payloadJson,
        DateTimeOffset occurredAt) => new(
        $"event-{sequence}",
        "series-e",
        sequence,
        eventType,
        occurredAt,
        subjectKind,
        subjectId,
        pollTraceId,
        projectionCommitId,
        PayloadVersion: 1,
        payloadJson);

    private static DemandRawObservationSnapshot Observation(
        int ordinal,
        string pollTraceId,
        string projectionCommitId,
        string demandId,
        string workType,
        string sublot,
        string area,
        string eqp,
        string step,
        DateTimeOffset mesSourceDate,
        string package,
        MesObservationAssignment assignment = MesObservationAssignment.Assigned) => new(
        ordinal,
        pollTraceId,
        projectionCommitId,
        assignment,
        assignment == MesObservationAssignment.Assigned ? "series-e" : null,
        assignment == MesObservationAssignment.Assigned ? demandId : null,
        workType,
        sublot,
        area,
        eqp,
        step,
        mesSourceDate,
        package,
        ObservedAt: At,
        MesSourceDateRaw: mesSourceDate.ToString("O"));
}
