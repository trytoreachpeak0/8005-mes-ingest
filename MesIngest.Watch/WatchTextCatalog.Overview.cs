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

    public override IReadOnlyList<WatchTextCatalogEntry> Entries =>
        [.. CatalogEntries, .. WatchLegacyGeneratedText.OverviewEntries];

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

    public string AttentionDetail(long errors, long warnings) => Format(WatchLegacyGeneratedText.Overview040, new object?[] { errors, warnings }, new object?[] { errors, warnings });

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
        _ => kind,
    };

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

    public string LocalState(string raw) => (Language, raw) switch
    {
        (WatchDisplayLanguage.English, "本机默认") => "Local default",
        (WatchDisplayLanguage.English, "本机配置") => "Local configuration",
        (WatchDisplayLanguage.English, "本机已应用") => "Applied locally",
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
