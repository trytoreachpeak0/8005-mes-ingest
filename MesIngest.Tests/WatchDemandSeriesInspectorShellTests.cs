using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

[Collection("WpfDesktop")]
public sealed class WatchDemandSeriesInspectorShellTests
{
    [Fact]
    public void Closed_inspector_navigation_and_selection_do_not_fetch_invisible_detail()
        => StaTestRunner.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-inspector-closed-no-fetch-{Guid.NewGuid():N}");
            var client = new DemandSeriesClient(itemCount: 2);
            var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
            using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
            using var window = CreateWindow(root, client, inspector);
            window.InitializeAsync().GetAwaiter().GetResult();
            window.NavigateFromOverview(
                new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
            window.DemandSeriesNavigationTask.GetAwaiter().GetResult();

            var grid = Assert.IsType<DataGrid>(window.FindName("DemandSeriesGrid"));
            Assert.Equal("series-a", window.WorkspaceState.DemandSeries.SelectedId);
            Assert.Equal(0, client.DetailFetchCount);

            grid.SelectedItem = grid.Items.Cast<WatchDemandSeriesRowPresentation>()
                .Single(row => row.SeriesId == "series-b");

            Assert.Equal("series-b", window.WorkspaceState.DemandSeries.SelectedId);
            Assert.Equal(0, client.DetailFetchCount);
            Assert.False(inspector.IsOpen);

            window.Close();
            TryDelete(root);
        });

    [Fact]
    public void First_row_is_selected_and_explicit_enter_and_double_click_reuse_the_inspector() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-inspector-shell-{Guid.NewGuid():N}");
            var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
            using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
            using var window = CreateWindow(root, new DemandSeriesClient(itemCount: 2), inspector);
            window.InitializeAsync().GetAwaiter().GetResult();
            window.NavigateFromOverview(
                new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
            window.DemandSeriesNavigationTask.GetAwaiter().GetResult();
            window.Show();
            window.UpdateLayout();

            var grid = Assert.IsType<DataGrid>(window.FindName("DemandSeriesGrid"));
            var selected = Assert.IsType<WatchDemandSeriesRowPresentation>(grid.SelectedItem);
            Assert.Equal("series-a", selected.SeriesId);
            var command = Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesOpenInspectorButton"));
            Assert.True(command.IsEnabled);
            Assert.Equal("打开详情窗口", command.Content);
            Assert.Equal("打开 DemandSeries 详情窗口", AutomationProperties.GetName(command));

            command.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, inspectorWindow.ShowCount);
            Assert.Equal(1, inspectorWindow.ActivateCount);
            Assert.Equal("series-a", inspectorWindow.Presentation?.SeriesId);
            Assert.Equal(
                "首次观察到",
                inspectorWindow.Presentation?.FocusedGeneration.FormationReason.ChineseLabel);
            Assert.Equal(
                WatchDemandMesBoundaryState.NotApplicable,
                inspectorWindow.Presentation?.FocusedGeneration.MesBoundary.Before.State);
            Assert.Equal(
                WatchDemandMesBoundaryState.Unique,
                inspectorWindow.Presentation?.FocusedGeneration.MesBoundary.After.State);
            Assert.Equal("显示详情窗口", command.Content);

            RaiseKey(grid, Key.Enter);
            RaiseDoubleClick(grid);

            Assert.Equal(1, inspectorWindow.ShowCount);
            Assert.Equal(3, inspectorWindow.ActivateCount);
            Assert.True(inspector.IsOpen);

            window.Close();
            TryDelete(root);
        });

    [Fact]
    public void Space_never_opens_or_activates_and_selection_updates_open_content_without_focus_stealing() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-inspector-space-{Guid.NewGuid():N}");
            var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
            using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
            using var window = CreateWindow(root, new DemandSeriesClient(itemCount: 2), inspector);
            window.InitializeAsync().GetAwaiter().GetResult();
            window.NavigateFromOverview(
                new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
            window.DemandSeriesNavigationTask.GetAwaiter().GetResult();
            window.Show();
            window.UpdateLayout();

            var grid = Assert.IsType<DataGrid>(window.FindName("DemandSeriesGrid"));
            Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesOpenInspectorButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var activationCount = inspectorWindow.ActivateCount;

            RaiseKey(grid, Key.Space);
            Assert.Equal(activationCount, inspectorWindow.ActivateCount);

            grid.SelectedItem = grid.Items.Cast<WatchDemandSeriesRowPresentation>()
                .Single(row => row.SeriesId == "series-b");

            Assert.Equal("series-b", inspectorWindow.Presentation?.SeriesId);
            Assert.Equal(activationCount, inspectorWindow.ActivateCount);

            window.Close();
            TryDelete(root);
        });

    [Fact]
    public void Empty_result_disables_open_and_inline_detail_controls_no_longer_exist() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-inspector-empty-{Guid.NewGuid():N}");
            var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
            using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
            using var window = CreateWindow(root, new DemandSeriesClient(itemCount: 0), inspector);
            window.InitializeAsync().GetAwaiter().GetResult();
            window.NavigateFromOverview(
                new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
            window.DemandSeriesNavigationTask.GetAwaiter().GetResult();

            Assert.False(Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesOpenInspectorButton")).IsEnabled);
            Assert.Null(window.FindName("DemandSeriesDetailPanel"));
            Assert.Null(window.FindName("DemandSeriesDetailVisibilityToggle"));
            Assert.Null(window.FindName("DemandSeriesMasterDetailSplitter"));
            Assert.False(inspector.IsOpen);

            window.Close();
            TryDelete(root);
        });

    [Fact]
    public void Refresh_that_loses_an_existing_selection_clears_instead_of_selecting_another_series() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-inspector-selection-loss-{Guid.NewGuid():N}");
            var client = new DemandSeriesClient(itemCount: 2);
            var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
            using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
            using var window = CreateWindow(root, client, inspector);
            window.InitializeAsync().GetAwaiter().GetResult();
            window.NavigateFromOverview(
                new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
            window.DemandSeriesNavigationTask.GetAwaiter().GetResult();
            Assert.Equal("series-a", window.WorkspaceState.DemandSeries.SelectedId);

            client.RemoveFirstSeries = true;
            Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesApplyFiltersButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            var grid = Assert.IsType<DataGrid>(window.FindName("DemandSeriesGrid"));
            Assert.Equal(["series-b"], grid.Items.Cast<WatchDemandSeriesRowPresentation>()
                .Select(row => row.SeriesId)
                .ToArray());
            Assert.Null(window.WorkspaceState.DemandSeries.SelectedId);
            Assert.Null(grid.SelectedItem);
            Assert.False(Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesOpenInspectorButton")).IsEnabled);

            window.Close();
            TryDelete(root);
        });

    [Fact]
    public void Refresh_that_loses_open_target_clears_inspector_body_without_choosing_another_series() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-inspector-open-selection-loss-{Guid.NewGuid():N}");
            var client = new DemandSeriesClient(itemCount: 2);
            var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
            using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
            using var window = CreateWindow(root, client, inspector);
            window.InitializeAsync().GetAwaiter().GetResult();
            window.NavigateFromOverview(
                new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
            window.DemandSeriesNavigationTask.GetAwaiter().GetResult();
            Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesOpenInspectorButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("series-a", inspectorWindow.Presentation?.SeriesId);

            client.RemoveFirstSeries = true;
            Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesApplyFiltersButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.True(inspector.IsOpen);
            Assert.Null(window.WorkspaceState.DemandSeries.SelectedId);
            Assert.Null(inspectorWindow.State);
            Assert.Null(inspectorWindow.Presentation);

            window.Close();
            TryDelete(root);
        });

    [Fact]
    public void Double_click_during_a_new_selection_load_opens_the_still_current_series_after_commit() =>
        StaTestRunner.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), $"watch-inspector-pending-open-{Guid.NewGuid():N}");
            try
            {
                var client = new DemandSeriesClient(itemCount: 2)
                {
                    DelaySeriesBDetail = true,
                };
                var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
                using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
                using var window = CreateWindow(root, client, inspector);
                window.InitializeAsync().GetAwaiter().GetResult();
                window.NavigateFromOverview(
                    new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
                window.DemandSeriesNavigationTask.GetAwaiter().GetResult();
                window.Show();
                window.UpdateLayout();

            var grid = Assert.IsType<DataGrid>(window.FindName("DemandSeriesGrid"));
            grid.SelectedItem = grid.Items.Cast<WatchDemandSeriesRowPresentation>()
                .Single(row => row.SeriesId == "series-b");
            Assert.False(window.WorkspaceState.DemandSeries.IsDetailLoading);
            Assert.Equal(0, client.DetailFetchCount);

            RaiseDoubleClick(grid);

            Assert.True(inspector.IsOpen);
            Assert.Equal("series-b", inspectorWindow.State?.SeriesId);
            Assert.Null(inspectorWindow.Presentation);
            Assert.True(window.WorkspaceState.DemandSeries.IsDetailLoading);
            Assert.Equal(1, client.DetailFetchCount);
            client.ReleaseSeriesBDetail();
            PumpDispatcherUntil(() => inspectorWindow.Presentation is not null);

                Assert.Equal("series-b", inspectorWindow.Presentation?.SeriesId);
                Assert.Equal(1, inspectorWindow.ShowCount);
                Assert.Equal(1, inspectorWindow.ActivateCount);

                window.Close();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                TryDelete(root);
            }
        });

    [Fact]
    public void Closing_inspector_cancels_pending_detail_and_rejects_its_late_body() =>
        StaTestRunner.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-inspector-close-cancel-{Guid.NewGuid():N}");
            try
            {
                var client = new DemandSeriesClient(itemCount: 2)
                {
                    DelaySeriesBDetail = true,
                };
                var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
                using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
                using var window = CreateWindow(root, client, inspector);
                window.InitializeAsync().GetAwaiter().GetResult();
                window.NavigateFromOverview(
                    new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
                window.DemandSeriesNavigationTask.GetAwaiter().GetResult();
                window.Show();
                window.UpdateLayout();

                var grid = Assert.IsType<DataGrid>(window.FindName("DemandSeriesGrid"));
                grid.SelectedItem = grid.Items.Cast<WatchDemandSeriesRowPresentation>()
                    .Single(row => row.SeriesId == "series-b");
                RaiseDoubleClick(grid);
                Assert.True(window.WorkspaceState.DemandSeries.IsDetailLoading);

                inspectorWindow.SimulateClose();
                PumpDispatcherUntil(() => client.DetailCancellationCount == 1);
                client.ReleaseSeriesBDetail();
                PumpDispatcherUntil(() => !window.WorkspaceState.DemandSeries.IsDetailLoading);

                Assert.False(inspector.IsOpen);
                Assert.Null(window.WorkspaceState.DemandSeries.Detail);
                window.Close();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                TryDelete(root);
            }
        });

    [Fact]
    public void Same_series_refresh_failure_keeps_the_last_body_and_marks_it_stale() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-inspector-stale-retain-{Guid.NewGuid():N}");
            var client = new DemandSeriesClient(itemCount: 2);
            var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
            using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
            using var window = CreateWindow(root, client, inspector);
            window.InitializeAsync().GetAwaiter().GetResult();
            window.NavigateFromOverview(
                new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
            window.DemandSeriesNavigationTask.GetAwaiter().GetResult();
            window.Show();
            window.UpdateLayout();
            Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesOpenInspectorButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var retained = Assert.IsType<WatchDemandSeriesInspectorPresentation>(
                inspectorWindow.Presentation);

            client.FailNextDetail = true;
            Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesApplyFiltersButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(retained.SeriesId, inspectorWindow.Presentation?.SeriesId);
            Assert.Equal(
                retained.FocusedGeneration.DemandId,
                inspectorWindow.Presentation?.FocusedGeneration.DemandId);
            Assert.True(inspectorWindow.State?.IsStale);
            Assert.Equal("详情刷新失败，已保留上次证据", inspectorWindow.State?.StatusTitle);
            Assert.Equal(retained.FrozenSnapshot, inspectorWindow.State?.FrozenSnapshot);

            window.Close();
            TryDelete(root);
        });

    [Fact]
    public void Exact_overview_drill_locates_series_opens_inspector_and_carries_source_fence() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-inspector-exact-overview-{Guid.NewGuid():N}");
            var client = new DemandSeriesClient(itemCount: 2);
            var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
            using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
            using var window = CreateWindow(root, client, inspector);
            window.InitializeAsync().GetAwaiter().GetResult();

            window.NavigateFromOverview(new OverviewNavigationIntent(
                OverviewNavigationTargets.DemandSeriesDetail,
                SeriesId: "series-b"));
            window.DemandSeriesNavigationTask.GetAwaiter().GetResult();

            Assert.Equal("series-b", window.WorkspaceState.DemandSeries.SelectedId);
            Assert.True(inspector.IsOpen);
            Assert.Equal("series-b", inspectorWindow.Presentation?.SeriesId);
            Assert.Contains("概览", inspectorWindow.State?.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("overview-commit", inspectorWindow.State?.StatusMessage, StringComparison.Ordinal);

            window.Close();
            TryDelete(root);
        });

    [Fact]
    public async Task Focus_change_invalidates_pending_detail_before_it_can_commit()
    {
        var client = new DemandSeriesClient(itemCount: 2)
        {
            DelaySeriesBDetail = true,
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "secret", 30));
        await session.RefreshLatestDemandSeriesPageAsync(
            new DemandSeriesBrowseQuery(new DemandSeriesBrowseFilter()));
        session.SetDemandSeriesSelection("series-b");
        session.SetDemandSeriesFocus("demand-b");

        var pending = session.LoadSelectedDemandSeriesDetailAsync();
        Assert.True(session.State.DemandSeries.IsDetailLoading);

        session.SetDemandSeriesFocus("demand-other");
        await pending;

        Assert.Equal(1, client.DetailCancellationCount);
        Assert.Equal("demand-other", session.State.DemandSeries.DetailFocusId);
        Assert.False(session.State.DemandSeries.IsDetailLoading);
        Assert.Null(session.State.DemandSeries.Detail);
    }

    [Fact]
    public void Switching_series_to_a_failed_target_never_keeps_the_previous_body() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-inspector-switch-failure-{Guid.NewGuid():N}");
            var client = new DemandSeriesClient(itemCount: 2);
            var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
            using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
            using var window = CreateWindow(root, client, inspector);
            window.InitializeAsync().GetAwaiter().GetResult();
            window.NavigateFromOverview(
                new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
            window.DemandSeriesNavigationTask.GetAwaiter().GetResult();
            window.Show();
            window.UpdateLayout();
            Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesOpenInspectorButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("series-a", inspectorWindow.Presentation?.SeriesId);

            client.FailNextDetail = true;
            var grid = Assert.IsType<DataGrid>(window.FindName("DemandSeriesGrid"));
            grid.SelectedItem = grid.Items.Cast<WatchDemandSeriesRowPresentation>()
                .Single(row => row.SeriesId == "series-b");

            Assert.Equal("series-b", inspectorWindow.State?.SeriesId);
            Assert.Null(inspectorWindow.Presentation);
            Assert.Equal("详情读取失败", inspectorWindow.State?.StatusTitle);

            window.Close();
            TryDelete(root);
        });

    [Fact]
    public void Leaving_demand_series_pauses_page_refresh_and_keeps_inspector_snapshot() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-inspector-page-pause-{Guid.NewGuid():N}");
            var client = new DemandSeriesClient(itemCount: 2);
            var inspectorWindow = new RecordingDemandSeriesInspectorWindow();
            using var inspector = new WatchDemandSeriesInspectorCoordinator(() => inspectorWindow);
            using var window = CreateWindow(root, client, inspector);
            window.InitializeAsync().GetAwaiter().GetResult();
            window.NavigateFromOverview(
                new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries));
            window.DemandSeriesNavigationTask.GetAwaiter().GetResult();
            window.Show();
            window.UpdateLayout();
            Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("DemandSeriesOpenInspectorButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var retainedSnapshot = inspectorWindow.State?.FrozenSnapshot;
            var fetchCount = client.DetailFetchCount;

            Assert.IsAssignableFrom<ButtonBase>(window.FindName("OverviewNavigationItem"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(WatchWorkspacePage.Overview, window.ActivePage);
            Assert.True(inspectorWindow.State?.IsPaused);
            Assert.Equal(retainedSnapshot, inspectorWindow.State?.FrozenSnapshot);
            Assert.Equal(fetchCount, client.DetailFetchCount);
            Assert.Contains(
                "返回 DemandSeries 页面后恢复刷新",
                inspectorWindow.State?.StatusMessage,
                StringComparison.Ordinal);

            window.Close();
            TryDelete(root);
        });

    private static WatchWorkspaceWindow CreateWindow(
        string root,
        IWatchV2ApiClient client,
        WatchDemandSeriesInspectorCoordinator inspector) => new(
        new WatchHostSettings("http://host-a", "secret", 30),
        WatchV2Preferences.Default,
        Path.Combine(root, "connection.json"),
        Path.Combine(root, "workspace.json"),
        _ => client,
        initializeOnLoaded: false,
        demandSeriesInspectorCoordinator: inspector);

    private static void RaiseKey(DataGrid grid, Key key)
    {
        var source = PresentationSource.FromVisual(grid)
            ?? throw new InvalidOperationException("The grid must be connected to a presentation source.");
        grid.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
    }

    private static void RaiseDoubleClick(DataGrid grid)
    {
        grid.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
        });
    }

    private static void PumpDispatcherUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Dispatcher condition did not complete.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void TryDelete(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class DemandSeriesClient(int itemCount) : IWatchV2ApiClient
    {
        private static readonly DateTimeOffset At =
            DateTimeOffset.Parse("2026-08-21T01:00:00+08:00");

        public bool RemoveFirstSeries { get; set; }

        public bool DelaySeriesBDetail { get; init; }

        public int DetailFetchCount { get; private set; }

        public int DetailCancellationCount { get; private set; }

        public bool FailNextDetail { get; set; }

        private int ListFetchCount { get; set; }

        private TaskCompletionSource<DemandSeriesDetailSnapshot>? DelayedSeriesBDetail { get; set; }

        private DemandSeriesDetailSnapshot? SeriesBDetail { get; set; }

        public Task VerifyContractAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<WatchOverviewSnapshot> FetchOverviewAsync(
            WatchOverviewQuery query,
            CancellationToken cancellationToken = default)
        {
            var navigation = new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries);
            return Task.FromResult(new WatchOverviewSnapshot(
                new OperationalSnapshotIdentity("overview-commit", 1, At, "overview-poll", 1, 1, At),
                query.MesAreas ?? [],
                new WatchOverviewSeriesSummary(itemCount, itemCount, 0, 0, 0, navigation, navigation, navigation, navigation, navigation),
                new WatchOverviewReadabilitySummary(0, 0, 0, navigation, navigation, navigation),
                new WatchOverviewErrorSummary(0, 0, navigation, navigation, navigation),
                new WatchOverviewAttentionSummary(0, [], [], navigation),
                [],
                WatchOverviewRecentActivityStates.NoRecentHighlights,
                WatchOverviewRecentActivityStates.NoRecentHighlightsMessage));
        }

        public Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
            DemandSeriesBrowseQuery query,
            CancellationToken cancellationToken = default)
        {
            ListFetchCount++;
            var items = new[] { "series-a", "series-b" }
                .Take(itemCount)
                .Where(seriesId => !RemoveFirstSeries || seriesId != "series-a")
                .Select(Item)
                .ToArray();
            return Task.FromResult(new DemandSeriesListSnapshot(
                new DemandSeriesSnapshotIdentity("commit-list", 2, At, "poll-list"),
                $"snapshot-list-{ListFetchCount}",
                query.Filter,
                DemandSeriesBrowseOrder.Default,
                items.Length,
                new DemandSeriesFacets(items.Length, 0, items.Length, 0, 0),
                query.PageSize,
                1,
                items.Length == 0 ? 0 : 1,
                items,
                NextCursor: null,
                HasMore: false));
        }

        public Task<DemandSeriesDetailSnapshot> FetchDemandSeriesDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default)
        {
            DetailFetchCount++;
            if (FailNextDetail)
            {
                FailNextDetail = false;
                return Task.FromException<DemandSeriesDetailSnapshot>(
                    new WatchHostQueryException(
                        WatchHostFailureKind.ServerQuery,
                        "/api/v2/demand-series/{seriesId}",
                        "detail-failed",
                        "DemandSeries detail refresh failed."));
            }

            var demandId = $"demand-{seriesId[^1]}";
            var demand = new TransportDemandSnapshot(
                demandId,
                seriesId,
                1,
                PredecessorDemandId: null,
                DemandSeriesLifecycleContract.Visible,
                At,
                At,
                GoneConfirmedAt: null,
                "poll-create",
                "commit-create",
                "commit-list",
                new LiveMesFieldSetSnapshot("A1", "EQP-1", "焊线", At, "PKG-1"),
                ExternalReadabilityStates.Readable,
                [],
                "poll-create",
                "commit-create",
                At);
            var observation = new DemandRawObservationSnapshot(
                1,
                "poll-create",
                "commit-create",
                MesObservationAssignment.Assigned,
                seriesId,
                demandId,
                "WIRE_TO_GATE",
                $"SUB-{seriesId[^1]}",
                "A1",
                "EQP-1",
                "焊线",
                At,
                "PKG-1",
                At,
                "2026-08-21 01:00:00");
            var creation = new DemandSeriesEventSnapshot(
                $"event-{seriesId}",
                seriesId,
                1,
                "TRANSPORT_DEMAND_CREATED",
                At,
                "DEMAND",
                demandId,
                "poll-create",
                "commit-create",
                1,
                $"{{\"demandId\":\"{demandId}\",\"generation\":1}}");
            var detail = new DemandSeriesDetailSnapshot(
                new DemandSeriesSnapshotIdentity("commit-list", 2, At, "poll-list"),
                snapshotReference,
                new DemandSeriesSnapshot(
                    seriesId,
                    "WIRE_TO_GATE",
                    $"SUB-{seriesId[^1]}",
                    DemandSeriesLifecycleContract.Tracking,
                    DemandSeriesLifecycleContract.Visible,
                    At,
                    "poll-create",
                    "commit-create",
                    "commit-list",
                    demand,
                    [demand],
                    [observation],
                    [creation],
                    [],
                    [],
                    ArchivedAt: null,
                    LastSeriesSequence: 1));
            if (DelaySeriesBDetail && seriesId == "series-b")
            {
                SeriesBDetail = detail;
                DelayedSeriesBDetail = new TaskCompletionSource<DemandSeriesDetailSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() =>
                {
                    DetailCancellationCount++;
                    DelayedSeriesBDetail.TrySetCanceled(cancellationToken);
                });
                return DelayedSeriesBDetail.Task;
            }

            return Task.FromResult(detail);
        }

        public void ReleaseSeriesBDetail() => DelayedSeriesBDetail?.TrySetResult(
            SeriesBDetail ?? throw new InvalidOperationException("Series B detail did not start."));

        public Task<ReadabilityAuditListSnapshot> FetchReadabilityAuditAsync(
            ReadabilityAuditQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReadabilityAuditDetailSnapshot> FetchReadabilityAuditDetailAsync(
            string demandId,
            string snapshotReference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ErrorSearchListSnapshot> FetchErrorSearchAsync(
            ErrorSearchQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose()
        {
        }

        private static DemandSeriesListItemSnapshot Item(string seriesId) => new(
            seriesId,
            "WIRE_TO_GATE",
            $"SUB-{seriesId[^1]}",
            DemandSeriesLifecycleContract.Tracking,
            DemandSeriesLifecycleContract.Visible,
            At,
            ArchivedAt: null,
            $"demand-{seriesId[^1]}",
            1,
            DemandSeriesLifecycleContract.Visible,
            At,
            GoneConfirmedAt: null,
            new LiveMesFieldSetSnapshot("A1", "EQP-1", "焊线", At, "PKG-1"),
            ExternalReadabilityStates.Readable,
            [],
            LastSeriesSequence: 1,
            LatestPollTraceId: "poll-create",
            LatestProjectionCommitId: "commit-list");
    }
}
