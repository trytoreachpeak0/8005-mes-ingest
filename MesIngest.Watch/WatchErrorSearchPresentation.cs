using System.Globalization;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal sealed record WatchErrorSearchCategoryFacetPresentation(
    string Category,
    long SeriesCount);

internal sealed record WatchErrorSearchActivityFacetPresentation(
    string RawState,
    string State,
    long SeriesCount);

internal sealed record WatchErrorSearchMatchedErrorPresentation(
    string Code,
    string Category,
    string Severity);

internal sealed record WatchErrorSearchRowPresentation(
    string SeriesId,
    string WorkType,
    string Sublot,
    string ActivityState,
    IReadOnlyList<WatchErrorSearchMatchedErrorPresentation> MatchedErrors,
    string MatchedErrorSummary,
    string LatestMatchedEvidenceAt,
    int MatchedPeriodCount,
    string MatchedPeriodSummary,
    int MatchedDemandGenerationCount,
    string MatchedDemandGenerationSummary,
    string MesArea,
    string MesAreaAvailability);

internal sealed record WatchErrorSearchEvidencePresentation(
    string EvidenceId,
    string EvidenceKind,
    string SubjectKind,
    string ObservedAt,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    IReadOnlyList<string> RelatedWorkTypes,
    string SubjectReference,
    string DiagnosticSummary,
    string ExpectedRule,
    bool CanReadRawEvidence);

internal sealed record WatchErrorSearchPeriodPresentation(
    string PeriodId,
    string Code,
    string Category,
    string Severity,
    string Target,
    string SubjectKind,
    string StartReason,
    string StartedAt,
    string EndedAt,
    string EndReason,
    bool StartsBeforeWindow,
    bool EndsAfterWindow,
    bool ActiveAtAsOf,
    string BoundarySummary,
    IReadOnlyList<WatchErrorSearchEvidencePresentation> Evidence);

internal sealed record WatchErrorSearchDetailPresentation(
    string Heading,
    string SnapshotFacts,
    IReadOnlyList<string> MatchedDemandIds,
    string GenerationSummary,
    IReadOnlyList<WatchErrorSearchPeriodPresentation> Periods);

internal sealed record WatchErrorSearchDetailStatusPresentation(
    bool IsLoading,
    bool HasFailure,
    WatchPresentationSeverity Severity,
    string Title,
    string Message);

internal sealed record WatchErrorRawFieldPresentation(string Name, string Value);

internal sealed record WatchErrorRawItemPresentation(
    int Ordinal,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    string ObservedAt,
    IReadOnlyList<WatchErrorRawFieldPresentation> Fields);

internal sealed record WatchErrorRawEvidencePresentation(
    bool IsVisible,
    bool IsLoading,
    bool IsStale,
    bool HasContractViolation,
    WatchPresentationSeverity StatusSeverity,
    string StatusTitle,
    string StatusMessage,
    string LimitsSummary,
    IReadOnlyList<string> IncludedFields,
    int ItemCount,
    int PayloadBytes,
    IReadOnlyList<WatchErrorRawItemPresentation> Items)
{
    public static WatchErrorRawEvidencePresentation Hidden { get; } = new(
        IsVisible: false,
        IsLoading: false,
        IsStale: false,
        HasContractViolation: false,
        WatchPresentationSeverity.None,
        string.Empty,
        string.Empty,
        string.Empty,
        [],
        ItemCount: 0,
        PayloadBytes: 0,
        Items: []);
}

internal sealed record WatchErrorSearchPresentation(
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
    string CurrentQuerySummary,
    string CommittedConditions,
    string CommittedWindow,
    string PageSummary,
    string EmptyResultMessage,
    string OrderSummary,
    bool CanGoPrevious,
    bool CanGoNext,
    IReadOnlyList<WatchErrorSearchCategoryFacetPresentation> CategoryFacets,
    IReadOnlyList<WatchErrorSearchActivityFacetPresentation> ActivityFacets,
    IReadOnlyList<WatchErrorSearchRowPresentation> Rows,
    WatchErrorSearchDetailPresentation? Detail,
    WatchErrorSearchDetailStatusPresentation DetailStatus,
    WatchErrorRawEvidencePresentation RawEvidence)
{
    public static WatchErrorSearchPresentation Project(
        WatchV2WorkspaceState workspace,
        ErrorSearchQuery query,
        WatchErrorRawEvidenceState? rawEvidence = null,
        WatchTextCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(query);
        var normalizedQuery = query.NormalizeAndValidate();
        catalog ??= WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese);
        var text = catalog.ErrorSearch;
        var view = workspace.ErrorSearch;
        var snapshot = view.Snapshot;
        var info = ProjectInfo(workspace, view, catalog);
        if (snapshot is null)
        {
            return new WatchErrorSearchPresentation(
                HasSnapshot: false,
                view.IsRefreshing,
                view.IsStale,
                info.IsOpen,
                info.Severity,
                info.Title,
                info.Message,
                text.Select(WatchGeneratedText.ErrorSearchPresentation105),
                ProjectClientAttempts(view, catalog),
                view.SelectionNotice,
                text.Select(WatchGeneratedText.ErrorSearchPresentation106) + ProjectFilter(normalizedQuery.Filter, catalog) + " · " + ProjectWindowSelection(normalizedQuery.Window, catalog),
                text.Select(WatchGeneratedText.ErrorSearchPresentation107),
                text.Select(WatchGeneratedText.ErrorSearchPresentation108),
                text.Select(WatchGeneratedText.ErrorSearchPresentation109),
                EmptyResultMessage: string.Empty,
                text.Select(WatchGeneratedText.ErrorSearchPresentation110) + text.OrderLabel(ErrorSearchOrder.Default),
                CanGoPrevious: false,
                CanGoNext: false,
                CategoryFacets: [],
                ActivityFacets: [],
                Rows: [],
                Detail: null,
                DetailStatus: ProjectDetailStatus(
                    view,
                    snapshotReference: null,
                    hasMatchingDetail: false,
                    catalog),
                RawEvidence: WatchErrorRawEvidencePresentation.Hidden);
        }

        var detail = ProjectDetail(snapshot, view.SelectedId, view.Detail, catalog);
        var displayPage = snapshot.TotalPages == 0 ? 0 : snapshot.PageNumber;
        return new WatchErrorSearchPresentation(
            HasSnapshot: true,
            view.IsRefreshing,
            view.IsStale,
            info.IsOpen,
            info.Severity,
            info.Title,
            info.Message,
            ProjectSnapshotFacts(snapshot.Snapshot, catalog),
            ProjectClientAttempts(view, catalog),
            view.SelectionNotice,
            text.Select(WatchGeneratedText.ErrorSearchPresentation106) + ProjectFilter(normalizedQuery.Filter, catalog) + " · " + ProjectWindowSelection(normalizedQuery.Window, catalog),
            text.Select(WatchGeneratedText.ErrorSearchPresentation111) + ProjectFilter(snapshot.Filter, catalog),
            ProjectResolvedWindow(snapshot.Window, catalog),
            text.Format(WatchGeneratedText.ErrorSearchPresentation112, new object?[] { snapshot.TotalSeriesCount, displayPage, snapshot.TotalPages }, new object?[] { snapshot.TotalSeriesCount, displayPage, snapshot.TotalPages }),
            snapshot.TotalSeriesCount == 0
                && !view.IsRefreshing
                && !view.IsStale
                && view.LastFailureAt is null
                    ? text.Select(WatchGeneratedText.ErrorSearchPresentation113)
                    : string.Empty,
            text.Select(WatchGeneratedText.ErrorSearchPresentation110) + text.OrderLabel(snapshot.Order),
            snapshot.TotalPages > 0 && snapshot.PageNumber > 1,
            snapshot.HasMore && snapshot.PageNumber < snapshot.TotalPages,
            snapshot.Facets.Categories
                .Select(value => new WatchErrorSearchCategoryFacetPresentation(
                    value.Category,
                    value.SeriesCount))
                .ToArray(),
            snapshot.Facets.ActivityStates
                .Select(value => new WatchErrorSearchActivityFacetPresentation(
                    value.State,
                    catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeActivityState(value.State)),
                    value.SeriesCount))
                .ToArray(),
            snapshot.Items.Select(item => ProjectRow(item, catalog)).ToArray(),
            detail,
            ProjectDetailStatus(
                view,
                snapshot.SnapshotReference,
                hasMatchingDetail: detail is not null,
                catalog),
            ProjectRawEvidence(snapshot, detail, rawEvidence ?? WatchErrorRawEvidenceState.Empty, catalog));
    }

    private static (bool IsOpen, WatchPresentationSeverity Severity, string Title, string Message)
        ProjectInfo(
            WatchV2WorkspaceState workspace,
            WatchV2ViewState<ErrorSearchListSnapshot, ErrorSearchDetailSnapshot> view,
            WatchTextCatalog catalog)
    {
        var text = catalog.ErrorSearch;
        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Failed)
        {
            return (
                true,
                WatchPresentationSeverity.Error,
                text.Select(WatchGeneratedText.ErrorSearchPresentation114),
                text.Select(WatchGeneratedText.ErrorSearchPresentation115) + FailureMessage(workspace.FailureCode, workspace.ErrorMessage, workspace.CorrelationId, catalog));
        }

        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Connecting)
        {
            return (
                true,
                WatchPresentationSeverity.Informational,
                text.Select(WatchGeneratedText.ErrorSearchPresentation116),
                text.Select(WatchGeneratedText.ErrorSearchPresentation117));
        }

        if (view.IsRefreshing)
        {
            var retained = view.Snapshot is null
                ? text.Select(WatchGeneratedText.ErrorSearchPresentation118)
                : text.Select(WatchGeneratedText.ErrorSearchPresentation119) + FormatUtc(view.Snapshot.Snapshot.ErrorSearchAsOf, catalog) + text.Select(WatchGeneratedText.ErrorSearchPresentation120);
            return (
                true,
                WatchPresentationSeverity.Informational,
                view.Snapshot is null ? text.Select(WatchGeneratedText.ErrorSearchPresentation121) : text.Select(WatchGeneratedText.ErrorSearchPresentation122),
                retained);
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? text.Select(WatchGeneratedText.ErrorSearchPresentation123)
                : text.Select(WatchGeneratedText.ErrorSearchPresentation124) + FormatUtc(view.Snapshot.Snapshot.ErrorSearchAsOf, catalog) + text.Select(WatchGeneratedText.ErrorSearchPresentation125);
            return (
                true,
                view.Snapshot is null ? WatchPresentationSeverity.Error : WatchPresentationSeverity.Warning,
                view.Snapshot is null
                    ? text.Select(WatchGeneratedText.ErrorSearchPresentation126)
                    : text.Select(WatchGeneratedText.ErrorSearchPresentation127),
                text.Select(WatchGeneratedText.ErrorSearchPresentation128) + catalog.FormatAbsoluteTime(failedAt) + text.Select(WatchGeneratedText.ErrorSearchPresentation129) + retained + FailureMessage(view.FailureCode, view.ErrorMessage, view.CorrelationId, catalog));
        }

        if (string.Equals(
                view.SelectionNotice,
                WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
                StringComparison.Ordinal))
        {
            return (
                true,
                WatchPresentationSeverity.Warning,
                text.Select(WatchGeneratedText.ErrorSearchPresentation130),
                text.Select(WatchGeneratedText.ErrorSearchPresentation131));
        }

        if (view.IsStale && view.Snapshot is not null)
        {
            return (
                true,
                WatchPresentationSeverity.Warning,
                text.Select(WatchGeneratedText.ErrorSearchPresentation132),
                text.Select(WatchGeneratedText.ErrorSearchPresentation133) + FormatUtc(view.Snapshot.Snapshot.ErrorSearchAsOf, catalog) + text.Select(WatchGeneratedText.ErrorSearchPresentation134));
        }

        return (false, WatchPresentationSeverity.None, string.Empty, string.Empty);
    }

    private static WatchErrorSearchDetailStatusPresentation ProjectDetailStatus(
        WatchV2ViewState<ErrorSearchListSnapshot, ErrorSearchDetailSnapshot> view,
        string? snapshotReference,
        bool hasMatchingDetail,
        WatchTextCatalog catalog)
    {
        var text = catalog.ErrorSearch;
        if (string.IsNullOrWhiteSpace(view.SelectedId))
        {
            return new WatchErrorSearchDetailStatusPresentation(
                IsLoading: false,
                HasFailure: false,
                WatchPresentationSeverity.Informational,
                text.Select(WatchGeneratedText.ErrorSearchPresentation135),
                text.Select(WatchGeneratedText.ErrorSearchPresentation136));
        }

        if (view.IsDetailLoading)
        {
            return new WatchErrorSearchDetailStatusPresentation(
                IsLoading: true,
                HasFailure: false,
                WatchPresentationSeverity.Informational,
                text.Select(WatchGeneratedText.ErrorSearchPresentation137),
                text.Select(WatchGeneratedText.ErrorSearchPresentation138) + view.SelectedId + text.Select(WatchGeneratedText.ErrorSearchPresentation139) + (snapshotReference ?? catalog.Common.NotLoaded) + text.Select(WatchGeneratedText.ErrorSearchPresentation140));
        }

        if (view.DetailLastFailureAt is { } failedAt)
        {
            var canceled = view.DetailFailureKind == WatchHostFailureKind.Canceled;
            return new WatchErrorSearchDetailStatusPresentation(
                IsLoading: false,
                HasFailure: true,
                canceled ? WatchPresentationSeverity.Warning : WatchPresentationSeverity.Error,
                canceled ? text.Select(WatchGeneratedText.ErrorSearchPresentation141) : text.Select(WatchGeneratedText.ErrorSearchPresentation142),
                text.Select(WatchGeneratedText.ErrorSearchPresentation143) + view.SelectedId + text.Select(WatchGeneratedText.ErrorSearchPresentation144)
                + text.Select(WatchGeneratedText.ErrorSearchPresentation128) + catalog.FormatAbsoluteTime(failedAt) + text.Select(WatchGeneratedText.ErrorSearchPresentation129)
                + FailureMessage(
                    view.DetailFailureCode,
                    view.DetailErrorMessage,
                    view.DetailCorrelationId,
                    catalog));
        }

        return !hasMatchingDetail
            ? new WatchErrorSearchDetailStatusPresentation(
                IsLoading: false,
                HasFailure: false,
                WatchPresentationSeverity.Informational,
                text.Select(WatchGeneratedText.ErrorSearchPresentation145),
                text.Select(WatchGeneratedText.ErrorSearchPresentation143) + view.SelectedId + text.Select(WatchGeneratedText.ErrorSearchPresentation146))
            : new WatchErrorSearchDetailStatusPresentation(
                IsLoading: false,
                HasFailure: false,
                WatchPresentationSeverity.None,
                text.Select(WatchGeneratedText.ErrorSearchPresentation147),
                text.Select(WatchGeneratedText.ErrorSearchPresentation143) + view.SelectedId + text.Select(WatchGeneratedText.ErrorSearchPresentation148) + (snapshotReference ?? catalog.Common.NotLoaded) + text.Select(WatchGeneratedText.ErrorSearchPresentation149));
    }

    private static WatchErrorSearchRowPresentation ProjectRow(
        ErrorSearchListItemSnapshot item,
        WatchTextCatalog catalog) => new(
        item.SeriesId,
        item.WorkType,
        item.Sublot,
        catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeActivityState(item.ActivityState)),
        item.MatchedErrors.Select(value => new WatchErrorSearchMatchedErrorPresentation(
            value.Code,
            value.Category,
            value.Severity)).ToArray(),
        string.Join("; ", item.MatchedErrors.Select(value =>
            catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeErrorCode(value.Code)))),
        catalog.FormatAbsoluteTime(item.LatestMatchedEvidenceAt),
        item.MatchedPeriodCount,
        catalog.ErrorSearch.Format(WatchGeneratedText.ErrorSearchPresentation150, new object?[] { item.MatchedPeriodCount }, new object?[] { item.MatchedPeriodCount }),
        item.MatchedDemandGenerationCount,
        item.MatchedDemandGenerationCount > 1
            ? catalog.ErrorSearch.Format(WatchGeneratedText.ErrorSearchPresentation151, new object?[] { item.MatchedDemandGenerationCount }, new object?[] { item.MatchedDemandGenerationCount })
            : catalog.ErrorSearch.Format(WatchGeneratedText.ErrorSearchPresentation152, new object?[] { item.MatchedDemandGenerationCount }, new object?[] { item.MatchedDemandGenerationCount }),
        ProjectText(item.MesArea, catalog),
        item.MesAreaAvailability);

    private static WatchErrorSearchDetailPresentation? ProjectDetail(
        ErrorSearchListSnapshot list,
        string? selectedId,
        ErrorSearchDetailSnapshot? detail,
        WatchTextCatalog catalog)
    {
        if (detail is null
            || !WatchErrorSearchDetailConsistency.Matches(list, selectedId, detail))
        {
            return null;
        }

        var periods = detail.Periods.Select(period => ProjectPeriod(period, catalog)).ToArray();
        var demandIds = detail.Periods
            .SelectMany(period => period.Evidence)
            .Select(evidence => evidence.DemandId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var generationSummary = demandIds.Length switch
        {
            0 => catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation153),
            1 => catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation154) + demandIds[0],
            _ => catalog.ErrorSearch.Format(WatchGeneratedText.ErrorSearchPresentation155, new object?[] { demandIds.Length }, new object?[] { demandIds.Length }) + string.Join(catalog.Common.ListSeparator, demandIds),
        };
        return new WatchErrorSearchDetailPresentation(
            $"{detail.Series.SeriesId} · {catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeActivityState(detail.Series.ActivityState))}",
            catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation156) + detail.SnapshotReference + " · " + ProjectSnapshotFacts(detail.Snapshot, catalog) + " · " + ProjectResolvedWindow(detail.Window, catalog) + catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation157) + detail.Order,
            demandIds,
            generationSummary,
            periods);
    }

    private static WatchErrorSearchPeriodPresentation ProjectPeriod(
        ErrorSearchDetailPeriodSnapshot period,
        WatchTextCatalog catalog) => new(
        period.PeriodId,
        catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeErrorCode(period.Code)),
        catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeCategory(period.Category)),
        period.Severity,
        period.Target,
        period.SubjectKind,
        period.StartReason,
        catalog.FormatAbsoluteTime(period.StartedAt),
        ProjectTime(period.EndedAt, catalog),
        ProjectText(period.EndReason, catalog),
        period.StartsBeforeWindow,
        period.EndsAfterWindow,
        period.ActiveAtAsOf,
        ProjectBoundary(period, catalog),
        period.Evidence.Select(evidence => ProjectEvidence(evidence, catalog)).ToArray());

    private static WatchErrorSearchEvidencePresentation ProjectEvidence(
        ErrorSearchDetailEvidenceSnapshot evidence,
        WatchTextCatalog catalog)
    {
        var references = new List<string>();
        if (!string.IsNullOrWhiteSpace(evidence.DemandId))
        {
            references.Add($"{catalog.Columns.DemandId} {evidence.DemandId}");
        }
        if (evidence.RelatedWorkTypes.Count > 0)
        {
            references.Add($"{catalog.Columns.WorkType} {string.Join(catalog.Common.ListSeparator, evidence.RelatedWorkTypes)}");
        }

        return new WatchErrorSearchEvidencePresentation(
            evidence.EvidenceId,
            evidence.EvidenceKind,
            evidence.SubjectKind,
            catalog.FormatAbsoluteTime(evidence.ObservedAt),
            evidence.PollTraceId,
            evidence.ProjectionCommitId,
            evidence.DemandId,
            evidence.RelatedWorkTypes,
            references.Count == 0 ? catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation158) : string.Join(" · ", references),
            ProjectDiagnostic(evidence, catalog),
            evidence.ExpectedRule,
            evidence.RawEvidenceAvailable);
    }

    private static string ProjectDiagnostic(ErrorSearchDetailEvidenceSnapshot evidence, WatchTextCatalog catalog) =>
        evidence.DiagnosticValue.Kind switch
        {
            ErrorSearchDiagnosticValueKinds.Scalar =>
                ProjectText(evidence.DiagnosticValue.ScalarValue, catalog),
            ErrorSearchDiagnosticValueKinds.WorkTypeMembership =>
                evidence.RelatedWorkTypes.Count == 0
                    ? $"{catalog.Columns.WorkType} {catalog.Common.SourceNotProvided}"
                    : $"{catalog.Columns.WorkType} {string.Join(catalog.Common.ListSeparator, evidence.RelatedWorkTypes)}",
            ErrorSearchDiagnosticValueKinds.RawObservationSet =>
                catalog.ErrorSearch.Format(WatchGeneratedText.ErrorSearchPresentation159, new object?[] { evidence.DiagnosticValue.ObservationCount.GetValueOrDefault() }, new object?[] { evidence.DiagnosticValue.ObservationCount.GetValueOrDefault() }) + ProjectText(evidence.DiagnosticValue.Sha256Digest, catalog),
            _ => evidence.DiagnosticValue.Kind,
        };

    private static WatchErrorRawEvidencePresentation ProjectRawEvidence(
        ErrorSearchListSnapshot list,
        WatchErrorSearchDetailPresentation? detail,
        WatchErrorRawEvidenceState state,
        WatchTextCatalog catalog)
    {
        if (detail is null
            || string.IsNullOrWhiteSpace(state.SnapshotReference)
            || string.IsNullOrWhiteSpace(state.SeriesId)
            || string.IsNullOrWhiteSpace(state.EvidenceId)
            || !string.Equals(state.SnapshotReference, list.SnapshotReference, StringComparison.Ordinal)
            || !string.Equals(state.SeriesId, list.Items.FirstOrDefault(item =>
                string.Equals(item.SeriesId, state.SeriesId, StringComparison.Ordinal))?.SeriesId,
                StringComparison.Ordinal)
            || !detail.Periods.SelectMany(period => period.Evidence).Any(evidence =>
                string.Equals(evidence.EvidenceId, state.EvidenceId, StringComparison.Ordinal)))
        {
            return WatchErrorRawEvidencePresentation.Hidden;
        }

        var raw = state.Snapshot;
        if (raw is not null && !IsBoundedRawEvidence(list, state, raw))
        {
            return new WatchErrorRawEvidencePresentation(
                IsVisible: true,
                state.IsLoading,
                IsStale: false,
                HasContractViolation: true,
                WatchPresentationSeverity.Error,
                catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation160),
                catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation161),
                string.Empty,
                [],
                ItemCount: 0,
                PayloadBytes: 0,
                Items: []);
        }

        var items = raw is null
            ? Array.Empty<WatchErrorRawItemPresentation>()
            : raw.Items.Select(item => new WatchErrorRawItemPresentation(
                item.Ordinal,
                item.PollTraceId,
                item.ProjectionCommitId,
                item.DemandId,
                catalog.FormatAbsoluteTime(item.ObservedAt),
                raw.IncludedFields.Select(field => new WatchErrorRawFieldPresentation(
                    field,
                    item.Fields.TryGetValue(field, out var value) ? ProjectText(value, catalog) : catalog.Common.SourceNotProvided))
                    .ToArray()))
                .ToArray();
        var hasFailure = state.LastFailureAt is not null;
        var severity = hasFailure
            ? raw is null ? WatchPresentationSeverity.Error : WatchPresentationSeverity.Warning
            : state.IsLoading
                ? WatchPresentationSeverity.Informational
                : WatchPresentationSeverity.Success;
        var title = hasFailure
            ? raw is null ? catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation162) : catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation163)
            : state.IsLoading
                ? catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation164)
                : catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation165);
        var message = hasFailure
            ? catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation128) + catalog.FormatAbsoluteTime(state.LastFailureAt!.Value) + catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation166) + FailureMessage(state.FailureCode, state.ErrorMessage, state.CorrelationId, catalog)
            : state.IsLoading
                ? raw is null
                    ? catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation167)
                    : catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation168)
                : catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation169);
        return new WatchErrorRawEvidencePresentation(
            IsVisible: true,
            state.IsLoading,
            IsStale: (state.IsLoading || hasFailure) && raw is not null,
            HasContractViolation: false,
            severity,
            title,
            message,
            raw is null
                ? string.Empty
                : catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation170) + FormatNumber(raw.Limits.MaxItems) + catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation171) + FormatNumber(raw.Limits.MaxItemBytes) + catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation172) + FormatNumber(raw.Limits.MaxTotalBytes) + catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation173),
            raw?.IncludedFields ?? [],
            raw?.ItemCount ?? 0,
            raw?.PayloadBytes ?? 0,
            items);
    }

    private static bool IsBoundedRawEvidence(
        ErrorSearchListSnapshot list,
        WatchErrorRawEvidenceState state,
        ErrorSearchRawEvidenceSnapshot raw)
    {
        var allowed = ErrorSearchRawEvidenceFields.All.ToHashSet(StringComparer.Ordinal);
        var included = raw.IncludedFields.ToHashSet(StringComparer.Ordinal);
        var measuredPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(raw).Length;
        var measuredItemsAreBounded = raw.Items.All(item =>
        {
            var measuredItemBytes = JsonSerializer.SerializeToUtf8Bytes(item).Length;
            return measuredItemBytes <= raw.Limits.MaxItemBytes
                && measuredItemBytes <= ErrorSearchRawEvidenceLimits.MaximumItemBytes;
        });
        return string.Equals(raw.SnapshotReference, list.SnapshotReference, StringComparison.Ordinal)
            && raw.Snapshot == list.Snapshot
            && string.Equals(raw.SeriesId, state.SeriesId, StringComparison.Ordinal)
            && string.Equals(raw.EvidenceId, state.EvidenceId, StringComparison.Ordinal)
            && raw.ItemCount == raw.Items.Count
            && raw.ItemCount >= 0
            && raw.ItemCount <= raw.Limits.MaxItems
            && raw.ItemCount <= ErrorSearchRawEvidenceLimits.MaximumItems
            && raw.Limits.MaxItems is >= 1 and <= ErrorSearchRawEvidenceLimits.MaximumItems
            && raw.Limits.MaxItemBytes is >= 1 and <= ErrorSearchRawEvidenceLimits.MaximumItemBytes
            && raw.Limits.MaxTotalBytes is >= 1 and <= ErrorSearchRawEvidenceLimits.MaximumTotalBytes
            && raw.PayloadBytes >= 0
            && raw.PayloadBytes <= raw.Limits.MaxTotalBytes
            && raw.PayloadBytes <= ErrorSearchRawEvidenceLimits.MaximumTotalBytes
            && raw.PayloadBytes == measuredPayloadBytes
            && raw.IncludedFields.Count == raw.IncludedFields.Distinct(StringComparer.Ordinal).Count()
            && raw.IncludedFields.All(allowed.Contains)
            && raw.Items.All(item => item.Fields.Keys.All(included.Contains))
            && measuredItemsAreBounded
            && measuredPayloadBytes <= raw.Limits.MaxTotalBytes
            && measuredPayloadBytes <= ErrorSearchRawEvidenceLimits.MaximumTotalBytes;
    }

    private static string ProjectSnapshotFacts(ErrorSearchSnapshotIdentity identity, WatchTextCatalog catalog) =>
        $"{catalog.Columns.ErrorSearchAsOf} {FormatUtc(identity.ErrorSearchAsOf, catalog)} · "
        + catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation174)
        + $"{catalog.FormatAbsoluteTime(identity.ProjectionCommittedAt)} · {identity.ProjectionCommitId} · "
        + catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation175)
        + $"{identity.ProjectionSequence:N0} · {catalog.Columns.PollTrace} {identity.PollTraceId}";

    private static string ProjectFilter(ErrorSearchFilter filter, WatchTextCatalog catalog)
    {
        var normalized = filter.Normalize();
        var conditions = new List<string>();
        AddMany(
            catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation176),
            normalized.Categories.Select(code => ProjectCategory(code, catalog)));
        AddMany(
            catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation177),
            normalized.ErrorCodes.Select(code => ProjectErrorCode(code, catalog)));
        AddMany(
            catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation178),
            normalized.ActivityStates.Select(code => ProjectActivityState(code, catalog)));
        Add(catalog.Columns.SeriesId, normalized.SeriesId);
        Add(catalog.Columns.DemandId, normalized.DemandId);
        Add(catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation179), normalized.SublotContains);
        return conditions.Count == 0
            ? catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation180)
            : string.Join(" · ", conditions);

        void AddMany(string label, IEnumerable<string> values)
        {
            var projectedValues = values.ToArray();
            if (projectedValues.Length > 0)
            {
                conditions.Add($"{label} {string.Join(catalog.Common.ListSeparator, projectedValues)}");
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

    private static string ProjectCategory(string rawCode, WatchTextCatalog catalog) =>
        catalog.Language == WatchDisplayLanguage.SimplifiedChinese
            ? catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeCategory(rawCode))
            : rawCode;

    private static string ProjectErrorCode(string rawCode, WatchTextCatalog catalog) =>
        catalog.Language == WatchDisplayLanguage.SimplifiedChinese
            ? catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeErrorCode(rawCode))
            : rawCode;

    private static string ProjectActivityState(string rawCode, WatchTextCatalog catalog) =>
        catalog.Language == WatchDisplayLanguage.SimplifiedChinese
            ? catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeActivityState(rawCode))
            : rawCode;

    private static string ProjectWindowSelection(ErrorSearchWindowSelection window, WatchTextCatalog catalog) =>
        window.Kind == ErrorSearchWindowKinds.Custom
            ? catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation181) + $"[{ProjectUtcBoundary(window.FromUtc, "-∞", catalog)}, {ProjectUtcBoundary(window.ToUtc, catalog.Columns.ErrorSearchAsOf, catalog)})"
            : catalog.ErrorSearch.CodeWithMeaning(new WatchCodeMeaning(catalog.ErrorSearch.WindowLabel(window.Kind), window.Kind, true));

    private static string ProjectResolvedWindow(ErrorSearchResolvedWindow window, WatchTextCatalog catalog) =>
        catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation182) + $"[{ProjectUtcBoundary(window.FromUtc, "-∞", catalog)}, {FormatUtc(window.ToUtc, catalog)})";

    private static string ProjectUtcBoundary(DateTimeOffset? value, string fallback, WatchTextCatalog catalog) =>
        value is null ? fallback : FormatUtc(value.Value, catalog);

    private static string ProjectBoundary(ErrorSearchDetailPeriodSnapshot period, WatchTextCatalog catalog)
    {
        var boundary = (period.StartsBeforeWindow, period.EndsAfterWindow) switch
        {
            (true, true) => catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation183),
            (true, false) => catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation184),
            (false, true) => catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation185),
            _ => catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation186),
        };
        return period.ActiveAtAsOf
            ? boundary + catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation187)
            : boundary;
    }

    private static string ProjectClientAttempts(
        WatchV2ViewState<ErrorSearchListSnapshot, ErrorSearchDetailSnapshot> view,
        WatchTextCatalog catalog)
    {
        var successful = view.LastSuccessfulAt is { } success
            ? catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation188) + catalog.FormatAbsoluteTime(success)
            : catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation189);
        return view.LastFailureAt is { } failure
            ? successful + catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation190) + catalog.FormatAbsoluteTime(failure)
            : successful;
    }

    private static string FailureMessage(
        string? code,
        string? message,
        string? correlationId,
        WatchTextCatalog catalog)
    {
        var parts = new[] { code, message }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var detail = string.Join(" · ", parts);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            detail = string.IsNullOrEmpty(detail)
                ? catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation191) + correlationId
                : detail + catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation192) + correlationId;
        }
        return string.IsNullOrEmpty(detail) ? string.Empty : $" {detail}{catalog.ErrorSearch.Select(WatchGeneratedText.ErrorSearchPresentation120)}";
    }

    private static string FormatUtc(DateTimeOffset value, WatchTextCatalog _) =>
        WatchTimeDisplay.Format(value, TimeZoneInfo.Utc);

    private static string FormatNumber(int value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string ProjectText(string? value, WatchTextCatalog catalog) =>
        string.IsNullOrWhiteSpace(value) ? catalog.Common.SourceNotProvided : value;

    private static string ProjectTime(DateTimeOffset? value, WatchTextCatalog catalog) =>
        value is null ? catalog.Common.NotApplicable : catalog.FormatAbsoluteTime(value.Value);
}
