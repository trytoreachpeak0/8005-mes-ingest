using System.Globalization;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal sealed record WatchErrorSearchCategoryFacetPresentation(
    string Category,
    long SeriesCount);

internal sealed record WatchErrorSearchActivityFacetPresentation(
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
        WatchErrorRawEvidenceState? rawEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(query);
        var normalizedQuery = query.NormalizeAndValidate();
        var view = workspace.ErrorSearch;
        var snapshot = view.Snapshot;
        var info = ProjectInfo(workspace, view);
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
                "尚无 Error Search Host 快照",
                ProjectClientAttempts(view),
                view.SelectionNotice,
                $"当前待查询条件：{ProjectFilter(normalizedQuery.Filter)} · {ProjectWindowSelection(normalizedQuery.Window)}",
                "Host 已提交条件：尚无快照",
                "UTC 半开窗口：尚无快照",
                "尚无错误历史快照",
                EmptyResultMessage: string.Empty,
                $"Host 固定排序：{ErrorSearchOrder.Default}",
                CanGoPrevious: false,
                CanGoNext: false,
                CategoryFacets: [],
                ActivityFacets: [],
                Rows: [],
                Detail: null,
                DetailStatus: ProjectDetailStatus(view, snapshotReference: null),
                RawEvidence: WatchErrorRawEvidencePresentation.Hidden);
        }

        var detail = ProjectDetail(snapshot, view.Detail);
        var displayPage = snapshot.TotalPages == 0 ? 0 : snapshot.PageNumber;
        return new WatchErrorSearchPresentation(
            HasSnapshot: true,
            view.IsRefreshing,
            view.IsStale,
            info.IsOpen,
            info.Severity,
            info.Title,
            info.Message,
            ProjectSnapshotFacts(snapshot.Snapshot),
            ProjectClientAttempts(view),
            view.SelectionNotice,
            $"当前待查询条件：{ProjectFilter(normalizedQuery.Filter)} · {ProjectWindowSelection(normalizedQuery.Window)}",
            $"Host 已提交条件：{ProjectFilter(snapshot.Filter)}",
            ProjectResolvedWindow(snapshot.Window),
            $"精确 {snapshot.TotalSeriesCount:N0} 个 DemandSeries · 第 {displayPage:N0} / {snapshot.TotalPages:N0} 页",
            snapshot.TotalSeriesCount == 0
                && !view.IsRefreshing
                && !view.IsStale
                && view.LastFailureAt is null
                    ? "查询成功；Host 在当前已提交条件下精确 0 个 DemandSeries 命中。"
                    : string.Empty,
            $"Host 固定排序：{snapshot.Order}",
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
                    value.SeriesCount))
                .ToArray(),
            snapshot.Items.Select(ProjectRow).ToArray(),
            detail,
            ProjectDetailStatus(view, snapshot.SnapshotReference),
            ProjectRawEvidence(snapshot, detail, rawEvidence ?? WatchErrorRawEvidenceState.Empty));
    }

    private static (bool IsOpen, WatchPresentationSeverity Severity, string Title, string Message)
        ProjectInfo(
            WatchV2WorkspaceState workspace,
            WatchV2ViewState<ErrorSearchListSnapshot, ErrorSearchDetailSnapshot> view)
    {
        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Failed)
        {
            return (
                true,
                WatchPresentationSeverity.Error,
                "无法连接 Host",
                $"新 Host 未通过契约连接；旧 Host 数据已清空。{FailureMessage(workspace.FailureCode, workspace.ErrorMessage, workspace.CorrelationId)}");
        }

        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Connecting)
        {
            return (
                true,
                WatchPresentationSeverity.Informational,
                "正在连接 Host",
                "连接成功后将读取一份冻结的 Error Search 快照。");
        }

        if (view.IsRefreshing)
        {
            var retained = view.Snapshot is null
                ? "等待 Host 返回冻结快照。"
                : $"刷新期间继续显示冻结快照 ErrorSearchAsOf {FormatUtc(view.Snapshot.Snapshot.ErrorSearchAsOf)}。";
            return (
                true,
                WatchPresentationSeverity.Informational,
                view.Snapshot is null ? "正在读取错误历史" : "正在刷新错误历史",
                retained);
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? "当前没有可显示的成功快照。"
                : $"已保留上次快照 ErrorSearchAsOf {FormatUtc(view.Snapshot.Snapshot.ErrorSearchAsOf)}；其 UTC 窗口、条件、精确分面和结果不会被失败请求改写。";
            return (
                true,
                view.Snapshot is null ? WatchPresentationSeverity.Error : WatchPresentationSeverity.Warning,
                view.Snapshot is null
                    ? "错误历史读取失败"
                    : "错误历史刷新失败，已保留上次快照",
                $"失败于 {WatchTimeDisplay.Format(failedAt)}。{retained}{FailureMessage(view.FailureCode, view.ErrorMessage, view.CorrelationId)}");
        }

        if (string.Equals(
                view.SelectionNotice,
                WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
                StringComparison.Ordinal))
        {
            return (
                true,
                WatchPresentationSeverity.Warning,
                "原选择已不在刷新结果中",
                "刷新成功，但原 DemandSeries 已不在当前冻结结果中；已清除详情，请重新选择。");
        }

        if (view.IsStale && view.Snapshot is not null)
        {
            return (
                true,
                WatchPresentationSeverity.Warning,
                "刷新已取消，保留上次错误历史",
                $"本次读取未提交；继续显示冻结快照 ErrorSearchAsOf {FormatUtc(view.Snapshot.Snapshot.ErrorSearchAsOf)}，其 UTC 窗口、条件、精确分面和结果保持不变。");
        }

        return (false, WatchPresentationSeverity.None, string.Empty, string.Empty);
    }

    private static WatchErrorSearchDetailStatusPresentation ProjectDetailStatus(
        WatchV2ViewState<ErrorSearchListSnapshot, ErrorSearchDetailSnapshot> view,
        string? snapshotReference)
    {
        if (string.IsNullOrWhiteSpace(view.SelectedId))
        {
            return new WatchErrorSearchDetailStatusPresentation(
                IsLoading: false,
                HasFailure: false,
                WatchPresentationSeverity.Informational,
                "尚未选择 DemandSeries",
                "选择一个 Series，读取与当前冻结错误检索快照一致的命中期间与证据。");
        }

        if (view.IsDetailLoading)
        {
            return new WatchErrorSearchDetailStatusPresentation(
                IsLoading: true,
                HasFailure: false,
                WatchPresentationSeverity.Informational,
                "正在读取同快照错误详情",
                $"正在读取 Series {view.SelectedId} 在冻结快照 {snapshotReference ?? "—"} 中真正命中的期间与证据。");
        }

        if (view.DetailLastFailureAt is { } failedAt)
        {
            var canceled = view.DetailFailureKind == WatchHostFailureKind.Canceled;
            return new WatchErrorSearchDetailStatusPresentation(
                IsLoading: false,
                HasFailure: true,
                canceled ? WatchPresentationSeverity.Warning : WatchPresentationSeverity.Error,
                canceled ? "错误详情读取已取消" : "错误详情读取失败",
                $"Series {view.SelectedId} 的详情未替换列表；冻结快照、ErrorSearchAsOf、条件与分面仍保留。"
                + $"失败于 {WatchTimeDisplay.Format(failedAt)}。"
                + FailureMessage(
                    view.DetailFailureCode,
                    view.DetailErrorMessage,
                    view.DetailCorrelationId));
        }

        return view.Detail is null
            ? new WatchErrorSearchDetailStatusPresentation(
                IsLoading: false,
                HasFailure: false,
                WatchPresentationSeverity.Informational,
                "错误详情尚不可用",
                $"Series {view.SelectedId} 已选择，但当前没有与冻结快照一致的详情。")
            : new WatchErrorSearchDetailStatusPresentation(
                IsLoading: false,
                HasFailure: false,
                WatchPresentationSeverity.None,
                "错误详情已读取",
                $"Series {view.SelectedId} 的详情与冻结快照 {snapshotReference ?? "—"} 一致。");
    }

    private static WatchErrorSearchRowPresentation ProjectRow(
        ErrorSearchListItemSnapshot item) => new(
        item.SeriesId,
        item.WorkType,
        item.Sublot,
        item.ActivityState,
        item.MatchedErrors.Select(value => new WatchErrorSearchMatchedErrorPresentation(
            value.Code,
            value.Category,
            value.Severity)).ToArray(),
        WatchTimeDisplay.Format(item.LatestMatchedEvidenceAt),
        item.MatchedPeriodCount,
        $"命中 {item.MatchedPeriodCount:N0} 个期间",
        item.MatchedDemandGenerationCount,
        item.MatchedDemandGenerationCount > 1
            ? $"跨 {item.MatchedDemandGenerationCount:N0} 个 Demand 世代"
            : $"命中 {item.MatchedDemandGenerationCount:N0} 个 Demand 世代",
        ProjectText(item.MesArea),
        item.MesAreaAvailability);

    private static WatchErrorSearchDetailPresentation? ProjectDetail(
        ErrorSearchListSnapshot list,
        ErrorSearchDetailSnapshot? detail)
    {
        if (detail is null || !HasSameSnapshot(list, detail))
        {
            return null;
        }

        var periods = detail.Periods.Select(ProjectPeriod).ToArray();
        var demandIds = detail.Periods
            .SelectMany(period => period.Evidence)
            .Select(evidence => evidence.DemandId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var generationSummary = demandIds.Length switch
        {
            0 => "未返回 Demand 世代证据",
            1 => $"Demand 世代：{demandIds[0]}",
            _ => $"跨 {demandIds.Length:N0} 个 Demand 世代：{string.Join('、', demandIds)}",
        };
        return new WatchErrorSearchDetailPresentation(
            $"{detail.Series.SeriesId} · {detail.Series.ActivityState}",
            $"快照 {detail.SnapshotReference} · {ProjectSnapshotFacts(detail.Snapshot)} · {ProjectResolvedWindow(detail.Window)} · Host 固定排序 {detail.Order}",
            demandIds,
            generationSummary,
            periods);
    }

    private static WatchErrorSearchPeriodPresentation ProjectPeriod(
        ErrorSearchDetailPeriodSnapshot period) => new(
        period.PeriodId,
        period.Code,
        period.Category,
        period.Severity,
        period.Target,
        period.SubjectKind,
        period.StartReason,
        WatchTimeDisplay.Format(period.StartedAt),
        ProjectTime(period.EndedAt),
        ProjectText(period.EndReason),
        period.StartsBeforeWindow,
        period.EndsAfterWindow,
        period.ActiveAtAsOf,
        ProjectBoundary(period),
        period.Evidence.Select(ProjectEvidence).ToArray());

    private static WatchErrorSearchEvidencePresentation ProjectEvidence(
        ErrorSearchDetailEvidenceSnapshot evidence)
    {
        var references = new List<string>();
        if (!string.IsNullOrWhiteSpace(evidence.DemandId))
        {
            references.Add($"DemandId {evidence.DemandId}");
        }
        if (evidence.RelatedWorkTypes.Count > 0)
        {
            references.Add($"WorkType {string.Join('、', evidence.RelatedWorkTypes)}");
        }

        return new WatchErrorSearchEvidencePresentation(
            evidence.EvidenceId,
            evidence.EvidenceKind,
            evidence.SubjectKind,
            WatchTimeDisplay.Format(evidence.ObservedAt),
            evidence.PollTraceId,
            evidence.ProjectionCommitId,
            evidence.DemandId,
            evidence.RelatedWorkTypes,
            references.Count == 0 ? "无 DemandId 或 WorkType 引用" : string.Join(" · ", references),
            ProjectDiagnostic(evidence),
            evidence.ExpectedRule,
            evidence.RawEvidenceAvailable);
    }

    private static string ProjectDiagnostic(ErrorSearchDetailEvidenceSnapshot evidence) =>
        evidence.DiagnosticValue.Kind switch
        {
            ErrorSearchDiagnosticValueKinds.Scalar =>
                ProjectText(evidence.DiagnosticValue.ScalarValue),
            ErrorSearchDiagnosticValueKinds.WorkTypeMembership =>
                evidence.RelatedWorkTypes.Count == 0
                    ? "WorkType —"
                    : $"WorkType {string.Join('、', evidence.RelatedWorkTypes)}",
            ErrorSearchDiagnosticValueKinds.RawObservationSet =>
                $"{evidence.DiagnosticValue.ObservationCount.GetValueOrDefault():N0} 条原始观测 · SHA-256 {ProjectText(evidence.DiagnosticValue.Sha256Digest)}",
            _ => evidence.DiagnosticValue.Kind,
        };

    private static WatchErrorRawEvidencePresentation ProjectRawEvidence(
        ErrorSearchListSnapshot list,
        WatchErrorSearchDetailPresentation? detail,
        WatchErrorRawEvidenceState state)
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
                "Host 原始证据响应不符合契约",
                "Host 返回内容超出受限原始证据契约；未呈现任何原始字段，错误详情仍保留。",
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
                WatchTimeDisplay.Format(item.ObservedAt),
                raw.IncludedFields.Select(field => new WatchErrorRawFieldPresentation(
                    field,
                    item.Fields.TryGetValue(field, out var value) ? ProjectText(value) : "—"))
                    .ToArray()))
                .ToArray();
        var hasFailure = state.LastFailureAt is not null;
        var severity = hasFailure
            ? raw is null ? WatchPresentationSeverity.Error : WatchPresentationSeverity.Warning
            : state.IsLoading
                ? WatchPresentationSeverity.Informational
                : WatchPresentationSeverity.Success;
        var title = hasFailure
            ? raw is null ? "原始证据读取失败" : "原始证据刷新失败，已保留上次结果"
            : state.IsLoading
                ? "正在读取受限原始证据"
                : "已读取受限原始证据";
        var message = hasFailure
            ? $"失败于 {WatchTimeDisplay.Format(state.LastFailureAt!.Value)}。错误详情未受影响。{FailureMessage(state.FailureCode, state.ErrorMessage, state.CorrelationId)}"
            : state.IsLoading
                ? raw is null
                    ? "仅在显式请求后读取；正在等待 Host 返回白名单字段。"
                    : "刷新期间继续显示上次受限原始证据。"
                : "仅显示 Host 授权并返回的敏感字段白名单。";
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
                : $"最多 {FormatNumber(raw.Limits.MaxItems)} 条 · 单条 {FormatNumber(raw.Limits.MaxItemBytes)} 字节 · 总计 {FormatNumber(raw.Limits.MaxTotalBytes)} 字节",
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

    private static bool HasSameSnapshot(
        ErrorSearchListSnapshot list,
        ErrorSearchDetailSnapshot detail) =>
        string.Equals(list.SnapshotReference, detail.SnapshotReference, StringComparison.Ordinal)
        && list.Snapshot == detail.Snapshot
        && list.Window == detail.Window
        && string.Equals(list.Order, detail.Order, StringComparison.Ordinal)
        && SameFilter(list.Filter, detail.Filter)
        && list.Items.Any(item => string.Equals(
            item.SeriesId,
            detail.Series.SeriesId,
            StringComparison.Ordinal));

    private static bool SameFilter(ErrorSearchFilter left, ErrorSearchFilter right) =>
        left.Categories.SequenceEqual(right.Categories, StringComparer.Ordinal)
        && left.ErrorCodes.SequenceEqual(right.ErrorCodes, StringComparer.Ordinal)
        && left.ActivityStates.SequenceEqual(right.ActivityStates, StringComparer.Ordinal)
        && string.Equals(left.SeriesId, right.SeriesId, StringComparison.Ordinal)
        && string.Equals(left.DemandId, right.DemandId, StringComparison.Ordinal)
        && string.Equals(left.SublotContains, right.SublotContains, StringComparison.Ordinal);

    private static string ProjectSnapshotFacts(ErrorSearchSnapshotIdentity identity) =>
        $"ErrorSearchAsOf {FormatUtc(identity.ErrorSearchAsOf)} · Host 投影提交 {WatchTimeDisplay.Format(identity.ProjectionCommittedAt)} · {identity.ProjectionCommitId} · 序列 {identity.ProjectionSequence:N0} · PollTrace {identity.PollTraceId}";

    private static string ProjectFilter(ErrorSearchFilter filter)
    {
        var normalized = filter.Normalize();
        var conditions = new List<string>();
        AddMany("分类", normalized.Categories);
        AddMany("错误码", normalized.ErrorCodes);
        AddMany("状态", normalized.ActivityStates);
        Add("SeriesId", normalized.SeriesId);
        Add("DemandId", normalized.DemandId);
        Add("SUBLOT 包含", normalized.SublotContains);
        return conditions.Count == 0
            ? "全部错误分类与 DemandSeries"
            : string.Join(" · ", conditions);

        void AddMany(string label, IReadOnlyList<string> values)
        {
            if (values.Count > 0)
            {
                conditions.Add($"{label} {string.Join('、', values)}");
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

    private static string ProjectWindowSelection(ErrorSearchWindowSelection window) =>
        window.Kind == ErrorSearchWindowKinds.Custom
            ? $"自定义 UTC [{ProjectUtcBoundary(window.FromUtc, "-∞")}, {ProjectUtcBoundary(window.ToUtc, "ErrorSearchAsOf")})"
            : window.Kind;

    private static string ProjectResolvedWindow(ErrorSearchResolvedWindow window) =>
        $"UTC 半开窗口 [{ProjectUtcBoundary(window.FromUtc, "-∞")}, {FormatUtc(window.ToUtc)})";

    private static string ProjectUtcBoundary(DateTimeOffset? value, string fallback) =>
        value is null ? fallback : FormatUtc(value.Value);

    private static string ProjectBoundary(ErrorSearchDetailPeriodSnapshot period)
    {
        var boundary = (period.StartsBeforeWindow, period.EndsAfterWindow) switch
        {
            (true, true) => "期间开始早于窗口，结束晚于窗口",
            (true, false) => "期间开始早于窗口",
            (false, true) => "期间延伸到窗口之后",
            _ => "期间边界位于窗口内",
        };
        return period.ActiveAtAsOf
            ? $"{boundary} · ErrorSearchAsOf 时仍为 ACTIVE"
            : boundary;
    }

    private static string ProjectClientAttempts(
        WatchV2ViewState<ErrorSearchListSnapshot, ErrorSearchDetailSnapshot> view)
    {
        var successful = view.LastSuccessfulAt is { } success
            ? $"Watch 最近成功 {WatchTimeDisplay.Format(success)}"
            : "Watch 尚无成功读取";
        return view.LastFailureAt is { } failure
            ? $"{successful} · 最近失败 {WatchTimeDisplay.Format(failure)}"
            : successful;
    }

    private static string FailureMessage(
        string? code,
        string? message,
        string? correlationId)
    {
        var parts = new[] { code, message }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var detail = string.Join(" · ", parts);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            detail = string.IsNullOrEmpty(detail)
                ? $"关联 ID {correlationId}"
                : $"{detail} · 关联 ID {correlationId}";
        }
        return string.IsNullOrEmpty(detail) ? string.Empty : $" {detail}。";
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string FormatNumber(int value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string ProjectText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string ProjectTime(DateTimeOffset? value) =>
        value is null ? "—" : WatchTimeDisplay.Format(value.Value);
}
