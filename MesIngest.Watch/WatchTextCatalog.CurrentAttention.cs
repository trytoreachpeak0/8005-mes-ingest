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
    private static readonly WatchTextCatalogEntry ApplyFiltersAutomationEntry = Entry("apply-filters-automation", "应用接入告警条件", "Apply ingest-alert filters");
    private static readonly WatchTextCatalogEntry ClearFiltersAutomationEntry = Entry("clear-filters-automation", "清除接入告警条件", "Clear ingest-alert filters");
    private static readonly WatchTextCatalogEntry PreviousPageAutomationEntry = Entry("previous-page-automation", "接入告警上一页", "Previous ingest-alert page");
    private static readonly WatchTextCatalogEntry NextPageAutomationEntry = Entry("next-page-automation", "接入告警下一页", "Next ingest-alert page");
    private static readonly WatchTextCatalogEntry GoToPageAutomationEntry = Entry("go-to-page-automation", "接入告警直接页码跳转", "Go directly to an ingest-alert page");
    private static readonly WatchTextCatalogEntry OpenErrorSearchAutomationEntry = Entry("open-error-search-automation", "从当前需求系列错误下钻错误检索", "Drill from the current demand-series error into Error Search");
    private static readonly WatchTextCatalogEntry DefaultOrderEntry = Entry("order-default", "严重度降序、发生时间降序、稳定标识升序", CurrentIngestAttentionOrder.Default);
    private static readonly WatchTextCatalogEntry FacetsEntry = Entry("facets", "Host 精确分面", "Exact Host facets");
    private static readonly WatchTextCatalogEntry FacetsCardAutomationEntry = Entry("facets-card-automation", "接入告警精确分面", "Exact ingest-alert facets");
    private static readonly WatchTextCatalogEntry ResultsEntry = Entry("results", "当前仍需关注", "Currently needs attention");
    private static readonly WatchTextCatalogEntry ResultsCardAutomationEntry = Entry("results-card-automation", "当前接入告警结果", "Current ingest-alert results");
    private static readonly WatchTextCatalogEntry SelectedDetailEntry = Entry("selected-detail", "选中项详情", "Selected item details");
    private static readonly WatchTextCatalogEntry EvidenceEntry = Entry("evidence", "结构化证据与关联对象", "Structured evidence and related objects");
    private static readonly WatchTextCatalogEntry EvidenceCardAutomationEntry = Entry("evidence-card-automation", "接入告警结构化证据", "Structured ingest-alert evidence");
    private static readonly WatchTextCatalogEntry OpenSeriesEntry = Entry("open-series", "打开需求系列详情", "Open demand-series details");
    private static readonly WatchTextCatalogEntry OpenErrorSearchEntry = Entry("open-error-search", "在错误检索中打开", "Open in Error Search");
    private static readonly WatchTextCatalogEntry PreviousPageEntry = Entry("previous-page", "上一页", "Previous");
    private static readonly WatchTextCatalogEntry NextPageEntry = Entry("next-page", "下一页", "Next");
    private static readonly WatchTextCatalogEntry GoToPageEntry = Entry("go-to-page", "跳转", "Go");
    private static readonly WatchTextCatalogEntry SeriesErrorEntry = Entry("kind-series-error", "活动需求系列错误", "Active series error");
    private static readonly WatchTextCatalogEntry PollRunFailureEntry = Entry("kind-poll-run-failure", "轮询运行失败", "Poll run failure");
    private static readonly WatchTextCatalogEntry TaskTypeProtectionEntry = Entry("kind-task-type-protection", "任务类型保护", "Task-type protection");
    private static readonly WatchTextCatalogEntry UnassignedObservationEntry = Entry("kind-unassigned-observation", "未归属 MES 观测", "Unassigned MES observation");
    private static readonly WatchTextCatalogEntry HistoryCleanupFailureEntry = Entry("kind-history-cleanup-failure", "历史清理失败", "History-cleanup failure");
    private static readonly WatchTextCatalogEntry StoragePressureEntry = Entry("kind-storage-pressure", "存储压力", "Storage pressure");
    private static readonly WatchTextCatalogEntry HistoryResetEntry = Entry("kind-history-reset", "历史重置", "History reset");
    private static readonly WatchTextCatalogEntry ErrorSeverityEntry = Entry("severity-error", "错误", "Error");
    private static readonly WatchTextCatalogEntry WarningSeverityEntry = Entry("severity-warning", "警告", "Warning");
    private static readonly WatchTextCatalogEntry PausedEntry = Entry("protection-paused", "已暂停接入", "Ingest paused");
    private static readonly WatchTextCatalogEntry CriticalWarningEntry = Entry("protection-critical-warning", "存储空间严重告警", "Critical storage warning");
    private static readonly WatchTextCatalogEntry AcknowledgementRequiredEntry = Entry("protection-acknowledgement-required", "需要人工确认", "Acknowledgement required");
    private static readonly WatchTextCatalogEntry RecoveringEntry = Entry("protection-recovering", "正在恢复", "Recovering");
    private static readonly WatchTextCatalogEntry FailedEntry = Entry("protection-failed", "失败", "Failed");
    private static readonly WatchTextCatalogEntry IncompleteEntry = Entry("protection-incomplete", "未完成", "Incomplete");
    private static readonly WatchTextCatalogEntry SucceededEntry = Entry("protection-succeeded", "成功", "Succeeded");
    private static readonly WatchTextCatalogEntry UnknownKindEntry = Entry("unknown-kind", "未知关注类型：{0}", "Unknown attention type: {0}");
    private static readonly WatchTextCatalogEntry UnknownSeverityEntry = Entry("unknown-severity", "未知严重度：{0}", "Unknown severity: {0}");
    private static readonly WatchTextCatalogEntry UnknownStatusEntry = Entry("unknown-status", "未知状态：{0}", "Unknown status: {0}");

    private static readonly IReadOnlyList<WatchTextCatalogEntry> CatalogEntries =
    [
        PageTitleEntry, KindFilterEntry, SeverityFilterEntry, PerPageEntry,
        ApplyFiltersEntry, ClearFiltersEntry, ApplyFiltersAutomationEntry, ClearFiltersAutomationEntry,
        PreviousPageAutomationEntry, NextPageAutomationEntry, GoToPageAutomationEntry,
        OpenErrorSearchAutomationEntry, DefaultOrderEntry, FacetsEntry, FacetsCardAutomationEntry,
        ResultsEntry, ResultsCardAutomationEntry, SelectedDetailEntry, EvidenceEntry,
        EvidenceCardAutomationEntry, OpenSeriesEntry, OpenErrorSearchEntry,
        PreviousPageEntry, NextPageEntry, GoToPageEntry,
        SeriesErrorEntry, PollRunFailureEntry, TaskTypeProtectionEntry,
        UnassignedObservationEntry, HistoryCleanupFailureEntry, StoragePressureEntry,
        HistoryResetEntry, ErrorSeverityEntry, WarningSeverityEntry, PausedEntry,
        CriticalWarningEntry, AcknowledgementRequiredEntry, RecoveringEntry,
        FailedEntry, IncompleteEntry, SucceededEntry, UnknownKindEntry,
        UnknownSeverityEntry, UnknownStatusEntry,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries =>
        [.. CatalogEntries, .. WatchGeneratedText.CurrentAttentionEntries];

    public string PageTitle => Text(PageTitleEntry);
    public string KindFilter => Text(KindFilterEntry);
    public string SeverityFilter => Text(SeverityFilterEntry);
    public string PerPage => Text(PerPageEntry);
    public string ApplyFilters => Text(ApplyFiltersEntry);
    public string ClearFilters => Text(ClearFiltersEntry);
    public string ApplyFiltersAutomationName => Text(ApplyFiltersAutomationEntry);
    public string ClearFiltersAutomationName => Text(ClearFiltersAutomationEntry);
    public string PreviousPageAutomationName => Text(PreviousPageAutomationEntry);
    public string NextPageAutomationName => Text(NextPageAutomationEntry);
    public string GoToPageAutomationName => Text(GoToPageAutomationEntry);
    public string OpenErrorSearchAutomationName => Text(OpenErrorSearchAutomationEntry);
    public string OrderLabel(string rawOrder) => string.Equals(
        rawOrder,
        CurrentIngestAttentionOrder.Default,
        StringComparison.Ordinal)
            ? Text(DefaultOrderEntry)
            : rawOrder;
    public string Facets => Text(FacetsEntry);
    public string FacetsCardAutomationName => Text(FacetsCardAutomationEntry);
    public string Results => Text(ResultsEntry);
    public string ResultsCardAutomationName => Text(ResultsCardAutomationEntry);
    public string SelectedDetail => Text(SelectedDetailEntry);
    public string Evidence => Text(EvidenceEntry);
    public string EvidenceCardAutomationName => Text(EvidenceCardAutomationEntry);
    public string OpenSeries => Text(OpenSeriesEntry);
    public string OpenErrorSearch => Text(OpenErrorSearchEntry);
    public string PreviousPage => Text(PreviousPageEntry);
    public string NextPage => Text(NextPageEntry);
    public string GoToPage => Text(GoToPageEntry);

    public WatchCodeMeaning DescribeKind(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        var description = rawCode switch
        {
            CurrentIngestAttentionKinds.SeriesError => Text(SeriesErrorEntry),
            CurrentIngestAttentionKinds.PollRunFailure => Text(PollRunFailureEntry),
            CurrentIngestAttentionKinds.TaskTypeProtection => Text(TaskTypeProtectionEntry),
            CurrentIngestAttentionKinds.UnassignedMesObservation => Text(UnassignedObservationEntry),
            CurrentIngestAttentionKinds.HistoryCleanupFailure => Text(HistoryCleanupFailureEntry),
            CurrentIngestAttentionKinds.StoragePressure => Text(StoragePressureEntry),
            CurrentIngestAttentionKinds.HistoryReset => Text(HistoryResetEntry),
            _ => null,
        };
        return description is null
            ? new WatchCodeMeaning(string.Format(Text(UnknownKindEntry), rawCode), rawCode, false)
            : new WatchCodeMeaning(description, rawCode, true);
    }

    public WatchCodeMeaning DescribeSeverity(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        var description = rawCode switch
        {
            CurrentIngestAttentionSeverities.Error => Text(ErrorSeverityEntry),
            CurrentIngestAttentionSeverities.Warning => Text(WarningSeverityEntry),
            _ => null,
        };
        return description is null
            ? new WatchCodeMeaning(string.Format(Text(UnknownSeverityEntry), rawCode), rawCode, false)
            : new WatchCodeMeaning(description, rawCode, true);
    }

    public WatchCodeMeaning DescribeProtectionStatus(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        var description = rawCode switch
        {
            "PAUSED" => Text(PausedEntry),
            "CRITICAL_WARNING" => Text(CriticalWarningEntry),
            "ACKNOWLEDGEMENT_REQUIRED" => Text(AcknowledgementRequiredEntry),
            "RECOVERING" => Text(RecoveringEntry),
            "FAILED" => Text(FailedEntry),
            "INCOMPLETE" => Text(IncompleteEntry),
            "SUCCEEDED" => Text(SucceededEntry),
            _ => null,
        };
        return description is null
            ? new WatchCodeMeaning(string.Format(Text(UnknownStatusEntry), rawCode), rawCode, false)
            : new WatchCodeMeaning(description, rawCode, true);
    }

    public string CodeWithMeaning(WatchCodeMeaning meaning) =>
        PresentCodeMeaning(meaning);
}
