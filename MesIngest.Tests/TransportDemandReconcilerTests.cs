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
