using MesIngest.Core;

namespace MesIngest.Tests;

public class TransportDemandReconcilerTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8));

    private static readonly DateTimeOffset Now =
        new(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Empty_prior_and_successful_snapshot_creates_visible_demand_with_frozen_fields()
    {
        var row = Row(
            taskType: "DIE_TO_WIRE_STAGING",
            sublot: "Q26079458-1",
            area: "N09-01",
            eqp: "2ZPB76",
            step: "焊线",
            dates: new DateTimeOffset(2026, 8, 1, 13, 55, 40, TimeSpan.FromHours(8)),
            package: "TO-247APlus-4L");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("d1"));
        var result = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success([row]),
            Now,
            Baseline);

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal("d1", demand.DemandId);
        Assert.Equal(DemandStatus.Visible, demand.Status);
        Assert.Equal("DIE_TO_WIRE_STAGING", demand.TaskType);
        Assert.Equal("Q26079458-1", demand.Sublot);
        Assert.Equal("N09-01", demand.Area);
        Assert.Equal("2ZPB76", demand.Eqp);
        Assert.Equal("焊线", demand.Step);
        Assert.Equal(row.Dates, demand.Dates);
        Assert.Equal("TO-247APlus-4L", demand.Package);
        Assert.Equal(Now, demand.MesLastSeenAt);
        Assert.Equal(0, demand.DisappearCount);
        Assert.Equal(Now, demand.CreatedAt);
        Assert.Null(demand.GoneAt);
    }

    [Fact]
    public void Rows_before_go_live_baseline_are_not_created_as_visible()
    {
        var before = Row(
            "DIE_TO_OVEN",
            "Q100-1",
            "N01-01",
            "EQ1",
            "烘箱",
            new DateTimeOffset(2026, 7, 31, 23, 59, 59, TimeSpan.FromHours(8)),
            "PKG");
        var onBaseline = Row(
            "WIRE_TO_GATE",
            "Q100-2",
            "N02-02",
            "EQ2",
            "关卡",
            Baseline,
            "PKG2");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("a", "b"));
        var result = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success([before, onBaseline]),
            Now,
            Baseline);

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal("Q100-2", demand.Sublot);
        Assert.Equal("WIRE_TO_GATE", demand.TaskType);
    }

    [Fact]
    public void All_six_task_types_are_projected_with_same_rules()
    {
        string[] types =
        [
            "DIE_TO_WIRE_STAGING",
            "DIE_TO_OVEN",
            "WIRE_TO_GATE",
            "WIRE_TO_OPTICAL",
            "STAGING_TO_WIRE",
            "WIRE_TO_NITROGEN",
        ];

        var rows = types.Select((t, i) => Row(
            t,
            $"Q{i}-1",
            "N01-01",
            "EQ",
            "STEP",
            Baseline.AddHours(i + 1),
            $"PKG-{t}")).ToList();

        var ids = types.Select((_, i) => $"id{i}").ToArray();
        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator(ids));
        var result = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success(rows),
            Now,
            Baseline);

        Assert.Equal(6, result.State.Demands.Count);
        foreach (var type in types)
        {
            Assert.Contains(result.State.Demands, d =>
                d.TaskType == type
                && d.Status == DemandStatus.Visible
                && d.Package == $"PKG-{type}");
        }
    }

    [Fact]
    public void Package_is_frozen_even_when_unmatched_in_capacity_table()
    {
        var row = Row(
            "WIRE_TO_NITROGEN",
            "Q999-1",
            "N03-03",
            "EQ9",
            "焊线",
            Baseline.AddDays(1),
            "UNKNOWN-PACKAGE-XYZ");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("p1"));
        var result = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success([row]),
            Now,
            Baseline);

        Assert.Equal("UNKNOWN-PACKAGE-XYZ", Assert.Single(result.State.Demands).Package);
    }

    [Fact]
    public void Second_successful_snapshot_does_not_overwrite_frozen_fields_for_still_visible_key()
    {
        var first = Row(
            "DIE_TO_WIRE_STAGING",
            "Q26079458-1",
            "N09-01",
            "EQP-ORIGINAL",
            "焊线",
            Baseline.AddHours(1),
            "PKG-ORIGINAL");
        var drifted = Row(
            "DIE_TO_WIRE_STAGING",
            "Q26079458-1",
            "N99-99",
            "EQP-DRIFTED",
            "焊线2",
            Baseline.AddHours(2),
            "PKG-DRIFTED");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("d1", "d2"));
        var afterFirst = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success([first]),
            Now,
            Baseline);
        var afterSecond = reconciler.Reconcile(
            afterFirst.State,
            MesSnapshotOutcome.Success([drifted]),
            Now.AddMinutes(1),
            Baseline);

        var demand = Assert.Single(afterSecond.State.Demands);
        Assert.Equal("d1", demand.DemandId);
        Assert.Equal("EQP-ORIGINAL", demand.Eqp);
        Assert.Equal("N09-01", demand.Area);
        Assert.Equal("焊线", demand.Step);
        Assert.Equal(first.Dates, demand.Dates);
        Assert.Equal("PKG-ORIGINAL", demand.Package);

        var alert = Assert.Single(afterSecond.Alerts);
        Assert.Equal("FIELD_DRIFT", alert.Code);
        Assert.Equal("DIE_TO_WIRE_STAGING", alert.TaskType);
        Assert.Equal("Q26079458-1", alert.Sublot);
        Assert.Equal("d1", alert.DemandId);
    }

    [Fact]
    public void Still_visible_unchanged_fields_do_not_raise_field_drift_alert()
    {
        var row = Row(
            "DIE_TO_WIRE_STAGING",
            "Q26079458-1",
            "N09-01",
            "EQP-ORIGINAL",
            "焊线",
            Baseline.AddHours(1),
            "PKG-ORIGINAL");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("d1"));
        var afterFirst = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success([row]),
            Now,
            Baseline);
        var afterSecond = reconciler.Reconcile(
            afterFirst.State,
            MesSnapshotOutcome.Success([row]),
            Now.AddMinutes(1),
            Baseline);

        Assert.Empty(afterSecond.Alerts);
        Assert.Equal(Now.AddMinutes(1), Assert.Single(afterSecond.State.Demands).MesLastSeenAt);
    }

    [Fact]
    public void Still_visible_key_in_successful_snapshot_refreshes_last_seen_and_resets_disappear_count()
    {
        var prior = new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "d1",
                TaskType = "DIE_TO_WIRE_STAGING",
                Sublot = "Q26079458-1",
                Area = "N09-01",
                Eqp = "EQP-ORIGINAL",
                Step = "焊线",
                Dates = Baseline.AddHours(1),
                Package = "PKG-ORIGINAL",
                Status = DemandStatus.Visible,
                MesLastSeenAt = Now,
                DisappearCount = 1,
            },
        ]);
        var stillThere = Row(
            "DIE_TO_WIRE_STAGING",
            "Q26079458-1",
            "N09-01",
            "EQP-ORIGINAL",
            "焊线",
            Baseline.AddHours(1),
            "PKG-ORIGINAL");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var later = Now.AddMinutes(5);
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([stillThere]),
            later,
            Baseline);

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal("d1", demand.DemandId);
        Assert.Equal(DemandStatus.Visible, demand.Status);
        Assert.Equal(later, demand.MesLastSeenAt);
        Assert.Equal(0, demand.DisappearCount);
    }

    [Fact]
    public void Still_visible_key_present_below_baseline_dates_still_refreshes_presence()
    {
        var prior = new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "d1",
                TaskType = "DIE_TO_WIRE_STAGING",
                Sublot = "Q26079458-1",
                Area = "N09-01",
                Eqp = "EQP-ORIGINAL",
                Step = "焊线",
                Dates = Baseline.AddHours(1),
                Package = "PKG-ORIGINAL",
                Status = DemandStatus.Visible,
                MesLastSeenAt = Now,
                DisappearCount = 1,
            },
        ]);
        // Presence uses reconcile key in the successful snapshot; baseline only gates new creates.
        var stillThereWithOldDates = Row(
            "DIE_TO_WIRE_STAGING",
            "Q26079458-1",
            "N09-01",
            "EQP-ORIGINAL",
            "焊线",
            Baseline.AddDays(-10),
            "PKG-ORIGINAL");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var later = Now.AddMinutes(5);
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([stillThereWithOldDates]),
            later,
            Baseline);

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal(DemandStatus.Visible, demand.Status);
        Assert.Equal(later, demand.MesLastSeenAt);
        Assert.Equal(0, demand.DisappearCount);
    }

    [Fact]
    public void Failed_or_incomplete_snapshot_does_not_increment_disappear_or_mark_gone()
    {
        var prior = new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "d1",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q100-1",
                Area = "N01-01",
                Eqp = "EQ1",
                Step = "烘箱",
                Dates = Baseline.AddHours(1),
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = Now,
                DisappearCount = 1,
            },
        ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());

        var afterFailure = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Failure(),
            Now.AddMinutes(1),
            Baseline);
        var failed = Assert.Single(afterFailure.State.Demands);
        Assert.Equal(DemandStatus.Visible, failed.Status);
        Assert.Equal(1, failed.DisappearCount);
        Assert.Equal(Now, failed.MesLastSeenAt);

        var afterIncomplete = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Incomplete(),
            Now.AddMinutes(2),
            Baseline);
        var incomplete = Assert.Single(afterIncomplete.State.Demands);
        Assert.Equal(DemandStatus.Visible, incomplete.Status);
        Assert.Equal(1, incomplete.DisappearCount);
        Assert.Equal(Now, incomplete.MesLastSeenAt);
    }

    [Fact]
    public void Successful_absence_increments_disappear_count_and_marks_gone_at_threshold()
    {
        var prior = new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "d1",
                TaskType = "WIRE_TO_GATE",
                Sublot = "Q200-1",
                Area = "N02-02",
                Eqp = "EQ2",
                Step = "关卡",
                Dates = Baseline.AddHours(1),
                Package = "PKG2",
                Status = DemandStatus.Visible,
                MesLastSeenAt = Now,
                DisappearCount = 0,
            },
        ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());

        var afterFirstMiss = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(1),
            Baseline,
            disappearThreshold: 2);
        var once = Assert.Single(afterFirstMiss.State.Demands);
        Assert.Equal(DemandStatus.Visible, once.Status);
        Assert.Equal(1, once.DisappearCount);
        Assert.Equal(Now, once.MesLastSeenAt);

        var afterSecondMiss = reconciler.Reconcile(
            afterFirstMiss.State,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(2),
            Baseline,
            disappearThreshold: 2);
        var gone = Assert.Single(afterSecondMiss.State.Demands);
        Assert.Equal(DemandStatus.Gone, gone.Status);
        Assert.Equal(2, gone.DisappearCount);
        Assert.Equal("d1", gone.DemandId);
        Assert.Equal(Now.AddMinutes(2), gone.GoneAt);
    }

    [Fact]
    public void Gone_transition_uses_configurable_disappear_threshold()
    {
        var prior = new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "d1",
                TaskType = "WIRE_TO_OPTICAL",
                Sublot = "Q300-1",
                Area = "N04-01",
                Eqp = "EQ3",
                Step = "外观",
                Dates = Baseline.AddHours(1),
                Package = "PKG3",
                Status = DemandStatus.Visible,
                MesLastSeenAt = Now,
                DisappearCount = 0,
            },
        ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(1),
            Baseline,
            disappearThreshold: 1);

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal(DemandStatus.Gone, demand.Status);
        Assert.Equal(1, demand.DisappearCount);
    }

    [Fact]
    public void Reappear_after_gone_allocates_new_demand_id_and_raises_alert_without_reviving_old()
    {
        var prior = new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "old-gone",
                TaskType = "STAGING_TO_WIRE",
                Sublot = "Q400-1",
                Area = "N05-01",
                Eqp = "EQ4",
                Step = "焊线",
                Dates = Baseline.AddHours(1),
                Package = "PKG-OLD",
                Status = DemandStatus.Gone,
                MesLastSeenAt = Now,
                DisappearCount = 2,
            },
        ]);
        var reappeared = Row(
            "STAGING_TO_WIRE",
            "Q400-1",
            "N05-01",
            "EQ4-NEW",
            "焊线",
            Baseline.AddHours(3),
            "PKG-NEW");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("new-id"));
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([reappeared]),
            Now.AddMinutes(10),
            Baseline);

        Assert.Equal(2, result.State.Demands.Count);
        var old = Assert.Single(result.State.Demands, d => d.DemandId == "old-gone");
        Assert.Equal(DemandStatus.Gone, old.Status);
        Assert.Equal("PKG-OLD", old.Package);

        var created = Assert.Single(result.State.Demands, d => d.DemandId == "new-id");
        Assert.Equal(DemandStatus.Visible, created.Status);
        Assert.Equal("PKG-NEW", created.Package);
        Assert.Equal(Now.AddMinutes(10), created.MesLastSeenAt);
        Assert.Equal(0, created.DisappearCount);

        var alert = Assert.Single(result.Alerts);
        Assert.Equal("REAPPEAR_AFTER_GONE", alert.Code);
        Assert.Equal("STAGING_TO_WIRE", alert.TaskType);
        Assert.Equal("Q400-1", alert.Sublot);
        Assert.Equal("new-id", alert.DemandId);
    }

    [Fact]
    public void Duplicate_reconcile_key_in_successful_snapshot_blocks_create_and_raises_alert()
    {
        var a = Row(
            "DIE_TO_OVEN",
            "Q-DUP-1",
            "N01-01",
            "EQ1",
            "烘箱",
            Baseline.AddHours(1),
            "PKG-A");
        var b = Row(
            "DIE_TO_OVEN",
            "Q-DUP-1",
            "N01-02",
            "EQ2",
            "烘箱",
            Baseline.AddHours(2),
            "PKG-B");
        var ok = Row(
            "WIRE_TO_GATE",
            "Q-OK-1",
            "N02-02",
            "EQ3",
            "关卡",
            Baseline.AddHours(1),
            "PKG-OK");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("ok-id", "should-not-use"));
        var result = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success([a, b, ok]),
            Now,
            Baseline);

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal("ok-id", demand.DemandId);
        Assert.Equal("Q-OK-1", demand.Sublot);

        var alert = Assert.Single(result.Alerts);
        Assert.Equal("DUPLICATE_RECONCILE_KEY", alert.Code);
        Assert.Equal("DIE_TO_OVEN", alert.TaskType);
        Assert.Equal("Q-DUP-1", alert.Sublot);
    }

    [Fact]
    public void Duplicate_reconcile_key_blocks_update_of_existing_visible_without_disappear()
    {
        var prior = new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "d1",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q-DUP-1",
                Area = "N01-01",
                Eqp = "EQ1",
                Step = "烘箱",
                Dates = Baseline.AddHours(1),
                Package = "PKG-A",
                Status = DemandStatus.Visible,
                MesLastSeenAt = Now,
                DisappearCount = 0,
            },
        ]);
        var a = Row(
            "DIE_TO_OVEN",
            "Q-DUP-1",
            "N01-01",
            "EQ1",
            "烘箱",
            Baseline.AddHours(1),
            "PKG-A");
        var b = Row(
            "DIE_TO_OVEN",
            "Q-DUP-1",
            "N99-99",
            "EQ9",
            "烘箱",
            Baseline.AddHours(3),
            "PKG-B");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([a, b]),
            Now.AddMinutes(5),
            Baseline);

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal("d1", demand.DemandId);
        Assert.Equal(DemandStatus.Visible, demand.Status);
        Assert.Equal(Now, demand.MesLastSeenAt);
        Assert.Equal(0, demand.DisappearCount);
        Assert.Equal("EQ1", demand.Eqp);
        Assert.Equal("PKG-A", demand.Package);

        var alert = Assert.Single(result.Alerts);
        Assert.Equal("DUPLICATE_RECONCILE_KEY", alert.Code);
        Assert.Equal("d1", alert.DemandId);
        Assert.DoesNotContain(result.Alerts, x => x.Code == "FIELD_DRIFT");
    }

    [Fact]
    public void Empty_area_still_creates_visible_demand_with_location_risk_signal()
    {
        var row = Row(
            "DIE_TO_OVEN",
            "Q-AREA-EMPTY",
            area: null,
            eqp: "EQ1",
            step: "烘箱",
            dates: Baseline.AddHours(1),
            package: "PKG");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("d1"));
        var result = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success([row]),
            Now,
            Baseline);

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal(DemandStatus.Visible, demand.Status);
        Assert.Null(demand.Area);
        Assert.True(demand.LocationRisk);
        Assert.Equal("AREA_EMPTY", demand.LocationRiskCode);
    }

    [Fact]
    public void Unparseable_area_still_creates_visible_demand_with_location_risk_signal()
    {
        var row = Row(
            "DIE_TO_OVEN",
            "Q-AREA-BAD",
            area: "N",
            eqp: "EQ1",
            step: "烘箱",
            dates: Baseline.AddHours(1),
            package: "PKG");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("d1"));
        var result = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success([row]),
            Now,
            Baseline);

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal("N", demand.Area);
        Assert.True(demand.LocationRisk);
        Assert.Equal("AREA_UNPARSEABLE", demand.LocationRiskCode);
    }

    [Fact]
    public void Parseable_area_creates_demand_without_location_risk()
    {
        var row = Row(
            "DIE_TO_WIRE_STAGING",
            "Q-AREA-OK",
            area: "N09-01",
            eqp: "EQ1",
            step: "焊线",
            dates: Baseline.AddHours(1),
            package: "PKG");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("d1"));
        var result = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success([row]),
            Now,
            Baseline);

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal("N09-01", demand.Area);
        Assert.False(demand.LocationRisk);
        Assert.Null(demand.LocationRiskCode);
    }

    [Fact]
    public void Zero_drop_learns_healthy_count_from_successful_round_then_pauses_on_zero()
    {
        var rows = Enumerable.Range(1, 10)
            .Select(i => Row(
                "DIE_TO_OVEN",
                $"Q-{i}",
                "N01-01",
                "EQ1",
                "烘箱",
                Baseline.AddHours(1),
                "PKG"))
            .ToList();
        var ids = Enumerable.Range(1, 10).Select(i => $"d{i}").ToArray();
        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator(ids));

        var afterHealthy = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success(rows),
            Now,
            Baseline,
            zeroDropEnterThreshold: 10);
        Assert.Equal(10, Assert.Single(afterHealthy.State.TaskTypePauses).LastHealthyNonZeroCount);
        Assert.False(Assert.Single(afterHealthy.State.TaskTypePauses).PausedZeroDrop);

        var afterZero = reconciler.Reconcile(
            afterHealthy.State,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(1),
            Baseline,
            disappearThreshold: 2,
            zeroDropEnterThreshold: 10);

        Assert.True(Assert.Single(afterZero.State.TaskTypePauses).PausedZeroDrop);
        Assert.All(afterZero.State.Demands, d => Assert.Equal(0, d.DisappearCount));
        Assert.Equal("PAUSED_ZERO_DROP", Assert.Single(afterZero.Alerts).Code);
    }

    [Fact]
    public void Zero_drop_enters_pause_when_snapshot_rows_are_all_before_go_live_baseline()
    {
        // Raw snapshot is non-empty, but every row is filtered out of the projection —
        // type count must be 0 so zero-drop still enters (same as truly empty projection).
        var priorDemands = Enumerable.Range(1, 10)
            .Select(i => new TransportDemand
            {
                DemandId = $"d{i}",
                TaskType = "DIE_TO_OVEN",
                Sublot = $"Q-{i}",
                Area = "N01-01",
                Eqp = "EQ1",
                Step = "烘箱",
                Dates = Baseline.AddHours(1),
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = Now,
                DisappearCount = 0,
            })
            .ToList();
        var prior = new ProjectionState(
            priorDemands,
            [
                new TaskTypePauseState(
                    TaskType: "DIE_TO_OVEN",
                    PausedZeroDrop: false,
                    LastHealthyNonZeroCount: 10,
                    RecoveryStreak: 0),
            ]);

        var preBaselineOnly = Enumerable.Range(1, 12)
            .Select(i => Row(
                "DIE_TO_OVEN",
                $"PRE-{i}",
                "N01-01",
                "EQ1",
                "烘箱",
                Baseline.AddDays(-1),
                "PKG"))
            .ToList();

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success(preBaselineOnly),
            Now.AddMinutes(1),
            Baseline,
            disappearThreshold: 2,
            zeroDropEnterThreshold: 10);

        Assert.Equal(10, result.State.Demands.Count);
        Assert.All(result.State.Demands, d =>
        {
            Assert.Equal(DemandStatus.Visible, d.Status);
            Assert.Equal(0, d.DisappearCount);
            Assert.StartsWith("d", d.DemandId);
        });

        var pause = Assert.Single(result.State.TaskTypePauses, p => p.TaskType == "DIE_TO_OVEN");
        Assert.True(pause.PausedZeroDrop);
        Assert.Equal(10, pause.LastHealthyNonZeroCount);
        Assert.Equal("PAUSED_ZERO_DROP", Assert.Single(result.Alerts).Code);
    }

    [Fact]
    public void Zero_drop_does_not_enter_pause_when_post_baseline_count_is_non_zero()
    {
        var prior = new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d1",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-1",
                    Area = "N01-01",
                    Eqp = "EQ1",
                    Step = "烘箱",
                    Dates = Baseline.AddHours(1),
                    Package = "PKG",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = Now,
                    DisappearCount = 0,
                },
            ],
            [
                new TaskTypePauseState(
                    TaskType: "DIE_TO_OVEN",
                    PausedZeroDrop: false,
                    LastHealthyNonZeroCount: 10,
                    RecoveryStreak: 0),
            ]);

        var postBaseline = Row(
            "DIE_TO_OVEN",
            "Q-1",
            "N01-01",
            "EQ1",
            "烘箱",
            Baseline.AddHours(2),
            "PKG");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([postBaseline]),
            Now.AddMinutes(1),
            Baseline,
            disappearThreshold: 2,
            zeroDropEnterThreshold: 10);

        var pause = Assert.Single(result.State.TaskTypePauses, p => p.TaskType == "DIE_TO_OVEN");
        Assert.False(pause.PausedZeroDrop);
        Assert.Equal(1, pause.LastHealthyNonZeroCount);
        Assert.DoesNotContain(result.Alerts, a => a.Code == "PAUSED_ZERO_DROP");
    }

    [Fact]
    public void Zero_drop_does_not_raise_healthy_count_from_pre_baseline_rows_or_duplicates()
    {
        var preBaselineDuplicates = Enumerable.Range(1, 12)
            .Select(_ => Row(
                "DIE_TO_OVEN",
                "SAME-KEY",
                "N01-01",
                "EQ1",
                "烘箱",
                Baseline.AddDays(-2),
                "PKG"))
            .ToList();

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var result = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success(preBaselineDuplicates),
            Now,
            Baseline,
            zeroDropEnterThreshold: 10);

        Assert.Empty(result.State.Demands);
        Assert.DoesNotContain(result.State.TaskTypePauses, p => p.LastHealthyNonZeroCount > 0);
        Assert.DoesNotContain(result.Alerts, a => a.Code == "PAUSED_ZERO_DROP");
    }

    [Fact]
    public void Zero_drop_enters_paused_and_does_not_increment_disappear_when_prior_healthy_count_at_threshold()
    {
        var priorDemands = Enumerable.Range(1, 10)
            .Select(i => new TransportDemand
            {
                DemandId = $"d{i}",
                TaskType = "DIE_TO_OVEN",
                Sublot = $"Q-{i}",
                Area = "N01-01",
                Eqp = "EQ1",
                Step = "烘箱",
                Dates = Baseline.AddHours(1),
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = Now,
                DisappearCount = 0,
            })
            .ToList();
        var prior = new ProjectionState(
            priorDemands,
            [
                new TaskTypePauseState(
                    TaskType: "DIE_TO_OVEN",
                    PausedZeroDrop: false,
                    LastHealthyNonZeroCount: 10,
                    RecoveryStreak: 0),
            ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(1),
            Baseline,
            disappearThreshold: 2,
            zeroDropEnterThreshold: 10);

        Assert.All(result.State.Demands, d =>
        {
            Assert.Equal(DemandStatus.Visible, d.Status);
            Assert.Equal(0, d.DisappearCount);
        });

        var pause = Assert.Single(result.State.TaskTypePauses, p => p.TaskType == "DIE_TO_OVEN");
        Assert.True(pause.PausedZeroDrop);
        Assert.Equal(10, pause.LastHealthyNonZeroCount);
        Assert.Equal(0, pause.RecoveryStreak);

        var alert = Assert.Single(result.Alerts);
        Assert.Equal("PAUSED_ZERO_DROP", alert.Code);
        Assert.Equal("DIE_TO_OVEN", alert.TaskType);
    }

    [Fact]
    public void Zero_drop_pause_isolates_to_one_task_type_while_others_still_disappear()
    {
        var prior = new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "paused-1",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-PAUSED",
                    Area = "N01-01",
                    Eqp = "EQ1",
                    Step = "烘箱",
                    Dates = Baseline.AddHours(1),
                    Package = "PKG",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = Now,
                    DisappearCount = 0,
                },
                new TransportDemand
                {
                    DemandId = "other-1",
                    TaskType = "WIRE_TO_GATE",
                    Sublot = "Q-OTHER",
                    Area = "N02-02",
                    Eqp = "EQ2",
                    Step = "关卡",
                    Dates = Baseline.AddHours(1),
                    Package = "PKG2",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = Now,
                    DisappearCount = 0,
                },
            ],
            [
                new TaskTypePauseState("DIE_TO_OVEN", PausedZeroDrop: true, LastHealthyNonZeroCount: 12, RecoveryStreak: 0),
                new TaskTypePauseState("WIRE_TO_GATE", PausedZeroDrop: false, LastHealthyNonZeroCount: 1, RecoveryStreak: 0),
            ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(1),
            Baseline,
            disappearThreshold: 2,
            zeroDropEnterThreshold: 10);

        var paused = Assert.Single(result.State.Demands, d => d.DemandId == "paused-1");
        Assert.Equal(DemandStatus.Visible, paused.Status);
        Assert.Equal(0, paused.DisappearCount);

        var other = Assert.Single(result.State.Demands, d => d.DemandId == "other-1");
        Assert.Equal(DemandStatus.Visible, other.Status);
        Assert.Equal(1, other.DisappearCount);

        Assert.DoesNotContain(result.Alerts, a => a.Code == "PAUSED_ZERO_DROP");
        Assert.True(Assert.Single(result.State.TaskTypePauses, p => p.TaskType == "DIE_TO_OVEN").PausedZeroDrop);
        Assert.False(Assert.Single(result.State.TaskTypePauses, p => p.TaskType == "WIRE_TO_GATE").PausedZeroDrop);
    }

    [Fact]
    public void Zero_drop_below_enter_threshold_still_increments_disappear()
    {
        var prior = new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d1",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-1",
                    Area = "N01-01",
                    Eqp = "EQ1",
                    Step = "烘箱",
                    Dates = Baseline.AddHours(1),
                    Package = "PKG",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = Now,
                    DisappearCount = 0,
                },
            ],
            [
                new TaskTypePauseState("DIE_TO_OVEN", PausedZeroDrop: false, LastHealthyNonZeroCount: 9, RecoveryStreak: 0),
            ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(1),
            Baseline,
            disappearThreshold: 2,
            zeroDropEnterThreshold: 10);

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal(DemandStatus.Visible, demand.Status);
        Assert.Equal(1, demand.DisappearCount);
        Assert.False(Assert.Single(result.State.TaskTypePauses).PausedZeroDrop);
        Assert.Empty(result.Alerts);
    }

    [Fact]
    public void Zero_drop_clears_after_two_consecutive_successful_non_zero_rounds()
    {
        var prior = new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d1",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-1",
                    Area = "N01-01",
                    Eqp = "EQ1",
                    Step = "烘箱",
                    Dates = Baseline.AddHours(1),
                    Package = "PKG",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = Now,
                    DisappearCount = 0,
                },
            ],
            [
                new TaskTypePauseState("DIE_TO_OVEN", PausedZeroDrop: true, LastHealthyNonZeroCount: 10, RecoveryStreak: 0),
            ]);
        var row = Row(
            "DIE_TO_OVEN",
            "Q-1",
            "N01-01",
            "EQ1",
            "烘箱",
            Baseline.AddHours(1),
            "PKG");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var afterFirst = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([row]),
            Now.AddMinutes(1),
            Baseline,
            zeroDropEnterThreshold: 10);
        var pause1 = Assert.Single(afterFirst.State.TaskTypePauses);
        Assert.True(pause1.PausedZeroDrop);
        Assert.Equal(1, pause1.RecoveryStreak);
        Assert.Equal(1, pause1.LastHealthyNonZeroCount);

        var afterSecond = reconciler.Reconcile(
            afterFirst.State,
            MesSnapshotOutcome.Success([row]),
            Now.AddMinutes(2),
            Baseline,
            zeroDropEnterThreshold: 10);
        var pause2 = Assert.Single(afterSecond.State.TaskTypePauses);
        Assert.False(pause2.PausedZeroDrop);
        Assert.Equal(0, pause2.RecoveryStreak);
        Assert.Equal(1, pause2.LastHealthyNonZeroCount);

        // After clear, a zero round below previous healthy threshold of 1 does not re-enter pause.
        var afterZero = reconciler.Reconcile(
            afterSecond.State,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(3),
            Baseline,
            disappearThreshold: 2,
            zeroDropEnterThreshold: 10);
        Assert.False(Assert.Single(afterZero.State.TaskTypePauses).PausedZeroDrop);
        Assert.Equal(1, Assert.Single(afterZero.State.Demands).DisappearCount);
    }

    [Fact]
    public void Zero_drop_recovery_streak_resets_when_a_zero_round_interrupts()
    {
        var prior = new ProjectionState(
            Array.Empty<TransportDemand>(),
            [
                new TaskTypePauseState("DIE_TO_OVEN", PausedZeroDrop: true, LastHealthyNonZeroCount: 11, RecoveryStreak: 1),
            ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var afterZero = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now,
            Baseline,
            zeroDropEnterThreshold: 10);
        var pause = Assert.Single(afterZero.State.TaskTypePauses);
        Assert.True(pause.PausedZeroDrop);
        Assert.Equal(0, pause.RecoveryStreak);
        Assert.Equal(11, pause.LastHealthyNonZeroCount);
    }

    [Fact]
    public void Restart_barrier_round_does_not_increment_disappear_or_mark_gone_for_absent_keys()
    {
        var prior = new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "d1",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q-1",
                Area = "N01-01",
                Eqp = "EQ1",
                Step = "烘箱",
                Dates = Baseline.AddHours(1),
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = Now,
                DisappearCount = 1,
            },
        ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(1),
            Baseline,
            disappearThreshold: 2,
            restartRecovery: RestartRecovery.BarrierRound());

        var demand = Assert.Single(result.State.Demands);
        Assert.Equal(DemandStatus.Visible, demand.Status);
        Assert.Equal(1, demand.DisappearCount);
        Assert.Equal(Now, demand.MesLastSeenAt);
    }

    [Fact]
    public void Restart_barrier_round_creates_new_visible_and_refreshes_seen_for_present_keys()
    {
        var prior = new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "existing",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q-1",
                Area = "N01-01",
                Eqp = "EQ1",
                Step = "烘箱",
                Dates = Baseline.AddHours(1),
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = Now,
                DisappearCount = 1,
            },
        ]);
        var existing = Row("DIE_TO_OVEN", "Q-1", "N01-01", "EQ1", "烘箱", Baseline.AddHours(1), "PKG");
        var created = Row("DIE_TO_OVEN", "Q-2", "N02-02", "EQ2", "烘箱", Baseline.AddHours(2), "PKG2");

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("new"));
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([existing, created]),
            Now.AddMinutes(1),
            Baseline,
            restartRecovery: RestartRecovery.BarrierRound());

        Assert.Equal(2, result.State.Demands.Count);
        var refreshed = Assert.Single(result.State.Demands, d => d.DemandId == "existing");
        Assert.Equal(Now.AddMinutes(1), refreshed.MesLastSeenAt);
        Assert.Equal(0, refreshed.DisappearCount);
        var neu = Assert.Single(result.State.Demands, d => d.DemandId == "new");
        Assert.Equal(DemandStatus.Visible, neu.Status);
        Assert.Equal("Q-2", neu.Sublot);
    }

    [Fact]
    public void Restart_barrier_round_zero_count_preserves_last_healthy_and_does_not_enter_pause()
    {
        var prior = new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d1",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-1",
                    Area = "N01-01",
                    Eqp = "EQ1",
                    Step = "烘箱",
                    Dates = Baseline.AddHours(1),
                    Package = "PKG",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = Now,
                    DisappearCount = 0,
                },
            ],
            [
                new TaskTypePauseState("DIE_TO_OVEN", PausedZeroDrop: false, LastHealthyNonZeroCount: 12, RecoveryStreak: 0),
            ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(1),
            Baseline,
            zeroDropEnterThreshold: 10,
            restartRecovery: RestartRecovery.BarrierRound());

        var pause = Assert.Single(result.State.TaskTypePauses);
        Assert.False(pause.PausedZeroDrop);
        Assert.Equal(12, pause.LastHealthyNonZeroCount);
        Assert.DoesNotContain(result.Alerts, a => a.Code == "PAUSED_ZERO_DROP");
        Assert.Equal(0, Assert.Single(result.State.Demands).DisappearCount);
    }

    [Fact]
    public void Restart_post_barrier_after_two_zero_rounds_enters_paused_zero_drop()
    {
        var prior = new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d1",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-1",
                    Area = "N01-01",
                    Eqp = "EQ1",
                    Step = "烘箱",
                    Dates = Baseline.AddHours(1),
                    Package = "PKG",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = Now,
                    DisappearCount = 0,
                },
            ],
            [
                new TaskTypePauseState("DIE_TO_OVEN", PausedZeroDrop: false, LastHealthyNonZeroCount: 12, RecoveryStreak: 0),
            ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var afterBarrier = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(1),
            Baseline,
            zeroDropEnterThreshold: 10,
            restartRecovery: RestartRecovery.BarrierRound());
        Assert.False(Assert.Single(afterBarrier.State.TaskTypePauses).PausedZeroDrop);

        var afterSecond = reconciler.Reconcile(
            afterBarrier.State,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(2),
            Baseline,
            disappearThreshold: 2,
            zeroDropEnterThreshold: 10,
            restartRecovery: RestartRecovery.PostBarrierRound(
                new Dictionary<string, int>(StringComparer.Ordinal) { ["DIE_TO_OVEN"] = 0 }));

        Assert.True(Assert.Single(afterSecond.State.TaskTypePauses).PausedZeroDrop);
        Assert.Equal(12, Assert.Single(afterSecond.State.TaskTypePauses).LastHealthyNonZeroCount);
        Assert.Equal("PAUSED_ZERO_DROP", Assert.Single(afterSecond.Alerts).Code);
        Assert.Equal(0, Assert.Single(afterSecond.State.Demands).DisappearCount);
    }

    [Fact]
    public void Restart_second_successful_round_resumes_disappear_counting()
    {
        var prior = new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "d1",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q-1",
                Area = "N01-01",
                Eqp = "EQ1",
                Step = "烘箱",
                Dates = Baseline.AddHours(1),
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = Now,
                DisappearCount = 1,
            },
        ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var afterBarrier = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(1),
            Baseline,
            disappearThreshold: 2,
            restartRecovery: RestartRecovery.BarrierRound());
        Assert.Equal(1, Assert.Single(afterBarrier.State.Demands).DisappearCount);

        var afterSecond = reconciler.Reconcile(
            afterBarrier.State,
            MesSnapshotOutcome.Success([]),
            Now.AddMinutes(2),
            Baseline,
            disappearThreshold: 2,
            restartRecovery: RestartRecovery.PostBarrierRound(
                new Dictionary<string, int>(StringComparer.Ordinal)));

        var demand = Assert.Single(afterSecond.State.Demands);
        Assert.Equal(DemandStatus.Gone, demand.Status);
        Assert.Equal(2, demand.DisappearCount);
    }

    [Fact]
    public void Restart_barrier_then_second_non_zero_clears_persisted_paused_zero_drop()
    {
        var row = Row("DIE_TO_OVEN", "Q-1", "N01-01", "EQ1", "烘箱", Baseline.AddHours(1), "PKG");
        var prior = new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d1",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-1",
                    Area = "N01-01",
                    Eqp = "EQ1",
                    Step = "烘箱",
                    Dates = Baseline.AddHours(1),
                    Package = "PKG",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = Now,
                    DisappearCount = 0,
                },
            ],
            [
                new TaskTypePauseState("DIE_TO_OVEN", PausedZeroDrop: true, LastHealthyNonZeroCount: 12, RecoveryStreak: 0),
            ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var afterBarrier = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([row]),
            Now.AddMinutes(1),
            Baseline,
            zeroDropEnterThreshold: 10,
            restartRecovery: RestartRecovery.BarrierRound());
        var pause1 = Assert.Single(afterBarrier.State.TaskTypePauses);
        Assert.True(pause1.PausedZeroDrop);
        Assert.Equal(1, pause1.RecoveryStreak);
        Assert.Equal(12, pause1.LastHealthyNonZeroCount);

        var afterSecond = reconciler.Reconcile(
            afterBarrier.State,
            MesSnapshotOutcome.Success([row]),
            Now.AddMinutes(2),
            Baseline,
            zeroDropEnterThreshold: 10,
            restartRecovery: RestartRecovery.PostBarrierRound(
                new Dictionary<string, int>(StringComparer.Ordinal) { ["DIE_TO_OVEN"] = 1 }));
        var pause2 = Assert.Single(afterSecond.State.TaskTypePauses);
        Assert.False(pause2.PausedZeroDrop);
        Assert.Equal(0, pause2.RecoveryStreak);
    }

    [Fact]
    public void Restart_post_barrier_preserves_persisted_healthy_baseline_when_barrier_count_is_lower()
    {
        var prior = new ProjectionState(
            Array.Empty<TransportDemand>(),
            [
                new TaskTypePauseState("DIE_TO_OVEN", PausedZeroDrop: false, LastHealthyNonZeroCount: 50, RecoveryStreak: 0),
            ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        // Barrier saw only 3; must not demote persisted healthy baseline of 50.
        var result = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now,
            Baseline,
            zeroDropEnterThreshold: 10,
            restartRecovery: RestartRecovery.PostBarrierRound(
                new Dictionary<string, int>(StringComparer.Ordinal) { ["DIE_TO_OVEN"] = 3 }));

        var pause = Assert.Single(result.State.TaskTypePauses);
        Assert.True(pause.PausedZeroDrop);
        Assert.Equal(50, pause.LastHealthyNonZeroCount);
    }

    [Fact]
    public void Restart_post_barrier_seeds_or_raises_healthy_baseline_from_barrier_count()
    {
        var prior = new ProjectionState(
            Array.Empty<TransportDemand>(),
            [
                new TaskTypePauseState("DIE_TO_OVEN", PausedZeroDrop: false, LastHealthyNonZeroCount: 5, RecoveryStreak: 0),
            ]);

        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator());
        var raised = reconciler.Reconcile(
            prior,
            MesSnapshotOutcome.Success([]),
            Now,
            Baseline,
            zeroDropEnterThreshold: 10,
            restartRecovery: RestartRecovery.PostBarrierRound(
                new Dictionary<string, int>(StringComparer.Ordinal) { ["DIE_TO_OVEN"] = 12 }));

        Assert.Equal(12, Assert.Single(raised.State.TaskTypePauses).LastHealthyNonZeroCount);
        Assert.True(Assert.Single(raised.State.TaskTypePauses).PausedZeroDrop);

        var seeded = reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success([]),
            Now,
            Baseline,
            zeroDropEnterThreshold: 10,
            restartRecovery: RestartRecovery.PostBarrierRound(
                new Dictionary<string, int>(StringComparer.Ordinal) { ["WIRE_TO_GATE"] = 4 }));

        var seededPause = Assert.Single(seeded.State.TaskTypePauses);
        Assert.Equal("WIRE_TO_GATE", seededPause.TaskType);
        Assert.Equal(4, seededPause.LastHealthyNonZeroCount);
        Assert.False(seededPause.PausedZeroDrop);
    }

    private static MesSnapshotRow Row(
        string taskType,
        string sublot,
        string? area,
        string? eqp,
        string? step,
        DateTimeOffset dates,
        string? package) =>
        new(taskType, sublot, area, eqp, step, dates, package);
}
