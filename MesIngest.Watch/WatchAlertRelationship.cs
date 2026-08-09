using MesIngest.Core;

namespace MesIngest.Watch;

internal enum WatchAlertRelationshipScope
{
    ExactDemandId,
    ReappearTargets,
    BusinessKey,
    TaskType,
    Global,
}

internal sealed record WatchAlertRelationshipTarget(
    string Role,
    AlertDemandTarget Target);

internal sealed record WatchAlertRelationship(
    WatchAlertRelationshipScope Scope,
    string ScopeToken,
    string ScopeValue,
    string Heading,
    string Explanation,
    IReadOnlyList<WatchAlertRelationshipTarget> Targets,
    TransportDemandKey? BusinessKey)
{
    public static WatchAlertRelationship From(WatchAlertDto alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var detail = AlertDetailViewModel.From(alert);
        var reappearTargets = new List<WatchAlertRelationshipTarget>();
        if (detail.ReappearTargets.Previous is { } previous)
        {
            reappearTargets.Add(new("先前 GONE", previous));
        }

        if (detail.ReappearTargets.New is { } current)
        {
            reappearTargets.Add(new("当前再现", current));
        }

        if (reappearTargets.Count > 0)
        {
            return new WatchAlertRelationship(
                WatchAlertRelationshipScope.ReappearTargets,
                "EXACT DemandId",
                "先前 GONE / 当前再现",
                $"找到 {reappearTargets.Count} 个精确任务目标",
                "旧实例与新实例分别定位，不会互相替代。",
                reappearTargets,
                null);
        }

        if (!string.IsNullOrWhiteSpace(alert.DemandId))
        {
            return new WatchAlertRelationship(
                WatchAlertRelationshipScope.ExactDemandId,
                "EXACT DemandId",
                alert.DemandId.Trim(),
                "找到 1 个精确任务目标",
                "按告警携带的 DemandId 精确定位。",
                [new("关联任务", new AlertDemandTarget(alert.DemandId, AlertDemandTargetKind.Generic))],
                null);
        }

        if (detail.BusinessKeyTarget is { } businessKey)
        {
            return new WatchAlertRelationship(
                WatchAlertRelationshipScope.BusinessKey,
                "BUSINESS KEY",
                $"{businessKey.TaskType} + {businessKey.Sublot}",
                "可按业务键查找当前任务",
                "TASK_TYPE + SUBLOT 可能对应多个历史实例，不代表唯一精确目标。",
                [],
                businessKey);
        }

        if (!string.IsNullOrWhiteSpace(alert.TaskType))
        {
            return new WatchAlertRelationship(
                WatchAlertRelationshipScope.TaskType,
                "TASK TYPE",
                alert.TaskType.Trim(),
                "只有任务类型范围",
                "此告警没有 DemandId 或完整业务键，不能精确定位单条任务。",
                [],
                null);
        }

        return new WatchAlertRelationship(
            WatchAlertRelationshipScope.Global,
            "GLOBAL",
            "整轮 MesIngest",
            "全局接入告警",
            "此告警描述整轮接入状态，没有关联 TransportDemand。",
            [],
            null);
    }
}
