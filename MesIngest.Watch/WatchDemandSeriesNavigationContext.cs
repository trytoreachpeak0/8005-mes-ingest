using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

/// <summary>
/// Object-level facts exposed by the source page. Null values mean that the
/// source page did not expose that dimension, rather than that the fact was
/// empty. The target page compares only the dimensions present here.
/// </summary>
internal sealed record WatchDemandSeriesObjectFacts(
    string SeriesId,
    string? DemandId,
    string? WorkType,
    string? Sublot,
    int? Generation,
    string? DemandStatus,
    string? Lifecycle,
    string? CurrentPresence,
    string? ExternalReadabilityState,
    IReadOnlyList<string>? ReadabilityBlockers);

/// <summary>
/// UI-only drill context. The target page always reads its own Host snapshot;
/// this record preserves the source fence solely so the operator can compare it.
/// </summary>
internal sealed record WatchDemandSeriesNavigationContext(
    string SourceName,
    string? SeriesId,
    string? FocusedDemandId,
    string SourceProjectionCommitId,
    long SourceProjectionSequence,
    DateTimeOffset SourceProjectionCommittedAt,
    DateTimeOffset SourceSnapshotAsOf,
    IReadOnlyList<string> RequestedMesAreas,
    WatchDemandSeriesObjectFacts? SourceFacts = null,
    bool OpenInspector = true)
{
    public static WatchDemandSeriesNavigationContext? FromOverview(
        WatchOverviewSnapshot? source,
        OverviewNavigationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (source is null)
        {
            return null;
        }

        var activity = source.RecentActivity.FirstOrDefault(value =>
            string.Equals(value.SeriesId, intent.SeriesId, StringComparison.Ordinal));
        var workType = activity?.WorkType ?? intent.WorkType;
        var sourceFacts = string.IsNullOrWhiteSpace(intent.SeriesId)
            ? null
            : new WatchDemandSeriesObjectFacts(
                intent.SeriesId,
                DemandId: null,
                string.IsNullOrWhiteSpace(workType) ? null : workType,
                Sublot: null,
                Generation: null,
                DemandStatus: null,
                Lifecycle: null,
                CurrentPresence: null,
                ExternalReadabilityState: null,
                ReadabilityBlockers: null);
        return new WatchDemandSeriesNavigationContext(
            "概览",
            intent.SeriesId,
            FocusedDemandId: null,
            source.Snapshot.ProjectionCommitId,
            source.Snapshot.ProjectionSequence,
            source.Snapshot.ProjectionCommittedAt,
            source.Snapshot.SnapshotAsOf,
            (intent.MesAreas ?? source.MesAreas).ToArray(),
            sourceFacts,
            OpenInspector: string.Equals(
                intent.Target,
                OverviewNavigationTargets.DemandSeriesDetail,
                StringComparison.Ordinal));
    }

    public static WatchDemandSeriesNavigationContext FromReadabilityAudit(
        ReadabilityAuditListSnapshot source,
        string seriesId,
        string focusedDemandId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(focusedDemandId);
        var item = source.Items.FirstOrDefault(value =>
            string.Equals(value.SeriesId, seriesId, StringComparison.Ordinal)
            && string.Equals(value.DemandId, focusedDemandId, StringComparison.Ordinal));
        var sourceFacts = item is null
            ? null
            : new WatchDemandSeriesObjectFacts(
                item.SeriesId,
                item.DemandId,
                item.WorkType,
                item.Sublot,
                item.Generation,
                item.DemandStatus,
                item.SeriesLifecycle,
                item.SeriesCurrentPresence,
                item.ExternalReadabilityState,
                item.ReadabilityBlockers.ToArray());
        return new WatchDemandSeriesNavigationContext(
            "资格审计",
            seriesId,
            focusedDemandId,
            source.Snapshot.ProjectionCommitId,
            source.Snapshot.ProjectionSequence,
            source.Snapshot.ProjectionCommittedAt,
            // The audit identity has no separate as-of instant; its committed
            // projection time is the strongest source boundary it exposes.
            source.Snapshot.ProjectionCommittedAt,
            source.Filter.MesAreas.ToArray(),
            sourceFacts);
    }

    public static WatchDemandSeriesNavigationContext? FromErrorSearch(
        ErrorSearchListSnapshot? source,
        string? selectedId,
        ErrorSearchDetailSnapshot? detail)
    {
        if (source is null
            || detail is null
            || !WatchErrorSearchDetailConsistency.Matches(source, selectedId, detail))
        {
            return null;
        }

        var series = detail.Series;
        var sourceFacts = new WatchDemandSeriesObjectFacts(
            series.SeriesId,
            DemandId: null,
            series.WorkType,
            series.Sublot,
            Generation: null,
            DemandStatus: null,
            Lifecycle: null,
            CurrentPresence: null,
            ExternalReadabilityState: null,
            ReadabilityBlockers: null);
        return new WatchDemandSeriesNavigationContext(
            "错误检索",
            series.SeriesId,
            FocusedDemandId: null,
            source.Snapshot.ProjectionCommitId,
            source.Snapshot.ProjectionSequence,
            source.Snapshot.ProjectionCommittedAt,
            source.Snapshot.ErrorSearchAsOf,
            RequestedMesAreas: [],
            sourceFacts);
    }

    public static WatchDemandSeriesNavigationContext? FromCurrentAttention(
        CurrentIngestAttentionSnapshot? source,
        WatchCurrentIngestAttentionRowPresentation? selected)
    {
        if (source is null || selected?.SeriesId is not { Length: > 0 } seriesId)
        {
            return null;
        }

        var sourceFacts = new WatchDemandSeriesObjectFacts(
            seriesId,
            selected.Evidence.DemandId,
            selected.WorkType ?? selected.Evidence.WorkType,
            Sublot: null,
            Generation: null,
            DemandStatus: null,
            Lifecycle: null,
            CurrentPresence: null,
            ExternalReadabilityState: null,
            ReadabilityBlockers: null);
        return new WatchDemandSeriesNavigationContext(
            "当前关注",
            seriesId,
            selected.Evidence.DemandId,
            source.Snapshot.ProjectionCommitId,
            source.Snapshot.ProjectionSequence,
            source.Snapshot.ProjectionCommittedAt,
            source.Snapshot.SnapshotAsOf,
            RequestedMesAreas: [],
            sourceFacts,
            OpenInspector: true);
    }
}
