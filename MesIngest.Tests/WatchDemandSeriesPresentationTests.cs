using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchDemandSeriesPresentationTests
{
    [Fact]
    public void Loaded_page_uses_host_totals_and_keeps_every_operational_series_visible()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var snapshot = new DemandSeriesListSnapshot(
            new DemandSeriesSnapshotIdentity(
                HistoryEpoch.CreateNew(), "commit-20-a", 20, at, "poll-20-a"),
            "snapshot-20-a",
            new DemandSeriesBrowseFilter { MesAreas = ["A1-1"] },
            DemandSeriesBrowseOrder.Default,
            ExactTotalCount: 401,
            new DemandSeriesFacets(200, 101, 240, 130, 30),
            PageSize: 200,
            PageNumber: 2,
            TotalPages: 3,
            Items:
            [
                Item("series-tracking", "TRACKING", "VISIBLE", "READABLE", []),
                Item("series-gone", "TRACKING", "GONE", "NOT_READABLE", ["DEMAND_GONE"]),
                Item("series-archived", "ARCHIVED", "GONE", "NOT_READABLE", ["SERIES_ARCHIVED"]),
                Item(
                    "series-long-gone",
                    "ARCHIVED",
                    "LONG_GONE_BUT_VISIBLE",
                    "NOT_READABLE",
                    ["LONG_GONE_BUT_VISIBLE", "DUPLICATE_TRANSPORT_DEMAND_KEY"]),
            ],
            NextCursor: "cursor-page-3",
            HasMore: true);
        var state = WatchV2WorkspaceState.Reset(
            hostGeneration: 3,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            DemandSeries = WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot>
                .Empty(3) with
            {
                CommittedQueryKey = "series-page-two",
                PendingQueryKey = "series-page-two",
                Snapshot = snapshot,
                LastSuccessfulAt = at.AddSeconds(1),
            },
        };

        var presentation = WatchDemandSeriesPresentation.Project(
            state,
            new DemandSeriesBrowseQuery(
                new DemandSeriesBrowseFilter { MesAreas = ["A1-1"] },
                PageSize: 200,
                PageNumber: 2,
                SnapshotReference: "snapshot-20-a"),
            new WatchAreaDisplayContext("封装车间", ["A1-1"], "本机已应用", at),
            navigation: null,
            focusedDemandId: null);

        Assert.Equal("精确 401 个 Series · 第 2 / 3 页", presentation.PageSummary);
        Assert.Equal("Host 固定排序：开始时间降序、SeriesId 升序", presentation.OrderSummary);
        Assert.Equal("Host 已提交范围：A1-1", presentation.HostAreaScope);
        Assert.Equal("封装车间", presentation.LocalAreaHeading);
        Assert.Contains("本机已应用", presentation.LocalAreaDetail, StringComparison.Ordinal);
        Assert.Contains("A1-1", presentation.LocalAreaDetail, StringComparison.Ordinal);
        Assert.False(presentation.IsAreaScopeDifferent);
        Assert.True(presentation.CanGoPrevious);
        Assert.True(presentation.CanGoNext);
        Assert.Equal(
            [
                "TRACKING · VISIBLE",
                "TRACKING · GONE",
                "ARCHIVED · GONE",
                "ARCHIVED · LONG_GONE_BUT_VISIBLE",
            ],
            presentation.Rows.Select(row => row.LifecycleAndPresence).ToArray());
        Assert.Equal(
            "LONG_GONE_BUT_VISIBLE、DUPLICATE_TRANSPORT_DEMAND_KEY",
            presentation.Rows[3].Attention);
        Assert.Equal(4, presentation.Rows.Count);
        var firstRow = presentation.Rows[0];
        Assert.Equal(WatchTimeDisplay.Format(DateTimeOffset.Parse("2026-08-14T04:00:00Z")), firstRow.StartedAt);
        Assert.Equal("demand-series-tracking", firstRow.CurrentDemandId);
        Assert.Equal(2, firstRow.CurrentGeneration);
        Assert.Equal(42, firstRow.LastSeriesSequence);
        Assert.Equal("poll-20-a", firstRow.LatestPollTraceId);
        Assert.Equal("commit-20-a", firstRow.LatestProjectionCommitId);
        Assert.Equal("A1-1", Assert.IsType<WatchLiveMesFieldSetPresentation>(firstRow.LiveMesFields).Area);
    }

    [Fact]
    public void Empty_result_reports_zero_of_zero_and_keeps_committed_area_distinct_from_local_scope()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var snapshot = new DemandSeriesListSnapshot(
            new DemandSeriesSnapshotIdentity(
                HistoryEpoch.CreateNew(), "commit-empty-a1", 22, at, "poll-empty-a1"),
            "snapshot-empty-a1",
            new DemandSeriesBrowseFilter { MesAreas = ["A1-1"] },
            DemandSeriesBrowseOrder.Default,
            ExactTotalCount: 0,
            new DemandSeriesFacets(0, 0, 0, 0, 0),
            PageSize: 100,
            PageNumber: 1,
            TotalPages: 0,
            Items: [],
            NextCursor: null,
            HasMore: false);
        var state = WatchV2WorkspaceState.Reset(
            hostGeneration: 3,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            DemandSeries = WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot>
                .Empty(3) with
            {
                CommittedQueryKey = "area=A1-1",
                PendingQueryKey = "area=B2-2",
                Snapshot = snapshot,
                LastSuccessfulAt = at,
            },
        };

        var presentation = WatchDemandSeriesPresentation.Project(
            state,
            new DemandSeriesBrowseQuery(
                new DemandSeriesBrowseFilter { MesAreas = ["B2-2"] }),
            new WatchAreaDisplayContext("本机 B2 班次", ["B2-2"], "本机已选择", at),
            navigation: null,
            focusedDemandId: null);

        Assert.Equal("精确 0 个 Series · 第 0 / 0 页", presentation.PageSummary);
        Assert.Equal("Host 已提交范围：A1-1", presentation.HostAreaScope);
        Assert.Equal("本机 B2 班次", presentation.LocalAreaHeading);
        Assert.Contains("本机已选择", presentation.LocalAreaDetail, StringComparison.Ordinal);
        Assert.Contains("B2-2", presentation.LocalAreaDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("B2-2", presentation.HostAreaScope, StringComparison.Ordinal);
        Assert.True(presentation.IsAreaScopeDifferent);
        Assert.False(presentation.CanGoPrevious);
        Assert.False(presentation.CanGoNext);
        Assert.Empty(presentation.Rows);
    }

    [Fact]
    public void Refresh_failure_staleness_and_selection_loss_have_distinct_operator_messages()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var snapshot = EmptyListSnapshot(
            "commit-retained-a1",
            projectionSequence: 23,
            at,
            "poll-retained-a1",
            ["A1-1"]);
        var successful = WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot>
            .Empty(5) with
        {
            RequestGeneration = 1,
            PendingQueryKey = "area=A1-1",
            CommittedQueryKey = "area=A1-1",
            Snapshot = snapshot,
            LastSuccessfulAt = at.AddSeconds(1),
        };
        var query = new DemandSeriesBrowseQuery(
            new DemandSeriesBrowseFilter { MesAreas = ["B2-2"] });
        var local = new WatchAreaDisplayContext(
            "本机 B2 班次",
            ["B2-2"],
            "本机已选择",
            at.AddMinutes(1));

        var refreshing = WatchDemandSeriesPresentation.Project(
            Workspace(successful with
            {
                RequestGeneration = 2,
                IsRefreshing = true,
                PendingQueryKey = "area=B2-2",
            }),
            query,
            local,
            navigation: null,
            focusedDemandId: null);

        Assert.True(refreshing.HasSnapshot);
        Assert.True(refreshing.IsRefreshing);
        Assert.True(refreshing.IsStale);
        Assert.True(refreshing.IsInfoOpen);
        Assert.Equal(WatchPresentationSeverity.Informational, refreshing.InfoSeverity);
        Assert.Equal("正在刷新需求系列", refreshing.InfoTitle);
        Assert.Contains("继续显示 Host 快照", refreshing.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("commit-retained-a1", refreshing.SnapshotFacts, StringComparison.Ordinal);
        Assert.Contains("序列 23", refreshing.SnapshotFacts, StringComparison.Ordinal);
        Assert.Contains("poll-retained-a1", refreshing.SnapshotFacts, StringComparison.Ordinal);

        var failedAt = at.AddMinutes(2);
        var failed = WatchDemandSeriesPresentation.Project(
            Workspace(successful with
            {
                RequestGeneration = 2,
                PendingQueryKey = "area=B2-2",
                LastFailureAt = failedAt,
                FailedQueryKey = "area=B2-2",
                FailureKind = WatchHostFailureKind.Timeout,
                FailureCode = "DEMAND_SERIES_TIMEOUT",
                ErrorMessage = "Host demand-series request timed out.",
                CorrelationId = "correlation-ticket-20",
            }),
            query,
            local,
            navigation: null,
            focusedDemandId: null);

        Assert.False(failed.IsRefreshing);
        Assert.True(failed.IsStale);
        Assert.True(failed.IsInfoOpen);
        Assert.Equal(WatchPresentationSeverity.Warning, failed.InfoSeverity);
        Assert.Contains("已保留上次快照", failed.InfoTitle, StringComparison.Ordinal);
        Assert.Contains(WatchTimeDisplay.Format(failedAt), failed.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("timed out", failed.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("correlation-ticket-20", failed.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("最近失败", failed.ClientAttemptFacts, StringComparison.Ordinal);
        Assert.Equal("Host 已提交范围：A1-1", failed.HostAreaScope);

        var selectionLost = WatchDemandSeriesPresentation.Project(
            Workspace(successful with
            {
                SelectionNotice = WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
            }),
            new DemandSeriesBrowseQuery(snapshot.Filter),
            new WatchAreaDisplayContext("封装车间", ["A1-1"], "本机已应用", at),
            navigation: null,
            focusedDemandId: null);

        Assert.False(selectionLost.IsStale);
        Assert.True(selectionLost.IsInfoOpen);
        Assert.Equal(WatchPresentationSeverity.Warning, selectionLost.InfoSeverity);
        Assert.Equal("原选择已不在刷新结果中", selectionLost.InfoTitle);
        Assert.Contains("已清除详情", selectionLost.InfoMessage, StringComparison.Ordinal);
        Assert.Equal(
            WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
            selectionLost.SelectionNotice);
    }

    [Fact]
    public void Overview_navigation_context_preserves_target_and_reports_a_newer_target_snapshot()
    {
        var sourceCommittedAt = DateTimeOffset.Parse("2026-08-14T05:00:00Z");
        var intent = new OverviewNavigationIntent(
            OverviewNavigationTargets.DemandSeriesDetail,
            MesAreas: ["A1-1"],
            SeriesId: "series-from-overview");
        Assert.Null(WatchDemandSeriesNavigationContext.FromOverview(source: null, intent));
        var source = OverviewSnapshot(
            "commit-overview-source",
            projectionSequence: 30,
            sourceCommittedAt,
            intent);
        var navigation = Assert.IsType<WatchDemandSeriesNavigationContext>(
            WatchDemandSeriesNavigationContext.FromOverview(source, intent));

        Assert.Equal("概览", navigation.SourceName);
        Assert.Equal("series-from-overview", navigation.SeriesId);
        Assert.Null(navigation.FocusedDemandId);
        Assert.Equal("commit-overview-source", navigation.SourceProjectionCommitId);
        Assert.Equal(30, navigation.SourceProjectionSequence);
        Assert.Equal(["A1-1"], navigation.RequestedMesAreas);

        var target = EmptyListSnapshot(
            "commit-demand-target",
            projectionSequence: 31,
            sourceCommittedAt.AddMinutes(1),
            "poll-demand-target",
            ["A1-1"]);
        var view = WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot>
            .Empty(6) with
        {
            PendingQueryKey = "area=A1-1",
            CommittedQueryKey = "area=A1-1",
            Snapshot = target,
            LastSuccessfulAt = sourceCommittedAt.AddMinutes(2),
        };

        var presentation = WatchDemandSeriesPresentation.Project(
            Workspace(view),
            new DemandSeriesBrowseQuery(target.Filter),
            new WatchAreaDisplayContext("封装车间", ["A1-1"], "本机已应用", sourceCommittedAt),
            navigation,
            focusedDemandId: null);

        Assert.Equal(WatchDemandSeriesSourceComparison.TargetNewer, presentation.SourceComparison);
        Assert.Equal(WatchPresentationSeverity.Informational, presentation.SourceComparisonSeverity);
        Assert.Contains("概览", presentation.SourceSnapshotSummary, StringComparison.Ordinal);
        Assert.Contains(
            WatchTimeDisplay.Format(source.Snapshot.SnapshotAsOf),
            presentation.SourceSnapshotSummary,
            StringComparison.Ordinal);
        Assert.Contains("commit-overview-source", presentation.SourceSnapshotSummary, StringComparison.Ordinal);
        Assert.Contains("序列 30", presentation.SourceSnapshotSummary, StringComparison.Ordinal);
        Assert.Contains("目标页快照较来源更新", presentation.SourceComparisonMessage, StringComparison.Ordinal);
        Assert.Contains("无法判定对象事实是否变化", presentation.SourceComparisonMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VISIBLE", "READABLE", false, "对象事实未变化")]
    [InlineData("GONE", "NOT_READABLE", true, "事实已变化")]
    public void Source_comparison_reports_whether_exposed_demand_facts_changed(
        string targetPresence,
        string targetReadability,
        bool expectedChanged,
        string expectedMessage)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:00:00Z");
        var sourceFacts = new WatchDemandSeriesObjectFacts(
            "series-source-facts",
            "demand-series-source-facts",
            "WIRE_TO_GATE",
            "SL-series-source-facts",
            Generation: 2,
            DemandStatus: "VISIBLE",
            Lifecycle: "TRACKING",
            CurrentPresence: "VISIBLE",
            ExternalReadabilityState: "READABLE",
            ReadabilityBlockers: []);
        var navigation = new WatchDemandSeriesNavigationContext(
            "资格审计",
            sourceFacts.SeriesId,
            sourceFacts.DemandId,
            "commit-source-facts",
            SourceProjectionSequence: 30,
            at,
            at,
            RequestedMesAreas: [],
            sourceFacts);
        var targetItem = Item(
            sourceFacts.SeriesId,
            "TRACKING",
            targetPresence,
            targetReadability,
            expectedChanged ? ["DEMAND_GONE"] : []);
        var snapshot = EmptyListSnapshot(
            "commit-target-facts",
            projectionSequence: 31,
            at.AddMinutes(1),
            "poll-target-facts",
            []) with
        {
            ExactTotalCount = 1,
            TotalPages = 1,
            Items = [targetItem],
        };
        var view = WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot>
            .Empty(8) with
        {
            PendingQueryKey = "facts",
            CommittedQueryKey = "facts",
            Snapshot = snapshot,
            LastSuccessfulAt = at.AddMinutes(2),
        };

        var presentation = WatchDemandSeriesPresentation.Project(
            Workspace(view),
            new DemandSeriesBrowseQuery(snapshot.Filter),
            WatchAreaDisplayContext.AllAreas,
            navigation,
            focusedDemandId: null);

        Assert.Equal(WatchDemandSeriesSourceComparison.TargetNewer, presentation.SourceComparison);
        Assert.Contains(expectedMessage, presentation.SourceComparisonMessage, StringComparison.Ordinal);
        Assert.Equal(expectedChanged, presentation.SourceComparisonMessage.Contains(
            "→",
            StringComparison.Ordinal));
        Assert.Contains("来源对象事实", presentation.SourceSnapshotSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30, "commit-source", "SameProjection", "Success", "同一投影提交")]
    [InlineData(29, "commit-target-older", "TargetOlder", "Warning", "早于来源快照")]
    [InlineData(30, "commit-target-conflict", "ProjectionIdentityMismatch", "Error", "标识不同")]
    public void Source_comparison_uses_projection_sequence_then_commit_identity(
        long targetSequence,
        string targetCommitId,
        string expectedComparison,
        string expectedSeverity,
        string expectedMessage)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:00:00Z");
        var navigation = new WatchDemandSeriesNavigationContext(
            "资格审计",
            "series-source",
            "demand-source",
            "commit-source",
            SourceProjectionSequence: 30,
            at,
            at.AddSeconds(5),
            RequestedMesAreas: []);
        var snapshot = EmptyListSnapshot(
            targetCommitId,
            targetSequence,
            at.AddMinutes(1),
            "poll-target",
            []);
        var view = WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot>
            .Empty(7) with
        {
            PendingQueryKey = "all",
            CommittedQueryKey = "all",
            Snapshot = snapshot,
            LastSuccessfulAt = at.AddMinutes(2),
        };

        var presentation = WatchDemandSeriesPresentation.Project(
            Workspace(view),
            new DemandSeriesBrowseQuery(snapshot.Filter),
            WatchAreaDisplayContext.AllAreas,
            navigation,
            focusedDemandId: null);

        Assert.Equal(expectedComparison, presentation.SourceComparison.ToString());
        Assert.Equal(expectedSeverity, presentation.SourceComparisonSeverity.ToString());
        Assert.Contains(expectedMessage, presentation.SourceComparisonMessage, StringComparison.Ordinal);
    }

    private static WatchV2WorkspaceState Workspace(
        WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot> demandSeries) =>
        WatchV2WorkspaceState.Reset(
            hostGeneration: demandSeries.HostGeneration,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            DemandSeries = demandSeries,
        };

    private static DemandSeriesListSnapshot EmptyListSnapshot(
        string projectionCommitId,
        long projectionSequence,
        DateTimeOffset projectionCommittedAt,
        string pollTraceId,
        IReadOnlyList<string> mesAreas) => new(
        new DemandSeriesSnapshotIdentity(
            HistoryEpoch.CreateNew(),
            projectionCommitId,
            projectionSequence,
            projectionCommittedAt,
            pollTraceId),
        $"snapshot-{projectionCommitId}",
        new DemandSeriesBrowseFilter { MesAreas = mesAreas },
        DemandSeriesBrowseOrder.Default,
        ExactTotalCount: 0,
        new DemandSeriesFacets(0, 0, 0, 0, 0),
        PageSize: 100,
        PageNumber: 1,
        TotalPages: 0,
        Items: [],
        NextCursor: null,
        HasMore: false);

    private static WatchOverviewSnapshot OverviewSnapshot(
        string projectionCommitId,
        long projectionSequence,
        DateTimeOffset projectionCommittedAt,
        OverviewNavigationIntent navigation)
    {
        var identity = new OperationalSnapshotIdentity(
            projectionCommitId,
            projectionSequence,
            projectionCommittedAt,
            "poll-overview-source",
            PollTraceHighWater: 31,
            CatalogRevision: 7,
            SnapshotAsOf: projectionCommittedAt.AddSeconds(5));
        return new WatchOverviewSnapshot(
            identity,
            navigation.MesAreas ?? [],
            new WatchOverviewSeriesSummary(0, 0, 0, 0, 0, navigation, navigation, navigation, navigation, navigation),
            new WatchOverviewReadabilitySummary(0, 0, 0, navigation, navigation, navigation),
            new WatchOverviewErrorSummary(0, 0, navigation, navigation, navigation),
            new WatchOverviewAttentionSummary(0, [], [], navigation),
            [],
            WatchOverviewRecentActivityStates.NoRecentHighlights,
            WatchOverviewRecentActivityStates.NoRecentHighlightsMessage);
    }

    private static DemandSeriesListItemSnapshot Item(
        string seriesId,
        string lifecycle,
        string presence,
        string readability,
        IReadOnlyList<string> blockers) => new(
        seriesId,
        "WIRE_TO_GATE",
        $"SL-{seriesId}",
        lifecycle,
        presence,
        DateTimeOffset.Parse("2026-08-14T04:00:00Z"),
        lifecycle == "ARCHIVED" ? DateTimeOffset.Parse("2026-08-14T04:30:00Z") : null,
        $"demand-{seriesId}",
        2,
        presence == "GONE" ? "GONE" : "VISIBLE",
        DateTimeOffset.Parse("2026-08-14T04:20:00Z"),
        presence == "GONE" ? DateTimeOffset.Parse("2026-08-14T04:21:00Z") : null,
        new LiveMesFieldSetSnapshot("A1-1", "EQP-20", "STEP-20", DateTimeOffset.Parse("2026-08-13T21:00:00+08:00"), "PKG-20"),
        readability,
        blockers,
        LastSeriesSequence: 42,
        LatestPollTraceId: "poll-20-a",
        LatestProjectionCommitId: "commit-20-a");

}
