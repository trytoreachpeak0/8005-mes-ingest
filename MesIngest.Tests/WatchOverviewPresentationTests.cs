using System.Globalization;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchOverviewPresentationTests
{
    private const long HostGeneration = 19;
    private const string CommittedQueryKey = "area=A1-1";

    [Fact]
    public void Atomic_snapshot_projects_all_four_summaries_and_keeps_host_and_client_times_distinct()
    {
        var snapshot = OverviewSnapshot();
        var clientSuccessfulAt = DateTimeOffset.Parse("2026-08-14T01:23:45Z");
        var presentation = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot, clientSuccessfulAt)),
            WatchAreaDisplayContext.AllAreas);

        Assert.True(presentation.HasSnapshot);
        Assert.Equal("1,234", presentation.SeriesValue);
        Assert.Equal("700 / 777", presentation.ReadabilityValue);
        Assert.Equal("12", presentation.ErrorsValue);
        Assert.Equal("9", presentation.AttentionValue);
        Assert.Contains("34 归档后仍可见", presentation.SeriesDetail, StringComparison.Ordinal);
        Assert.Contains(
            DisplayTime(snapshot.Snapshot.SnapshotAsOf),
            presentation.SnapshotFacts,
            StringComparison.Ordinal);
        Assert.Contains(
            DisplayTime(snapshot.Snapshot.ProjectionCommittedAt),
            presentation.SnapshotFacts,
            StringComparison.Ordinal);
        Assert.Contains(
            DisplayTime(clientSuccessfulAt),
            presentation.ClientAttemptFacts,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            DisplayTime(clientSuccessfulAt),
            presentation.SnapshotFacts,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            DisplayTime(snapshot.Snapshot.SnapshotAsOf),
            presentation.ClientAttemptFacts,
            StringComparison.Ordinal);
    }

    [Fact]
    public void No_recent_highlights_uses_the_required_empty_text_without_claiming_overall_health()
    {
        var snapshot = OverviewSnapshot() with
        {
            RecentActivity = [],
            RecentActivityState = WatchOverviewRecentActivityStates.NoRecentHighlights,
            EmptyStateMessage = null,
        };

        var presentation = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            WatchAreaDisplayContext.AllAreas);

        Assert.Equal("近期无重点动态", presentation.RecentActivityHeading);
        Assert.Empty(presentation.RecentActivity);

        var visibleText = string.Join(
            " | ",
            presentation.HostStatus,
            presentation.HostDetail,
            presentation.InfoTitle,
            presentation.InfoMessage,
            presentation.SnapshotFacts,
            presentation.ClientAttemptFacts,
            presentation.SeriesValue,
            presentation.SeriesDetail,
            presentation.ReadabilityValue,
            presentation.ReadabilityDetail,
            presentation.ErrorsValue,
            presentation.ErrorsDetail,
            presentation.AttentionValue,
            presentation.AttentionDetail,
            presentation.LocalAreaHeading,
            presentation.LocalAreaDetail,
            presentation.HostAreaScope,
            presentation.RecentActivityHeading);
        Assert.DoesNotContain("健康", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("一切正常", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("无异常", visibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void Refreshing_a_different_query_keeps_the_entire_committed_snapshot_and_marks_it_stale()
    {
        var snapshot = OverviewSnapshot();
        var committed = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            WatchAreaDisplayContext.AllAreas);
        var refreshingView = SuccessfulView(snapshot) with
        {
            RequestGeneration = 2,
            IsRefreshing = true,
            PendingQueryKey = "area=B2-2",
        };

        var refreshing = WatchOverviewPresentation.Project(
            ConnectedWorkspace(refreshingView),
            WatchAreaDisplayContext.AllAreas);

        Assert.True(refreshing.HasSnapshot);
        Assert.True(refreshing.IsRefreshing);
        Assert.True(refreshing.IsStale);
        AssertSnapshotProjectionEqual(committed, refreshing);
        Assert.Contains("继续显示 Host 快照", refreshing.InfoMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_refresh_keeps_the_entire_committed_snapshot_and_marks_it_stale()
    {
        var snapshot = OverviewSnapshot();
        var committed = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            WatchAreaDisplayContext.AllAreas);
        var failedView = SuccessfulView(snapshot) with
        {
            RequestGeneration = 2,
            IsRefreshing = false,
            LastFailureAt = DateTimeOffset.Parse("2026-08-14T01:25:00Z"),
            FailureKind = WatchHostFailureKind.Timeout,
            FailureCode = "WATCH_TIMEOUT",
            ErrorMessage = "The overview request timed out.",
            CorrelationId = "correlation-ticket-19",
        };

        var failed = WatchOverviewPresentation.Project(
            ConnectedWorkspace(failedView),
            WatchAreaDisplayContext.AllAreas);

        Assert.True(failed.HasSnapshot);
        Assert.False(failed.IsRefreshing);
        Assert.True(failed.IsStale);
        AssertSnapshotProjectionEqual(committed, failed);
        Assert.Contains("已保留上次完整快照", failed.InfoTitle, StringComparison.Ordinal);
        Assert.Contains("最近失败", failed.ClientAttemptFacts, StringComparison.Ordinal);
    }

    [Fact]
    public void Retrying_after_a_failed_refresh_reports_refreshing_while_retaining_failure_context()
    {
        var snapshot = OverviewSnapshot();
        var failedAt = DateTimeOffset.Parse("2026-08-14T01:25:00Z");
        var retryingView = SuccessfulView(snapshot) with
        {
            RequestGeneration = 3,
            IsRefreshing = true,
            PendingQueryKey = "area=B2-2",
            LastFailureAt = failedAt,
            FailureKind = WatchHostFailureKind.Timeout,
            FailureCode = "WATCH_TIMEOUT",
            ErrorMessage = "The prior overview request timed out.",
        };

        var retrying = WatchOverviewPresentation.Project(
            ConnectedWorkspace(retryingView),
            WatchAreaDisplayContext.AllAreas);

        Assert.True(retrying.IsRefreshing);
        Assert.True(retrying.IsStale);
        Assert.Equal("正在刷新概览", retrying.InfoTitle);
        Assert.Contains("继续显示 Host 快照", retrying.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("上次失败", retrying.InfoMessage, StringComparison.Ordinal);
        Assert.Contains(DisplayTime(failedAt), retrying.InfoMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void New_local_area_context_does_not_relabel_the_retained_host_snapshot_scope()
    {
        var snapshot = OverviewSnapshot() with { MesAreas = ["A1-1"] };
        var localContext = new WatchAreaDisplayContext(
            "本机 B2-2 班次",
            ["B2-2"],
            "本机已选择",
            DateTimeOffset.Parse("2026-08-14T01:30:00Z"));

        var presentation = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            localContext);

        Assert.Equal("本机 B2-2 班次", presentation.LocalAreaHeading);
        Assert.Contains("本机已选择", presentation.LocalAreaDetail, StringComparison.Ordinal);
        Assert.Contains("1 个 AREA", presentation.LocalAreaDetail, StringComparison.Ordinal);
        Assert.Equal("Host 已提交范围：A1-1", presentation.HostAreaScope);
        Assert.DoesNotContain("B2-2", presentation.HostAreaScope, StringComparison.Ordinal);
        Assert.DoesNotContain("A1-1", presentation.LocalAreaHeading, StringComparison.Ordinal);
        Assert.DoesNotContain("A1-1", presentation.LocalAreaDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_host_replacement_with_no_snapshot_explicitly_shows_that_old_data_was_cleared()
    {
        var workspace = WatchV2WorkspaceState.Reset(
            HostGeneration + 1,
            "https://replacement-host.example",
            WatchHostConnectionStatus.Failed) with
        {
            FailureKind = WatchHostFailureKind.Contract,
            FailureCode = "CONTRACT_MISMATCH",
            ErrorMessage = "The replacement Host contract is incompatible.",
            CorrelationId = "replacement-correlation",
        };

        var presentation = WatchOverviewPresentation.Project(
            workspace,
            WatchAreaDisplayContext.AllAreas);

        Assert.False(presentation.HasSnapshot);
        Assert.False(presentation.IsStale);
        Assert.Equal("—", presentation.SeriesValue);
        Assert.Equal("—", presentation.ReadabilityValue);
        Assert.Equal("—", presentation.ErrorsValue);
        Assert.Equal("—", presentation.AttentionValue);
        Assert.Equal("Host 已提交范围：尚无快照", presentation.HostAreaScope);
        Assert.Contains("旧 Host 数据已清空", presentation.InfoMessage, StringComparison.Ordinal);
        Assert.Null(presentation.SeriesNavigation);
        Assert.Null(presentation.ReadabilityNavigation);
        Assert.Null(presentation.ErrorsNavigation);
        Assert.Null(presentation.AttentionNavigation);
        Assert.Empty(presentation.RecentActivity);
    }

    [Fact]
    public void Navigation_intent_objects_are_projected_unchanged()
    {
        var seriesIntent = new OverviewNavigationIntent(
            OverviewNavigationTargets.DemandSeries,
            PageNumber: 1,
            MesAreas: ["A1-1"],
            Lifecycles: ["TRACKING"],
            WorkType: "WT-A",
            Cursor: null);
        var readabilityIntent = new OverviewNavigationIntent(
            OverviewNavigationTargets.ReadabilityAudit,
            PageNumber: 1,
            MesAreas: ["A1-1"],
            ReadabilityStates: ["NOT_READABLE"],
            Cursor: null);
        var errorsIntent = new OverviewNavigationIntent(
            OverviewNavigationTargets.ErrorSearch,
            PageNumber: 1,
            ErrorActivityStates: ["ACTIVE"],
            ErrorWindow: "PRIOR_7_DAYS",
            Cursor: null);
        var attentionIntent = new OverviewNavigationIntent(
            OverviewNavigationTargets.CurrentIngestAttention,
            PageNumber: 1,
            AttentionKinds: [CurrentIngestAttentionKinds.PollRunFailure],
            AttentionSeverities: [CurrentIngestAttentionSeverities.Error],
            PollTraceId: "poll-ticket-19",
            Cursor: null);
        var activityIntent = new OverviewNavigationIntent(
            OverviewNavigationTargets.PollTrace,
            PageNumber: 1,
            PollTraceId: "poll-ticket-19",
            Cursor: null);
        var snapshot = OverviewSnapshot(
            seriesIntent,
            readabilityIntent,
            errorsIntent,
            attentionIntent,
            activityIntent);

        var presentation = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            WatchAreaDisplayContext.AllAreas);

        Assert.Same(seriesIntent, presentation.SeriesNavigation);
        Assert.Same(readabilityIntent, presentation.ReadabilityNavigation);
        Assert.Same(errorsIntent, presentation.ErrorsNavigation);
        Assert.Same(attentionIntent, presentation.AttentionNavigation);
        Assert.Single(presentation.RecentActivity);
        Assert.Same(activityIntent, presentation.RecentActivity[0].Navigation);
        Assert.Equal(1, presentation.SeriesNavigation!.PageNumber);
        Assert.Null(presentation.SeriesNavigation.Cursor);
        Assert.Equal(["A1-1"], presentation.SeriesNavigation.MesAreas);
    }

    private static WatchV2WorkspaceState ConnectedWorkspace(
        WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail> overview) =>
        WatchV2WorkspaceState.Reset(
            HostGeneration,
            "https://host-a.example",
            WatchHostConnectionStatus.Connected) with
        {
            Overview = overview,
        };

    private static WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail> SuccessfulView(
        WatchOverviewSnapshot snapshot,
        DateTimeOffset? lastSuccessfulAt = null) =>
        WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail>.Empty(HostGeneration) with
        {
            RequestGeneration = 1,
            PendingQueryKey = CommittedQueryKey,
            CommittedQueryKey = CommittedQueryKey,
            Snapshot = snapshot,
            LastSuccessfulAt = lastSuccessfulAt
                ?? DateTimeOffset.Parse("2026-08-14T01:20:00Z"),
        };

    private static WatchOverviewSnapshot OverviewSnapshot(
        OverviewNavigationIntent? seriesIntent = null,
        OverviewNavigationIntent? readabilityIntent = null,
        OverviewNavigationIntent? errorsIntent = null,
        OverviewNavigationIntent? attentionIntent = null,
        OverviewNavigationIntent? activityIntent = null)
    {
        seriesIntent ??= new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries);
        readabilityIntent ??= new OverviewNavigationIntent(
            OverviewNavigationTargets.ReadabilityAudit);
        errorsIntent ??= new OverviewNavigationIntent(OverviewNavigationTargets.ErrorSearch);
        attentionIntent ??= new OverviewNavigationIntent(
            OverviewNavigationTargets.CurrentIngestAttention);
        activityIntent ??= new OverviewNavigationIntent(OverviewNavigationTargets.PollTrace);
        var snapshotAsOf = DateTimeOffset.Parse("2026-08-14T01:15:00Z");
        var projectionCommittedAt = DateTimeOffset.Parse("2026-08-14T01:14:00Z");
        return new WatchOverviewSnapshot(
            new OperationalSnapshotIdentity(
                "projection-ticket-19",
                419,
                projectionCommittedAt,
                "poll-ticket-19",
                421,
                23,
                snapshotAsOf),
            ["A1-1"],
            new WatchOverviewSeriesSummary(
                1_234,
                900,
                200,
                100,
                34,
                seriesIntent,
                seriesIntent,
                seriesIntent,
                seriesIntent,
                seriesIntent),
            new WatchOverviewReadabilitySummary(
                777,
                700,
                77,
                readabilityIntent,
                readabilityIntent,
                readabilityIntent),
            new WatchOverviewErrorSummary(
                12,
                34,
                errorsIntent,
                errorsIntent,
                errorsIntent),
            new WatchOverviewAttentionSummary(
                9,
                [],
                [
                    new OverviewFacetSnapshot(
                        CurrentIngestAttentionSeverities.Error,
                        2,
                        attentionIntent),
                    new OverviewFacetSnapshot(
                        CurrentIngestAttentionSeverities.Warning,
                        7,
                        attentionIntent),
                ],
                attentionIntent),
            [
                new WatchOverviewActivitySnapshot(
                    "event-ticket-19",
                    CurrentIngestAttentionKinds.PollRunFailure,
                    "POLL_FAILED",
                    CurrentIngestAttentionSeverities.Error,
                    DateTimeOffset.Parse("2026-08-14T01:12:00Z"),
                    "series-ticket-19",
                    "WT-A",
                    "poll-ticket-19",
                    "projection-ticket-19",
                    activityIntent),
            ],
            WatchOverviewRecentActivityStates.HasRecentHighlights,
            null);
    }

    private static void AssertSnapshotProjectionEqual(
        WatchOverviewPresentation expected,
        WatchOverviewPresentation actual)
    {
        Assert.Equal(expected.SnapshotFacts, actual.SnapshotFacts);
        Assert.Equal(expected.SeriesValue, actual.SeriesValue);
        Assert.Equal(expected.SeriesDetail, actual.SeriesDetail);
        Assert.Equal(expected.ReadabilityValue, actual.ReadabilityValue);
        Assert.Equal(expected.ReadabilityDetail, actual.ReadabilityDetail);
        Assert.Equal(expected.ErrorsValue, actual.ErrorsValue);
        Assert.Equal(expected.ErrorsDetail, actual.ErrorsDetail);
        Assert.Equal(expected.AttentionValue, actual.AttentionValue);
        Assert.Equal(expected.AttentionDetail, actual.AttentionDetail);
        Assert.Equal(expected.HostAreaScope, actual.HostAreaScope);
        Assert.Equal(expected.RecentActivityHeading, actual.RecentActivityHeading);
        Assert.Equal(expected.RecentActivity, actual.RecentActivity);
        Assert.Same(expected.SeriesNavigation, actual.SeriesNavigation);
        Assert.Same(expected.ReadabilityNavigation, actual.ReadabilityNavigation);
        Assert.Same(expected.ErrorsNavigation, actual.ErrorsNavigation);
        Assert.Same(expected.AttentionNavigation, actual.AttentionNavigation);
    }

    private static string DisplayTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}
