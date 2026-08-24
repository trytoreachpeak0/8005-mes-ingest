using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal sealed record WatchCurrentIngestAttentionFacetPresentation(
    string Value,
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
        CurrentIngestAttentionQuery query)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(query);
        var normalizedQuery = query.NormalizeAndValidate();
        var view = workspace.CurrentAttention;
        var snapshot = view.Snapshot;
        var info = ProjectInfo(workspace, view);

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
                "尚无 Host 当前接入关注快照",
                ProjectionCommitId: null,
                SnapshotAsOf: null,
                ProjectClientAttempts(view),
                "尚无当前接入关注快照",
                EmptyResultMessage: string.Empty,
                $"Host 固定排序：{CurrentIngestAttentionOrder.Default}",
                "Host 已提交条件：尚无快照",
                $"当前待查询条件：{ProjectFilters(normalizedQuery.Kinds, normalizedQuery.Severities)}",
                GlobalAreaNotice,
                ReadOnlySemanticsNotice,
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
            ProjectSnapshotFacts(snapshot.Snapshot),
            snapshot.Snapshot.ProjectionCommitId,
            snapshot.Snapshot.SnapshotAsOf,
            ProjectClientAttempts(view),
            $"精确 {snapshot.ExactTotalItemCount:N0} 个当前关注项 · 第 {displayPageNumber:N0} / {snapshot.TotalPages:N0} 页",
            showSuccessfulEmpty
                ? "查询成功；Host 在当前已提交条件下精确 0 个当前接入关注项。已结束的 Series 错误仍可在错误检索中查找。"
                : string.Empty,
            $"Host 固定排序：{snapshot.Order}",
            $"Host 已提交条件：{ProjectFilters(snapshot.Kinds, snapshot.Severities)}",
            $"当前待查询条件：{ProjectFilters(normalizedQuery.Kinds, normalizedQuery.Severities)}",
            GlobalAreaNotice,
            ReadOnlySemanticsNotice,
            snapshot.TotalPages > 0 && snapshot.PageNumber > 1,
            snapshot.TotalPages > 0 && snapshot.PageNumber < snapshot.TotalPages,
            snapshot.Facets.Types
                .Select(facet => new WatchCurrentIngestAttentionFacetPresentation(
                    facet.Value,
                    facet.ItemCount))
                .ToArray(),
            snapshot.Facets.Severities
                .Select(facet => new WatchCurrentIngestAttentionFacetPresentation(
                    facet.Value,
                    facet.ItemCount))
                .ToArray(),
            snapshot.Items.Select(item => ProjectRow(item, snapshot)).ToArray());
    }

    private static WatchCurrentIngestAttentionRowPresentation ProjectRow(
        CurrentIngestAttentionItemSnapshot item,
        CurrentIngestAttentionSnapshot snapshot)
    {
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
            var definition = SeriesErrorCatalog.GetRequired(item.ErrorCode);
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
            ? $"最后成功 PollTrace {snapshot.Snapshot.PollTraceId} · 投影提交 {WatchTimeDisplay.Format(snapshot.Snapshot.ProjectionCommittedAt)} · {snapshot.Snapshot.ProjectionCommitId}"
            : null;
        var earliestAvailable = isStoragePressure
            ? snapshot.HistoryCleanup?.EarliestAvailableHostUtc is { } earliest
                ? $"earliest available {WatchTimeDisplay.Format(earliest)}"
                : "earliest available 尚未建立"
            : null;
        var rebuildProgress = isHistoryReset
            ? $"新 HistoryEpoch {ProjectEpoch(historyEpoch)} · 已建立到 ProjectionCommit {snapshot.Snapshot.ProjectionCommitId} / 序列 {snapshot.Snapshot.ProjectionSequence:N0} · {WatchTimeDisplay.Format(snapshot.Snapshot.ProjectionCommittedAt)}"
            : null;
        var currentReadRestriction = isStoragePaused
            ? "StoragePressurePause 期间外部当前目录与执行承诺读取返回 503 INGEST_NOT_CURRENT；Watch 仍显示最后成功投影、诊断和可用历史。"
            : isStoragePressure
                ? "CRITICAL_WARNING 尚未进入 StoragePressurePause；外部当前读取不会仅因该预警返回 503 INGEST_NOT_CURRENT。"
            : isHistoryReset
                ? "HistoryResetAcknowledgement 提交前，外部当前目录与执行承诺读取返回 503 INGEST_NOT_CURRENT。"
                : null;
        var localRecoveryGuidance = isStoragePaused
            ? StorageRecoveryGuidance(databaseName, historyEpoch)
            : isStoragePressure
                ? "当前仅为存储空间严重告警，不执行 resume-storage-pressure；先在数据库主机释放空间并持续观察，低于 10% 才会在下一轮 MES 查询前进入暂停。"
            : isHistoryReset
                ? HistoryResetGuidance(databaseName, historyEpoch)
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
            ProjectKind(item.Kind),
            item.Severity,
            ProjectSeverity(item.Severity),
            WatchTimeDisplay.Format(item.OccurredAt),
            item.StableIdentity,
            ProjectSubject(item),
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
        HistoryEpoch? historyEpoch) =>
        $"仅限授权管理员在数据库主机本地控制台运行：MesIngest.LocalAdministration resume-storage-pressure --database \"{ProjectText(databaseName)}\" --history-epoch {ProjectEpoch(historyEpoch)} --reason \"<填写恢复原因>\"。空间恢复到至少 15%、数据库 ONLINE/READ_WRITE 且人工提交前不会自动恢复。";

    private static string HistoryResetGuidance(
        string? databaseName,
        HistoryEpoch? historyEpoch) =>
        $"仅限授权管理员在数据库主机本地控制台运行：MesIngest.LocalAdministration acknowledge-history-reset --database \"{ProjectText(databaseName)}\" --history-epoch {ProjectEpoch(historyEpoch)} --reason \"<填写确认原因>\" --risk-acceptance {HistoryResetAcknowledgementPolicy.RequiredRiskAcceptance}。该确认接受旧历史与墓碑不可恢复风险，不恢复旧身份。";

    private static string ProjectEpoch(HistoryEpoch? historyEpoch) =>
        historyEpoch?.ToString() ?? "<HistoryEpoch unavailable>";

    private static (bool IsOpen, WatchPresentationSeverity Severity, string Title, string Message)
        ProjectInfo(
            WatchV2WorkspaceState workspace,
            WatchV2ViewState<CurrentIngestAttentionSnapshot, WatchNoDetail> view)
    {
        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Failed)
        {
            return (
                true,
                WatchPresentationSeverity.Error,
                "无法连接 Host",
                $"新 Host 未通过契约连接；旧 Host 数据已清空。{FailureMessage(workspace.ErrorMessage, workspace.CorrelationId)}");
        }

        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Connecting)
        {
            return (
                true,
                WatchPresentationSeverity.Informational,
                "正在连接 Host",
                "连接成功后将读取全 Host 的当前接入关注快照。");
        }

        if (view.IsRefreshing)
        {
            var retained = view.Snapshot is null
                ? "等待 Host 返回当前接入关注快照。"
                : $"刷新期间继续显示 Host 快照 {WatchTimeDisplay.Format(view.Snapshot.Snapshot.SnapshotAsOf)}。";
            var priorFailure = view.LastFailureAt is { } priorFailedAt
                ? $" 上次失败于 {WatchTimeDisplay.Format(priorFailedAt)}；本次正在重试。"
                : string.Empty;
            return (
                true,
                WatchPresentationSeverity.Informational,
                view.Snapshot is null
                    ? "正在读取当前接入关注"
                    : "正在刷新当前接入关注",
                $"{retained}{priorFailure}");
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? "当前没有可显示的成功快照。"
                : $"继续显示 Host 快照 {WatchTimeDisplay.Format(view.Snapshot.Snapshot.SnapshotAsOf)}；其筛选、精确分面、排序和页状态不会被失败查询改写。";
            return (
                true,
                view.Snapshot is null
                    ? WatchPresentationSeverity.Error
                    : WatchPresentationSeverity.Warning,
                view.Snapshot is null
                    ? "当前接入关注读取失败"
                    : "当前接入关注刷新失败，已保留上次快照",
                $"失败于 {WatchTimeDisplay.Format(failedAt)}。{retained}{FailureMessage(view.ErrorMessage, view.CorrelationId)}");
        }

        if (view.IsStale && view.Snapshot is not null)
        {
            return (
                true,
                WatchPresentationSeverity.Warning,
                "刷新未提交，已保留上次快照",
                $"本次读取已取消或未提交；继续显示 Host 快照 {WatchTimeDisplay.Format(view.Snapshot.Snapshot.SnapshotAsOf)}，其筛选、精确分面、排序和页状态保持不变。");
        }

        return (false, WatchPresentationSeverity.None, string.Empty, string.Empty);
    }

    private static string ProjectSnapshotFacts(OperationalSnapshotIdentity snapshot) =>
        $"Host 快照 {WatchTimeDisplay.Format(snapshot.SnapshotAsOf)} · 投影提交 {WatchTimeDisplay.Format(snapshot.ProjectionCommittedAt)} · {snapshot.ProjectionCommitId} · 序列 {snapshot.ProjectionSequence:N0} · PollTrace {snapshot.PollTraceId} · PollTrace HighWater {snapshot.PollTraceHighWater:N0} · CatalogRevision {snapshot.CatalogRevision:N0}";

    private static string ProjectClientAttempts(
        WatchV2ViewState<CurrentIngestAttentionSnapshot, WatchNoDetail> view)
    {
        var successful = view.LastSuccessfulAt is { } lastSuccessfulAt
            ? $"Watch 最近成功 {WatchTimeDisplay.Format(lastSuccessfulAt)}"
            : "Watch 尚无成功读取";
        return view.LastFailureAt is { } lastFailureAt
            ? $"{successful} · 最近失败 {WatchTimeDisplay.Format(lastFailureAt)}"
            : successful;
    }

    private static string ProjectFilters(
        IReadOnlyList<string>? kinds,
        IReadOnlyList<string>? severities)
    {
        var kindSummary = kinds is { Count: > 0 }
            ? $"种类 {string.Join('、', kinds)}"
            : "全部种类";
        var severitySummary = severities is { Count: > 0 }
            ? $"严重度 {string.Join('、', severities)}"
            : "全部严重度";
        return $"{kindSummary} · {severitySummary}";
    }

    private static string ProjectKind(string kind) => kind switch
    {
        CurrentIngestAttentionKinds.SeriesError => "活动 Series 错误",
        CurrentIngestAttentionKinds.PollRunFailure => "轮询运行失败",
        CurrentIngestAttentionKinds.TaskTypeProtection => "TaskType 保护",
        CurrentIngestAttentionKinds.UnassignedMesObservation => "未归属 MES 观测",
        CurrentIngestAttentionKinds.HistoryCleanupFailure => "HISTORY_CLEANUP_FAILURE",
        CurrentIngestAttentionKinds.StoragePressure => "存储压力",
        CurrentIngestAttentionKinds.HistoryReset => "历史重置",
        _ => kind,
    };

    private static WatchPresentationSeverity ProjectSeverity(string severity) => severity switch
    {
        CurrentIngestAttentionSeverities.Error => WatchPresentationSeverity.Error,
        CurrentIngestAttentionSeverities.Warning => WatchPresentationSeverity.Warning,
        _ => WatchPresentationSeverity.Informational,
    };

    private static string ProjectSubject(CurrentIngestAttentionItemSnapshot item) => item.Kind switch
    {
        CurrentIngestAttentionKinds.SeriesError =>
            $"Series {ProjectText(item.SeriesId)} · {ProjectText(item.ErrorCode)} · {ProjectText(item.Target)} · {ProjectText(item.SubjectKind)}",
        CurrentIngestAttentionKinds.PollRunFailure =>
            $"PollTrace {ProjectText(item.Evidence.PollTraceId)} · {ProjectText(item.Evidence.Outcome)}",
        CurrentIngestAttentionKinds.TaskTypeProtection =>
            $"WorkType {ProjectText(item.WorkType ?? item.Evidence.WorkType)} · {ProjectText(item.Evidence.Phase)}",
        CurrentIngestAttentionKinds.UnassignedMesObservation =>
            $"PollTrace {ProjectText(item.Evidence.PollTraceId)} · 观测序号 {item.Evidence.ObservationOrdinal?.ToString() ?? "—"}",
        CurrentIngestAttentionKinds.StoragePressure =>
            $"数据库 {ProjectText(item.Evidence.DatabaseName)} · 卷 {ProjectText(item.Evidence.VolumeRoot)} · 可用 {item.Evidence.AvailablePercent?.ToString("0.###") ?? "—"}%",
        _ => item.StableIdentity,
    };

    private static string ProjectText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string FailureMessage(string? message, string? correlationId)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}";
        return string.IsNullOrWhiteSpace(correlationId)
            ? detail
            : $"{detail} 关联 ID {correlationId}。";
    }
}
