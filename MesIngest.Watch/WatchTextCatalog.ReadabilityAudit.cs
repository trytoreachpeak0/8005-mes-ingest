using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal sealed record WatchValueSemanticDescription(
    WatchDisplayValueKind Kind,
    string Heading,
    string Help);

internal sealed partial class WatchReadabilityAuditText
{
    private static WatchTextCatalogEntry E(string id, string zh, string en) => new(id, zh, en);

    private static readonly WatchTextCatalogEntry PageTitleEntry = E("audit.page.title", "资格审计", "Eligibility audit");
    private static readonly WatchTextCatalogEntry PageSubtitleEntry = E("audit.page.subtitle", "解释每个 TransportDemand 当前能否被外部读取，以及为什么", "Explain whether every TransportDemand is externally readable now, and why");
    private static readonly WatchTextCatalogEntry CurrentObservationEntry = E("audit.section.observation", "当前唯一 MES 观测", "Current unique MES observation");
    private static readonly WatchTextCatalogEntry CurrentBlockerEntry = E("audit.section.blocker", "当前阻断条件", "Current blocking condition");
    private static readonly WatchTextCatalogEntry ExternalReadabilityEntry = E("audit.section.qualification", "外部可读资格", "External readability");
    private static readonly WatchTextCatalogEntry MissingSemanticsEntry = E("audit.section.valueSemantics", "缺值与查询语义", "Missing-value and query semantics");
    private static readonly WatchTextCatalogEntry AllEntry = E("audit.filter.all", "全部", "All");
    private static readonly WatchTextCatalogEntry ReadableEntry = E("audit.state.readable", "外部可读", "Readable");
    private static readonly WatchTextCatalogEntry NotReadableEntry = E("audit.state.notReadable", "不可读", "Not readable");
    private static readonly WatchTextCatalogEntry BlockedEntry = E("audit.state.blocked", "阻断", "Blocked");
    private static readonly WatchTextCatalogEntry StateFilterEntry = E("audit.filter.outcome", "资格结果", "Eligibility outcome");
    private static readonly WatchTextCatalogEntry WorkTypeFilterEntry = E("audit.filter.workType", "工序类型", "Work type");
    private static readonly WatchTextCatalogEntry BlockerFilterEntry = E("audit.filter.blocker", "不可读原因", "Unreadable reason");
    private static readonly WatchTextCatalogEntry SearchFilterEntry = E("audit.filter.search", "DemandId / SUBLOT", "DemandId / SUBLOT");
    private static readonly WatchTextCatalogEntry AreaFilterEntry = E("audit.filter.area", "AREA 筛选", "AREA filter");
    private static readonly WatchTextCatalogEntry ApplyFiltersEntry = E("audit.filter.apply", "应用条件", "Apply filters");
    private static readonly WatchTextCatalogEntry AllWorkTypesEntry = E("audit.filter.allWorkTypes", "全部工序类型", "All work types");
    private static readonly WatchTextCatalogEntry AllBlockersEntry = E("audit.filter.allBlockers", "全部原因", "All reasons");
    private static readonly WatchTextCatalogEntry MasterHeadingEntry = E("audit.master.heading", "全部 TransportDemand", "All TransportDemand");
    private static readonly WatchTextCatalogEntry DemandIdLabelEntry = E("audit.label.demandId", "Demand 标识", "Demand ID");
    private static readonly WatchTextCatalogEntry WorkTypeLabelEntry = E("audit.label.workType", "工序类型", "Work type");
    private static readonly WatchTextCatalogEntry SublotLabelEntry = E("audit.label.sublot", "批次", "SUBLOT");
    private static readonly WatchTextCatalogEntry PreviousPageEntry = E("audit.paging.previous", "上一页", "Previous");
    private static readonly WatchTextCatalogEntry NextPageEntry = E("audit.paging.next", "下一页", "Next");
    private static readonly WatchTextCatalogEntry GoToPageEntry = E("audit.paging.go", "跳转", "Go");
    private static readonly WatchTextCatalogEntry ClearFiltersEntry = E("audit.paging.clear", "清除条件", "Clear filters");
    private static readonly WatchTextCatalogEntry PerPageEntry = E("audit.paging.perPage", "每页", "Per page");
    private static readonly WatchTextCatalogEntry ViewSeriesEntry = E("audit.detail.viewSeries", "查看所属系列", "View owning series");
    private static readonly WatchTextCatalogEntry DeepEvidenceEntry = E("audit.detail.deepEvidence", "查看完整结构化证据", "View complete structured evidence");
    private static readonly WatchTextCatalogEntry QualificationChecksEntry = E("audit.detail.checks", "资格检查", "Qualification checks");
    private static readonly WatchTextCatalogEntry BlockerEvidenceEntry = E("audit.detail.blockerEvidence", "阻断证据", "Blocking evidence");
    private static readonly WatchTextCatalogEntry RawObservationsEntry = E("audit.detail.rawObservations", "原始观测", "Raw observations");
    private static readonly WatchTextCatalogEntry NoHostSnapshotEntry = E("audit.state.noHostSnapshot", "尚无 Host 资格审计快照", "No Host eligibility-audit snapshot");
    private static readonly WatchTextCatalogEntry NoSnapshotEntry = E("audit.state.noSnapshot", "尚无资格审计快照", "No eligibility-audit snapshot");
    private static readonly WatchTextCatalogEntry HostNoScopeEntry = E("audit.state.hostNoScope", "Host 已提交范围：尚无快照", "Host committed scope: no snapshot");
    private static readonly WatchTextCatalogEntry NotSelectedEntry = E("audit.detail.notSelected", "尚未选择 Demand", "No Demand selected");
    private static readonly WatchTextCatalogEntry SelectDemandEntry = E("audit.detail.select", "从左侧列表选择 Demand 后读取同一审计快照的资格检查与全部证据。", "Select a Demand in the left list to read its qualification checks and complete evidence from the same audit snapshot.");
    private static readonly WatchTextCatalogEntry NoBlockerEntry = E("audit.blocker.none", "无阻断条件", "No blocking condition");
    private static readonly WatchTextCatalogEntry UnknownBlockerEntry = E("audit.blocker.unknown", "其他/未知阻断原因", "Other or unknown blocking reason");
    private static readonly WatchTextCatalogEntry SourceNotProvidedHelpEntry = E("audit.value.sourceNotProvided.help", "源值为 NULL", "Source value is NULL");
    private static readonly WatchTextCatalogEntry SystemUnknownHelpEntry = E("audit.value.systemUnknown.help", "未知动态码保留原值", "Unknown dynamic code retained");
    private static readonly WatchTextCatalogEntry NotApplicableHelpEntry = E("audit.value.notApplicable.help", "该字段不参与此工序", "Field does not apply to this work type");
    private static readonly WatchTextCatalogEntry NotLoadedHelpEntry = E("audit.value.notLoaded.help", "详情仍在加载", "Detail is still loading");
    private static readonly WatchTextCatalogEntry EmptyResultHelpEntry = E("audit.value.emptyResult.help", "成功返回 0 条", "Successful result: 0 rows");
    private static readonly WatchTextCatalogEntry ReadFailedHelpEntry = E("audit.value.readFailed.help", "读取失败；保留上次成功值及其时点", "Read failed; last successful value and its timestamp retained");

    private static readonly IReadOnlyDictionary<string, WatchTextCatalogEntry> BlockerEntries =
        new Dictionary<string, WatchTextCatalogEntry>(StringComparer.Ordinal)
        {
            ["LONG_GONE_BUT_VISIBLE"] = E("audit.blocker.longGoneVisible", "归档后再次出现", "Visible again after archive"),
            ["DUPLICATE_TRANSPORT_DEMAND_KEY"] = E("audit.blocker.duplicateKey", "TransportDemand 业务键重复", "Duplicate TransportDemand business key"),
            ["SUBLOT_MULTIPLE_WORK_TYPES"] = E("audit.blocker.multipleWorkTypes", "SUBLOT 同时存在多个 WorkType", "SUBLOT has multiple WorkTypes"),
            ["REQUIRED_MES_FIELD_MISSING"] = E("audit.blocker.requiredMissing", "必需 MES 字段缺失", "Required MES field is missing"),
            ["INVALID_MES_FIELD_FORMAT"] = E("audit.blocker.invalidFormat", "MES 字段格式无效", "MES field format is invalid"),
            ["DEMAND_GONE"] = E("audit.blocker.demandGone", "Demand 已消失", "Demand is GONE"),
            ["SERIES_ARCHIVED"] = E("audit.blocker.seriesArchived", "所属需求系列已归档", "Owning demand series is archived"),
        };

    private static readonly IReadOnlyList<WatchTextCatalogEntry> CatalogEntries =
    [
        PageTitleEntry, PageSubtitleEntry, CurrentObservationEntry, CurrentBlockerEntry,
        ExternalReadabilityEntry, MissingSemanticsEntry, AllEntry, ReadableEntry,
        NotReadableEntry, BlockedEntry, StateFilterEntry, WorkTypeFilterEntry,
        BlockerFilterEntry, SearchFilterEntry, AreaFilterEntry, ApplyFiltersEntry,
        AllWorkTypesEntry, AllBlockersEntry, MasterHeadingEntry, DemandIdLabelEntry,
        WorkTypeLabelEntry, SublotLabelEntry, PreviousPageEntry, NextPageEntry,
        GoToPageEntry, ClearFiltersEntry, PerPageEntry, ViewSeriesEntry, DeepEvidenceEntry,
        QualificationChecksEntry, BlockerEvidenceEntry, RawObservationsEntry,
        NoHostSnapshotEntry, NoSnapshotEntry, HostNoScopeEntry, NotSelectedEntry,
        SelectDemandEntry, NoBlockerEntry, UnknownBlockerEntry, SourceNotProvidedHelpEntry,
        SystemUnknownHelpEntry, NotApplicableHelpEntry, NotLoadedHelpEntry,
        EmptyResultHelpEntry, ReadFailedHelpEntry,
        .. BlockerEntries.Values,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => CatalogEntries;

    public string PageTitle => Text(PageTitleEntry);
    public string PageSubtitle => Text(PageSubtitleEntry);
    public string CurrentObservation => Text(CurrentObservationEntry);
    public string CurrentBlocker => Text(CurrentBlockerEntry);
    public string ExternalReadability => Text(ExternalReadabilityEntry);
    public string MissingSemantics => Text(MissingSemanticsEntry);
    public string All => Text(AllEntry);
    public string Readable => Text(ReadableEntry);
    public string NotReadable => Text(NotReadableEntry);
    public string Blocked => Text(BlockedEntry);
    public string StateFilter => Text(StateFilterEntry);
    public string WorkTypeFilter => Text(WorkTypeFilterEntry);
    public string BlockerFilter => Text(BlockerFilterEntry);
    public string SearchFilter => Text(SearchFilterEntry);
    public string AreaFilter => Text(AreaFilterEntry);
    public string ApplyFilters => Text(ApplyFiltersEntry);
    public string AllWorkTypes => Text(AllWorkTypesEntry);
    public string AllBlockers => Text(AllBlockersEntry);
    public string MasterHeading => Text(MasterHeadingEntry);
    public string DemandIdLabel => Text(DemandIdLabelEntry);
    public string WorkTypeLabel => Text(WorkTypeLabelEntry);
    public string SublotLabel => Text(SublotLabelEntry);
    public string PreviousPage => Text(PreviousPageEntry);
    public string NextPage => Text(NextPageEntry);
    public string GoToPage => Text(GoToPageEntry);
    public string ClearFilters => Text(ClearFiltersEntry);
    public string PerPage => Text(PerPageEntry);
    public string ViewSeries => Text(ViewSeriesEntry);
    public string DeepEvidence => Text(DeepEvidenceEntry);
    public string QualificationChecks => Text(QualificationChecksEntry);
    public string BlockerEvidence => Text(BlockerEvidenceEntry);
    public string RawObservations => Text(RawObservationsEntry);
    public string NoHostSnapshot => Text(NoHostSnapshotEntry);
    public string NoSnapshot => Text(NoSnapshotEntry);
    public string HostNoScope => Text(HostNoScopeEntry);
    public string NotSelected => Text(NotSelectedEntry);
    public string SelectDemand => Text(SelectDemandEntry);
    public string NoBlocker => Text(NoBlockerEntry);

    public IReadOnlyList<WatchValueSemanticDescription> ValueSemantics =>
    [
        new(WatchDisplayValueKind.SourceNotProvided, TextFor(WatchDisplayValueKind.SourceNotProvided), Text(SourceNotProvidedHelpEntry)),
        new(WatchDisplayValueKind.SystemUnknown, TextFor(WatchDisplayValueKind.SystemUnknown), Text(SystemUnknownHelpEntry)),
        new(WatchDisplayValueKind.NotApplicable, TextFor(WatchDisplayValueKind.NotApplicable), Text(NotApplicableHelpEntry)),
        new(WatchDisplayValueKind.NotLoaded, TextFor(WatchDisplayValueKind.NotLoaded), Text(NotLoadedHelpEntry)),
        new(WatchDisplayValueKind.EmptyResult, TextFor(WatchDisplayValueKind.EmptyResult), Text(EmptyResultHelpEntry)),
        new(WatchDisplayValueKind.ReadFailed, TextFor(WatchDisplayValueKind.ReadFailed), Text(ReadFailedHelpEntry)),
    ];

    public WatchCodeMeaning DescribeBlocker(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        return BlockerEntries.TryGetValue(rawCode, out var entry)
            ? new WatchCodeMeaning(Text(entry), rawCode, IsKnown: true)
            : new WatchCodeMeaning(Text(UnknownBlockerEntry), rawCode, IsKnown: false);
    }

    public string DescribeReadability(string rawState) => rawState switch
    {
        ExternalReadabilityStates.Readable => Readable,
        ExternalReadabilityStates.NotReadable => NotReadable,
        _ => rawState,
    };

    public string HostSnapshotFacts(
        DateTimeOffset committedAt,
        string commitId,
        long sequence,
        string pollTraceId,
        long catalogRevision) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"Host 投影提交 {WatchTimeDisplay.Format(committedAt)} · {commitId} · 序列 {sequence:N0} · PollTrace {pollTraceId} · CatalogRevision {catalogRevision:N0}"
        : $"Host projection committed {WatchTimeDisplay.Format(committedAt)} · {commitId} · sequence {sequence:N0} · PollTrace {pollTraceId} · CatalogRevision {catalogRevision:N0}";

    public string PageSummary(long total, int page, int pages) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"精确 {total:N0} 个运输需求代次 · 第 {page:N0} / {pages:N0} 页"
            : $"Exactly {total:N0} Demands · page {page:N0} of {pages:N0}";

    public string EmptyResult(long total) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"查询成功；Host 在当前已提交条件下精确 {total:N0} 个运输需求代次命中。"
        : $"Query succeeded; exactly {total:N0} Demands matched the committed Host conditions.";

    public string HostOrder(string order) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"Host 固定排序：{order}"
        : $"Fixed Host order: {order}";

    public string HostCommittedConditions(string conditions) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"Host 已提交条件：{conditions}"
            : $"Host committed conditions: {conditions}";

    public string PendingConditions(string conditions) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"当前待查询条件：{conditions}"
            : $"Pending query conditions: {conditions}";

    public string AllDemands => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "全部运输需求代次"
        : "All Demands";

    public string HostAreaScope(IReadOnlyList<string> areas) => areas.Count == 0
        ? Language == WatchDisplayLanguage.SimplifiedChinese
            ? "Host 已提交范围：全部 AREA"
            : "Host committed scope: all AREA"
        : Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"Host 已提交范围：{string.Join('、', areas)}"
            : $"Host committed scope: {string.Join(", ", areas)}";

    public string LocalAreaDetail(string state, IReadOnlyList<string> areas) => areas.Count == 0
        ? Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"{state} · 未限制 Host AREA 查询"
            : $"{state} · Host AREA query is unrestricted"
        : $"{state} · {string.Join(Language == WatchDisplayLanguage.SimplifiedChinese ? "、" : ", ", areas)}";

    public string ClientAttempts(DateTimeOffset? successfulAt, DateTimeOffset? failedAt)
    {
        var successful = successfulAt is { } success
            ? Language == WatchDisplayLanguage.SimplifiedChinese
                ? $"Watch 最近成功 {WatchTimeDisplay.Format(success)}"
                : $"Watch last succeeded {WatchTimeDisplay.Format(success)}"
            : Language == WatchDisplayLanguage.SimplifiedChinese
                ? "Watch 尚无成功读取"
                : "Watch has no successful read";
        return failedAt is { } failure
            ? Language == WatchDisplayLanguage.SimplifiedChinese
                ? $"{successful} · 最近失败 {WatchTimeDisplay.Format(failure)}"
                : $"{successful} · last failed {WatchTimeDisplay.Format(failure)}"
            : successful;
    }

    public string FilterSummary(ReadabilityAuditFilter filter)
    {
        var conditions = new List<string>();
        AddMany(Language == WatchDisplayLanguage.SimplifiedChinese ? "资格" : StateFilter, filter.ReadabilityStates);
        AddMany("WorkType", filter.WorkTypes);
        AddMany(Language == WatchDisplayLanguage.SimplifiedChinese ? "阻断" : BlockerFilter, filter.Blockers);
        Add("DemandId", filter.DemandId);
        Add(Language == WatchDisplayLanguage.SimplifiedChinese ? "SUBLOT 包含" : "SUBLOT contains", filter.SublotContains);
        AddMany("AREA", filter.MesAreas);
        return conditions.Count == 0 ? AllDemands : string.Join(" · ", conditions);

        void AddMany(string label, IReadOnlyList<string> values)
        {
            if (values.Count > 0)
            {
                conditions.Add($"{label} {string.Join(Language == WatchDisplayLanguage.SimplifiedChinese ? "、" : ", ", values)}");
            }
        }

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                conditions.Add($"{label} {value}");
            }
        }
    }

    public string RefreshPolicy(int seconds, bool refreshing) => refreshing
        ? Language == WatchDisplayLanguage.SimplifiedChinese
            ? "正在读取 · 自动刷新保持开启"
            : "Reading · auto-refresh remains enabled"
        : Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"每 {seconds} 秒自动刷新"
            : $"Auto-refresh every {seconds} seconds";

    public string DetailConclusion(string demandId, bool readable) => readable
        ? Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"{demandId} 当前可被外部读取"
            : $"{demandId} is externally readable"
        : Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"{demandId} 当前不可被外部读取"
            : $"{demandId} is not externally readable";

    public string QualificationConclusion(string rawState) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"结论：{DescribeReadability(rawState)}"
            : $"Conclusion: {DescribeReadability(rawState)}";

    public string BusinessIdentity(
        string sublot,
        string seriesId,
        int generation,
        string lastSeen) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"{sublot} · {seriesId} · Demand Generation {generation:N0} · 最后看见 {lastSeen}"
        : $"{sublot} · {seriesId} · Demand Generation {generation:N0} · Last seen {lastSeen}";

    public string DetailFacts(
        string snapshotReference,
        DateTimeOffset committedAt,
        string commitId,
        long sequence,
        string pollTraceId,
        long catalogRevision,
        string observationPollTraceId,
        string observationCommitId) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"审计快照 {snapshotReference} · Host 投影提交 {WatchTimeDisplay.Format(committedAt)} · {commitId} · 序列 {sequence:N0} · PollTrace {pollTraceId} · CatalogRevision {catalogRevision:N0} · Demand 最新观测 PollTrace {observationPollTraceId} · ProjectionCommit {observationCommitId}"
        : $"Audit snapshot {snapshotReference} · Host projection committed {WatchTimeDisplay.Format(committedAt)} · {commitId} · sequence {sequence:N0} · PollTrace {pollTraceId} · CatalogRevision {catalogRevision:N0} · Demand latest observation PollTrace {observationPollTraceId} · ProjectionCommit {observationCommitId}";

    public string SeriesFacts(
        string seriesId,
        string workType,
        string sublot,
        string lifecycle,
        string presence,
        string currentDemandId,
        string startedAt,
        string archivedAt) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"Series {seriesId} · {workType} · SUBLOT {sublot} · {lifecycle} · {presence} · 当前 Demand {currentDemandId} · 开始 {startedAt} · 归档 {archivedAt}"
        : $"Series {seriesId} · {workType} · SUBLOT {sublot} · {lifecycle} · {presence} · current Demand {currentDemandId} · started {startedAt} · archived {archivedAt}";

    public string NoTrustedLiveMes => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "无可信 LiveMesFieldSet；请核对下方原始观测。"
        : "No trusted LiveMesFieldSet; inspect the raw observations below.";

    public string TrustedLiveMes(WatchReadabilityLiveMesFieldSetPresentation fields) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"可信 LiveMesFieldSet · AREA {fields.Area} · EQP {fields.Eqp} · STEP {fields.Step} · MesSourceDate {fields.MesSourceDate} · PACKAGE {fields.Package}"
            : $"Trusted LiveMesFieldSet · AREA {fields.Area} · EQP {fields.Eqp} · STEP {fields.Step} · MesSourceDate {fields.MesSourceDate} · PACKAGE {fields.Package}";

    public string ObservationSummary(int count, bool trusted, bool conflicting) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? trusted
                ? $"{count:N0} 条原始观测 · 当前可信 LiveMesFieldSet 可用"
                : conflicting
                    ? $"{count:N0} 条原始观测 · 无可信单值；保留原始观测冲突证据"
                    : $"{count:N0} 条原始观测 · 无可信 LiveMesFieldSet"
            : trusted
                ? $"{count:N0} raw observations · trusted LiveMesFieldSet available"
                : conflicting
                    ? $"{count:N0} raw observations · no trusted single value; raw conflict evidence retained"
                    : $"{count:N0} raw observations · no trusted LiveMesFieldSet";

    public string PollTraceFacts(
        string pollTraceId,
        string queryVersion,
        string outcome,
        string startedAt,
        string completedAt,
        long rowCount,
        string digest,
        string commitId,
        long sequence) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"PollTrace {pollTraceId} · {queryVersion} · {outcome} · {startedAt} → {completedAt} · {rowCount:N0} 行 · Digest {digest} · ProjectionCommit {commitId} · 序列 {sequence:N0}"
        : $"PollTrace {pollTraceId} · {queryVersion} · {outcome} · {startedAt} → {completedAt} · {rowCount:N0} rows · Digest {digest} · ProjectionCommit {commitId} · sequence {sequence:N0}";

    public string QualificationMeaning(string code, string fallbackEnglish) => (Language, code) switch
    {
        (WatchDisplayLanguage.SimplifiedChinese, "DEMAND_VISIBLE") => "Demand 在当前完整源投影中可见",
        (WatchDisplayLanguage.SimplifiedChinese, "SERIES_TRACKING") => "所属需求系列尚未归档",
        (WatchDisplayLanguage.SimplifiedChinese, "NOT_LONG_GONE_BUT_VISIBLE") => "Demand 不是归档后再次出现",
        (WatchDisplayLanguage.SimplifiedChinese, "UNIQUE_RAW_OBSERVATION") => "当前轮次中该 Demand 业务键只有一条原始观测",
        (WatchDisplayLanguage.SimplifiedChinese, "ONE_WORK_TYPE_PER_SUBLOT") => "当前轮次中 SUBLOT 只对应一个 WorkType",
        (WatchDisplayLanguage.SimplifiedChinese, "REQUIRED_MES_FIELDS_PRESENT") => "全部必需 MES 字段均已提供且非空白",
        (WatchDisplayLanguage.SimplifiedChinese, "MES_FIELD_FORMAT_VALID") => "全部已提供 MES 字段符合领域格式",
        _ => fallbackEnglish,
    };

    public string CannotConnectHost => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "无法连接 Host"
        : "Cannot connect to Host";
    public string ReplacementHostFailure(string failure) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"新 Host 未通过契约连接；旧 Host 数据已清空。{failure}"
        : $"The replacement Host failed contract connection; old Host data was cleared. {failure}";
    public string ConnectingHost => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "正在连接 Host"
        : "Connecting to Host";
    public string ConnectingMessage => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "连接成功后将读取一份冻结的资格审计快照。"
        : "A frozen eligibility-audit snapshot will be read after connection succeeds.";
    public string WaitingFrozenSnapshot => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "等待 Host 返回冻结快照。"
        : "Waiting for the Host to return a frozen snapshot.";
    public string RetainedDuringRefresh(DateTimeOffset committedAt, WatchTextCatalog catalog) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"刷新期间继续显示 Host 快照 {catalog.FormatAbsoluteTime(committedAt)}。"
            : $"Refresh in progress; continues to show Host snapshot {catalog.FormatAbsoluteTime(committedAt)}.";
    public string PriorFailureRetry(DateTimeOffset failedAt, WatchTextCatalog catalog) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? $" 上次失败于 {catalog.FormatAbsoluteTime(failedAt)}；本次正在重试。"
            : $" Previous attempt failed {catalog.FormatAbsoluteTime(failedAt)}; retry in progress.";
    public string LoadingTitle(bool hasSnapshot) => (Language, hasSnapshot) switch
    {
        (WatchDisplayLanguage.SimplifiedChinese, false) => "正在读取资格审计",
        (WatchDisplayLanguage.SimplifiedChinese, true) => "正在刷新资格审计",
        (WatchDisplayLanguage.English, false) => "Loading eligibility audit",
        _ => "Refreshing eligibility audit",
    };
    public string NoSuccessfulSnapshot => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "当前没有可显示的成功快照。"
        : "There is no successful snapshot to display.";
    public string RetainedAfterFailure(DateTimeOffset committedAt, WatchTextCatalog catalog) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"继续显示 Host 快照 {catalog.FormatAbsoluteTime(committedAt)}；其筛选、精确分面和 AREA 范围不会被失败查询改写。"
            : $"Continues to show Host snapshot {catalog.FormatAbsoluteTime(committedAt)}; the failed query did not rewrite its filters, exact facets, or AREA scope.";
    public string FailureTitle(bool hasSnapshot) => (Language, hasSnapshot) switch
    {
        (WatchDisplayLanguage.SimplifiedChinese, false) => "资格审计读取失败",
        (WatchDisplayLanguage.SimplifiedChinese, true) => "资格审计刷新失败，已保留上次快照",
        (WatchDisplayLanguage.English, false) => "Eligibility-audit read failed",
        _ => "Eligibility-audit refresh failed; previous snapshot retained",
    };
    public string FailedAt(DateTimeOffset failedAt, string retained, string failure, WatchTextCatalog catalog) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"失败于 {catalog.FormatAbsoluteTime(failedAt)}。{retained}{failure}"
            : $"Failed {catalog.FormatAbsoluteTime(failedAt)}. {retained}{failure}";
    public string SelectionLostTitle => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "原选择已不在刷新结果中"
        : "Previous selection is no longer in the refreshed results";
    public string SelectionLostMessage => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "刷新成功，但原运输需求代次已不在当前冻结结果中；已清除详情，请重新选择。"
        : "Refresh succeeded, but the previous demand generation is no longer in the frozen results. Details were cleared; select another item.";
    public string FailureMessage(string? message, string? correlationId)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}";
        return string.IsNullOrWhiteSpace(correlationId)
            ? detail
            : Language == WatchDisplayLanguage.SimplifiedChinese
                ? $"{detail} 关联 ID {correlationId}。"
                : $"{detail} Correlation ID {correlationId}.";
    }

    public string ReadStateAutomation(string title, string message, bool isOpen) => isOpen
        ? Language == WatchDisplayLanguage.English ? $"{title}. {message}" : $"{title}。{message}"
        : Language == WatchDisplayLanguage.English
            ? "Eligibility-audit read state: no active notification"
            : "资格审计读取状态：当前无活动通知";
    public string PageSummaryAutomation(string summary) => Language == WatchDisplayLanguage.English
        ? $"Eligibility-audit exact total and page: {summary}"
        : $"资格审计精确总数与页码：{summary}";
    public string AreaScopeAutomation(string scope) => Language == WatchDisplayLanguage.English
        ? $"Eligibility-audit AREA scope: {scope}"
        : $"资格审计 AREA 范围：{scope}";
    public string NonEmptyOrUnread => Language == WatchDisplayLanguage.English
        ? "Eligibility-audit results are nonempty or have not been read successfully"
        : "资格审计结果不为空或尚未成功读取";
    public string DetailEvidence(string facts, string blockers) => Language == WatchDisplayLanguage.English
        ? $"{facts} · All blockers {blockers}"
        : $"{facts} · 全部阻断 {blockers}";
    public string LiveMesFieldsAutomation(
        string area, string eqp, string step, string dates, string package) =>
        Language == WatchDisplayLanguage.English
            ? $"Eligibility-audit last trusted MES fields: AREA {area}; EQP {eqp}; STEP {step}; DATES {dates}; PACKAGE {package}"
            : $"资格审计最后可信 MES 字段：AREA {area}；EQP {eqp}；STEP {step}；DATES {dates}；PACKAGE {package}";
    public string FieldAutomation(string field, string value) => Language == WatchDisplayLanguage.English
        ? $"Eligibility-audit MES {field} {value}"
        : $"资格审计 MES {field} {value}";
    public string BusinessIdentityAutomation(string value) => Language == WatchDisplayLanguage.English
        ? $"Eligibility-audit business identity: {value}"
        : $"资格审计业务身份：{value}";
    public string SeriesFactsAutomation(string value) => Language == WatchDisplayLanguage.English
        ? $"Eligibility-audit demand-series facts: {value}"
        : $"资格审计所属需求系列事实：{value}";
    public string ObservationAutomation(string value) => Language == WatchDisplayLanguage.English
        ? $"Eligibility-audit MES observation: {value}"
        : $"资格审计 MES 观测说明：{value}";
    public string SelectPrimaryEvidence => Language == WatchDisplayLanguage.English
        ? "Select an item to view primary blocker evidence."
        : "选择后显示首要阻断证据。";
    public string NoBlockerEvidence => Language == WatchDisplayLanguage.English
        ? "The current qualification checks returned no blocker evidence."
        : "当前资格检查未返回阻断证据。";
    public string MissingStructuredEvidence => Language == WatchDisplayLanguage.English
        ? "Host returned no structured evidence for the primary blocker."
        : "Host 未返回首要阻断的结构化证据。";
    public string ObservedValue(string subjectKind, string value, string at) => Language == WatchDisplayLanguage.English
        ? $"{subjectKind} · Observed value {value} · {at}"
        : $"{subjectKind} · 观测值 {value} · {at}";
    public string Rule(string rule) => Language == WatchDisplayLanguage.English ? $"Rule: {rule}" : $"规则：{rule}";
    public string PrimaryBlockerAutomation(string code, string evidence, string rule) =>
        Language == WatchDisplayLanguage.English
            ? $"Primary blocker: {code}; {evidence}; {rule}"
            : $"首要阻断：{code}；{evidence}；{rule}";
    public string RevisionPrompt => Language == WatchDisplayLanguage.English
        ? "Select a transport demand to view the complete Catalog revision and frozen-snapshot chain."
        : "选择运输需求后显示完整 Catalog 修订与冻结快照链。";
    public string RevisionNotLoaded => $"Catalog Revision {WatchTextCatalog.For(Language).Common.NotLoaded}";
    public string RevisionAutomation(string value) => Language == WatchDisplayLanguage.English
        ? $"Eligibility-audit Catalog revision facts: {value}"
        : $"资格审计修订目录事实：{value}";
    public string DetailStatusTitle(bool selectionLost, bool selected, bool failed) =>
        (Language, selectionLost, selected, failed) switch
        {
            (WatchDisplayLanguage.English, true, _, _) => "Previous selection cleared",
            (WatchDisplayLanguage.SimplifiedChinese, true, _, _) => "原选择已清除",
            (_, _, false, _) => NotSelected,
            (WatchDisplayLanguage.English, _, true, true) => "Could not read same-snapshot details",
            (WatchDisplayLanguage.SimplifiedChinese, _, true, true) => "无法读取同快照详情",
            (WatchDisplayLanguage.English, _, true, false) => "Loading same-snapshot details",
            _ => "正在读取同快照详情",
        };
    public string DetailStatusMessage(string demandId, bool failed) => Language == WatchDisplayLanguage.English
        ? failed
            ? $"Details for transport demand {demandId} failed to load; the list still belongs to the frozen snapshot shown above."
            : $"Transport demand {demandId} is selected; loading details for the current snapshotReference."
        : failed
            ? $"运输需求 {demandId} 的详情读取失败；列表仍属于上方标明的冻结快照。"
            : $"已选择运输需求 {demandId}；正在读取当前 snapshotReference 的详情。";

    private string TextFor(WatchDisplayValueKind kind) => kind switch
    {
        WatchDisplayValueKind.SourceNotProvided => WatchTextCatalog.For(Language).Common.SourceNotProvided,
        WatchDisplayValueKind.SystemUnknown => WatchTextCatalog.For(Language).Common.SystemUnknown,
        WatchDisplayValueKind.NotApplicable => WatchTextCatalog.For(Language).Common.NotApplicable,
        WatchDisplayValueKind.NotLoaded => WatchTextCatalog.For(Language).Common.NotLoaded,
        WatchDisplayValueKind.EmptyResult => WatchTextCatalog.For(Language).Common.EmptyResult,
        WatchDisplayValueKind.ReadFailed => WatchTextCatalog.For(Language).Common.ReadFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
