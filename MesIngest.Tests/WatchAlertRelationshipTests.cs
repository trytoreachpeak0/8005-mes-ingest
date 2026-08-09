using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchAlertRelationshipTests
{
    [Fact]
    public void Reappear_keeps_previous_and_new_exact_targets_separate()
    {
        var relationship = WatchAlertRelationship.From(Alert(
            "REAPPEAR_AFTER_GONE",
            "new-id",
            "DIE_TO_OVEN",
            "S1",
            """{"previousDemandId":"old-id","newDemandId":"new-id"}"""));

        Assert.Equal(WatchAlertRelationshipScope.ReappearTargets, relationship.Scope);
        Assert.Equal("先前 GONE / 当前再现", relationship.ScopeValue);
        Assert.Equal(["先前 GONE", "当前再现"], relationship.Targets.Select(target => target.Role));
        Assert.Equal(["old-id", "new-id"], relationship.Targets.Select(target => target.Target.DemandId));
        Assert.Null(relationship.BusinessKey);
    }

    [Fact]
    public void Distinguishes_exact_business_key_task_type_and_global_scopes()
    {
        var exact = WatchAlertRelationship.From(Alert("FIELD_DRIFT", "demand-1", "WIRE_TO_GATE", "S1"));
        var key = WatchAlertRelationship.From(Alert("DUPLICATE_RECONCILE_KEY", null, "WIRE_TO_GATE", "S1"));
        var taskType = WatchAlertRelationship.From(Alert("PAUSED_ZERO_DROP", null, "WIRE_TO_GATE", null));
        var global = WatchAlertRelationship.From(Alert("POLL_FAILURE", null, null, null));

        Assert.Equal(WatchAlertRelationshipScope.ExactDemandId, exact.Scope);
        Assert.Equal("demand-1", exact.ScopeValue);
        Assert.Single(exact.Targets);
        Assert.Equal(WatchAlertRelationshipScope.BusinessKey, key.Scope);
        Assert.Equal("WIRE_TO_GATE + S1", key.ScopeValue);
        Assert.NotNull(key.BusinessKey);
        Assert.Equal(WatchAlertRelationshipScope.TaskType, taskType.Scope);
        Assert.Equal("WIRE_TO_GATE", taskType.ScopeValue);
        Assert.Empty(taskType.Targets);
        Assert.Equal(WatchAlertRelationshipScope.Global, global.Scope);
        Assert.Equal("整轮 MesIngest", global.ScopeValue);
        Assert.Empty(global.Targets);
    }

    private static WatchAlertDto Alert(
        string code,
        string? demandId,
        string? taskType,
        string? sublot,
        string? details = null) => new(
            "alert-1",
            code,
            code == "REAPPEAR_AFTER_GONE" ? "WARNING" : "ERROR",
            taskType,
            sublot,
            demandId,
            "message",
            details,
            DateTimeOffset.Parse("2026-08-08T01:00:00Z"),
            DateTimeOffset.Parse("2026-08-08T01:01:00Z"),
            1,
            true,
            null,
            DateTimeOffset.Parse("2026-08-08T01:00:00Z"));
}
