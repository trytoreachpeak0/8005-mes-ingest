using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal sealed partial class WatchErrorSearchText
{
    private static WatchTextCatalogEntry Entry(string id, string zh, string en) =>
        new($"error-search.{id}", zh, en);

    private static readonly WatchTextCatalogEntry PageTitleEntry =
        Entry("page-title", "错误检索", "Error search");
    private static readonly WatchTextCatalogEntry CategoryTitleEntry =
        Entry("category-title", "错误分类", "Error categories");
    private static readonly WatchTextCatalogEntry CategoryHelpEntry =
        Entry("category-help", "选择一个或多个分类，再应用条件缩小中间结果；计数来自 Host 冻结快照。", "Select one or more categories, then apply the filters; counts come from the frozen Host snapshot.");
    private static readonly WatchTextCatalogEntry CategorySearchEntry =
        Entry("category-search", "搜索分类或错误码", "Search categories or error codes");
    private static readonly WatchTextCatalogEntry ActivityFacetTitleEntry =
        Entry("activity-facet-title", "活动状态精确分面", "Exact activity-state facets");
    private static readonly WatchTextCatalogEntry ResultsTitleEntry =
        Entry("results-title", "去重需求系列", "Distinct demand series");
    private static readonly WatchTextCatalogEntry ClearFiltersEntry =
        Entry("clear-filters", "清除条件", "Clear filters");
    private static readonly WatchTextCatalogEntry ErrorCodeEntry =
        Entry("error-code", "错误码", "Error code");
    private static readonly WatchTextCatalogEntry ActivityStateEntry =
        Entry("activity-state", "活动状态", "Activity state");
    private static readonly WatchTextCatalogEntry TimeRangeEntry =
        Entry("time-range", "时间范围", "Time range");
    private static readonly WatchTextCatalogEntry ApplyFiltersEntry =
        Entry("apply-filters", "应用条件", "Apply filters");
    private static readonly WatchTextCatalogEntry MoreFiltersEntry =
        Entry("more-filters", "更多条件", "More filters");
    private static readonly WatchTextCatalogEntry PerPageEntry =
        Entry("per-page", "每页", "Per page");
    private static readonly WatchTextCatalogEntry PreviousPageEntry =
        Entry("previous-page", "上一页", "Previous");
    private static readonly WatchTextCatalogEntry NextPageEntry =
        Entry("next-page", "下一页", "Next");
    private static readonly WatchTextCatalogEntry GoToPageEntry =
        Entry("go-to-page", "跳转", "Go");
    private static readonly WatchTextCatalogEntry DetailTitleEntry =
        Entry("detail-title", "命中证据详情", "Matched evidence details");
    private static readonly WatchTextCatalogEntry PeriodsTitleEntry =
        Entry("periods-title", "真正命中的期间", "Actually matched periods");
    private static readonly WatchTextCatalogEntry EvidenceTitleEntry =
        Entry("evidence-title", "可解释证据", "Diagnostic evidence");
    private static readonly WatchTextCatalogEntry RawEvidenceTitleEntry =
        Entry("raw-evidence-title", "受限原始证据（显式按需）", "Restricted raw evidence (explicit on demand)");
    private static readonly WatchTextCatalogEntry RawEvidenceHelpEntry =
        Entry("raw-evidence-help", "仅请求白名单字段；默认不加载，失败不会替换错误历史或详情快照。", "Only allow-listed fields are requested. Nothing is loaded by default, and a failure never replaces the error-history or detail snapshot.");
    private static readonly WatchTextCatalogEntry LoadRawEvidenceEntry =
        Entry("load-raw-evidence", "加载原始证据", "Load raw evidence");
    private static readonly WatchTextCatalogEntry OpenSeriesEntry =
        Entry("open-series", "在需求系列中打开", "Open in demand series");
    private static readonly WatchTextCatalogEntry RequiredFieldMissingEntry = Entry("code-required-field-missing", "必填 MES 字段缺失", "Required MES field is missing");
    private static readonly WatchTextCatalogEntry InvalidFieldFormatEntry = Entry("code-invalid-field-format", "MES 字段格式无效", "MES field does not match its domain format");
    private static readonly WatchTextCatalogEntry DuplicateDemandKeyEntry = Entry("code-duplicate-demand-key", "运输需求业务键重复", "Duplicate transport-demand business key");
    private static readonly WatchTextCatalogEntry MultipleWorkTypesEntry = Entry("code-multiple-work-types", "SUBLOT 同时属于多个 WorkType", "SUBLOT appears in multiple WorkTypes");
    private static readonly WatchTextCatalogEntry LongGoneVisibleEntry = Entry("code-long-gone-visible", "已归档需求系列再次出现在 MES", "Archived demand series became visible in MES again");
    private static readonly WatchTextCatalogEntry DataCompletenessEntry = Entry("category-data-completeness", "数据完整性", "Data completeness");
    private static readonly WatchTextCatalogEntry DataFormatEntry = Entry("category-data-format", "数据格式", "Data format");
    private static readonly WatchTextCatalogEntry ObservationConflictEntry = Entry("category-observation-conflict", "观测冲突", "Observation conflict");
    private static readonly WatchTextCatalogEntry LifecycleConflictEntry = Entry("category-lifecycle-conflict", "生命周期冲突", "Lifecycle conflict");
    private static readonly WatchTextCatalogEntry ActiveEntry = Entry("activity-active", "活动中", "Active");
    private static readonly WatchTextCatalogEntry EndedEntry = Entry("activity-ended", "已结束", "Ended");
    private static readonly WatchTextCatalogEntry Last24HoursEntry = Entry("window-last-24-hours", "最近 24 小时", "Last 24 hours");
    private static readonly WatchTextCatalogEntry Last7DaysEntry = Entry("window-last-7-days", "最近 7 天", "Last 7 days");
    private static readonly WatchTextCatalogEntry Last15DaysEntry = Entry("window-last-15-days", "最近 15 天", "Last 15 days");
    private static readonly WatchTextCatalogEntry AllHistoryEntry = Entry("window-all-history", "全部历史", "All history");
    private static readonly WatchTextCatalogEntry CustomWindowEntry = Entry("window-custom", "自定义窗口", "Custom window");
    private static readonly WatchTextCatalogEntry UnknownErrorCodeEntry = Entry("unknown-error-code", "未知错误码：{0}", "Unknown error code: {0}");
    private static readonly WatchTextCatalogEntry UnknownCategoryEntry = Entry("unknown-category", "未知分类：{0}", "Unknown category: {0}");
    private static readonly WatchTextCatalogEntry UnknownActivityStateEntry = Entry("unknown-activity-state", "未知状态：{0}", "Unknown state: {0}");

    private static readonly IReadOnlyList<WatchTextCatalogEntry> CatalogEntries =
    [
        PageTitleEntry, CategoryTitleEntry, CategoryHelpEntry, CategorySearchEntry,
        ActivityFacetTitleEntry, ResultsTitleEntry, ClearFiltersEntry, ErrorCodeEntry,
        ActivityStateEntry, TimeRangeEntry, ApplyFiltersEntry, MoreFiltersEntry,
        PerPageEntry, PreviousPageEntry, NextPageEntry, GoToPageEntry, DetailTitleEntry,
        PeriodsTitleEntry, EvidenceTitleEntry, RawEvidenceTitleEntry, RawEvidenceHelpEntry,
        LoadRawEvidenceEntry, OpenSeriesEntry,
        RequiredFieldMissingEntry, InvalidFieldFormatEntry, DuplicateDemandKeyEntry,
        MultipleWorkTypesEntry, LongGoneVisibleEntry, DataCompletenessEntry,
        DataFormatEntry, ObservationConflictEntry, LifecycleConflictEntry,
        ActiveEntry, EndedEntry, Last24HoursEntry, Last7DaysEntry, Last15DaysEntry,
        AllHistoryEntry, CustomWindowEntry, UnknownErrorCodeEntry,
        UnknownCategoryEntry, UnknownActivityStateEntry,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => CatalogEntries;

    public string PageTitle => Text(PageTitleEntry);
    public string CategoryTitle => Text(CategoryTitleEntry);
    public string CategoryHelp => Text(CategoryHelpEntry);
    public string CategorySearch => Text(CategorySearchEntry);
    public string ActivityFacetTitle => Text(ActivityFacetTitleEntry);
    public string ResultsTitle => Text(ResultsTitleEntry);
    public string ClearFilters => Text(ClearFiltersEntry);
    public string ErrorCode => Text(ErrorCodeEntry);
    public string ActivityState => Text(ActivityStateEntry);
    public string TimeRange => Text(TimeRangeEntry);
    public string ApplyFilters => Text(ApplyFiltersEntry);
    public string MoreFilters => Text(MoreFiltersEntry);
    public string PerPage => Text(PerPageEntry);
    public string PreviousPage => Text(PreviousPageEntry);
    public string NextPage => Text(NextPageEntry);
    public string GoToPage => Text(GoToPageEntry);
    public string DetailTitle => Text(DetailTitleEntry);
    public string PeriodsTitle => Text(PeriodsTitleEntry);
    public string EvidenceTitle => Text(EvidenceTitleEntry);
    public string RawEvidenceTitle => Text(RawEvidenceTitleEntry);
    public string RawEvidenceHelp => Text(RawEvidenceHelpEntry);
    public string LoadRawEvidence => Text(LoadRawEvidenceEntry);
    public string OpenSeries => Text(OpenSeriesEntry);

    // Formatting-only escape hatch for legacy projections. It is deliberately not
    // public: new reusable UI copy must be represented by an enumerable entry.
    internal string Pick(string simplifiedChinese, string english) =>
        Language == WatchDisplayLanguage.SimplifiedChinese ? simplifiedChinese : english;

    public WatchCodeMeaning DescribeErrorCode(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        var description = rawCode switch
        {
            "REQUIRED_MES_FIELD_MISSING" => Text(RequiredFieldMissingEntry),
            "INVALID_MES_FIELD_FORMAT" => Text(InvalidFieldFormatEntry),
            "DUPLICATE_TRANSPORT_DEMAND_KEY" => Text(DuplicateDemandKeyEntry),
            "SUBLOT_MULTIPLE_WORK_TYPES" => Text(MultipleWorkTypesEntry),
            "LONG_GONE_BUT_VISIBLE" => Text(LongGoneVisibleEntry),
            _ => null,
        };
        return description is null
            ? new WatchCodeMeaning(
                string.Format(Text(UnknownErrorCodeEntry), rawCode),
                rawCode,
                IsKnown: false)
            : new WatchCodeMeaning(description, rawCode, IsKnown: true);
    }

    public WatchCodeMeaning DescribeCategory(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        var description = rawCode switch
        {
            "DATA_COMPLETENESS" => Text(DataCompletenessEntry),
            "DATA_FORMAT" => Text(DataFormatEntry),
            "OBSERVATION_CONFLICT" => Text(ObservationConflictEntry),
            "LIFECYCLE_CONFLICT" => Text(LifecycleConflictEntry),
            _ => null,
        };
        return description is null
            ? new WatchCodeMeaning(string.Format(Text(UnknownCategoryEntry), rawCode), rawCode, false)
            : new WatchCodeMeaning(description, rawCode, true);
    }

    public WatchCodeMeaning DescribeActivityState(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        var description = rawCode switch
        {
            ErrorSearchActivityStates.Active => Text(ActiveEntry),
            ErrorSearchActivityStates.Ended => Text(EndedEntry),
            _ => null,
        };
        return description is null
            ? new WatchCodeMeaning(string.Format(Text(UnknownActivityStateEntry), rawCode), rawCode, false)
            : new WatchCodeMeaning(description, rawCode, true);
    }

    public string CodeWithMeaning(WatchCodeMeaning meaning) =>
        $"{meaning.Description} · {meaning.RawCode}";

    public string WindowLabel(string rawCode) => rawCode switch
    {
        ErrorSearchWindowKinds.Last24Hours => Text(Last24HoursEntry),
        ErrorSearchWindowKinds.Last7Days => Text(Last7DaysEntry),
        ErrorSearchWindowKinds.Last15Days => Text(Last15DaysEntry),
        ErrorSearchWindowKinds.AllHistory => Text(AllHistoryEntry),
        _ => CodeWithMeaning(new WatchCodeMeaning(Text(CustomWindowEntry), rawCode, true)),
    };
}
