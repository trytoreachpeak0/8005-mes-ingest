using System.Globalization;

namespace MesIngest.Watch;

internal sealed partial class WatchFeedbackText
{
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
        new("feedback.settings.saved", "本机设置已保存", "Local settings saved"),
        new("feedback.settings.saved-detail", "自动刷新保持开启；当前 Host 会话未重建。", "Auto-refresh remains enabled; the current Host session was not rebuilt."),
        new("feedback.settings.save-failed", "无法保存本机设置", "Could not save local settings"),
        new("feedback.settings.save-failed-detail", "本机设置文件无法写入。请检查文件权限后重试。", "The local settings file could not be written. Check file permissions and try again."),
        new("feedback.action.retry-save", "重试保存", "Retry save"),
        new("feedback.action.retry", "重试", "Retry"),
        new("feedback.recovered", "恢复", "Recovered"),
        new("feedback.read-recovered", "读取已恢复", "Read recovered"),
        new("feedback.background-fault", "后台期间出现新的持续故障", "A new continuing fault occurred in the background"),
        new("feedback.action.view-fault", "查看故障", "View fault"),
        new("feedback.action.return-area", "返回 AREA 配置", "Return to AREA profiles"),
        new("feedback.area-monitor-degraded", "AREA 配置目录监视已降级", "AREA profile folder monitoring is degraded"),
        new("feedback.selection-cleared", "刷新已清除原选择；详情保持未选择，请重新选择一项。", "Refresh cleared the previous selection; details remain unselected. Select an item again."),
        new("feedback.operation-failed", "操作未完成；请检查输入、文件权限或当前磁盘版本后重试。", "The operation did not complete. Check the input, file permissions, or current disk version and try again."),
        new("feedback.selection.demand-series", "原需求系列已不在刷新结果中", "The previous demand series is no longer in the refreshed results"),
        new("feedback.selection.transport-demand", "原运输需求已不在刷新结果中", "The previous transport demand is no longer in the refreshed results"),
        new("feedback.selection.error", "原错误项已不在刷新结果中", "The previous error item is no longer in the refreshed results"),
        new("feedback.selection.attention", "原关注项已不在刷新结果中", "The previous attention item is no longer in the refreshed results"),
        new("feedback.operation.readability.title", "无法执行资格审计操作", "Unable to complete the eligibility-audit operation"),
        new("feedback.operation.readability.action", "返回资格审计", "Return to eligibility audit"),
        new("feedback.operation.demand-series.title", "无法执行需求系列操作", "Unable to complete the demand-series operation"),
        new("feedback.operation.demand-series.action", "返回需求系列", "Return to demand series"),
        new("feedback.operation.retry-message", "请检查输入或当前快照后重试。", "Check the input or current snapshot and try again."),
        new("feedback.settings.host-applied.title", "Host 设置已应用", "Host settings applied"),
        new("feedback.settings.host-applied.message", "契约兼容，概览已读取并恢复自动刷新。", "The contract is compatible; overview was read and auto-refresh resumed."),
        new("feedback.settings.host-failed.title", "无法应用 Host 设置", "Unable to apply Host settings"),
        new("feedback.settings.host-failed.message", "请检查地址格式、超时范围或本机设置文件后重试。", "Check the address format, timeout range, or local settings file and try again."),
        new("feedback.settings.layout-restored.title", "已恢复默认布局", "Default layout restored"),
        new("feedback.settings.layout-restored.message", "窗口恢复为 1440×900 和紧凑导航 rail；刷新间隔与 Host 会话保持不变。", "The window was restored to 1440×900 with a compact navigation rail; refresh intervals and the Host session are unchanged."),
        new("feedback.settings.layout-failed.title", "无法恢复默认布局", "Unable to restore the default layout"),
        new("feedback.settings.layout-failed.message", "本机布局设置无法写入；请检查文件权限后重试。", "The local layout settings could not be written. Check file permissions and try again."),
        new("feedback.settings.defaults-restored.title", "已恢复默认设置", "Default settings restored"),
        new("feedback.settings.defaults-restored.message", "窗口恢复为 1440×900、紧凑导航 rail；五个数据视图保持 10 秒自动刷新。Host 会话未重建。", "The window was restored to 1440×900 with a compact navigation rail; all five data views keep 10-second auto-refresh. The Host session was not rebuilt."),
        new("feedback.settings.defaults-failed.title", "无法恢复默认设置", "Unable to restore default settings"),
        new("feedback.settings.defaults-failed.message", "本机设置无法写入；请检查文件权限后重试。", "The local settings could not be written. Check file permissions and try again."),
        new("feedback.fault.host-connection", "Host 连接持续失败", "Host connection keeps failing"),
        new("feedback.fault.overview-refresh", "概览读取持续失败", "Overview read keeps failing"),
        new("feedback.fault.overview-protection", "保护状态读取持续失败", "Protection-state read keeps failing"),
        new("feedback.fault.demand-refresh", "需求系列读取持续失败", "Demand-series read keeps failing"),
        new("feedback.fault.demand-detail", "需求系列详情读取持续失败", "Demand-series detail read keeps failing"),
        new("feedback.fault.readability-refresh", "资格审计读取持续失败", "Eligibility-audit read keeps failing"),
        new("feedback.fault.readability-detail", "资格审计详情读取持续失败", "Eligibility-audit detail read keeps failing"),
        new("feedback.fault.error-search-refresh", "错误检索读取持续失败", "Error Search read keeps failing"),
        new("feedback.fault.error-search-detail", "错误详情读取持续失败", "Error-detail read keeps failing"),
        new("feedback.fault.attention-refresh", "接入告警读取持续失败", "Current-ingest-attention read keeps failing"),
        new("feedback.fault.generic", "读取持续失败", "Read keeps failing"),
        new("feedback.action.open-settings", "打开设置", "Open settings"),
        new("feedback.action.view-page", "查看页面", "View page"),
        new("feedback.recovered.message", "{0}已恢复；稳定故障状态已清除。", "{0} recovered; the continuing-fault state was cleared."),
        new("feedback.background-fault.title", "后台期间出现新的持续故障", "New continuing fault occurred in the background"),
        new("feedback.background-fault.message", "仍有 {0} 个新故障活动；请查看页面标题状态。", "{0} new faults remain active; review the page-header status."),
        new("feedback.fault-details.message", "当前共有 {0} 个持续故障。{1}", "There are currently {0} continuing faults. {1}"),
        new("feedback.failure.authentication", "Host 拒绝了当前凭据；请在设置中更新凭据后重试。", "Host rejected the current credential. Update it in Settings and try again."),
        new("feedback.failure.contract", "Host 返回内容与当前 Watch 不兼容；请核对 Host 版本。", "Host returned content incompatible with this Watch version. Check the Host version."),
        new("feedback.failure.host-timeout", "Host 连接验证超时；请检查连接设置后重试。", "Host connection validation timed out. Check the connection settings and try again."),
        new("feedback.failure.host-network", "Host 暂时不可达；请检查连接设置后重试。", "Host is temporarily unreachable. Check the connection settings and try again."),
        new("feedback.failure.host-generic", "Host 连接验证失败；请检查连接设置后重试。", "Host connection validation failed. Check the connection settings and try again."),
        new("feedback.failure.timeout", "请求超时；页面保持当前稳定状态，自动刷新仍会继续。", "The request timed out. The page keeps its stable state and auto-refresh continues."),
        new("feedback.failure.network", "Host 暂时不可达；页面保持当前稳定状态，自动刷新仍会继续。", "Host is temporarily unreachable. The page keeps its stable state and auto-refresh continues."),
        new("feedback.failure.server-query", "Host 查询失败；页面保持当前稳定状态，自动刷新仍会继续。", "The Host query failed. The page keeps its stable state and auto-refresh continues."),
        new("feedback.failure.generic", "读取暂时失败；页面保持当前稳定状态，自动刷新仍会继续。", "The read temporarily failed. The page keeps its stable state and auto-refresh continues."),
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries =>
        [.. FeedbackEntries, .. WatchLegacyGeneratedText.FeedbackEntries];

    private string Get(string id) => Text(FeedbackEntries.Single(entry => entry.SemanticId == id));

    internal static WatchLocalizedText Localized(string semanticId)
    {
        var entry = FeedbackEntries.Single(entry => entry.SemanticId == semanticId);
        return WatchLocalizedText.FromEntry(entry);
    }

    internal static WatchLocalizedText LocalizedFormat(string semanticId, params object?[] arguments)
    {
        var entry = FeedbackEntries.Single(entry => entry.SemanticId == semanticId);
        return new WatchLocalizedText(
            string.Format(CultureInfo.InvariantCulture, entry.SimplifiedChinese, arguments),
            string.Format(CultureInfo.InvariantCulture, entry.English, arguments));
    }

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
    public string ConflictDialogBody(string profileName) => Format(WatchLegacyGeneratedText.Feedback026, new object?[] { profileName }, new object?[] { profileName });
    public string ConflictDialogBody2 => Get("feedback.dialog.conflict.body2");

    internal static WatchLocalizedNotificationContent Localize(
        WatchNotificationEvent notification) => new(
        new(
            WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).Feedback.Severity(notification.Severity),
            WatchTextCatalog.For(WatchDisplayLanguage.English).Feedback.Severity(notification.Severity)),
        new(notification.Title, notification.Title),
        new(notification.Message, notification.Message),
        notification.ActionLabel is null
            ? null
            : new WatchLocalizedText(notification.ActionLabel, notification.ActionLabel));

    internal static WatchLocalizedNotificationContent ContinuingFault(
        string sourceKey,
        WatchHostFailureKind failureKind)
    {
        var title = Localized(sourceKey switch
        {
            "host.connection" => "feedback.fault.host-connection",
            "overview.refresh" => "feedback.fault.overview-refresh",
            "overview.protection" => "feedback.fault.overview-protection",
            "demand-series.refresh" => "feedback.fault.demand-refresh",
            "demand-series.detail" => "feedback.fault.demand-detail",
            "readability.refresh" => "feedback.fault.readability-refresh",
            "readability.detail" => "feedback.fault.readability-detail",
            "error-search.refresh" => "feedback.fault.error-search-refresh",
            "error-search.detail" => "feedback.fault.error-search-detail",
            "attention.refresh" => "feedback.fault.attention-refresh",
            _ => "feedback.fault.generic",
        });
        var isHostConnection = sourceKey == "host.connection";
        var message = FailureMessage(failureKind, isHostConnection);
        var action = Localized(isHostConnection
            ? "feedback.action.open-settings"
            : "feedback.action.view-page");
        return new WatchLocalizedNotificationContent(
            Localized("feedback.severity.error"),
            title,
            message,
            action);
    }

    internal static WatchLocalizedNotificationContent Recovered(
        WatchLocalizedText faultTitle) => new(
        Localized("feedback.recovered"),
        Localized("feedback.read-recovered"),
        new WatchLocalizedText(
            string.Format(CultureInfo.InvariantCulture, Localized("feedback.recovered.message").SimplifiedChinese, faultTitle.SimplifiedChinese),
            string.Format(CultureInfo.InvariantCulture, Localized("feedback.recovered.message").English, faultTitle.English)));

    internal static WatchLocalizedNotificationContent BackgroundFaultSummary(
        int count,
        WatchLocalizedText mostSevereTitle) => new(
        Localized("feedback.severity.error"),
        Localized("feedback.background-fault.title"),
        count == 1
            ? mostSevereTitle
            : LocalizedFormat("feedback.background-fault.message", count),
        Localized("feedback.action.view-fault"));

    internal static WatchLocalizedNotificationContent FaultDetails(
        int count,
        WatchLocalizedNotificationContent fault) => new(
        Localized("feedback.severity.error"),
        fault.Title,
        count == 1
            ? fault.Message
            : new WatchLocalizedText(
                string.Format(CultureInfo.InvariantCulture, Localized("feedback.fault-details.message").SimplifiedChinese, count, fault.Message.SimplifiedChinese),
                string.Format(CultureInfo.InvariantCulture, Localized("feedback.fault-details.message").English, count, fault.Message.English)));

    internal static (string Text, string Detail, string Automation) FaultHeader(
        WatchDisplayLanguage language,
        IReadOnlyList<WatchContinuingFault> faults)
    {
        var projected = faults.Select(fault => fault.Project(language)).ToArray();
        var text = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            WatchLegacyGeneratedText.Feedback027.In(language),
            faults.Count);
        var detail = string.Join(
            WatchLegacyGeneratedText.Feedback028.In(language),
            projected.Select(item => item.Title));
        var automation = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            WatchLegacyGeneratedText.Feedback029.In(language),
            text,
            detail);
        return (text, detail, automation);
    }

    private static WatchLocalizedText FailureMessage(
        WatchHostFailureKind failureKind,
        bool isHostConnection) => (failureKind, isHostConnection) switch
    {
        (WatchHostFailureKind.Authentication, _) =>
            Localized("feedback.failure.authentication"),
        (WatchHostFailureKind.Contract or WatchHostFailureKind.Decode, _) =>
            Localized("feedback.failure.contract"),
        (WatchHostFailureKind.Timeout, true) =>
            Localized("feedback.failure.host-timeout"),
        (WatchHostFailureKind.Network or WatchHostFailureKind.Http, true) =>
            Localized("feedback.failure.host-network"),
        (_, true) =>
            Localized("feedback.failure.host-generic"),
        (WatchHostFailureKind.Timeout, false) =>
            Localized("feedback.failure.timeout"),
        (WatchHostFailureKind.Network or WatchHostFailureKind.Http, false) =>
            Localized("feedback.failure.network"),
        (WatchHostFailureKind.ServerQuery, false) =>
            Localized("feedback.failure.server-query"),
        _ =>
            Localized("feedback.failure.generic"),
    };

}
