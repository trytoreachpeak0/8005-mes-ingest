using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;
using FluentButton = Wpf.Ui.Controls.Button;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace MesIngest.Watch.UiTests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class WatchV2ProductionHostCollection
{
    public const string CollectionName = "Watch V2 production Host";
}

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchV2ProductionHostTests
{
    [Fact]
    public async Task Production_composition_verifies_contract_then_loads_one_atomic_overview_into_the_shell()
    {
        const string credential = "production-host-secret";
        var overview = CreateOverviewSnapshot("overview-a", ["A1-1"]);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("production-host-a", credential)
            {
                Overview = FakeHostReply.Return(overview),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl, credential),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(TestContext.Current.CancellationToken);

                var state = window.WorkspaceState;
                var committed = Assert.IsType<WatchOverviewSnapshot>(state.Overview.Snapshot);
                Assert.Equal(WatchHostConnectionStatus.Connected, state.ConnectionStatus);
                Assert.Equal("overview-a", committed.Snapshot.ProjectionCommitId);
                Assert.Equal(31, committed.Series.ExactTotalSeriesCount);
                Assert.Equal(47, committed.Readability.ExactTotalDemandGenerationCount);
                Assert.Equal(7, committed.Errors.ActiveSeriesCount);
                Assert.Equal(13, committed.Attention.ExactTotalItemCount);

                Assert.Equal("31", Find<TextBlock>(window, "SeriesSummaryValue").Text);
                Assert.Equal("41 / 47", Find<TextBlock>(window, "ReadabilitySummaryValue").Text);
                Assert.Equal("7", Find<TextBlock>(window, "ErrorsSummaryValue").Text);
                Assert.Equal("13", Find<TextBlock>(window, "AttentionSummaryValue").Text);
                Assert.Contains(
                    "2 归档后仍可见",
                    Find<TextBlock>(window, "SeriesSummaryDetail").Text,
                    StringComparison.Ordinal);
                Assert.Equal(
                    "Host 已提交范围：A1-1",
                    Find<TextBlock>(window, "HostAreaScopeText").Text);
                Assert.Equal(
                    WatchOverviewRecentActivityStates.NoRecentHighlightsMessage,
                    Find<TextBlock>(window, "RecentActivityHeadingText").Text);
                Assert.Contains(
                    Find<TextBlock>(window, "SnapshotFactsText").Text,
                    AutomationProperties.GetName(Find<TextBlock>(window, "SnapshotFactsText")),
                    StringComparison.Ordinal);
                Assert.Contains(
                    Find<TextBlock>(window, "ClientAttemptFactsText").Text,
                    AutomationProperties.GetName(Find<TextBlock>(window, "ClientAttemptFactsText")),
                    StringComparison.Ordinal);
                Assert.Contains(
                    Find<TextBlock>(window, "SnapshotFactsText").Text,
                    AutomationProperties.GetName(Find<TextBlock>(window, "OverviewContextText")),
                    StringComparison.Ordinal);
                Assert.Contains(
                    "不是健康结论",
                    Assert.IsAssignableFrom<TextBlock>(
                        Find<StackPanel>(window, "RecentActivityItems").Children[0]).Text,
                    StringComparison.Ordinal);
                Assert.Same(
                    committed.Series.TrackingNavigation,
                    Find<FluentButton>(window, "SeriesTrackingAction").Tag);
                Assert.Same(
                    committed.Series.ArchivedNavigation,
                    Find<FluentButton>(window, "SeriesArchivedAction").Tag);
                Assert.Same(
                    committed.Series.GoneNavigation,
                    Find<FluentButton>(window, "SeriesGoneAction").Tag);
                Assert.Same(
                    committed.Series.LongGoneButVisibleNavigation,
                    Find<FluentButton>(window, "SeriesLongGoneVisibleAction").Tag);
                Assert.Same(
                    committed.Readability.ReadableNavigation,
                    Find<FluentButton>(window, "ReadableSummaryAction").Tag);
                Assert.Same(
                    committed.Readability.NotReadableNavigation,
                    Find<FluentButton>(window, "NotReadableSummaryAction").Tag);
                Assert.Same(
                    committed.Errors.ActiveNavigation,
                    Find<FluentButton>(window, "ActiveErrorsSummaryAction").Tag);
                Assert.Same(
                    committed.Errors.Prior7DaysNavigation,
                    Find<FluentButton>(window, "PriorErrorsSummaryAction").Tag);
                Assert.Equal(
                    ["Series 错误 · 13", "ERROR · 3", "WARNING · 10"],
                    Find<WrapPanel>(window, "AttentionSummaryActions").Children
                        .OfType<FluentButton>()
                        .Skip(1)
                        .Select(button => button.Content?.ToString() ?? string.Empty)
                        .ToArray());

                var hostFooter = Find<NavigationViewItem>(window, "HostNavigationItem");
                Assert.Equal("Host 已连接", hostFooter.Content?.ToString());
                Assert.Contains(host.BaseUrl, hostFooter.ToolTip?.ToString(), StringComparison.Ordinal);
                Assert.Contains(
                    "Host 已连接",
                    AutomationProperties.GetName(hostFooter),
                    StringComparison.Ordinal);
                Assert.Equal(
                    SymbolRegular.CloudCheckmark24,
                    Find<SymbolIcon>(window, "HostNavigationIcon").Symbol);

                var completed = host.Timeline
                    .Where(entry => entry.State == FakeHostRequestState.Completed
                        && entry.Operation is FakeHostOperation.ContractV2
                            or FakeHostOperation.OverviewV2)
                    .ToArray();
                Assert.Equal(
                    [FakeHostOperation.ContractV2, FakeHostOperation.OverviewV2],
                    completed.Select(entry => entry.Operation).ToArray());
                Assert.Equal(
                    ["/api/v2/contract", "/api/v2/watch-overview"],
                    completed.Select(entry => entry.Endpoint).ToArray());
                Assert.Single(host.Timeline, entry =>
                    entry.Operation == FakeHostOperation.OverviewV2
                    && entry.State == FakeHostRequestState.Completed);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Failed_area_refresh_retains_all_of_snapshot_a_while_local_and_host_area_scopes_stay_distinct()
    {
        const string credential = "area-scope-secret";
        var overviewA = CreateOverviewSnapshot("overview-area-a", ["A1-1"]);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("area-scope-host", credential)
            {
                Overview = FakeHostReply.Sequence<WatchOverviewQuery, WatchOverviewSnapshot>(
                    FakeHostReply.Return(overviewA),
                    FakeHostReply.Fail<WatchOverviewSnapshot>(
                        WatchHostFailureKind.ServerQuery,
                        "/api/v2/watch-overview",
                        "AREA B projection is unavailable")),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl, credential),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(TestContext.Current.CancellationToken);
                var committedA = Assert.IsType<WatchOverviewSnapshot>(
                    window.WorkspaceState.Overview.Snapshot);

                await window.ApplyAreaContextAsync(
                    new WatchAreaDisplayContext(
                        "AREA B 本机筛选",
                        ["B1-1"],
                        "本机已应用",
                        DateTimeOffset.Parse("2026-08-14T08:09:10+08:00")),
                    TestContext.Current.CancellationToken);

                var afterFailure = window.WorkspaceState.Overview;
                Assert.Same(committedA, afterFailure.Snapshot);
                Assert.Equal("overview-area-a", afterFailure.Snapshot!.Snapshot.ProjectionCommitId);
                Assert.Same(committedA.Series, afterFailure.Snapshot.Series);
                Assert.Same(committedA.Readability, afterFailure.Snapshot.Readability);
                Assert.Same(committedA.Errors, afterFailure.Snapshot.Errors);
                Assert.Same(committedA.Attention, afterFailure.Snapshot.Attention);
                Assert.Equal(string.Empty, afterFailure.CommittedQueryKey);
                Assert.Equal("area=B1-1", afterFailure.PendingQueryKey);
                Assert.Equal("area=B1-1", afterFailure.FailedQueryKey);
                Assert.True(afterFailure.IsStale);

                Assert.Equal(
                    "AREA B 本机筛选",
                    Find<TextBlock>(window, "LocalAreaHeadingText").Text);
                Assert.Equal(
                    "Host 已提交范围：A1-1",
                    Find<TextBlock>(window, "HostAreaScopeText").Text);
                Assert.Equal("31", Find<TextBlock>(window, "SeriesSummaryValue").Text);
                Assert.Equal("41 / 47", Find<TextBlock>(window, "ReadabilitySummaryValue").Text);
                Assert.Equal("7", Find<TextBlock>(window, "ErrorsSummaryValue").Text);
                Assert.Equal("13", Find<TextBlock>(window, "AttentionSummaryValue").Text);
                Assert.NotEmpty(Find<TextBlock>(window, "StaleNoticeText").Text);
                Assert.Contains(
                    "数据可能已过期",
                    AutomationProperties.GetName(Find<TextBlock>(window, "StaleNoticeText")),
                    StringComparison.Ordinal);
                var failureNotice = Find<InfoBar>(window, "OverviewInfoBar");
                Assert.True(failureNotice.IsOpen);
                Assert.Equal("概览刷新失败，已保留上次完整快照", failureNotice.Title);
                Assert.Contains(
                    failureNotice.Title,
                    AutomationProperties.GetName(failureNotice),
                    StringComparison.Ordinal);

                Assert.Contains(host.Timeline, entry =>
                    entry.Operation == FakeHostOperation.OverviewV2
                    && entry.State == FakeHostRequestState.Completed
                    && entry.Endpoint == "/api/v2/watch-overview");
                Assert.Contains(host.Timeline, entry =>
                    entry.Operation == FakeHostOperation.OverviewV2
                    && entry.State == FakeHostRequestState.Failed
                    && entry.Endpoint == "/api/v2/watch-overview?area=B1-1");
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Local_intervals_do_not_replace_the_host_then_a_failed_host_switch_clears_a_without_persisting_secrets()
    {
        const string credentialA = "never-persist-secret-a";
        const string credentialB = "never-persist-secret-b";
        var overviewA = CreateOverviewSnapshot("overview-before-host-switch", ["A1-1"]);
        await using var hostA = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("preferences-host-a", credentialA)
            {
                Overview = FakeHostReply.Return(overviewA),
            },
            TestContext.Current.CancellationToken);
        await using var hostB = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("contract-failure-host-b", credentialB)
            {
                Contract = FakeHostReply.Return(
                    FakeHostV2ContractSnapshot.Exact with
                    {
                        ContractVersion = "incompatible-ticket-19-contract",
                    }),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(hostA.BaseUrl, credentialA),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(TestContext.Current.CancellationToken);
                var snapshotA = Assert.IsType<WatchOverviewSnapshot>(
                    window.WorkspaceState.Overview.Snapshot);
                var generationA = window.WorkspaceState.HostGeneration;
                var contractRequestsBeforeLocalSave = CountStartedContracts(hostA);
                var intervals = new WatchV2AutoRefreshSettings(
                    new WatchV2AutoRefreshSetting(60),
                    new WatchV2AutoRefreshSetting(300),
                    new WatchV2AutoRefreshSetting(10),
                    new WatchV2AutoRefreshSetting(30),
                    new WatchV2AutoRefreshSetting(60));

                window.ApplyLocalPreferences(
                    intervals,
                    new WatchV2DisplayPreferences(
                        rememberWindowSize: true,
                        windowWidth: 1280,
                        windowHeight: 800,
                        isNavigationPaneOpen: false));

                Assert.Equal(generationA, window.WorkspaceState.HostGeneration);
                Assert.Same(snapshotA, window.WorkspaceState.Overview.Snapshot);
                Assert.Equal(contractRequestsBeforeLocalSave, CountStartedContracts(hostA));
                Assert.Equal(60, window.AutoRefreshSettings.Overview.IntervalSeconds);
                Assert.Equal(300, window.AutoRefreshSettings.DemandSeries.IntervalSeconds);
                Assert.Equal(10, window.AutoRefreshSettings.ReadabilityAudit.IntervalSeconds);
                var workspaceJson = File.ReadAllText(files.WorkspacePath);
                Assert.DoesNotContain("\"enabled\"", workspaceJson, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(credentialA, workspaceJson, StringComparison.Ordinal);

                await window.ApplyHostAsync(
                    new WatchHostSettings(hostB.BaseUrl, credentialB, 45),
                    TestContext.Current.CancellationToken);

                var failedHost = window.WorkspaceState;
                Assert.Equal(generationA + 1, failedHost.HostGeneration);
                Assert.Equal(hostB.BaseUrl, failedHost.BaseUrl);
                Assert.Equal(WatchHostConnectionStatus.Failed, failedHost.ConnectionStatus);
                Assert.Equal(WatchHostFailureKind.Contract, failedHost.FailureKind);
                Assert.Null(failedHost.Overview.Snapshot);
                Assert.DoesNotContain(hostB.Timeline, entry =>
                    entry.Operation == FakeHostOperation.OverviewV2);

                var connectionJson = File.ReadAllText(files.ConnectionPath);
                Assert.Contains(hostB.BaseUrl, connectionJson, StringComparison.Ordinal);
                Assert.Contains(
                    "\"requestTimeoutSeconds\": 45",
                    connectionJson,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "\"credentialReference\": \"external-configuration\"",
                    connectionJson,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(credentialA, connectionJson, StringComparison.Ordinal);
                Assert.DoesNotContain(credentialB, connectionJson, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "\"credential\":",
                    connectionJson,
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Older_host_apply_cannot_overwrite_newer_connection_preferences_after_late_completion()
    {
        const string hostAUrl = "http://slow-host-a";
        const string hostBUrl = "http://fast-host-b";
        var slowContract = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var slowClient = new ControllableV2Client(
            slowContract.Task,
            CreateOverviewSnapshot("slow-a", []));
        var fastClient = new ControllableV2Client(
            Task.CompletedTask,
            CreateOverviewSnapshot("fast-b", []));
        using var files = new TemporaryWatchFiles();

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(hostAUrl, "secret-a"),
                settings => settings.BaseUrl == hostAUrl ? slowClient : fastClient,
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                var applyA = window.ApplyHostAsync(
                    new WatchHostSettings(hostAUrl, "secret-a", 30),
                    TestContext.Current.CancellationToken);
                await slowClient.ContractStarted.Task.WaitAsync(
                    TestContext.Current.CancellationToken);

                await window.ApplyHostAsync(
                    new WatchHostSettings(hostBUrl, "secret-b", 45),
                    TestContext.Current.CancellationToken);
                slowContract.TrySetResult();
                await applyA;

                Assert.Equal(hostBUrl, window.WorkspaceState.BaseUrl);
                Assert.Equal(WatchHostConnectionStatus.Connected, window.WorkspaceState.ConnectionStatus);
                Assert.Equal("fast-b", window.WorkspaceState.Overview.Snapshot?.Snapshot.ProjectionCommitId);
                var connectionJson = File.ReadAllText(files.ConnectionPath);
                Assert.Contains(hostBUrl, connectionJson, StringComparison.Ordinal);
                Assert.DoesNotContain(hostAUrl, connectionJson, StringComparison.Ordinal);
                Assert.DoesNotContain("secret-a", connectionJson, StringComparison.Ordinal);
                Assert.DoesNotContain("secret-b", connectionJson, StringComparison.Ordinal);
            }
            finally
            {
                slowContract.TrySetResult();
                window.Dispose();
            }
        });
    }

    private static WatchOptions CreateOptions(string baseUrl, string credential) => new()
    {
        BaseUrl = baseUrl,
        SharedSecret = credential,
        RequestTimeoutSeconds = 30,
        RenderingMode = WatchRenderingMode.SoftwareOnly,
    };

    private static WatchOverviewSnapshot CreateOverviewSnapshot(
        string projectionCommitId,
        IReadOnlyList<string> areas)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var snapshot = new OperationalSnapshotIdentity(
            projectionCommitId,
            101,
            at,
            $"poll-{projectionCommitId}",
            107,
            11,
            at);
        var seriesNavigation = new OverviewNavigationIntent(
            OverviewNavigationTargets.DemandSeries,
            MesAreas: areas,
            Cursor: null);
        var readabilityNavigation = new OverviewNavigationIntent(
            OverviewNavigationTargets.ReadabilityAudit,
            MesAreas: areas,
            Cursor: null);
        var errorNavigation = new OverviewNavigationIntent(
            OverviewNavigationTargets.ErrorSearch,
            MesAreas: areas,
            ErrorWindow: ErrorSearchWindowKinds.Last7Days,
            Cursor: null);
        var attentionNavigation = new OverviewNavigationIntent(
            OverviewNavigationTargets.CurrentIngestAttention,
            MesAreas: areas,
            Cursor: null);
        return new WatchOverviewSnapshot(
            snapshot,
            areas,
            new WatchOverviewSeriesSummary(
                31,
                17,
                9,
                5,
                2,
                seriesNavigation,
                seriesNavigation,
                seriesNavigation,
                seriesNavigation,
                seriesNavigation),
            new WatchOverviewReadabilitySummary(
                47,
                41,
                6,
                readabilityNavigation,
                readabilityNavigation,
                readabilityNavigation),
            new WatchOverviewErrorSummary(
                7,
                11,
                errorNavigation,
                errorNavigation,
                errorNavigation),
            new WatchOverviewAttentionSummary(
                13,
                [new OverviewFacetSnapshot(
                    CurrentIngestAttentionKinds.SeriesError,
                    13,
                    attentionNavigation)],
                [
                    new OverviewFacetSnapshot(
                        CurrentIngestAttentionSeverities.Error,
                        3,
                        attentionNavigation),
                    new OverviewFacetSnapshot(
                        CurrentIngestAttentionSeverities.Warning,
                        10,
                        attentionNavigation),
                ],
                attentionNavigation),
            [],
            WatchOverviewRecentActivityStates.NoRecentHighlights,
            WatchOverviewRecentActivityStates.NoRecentHighlightsMessage);
    }

    private static int CountStartedContracts(ScriptedFakeHost host) => host.Timeline.Count(entry =>
        entry.Operation == FakeHostOperation.ContractV2
        && entry.State == FakeHostRequestState.Started);

    private sealed class ControllableV2Client(
        Task contractCompletion,
        WatchOverviewSnapshot overview) : IWatchV2ApiClient
    {
        public TaskCompletionSource ContractStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task VerifyContractAsync(CancellationToken cancellationToken)
        {
            ContractStarted.TrySetResult();
            await contractCompletion.ConfigureAwait(false);
        }

        public Task<WatchOverviewSnapshot> FetchOverviewAsync(
            WatchOverviewQuery query,
            CancellationToken cancellationToken = default) => Task.FromResult(overview);

        public Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
            DemandSeriesBrowseQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DemandSeriesDetailSnapshot> FetchDemandSeriesDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
    }

    private static T Find<T>(WatchWorkspaceWindow window, string name)
        where T : class => Assert.IsAssignableFrom<T>(window.FindName(name));

    private static Task RunInStaDispatcherAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            _ = ExecuteAsync();
            Dispatcher.Run();
            if (failure is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(failure);
            }

            async Task ExecuteAsync()
            {
                try
                {
                    await action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Watch V2 production Host test STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class TemporaryWatchFiles : IDisposable
    {
        public TemporaryWatchFiles()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"watch-v2-production-host-{Guid.NewGuid():N}");
            ConnectionPath = Path.Combine(Root, "connection.json");
            WorkspacePath = Path.Combine(Root, "workspace.json");
        }

        public string Root { get; }

        public string ConnectionPath { get; }

        public string WorkspacePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
