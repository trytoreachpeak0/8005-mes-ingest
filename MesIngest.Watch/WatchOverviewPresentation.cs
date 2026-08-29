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
        "全部区域",
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
    string SeriesUnit,
    string SeriesDetail,
    string ReadabilityValue,
    string ReadabilityUnit,
    string ReadabilityDetail,
    string ErrorsValue,
    string ErrorsUnit,
    string ErrorsDetail,
    string AttentionValue,
    string AttentionUnit,
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
        WatchAreaDisplayContext localAreaContext,
        WatchTextCatalog? catalog = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(localAreaContext);
        catalog ??= WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese);
        var text = catalog.Overview;
        var local = localAreaContext.NormalizeAndValidate();
        var view = workspace.Overview;
        var snapshot = view.Snapshot;
        var host = ProjectHost(workspace, view, text);
        var info = ProjectInfo(workspace, view, text, catalog);
        var protection = ProjectProtection(workspace, snapshot, text, catalog);

        var localAreaDetail = text.LocalAreaDetail(
            local.LocalState,
            local.MesAreas,
            local.LastUpdatedAt,
            catalog);

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
                text.NoHostSnapshot,
                text.ClientAttempts(view.LastSuccessfulAt, view.LastFailureAt, catalog),
                catalog.Common.NotLoaded,
                text.SeriesUnit,
                text.WaitingForHost,
                catalog.Common.NotLoaded,
                text.DemandUnit,
                text.WaitingForHost,
                catalog.Common.NotLoaded,
                text.ErrorSeriesUnit,
                text.WaitingForHost,
                catalog.Common.NotLoaded,
                text.AttentionUnit,
                text.WaitingForHost,
                protection,
                local.MesAreas.Count == 0 && local.ProfileName == WatchAreaDisplayContext.AllAreas.ProfileName
                    ? text.AllArea
                    : local.ProfileName,
                localAreaDetail,
                text.HostNoScope,
                text.RecentHighlights,
                [],
                null,
                null,
                null,
                null);
        }

        var activities = snapshot.RecentActivity
            .Take(5)
            .Select(activity => new WatchOverviewActivityPresentation(
                catalog.Language == WatchDisplayLanguage.SimplifiedChinese
                    ? text.ActivityKind(activity.Kind)
                    : $"{text.ActivityKind(activity.Kind)} · {activity.EventType}",
                ActivityDetail(activity),
                catalog.FormatAbsoluteTime(activity.OccurredAt),
                ActivitySeverity(activity.Severity),
                activity.Navigation))
            .ToArray();
        var recentActivityHeading = activities.Length == 0
            ? text.NoRecentHighlights
            : text.RecentHighlights;

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
            text.SnapshotFacts(
                snapshot.Snapshot.SnapshotAsOf,
                snapshot.Snapshot.ProjectionCommittedAt,
                snapshot.Snapshot.ProjectionSequence,
                now ?? DateTimeOffset.Now,
                catalog),
            text.ClientAttempts(view.LastSuccessfulAt, view.LastFailureAt, catalog),
            snapshot.Series.ExactTotalSeriesCount.ToString("N0", CultureInfo.InvariantCulture),
            text.SeriesUnit,
            text.SeriesDetail(snapshot.Series.TrackingCount, snapshot.Series.ArchivedCount, snapshot.Series.GoneCount, snapshot.Series.LongGoneButVisibleCount),
            $"{snapshot.Readability.ReadableCount:N0} / {snapshot.Readability.ExactTotalDemandGenerationCount:N0}",
            text.DemandUnit,
            text.ReadabilityDetail(snapshot.Readability.NotReadableCount),
            snapshot.Errors.ActiveSeriesCount.ToString("N0", CultureInfo.InvariantCulture),
            text.ErrorSeriesUnit,
            text.ErrorsDetail(snapshot.Errors.Prior7DaysSeriesCount),
            snapshot.Attention.ExactTotalItemCount.ToString("N0", CultureInfo.InvariantCulture),
            text.AttentionUnit,
            ProjectAttentionDetail(snapshot.Attention, text),
            protection,
            local.MesAreas.Count == 0 && local.ProfileName == WatchAreaDisplayContext.AllAreas.ProfileName
                ? text.AllArea
                : local.ProfileName,
            localAreaDetail,
            text.HostAreas(snapshot.MesAreas),
            recentActivityHeading,
            activities,
            snapshot.Series.Navigation,
            snapshot.Readability.Navigation,
            snapshot.Errors.Navigation,
            snapshot.Attention.Navigation);
    }

    private static WatchProtectionStatusPresentation ProjectProtection(
        WatchV2WorkspaceState workspace,
        WatchOverviewSnapshot? overview,
        WatchOverviewText text,
        WatchTextCatalog catalog)
    {
        if (workspace.ConnectionStatus != WatchHostConnectionStatus.Connected)
        {
            return new(
                text.ProtectionUnavailable,
                text.ProtectionUnavailableDetail,
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
                    text.HistoryResetPending,
                    text.HistoryResetDetail(ProjectEpoch(current.Snapshot.HistoryEpoch, catalog)),
                    WatchPresentationSeverity.Error,
                    RequiresAttention: true);
            }

            if (current.StoragePressure is { } storage)
            {
                var observed = text.StorageObserved(
                    storage.Space.VolumeRoot,
                    storage.Space.AvailablePercent,
                    storage.ObservedAt,
                    catalog);
                return storage.Status switch
                {
                    StoragePressureStatuses.Paused => new(
                        "StoragePressurePause",
                        text.StoragePaused(observed),
                        WatchPresentationSeverity.Error,
                        RequiresAttention: true),
                    StoragePressureStatuses.Warning => new(
                        text.StorageWarning,
                        text.StorageWarningDetail(observed),
                        WatchPresentationSeverity.Warning,
                        RequiresAttention: true),
                    _ => new(
                        text.StorageHealthy,
                        text.StorageHealthyDetail(observed, ProjectEpoch(storage.HistoryEpoch, catalog)),
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
                    text.HistoryResetPending,
                    text.HistoryResetOverviewDetail(ProjectEpoch(overview.Snapshot.HistoryEpoch, catalog)),
                    WatchPresentationSeverity.Error,
                    RequiresAttention: true);
            }

            if (overviewKinds.Contains(CurrentIngestAttentionKinds.StoragePressure))
            {
                return new(
                    text.StorageNeedsAttention,
                    text.StorageNeedsAttentionDetail,
                    WatchPresentationSeverity.Warning,
                    RequiresAttention: true);
            }

            return new(
                text.NoProtectionReported,
                text.NoProtectionDetail(overview.Snapshot.SnapshotAsOf, catalog),
                WatchPresentationSeverity.Success,
                RequiresAttention: false);
        }

        return new(
            text.WaitingProtection,
            text.WaitingForHost,
            WatchPresentationSeverity.Informational,
            RequiresAttention: false);
    }

    private static string ProjectEpoch(HistoryEpoch? historyEpoch, WatchTextCatalog catalog) =>
        historyEpoch?.ToString() ?? catalog.Common.SystemUnknown;

    private static (string Status, string Detail, WatchPresentationSeverity Severity) ProjectHost(
        WatchV2WorkspaceState workspace,
        WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail> overview,
        WatchOverviewText text)
    {
        var baseUrl = workspace.BaseUrl ?? text.AddressNotConfigured;
        return workspace.ConnectionStatus switch
        {
            WatchHostConnectionStatus.NotConfigured =>
                (text.HostStatus(workspace.ConnectionStatus, false), baseUrl, WatchPresentationSeverity.Informational),
            WatchHostConnectionStatus.Connecting =>
                (text.HostStatus(workspace.ConnectionStatus, false), baseUrl, WatchPresentationSeverity.Informational),
            WatchHostConnectionStatus.Failed =>
                (text.HostStatus(workspace.ConnectionStatus, false), $"{baseUrl} · {FailureLabel(workspace.FailureKind, workspace.FailureCode)}", WatchPresentationSeverity.Error),
            WatchHostConnectionStatus.Connected when overview.LastFailureAt is not null =>
                (text.HostStatus(workspace.ConnectionStatus, true), $"{baseUrl} · {FailureLabel(overview.FailureKind, overview.FailureCode)}", WatchPresentationSeverity.Warning),
            WatchHostConnectionStatus.Connected =>
                (text.HostStatus(workspace.ConnectionStatus, false), $"{baseUrl} · {text.ContractCompatible}", WatchPresentationSeverity.Success),
            _ => throw new ArgumentOutOfRangeException(nameof(workspace.ConnectionStatus)),
        };
    }

    private static (bool IsOpen, WatchPresentationSeverity Severity, string Title, string Message) ProjectInfo(
        WatchV2WorkspaceState workspace,
        WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail> view,
        WatchOverviewText text,
        WatchTextCatalog catalog)
    {
        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Failed)
        {
            return (
                true,
                WatchPresentationSeverity.Error,
                text.CannotConnectHost,
                text.ReplacementHostFailure(text.FailureMessage(workspace.ErrorMessage, workspace.CorrelationId)));
        }

        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Connecting)
        {
            return (true, WatchPresentationSeverity.Informational, text.ConnectingHost, text.WaitingForHost);
        }

        if (view.IsRefreshing)
        {
            var retained = view.Snapshot is null
                ? text.WaitingAtomicSnapshot
                : text.RetainedDuringRefresh(view.Snapshot.Snapshot.SnapshotAsOf, catalog);
            var priorFailure = view.LastFailureAt is { } priorFailedAt
                ? text.PriorFailureRetry(priorFailedAt, catalog)
                : string.Empty;
            return (
                true,
                WatchPresentationSeverity.Informational,
                view.Snapshot is null ? text.ReadingOverview : text.RefreshingOverview,
                $"{retained}{priorFailure}");
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? text.NoSuccessfulSnapshot
                : text.RetainedAfterFailure(view.Snapshot.Snapshot.SnapshotAsOf, catalog);
            return (
                true,
                view.Snapshot is null ? WatchPresentationSeverity.Error : WatchPresentationSeverity.Warning,
                view.Snapshot is null ? text.OverviewReadFailed : text.OverviewRefreshFailedRetained,
                text.FailedAt(
                    failedAt,
                    retained,
                    text.FailureMessage(view.ErrorMessage, view.CorrelationId),
                    catalog));
        }

        return (false, WatchPresentationSeverity.None, string.Empty, string.Empty);
    }

    private static string ProjectAttentionDetail(
        WatchOverviewAttentionSummary attention,
        WatchOverviewText text)
    {
        var errors = attention.Severities.FirstOrDefault(item =>
            string.Equals(item.Value, CurrentIngestAttentionSeverities.Error, StringComparison.Ordinal))?.Count ?? 0;
        var warnings = attention.Severities.FirstOrDefault(item =>
            string.Equals(item.Value, CurrentIngestAttentionSeverities.Warning, StringComparison.Ordinal))?.Count ?? 0;
        return text.AttentionDetail(errors, warnings);
    }

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

}
