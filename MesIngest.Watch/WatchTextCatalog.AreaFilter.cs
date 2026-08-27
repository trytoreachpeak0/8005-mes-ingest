using System.Globalization;

namespace MesIngest.Watch;

internal sealed partial class WatchAreaFilterText
{
    private static readonly WatchTextCatalogEntry[] AreaEntries =
    [
        new("area.page.title", "AREA 筛选", "AREA filter profiles"),
        new("area.page.subtitle", "当前 Windows 用户的本机命名 TXT 显示范围；不会改变外部资格、CatalogRevision 或 Dispatch 范围", "Named local TXT display scopes for the current Windows user; external eligibility, CatalogRevision, and Dispatch scope are unchanged"),
        new("area.list.title", "筛选配置", "Filter profiles"),
        new("area.list.notLoaded", "尚未读取本机配置", "Local profiles have not been loaded"),
        new("area.list.openFolder", "打开配置文件夹", "Open profiles folder"),
        new("area.list.new", "新建", "New"),
        new("area.list.saveAs", "另存为", "Save as"),
        new("area.list.rename", "重命名", "Rename"),
        new("area.list.delete", "删除", "Delete"),
        new("area.operation.confirm", "确认", "Confirm"),
        new("area.operation.cancel", "取消", "Cancel"),
        new("area.operation.confirmName", "确认名称", "Confirm name"),
        new("area.operation.confirmDelete", "确认删除", "Confirm delete"),
        new("area.localOnly", "仅影响本机当前用户的显示", "Affects display for the current local user only"),
        new("area.draft.saveAs", "另存草稿", "Save draft as"),
        new("area.apply.apply", "应用此配置", "Apply profile"),
        new("area.apply.applied", "已应用", "Applied"),
        new("area.apply.reapply", "重新应用", "Reapply"),
        new("area.editor.new", "新建 AREA 配置", "New AREA profile"),
        new("area.editor.noValid", "尚无有效 AREA", "No valid AREA values"),
        new("area.editor.storage", "本机配置目录 · UTF-8", "Local profiles folder · UTF-8"),
        new("area.validation.notPassed", "格式尚未通过校验", "Validation has not passed"),
        new("area.disk.notSaved", "尚未保存", "Not saved"),
        new("area.validation.details", "查看逐项校验", "View validation details"),
        new("area.validation.line", "行", "Line"),
        new("area.validation.code", "代码", "Code"),
        new("area.validation.message", "说明", "Description"),
        new("area.row.applied", "当前应用", "Applied"),
        new("area.row.valid", "有效", "Valid"),
        new("area.row.invalid", "无效", "Invalid"),
        new("area.row.all", "全部 AREA（不筛选）", "All AREA (no filter)"),
        new("area.row.unrestricted", "不限制显示范围", "Display scope is unrestricted"),
        new("area.disk.conflict", "磁盘已变更 · 等待选择", "File changed on disk · Choose an action"),
        new("area.disk.deletedDraft", "文件已删除 · 未命名草稿", "File deleted · Untitled draft"),
        new("area.disk.deletedApplied", "文件已删除 · 范围仍生效", "File deleted · Scope remains active"),
        new("area.disk.pending", "未落盘 · 即将自动保存", "Not on disk · Auto-save pending"),
        new("area.disk.saved", "已自动保存", "Auto-saved"),
        new("area.disk.noFile", "不对应 TXT 文件", "No TXT file"),
        new("area.feedback.severity.information", "信息", "Information"),
        new("area.feedback.severity.success", "成功", "Success"),
        new("area.feedback.severity.warning", "警告", "Warning"),
        new("area.feedback.severity.error", "错误", "Error"),
        new("area.feedback.action.return", "返回 AREA 配置", "Return to AREA profiles"),
        new("area.feedback.restore.title", "无法恢复上次 AREA 配置", "Could not restore the previous AREA profile"),
        new("area.feedback.restore.message", "已回退到全部 AREA。{0}", "The display scope fell back to all AREA values. Check the local applied-profile marker.{0}"),
        new("area.feedback.readProfiles.title", "无法读取本机 AREA 配置", "Could not read local AREA profiles"),
        new("area.feedback.readProfiles.listMessage", "配置列表可能不是最新的。{0}", "The profile list might not be current. Check the local profile folder and permissions.{0}"),
        new("area.feedback.readProfiles.scopeMessage", "当前已应用显示范围保持不变。{0}", "The currently applied display scope is unchanged. Check the local profile folder and permissions.{0}"),
        new("area.feedback.readApplied.title", "无法读取当前应用配置的 TXT", "Could not read the applied AREA profile TXT"),
        new("area.feedback.readApplied.message", "已应用 AREA 快照保持不变。{0}", "The applied AREA snapshot is unchanged. Check the local profile file and permissions.{0}"),
        new("area.feedback.directoryWatch.title", "AREA 配置目录监视已降级", "AREA profile folder monitoring is degraded"),
        new("area.feedback.directoryWatch.message", "配置列表可能不是最新的。{0}", "The profile list might not be current. The folder watcher will continue trying to recover.{0}"),
        new("area.feedback.directoryOpened.title", "AREA 配置目录已打开", "AREA profiles folder opened"),
        new("area.feedback.directoryOpened.message", "已通过平台文件管理器打开 {0}。", "Opened {0} in the platform file manager."),
        new("area.feedback.directoryPrepared.title", "AREA 配置目录已准备", "AREA profiles folder is ready"),
        new("area.feedback.directoryPrepared.message", "已确认 {0} 存在；UI 测试模式未启动文件管理器。", "Confirmed that {0} exists; UI test mode did not start the file manager."),
        new("area.feedback.readProfile.title", "无法读取 AREA TXT 配置", "Could not read the AREA TXT profile"),
        new("area.feedback.readProfile.message", "无法读取所选文件。{0}", "The selected profile could not be read. Check the file and its permissions.{0}"),
        new("area.feedback.confirmApplied.title", "无法确认已应用 AREA 范围", "Could not confirm the applied AREA scope"),
        new("area.feedback.confirmApplied.message", "{0} 文件操作已取消；请先修复标记或明确应用全部 AREA。", "{0}The file operation was cancelled. Repair the applied-profile marker or explicitly apply all AREA values first."),
        new("area.feedback.prepareOperation.title", "无法准备 AREA 文件操作", "Could not prepare the AREA file operation"),
        new("area.feedback.prepareOperation.missingVersion", "当前 AREA TXT 未记录所显示的磁盘版本；请重新加载后再试。", "The displayed AREA TXT has no recorded disk version. Reload it and try again."),
        new("area.feedback.prepareOperation.message", "无法读取当前磁盘版本。{0}", "The current disk version could not be read. Check the profile file and permissions.{0}"),
        new("area.feedback.changedOnDisk.title", "AREA TXT 已在磁盘更改", "AREA TXT changed on disk"),
        new("area.feedback.changedOnDisk.message", "所显示的 AREA TXT 已在磁盘更改；请重新加载后再选择文件操作。", "The displayed AREA TXT changed on disk. Reload it before choosing a file operation."),
        new("area.feedback.draftNamed.title", "新 AREA 草稿已命名", "New AREA draft named"),
        new("area.feedback.draftNamed.message", "请填写至少一个有效 AREA，然后保存；尚未创建或应用本机 TXT 文件。", "Enter at least one valid AREA value and save it; no local TXT file has been created or applied yet."),
        new("area.feedback.savedAs.title", "AREA 配置已另存为", "AREA profile saved as"),
        new("area.feedback.savedAs.message", "已创建 {0}.txt；原文件和当前应用范围均未改变。", "Created {0}.txt; the original file and currently applied scope are unchanged."),
        new("area.feedback.renamed.title", "AREA 配置已重命名", "AREA profile renamed"),
        new("area.feedback.renamed.message", "{0}.txt 已重命名为 {1}.txt；AREA 内容未改变。", "Renamed {0}.txt to {1}.txt; the AREA content is unchanged."),
        new("area.feedback.deleted.title", "AREA 配置已删除", "AREA profile deleted"),
        new("area.feedback.deleted.message", "{0}.txt 已删除；当前应用范围未改变。", "Deleted {0}.txt; the currently applied scope is unchanged."),
        new("area.feedback.deletedApplied.message", "{0}.txt 已删除；已应用 AREA 快照与当前显示范围仍生效，可按原名另存恢复。", "Deleted {0}.txt; the applied AREA snapshot and current display scope remain active. Save under the original name to restore the file."),
        new("area.feedback.savedNotApplied.title", "AREA 配置已保存但范围未应用", "AREA profile saved but scope not applied"),
        new("area.feedback.savedNotApplied.message", "配置已保存，但显示范围未改变。{0}", "The profile was saved, but the display scope did not change. Check the applied-profile marker and retry.{0}"),
        new("area.feedback.applied.title", "AREA 配置已应用", "AREA profile applied"),
        new("area.feedback.applied.message", "概览、需求系列和资格审计已清除冻结游标并从第一页重新读取；错误检索与接入告警未改变。", "Overview, demand series, and eligibility audit cleared their frozen cursors and reloaded from the first page; error search and ingest attention are unchanged."),
        new("area.feedback.allApplied.title", "已应用全部 AREA", "All AREA values applied"),
        new("area.feedback.allApplied.message", "本机范围标记已持久化；三个 AREA 相关只读视图已从第一页重新读取。", "The local scope marker was persisted; the three AREA-aware read-only views reloaded from the first page."),
        new("area.feedback.operationFailed.title", "无法完成 AREA 配置操作", "Could not complete the AREA profile operation"),
        new("area.feedback.operationFailed.message", "操作未完成；请检查输入、文件权限或当前磁盘版本后重试。{0}", "The operation did not complete. Check the input, file permissions, or current disk version and try again.{0}"),
        new("area.apply.blocked.deleted", "文件已删除 · 当前显示范围仍生效", "File deleted · Current display scope remains active"),
        new("area.apply.blocked.invalid", "内容非法不可应用 · 当前显示范围保持不变", "Invalid content cannot be applied · Current display scope is unchanged"),
        new("area.apply.blocked.other", "{0}", "The AREA profile cannot be applied in its current state{0}"),
        new("area.apply.automation.applied", "选中 AREA 配置已是当前显示范围", "The selected AREA profile is already the current display scope"),
        new("area.apply.automation.reapply", "重新应用选中 AREA 配置", "Reapply the selected AREA profile"),
        new("area.apply.automation.apply", "应用选中 AREA 配置", "Apply the selected AREA profile"),
        new("area.diagnostic.invalidActiveMarker", "已应用 AREA 标记无效。", "The applied AREA marker is invalid."),
        new("area.diagnostic.profileNameRequired", "必须提供 AREA 配置名称。", "An AREA profile name is required."),
        new("area.diagnostic.unsafeProfileName", "AREA 配置名称不能用作本机文件名。", "The AREA profile name cannot be used as a local file name."),
        new("area.diagnostic.invalidMesArea", "AREA 必须匹配 ^[A-Z][1-9][0-9]?-[1-9][0-9]?$。", "AREA must match ^[A-Z][1-9][0-9]?-[1-9][0-9]?$."),
        new("area.diagnostic.duplicateMesArea", "AREA“{0}”重复出现。", "AREA '{0}' appears more than once."),
        new("area.diagnostic.tooManyMesAreas", "最多支持 {0} 个不同 AREA。", "At most {0} distinct AREA values are supported."),
        new("area.diagnostic.emptyAreaSet", "AREA 配置必须包含至少一个有效 AREA。", "The AREA profile must contain at least one valid AREA value."),
        new("area.diagnostic.invalidUtf8", "文件不是有效的 UTF-8。", "The file is not valid UTF-8."),
        new("area.diagnostic.profileNotFound", "找不到 AREA 配置文件。", "The AREA profile file was not found."),
        new("area.diagnostic.profileAlreadyExists", "已存在同名 AREA 配置。", "An AREA profile with this name already exists."),
        new("area.diagnostic.profileChangedOnDisk", "AREA 配置已在磁盘更改。", "The AREA profile changed on disk."),
        new("area.diagnostic.profileNameUnchanged", "新的 AREA 配置名称未改变。", "The new AREA profile name is unchanged."),
        new("area.diagnostic.activeMarkerWriteFailed", "无法写入已应用 AREA 标记。", "The applied AREA marker could not be written."),
        new("area.diagnostic.unknown", "{0}: {1}", "AREA profile diagnostic {0}.{1}"),
        new("area.operation.confirmRenameAutomation", "确认重命名 {0}.txt", "Confirm rename {0}.txt"),
        new("area.operation.confirmDeleteAutomation", "确认删除 {0}.txt", "Confirm delete {0}.txt"),
        new("area.operation.confirmCreateAutomation", "确认新建 AREA 配置名称", "Confirm new AREA profile name"),
        new("area.operation.confirmSaveAsAutomation", "确认另存 AREA TXT 配置", "Confirm saving the AREA TXT profile as a new file"),
        new("area.operation.confirmFileAutomation", "确认 AREA 文件操作", "Confirm AREA file operation"),
        new("area.operation.createPrompt", "命名新 AREA 配置", "Name the new AREA profile"),
        new("area.operation.saveAsPrompt", "将 AREA 配置另存为新文件", "Save the AREA profile as a new file"),
        new("area.selector.all", "全部 AREA", "All AREA"),
        new("area.selector.valid", "{0} · {1:N0}", "{0} · {1:N0}"),
        new("area.selector.invalid", "{0} · 无效", "{0} · Invalid"),
        new("area.selector.allAutomation", "AREA 筛选：全部 AREA", "AREA filter: All AREA"),
        new("area.selector.validAutomation", "AREA 筛选：{0}；{1:N0} 个 AREA", "AREA filter: {0}; {1:N0} AREAs"),
        new("area.selector.invalidAutomation", "AREA 筛选：{0}；配置无效，不能应用", "AREA filter: {0}; invalid profile cannot be applied"),
        new("area.editor.currentScopeAutomation", "当前 AREA 范围：{0}", "Current AREA scope: {0}"),
        new("area.editor.currentFileAutomation", "当前 AREA TXT 文件：{0}", "Current AREA TXT file: {0}"),
        new("area.editor.validCountAutomation", "AREA 配置有效数量：{0}", "Valid AREA configuration count: {0}"),
        new("area.editor.validationAutomation", "AREA 配置校验：{0}", "AREA profile validation: {0}"),
        new("area.editor.diskStateAutomation", "AREA 配置保存状态：{0}", "AREA profile save state: {0}"),
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries =>
        [.. AreaEntries, .. WatchLegacyGeneratedText.AreaFilterEntries];

    private string Get(string id) => Text(AreaEntries.Single(entry => entry.SemanticId == id));

    public string PageTitle => Get("area.page.title");
    public string PageSubtitle => Get("area.page.subtitle");
    public string ListTitle => Get("area.list.title");
    public string ListNotLoaded => Get("area.list.notLoaded");
    public string OpenFolder => Get("area.list.openFolder");
    public string NewProfile => Get("area.list.new");
    public string SaveAs => Get("area.list.saveAs");
    public string Rename => Get("area.list.rename");
    public string Delete => Get("area.list.delete");
    public string Confirm => Get("area.operation.confirm");
    public string Cancel => Get("area.operation.cancel");
    public string ConfirmName => Get("area.operation.confirmName");
    public string ConfirmDelete => Get("area.operation.confirmDelete");
    public string LocalOnly => Get("area.localOnly");
    public string SaveDraftAs => Get("area.draft.saveAs");
    public string Apply => Get("area.apply.apply");
    public string Applied => Get("area.apply.applied");
    public string Reapply => Get("area.apply.reapply");
    public string NewProfileTitle => Get("area.editor.new");
    public string NoValidAreas => Get("area.editor.noValid");
    public string StorageCaption => Get("area.editor.storage");
    public string ValidationNotPassed => Get("area.validation.notPassed");
    public string NotSaved => Get("area.disk.notSaved");
    public string ValidationDetails => Get("area.validation.details");
    public string ValidationLine => Get("area.validation.line");
    public string ValidationCode => Get("area.validation.code");
    public string ValidationMessage => Get("area.validation.message");
    public string AppliedBadge => Get("area.row.applied");
    public string Valid => Get("area.row.valid");
    public string Invalid => Get("area.row.invalid");
    public string AllAreas => Get("area.row.all");
    public string Unrestricted => Get("area.row.unrestricted");
    public string DiskConflict => Get("area.disk.conflict");
    public string DeletedDraft => Get("area.disk.deletedDraft");
    public string DeletedApplied => Get("area.disk.deletedApplied");
    public string AutoSavePending => Get("area.disk.pending");
    public string AutoSaved => Get("area.disk.saved");
    public string NoFile => Get("area.disk.noFile");

    internal WatchLocalizedNotificationContent RestorePreviousFailed(string detail) =>
        Feedback(
            "area.feedback.severity.warning",
            "area.feedback.restore.title",
            "area.feedback.restore.message",
            [detail],
            [string.Empty]);

    internal WatchLocalizedNotificationContent ReadProfilesFailed(
        string detail,
        bool preserveAppliedScope) => Feedback(
            "area.feedback.severity.warning",
            "area.feedback.readProfiles.title",
            preserveAppliedScope
                ? "area.feedback.readProfiles.scopeMessage"
                : "area.feedback.readProfiles.listMessage",
            [detail],
            [string.Empty]);

    internal WatchLocalizedNotificationContent ReadAppliedProfileFailed(string detail) =>
        Feedback(
            "area.feedback.severity.warning",
            "area.feedback.readApplied.title",
            "area.feedback.readApplied.message",
            [detail],
            [string.Empty]);

    internal WatchLocalizedNotificationContent DirectoryWatchDegraded(string detail) =>
        Feedback(
            "area.feedback.severity.warning",
            "area.feedback.directoryWatch.title",
            "area.feedback.directoryWatch.message",
            [detail],
            [string.Empty]);

    internal WatchLocalizedNotificationContent DirectoryOpened(
        string directory,
        bool launchedFileManager) => Feedback(
            "area.feedback.severity.information",
            launchedFileManager
                ? "area.feedback.directoryOpened.title"
                : "area.feedback.directoryPrepared.title",
            launchedFileManager
                ? "area.feedback.directoryOpened.message"
                : "area.feedback.directoryPrepared.message",
            [directory],
            [directory]);

    internal WatchLocalizedNotificationContent ReadProfileFailed(string detail) => Feedback(
        "area.feedback.severity.error",
        "area.feedback.readProfile.title",
        "area.feedback.readProfile.message",
        [detail],
        [string.Empty],
        includeReturnAction: true);

    internal WatchLocalizedNotificationContent ConfirmAppliedScopeFailed(string detail) => Feedback(
        "area.feedback.severity.error",
        "area.feedback.confirmApplied.title",
        "area.feedback.confirmApplied.message",
        [detail],
        [string.Empty],
        includeReturnAction: true);

    internal WatchLocalizedNotificationContent PrepareOperationMissingVersion() => Feedback(
        "area.feedback.severity.error",
        "area.feedback.prepareOperation.title",
        "area.feedback.prepareOperation.missingVersion",
        [],
        [],
        includeReturnAction: true);

    internal WatchLocalizedNotificationContent ProfileChangedOnDisk() => Feedback(
        "area.feedback.severity.error",
        "area.feedback.changedOnDisk.title",
        "area.feedback.changedOnDisk.message",
        [],
        [],
        includeReturnAction: true);

    internal WatchLocalizedNotificationContent PrepareOperationFailed(string detail) => Feedback(
        "area.feedback.severity.error",
        "area.feedback.prepareOperation.title",
        "area.feedback.prepareOperation.message",
        [detail],
        [string.Empty],
        includeReturnAction: true);

    internal WatchLocalizedNotificationContent DraftNamed() => Feedback(
        "area.feedback.severity.information",
        "area.feedback.draftNamed.title",
        "area.feedback.draftNamed.message",
        [],
        []);

    internal WatchLocalizedNotificationContent SavedAs(string profileName) => Feedback(
        "area.feedback.severity.success",
        "area.feedback.savedAs.title",
        "area.feedback.savedAs.message",
        [profileName],
        [profileName]);

    internal WatchLocalizedNotificationContent Renamed(
        string previousProfileName,
        string newProfileName) => Feedback(
            "area.feedback.severity.success",
            "area.feedback.renamed.title",
            "area.feedback.renamed.message",
            [previousProfileName, newProfileName],
            [previousProfileName, newProfileName]);

    internal WatchLocalizedNotificationContent Deleted(
        string profileName,
        bool appliedProfileWasDeleted) => Feedback(
            "area.feedback.severity.success",
            "area.feedback.deleted.title",
            appliedProfileWasDeleted
                ? "area.feedback.deletedApplied.message"
                : "area.feedback.deleted.message",
            [profileName],
            [profileName]);

    internal WatchLocalizedNotificationContent SavedButNotApplied(string detail) => Feedback(
        "area.feedback.severity.error",
        "area.feedback.savedNotApplied.title",
        "area.feedback.savedNotApplied.message",
        [detail],
        [string.Empty],
        includeReturnAction: true);

    internal WatchLocalizedNotificationContent ProfileApplied() => Feedback(
        "area.feedback.severity.success",
        "area.feedback.applied.title",
        "area.feedback.applied.message",
        [],
        []);

    internal WatchLocalizedNotificationContent AllAreasApplied() => Feedback(
        "area.feedback.severity.success",
        "area.feedback.allApplied.title",
        "area.feedback.allApplied.message",
        [],
        []);

    internal WatchLocalizedNotificationContent OperationFailed(string detail) => Feedback(
        "area.feedback.severity.error",
        "area.feedback.operationFailed.title",
        "area.feedback.operationFailed.message",
        [detail],
        [string.Empty],
        includeReturnAction: true);

    private static WatchLocalizedNotificationContent Feedback(
        string severityId,
        string titleId,
        string messageId,
        object?[] simplifiedChineseArguments,
        object?[] englishArguments,
        bool includeReturnAction = false) => new(
            Pair(severityId, [], []),
            Pair(titleId, [], []),
            Pair(messageId, simplifiedChineseArguments, englishArguments),
            includeReturnAction
                ? Pair("area.feedback.action.return", [], [])
                : null);

    private static WatchLocalizedText Pair(
        string id,
        object?[] simplifiedChineseArguments,
        object?[] englishArguments)
    {
        var entry = AreaEntries.Single(candidate => candidate.SemanticId == id);
        return new WatchLocalizedText(
            string.Format(
                CultureInfo.InvariantCulture,
                entry.SimplifiedChinese,
                simplifiedChineseArguments),
            string.Format(
                CultureInfo.InvariantCulture,
                entry.English,
                englishArguments));
    }

    public string FileSummary(int files, int invalid) => Format(WatchLegacyGeneratedText.AreaFilter001, new object?[] { files, invalid }, new object?[] { files.ToString("N0", CultureInfo.GetCultureInfo("en-US")), invalid.ToString("N0", CultureInfo.GetCultureInfo("en-US")) });

    public string FilteredSummary(int visible, int files, int invalid) => Format(WatchLegacyGeneratedText.AreaFilter002, new object?[] { visible, files, invalid }, new object?[] { visible, files, invalid });

    public string AreaCount(int count) => Format(WatchLegacyGeneratedText.AreaFilter003, new object?[] { count }, new object?[] { count });

    public string MissingAttention => Select(WatchLegacyGeneratedText.AreaFilter004);
    public string AppliedInvalidAttention => Select(WatchLegacyGeneratedText.AreaFilter005);
    public string InvalidAttention => Select(WatchLegacyGeneratedText.AreaFilter006);
    public string ReapplyAttention => Select(WatchLegacyGeneratedText.AreaFilter007);
    public string Modified(string? time) => Format(WatchLegacyGeneratedText.AreaFilter008, new object?[] { time }, new object?[] { time });
    public string AppliedSnapshot => Select(WatchLegacyGeneratedText.AreaFilter009);
    public string FileCountMetadata(int count, string suffix) =>
        $"{AreaCount(count)} · {suffix}";
    public string ValidCount(int count) => Format(WatchLegacyGeneratedText.AreaFilter010, new object?[] { count }, new object?[] { count });
    public string InvalidCount(int count) => Format(WatchLegacyGeneratedText.AreaFilter011, new object?[] { count }, new object?[] { count });
    public string ValidFormat(int bytes) => Format(WatchLegacyGeneratedText.AreaFilter012, new object?[] { bytes }, new object?[] { bytes });
    public string InvalidFormat(int count) => Format(WatchLegacyGeneratedText.AreaFilter013, new object?[] { count }, new object?[] { count });
    public string AllAreasValidation => Select(WatchLegacyGeneratedText.AreaFilter014);
    public string LocalizeApplyBlockedReason(string? chineseReason) => chineseReason switch
    {
        null => string.Empty,
        "文件已删除 · 当前显示范围仍生效" => Get("area.apply.blocked.deleted"),
        "内容非法不可应用 · 当前显示范围保持不变" => Get("area.apply.blocked.invalid"),
        _ => Format(
            AreaEntries.Single(entry => entry.SemanticId == "area.apply.blocked.other"),
            [chineseReason],
            [string.Empty]),
    };

    public string CurrentApplied(string summary, string? time = null) => Format(WatchLegacyGeneratedText.AreaFilter015, new object?[] { summary, (time is null ? string.Empty : $" · {time}") }, new object?[] { summary, (time is null ? string.Empty : $" · {time}") });

    public string RenamePrompt(string? profileName) => Format(WatchLegacyGeneratedText.AreaFilter016, new object?[] { profileName }, new object?[] { profileName });
    public string DeletePrompt(string? profileName) => Format(WatchLegacyGeneratedText.AreaFilter017, new object?[] { profileName }, new object?[] { profileName });
    public string ConfirmRenameAutomation(string profileName) => string.Format(Get("area.operation.confirmRenameAutomation"), profileName);
    public string ConfirmDeleteAutomation(string profileName) => string.Format(Get("area.operation.confirmDeleteAutomation"), profileName);
    public string ConfirmCreateAutomation => Get("area.operation.confirmCreateAutomation");
    public string ConfirmSaveAsAutomation => Get("area.operation.confirmSaveAsAutomation");
    public string ConfirmFileAutomation => Get("area.operation.confirmFileAutomation");
    public string CreatePrompt => Get("area.operation.createPrompt");
    public string SaveAsPrompt => Get("area.operation.saveAsPrompt");
    public string SelectorDisplay(string? profileName, int areaCount, bool isValid) =>
        profileName is null
            ? Get("area.selector.all")
            : isValid
                ? Format(
                    AreaEntries.Single(entry => entry.SemanticId == "area.selector.valid"),
                    [profileName, areaCount],
                    [profileName, areaCount])
                : Format(
                    AreaEntries.Single(entry => entry.SemanticId == "area.selector.invalid"),
                    [profileName],
                    [profileName]);
    public string SelectorAutomation(string? profileName, int areaCount, bool isValid) =>
        profileName is null
            ? Get("area.selector.allAutomation")
            : isValid
                ? Format(
                    AreaEntries.Single(entry => entry.SemanticId == "area.selector.validAutomation"),
                    [profileName, areaCount],
                    [profileName, areaCount])
                : Format(
                    AreaEntries.Single(entry => entry.SemanticId == "area.selector.invalidAutomation"),
                    [profileName],
                    [profileName]);
    public string CurrentScopeAutomation(string value) => Format(
        AreaEntries.Single(entry => entry.SemanticId == "area.editor.currentScopeAutomation"),
        [value],
        [value]);
    public string CurrentFileAutomation(string value) => Format(
        AreaEntries.Single(entry => entry.SemanticId == "area.editor.currentFileAutomation"),
        [value],
        [value]);
    public string ValidCountAutomation(string value) => Format(
        AreaEntries.Single(entry => entry.SemanticId == "area.editor.validCountAutomation"),
        [value],
        [value]);
    public string ValidationSummaryAutomation(string value) => Format(
        AreaEntries.Single(entry => entry.SemanticId == "area.editor.validationAutomation"),
        [value],
        [value]);
    public string DiskStateAutomation(string value) => Format(
        AreaEntries.Single(entry => entry.SemanticId == "area.editor.diskStateAutomation"),
        [value],
        [value]);
    public string FileNameTooltip => Select(WatchLegacyGeneratedText.AreaFilter018);
    public string SearchAutomation => Select(WatchLegacyGeneratedText.AreaFilter019);
    public string ListAutomation => Select(WatchLegacyGeneratedText.AreaFilter020);
    public string MasterAutomation => Select(WatchLegacyGeneratedText.AreaFilter021);
    public string EditorAutomation => Select(WatchLegacyGeneratedText.AreaFilter022);
    public string TargetNameAutomation => Select(WatchLegacyGeneratedText.AreaFilter023);
    public string ValidationAutomation => Select(WatchLegacyGeneratedText.AreaFilter024);
    public string ApplyAutomation(
        WatchAreaProfileApplyAction action,
        string? blockedReason)
    {
        var label = action switch
        {
            WatchAreaProfileApplyAction.Applied => Get("area.apply.automation.applied"),
            WatchAreaProfileApplyAction.Reapply => Get("area.apply.automation.reapply"),
            _ => Get("area.apply.automation.apply"),
        };
        var localizedReason = LocalizeApplyBlockedReason(blockedReason);
        return string.IsNullOrEmpty(localizedReason)
            ? label
            : Format(WatchLegacyGeneratedText.AreaFilter025, new object?[] { label, localizedReason }, new object?[] { label, localizedReason });
    }

    public string DiagnosticMessage(WatchAreaFilterProfileDiagnostic diagnostic)
    {
        return diagnostic.Code switch
        {
            WatchAreaFilterProfileDiagnosticCodes.InvalidActiveMarker => Get("area.diagnostic.invalidActiveMarker"),
            WatchAreaFilterProfileDiagnosticCodes.ProfileNameRequired => Get("area.diagnostic.profileNameRequired"),
            WatchAreaFilterProfileDiagnosticCodes.UnsafeProfileName => Get("area.diagnostic.unsafeProfileName"),
            WatchAreaFilterProfileDiagnosticCodes.InvalidMesArea => Get("area.diagnostic.invalidMesArea"),
            WatchAreaFilterProfileDiagnosticCodes.DuplicateMesArea => Format(
                AreaEntries.Single(entry => entry.SemanticId == "area.diagnostic.duplicateMesArea"),
                [diagnostic.Value],
                [diagnostic.Value]),
            WatchAreaFilterProfileDiagnosticCodes.TooManyMesAreas => Format(
                AreaEntries.Single(entry => entry.SemanticId == "area.diagnostic.tooManyMesAreas"),
                [WatchAreaFilterProfileParser.MaximumAreaCount],
                [WatchAreaFilterProfileParser.MaximumAreaCount]),
            WatchAreaFilterProfileDiagnosticCodes.EmptyAreaSet => Get("area.diagnostic.emptyAreaSet"),
            WatchAreaFilterProfileDiagnosticCodes.InvalidUtf8 => Get("area.diagnostic.invalidUtf8"),
            WatchAreaFilterProfileDiagnosticCodes.ProfileNotFound => Get("area.diagnostic.profileNotFound"),
            WatchAreaFilterProfileDiagnosticCodes.ProfileAlreadyExists => Get("area.diagnostic.profileAlreadyExists"),
            WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk => Get("area.diagnostic.profileChangedOnDisk"),
            WatchAreaFilterProfileDiagnosticCodes.ProfileNameUnchanged => Get("area.diagnostic.profileNameUnchanged"),
            WatchAreaFilterProfileDiagnosticCodes.ActiveMarkerWriteFailed => Get("area.diagnostic.activeMarkerWriteFailed"),
            _ => Format(
                AreaEntries.Single(entry => entry.SemanticId == "area.diagnostic.unknown"),
                [diagnostic.Code, diagnostic.Message],
                [diagnostic.Code, string.Empty]),
        };
    }
}
