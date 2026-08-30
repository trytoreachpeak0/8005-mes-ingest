using System.Globalization;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal sealed partial class WatchOverviewText
{
    private static WatchTextCatalogEntry E(string id, string zh, string en) => new(id, zh, en);

    private static readonly WatchTextCatalogEntry PageTitleEntry = E("overview.page.title", "概览", "Overview");
    private static readonly WatchTextCatalogEntry SeriesEntry = E("overview.card.series", "需求系列", "Demand series");
    private static readonly WatchTextCatalogEntry ReadabilityEntry = E("overview.card.readability", "资格审计", "Eligibility audit");
    private static readonly WatchTextCatalogEntry ErrorsEntry = E("overview.card.errors", "错误检索", "Error search");
    private static readonly WatchTextCatalogEntry AreaEntry = E("overview.card.area", "AREA 筛选", "AREA filters");
    private static readonly WatchTextCatalogEntry AttentionEntry = E("overview.card.attention", "接入告警", "Ingest alerts");
    private static readonly WatchTextCatalogEntry ViewEntry = E("overview.action.view", "查看 →", "View →");
    private static readonly WatchTextCatalogEntry ViewAllFirstPageEntry = E("overview.action.viewAllFirstPage", "查看全部{0}第一页", "View all {0}, first page");
    private static readonly WatchTextCatalogEntry ManageEntry = E("overview.action.manage", "管理 →", "Manage →");
    private static readonly WatchTextCatalogEntry ReadableEntry = E("overview.action.readable", "可读", "Readable");
    private static readonly WatchTextCatalogEntry NotReadableEntry = E("overview.action.notReadable", "不可读", "Not readable");
    private static readonly WatchTextCatalogEntry ActiveEntry = E("overview.action.active", "活动", "Active");
    private static readonly WatchTextCatalogEntry PriorSevenDaysEntry = E("overview.action.priorSevenDays", "近 7 天", "Prior 7 days");
    private static readonly WatchTextCatalogEntry RecentEntry = E("overview.recent.heading", "近期重点动态", "Recent highlights");
    private static readonly WatchTextCatalogEntry RecentHelpEntry = E("overview.recent.help", "最近成功窗口中最值得关注的变化", "Important changes in the last successful window");
    private static readonly WatchTextCatalogEntry DescendingEntry = E("overview.recent.order", "按时间倒序", "Newest first");
    private static readonly WatchTextCatalogEntry NoRecentEntry = E("overview.recent.empty", "近期无重点动态", "No recent highlights");
    private static readonly WatchTextCatalogEntry SeriesUnitEntry = E("overview.unit.series", "个系列", "Demand series");
    private static readonly WatchTextCatalogEntry DemandUnitEntry = E("overview.unit.demands", "个 Demand", "Demands");
    private static readonly WatchTextCatalogEntry ErrorSeriesUnitEntry = E("overview.unit.errorSeries", "个错误系列", "error series");
    private static readonly WatchTextCatalogEntry AttentionUnitEntry = E("overview.unit.attention", "个关注项", "attention items");
    private static readonly WatchTextCatalogEntry NoHostSnapshotEntry = E("overview.state.noHostSnapshot", "尚无 Host 业务快照", "No Host business snapshot");
    private static readonly WatchTextCatalogEntry WaitingEntry = E("overview.state.waiting", "等待 Host 概览", "Waiting for Host overview");
    private static readonly WatchTextCatalogEntry HostNoScopeEntry = E("overview.state.hostNoScope", "Host 已提交范围：尚无快照", "Host committed scope: no snapshot");
    private static readonly WatchTextCatalogEntry ProtectionEntry = E("overview.section.protection", "存储与历史保护", "Storage and history protection");
    private static readonly WatchTextCatalogEntry SnapshotEntry = E("overview.section.snapshot", "概览快照事实", "Overview snapshot facts");
    private static readonly WatchTextCatalogEntry AllAreaEntry = E("overview.area.all", "全部 AREA", "All AREA");
    private static readonly WatchTextCatalogEntry ContextAutomationEntry = E("overview.automation.context", "概览快照、客户端读取时间与自动刷新策略：{0}", "Overview snapshot, client read time, and auto-refresh policy: {0}");
    private static readonly WatchTextCatalogEntry NoReadNoticeEntry = E("overview.automation.noReadNotice", "概览读取状态：当前无活动通知", "Overview read state: no active notice");
    private static readonly WatchTextCatalogEntry ReadNoticeEntry = E("overview.automation.readNotice", "{0}。{1}", "{0}. {1}");
    private static readonly WatchTextCatalogEntry NoStaleEntry = E("overview.automation.notStale", "概览数据未标记为陈旧", "Overview data is not marked stale");
    private static readonly WatchTextCatalogEntry AttentionAutomationEntry = E("overview.automation.attention", "存储与历史保护状态：{0}。{1}。接入告警严重度精确分面：{2}", "Storage and history protection: {0}. {1}. Exact ingest-alert severity facets: {2}");
    private static readonly WatchTextCatalogEntry HealthAutomationEntry = E("overview.automation.health", "概览健康区：{0}。{1}", "Overview health region: {0}. {1}");
    private static readonly WatchTextCatalogEntry ErrorSeverityEntry = E("overview.severity.error", "错误", "ERROR");
    private static readonly WatchTextCatalogEntry WarningSeverityEntry = E("overview.severity.warning", "警告", "WARNING");
    private static readonly WatchTextCatalogEntry SeriesLifecycleActivityEntry = E("overview.activity.seriesLifecycle", "需求系列生命周期", "SERIES_LIFECYCLE");
    private static readonly WatchTextCatalogEntry SeriesErrorPeriodActivityEntry = E("overview.activity.seriesErrorPeriod", "需求系列错误期间", "SERIES_ERROR_PERIOD");
    private static readonly WatchTextCatalogEntry ConciseNoScopeEntry = E("overview.concise.noScope", "Host 尚无范围", "Host scope is not loaded");
    private static readonly WatchTextCatalogEntry ConciseAllScopeEntry = E("overview.concise.allScope", "Host 全部 AREA", "All Host AREA values");
    private static readonly WatchTextCatalogEntry ConciseSomeScopeEntry = E("overview.concise.someScope", "Host {0}", "Host {0}");
    private static readonly WatchTextCatalogEntry ConciseManyScopeEntry = E("overview.concise.manyScope", "Host {0} 等 {1:N0} 个 AREA", "Host {0} and {1:N0} AREA values total");

    private enum ActivityTextId
    {
        SeriesStarted, DemandGone, SeriesArchived, DemandReappeared,
        ProtectionEntered, ProtectionRecovering, ProtectionCleared, AbsenceAuthorityRestored,
        UnassignedAppeared, UnassignedChanged, UnassignedCleared, PollFailed, PollRecovered, UnknownHeading,
        SeriesStartedDetail, DemandGoneDetail, SeriesArchivedDetail, DemandReappearedDetail,
        ProtectionEnteredDetail, ProtectionRecoveringDetail, ProtectionClearedDetail, AbsenceAuthorityRestoredDetail,
        UnassignedAppearedDetail, UnassignedChangedDetail, UnassignedClearedDetail,
        PollFailedDetail, PollRecoveredDetail, UnknownDetail,
        SeriesLabel, WorkTypeLabel, PollTraceLabel, SubjectLabel, EventLabel,
        ErrorSeverity, WarningSeverity, RecoveredSeverity, InformationSeverity,
        DieToWireStaging, DieToOven, WireToGate, WireToOptical, StagingToWire, WireToNitrogen, UnknownWorkType,
        MissingHeading, InvalidHeading, DuplicateHeading, MultipleWorkTypesHeading, LongGoneVisibleHeading,
        ErrorStartedHeading, SubjectRecoveredHeading, ErrorRecoveredHeading, ErrorGoneHeading, ErrorArchivedHeading, ErrorEndedHeading,
        InvalidValueDetail, MissingValueDetail, DuplicateDetail, MultipleWorkTypesDetail, ListSeparator,
        LongGoneVisibleDetail, ValidationFailedDetail, MissingClearedDetail, InvalidClearedDetail,
        ConditionClearedDetail, DemandGoneErrorDetail, ArchivedErrorDetail, ErrorEndedDetail,
        DemandField, AreaSubject, EquipmentSubject, StepSubject, DatesSubject, PackageSubject, UnknownSubject,
        ValidValue,
    }

    private static readonly IReadOnlyDictionary<ActivityTextId, WatchTextCatalogEntry> ActivityEntries =
        new Dictionary<ActivityTextId, WatchTextCatalogEntry>
        {
            [ActivityTextId.SeriesStarted] = E("overview.activity.seriesStarted", "需求系列开始跟踪", "Demand series tracking started"),
            [ActivityTextId.DemandGone] = E("overview.activity.demandGone", "运输需求已消失", "Demand disappeared from MES"),
            [ActivityTextId.SeriesArchived] = E("overview.activity.seriesArchived", "需求系列已超时归档", "Demand archived after timeout"),
            [ActivityTextId.DemandReappeared] = E("overview.activity.demandReappeared", "运输需求再次出现", "Demand reappeared"),
            [ActivityTextId.ProtectionEntered] = E("overview.activity.protectionEntered", "工序类型保护已启动", "Work-type protection started"),
            [ActivityTextId.ProtectionRecovering] = E("overview.activity.protectionRecovering", "工序类型保护正在恢复", "Work-type protection recovering"),
            [ActivityTextId.ProtectionCleared] = E("overview.activity.protectionCleared", "工序类型保护已解除", "Work-type protection cleared"),
            [ActivityTextId.AbsenceAuthorityRestored] = E("overview.activity.absenceAuthorityRestored", "工序类型缺席判定已恢复", "Work-type absence authority restored"),
            [ActivityTextId.UnassignedAppeared] = E("overview.activity.unassignedAppeared", "出现未归属的制造执行系统观测", "Unassigned MES observation found"),
            [ActivityTextId.UnassignedChanged] = E("overview.activity.unassignedChanged", "未归属的制造执行系统观测已变化", "Unassigned MES observation changed"),
            [ActivityTextId.UnassignedCleared] = E("overview.activity.unassignedCleared", "未归属的制造执行系统观测已清除", "Unassigned MES observation cleared"),
            [ActivityTextId.PollFailed] = E("overview.activity.pollFailed", "制造执行系统轮询失败", "MES poll failed"),
            [ActivityTextId.PollRecovered] = E("overview.activity.pollRecovered", "制造执行系统轮询已恢复", "MES poll recovered"),
            [ActivityTextId.UnknownHeading] = E("overview.activity.unknownHeading", "未知概览事件（请升级应用）", "Unknown activity (upgrade Watch)"),
            [ActivityTextId.SeriesStartedDetail] = E("overview.activity.seriesStartedDetail", "服务端已开始持续跟踪该需求系列。", "The Host started tracking this demand series."),
            [ActivityTextId.DemandGoneDetail] = E("overview.activity.demandGoneDetail", "当前制造执行系统快照中已找不到该需求，等待归档条件确认。", "The demand is absent from the current MES snapshot and awaits archive confirmation."),
            [ActivityTextId.SeriesArchivedDetail] = E("overview.activity.seriesArchivedDetail", "需求持续消失达到归档时限，已停止活动跟踪。", "The demand remained absent through the archive timeout and is no longer actively tracked."),
            [ActivityTextId.DemandReappearedDetail] = E("overview.activity.demandReappearedDetail", "此前消失的需求再次被制造执行系统观测到。", "A previously absent demand was observed in MES again."),
            [ActivityTextId.ProtectionEnteredDetail] = E("overview.activity.protectionEnteredDetail", "当前工序数据不足，系统暂停据此判定需求消失。", "Work-type data is incomplete, so absence decisions are temporarily protected."),
            [ActivityTextId.ProtectionRecoveringDetail] = E("overview.activity.protectionRecoveringDetail", "工序数据正在恢复，保护仍暂时生效。", "Work-type data is recovering; protection remains active for now."),
            [ActivityTextId.ProtectionClearedDetail] = E("overview.activity.protectionClearedDetail", "工序数据已稳定恢复，保护已解除。", "Work-type data is stable again and protection has cleared."),
            [ActivityTextId.AbsenceAuthorityRestoredDetail] = E("overview.activity.absenceAuthorityRestoredDetail", "该工序已重新具备需求缺失判定条件。", "This work type can make absence decisions again."),
            [ActivityTextId.UnassignedAppearedDetail] = E("overview.activity.unassignedAppearedDetail", "制造执行系统返回了暂时无法归入需求系列的记录。", "MES returned an observation that cannot yet be assigned to a demand series."),
            [ActivityTextId.UnassignedChangedDetail] = E("overview.activity.unassignedChangedDetail", "未分配记录的内容与上次观测不同。", "The unassigned observation differs from its previous value."),
            [ActivityTextId.UnassignedClearedDetail] = E("overview.activity.unassignedClearedDetail", "此前未分配的记录已不再出现。", "The previously unassigned observation is no longer present."),
            [ActivityTextId.PollFailedDetail] = E("overview.activity.pollFailedDetail", "本次制造执行系统读取未成功，服务端将继续重试。", "This MES read failed; the Host will retry."),
            [ActivityTextId.PollRecoveredDetail] = E("overview.activity.pollRecoveredDetail", "制造执行系统读取已恢复成功。", "MES reads are succeeding again."),
            [ActivityTextId.UnknownDetail] = E("overview.activity.unknownDetail", "当前应用版本无法解释该事件；请升级后查看。", "This Watch version cannot explain the event; upgrade to view it."),
            [ActivityTextId.SeriesLabel] = E("overview.activity.seriesLabel", "需求系列：", "Series: "),
            [ActivityTextId.WorkTypeLabel] = E("overview.activity.workTypeLabel", "工序类型：", "Work type: "),
            [ActivityTextId.PollTraceLabel] = E("overview.activity.pollTraceLabel", "轮询追踪：", "Poll: "),
            [ActivityTextId.SubjectLabel] = E("overview.activity.subjectLabel", "对象类型：", "Subject: "),
            [ActivityTextId.EventLabel] = E("overview.activity.eventLabel", "事件：", "Event: "),
            [ActivityTextId.ErrorSeverity] = E("overview.activity.errorSeverity", "错误", "Error"),
            [ActivityTextId.WarningSeverity] = E("overview.activity.warningSeverity", "警告", "Attention"),
            [ActivityTextId.RecoveredSeverity] = E("overview.activity.recoveredSeverity", "已恢复", "Recovered"),
            [ActivityTextId.InformationSeverity] = E("overview.activity.informationSeverity", "信息", "Information"),
            [ActivityTextId.DieToWireStaging] = E("overview.activity.workType.dieToWireStaging", "装片机台 → 焊线/键合派工待送区", "DIE_TO_WIRE_STAGING"),
            [ActivityTextId.DieToOven] = E("overview.activity.workType.dieToOven", "装片机台 → 烘箱间", "DIE_TO_OVEN"),
            [ActivityTextId.WireToGate] = E("overview.activity.workType.wireToGate", "焊线/键合机台 → 人工质检关卡区", "WIRE_TO_GATE"),
            [ActivityTextId.WireToOptical] = E("overview.activity.workType.wireToOptical", "焊线/键合机台 → 三光区", "WIRE_TO_OPTICAL"),
            [ActivityTextId.StagingToWire] = E("overview.activity.workType.stagingToWire", "焊线/键合派工待送区 → 指定焊线/键合机台", "STAGING_TO_WIRE"),
            [ActivityTextId.WireToNitrogen] = E("overview.activity.workType.wireToNitrogen", "焊线1机台 → 氮气柜", "WIRE_TO_NITROGEN"),
            [ActivityTextId.UnknownWorkType] = E("overview.activity.workType.unknown", "未知工序（请升级应用）", "Unknown work type (upgrade Watch)"),
            [ActivityTextId.MissingHeading] = E("overview.activity.error.missingHeading", "{0} 缺失", "{0} is missing"),
            [ActivityTextId.InvalidHeading] = E("overview.activity.error.invalidHeading", "{0} 格式无效", "{0} format is invalid"),
            [ActivityTextId.DuplicateHeading] = E("overview.activity.error.duplicateHeading", "运输需求出现重复观测", "Duplicate transport-demand observations"),
            [ActivityTextId.MultipleWorkTypesHeading] = E("overview.activity.error.multipleWorkTypesHeading", "子批次同时出现在多个工序", "Sublot appears in multiple work types"),
            [ActivityTextId.LongGoneVisibleHeading] = E("overview.activity.error.longGoneVisibleHeading", "已归档需求重新出现在制造执行系统", "Archived demand reappeared in MES"),
            [ActivityTextId.ErrorStartedHeading] = E("overview.activity.error.startedHeading", "需求系列错误已开始", "Demand data validation failed"),
            [ActivityTextId.SubjectRecoveredHeading] = E("overview.activity.error.subjectRecoveredHeading", "{0} 错误已恢复", "{0} error recovered"),
            [ActivityTextId.ErrorRecoveredHeading] = E("overview.activity.error.recoveredHeading", "需求系列错误已恢复", "Demand data error recovered"),
            [ActivityTextId.ErrorGoneHeading] = E("overview.activity.error.goneHeading", "需求消失，错误期间已结束", "Demand disappeared; error tracking stopped"),
            [ActivityTextId.ErrorArchivedHeading] = E("overview.activity.error.archivedHeading", "需求系列归档，错误期间已结束", "Demand series archived; error ended"),
            [ActivityTextId.ErrorEndedHeading] = E("overview.activity.error.endedHeading", "需求数据错误已结束", "Demand data error ended"),
            [ActivityTextId.InvalidValueDetail] = E("overview.activity.error.invalidValueDetail", "收到 {0}，预期类似 {1}", "Received {0}; expected a value like {1}"),
            [ActivityTextId.MissingValueDetail] = E("overview.activity.error.missingValueDetail", "制造执行系统未提供该必填字段。", "MES did not provide this required field."),
            [ActivityTextId.DuplicateDetail] = E("overview.activity.error.duplicateDetail", "同一运输需求被观测到 {0:N0} 次。", "The same transport demand was observed {0:N0} times."),
            [ActivityTextId.MultipleWorkTypesDetail] = E("overview.activity.error.multipleWorkTypesDetail", "同一子批次同时出现在：{0}。", "The same sublot appears in: {0}."),
            [ActivityTextId.ListSeparator] = E("overview.activity.listSeparator", "、", ", "),
            [ActivityTextId.LongGoneVisibleDetail] = E("overview.activity.error.longGoneVisibleDetail", "该需求已归档，但当前制造执行系统快照仍可见。", "The demand is archived but remains visible in the current MES snapshot."),
            [ActivityTextId.ValidationFailedDetail] = E("overview.activity.error.validationFailedDetail", "服务端检测到需求数据不符合业务规则。", "The Host detected demand data that does not satisfy business rules."),
            [ActivityTextId.MissingClearedDetail] = E("overview.activity.error.missingClearedDetail", "{0} 已补齐。", "{0} is now present."),
            [ActivityTextId.InvalidClearedDetail] = E("overview.activity.error.invalidClearedDetail", "{0} 已恢复为有效格式。", "{0} now has a valid format."),
            [ActivityTextId.ConditionClearedDetail] = E("overview.activity.error.conditionClearedDetail", "错误条件已消除，错误已解除。", "The triggering condition cleared and the error is resolved."),
            [ActivityTextId.DemandGoneErrorDetail] = E("overview.activity.error.demandGoneDetail", "需求已消失，{0} 错误停止跟踪。", "The demand disappeared, so {0} error tracking stopped."),
            [ActivityTextId.ArchivedErrorDetail] = E("overview.activity.error.archivedDetail", "需求系列已归档，错误随之结束。", "The demand series was archived, ending the error."),
            [ActivityTextId.ErrorEndedDetail] = E("overview.activity.error.endedDetail", "错误期间已结束。", "The error period ended."),
            [ActivityTextId.DemandField] = E("overview.activity.subject.demandField", "需求字段", "Demand field"),
            [ActivityTextId.AreaSubject] = E("overview.activity.subject.area", "区域", "AREA"),
            [ActivityTextId.EquipmentSubject] = E("overview.activity.subject.equipment", "设备", "EQP"),
            [ActivityTextId.StepSubject] = E("overview.activity.subject.step", "下一工序", "STEP"),
            [ActivityTextId.DatesSubject] = E("overview.activity.subject.dates", "来源时间", "DATES"),
            [ActivityTextId.PackageSubject] = E("overview.activity.subject.package", "封装形式", "PACKAGE"),
            [ActivityTextId.UnknownSubject] = E("overview.activity.subject.unknown", "未知对象（请升级应用）", "Unknown subject (upgrade Watch)"),
            [ActivityTextId.ValidValue] = E("overview.activity.validValue", "有效值", "a valid value"),
        };

    private static readonly IReadOnlyList<WatchTextCatalogEntry> CatalogEntries =
    [
        PageTitleEntry, SeriesEntry, ReadabilityEntry, ErrorsEntry, AreaEntry, AttentionEntry,
        ViewEntry, ViewAllFirstPageEntry, ManageEntry, ReadableEntry, NotReadableEntry, ActiveEntry,
        PriorSevenDaysEntry, RecentEntry, RecentHelpEntry, DescendingEntry, NoRecentEntry, SeriesUnitEntry,
        DemandUnitEntry, ErrorSeriesUnitEntry, AttentionUnitEntry, NoHostSnapshotEntry,
        WaitingEntry, HostNoScopeEntry, ProtectionEntry, SnapshotEntry, AllAreaEntry,
        ContextAutomationEntry, NoReadNoticeEntry, ReadNoticeEntry, NoStaleEntry,
        AttentionAutomationEntry, HealthAutomationEntry, ErrorSeverityEntry, WarningSeverityEntry,
        SeriesLifecycleActivityEntry, SeriesErrorPeriodActivityEntry, ConciseNoScopeEntry,
        ConciseAllScopeEntry, ConciseSomeScopeEntry, ConciseManyScopeEntry,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries =>
        [.. CatalogEntries, .. ActivityEntries.Values, .. WatchLegacyGeneratedText.OverviewEntries];

    public string PageTitle => Text(PageTitleEntry);
    public string Series => Text(SeriesEntry);
    public string Readability => Text(ReadabilityEntry);
    public string Errors => Text(ErrorsEntry);
    public string Area => Text(AreaEntry);
    public string Attention => Text(AttentionEntry);
    public string View => Text(ViewEntry);
    public string ViewAllFirstPage(string page) => string.Format(Text(ViewAllFirstPageEntry), page);
    public string Manage => Text(ManageEntry);
    public string Readable => Text(ReadableEntry);
    public string NotReadable => Text(NotReadableEntry);
    public string Active => Text(ActiveEntry);
    public string PriorSevenDays => Text(PriorSevenDaysEntry);
    public string RecentHighlights => Text(RecentEntry);
    public string RecentHighlightsHelp => Text(RecentHelpEntry);
    public string NewestFirst => Text(DescendingEntry);
    public string NoRecentHighlights => Text(NoRecentEntry);
    public string SeriesUnit => Text(SeriesUnitEntry);
    public string DemandUnit => Text(DemandUnitEntry);
    public string ErrorSeriesUnit => Text(ErrorSeriesUnitEntry);
    public string AttentionUnit => Text(AttentionUnitEntry);
    public string NoHostSnapshot => Text(NoHostSnapshotEntry);
    public string WaitingForHost => Text(WaitingEntry);
    public string HostNoScope => Text(HostNoScopeEntry);
    public string Protection => Text(ProtectionEntry);
    public string Snapshot => Text(SnapshotEntry);
    public string AllArea => Text(AllAreaEntry);
    public string ContextAutomation(string context) => string.Format(Text(ContextAutomationEntry), context);
    public string ReadStateAutomation(string title, string message, bool isOpen) => isOpen
        ? string.Format(Text(ReadNoticeEntry), title, message)
        : Text(NoReadNoticeEntry);
    public string StaleAutomation(string staleText, bool isStale) => isStale
        ? staleText
        : Text(NoStaleEntry);
    public string AttentionAutomation(string status, string detail, string facets) =>
        string.Format(Text(AttentionAutomationEntry), status, detail, facets);
    public string HealthAutomation(string status, string detail) =>
        string.Format(Text(HealthAutomationEntry), status, detail);
    public string ConciseHostAreaScope(IReadOnlyList<string>? areas, bool hasSnapshot)
    {
        if (!hasSnapshot)
        {
            return Text(ConciseNoScopeEntry);
        }

        if (areas is null || areas.Count == 0)
        {
            return Text(ConciseAllScopeEntry);
        }

        const int visibleAreaCount = 2;
        var visible = string.Join(WatchTextCatalog.For(Language).Common.ListSeparator, areas.Take(visibleAreaCount));
        return areas.Count <= visibleAreaCount
            ? string.Format(Text(ConciseSomeScopeEntry), visible)
            : string.Format(Text(ConciseManyScopeEntry), visible, areas.Count);
    }

    public string LocalAreaDetail(string state, IReadOnlyList<string> areas, DateTimeOffset? updatedAt, WatchTextCatalog catalog)
    {
        state = LocalState(state);
        var result = areas.Count == 0
            ? Format(WatchLegacyGeneratedText.Overview030, new object?[] { state }, new object?[] { state })
            : Format(WatchLegacyGeneratedText.Overview031, new object?[] { state, areas.Count }, new object?[] { state, areas.Count });
        return updatedAt is null
            ? result
            : Format(WatchLegacyGeneratedText.Overview032, new object?[] { result, catalog.FormatAbsoluteTime(updatedAt.Value) }, new object?[] { result, catalog.FormatAbsoluteTime(updatedAt.Value) });
    }

    public string SnapshotFacts(DateTimeOffset asOf, DateTimeOffset committedAt, long sequence, DateTimeOffset now, WatchTextCatalog catalog)
    {
        var absolute = catalog.FormatAbsoluteTime(asOf);
        var relative = catalog.FormatRelativeTime(asOf, now);
        return Format(WatchLegacyGeneratedText.Overview033, new object?[] { absolute, relative, catalog.FormatAbsoluteTime(committedAt), sequence }, new object?[] { absolute, relative, catalog.FormatAbsoluteTime(committedAt), sequence });
    }

    public string ClientAttempts(DateTimeOffset? successfulAt, DateTimeOffset? failedAt, WatchTextCatalog catalog)
    {
        var successful = successfulAt is { } success
            ? Format(WatchLegacyGeneratedText.Overview034, new object?[] { catalog.FormatAbsoluteTime(success) }, new object?[] { catalog.FormatAbsoluteTime(success) })
            : Select(WatchLegacyGeneratedText.Overview035);
        return failedAt is { } failure
            ? Format(WatchLegacyGeneratedText.Overview036, new object?[] { successful, catalog.FormatAbsoluteTime(failure) }, new object?[] { successful, catalog.FormatAbsoluteTime(failure) })
            : successful;
    }

    public string SeriesDetail(long tracking, long archived, long gone, long longGoneVisible) =>
        Format(WatchLegacyGeneratedText.Overview037, new object?[] { tracking, archived, gone, longGoneVisible }, new object?[] { tracking, archived, gone, longGoneVisible });

    public string ReadabilityDetail(long notReadable) => Format(WatchLegacyGeneratedText.Overview038, new object?[] { notReadable }, new object?[] { notReadable });

    public string ErrorsDetail(long priorSevenDays) => Format(WatchLegacyGeneratedText.Overview039, new object?[] { priorSevenDays }, new object?[] { priorSevenDays });

    public string AttentionDetail(long errors, long warnings) =>
        Language is WatchDisplayLanguage.SimplifiedChinese
            ? string.Format(
                CultureInfo.InvariantCulture,
                "当前接入关注项 · {0:N0} 个错误 · {1:N0} 个警告",
                errors,
                warnings)
            : Format(
                WatchLegacyGeneratedText.Overview040,
                new object?[] { errors, warnings },
                new object?[] { errors, warnings });

    public string HostAreas(IReadOnlyList<string> areas) => areas.Count == 0
        ? Select(WatchLegacyGeneratedText.Overview041)
        : areas.Count <= 4
            ? Format(WatchLegacyGeneratedText.Overview042, new object?[] { string.Join("、", areas) }, new object?[] { string.Join(", ", areas) })
            : Format(WatchLegacyGeneratedText.Overview043, new object?[] { areas.Count, string.Join("、", areas.Take(3)) }, new object?[] { areas.Count, string.Join(", ", areas.Take(3)) });

    public string ActivityKind(string kind) => kind switch
    {
        CurrentIngestAttentionKinds.SeriesError => Errors,
        CurrentIngestAttentionKinds.PollRunFailure => Select(WatchLegacyGeneratedText.Overview044),
        CurrentIngestAttentionKinds.TaskTypeProtection => Select(WatchLegacyGeneratedText.Overview045),
        CurrentIngestAttentionKinds.UnassignedMesObservation => Select(WatchLegacyGeneratedText.Overview046),
        "SERIES_LIFECYCLE" => Text(SeriesLifecycleActivityEntry),
        "SERIES_ERROR_PERIOD" => Text(SeriesErrorPeriodActivityEntry),
        _ => kind,
    };

    public string ActivityHeading(string eventType, WatchOverviewActivityExplanation? explanation) => eventType switch
    {
        "DEMAND_SERIES_STARTED" => ActivityText(ActivityTextId.SeriesStarted),
        "DEMAND_GONE" => ActivityText(ActivityTextId.DemandGone),
        "GONE_TIMEOUT_ARCHIVED" => ActivityText(ActivityTextId.SeriesArchived),
        "TRANSPORT_DEMAND_CREATED" => ActivityText(ActivityTextId.DemandReappeared),
        "SERIES_ERROR_PERIOD_STARTED" => ErrorHeading(explanation),
        "SERIES_ERROR_PERIOD_ENDED" => ErrorEndedHeading(explanation),
        "TASK_TYPE_PROTECTION_ENTERED" => ActivityText(ActivityTextId.ProtectionEntered),
        "TASK_TYPE_PROTECTION_RECOVERY_PROGRESS" => ActivityText(ActivityTextId.ProtectionRecovering),
        "TASK_TYPE_PROTECTION_CLEARED" => ActivityText(ActivityTextId.ProtectionCleared),
        "TASK_TYPE_ABSENCE_AUTHORITY_RESTORED" => ActivityText(ActivityTextId.AbsenceAuthorityRestored),
        "UNASSIGNED_MES_OBSERVATION_APPEARED" => ActivityText(ActivityTextId.UnassignedAppeared),
        "UNASSIGNED_MES_OBSERVATION_CONTENT_CHANGED" => ActivityText(ActivityTextId.UnassignedChanged),
        "UNASSIGNED_MES_OBSERVATION_CLEARED" => ActivityText(ActivityTextId.UnassignedCleared),
        "POLL_RUN_FAILED" => ActivityText(ActivityTextId.PollFailed),
        "POLL_RUN_RECOVERED" => ActivityText(ActivityTextId.PollRecovered),
        _ => ActivityText(ActivityTextId.UnknownHeading),
    };

    public string ActivityExplanation(string eventType, WatchOverviewActivityExplanation? explanation) => eventType switch
    {
        "SERIES_ERROR_PERIOD_STARTED" => ErrorStartedExplanation(explanation),
        "SERIES_ERROR_PERIOD_ENDED" => ErrorEndedExplanation(explanation),
        "DEMAND_SERIES_STARTED" => ActivityText(ActivityTextId.SeriesStartedDetail),
        "DEMAND_GONE" => ActivityText(ActivityTextId.DemandGoneDetail),
        "GONE_TIMEOUT_ARCHIVED" => ActivityText(ActivityTextId.SeriesArchivedDetail),
        "TRANSPORT_DEMAND_CREATED" => ActivityText(ActivityTextId.DemandReappearedDetail),
        "TASK_TYPE_PROTECTION_ENTERED" => ActivityText(ActivityTextId.ProtectionEnteredDetail),
        "TASK_TYPE_PROTECTION_RECOVERY_PROGRESS" => ActivityText(ActivityTextId.ProtectionRecoveringDetail),
        "TASK_TYPE_PROTECTION_CLEARED" => ActivityText(ActivityTextId.ProtectionClearedDetail),
        "TASK_TYPE_ABSENCE_AUTHORITY_RESTORED" => ActivityText(ActivityTextId.AbsenceAuthorityRestoredDetail),
        "UNASSIGNED_MES_OBSERVATION_APPEARED" => ActivityText(ActivityTextId.UnassignedAppearedDetail),
        "UNASSIGNED_MES_OBSERVATION_CONTENT_CHANGED" => ActivityText(ActivityTextId.UnassignedChangedDetail),
        "UNASSIGNED_MES_OBSERVATION_CLEARED" => ActivityText(ActivityTextId.UnassignedClearedDetail),
        "POLL_RUN_FAILED" => string.IsNullOrWhiteSpace(explanation?.SafeDetail)
            ? ActivityText(ActivityTextId.PollFailedDetail)
            : explanation.SafeDetail!,
        "POLL_RUN_RECOVERED" => ActivityText(ActivityTextId.PollRecoveredDetail),
        _ => ActivityText(ActivityTextId.UnknownDetail),
    };

    public string ActivityMetadata(WatchOverviewActivitySnapshot activity)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(activity.SeriesId))
        {
            parts.Add($"{ActivityText(ActivityTextId.SeriesLabel)}{ShortId(activity.SeriesId)}");
        }

        if (!string.IsNullOrWhiteSpace(activity.WorkType))
        {
            parts.Add($"{ActivityText(ActivityTextId.WorkTypeLabel)}{WorkType(activity.WorkType)}");
        }

        if (!string.IsNullOrWhiteSpace(activity.PollTraceId))
        {
            parts.Add($"{ActivityText(ActivityTextId.PollTraceLabel)}{ShortId(activity.PollTraceId)}");
        }

        if (!string.IsNullOrWhiteSpace(activity.Explanation?.SubjectKind))
        {
            parts.Insert(0, $"{ActivityText(ActivityTextId.SubjectLabel)}{SubjectLabel(activity.Explanation.SubjectKind)}");
        }

        return parts.Count == 0
            ? $"{ActivityText(ActivityTextId.EventLabel)}{ShortId(activity.EventId)}"
            : string.Join(" · ", parts);
    }

    public string ActivityTechnicalDetail(WatchOverviewActivitySnapshot activity) => string.Join(
        " · ",
        new[]
        {
            $"EventType={activity.EventType}",
            $"EventId={activity.EventId}",
            activity.SeriesId is null ? null : $"SeriesId={activity.SeriesId}",
            activity.WorkType is null ? null : $"WorkType={activity.WorkType}",
            activity.PollTraceId is null ? null : $"PollTraceId={activity.PollTraceId}",
            activity.Explanation?.Code is null ? null : $"Code={activity.Explanation.Code}",
            activity.Explanation?.SubjectKind is null ? null : $"SubjectKind={activity.Explanation.SubjectKind}",
        }.Where(value => value is not null));

    public string ActivitySeverity(WatchPresentationSeverity severity) => severity switch
    {
        WatchPresentationSeverity.Error => ActivityText(ActivityTextId.ErrorSeverity),
        WatchPresentationSeverity.Warning => ActivityText(ActivityTextId.WarningSeverity),
        WatchPresentationSeverity.Success => ActivityText(ActivityTextId.RecoveredSeverity),
        _ => ActivityText(ActivityTextId.InformationSeverity),
    };

    public string WorkType(string workType) => workType switch
        {
            "DIE_TO_WIRE_STAGING" => ActivityText(ActivityTextId.DieToWireStaging),
            "DIE_TO_OVEN" => ActivityText(ActivityTextId.DieToOven),
            "WIRE_TO_GATE" => ActivityText(ActivityTextId.WireToGate),
            "WIRE_TO_OPTICAL" => ActivityText(ActivityTextId.WireToOptical),
            "STAGING_TO_WIRE" => ActivityText(ActivityTextId.StagingToWire),
            "WIRE_TO_NITROGEN" => ActivityText(ActivityTextId.WireToNitrogen),
            _ => ActivityText(ActivityTextId.UnknownWorkType),
        };

    private string ErrorHeading(WatchOverviewActivityExplanation? explanation) => explanation?.Code switch
    {
        "REQUIRED_MES_FIELD_MISSING" => ActivityFormat(ActivityTextId.MissingHeading, Subject(explanation.SubjectKind)),
        "INVALID_MES_FIELD_FORMAT" => ActivityFormat(ActivityTextId.InvalidHeading, Subject(explanation.SubjectKind)),
        "DUPLICATE_TRANSPORT_DEMAND_KEY" => ActivityText(ActivityTextId.DuplicateHeading),
        "SUBLOT_MULTIPLE_WORK_TYPES" => ActivityText(ActivityTextId.MultipleWorkTypesHeading),
        "LONG_GONE_BUT_VISIBLE" => ActivityText(ActivityTextId.LongGoneVisibleHeading),
        _ => ActivityText(ActivityTextId.ErrorStartedHeading),
    };

    private string ErrorEndedHeading(WatchOverviewActivityExplanation? explanation) => explanation?.EndReason switch
    {
        "CONDITION_CLEARED" when !string.IsNullOrWhiteSpace(explanation.SubjectKind) =>
            ActivityFormat(ActivityTextId.SubjectRecoveredHeading, Subject(explanation.SubjectKind)),
        "CONDITION_CLEARED" => ActivityText(ActivityTextId.ErrorRecoveredHeading),
        "DEMAND_GONE" => ActivityText(ActivityTextId.ErrorGoneHeading),
        "SERIES_ARCHIVED" or "GONE_TIMEOUT_ARCHIVED" => ActivityText(ActivityTextId.ErrorArchivedHeading),
        _ => ActivityText(ActivityTextId.ErrorEndedHeading),
    };

    private string ErrorStartedExplanation(WatchOverviewActivityExplanation? explanation) => explanation?.Code switch
    {
        "INVALID_MES_FIELD_FORMAT" when !string.IsNullOrWhiteSpace(explanation.ObservedValue) =>
            string.Format(
                CultureInfo.InvariantCulture,
                ActivityText(ActivityTextId.InvalidValueDetail),
                explanation.ObservedValue,
                ExpectedExample(explanation)),
        "REQUIRED_MES_FIELD_MISSING" => ActivityText(ActivityTextId.MissingValueDetail),
        "DUPLICATE_TRANSPORT_DEMAND_KEY" => string.Format(
            CultureInfo.InvariantCulture,
            ActivityText(ActivityTextId.DuplicateDetail),
            explanation.ObservationCount ?? 2),
        "SUBLOT_MULTIPLE_WORK_TYPES" => string.Format(
            CultureInfo.InvariantCulture,
            ActivityText(ActivityTextId.MultipleWorkTypesDetail),
            string.Join(ActivityText(ActivityTextId.ListSeparator), explanation.RelatedWorkTypes?.Select(WorkType) ?? [])),
        "LONG_GONE_BUT_VISIBLE" => ActivityText(ActivityTextId.LongGoneVisibleDetail),
        _ => ActivityText(ActivityTextId.ValidationFailedDetail),
    };

    private string ErrorEndedExplanation(WatchOverviewActivityExplanation? explanation) => explanation?.EndReason switch
    {
        "CONDITION_CLEARED" when explanation.Code == "REQUIRED_MES_FIELD_MISSING" =>
            ActivityFormat(ActivityTextId.MissingClearedDetail, Subject(explanation.SubjectKind)),
        "CONDITION_CLEARED" when explanation.Code == "INVALID_MES_FIELD_FORMAT" =>
            ActivityFormat(ActivityTextId.InvalidClearedDetail, Subject(explanation.SubjectKind)),
        "CONDITION_CLEARED" => ActivityText(ActivityTextId.ConditionClearedDetail),
        "DEMAND_GONE" => ActivityFormat(ActivityTextId.DemandGoneErrorDetail, Subject(explanation.SubjectKind)),
        "SERIES_ARCHIVED" or "GONE_TIMEOUT_ARCHIVED" => ActivityText(ActivityTextId.ArchivedErrorDetail),
        _ => ActivityText(ActivityTextId.ErrorEndedDetail),
    };

    private string Subject(string? subjectKind) => string.IsNullOrWhiteSpace(subjectKind)
        ? ActivityText(ActivityTextId.DemandField)
        : subjectKind;

    private string SubjectLabel(string subjectKind) => subjectKind switch
        {
            "AREA" => ActivityText(ActivityTextId.AreaSubject),
            "EQP" => ActivityText(ActivityTextId.EquipmentSubject),
            "STEP" => ActivityText(ActivityTextId.StepSubject),
            "DATES" => ActivityText(ActivityTextId.DatesSubject),
            "PACKAGE" => ActivityText(ActivityTextId.PackageSubject),
            _ => ActivityText(ActivityTextId.UnknownSubject),
        };

    private string ExpectedExample(WatchOverviewActivityExplanation? explanation)
    {
        if (!string.Equals(explanation?.SubjectKind, "AREA", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityText(ActivityTextId.ValidValue);
        }

        var value = explanation!.ObservedValue;
        var parts = value?.Split('-');
        if (parts is { Length: 2 }
            && parts[0].Length >= 2
            && char.IsLetter(parts[0][0])
            && int.TryParse(parts[0][1..], CultureInfo.InvariantCulture, out var group)
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var station)
            && group is >= 1 and <= 99
            && station is >= 1 and <= 99)
        {
            return $"{char.ToUpperInvariant(parts[0][0])}{group}-{station}";
        }

        return "A1-1";
    }

    private static string ShortId(string value) => value.Length <= 10 ? value : $"{value[..8]}…";

    private string ActivityText(ActivityTextId id) => Text(ActivityEntries[id]);

    private string ActivityFormat(ActivityTextId id, params object?[] values) =>
        string.Format(CultureInfo.InvariantCulture, ActivityText(id), values);

    public string RefreshPolicy(int seconds) => Format(WatchLegacyGeneratedText.Overview047, new object?[] { seconds }, new object?[] { seconds });

    public string StaleSnapshot => Select(WatchLegacyGeneratedText.Overview048);

    public string ProtectionUnavailable => Select(WatchLegacyGeneratedText.Overview049);
    public string ProtectionUnavailableDetail => Select(WatchLegacyGeneratedText.Overview050);
    public string HistoryResetPending => Select(WatchLegacyGeneratedText.Overview051);
    public string HistoryResetDetail(string epoch) => Format(WatchLegacyGeneratedText.Overview052, new object?[] { epoch }, new object?[] { epoch });
    public string HistoryResetOverviewDetail(string epoch) => Format(WatchLegacyGeneratedText.Overview053, new object?[] { epoch }, new object?[] { epoch });
    public string StorageObserved(string volume, decimal availablePercent, DateTimeOffset observedAt, WatchTextCatalog catalog) =>
        Format(WatchLegacyGeneratedText.Overview054, new object?[] { volume, availablePercent, catalog.FormatAbsoluteTime(observedAt) }, new object?[] { volume, availablePercent, catalog.FormatAbsoluteTime(observedAt) });
    public string StoragePaused(string observed) => Format(WatchLegacyGeneratedText.Overview055, new object?[] { observed }, new object?[] { observed });
    public string StorageWarning => Select(WatchLegacyGeneratedText.Overview056);
    public string StorageWarningDetail(string observed) => Format(WatchLegacyGeneratedText.Overview057, new object?[] { observed }, new object?[] { observed });
    public string StorageHealthy => Select(WatchLegacyGeneratedText.Overview058);
    public string StorageHealthyDetail(string observed, string epoch) => Format(WatchLegacyGeneratedText.Overview059, new object?[] { observed, epoch }, new object?[] { observed, epoch });
    public string StorageNeedsAttention => Select(WatchLegacyGeneratedText.Overview060);
    public string StorageNeedsAttentionDetail => Select(WatchLegacyGeneratedText.Overview061);
    public string NoProtectionReported => Select(WatchLegacyGeneratedText.Overview062);
    public string NoProtectionDetail(DateTimeOffset snapshotAt, WatchTextCatalog catalog) => Format(WatchLegacyGeneratedText.Overview063, new object?[] { catalog.FormatAbsoluteTime(snapshotAt) }, new object?[] { catalog.FormatAbsoluteTime(snapshotAt) });
    public string WaitingProtection => Select(WatchLegacyGeneratedText.Overview064);

    public string HostStatus(WatchHostConnectionStatus status, bool hasRecentFailure) => (status, hasRecentFailure) switch
    {
        (WatchHostConnectionStatus.NotConfigured, _) => Select(WatchLegacyGeneratedText.OverviewHostDisconnected),
        (WatchHostConnectionStatus.Connecting, _) => Select(WatchLegacyGeneratedText.OverviewHostConnecting),
        (WatchHostConnectionStatus.Failed, _) => Select(WatchLegacyGeneratedText.OverviewHostFailed),
        (WatchHostConnectionStatus.Connected, true) => Select(WatchLegacyGeneratedText.OverviewHostConnectedRecentFailure),
        (WatchHostConnectionStatus.Connected, false) => Select(WatchLegacyGeneratedText.OverviewHostConnected),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public string ContractCompatible => Select(WatchLegacyGeneratedText.Overview065);
    public string AddressNotConfigured => Select(WatchLegacyGeneratedText.Overview066);
    public string CannotConnectHost => Select(WatchLegacyGeneratedText.Overview067);
    public string ConnectingHost => Select(WatchLegacyGeneratedText.Overview068);
    public string ReadingOverview => Select(WatchLegacyGeneratedText.Overview069);
    public string RefreshingOverview => Select(WatchLegacyGeneratedText.Overview070);
    public string OverviewReadFailed => Select(WatchLegacyGeneratedText.Overview071);
    public string OverviewRefreshFailedRetained => Select(WatchLegacyGeneratedText.Overview072);
    public string CurrentScope => Select(WatchLegacyGeneratedText.Overview073);
    public string ScopeHelp => Select(WatchLegacyGeneratedText.Overview074);
    public string ManageAreaFilters => Select(WatchLegacyGeneratedText.Overview075);
    public string WaitingSeverityFacets => Select(WatchLegacyGeneratedText.Overview076);
    public string NoSeverityFacets => Select(WatchLegacyGeneratedText.Overview077);
    public string RecentEmptyExplanation => Select(WatchLegacyGeneratedText.Overview078);
    public string OpenActivity(string heading) => Format(WatchLegacyGeneratedText.Overview079, new object?[] { heading }, new object?[] { heading });
    public string AttentionFacet(string value) => value switch
    {
        CurrentIngestAttentionKinds.SeriesError => Select(WatchLegacyGeneratedText.Overview080),
        CurrentIngestAttentionKinds.PollRunFailure => Select(WatchLegacyGeneratedText.Overview081),
        CurrentIngestAttentionKinds.TaskTypeProtection => Select(WatchLegacyGeneratedText.Overview045),
        CurrentIngestAttentionKinds.UnassignedMesObservation => Select(WatchLegacyGeneratedText.Overview082),
        CurrentIngestAttentionKinds.HistoryCleanupFailure => Select(WatchLegacyGeneratedText.Overview083),
        CurrentIngestAttentionKinds.StoragePressure => Select(WatchLegacyGeneratedText.Overview084),
        CurrentIngestAttentionKinds.HistoryReset => Select(WatchLegacyGeneratedText.Overview085),
        CurrentIngestAttentionSeverities.Error => Text(ErrorSeverityEntry),
        CurrentIngestAttentionSeverities.Warning => Text(WarningSeverityEntry),
        _ => value,
    };
    public string WaitingAtomicSnapshot => Select(WatchLegacyGeneratedText.Overview086);
    public string RetainedDuringRefresh(DateTimeOffset snapshotAt, WatchTextCatalog catalog) => Format(WatchLegacyGeneratedText.Overview087, new object?[] { catalog.FormatAbsoluteTime(snapshotAt) }, new object?[] { catalog.FormatAbsoluteTime(snapshotAt) });
    public string PriorFailureRetry(DateTimeOffset failedAt, WatchTextCatalog catalog) => Format(WatchLegacyGeneratedText.Overview088, new object?[] { catalog.FormatAbsoluteTime(failedAt) }, new object?[] { catalog.FormatAbsoluteTime(failedAt) });
    public string NoSuccessfulSnapshot => Select(WatchLegacyGeneratedText.Overview089);
    public string RetainedAfterFailure(DateTimeOffset snapshotAt, WatchTextCatalog catalog) => Format(WatchLegacyGeneratedText.Overview090, new object?[] { catalog.FormatAbsoluteTime(snapshotAt) }, new object?[] { catalog.FormatAbsoluteTime(snapshotAt) });
    public string FailedAt(DateTimeOffset failedAt, string retained, string failure, WatchTextCatalog catalog) => Format(WatchLegacyGeneratedText.Overview091, new object?[] { catalog.FormatAbsoluteTime(failedAt), retained, failure }, new object?[] { catalog.FormatAbsoluteTime(failedAt), retained, failure });
    public string ReplacementHostFailure(string failure) => Format(WatchLegacyGeneratedText.Overview092, new object?[] { failure }, new object?[] { failure });
    public string FailureMessage(string? message, string? correlationId)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}";
        return string.IsNullOrWhiteSpace(correlationId)
            ? detail
            : Format(WatchLegacyGeneratedText.Overview093, new object?[] { detail, correlationId }, new object?[] { detail, correlationId });
    }

    public string LocalState(string raw) => raw switch
    {
        "本机默认" => Select(WatchLegacyGeneratedText.OverviewLocalDefault),
        "本机配置" => Select(WatchLegacyGeneratedText.OverviewLocalConfiguration),
        "本机已应用" => Select(WatchLegacyGeneratedText.OverviewLocalApplied),
        _ => raw,
    };

    public string OverviewHostStatusAutomation(string status) =>
        Format(WatchLegacyGeneratedText.Overview094, new object?[] { status }, new object?[] { status });

    public string HostNavigationAutomation(
        string status,
        string protectionDetail) =>
        Format(WatchLegacyGeneratedText.Overview095, new object?[] { status, protectionDetail }, new object?[] { status, protectionDetail });

    public string RecentPageReadFailure(DateTimeOffset failedAt, WatchTextCatalog catalog) =>
        Format(WatchLegacyGeneratedText.Overview096, new object?[] { catalog.FormatAbsoluteTime(failedAt) }, new object?[] { catalog.FormatAbsoluteTime(failedAt) });

    public string SettingsHostStatusAutomation(string status) =>
        Format(WatchLegacyGeneratedText.Overview097, new object?[] { status }, new object?[] { status });
}
