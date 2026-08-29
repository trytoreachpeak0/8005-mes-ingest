using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal sealed record WatchReadabilityStateFacetPresentation(
    string State,
    long DemandCount);

internal sealed record WatchReadabilityBlockerFacetPresentation(
    string Code,
    long DemandCount);

internal sealed record WatchReadabilityLiveMesFieldSetPresentation(
    string Area,
    string Eqp,
    string Step,
    string MesSourceDate,
    string Package);

internal sealed record WatchReadabilityAuditRowPresentation(
    string DemandId,
    string SeriesId,
    string WorkType,
    string Sublot,
    int Generation,
    string PredecessorDemandId,
    string DemandStatus,
    string SeriesLifecycle,
    string SeriesCurrentPresence,
    bool IsCurrentGeneration,
    string DemandCreatedAt,
    string DemandLastSeenAt,
    string GoneConfirmedAt,
    WatchReadabilityLiveMesFieldSetPresentation? LiveMesFields,
    string MesArea,
    int CurrentRawObservationCount,
    string ExternalReadabilityState,
    string LeadReadabilityBlocker,
    IReadOnlyList<string> ReadabilityBlockers,
    string AllBlockersSummary,
    string LatestObservationPollTraceId,
    string LatestObservationProjectionCommitId,
    string LatestObservationAt,
    string DemandLabel,
    string WorkTypeLabel,
    string SublotLabel,
    string ExternalReadabilityMeaning,
    string LeadReadabilityBlockerMeaning,
    WatchColumnText Labels)
{
    private WatchTextCatalog Catalog => WatchTextCatalog.For(Labels.DisplayLanguage);

    public string LifecycleSummary
    {
        get
        {
            var demandStatus = Labels.DisplayLanguage == WatchDisplayLanguage.SimplifiedChinese
                ? Catalog.DemandSeries.DescribePresence(DemandStatus)
                : DemandStatus;
            var lifecycle = Labels.DisplayLanguage == WatchDisplayLanguage.SimplifiedChinese
                ? Catalog.DemandSeries.DescribeLifecycle(SeriesLifecycle)
                : SeriesLifecycle;
            var presence = Labels.DisplayLanguage == WatchDisplayLanguage.SimplifiedChinese
                ? Catalog.DemandSeries.DescribePresence(SeriesCurrentPresence)
                : SeriesCurrentPresence;
            return $"{Labels.Demand} {demandStatus} · {Labels.Series} {lifecycle} · {presence}";
        }
    }

    public string DemandIdentity => $"{DemandLabel} {DemandId}";

    public string WorkTypeIdentity => $"{WorkTypeLabel} {WorkType}";

    public string SublotIdentity => $"{SublotLabel} {Sublot}";

    public string ReadabilityDisplay => Catalog.ReadabilityAudit.CodeWithMeaning(
        Catalog.ReadabilityAudit.DescribeReadabilityMeaning(ExternalReadabilityState));

    public string BlockerDisplay => string.IsNullOrWhiteSpace(LeadReadabilityBlocker)
        ? LeadReadabilityBlockerMeaning
        : Catalog.ReadabilityAudit.CodeWithMeaning(
            Catalog.ReadabilityAudit.DescribeBlocker(LeadReadabilityBlocker));
}

internal sealed record WatchReadabilityQualificationCheckPresentation(
    string Code,
    string BlockingCode,
    string Result,
    string Meaning,
    WatchReadabilityAuditText? Catalog = null)
{
    public string ResultDisplay => (Catalog
        ?? WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).ReadabilityAudit)
        .QualificationResult(Result);
}

internal sealed record WatchReadabilityBlockerEvidencePresentation(
    string Code,
    int Priority,
    int EvidenceOrdinal,
    string SubjectKind,
    string ObservedValue,
    string ExpectedRule,
    string ObservedAt,
    string PollTraceId,
    string ProjectionCommitId);

internal sealed record WatchReadabilityRawObservationPresentation(
    int Ordinal,
    string PollTraceId,
    string ProjectionCommitId,
    string Assignment,
    string SeriesId,
    string DemandId,
    string WorkType,
    string Sublot,
    string Area,
    string Eqp,
    string Step,
    string MesSourceDate,
    string Package,
    string ObservedAt,
    string MesSourceDateRaw);

internal sealed record WatchReadabilityAuditDetailPresentation(
    string DemandId,
    string ExternalReadabilityState,
    string? LeadReadabilityBlocker,
    string Heading,
    string BusinessIdentity,
    string Facts,
    string SeriesFacts,
    string LiveMesFacts,
    string ObservationSummary,
    string AllBlockersSummary,
    WatchReadabilityLiveMesFieldSetPresentation? LiveMesFields,
    IReadOnlyList<WatchReadabilityQualificationCheckPresentation> QualificationChecks,
    IReadOnlyList<WatchReadabilityBlockerEvidencePresentation> BlockerEvidence,
    IReadOnlyList<WatchReadabilityRawObservationPresentation> RawObservations,
    string PollTraceFacts,
    string SnapshotReference,
    string ProjectionCommitId,
    long ProjectionSequence,
    string PollTraceId,
    long CatalogRevision)
{
    public string SemanticState => ExternalReadabilityState switch
    {
        ExternalReadabilityStates.Readable => "Readable",
        ExternalReadabilityStates.NotReadable => "Blocked",
        _ => "Neutral",
    };

    public WatchReadabilityBlockerEvidencePresentation? PrimaryBlockerEvidence =>
        string.IsNullOrWhiteSpace(LeadReadabilityBlocker)
            ? null
            : BlockerEvidence.FirstOrDefault(blocker => string.Equals(
                blocker.Code,
                LeadReadabilityBlocker,
                StringComparison.Ordinal));
}

internal sealed record WatchReadabilityAuditPresentation(
    bool HasSnapshot,
    bool IsRefreshing,
    bool IsStale,
    bool IsInfoOpen,
    WatchPresentationSeverity InfoSeverity,
    string InfoTitle,
    string InfoMessage,
    string SnapshotFacts,
    string ClientAttemptFacts,
    string? SelectionNotice,
    string PageSummary,
    string EmptyResultMessage,
    string OrderSummary,
    string HostFilterSummary,
    string CurrentQuerySummary,
    string HostAreaScope,
    string LocalAreaHeading,
    string LocalAreaDetail,
    bool IsAreaScopeDifferent,
    bool CanGoPrevious,
    bool CanGoNext,
    IReadOnlyList<WatchReadabilityStateFacetPresentation> StateFacets,
    IReadOnlyList<WatchReadabilityBlockerFacetPresentation> BlockerFacets,
    IReadOnlyList<WatchReadabilityAuditRowPresentation> Rows,
    WatchReadabilityAuditDetailPresentation? Detail)
{
    public static WatchReadabilityAuditPresentation Project(
        WatchV2WorkspaceState workspace,
        ReadabilityAuditQuery query,
        WatchAreaDisplayContext areaContext,
        WatchTextCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(areaContext);

        catalog ??= WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese);
        var text = catalog.ReadabilityAudit;
        var localArea = areaContext.NormalizeAndValidate();
        var view = workspace.ReadabilityAudit;
        var snapshot = view.Snapshot;
        var info = ProjectInfo(workspace, view, catalog);
        if (snapshot is null)
        {
            return new WatchReadabilityAuditPresentation(
                HasSnapshot: false,
                view.IsRefreshing,
                view.IsStale,
                info.IsOpen,
                info.Severity,
                info.Title,
                info.Message,
                text.NoHostSnapshot,
                text.ClientAttempts(view.LastSuccessfulAt, view.LastFailureAt),
                view.SelectionNotice,
                text.NoSnapshot,
                EmptyResultMessage: string.Empty,
                text.HostOrder(ReadabilityAuditOrder.Default),
                text.HostCommittedConditions(text.NoSnapshot),
                text.PendingConditions(text.FilterSummary(query.Filter.Normalize())),
                text.HostNoScope,
                text.LocalAreaHeading(localArea.ProfileName),
                text.LocalAreaDetail(localArea.LocalState, localArea.MesAreas),
                IsAreaScopeDifferent: false,
                CanGoPrevious: false,
                CanGoNext: false,
                StateFacets: [],
                BlockerFacets: [],
                Rows: [],
                Detail: null);
        }

        var displayPageNumber = snapshot.TotalPages == 0 ? 0 : snapshot.PageNumber;
        var committedAreas = snapshot.Filter.Normalize().MesAreas;
        return new WatchReadabilityAuditPresentation(
            HasSnapshot: true,
            view.IsRefreshing,
            view.IsStale,
            info.IsOpen,
            info.Severity,
            info.Title,
            info.Message,
            text.HostSnapshotFacts(
                snapshot.Snapshot.ProjectionCommittedAt,
                snapshot.Snapshot.ProjectionCommitId,
                snapshot.Snapshot.ProjectionSequence,
                snapshot.Snapshot.PollTraceId,
                snapshot.Snapshot.CatalogRevision),
            text.ClientAttempts(view.LastSuccessfulAt, view.LastFailureAt),
            view.SelectionNotice,
            text.PageSummary(snapshot.ExactTotalDemandCount, displayPageNumber, snapshot.TotalPages),
            snapshot.ExactTotalDemandCount == 0
                ? text.EmptyResult(0)
                : string.Empty,
            text.HostOrder(snapshot.Order),
            text.HostCommittedConditions(text.FilterSummary(snapshot.Filter.Normalize())),
            text.PendingConditions(text.FilterSummary(query.Filter.Normalize())),
            text.HostAreaScope(committedAreas),
            text.LocalAreaHeading(localArea.ProfileName),
            text.LocalAreaDetail(localArea.LocalState, localArea.MesAreas),
            !committedAreas.SequenceEqual(localArea.MesAreas, StringComparer.Ordinal),
            snapshot.TotalPages > 0 && snapshot.PageNumber > 1,
            snapshot.TotalPages > 0 && snapshot.PageNumber < snapshot.TotalPages,
            snapshot.Facets.ReadabilityStates
                .Select(facet => new WatchReadabilityStateFacetPresentation(
                    ProjectReadability(facet.State, catalog),
                    facet.DemandCount))
                .ToArray(),
            snapshot.Facets.Blockers
                .Select(facet => new WatchReadabilityBlockerFacetPresentation(
                    ProjectBlocker(facet.Code, catalog),
                    facet.DemandCount))
                .ToArray(),
            snapshot.Items.Select(item => ProjectRow(item, catalog)).ToArray(),
            ProjectDetail(snapshot, view.Detail, catalog));
    }

    private static (bool IsOpen, WatchPresentationSeverity Severity, string Title, string Message)
        ProjectInfo(
            WatchV2WorkspaceState workspace,
            WatchV2ViewState<ReadabilityAuditListSnapshot, ReadabilityAuditDetailSnapshot> view,
            WatchTextCatalog catalog)
    {
        var text = catalog.ReadabilityAudit;
        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Failed)
        {
            return (
                true,
                WatchPresentationSeverity.Error,
                text.CannotConnectHost,
                text.ReplacementHostFailure(text.FailureMessage(
                    workspace.ErrorMessage,
                    workspace.CorrelationId)));
        }

        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Connecting)
        {
            return (
                true,
                WatchPresentationSeverity.Informational,
                text.ConnectingHost,
                text.ConnectingMessage);
        }

        if (view.IsRefreshing)
        {
            var retained = view.Snapshot is null
                ? text.WaitingFrozenSnapshot
                : text.RetainedDuringRefresh(
                    view.Snapshot.Snapshot.ProjectionCommittedAt,
                    catalog);
            var priorFailure = view.LastFailureAt is { } priorFailedAt
                ? text.PriorFailureRetry(priorFailedAt, catalog)
                : string.Empty;
            return (
                true,
                WatchPresentationSeverity.Informational,
                text.LoadingTitle(view.Snapshot is not null),
                $"{retained}{priorFailure}");
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? text.NoSuccessfulSnapshot
                : text.RetainedAfterFailure(
                    view.Snapshot.Snapshot.ProjectionCommittedAt,
                    catalog);
            return (
                true,
                view.Snapshot is null
                    ? WatchPresentationSeverity.Error
                    : WatchPresentationSeverity.Warning,
                text.FailureTitle(view.Snapshot is not null),
                text.FailedAt(
                    failedAt,
                    retained,
                    text.FailureMessage(view.ErrorMessage, view.CorrelationId),
                    catalog));
        }

        if (string.Equals(
                view.SelectionNotice,
                WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
                StringComparison.Ordinal))
        {
            return (
                true,
                WatchPresentationSeverity.Warning,
                text.SelectionLostTitle,
                text.SelectionLostMessage);
        }

        return (false, WatchPresentationSeverity.None, string.Empty, string.Empty);
    }

    private static WatchReadabilityAuditDetailPresentation? ProjectDetail(
        ReadabilityAuditListSnapshot list,
        ReadabilityAuditDetailSnapshot? detail,
        WatchTextCatalog catalog)
    {
        if (detail is null || !HasSameSnapshotIdentity(list, detail))
        {
            return null;
        }

        var demand = detail.Demand;
        var identity = detail.Snapshot;
        var text = catalog.ReadabilityAudit;
        var liveMes = ProjectLiveMesFields(demand.LiveMesFields, catalog);
        var rawObservations = detail.LatestRawObservations
            .OrderBy(observation => observation.Ordinal)
            .Select(observation => new WatchReadabilityRawObservationPresentation(
                observation.Ordinal,
                observation.PollTraceId,
                observation.ProjectionCommitId,
                observation.Assignment.ToString(),
                ProjectText(observation.SeriesId, catalog),
                ProjectText(observation.DemandId, catalog),
                ProjectText(observation.WorkType, catalog),
                ProjectText(observation.Sublot, catalog),
                ProjectText(observation.Area, catalog),
                ProjectText(observation.Eqp, catalog),
                ProjectText(observation.Step, catalog),
                ProjectTime(observation.MesSourceDate, catalog),
                ProjectText(observation.Package, catalog),
                ProjectTime(observation.ObservedAt, catalog),
                ProjectText(observation.MesSourceDateRaw, catalog)))
            .ToArray();
        var observationSummary = text.ObservationSummary(
            rawObservations.Length,
            trusted: liveMes is not null,
            conflicting: liveMes is null && rawObservations.Length > 1);

        var pollTrace = detail.LatestObservationPollTrace;
        return new WatchReadabilityAuditDetailPresentation(
            demand.DemandId,
            demand.ExternalReadabilityState,
            demand.LeadReadabilityBlocker,
            $"{demand.DemandId} · {demand.WorkType}",
            text.BusinessIdentity(
                ProjectText(demand.Sublot, catalog),
                ProjectText(demand.SeriesId, catalog),
                demand.Generation,
                ProjectTime(demand.DemandLastSeenAt, catalog)),
            text.DetailFacts(
                detail.SnapshotReference,
                identity.ProjectionCommittedAt,
                identity.ProjectionCommitId,
                identity.ProjectionSequence,
                identity.PollTraceId,
                identity.CatalogRevision,
                demand.LatestObservationPollTraceId,
                demand.LatestObservationProjectionCommitId),
            text.SeriesFacts(
                detail.Series.SeriesId,
                detail.Series.WorkType,
                detail.Series.Sublot,
                ProjectLifecycle(detail.Series.Lifecycle, catalog),
                ProjectPresence(detail.Series.CurrentPresence, catalog),
                detail.Series.CurrentDemandId,
                ProjectTime(detail.Series.StartedAt, catalog),
                detail.Series.ArchivedAt is null
                    ? catalog.Common.NotApplicable
                    : ProjectTime(detail.Series.ArchivedAt, catalog)),
            liveMes is null
                ? text.NoTrustedLiveMes
                : text.TrustedLiveMes(liveMes),
            observationSummary,
            detail.Blockers.Count == 0
                ? text.NoBlocker
                : string.Join(catalog.Common.ListSeparator, detail.Blockers
                    .OrderBy(blocker => blocker.Priority)
                    .Select(blocker => ProjectBlocker(blocker.Code, catalog))),
            liveMes,
            detail.QualificationChecks
                .Select(check => new WatchReadabilityQualificationCheckPresentation(
                    check.Code,
                    check.BlockingCode,
                    check.Result,
                    text.QualificationMeaning(
                        check.Code,
                        ReadabilityQualificationCheckCatalog.Definitions
                        .FirstOrDefault(definition => string.Equals(
                            definition.Code,
                            check.Code,
                            StringComparison.Ordinal))?.Meaning
                        ?? catalog.Columns.QualificationCheck),
                    text))
                .ToArray(),
            detail.Blockers
                .SelectMany(blocker => blocker.Evidence.Select((evidence, index) =>
                    new WatchReadabilityBlockerEvidencePresentation(
                        blocker.Code,
                        blocker.Priority,
                        index + 1,
                        evidence.SubjectKind,
                        ProjectText(evidence.ObservedValue, catalog),
                        evidence.ExpectedRule,
                        ProjectTime(evidence.ObservedAt, catalog),
                        evidence.PollTraceId,
                        evidence.ProjectionCommitId)))
                .ToArray(),
            rawObservations,
            text.PollTraceFacts(
                pollTrace.PollTraceId,
                pollTrace.QueryVersion,
                pollTrace.Outcome,
                ProjectTime(pollTrace.StartedAt, catalog),
                ProjectTime(pollTrace.CompletedAt, catalog),
                pollTrace.RowCount,
                pollTrace.ContentDigest,
                pollTrace.ProjectionCommitId,
                pollTrace.ProjectionSequence),
            detail.SnapshotReference,
            identity.ProjectionCommitId,
            identity.ProjectionSequence,
            identity.PollTraceId,
            identity.CatalogRevision);
    }

    private static bool HasSameSnapshotIdentity(
        ReadabilityAuditListSnapshot list,
        ReadabilityAuditDetailSnapshot detail) =>
        string.Equals(list.SnapshotReference, detail.SnapshotReference, StringComparison.Ordinal)
        && string.Equals(
            list.Snapshot.ProjectionCommitId,
            detail.Snapshot.ProjectionCommitId,
            StringComparison.Ordinal)
        && list.Snapshot.ProjectionSequence == detail.Snapshot.ProjectionSequence
        && list.Snapshot.ProjectionCommittedAt == detail.Snapshot.ProjectionCommittedAt
        && string.Equals(
            list.Snapshot.PollTraceId,
            detail.Snapshot.PollTraceId,
            StringComparison.Ordinal)
        && list.Snapshot.CatalogRevision == detail.Snapshot.CatalogRevision
        && string.Equals(
            list.Snapshot.ContractVersion,
            detail.Snapshot.ContractVersion,
            StringComparison.Ordinal);

    private static WatchReadabilityAuditRowPresentation ProjectRow(
        ReadabilityAuditListItemSnapshot item,
        WatchTextCatalog catalog) => new(
        item.DemandId,
        item.SeriesId,
        item.WorkType,
        item.Sublot,
        item.Generation,
        ProjectText(item.PredecessorDemandId, catalog),
        item.DemandStatus,
        item.SeriesLifecycle,
        item.SeriesCurrentPresence,
        item.IsCurrentGeneration,
        ProjectTime(item.DemandCreatedAt, catalog),
        ProjectTime(item.DemandLastSeenAt, catalog),
        item.GoneConfirmedAt is null
            ? catalog.Common.NotApplicable
            : ProjectTime(item.GoneConfirmedAt, catalog),
        ProjectLiveMesFields(item.LiveMesFields, catalog),
        item.LiveMesFields is null
            ? catalog.Common.SystemUnknown
            : ProjectSourceText(item.LiveMesFields.Area, catalog),
        item.CurrentRawObservationCount,
        item.ExternalReadabilityState,
        ProjectText(item.LeadReadabilityBlocker, catalog),
        item.ReadabilityBlockers,
        item.ReadabilityBlockers.Count == 0
            ? catalog.ReadabilityAudit.NoBlocker
            : string.Join('、', item.ReadabilityBlockers.Select(code => ProjectBlocker(code, catalog))),
        item.LatestObservationPollTraceId,
        item.LatestObservationProjectionCommitId,
        ProjectTime(item.LatestObservationAt, catalog),
        catalog.ReadabilityAudit.DemandIdLabel,
        catalog.ReadabilityAudit.WorkTypeLabel,
        catalog.ReadabilityAudit.SublotLabel,
        catalog.ReadabilityAudit.DescribeReadability(item.ExternalReadabilityState),
        string.IsNullOrWhiteSpace(item.LeadReadabilityBlocker)
            ? catalog.ReadabilityAudit.NoBlocker
            : catalog.ReadabilityAudit.DescribeBlocker(item.LeadReadabilityBlocker).Description,
        catalog.Columns);

    private static WatchReadabilityLiveMesFieldSetPresentation? ProjectLiveMesFields(
        LiveMesFieldSetSnapshot? fields,
        WatchTextCatalog catalog) => fields is null
        ? null
        : new WatchReadabilityLiveMesFieldSetPresentation(
            ProjectSourceText(fields.Area, catalog),
            ProjectSourceText(fields.Eqp, catalog),
            ProjectSourceText(fields.Step, catalog),
            fields.MesSourceDate is null || fields.MesSourceDate == default
                ? catalog.Common.SourceNotProvided
                : catalog.FormatAbsoluteTime(fields.MesSourceDate.Value),
            ProjectSourceText(fields.Package, catalog));

    private static string ProjectLifecycle(string rawCode, WatchTextCatalog catalog) =>
        catalog.Language == WatchDisplayLanguage.SimplifiedChinese
            ? catalog.DemandSeries.DescribeLifecycle(rawCode)
            : rawCode;

    private static string ProjectPresence(string rawCode, WatchTextCatalog catalog) =>
        catalog.Language == WatchDisplayLanguage.SimplifiedChinese
            ? catalog.DemandSeries.DescribePresence(rawCode)
            : rawCode;

    private static string ProjectBlocker(string rawCode, WatchTextCatalog catalog) =>
        catalog.Language == WatchDisplayLanguage.SimplifiedChinese
            ? catalog.ReadabilityAudit.CodeWithMeaning(catalog.ReadabilityAudit.DescribeBlocker(rawCode))
            : rawCode;

    private static string ProjectReadability(string rawCode, WatchTextCatalog catalog) =>
        catalog.Language == WatchDisplayLanguage.SimplifiedChinese
            ? catalog.ReadabilityAudit.CodeWithMeaning(catalog.ReadabilityAudit.DescribeReadabilityMeaning(rawCode))
            : rawCode;

    private static string ProjectSourceText(string? value, WatchTextCatalog catalog) =>
        string.IsNullOrWhiteSpace(value) ? catalog.Common.SourceNotProvided : value;

    private static string ProjectText(string? value, WatchTextCatalog catalog) =>
        string.IsNullOrWhiteSpace(value)
            ? catalog.Common.SourceNotProvided
            : value;

    private static string ProjectTime(DateTimeOffset? value, WatchTextCatalog catalog) =>
        value is null || value == default
            ? catalog.Common.SourceNotProvided
            : catalog.FormatAbsoluteTime(value.Value);
}
