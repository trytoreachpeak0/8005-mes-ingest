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
        "文件已删除 · 当前显示范围仍生效" when Language == WatchDisplayLanguage.English =>
            "File deleted · Current display scope remains active",
        "内容非法不可应用 · 当前显示范围保持不变" when Language == WatchDisplayLanguage.English =>
            "Invalid content cannot be applied · Current display scope is unchanged",
        _ => chineseReason,
    };

    public string CurrentApplied(string summary, string? time = null) => Format(WatchLegacyGeneratedText.AreaFilter015, new object?[] { summary, (time is null ? string.Empty : $" · {time}") }, new object?[] { summary, (time is null ? string.Empty : $" · {time}") });

    public string RenamePrompt(string? profileName) => Format(WatchLegacyGeneratedText.AreaFilter016, new object?[] { profileName }, new object?[] { profileName });
    public string DeletePrompt(string? profileName) => Format(WatchLegacyGeneratedText.AreaFilter017, new object?[] { profileName }, new object?[] { profileName });
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
            WatchAreaProfileApplyAction.Applied when Language == WatchDisplayLanguage.English =>
                "The selected AREA profile is already the current display scope",
            WatchAreaProfileApplyAction.Applied => "选中 AREA 配置已是当前显示范围",
            WatchAreaProfileApplyAction.Reapply when Language == WatchDisplayLanguage.English =>
                "Reapply the selected AREA profile",
            WatchAreaProfileApplyAction.Reapply => "重新应用选中 AREA 配置",
            _ when Language == WatchDisplayLanguage.English => "Apply the selected AREA profile",
            _ => "应用选中 AREA 配置",
        };
        var localizedReason = LocalizeApplyBlockedReason(blockedReason);
        return string.IsNullOrEmpty(localizedReason)
            ? label
            : Format(WatchLegacyGeneratedText.AreaFilter025, new object?[] { label, localizedReason }, new object?[] { label, localizedReason });
    }

    public string DiagnosticMessage(WatchAreaFilterProfileDiagnostic diagnostic)
    {
        if (Language == WatchDisplayLanguage.SimplifiedChinese)
        {
            return diagnostic.Message;
        }

        return diagnostic.Code switch
        {
            WatchAreaFilterProfileDiagnosticCodes.ProfileNameRequired => "An AREA profile name is required.",
            WatchAreaFilterProfileDiagnosticCodes.UnsafeProfileName => "The AREA profile name cannot be used as a local file name.",
            WatchAreaFilterProfileDiagnosticCodes.InvalidMesArea => "AREA must match ^[A-Z][1-9][0-9]?-[1-9][0-9]?$.",
            WatchAreaFilterProfileDiagnosticCodes.DuplicateMesArea => $"AREA '{diagnostic.Value}' appears more than once.",
            WatchAreaFilterProfileDiagnosticCodes.TooManyMesAreas => $"At most {WatchAreaFilterProfileParser.MaximumAreaCount} distinct AREA values are supported.",
            WatchAreaFilterProfileDiagnosticCodes.EmptyAreaSet => "The AREA profile must contain at least one valid AREA value.",
            WatchAreaFilterProfileDiagnosticCodes.InvalidUtf8 => "The file is not valid UTF-8.",
            WatchAreaFilterProfileDiagnosticCodes.ProfileNotFound => "The AREA profile file was not found.",
            WatchAreaFilterProfileDiagnosticCodes.ProfileAlreadyExists => "An AREA profile with this name already exists.",
            WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk => "The AREA profile changed on disk.",
            WatchAreaFilterProfileDiagnosticCodes.ProfileNameUnchanged => "The new AREA profile name is unchanged.",
            WatchAreaFilterProfileDiagnosticCodes.ActiveMarkerWriteFailed => "The applied AREA marker could not be written.",
            _ => $"{diagnostic.Code}: {diagnostic.Message}",
        };
    }
}
