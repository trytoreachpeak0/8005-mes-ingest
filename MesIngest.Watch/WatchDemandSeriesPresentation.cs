using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal enum WatchDemandSeriesSourceComparison
{
    None,
    TargetUnavailable,
    SameProjection,
    TargetNewer,
    TargetOlder,
    ProjectionIdentityMismatch,
}

internal sealed record WatchLiveMesFieldSetPresentation(
    string Area,
    string Eqp,
    string Step,
    string MesSourceDate,
    string Package);

internal sealed record WatchDemandSeriesRowPresentation(
    string SeriesId,
    string WorkType,
    string Sublot,
    string StartedAt,
    string ArchivedAt,
    string LifecycleAndPresence,
    string CurrentDemandId,
    int CurrentGeneration,
    string CurrentDemandStatus,
    string DemandLastSeenAt,
    string GoneConfirmedAt,
    long LastSeriesSequence,
    WatchLiveMesFieldSetPresentation? LiveMesFields,
    string ExternalReadabilityState,
    IReadOnlyList<string> ReadabilityBlockers,
    string Attention,
    string LatestPollTraceId,
    string LatestProjectionCommitId);

internal sealed record WatchDemandSeriesPresentation(
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
    WatchDemandSeriesSourceComparison SourceComparison,
    WatchPresentationSeverity SourceComparisonSeverity,
    string SourceSnapshotSummary,
    string SourceComparisonMessage,
    string PageSummary,
    string OrderSummary,
    string HostAreaScope,
    string LocalAreaHeading,
    string LocalAreaDetail,
    bool IsAreaScopeDifferent,
    bool CanGoPrevious,
    bool CanGoNext,
    IReadOnlyList<WatchDemandSeriesRowPresentation> Rows)
{
    public static WatchDemandSeriesPresentation Project(
        WatchV2WorkspaceState workspace,
        DemandSeriesBrowseQuery query,
        WatchAreaDisplayContext areaContext,
        WatchDemandSeriesNavigationContext? navigation,
        string? focusedDemandId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(areaContext);
        var localArea = areaContext.NormalizeAndValidate();

        var view = workspace.DemandSeries;
        var snapshot = view.Snapshot;
        var info = ProjectInfo(workspace, view);
        var source = ProjectSourceComparison(
            navigation,
            snapshot,
            view.Detail,
            focusedDemandId ?? navigation?.FocusedDemandId);
        if (snapshot is null)
        {
            return new WatchDemandSeriesPresentation(
                HasSnapshot: false,
                view.IsRefreshing,
                view.IsStale,
                info.IsOpen,
                info.Severity,
                info.Title,
                info.Message,
                "尚无 Host 业务快照",
                ProjectClientAttempts(view),
                view.SelectionNotice,
                source.Comparison,
                source.Severity,
                source.SourceSummary,
                source.Message,
                "尚无需求系列快照",
                "Host 固定排序：开始时间降序、SeriesId 升序",
                "Host 已提交范围：尚无快照",
                localArea.ProfileName,
                ProjectLocalAreaDetail(localArea),
                IsAreaScopeDifferent: false,
                CanGoPrevious: false,
                CanGoNext: false,
                []);
        }

        var displayPageNumber = snapshot.TotalPages == 0 ? 0 : snapshot.PageNumber;
        var committedAreas = snapshot.Filter.Normalize().MesAreas;

        return new WatchDemandSeriesPresentation(
            HasSnapshot: true,
            view.IsRefreshing,
            view.IsStale,
            info.IsOpen,
            info.Severity,
            info.Title,
            info.Message,
            $"Host 投影提交 {WatchTimeDisplay.Format(snapshot.Snapshot.ProjectionCommittedAt)} · {snapshot.Snapshot.ProjectionCommitId} · 序列 {snapshot.Snapshot.ProjectionSequence:N0} · PollTrace {snapshot.Snapshot.PollTraceId}",
            ProjectClientAttempts(view),
            view.SelectionNotice,
            source.Comparison,
            source.Severity,
            source.SourceSummary,
            source.Message,
            $"精确 {snapshot.ExactTotalCount:N0} 个 Series · 第 {displayPageNumber:N0} / {snapshot.TotalPages:N0} 页",
            "Host 固定排序：开始时间降序、SeriesId 升序",
            ProjectHostAreas(committedAreas),
            localArea.ProfileName,
            ProjectLocalAreaDetail(localArea),
            !committedAreas.SequenceEqual(localArea.MesAreas, StringComparer.Ordinal),
            snapshot.TotalPages > 0 && snapshot.PageNumber > 1,
            snapshot.TotalPages > 0 && snapshot.PageNumber < snapshot.TotalPages,
            snapshot.Items.Select(ProjectRow).ToArray());
    }

    private static (
        WatchDemandSeriesSourceComparison Comparison,
        WatchPresentationSeverity Severity,
        string SourceSummary,
        string Message) ProjectSourceComparison(
            WatchDemandSeriesNavigationContext? navigation,
            DemandSeriesListSnapshot? targetSnapshot,
            DemandSeriesDetailSnapshot? targetDetail,
            string? focusedDemandId)
    {
        if (navigation is null)
        {
            return (
                WatchDemandSeriesSourceComparison.None,
                WatchPresentationSeverity.None,
                string.Empty,
                string.Empty);
        }

        var sourceFacts = navigation.SourceFacts;
        var factSummary = sourceFacts is null
            ? "来源未提供对象级事实"
            : $"来源对象事实：{DescribeFacts(sourceFacts)}";
        var summary = $"来源 {navigation.SourceName} · 快照时点 {WatchTimeDisplay.Format(navigation.SourceSnapshotAsOf)} · Host 投影提交 {WatchTimeDisplay.Format(navigation.SourceProjectionCommittedAt)} · {navigation.SourceProjectionCommitId} · 序列 {navigation.SourceProjectionSequence:N0} · {factSummary}";
        if (targetSnapshot is null)
        {
            return (
                WatchDemandSeriesSourceComparison.TargetUnavailable,
                WatchPresentationSeverity.Informational,
                summary,
                "目标页尚无成功快照，暂时无法比较来源事实。");
        }

        var target = targetSnapshot.Snapshot;
        var factComparison = CompareFacts(
            sourceFacts,
            ResolveTargetFacts(navigation, targetSnapshot, targetDetail, focusedDemandId));
        if (target.ProjectionSequence > navigation.SourceProjectionSequence)
        {
            return (
                WatchDemandSeriesSourceComparison.TargetNewer,
                WatchPresentationSeverity.Informational,
                summary,
                $"目标页快照较来源更新；{factComparison} 以下内容以目标页快照为准。");
        }

        if (target.ProjectionSequence < navigation.SourceProjectionSequence)
        {
            return (
                WatchDemandSeriesSourceComparison.TargetOlder,
                WatchPresentationSeverity.Warning,
                summary,
                $"目标页快照早于来源快照；{factComparison} 请刷新后再核对事实。");
        }

        if (string.Equals(
                target.ProjectionCommitId,
                navigation.SourceProjectionCommitId,
                StringComparison.Ordinal))
        {
            return (
                WatchDemandSeriesSourceComparison.SameProjection,
                WatchPresentationSeverity.Success,
                summary,
                $"目标页与来源使用同一投影提交；{factComparison}");
        }

        return (
            WatchDemandSeriesSourceComparison.ProjectionIdentityMismatch,
            WatchPresentationSeverity.Error,
            summary,
            "目标页与来源序列相同但投影提交标识不同，无法安全解释快照关系。");
    }

    private static WatchDemandSeriesObjectFacts? ResolveTargetFacts(
        WatchDemandSeriesNavigationContext navigation,
        DemandSeriesListSnapshot targetSnapshot,
        DemandSeriesDetailSnapshot? targetDetail,
        string? focusedDemandId)
    {
        if (string.IsNullOrWhiteSpace(navigation.SeriesId))
        {
            return null;
        }

        if (targetDetail?.Series is { } series
            && string.Equals(series.SeriesId, navigation.SeriesId, StringComparison.Ordinal))
        {
            var demandId = focusedDemandId ?? navigation.FocusedDemandId;
            var demand = series.Demands.FirstOrDefault(value => string.Equals(
                    value.DemandId,
                    demandId,
                    StringComparison.Ordinal))
                ?? series.CurrentDemand;
            return new WatchDemandSeriesObjectFacts(
                series.SeriesId,
                demand.DemandId,
                series.WorkType,
                series.Sublot,
                demand.Generation,
                demand.Status,
                series.Lifecycle,
                series.CurrentPresence,
                demand.ExternalReadabilityState,
                demand.ReadabilityBlockers.ToArray());
        }

        var item = targetSnapshot.Items.FirstOrDefault(value => string.Equals(
            value.SeriesId,
            navigation.SeriesId,
            StringComparison.Ordinal));
        return item is null
            ? null
            : new WatchDemandSeriesObjectFacts(
                item.SeriesId,
                item.CurrentDemandId,
                item.WorkType,
                item.Sublot,
                item.CurrentGeneration,
                item.CurrentDemandStatus,
                item.Lifecycle,
                item.CurrentPresence,
                item.ExternalReadabilityState,
                item.ReadabilityBlockers.ToArray());
    }

    private static string CompareFacts(
        WatchDemandSeriesObjectFacts? source,
        WatchDemandSeriesObjectFacts? target)
    {
        if (source is null)
        {
            return "来源没有对象级事实，无法判定对象事实是否变化；";
        }

        if (target is null)
        {
            return "目标快照未包含该对象，无法判定对象事实是否变化；";
        }

        var compared = new List<string>();
        var differences = new List<string>();
        Compare("WorkType", source.WorkType, target.WorkType, compared, differences);
        Compare("SUBLOT", source.Sublot, target.Sublot, compared, differences);
        Compare("世代", source.Generation, target.Generation, compared, differences);
        Compare("Demand 状态", source.DemandStatus, target.DemandStatus, compared, differences);
        Compare("生命周期", source.Lifecycle, target.Lifecycle, compared, differences);
        Compare("当前出现", source.CurrentPresence, target.CurrentPresence, compared, differences);
        Compare(
            "外部可读",
            source.ExternalReadabilityState,
            target.ExternalReadabilityState,
            compared,
            differences);
        if (source.ReadabilityBlockers is not null)
        {
            var sourceBlockers = string.Join('、', source.ReadabilityBlockers.Order(StringComparer.Ordinal));
            var targetBlockers = string.Join(
                '、',
                (target.ReadabilityBlockers ?? []).Order(StringComparer.Ordinal));
            Compare("资格阻断", sourceBlockers, targetBlockers, compared, differences, emptyLabel: "无");
        }

        if (compared.Count == 0)
        {
            return "来源没有可与目标核对的对象字段，无法判定对象事实是否变化；";
        }

        return differences.Count == 0
            ? $"来源暴露的对象事实未变化（已核对 {string.Join('、', compared)}）。"
            : $"事实已变化：{string.Join("；", differences)}。";
    }

    private static void Compare<T>(
        string label,
        T? source,
        T? target,
        ICollection<string> compared,
        ICollection<string> differences,
        string emptyLabel = "—")
    {
        if (source is null)
        {
            return;
        }

        compared.Add(label);
        if (!EqualityComparer<T>.Default.Equals(source, target))
        {
            differences.Add($"{label} {Display(source, emptyLabel)} → {Display(target, emptyLabel)}");
        }
    }

    private static string Display<T>(T? value, string emptyLabel)
    {
        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? emptyLabel : text;
    }

    private static string DescribeFacts(WatchDemandSeriesObjectFacts facts)
    {
        var values = new List<string> { $"SeriesId {facts.SeriesId}" };
        Add("DemandId", facts.DemandId);
        Add("WorkType", facts.WorkType);
        Add("SUBLOT", facts.Sublot);
        Add("世代", facts.Generation);
        Add("Demand 状态", facts.DemandStatus);
        Add("生命周期", facts.Lifecycle);
        Add("当前出现", facts.CurrentPresence);
        Add("外部可读", facts.ExternalReadabilityState);
        if (facts.ReadabilityBlockers is not null)
        {
            values.Add(facts.ReadabilityBlockers.Count == 0
                ? "资格阻断 无"
                : $"资格阻断 {string.Join('、', facts.ReadabilityBlockers)}");
        }

        return string.Join("；", values);

        void Add<T>(string label, T? value)
        {
            if (value is not null && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                values.Add($"{label} {value}");
            }
        }
    }

    private static (bool IsOpen, WatchPresentationSeverity Severity, string Title, string Message)
        ProjectInfo(
            WatchV2WorkspaceState workspace,
            WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot> view)
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
                "连接成功后将读取一份冻结的需求系列快照。");
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
                view.Snapshot is null ? "正在读取需求系列" : "正在刷新需求系列",
                $"{retained}{priorFailure}");
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? "当前没有可显示的成功快照。"
                : $"继续显示 Host 快照 {WatchTimeDisplay.Format(view.Snapshot.Snapshot.ProjectionCommittedAt)}；其筛选与 AREA 范围不会被失败查询改写。";
            return (
                true,
                view.Snapshot is null
                    ? WatchPresentationSeverity.Error
                    : WatchPresentationSeverity.Warning,
                view.Snapshot is null
                    ? "需求系列读取失败"
                    : "需求系列刷新失败，已保留上次快照",
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
                "刷新成功，但原 Series 已不在当前冻结结果中；已清除详情，请重新选择。");
        }

        return (false, WatchPresentationSeverity.None, string.Empty, string.Empty);
    }

    private static string ProjectClientAttempts(
        WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot> view)
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

    private static WatchDemandSeriesRowPresentation ProjectRow(
        DemandSeriesListItemSnapshot item) => new(
        item.SeriesId,
        item.WorkType,
        item.Sublot,
        ProjectTime(item.StartedAt),
        ProjectTime(item.ArchivedAt),
        $"{item.Lifecycle} · {item.CurrentPresence}",
        item.CurrentDemandId,
        item.CurrentGeneration,
        item.CurrentDemandStatus,
        ProjectTime(item.DemandLastSeenAt),
        ProjectTime(item.GoneConfirmedAt),
        item.LastSeriesSequence,
        ProjectLiveMesFields(item.LiveMesFields),
        item.ExternalReadabilityState,
        item.ReadabilityBlockers,
        item.ReadabilityBlockers.Count == 0
            ? item.ExternalReadabilityState
            : string.Join('、', item.ReadabilityBlockers),
        item.LatestPollTraceId,
        item.LatestProjectionCommitId);

    private static WatchLiveMesFieldSetPresentation? ProjectLiveMesFields(
        LiveMesFieldSetSnapshot? fields) => fields is null
        ? null
        : new WatchLiveMesFieldSetPresentation(
            ProjectText(fields.Area),
            ProjectText(fields.Eqp),
            ProjectText(fields.Step),
            ProjectTime(fields.MesSourceDate),
            ProjectText(fields.Package));

    private static string ProjectText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string ProjectTime(DateTimeOffset? value) =>
        value is null || value == default
            ? "—"
            : WatchTimeDisplay.Format(value.Value);

    private static string ProjectLocalAreaDetail(WatchAreaDisplayContext context) =>
        context.MesAreas.Count == 0
            ? $"{context.LocalState} · 未限制 Host AREA 查询"
            : $"{context.LocalState} · {string.Join('、', context.MesAreas)}";

    private static string ProjectHostAreas(IReadOnlyList<string> mesAreas) =>
        mesAreas.Count == 0
            ? "Host 已提交范围：全部 AREA"
            : $"Host 已提交范围：{string.Join('、', mesAreas)}";
}
