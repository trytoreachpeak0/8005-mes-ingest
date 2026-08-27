using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal sealed partial class WatchCurrentAttentionText
{
    private static WatchTextCatalogEntry Entry(string id, string zh, string en) =>
        new($"current-attention.{id}", zh, en);

    private static readonly WatchTextCatalogEntry PageTitleEntry = Entry("page-title", "接入告警", "Current ingest attention");
    private static readonly WatchTextCatalogEntry KindFilterEntry = Entry("kind-filter", "关注类型", "Attention type");
    private static readonly WatchTextCatalogEntry SeverityFilterEntry = Entry("severity-filter", "严重度", "Severity");
    private static readonly WatchTextCatalogEntry PerPageEntry = Entry("per-page", "每页", "Per page");
    private static readonly WatchTextCatalogEntry ApplyFiltersEntry = Entry("apply-filters", "应用条件", "Apply filters");
    private static readonly WatchTextCatalogEntry ClearFiltersEntry = Entry("clear-filters", "清除", "Clear");
    private static readonly WatchTextCatalogEntry FacetsEntry = Entry("facets", "Host 精确分面", "Exact Host facets");
    private static readonly WatchTextCatalogEntry ResultsEntry = Entry("results", "当前仍需关注", "Currently needs attention");
    private static readonly WatchTextCatalogEntry SelectedDetailEntry = Entry("selected-detail", "选中项详情", "Selected item details");
    private static readonly WatchTextCatalogEntry EvidenceEntry = Entry("evidence", "结构化证据与关联对象", "Structured evidence and related objects");
    private static readonly WatchTextCatalogEntry OpenSeriesEntry = Entry("open-series", "打开需求系列详情", "Open demand-series details");
    private static readonly WatchTextCatalogEntry OpenErrorSearchEntry = Entry("open-error-search", "在错误检索中打开", "Open in Error Search");
    private static readonly WatchTextCatalogEntry PreviousPageEntry = Entry("previous-page", "上一页", "Previous");
    private static readonly WatchTextCatalogEntry NextPageEntry = Entry("next-page", "下一页", "Next");
    private static readonly WatchTextCatalogEntry GoToPageEntry = Entry("go-to-page", "跳转", "Go");

    private static readonly IReadOnlyList<WatchTextCatalogEntry> CatalogEntries =
    [
        PageTitleEntry, KindFilterEntry, SeverityFilterEntry, PerPageEntry,
        ApplyFiltersEntry, ClearFiltersEntry, FacetsEntry, ResultsEntry,
        SelectedDetailEntry, EvidenceEntry, OpenSeriesEntry, OpenErrorSearchEntry,
        PreviousPageEntry, NextPageEntry, GoToPageEntry,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => CatalogEntries;

    public string PageTitle => Text(PageTitleEntry);
    public string KindFilter => Text(KindFilterEntry);
    public string SeverityFilter => Text(SeverityFilterEntry);
    public string PerPage => Text(PerPageEntry);
    public string ApplyFilters => Text(ApplyFiltersEntry);
    public string ClearFilters => Text(ClearFiltersEntry);
    public string Facets => Text(FacetsEntry);
    public string Results => Text(ResultsEntry);
    public string SelectedDetail => Text(SelectedDetailEntry);
    public string Evidence => Text(EvidenceEntry);
    public string OpenSeries => Text(OpenSeriesEntry);
    public string OpenErrorSearch => Text(OpenErrorSearchEntry);
    public string PreviousPage => Text(PreviousPageEntry);
    public string NextPage => Text(NextPageEntry);
    public string GoToPage => Text(GoToPageEntry);

    public string Pick(string simplifiedChinese, string english) =>
        Language == WatchDisplayLanguage.SimplifiedChinese ? simplifiedChinese : english;

    public WatchCodeMeaning DescribeKind(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        var description = rawCode switch
        {
            CurrentIngestAttentionKinds.SeriesError => Pick("活动需求系列错误", "Active series error"),
            CurrentIngestAttentionKinds.PollRunFailure => Pick("轮询运行失败", "Poll run failure"),
            CurrentIngestAttentionKinds.TaskTypeProtection => Pick("任务类型保护", "Task-type protection"),
            CurrentIngestAttentionKinds.UnassignedMesObservation => Pick("未归属 MES 观测", "Unassigned MES observation"),
            CurrentIngestAttentionKinds.HistoryCleanupFailure => Pick("历史清理失败", "History-cleanup failure"),
            CurrentIngestAttentionKinds.StoragePressure => Pick("存储压力", "Storage pressure"),
            CurrentIngestAttentionKinds.HistoryReset => Pick("历史重置", "History reset"),
            _ => null,
        };
        return description is null
            ? new WatchCodeMeaning(Pick($"未知关注类型：{rawCode}", $"Unknown attention type: {rawCode}"), rawCode, false)
            : new WatchCodeMeaning(description, rawCode, true);
    }

    public WatchCodeMeaning DescribeSeverity(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        var description = rawCode switch
        {
            CurrentIngestAttentionSeverities.Error => Pick("错误", "Error"),
            CurrentIngestAttentionSeverities.Warning => Pick("警告", "Warning"),
            _ => null,
        };
        return description is null
            ? new WatchCodeMeaning(Pick($"未知严重度：{rawCode}", $"Unknown severity: {rawCode}"), rawCode, false)
            : new WatchCodeMeaning(description, rawCode, true);
    }

    public WatchCodeMeaning DescribeProtectionStatus(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        var description = rawCode switch
        {
            "PAUSED" => Pick("已暂停接入", "Ingest paused"),
            "CRITICAL_WARNING" => Pick("存储空间严重告警", "Critical storage warning"),
            "ACKNOWLEDGEMENT_REQUIRED" => Pick("需要人工确认", "Acknowledgement required"),
            "RECOVERING" => Pick("正在恢复", "Recovering"),
            "FAILED" => Pick("失败", "Failed"),
            "INCOMPLETE" => Pick("未完成", "Incomplete"),
            "SUCCEEDED" => Pick("成功", "Succeeded"),
            _ => null,
        };
        return description is null
            ? new WatchCodeMeaning(Pick($"未知状态：{rawCode}", $"Unknown status: {rawCode}"), rawCode, false)
            : new WatchCodeMeaning(description, rawCode, true);
    }

    public string CodeWithMeaning(WatchCodeMeaning meaning) =>
        $"{meaning.Description} · {meaning.RawCode}";
}
