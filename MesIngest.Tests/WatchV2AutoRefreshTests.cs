using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchV2AutoRefreshTests
{
    [Fact]
    public async Task Due_tick_refreshes_the_active_overview_through_the_workspace_session()
    {
        var clock = new ManualTimerTimeProvider(
            DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
        var client = new RecordingV2Client();
        using var session = new WatchV2WorkspaceSession(_ => client, clock);
        await session.ApplyAsync(ValidHostSettings());
        using var coordinator = new WatchV2AutoRefreshCoordinator(
            session,
            WatchV2AutoRefreshSettings.Default,
            clock);

        coordinator.ActivateOverview(new WatchOverviewQuery(["A1-1"]));
        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.Equal(0, client.OverviewCallCount);
        Assert.Null(session.State.Overview.Snapshot);

        clock.Advance(TimeSpan.FromSeconds(1));
        await coordinator.WaitForIdleAsync();

        Assert.Equal(1, client.OverviewCallCount);
        Assert.Equal(["A1-1"], session.State.Overview.Snapshot?.MesAreas);
    }

    [Fact]
    public async Task Every_host_data_view_dispatches_its_matching_workspace_refresh()
    {
        var clock = new ManualTimerTimeProvider(
            DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
        var client = new RecordingV2Client();
        using var session = new WatchV2WorkspaceSession(_ => client, clock);
        await session.ApplyAsync(ValidHostSettings());
        using var coordinator = new WatchV2AutoRefreshCoordinator(
            session,
            WatchV2AutoRefreshSettings.Default,
            clock);

        coordinator.ActivateOverview(new WatchOverviewQuery());
        await AdvanceOneIntervalAsync(clock, coordinator);
        coordinator.ActivateDemandSeries(
            new DemandSeriesBrowseQuery(new DemandSeriesBrowseFilter()));
        await AdvanceOneIntervalAsync(clock, coordinator);
        coordinator.ActivateReadabilityAudit(
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()));
        await AdvanceOneIntervalAsync(clock, coordinator);
        coordinator.ActivateErrorSearch(
            new ErrorSearchQuery(
                new ErrorSearchFilter(),
                ErrorSearchWindowSelection.Last7Days));
        await AdvanceOneIntervalAsync(clock, coordinator);
        coordinator.ActivateCurrentAttention(new CurrentIngestAttentionQuery());
        await AdvanceOneIntervalAsync(clock, coordinator);

        Assert.Equal(1, client.OverviewCallCount);
        Assert.Equal(1, client.DemandSeriesCallCount);
        Assert.Equal(1, client.ReadabilityAuditCallCount);
        Assert.Equal(1, client.ErrorSearchCallCount);
        Assert.Equal(1, client.CurrentAttentionCallCount);
        Assert.NotNull(session.State.Overview.Snapshot);
        Assert.NotNull(session.State.DemandSeries.Snapshot);
        Assert.NotNull(session.State.ReadabilityAudit.Snapshot);
        Assert.NotNull(session.State.ErrorSearch.Snapshot);
        Assert.NotNull(session.State.CurrentAttention.Snapshot);
    }

    [Fact]
    public async Task Automatic_demand_series_refresh_opens_the_same_page_on_a_new_snapshot()
    {
        var clock = new ManualTimerTimeProvider(
            DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
        var client = new RecordingV2Client
        {
            DemandSeriesHandler = (query, _) => Task.FromResult(query switch
            {
                { PageNumber: 1, SnapshotReference: null } =>
                    RecordingV2Client.DemandSeriesSnapshot(
                        query,
                        "commit-new",
                        "snapshot-new",
                        totalPages: 3),
                { PageNumber: 2, SnapshotReference: "snapshot-new" } =>
                    RecordingV2Client.DemandSeriesSnapshot(
                        query,
                        "commit-new",
                        "snapshot-new",
                        totalPages: 3),
                _ => throw new InvalidOperationException(
                    $"Unexpected automatic DemandSeries query: page={query.PageNumber}, snapshot={query.SnapshotReference ?? "<latest>"}."),
            }),
        };
        using var session = new WatchV2WorkspaceSession(_ => client, clock);
        await session.ApplyAsync(ValidHostSettings());
        using var coordinator = new WatchV2AutoRefreshCoordinator(
            session,
            WatchV2AutoRefreshSettings.Default,
            clock);
        var activePage = new DemandSeriesBrowseQuery(
            new DemandSeriesBrowseFilter
            {
                Lifecycles = [DemandSeriesLifecycleContract.Tracking],
                MesAreas = ["A1-1"],
            },
            PageSize: 25,
            PageNumber: 2,
            SnapshotReference: "snapshot-old");
        coordinator.ActivateDemandSeries(activePage);

        await AdvanceOneIntervalAsync(clock, coordinator);

        Assert.Collection(
            client.DemandSeriesQueries,
            firstPage =>
            {
                Assert.Equal(1, firstPage.PageNumber);
                Assert.Null(firstPage.SnapshotReference);
                Assert.Equal(activePage.Filter.Lifecycles, firstPage.Filter.Lifecycles);
                Assert.Equal(activePage.Filter.MesAreas, firstPage.Filter.MesAreas);
                Assert.Equal(activePage.PageSize, firstPage.PageSize);
                Assert.Equal(activePage.Order, firstPage.Order);
            },
            secondPage =>
            {
                Assert.Equal(2, secondPage.PageNumber);
                Assert.Equal("snapshot-new", secondPage.SnapshotReference);
                Assert.Equal(activePage.Filter.Lifecycles, secondPage.Filter.Lifecycles);
                Assert.Equal(activePage.Filter.MesAreas, secondPage.Filter.MesAreas);
                Assert.Equal(activePage.PageSize, secondPage.PageSize);
                Assert.Equal(activePage.Order, secondPage.Order);
            });
        var committed = Assert.IsType<DemandSeriesListSnapshot>(
            session.State.DemandSeries.Snapshot);
        Assert.Equal("snapshot-new", committed.SnapshotReference);
        Assert.Equal(2, committed.PageNumber);
    }

    [Fact]
    public async Task Automatic_audit_refresh_retains_off_page_selection_and_clears_only_on_explicit_snapshot_absence()
    {
        var clock = new ManualTimerTimeProvider(
            DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
        var client = new RecordingV2Client();
        client.ReadabilityAuditHandler = (query, _) => Task.FromResult(
            ClientAuditResponse(query));
        client.ReadabilityAuditDetailHandler = (demandId, snapshotReference, _) =>
            snapshotReference == "snapshot-newer"
                ? Task.FromException<ReadabilityAuditDetailSnapshot>(new WatchHostQueryException(
                    WatchHostFailureKind.ServerQuery,
                    "/api/v2/readability-audit/{demandId}",
                    "audit-object-missing",
                    "The selected Demand is not present in the audit snapshot.",
                    errorCode: ReadabilityAuditErrorCodes.ObjectNotInSnapshot))
                : Task.FromResult(RecordingV2Client.ReadabilityAuditDetailSnapshot(
                    demandId,
                    snapshotReference));
        using var session = new WatchV2WorkspaceSession(_ => client, clock);
        await session.ApplyAsync(ValidHostSettings());
        var activePage = new ReadabilityAuditQuery(
            new ReadabilityAuditFilter
            {
                ReadabilityStates = [ExternalReadabilityStates.NotReadable],
                MesAreas = ["A1-1"],
            },
            PageSize: 25,
            PageNumber: 4,
            SnapshotReference: "snapshot-old");
        await session.RefreshReadabilityAuditAsync(activePage);
        await session.SelectReadabilityDemandAsync("demand-a");
        using var coordinator = new WatchV2AutoRefreshCoordinator(
            session,
            WatchV2AutoRefreshSettings.Default,
            clock);
        coordinator.ActivateReadabilityAudit(activePage);

        await AdvanceOneIntervalAsync(clock, coordinator);

        Assert.Collection(
            client.ReadabilityAuditQueries,
            oldPage =>
            {
                Assert.Equal(4, oldPage.PageNumber);
                Assert.Equal("snapshot-old", oldPage.SnapshotReference);
            },
            latestFirstPage =>
            {
                Assert.Equal(1, latestFirstPage.PageNumber);
                Assert.Null(latestFirstPage.SnapshotReference);
                Assert.Null(latestFirstPage.Cursor);
                Assert.Equal(activePage.Filter.ReadabilityStates, latestFirstPage.Filter.ReadabilityStates);
                Assert.Equal(activePage.Filter.MesAreas, latestFirstPage.Filter.MesAreas);
                Assert.Equal(activePage.PageSize, latestFirstPage.PageSize);
                Assert.Equal(activePage.Order, latestFirstPage.Order);
            },
            convergedPage =>
            {
                Assert.Equal(2, convergedPage.PageNumber);
                Assert.Equal("snapshot-new", convergedPage.SnapshotReference);
                Assert.Null(convergedPage.Cursor);
            });
        Assert.Equal(["snapshot-old", "snapshot-new"],
            client.ReadabilityAuditDetailSnapshotReferences);

        var committed = Assert.IsType<ReadabilityAuditListSnapshot>(
            session.State.ReadabilityAudit.Snapshot);
        var detail = Assert.IsType<ReadabilityAuditDetailSnapshot>(
            session.State.ReadabilityAudit.Detail);
        Assert.Equal("commit-new", committed.Snapshot.ProjectionCommitId);
        Assert.Equal("snapshot-new", committed.SnapshotReference);
        Assert.Equal(2, committed.PageNumber);
        Assert.DoesNotContain(committed.Items, item => item.DemandId == "demand-a");
        Assert.Equal("demand-a", session.State.ReadabilityAudit.SelectedId);
        Assert.Equal(committed.SnapshotReference, detail.SnapshotReference);
        Assert.Equal(
            committed.Snapshot.ProjectionCommitId,
            detail.Snapshot.ProjectionCommitId);

        await AdvanceOneIntervalAsync(clock, coordinator);

        Assert.Equal(4, client.ReadabilityAuditQueries.Count);
        Assert.Equal(1, client.ReadabilityAuditQueries[3].PageNumber);
        Assert.Null(client.ReadabilityAuditQueries[3].SnapshotReference);
        Assert.Equal("commit-newer",
            session.State.ReadabilityAudit.Snapshot!.Snapshot.ProjectionCommitId);
        Assert.Null(session.State.ReadabilityAudit.SelectedId);
        Assert.Null(session.State.ReadabilityAudit.Detail);
        Assert.Equal(
            ["snapshot-old", "snapshot-new", "snapshot-newer"],
            client.ReadabilityAuditDetailSnapshotReferences);
        Assert.Equal(
            WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
            session.State.ReadabilityAudit.SelectionNotice);

        ReadabilityAuditListSnapshot ClientAuditResponse(ReadabilityAuditQuery query) =>
            client.ReadabilityAuditQueries.Count switch
            {
                1 => RecordingV2Client.ReadabilityAuditSnapshot(
                    query,
                    "commit-old",
                    "snapshot-old",
                    totalPages: 4,
                    demandId: "demand-a"),
                2 => RecordingV2Client.ReadabilityAuditSnapshot(
                    query,
                    "commit-new",
                    "snapshot-new",
                    totalPages: 2,
                    demandId: "demand-first-page"),
                3 => RecordingV2Client.ReadabilityAuditSnapshot(
                    query,
                    "commit-new",
                    "snapshot-new",
                    totalPages: 2,
                    demandId: "demand-converged-page"),
                4 => RecordingV2Client.ReadabilityAuditSnapshot(
                    query,
                    "commit-newer",
                    "snapshot-newer",
                    totalPages: 1,
                    demandId: "demand-b"),
                _ => throw new InvalidOperationException("Unexpected ReadabilityAudit request."),
            };
    }

    [Fact]
    public async Task Busy_due_tick_is_not_overlapped_or_queued_by_the_coordinator()
    {
        var clock = new ManualTimerTimeProvider(
            DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
        var firstResponse = new TaskCompletionSource<WatchOverviewSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingV2Client();
        client.OverviewHandler = (query, _) => client.OverviewCallCount == 1
            ? firstResponse.Task
            : Task.FromResult(RecordingV2Client.OverviewSnapshot(query));
        using var session = new WatchV2WorkspaceSession(_ => client, clock);
        await session.ApplyAsync(ValidHostSettings());
        using var coordinator = new WatchV2AutoRefreshCoordinator(
            session,
            WatchV2AutoRefreshSettings.Default,
            clock);
        coordinator.ActivateOverview(new WatchOverviewQuery());

        clock.Advance(TimeSpan.FromSeconds(10));
        var firstRefresh = coordinator.WaitForIdleAsync();
        Assert.Equal(1, client.OverviewCallCount);
        Assert.False(firstRefresh.IsCompleted);

        coordinator.Update(
            WatchV2DataView.Overview,
            new WatchV2AutoRefreshSetting(10));
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(1, client.OverviewCallCount);
        firstResponse.SetResult(RecordingV2Client.OverviewSnapshot(new WatchOverviewQuery()));
        await firstRefresh;
        Assert.Equal(1, client.OverviewCallCount);

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, client.OverviewCallCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        await coordinator.WaitForIdleAsync();
        Assert.Equal(2, client.OverviewCallCount);
    }

    [Fact]
    public async Task Refresh_notifications_observe_started_after_the_session_enters_refreshing_and_completed_after_success_commits()
    {
        var clock = new ManualTimerTimeProvider(
            DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
        var response = new TaskCompletionSource<WatchOverviewSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingV2Client
        {
            OverviewHandler = (_, _) => response.Task,
        };
        using var session = new WatchV2WorkspaceSession(_ => client, clock);
        await session.ApplyAsync(ValidHostSettings());
        using var coordinator = new WatchV2AutoRefreshCoordinator(
            session,
            WatchV2AutoRefreshSettings.Default,
            clock);
        var observations = new List<RefreshObservation>();
        coordinator.RefreshStateChanged += (_, change) => observations.Add(new(
            change.View,
            change.Phase,
            session.State.Overview.IsRefreshing,
            session.State.Overview.Snapshot,
            session.State.Overview.LastFailureAt));
        var query = new WatchOverviewQuery(["A1-1"]);
        coordinator.ActivateOverview(query);

        clock.Advance(TimeSpan.FromSeconds(10));
        var refresh = coordinator.WaitForIdleAsync();

        var started = Assert.Single(observations);
        Assert.Equal(WatchV2DataView.Overview, started.View);
        Assert.Equal(WatchV2AutoRefreshPhase.Started, started.Phase);
        Assert.True(started.IsRefreshing);
        Assert.Null(started.Snapshot);

        var snapshot = RecordingV2Client.OverviewSnapshot(query);
        response.SetResult(snapshot);
        await refresh;

        Assert.Collection(
            observations,
            value => Assert.Equal(WatchV2AutoRefreshPhase.Started, value.Phase),
            value =>
            {
                Assert.Equal(WatchV2DataView.Overview, value.View);
                Assert.Equal(WatchV2AutoRefreshPhase.Completed, value.Phase);
                Assert.False(value.IsRefreshing);
                Assert.Same(snapshot, value.Snapshot);
                Assert.Null(value.LastFailureAt);
            });
    }

    [Fact]
    public async Task Refresh_completion_notification_observes_the_committed_failure_state()
    {
        var clock = new ManualTimerTimeProvider(
            DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
        var response = new TaskCompletionSource<WatchOverviewSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingV2Client
        {
            OverviewHandler = (_, _) => response.Task,
        };
        using var session = new WatchV2WorkspaceSession(_ => client, clock);
        await session.ApplyAsync(ValidHostSettings());
        using var coordinator = new WatchV2AutoRefreshCoordinator(
            session,
            WatchV2AutoRefreshSettings.Default,
            clock);
        var observations = new List<RefreshObservation>();
        coordinator.RefreshStateChanged += (_, change) => observations.Add(new(
            change.View,
            change.Phase,
            session.State.Overview.IsRefreshing,
            session.State.Overview.Snapshot,
            session.State.Overview.LastFailureAt));
        coordinator.ActivateOverview(new WatchOverviewQuery());

        clock.Advance(TimeSpan.FromSeconds(10));
        var refresh = coordinator.WaitForIdleAsync();
        var started = Assert.Single(observations);
        Assert.Equal(WatchV2AutoRefreshPhase.Started, started.Phase);
        Assert.True(started.IsRefreshing);

        response.SetException(new InvalidOperationException(
            "scripted automatic refresh failure"));
        await refresh;

        Assert.Collection(
            observations,
            value =>
            {
                Assert.Equal(WatchV2AutoRefreshPhase.Started, value.Phase);
                Assert.True(value.IsRefreshing);
            },
            value =>
            {
                Assert.Equal(WatchV2AutoRefreshPhase.Completed, value.Phase);
                Assert.False(value.IsRefreshing);
                Assert.Null(value.Snapshot);
                Assert.Equal(clock.GetUtcNow(), value.LastFailureAt);
            });
        Assert.Equal(WatchHostFailureKind.Unknown, session.State.Overview.FailureKind);
    }

    [Fact]
    public async Task Throwing_notification_subscribers_do_not_break_single_flight_or_other_subscribers()
    {
        var clock = new ManualTimerTimeProvider(
            DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
        var client = new RecordingV2Client();
        using var session = new WatchV2WorkspaceSession(_ => client, clock);
        await session.ApplyAsync(ValidHostSettings());
        using var coordinator = new WatchV2AutoRefreshCoordinator(
            session,
            WatchV2AutoRefreshSettings.Default,
            clock);
        var phases = new List<WatchV2AutoRefreshPhase>();
        coordinator.RefreshStateChanged += (_, _) =>
            throw new InvalidOperationException("observer failure must be isolated");
        coordinator.RefreshStateChanged += (_, change) => phases.Add(change.Phase);
        coordinator.ActivateOverview(new WatchOverviewQuery());

        await AdvanceOneIntervalAsync(clock, coordinator);
        await AdvanceOneIntervalAsync(clock, coordinator);

        Assert.Equal(2, client.OverviewCallCount);
        Assert.Equal(
            [
                WatchV2AutoRefreshPhase.Started,
                WatchV2AutoRefreshPhase.Completed,
                WatchV2AutoRefreshPhase.Started,
                WatchV2AutoRefreshPhase.Completed,
            ],
            phases);
        Assert.Null(coordinator.LastUnhandledException);
    }

    [Fact]
    public async Task Disposal_suppresses_completion_notification_from_an_in_flight_refresh()
    {
        var clock = new ManualTimerTimeProvider(
            DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
        var response = new TaskCompletionSource<WatchOverviewSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingV2Client
        {
            OverviewHandler = (_, _) => response.Task,
        };
        using var session = new WatchV2WorkspaceSession(_ => client, clock);
        await session.ApplyAsync(ValidHostSettings());
        var coordinator = new WatchV2AutoRefreshCoordinator(
            session,
            WatchV2AutoRefreshSettings.Default,
            clock);
        var phases = new List<WatchV2AutoRefreshPhase>();
        coordinator.RefreshStateChanged += (_, change) => phases.Add(change.Phase);
        coordinator.ActivateOverview(new WatchOverviewQuery());

        clock.Advance(TimeSpan.FromSeconds(10));
        var refresh = coordinator.WaitForIdleAsync();
        coordinator.Dispose();
        response.SetResult(RecordingV2Client.OverviewSnapshot(new WatchOverviewQuery()));
        await refresh;

        Assert.Equal([WatchV2AutoRefreshPhase.Started], phases);
    }

    [Fact]
    public async Task Updating_the_active_interval_rearms_the_due_time()
    {
        var clock = new ManualTimerTimeProvider(
            DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
        var client = new RecordingV2Client();
        using var session = new WatchV2WorkspaceSession(_ => client, clock);
        await session.ApplyAsync(ValidHostSettings());
        var settings = WatchV2AutoRefreshSettings.Default.With(
            WatchV2DataView.Overview,
            new WatchV2AutoRefreshSetting(30));
        using var coordinator = new WatchV2AutoRefreshCoordinator(session, settings, clock);
        coordinator.ActivateOverview(new WatchOverviewQuery());

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, client.OverviewCallCount);

        coordinator.Update(
            WatchV2DataView.Overview,
            new WatchV2AutoRefreshSetting(10));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, client.OverviewCallCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        await coordinator.WaitForIdleAsync();
        Assert.Equal(1, client.OverviewCallCount);
        Assert.Equal(10, coordinator.Settings.Overview.IntervalSeconds);
    }

    [Fact]
    public async Task Disposal_stops_future_automatic_refreshes()
    {
        var clock = new ManualTimerTimeProvider(
            DateTimeOffset.Parse("2026-08-14T08:00:00Z"));
        var client = new RecordingV2Client();
        using var session = new WatchV2WorkspaceSession(_ => client, clock);
        await session.ApplyAsync(ValidHostSettings());
        var coordinator = new WatchV2AutoRefreshCoordinator(
            session,
            WatchV2AutoRefreshSettings.Default,
            clock);
        coordinator.ActivateOverview(new WatchOverviewQuery());

        coordinator.Dispose();
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, client.OverviewCallCount);
        Assert.Throws<ObjectDisposedException>(() => coordinator.ActivateOverview(
            new WatchOverviewQuery()));
    }

    [Fact]
    public void Settings_cover_all_five_host_data_views_and_expose_only_an_interval()
    {
        var views = Enum.GetValues<WatchV2DataView>();

        Assert.Equal(
            new[]
            {
                WatchV2DataView.Overview,
                WatchV2DataView.DemandSeries,
                WatchV2DataView.ReadabilityAudit,
                WatchV2DataView.ErrorSearch,
                WatchV2DataView.CurrentIngestAttention,
            },
            views);
        foreach (var view in views)
        {
            Assert.Equal(10, WatchV2AutoRefreshSettings.Default.For(view).IntervalSeconds);
        }

        Assert.Null(typeof(WatchV2AutoRefreshSetting).GetProperty("Enabled"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(300)]
    public void Supported_intervals_are_accepted(int intervalSeconds)
    {
        var setting = new WatchV2AutoRefreshSetting(intervalSeconds);

        Assert.Equal(intervalSeconds, setting.IntervalSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(301)]
    public void Unsupported_intervals_are_rejected(int intervalSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WatchV2AutoRefreshSetting(intervalSeconds));
    }

    [Fact]
    public void Automatic_refresh_is_always_scheduled_for_only_the_current_host_data_view()
    {
        var start = DateTimeOffset.Parse("2026-08-14T08:00:00Z");
        var clock = new AdjustableTimeProvider(start);
        var settings = WatchV2AutoRefreshSettings.Default
            .With(
                WatchV2DataView.DemandSeries,
                new WatchV2AutoRefreshSetting(10))
            .With(
                WatchV2DataView.ErrorSearch,
                new WatchV2AutoRefreshSetting(30));
        var schedule = new WatchV2AutoRefreshSchedule(settings, clock);

        schedule.Activate(WatchV2DataView.DemandSeries);
        clock.SetUtcNow(start.AddSeconds(5));
        schedule.Activate(WatchV2DataView.ErrorSearch);

        clock.SetUtcNow(start.AddSeconds(10));
        Assert.Null(schedule.TryTakeDue(refreshInProgress: false));
        clock.SetUtcNow(start.AddSeconds(35));
        Assert.Equal(
            WatchV2DataView.ErrorSearch,
            schedule.TryTakeDue(refreshInProgress: false));
    }

    [Fact]
    public void Busy_due_tick_is_dropped_and_completion_restarts_the_full_interval()
    {
        var start = DateTimeOffset.Parse("2026-08-14T08:00:00Z");
        var clock = new AdjustableTimeProvider(start);
        var schedule = new WatchV2AutoRefreshSchedule(
            WatchV2AutoRefreshSettings.Default,
            clock);
        schedule.Activate(WatchV2DataView.Overview);

        clock.SetUtcNow(start.AddSeconds(10));
        Assert.Null(schedule.TryTakeDue(refreshInProgress: true));
        clock.SetUtcNow(start.AddSeconds(11));
        Assert.Null(schedule.TryTakeDue(refreshInProgress: false));

        clock.SetUtcNow(start.AddSeconds(15));
        schedule.CompleteRefresh(WatchV2DataView.Overview);
        clock.SetUtcNow(start.AddSeconds(24));
        Assert.Null(schedule.TryTakeDue(refreshInProgress: false));
        clock.SetUtcNow(start.AddSeconds(25));
        Assert.Equal(
            WatchV2DataView.Overview,
            schedule.TryTakeDue(refreshInProgress: false));
    }

    private static WatchHostSettings ValidHostSettings() => new(
        "http://localhost:5000",
        "test-token",
        5);

    private static async Task AdvanceOneIntervalAsync(
        ManualTimerTimeProvider clock,
        WatchV2AutoRefreshCoordinator coordinator)
    {
        clock.Advance(TimeSpan.FromSeconds(10));
        await coordinator.WaitForIdleAsync();
    }

    private sealed record RefreshObservation(
        WatchV2DataView View,
        WatchV2AutoRefreshPhase Phase,
        bool IsRefreshing,
        WatchOverviewSnapshot? Snapshot,
        DateTimeOffset? LastFailureAt);

    private sealed class RecordingV2Client : IWatchV2ApiClient
    {
        public Func<WatchOverviewQuery, CancellationToken, Task<WatchOverviewSnapshot>>?
            OverviewHandler { get; set; }

        public Func<DemandSeriesBrowseQuery, CancellationToken, Task<DemandSeriesListSnapshot>>?
            DemandSeriesHandler { get; set; }

        public Func<ReadabilityAuditQuery, CancellationToken, Task<ReadabilityAuditListSnapshot>>?
            ReadabilityAuditHandler { get; set; }

        public Func<string, string, CancellationToken, Task<ReadabilityAuditDetailSnapshot>>?
            ReadabilityAuditDetailHandler { get; set; }

        public int OverviewCallCount { get; private set; }

        public int DemandSeriesCallCount { get; private set; }

        public List<DemandSeriesBrowseQuery> DemandSeriesQueries { get; } = [];

        public int ReadabilityAuditCallCount { get; private set; }

        public List<ReadabilityAuditQuery> ReadabilityAuditQueries { get; } = [];

        public List<string> ReadabilityAuditDetailSnapshotReferences { get; } = [];

        public int ErrorSearchCallCount { get; private set; }

        public int CurrentAttentionCallCount { get; private set; }

        public Task VerifyContractAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<WatchOverviewSnapshot> FetchOverviewAsync(
            WatchOverviewQuery query,
            CancellationToken cancellationToken = default)
        {
            OverviewCallCount++;
            return OverviewHandler?.Invoke(query, cancellationToken)
                ?? Task.FromResult(OverviewSnapshot(query));
        }

        public static WatchOverviewSnapshot OverviewSnapshot(WatchOverviewQuery query)
        {
            var at = DateTimeOffset.Parse("2026-08-14T08:00:00Z");
            var identity = new OperationalSnapshotIdentity(
                "commit-overview",
                1,
                at,
                "poll-overview",
                1,
                1,
                at);
            var series = new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries);
            var audit = new OverviewNavigationIntent(OverviewNavigationTargets.ReadabilityAudit);
            var errors = new OverviewNavigationIntent(OverviewNavigationTargets.ErrorSearch);
            var attention = new OverviewNavigationIntent(
                OverviewNavigationTargets.CurrentIngestAttention);
            return new WatchOverviewSnapshot(
                identity,
                query.MesAreas ?? [],
                new WatchOverviewSeriesSummary(
                    0,
                    0,
                    0,
                    0,
                    0,
                    series,
                    series,
                    series,
                    series,
                    series),
                new WatchOverviewReadabilitySummary(0, 0, 0, audit, audit, audit),
                new WatchOverviewErrorSummary(0, 0, errors, errors, errors),
                new WatchOverviewAttentionSummary(0, [], [], attention),
                [],
                WatchOverviewRecentActivityStates.NoRecentHighlights,
                WatchOverviewRecentActivityStates.NoRecentHighlightsMessage);
        }

        public Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
            DemandSeriesBrowseQuery query,
            CancellationToken cancellationToken = default)
        {
            DemandSeriesCallCount++;
            DemandSeriesQueries.Add(query);
            return DemandSeriesHandler?.Invoke(query, cancellationToken)
                ?? Task.FromResult(DemandSeriesSnapshot(
                    query,
                    "commit-demand",
                    "snapshot-demand",
                    totalPages: 0));
        }

        public static DemandSeriesListSnapshot DemandSeriesSnapshot(
            DemandSeriesBrowseQuery query,
            string projectionCommitId,
            string snapshotReference,
            int totalPages)
        {
            var at = DateTimeOffset.Parse("2026-08-14T08:00:00Z");
            return new DemandSeriesListSnapshot(
                new DemandSeriesSnapshotIdentity(
                    projectionCommitId,
                    1,
                    at,
                    "poll-demand"),
                snapshotReference,
                query.Filter,
                query.Order,
                0,
                new DemandSeriesFacets(0, 0, 0, 0, 0),
                query.PageSize,
                query.PageNumber,
                totalPages,
                [],
                null,
                false);
        }

        public Task<DemandSeriesDetailSnapshot> FetchDemandSeriesDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReadabilityAuditListSnapshot> FetchReadabilityAuditAsync(
            ReadabilityAuditQuery query,
            CancellationToken cancellationToken = default)
        {
            ReadabilityAuditCallCount++;
            ReadabilityAuditQueries.Add(query);
            return ReadabilityAuditHandler?.Invoke(query, cancellationToken)
                ?? Task.FromResult(ReadabilityAuditSnapshot(
                    query,
                    "commit-audit",
                    "snapshot-audit",
                    totalPages: 0));
        }

        public Task<ReadabilityAuditDetailSnapshot> FetchReadabilityAuditDetailAsync(
            string demandId,
            string snapshotReference,
            CancellationToken cancellationToken = default)
        {
            ReadabilityAuditDetailSnapshotReferences.Add(snapshotReference);
            return ReadabilityAuditDetailHandler?.Invoke(
                    demandId,
                    snapshotReference,
                    cancellationToken)
                ?? Task.FromException<ReadabilityAuditDetailSnapshot>(
                    new NotSupportedException());
        }

        public static ReadabilityAuditListSnapshot ReadabilityAuditSnapshot(
            ReadabilityAuditQuery query,
            string projectionCommitId,
            string snapshotReference,
            int totalPages,
            string? demandId = null)
        {
            var at = DateTimeOffset.Parse("2026-08-14T08:00:00Z");
            var items = demandId is null
                ? Array.Empty<ReadabilityAuditListItemSnapshot>()
                :
                [
                    new ReadabilityAuditListItemSnapshot(
                        demandId,
                        "series-a",
                        "WIRE_TO_NITROGEN",
                        "SL-20",
                        1,
                        null,
                        "ACTIVE",
                        DemandSeriesLifecycleContract.Tracking,
                        "VISIBLE",
                        true,
                        at,
                        at,
                        null,
                        null,
                        1,
                        ExternalReadabilityStates.NotReadable,
                        "DEMAND_GONE",
                        ["DEMAND_GONE"],
                        "poll-audit",
                        projectionCommitId,
                        at),
                ];
            return new ReadabilityAuditListSnapshot(
                new ReadabilityAuditSnapshotIdentity(
                    projectionCommitId,
                    1,
                    at,
                    "poll-audit",
                    1),
                snapshotReference,
                query.Filter,
                query.Order,
                items.Length,
                new ReadabilityAuditFacets([], []),
                query.PageSize,
                query.PageNumber,
                totalPages,
                items,
                null,
                false);
        }

        public static ReadabilityAuditDetailSnapshot ReadabilityAuditDetailSnapshot(
            string demandId,
            string snapshotReference)
        {
            var commit = snapshotReference["snapshot-".Length..];
            var list = ReadabilityAuditSnapshot(
                new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
                $"commit-{commit}",
                snapshotReference,
                totalPages: 1,
                demandId: demandId);
            return new ReadabilityAuditDetailSnapshot(
                list.Snapshot,
                snapshotReference,
                list.Items.Single(),
                null!,
                [],
                [],
                [],
                null!);
        }

        public Task<ErrorSearchListSnapshot> FetchErrorSearchAsync(
            ErrorSearchQuery query,
            CancellationToken cancellationToken = default)
        {
            ErrorSearchCallCount++;
            var at = DateTimeOffset.Parse("2026-08-14T08:00:00Z");
            return Task.FromResult(new ErrorSearchListSnapshot(
                "snapshot-errors",
                new ErrorSearchSnapshotIdentity(
                    at,
                    "commit-errors",
                    1,
                    at,
                    "poll-errors"),
                query.Filter,
                query.Window.Resolve(at),
                query.Order,
                0,
                new ErrorSearchFacets([], []),
                query.PageSize,
                1,
                0,
                [],
                null,
                false));
        }

        public Task<ErrorSearchDetailSnapshot> FetchErrorSearchDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ErrorSearchRawEvidenceSnapshot> FetchErrorRawEvidenceAsync(
            string seriesId,
            string evidenceId,
            string snapshotReference,
            ErrorSearchRawEvidenceQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CurrentIngestAttentionSnapshot> FetchCurrentAttentionAsync(
            CurrentIngestAttentionQuery query,
            CancellationToken cancellationToken = default)
        {
            CurrentAttentionCallCount++;
            var at = DateTimeOffset.Parse("2026-08-14T08:00:00Z");
            return Task.FromResult(new CurrentIngestAttentionSnapshot(
                new OperationalSnapshotIdentity(
                    "commit-attention",
                    1,
                    at,
                    "poll-attention",
                    1,
                    1,
                    at),
                0,
                new CurrentIngestAttentionFacets([], []),
                query.Order,
                query.PageSize,
                query.PageNumber,
                0,
                query.Kinds ?? [],
                query.Severities ?? [],
                []));
        }

        public void Dispose()
        {
        }
    }

    private sealed class ManualTimerTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            }

            lock (_gate)
            {
                _utcNow += elapsed;
            }

            while (TryTakeDueCallback(out var callback))
            {
                callback();
            }
        }

        private bool TryTakeDueCallback(out Action callback)
        {
            lock (_gate)
            {
                var timer = _timers.FirstOrDefault(value => value.IsDue(_utcNow));
                if (timer is null)
                {
                    callback = null!;
                    return false;
                }

                callback = timer.TakeCallback(_utcNow);
                return true;
            }
        }

        private sealed class ManualTimer(
            ManualTimerTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private DateTimeOffset? _dueAt;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public bool IsDue(DateTimeOffset utcNow) =>
                !_disposed && _dueAt is { } dueAt && dueAt <= utcNow;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _period = period;
                    _dueAt = dueTime == Timeout.InfiniteTimeSpan
                        ? null
                        : owner._utcNow + dueTime;
                    return true;
                }
            }

            public Action TakeCallback(DateTimeOffset utcNow)
            {
                _dueAt = _period == Timeout.InfiniteTimeSpan
                    ? null
                    : utcNow + _period;
                return () => callback(state);
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    _disposed = true;
                    _dueAt = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
