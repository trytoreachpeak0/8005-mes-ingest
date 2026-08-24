using System.Globalization;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal enum WatchPresentationSeverity
{
    None,
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Local display context for the AREA profile selected by the operator. This is
/// deliberately not part of the Host snapshot contract: a failed query may
/// leave the old Host scope on screen while this local selection has changed.
/// </summary>
internal sealed record WatchAreaDisplayContext(
    string ProfileName,
    IReadOnlyList<string> MesAreas,
    string LocalState,
    DateTimeOffset? LastUpdatedAt)
{
    public static WatchAreaDisplayContext AllAreas { get; } = new(
        "全部 AREA",
        [],
        "本机默认",
        null);

    public WatchAreaDisplayContext NormalizeAndValidate()
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            throw new ArgumentException("AREA profile name cannot be empty.", nameof(ProfileName));
        }

        var normalized = new WatchOverviewQuery(MesAreas).NormalizeAndValidate().MesAreas
            ?? Array.Empty<string>();
        return this with
        {
            ProfileName = ProfileName.Trim(),
            MesAreas = normalized,
            LocalState = string.IsNullOrWhiteSpace(LocalState) ? "本机配置" : LocalState.Trim(),
        };
    }
}

internal sealed record WatchOverviewActivityPresentation(
    string Heading,
    string Detail,
    string OccurredAt,
    WatchPresentationSeverity Severity,
    OverviewNavigationIntent Navigation);

internal sealed record WatchProtectionStatusPresentation(
    string Status,
    string Detail,
    WatchPresentationSeverity Severity,
    bool RequiresAttention);

internal sealed record WatchOverviewPresentation(
    string HostStatus,
    string HostDetail,
    WatchPresentationSeverity HostSeverity,
    bool IsInfoOpen,
    WatchPresentationSeverity InfoSeverity,
    string InfoTitle,
    string InfoMessage,
    bool HasSnapshot,
    bool IsRefreshing,
    bool IsStale,
    string SnapshotFacts,
    string ClientAttemptFacts,
    string SeriesValue,
    string SeriesDetail,
    string ReadabilityValue,
    string ReadabilityDetail,
    string ErrorsValue,
    string ErrorsDetail,
    string AttentionValue,
    string AttentionDetail,
    WatchProtectionStatusPresentation Protection,
    string LocalAreaHeading,
    string LocalAreaDetail,
    string HostAreaScope,
    string RecentActivityHeading,
    IReadOnlyList<WatchOverviewActivityPresentation> RecentActivity,
    OverviewNavigationIntent? SeriesNavigation,
    OverviewNavigationIntent? ReadabilityNavigation,
    OverviewNavigationIntent? ErrorsNavigation,
    OverviewNavigationIntent? AttentionNavigation)
{
    public static WatchOverviewPresentation Project(
        WatchV2WorkspaceState workspace,
        WatchAreaDisplayContext localAreaContext)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(localAreaContext);
        var local = localAreaContext.NormalizeAndValidate();
        var view = workspace.Overview;
        var snapshot = view.Snapshot;
        var host = ProjectHost(workspace, view);
        var info = ProjectInfo(workspace, view);
        var protection = ProjectProtection(workspace, snapshot);

        var localAreaDetail = local.MesAreas.Count == 0
            ? $"{local.LocalState} · 未限制 Host AREA 查询"
            : $"{local.LocalState} · {local.MesAreas.Count} 个 AREA";
        if (local.LastUpdatedAt is { } localUpdatedAt)
        {
            localAreaDetail += $" · 本机更新 {FormatTime(localUpdatedAt)}";
        }

        if (snapshot is null)
        {
            return new WatchOverviewPresentation(
                host.Status,
                host.Detail,
                host.Severity,
                info.IsOpen,
                info.Severity,
                info.Title,
                info.Message,
                HasSnapshot: false,
                view.IsRefreshing,
                IsStale: false,
                "尚无 Host 业务快照",
                ProjectClientAttempts(view),
                "—",
                "等待 Host 概览",
                "—",
                "等待 Host 概览",
                "—",
                "等待 Host 概览",
                "—",
                "等待 Host 概览",
                protection,
                local.ProfileName,
                localAreaDetail,
                "Host 已提交范围：尚无快照",
                "近期重点动态",
                [],
                null,
                null,
                null,
                null);
        }

        var activities = snapshot.RecentActivity
            .Take(5)
            .Select(activity => new WatchOverviewActivityPresentation(
                $"{ActivityKind(activity.Kind)} · {activity.EventType}",
                ActivityDetail(activity),
                FormatTime(activity.OccurredAt),
                ActivitySeverity(activity.Severity),
                activity.Navigation))
            .ToArray();
        var recentActivityHeading = activities.Length == 0
            ? snapshot.EmptyStateMessage ?? WatchOverviewRecentActivityStates.NoRecentHighlightsMessage
            : "近期重点动态";

        return new WatchOverviewPresentation(
            host.Status,
            host.Detail,
            host.Severity,
            info.IsOpen,
            info.Severity,
            info.Title,
            info.Message,
            HasSnapshot: true,
            view.IsRefreshing,
            view.IsStale,
            $"Host 快照 {FormatTime(snapshot.Snapshot.SnapshotAsOf)} · 投影提交 {FormatTime(snapshot.Snapshot.ProjectionCommittedAt)} · 序列 {snapshot.Snapshot.ProjectionSequence}",
            ProjectClientAttempts(view),
            snapshot.Series.ExactTotalSeriesCount.ToString("N0", CultureInfo.InvariantCulture),
            $"{snapshot.Series.TrackingCount:N0} Tracking · {snapshot.Series.ArchivedCount:N0} Archived · {snapshot.Series.GoneCount:N0} GONE · {snapshot.Series.LongGoneButVisibleCount:N0} 归档后仍可见",
            $"{snapshot.Readability.ReadableCount:N0} / {snapshot.Readability.ExactTotalDemandGenerationCount:N0}",
            $"当前外部可读 · {snapshot.Readability.NotReadableCount:N0} 个不可见或阻断",
            snapshot.Errors.ActiveSeriesCount.ToString("N0", CultureInfo.InvariantCulture),
            $"活动错误 Series · 近 7 天 {snapshot.Errors.Prior7DaysSeriesCount:N0} 个 Series",
            snapshot.Attention.ExactTotalItemCount.ToString("N0", CultureInfo.InvariantCulture),
            ProjectAttentionDetail(snapshot.Attention),
            protection,
            local.ProfileName,
            localAreaDetail,
            ProjectHostAreas(snapshot.MesAreas),
            recentActivityHeading,
            activities,
            snapshot.Series.Navigation,
            snapshot.Readability.Navigation,
            snapshot.Errors.Navigation,
            snapshot.Attention.Navigation);
    }

    private static WatchProtectionStatusPresentation ProjectProtection(
        WatchV2WorkspaceState workspace,
        WatchOverviewSnapshot? overview)
    {
        if (workspace.ConnectionStatus != WatchHostConnectionStatus.Connected)
        {
            return new(
                "保护状态不可用",
                "连接 Host 后读取 StoragePressure 与 HistoryEpoch 保护状态。",
                WatchPresentationSeverity.Informational,
                RequiresAttention: false);
        }

        var overviewKinds = overview?.Attention.Types
            .Where(facet => facet.Count > 0)
            .Select(facet => facet.Value)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var overviewHasProtection = overviewKinds.Contains(
                CurrentIngestAttentionKinds.HistoryReset)
            || overviewKinds.Contains(CurrentIngestAttentionKinds.StoragePressure);
        var protectionDetail = (
            workspace.Protection.Snapshot,
            workspace.Protection.LastSuccessfulAt);
        var currentAttentionDetail = (
            workspace.CurrentAttention.Snapshot,
            workspace.CurrentAttention.LastSuccessfulAt);
        var currentDetail = overviewHasProtection
                && protectionDetail.Snapshot is not null
            ? protectionDetail
            : new[] { protectionDetail, currentAttentionDetail }
                .Where(candidate => candidate.Snapshot is not null)
                .OrderByDescending(candidate => candidate.LastSuccessfulAt)
                .ThenByDescending(candidate => candidate.Snapshot!.Snapshot.SnapshotAsOf)
                .FirstOrDefault();
        var current = currentDetail.Snapshot;
        var overviewEpoch = overview?.Snapshot.HistoryEpoch;
        var currentEpoch = current?.Snapshot.HistoryEpoch;
        var currentMatchesOverview = current is not null
            && (overviewEpoch is null
                || currentEpoch is null
                || overviewEpoch == currentEpoch)
            && (overview is null
                || overviewHasProtection
                || currentDetail.LastSuccessfulAt > workspace.Overview.LastSuccessfulAt);
        if (currentMatchesOverview)
        {
            var historyReset = current!.Items.FirstOrDefault(item => string.Equals(
                item.Kind,
                CurrentIngestAttentionKinds.HistoryReset,
                StringComparison.Ordinal));
            if (historyReset is not null)
            {
                return new(
                    "历史重置待确认",
                    $"HistoryEpoch {ProjectEpoch(current.Snapshot.HistoryEpoch)} · 外部当前读取 503 INGEST_NOT_CURRENT · 仅可在数据库主机本地提交 HistoryResetAcknowledgement。",
                    WatchPresentationSeverity.Error,
                    RequiresAttention: true);
            }

            if (current.StoragePressure is { } storage)
            {
                var observed = $"卷 {storage.Space.VolumeRoot} 可用 {storage.Space.AvailablePercent:0.###}% · 观测 {FormatTime(storage.ObservedAt)}";
                return storage.Status switch
                {
                    StoragePressureStatuses.Paused => new(
                        "StoragePressurePause",
                        $"{observed} · MES 轮询暂停；仅可在数据库主机本地恢复。",
                        WatchPresentationSeverity.Error,
                        RequiresAttention: true),
                    StoragePressureStatuses.Warning => new(
                        "存储空间严重告警",
                        $"{observed} · 低于 15% 告警阈值，尚未进入暂停。",
                        WatchPresentationSeverity.Warning,
                        RequiresAttention: true),
                    _ => new(
                        "存储与历史保护正常",
                        $"{observed} · 当前 HistoryEpoch {ProjectEpoch(storage.HistoryEpoch)}。",
                        WatchPresentationSeverity.Success,
                        RequiresAttention: false),
                };
            }
        }

        if (overview is not null)
        {
            if (overviewKinds.Contains(CurrentIngestAttentionKinds.HistoryReset))
            {
                return new(
                    "历史重置待确认",
                    $"HistoryEpoch {ProjectEpoch(overview.Snapshot.HistoryEpoch)} · 打开接入告警查看新纪元建立进度、503 原因与本地确认指引。",
                    WatchPresentationSeverity.Error,
                    RequiresAttention: true);
            }

            if (overviewKinds.Contains(CurrentIngestAttentionKinds.StoragePressure))
            {
                return new(
                    "存储压力需处理",
                    "打开接入告警查看剩余空间、是否已暂停、最后成功窗口与本地恢复指引。",
                    WatchPresentationSeverity.Warning,
                    RequiresAttention: true);
            }

            return new(
                "未报告存储或历史保护项",
                $"Host 快照 {FormatTime(overview.Snapshot.SnapshotAsOf)} 的当前关注分面中无 StoragePressure 或 HistoryReset。",
                WatchPresentationSeverity.Success,
                RequiresAttention: false);
        }

        return new(
            "等待保护状态",
            "等待 Host 概览快照。",
            WatchPresentationSeverity.Informational,
            RequiresAttention: false);
    }

    private static string ProjectEpoch(HistoryEpoch? historyEpoch) =>
        historyEpoch?.ToString() ?? "—";

    private static (string Status, string Detail, WatchPresentationSeverity Severity) ProjectHost(
        WatchV2WorkspaceState workspace,
        WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail> overview)
    {
        var baseUrl = workspace.BaseUrl ?? "未配置地址";
        return workspace.ConnectionStatus switch
        {
            WatchHostConnectionStatus.NotConfigured =>
                ("Host 未连接", baseUrl, WatchPresentationSeverity.Informational),
            WatchHostConnectionStatus.Connecting =>
                ("Host 连接中", baseUrl, WatchPresentationSeverity.Informational),
            WatchHostConnectionStatus.Failed =>
                ("Host 连接失败", $"{baseUrl} · {FailureLabel(workspace.FailureKind, workspace.FailureCode)}", WatchPresentationSeverity.Error),
            WatchHostConnectionStatus.Connected when overview.LastFailureAt is not null =>
                ("Host 已连接 · 最近读取失败", $"{baseUrl} · {FailureLabel(overview.FailureKind, overview.FailureCode)}", WatchPresentationSeverity.Warning),
            WatchHostConnectionStatus.Connected =>
                ("Host 已连接", $"{baseUrl} · 契约兼容", WatchPresentationSeverity.Success),
            _ => throw new ArgumentOutOfRangeException(nameof(workspace.ConnectionStatus)),
        };
    }

    private static (bool IsOpen, WatchPresentationSeverity Severity, string Title, string Message) ProjectInfo(
        WatchV2WorkspaceState workspace,
        WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail> view)
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
            return (true, WatchPresentationSeverity.Informational, "正在连接 Host", "连接成功后将读取同一份原子概览快照。");
        }

        if (view.IsRefreshing)
        {
            var retained = view.Snapshot is null
                ? "等待 Host 返回原子快照。"
                : $"刷新期间继续显示 Host 快照 {FormatTime(view.Snapshot.Snapshot.SnapshotAsOf)}。";
            var priorFailure = view.LastFailureAt is { } priorFailedAt
                ? $" 上次失败于 {FormatTime(priorFailedAt)}；本次正在重试。"
                : string.Empty;
            return (
                true,
                WatchPresentationSeverity.Informational,
                view.Snapshot is null ? "正在读取概览" : "正在刷新概览",
                $"{retained}{priorFailure}");
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? "当前没有可显示的成功快照。"
                : $"继续显示 Host 快照 {FormatTime(view.Snapshot.Snapshot.SnapshotAsOf)}；其范围不会被失败查询改写。";
            return (
                true,
                view.Snapshot is null ? WatchPresentationSeverity.Error : WatchPresentationSeverity.Warning,
                view.Snapshot is null ? "概览读取失败" : "概览刷新失败，已保留上次完整快照",
                $"失败于 {FormatTime(failedAt)}。{retained}{FailureMessage(view.ErrorMessage, view.CorrelationId)}");
        }

        return (false, WatchPresentationSeverity.None, string.Empty, string.Empty);
    }

    private static string ProjectClientAttempts(
        WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail> view)
    {
        var successful = view.LastSuccessfulAt is { } lastSuccessfulAt
            ? $"Watch 最近成功 {FormatTime(lastSuccessfulAt)}"
            : "Watch 尚无成功读取";
        return view.LastFailureAt is { } lastFailureAt
            ? $"{successful} · 最近失败 {FormatTime(lastFailureAt)}"
            : successful;
    }

    private static string ProjectHostAreas(IReadOnlyList<string> mesAreas) =>
        mesAreas.Count == 0
            ? "Host 已提交范围：全部 AREA"
            : mesAreas.Count <= 4
                ? $"Host 已提交范围：{string.Join("、", mesAreas)}"
                : $"Host 已提交范围：{mesAreas.Count} 个 AREA（{string.Join("、", mesAreas.Take(3))}…）";

    private static string ProjectAttentionDetail(WatchOverviewAttentionSummary attention)
    {
        var errors = attention.Severities.FirstOrDefault(item =>
            string.Equals(item.Value, CurrentIngestAttentionSeverities.Error, StringComparison.Ordinal))?.Count ?? 0;
        var warnings = attention.Severities.FirstOrDefault(item =>
            string.Equals(item.Value, CurrentIngestAttentionSeverities.Warning, StringComparison.Ordinal))?.Count ?? 0;
        return $"当前接入关注项 · {errors:N0} ERROR · {warnings:N0} WARNING";
    }

    private static string ActivityKind(string kind) => kind switch
    {
        CurrentIngestAttentionKinds.SeriesError => "错误检索",
        CurrentIngestAttentionKinds.PollRunFailure => "轮询运行",
        CurrentIngestAttentionKinds.TaskTypeProtection => "任务类型保护",
        CurrentIngestAttentionKinds.UnassignedMesObservation => "未分配 MES 观测",
        _ => kind,
    };

    private static string ActivityDetail(WatchOverviewActivitySnapshot activity)
    {
        var parts = new[]
            {
                activity.SeriesId,
                activity.WorkType,
                activity.PollTraceId,
            }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var detail = string.Join(" · ", parts);
        return string.IsNullOrEmpty(detail) ? activity.EventId : detail;
    }

    private static WatchPresentationSeverity ActivitySeverity(string severity) =>
        severity switch
        {
            CurrentIngestAttentionSeverities.Error => WatchPresentationSeverity.Error,
            CurrentIngestAttentionSeverities.Warning => WatchPresentationSeverity.Warning,
            _ => WatchPresentationSeverity.Informational,
        };

    private static string FailureLabel(WatchHostFailureKind kind, string? code) =>
        code is null ? kind.ToString() : $"{kind} / {code}";

    private static string FailureMessage(string? message, string? correlationId)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}";
        return string.IsNullOrWhiteSpace(correlationId)
            ? detail
            : $"{detail} 关联 ID {correlationId}。";
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}
