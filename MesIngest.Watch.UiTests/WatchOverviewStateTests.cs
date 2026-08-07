using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

public sealed class WatchOverviewStateTests
{
    [Fact]
    public async Task Successful_refresh_projects_bounded_counts_and_current_page_task_types()
    {
        var now = new DateTimeOffset(2026, 8, 8, 9, 30, 0, TimeSpan.FromHours(8));
        var snapshot = new WatchSnapshot(
            Demands:
            [
                Demand("d-1", "DIE_TO_OVEN"),
                Demand("d-2", "DIE_TO_OVEN"),
                Demand("d-3", "WIRE_TO_GATE"),
            ],
            Alerts: [Alert("a-1", "WARNING")],
            PollHealth: Health(),
            FetchError: null,
            DemandsHasMore: false,
            AlertsHasMore: true,
            DemandsSucceeded: true,
            AlertsSucceeded: true,
            PollHealthSucceeded: true);
        var queries = new SnapshotQueries(snapshot);
        var overview = new WatchOverviewSession(queries, () => now);

        await overview.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("100+", overview.State.Alerts.CountLabel);
        Assert.Equal("3", overview.State.Demands.CountLabel);
        Assert.Equal(now, overview.State.Alerts.LastSuccessfulAt);
        Assert.Equal(now, overview.State.Demands.LastSuccessfulAt);
        Assert.Equal(now, overview.State.PollHealth.LastSuccessfulAt);
        Assert.Equal(
            "当前页：DIE_TO_OVEN 2 · WIRE_TO_GATE 1",
            overview.State.Demands.TaskTypeSummary);
        Assert.False(overview.State.IsPartialFailure);
    }

    [Fact]
    public async Task Partial_failure_keeps_failed_card_and_commits_other_resources()
    {
        var firstAt = new DateTimeOffset(2026, 8, 8, 9, 30, 0, TimeSpan.FromHours(8));
        var secondAt = firstAt.AddMinutes(1);
        var first = new WatchSnapshot(
            [Demand("d-1", "DIE_TO_OVEN")],
            [Alert("a-1", "WARNING")],
            Health(),
            FetchError: null,
            DemandsSucceeded: true,
            AlertsSucceeded: true,
            PollHealthSucceeded: true);
        var partial = new WatchSnapshot(
            [Demand("d-2", "WIRE_TO_GATE"), Demand("d-3", "WIRE_TO_GATE")],
            [],
            Health() with { Outcome = "SUCCESS_2", RowCount = 42 },
            FetchError: "endpoint=/api/alerts fake failure",
            DemandsSucceeded: true,
            AlertsSucceeded: false,
            PollHealthSucceeded: true);
        var now = firstAt;
        var overview = new WatchOverviewSession(
            new SequenceQueries(first, partial),
            () => now);

        await overview.RefreshAsync(TestContext.Current.CancellationToken);
        now = secondAt;
        await overview.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("a-1", Assert.Single(overview.State.Alerts.Items).AlertId);
        Assert.Equal(firstAt, overview.State.Alerts.LastSuccessfulAt);
        Assert.True(overview.State.Alerts.IsStale);
        Assert.Contains("/api/alerts", overview.State.Alerts.Error, StringComparison.Ordinal);
        Assert.Equal("2", overview.State.Demands.CountLabel);
        Assert.Equal(secondAt, overview.State.Demands.LastSuccessfulAt);
        Assert.False(overview.State.Demands.IsStale);
        Assert.Equal("SUCCESS_2", overview.State.PollHealth.Value?.Outcome);
        Assert.Equal(secondAt, overview.State.PollHealth.LastSuccessfulAt);
        Assert.True(overview.State.IsPartialFailure);
    }

    [Fact]
    public void Projection_distinguishes_every_overview_health_conclusion_with_text_and_icon()
    {
        var healthy = Health();
        var paused = healthy with
        {
            TaskTypePauses =
            [
                new WatchTaskTypePauseDto("WIRE_TO_NITROGEN", true, 4, 0),
            ],
            FailureStage = "RECONCILE",
        };
        var connected = Host(WatchHostConnectionStatus.Connected);

        Assert.Equal("✕ 连接失败", WatchOverviewProjection.Project(
            Host(WatchHostConnectionStatus.Failed, WatchHostFailureKind.Network),
            State(healthy)).ConclusionText);
        Assert.Equal("✕ 契约不兼容", WatchOverviewProjection.Project(
            Host(WatchHostConnectionStatus.Failed, WatchHostFailureKind.Contract),
            State(healthy)).ConclusionText);
        Assert.Equal("△ 部分失败", WatchOverviewProjection.Project(
            connected,
            State(healthy) with
            {
                Alerts = WatchOverviewAlertState.Empty with { Error = "alert failed" },
            }).ConclusionText);
        var allFailed = State(healthy, Alert("cached-warning", "WARNING")) with
        {
            PollHealth = WatchOverviewPollHealthState.Empty with { Error = "health failed" },
            Alerts = WatchOverviewAlertState.Empty with { Error = "alerts failed" },
            Demands = WatchOverviewDemandState.Empty with { Error = "demands failed" },
        };
        Assert.Equal("✕ 连接失败", WatchOverviewProjection.Project(
            connected,
            allFailed).ConclusionText);
        Assert.Equal("○ 尚无轮询", WatchOverviewProjection.Project(
            connected,
            State(null)).ConclusionText);
        Assert.Equal("✕ 最近轮询失败", WatchOverviewProjection.Project(
            connected,
            State(healthy with { Success = false, Outcome = "FAILED" })).ConclusionText);
        Assert.Equal("⏸ PausedZeroDrop", WatchOverviewProjection.Project(
            connected,
            State(paused)).ConclusionText);
        Assert.Equal("✕ 存在活动 ERROR", WatchOverviewProjection.Project(
            connected,
            State(healthy, Alert("a-1", "ERROR"))).ConclusionText);
        Assert.Equal("△ 仅有 WARNING", WatchOverviewProjection.Project(
            connected,
            State(healthy, Alert("a-1", "WARNING"))).ConclusionText);
        Assert.Equal("✓ 健康", WatchOverviewProjection.Project(
            connected,
            State(healthy)).ConclusionText);

        var pausedProjection = WatchOverviewProjection.Project(connected, State(paused));
        Assert.Contains("failureStage=RECONCILE", pausedProjection.PollHealthText, StringComparison.Ordinal);
        Assert.Contains("暂停 TASK_TYPE：WIRE_TO_NITROGEN", pausedProjection.PollHealthText, StringComparison.Ordinal);
    }

    private static WatchDemandDto Demand(string id, string taskType) => new(
        id,
        taskType,
        $"S-{id}",
        "A01-01",
        "EQP-1",
        "STEP-1",
        DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"),
        "PKG",
        "VISIBLE",
        DateTimeOffset.Parse("2026-08-08T09:00:00+08:00"),
        0,
        false,
        null,
        DateTimeOffset.Parse("2026-08-08T08:05:00+08:00"),
        null);

    private static WatchAlertDto Alert(string id, string severity) => new(
        id,
        "REAPPEAR_AFTER_GONE",
        severity,
        "WIRE_TO_GATE",
        "S-1",
        "d-3",
        "fake alert",
        null,
        DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"),
        DateTimeOffset.Parse("2026-08-08T09:00:00+08:00"),
        1,
        true,
        null,
        DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"));

    private static WatchPollHealthDto Health() => new(
        DateTimeOffset.Parse("2026-08-08T09:29:59+08:00"),
        DateTimeOffset.Parse("2026-08-08T09:30:00+08:00"),
        1000,
        27,
        true,
        "SUCCESS",
        []);

    private static WatchHostSessionState Host(
        WatchHostConnectionStatus status,
        WatchHostFailureKind failureKind = WatchHostFailureKind.None) => new(
        1,
        "http://fake-watch.test",
        status,
        null,
        DateTimeOffset.Parse("2026-08-08T09:00:00+08:00"),
        failureKind,
        status == WatchHostConnectionStatus.Failed ? "fake failure" : null,
        status == WatchHostConnectionStatus.Failed ? "/api/contract" : null,
        status == WatchHostConnectionStatus.Failed ? "fake-correlation" : null);

    private static WatchOverviewState State(
        WatchPollHealthDto? health,
        params WatchAlertDto[] alerts) => new(
        new WatchOverviewPollHealthState(
            health,
            DateTimeOffset.Parse("2026-08-08T09:30:00+08:00"),
            false,
            null),
        new WatchOverviewAlertState(
            alerts,
            false,
            DateTimeOffset.Parse("2026-08-08T09:30:00+08:00"),
            false,
            null),
        new WatchOverviewDemandState(
            [],
            false,
            DateTimeOffset.Parse("2026-08-08T09:30:00+08:00"),
            false,
            null));

    private sealed class SnapshotQueries(WatchSnapshot snapshot) : IWatchReadQueries
    {
        public Task<WatchSnapshot> FetchSnapshotAsync(
            WatchDemandBrowseQuery demandQuery,
            WatchAlertBrowseQuery alertQuery,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(WatchDemandBrowseQuery.Default, demandQuery);
            Assert.Equal(WatchAlertBrowseQuery.Default, alertQuery);
            return Task.FromResult(snapshot);
        }

        public Task<WatchDemandPage> FetchDemandPageAsync(
            WatchDemandBrowseQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WatchAlertPage> FetchAlertPageAsync(
            WatchAlertBrowseQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WatchDemandDto?> FetchDemandByIdAsync(
            string demandId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SequenceQueries(params WatchSnapshot[] snapshots) : IWatchReadQueries
    {
        private int _index;

        public Task<WatchSnapshot> FetchSnapshotAsync(
            WatchDemandBrowseQuery demandQuery,
            WatchAlertBrowseQuery alertQuery,
            CancellationToken cancellationToken = default)
        {
            var index = Math.Min(_index++, snapshots.Length - 1);
            return Task.FromResult(snapshots[index]);
        }

        public Task<WatchDemandPage> FetchDemandPageAsync(
            WatchDemandBrowseQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WatchAlertPage> FetchAlertPageAsync(
            WatchAlertBrowseQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WatchDemandDto?> FetchDemandByIdAsync(
            string demandId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
