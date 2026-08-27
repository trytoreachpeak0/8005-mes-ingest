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

    private static readonly IReadOnlyList<WatchTextCatalogEntry> CatalogEntries =
    [
        PageTitleEntry, SeriesEntry, ReadabilityEntry, ErrorsEntry, AreaEntry, AttentionEntry,
        ViewEntry, ManageEntry, ReadableEntry, NotReadableEntry, ActiveEntry,
        PriorSevenDaysEntry, RecentEntry, RecentHelpEntry, DescendingEntry, NoRecentEntry, SeriesUnitEntry,
        DemandUnitEntry, ErrorSeriesUnitEntry, AttentionUnitEntry, NoHostSnapshotEntry,
        WaitingEntry, HostNoScopeEntry, ProtectionEntry, SnapshotEntry, AllAreaEntry,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => CatalogEntries;

    public string PageTitle => Text(PageTitleEntry);
    public string Series => Text(SeriesEntry);
    public string Readability => Text(ReadabilityEntry);
    public string Errors => Text(ErrorsEntry);
    public string Area => Text(AreaEntry);
    public string Attention => Text(AttentionEntry);
    public string View => Text(ViewEntry);
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

    public string LocalAreaDetail(string state, IReadOnlyList<string> areas, DateTimeOffset? updatedAt, WatchTextCatalog catalog)
    {
        state = LocalState(state);
        var result = areas.Count == 0
            ? Language == WatchDisplayLanguage.SimplifiedChinese
                ? $"{state} · 未限制 Host AREA 查询"
                : $"{state} · Host AREA query is unrestricted"
            : Language == WatchDisplayLanguage.SimplifiedChinese
                ? $"{state} · {areas.Count} 个 AREA"
                : $"{state} · {areas.Count} AREA values";
        return updatedAt is null
            ? result
            : Language == WatchDisplayLanguage.SimplifiedChinese
                ? $"{result} · 本机更新 {catalog.FormatAbsoluteTime(updatedAt.Value)}"
                : $"{result} · local update {catalog.FormatAbsoluteTime(updatedAt.Value)}";
    }

    public string SnapshotFacts(DateTimeOffset asOf, DateTimeOffset committedAt, long sequence, DateTimeOffset now, WatchTextCatalog catalog)
    {
        var absolute = catalog.FormatAbsoluteTime(asOf);
        var relative = catalog.FormatRelativeTime(asOf, now);
        return Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"Host 快照 {absolute}（{relative}） · 投影提交 {catalog.FormatAbsoluteTime(committedAt)} · 序列 {sequence}"
            : $"Host snapshot {absolute} ({relative}) · projection committed {catalog.FormatAbsoluteTime(committedAt)} · sequence {sequence}";
    }

    public string ClientAttempts(DateTimeOffset? successfulAt, DateTimeOffset? failedAt, WatchTextCatalog catalog)
    {
        var successful = successfulAt is { } success
            ? Language == WatchDisplayLanguage.SimplifiedChinese
                ? $"Watch 最近成功 {catalog.FormatAbsoluteTime(success)}"
                : $"Watch last succeeded {catalog.FormatAbsoluteTime(success)}"
            : Language == WatchDisplayLanguage.SimplifiedChinese
                ? "Watch 尚无成功读取"
                : "Watch has no successful read";
        return failedAt is { } failure
            ? Language == WatchDisplayLanguage.SimplifiedChinese
                ? $"{successful} · 最近失败 {catalog.FormatAbsoluteTime(failure)}"
                : $"{successful} · last failed {catalog.FormatAbsoluteTime(failure)}"
            : successful;
    }

    public string SeriesDetail(long tracking, long archived, long gone, long longGoneVisible) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"{tracking:N0} Tracking · {archived:N0} Archived · {gone:N0} GONE · {longGoneVisible:N0} 归档后仍可见"
            : $"{tracking:N0} Tracking · {archived:N0} Archived · {gone:N0} GONE · {longGoneVisible:N0} visible after archive";

    public string ReadabilityDetail(long notReadable) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"当前外部可读 · {notReadable:N0} 个不可见或阻断"
        : $"Currently externally readable · {notReadable:N0} invisible or blocked";

    public string ErrorsDetail(long priorSevenDays) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"活动错误 Series · 近 7 天 {priorSevenDays:N0} 个 Series"
        : $"Active error series · {priorSevenDays:N0} series in the prior 7 days";

    public string AttentionDetail(long errors, long warnings) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"当前接入关注项 · {errors:N0} ERROR · {warnings:N0} WARNING"
        : $"Current ingest attention · {errors:N0} ERROR · {warnings:N0} WARNING";

    public string HostAreas(IReadOnlyList<string> areas) => areas.Count == 0
        ? Language == WatchDisplayLanguage.SimplifiedChinese ? "Host 已提交范围：全部 AREA" : "Host committed scope: all AREA"
        : areas.Count <= 4
            ? Language == WatchDisplayLanguage.SimplifiedChinese
                ? $"Host 已提交范围：{string.Join("、", areas)}"
                : $"Host committed scope: {string.Join(", ", areas)}"
            : Language == WatchDisplayLanguage.SimplifiedChinese
                ? $"Host 已提交范围：{areas.Count} 个 AREA（{string.Join("、", areas.Take(3))}…）"
                : $"Host committed scope: {areas.Count} AREA values ({string.Join(", ", areas.Take(3))}…)";

    public string ActivityKind(string kind) => kind switch
    {
        CurrentIngestAttentionKinds.SeriesError => Errors,
        CurrentIngestAttentionKinds.PollRunFailure => Language == WatchDisplayLanguage.SimplifiedChinese ? "轮询运行" : "Poll run",
        CurrentIngestAttentionKinds.TaskTypeProtection => Language == WatchDisplayLanguage.SimplifiedChinese ? "任务类型保护" : "Task-type protection",
        CurrentIngestAttentionKinds.UnassignedMesObservation => Language == WatchDisplayLanguage.SimplifiedChinese ? "未分配 MES 观测" : "Unassigned MES observation",
        _ => kind,
    };

    public string RefreshPolicy(int seconds) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"自动刷新 {seconds} 秒"
        : $"Auto-refresh {seconds} seconds";

    public string StaleSnapshot => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "数据可能已过期；卡片仍属于上方标明的 Host 已提交范围。"
        : "Data may be stale; every card still belongs to the committed Host scope shown above.";

    public string ProtectionUnavailable => Language == WatchDisplayLanguage.SimplifiedChinese ? "保护状态不可用" : "Protection status unavailable";
    public string ProtectionUnavailableDetail => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "连接 Host 后读取 StoragePressure 与 HistoryEpoch 保护状态。"
        : "Connect to Host to read StoragePressure and HistoryEpoch protection status.";
    public string HistoryResetPending => Language == WatchDisplayLanguage.SimplifiedChinese ? "历史重置待确认" : "History reset awaiting acknowledgement";
    public string HistoryResetDetail(string epoch) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"HistoryEpoch {epoch} · 外部当前读取 503 INGEST_NOT_CURRENT · 仅可在数据库主机本地提交 HistoryResetAcknowledgement。"
        : $"HistoryEpoch {epoch} · external reads currently return 503 INGEST_NOT_CURRENT · HistoryResetAcknowledgement can be submitted only on the database host.";
    public string HistoryResetOverviewDetail(string epoch) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"HistoryEpoch {epoch} · 打开接入告警查看新纪元建立进度、503 原因与本地确认指引。"
        : $"HistoryEpoch {epoch} · open Ingest alerts for new-epoch progress, the 503 reason, and local acknowledgement guidance.";
    public string StorageObserved(string volume, decimal availablePercent, DateTimeOffset observedAt, WatchTextCatalog catalog) =>
        Language == WatchDisplayLanguage.SimplifiedChinese
            ? $"卷 {volume} 可用 {availablePercent:0.###}% · 观测 {catalog.FormatAbsoluteTime(observedAt)}"
            : $"Volume {volume} has {availablePercent:0.###}% available · observed {catalog.FormatAbsoluteTime(observedAt)}";
    public string StoragePaused(string observed) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"{observed} · MES 轮询暂停；仅可在数据库主机本地恢复。"
        : $"{observed} · MES polling is paused; recovery is available only on the database host.";
    public string StorageWarning => Language == WatchDisplayLanguage.SimplifiedChinese ? "存储空间严重告警" : "Critical storage-space warning";
    public string StorageWarningDetail(string observed) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"{observed} · 低于 15% 告警阈值，尚未进入暂停。"
        : $"{observed} · below the 15% warning threshold but not yet paused.";
    public string StorageHealthy => Language == WatchDisplayLanguage.SimplifiedChinese ? "存储与历史保护正常" : "Storage and history protection healthy";
    public string StorageHealthyDetail(string observed, string epoch) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"{observed} · 当前 HistoryEpoch {epoch}。"
        : $"{observed} · current HistoryEpoch {epoch}.";
    public string StorageNeedsAttention => Language == WatchDisplayLanguage.SimplifiedChinese ? "存储压力需处理" : "Storage pressure needs attention";
    public string StorageNeedsAttentionDetail => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "打开接入告警查看剩余空间、是否已暂停、最后成功窗口与本地恢复指引。"
        : "Open Ingest alerts for remaining space, pause state, the last successful window, and local recovery guidance.";
    public string NoProtectionReported => Language == WatchDisplayLanguage.SimplifiedChinese ? "未报告存储或历史保护项" : "No storage or history protection item reported";
    public string NoProtectionDetail(DateTimeOffset snapshotAt, WatchTextCatalog catalog) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"Host 快照 {catalog.FormatAbsoluteTime(snapshotAt)} 的当前关注分面中无 StoragePressure 或 HistoryReset。"
        : $"Host snapshot {catalog.FormatAbsoluteTime(snapshotAt)} has no StoragePressure or HistoryReset facet.";
    public string WaitingProtection => Language == WatchDisplayLanguage.SimplifiedChinese ? "等待保护状态" : "Waiting for protection status";

    public string HostStatus(WatchHostConnectionStatus status, bool hasRecentFailure) => (Language, status, hasRecentFailure) switch
    {
        (WatchDisplayLanguage.SimplifiedChinese, WatchHostConnectionStatus.NotConfigured, _) => "Host 未连接",
        (WatchDisplayLanguage.SimplifiedChinese, WatchHostConnectionStatus.Connecting, _) => "Host 连接中",
        (WatchDisplayLanguage.SimplifiedChinese, WatchHostConnectionStatus.Failed, _) => "Host 连接失败",
        (WatchDisplayLanguage.SimplifiedChinese, WatchHostConnectionStatus.Connected, true) => "Host 已连接 · 最近读取失败",
        (WatchDisplayLanguage.SimplifiedChinese, WatchHostConnectionStatus.Connected, false) => "Host 已连接",
        (WatchDisplayLanguage.English, WatchHostConnectionStatus.NotConfigured, _) => "Host disconnected",
        (WatchDisplayLanguage.English, WatchHostConnectionStatus.Connecting, _) => "Connecting to Host",
        (WatchDisplayLanguage.English, WatchHostConnectionStatus.Failed, _) => "Host connection failed",
        (WatchDisplayLanguage.English, WatchHostConnectionStatus.Connected, true) => "Host connected · recent read failed",
        (WatchDisplayLanguage.English, WatchHostConnectionStatus.Connected, false) => "Host connected",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public string ContractCompatible => Language == WatchDisplayLanguage.SimplifiedChinese ? "契约兼容" : "contract compatible";
    public string AddressNotConfigured => Language == WatchDisplayLanguage.SimplifiedChinese ? "未配置地址" : "address not configured";
    public string CannotConnectHost => Language == WatchDisplayLanguage.SimplifiedChinese ? "无法连接 Host" : "Cannot connect to Host";
    public string ConnectingHost => Language == WatchDisplayLanguage.SimplifiedChinese ? "正在连接 Host" : "Connecting to Host";
    public string ReadingOverview => Language == WatchDisplayLanguage.SimplifiedChinese ? "正在读取概览" : "Reading overview";
    public string RefreshingOverview => Language == WatchDisplayLanguage.SimplifiedChinese ? "正在刷新概览" : "Refreshing overview";
    public string OverviewReadFailed => Language == WatchDisplayLanguage.SimplifiedChinese ? "概览读取失败" : "Overview read failed";
    public string OverviewRefreshFailedRetained => Language == WatchDisplayLanguage.SimplifiedChinese ? "概览刷新失败，已保留上次完整快照" : "Overview refresh failed; last complete snapshot retained";
    public string CurrentScope => Language == WatchDisplayLanguage.SimplifiedChinese ? "当前显示范围" : "Current display scope";
    public string ScopeHelp => Language == WatchDisplayLanguage.SimplifiedChinese ? "AREA 配置会同时影响两个数据页" : "The AREA profile scopes both data pages";
    public string ManageAreaFilters => Language == WatchDisplayLanguage.SimplifiedChinese ? "管理 AREA 筛选" : "Manage AREA filters";
    public string WaitingSeverityFacets => Language == WatchDisplayLanguage.SimplifiedChinese ? "等待严重度分面" : "Waiting for severity facets";
    public string NoSeverityFacets => Language == WatchDisplayLanguage.SimplifiedChinese ? "Host 未返回严重度分面" : "Host returned no severity facets";
    public string RecentEmptyExplanation => Language == WatchDisplayLanguage.SimplifiedChinese
        ? "Host 在该快照窗口内没有报告重点转换；这不是健康结论。"
        : "Host reported no highlighted transition in this snapshot window; this is not a health conclusion.";
    public string OpenActivity(string heading) => Language == WatchDisplayLanguage.SimplifiedChinese ? $"打开重点动态 {heading}" : $"Open highlighted activity {heading}";
    public string AttentionFacet(string value) => value switch
    {
        CurrentIngestAttentionKinds.SeriesError => Language == WatchDisplayLanguage.SimplifiedChinese ? "Series 错误" : "Series errors",
        CurrentIngestAttentionKinds.PollRunFailure => Language == WatchDisplayLanguage.SimplifiedChinese ? "轮询失败" : "Poll failures",
        CurrentIngestAttentionKinds.TaskTypeProtection => Language == WatchDisplayLanguage.SimplifiedChinese ? "任务类型保护" : "Task-type protection",
        CurrentIngestAttentionKinds.UnassignedMesObservation => Language == WatchDisplayLanguage.SimplifiedChinese ? "未分配 MES" : "Unassigned MES",
        CurrentIngestAttentionKinds.HistoryCleanupFailure => Language == WatchDisplayLanguage.SimplifiedChinese ? "历史清理失败" : "History cleanup failure",
        CurrentIngestAttentionKinds.StoragePressure => Language == WatchDisplayLanguage.SimplifiedChinese ? "存储压力" : "Storage pressure",
        CurrentIngestAttentionKinds.HistoryReset => Language == WatchDisplayLanguage.SimplifiedChinese ? "历史重置" : "History reset",
        _ => value,
    };
    public string WaitingAtomicSnapshot => Language == WatchDisplayLanguage.SimplifiedChinese ? "等待 Host 返回原子快照。" : "Waiting for the atomic Host snapshot.";
    public string RetainedDuringRefresh(DateTimeOffset snapshotAt, WatchTextCatalog catalog) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"刷新期间继续显示 Host 快照 {catalog.FormatAbsoluteTime(snapshotAt)}。"
        : $"Refresh in progress; continues to show Host snapshot {catalog.FormatAbsoluteTime(snapshotAt)}.";
    public string PriorFailureRetry(DateTimeOffset failedAt, WatchTextCatalog catalog) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $" 上次失败于 {catalog.FormatAbsoluteTime(failedAt)}；本次正在重试。"
        : $" Previous attempt failed {catalog.FormatAbsoluteTime(failedAt)}; retry in progress.";
    public string NoSuccessfulSnapshot => Language == WatchDisplayLanguage.SimplifiedChinese ? "当前没有可显示的成功快照。" : "There is no successful snapshot to display.";
    public string RetainedAfterFailure(DateTimeOffset snapshotAt, WatchTextCatalog catalog) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"继续显示 Host 快照 {catalog.FormatAbsoluteTime(snapshotAt)}；其范围不会被失败查询改写。"
        : $"Continues to show Host snapshot {catalog.FormatAbsoluteTime(snapshotAt)}; the failed query did not rewrite its scope.";
    public string FailedAt(DateTimeOffset failedAt, string retained, string failure, WatchTextCatalog catalog) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"失败于 {catalog.FormatAbsoluteTime(failedAt)}。{retained}{failure}"
        : $"Failed {catalog.FormatAbsoluteTime(failedAt)}. {retained}{failure}";
    public string ReplacementHostFailure(string failure) => Language == WatchDisplayLanguage.SimplifiedChinese
        ? $"新 Host 未通过契约连接；旧 Host 数据已清空。{failure}"
        : $"The replacement Host failed contract connection; old Host data was cleared.{failure}";
    public string FailureMessage(string? message, string? correlationId)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}";
        return string.IsNullOrWhiteSpace(correlationId)
            ? detail
            : Language == WatchDisplayLanguage.SimplifiedChinese
                ? $"{detail} 关联 ID {correlationId}。"
                : $"{detail} Correlation ID {correlationId}.";
    }

    public string LocalState(string raw) => (Language, raw) switch
    {
        (WatchDisplayLanguage.English, "本机默认") => "Local default",
        (WatchDisplayLanguage.English, "本机配置") => "Local configuration",
        (WatchDisplayLanguage.English, "本机已应用") => "Applied locally",
        _ => raw,
    };
}
