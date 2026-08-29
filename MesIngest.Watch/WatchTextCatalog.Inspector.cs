using System.Globalization;

namespace MesIngest.Watch;

internal sealed partial class WatchInspectorText
{
    private static WatchTextCatalogEntry E(string id, string zh, string en) => new(id, zh, en);

    private static readonly WatchTextCatalogEntry WindowTitleEntry = E("inspector.window.title", "需求系列调查窗口", "Demand series Inspector");
    private static readonly WatchTextCatalogEntry WindowTitleWithSeriesEntry = E("inspector.window.titleWithSeries", "需求系列调查窗口 · {0}", "Demand series Inspector · {0}");
    private static readonly WatchTextCatalogEntry AppTitleEntry = E("inspector.window.appTitle", "制造执行系统接入运维台 · 需求系列调查窗口", "MesIngest Watch · Demand series Inspector");
    private static readonly WatchTextCatalogEntry AppTitleWithSeriesEntry = E("inspector.window.appTitleWithSeries", "制造执行系统接入运维台 · 需求系列调查窗口 · {0}", "MesIngest Watch · Demand series Inspector · {0}");
    private static readonly WatchTextCatalogEntry TitleBarAutomationEntry = E("inspector.window.titleBarAutomation", "需求系列调查窗口标题栏", "Demand series Inspector title bar");
    private static readonly WatchTextCatalogEntry ContextAutomationEntry = E("inspector.context.automation", "需求系列与冻结快照上下文", "Demand series and frozen snapshot context");
    private static readonly WatchTextCatalogEntry NoSelectionEntry = E("inspector.context.noSelection", "尚未选择需求系列", "No demand series selected");
    private static readonly WatchTextCatalogEntry NoSnapshotEntry = E("inspector.context.noSnapshot", "尚无冻结快照", "No frozen snapshot");
    private static readonly WatchTextCatalogEntry FrozenSnapshotEntry = E("inspector.context.snapshot", "冻结快照 {0} · {1}", "Frozen snapshot {0} · {1}");
    private static readonly WatchTextCatalogEntry GenerationAnalysisEntry = E("inspector.tab.generations", "世代分析", "Generation analysis");
    private static readonly WatchTextCatalogEntry EventsEntry = E("inspector.tab.events", "事件", "Events");
    private static readonly WatchTextCatalogEntry InvestigationTasksEntry = E("inspector.tabs.automation", "需求系列调查任务", "Demand series investigation tasks");
    private static readonly WatchTextCatalogEntry DemandGenerationsEntry = E("inspector.generations.heading", "运输需求代次", "Demand generations");
    private static readonly WatchTextCatalogEntry GenerationHelpEntry = E("inspector.generations.help", "滚动并选择 DemandId", "Scroll and select a DemandId");
    private static readonly WatchTextCatalogEntry GenerationCountEntry = E("inspector.generations.count", "{0} 个世代", "{0} generations");
    private static readonly WatchTextCatalogEntry LoadingGenerationsEntry = E("inspector.generations.loading", "正在读取世代", "Loading generations");
    private static readonly WatchTextCatalogEntry GenerationNavigationEntry = E("inspector.generations.navigation", "运输需求代次导航", "Demand generation navigation");
    private static readonly WatchTextCatalogEntry CurrentEntry = E("inspector.generation.current", "当前", "Current");
    private static readonly WatchTextCatalogEntry CurrentGenerationEntry = E("inspector.generation.currentMarker", "当前世代", "Current generation");
    private static readonly WatchTextCatalogEntry HistoricalGenerationEntry = E("inspector.generation.historicalMarker", "历史世代", "Historical generation");
    private static readonly WatchTextCatalogEntry GenerationNavigationNameEntry = E("inspector.generation.navigationName", "第 {0} 代；DemandId {1}；状态 {2}；{3}", "Generation {0}; DemandId {1}; status {2}; {3}");
    private static readonly WatchTextCatalogEntry GenerationIdentityEntry = E("inspector.generation.identity", "DemandId {0} · 第 {1} 代 · {2}", "DemandId {0} · generation {1} · {2}");
    private static readonly WatchTextCatalogEntry GenerationIdentityNameEntry = E("inspector.generation.identityName", "选中世代：DemandId {0}；第 {1} 代；状态 {2}；{3}", "Selected generation: DemandId {0}; generation {1}; status {2}; {3}");
    private static readonly WatchTextCatalogEntry PredecessorEntry = E("inspector.generation.predecessor", "前驱 {0}", "Predecessor {0}");
    private static readonly WatchTextCatalogEntry FirstGenerationEntry = E("inspector.generation.first", "需求系列首个运输需求代次", "First Demand generation in the series");
    private static readonly WatchTextCatalogEntry RelatedEventsEntry = E("inspector.generation.relatedEvents", "相关事件", "Related events");
    private static readonly WatchTextCatalogEntry RelatedEventsAutomationEntry = E("inspector.generation.relatedEventsAutomation", "查看选中世代的相关事件", "Show events related to the selected generation");
    private static readonly WatchTextCatalogEntry FormationReasonEntry = E("inspector.formation.reason", "形成原因", "Formation reason");
    private static readonly WatchTextCatalogEntry RawReasonCodeEntry = E("inspector.formation.rawCode", "内部原因码：{0}", "Raw reason code: {0}");
    private static readonly WatchTextCatalogEntry FormationReasonNameEntry = E("inspector.formation.reasonName", "形成原因：{0}；原始原因码：{1}", "Formation reason: {0}; raw reason code: {1}");
    private static readonly WatchTextCatalogEntry FormationFactsEntry = E("inspector.formation.facts", "Demand 形成事实", "Demand formation facts");
    private static readonly WatchTextCatalogEntry FormationFactsCountEntry = E("inspector.formation.factsCount", "Demand 形成事实，共 {0} 项", "Demand formation facts, {0} items");
    private static readonly WatchTextCatalogEntry FirstObservedEntry = E("inspector.reason.firstObserved", "首次观察到", "First observed");
    private static readonly WatchTextCatalogEntry PrearchiveEntry = E("inspector.reason.prearchive", "归档前消失后再现", "Reappeared before archive");
    private static readonly WatchTextCatalogEntry PostarchiveEntry = E("inspector.reason.postarchive", "归档后再次出现", "Reappeared after archive");
    private static readonly WatchTextCatalogEntry UnknownReasonEntry = E("inspector.reason.unknown", "形成原因暂无法确认", "Formation reason cannot be determined");
    private static readonly WatchTextCatalogEntry PredecessorFactEntry = E("inspector.fact.predecessor", "前代 Demand", "Predecessor Demand");
    private static readonly WatchTextCatalogEntry PredecessorObservationEntry = E("inspector.fact.predecessorObservation", "前代最后匹配观测", "Predecessor's last matching observation");
    private static readonly WatchTextCatalogEntry GoneFactEntry = E("inspector.fact.gone", "权威缺失 / GONE", "Authoritative absence / GONE");
    private static readonly WatchTextCatalogEntry ArchiveFactEntry = E("inspector.fact.archive", "需求系列归档", "Demand series archive");
    private static readonly WatchTextCatalogEntry FirstObservationFactEntry = E("inspector.fact.firstObservation", "首次匹配观测", "First matching observation");
    private static readonly WatchTextCatalogEntry NewObservationFactEntry = E("inspector.fact.newObservation", "新世代首次匹配观测", "New generation's first matching observation");
    private static readonly WatchTextCatalogEntry CreationEventFactEntry = E("inspector.fact.creationEvent", "创建事件", "Creation event");
    private static readonly WatchTextCatalogEntry MissingFactEntry = E("inspector.fact.missing", "冻结快照中未找到", "Not found in the frozen snapshot");
    private static readonly WatchTextCatalogEntry MissingOccurrenceEntry = E("inspector.fact.missingOccurrence", "冻结快照中未找到此项事实", "This fact was not found in the frozen snapshot");
    private static readonly WatchTextCatalogEntry OccurrenceEntry = E("inspector.fact.occurrence", "{0}{1}", "{0}{1}");
    private static readonly WatchTextCatalogEntry SequenceEntry = E("inspector.fact.sequence", " · #{0}", " · #{0}");
    private static readonly WatchTextCatalogEntry EvidenceEntry = E("inspector.fact.evidence", "PollTrace {0} · ProjectionCommit {1}", "PollTrace {0} · ProjectionCommit {1}");
    private static readonly WatchTextCatalogEntry MissingEvidenceEntry = E("inspector.fact.missingEvidence", "无 PollTrace 或 ProjectionCommit 提交证据", "No PollTrace or ProjectionCommit evidence");
    private static readonly WatchTextCatalogEntry FactAutomationEntry = E("inspector.fact.automation", "{0}：{1}；{2}；{3}", "{0}: {1}; {2}; {3}");
    private static readonly WatchTextCatalogEntry MesDiffHeadingEntry = E("inspector.mes.heading", "选中世代的 MES 查询前后差异", "MES query differences for the selected generation");
    private static readonly WatchTextCatalogEntry MesDiffHelpEntry = E("inspector.mes.help", "切换 DemandId 时，边界原始值会原子切换", "Changing DemandId atomically switches the boundary raw values");
    private static readonly WatchTextCatalogEntry NativeFieldCountEntry = E("inspector.mes.fieldCount", "{0} 个原生字段", "{0} native fields");
    private static readonly WatchTextCatalogEntry NativeFieldEntry = E("inspector.mes.field", "MES 原生字段", "Native MES field");
    private static readonly WatchTextCatalogEntry BeforeValueEntry = E("inspector.mes.before", "前驱最后查询值", "Predecessor's last query value");
    private static readonly WatchTextCatalogEntry AfterValueEntry = E("inspector.mes.after", "选中世代首次查询值", "Selected generation's first query value");
    private static readonly WatchTextCatalogEntry ChangeEntry = E("inspector.mes.change", "变化", "Change");
    private static readonly WatchTextCatalogEntry ChangedEntry = E("inspector.mes.changed", "已变化", "Changed");
    private static readonly WatchTextCatalogEntry UnchangedEntry = E("inspector.mes.unchanged", "保持不变", "Unchanged");
    private static readonly WatchTextCatalogEntry ScalarAutomationEntry = E("inspector.mes.scalarAutomation", "{0}：{1} → {2}，{3}", "{0}: {1} → {2}, {3}");
    private static readonly WatchTextCatalogEntry BeforeBoundaryEntry = E("inspector.boundary.before", "前代最后匹配观测", "Predecessor's last matching observation");
    private static readonly WatchTextCatalogEntry FirstBoundaryEntry = E("inspector.boundary.first", "首次匹配观测", "First matching observation");
    private static readonly WatchTextCatalogEntry NewBoundaryEntry = E("inspector.boundary.new", "新世代首次匹配观测", "New generation's first matching observation");
    private static readonly WatchTextCatalogEntry BoundaryMissingEntry = E("inspector.boundary.missing", "{0}：缺失", "{0}: missing");
    private static readonly WatchTextCatalogEntry BoundaryConflictEntry = E("inspector.boundary.conflict", "{0}：多行冲突", "{0}: multiple-row conflict");
    private static readonly WatchTextCatalogEntry BoundaryUniqueEntry = E("inspector.boundary.unique", "{0}：唯一可信原始行", "{0}: one trusted raw row");
    private static readonly WatchTextCatalogEntry BoundaryNotApplicableEntry = E("inspector.boundary.notApplicable", "{0}：不适用", "{0}: not applicable");
    private static readonly WatchTextCatalogEntry SourceNotProvidedEntry = E("inspector.value.sourceNotProvided", "来源未提供", "Source not provided");
    private static readonly WatchTextCatalogEntry NotApplicableEntry = E("inspector.value.notApplicable", "不适用", "Not applicable");
    private static readonly WatchTextCatalogEntry MesExplanationEntry = E("inspector.mes.explanation", "MES 字段差异只是边界两侧的观察证据，不是 TransportDemand/DemandId 形成原因。", "MES field differences are observations on each side of the boundary; they are not the reason a TransportDemand/DemandId was formed.");
    private static readonly WatchTextCatalogEntry FirstConclusionEntry = E("inspector.mes.firstConclusion", "{0} 是该需求系列的首个运输需求代次；{1}", "{0} is the first Demand generation in this series; {1}");
    private static readonly WatchTextCatalogEntry LaterConclusionEntry = E("inspector.mes.laterConclusion", "{0}形成第 {1} 代；{2}", "{0} formed generation {1}; {2}");
    private static readonly WatchTextCatalogEntry RawRowsEntry = E("inspector.rawRows.name", "MES 边界原始行", "MES boundary raw rows");
    private static readonly WatchTextCatalogEntry RawRowsHelpEntry = E("inspector.rawRows.help", "保留 Assignment、SeriesId、DemandId、七个 MES 原生字段、PollTrace 与 ProjectionCommit；不挑选 canonical row。", "Preserves Assignment, SeriesId, DemandId, seven native MES fields, PollTrace, and ProjectionCommit; no canonical row is selected.");
    private static readonly WatchTextCatalogEntry PermanentEventsEntry = E("inspector.events.heading", "永久事件", "Permanent events");
    private static readonly WatchTextCatalogEntry AllEventsEntry = E("inspector.events.all", "全部 Series 事件", "All demand series events");
    private static readonly WatchTextCatalogEntry SelectedEventsEntry = E("inspector.events.selected", "当前 Demand 相关事件", "Events for current Demand");
    private static readonly WatchTextCatalogEntry ShowAllEventsEntry = E("inspector.events.showAll", "显示全部 Series 事件", "Show all demand series events");
    private static readonly WatchTextCatalogEntry ShowSelectedEventsEntry = E("inspector.events.showSelected", "显示当前 Demand 相关事件", "Show events for the current Demand");
    private static readonly WatchTextCatalogEntry EventLogEntry = E("inspector.events.log", "事件日志", "Event log");
    private static readonly WatchTextCatalogEntry EventLogHelpEntry = E("inspector.events.logHelp", "事件是冻结审计事实；切换过滤不会改变世代分析中的选择", "Events are frozen audit facts; changing the filter does not change the generation selection");
    private static readonly WatchTextCatalogEntry AllEventContextEntry = E("inspector.events.allContext", "全部 Series 事件 · DemandId {0} · 冻结快照内按 SeriesSequence 展示 {1} 条", "All demand series events · DemandId {0} · {1} shown by SeriesSequence in the frozen snapshot");
    private static readonly WatchTextCatalogEntry SelectedEventContextEntry = E("inspector.events.selectedContext", "当前 Demand 相关事件 · DemandId {0} · {1} / {2} 条", "Events for current Demand · DemandId {0} · {1} of {2}");
    private static readonly WatchTextCatalogEntry EventGridEntry = E("inspector.events.grid", "DemandSeries 永久事件", "Permanent DemandSeries events");
    private static readonly WatchTextCatalogEntry EventHelpEntry = E("inspector.events.help", "事件字段保持原始 SeriesSequence、EventId、SeriesId、OccurredAt、EventType、SubjectKind、SubjectId、PollTraceId、ProjectionCommitId、PayloadVersion 与 PayloadJson。过滤只改变同一冻结集合的本地视图。", "Event fields preserve raw SeriesSequence, EventId, SeriesId, OccurredAt, EventType, SubjectKind, SubjectId, PollTraceId, ProjectionCommitId, PayloadVersion, and PayloadJson. Filtering changes only the local view of the same frozen set.");
    private static readonly WatchTextCatalogEntry ReadStateEntry = E("inspector.state.read", "需求系列调查器读取状态", "Demand series Inspector read status");
    private static readonly WatchTextCatalogEntry NoNoticeEntry = E("inspector.state.noNotice", "当前无通知", "No current notification");
    private static readonly WatchTextCatalogEntry ClearedTitleEntry = E("inspector.state.clearedTitle", "当前选择已清除", "Current selection cleared");
    private static readonly WatchTextCatalogEntry ClearedMessageEntry = E("inspector.state.clearedMessage", "所选需求系列已离开最新结果；没有自动选择另一需求系列。", "The selected demand series left the latest result; another series was not selected automatically.");
    private static readonly WatchTextCatalogEntry LoadingDetailEntry = E("inspector.state.loadingDetail", "正在读取所选需求系列详情", "Loading selected demand series detail");
    private static readonly WatchTextCatalogEntry LoadingEntry = E("inspector.state.loading", "正在读取", "loading");
    private static readonly WatchTextCatalogEntry UnavailableEntry = E("inspector.state.unavailable", "不可用", "unavailable");
    private static readonly WatchTextCatalogEntry StableIdentityEntry = E("inspector.context.identity", "生命周期 {0} · WorkType {1} · SUBLOT {2}", "Lifecycle {0} · WorkType {1} · SUBLOT {2}");
    private static readonly WatchTextCatalogEntry PresenceContextEntry = E("inspector.context.presence", "当前出现状态 {0}", "Current presence {0}");
    private static readonly WatchTextCatalogEntry SeriesContextEntry = E("inspector.context.series", "Series {0}", "SeriesId {0}");
    private static readonly WatchTextCatalogEntry ContextNameFormatEntry = E("inspector.context.nameFormat", "{0}；{1}；{2}；{3}", "{0}; {1}; {2}; {3}");
    private static readonly WatchTextCatalogEntry StatusNameEntry = E("inspector.state.name", "{0}。{1}", "{0}. {1}");
    private static readonly WatchTextCatalogEntry DetailStateNameEntry = E("inspector.state.detailName", "所选需求系列详情{0}", "Selected demand series detail {0}");
    private static readonly WatchTextCatalogEntry FormationStateNameEntry = E("inspector.state.formationName", "Demand 形成原因{0}", "Demand formation reason {0}");
    private static readonly WatchTextCatalogEntry FormationFactsStateNameEntry = E("inspector.state.formationFactsName", "Demand 形成事实{0}", "Demand formation facts {0}");
    private static readonly WatchTextCatalogEntry ScalarBoundaryStateNameEntry = E("inspector.state.scalarBoundaryName", "MES 标量对比边界来源{0}", "MES scalar comparison boundary source {0}");
    private static readonly WatchTextCatalogEntry RawRowsStateNameEntry = E("inspector.state.rawRowsName", "MES 边界原始行{0}", "MES boundary raw rows {0}");
    private static readonly WatchTextCatalogEntry EventContextStateNameEntry = E("inspector.state.eventContextName", "事件 DemandId 过滤上下文{0}", "Event DemandId filter context {0}");
    private static readonly WatchTextCatalogEntry EventGridStateNameEntry = E("inspector.state.eventGridName", "DemandSeries 永久事件{0}", "Permanent demand series events {0}");
    private static readonly WatchTextCatalogEntry RawRowsCountEntry = E("inspector.rawRows.countName", "MES 边界原始行；{0}；{1}；共 {2} 行", "MES boundary raw rows; {0}; {1}; {2} rows");
    private static readonly WatchTextCatalogEntry EventGridContextEntry = E("inspector.events.gridContext", "{0}；{1}", "{0}; {1}");
    private static readonly WatchTextCatalogEntry BoundaryColumnEntry = E("inspector.rawRows.boundaryColumn", "边界", "Boundary");
    private static readonly WatchTextCatalogEntry OrdinalColumnEntry = E("inspector.columns.ordinal", "序号", "Ordinal");
    private static readonly WatchTextCatalogEntry AssignmentColumnEntry = E("inspector.columns.assignment", "归属", "Assignment");
    private static readonly WatchTextCatalogEntry SeriesIdColumnEntry = E("inspector.columns.seriesId", "需求系列标识", "SeriesId");
    private static readonly WatchTextCatalogEntry DemandIdColumnEntry = E("inspector.columns.demandId", "运输需求标识", "DemandId");
    private static readonly WatchTextCatalogEntry WorkTypeColumnEntry = E("inspector.columns.workType", "工序类型", "TASK_TYPE");
    private static readonly WatchTextCatalogEntry SublotColumnEntry = E("inspector.columns.sublot", "子批次", "SUBLOT");
    private static readonly WatchTextCatalogEntry AreaColumnEntry = E("inspector.columns.area", "区域", "AREA");
    private static readonly WatchTextCatalogEntry EqpColumnEntry = E("inspector.columns.eqp", "设备", "EQP");
    private static readonly WatchTextCatalogEntry StepColumnEntry = E("inspector.columns.step", "下一工序", "STEP");
    private static readonly WatchTextCatalogEntry SourceDateColumnEntry = E("inspector.columns.sourceDate", "来源时间", "DATES / MesSourceDate");
    private static readonly WatchTextCatalogEntry PackageColumnEntry = E("inspector.columns.package", "封装形式", "PACKAGE");
    private static readonly WatchTextCatalogEntry PollTraceColumnEntry = E("inspector.columns.pollTrace", "轮询追踪", "PollTrace");
    private static readonly WatchTextCatalogEntry ProjectionCommitColumnEntry = E("inspector.columns.projectionCommit", "投影提交", "ProjectionCommit");
    private static readonly WatchTextCatalogEntry SeriesSequenceColumnEntry = E("inspector.columns.seriesSequence", "需求系列序号", "SeriesSequence");
    private static readonly WatchTextCatalogEntry EventIdColumnEntry = E("inspector.columns.eventId", "事件标识", "EventId");
    private static readonly WatchTextCatalogEntry OccurredAtColumnEntry = E("inspector.columns.occurredAt", "发生时间", "OccurredAt");
    private static readonly WatchTextCatalogEntry EventTypeColumnEntry = E("inspector.columns.eventType", "事件类型", "EventType");
    private static readonly WatchTextCatalogEntry SubjectKindColumnEntry = E("inspector.columns.subjectKind", "主体类型", "SubjectKind");
    private static readonly WatchTextCatalogEntry SubjectIdColumnEntry = E("inspector.columns.subjectId", "主体标识", "SubjectId");
    private static readonly WatchTextCatalogEntry PollTraceIdColumnEntry = E("inspector.columns.pollTraceId", "轮询追踪标识", "PollTraceId");
    private static readonly WatchTextCatalogEntry ProjectionCommitIdColumnEntry = E("inspector.columns.projectionCommitId", "投影提交标识", "ProjectionCommitId");
    private static readonly WatchTextCatalogEntry PayloadVersionColumnEntry = E("inspector.columns.payloadVersion", "载荷版本", "PayloadVersion");
    private static readonly WatchTextCatalogEntry PayloadJsonColumnEntry = E("inspector.columns.payloadJson", "载荷内容", "PayloadJson");
    private static readonly WatchTextCatalogEntry FrozenEventCountEntry = E("inspector.events.frozenCount", "冻结事件总数 {0}", "{0} frozen events in total");
    private static readonly WatchTextCatalogEntry RetainedSnapshotEntry = E("inspector.state.retainedSnapshot", "目标列表已提交冻结快照 {0}；正文仍保留上一成功冻结快照 {1}，直到匹配的新详情原子提交。", "The target list committed frozen snapshot {0}; the body retains the last successful frozen snapshot {1} until matching new detail commits atomically.");
    private static readonly WatchTextCatalogEntry DetailReadFailedEntry = E("inspector.state.detailReadFailed", "详情读取失败", "Detail read failed");
    private static readonly WatchTextCatalogEntry DetailRefreshFailedEntry = E("inspector.state.detailRefreshFailed", "详情刷新失败，已保留上次证据", "Detail refresh failed; previous evidence retained");
    private static readonly WatchTextCatalogEntry PagePausedEntry = E("inspector.state.pagePaused", "DemandSeries 页面刷新已暂停", "DemandSeries page refresh paused");
    private static readonly WatchTextCatalogEntry RefreshingRetainedEntry = E("inspector.state.refreshingRetained", "正在刷新详情，已保留上次证据", "Refreshing detail; previous evidence retained");
    private static readonly WatchTextCatalogEntry SnapshotPendingEntry = E("inspector.state.snapshotPending", "详情尚未与最新快照同步，已保留上次证据", "Detail is not yet synchronized with the latest snapshot; previous evidence retained");
    private static readonly WatchTextCatalogEntry RefreshFailedEntry = E("inspector.state.refreshFailed", "刷新失败，已保留上次证据", "Refresh failed; previous evidence retained");
    private static readonly WatchTextCatalogEntry ReadingSelectionEntry = E("inspector.state.readingSelection", "正在读取所选需求系列详情", "Reading selected demand series detail");
    private static readonly WatchTextCatalogEntry SourceComparisonEntry = E("inspector.state.sourceComparison", "来源快照比较", "Source snapshot comparison");
    private static readonly WatchTextCatalogEntry PausedSnapshotEntry = E("inspector.state.pausedSnapshot", "保留冻结快照 {0}；返回 DemandSeries 页面后恢复刷新。", "Frozen snapshot {0} is retained; refresh resumes after returning to the DemandSeries page.");
    private static readonly WatchTextCatalogEntry MissingCreationEntry = E("inspector.reason.missingCreation", "（创建事件缺失）", "(creation event missing)");
    private static readonly WatchTextCatalogEntry ConflictingCreationEntry = E("inspector.reason.conflictingCreation", "（创建事件冲突）", "(creation events conflict)");
    private static readonly WatchTextCatalogEntry InvalidReasonPayloadEntry = E("inspector.reason.invalidPayload", "（原因载荷无效）", "(invalid reason payload)");
    private static readonly WatchTextCatalogEntry MissingReasonCodeEntry = E("inspector.reason.missingCode", "（原因码缺失）", "(reason code missing)");

    private static readonly IReadOnlyList<WatchTextCatalogEntry> OwnEntries =
    [
        WindowTitleEntry, WindowTitleWithSeriesEntry, AppTitleEntry, AppTitleWithSeriesEntry,
        TitleBarAutomationEntry, ContextAutomationEntry, NoSelectionEntry, NoSnapshotEntry,
        FrozenSnapshotEntry, GenerationAnalysisEntry, EventsEntry, InvestigationTasksEntry,
        DemandGenerationsEntry, GenerationHelpEntry, GenerationCountEntry,
        LoadingGenerationsEntry, GenerationNavigationEntry, CurrentEntry,
        CurrentGenerationEntry, HistoricalGenerationEntry, GenerationNavigationNameEntry,
        GenerationIdentityEntry, GenerationIdentityNameEntry, PredecessorEntry,
        FirstGenerationEntry, RelatedEventsEntry, RelatedEventsAutomationEntry,
        FormationReasonEntry, RawReasonCodeEntry, FormationReasonNameEntry,
        FormationFactsEntry, FormationFactsCountEntry, FirstObservedEntry, PrearchiveEntry,
        PostarchiveEntry, UnknownReasonEntry, PredecessorFactEntry,
        PredecessorObservationEntry, GoneFactEntry, ArchiveFactEntry,
        FirstObservationFactEntry, NewObservationFactEntry, CreationEventFactEntry,
        MissingFactEntry, MissingOccurrenceEntry, OccurrenceEntry, SequenceEntry,
        EvidenceEntry, MissingEvidenceEntry, FactAutomationEntry, MesDiffHeadingEntry,
        MesDiffHelpEntry, NativeFieldCountEntry, NativeFieldEntry, BeforeValueEntry,
        AfterValueEntry, ChangeEntry, ChangedEntry, UnchangedEntry, ScalarAutomationEntry,
        BeforeBoundaryEntry, FirstBoundaryEntry, NewBoundaryEntry, BoundaryMissingEntry,
        BoundaryConflictEntry, BoundaryUniqueEntry, BoundaryNotApplicableEntry,
        SourceNotProvidedEntry, NotApplicableEntry, MesExplanationEntry,
        FirstConclusionEntry, LaterConclusionEntry, RawRowsEntry, RawRowsHelpEntry,
        PermanentEventsEntry, AllEventsEntry, SelectedEventsEntry, ShowAllEventsEntry,
        ShowSelectedEventsEntry, EventLogEntry,
        EventLogHelpEntry, AllEventContextEntry, SelectedEventContextEntry, EventGridEntry,
        EventHelpEntry, ReadStateEntry, NoNoticeEntry, ClearedTitleEntry,
        ClearedMessageEntry, LoadingDetailEntry, LoadingEntry, UnavailableEntry,
        StableIdentityEntry, PresenceContextEntry, SeriesContextEntry, ContextNameFormatEntry,
        StatusNameEntry, DetailStateNameEntry, FormationStateNameEntry,
        FormationFactsStateNameEntry, ScalarBoundaryStateNameEntry, RawRowsStateNameEntry,
        EventContextStateNameEntry, EventGridStateNameEntry, RawRowsCountEntry,
        EventGridContextEntry, BoundaryColumnEntry, OrdinalColumnEntry,
        AssignmentColumnEntry, SeriesIdColumnEntry, DemandIdColumnEntry,
        WorkTypeColumnEntry, SublotColumnEntry, AreaColumnEntry, EqpColumnEntry,
        StepColumnEntry, SourceDateColumnEntry, PackageColumnEntry,
        PollTraceColumnEntry, ProjectionCommitColumnEntry,
        SeriesSequenceColumnEntry, EventIdColumnEntry, OccurredAtColumnEntry,
        EventTypeColumnEntry, SubjectKindColumnEntry, SubjectIdColumnEntry,
        PollTraceIdColumnEntry, ProjectionCommitIdColumnEntry,
        PayloadVersionColumnEntry, PayloadJsonColumnEntry, FrozenEventCountEntry,
        RetainedSnapshotEntry, DetailReadFailedEntry, DetailRefreshFailedEntry,
        PagePausedEntry, RefreshingRetainedEntry, SnapshotPendingEntry,
        RefreshFailedEntry, ReadingSelectionEntry, SourceComparisonEntry,
        PausedSnapshotEntry, MissingCreationEntry, ConflictingCreationEntry,
        InvalidReasonPayloadEntry, MissingReasonCodeEntry,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => OwnEntries;

    public string WindowTitle => Text(WindowTitleEntry);
    public string AppTitle => Text(AppTitleEntry);
    public string FormatWindowTitle(string seriesId) => F(WindowTitleWithSeriesEntry, seriesId);
    public string FormatAppTitle(string seriesId) => F(AppTitleWithSeriesEntry, seriesId);
    public string TitleBarAutomationName => Text(TitleBarAutomationEntry);
    public string ContextAutomationName => Text(ContextAutomationEntry);
    public string NoSelection => Text(NoSelectionEntry);
    public string NoSnapshot => Text(NoSnapshotEntry);
    public string FormatFrozenSnapshot(string reference, string at) => F(FrozenSnapshotEntry, reference, at);
    public string GenerationAnalysis => Text(GenerationAnalysisEntry);
    public string Events => Text(EventsEntry);
    public string InvestigationTasks => Text(InvestigationTasksEntry);
    public string DemandGenerations => Text(DemandGenerationsEntry);
    public string GenerationHelp => Text(GenerationHelpEntry);
    public string FormatGenerationCount(int count) => F(GenerationCountEntry, count.ToString("N0", CultureInfo.InvariantCulture));
    public string LoadingGenerations => Text(LoadingGenerationsEntry);
    public string GenerationNavigation => Text(GenerationNavigationEntry);
    public string Current => Text(CurrentEntry);
    public string CurrentGeneration => Text(CurrentGenerationEntry);
    public string HistoricalGeneration => Text(HistoricalGenerationEntry);
    public string FormatGenerationNavigation(int generation, string demandId, string status, string marker) => F(GenerationNavigationNameEntry, generation, demandId, status, marker);
    public string FormatGenerationIdentity(string demandId, int generation, string status) => F(GenerationIdentityEntry, demandId, generation, status);
    public string FormatGenerationIdentityName(string demandId, int generation, string status, string marker) => F(GenerationIdentityNameEntry, demandId, generation, status, marker);
    public string FormatPredecessor(string id) => F(PredecessorEntry, id);
    public string FirstGeneration => Text(FirstGenerationEntry);
    public string RelatedEvents => Text(RelatedEventsEntry);
    public string RelatedEventsAutomationName => Text(RelatedEventsAutomationEntry);
    public string FormationReason => Text(FormationReasonEntry);
    public string FormatRawReasonCode(string code) => F(RawReasonCodeEntry, code);
    public string FormatFormationReasonName(string label, string code) => F(FormationReasonNameEntry, label, code);
    public string FormationFacts => Text(FormationFactsEntry);
    public string FormatFormationFactsCount(int count) => F(FormationFactsCountEntry, count.ToString("N0", CultureInfo.InvariantCulture));
    public string DescribeReason(string rawCode) => rawCode switch
    {
        "FIRST_OBSERVED" => Text(FirstObservedEntry),
        "PREARCHIVE_REAPPEARANCE" => Text(PrearchiveEntry),
        "POSTARCHIVE_REAPPEARANCE" => Text(PostarchiveEntry),
        _ => Text(UnknownReasonEntry),
    };
    public string FactLabel(WatchDemandFormationFactKind kind, bool isNewObservation = false) => kind switch
    {
        WatchDemandFormationFactKind.PredecessorIdentity => Text(PredecessorFactEntry),
        WatchDemandFormationFactKind.PredecessorLastObservation => Text(PredecessorObservationEntry),
        WatchDemandFormationFactKind.AuthoritativeGone => Text(GoneFactEntry),
        WatchDemandFormationFactKind.Archive => Text(ArchiveFactEntry),
        WatchDemandFormationFactKind.FirstObservation when isNewObservation => Text(NewObservationFactEntry),
        WatchDemandFormationFactKind.FirstObservation => Text(FirstObservationFactEntry),
        _ => Text(CreationEventFactEntry),
    };
    public string CreationEventFact => Text(CreationEventFactEntry);
    public string MissingFact => Text(MissingFactEntry);
    public string MissingOccurrence => Text(MissingOccurrenceEntry);
    public string FormatOccurrence(string at, long? sequence) => F(OccurrenceEntry, at, sequence is null ? string.Empty : F(SequenceEntry, sequence.Value));
    public string FormatEvidence(string? pollTraceId, string? commitId) => F(EvidenceEntry, pollTraceId ?? string.Empty, commitId ?? string.Empty);
    public string MissingEvidence => Text(MissingEvidenceEntry);
    public string FormatFactAutomation(string label, string value, string occurrence, string evidence) => F(FactAutomationEntry, label, value, occurrence, evidence);
    public string MesDiffHeading => Text(MesDiffHeadingEntry);
    public string MesDiffHelp => Text(MesDiffHelpEntry);
    public string FormatNativeFieldCount(int count) => F(NativeFieldCountEntry, count.ToString("N0", CultureInfo.InvariantCulture));
    public string NativeField => Text(NativeFieldEntry);
    public string BeforeValue => Text(BeforeValueEntry);
    public string AfterValue => Text(AfterValueEntry);
    public string Change => Text(ChangeEntry);
    public string Changed => Text(ChangedEntry);
    public string Unchanged => Text(UnchangedEntry);
    public string FormatScalarAutomation(string field, string before, string after, string change) => F(ScalarAutomationEntry, field, before, after, change);
    public string BeforeBoundary => Text(BeforeBoundaryEntry);
    public string FirstBoundary => Text(FirstBoundaryEntry);
    public string NewBoundary => Text(NewBoundaryEntry);
    public string FormatBoundaryState(string label, WatchDemandMesBoundaryState state) => state switch
    {
        WatchDemandMesBoundaryState.Missing => F(BoundaryMissingEntry, label),
        WatchDemandMesBoundaryState.Conflict => F(BoundaryConflictEntry, label),
        WatchDemandMesBoundaryState.Unique => F(BoundaryUniqueEntry, label),
        _ => F(BoundaryNotApplicableEntry, label),
    };
    public string SourceNotProvided => Text(SourceNotProvidedEntry);
    public string NotApplicable => Text(NotApplicableEntry);
    public string MesExplanation => Text(MesExplanationEntry);
    public string FormatFirstConclusion(string demandId, string explanation) => F(FirstConclusionEntry, demandId, explanation);
    public string FormatLaterConclusion(string reason, int generation, string explanation) => F(LaterConclusionEntry, reason, generation, explanation);
    public string RawRows => Text(RawRowsEntry);
    public string RawRowsHelp => Text(RawRowsHelpEntry);
    public string PermanentEvents => Text(PermanentEventsEntry);
    public string AllEvents => Text(AllEventsEntry);
    public string SelectedEvents => Text(SelectedEventsEntry);
    public string ShowAllEvents => Text(ShowAllEventsEntry);
    public string ShowSelectedEvents => Text(ShowSelectedEventsEntry);
    public string EventLog => Text(EventLogEntry);
    public string EventLogHelp => Text(EventLogHelpEntry);
    public string FormatAllEventContext(string demandId, int count) => F(AllEventContextEntry, demandId, count.ToString("N0", CultureInfo.InvariantCulture));
    public string FormatSelectedEventContext(string demandId, int related, int total) => F(SelectedEventContextEntry, demandId, related.ToString("N0", CultureInfo.InvariantCulture), total.ToString("N0", CultureInfo.InvariantCulture));
    public string EventGrid => Text(EventGridEntry);
    public string EventHelp => Text(EventHelpEntry);
    public string ReadState => Text(ReadStateEntry);
    public string NoNotice => Text(NoNoticeEntry);
    public string ClearedTitle => Text(ClearedTitleEntry);
    public string ClearedMessage => Text(ClearedMessageEntry);
    public string LoadingDetail => Text(LoadingDetailEntry);
    public string Loading => Text(LoadingEntry);
    public string Unavailable => Text(UnavailableEntry);
    public string FormatStableIdentity(string lifecycle, string workType, string sublot) => F(StableIdentityEntry, lifecycle, workType, sublot);
    public string FormatPresenceContext(string presence) => F(PresenceContextEntry, presence);
    public string FormatSeriesContext(string seriesId) => F(SeriesContextEntry, seriesId);
    public string FormatContextName(string series, string presence, string identity, string snapshot) => F(ContextNameFormatEntry, series, presence, identity, snapshot);
    public string FormatStatusName(string title, string message) => F(StatusNameEntry, title, message);
    public string FormatDetailStateName(string state) => F(DetailStateNameEntry, state);
    public string FormatFormationStateName(string state) => F(FormationStateNameEntry, state);
    public string FormatFormationFactsStateName(string state) => F(FormationFactsStateNameEntry, state);
    public string FormatScalarBoundaryStateName(string state) => F(ScalarBoundaryStateNameEntry, state);
    public string FormatRawRowsStateName(string state) => F(RawRowsStateNameEntry, state);
    public string FormatEventContextStateName(string state) => F(EventContextStateNameEntry, state);
    public string FormatEventGridStateName(string state) => F(EventGridStateNameEntry, state);
    public string FormatRawRowsCount(string before, string after, int count) => F(RawRowsCountEntry, before, after, count.ToString("N0", CultureInfo.InvariantCulture));
    public string FormatEventGridContext(string grid, string context) => F(EventGridContextEntry, grid, context);
    public string BoundaryColumn => Text(BoundaryColumnEntry);
    public IReadOnlyList<string> RawObservationColumnHeaders =>
    [
        Text(BoundaryColumnEntry), Text(OrdinalColumnEntry), Text(AssignmentColumnEntry),
        Text(SeriesIdColumnEntry), Text(DemandIdColumnEntry), Text(WorkTypeColumnEntry),
        Text(SublotColumnEntry), Text(AreaColumnEntry), Text(EqpColumnEntry),
        Text(StepColumnEntry), Text(SourceDateColumnEntry), Text(PackageColumnEntry),
        Text(PollTraceColumnEntry), Text(ProjectionCommitColumnEntry),
    ];
    public IReadOnlyList<string> EventColumnHeaders =>
    [
        Text(SeriesSequenceColumnEntry), Text(EventIdColumnEntry), Text(SeriesIdColumnEntry),
        Text(OccurredAtColumnEntry), Text(EventTypeColumnEntry), Text(SubjectKindColumnEntry),
        Text(SubjectIdColumnEntry), Text(PollTraceIdColumnEntry),
        Text(ProjectionCommitIdColumnEntry), Text(PayloadVersionColumnEntry),
        Text(PayloadJsonColumnEntry),
    ];
    public string FormatFrozenEventCount(int count) => F(FrozenEventCountEntry, count.ToString("N0", CultureInfo.InvariantCulture));
    public string FormatRetainedSnapshot(string target, string retained) => F(RetainedSnapshotEntry, target, retained);
    public string DetailReadFailed => Text(DetailReadFailedEntry);
    public string DetailRefreshFailed => Text(DetailRefreshFailedEntry);
    public string PagePaused => Text(PagePausedEntry);
    public string RefreshingRetained => Text(RefreshingRetainedEntry);
    public string SnapshotPending => Text(SnapshotPendingEntry);
    public string RefreshFailed => Text(RefreshFailedEntry);
    public string ReadingSelection => Text(ReadingSelectionEntry);
    public string SourceComparison => Text(SourceComparisonEntry);
    public string FormatPausedSnapshot(string reference) => F(PausedSnapshotEntry, reference);
    public string MissingCreation => Text(MissingCreationEntry);
    public string ConflictingCreation => Text(ConflictingCreationEntry);
    public string InvalidReasonPayload => Text(InvalidReasonPayloadEntry);
    public string MissingReasonCode => Text(MissingReasonCodeEntry);

    private string F(WatchTextCatalogEntry entry, params object[] values) =>
        string.Format(CultureInfo.InvariantCulture, Text(entry), values);
}
