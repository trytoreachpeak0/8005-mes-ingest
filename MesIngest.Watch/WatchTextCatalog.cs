using System.Globalization;

namespace MesIngest.Watch;

internal sealed record WatchTextCatalogEntry(
    string SemanticId,
    string SimplifiedChinese,
    string English)
{
    internal string In(WatchDisplayLanguage language) => language switch
    {
        WatchDisplayLanguage.SimplifiedChinese => SimplifiedChinese,
        WatchDisplayLanguage.English => English,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };
}

internal interface IWatchTextCatalogSection
{
    IReadOnlyList<WatchTextCatalogEntry> Entries { get; }
}

internal abstract class WatchTextCatalogSection(WatchDisplayLanguage language)
    : IWatchTextCatalogSection
{
    protected WatchDisplayLanguage Language { get; } = language;

    public abstract IReadOnlyList<WatchTextCatalogEntry> Entries { get; }

    protected string Text(WatchTextCatalogEntry entry) => entry.In(Language);
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
        value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

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
        var label = (Language, unit) switch
        {
            (WatchDisplayLanguage.SimplifiedChinese, WatchCountUnit.Items) => "项",
            (WatchDisplayLanguage.SimplifiedChinese, WatchCountUnit.Demands) => "个 Demand",
            (WatchDisplayLanguage.SimplifiedChinese, WatchCountUnit.Series) => "个 Series",
            (WatchDisplayLanguage.English, WatchCountUnit.Items) => "items",
            (WatchDisplayLanguage.English, WatchCountUnit.Demands) => "Demands",
            (WatchDisplayLanguage.English, WatchCountUnit.Series) => "Series",
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
        };
        return $"{value.ToString("N0", culture)} {label}";
    }
}

