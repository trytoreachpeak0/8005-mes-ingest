using MesIngest.Watch;

namespace MesIngest.Tests;

public class WatchDemandDetailsTests
{
    [Fact]
    public void Separates_frozen_mes_inputs_from_local_projection_with_full_values_and_meaning()
    {
        var beijing = ResolveTz("China Standard Time", "Asia/Shanghai");
        var demand = new WatchDemandDto(
            DemandId: "abcdef0123456789abcdef0123456789",
            TaskType: "WIRE_TO_NITROGEN",
            Sublot: "S-LONG-001",
            Area: "A01-01",
            Eqp: "EQP-01",
            Step: "焊线2",
            Dates: new DateTimeOffset(2026, 8, 8, 1, 2, 3, TimeSpan.Zero),
            Package: "PKG-LONG-VALUE",
            Status: "VISIBLE",
            MesLastSeenAt: new DateTimeOffset(2026, 8, 8, 1, 3, 4, TimeSpan.Zero),
            DisappearCount: 0,
            LocationRisk: true,
            LocationRiskCode: "AREA_UNPARSEABLE",
            CreatedAt: new DateTimeOffset(2026, 8, 8, 1, 0, 0, TimeSpan.Zero),
            GoneAt: null);

        var details = WatchDemandDetails.From(demand, beijing);

        Assert.Equal(demand.DemandId, details.DemandId);
        Assert.Equal(demand.TaskType, details.TaskType);
        Assert.Equal(demand.Sublot, details.Sublot);
        Assert.Equal("MES 输入（创建时冻结）", details.MesInputGroup.Title);
        Assert.Equal(
            ["TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "DATES", "PACKAGE"],
            details.MesInputGroup.Fields.Select(field => field.Name));
        Assert.Equal("本地 TransportDemand 投影", details.LocalProjectionGroup.Title);
        Assert.Equal(
            [
                "DemandId",
                "status",
                "mesLastSeenAt",
                "disappearCount",
                "locationRisk",
                "locationRiskCode",
                "createdAt",
                "goneAt",
            ],
            details.LocalProjectionGroup.Fields.Select(field => field.Name));
        Assert.Equal("2026-08-08 09:02:03 +08:00", Field(details.MesInputGroup, "DATES").Value);
        Assert.Contains("当前工序", Field(details.MesInputGroup, "DATES").Explanation);
        Assert.Contains("下一工序", Field(details.MesInputGroup, "STEP").Explanation);
        Assert.Contains("Demand 风险", Field(details.LocalProjectionGroup, "locationRisk").Explanation);
        Assert.Contains("本地实例创建", Field(details.LocalProjectionGroup, "createdAt").Explanation);
        Assert.Equal("null", Field(details.LocalProjectionGroup, "goneAt").Value);
        Assert.Contains("VISIBLE", Field(details.LocalProjectionGroup, "goneAt").Explanation);

        var clipboard = details.ToClipboardText();
        Assert.StartsWith("TASK_TYPE\tSUBLOT\tAREA\tEQP\tSTEP\tDATES\tPACKAGE\tDemandId", clipboard);
        Assert.Contains("WIRE_TO_NITROGEN\tS-LONG-001\tA01-01\tEQP-01\t焊线2", clipboard);
        Assert.Contains("\tAREA_UNPARSEABLE\t", clipboard);
    }

    [Fact]
    public void Projects_related_alerts_without_flattening_exact_and_business_key_relationships()
    {
        var demand = new WatchDemandDto(
            "demand-1", "DIE_TO_OVEN", "S1", "A1", "E1", "烘烤",
            DateTimeOffset.Parse("2026-08-08T01:00:00Z"), "PKG", "VISIBLE",
            DateTimeOffset.Parse("2026-08-08T01:01:00Z"), 0, false, null,
            DateTimeOffset.Parse("2026-08-08T01:00:00Z"), null,
            [
                Alert("FIELD_DRIFT", "demand-1"),
                Alert("DUPLICATE_RECONCILE_KEY", null),
            ]);

        var details = WatchDemandDetails.From(demand);

        Assert.Equal("找到 2 条相关 IngestAlert。", details.RelatedAlertSummary);
        Assert.Equal("EXACT DemandId", details.RelatedAlerts[0].RelationshipBasis);
        Assert.Equal("精确关联", details.RelatedAlerts[0].RelationshipLabel);
        Assert.Equal("BUSINESS KEY", details.RelatedAlerts[1].RelationshipBasis);
        Assert.Equal("业务键关联", details.RelatedAlerts[1].RelationshipLabel);
    }

    private static WatchAlertDto Alert(string code, string? demandId) => new(
        "alert-" + code,
        code,
        "ERROR",
        "DIE_TO_OVEN",
        "S1",
        demandId,
        "message",
        null,
        DateTimeOffset.Parse("2026-08-08T01:00:00Z"),
        DateTimeOffset.Parse("2026-08-08T01:01:00Z"),
        1,
        true,
        null,
        DateTimeOffset.Parse("2026-08-08T01:00:00Z"));

    private static WatchDemandDetailField Field(WatchDemandDetailGroup group, string name) =>
        group.Fields.Single(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    private static TimeZoneInfo ResolveTz(string windowsId, string ianaId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
    }
}
