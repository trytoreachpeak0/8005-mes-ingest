using System.Globalization;

namespace MesIngest.Watch;

internal sealed partial class WatchDemandSeriesText
{
    private static WatchTextCatalogEntry E(string id, string zh, string en) => new(id, zh, en);

    private static readonly WatchTextCatalogEntry PageTitleEntry = E("demandSeries.page.title", "需求系列", "Demand series");
    private static readonly WatchTextCatalogEntry PageAutomationEntry = E("demandSeries.page.automation", "需求系列页面", "Demand series page");
    private static readonly WatchTextCatalogEntry WorkspaceAutomationEntry = E("demandSeries.workspace.automation", "需求系列工作区", "Demand series workspace");
    private static readonly WatchTextCatalogEntry ContextAutomationEntry = E("demandSeries.context.automation", "需求系列快照与 AREA 范围", "Demand series snapshot and AREA scope");
    private static readonly WatchTextCatalogEntry FiltersEntry = E("demandSeries.filters.name", "需求系列筛选", "Demand series filters");
    private static readonly WatchTextCatalogEntry LifecycleEntry = E("demandSeries.filters.lifecycle", "生命周期", "Lifecycle");
    private static readonly WatchTextCatalogEntry AllEntry = E("demandSeries.filters.all", "全部", "All");
    private static readonly WatchTextCatalogEntry TrackingEntry = E("demandSeries.lifecycle.tracking", "跟踪中", "Tracking");
    private static readonly WatchTextCatalogEntry ArchivedEntry = E("demandSeries.lifecycle.archived", "已归档", "Archived");
    private static readonly WatchTextCatalogEntry UnknownLifecycleEntry = E("demandSeries.lifecycle.unknown", "未知生命周期", "Unknown lifecycle");
    private static readonly WatchTextCatalogEntry PresenceEntry = E("demandSeries.filters.presence", "当前出现", "Current presence");
    private static readonly WatchTextCatalogEntry AllPresenceEntry = E("demandSeries.filters.allPresence", "全部出现状态", "All presence states");
    private static readonly WatchTextCatalogEntry VisibleEntry = E("demandSeries.presence.visible", "当前可见", "Visible now");
    private static readonly WatchTextCatalogEntry GoneEntry = E("demandSeries.presence.gone", "当前消失", "Gone");
    private static readonly WatchTextCatalogEntry LongGoneEntry = E("demandSeries.presence.longGone", "长期消失但仍可见", "Long gone but visible");
    private static readonly WatchTextCatalogEntry UnknownPresenceEntry = E("demandSeries.presence.unknown", "未知出现状态", "Unknown presence");
    private static readonly WatchTextCatalogEntry WorkTypeEntry = E("demandSeries.filters.workType", "WorkType", "WorkType");
    private static readonly WatchTextCatalogEntry AllWorkTypesEntry = E("demandSeries.filters.allWorkTypes", "全部 WorkType", "All WorkTypes");
    private static readonly WatchTextCatalogEntry WorkTypeChoiceEntry = E("demandSeries.filters.workTypeChoice", "任务类型 ({0})", "Work type ({0})");
    private static readonly WatchTextCatalogEntry SublotEntry = E("demandSeries.filters.sublot", "SUBLOT", "SUBLOT");
    private static readonly WatchTextCatalogEntry IdentityEntry = E("demandSeries.filters.identity", "SeriesId / DemandId", "SeriesId / DemandId");
    private static readonly WatchTextCatalogEntry AreaFilterEntry = E("demandSeries.filters.area", "AREA 筛选", "AREA filter");
    private static readonly WatchTextCatalogEntry ApplyEntry = E("demandSeries.filters.apply", "应用条件", "Apply filters");
    private static readonly WatchTextCatalogEntry ClearEntry = E("demandSeries.filters.clear", "清除", "Clear");
    private static readonly WatchTextCatalogEntry MasterHeadingEntry = E("demandSeries.list.heading", "当前范围内的需求系列", "Demand series in the current scope");
    private static readonly WatchTextCatalogEntry OpenInspectorEntry = E("demandSeries.inspector.open", "打开 Inspector", "Open Inspector");
    private static readonly WatchTextCatalogEntry ShowInspectorEntry = E("demandSeries.inspector.show", "显示 Inspector", "Show Inspector");
    private static readonly WatchTextCatalogEntry PreviousEntry = E("demandSeries.paging.previous", "上一页", "Previous");
    private static readonly WatchTextCatalogEntry NextEntry = E("demandSeries.paging.next", "下一页", "Next");
    private static readonly WatchTextCatalogEntry GoToEntry = E("demandSeries.paging.goTo", "跳转", "Go");
    private static readonly WatchTextCatalogEntry PerPageEntry = E("demandSeries.paging.perPage", "每页", "Per page");
    private static readonly WatchTextCatalogEntry EmptyTitleEntry = E("demandSeries.empty.title", "当前条件没有需求系列", "No demand series match the current filters");
    private static readonly WatchTextCatalogEntry EmptyMessageEntry = E("demandSeries.empty.message", "Host 已返回精确 0 个结果。可调整筛选；若这是跨 AREA 下钻，请使用上方显式范围确认。", "Host returned exactly 0 results. Adjust the filters, or explicitly confirm the scope above for a cross-AREA drill-down.");
    private static readonly WatchTextCatalogEntry SourceComparisonEntry = E("demandSeries.source.title", "来源快照比较", "Source snapshot comparison");
    private static readonly WatchTextCatalogEntry TopInfoEntry = E("demandSeries.info.automation", "需求系列顶部说明区", "Demand series information region");
    private static readonly WatchTextCatalogEntry ReadStateEntry = E("demandSeries.readState.automation", "需求系列读取状态", "Demand series read status");
    private static readonly WatchTextCatalogEntry MainListEntry = E("demandSeries.list.automation", "需求系列主列表", "Demand series master list");
    private static readonly WatchTextCatalogEntry ListEntry = E("demandSeries.grid.automation", "需求系列列表", "Demand series list");
    private static readonly WatchTextCatalogEntry EmptyAutomationEntry = E("demandSeries.empty.automation", "需求系列空结果", "Demand series empty result");
    private static readonly WatchTextCatalogEntry SeriesIdFilterEntry = E("demandSeries.filters.seriesIdAutomation", "SeriesId 筛选", "SeriesId filter");
    private static readonly WatchTextCatalogEntry DemandIdFilterEntry = E("demandSeries.filters.demandIdAutomation", "DemandId 筛选", "DemandId filter");
    private static readonly WatchTextCatalogEntry SublotFilterEntry = E("demandSeries.filters.sublotAutomation", "SUBLOT 筛选", "SUBLOT filter");
    private static readonly WatchTextCatalogEntry WorkTypeFilterEntry = E("demandSeries.filters.workTypeAutomation", "WorkType 筛选", "WorkType filter");
    private static readonly WatchTextCatalogEntry PresenceFilterEntry = E("demandSeries.filters.presenceAutomation", "当前出现状态筛选", "Current presence filter");
    private static readonly WatchTextCatalogEntry AreaSelectorEntry = E("demandSeries.filters.areaAutomation", "需求系列 AREA 配置选择器", "Demand series AREA profile selector");
    private static readonly WatchTextCatalogEntry ColumnLifecycleEntry = E("demandSeries.column.lifecycle", "生命周期 / 当前出现", "Lifecycle / current presence");
    private static readonly WatchTextCatalogEntry ColumnAreaEntry = E("demandSeries.column.area", "当前 AREA", "Current AREA");
    private static readonly WatchTextCatalogEntry ColumnDemandEntry = E("demandSeries.column.demand", "当前 Demand", "Current Demand");
    private static readonly WatchTextCatalogEntry ColumnGenerationEntry = E("demandSeries.column.generation", "世代", "Generation");
    private static readonly WatchTextCatalogEntry ColumnEventsEntry = E("demandSeries.column.events", "事件", "Events");
    private static readonly WatchTextCatalogEntry ColumnStartedEntry = E("demandSeries.column.started", "开始时间", "Started at");
    private static readonly WatchTextCatalogEntry ColumnLastSeenEntry = E("demandSeries.column.lastSeen", "最后观测", "Last seen");
    private static readonly WatchTextCatalogEntry ColumnGoneSinceEntry = E("demandSeries.column.goneSince", "确认消失", "Gone since");
    private static readonly WatchTextCatalogEntry ColumnArchivedEntry = E("demandSeries.column.archived", "归档时间", "Archived at");
    private static readonly WatchTextCatalogEntry SelectedEntry = E("demandSeries.selection.selected", "已选择", "Selected");
    private static readonly WatchTextCatalogEntry NotSelectedEntry = E("demandSeries.selection.notSelected", "未选择", "Not selected");
    private static readonly WatchTextCatalogEntry NoBusinessSnapshotEntry = E("demandSeries.state.noBusinessSnapshot", "尚无 Host 业务快照", "No Host business snapshot yet");
    private static readonly WatchTextCatalogEntry NoSnapshotEntry = E("demandSeries.state.noSnapshot", "尚无需求系列快照", "No demand series snapshot yet");
    private static readonly WatchTextCatalogEntry FixedOrderEntry = E("demandSeries.order.fixed", "Host 固定排序：开始时间降序、SeriesId 升序", "Host fixed order: started time descending, then SeriesId ascending");
    private static readonly WatchTextCatalogEntry NoHostScopeEntry = E("demandSeries.scope.noSnapshot", "Host 已提交范围：尚无快照", "Host committed scope: no snapshot yet");
    private static readonly WatchTextCatalogEntry HostAllAreasEntry = E("demandSeries.scope.all", "Host 已提交范围：全部 AREA", "Host committed scope: all AREAs");
    private static readonly WatchTextCatalogEntry HostAreasEntry = E("demandSeries.scope.areas", "Host 已提交范围：{0}", "Host committed scope: {0}");
    private static readonly WatchTextCatalogEntry LocalUnlimitedEntry = E("demandSeries.scope.localUnlimited", "{0} · 未限制 Host AREA 查询", "{0} · Host AREA query is unrestricted");
    private static readonly WatchTextCatalogEntry LocalAreasEntry = E("demandSeries.scope.localAreas", "{0} · {1}", "{0} · {1}");
    private static readonly WatchTextCatalogEntry SnapshotFactsEntry = E("demandSeries.snapshot.facts", "Host 投影提交 {0} · {1} · 序列 {2} · PollTrace {3}", "Host projection committed {0} · {1} · sequence {2} · PollTrace {3}");
    private static readonly WatchTextCatalogEntry PageSummaryEntry = E("demandSeries.paging.summary", "精确 {0} 个需求系列 · 第 {1} / {2} 页", "Exactly {0} demand series · page {1} of {2}");
    private static readonly WatchTextCatalogEntry ConnectingTitleEntry = E("demandSeries.state.connectingTitle", "正在连接 Host", "Connecting to Host");
    private static readonly WatchTextCatalogEntry ConnectingMessageEntry = E("demandSeries.state.connectingMessage", "连接成功后将读取一份冻结的需求系列快照。", "A frozen demand series snapshot will be read after the connection succeeds.");
    private static readonly WatchTextCatalogEntry HostFailedTitleEntry = E("demandSeries.state.hostFailedTitle", "无法连接 Host", "Cannot connect to Host");
    private static readonly WatchTextCatalogEntry HostFailedMessageEntry = E("demandSeries.state.hostFailedMessage", "新 Host 未通过契约连接；旧 Host 数据已清空。{0}", "The new Host did not pass contract connection; data from the old Host was cleared.{0}");
    private static readonly WatchTextCatalogEntry LoadingTitleEntry = E("demandSeries.state.loadingTitle", "正在读取需求系列", "Loading demand series");
    private static readonly WatchTextCatalogEntry RefreshingTitleEntry = E("demandSeries.state.refreshingTitle", "正在刷新需求系列", "Refreshing demand series");
    private static readonly WatchTextCatalogEntry WaitingSnapshotEntry = E("demandSeries.state.waitingSnapshot", "等待 Host 返回冻结快照。", "Waiting for Host to return a frozen snapshot.");
    private static readonly WatchTextCatalogEntry RetainedDuringRefreshEntry = E("demandSeries.state.retainedRefresh", "刷新期间继续显示 Host 快照 {0}。", "Continuing to show Host snapshot {0} during refresh.");
    private static readonly WatchTextCatalogEntry PriorFailureRetryEntry = E("demandSeries.state.priorFailureRetry", " 上次失败于 {0}；本次正在重试。", " The previous attempt failed at {0}; retrying now.");
    private static readonly WatchTextCatalogEntry ReadFailedTitleEntry = E("demandSeries.state.readFailedTitle", "需求系列读取失败", "Demand series read failed");
    private static readonly WatchTextCatalogEntry RefreshFailedTitleEntry = E("demandSeries.state.refreshFailedTitle", "需求系列刷新失败，已保留上次快照", "Demand series refresh failed; last snapshot retained");
    private static readonly WatchTextCatalogEntry NoSuccessfulSnapshotEntry = E("demandSeries.state.noSuccessfulSnapshot", "当前没有可显示的成功快照。", "There is no successful snapshot to display.");
    private static readonly WatchTextCatalogEntry RetainedAfterFailureEntry = E("demandSeries.state.retainedFailure", "继续显示 Host 快照 {0}；其筛选与 AREA 范围不会被失败查询改写。", "Continuing to show Host snapshot {0}; its filters and AREA scope were not overwritten by the failed query.");
    private static readonly WatchTextCatalogEntry FailedAtEntry = E("demandSeries.state.failedAt", "失败于 {0}。{1}{2}", "Failed at {0}. {1}{2}");
    private static readonly WatchTextCatalogEntry SelectionLostTitleEntry = E("demandSeries.state.selectionLostTitle", "原选择已不在刷新结果中", "Previous selection is no longer in the refreshed result");
    private static readonly WatchTextCatalogEntry SelectionLostMessageEntry = E("demandSeries.state.selectionLostMessage", "刷新成功，但原 Series 已不在当前冻结结果中；已清除详情，请重新选择。", "Refresh succeeded, but the previous Series is no longer in the frozen result. Its detail was cleared; select another Series.");
    private static readonly WatchTextCatalogEntry WatchSuccessEntry = E("demandSeries.attempt.success", "Watch 最近成功 {0}", "Watch last succeeded at {0}");
    private static readonly WatchTextCatalogEntry WatchNoSuccessEntry = E("demandSeries.attempt.noSuccess", "Watch 尚无成功读取", "Watch has no successful read yet");
    private static readonly WatchTextCatalogEntry WatchFailureEntry = E("demandSeries.attempt.failure", "{0} · 最近失败 {1}", "{0} · latest failure {1}");
    private static readonly WatchTextCatalogEntry FailureCorrelationEntry = E("demandSeries.failure.correlation", " 关联 ID {0}。", " Correlation ID {0}.");
    private static readonly WatchTextCatalogEntry LocalAreaContextEntry = E("demandSeries.context.localArea", "本机 AREA：{0}", "Local AREA: {0}");
    private static readonly WatchTextCatalogEntry AutoRefreshEntry = E("demandSeries.context.autoRefresh", "自动刷新 {0} 秒", "Automatic refresh every {0} seconds");
    private static readonly WatchTextCatalogEntry LastSuccessEntry = E("demandSeries.context.lastSuccess", "最近成功 {0}", "Last successful read {0}");
    private static readonly WatchTextCatalogEntry WaitingHostEntry = E("demandSeries.context.waitingHost", "等待 Host 快照", "Waiting for Host snapshot");
    private static readonly WatchTextCatalogEntry HostNoScopeEntry = E("demandSeries.context.hostNoScope", "Host 尚无范围", "Host has no committed scope");
    private static readonly WatchTextCatalogEntry HostAllScopeEntry = E("demandSeries.context.hostAllScope", "Host 全部 AREA", "All Host AREAs");
    private static readonly WatchTextCatalogEntry HostSomeScopeEntry = E("demandSeries.context.hostSomeScope", "Host {0}", "Host {0}");
    private static readonly WatchTextCatalogEntry HostManyScopeEntry = E("demandSeries.context.hostManyScope", "Host {0} 等 {1} 个 AREA", "Host {0} and {1} AREAs total");
    private static readonly WatchTextCatalogEntry ContextNameEntry = E("demandSeries.context.name", "需求系列快照与 AREA 范围：{0}", "Demand series snapshot and AREA scope: {0}");
    private static readonly WatchTextCatalogEntry FacetNameEntry = E("demandSeries.facet.name", "Host 精确分面：{0}", "Exact Host facet: {0}");
    private static readonly WatchTextCatalogEntry PageSummaryNameEntry = E("demandSeries.paging.summaryAutomation", "需求系列精确分页摘要：{0}", "Exact demand series page summary: {0}");
    private static readonly WatchTextCatalogEntry InfoNameEntry = E("demandSeries.info.name", "{0}。{1}", "{0}. {1}");
    private static readonly WatchTextCatalogEntry TopInfoNameEntry = E("demandSeries.info.topName", "需求系列顶部说明区。{0}", "Demand series information region. {0}");
    private static readonly WatchTextCatalogEntry SourceNoFactsEntry = E("demandSeries.source.noFacts", "来源未提供对象级事实", "Source did not provide object-level facts");
    private static readonly WatchTextCatalogEntry SourceFactsEntry = E("demandSeries.source.facts", "来源对象事实：{0}", "Source object facts: {0}");
    private static readonly WatchTextCatalogEntry SourceSummaryEntry = E("demandSeries.source.summary", "来源 {0} · 快照时点 {1} · Host 投影提交 {2} · {3} · 序列 {4} · {5}", "Source {0} · snapshot time {1} · Host projection committed {2} · {3} · sequence {4} · {5}");
    private static readonly WatchTextCatalogEntry TargetUnavailableEntry = E("demandSeries.source.targetUnavailable", "目标页尚无成功快照，暂时无法比较来源事实。", "The target page has no successful snapshot, so source facts cannot be compared yet.");
    private static readonly WatchTextCatalogEntry TargetNewerEntry = E("demandSeries.source.targetNewer", "目标页快照较来源更新；{0} 以下内容以目标页快照为准。", "The target snapshot is newer than the source; {0} the target snapshot governs the content below.");
    private static readonly WatchTextCatalogEntry TargetOlderEntry = E("demandSeries.source.targetOlder", "目标页快照早于来源快照；{0} 请刷新后再核对事实。", "The target snapshot is older than the source; {0} refresh before comparing facts.");
    private static readonly WatchTextCatalogEntry SameProjectionEntry = E("demandSeries.source.sameProjection", "目标页与来源使用同一投影提交；{0}", "The target page and source use the same projection commit; {0}");
    private static readonly WatchTextCatalogEntry ProjectionMismatchEntry = E("demandSeries.source.projectionMismatch", "目标页与来源序列相同但投影提交标识不同，无法安全解释快照关系。", "The target and source have the same sequence but different projection commit IDs; their snapshot relationship cannot be interpreted safely.");
    private static readonly WatchTextCatalogEntry SourceCannotCompareEntry = E("demandSeries.source.cannotCompare", "来源没有对象级事实，无法判定对象事实是否变化；", "The source has no object-level facts, so object changes cannot be determined;");
    private static readonly WatchTextCatalogEntry TargetCannotCompareEntry = E("demandSeries.source.targetCannotCompare", "目标快照未包含该对象，无法判定对象事实是否变化；", "The target snapshot does not contain this object, so object changes cannot be determined;");
    private static readonly WatchTextCatalogEntry NoComparableFieldsEntry = E("demandSeries.source.noComparableFields", "来源没有可与目标核对的对象字段，无法判定对象事实是否变化；", "The source has no object fields that can be compared with the target, so object changes cannot be determined;");
    private static readonly WatchTextCatalogEntry FactsUnchangedEntry = E("demandSeries.source.factsUnchanged", "来源暴露的对象事实未变化（已核对 {0}）。", "Source object facts are unchanged (compared {0}).");
    private static readonly WatchTextCatalogEntry FactsChangedEntry = E("demandSeries.source.factsChanged", "事实已变化：{0}。", "Facts changed: {0}.");
    private static readonly WatchTextCatalogEntry GenerationLabelEntry = E("demandSeries.fact.generation", "世代", "Generation");
    private static readonly WatchTextCatalogEntry DemandStateLabelEntry = E("demandSeries.fact.demandState", "Demand 状态", "Demand state");
    private static readonly WatchTextCatalogEntry LifecycleLabelEntry = E("demandSeries.fact.lifecycle", "生命周期", "Lifecycle");
    private static readonly WatchTextCatalogEntry PresenceLabelEntry = E("demandSeries.fact.presence", "当前出现", "Current presence");
    private static readonly WatchTextCatalogEntry ReadabilityLabelEntry = E("demandSeries.fact.readability", "外部可读", "External readability");
    private static readonly WatchTextCatalogEntry BlockersLabelEntry = E("demandSeries.fact.blockers", "资格阻断", "Eligibility blockers");
    private static readonly WatchTextCatalogEntry NoneEntry = E("demandSeries.fact.none", "无", "None");
    private static readonly WatchTextCatalogEntry FilterChoiceNameEntry = E("demandSeries.filters.choiceAutomation", "{0}：{1}", "{0}: {1}");
    private static readonly WatchTextCatalogEntry OverviewSourceEntry = E("demandSeries.source.overview", "概览", "Overview");
    private static readonly WatchTextCatalogEntry ReadabilitySourceEntry = E("demandSeries.source.readability", "资格审计", "Eligibility audit");
    private static readonly WatchTextCatalogEntry ErrorSourceEntry = E("demandSeries.source.errorSearch", "错误检索", "Error search");
    private static readonly WatchTextCatalogEntry AttentionSourceEntry = E("demandSeries.source.attention", "当前关注", "Current attention");

    private static readonly IReadOnlyList<WatchTextCatalogEntry> OwnEntries =
    [
        PageTitleEntry, PageAutomationEntry, WorkspaceAutomationEntry, ContextAutomationEntry,
        FiltersEntry, LifecycleEntry, AllEntry, TrackingEntry, ArchivedEntry,
        UnknownLifecycleEntry, PresenceEntry, AllPresenceEntry, VisibleEntry, GoneEntry,
        LongGoneEntry, UnknownPresenceEntry, WorkTypeEntry, AllWorkTypesEntry,
        WorkTypeChoiceEntry, SublotEntry, IdentityEntry, AreaFilterEntry, ApplyEntry,
        ClearEntry, MasterHeadingEntry, OpenInspectorEntry, ShowInspectorEntry, PreviousEntry,
        NextEntry, GoToEntry, PerPageEntry, EmptyTitleEntry, EmptyMessageEntry,
        SourceComparisonEntry, TopInfoEntry, ReadStateEntry, MainListEntry, ListEntry,
        EmptyAutomationEntry, SeriesIdFilterEntry, DemandIdFilterEntry, SublotFilterEntry,
        WorkTypeFilterEntry, PresenceFilterEntry, AreaSelectorEntry, ColumnLifecycleEntry,
        ColumnAreaEntry, ColumnDemandEntry, ColumnGenerationEntry, ColumnEventsEntry,
        ColumnStartedEntry, ColumnLastSeenEntry, ColumnGoneSinceEntry, ColumnArchivedEntry,
        SelectedEntry, NotSelectedEntry, NoBusinessSnapshotEntry, NoSnapshotEntry,
        FixedOrderEntry, NoHostScopeEntry, HostAllAreasEntry, HostAreasEntry,
        LocalUnlimitedEntry, LocalAreasEntry, SnapshotFactsEntry, PageSummaryEntry,
        ConnectingTitleEntry, ConnectingMessageEntry, HostFailedTitleEntry,
        HostFailedMessageEntry, LoadingTitleEntry, RefreshingTitleEntry, WaitingSnapshotEntry,
        RetainedDuringRefreshEntry, PriorFailureRetryEntry, ReadFailedTitleEntry,
        RefreshFailedTitleEntry, NoSuccessfulSnapshotEntry, RetainedAfterFailureEntry,
        FailedAtEntry, SelectionLostTitleEntry, SelectionLostMessageEntry, WatchSuccessEntry,
        WatchNoSuccessEntry, WatchFailureEntry, FailureCorrelationEntry, LocalAreaContextEntry,
        AutoRefreshEntry, LastSuccessEntry, WaitingHostEntry, HostNoScopeEntry,
        HostAllScopeEntry, HostSomeScopeEntry, HostManyScopeEntry, ContextNameEntry,
        FacetNameEntry, PageSummaryNameEntry, InfoNameEntry, TopInfoNameEntry,
        SourceNoFactsEntry, SourceFactsEntry, SourceSummaryEntry, TargetUnavailableEntry,
        TargetNewerEntry, TargetOlderEntry, SameProjectionEntry, ProjectionMismatchEntry,
        SourceCannotCompareEntry, TargetCannotCompareEntry, NoComparableFieldsEntry,
        FactsUnchangedEntry, FactsChangedEntry, GenerationLabelEntry, DemandStateLabelEntry,
        LifecycleLabelEntry, PresenceLabelEntry, ReadabilityLabelEntry, BlockersLabelEntry,
        NoneEntry, FilterChoiceNameEntry, OverviewSourceEntry, ReadabilitySourceEntry,
        ErrorSourceEntry, AttentionSourceEntry,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => OwnEntries;

    public string PageTitle => Text(PageTitleEntry);
    public string PageAutomationName => Text(PageAutomationEntry);
    public string WorkspaceAutomationName => Text(WorkspaceAutomationEntry);
    public string ContextAutomationName => Text(ContextAutomationEntry);
    public string Filters => Text(FiltersEntry);
    public string Lifecycle => Text(LifecycleEntry);
    public string All => Text(AllEntry);
    public string Tracking => Text(TrackingEntry);
    public string Archived => Text(ArchivedEntry);
    public string Presence => Text(PresenceEntry);
    public string AllPresence => Text(AllPresenceEntry);
    public string WorkType => Text(WorkTypeEntry);
    public string AllWorkTypes => Text(AllWorkTypesEntry);
    public string Sublot => Text(SublotEntry);
    public string Identity => Text(IdentityEntry);
    public string AreaFilter => Text(AreaFilterEntry);
    public string ApplyFilters => Text(ApplyEntry);
    public string ClearFilters => Text(ClearEntry);
    public string MasterHeading => Text(MasterHeadingEntry);
    public string OpenInspector => Text(OpenInspectorEntry);
    public string ShowInspector => Text(ShowInspectorEntry);
    public string Previous => Text(PreviousEntry);
    public string Next => Text(NextEntry);
    public string GoToPage => Text(GoToEntry);
    public string PerPage => Text(PerPageEntry);
    public string EmptyTitle => Text(EmptyTitleEntry);
    public string EmptyMessage => Text(EmptyMessageEntry);
    public string SourceComparison => Text(SourceComparisonEntry);
    public string TopInfoAutomationName => Text(TopInfoEntry);
    public string ReadStateAutomationName => Text(ReadStateEntry);
    public string MainListAutomationName => Text(MainListEntry);
    public string ListAutomationName => Text(ListEntry);
    public string EmptyAutomationName => Text(EmptyAutomationEntry);
    public string SeriesIdFilterAutomationName => Text(SeriesIdFilterEntry);
    public string DemandIdFilterAutomationName => Text(DemandIdFilterEntry);
    public string SublotFilterAutomationName => Text(SublotFilterEntry);
    public string WorkTypeFilterAutomationName => Text(WorkTypeFilterEntry);
    public string PresenceFilterAutomationName => Text(PresenceFilterEntry);
    public string AreaSelectorAutomationName => Text(AreaSelectorEntry);
    public string ColumnLifecycle => Text(ColumnLifecycleEntry);
    public string ColumnArea => Text(ColumnAreaEntry);
    public string ColumnDemand => Text(ColumnDemandEntry);
    public string ColumnGeneration => Text(ColumnGenerationEntry);
    public string ColumnEvents => Text(ColumnEventsEntry);
    public string ColumnStarted => Text(ColumnStartedEntry);
    public string ColumnLastSeen => Text(ColumnLastSeenEntry);
    public string ColumnGoneSince => Text(ColumnGoneSinceEntry);
    public string ColumnArchived => Text(ColumnArchivedEntry);
    public string Selected => Text(SelectedEntry);
    public string NotSelected => Text(NotSelectedEntry);
    public string NoBusinessSnapshot => Text(NoBusinessSnapshotEntry);
    public string NoSnapshot => Text(NoSnapshotEntry);
    public string FixedOrder => Text(FixedOrderEntry);
    public string NoHostScope => Text(NoHostScopeEntry);
    public string ConnectingTitle => Text(ConnectingTitleEntry);
    public string ConnectingMessage => Text(ConnectingMessageEntry);
    public string LoadingTitle => Text(LoadingTitleEntry);
    public string RefreshingTitle => Text(RefreshingTitleEntry);
    public string ReadFailedTitle => Text(ReadFailedTitleEntry);
    public string RefreshFailedTitle => Text(RefreshFailedTitleEntry);
    public string SelectionLostTitle => Text(SelectionLostTitleEntry);
    public string SelectionLostMessage => Text(SelectionLostMessageEntry);

    public string DescribeLifecycle(string rawCode) => rawCode switch
    {
        "TRACKING" => WithKnownCode(TrackingEntry, rawCode),
        "ARCHIVED" => WithKnownCode(ArchivedEntry, rawCode),
        _ => WithCode(UnknownLifecycleEntry, rawCode),
    };

    public string DescribePresence(string rawCode) => rawCode switch
    {
        "VISIBLE" => WithKnownCode(VisibleEntry, rawCode),
        "GONE" => WithKnownCode(GoneEntry, rawCode),
        "LONG_GONE_BUT_VISIBLE" => WithKnownCode(LongGoneEntry, rawCode),
        _ => WithCode(UnknownPresenceEntry, rawCode),
    };

    public string DescribeWorkType(string rawCode) => string.Format(
        CultureInfo.InvariantCulture,
        Text(WorkTypeChoiceEntry),
        rawCode);

    public string FormatHostAreas(IReadOnlyList<string> areas) => areas.Count == 0
        ? Text(HostAllAreasEntry)
        : F(HostAreasEntry, string.Join('、', areas));

    public string FormatLocalAreas(string localState, IReadOnlyList<string> areas) =>
        areas.Count == 0
            ? F(LocalUnlimitedEntry, localState)
            : F(LocalAreasEntry, localState, string.Join('、', areas));

    public string FormatSnapshotFacts(
        string committedAt,
        string commitId,
        long sequence,
        string pollTraceId) => F(
        SnapshotFactsEntry,
        committedAt,
        commitId,
        sequence.ToString("N0", CultureInfo.InvariantCulture),
        pollTraceId);

    public string FormatPageSummary(long count, int page, int totalPages) => F(
        PageSummaryEntry,
        count.ToString("N0", CultureInfo.InvariantCulture),
        page.ToString("N0", CultureInfo.InvariantCulture),
        totalPages.ToString("N0", CultureInfo.InvariantCulture));

    public string FormatHostFailed(string failure) => F(HostFailedMessageEntry, failure);
    public string HostFailedTitle => Text(HostFailedTitleEntry);
    public string WaitingSnapshot => Text(WaitingSnapshotEntry);
    public string FormatRetainedDuringRefresh(string at) => F(RetainedDuringRefreshEntry, at);
    public string FormatPriorFailureRetry(string at) => F(PriorFailureRetryEntry, at);
    public string NoSuccessfulSnapshot => Text(NoSuccessfulSnapshotEntry);
    public string FormatRetainedAfterFailure(string at) => F(RetainedAfterFailureEntry, at);
    public string FormatFailedAt(string at, string retained, string failure) => F(FailedAtEntry, at, retained, failure);
    public string FormatWatchSuccess(string at) => F(WatchSuccessEntry, at);
    public string WatchNoSuccess => Text(WatchNoSuccessEntry);
    public string FormatWatchFailure(string successful, string at) => F(WatchFailureEntry, successful, at);
    public string FormatFailureCorrelation(string correlationId) => F(FailureCorrelationEntry, correlationId);
    public string FormatLocalAreaContext(string value) => F(LocalAreaContextEntry, value);
    public string FormatAutoRefresh(int seconds) => F(AutoRefreshEntry, seconds);
    public string FormatLastSuccess(string at) => F(LastSuccessEntry, at);
    public string WaitingHost => Text(WaitingHostEntry);
    public string FormatConciseHostScope(IReadOnlyList<string>? areas, bool hasSnapshot)
    {
        if (!hasSnapshot)
        {
            return Text(HostNoScopeEntry);
        }

        if (areas is null || areas.Count == 0)
        {
            return Text(HostAllScopeEntry);
        }

        const int visibleAreaCount = 2;
        var visible = string.Join('、', areas.Take(visibleAreaCount));
        return areas.Count <= visibleAreaCount
            ? F(HostSomeScopeEntry, visible)
            : F(HostManyScopeEntry, visible, areas.Count);
    }

    public string FormatContextName(string value) => F(ContextNameEntry, value);
    public string FormatFacetName(string value) => F(FacetNameEntry, value);
    public string FormatPageSummaryName(string value) => F(PageSummaryNameEntry, value);
    public string FormatInfoName(string title, string message) => F(InfoNameEntry, title, message);
    public string FormatTopInfoName(string value) => F(TopInfoNameEntry, value);
    public string SourceNoFacts => Text(SourceNoFactsEntry);
    public string FormatSourceFacts(string facts) => F(SourceFactsEntry, facts);
    public string FormatSourceSummary(string source, string snapshotAt, string committedAt, string commitId, long sequence, string facts) =>
        F(SourceSummaryEntry, source, snapshotAt, committedAt, commitId, sequence, facts);
    public string TargetUnavailable => Text(TargetUnavailableEntry);
    public string FormatTargetNewer(string comparison) => F(TargetNewerEntry, comparison);
    public string FormatTargetOlder(string comparison) => F(TargetOlderEntry, comparison);
    public string FormatSameProjection(string comparison) => F(SameProjectionEntry, comparison);
    public string ProjectionMismatch => Text(ProjectionMismatchEntry);
    public string SourceCannotCompare => Text(SourceCannotCompareEntry);
    public string TargetCannotCompare => Text(TargetCannotCompareEntry);
    public string NoComparableFields => Text(NoComparableFieldsEntry);
    public string FormatFactsUnchanged(string fields) => F(FactsUnchangedEntry, fields);
    public string FormatFactsChanged(string differences) => F(FactsChangedEntry, differences);
    public string GenerationLabel => Text(GenerationLabelEntry);
    public string DemandStateLabel => Text(DemandStateLabelEntry);
    public string LifecycleLabel => Text(LifecycleLabelEntry);
    public string PresenceLabel => Text(PresenceLabelEntry);
    public string ReadabilityLabel => Text(ReadabilityLabelEntry);
    public string BlockersLabel => Text(BlockersLabelEntry);
    public string None => Text(NoneEntry);
    public string SourceNotProvided => WatchTextCatalog.For(Language).Common.SourceNotProvided;
    public string FormatFilterChoiceName(string label, string choice) => F(FilterChoiceNameEntry, label, choice);
    public string DescribeSource(string sourceName) => sourceName switch
    {
        "概览" => Text(OverviewSourceEntry),
        "资格审计" => Text(ReadabilitySourceEntry),
        "错误检索" => Text(ErrorSourceEntry),
        "当前关注" => Text(AttentionSourceEntry),
        _ => sourceName,
    };

    private string WithCode(WatchTextCatalogEntry meaning, string rawCode) =>
        $"{Text(meaning)} ({rawCode})";

    private string WithKnownCode(WatchTextCatalogEntry meaning, string rawCode) =>
        Language is WatchDisplayLanguage.SimplifiedChinese
            ? Text(meaning)
            : WithCode(meaning, rawCode);

    private string F(WatchTextCatalogEntry entry, params object[] values) =>
        string.Format(CultureInfo.InvariantCulture, Text(entry), values);
}
