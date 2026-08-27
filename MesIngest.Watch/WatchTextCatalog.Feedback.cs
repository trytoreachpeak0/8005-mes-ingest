using System.Globalization;

namespace MesIngest.Watch;

internal sealed partial class WatchFeedbackText
{
    private static readonly IReadOnlyDictionary<string, string> KnownEnglish =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["本机设置已保存"] = "Local settings saved",
            ["自动刷新保持开启；当前 Host 会话未重建。"] = "Auto-refresh remains enabled; the current Host session was not rebuilt.",
            ["无法保存本机设置"] = "Could not save local settings",
            ["本机设置文件无法写入。请检查文件权限后重试。"] = "The local settings file could not be written. Check file permissions and try again.",
            ["重试保存"] = "Retry save",
            ["重试"] = "Retry",
            ["恢复"] = "Recovered",
            ["读取已恢复"] = "Read recovered",
            ["后台期间出现新的持续故障"] = "A new continuing fault occurred in the background",
            ["查看故障"] = "View fault",
            ["返回 AREA 配置"] = "Return to AREA profiles",
            ["AREA 配置目录监视已降级"] = "AREA profile folder monitoring is degraded",
            ["刷新已清除原选择；详情保持未选择，请重新选择一项。"] = "Refresh cleared the previous selection; details remain unselected. Select an item again.",
            ["操作未完成；请检查输入、文件权限或当前磁盘版本后重试。"] = "The operation did not complete. Check the input, file permissions, or current disk version and try again.",
        };

    private static readonly WatchTextCatalogEntry[] FeedbackEntries =
    [
        new("feedback.severity.information", "信息", "Information"),
        new("feedback.severity.success", "成功", "Success"),
        new("feedback.severity.warning", "警告", "Warning"),
        new("feedback.severity.error", "错误", "Error"),
        new("feedback.occurrence.first", "首次出现", "First occurrence"),
        new("feedback.occurrence.merged", "已合并 {0} 次", "Merged {0} times"),
        new("feedback.timer.seconds", "{0} 秒", "{0} seconds"),
        new("feedback.dismiss", "关闭通知", "Dismiss notification"),
        new("feedback.severity.automation", "{0}严重度", "{0} severity"),
        new("feedback.overlay.name", "窗口通知", "Window notifications"),
        new("feedback.overlay.live", "窗口通知播报", "Window notification announcements"),
        new("feedback.overlay.cards", "窗口通知卡片", "Window notification cards"),
        new("feedback.dialog.allAreas.title", "目标不在当前 AREA 范围", "Target is outside the current AREA scope"),
        new("feedback.dialog.allAreas.primary", "切换到全部 AREA", "Switch to all AREA"),
        new("feedback.dialog.cancel", "取消", "Cancel"),
        new("feedback.dialog.allAreas.body1", "切换到“全部 AREA”会清除冻结游标，并重新查询第一页和目标详情。", "Switching to all AREA clears the frozen cursor and queries the first page and target details again."),
        new("feedback.dialog.allAreas.body2", "这只改变本机显示范围，不会改变 Host 业务投影或 Dispatch 范围。", "This changes only the local display scope; the Host business projection and Dispatch scope remain unchanged."),
        new("feedback.dialog.conflict.title", "AREA 文件已被其他程序修改", "AREA file was modified by another program"),
        new("feedback.dialog.conflict.overwrite", "覆盖并保存", "Overwrite and save"),
        new("feedback.dialog.conflict.reload", "重新载入文件", "Reload file"),
        new("feedback.dialog.conflict.later", "稍后处理", "Handle later"),
        new("feedback.dialog.conflict.body2", "关闭此对话框不会丢弃草稿，但自动保存会继续暂停。", "Closing this dialog does not discard the draft, but auto-save remains paused."),
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => FeedbackEntries;

    private string Get(string id) => Text(FeedbackEntries.Single(entry => entry.SemanticId == id));

    public string Severity(WatchNotificationSeverity severity) => severity switch
    {
        WatchNotificationSeverity.Success => Get("feedback.severity.success"),
        WatchNotificationSeverity.Warning => Get("feedback.severity.warning"),
        WatchNotificationSeverity.Error => Get("feedback.severity.error"),
        _ => Get("feedback.severity.information"),
    };

    public string Occurrence(int count) => count > 1
        ? string.Format(CultureInfo.InvariantCulture, Get("feedback.occurrence.merged"), count)
        : Get("feedback.occurrence.first");

    public string Timer(int seconds) =>
        string.Format(CultureInfo.InvariantCulture, Get("feedback.timer.seconds"), seconds);

    public string Dismiss => Get("feedback.dismiss");

    public string SeverityAutomation(string severityText) =>
        string.Format(CultureInfo.InvariantCulture, Get("feedback.severity.automation"), severityText);
    public string OverlayName => Get("feedback.overlay.name");
    public string LiveRegionName => Get("feedback.overlay.live");
    public string CardsName => Get("feedback.overlay.cards");
    public string AllAreasDialogTitle => Get("feedback.dialog.allAreas.title");
    public string AllAreasDialogPrimary => Get("feedback.dialog.allAreas.primary");
    public string DialogCancel => Get("feedback.dialog.cancel");
    public string AllAreasDialogBody1 => Get("feedback.dialog.allAreas.body1");
    public string AllAreasDialogBody2 => Get("feedback.dialog.allAreas.body2");
    public string ConflictDialogTitle => Get("feedback.dialog.conflict.title");
    public string ConflictDialogOverwrite => Get("feedback.dialog.conflict.overwrite");
    public string ConflictDialogReload => Get("feedback.dialog.conflict.reload");
    public string ConflictDialogLater => Get("feedback.dialog.conflict.later");
    public string ConflictDialogBody(string profileName) => Language == WatchDisplayLanguage.English
        ? $"Both the on-disk version of {profileName}.txt and the current draft changed, so they cannot be merged automatically."
        : $"{profileName}.txt 的磁盘版本与当前草稿都已改变，无法自动合并。";
    public string ConflictDialogBody2 => Get("feedback.dialog.conflict.body2");

    internal static WatchLocalizedNotificationContent Localize(
        WatchNotificationEvent notification) => new(
        new(
            WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).Feedback.Severity(notification.Severity),
            WatchTextCatalog.For(WatchDisplayLanguage.English).Feedback.Severity(notification.Severity)),
        new(notification.Title, TranslateKnownChinese(notification.Title)),
        new(notification.Message, TranslateKnownChinese(notification.Message)),
        notification.ActionLabel is null
            ? null
            : new WatchLocalizedText(notification.ActionLabel, TranslateKnownChinese(notification.ActionLabel)));

    internal static WatchLocalizedNotificationContent ContinuingFault(
        string sourceKey,
        WatchHostFailureKind failureKind)
    {
        var title = sourceKey switch
        {
            "host.connection" => ("Host 连接持续失败", "Host connection keeps failing"),
            "overview.refresh" => ("概览读取持续失败", "Overview read keeps failing"),
            "overview.protection" => ("保护状态读取持续失败", "Protection-state read keeps failing"),
            "demand-series.refresh" => ("需求系列读取持续失败", "Demand-series read keeps failing"),
            "demand-series.detail" => ("需求系列详情读取持续失败", "Demand-series detail read keeps failing"),
            "readability.refresh" => ("资格审计读取持续失败", "Eligibility-audit read keeps failing"),
            "readability.detail" => ("资格审计详情读取持续失败", "Eligibility-audit detail read keeps failing"),
            "error-search.refresh" => ("错误检索读取持续失败", "Error Search read keeps failing"),
            "error-search.detail" => ("错误详情读取持续失败", "Error-detail read keeps failing"),
            "attention.refresh" => ("接入告警读取持续失败", "Current-ingest-attention read keeps failing"),
            _ => ("读取持续失败", "Read keeps failing"),
        };
        var isHostConnection = sourceKey == "host.connection";
        var message = FailureMessage(failureKind, isHostConnection);
        var action = isHostConnection
            ? ("打开设置", "Open settings")
            : ("查看页面", "View page");
        return new WatchLocalizedNotificationContent(
            new("错误", "Error"),
            new(title.Item1, title.Item2),
            new(message.Chinese, message.English),
            new(action.Item1, action.Item2));
    }

    internal static WatchLocalizedNotificationContent Recovered(
        WatchLocalizedText faultTitle) => new(
        new("恢复", "Recovered"),
        new("读取已恢复", "Read recovered"),
        new(
            $"{faultTitle.SimplifiedChinese}已恢复；稳定故障状态已清除。",
            $"{faultTitle.English} recovered; the continuing-fault state was cleared."));

    internal static WatchLocalizedNotificationContent BackgroundFaultSummary(
        int count,
        WatchLocalizedText mostSevereTitle) => new(
        new("错误", "Error"),
        new("后台期间出现新的持续故障", "New continuing fault occurred in the background"),
        count == 1
            ? mostSevereTitle
            : new WatchLocalizedText(
                $"仍有 {count} 个新故障活动；请查看页面标题状态。",
                $"{count} new faults remain active; review the page-header status."),
        new("查看故障", "View fault"));

    internal static WatchLocalizedNotificationContent FaultDetails(
        int count,
        WatchLocalizedNotificationContent fault) => new(
        new("错误", "Error"),
        fault.Title,
        count == 1
            ? fault.Message
            : new WatchLocalizedText(
                $"当前共有 {count} 个持续故障。{fault.Message.SimplifiedChinese}",
                $"There are currently {count} continuing faults. {fault.Message.English}"));

    internal static (string Text, string Detail, string Automation) FaultHeader(
        WatchDisplayLanguage language,
        IReadOnlyList<WatchContinuingFault> faults)
    {
        var projected = faults.Select(fault => fault.Project(language)).ToArray();
        var text = language == WatchDisplayLanguage.English
            ? $"Error · {faults.Count} faults"
            : $"错误 · {faults.Count} 个故障";
        var detail = string.Join(
            language == WatchDisplayLanguage.English ? "; " : "；",
            projected.Select(item => item.Title));
        var automation = language == WatchDisplayLanguage.English
            ? $"{text}. {detail}. Open fault details"
            : $"{text}。{detail}。打开故障详情";
        return (text, detail, automation);
    }

    private static (string Chinese, string English) FailureMessage(
        WatchHostFailureKind failureKind,
        bool isHostConnection) => (failureKind, isHostConnection) switch
    {
        (WatchHostFailureKind.Authentication, _) =>
            ("Host 拒绝了当前凭据；请在设置中更新凭据后重试。", "Host rejected the current credential. Update it in Settings and try again."),
        (WatchHostFailureKind.Contract or WatchHostFailureKind.Decode, _) =>
            ("Host 返回内容与当前 Watch 不兼容；请核对 Host 版本。", "Host returned content incompatible with this Watch version. Check the Host version."),
        (WatchHostFailureKind.Timeout, true) =>
            ("Host 连接验证超时；请检查连接设置后重试。", "Host connection validation timed out. Check the connection settings and try again."),
        (WatchHostFailureKind.Network or WatchHostFailureKind.Http, true) =>
            ("Host 暂时不可达；请检查连接设置后重试。", "Host is temporarily unreachable. Check the connection settings and try again."),
        (_, true) =>
            ("Host 连接验证失败；请检查连接设置后重试。", "Host connection validation failed. Check the connection settings and try again."),
        (WatchHostFailureKind.Timeout, false) =>
            ("请求超时；页面保持当前稳定状态，自动刷新仍会继续。", "The request timed out. The page keeps its stable state and auto-refresh continues."),
        (WatchHostFailureKind.Network or WatchHostFailureKind.Http, false) =>
            ("Host 暂时不可达；页面保持当前稳定状态，自动刷新仍会继续。", "Host is temporarily unreachable. The page keeps its stable state and auto-refresh continues."),
        (WatchHostFailureKind.ServerQuery, false) =>
            ("Host 查询失败；页面保持当前稳定状态，自动刷新仍会继续。", "The Host query failed. The page keeps its stable state and auto-refresh continues."),
        _ =>
            ("读取暂时失败；页面保持当前稳定状态，自动刷新仍会继续。", "The read temporarily failed. The page keeps its stable state and auto-refresh continues."),
    };

    internal static string TranslateKnownChinese(string chinese)
    {
        if (KnownEnglish.TryGetValue(chinese, out var translated))
        {
            return translated;
        }

        return chinese
            .Replace("配置列表可能不是最新的。", "The profile list may not be current. ", StringComparison.Ordinal)
            .Replace("无法读取", "Could not read ", StringComparison.Ordinal)
            .Replace("读取失败", "Read failed", StringComparison.Ordinal)
            .Replace("刷新失败", "Refresh failed", StringComparison.Ordinal)
            .Replace("保存失败", "Save failed", StringComparison.Ordinal)
            .Replace("已恢复", " recovered", StringComparison.Ordinal)
            .Replace("当前共有", "There are currently ", StringComparison.Ordinal)
            .Replace("个持续故障", " continuing faults", StringComparison.Ordinal)
            .Replace("查看", "View ", StringComparison.Ordinal);
    }
}
