namespace MesIngest.Watch;

internal sealed class WatchCommonText(WatchDisplayLanguage language)
    : WatchTextCatalogSection(language)
{
    private static readonly WatchTextCatalogEntry SimplifiedChineseNameEntry =
        new("common.language.simplifiedChinese", "简体中文", "简体中文");
    private static readonly WatchTextCatalogEntry EnglishNameEntry =
        new("common.language.english", "English", "English");
    private static readonly WatchTextCatalogEntry UnknownCodeEntry =
        new("common.code.unknown", "未知代码：{0}", "Unknown code: {0}");
    private static readonly WatchTextCatalogEntry SourceNotProvidedEntry =
        new("common.value.sourceNotProvided", "来源未提供", "Not provided by source");
    private static readonly WatchTextCatalogEntry SystemUnknownEntry =
        new("common.value.systemUnknown", "系统未知", "Unknown to system");
    private static readonly WatchTextCatalogEntry NotApplicableEntry =
        new("common.value.notApplicable", "不适用", "Not applicable");
    private static readonly WatchTextCatalogEntry NotLoadedEntry =
        new("common.value.notLoaded", "尚未加载", "Not loaded");
    private static readonly WatchTextCatalogEntry EmptyResultEntry =
        new("common.value.emptyResult", "查询成功但无结果", "Query succeeded; no results");
    private static readonly WatchTextCatalogEntry ReadFailedEntry =
        new("common.value.readFailed", "读取失败", "Read failed");
    private static readonly WatchTextCatalogEntry SecondsAgoEntry =
        new("common.time.secondsAgo", "{0} 秒前", "{0} seconds ago");
    private static readonly WatchTextCatalogEntry MinutesAgoEntry =
        new("common.time.minutesAgo", "{0} 分钟前", "{0} minutes ago");
    private static readonly WatchTextCatalogEntry HoursAgoEntry =
        new("common.time.hoursAgo", "{0} 小时前", "{0} hours ago");

    private static readonly IReadOnlyList<WatchTextCatalogEntry> CatalogEntries =
    [
        SimplifiedChineseNameEntry,
        EnglishNameEntry,
        UnknownCodeEntry,
        SourceNotProvidedEntry,
        SystemUnknownEntry,
        NotApplicableEntry,
        NotLoadedEntry,
        EmptyResultEntry,
        ReadFailedEntry,
        SecondsAgoEntry,
        MinutesAgoEntry,
        HoursAgoEntry,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => CatalogEntries;

    public string SimplifiedChineseLanguageName => Text(SimplifiedChineseNameEntry);

    public string EnglishLanguageName => Text(EnglishNameEntry);

    public string UnknownCodeFormat => Text(UnknownCodeEntry);

    public string SourceNotProvided => Text(SourceNotProvidedEntry);

    public string SystemUnknown => Text(SystemUnknownEntry);

    public string NotApplicable => Text(NotApplicableEntry);

    public string NotLoaded => Text(NotLoadedEntry);

    public string EmptyResult => Text(EmptyResultEntry);

    public string ReadFailed => Text(ReadFailedEntry);

    public string SecondsAgoFormat => Text(SecondsAgoEntry);

    public string MinutesAgoFormat => Text(MinutesAgoEntry);

    public string HoursAgoFormat => Text(HoursAgoEntry);
}

