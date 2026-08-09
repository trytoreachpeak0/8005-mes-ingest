namespace MesIngest.Watch;

public sealed record WatchDemandDetailField(
    string Name,
    string Value,
    string Explanation);

public sealed record WatchDemandDetailGroup(
    string Title,
    IReadOnlyList<WatchDemandDetailField> Fields);

public sealed record WatchDemandDetails(
    WatchDemandDetailGroup MesInputGroup,
    WatchDemandDetailGroup LocalProjectionGroup)
{
    public string DemandId => FieldValue(LocalProjectionGroup, "DemandId");

    public string TaskType => FieldValue(MesInputGroup, "TASK_TYPE");

    public string Sublot => FieldValue(MesInputGroup, "SUBLOT");

    public IReadOnlyList<WatchDemandRelatedAlert> RelatedAlerts { get; init; } = [];

    public string RelatedAlertSummary => RelatedAlerts.Count == 0
        ? "当前任务没有相关 IngestAlert。"
        : $"找到 {RelatedAlerts.Count} 条相关 IngestAlert。";

    private static string FieldValue(WatchDemandDetailGroup group, string name) =>
        group.Fields.First(field => string.Equals(field.Name, name, StringComparison.Ordinal)).Value;

    public static WatchDemandDetails From(
        WatchDemandDto demand,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(demand);

        string Format(object? value) => WatchGridClipboard.FormatValue(value, timeZone);

        return new WatchDemandDetails(
            new WatchDemandDetailGroup(
                "MES 输入（创建时冻结）",
                [
                    new("TASK_TYPE", Format(demand.TaskType), "MES 任务类型；创建 TransportDemand 时冻结。"),
                    new("SUBLOT", Format(demand.Sublot), "MES 子批次；创建 TransportDemand 时冻结。"),
                    new("AREA", Format(demand.Area), "MES 当前区域原始值；创建 TransportDemand 时冻结。"),
                    new("EQP", Format(demand.Eqp), "MES 当前机台原始值；创建 TransportDemand 时冻结。"),
                    new("STEP", Format(demand.Step), "MES 下一工序；不是 DATES 所属的当前工序。"),
                    new("DATES", Format(demand.Dates), "MES 当前工序进入时间；创建 TransportDemand 时冻结。"),
                    new("PACKAGE", Format(demand.Package), "MES 包装规格原始值；创建 TransportDemand 时冻结。"),
                ]),
            new WatchDemandDetailGroup(
                "本地 TransportDemand 投影",
                [
                    new("DemandId", Format(demand.DemandId), "TransportDemand 本地稳定实例标识。"),
                    new("status", Format(demand.Status), "本地实例当前为 VISIBLE 或 GONE。"),
                    new("mesLastSeenAt", Format(demand.MesLastSeenAt), "MesIngest 最近一次在 MES 快照中观察到此实例的时间。"),
                    new("disappearCount", Format(demand.DisappearCount), "本地连续未观察计数；不等同于 MES 业务字段。"),
                    new("locationRisk", Format(demand.LocationRisk), "AREA_EMPTY 或 AREA_UNPARSEABLE 对应的 Demand 风险；不是 IngestAlert。"),
                    new("locationRiskCode", Format(demand.LocationRiskCode), "位置风险代码；AREA_EMPTY/AREA_UNPARSEABLE 属于 Demand 风险。"),
                    new("createdAt", Format(demand.CreatedAt), "本地实例创建时间（TransportDemand）。"),
                    new("goneAt", Format(demand.GoneAt), "本地实例转为 GONE 的时间；VISIBLE 时为 null。"),
                ]))
        {
            RelatedAlerts = (demand.Alerts ?? [])
                .Select(alert => WatchDemandRelatedAlert.From(demand, alert, timeZone))
                .ToList(),
        };
    }
}

public sealed record WatchDemandRelatedAlert(
    string Code,
    string Severity,
    string RelationshipBasis,
    string RelationshipLabel,
    string Message,
    string LastSeenAt)
{
    public static WatchDemandRelatedAlert From(
        WatchDemandDto demand,
        WatchAlertDto alert,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(alert);
        var exact = !string.IsNullOrWhiteSpace(alert.DemandId)
            && string.Equals(alert.DemandId, demand.DemandId, StringComparison.Ordinal);
        return new WatchDemandRelatedAlert(
            alert.Code,
            alert.Severity ?? string.Empty,
            exact ? "EXACT DemandId" : "BUSINESS KEY",
            exact ? "精确关联" : "业务键关联",
            alert.Message ?? string.Empty,
            WatchGridClipboard.FormatValue(alert.LastSeenAt, timeZone));
    }
}
