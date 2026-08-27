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
    private static readonly WatchTextCatalogEntry AreaSelectorAutomationEntry = E("audit.filter.areaAutomation", "资格审计 AREA 配置选择器", "Eligibility-audit AREA profile selector");
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
    private static readonly WatchTextCatalogEntry LocalAreaHeaderEntry = E("audit.header.localArea", "本机 AREA：{0}", "Local AREA: {0}");
    private static readonly WatchTextCatalogEntry HeaderFactsEntry = E("audit.header.facts", "本机 {0} · {1} · {2}", "Local {0} · {1} · {2}");
    private static readonly WatchTextCatalogEntry HeaderAutomationEntry = E("audit.header.automation", "资格审计 AREA 与更新时间：{0}", "Eligibility-audit AREA scope and update time: {0}");
    private static readonly WatchTextCatalogEntry SnapshotNotLoadedEntry = E("audit.snapshot.notLoaded", "SnapshotReference 尚无快照", "SnapshotReference is not loaded");
    private static readonly WatchTextCatalogEntry SnapshotReferenceEntry = E("audit.snapshot.reference", "SnapshotReference {0}", "SnapshotReference {0}");
    private static readonly WatchTextCatalogEntry StateCountEntry = E("audit.state.count", "{0} {1}", "{0} {1}");
    private static readonly WatchTextCatalogEntry ExactCountAutomationEntry = E("audit.state.exactAutomation", "Host 精确 {0}", "Exact Host count: {0}");
    private static readonly WatchTextCatalogEntry BlockerFacetNotLoadedEntry = E("audit.facets.notLoaded", "阻断原因精确分面：尚无快照", "Exact blocker facets: snapshot not loaded");
    private static readonly WatchTextCatalogEntry BlockerFacetEmptyEntry = E("audit.facets.empty", "阻断原因精确分面：无命中", "Exact blocker facets: no matches");
    private static readonly WatchTextCatalogEntry BlockerFacetSummaryEntry = E("audit.facets.summary", "阻断原因精确分面：{0}", "Exact blocker facets: {0}");
    private static readonly WatchTextCatalogEntry CompactOverlapEntry = E("audit.compact.overlap", "原因可重叠；不可见总数按运输需求代次去重", "Reasons may overlap; the not-readable total is distinct by transport-demand generation");
    private static readonly WatchTextCatalogEntry CompactAutomationEntry = E("audit.compact.automation", "资格审计紧凑快照事实：{0}", "Compact eligibility-audit snapshot facts: {0}");
    private static readonly WatchTextCatalogEntry ReadableDetailMessageEntry = E("audit.detail.readableMessage", "当前冻结审计快照中的全部外部可见资格检查通过。", "All external-readability qualification checks pass in the current frozen audit snapshot.");
    private static readonly WatchTextCatalogEntry BlockedDetailMessageEntry = E("audit.detail.blockedMessage", "当前冻结审计快照的阻断条件：{0}。", "Blocking conditions in the current frozen audit snapshot: {0}.");
    private static readonly WatchTextCatalogEntry DetailInfoAutomationEntry = E("audit.detail.infoAutomation", "{0}。{1}", "{0}. {1}");

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
        BlockerFilterEntry, SearchFilterEntry, AreaFilterEntry, AreaSelectorAutomationEntry, ApplyFiltersEntry,
        AllWorkTypesEntry, AllBlockersEntry, MasterHeadingEntry, DemandIdLabelEntry,
        WorkTypeLabelEntry, SublotLabelEntry, PreviousPageEntry, NextPageEntry,
        GoToPageEntry, ClearFiltersEntry, PerPageEntry, ViewSeriesEntry, DeepEvidenceEntry,
        QualificationChecksEntry, BlockerEvidenceEntry, RawObservationsEntry,
        NoHostSnapshotEntry, NoSnapshotEntry, HostNoScopeEntry, NotSelectedEntry,
        SelectDemandEntry, NoBlockerEntry, UnknownBlockerEntry, SourceNotProvidedHelpEntry,
        SystemUnknownHelpEntry, NotApplicableHelpEntry, NotLoadedHelpEntry,
        EmptyResultHelpEntry, ReadFailedHelpEntry,
        LocalAreaHeaderEntry, HeaderFactsEntry, HeaderAutomationEntry,
        SnapshotNotLoadedEntry, SnapshotReferenceEntry, StateCountEntry,
        ExactCountAutomationEntry, BlockerFacetNotLoadedEntry, BlockerFacetEmptyEntry,
        BlockerFacetSummaryEntry, CompactOverlapEntry, CompactAutomationEntry,
        ReadableDetailMessageEntry, BlockedDetailMessageEntry, DetailInfoAutomationEntry,
        .. BlockerEntries.Values,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries =>
        [.. CatalogEntries, .. WatchLegacyGeneratedText.ReadabilityAuditEntries];

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
    public string AreaSelectorAutomationName => Text(AreaSelectorAutomationEntry);
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
    public string FacetOverlapHelp => Select(WatchLegacyGeneratedText.ReadabilityFacetOverlapHelp);
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
        long catalogRevision) => Format(WatchLegacyGeneratedText.ReadabilityAudit098, new object?[] { WatchTimeDisplay.Format(committedAt), commitId, sequence, pollTraceId, catalogRevision }, new object?[] { WatchTimeDisplay.Format(committedAt), commitId, sequence, pollTraceId, catalogRevision });

    public string PageSummary(long total, int page, int pages) =>
        Format(WatchLegacyGeneratedText.ReadabilityAudit099, new object?[] { total, page, pages }, new object?[] { total, page, pages });

    public string EmptyResult(long total) => Format(WatchLegacyGeneratedText.ReadabilityAudit100, new object?[] { total }, new object?[] { total });

    public string HostOrder(string order) => Format(WatchLegacyGeneratedText.ReadabilityAudit101, new object?[] { order }, new object?[] { order });

    public string HostCommittedConditions(string conditions) =>
        Format(WatchLegacyGeneratedText.ReadabilityAudit102, new object?[] { conditions }, new object?[] { conditions });

    public string PendingConditions(string conditions) =>
        Format(WatchLegacyGeneratedText.ReadabilityAudit103, new object?[] { conditions }, new object?[] { conditions });

    public string AllDemands => Select(WatchLegacyGeneratedText.ReadabilityAudit104);

    public string HostAreaScope(IReadOnlyList<string> areas) => areas.Count == 0
        ? Select(WatchLegacyGeneratedText.ReadabilityAudit105)
        : Format(WatchLegacyGeneratedText.ReadabilityAudit106, new object?[] { string.Join('、', areas) }, new object?[] { string.Join(", ", areas) });

    public string LocalAreaDetail(string state, IReadOnlyList<string> areas)
    {
        var localizedState = WatchTextCatalog.For(Language).Overview.LocalState(state);
        return areas.Count == 0
            ? Format(WatchLegacyGeneratedText.ReadabilityAudit107, new object?[] { localizedState }, new object?[] { localizedState })
            : $"{localizedState} · {string.Join(Select(WatchLegacyGeneratedText.ReadabilityAudit108), areas)}";
    }

    public string ClientAttempts(DateTimeOffset? successfulAt, DateTimeOffset? failedAt)
    {
        var successful = successfulAt is { } success
            ? Format(WatchLegacyGeneratedText.ReadabilityAudit109, new object?[] { WatchTimeDisplay.Format(success) }, new object?[] { WatchTimeDisplay.Format(success) })
            : Select(WatchLegacyGeneratedText.ReadabilityAudit110);
        return failedAt is { } failure
            ? Format(WatchLegacyGeneratedText.ReadabilityAudit111, new object?[] { successful, WatchTimeDisplay.Format(failure) }, new object?[] { successful, WatchTimeDisplay.Format(failure) })
            : successful;
    }

    public string FilterSummary(ReadabilityAuditFilter filter)
    {
        var conditions = new List<string>();
        AddMany(Select(WatchLegacyGeneratedText.ReadabilityFilterEligibilityLabel), filter.ReadabilityStates);
        AddMany("WorkType", filter.WorkTypes);
        AddMany(Select(WatchLegacyGeneratedText.ReadabilityFilterBlockerLabel), filter.Blockers);
        Add("DemandId", filter.DemandId);
        Add(Select(WatchLegacyGeneratedText.ReadabilityAudit112), filter.SublotContains);
        AddMany("AREA", filter.MesAreas);
        return conditions.Count == 0 ? AllDemands : string.Join(" · ", conditions);

        void AddMany(string label, IReadOnlyList<string> values)
        {
            if (values.Count > 0)
            {
                conditions.Add($"{label} {string.Join(Select(WatchLegacyGeneratedText.ReadabilityAudit108), values)}");
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
        ? Select(WatchLegacyGeneratedText.ReadabilityAudit113)
        : Format(WatchLegacyGeneratedText.ReadabilityAudit114, new object?[] { seconds }, new object?[] { seconds });

    public string DetailConclusion(string demandId, bool readable) => readable
        ? Format(WatchLegacyGeneratedText.ReadabilityAudit115, new object?[] { demandId }, new object?[] { demandId })
        : Format(WatchLegacyGeneratedText.ReadabilityAudit116, new object?[] { demandId }, new object?[] { demandId });

    public string QualificationConclusion(string rawState) =>
        Format(WatchLegacyGeneratedText.ReadabilityAudit117, new object?[] { DescribeReadability(rawState) }, new object?[] { DescribeReadability(rawState) });

    public string BusinessIdentity(
        string sublot,
        string seriesId,
        int generation,
        string lastSeen) => Format(WatchLegacyGeneratedText.ReadabilityAudit118, new object?[] { sublot, seriesId, generation, lastSeen }, new object?[] { sublot, seriesId, generation, lastSeen });

    public string DetailFacts(
        string snapshotReference,
        DateTimeOffset committedAt,
        string commitId,
        long sequence,
        string pollTraceId,
        long catalogRevision,
        string observationPollTraceId,
        string observationCommitId) => Format(WatchLegacyGeneratedText.ReadabilityAudit119, new object?[] { snapshotReference, WatchTimeDisplay.Format(committedAt), commitId, sequence, pollTraceId, catalogRevision, observationPollTraceId, observationCommitId }, new object?[] { snapshotReference, WatchTimeDisplay.Format(committedAt), commitId, sequence, pollTraceId, catalogRevision, observationPollTraceId, observationCommitId });

    public string SeriesFacts(
        string seriesId,
        string workType,
        string sublot,
        string lifecycle,
        string presence,
        string currentDemandId,
        string startedAt,
        string archivedAt) => Format(WatchLegacyGeneratedText.ReadabilityAudit120, new object?[] { seriesId, workType, sublot, lifecycle, presence, currentDemandId, startedAt, archivedAt }, new object?[] { seriesId, workType, sublot, lifecycle, presence, currentDemandId, startedAt, archivedAt });

    public string NoTrustedLiveMes => Select(WatchLegacyGeneratedText.ReadabilityAudit121);

    public string TrustedLiveMes(WatchReadabilityLiveMesFieldSetPresentation fields) =>
        Format(WatchLegacyGeneratedText.ReadabilityAudit122, new object?[] { fields.Area, fields.Eqp, fields.Step, fields.MesSourceDate, fields.Package }, new object?[] { fields.Area, fields.Eqp, fields.Step, fields.MesSourceDate, fields.Package });

    public string ObservationSummary(int count, bool trusted, bool conflicting) =>
        Format(
            trusted
                ? WatchLegacyGeneratedText.ReadabilityObservationTrusted
                : conflicting
                    ? WatchLegacyGeneratedText.ReadabilityObservationConflict
                    : WatchLegacyGeneratedText.ReadabilityObservationUntrusted,
            [count],
            [count]);

    public string PollTraceFacts(
        string pollTraceId,
        string queryVersion,
        string outcome,
        string startedAt,
        string completedAt,
        long rowCount,
        string digest,
        string commitId,
        long sequence) => Format(WatchLegacyGeneratedText.ReadabilityAudit123, new object?[] { pollTraceId, queryVersion, outcome, startedAt, completedAt, rowCount, digest, commitId, sequence }, new object?[] { pollTraceId, queryVersion, outcome, startedAt, completedAt, rowCount, digest, commitId, sequence });

    public string QualificationMeaning(string code, string fallbackEnglish) => code switch
    {
        "DEMAND_VISIBLE" => Select(WatchLegacyGeneratedText.ReadabilityQualificationDemandVisible),
        "SERIES_TRACKING" => Select(WatchLegacyGeneratedText.ReadabilityQualificationSeriesTracking),
        "NOT_LONG_GONE_BUT_VISIBLE" => Select(WatchLegacyGeneratedText.ReadabilityQualificationNotLongGone),
        "UNIQUE_RAW_OBSERVATION" => Select(WatchLegacyGeneratedText.ReadabilityQualificationUniqueObservation),
        "ONE_WORK_TYPE_PER_SUBLOT" => Select(WatchLegacyGeneratedText.ReadabilityQualificationOneWorkType),
        "REQUIRED_MES_FIELDS_PRESENT" => Select(WatchLegacyGeneratedText.ReadabilityQualificationFieldsPresent),
        "MES_FIELD_FORMAT_VALID" => Select(WatchLegacyGeneratedText.ReadabilityQualificationFieldsValid),
        _ => fallbackEnglish,
    };

    public string CannotConnectHost => Select(WatchLegacyGeneratedText.ReadabilityAudit124);
    public string ReplacementHostFailure(string failure) => Format(WatchLegacyGeneratedText.ReadabilityAudit125, new object?[] { failure }, new object?[] { failure });
    public string ConnectingHost => Select(WatchLegacyGeneratedText.ReadabilityAudit126);
    public string ConnectingMessage => Select(WatchLegacyGeneratedText.ReadabilityAudit127);
    public string WaitingFrozenSnapshot => Select(WatchLegacyGeneratedText.ReadabilityAudit128);
    public string RetainedDuringRefresh(DateTimeOffset committedAt, WatchTextCatalog catalog) =>
        Format(WatchLegacyGeneratedText.ReadabilityAudit129, new object?[] { catalog.FormatAbsoluteTime(committedAt) }, new object?[] { catalog.FormatAbsoluteTime(committedAt) });
    public string PriorFailureRetry(DateTimeOffset failedAt, WatchTextCatalog catalog) =>
        Format(WatchLegacyGeneratedText.ReadabilityAudit130, new object?[] { catalog.FormatAbsoluteTime(failedAt) }, new object?[] { catalog.FormatAbsoluteTime(failedAt) });
    public string LoadingTitle(bool hasSnapshot) => Select(
        hasSnapshot
            ? WatchLegacyGeneratedText.ReadabilityRefreshing
            : WatchLegacyGeneratedText.ReadabilityLoading);
    public string NoSuccessfulSnapshot => Select(WatchLegacyGeneratedText.ReadabilityAudit131);
    public string RetainedAfterFailure(DateTimeOffset committedAt, WatchTextCatalog catalog) =>
        Format(WatchLegacyGeneratedText.ReadabilityAudit132, new object?[] { catalog.FormatAbsoluteTime(committedAt) }, new object?[] { catalog.FormatAbsoluteTime(committedAt) });
    public string FailureTitle(bool hasSnapshot) => Select(
        hasSnapshot
            ? WatchLegacyGeneratedText.ReadabilityRefreshFailed
            : WatchLegacyGeneratedText.ReadabilityReadFailed);
    public string FailedAt(DateTimeOffset failedAt, string retained, string failure, WatchTextCatalog catalog) =>
        Format(WatchLegacyGeneratedText.ReadabilityAudit133, new object?[] { catalog.FormatAbsoluteTime(failedAt), retained, failure }, new object?[] { catalog.FormatAbsoluteTime(failedAt), retained, failure });
    public string SelectionLostTitle => Select(WatchLegacyGeneratedText.ReadabilityAudit134);
    public string SelectionLostMessage => Select(WatchLegacyGeneratedText.ReadabilityAudit135);
    public string FailureMessage(string? message, string? correlationId)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}";
        return string.IsNullOrWhiteSpace(correlationId)
            ? detail
            : Format(WatchLegacyGeneratedText.ReadabilityAudit136, new object?[] { detail, correlationId }, new object?[] { detail, correlationId });
    }

    public string ReadStateAutomation(string title, string message, bool isOpen) => isOpen
        ? Format(WatchLegacyGeneratedText.ReadabilityAudit137, new object?[] { title, message }, new object?[] { title, message })
        : Select(WatchLegacyGeneratedText.ReadabilityAudit138);
    public string PageSummaryAutomation(string summary) => Format(WatchLegacyGeneratedText.ReadabilityAudit139, new object?[] { summary }, new object?[] { summary });
    public string AreaScopeAutomation(string scope) => Format(WatchLegacyGeneratedText.ReadabilityAudit140, new object?[] { scope }, new object?[] { scope });
    public string NonEmptyOrUnread => Select(WatchLegacyGeneratedText.ReadabilityAudit141);
    public string DetailEvidence(string facts, string blockers) => Format(WatchLegacyGeneratedText.ReadabilityAudit142, new object?[] { facts, blockers }, new object?[] { facts, blockers });
    public string LiveMesFieldsAutomation(
        string area, string eqp, string step, string dates, string package) =>
        Format(WatchLegacyGeneratedText.ReadabilityAudit143, new object?[] { area, eqp, step, dates, package }, new object?[] { area, eqp, step, dates, package });
    public string FieldAutomation(string field, string value) => Format(WatchLegacyGeneratedText.ReadabilityAudit144, new object?[] { field, value }, new object?[] { field, value });
    public string BusinessIdentityAutomation(string value) => Format(WatchLegacyGeneratedText.ReadabilityAudit145, new object?[] { value }, new object?[] { value });
    public string SeriesFactsAutomation(string value) => Format(WatchLegacyGeneratedText.ReadabilityAudit146, new object?[] { value }, new object?[] { value });
    public string ObservationAutomation(string value) => Format(WatchLegacyGeneratedText.ReadabilityAudit147, new object?[] { value }, new object?[] { value });
    public string SelectPrimaryEvidence => Select(WatchLegacyGeneratedText.ReadabilityAudit148);
    public string NoBlockerEvidence => Select(WatchLegacyGeneratedText.ReadabilityAudit149);
    public string MissingStructuredEvidence => Select(WatchLegacyGeneratedText.ReadabilityAudit150);
    public string ObservedValue(string subjectKind, string value, string at) => Format(WatchLegacyGeneratedText.ReadabilityAudit151, new object?[] { subjectKind, value, at }, new object?[] { subjectKind, value, at });
    public string Rule(string rule) => Format(WatchLegacyGeneratedText.ReadabilityAudit152, new object?[] { rule }, new object?[] { rule });
    public string PrimaryBlockerAutomation(string code, string evidence, string rule) =>
        Format(WatchLegacyGeneratedText.ReadabilityAudit153, new object?[] { code, evidence, rule }, new object?[] { code, evidence, rule });
    public string RevisionPrompt => Select(WatchLegacyGeneratedText.ReadabilityAudit154);
    public string RevisionNotLoaded => $"Catalog Revision {WatchTextCatalog.For(Language).Common.NotLoaded}";
    public string RevisionAutomation(string value) => Format(WatchLegacyGeneratedText.ReadabilityAudit155, new object?[] { value }, new object?[] { value });
    public string DetailStatusTitle(bool selectionLost, bool selected, bool failed) =>
        (selectionLost, selected, failed) switch
        {
            (true, _, _) => Select(WatchLegacyGeneratedText.ReadabilitySelectionCleared),
            (_, false, _) => NotSelected,
            (_, true, true) => Select(WatchLegacyGeneratedText.ReadabilityDetailFailed),
            _ => Select(WatchLegacyGeneratedText.ReadabilityDetailLoading),
        };
    public string DetailStatusMessage(string demandId, bool failed) => Format(
        failed
            ? WatchLegacyGeneratedText.ReadabilityDetailFailedMessage
            : WatchLegacyGeneratedText.ReadabilityDetailLoadingMessage,
        [demandId],
        [demandId]);
    public string LocalAreaHeader(string heading) => string.Format(Text(LocalAreaHeaderEntry), heading);
    public string LocalAreaHeading(string heading) =>
        string.Equals(
            heading,
            WatchAreaDisplayContext.AllAreas.ProfileName,
            StringComparison.Ordinal)
            ? WatchTextCatalog.For(Language).Overview.AllArea
            : heading;
    public string HeaderFacts(string localArea, string hostScope, string freshness) =>
        string.Format(Text(HeaderFactsEntry), localArea, hostScope, freshness);
    public string HeaderAutomation(string facts) => string.Format(Text(HeaderAutomationEntry), facts);
    public string SnapshotReference(string? snapshotReference) => string.IsNullOrWhiteSpace(snapshotReference)
        ? Text(SnapshotNotLoadedEntry)
        : string.Format(Text(SnapshotReferenceEntry), snapshotReference);
    public string StateCount(string label, string value) => string.Format(Text(StateCountEntry), label, value);
    public string ExactCountAutomation(string value) => string.Format(Text(ExactCountAutomationEntry), value);
    public string BlockerFacetSummary(IReadOnlyList<WatchReadabilityBlockerFacetPresentation>? facets, bool hasSnapshot)
    {
        if (!hasSnapshot)
        {
            return Text(BlockerFacetNotLoadedEntry);
        }

        if (facets is null || facets.Count == 0)
        {
            return Text(BlockerFacetEmptyEntry);
        }

        return string.Format(
            Text(BlockerFacetSummaryEntry),
            string.Join(
                WatchTextCatalog.For(Language).Common.ListSeparator,
                facets.Select(facet => $"{facet.Code} {facet.DemandCount:N0}")));
    }
    public string CompactOverlap => Text(CompactOverlapEntry);
    public string CompactAutomation(string facts) => string.Format(Text(CompactAutomationEntry), facts);
    public string DetailMessage(bool readable, string blockers) => readable
        ? Text(ReadableDetailMessageEntry)
        : string.Format(Text(BlockedDetailMessageEntry), blockers);
    public string DetailInfoAutomation(string title, string message) =>
        string.Format(Text(DetailInfoAutomationEntry), title, message);

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
