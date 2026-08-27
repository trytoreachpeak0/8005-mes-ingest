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
        string? focusedDemandId,
        WatchDemandSeriesText? text = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(areaContext);
        text ??= WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).DemandSeries;
        var catalog = WatchTextCatalog.For(text.DisplayLanguage);
        var localArea = areaContext.NormalizeAndValidate();

        var view = workspace.DemandSeries;
        var snapshot = view.Snapshot;
        var info = ProjectInfo(workspace, view, text);
        var source = ProjectSourceComparison(
            navigation,
            snapshot,
            view.Detail,
            focusedDemandId ?? navigation?.FocusedDemandId,
            text);
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
                text.NoBusinessSnapshot,
                ProjectClientAttempts(view, text),
                view.SelectionNotice,
                source.Comparison,
                source.Severity,
                source.SourceSummary,
                source.Message,
                text.NoSnapshot,
                text.FixedOrder,
                text.NoHostScope,
                localArea.ProfileName,
                ProjectLocalAreaDetail(localArea, text),
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
            text.FormatSnapshotFacts(
                WatchTimeDisplay.Format(snapshot.Snapshot.ProjectionCommittedAt),
                snapshot.Snapshot.ProjectionCommitId,
                snapshot.Snapshot.ProjectionSequence,
                snapshot.Snapshot.PollTraceId),
            ProjectClientAttempts(view, text),
            view.SelectionNotice,
            source.Comparison,
            source.Severity,
            source.SourceSummary,
            source.Message,
            text.FormatPageSummary(snapshot.ExactTotalCount, displayPageNumber, snapshot.TotalPages),
            text.FixedOrder,
            text.FormatHostAreas(committedAreas),
            localArea.ProfileName,
            ProjectLocalAreaDetail(localArea, text),
            !committedAreas.SequenceEqual(localArea.MesAreas, StringComparer.Ordinal),
            snapshot.TotalPages > 0 && snapshot.PageNumber > 1,
            snapshot.TotalPages > 0 && snapshot.PageNumber < snapshot.TotalPages,
            snapshot.Items.Select(item => ProjectRow(item, catalog)).ToArray());
    }

    private static (
        WatchDemandSeriesSourceComparison Comparison,
        WatchPresentationSeverity Severity,
        string SourceSummary,
        string Message) ProjectSourceComparison(
            WatchDemandSeriesNavigationContext? navigation,
            DemandSeriesListSnapshot? targetSnapshot,
            DemandSeriesDetailSnapshot? targetDetail,
            string? focusedDemandId,
            WatchDemandSeriesText text)
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
            ? text.SourceNoFacts
            : text.FormatSourceFacts(DescribeFacts(sourceFacts, text));
        var summary = text.FormatSourceSummary(
            text.DescribeSource(navigation.SourceName),
            WatchTimeDisplay.Format(navigation.SourceSnapshotAsOf),
            WatchTimeDisplay.Format(navigation.SourceProjectionCommittedAt),
            navigation.SourceProjectionCommitId,
            navigation.SourceProjectionSequence,
            factSummary);
        if (targetSnapshot is null)
        {
            return (
                WatchDemandSeriesSourceComparison.TargetUnavailable,
                WatchPresentationSeverity.Informational,
                summary,
                text.TargetUnavailable);
        }

        var target = targetSnapshot.Snapshot;
        var factComparison = CompareFacts(
            sourceFacts,
            ResolveTargetFacts(navigation, targetSnapshot, targetDetail, focusedDemandId),
            text);
        if (target.ProjectionSequence > navigation.SourceProjectionSequence)
        {
            return (
                WatchDemandSeriesSourceComparison.TargetNewer,
                WatchPresentationSeverity.Informational,
                summary,
                text.FormatTargetNewer(factComparison));
        }

        if (target.ProjectionSequence < navigation.SourceProjectionSequence)
        {
            return (
                WatchDemandSeriesSourceComparison.TargetOlder,
                WatchPresentationSeverity.Warning,
                summary,
                text.FormatTargetOlder(factComparison));
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
                text.FormatSameProjection(factComparison));
        }

        return (
            WatchDemandSeriesSourceComparison.ProjectionIdentityMismatch,
            WatchPresentationSeverity.Error,
            summary,
            text.ProjectionMismatch);
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
        WatchDemandSeriesObjectFacts? target,
        WatchDemandSeriesText text)
    {
        if (source is null)
        {
            return text.SourceCannotCompare;
        }

        if (target is null)
        {
            return text.TargetCannotCompare;
        }

        var compared = new List<string>();
        var differences = new List<string>();
        Compare("WorkType", source.WorkType, target.WorkType, compared, differences);
        Compare("SUBLOT", source.Sublot, target.Sublot, compared, differences);
        Compare(text.GenerationLabel, source.Generation, target.Generation, compared, differences);
        Compare(text.DemandStateLabel, source.DemandStatus, target.DemandStatus, compared, differences);
        Compare(text.LifecycleLabel, source.Lifecycle, target.Lifecycle, compared, differences);
        Compare(text.PresenceLabel, source.CurrentPresence, target.CurrentPresence, compared, differences);
        Compare(
            text.ReadabilityLabel,
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
            Compare(text.BlockersLabel, sourceBlockers, targetBlockers, compared, differences, emptyLabel: text.None);
        }

        if (compared.Count == 0)
        {
            return text.NoComparableFields;
        }

        return differences.Count == 0
            ? text.FormatFactsUnchanged(string.Join('、', compared))
            : text.FormatFactsChanged(string.Join("；", differences));
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

    private static string DescribeFacts(
        WatchDemandSeriesObjectFacts facts,
        WatchDemandSeriesText text)
    {
        var values = new List<string> { $"SeriesId {facts.SeriesId}" };
        Add("DemandId", facts.DemandId);
        Add("WorkType", facts.WorkType);
        Add("SUBLOT", facts.Sublot);
        Add(text.GenerationLabel, facts.Generation);
        Add(text.DemandStateLabel, facts.DemandStatus);
        Add(text.LifecycleLabel, facts.Lifecycle);
        Add(text.PresenceLabel, facts.CurrentPresence);
        Add(text.ReadabilityLabel, facts.ExternalReadabilityState);
        if (facts.ReadabilityBlockers is not null)
        {
            values.Add(facts.ReadabilityBlockers.Count == 0
                ? $"{text.BlockersLabel} {text.None}"
                : $"{text.BlockersLabel} {string.Join('、', facts.ReadabilityBlockers)}");
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
            WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot> view,
            WatchDemandSeriesText text)
    {
        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Failed)
        {
            return (
                true,
                WatchPresentationSeverity.Error,
                text.HostFailedTitle,
                text.FormatHostFailed(FailureMessage(workspace.ErrorMessage, workspace.CorrelationId, text)));
        }

        if (workspace.ConnectionStatus == WatchHostConnectionStatus.Connecting)
        {
            return (
                true,
                WatchPresentationSeverity.Informational,
                text.ConnectingTitle,
                text.ConnectingMessage);
        }

        if (view.IsRefreshing)
        {
            var retained = view.Snapshot is null
                ? text.WaitingSnapshot
                : text.FormatRetainedDuringRefresh(WatchTimeDisplay.Format(view.Snapshot.Snapshot.ProjectionCommittedAt));
            var priorFailure = view.LastFailureAt is { } priorFailedAt
                ? text.FormatPriorFailureRetry(WatchTimeDisplay.Format(priorFailedAt))
                : string.Empty;
            return (
                true,
                WatchPresentationSeverity.Informational,
                view.Snapshot is null ? text.LoadingTitle : text.RefreshingTitle,
                $"{retained}{priorFailure}");
        }

        if (view.LastFailureAt is { } failedAt)
        {
            var retained = view.Snapshot is null
                ? text.NoSuccessfulSnapshot
                : text.FormatRetainedAfterFailure(WatchTimeDisplay.Format(view.Snapshot.Snapshot.ProjectionCommittedAt));
            return (
                true,
                view.Snapshot is null
                    ? WatchPresentationSeverity.Error
                    : WatchPresentationSeverity.Warning,
                view.Snapshot is null
                    ? text.ReadFailedTitle
                    : text.RefreshFailedTitle,
                text.FormatFailedAt(
                    WatchTimeDisplay.Format(failedAt),
                    retained,
                    FailureMessage(view.ErrorMessage, view.CorrelationId, text)));
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

    private static string ProjectClientAttempts(
        WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot> view,
        WatchDemandSeriesText text)
    {
        var successful = view.LastSuccessfulAt is { } lastSuccessfulAt
            ? text.FormatWatchSuccess(WatchTimeDisplay.Format(lastSuccessfulAt))
            : text.WatchNoSuccess;
        return view.LastFailureAt is { } lastFailureAt
            ? text.FormatWatchFailure(successful, WatchTimeDisplay.Format(lastFailureAt))
            : successful;
    }

    private static string FailureMessage(
        string? message,
        string? correlationId,
        WatchDemandSeriesText text)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}";
        return string.IsNullOrWhiteSpace(correlationId)
            ? detail
            : $"{detail}{text.FormatFailureCorrelation(correlationId)}";
    }

    private static WatchDemandSeriesRowPresentation ProjectRow(
        DemandSeriesListItemSnapshot item,
        WatchTextCatalog catalog)
    {
        var text = catalog.DemandSeries;
        return new(
        item.SeriesId,
        item.WorkType,
        item.Sublot,
        ProjectTime(item.StartedAt, catalog),
        item.ArchivedAt is null
            ? catalog.Common.NotApplicable
            : ProjectTime(item.ArchivedAt, catalog),
        $"{text.DescribeLifecycle(item.Lifecycle)} · {text.DescribePresence(item.CurrentPresence)}",
        item.CurrentDemandId,
        item.CurrentGeneration,
        item.CurrentDemandStatus,
        ProjectTime(item.DemandLastSeenAt, catalog),
        item.GoneConfirmedAt is null
            ? catalog.Common.NotApplicable
            : ProjectTime(item.GoneConfirmedAt, catalog),
        item.LastSeriesSequence,
        ProjectLiveMesFields(item.LiveMesFields, catalog),
        item.ExternalReadabilityState,
        item.ReadabilityBlockers,
        item.ReadabilityBlockers.Count == 0
            ? item.ExternalReadabilityState
            : string.Join('、', item.ReadabilityBlockers),
        item.LatestPollTraceId,
        item.LatestProjectionCommitId);
    }

    private static WatchLiveMesFieldSetPresentation? ProjectLiveMesFields(
        LiveMesFieldSetSnapshot? fields,
        WatchTextCatalog catalog) => fields is null
        ? null
        : new WatchLiveMesFieldSetPresentation(
            ProjectText(fields.Area, catalog),
            ProjectText(fields.Eqp, catalog),
            ProjectText(fields.Step, catalog),
            ProjectTime(fields.MesSourceDate, catalog),
            ProjectText(fields.Package, catalog));

    private static string ProjectText(string? value, WatchTextCatalog catalog) =>
        string.IsNullOrWhiteSpace(value) ? catalog.Common.SourceNotProvided : value;

    private static string ProjectTime(DateTimeOffset? value, WatchTextCatalog catalog) =>
        value is null || value == default
            ? catalog.Common.SourceNotProvided
            : catalog.FormatAbsoluteTime(value.Value);

    private static string ProjectLocalAreaDetail(
        WatchAreaDisplayContext context,
        WatchDemandSeriesText text) => text.FormatLocalAreas(
        context.LocalState,
        context.MesAreas);
}
