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
    string LatestObservationAt)
{
    public string LifecycleSummary =>
        $"Demand {DemandStatus} · Series {SeriesLifecycle} · {SeriesCurrentPresence}";
}

internal sealed record WatchReadabilityQualificationCheckPresentation(
    string Code,
    string BlockingCode,
    string Result,
    string Meaning);

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
    string Heading,
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
    long CatalogRevision);

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
        WatchAreaDisplayContext areaContext)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(areaContext);

        var localArea = areaContext.NormalizeAndValidate();
        var view = workspace.ReadabilityAudit;
        var snapshot = view.Snapshot;
        var info = ProjectInfo(workspace, view);
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
                "尚无 Host 资格审计快照",
                ProjectClientAttempts(view),
                view.SelectionNotice,
                "尚无资格审计快照",
                EmptyResultMessage: string.Empty,
                $"Host 固定排序：{ReadabilityAuditOrder.Default}",
                "Host 已提交条件：尚无快照",
                $"当前待查询条件：{ProjectFilter(query.Filter.Normalize())}",
                "Host 已提交范围：尚无快照",
                localArea.ProfileName,
                ProjectLocalAreaDetail(localArea),
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
            $"Host 投影提交 {WatchTimeDisplay.Format(snapshot.Snapshot.ProjectionCommittedAt)} · {snapshot.Snapshot.ProjectionCommitId} · 序列 {snapshot.Snapshot.ProjectionSequence:N0} · PollTrace {snapshot.Snapshot.PollTraceId} · CatalogRevision {snapshot.Snapshot.CatalogRevision:N0}",
            ProjectClientAttempts(view),
            view.SelectionNotice,
            $"精确 {snapshot.ExactTotalDemandCount:N0} 个 Demand 世代 · 第 {displayPageNumber:N0} / {snapshot.TotalPages:N0} 页",
            snapshot.ExactTotalDemandCount == 0
                ? "查询成功；Host 在当前已提交条件下精确 0 个 Demand 世代命中。"
                : string.Empty,
            $"Host 固定排序：{snapshot.Order}",
            $"Host 已提交条件：{ProjectFilter(snapshot.Filter.Normalize())}",
            $"当前待查询条件：{ProjectFilter(query.Filter.Normalize())}",
            ProjectHostAreas(committedAreas),
            localArea.ProfileName,
            ProjectLocalAreaDetail(localArea),
            !committedAreas.SequenceEqual(localArea.MesAreas, StringComparer.Ordinal),
            snapshot.TotalPages > 0 && snapshot.PageNumber > 1,
            snapshot.TotalPages > 0 && snapshot.PageNumber < snapshot.TotalPages,
            snapshot.Facets.ReadabilityStates
                .Select(facet => new WatchReadabilityStateFacetPresentation(facet.State, facet.DemandCount))
                .ToArray(),
            snapshot.Facets.Blockers
                .Select(facet => new WatchReadabilityBlockerFacetPresentation(facet.Code, facet.DemandCount))
                .ToArray(),
            snapshot.Items.Select(ProjectRow).ToArray(),
            ProjectDetail(snapshot, view.Detail));
    }

    private static (bool IsOpen, WatchPresentationSeverity Severity, string Title, string Message)
        ProjectInfo(
            WatchV2WorkspaceState workspace,
            WatchV2ViewState<ReadabilityAuditListSnapshot, ReadabilityAuditDetailSnapshot> view)
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
                "连接成功后将读取一份冻结的资格审计快照。");
        }

        if (view.IsRefreshing)
        {
            var retained = view.Snapshot is null
                ? "等待 Host 返回冻结快照。"
                : $"刷新期间继续显示 Host 快照 {WatchTimeDisplay.Format(view.Snapshot.Snapshot.ProjectionCommittedAt)}。";
            var priorFailure = view.LastFailureAt is { } priorFailedAt
                ? $" 上次失败于 {WatchTimeDisplay.Format(priorFailedAt)}；本次正在重试。"
                : string.Empty;
            return (
                true,
                WatchPresentationSeverity.Informational,
                view.Snapshot is null ? "正在读取资格审计" : "正在刷新资格审计",
                $"{retained}{priorFailure}");
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? "当前没有可显示的成功快照。"
                : $"继续显示 Host 快照 {WatchTimeDisplay.Format(view.Snapshot.Snapshot.ProjectionCommittedAt)}；其筛选、精确分面和 AREA 范围不会被失败查询改写。";
            return (
                true,
                view.Snapshot is null
                    ? WatchPresentationSeverity.Error
                    : WatchPresentationSeverity.Warning,
                view.Snapshot is null
                    ? "资格审计读取失败"
                    : "资格审计刷新失败，已保留上次快照",
                $"失败于 {WatchTimeDisplay.Format(failedAt)}。{retained}{FailureMessage(view.ErrorMessage, view.CorrelationId)}");
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
                "刷新成功，但原 Demand 世代已不在当前冻结结果中；已清除详情，请重新选择。");
        }

        return (false, WatchPresentationSeverity.None, string.Empty, string.Empty);
    }

    private static WatchReadabilityAuditDetailPresentation? ProjectDetail(
        ReadabilityAuditListSnapshot list,
        ReadabilityAuditDetailSnapshot? detail)
    {
        if (detail is null || !HasSameSnapshotIdentity(list, detail))
        {
            return null;
        }

        var demand = detail.Demand;
        var identity = detail.Snapshot;
        var liveMes = ProjectLiveMesFields(demand.LiveMesFields);
        var rawObservations = detail.LatestRawObservations
            .OrderBy(observation => observation.Ordinal)
            .Select(observation => new WatchReadabilityRawObservationPresentation(
                observation.Ordinal,
                observation.PollTraceId,
                observation.ProjectionCommitId,
                observation.Assignment.ToString(),
                ProjectText(observation.SeriesId),
                ProjectText(observation.DemandId),
                ProjectText(observation.WorkType),
                ProjectText(observation.Sublot),
                ProjectText(observation.Area),
                ProjectText(observation.Eqp),
                ProjectText(observation.Step),
                ProjectTime(observation.MesSourceDate),
                ProjectText(observation.Package),
                ProjectTime(observation.ObservedAt),
                ProjectText(observation.MesSourceDateRaw)))
            .ToArray();
        var observationSummary = liveMes is not null
            ? $"{rawObservations.Length:N0} 条原始观测 · 当前可信 LiveMesFieldSet 可用"
            : rawObservations.Length > 1
                ? $"{rawObservations.Length:N0} 条原始观测 · 无可信单值；保留原始观测冲突证据"
                : $"{rawObservations.Length:N0} 条原始观测 · 无可信 LiveMesFieldSet";

        var pollTrace = detail.LatestObservationPollTrace;
        return new WatchReadabilityAuditDetailPresentation(
            demand.DemandId,
            demand.ExternalReadabilityState,
            $"{demand.DemandId} · {demand.ExternalReadabilityState}",
            $"审计快照 {detail.SnapshotReference} · Host 投影提交 {WatchTimeDisplay.Format(identity.ProjectionCommittedAt)} · {identity.ProjectionCommitId} · 序列 {identity.ProjectionSequence:N0} · PollTrace {identity.PollTraceId} · CatalogRevision {identity.CatalogRevision:N0} · Demand 最新观测 PollTrace {demand.LatestObservationPollTraceId} · ProjectionCommit {demand.LatestObservationProjectionCommitId}",
            $"Series {detail.Series.SeriesId} · {detail.Series.WorkType} · SUBLOT {detail.Series.Sublot} · {detail.Series.Lifecycle} · {detail.Series.CurrentPresence} · 当前 Demand {detail.Series.CurrentDemandId} · 开始 {ProjectTime(detail.Series.StartedAt)} · 归档 {ProjectTime(detail.Series.ArchivedAt)}",
            liveMes is null
                ? "无可信 LiveMesFieldSet；请核对下方原始观测。"
                : $"可信 LiveMesFieldSet · AREA {liveMes.Area} · EQP {liveMes.Eqp} · STEP {liveMes.Step} · MesSourceDate {liveMes.MesSourceDate} · PACKAGE {liveMes.Package}",
            observationSummary,
            detail.Blockers.Count == 0
                ? "无"
                : string.Join('、', detail.Blockers
                    .OrderBy(blocker => blocker.Priority)
                    .Select(blocker => blocker.Code)),
            liveMes,
            detail.QualificationChecks
                .Select(check => new WatchReadabilityQualificationCheckPresentation(
                    check.Code,
                    check.BlockingCode,
                    check.Result,
                    ReadabilityQualificationCheckCatalog.Definitions
                        .FirstOrDefault(definition => string.Equals(
                            definition.Code,
                            check.Code,
                            StringComparison.Ordinal))?.Meaning
                        ?? "Host 资格检查"))
                .ToArray(),
            detail.Blockers
                .SelectMany(blocker => blocker.Evidence.Select((evidence, index) =>
                    new WatchReadabilityBlockerEvidencePresentation(
                        blocker.Code,
                        blocker.Priority,
                        index + 1,
                        evidence.SubjectKind,
                        ProjectText(evidence.ObservedValue),
                        evidence.ExpectedRule,
                        ProjectTime(evidence.ObservedAt),
                        evidence.PollTraceId,
                        evidence.ProjectionCommitId)))
                .ToArray(),
            rawObservations,
            $"PollTrace {pollTrace.PollTraceId} · {pollTrace.QueryVersion} · {pollTrace.Outcome} · {ProjectTime(pollTrace.StartedAt)} → {ProjectTime(pollTrace.CompletedAt)} · {pollTrace.RowCount:N0} 行 · Digest {pollTrace.ContentDigest} · ProjectionCommit {pollTrace.ProjectionCommitId} · 序列 {pollTrace.ProjectionSequence:N0}",
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
        ReadabilityAuditListItemSnapshot item) => new(
        item.DemandId,
        item.SeriesId,
        item.WorkType,
        item.Sublot,
        item.Generation,
        ProjectText(item.PredecessorDemandId),
        item.DemandStatus,
        item.SeriesLifecycle,
        item.SeriesCurrentPresence,
        item.IsCurrentGeneration,
        ProjectTime(item.DemandCreatedAt),
        ProjectTime(item.DemandLastSeenAt),
        ProjectTime(item.GoneConfirmedAt),
        ProjectLiveMesFields(item.LiveMesFields),
        ProjectText(item.LiveMesFields?.Area),
        item.CurrentRawObservationCount,
        item.ExternalReadabilityState,
        ProjectText(item.LeadReadabilityBlocker),
        item.ReadabilityBlockers,
        item.ReadabilityBlockers.Count == 0
            ? "无"
            : string.Join('、', item.ReadabilityBlockers),
        item.LatestObservationPollTraceId,
        item.LatestObservationProjectionCommitId,
        ProjectTime(item.LatestObservationAt));

    private static WatchReadabilityLiveMesFieldSetPresentation? ProjectLiveMesFields(
        LiveMesFieldSetSnapshot? fields) => fields is null
        ? null
        : new WatchReadabilityLiveMesFieldSetPresentation(
            ProjectText(fields.Area),
            ProjectText(fields.Eqp),
            ProjectText(fields.Step),
            ProjectTime(fields.MesSourceDate),
            ProjectText(fields.Package));

    private static string ProjectFilter(ReadabilityAuditFilter filter)
    {
        var conditions = new List<string>();
        AddMany("资格", filter.ReadabilityStates);
        AddMany("WorkType", filter.WorkTypes);
        AddMany("阻断", filter.Blockers);
        Add("DemandId", filter.DemandId);
        Add("SUBLOT 包含", filter.SublotContains);
        AddMany("AREA", filter.MesAreas);
        return conditions.Count == 0
            ? "全部 Demand 世代"
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

    private static string ProjectClientAttempts(
        WatchV2ViewState<ReadabilityAuditListSnapshot, ReadabilityAuditDetailSnapshot> view)
    {
        var successful = view.LastSuccessfulAt is { } lastSuccessfulAt
            ? $"Watch 最近成功 {WatchTimeDisplay.Format(lastSuccessfulAt)}"
            : "Watch 尚无成功读取";
        return view.LastFailureAt is { } lastFailureAt
            ? $"{successful} · 最近失败 {WatchTimeDisplay.Format(lastFailureAt)}"
            : successful;
    }

    private static string FailureMessage(string? message, string? correlationId)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}";
        return string.IsNullOrWhiteSpace(correlationId)
            ? detail
            : $"{detail} 关联 ID {correlationId}。";
    }

    private static string ProjectLocalAreaDetail(WatchAreaDisplayContext context) =>
        context.MesAreas.Count == 0
            ? $"{context.LocalState} · 未限制 Host AREA 查询"
            : $"{context.LocalState} · {string.Join('、', context.MesAreas)}";

    private static string ProjectHostAreas(IReadOnlyList<string> mesAreas) =>
        mesAreas.Count == 0
            ? "Host 已提交范围：全部 AREA"
            : $"Host 已提交范围：{string.Join('、', mesAreas)}";

    private static string ProjectText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string ProjectTime(DateTimeOffset? value) =>
        value is null || value == default
            ? "—"
            : WatchTimeDisplay.Format(value.Value);
}
