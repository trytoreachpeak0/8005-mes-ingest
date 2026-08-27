using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MesIngest.Watch.FluentPrototype;

// PROTOTYPE ONLY. The production implementation must use the centralized,
// strongly typed bilingual resource catalog required by ADR-0031.
internal static class PrototypeLocalization
{
    private static readonly IReadOnlyDictionary<string, (string Zh, string En)> Strings =
        new Dictionary<string, (string Zh, string En)>(StringComparer.Ordinal)
        {
            ["Nav.Overview"] = ("概览", "Overview"),
            ["Nav.Series"] = ("需求系列", "Demand series"),
            ["Nav.Audit"] = ("资格审计", "Eligibility audit"),
            ["Nav.Errors"] = ("错误检索", "Error search"),
            ["Nav.Area"] = ("AREA 筛选", "AREA filters"),
            ["Nav.Alerts"] = ("接入告警", "Ingest alerts"),
            ["Nav.Host"] = ("Host 已连接", "Host connected"),
            ["Nav.Settings"] = ("设置", "Settings"),
            ["Page.Title"] = ("资格审计", "Eligibility audit"),
            ["Page.Subtitle"] = ("解释每个 TransportDemand 当前能否被外部读取，以及为什么 · 自动刷新 10 秒", "Explain whether every TransportDemand is externally readable now, and why · Auto-refresh 10 s"),
            ["Page.Revision"] = ("目录修订 184", "Catalog revision 184"),
            ["Page.Updated"] = ("更新于 2026-08-27 12:42:16 +08:00 · 18 秒前", "Updated 2026-08-27 12:42:16 +08:00 · 18 seconds ago"),
            ["Filter.Outcome"] = ("资格结果", "Eligibility outcome"),
            ["Filter.All"] = ("全部 2,852", "All 2,852"),
            ["Filter.Readable"] = ("外部可读 1,842", "Readable 1,842"),
            ["Filter.NotReadable"] = ("不可读 1,010", "Not readable 1,010"),
            ["Filter.WorkType"] = ("工序类型", "Work type"),
            ["Filter.AllWorkTypes"] = ("全部工序类型", "All work types"),
            ["Filter.Reason"] = ("不可读原因", "Unreadable reason"),
            ["Filter.AllReasons"] = ("全部原因", "All reasons"),
            ["Filter.Search"] = ("DemandId / SUBLOT / AREA", "DemandId / SUBLOT / AREA"),
            ["Filter.Apply"] = ("应用条件", "Apply filters"),
            ["State.SummaryZh"] = ("当前状态：简体中文 · 全部结果 · 选中 D-001846 · 滚动位置 6 / 2,852", "State: English · All results · D-001846 selected · Scroll position 6 / 2,852"),
            ["Master.All"] = ("全部 TransportDemand", "All TransportDemand"),
            ["Master.Help"] = ("每个值都有可见标签；原始码保留为次要技术信息", "Every value has a visible label; raw codes remain secondary technical information"),
            ["Label.DemandId"] = ("Demand 标识", "Demand ID"),
            ["Label.WorkType"] = ("工序类型", "Work type"),
            ["Label.Sublot"] = ("批次", "SUBLOT"),
            ["Label.Area"] = ("区域", "AREA"),
            ["Label.Eqp"] = ("设备", "EQP"),
            ["Label.Step"] = ("工序", "STEP"),
            ["Label.Dates"] = ("观测时间", "DATES"),
            ["Label.Package"] = ("封装", "PACKAGE"),
            ["Label.Outcome"] = ("资格结果", "Eligibility outcome"),
            ["Label.Reason"] = ("原因", "Reason"),
            ["Label.LastSeen"] = ("最近观测", "Last observed"),
            ["Label.Source"] = ("来源事实", "Source fact"),
            ["Label.Meaning"] = ("界面含义", "UI meaning"),
            ["Label.Value"] = ("当前值", "Current value"),
            ["Label.RawField"] = ("原始字段", "Raw field"),
            ["Label.SourceState"] = ("值状态", "Value state"),
            ["Status.Readable"] = ("可读", "Readable"),
            ["Status.Blocked"] = ("阻断", "Blocked"),
            ["Status.Invisible"] = ("不可见", "Invisible"),
            ["Status.Active"] = ("当前活动", "Active now"),
            ["Reason.MissingArea"] = ("AREA 来源未提供", "AREA not provided by source"),
            ["Reason.Gone"] = ("Demand 已消失", "Demand is GONE"),
            ["Reason.Archived"] = ("所属系列已归档", "Owning series is archived"),
            ["Reason.Duplicate"] = ("业务键重复", "Duplicate business key"),
            ["Reason.None"] = ("无阻断条件", "No blocking condition"),
            ["Alert.Title"] = ("D-001846 当前不可被外部读取", "D-001846 is not externally readable"),
            ["Alert.Body"] = ("源系统未提供 AREA；Demand 仍完整保留在 Watch 中，但不会进入外部可读目录。", "The source system did not provide AREA. The Demand remains in Watch but is excluded from the externally readable catalog."),
            ["Detail.Observation"] = ("当前唯一 MES 观测", "Current unique MES observation"),
            ["Detail.Blocker"] = ("当前阻断条件", "Current blocking condition"),
            ["Detail.Scope"] = ("外部可读资格", "External readability"),
            ["Detail.Semantics"] = ("缺值与查询语义", "Missing-value and query semantics"),
            ["Detail.FieldLedger"] = ("字段语义账本", "Field semantics ledger"),
            ["Detail.Evidence"] = ("资格证据链", "Eligibility evidence chain"),
            ["Detail.Selected"] = ("已选 Demand", "Selected Demand"),
            ["Detail.ViewSeries"] = ("查看所属系列", "View owning series"),
            ["Check.Presence"] = ("当前状态为 VISIBLE", "Current state is VISIBLE"),
            ["Check.Unique"] = ("存在且只有一条原始观测", "Exactly one source observation exists"),
            ["Check.Area"] = ("AREA 必须有效", "AREA must be valid"),
            ["Check.Other"] = ("STEP、DATES、PACKAGE 有效", "STEP, DATES, and PACKAGE are valid"),
            ["Check.Conclusion"] = ("结论：外部当前看不到", "Conclusion: not externally readable now"),
            ["Missing.NotProvided"] = ("来源未提供", "Not provided by source"),
            ["Missing.Unknown"] = ("系统未知", "Unknown to system"),
            ["Missing.NotApplicable"] = ("不适用", "Not applicable"),
            ["Missing.NotLoaded"] = ("尚未加载", "Not loaded"),
            ["Missing.Empty"] = ("查询成功但无结果", "Query succeeded; no results"),
            ["Missing.Failed"] = ("读取失败", "Read failed"),
            ["Missing.NotProvidedHelp"] = ("源值为 NULL", "Source value is NULL"),
            ["Missing.UnknownHelp"] = ("未知动态码保留原值", "Unknown dynamic code retained"),
            ["Missing.NotApplicableHelp"] = ("该字段不参与此工序", "Field does not apply to this work type"),
            ["Missing.NotLoadedHelp"] = ("详情仍在加载", "Detail is still loading"),
            ["Missing.EmptyHelp"] = ("成功返回 0 条", "Successful result: 0 rows"),
            ["Missing.FailedHelp"] = ("超时，保留上次成功值", "Timed out; last successful value retained"),
            ["A.Name"] = ("A — 语义分层工作台", "A — Semantic workbench"),
            ["A.MasterHint"] = ("标签化条目", "Labeled entries"),
            ["B.Name"] = ("B — 字段语义账本", "B — Field semantics ledger"),
            ["B.MasterHint"] = ("有表头的紧凑主列表", "Compact master table with headers"),
            ["B.CodeAnalysis"] = ("状态码解释", "Status-code interpretation"),
            ["B.CodeBody"] = ("已知代码同时显示本地化含义；协议码保持原值，查询与筛选仍发送规范代码。", "Known codes show localized meaning alongside the raw protocol value. Queries and filters still send canonical codes."),
            ["C.Name"] = ("C — 证据叙事视图", "C — Evidence narrative"),
            ["C.MasterHint"] = ("按资格结果分组", "Grouped by eligibility outcome"),
            ["C.Step1"] = ("1 读取源事实", "1 Read source fact"),
            ["C.Step1Body"] = ("成功读取 1 条 MES 观测；AREA 的原始值为 NULL。", "One MES observation was read successfully; raw AREA is NULL."),
            ["C.Step2"] = ("2 解释字段语义", "2 Interpret field semantics"),
            ["C.Step2Body"] = ("NULL 表示来源未提供，不等同于系统未知、尚未加载或查询无结果。", "NULL means not provided by source; it is not unknown, not loaded, or an empty query result."),
            ["C.Step3"] = ("3 评估资格规则", "3 Evaluate eligibility rule"),
            ["C.Step3Body"] = ("AREA 有效性条件不成立，触发已知阻断码。", "The AREA validity condition fails and raises a known blocker code."),
            ["C.Step4"] = ("4 得出当前结论", "4 Reach current conclusion"),
            ["C.Step4Body"] = ("Demand 保留在 Watch；外部目录当前不可见。", "The Demand remains in Watch and is currently absent from the external catalog."),
            ["C.Next"] = ("建议操作：修复源 AREA 后等待下一次完整成功轮次；无需删除 Demand。", "Next action: fix source AREA and wait for the next complete successful cycle; do not delete the Demand."),
            ["Group.Blocked"] = ("阻断 · 2", "Blocked · 2"),
            ["Group.Invisible"] = ("不可见 · 2", "Invisible · 2"),
            ["Group.Readable"] = ("可读 · 1,842", "Readable · 1,842"),
            ["Review.LanguageZh"] = ("预览语言：简体中文", "Preview language: Simplified Chinese"),
            ["Review.LanguageEn"] = ("预览语言：English", "Preview language: English"),
            ["Review.SwitchEnglish"] = ("切换到 English", "Switch to English"),
            ["Review.SwitchChinese"] = ("切换到简体中文", "Switch to Simplified Chinese"),
        };

    public static string Get(string key, bool english) =>
        Strings.TryGetValue(key, out var value) ? (english ? value.En : value.Zh) : key;

    public static void Apply(DependencyObject root, bool english)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        ApplyRecursive(root, english, visited);
    }

    private static void ApplyRecursive(
        DependencyObject root,
        bool english,
        ISet<DependencyObject> visited)
    {
        if (!visited.Add(root))
        {
            return;
        }

        ApplyOne(root, english);

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            ApplyRecursive(child, english, visited);
        }

        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D)
        {
            return;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            ApplyRecursive(VisualTreeHelper.GetChild(root, i), english, visited);
        }
    }

    private static void ApplyOne(DependencyObject element, bool english)
    {
        if (element is not FrameworkElement frameworkElement
            || frameworkElement.Tag is not string tag
            || !tag.StartsWith("i18n:", StringComparison.Ordinal))
        {
            return;
        }

        var text = Get(tag[5..], english);
        switch (element)
        {
            case TextBlock textBlock:
                textBlock.Text = text;
                break;
            case ContentControl contentControl:
                contentControl.Content = text;
                break;
        }
    }
}
