using System.Globalization;
using System.Text.RegularExpressions;

namespace MesIngest.Watch;

internal sealed record WatchTextCatalogEntry
{
    internal WatchTextCatalogEntry(
        string semanticId,
        string simplifiedChinese,
        string english)
    {
        SemanticId = semanticId;
        SimplifiedChinese = WatchSimplifiedChineseStaticText.Normalize(simplifiedChinese);
        English = english;
    }

    public string SemanticId { get; }

    public string SimplifiedChinese { get; }

    public string English { get; }

    internal string In(WatchDisplayLanguage language) => language switch
    {
        WatchDisplayLanguage.SimplifiedChinese => SimplifiedChinese,
        WatchDisplayLanguage.English => English,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };
}

internal static partial class WatchSimplifiedChineseStaticText
{
    private static readonly Regex CjkSpacingPattern = new(
        @"(?<=[\p{IsCJKUnifiedIdeographs}])\x20(?=[\p{IsCJKUnifiedIdeographs}])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CjkAfterFullWidthParenthesisSpacingPattern = new(
        @"(?<=）)\x20(?=[\p{IsCJKUnifiedIdeographs}，。；：、/])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Replacement)[] TokenReplacements =
    [
        Token("MesIngest Watch", "制造执行系统接入运维台"),
        Token("fingerprint incident", "指纹异常事件"),
        Token("Demand Generation", "运输需求代次"),
        Token("Error Search", "错误检索"),
        Token("HistoryResetAcknowledgement", "历史重置确认"),
        Token("StoragePressurePause", "存储压力暂停"),
        Token("CRITICAL_WARNING", "严重预警"),
        Token("INGEST_NOT_CURRENT", "当前接入数据不可用"),
        Token("PollTrace HighWater", "轮询追踪高水位"),
        Token("LiveMesFieldSet", "实时制造执行系统字段集"),
        Token("ErrorSearchAsOf", "错误检索查询时点"),
        Token("SnapshotReference", "快照引用"),
        Token("ProjectionCommitId", "投影提交标识"),
        Token("ProjectionSequence", "投影序列"),
        Token("ProjectionCommit", "投影提交"),
        Token("PollTraceId", "轮询追踪标识"),
        Token("PollTraceSequence", "轮询追踪序列"),
        Token("PollTrace", "轮询追踪"),
        Token("CatalogRevision", "目录修订号"),
        Token("TransportDemand", "运输需求"),
        Token("DemandSeries", "需求系列"),
        Token("SeriesId", "需求系列标识"),
        Token("DemandId", "运输需求标识"),
        Token("WorkType", "工序类型"),
        Token("MesSourceDate", "来源时间"),
        Token("ObservationOrdinal", "观测序号"),
        Token("EvidenceId", "证据标识"),
        Token("ContentDigest", "内容摘要"),
        Token("ErrorCategory", "错误分类"),
        Token("ErrorCode", "错误码"),
        Token("ProtectionStatus", "保护状态"),
        Token("LastSuccessfulWindow", "最后成功窗口"),
        Token("EarliestAvailableHostUtc", "服务端最早可用时间"),
        Token("HistoryEpochProgress", "历史纪元进度"),
        Token("HistoryEpoch", "历史纪元"),
        Token("HistoryReset", "历史重置"),
        Token("StoragePressure", "存储压力"),
        Token("CurrentReadRestriction", "当前读取限制"),
        Token("LocalAdministration", "本地管理"),
        Token("Reason", "原因"),
        Token("Phase", "阶段"),
        Token("Outcome", "结果"),
        Token("TASK_TYPE", "工序类型"),
        Token("SeriesSequence", "需求系列序列"),
        Token("EventId", "事件标识"),
        Token("OccurredAt", "发生时间"),
        Token("EventType", "事件类型"),
        Token("SubjectKind", "主体类型"),
        Token("SubjectId", "主体标识"),
        Token("PayloadVersion", "负载版本"),
        Token("PayloadJson", "负载结构化原始数据"),
        Token("Assignment", "归属状态"),
        Token("SUBLOT", "子批次"),
        Token("PACKAGE", "封装形式"),
        Token("DATES", "来源时间"),
        Token("AREA", "区域"),
        Token("EQP", "设备"),
        Token("STEP", "下一工序"),
        Token("Inspector", "调查窗口"),
        Token("Endpoint", "服务地址"),
        Token("snapshotReference", "快照引用"),
        Token("snapshot", "快照"),
        Token("canonical", "规范"),
        Token("incident", "异常期间"),
        Token("Tracking", "跟踪中"),
        Token("Archived", "已归档"),
        Token("Dispatch", "调度"),
        Token("Digest", "内容摘要"),
        Token("Catalog", "目录"),
        Token("UTC", "协调世界时"),
        Token("NULL", "空值"),
        Token("row", "行"),
        Token("Windows", "系统"),
        Token("Host", "服务端"),
        Token("Watch", "运维台"),
        Token("Series", "需求系列"),
        Token("Demand", "运输需求"),
        Token("MES", "制造执行系统"),
        Token("English", "英语"),
        Token("JSON", "结构化原始数据"),
        Token("API", "接口"),
        Token("URI", "资源地址"),
        Token("URL", "网址"),
        Token("TXT", "文本"),
        Token("UI", "界面"),
        Token("ID", "标识"),
        Token("rail", "导航栏"),
        Token("epx", "像素"),
        Token("NOT_READABLE", "不可读"),
        Token("READABLE", "可读"),
        Token("VISIBLE", "可见"),
        Token("GONE", "已消失"),
        Token("TRACKING", "跟踪中"),
        Token("ARCHIVED", "已归档"),
        Token("ACTIVE", "活动"),
        Token("ENDED", "已结束"),
        Token("PASS", "通过"),
        Token("FAIL", "未通过"),
        Token("ERROR", "错误"),
        Token("WARNING", "警告"),
        Token("ONLINE", "在线"),
        Token("OFFLINE", "离线"),
        Token("READ_WRITE", "可读写"),
        Token("READ_ONLY", "只读"),
        Token("HEALTHY", "正常"),
        Token("DEGRADED", "降级"),
        Token("CRITICAL", "严重"),
        Token("INSUFFICIENT_DATA", "数据不足"),
    ];

    internal static string Normalize(string value)
    {
        var normalized = value;
        foreach (var (pattern, replacement) in TokenReplacements)
        {
            normalized = pattern.Replace(normalized, replacement);
        }

        normalized = CjkSpacingPattern.Replace(normalized, string.Empty);
        normalized = CjkAfterFullWidthParenthesisSpacingPattern.Replace(normalized, string.Empty);
        return normalized.Replace(
            "错误检索查询时点时",
            "错误检索查询时点",
            StringComparison.Ordinal);
    }

    private static (Regex Pattern, string Replacement) Token(
        string token,
        string replacement) =>
        (new Regex(
            $"(?<![A-Za-z0-9_]){Regex.Escape(token)}(?![A-Za-z0-9_])",
            RegexOptions.CultureInvariant | RegexOptions.Compiled), replacement);
}

internal interface IWatchTextCatalogSection
{
    IReadOnlyList<WatchTextCatalogEntry> Entries { get; }
}

internal abstract class WatchTextCatalogSection(WatchDisplayLanguage language)
    : IWatchTextCatalogSection
{
    protected WatchDisplayLanguage Language { get; } = language;

    internal WatchDisplayLanguage DisplayLanguage => Language;

    public abstract IReadOnlyList<WatchTextCatalogEntry> Entries { get; }

    protected string Text(WatchTextCatalogEntry entry) => entry.In(Language);

    protected string PresentCodeMeaning(WatchCodeMeaning meaning) =>
        Language is WatchDisplayLanguage.SimplifiedChinese && meaning.IsKnown
            ? meaning.Description
            : !meaning.IsKnown && meaning.Description.Contains(meaning.RawCode, StringComparison.Ordinal)
                ? meaning.Description
                : $"{meaning.Description} · {meaning.RawCode}";

    internal string Select(WatchTextCatalogEntry entry) => entry.In(Language);

    internal string Format(
        WatchTextCatalogEntry entry,
        object?[] simplifiedChineseArguments,
        object?[] englishArguments) =>
        string.Format(
            CultureInfo.InvariantCulture,
            entry.In(Language),
            Language == WatchDisplayLanguage.SimplifiedChinese
                ? simplifiedChineseArguments
                : englishArguments);
}

internal enum WatchDisplayValueKind
{
    Present,
    SourceNotProvided,
    SystemUnknown,
    NotApplicable,
    NotLoaded,
    EmptyResult,
    ReadFailed,
}

internal sealed record WatchDisplayValue(
    WatchDisplayValueKind Kind,
    string? RawValue = null,
    string? Failure = null,
    DateTimeOffset? RetainedSuccessfulAt = null)
{
    internal static WatchDisplayValue SourceNotProvided { get; } =
        new(WatchDisplayValueKind.SourceNotProvided);

    internal static WatchDisplayValue SystemUnknown { get; } =
        new(WatchDisplayValueKind.SystemUnknown);

    internal static WatchDisplayValue NotApplicable { get; } =
        new(WatchDisplayValueKind.NotApplicable);

    internal static WatchDisplayValue NotLoaded { get; } =
        new(WatchDisplayValueKind.NotLoaded);

    internal static WatchDisplayValue EmptyResult { get; } =
        new(WatchDisplayValueKind.EmptyResult);

    internal static WatchDisplayValue ReadFailed { get; } =
        new(WatchDisplayValueKind.ReadFailed);

    internal static WatchDisplayValue Present(string rawValue) => new(
        WatchDisplayValueKind.Present,
        string.IsNullOrEmpty(rawValue)
            ? throw new ArgumentException("A present display value cannot be empty.", nameof(rawValue))
            : rawValue);
}

internal sealed record WatchCodeMeaning(
    string Description,
    string RawCode,
    bool IsKnown);

internal enum WatchCountUnit
{
    Items,
    Demands,
    Series,
}

internal sealed class WatchTextCatalog
{
    private static readonly IReadOnlyList<Func<WatchTextCatalog, IWatchTextCatalogSection>>
        SectionSelectors =
        [
            static catalog => catalog.Common,
            static catalog => catalog.Columns,
            static catalog => catalog.Shell,
            static catalog => catalog.Settings,
            static catalog => catalog.Overview,
            static catalog => catalog.DemandSeries,
            static catalog => catalog.ReadabilityAudit,
            static catalog => catalog.ErrorSearch,
            static catalog => catalog.AreaFilter,
            static catalog => catalog.CurrentAttention,
            static catalog => catalog.Inspector,
            static catalog => catalog.Feedback,
        ];

    private static readonly WatchTextCatalog Chinese =
        new(WatchDisplayLanguage.SimplifiedChinese);

    private static readonly WatchTextCatalog English =
        new(WatchDisplayLanguage.English);

    private WatchTextCatalog(WatchDisplayLanguage language)
    {
        Language = language;
        Common = new WatchCommonText(language);
        Columns = new WatchColumnText(language);
        Shell = new WatchShellText(language);
        Settings = new WatchSettingsText(language);
        Overview = new WatchOverviewText(language);
        DemandSeries = new WatchDemandSeriesText(language);
        ReadabilityAudit = new WatchReadabilityAuditText(language);
        ErrorSearch = new WatchErrorSearchText(language);
        AreaFilter = new WatchAreaFilterText(language);
        CurrentAttention = new WatchCurrentAttentionText(language);
        Inspector = new WatchInspectorText(language);
        Feedback = new WatchFeedbackText(language);
    }

    internal WatchDisplayLanguage Language { get; }

    public WatchCommonText Common { get; }

    public WatchColumnText Columns { get; }

    public WatchShellText Shell { get; }

    public WatchSettingsText Settings { get; }

    public WatchOverviewText Overview { get; }

    public WatchDemandSeriesText DemandSeries { get; }

    public WatchReadabilityAuditText ReadabilityAudit { get; }

    public WatchErrorSearchText ErrorSearch { get; }

    public WatchAreaFilterText AreaFilter { get; }

    public WatchCurrentAttentionText CurrentAttention { get; }

    public WatchInspectorText Inspector { get; }

    public WatchFeedbackText Feedback { get; }

    internal static IReadOnlyList<WatchTextCatalogEntry> AllEntries { get; } =
        SectionSelectors
            .SelectMany(selector => selector(Chinese).Entries)
            .ToArray();

    internal static WatchTextCatalog For(WatchDisplayLanguage language) => language switch
    {
        WatchDisplayLanguage.SimplifiedChinese => Chinese,
        WatchDisplayLanguage.English => English,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    internal WatchCodeMeaning DescribeUnknownCode(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        return new WatchCodeMeaning(
            string.Format(
                CultureInfo.InvariantCulture,
                Common.UnknownCodeFormat,
                rawCode),
            rawCode,
            IsKnown: false);
    }

    internal string RenderValue(WatchDisplayValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            WatchDisplayValueKind.Present => value.RawValue
                ?? throw new ArgumentException("A present display value requires a raw value.", nameof(value)),
            WatchDisplayValueKind.SourceNotProvided => Common.SourceNotProvided,
            WatchDisplayValueKind.SystemUnknown => Common.SystemUnknown,
            WatchDisplayValueKind.NotApplicable => Common.NotApplicable,
            WatchDisplayValueKind.NotLoaded => Common.NotLoaded,
            WatchDisplayValueKind.EmptyResult => Common.EmptyResult,
            WatchDisplayValueKind.ReadFailed => Common.ReadFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, null),
        };
    }

    internal string FormatAbsoluteTime(DateTimeOffset value) =>
        WatchTimeDisplay.Format(value);

    internal string FormatRelativeTime(DateTimeOffset observedAt, DateTimeOffset now)
    {
        var elapsed = now - observedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                Common.SecondsAgoFormat,
                Math.Max(0, (int)Math.Floor(elapsed.TotalSeconds)));
        }

        if (elapsed.TotalHours < 1)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                Common.MinutesAgoFormat,
                Math.Max(1, (int)Math.Floor(elapsed.TotalMinutes)));
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            Common.HoursAgoFormat,
            Math.Max(1, (int)Math.Floor(elapsed.TotalHours)));
    }

    internal string FormatCount(long value, WatchCountUnit unit)
    {
        var culture = Language == WatchDisplayLanguage.SimplifiedChinese
            ? CultureInfo.GetCultureInfo("zh-CN")
            : CultureInfo.GetCultureInfo("en-US");
        var label = unit switch
        {
            WatchCountUnit.Items => Common.ItemCountUnit,
            WatchCountUnit.Demands => Common.DemandCountUnit,
            WatchCountUnit.Series => Common.SeriesCountUnit,
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
        };
        return $"{value.ToString("N0", culture)} {label}";
    }
}

