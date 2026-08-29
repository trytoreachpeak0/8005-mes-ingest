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
    decimal? AvailablePercent,
    WatchColumnText Labels)
{
    public string Facts => string.Join(
        " · ",
        new string?[]
        {
            ProjectionCommitId is null
                ? null
                : $"{Labels.ProjectionCommit} {ProjectionCommitId}",
            ProjectionSequence is null
                ? null
                : $"{Labels.Sequence} {ProjectionSequence:N0}",
            PollTraceId is null
                ? null
                : $"{Labels.PollTrace} {PollTraceId}",
            PollTraceSequence is null
                ? null
                : $"{Labels.PollTraceSequence} {PollTraceSequence:N0}",
            SeriesId is null ? null : $"{Labels.Series} {SeriesId}",
            DemandId is null ? null : $"{Labels.Demand} {DemandId}",
            WorkType is null ? null : $"{Labels.WorkType} {WorkType}",
            ObservationOrdinal is null
                ? null
                : $"{Labels.ObservationOrdinal} {ObservationOrdinal:N0}",
            EvidenceId is null ? null : $"{Labels.Evidence} {EvidenceId}",
            ContentDigest is null ? null : $"{Labels.Digest} {ContentDigest}",
            Phase is null ? null : $"{Labels.Phase} {Phase}",
            Outcome is null ? null : $"{Labels.Outcome} {Outcome}",
            DatabaseName is null ? null : $"{Labels.Database} {DatabaseName}",
            VolumeRoot is null ? null : $"{Labels.Volume} {VolumeRoot}",
            AvailablePercent is null ? null : $"{Labels.Available} {AvailablePercent:0.###}%",
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
                text.Select(WatchGeneratedText.CurrentAttentionPresentation001),
                ProjectionCommitId: null,
                SnapshotAsOf: null,
                ProjectClientAttempts(view, catalog),
                text.Select(WatchGeneratedText.CurrentAttentionPresentation002),
                EmptyResultMessage: string.Empty,
                text.Select(WatchGeneratedText.CurrentAttentionPresentation003) + text.OrderLabel(CurrentIngestAttentionOrder.Default),
                text.Select(WatchGeneratedText.CurrentAttentionPresentation004),
                text.Select(WatchGeneratedText.CurrentAttentionPresentation005) + ProjectFilters(normalizedQuery.Kinds, normalizedQuery.Severities, catalog),
                text.Select(WatchGeneratedText.CurrentAttentionPresentation006),
                text.Select(WatchGeneratedText.CurrentAttentionPresentation007),
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
            text.Format(WatchGeneratedText.CurrentAttentionPresentation008, new object?[] { snapshot.ExactTotalItemCount, displayPageNumber, snapshot.TotalPages }, new object?[] { snapshot.ExactTotalItemCount, displayPageNumber, snapshot.TotalPages }),
            showSuccessfulEmpty
                ? text.Select(WatchGeneratedText.CurrentAttentionPresentation009)
                : string.Empty,
            text.Select(WatchGeneratedText.CurrentAttentionPresentation003) + text.OrderLabel(snapshot.Order),
            text.Select(WatchGeneratedText.CurrentAttentionPresentation010) + ProjectFilters(snapshot.Kinds, snapshot.Severities, catalog),
            text.Select(WatchGeneratedText.CurrentAttentionPresentation005) + ProjectFilters(normalizedQuery.Kinds, normalizedQuery.Severities, catalog),
            text.Select(WatchGeneratedText.CurrentAttentionPresentation006),
            text.Select(WatchGeneratedText.CurrentAttentionPresentation007),
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
            ? text.Select(WatchGeneratedText.CurrentAttentionPresentation011) + snapshot.Snapshot.PollTraceId + text.Select(WatchGeneratedText.CurrentAttentionPresentation012) + catalog.FormatAbsoluteTime(snapshot.Snapshot.ProjectionCommittedAt) + " · " + snapshot.Snapshot.ProjectionCommitId
            : null;
        var earliestAvailable = isStoragePressure
            ? snapshot.HistoryCleanup?.EarliestAvailableHostUtc is { } earliest
                ? text.Select(WatchGeneratedText.CurrentAttentionPresentation013) + catalog.FormatAbsoluteTime(earliest)
                : text.Select(WatchGeneratedText.CurrentAttentionPresentation014)
            : null;
        var rebuildProgress = isHistoryReset
            ? text.Select(WatchGeneratedText.CurrentAttentionPresentation015) + ProjectEpoch(historyEpoch) + text.Select(WatchGeneratedText.CurrentAttentionPresentation016) + snapshot.Snapshot.ProjectionCommitId + text.Select(WatchGeneratedText.CurrentAttentionPresentation017) + $"{snapshot.Snapshot.ProjectionSequence:N0} · {catalog.FormatAbsoluteTime(snapshot.Snapshot.ProjectionCommittedAt)}"
            : null;
        var currentReadRestriction = isStoragePaused
            ? text.Select(WatchGeneratedText.CurrentAttentionPresentation018)
            : isStoragePressure
                ? text.Select(WatchGeneratedText.CurrentAttentionPresentation019)
            : isHistoryReset
                ? text.Select(WatchGeneratedText.CurrentAttentionPresentation020)
                : null;
        var localRecoveryGuidance = isStoragePaused
            ? StorageRecoveryGuidance(databaseName, historyEpoch, catalog)
            : isStoragePressure
                ? text.Select(WatchGeneratedText.CurrentAttentionPresentation021)
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
                evidence.AvailablePercent,
                catalog.Columns),
            item.Navigation,
            errorSearchDrill,
            protection);
    }

    private static string StorageRecoveryGuidance(
        string? databaseName,
        HistoryEpoch? historyEpoch,
        WatchTextCatalog catalog) =>
        catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation022)
        + $"MesIngest.LocalAdministration resume-storage-pressure --database \"{ProjectText(databaseName, catalog)}\" --history-epoch {ProjectEpoch(historyEpoch)} --reason \"<reason>\""
        + catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation023);

    private static string HistoryResetGuidance(
        string? databaseName,
        HistoryEpoch? historyEpoch,
        WatchTextCatalog catalog) =>
        catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation022)
        + $"MesIngest.LocalAdministration acknowledge-history-reset --database \"{ProjectText(databaseName, catalog)}\" --history-epoch {ProjectEpoch(historyEpoch)} --reason \"<reason>\" --risk-acceptance {HistoryResetAcknowledgementPolicy.RequiredRiskAcceptance}"
        + catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation024);

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
                text.Select(WatchGeneratedText.CurrentAttentionPresentation025),
                text.Select(WatchGeneratedText.CurrentAttentionPresentation026) + FailureMessage(workspace.ErrorMessage, workspace.CorrelationId, catalog));
        }

        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Connecting)
        {
            return (
                true,
                WatchPresentationSeverity.Informational,
                text.Select(WatchGeneratedText.CurrentAttentionPresentation027),
                text.Select(WatchGeneratedText.CurrentAttentionPresentation028));
        }

        if (view.IsRefreshing)
        {
            var retained = view.Snapshot is null
                ? text.Select(WatchGeneratedText.CurrentAttentionPresentation029)
                : text.Select(WatchGeneratedText.CurrentAttentionPresentation030) + catalog.FormatAbsoluteTime(view.Snapshot.Snapshot.SnapshotAsOf) + text.Select(WatchGeneratedText.CurrentAttentionPresentation031);
            var priorFailure = view.LastFailureAt is { } priorFailedAt
                ? text.Select(WatchGeneratedText.CurrentAttentionPresentation032) + catalog.FormatAbsoluteTime(priorFailedAt) + text.Select(WatchGeneratedText.CurrentAttentionPresentation033)
                : string.Empty;
            return (
                true,
                WatchPresentationSeverity.Informational,
                view.Snapshot is null
                    ? text.Select(WatchGeneratedText.CurrentAttentionPresentation034)
                    : text.Select(WatchGeneratedText.CurrentAttentionPresentation035),
                $"{retained}{priorFailure}");
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? text.Select(WatchGeneratedText.CurrentAttentionPresentation036)
                : text.Select(WatchGeneratedText.CurrentAttentionPresentation037) + catalog.FormatAbsoluteTime(view.Snapshot.Snapshot.SnapshotAsOf) + text.Select(WatchGeneratedText.CurrentAttentionPresentation038);
            return (
                true,
                view.Snapshot is null
                    ? WatchPresentationSeverity.Error
                    : WatchPresentationSeverity.Warning,
                view.Snapshot is null
                    ? text.Select(WatchGeneratedText.CurrentAttentionPresentation039)
                    : text.Select(WatchGeneratedText.CurrentAttentionPresentation040),
                text.Select(WatchGeneratedText.CurrentAttentionPresentation041) + catalog.FormatAbsoluteTime(failedAt) + text.Select(WatchGeneratedText.CurrentAttentionPresentation042) + retained + FailureMessage(view.ErrorMessage, view.CorrelationId, catalog));
        }

        if (view.IsStale && view.Snapshot is not null)
        {
            return (
                true,
                WatchPresentationSeverity.Warning,
                text.Select(WatchGeneratedText.CurrentAttentionPresentation043),
                text.Select(WatchGeneratedText.CurrentAttentionPresentation044) + catalog.FormatAbsoluteTime(view.Snapshot.Snapshot.SnapshotAsOf) + text.Select(WatchGeneratedText.CurrentAttentionPresentation045));
        }

        return (false, WatchPresentationSeverity.None, string.Empty, string.Empty);
    }

    private static string ProjectSnapshotFacts(OperationalSnapshotIdentity snapshot, WatchTextCatalog catalog) =>
        catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation046) + catalog.FormatAbsoluteTime(snapshot.SnapshotAsOf)
        + catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation012) + catalog.FormatAbsoluteTime(snapshot.ProjectionCommittedAt)
        + $" · {snapshot.ProjectionCommitId} · " + catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation047) + $"{snapshot.ProjectionSequence:N0} · {catalog.Columns.PollTrace} {snapshot.PollTraceId} · {catalog.Columns.PollTraceHighWater} {snapshot.PollTraceHighWater:N0} · {catalog.Columns.CatalogRevision} {snapshot.CatalogRevision:N0}";

    private static string ProjectClientAttempts(
        WatchV2ViewState<CurrentIngestAttentionSnapshot, WatchNoDetail> view,
        WatchTextCatalog catalog)
    {
        var successful = view.LastSuccessfulAt is { } lastSuccessfulAt
            ? catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation048) + catalog.FormatAbsoluteTime(lastSuccessfulAt)
            : catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation049);
        return view.LastFailureAt is { } lastFailureAt
            ? successful + catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation050) + catalog.FormatAbsoluteTime(lastFailureAt)
            : successful;
    }

    private static string ProjectFilters(
        IReadOnlyList<string>? kinds,
        IReadOnlyList<string>? severities,
        WatchTextCatalog catalog)
    {
        var kindSummary = kinds is { Count: > 0 }
            ? catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation051)
                + string.Join(catalog.Common.ListSeparator, kinds.Select(code => ProjectKind(code, catalog)))
            : catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation052);
        var severitySummary = severities is { Count: > 0 }
            ? catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation053)
                + string.Join(catalog.Common.ListSeparator, severities.Select(code => ProjectSeverityLabel(code, catalog)))
            : catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation054);
        return $"{kindSummary} · {severitySummary}";
    }

    private static string ProjectKind(string rawCode, WatchTextCatalog catalog) =>
        catalog.Language == WatchDisplayLanguage.SimplifiedChinese
            ? catalog.CurrentAttention.CodeWithMeaning(catalog.CurrentAttention.DescribeKind(rawCode))
            : rawCode;

    private static string ProjectSeverityLabel(string rawCode, WatchTextCatalog catalog) =>
        catalog.Language == WatchDisplayLanguage.SimplifiedChinese
            ? catalog.CurrentAttention.CodeWithMeaning(catalog.CurrentAttention.DescribeSeverity(rawCode))
            : rawCode;

    private static WatchPresentationSeverity ProjectSeverity(string severity) => severity switch
    {
        CurrentIngestAttentionSeverities.Error => WatchPresentationSeverity.Error,
        CurrentIngestAttentionSeverities.Warning => WatchPresentationSeverity.Warning,
        _ => WatchPresentationSeverity.Informational,
    };

    private static string ProjectSubject(CurrentIngestAttentionItemSnapshot item, WatchTextCatalog catalog) => item.Kind switch
    {
        CurrentIngestAttentionKinds.SeriesError =>
            $"{catalog.Columns.SeriesId} {ProjectText(item.SeriesId, catalog)} · "
            + catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeErrorCode(ProjectText(item.ErrorCode, catalog)))
            + $" · {catalog.Columns.Target} {ProjectText(item.Target, catalog)} · "
            + catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation055) + ProjectText(item.SubjectKind, catalog),
        CurrentIngestAttentionKinds.PollRunFailure =>
            $"{catalog.Columns.PollTrace} {ProjectText(item.Evidence.PollTraceId, catalog)} · " + ProjectStatus(item.Evidence.Outcome, catalog),
        CurrentIngestAttentionKinds.TaskTypeProtection =>
            $"{catalog.Columns.WorkType} {ProjectText(item.WorkType ?? item.Evidence.WorkType, catalog)} · " + ProjectStatus(item.Evidence.Phase, catalog),
        CurrentIngestAttentionKinds.UnassignedMesObservation =>
            $"{catalog.Columns.PollTrace} {ProjectText(item.Evidence.PollTraceId, catalog)} · " + catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation056) + (item.Evidence.ObservationOrdinal?.ToString() ?? catalog.Common.SourceNotProvided),
        CurrentIngestAttentionKinds.StoragePressure =>
            catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation057) + ProjectText(item.Evidence.DatabaseName, catalog)
            + catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation058) + ProjectText(item.Evidence.VolumeRoot, catalog)
            + catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation059) + (item.Evidence.AvailablePercent?.ToString("0.###") ?? catalog.Common.SourceNotProvided) + "%",
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
            : detail + catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation060) + correlationId + catalog.CurrentAttention.Select(WatchGeneratedText.CurrentAttentionPresentation031);
    }
}
