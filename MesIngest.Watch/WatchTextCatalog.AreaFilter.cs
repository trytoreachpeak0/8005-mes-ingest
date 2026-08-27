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

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => AreaEntries;

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

    public string FileSummary(int files, int invalid) => Language == WatchDisplayLanguage.English
        ? $"{files.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} files · {invalid.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} need repair"
        : $"{files:N0} 个文件 · {invalid:N0} 个需要修复";

    public string FilteredSummary(int visible, int files, int invalid) => Language == WatchDisplayLanguage.English
        ? $"Showing {visible:N0} / {files:N0} profiles · {invalid:N0} need repair"
        : $"显示 {visible:N0} / {files:N0} 个配置 · {invalid:N0} 个需要修复";

    public string AreaCount(int count) => Language == WatchDisplayLanguage.English
        ? $"{count:N0} AREA values"
        : $"{count:N0} 个 AREA";

    public string MissingAttention => Language == WatchDisplayLanguage.English
        ? "File deleted · Scope remains active"
        : "文件已删除 · 范围仍生效";
    public string AppliedInvalidAttention => Language == WatchDisplayLanguage.English
        ? "Invalid content · Reapply required"
        : "内容非法 · 待重新应用";
    public string InvalidAttention => Language == WatchDisplayLanguage.English
        ? "Invalid content · Repair required"
        : "内容非法 · 需修复";
    public string ReapplyAttention => Language == WatchDisplayLanguage.English
        ? "Reapply required"
        : "待重新应用";
    public string Modified(string? time) => Language == WatchDisplayLanguage.English
        ? $"Modified {time}"
        : $"{time} 修改";
    public string AppliedSnapshot => Language == WatchDisplayLanguage.English
        ? "applied snapshot"
        : "已应用快照";
    public string FileCountMetadata(int count, string suffix) =>
        $"{AreaCount(count)} · {suffix}";
    public string ValidCount(int count) => Language == WatchDisplayLanguage.English
        ? $"✓ {count:N0} valid AREA values"
        : $"✓ {count:N0} 个有效 AREA";
    public string InvalidCount(int count) => Language == WatchDisplayLanguage.English
        ? $"{count:N0} issues · Invalid"
        : $"{count:N0} 项问题 · 无效";
    public string ValidFormat(int bytes) => Language == WatchDisplayLanguage.English
        ? $"✓ Valid format · {bytes:N0} B"
        : $"✓ 格式有效 · {bytes:N0} B";
    public string InvalidFormat(int count) => Language == WatchDisplayLanguage.English
        ? $"{count:N0} issues · Invalid content cannot be applied"
        : $"{count:N0} 项问题 · 非法内容不可应用";
    public string AllAreasValidation => Language == WatchDisplayLanguage.English
        ? "Shows all AREA values; no TXT filter is applied"
        : "显示所有 AREA，不应用 TXT 筛选";
    public string LocalizeApplyBlockedReason(string? chineseReason) => chineseReason switch
    {
        null => string.Empty,
        "文件已删除 · 当前显示范围仍生效" when Language == WatchDisplayLanguage.English =>
            "File deleted · Current display scope remains active",
        "内容非法不可应用 · 当前显示范围保持不变" when Language == WatchDisplayLanguage.English =>
            "Invalid content cannot be applied · Current display scope is unchanged",
        _ => chineseReason,
    };

    public string CurrentApplied(string summary, string? time = null) => Language == WatchDisplayLanguage.English
        ? $"Currently applied: {summary}{(time is null ? string.Empty : $" · {time}")}"
        : $"当前应用：{summary}{(time is null ? string.Empty : $" · {time}")}";

    public string RenamePrompt(string? profileName) => Language == WatchDisplayLanguage.English
        ? $"Rename “{profileName}.txt”"
        : $"重命名“{profileName}.txt”";
    public string DeletePrompt(string? profileName) => Language == WatchDisplayLanguage.English
        ? $"Confirm deletion of “{profileName}.txt” again; if it is applied, the AREA snapshot and display scope remain active after deletion"
        : $"再次确认删除“{profileName}.txt”；若该配置为当前应用，删除后已应用 AREA 快照与显示范围仍生效";

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
