using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal sealed record WatchCurrentIngestAttentionFacetPresentation(
    string Value,
    string DisplayValue,
    long ItemCount);

internal sealed record WatchSeriesErrorIdentityPresentation(
    string StableIdentity,
    string SeriesId,
    string ErrorCode,
    string Category,
    string Target,
    string SubjectKind);

internal sealed record WatchCurrentIngestAttentionEvidencePresentation(
    string? ProjectionCommitId,
    long? ProjectionSequence,
    string? PollTraceId,
    long? PollTraceSequence,
    string? SeriesId,
    string? DemandId,
    string? WorkType,
    int? ObservationOrdinal,
    string? EvidenceId,
    string? ContentDigest,
    string? Phase,
    string? Outcome,
    string? DatabaseName,
    string? VolumeRoot,
    decimal? AvailablePercent)
{
    public string Facts => string.Join(
        " · ",
        new string?[]
        {
            ProjectionCommitId is null
                ? null
                : $"ProjectionCommit {ProjectionCommitId}",
            ProjectionSequence is null
                ? null
                : $"序列 {ProjectionSequence:N0}",
            PollTraceId is null
                ? null
                : $"PollTrace {PollTraceId}",
            PollTraceSequence is null
                ? null
                : $"PollTrace 序列 {PollTraceSequence:N0}",
            SeriesId is null ? null : $"Series {SeriesId}",
            DemandId is null ? null : $"Demand {DemandId}",
            WorkType is null ? null : $"WorkType {WorkType}",
            ObservationOrdinal is null
                ? null
                : $"观测序号 {ObservationOrdinal:N0}",
            EvidenceId is null ? null : $"证据 {EvidenceId}",
            ContentDigest is null ? null : $"Digest {ContentDigest}",
            Phase is null ? null : $"阶段 {Phase}",
            Outcome is null ? null : $"结果 {Outcome}",
            DatabaseName is null ? null : $"数据库 {DatabaseName}",
            VolumeRoot is null ? null : $"卷 {VolumeRoot}",
            AvailablePercent is null ? null : $"可用 {AvailablePercent:0.###}%",
        }.Where(value => value is not null));
}

internal sealed record WatchProtectionDetailPresentation(
    string? Status,
    string? Reason,
    string? LastSuccessfulWindow,
    string? EarliestAvailable,
    string? RebuildProgress,
    string? CurrentReadRestriction,
    string? LocalAdministrationGuidance);

internal sealed record WatchCurrentIngestAttentionRowPresentation(
    string Kind,
    string KindLabel,
    string Severity,
    string SeverityLabel,
    WatchPresentationSeverity SeverityStyle,
    string OccurredAt,
    string StableIdentity,
    string SubjectSummary,
    string? SeriesId,
    string? WorkType,
    string? ErrorCode,
    string? ErrorCategory,
    string? Target,
    string? SubjectKind,
    WatchSeriesErrorIdentityPresentation? SeriesErrorIdentity,
    WatchCurrentIngestAttentionEvidencePresentation Evidence,
    OverviewNavigationIntent Navigation,
    ErrorSearchQuery? ErrorSearchDrill,
    WatchProtectionDetailPresentation? Protection);

internal sealed record WatchCurrentIngestAttentionPresentation(
    bool HasSnapshot,
    bool IsRefreshing,
    bool IsStale,
    bool IsInfoOpen,
    WatchPresentationSeverity InfoSeverity,
    string InfoTitle,
    string InfoMessage,
    string SnapshotFacts,
    string? ProjectionCommitId,
    DateTimeOffset? SnapshotAsOf,
    string ClientAttemptFacts,
    string PageSummary,
    string EmptyResultMessage,
    string OrderSummary,
    string HostFilterSummary,
    string CurrentQuerySummary,
    string AreaIsolationNotice,
    string SemanticsNotice,
    bool CanGoPrevious,
    bool CanGoNext,
    IReadOnlyList<WatchCurrentIngestAttentionFacetPresentation> TypeFacets,
    IReadOnlyList<WatchCurrentIngestAttentionFacetPresentation> SeverityFacets,
    IReadOnlyList<WatchCurrentIngestAttentionRowPresentation> Rows)
{
    private const string GlobalAreaNotice =
        "全 Host 当前关注；本机 AREA 配置不会筛选、计数或翻页此页面。";

    private const string ReadOnlySemanticsNotice =
        "只读当前关注项；不创建 fingerprint incident，不提供人工确认、人工恢复或关闭操作。";

    public static WatchCurrentIngestAttentionPresentation Project(
        WatchV2WorkspaceState workspace,
        CurrentIngestAttentionQuery query,
        WatchTextCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(query);
        var normalizedQuery = query.NormalizeAndValidate();
        catalog ??= WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese);
        var text = catalog.CurrentAttention;
        var view = workspace.CurrentAttention;
        var snapshot = view.Snapshot;
        var info = ProjectInfo(workspace, view, catalog);

        if (snapshot is null)
        {
            return new WatchCurrentIngestAttentionPresentation(
                HasSnapshot: false,
                view.IsRefreshing,
                view.IsStale,
                info.IsOpen,
                info.Severity,
                info.Title,
                info.Message,
                text.Pick("尚无 Host 当前接入关注快照", "No current-ingest-attention Host snapshot"),
                ProjectionCommitId: null,
                SnapshotAsOf: null,
                ProjectClientAttempts(view, catalog),
                text.Pick("尚无当前接入关注快照", "No current-ingest-attention snapshot"),
                EmptyResultMessage: string.Empty,
                text.Pick("Host 固定排序：", "Fixed Host order: ") + CurrentIngestAttentionOrder.Default,
                text.Pick("Host 已提交条件：尚无快照", "Host committed filters: no snapshot"),
                text.Pick("当前待查询条件：", "Pending query: ") + ProjectFilters(normalizedQuery.Kinds, normalizedQuery.Severities, catalog),
                text.Pick(GlobalAreaNotice, "All current Host attention; local AREA configuration does not filter, count, or page this view."),
                text.Pick(ReadOnlySemanticsNotice, "Read-only current attention. This view does not create fingerprint incidents or offer acknowledgement, recovery, or close operations."),
                CanGoPrevious: false,
                CanGoNext: false,
                TypeFacets: [],
                SeverityFacets: [],
                Rows: []);
        }

        var displayPageNumber = snapshot.TotalPages == 0 ? 0 : snapshot.PageNumber;
        var showSuccessfulEmpty = snapshot.ExactTotalItemCount == 0
            && !view.IsRefreshing
            && !view.IsStale
            && view.LastFailureAt is null;
        return new WatchCurrentIngestAttentionPresentation(
            HasSnapshot: true,
            view.IsRefreshing,
            view.IsStale,
            info.IsOpen,
            info.Severity,
            info.Title,
            info.Message,
            ProjectSnapshotFacts(snapshot.Snapshot, catalog),
            snapshot.Snapshot.ProjectionCommitId,
            snapshot.Snapshot.SnapshotAsOf,
            ProjectClientAttempts(view, catalog),
            text.Pick($"精确 {snapshot.ExactTotalItemCount:N0} 个当前关注项 · 第 {displayPageNumber:N0} / {snapshot.TotalPages:N0} 页", $"Exact {snapshot.ExactTotalItemCount:N0} current attention items · Page {displayPageNumber:N0} of {snapshot.TotalPages:N0}"),
            showSuccessfulEmpty
                ? text.Pick("查询成功；Host 在当前已提交条件下精确 0 个当前接入关注项。已结束的需求系列错误仍可在错误检索中查找。", "Query succeeded; exactly 0 current attention items match the committed Host filters. Ended series errors remain available in Error Search.")
                : string.Empty,
            text.Pick("Host 固定排序：", "Fixed Host order: ") + snapshot.Order,
            text.Pick("Host 已提交条件：", "Host committed filters: ") + ProjectFilters(snapshot.Kinds, snapshot.Severities, catalog),
            text.Pick("当前待查询条件：", "Pending query: ") + ProjectFilters(normalizedQuery.Kinds, normalizedQuery.Severities, catalog),
            text.Pick(GlobalAreaNotice, "All current Host attention; local AREA configuration does not filter, count, or page this view."),
            text.Pick(ReadOnlySemanticsNotice, "Read-only current attention. This view does not create fingerprint incidents or offer acknowledgement, recovery, or close operations."),
            snapshot.TotalPages > 0 && snapshot.PageNumber > 1,
            snapshot.TotalPages > 0 && snapshot.PageNumber < snapshot.TotalPages,
            snapshot.Facets.Types
                .Select(facet => new WatchCurrentIngestAttentionFacetPresentation(
                    facet.Value,
                    text.CodeWithMeaning(text.DescribeKind(facet.Value)),
                    facet.ItemCount))
                .ToArray(),
            snapshot.Facets.Severities
                .Select(facet => new WatchCurrentIngestAttentionFacetPresentation(
                    facet.Value,
                    text.CodeWithMeaning(text.DescribeSeverity(facet.Value)),
                    facet.ItemCount))
                .ToArray(),
            snapshot.Items.Select(item => ProjectRow(item, snapshot, catalog)).ToArray());
    }

    private static WatchCurrentIngestAttentionRowPresentation ProjectRow(
        CurrentIngestAttentionItemSnapshot item,
        CurrentIngestAttentionSnapshot snapshot,
        WatchTextCatalog catalog)
    {
        var text = catalog.CurrentAttention;
        WatchSeriesErrorIdentityPresentation? seriesErrorIdentity = null;
        ErrorSearchQuery? errorSearchDrill = null;
        string? errorCategory = null;
        if (string.Equals(
                item.Kind,
                CurrentIngestAttentionKinds.SeriesError,
                StringComparison.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SeriesId);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.ErrorCode);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Target);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SubjectKind);
            var definition = SeriesErrorCatalog.Definitions.SingleOrDefault(definition =>
                string.Equals(definition.Code, item.ErrorCode, StringComparison.Ordinal));
            if (definition is not null)
            {
                errorCategory = definition.Category;
                seriesErrorIdentity = new WatchSeriesErrorIdentityPresentation(
                    item.StableIdentity,
                    item.SeriesId,
                    definition.Code,
                    definition.Category,
                    item.Target,
                    item.SubjectKind);
                errorSearchDrill = WatchCurrentIngestAttentionQueries.CreateErrorSearchDrill(item);
            }
        }

        var evidence = item.Evidence;
        var isStoragePressure = string.Equals(
            item.Kind,
            CurrentIngestAttentionKinds.StoragePressure,
            StringComparison.Ordinal);
        var isHistoryReset = string.Equals(
            item.Kind,
            CurrentIngestAttentionKinds.HistoryReset,
            StringComparison.Ordinal);
        var historyEpoch = snapshot.Snapshot.HistoryEpoch
            ?? (isStoragePressure ? snapshot.StoragePressure?.HistoryEpoch : null);
        var databaseName = evidence.DatabaseName ?? item.Target;
        var protectionStatus = isStoragePressure
            ? snapshot.StoragePressure?.Status ?? evidence.Phase ?? item.ErrorCode
            : isHistoryReset
                ? item.ErrorCode ?? evidence.Phase
                : null;
        var protectionReason = isStoragePressure
            ? snapshot.StoragePressure?.PauseReason ?? evidence.FailureReason
            : isHistoryReset
                ? evidence.FailureReason
                : null;
        var isStoragePaused = isStoragePressure
            && string.Equals(
                protectionStatus,
                StoragePressureStatuses.Paused,
                StringComparison.Ordinal);
        var lastSuccessfulWindow = isStoragePressure
            ? text.Pick("最后成功 PollTrace ", "Last successful PollTrace ") + snapshot.Snapshot.PollTraceId + text.Pick(" · 投影提交 ", " · projection committed ") + catalog.FormatAbsoluteTime(snapshot.Snapshot.ProjectionCommittedAt) + " · " + snapshot.Snapshot.ProjectionCommitId
            : null;
        var earliestAvailable = isStoragePressure
            ? snapshot.HistoryCleanup?.EarliestAvailableHostUtc is { } earliest
                ? text.Pick("最早可用历史 ", "Earliest available history ") + catalog.FormatAbsoluteTime(earliest)
                : text.Pick("最早可用历史尚未建立", "Earliest available history is not established")
            : null;
        var rebuildProgress = isHistoryReset
            ? text.Pick("新 HistoryEpoch ", "New HistoryEpoch ") + ProjectEpoch(historyEpoch) + text.Pick(" · 已建立到 ProjectionCommit ", " · established through ProjectionCommit ") + snapshot.Snapshot.ProjectionCommitId + text.Pick(" / 序列 ", " / sequence ") + $"{snapshot.Snapshot.ProjectionSequence:N0} · {catalog.FormatAbsoluteTime(snapshot.Snapshot.ProjectionCommittedAt)}"
            : null;
        var currentReadRestriction = isStoragePaused
            ? text.Pick("StoragePressurePause 期间外部当前目录与执行承诺读取返回 503 INGEST_NOT_CURRENT；Watch 仍显示最后成功投影、诊断和可用历史。", "During StoragePressurePause, external current-catalog and execution-commitment reads return 503 INGEST_NOT_CURRENT. Watch continues to show the last successful projection, diagnostics, and available history.")
            : isStoragePressure
                ? text.Pick("CRITICAL_WARNING 尚未进入 StoragePressurePause；外部当前读取不会仅因该预警返回 503 INGEST_NOT_CURRENT。", "CRITICAL_WARNING has not entered StoragePressurePause; external current reads do not return 503 INGEST_NOT_CURRENT solely because of this warning.")
            : isHistoryReset
                ? text.Pick("HistoryResetAcknowledgement 提交前，外部当前目录与执行承诺读取返回 503 INGEST_NOT_CURRENT。", "Before HistoryResetAcknowledgement is submitted, external current-catalog and execution-commitment reads return 503 INGEST_NOT_CURRENT.")
                : null;
        var localRecoveryGuidance = isStoragePaused
            ? StorageRecoveryGuidance(databaseName, historyEpoch, catalog)
            : isStoragePressure
                ? text.Pick("当前仅为存储空间严重告警，不执行 resume-storage-pressure；先在数据库主机释放空间并持续观察，低于 10% 才会在下一轮 MES 查询前进入暂停。", "This is only a critical storage-space warning; do not run resume-storage-pressure. Free space on the database host and keep observing. Pause begins before the next MES query only after available space drops below 10%.")
            : isHistoryReset
                ? HistoryResetGuidance(databaseName, historyEpoch, catalog)
                : null;
        var protection = isStoragePressure || isHistoryReset
            ? new WatchProtectionDetailPresentation(
                protectionStatus,
                protectionReason,
                lastSuccessfulWindow,
                earliestAvailable,
                rebuildProgress,
                currentReadRestriction,
                localRecoveryGuidance)
            : null;
        return new WatchCurrentIngestAttentionRowPresentation(
            item.Kind,
            text.CodeWithMeaning(text.DescribeKind(item.Kind)),
            item.Severity,
            text.CodeWithMeaning(text.DescribeSeverity(item.Severity)),
            ProjectSeverity(item.Severity),
            catalog.FormatAbsoluteTime(item.OccurredAt),
            item.StableIdentity,
            ProjectSubject(item, catalog),
            item.SeriesId,
            item.WorkType,
            item.ErrorCode,
            errorCategory,
            item.Target,
            item.SubjectKind,
            seriesErrorIdentity,
            new WatchCurrentIngestAttentionEvidencePresentation(
                evidence.ProjectionCommitId,
                evidence.ProjectionSequence,
                evidence.PollTraceId,
                evidence.PollTraceSequence,
                evidence.SeriesId,
                evidence.DemandId,
                evidence.WorkType,
                evidence.ObservationOrdinal,
                evidence.EvidenceId,
                evidence.ContentDigest,
                evidence.Phase,
                evidence.Outcome,
                evidence.DatabaseName,
                evidence.VolumeRoot,
                evidence.AvailablePercent),
            item.Navigation,
            errorSearchDrill,
            protection);
    }

    private static string StorageRecoveryGuidance(
        string? databaseName,
        HistoryEpoch? historyEpoch,
        WatchTextCatalog catalog) =>
        catalog.CurrentAttention.Pick("仅限授权管理员在数据库主机本地控制台运行：", "Authorized administrators only; run from the database host's local console: ")
        + $"MesIngest.LocalAdministration resume-storage-pressure --database \"{ProjectText(databaseName, catalog)}\" --history-epoch {ProjectEpoch(historyEpoch)} --reason \"<reason>\""
        + catalog.CurrentAttention.Pick("。空间恢复到至少 15%、数据库 ONLINE/READ_WRITE 且人工提交前不会自动恢复。", ". Recovery is not automatic before space reaches at least 15%, the database is ONLINE/READ_WRITE, and an administrator submits the command.");

    private static string HistoryResetGuidance(
        string? databaseName,
        HistoryEpoch? historyEpoch,
        WatchTextCatalog catalog) =>
        catalog.CurrentAttention.Pick("仅限授权管理员在数据库主机本地控制台运行：", "Authorized administrators only; run from the database host's local console: ")
        + $"MesIngest.LocalAdministration acknowledge-history-reset --database \"{ProjectText(databaseName, catalog)}\" --history-epoch {ProjectEpoch(historyEpoch)} --reason \"<reason>\" --risk-acceptance {HistoryResetAcknowledgementPolicy.RequiredRiskAcceptance}"
        + catalog.CurrentAttention.Pick("。该确认接受旧历史与墓碑不可恢复风险，不恢复旧身份。", ". This acknowledges that previous history and tombstones are unrecoverable; it does not restore old identities.");

    private static string ProjectEpoch(HistoryEpoch? historyEpoch) =>
        historyEpoch?.ToString() ?? "<HistoryEpoch unavailable>";

    private static (bool IsOpen, WatchPresentationSeverity Severity, string Title, string Message)
        ProjectInfo(
            WatchV2WorkspaceState workspace,
            WatchV2ViewState<CurrentIngestAttentionSnapshot, WatchNoDetail> view,
            WatchTextCatalog catalog)
    {
        var text = catalog.CurrentAttention;
        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Failed)
        {
            return (
                true,
                WatchPresentationSeverity.Error,
                text.Pick("无法连接 Host", "Unable to connect to Host"),
                text.Pick("新 Host 未通过契约连接；旧 Host 数据已清空。", "The new Host failed contract connection; old Host data was cleared. ") + FailureMessage(workspace.ErrorMessage, workspace.CorrelationId, catalog));
        }

        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Connecting)
        {
            return (
                true,
                WatchPresentationSeverity.Informational,
                text.Pick("正在连接 Host", "Connecting to Host"),
                text.Pick("连接成功后将读取全 Host 的当前接入关注快照。", "The current-ingest-attention snapshot for the entire Host will be read after connection succeeds."));
        }

        if (view.IsRefreshing)
        {
            var retained = view.Snapshot is null
                ? text.Pick("等待 Host 返回当前接入关注快照。", "Waiting for the Host to return a current-ingest-attention snapshot.")
                : text.Pick("刷新期间继续显示 Host 快照 ", "The Host snapshot remains visible during refresh: ") + catalog.FormatAbsoluteTime(view.Snapshot.Snapshot.SnapshotAsOf) + text.Pick("。", ".");
            var priorFailure = view.LastFailureAt is { } priorFailedAt
                ? text.Pick(" 上次失败于 ", " Last failed at ") + catalog.FormatAbsoluteTime(priorFailedAt) + text.Pick("；本次正在重试。", "; retrying now.")
                : string.Empty;
            return (
                true,
                WatchPresentationSeverity.Informational,
                view.Snapshot is null
                    ? text.Pick("正在读取当前接入关注", "Loading current ingest attention")
                    : text.Pick("正在刷新当前接入关注", "Refreshing current ingest attention"),
                $"{retained}{priorFailure}");
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? text.Pick("当前没有可显示的成功快照。", "There is no successful snapshot to display.")
                : text.Pick("继续显示 Host 快照 ", "Continuing to show Host snapshot ") + catalog.FormatAbsoluteTime(view.Snapshot.Snapshot.SnapshotAsOf) + text.Pick("；其筛选、精确分面、排序和页状态不会被失败查询改写。", "; its filters, exact facets, order, and page state were not replaced by the failed query.");
            return (
                true,
                view.Snapshot is null
                    ? WatchPresentationSeverity.Error
                    : WatchPresentationSeverity.Warning,
                view.Snapshot is null
                    ? text.Pick("当前接入关注读取失败", "Current-ingest-attention read failed")
                    : text.Pick("当前接入关注刷新失败，已保留上次快照", "Current-ingest-attention refresh failed; previous snapshot retained"),
                text.Pick("失败于 ", "Failed at ") + catalog.FormatAbsoluteTime(failedAt) + text.Pick("。", ". ") + retained + FailureMessage(view.ErrorMessage, view.CorrelationId, catalog));
        }

        if (view.IsStale && view.Snapshot is not null)
        {
            return (
                true,
                WatchPresentationSeverity.Warning,
                text.Pick("刷新未提交，已保留上次快照", "Refresh was not committed; previous snapshot retained"),
                text.Pick("本次读取已取消或未提交；继续显示 Host 快照 ", "This read was canceled or not committed; continuing to show Host snapshot ") + catalog.FormatAbsoluteTime(view.Snapshot.Snapshot.SnapshotAsOf) + text.Pick("，其筛选、精确分面、排序和页状态保持不变。", "; its filters, exact facets, order, and page state remain unchanged."));
        }

        return (false, WatchPresentationSeverity.None, string.Empty, string.Empty);
    }

    private static string ProjectSnapshotFacts(OperationalSnapshotIdentity snapshot, WatchTextCatalog catalog) =>
        catalog.CurrentAttention.Pick("Host 快照 ", "Host snapshot ") + catalog.FormatAbsoluteTime(snapshot.SnapshotAsOf)
        + catalog.CurrentAttention.Pick(" · 投影提交 ", " · projection committed ") + catalog.FormatAbsoluteTime(snapshot.ProjectionCommittedAt)
        + $" · {snapshot.ProjectionCommitId} · " + catalog.CurrentAttention.Pick("序列 ", "sequence ") + $"{snapshot.ProjectionSequence:N0} · PollTrace {snapshot.PollTraceId} · PollTrace HighWater {snapshot.PollTraceHighWater:N0} · CatalogRevision {snapshot.CatalogRevision:N0}";

    private static string ProjectClientAttempts(
        WatchV2ViewState<CurrentIngestAttentionSnapshot, WatchNoDetail> view,
        WatchTextCatalog catalog)
    {
        var successful = view.LastSuccessfulAt is { } lastSuccessfulAt
            ? catalog.CurrentAttention.Pick("Watch 最近成功 ", "Watch last succeeded ") + catalog.FormatAbsoluteTime(lastSuccessfulAt)
            : catalog.CurrentAttention.Pick("Watch 尚无成功读取", "Watch has no successful read");
        return view.LastFailureAt is { } lastFailureAt
            ? successful + catalog.CurrentAttention.Pick(" · 最近失败 ", " · last failed ") + catalog.FormatAbsoluteTime(lastFailureAt)
            : successful;
    }

    private static string ProjectFilters(
        IReadOnlyList<string>? kinds,
        IReadOnlyList<string>? severities,
        WatchTextCatalog catalog)
    {
        var kindSummary = kinds is { Count: > 0 }
            ? catalog.CurrentAttention.Pick("种类 ", "Types ") + string.Join(catalog.Language == WatchDisplayLanguage.SimplifiedChinese ? "、" : ", ", kinds)
            : catalog.CurrentAttention.Pick("全部种类", "All types");
        var severitySummary = severities is { Count: > 0 }
            ? catalog.CurrentAttention.Pick("严重度 ", "Severities ") + string.Join(catalog.Language == WatchDisplayLanguage.SimplifiedChinese ? "、" : ", ", severities)
            : catalog.CurrentAttention.Pick("全部严重度", "All severities");
        return $"{kindSummary} · {severitySummary}";
    }

    private static WatchPresentationSeverity ProjectSeverity(string severity) => severity switch
    {
        CurrentIngestAttentionSeverities.Error => WatchPresentationSeverity.Error,
        CurrentIngestAttentionSeverities.Warning => WatchPresentationSeverity.Warning,
        _ => WatchPresentationSeverity.Informational,
    };

    private static string ProjectSubject(CurrentIngestAttentionItemSnapshot item, WatchTextCatalog catalog) => item.Kind switch
    {
        CurrentIngestAttentionKinds.SeriesError =>
            $"SeriesId {ProjectText(item.SeriesId, catalog)} · "
            + catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeErrorCode(ProjectText(item.ErrorCode, catalog)))
            + $" · Target {ProjectText(item.Target, catalog)} · "
            + catalog.CurrentAttention.Pick("主体种类 ", "Subject kind ") + ProjectText(item.SubjectKind, catalog),
        CurrentIngestAttentionKinds.PollRunFailure =>
            $"PollTrace {ProjectText(item.Evidence.PollTraceId, catalog)} · " + ProjectStatus(item.Evidence.Outcome, catalog),
        CurrentIngestAttentionKinds.TaskTypeProtection =>
            $"WorkType {ProjectText(item.WorkType ?? item.Evidence.WorkType, catalog)} · " + ProjectStatus(item.Evidence.Phase, catalog),
        CurrentIngestAttentionKinds.UnassignedMesObservation =>
            $"PollTrace {ProjectText(item.Evidence.PollTraceId, catalog)} · " + catalog.CurrentAttention.Pick("观测序号 ", "Observation ordinal ") + (item.Evidence.ObservationOrdinal?.ToString() ?? catalog.Common.SourceNotProvided),
        CurrentIngestAttentionKinds.StoragePressure =>
            catalog.CurrentAttention.Pick("数据库 ", "Database ") + ProjectText(item.Evidence.DatabaseName, catalog)
            + catalog.CurrentAttention.Pick(" · 卷 ", " · volume ") + ProjectText(item.Evidence.VolumeRoot, catalog)
            + catalog.CurrentAttention.Pick(" · 可用 ", " · available ") + (item.Evidence.AvailablePercent?.ToString("0.###") ?? catalog.Common.SourceNotProvided) + "%",
        _ => item.StableIdentity,
    };

    private static string ProjectStatus(string? value, WatchTextCatalog catalog) =>
        string.IsNullOrWhiteSpace(value)
            ? catalog.Common.SourceNotProvided
            : catalog.CurrentAttention.CodeWithMeaning(catalog.CurrentAttention.DescribeProtectionStatus(value));

    private static string ProjectText(string? value, WatchTextCatalog catalog) =>
        string.IsNullOrWhiteSpace(value) ? catalog.Common.SourceNotProvided : value;

    private static string FailureMessage(string? message, string? correlationId, WatchTextCatalog catalog)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}";
        return string.IsNullOrWhiteSpace(correlationId)
            ? detail
            : detail + catalog.CurrentAttention.Pick(" 关联 ID ", " Correlation ID ") + correlationId + catalog.CurrentAttention.Pick("。", ".");
    }
}
