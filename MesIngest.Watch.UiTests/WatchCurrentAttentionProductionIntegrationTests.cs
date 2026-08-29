using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchCurrentAttentionProductionIntegrationTests
{
    private const string SeriesId = "SERIES-ATTENTION-22";
    private const string SeriesErrorCode = "REQUIRED_MES_FIELD_MISSING";
    private const string SeriesErrorCategory = "DATA_COMPLETENESS";
    private const string StableSeriesErrorIdentity =
        "SERIES_ERROR|SERIES-ATTENTION-22|REQUIRED_MES_FIELD_MISSING";
    private static readonly DateTimeOffset At =
        DateTimeOffset.Parse("2026-08-14T07:08:09Z");

    [Fact]
    public async Task Current_attention_renders_all_four_kinds_exact_facets_page_and_structured_evidence_then_drills_an_explicit_error_first_page()
    {
        var attentionQueries = new ConcurrentQueue<CurrentIngestAttentionQuery>();
        var errorQueryReceived = new TaskCompletionSource<ErrorSearchQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-22-current-attention-production", "ticket-22-attention-secret")
            {
                Overview = FakeHostReply.Return(WatchErrorSearchProductionIntegrationTests.CreateOverview()),
                CurrentAttention = FakeHostReply.Select<CurrentIngestAttentionQuery, CurrentIngestAttentionSnapshot>(query =>
                {
                    attentionQueries.Enqueue(query);
                    return FakeHostReply.Return(CreateAttentionSnapshot(query));
                }),
                ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
                {
                    errorQueryReceived.TrySetResult(query);
                    return FakeHostReply.Return(
                        WatchErrorSearchProductionIntegrationTests.CreateErrorPage(
                            query,
                            "attention-drill-error-snapshot-22",
                            pageNumber: 1,
                            totalPages: 1,
                            totalSeriesCount: 1,
                            item: CreateDrillErrorItem()));
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();
        using var timeout = WatchErrorSearchProductionIntegrationTests.CreateTimeout();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = host.BaseUrl,
                    SharedSecret = "ticket-22-attention-secret",
                    RequestTimeoutSeconds = 30,
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.CurrentIngestAttention,
                    PageNumber: 1,
                    AttentionKinds:
                    [
                        CurrentIngestAttentionKinds.UnassignedMesObservation,
                        CurrentIngestAttentionKinds.SeriesError,
                        CurrentIngestAttentionKinds.TaskTypeProtection,
                        CurrentIngestAttentionKinds.PollRunFailure,
                        CurrentIngestAttentionKinds.SeriesError,
                    ],
                    AttentionSeverities:
                    [
                        CurrentIngestAttentionSeverities.Warning,
                        CurrentIngestAttentionSeverities.Error,
                    ],
                    Cursor: null));

                await window.CurrentAttentionNavigationTask.WaitAsync(timeout.Token);
                var query = attentionQueries.Last();
                Assert.Equal(1, query.PageNumber);
                Assert.Equal(CurrentIngestAttentionQuery.DefaultPageSize, query.PageSize);
                Assert.Equal(CurrentIngestAttentionOrder.Default, query.Order);
                Assert.Equal(
                    [
                        CurrentIngestAttentionKinds.PollRunFailure,
                        CurrentIngestAttentionKinds.SeriesError,
                        CurrentIngestAttentionKinds.TaskTypeProtection,
                        CurrentIngestAttentionKinds.UnassignedMesObservation,
                    ],
                    query.Kinds);
                Assert.Equal(
                    [
                        CurrentIngestAttentionSeverities.Error,
                        CurrentIngestAttentionSeverities.Warning,
                    ],
                    query.Severities);
                Assert.Equal(WatchWorkspacePage.CurrentAttention, window.ActivePage);

                var committed = Assert.IsType<CurrentIngestAttentionSnapshot>(
                    window.WorkspaceState.CurrentAttention.Snapshot);
                Assert.Equal(8, committed.ExactTotalItemCount);
                Assert.Equal(4, committed.Facets.Types.Count);
                Assert.Equal(2, committed.Facets.Severities.Count);
                Assert.Equal(1, committed.PageNumber);
                Assert.Equal(1, committed.TotalPages);
                Assert.Equal(CurrentIngestAttentionQuery.DefaultPageSize, committed.PageSize);
                Assert.Equal(
                    new[]
                    {
                        CurrentIngestAttentionKinds.SeriesError,
                        CurrentIngestAttentionKinds.PollRunFailure,
                        CurrentIngestAttentionKinds.TaskTypeProtection,
                        CurrentIngestAttentionKinds.UnassignedMesObservation,
                    }.Order(StringComparer.Ordinal),
                    committed.Items.Select(item => item.Kind).Order(StringComparer.Ordinal));
                Assert.Equal(4, Find<DataGrid>(window, "CurrentAttentionKindFacetGrid").Items.Count);
                Assert.Equal(2, Find<DataGrid>(window, "CurrentAttentionSeverityFacetGrid").Items.Count);
                var grid = Find<DataGrid>(window, "CurrentAttentionGrid");
                Assert.Equal(4, grid.Items.Count);
                var renderedRows = grid.Items
                    .Cast<WatchCurrentIngestAttentionRowPresentation>()
                    .ToArray();
                Assert.Equal(
                    new[]
                    {
                        CurrentIngestAttentionKinds.SeriesError,
                        CurrentIngestAttentionKinds.PollRunFailure,
                        CurrentIngestAttentionKinds.TaskTypeProtection,
                        CurrentIngestAttentionKinds.UnassignedMesObservation,
                    }.Order(StringComparer.Ordinal),
                    renderedRows.Select(row => row.Kind).Order(StringComparer.Ordinal));
                Assert.All(renderedRows, row =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(row.SubjectSummary));
                    Assert.False(string.IsNullOrWhiteSpace(row.KindLabel));
                    Assert.True(
                        row.Severity is CurrentIngestAttentionSeverities.Error
                            or CurrentIngestAttentionSeverities.Warning);
                    Assert.False(string.IsNullOrWhiteSpace(row.OccurredAt));
                    Assert.False(string.IsNullOrWhiteSpace(row.StableIdentity));
                });
                Assert.Contains(
                    renderedRows,
                    row => row.Kind == CurrentIngestAttentionKinds.SeriesError
                        && row.SubjectSummary.Contains(SeriesId, StringComparison.Ordinal)
                        && row.StableIdentity == StableSeriesErrorIdentity);
                Assert.Contains(
                    renderedRows,
                    row => row.Kind == CurrentIngestAttentionKinds.PollRunFailure
                        && row.SubjectSummary.Contains("attention-poll-failed-22", StringComparison.Ordinal)
                        && row.Severity == CurrentIngestAttentionSeverities.Error);
                Assert.Contains(
                    renderedRows,
                    row => row.Kind == CurrentIngestAttentionKinds.TaskTypeProtection
                        && row.SubjectSummary.Contains("WORK-UNSUPPORTED-22", StringComparison.Ordinal)
                        && row.Severity == CurrentIngestAttentionSeverities.Warning);
                Assert.Contains(
                    renderedRows,
                    row => row.Kind == CurrentIngestAttentionKinds.UnassignedMesObservation
                        && row.SubjectSummary.Contains("观测序号 7", StringComparison.Ordinal)
                        && row.Severity == CurrentIngestAttentionSeverities.Warning);
                Assert.Contains(
                    "精确 8",
                    Find<TextBlock>(window, "CurrentAttentionPageSummaryText").Text,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "第 1 / 1 页",
                    Find<TextBlock>(window, "CurrentAttentionPageSummaryText").Text,
                    StringComparison.Ordinal);

                var seriesError = Assert.Single(
                    committed.Items,
                    item => item.Kind == CurrentIngestAttentionKinds.SeriesError);
                Assert.Equal(StableSeriesErrorIdentity, seriesError.StableIdentity);
                Assert.Equal(SeriesId, seriesError.SeriesId);
                Assert.Equal(SeriesErrorCode, seriesError.ErrorCode);
                Assert.Equal("attention-commit-22", seriesError.Evidence.ProjectionCommitId);
                Assert.Equal(222, seriesError.Evidence.ProjectionSequence);
                Assert.Equal("attention-poll-22", seriesError.Evidence.PollTraceId);
                Assert.Equal(229, seriesError.Evidence.PollTraceSequence);
                Assert.Equal("DEMAND-ATTENTION-22", seriesError.Evidence.DemandId);
                Assert.Equal("WIRE_TO_GATE", seriesError.Evidence.WorkType);
                Assert.Equal("EVIDENCE-ATTENTION-22", seriesError.Evidence.EvidenceId);

                for (var index = 0; index < committed.Items.Count; index++)
                {
                    grid.SelectedIndex = index;
                    window.UpdateLayout();
                    var evidenceGrid = Find<DataGrid>(window, "CurrentAttentionEvidenceGrid");
                    Assert.NotEmpty(evidenceGrid.Items);
                    Assert.Contains(
                        committed.Items[index].Evidence
                            .GetType()
                            .GetProperties()
                            .Select(property => property.GetValue(committed.Items[index].Evidence))
                            .Where(value => value is not null)
                            .Select(value => value!.ToString()),
                        value => FlattenItems(evidenceGrid.Items).Contains(
                            value!,
                            StringComparison.Ordinal));
                }

                grid.SelectedIndex = 0;
                window.UpdateLayout();
                Click(Find<ButtonBase>(window, "CurrentAttentionOpenErrorSearchButton"));
                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);

                var errorQuery = await errorQueryReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal([SeriesErrorCategory], errorQuery.Filter.Categories);
                Assert.Equal([SeriesErrorCode], errorQuery.Filter.ErrorCodes);
                Assert.Equal(
                    [ErrorSearchActivityStates.Active],
                    errorQuery.Filter.ActivityStates);
                Assert.Equal(SeriesId, errorQuery.Filter.SeriesId);
                Assert.Null(errorQuery.Filter.DemandId);
                Assert.Null(errorQuery.Filter.SublotContains);
                Assert.Equal(ErrorSearchWindowKinds.Last7Days, errorQuery.Window.Kind);
                Assert.Null(errorQuery.SnapshotReference);
                Assert.Null(errorQuery.Cursor);
                Assert.Equal(WatchWorkspacePage.ErrorSearch, window.ActivePage);
                Assert.DoesNotContain(
                    "AREA",
                    Find<TextBlock>(window, "ErrorSearchNormalizedFilterText").Text,
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Applying_a_new_host_clears_error_and_current_attention_queries_before_the_new_host_is_read()
    {
        const string credentialA = "ticket-22-query-reset-a";
        const string credentialB = "ticket-22-query-reset-b";
        var hostBErrorQueries = new ConcurrentQueue<ErrorSearchQuery>();
        var hostBAttentionQueries = new ConcurrentQueue<CurrentIngestAttentionQuery>();
        await using var hostA = await ScriptedFakeHost.StartV2Async(
            CreateQueryResetScenario("ticket-22-query-reset-a", credentialA),
            TestContext.Current.CancellationToken);
        await using var hostB = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-22-query-reset-b", credentialB)
            {
                Overview = FakeHostReply.Return(WatchErrorSearchProductionIntegrationTests.CreateOverview()),
                ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
                {
                    hostBErrorQueries.Enqueue(query);
                    return FakeHostReply.Return(CreateEmptyErrorSnapshot(query, "query-reset-b-error"));
                }),
                CurrentAttention = FakeHostReply.Select<CurrentIngestAttentionQuery, CurrentIngestAttentionSnapshot>(query =>
                {
                    hostBAttentionQueries.Enqueue(query);
                    return FakeHostReply.Return(CreateAttentionSnapshot(query));
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();
        using var timeout = WatchErrorSearchProductionIntegrationTests.CreateTimeout();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(hostA.BaseUrl, credentialA),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "ErrorSearchNavigationItem"));
                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);
                WatchErrorSearchProductionIntegrationTests.SelectErrorCategories(
                    window,
                    SeriesErrorCategory);
                Find<TextBox>(window, "ErrorSearchSeriesIdFilter").Text = SeriesId;
                Click(Find<ButtonBase>(window, "ErrorSearchApplyFilterButton"));
                await window.ErrorSearchOperationTask.WaitAsync(timeout.Token);
                Assert.Equal(
                    [SeriesErrorCategory],
                    window.WorkspaceState.ErrorSearch.Snapshot?.Filter.Categories);
                Assert.Equal(
                    SeriesId,
                    window.WorkspaceState.ErrorSearch.Snapshot?.Filter.SeriesId);

                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "CurrentAttentionNavigationItem"));
                await window.CurrentAttentionNavigationTask.WaitAsync(timeout.Token);
                Find<ComboBox>(window, "CurrentAttentionKindFilter").Text =
                    CurrentIngestAttentionKinds.SeriesError;
                Find<ComboBox>(window, "CurrentAttentionSeverityFilter").Text =
                    CurrentIngestAttentionSeverities.Error;
                Click(Find<ButtonBase>(window, "CurrentAttentionApplyFilterButton"));
                await window.CurrentAttentionOperationTask.WaitAsync(timeout.Token);
                Assert.Equal(
                    [CurrentIngestAttentionKinds.SeriesError],
                    window.WorkspaceState.CurrentAttention.Snapshot?.Kinds);
                Assert.Equal(
                    [CurrentIngestAttentionSeverities.Error],
                    window.WorkspaceState.CurrentAttention.Snapshot?.Severities);

                Assert.True(await window.ApplyHostAsync(
                    new WatchHostSettings(hostB.BaseUrl, credentialB, 30),
                    timeout.Token));
                Assert.Empty(Find<ListBox>(window, "ErrorSearchCategoryList").SelectedItems);
                Assert.True(string.IsNullOrWhiteSpace(
                    Find<TextBox>(window, "ErrorSearchCategorySearchInput").Text));
                Assert.True(string.IsNullOrWhiteSpace(
                    Find<TextBox>(window, "ErrorSearchSeriesIdFilter").Text));
                Assert.Equal(
                    "全部类型",
                    Find<ComboBox>(window, "CurrentAttentionKindFilter").Text);
                Assert.Equal(
                    "全部严重度",
                    Find<ComboBox>(window, "CurrentAttentionSeverityFilter").Text);

                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "ErrorSearchNavigationItem"));
                await window.ErrorSearchNavigationTask.WaitAsync(timeout.Token);
                var newHostErrorQuery = hostBErrorQueries.Last();
                Assert.Empty(newHostErrorQuery.Filter.Categories);
                Assert.Empty(newHostErrorQuery.Filter.ErrorCodes);
                Assert.Empty(newHostErrorQuery.Filter.ActivityStates);
                Assert.Null(newHostErrorQuery.Filter.SeriesId);
                Assert.Equal(ErrorSearchWindowSelection.Last7Days, newHostErrorQuery.Window);
                Assert.Null(newHostErrorQuery.SnapshotReference);
                Assert.Null(newHostErrorQuery.Cursor);

                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "CurrentAttentionNavigationItem"));
                await window.CurrentAttentionNavigationTask.WaitAsync(timeout.Token);
                var newHostAttentionQuery = hostBAttentionQueries.Last();
                Assert.Empty(newHostAttentionQuery.Kinds ?? []);
                Assert.Empty(newHostAttentionQuery.Severities ?? []);
                Assert.Equal(1, newHostAttentionQuery.PageNumber);
                Assert.Equal(CurrentIngestAttentionQuery.DefaultPageSize, newHostAttentionQuery.PageSize);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task A_selected_attention_identity_that_disappears_after_refresh_clears_evidence_without_selecting_another_item()
    {
        var requestNumber = 0;
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-22-attention-selection-relocation", "ticket-22-selection-secret")
            {
                Overview = FakeHostReply.Return(WatchErrorSearchProductionIntegrationTests.CreateOverview()),
                CurrentAttention = FakeHostReply.Select<CurrentIngestAttentionQuery, CurrentIngestAttentionSnapshot>(query =>
                {
                    var snapshot = CreateAttentionSnapshot(query);
                    if (Interlocked.Increment(ref requestNumber) == 1)
                    {
                        return FakeHostReply.Return(snapshot);
                    }

                    var remaining = snapshot.Items
                        .Where(item => !string.Equals(
                            item.StableIdentity,
                            "POLL_RUN_FAILURE|ATTENTION-POLL-FAILED-22",
                            StringComparison.Ordinal))
                        .ToArray();
                    return FakeHostReply.Return(snapshot with
                    {
                        Snapshot = snapshot.Snapshot with
                        {
                            ProjectionCommitId = "attention-commit-refresh-22",
                            ProjectionSequence = 223,
                            SnapshotAsOf = At.AddMinutes(1),
                        },
                        ExactTotalItemCount = remaining.Length,
                        Items = remaining,
                    });
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();
        using var timeout = WatchErrorSearchProductionIntegrationTests.CreateTimeout();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl, "ticket-22-selection-secret"),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "CurrentAttentionNavigationItem"));
                await window.CurrentAttentionNavigationTask.WaitAsync(timeout.Token);
                var grid = Find<DataGrid>(window, "CurrentAttentionGrid");
                grid.SelectedIndex = 1;
                window.UpdateLayout();
                var selected = Assert.IsType<WatchCurrentIngestAttentionRowPresentation>(
                    grid.SelectedItem);
                Assert.Equal(
                    "POLL_RUN_FAILURE|ATTENTION-POLL-FAILED-22",
                    selected.StableIdentity);
                Assert.NotEmpty(Find<DataGrid>(window, "CurrentAttentionEvidenceGrid").Items);

                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "CurrentAttentionNavigationItem"));
                await window.CurrentAttentionNavigationTask.WaitAsync(timeout.Token);

                Assert.Null(grid.SelectedItem);
                Assert.Empty(Find<DataGrid>(window, "CurrentAttentionEvidenceGrid").Items);
                Assert.False(Find<ButtonBase>(window, "CurrentAttentionOpenErrorSearchButton").IsEnabled);
                Assert.Contains(
                    "选择一项",
                    Find<TextBlock>(window, "CurrentAttentionSelectedContextText").Text,
                    StringComparison.Ordinal);
                var notice = NotificationText(window);
                Assert.Contains("原关注项已不在刷新结果中", notice, StringComparison.Ordinal);
                Assert.Contains("刷新已清除原选择", notice, StringComparison.Ordinal);
                Assert.DoesNotContain(selected.StableIdentity, notice, StringComparison.Ordinal);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    internal static CurrentIngestAttentionSnapshot CreateAttentionSnapshot(
        CurrentIngestAttentionQuery query)
    {
        var normalized = query.NormalizeAndValidate();
        var epoch = HistoryEpoch.FromGuid(
            Guid.Parse("22222222-2222-2222-2222-222222222222"));
        return new CurrentIngestAttentionSnapshot(
            new OperationalSnapshotIdentity(
                "attention-commit-22",
                222,
                At,
                "attention-poll-22",
                229,
                22,
                At,
                HistoryEpoch: epoch),
            ExactTotalItemCount: 8,
            new CurrentIngestAttentionFacets(
                [
                    new CurrentIngestAttentionFacetSnapshot(
                        CurrentIngestAttentionKinds.SeriesError,
                        2),
                    new CurrentIngestAttentionFacetSnapshot(
                        CurrentIngestAttentionKinds.PollRunFailure,
                        1),
                    new CurrentIngestAttentionFacetSnapshot(
                        CurrentIngestAttentionKinds.TaskTypeProtection,
                        3),
                    new CurrentIngestAttentionFacetSnapshot(
                        CurrentIngestAttentionKinds.UnassignedMesObservation,
                        2),
                ],
                [
                    new CurrentIngestAttentionFacetSnapshot(
                        CurrentIngestAttentionSeverities.Error,
                        5),
                    new CurrentIngestAttentionFacetSnapshot(
                        CurrentIngestAttentionSeverities.Warning,
                        3),
                ]),
            normalized.Order,
            normalized.PageSize,
            normalized.PageNumber,
            TotalPages: 1,
            normalized.Kinds ?? [],
            normalized.Severities ?? [],
            CreateAttentionItems(),
            HistoryCleanupStateSnapshot.NotRun with
            {
                EarliestAvailableHostUtc = At.AddDays(-15),
            },
            new StoragePressureStateSnapshot(
                StoragePressureStatuses.Healthy,
                epoch,
                "MesIngest",
                @"D:\SqlData\MesIngest.mdf",
                VolumeSpaceSample.FromPercent(@"D:\", 1_000_000, 25m),
                At,
                PausedAt: null,
                PauseId: null,
                PauseReason: null,
                RecoveryAuditId: null));
    }

    private static FakeHostV2Scenario CreateQueryResetScenario(
        string name,
        string credential) => new(name, credential)
    {
        Overview = FakeHostReply.Return(WatchErrorSearchProductionIntegrationTests.CreateOverview()),
        ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
            FakeHostReply.Return(CreateEmptyErrorSnapshot(query, "query-reset-a-error"))),
        CurrentAttention = FakeHostReply.Select<CurrentIngestAttentionQuery, CurrentIngestAttentionSnapshot>(query =>
            FakeHostReply.Return(CreateAttentionSnapshot(query))),
    };

    private static ErrorSearchListSnapshot CreateEmptyErrorSnapshot(
        ErrorSearchQuery query,
        string snapshotReference) => WatchErrorSearchProductionIntegrationTests.CreateErrorPage(
        query,
        snapshotReference,
        pageNumber: 1,
        totalPages: 0,
        totalSeriesCount: 0,
        includeItem: false);

    private static WatchOptions CreateOptions(string baseUrl, string credential) => new()
    {
        BaseUrl = baseUrl,
        SharedSecret = credential,
        RequestTimeoutSeconds = 30,
        RenderingMode = WatchRenderingMode.SoftwareOnly,
    };

    private static IReadOnlyList<CurrentIngestAttentionItemSnapshot> CreateAttentionItems() =>
    [
        new CurrentIngestAttentionItemSnapshot(
            CurrentIngestAttentionKinds.SeriesError,
            CurrentIngestAttentionSeverities.Error,
            At,
            StableSeriesErrorIdentity,
            SeriesId,
            "WIRE_TO_GATE",
            SeriesErrorCode,
            "AREA",
            "DEMAND",
            new CurrentIngestAttentionEvidenceSnapshot(
                ProjectionCommitId: "attention-commit-22",
                ProjectionSequence: 222,
                PollTraceId: "attention-poll-22",
                PollTraceSequence: 229,
                SeriesId: SeriesId,
                DemandId: "DEMAND-ATTENTION-22",
                WorkType: "WIRE_TO_GATE",
                EvidenceId: "EVIDENCE-ATTENTION-22"),
            new OverviewNavigationIntent(
                OverviewNavigationTargets.ErrorSearch,
                PageNumber: 1,
                ErrorActivityStates: [ErrorSearchActivityStates.Active],
                ErrorWindow: ErrorSearchWindowKinds.Last7Days,
                SeriesId: SeriesId,
                Cursor: null)),
        new CurrentIngestAttentionItemSnapshot(
            CurrentIngestAttentionKinds.PollRunFailure,
            CurrentIngestAttentionSeverities.Error,
            At.AddMinutes(-1),
            "POLL_RUN_FAILURE|ATTENTION-POLL-FAILED-22",
            SeriesId: null,
            WorkType: null,
            ErrorCode: null,
            Target: "MES_TASK_UNION_V2",
            SubjectKind: "POLL_RUN",
            new CurrentIngestAttentionEvidenceSnapshot(
                PollTraceId: "attention-poll-failed-22",
                PollTraceSequence: 228,
                ContentDigest: "DIGEST-POLL-22",
                Phase: "FETCH",
                Outcome: "FAILED"),
            new OverviewNavigationIntent(
                OverviewNavigationTargets.PollTrace,
                PageNumber: 1,
                PollTraceId: "attention-poll-failed-22",
                Cursor: null)),
        new CurrentIngestAttentionItemSnapshot(
            CurrentIngestAttentionKinds.TaskTypeProtection,
            CurrentIngestAttentionSeverities.Warning,
            At.AddMinutes(-2),
            "TASK_TYPE_PROTECTION|WORK-UNSUPPORTED-22",
            SeriesId: null,
            WorkType: "WORK-UNSUPPORTED-22",
            ErrorCode: null,
            Target: "WORK-UNSUPPORTED-22",
            SubjectKind: "WORK_TYPE",
            new CurrentIngestAttentionEvidenceSnapshot(
                ProjectionCommitId: "attention-commit-22",
                ProjectionSequence: 222,
                WorkType: "WORK-UNSUPPORTED-22",
                EvidenceId: "PROTECTION-22",
                Phase: "CLASSIFY",
                Outcome: "PROTECTED"),
            new OverviewNavigationIntent(
                OverviewNavigationTargets.TaskTypeProtection,
                PageNumber: 1,
                WorkType: "WORK-UNSUPPORTED-22",
                Cursor: null)),
        new CurrentIngestAttentionItemSnapshot(
            CurrentIngestAttentionKinds.UnassignedMesObservation,
            CurrentIngestAttentionSeverities.Warning,
            At.AddMinutes(-3),
            "UNASSIGNED_MES_OBSERVATION|ATTENTION-POLL-22|7",
            SeriesId: null,
            WorkType: "WIRE_TO_GATE",
            ErrorCode: null,
            Target: "OBSERVATION-7",
            SubjectKind: "RAW_OBSERVATION",
            new CurrentIngestAttentionEvidenceSnapshot(
                ProjectionCommitId: "attention-commit-22",
                ProjectionSequence: 222,
                PollTraceId: "attention-poll-22",
                PollTraceSequence: 229,
                WorkType: "WIRE_TO_GATE",
                ObservationOrdinal: 7,
                ContentDigest: "DIGEST-OBSERVATION-22",
                Phase: "ASSIGN",
                Outcome: "UNASSIGNED"),
            new OverviewNavigationIntent(
                OverviewNavigationTargets.PollTrace,
                PageNumber: 1,
                PollTraceId: "attention-poll-22",
                Cursor: null)),
    ];

    private static ErrorSearchListItemSnapshot CreateDrillErrorItem() => new(
        SeriesId,
        "WIRE_TO_GATE",
        "SUBLOT-ATTENTION-22",
        ErrorSearchActivityStates.Active,
        [new ErrorSearchMatchedErrorSnapshot(SeriesErrorCode, SeriesErrorCategory, "ERROR")],
        At,
        MatchedPeriodCount: 1,
        MatchedDemandGenerationCount: 1,
        MesArea: "A1-1",
        ErrorSearchMesAreaAvailability.CurrentTrusted);

    private static string FlattenItems(ItemCollection items) => string.Join(
        " | ",
        items.Cast<object>().Select(item => item.ToString()));

    private static T Find<T>(FrameworkElement root, string name)
        where T : class => WatchErrorSearchProductionIntegrationTests.Find<T>(root, name);

    private static string NotificationText(WatchWorkspaceWindow window)
    {
        window.UpdateLayout();
        var cards = string.Join(
            " · ",
            Find<ItemsControl>(window, "NotificationItemsControl").Items
                .Cast<object>()
                .Select(item => item.GetType().GetProperty("AutomationName")?.GetValue(item)?.ToString())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        return string.Join(
            " · ",
            new[]
            {
                Find<System.Windows.Controls.TextBlock>(window, "NotificationLiveRegion").Text,
                cards,
            }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in VisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Click(UIElement element) =>
        element.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
}
