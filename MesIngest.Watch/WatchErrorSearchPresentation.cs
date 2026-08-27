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
                text.Pick("尚无 Error Search Host 快照", "No Error Search Host snapshot"),
                ProjectClientAttempts(view, catalog),
                view.SelectionNotice,
                text.Pick("当前待查询条件：", "Pending query: ") + ProjectFilter(normalizedQuery.Filter, catalog) + " · " + ProjectWindowSelection(normalizedQuery.Window, catalog),
                text.Pick("Host 已提交条件：尚无快照", "Host committed filters: no snapshot"),
                text.Pick("UTC 半开窗口：尚无快照", "UTC half-open window: no snapshot"),
                text.Pick("尚无错误历史快照", "No error-history snapshot"),
                EmptyResultMessage: string.Empty,
                text.Pick("Host 固定排序：", "Fixed Host order: ") + ErrorSearchOrder.Default,
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
            text.Pick("当前待查询条件：", "Pending query: ") + ProjectFilter(normalizedQuery.Filter, catalog) + " · " + ProjectWindowSelection(normalizedQuery.Window, catalog),
            text.Pick("Host 已提交条件：", "Host committed filters: ") + ProjectFilter(snapshot.Filter, catalog),
            ProjectResolvedWindow(snapshot.Window, catalog),
            text.Pick(
                $"精确 {snapshot.TotalSeriesCount:N0} 个 DemandSeries · 第 {displayPage:N0} / {snapshot.TotalPages:N0} 页",
                $"Exact {snapshot.TotalSeriesCount:N0} demand series · Page {displayPage:N0} of {snapshot.TotalPages:N0}"),
            snapshot.TotalSeriesCount == 0
                && !view.IsRefreshing
                && !view.IsStale
                && view.LastFailureAt is null
                    ? text.Pick("查询成功；Host 在当前已提交条件下精确 0 个 DemandSeries 命中。", "Query succeeded; exactly 0 demand series match the committed Host filters.")
                    : string.Empty,
            text.Pick("Host 固定排序：", "Fixed Host order: ") + snapshot.Order,
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
                text.Pick("无法连接 Host", "Unable to connect to Host"),
                text.Pick("新 Host 未通过契约连接；旧 Host 数据已清空。", "The new Host failed contract connection; old Host data was cleared. ") + FailureMessage(workspace.FailureCode, workspace.ErrorMessage, workspace.CorrelationId, catalog));
        }

        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Connecting)
        {
            return (
                true,
                WatchPresentationSeverity.Informational,
                text.Pick("正在连接 Host", "Connecting to Host"),
                text.Pick("连接成功后将读取一份冻结的 Error Search 快照。", "A frozen Error Search snapshot will be read after connection succeeds."));
        }

        if (view.IsRefreshing)
        {
            var retained = view.Snapshot is null
                ? text.Pick("等待 Host 返回冻结快照。", "Waiting for the Host to return a frozen snapshot.")
                : text.Pick("刷新期间继续显示冻结快照 ErrorSearchAsOf ", "The frozen snapshot remains visible during refresh: ErrorSearchAsOf ") + FormatUtc(view.Snapshot.Snapshot.ErrorSearchAsOf, catalog) + text.Pick("。", ".");
            return (
                true,
                WatchPresentationSeverity.Informational,
                view.Snapshot is null ? text.Pick("正在读取错误历史", "Loading error history") : text.Pick("正在刷新错误历史", "Refreshing error history"),
                retained);
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? text.Pick("当前没有可显示的成功快照。", "There is no successful snapshot to display.")
                : text.Pick("已保留上次快照 ErrorSearchAsOf ", "Retaining the previous snapshot ErrorSearchAsOf ") + FormatUtc(view.Snapshot.Snapshot.ErrorSearchAsOf, catalog) + text.Pick("；其 UTC 窗口、条件、精确分面和结果不会被失败请求改写。", "; its UTC window, filters, exact facets, and results were not replaced by the failed request.");
            return (
                true,
                view.Snapshot is null ? WatchPresentationSeverity.Error : WatchPresentationSeverity.Warning,
                view.Snapshot is null
                    ? text.Pick("错误历史读取失败", "Error-history read failed")
                    : text.Pick("错误历史刷新失败，已保留上次快照", "Error-history refresh failed; previous snapshot retained"),
                text.Pick("失败于 ", "Failed at ") + catalog.FormatAbsoluteTime(failedAt) + text.Pick("。", ". ") + retained + FailureMessage(view.FailureCode, view.ErrorMessage, view.CorrelationId, catalog));
        }

        if (string.Equals(
                view.SelectionNotice,
                WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
                StringComparison.Ordinal))
        {
            return (
                true,
                WatchPresentationSeverity.Warning,
                text.Pick("原选择已不在刷新结果中", "Previous selection is no longer in the refreshed results"),
                text.Pick("刷新成功，但原需求系列已不在当前冻结结果中；已清除详情，请重新选择。", "Refresh succeeded, but the previous demand series is no longer in the frozen results. Details were cleared; select another series."));
        }

        if (view.IsStale && view.Snapshot is not null)
        {
            return (
                true,
                WatchPresentationSeverity.Warning,
                text.Pick("刷新已取消，保留上次错误历史", "Refresh was canceled; previous error history retained"),
                text.Pick("本次读取未提交；继续显示冻结快照 ErrorSearchAsOf ", "This read was not committed; continuing to show frozen snapshot ErrorSearchAsOf ") + FormatUtc(view.Snapshot.Snapshot.ErrorSearchAsOf, catalog) + text.Pick("，其 UTC 窗口、条件、精确分面和结果保持不变。", "; its UTC window, filters, exact facets, and results remain unchanged."));
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
                text.Pick("尚未选择需求系列", "No demand series selected"),
                text.Pick("选择一个需求系列，读取与当前冻结错误检索快照一致的命中期间与证据。", "Select a demand series to load matched periods and evidence from the current frozen Error Search snapshot."));
        }

        if (view.IsDetailLoading)
        {
            return new WatchErrorSearchDetailStatusPresentation(
                IsLoading: true,
                HasFailure: false,
                WatchPresentationSeverity.Informational,
                text.Pick("正在读取同快照错误详情", "Loading same-snapshot error details"),
                text.Pick("正在读取需求系列 ", "Loading demand series ") + view.SelectedId + text.Pick(" 在冻结快照 ", " from frozen snapshot ") + (snapshotReference ?? catalog.Common.NotLoaded) + text.Pick(" 中真正命中的期间与证据。", ": actually matched periods and evidence."));
        }

        if (view.DetailLastFailureAt is { } failedAt)
        {
            var canceled = view.DetailFailureKind == WatchHostFailureKind.Canceled;
            return new WatchErrorSearchDetailStatusPresentation(
                IsLoading: false,
                HasFailure: true,
                canceled ? WatchPresentationSeverity.Warning : WatchPresentationSeverity.Error,
                canceled ? text.Pick("错误详情读取已取消", "Error-detail read canceled") : text.Pick("错误详情读取失败", "Error-detail read failed"),
                text.Pick("需求系列 ", "Demand series ") + view.SelectedId + text.Pick(" 的详情未替换列表；冻结快照、ErrorSearchAsOf、条件与分面仍保留。", " details did not replace the list; the frozen snapshot, ErrorSearchAsOf, filters, and facets remain.")
                + text.Pick("失败于 ", "Failed at ") + catalog.FormatAbsoluteTime(failedAt) + text.Pick("。", ". ")
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
                text.Pick("错误详情尚不可用", "Error details are not available"),
                text.Pick("需求系列 ", "Demand series ") + view.SelectedId + text.Pick(" 已选择，但当前没有与冻结快照一致的详情。", " is selected, but no details match the frozen snapshot."))
            : new WatchErrorSearchDetailStatusPresentation(
                IsLoading: false,
                HasFailure: false,
                WatchPresentationSeverity.None,
                text.Pick("错误详情已读取", "Error details loaded"),
                text.Pick("需求系列 ", "Demand series ") + view.SelectedId + text.Pick(" 的详情与冻结快照 ", " details match frozen snapshot ") + (snapshotReference ?? catalog.Common.NotLoaded) + text.Pick(" 一致。", "."));
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
        catalog.ErrorSearch.Pick($"命中 {item.MatchedPeriodCount:N0} 个期间", $"{item.MatchedPeriodCount:N0} matched periods"),
        item.MatchedDemandGenerationCount,
        item.MatchedDemandGenerationCount > 1
            ? catalog.ErrorSearch.Pick($"跨 {item.MatchedDemandGenerationCount:N0} 个需求代次", $"Across {item.MatchedDemandGenerationCount:N0} demand generations")
            : catalog.ErrorSearch.Pick($"命中 {item.MatchedDemandGenerationCount:N0} 个需求代次", $"{item.MatchedDemandGenerationCount:N0} matched demand generation"),
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
            0 => catalog.ErrorSearch.Pick("未返回需求代次证据", "No demand-generation evidence returned"),
            1 => catalog.ErrorSearch.Pick("需求代次：", "Demand generation: ") + demandIds[0],
            _ => catalog.ErrorSearch.Pick($"跨 {demandIds.Length:N0} 个需求代次：", $"Across {demandIds.Length:N0} demand generations: ") + string.Join(catalog.Language == WatchDisplayLanguage.SimplifiedChinese ? "、" : ", ", demandIds),
        };
        return new WatchErrorSearchDetailPresentation(
            $"{detail.Series.SeriesId} · {catalog.ErrorSearch.CodeWithMeaning(catalog.ErrorSearch.DescribeActivityState(detail.Series.ActivityState))}",
            catalog.ErrorSearch.Pick("快照 ", "Snapshot ") + detail.SnapshotReference + " · " + ProjectSnapshotFacts(detail.Snapshot, catalog) + " · " + ProjectResolvedWindow(detail.Window, catalog) + catalog.ErrorSearch.Pick(" · Host 固定排序 ", " · Fixed Host order ") + detail.Order,
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
            references.Add($"DemandId {evidence.DemandId}");
        }
        if (evidence.RelatedWorkTypes.Count > 0)
        {
            references.Add($"WorkType {string.Join(catalog.Language == WatchDisplayLanguage.SimplifiedChinese ? "、" : ", ", evidence.RelatedWorkTypes)}");
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
            references.Count == 0 ? catalog.ErrorSearch.Pick("无 DemandId 或 WorkType 引用", "No DemandId or WorkType reference") : string.Join(" · ", references),
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
                    ? $"WorkType {catalog.Common.SourceNotProvided}"
                    : $"WorkType {string.Join(catalog.Language == WatchDisplayLanguage.SimplifiedChinese ? "、" : ", ", evidence.RelatedWorkTypes)}",
            ErrorSearchDiagnosticValueKinds.RawObservationSet =>
                catalog.ErrorSearch.Pick($"{evidence.DiagnosticValue.ObservationCount.GetValueOrDefault():N0} 条原始观测 · SHA-256 ", $"{evidence.DiagnosticValue.ObservationCount.GetValueOrDefault():N0} raw observations · SHA-256 ") + ProjectText(evidence.DiagnosticValue.Sha256Digest, catalog),
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
                catalog.ErrorSearch.Pick("Host 原始证据响应不符合契约", "Host raw-evidence response violates the contract"),
                catalog.ErrorSearch.Pick("Host 返回内容超出受限原始证据契约；未呈现任何原始字段，错误详情仍保留。", "The Host response exceeded the restricted raw-evidence contract. No raw fields are shown, and the error details remain available."),
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
            ? raw is null ? catalog.ErrorSearch.Pick("原始证据读取失败", "Raw-evidence read failed") : catalog.ErrorSearch.Pick("原始证据刷新失败，已保留上次结果", "Raw-evidence refresh failed; previous results retained")
            : state.IsLoading
                ? catalog.ErrorSearch.Pick("正在读取受限原始证据", "Loading restricted raw evidence")
                : catalog.ErrorSearch.Pick("已读取受限原始证据", "Restricted raw evidence loaded");
        var message = hasFailure
            ? catalog.ErrorSearch.Pick("失败于 ", "Failed at ") + catalog.FormatAbsoluteTime(state.LastFailureAt!.Value) + catalog.ErrorSearch.Pick("。错误详情未受影响。", ". Error details were not affected. ") + FailureMessage(state.FailureCode, state.ErrorMessage, state.CorrelationId, catalog)
            : state.IsLoading
                ? raw is null
                    ? catalog.ErrorSearch.Pick("仅在显式请求后读取；正在等待 Host 返回白名单字段。", "Loaded only after an explicit request; waiting for the Host to return allow-listed fields.")
                    : catalog.ErrorSearch.Pick("刷新期间继续显示上次受限原始证据。", "Previous restricted raw evidence remains visible during refresh.")
                : catalog.ErrorSearch.Pick("仅显示 Host 授权并返回的敏感字段白名单。", "Only sensitive fields authorized and returned by the Host are shown.");
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
                : catalog.ErrorSearch.Pick("最多 ", "Maximum ") + FormatNumber(raw.Limits.MaxItems) + catalog.ErrorSearch.Pick(" 条 · 单条 ", " items · ") + FormatNumber(raw.Limits.MaxItemBytes) + catalog.ErrorSearch.Pick(" 字节/条 · 总计 ", " bytes/item · ") + FormatNumber(raw.Limits.MaxTotalBytes) + catalog.ErrorSearch.Pick(" 字节", " bytes total"),
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
        $"ErrorSearchAsOf {FormatUtc(identity.ErrorSearchAsOf, catalog)} · "
        + catalog.ErrorSearch.Pick("Host 投影提交 ", "Host projection committed ")
        + $"{catalog.FormatAbsoluteTime(identity.ProjectionCommittedAt)} · {identity.ProjectionCommitId} · "
        + catalog.ErrorSearch.Pick("序列 ", "sequence ")
        + $"{identity.ProjectionSequence:N0} · PollTrace {identity.PollTraceId}";

    private static string ProjectFilter(ErrorSearchFilter filter, WatchTextCatalog catalog)
    {
        var normalized = filter.Normalize();
        var conditions = new List<string>();
        AddMany(catalog.ErrorSearch.Pick("分类", "Categories"), normalized.Categories);
        AddMany(catalog.ErrorSearch.Pick("错误码", "Error codes"), normalized.ErrorCodes);
        AddMany(catalog.ErrorSearch.Pick("状态", "States"), normalized.ActivityStates);
        Add("SeriesId", normalized.SeriesId);
        Add("DemandId", normalized.DemandId);
        Add(catalog.ErrorSearch.Pick("SUBLOT 包含", "SUBLOT contains"), normalized.SublotContains);
        return conditions.Count == 0
            ? catalog.ErrorSearch.Pick("全部错误分类与需求系列", "All error categories and demand series")
            : string.Join(" · ", conditions);

        void AddMany(string label, IReadOnlyList<string> values)
        {
            if (values.Count > 0)
            {
                conditions.Add($"{label} {string.Join(catalog.Language == WatchDisplayLanguage.SimplifiedChinese ? "、" : ", ", values)}");
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

    private static string ProjectWindowSelection(ErrorSearchWindowSelection window, WatchTextCatalog catalog) =>
        window.Kind == ErrorSearchWindowKinds.Custom
            ? catalog.ErrorSearch.Pick("自定义 UTC ", "Custom UTC ") + $"[{ProjectUtcBoundary(window.FromUtc, "-∞", catalog)}, {ProjectUtcBoundary(window.ToUtc, "ErrorSearchAsOf", catalog)})"
            : catalog.ErrorSearch.CodeWithMeaning(new WatchCodeMeaning(catalog.ErrorSearch.WindowLabel(window.Kind), window.Kind, true));

    private static string ProjectResolvedWindow(ErrorSearchResolvedWindow window, WatchTextCatalog catalog) =>
        catalog.ErrorSearch.Pick("UTC 半开窗口 ", "UTC half-open window ") + $"[{ProjectUtcBoundary(window.FromUtc, "-∞", catalog)}, {FormatUtc(window.ToUtc, catalog)})";

    private static string ProjectUtcBoundary(DateTimeOffset? value, string fallback, WatchTextCatalog catalog) =>
        value is null ? fallback : FormatUtc(value.Value, catalog);

    private static string ProjectBoundary(ErrorSearchDetailPeriodSnapshot period, WatchTextCatalog catalog)
    {
        var boundary = (period.StartsBeforeWindow, period.EndsAfterWindow) switch
        {
            (true, true) => catalog.ErrorSearch.Pick("期间开始早于窗口，结束晚于窗口", "Period starts before and ends after the window"),
            (true, false) => catalog.ErrorSearch.Pick("期间开始早于窗口", "Period starts before the window"),
            (false, true) => catalog.ErrorSearch.Pick("期间延伸到窗口之后", "Period extends after the window"),
            _ => catalog.ErrorSearch.Pick("期间边界位于窗口内", "Period boundaries are within the window"),
        };
        return period.ActiveAtAsOf
            ? boundary + catalog.ErrorSearch.Pick(" · ErrorSearchAsOf 时仍为 ACTIVE", " · still ACTIVE at ErrorSearchAsOf")
            : boundary;
    }

    private static string ProjectClientAttempts(
        WatchV2ViewState<ErrorSearchListSnapshot, ErrorSearchDetailSnapshot> view,
        WatchTextCatalog catalog)
    {
        var successful = view.LastSuccessfulAt is { } success
            ? catalog.ErrorSearch.Pick("Watch 最近成功 ", "Watch last succeeded ") + catalog.FormatAbsoluteTime(success)
            : catalog.ErrorSearch.Pick("Watch 尚无成功读取", "Watch has no successful read");
        return view.LastFailureAt is { } failure
            ? successful + catalog.ErrorSearch.Pick(" · 最近失败 ", " · last failed ") + catalog.FormatAbsoluteTime(failure)
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
                ? catalog.ErrorSearch.Pick("关联 ID ", "Correlation ID ") + correlationId
                : detail + catalog.ErrorSearch.Pick(" · 关联 ID ", " · Correlation ID ") + correlationId;
        }
        return string.IsNullOrEmpty(detail) ? string.Empty : $" {detail}{catalog.ErrorSearch.Pick("。", ".")}";
    }

    private static string FormatUtc(DateTimeOffset value, WatchTextCatalog catalog) =>
        catalog.FormatAbsoluteTime(value.ToUniversalTime());

    private static string FormatNumber(int value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string ProjectText(string? value, WatchTextCatalog catalog) =>
        string.IsNullOrWhiteSpace(value) ? catalog.Common.SourceNotProvided : value;

    private static string ProjectTime(DateTimeOffset? value, WatchTextCatalog catalog) =>
        value is null ? catalog.Common.NotApplicable : catalog.FormatAbsoluteTime(value.Value);
}
