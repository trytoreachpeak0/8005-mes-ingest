using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchReadabilityAuditProductionIntegrationTests
{
    [Fact]
    public async Task Overview_drill_loads_host_exact_audit_facets_then_same_snapshot_detail()
    {
        const string credential = "readability-production-secret";
        const string demandId = "demand-audit-21";
        const string snapshotReference = "snapshot-audit-21";
        var queryReceived = new TaskCompletionSource<ReadabilityAuditQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detailReceived = new TaskCompletionSource<FakeHostV2DetailRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var multiFilterReceived = new TaskCompletionSource<ReadabilityAuditQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var directPageReceived = new TaskCompletionSource<ReadabilityAuditQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("readability-production", credential)
            {
                Overview = FakeHostReply.Select<WatchOverviewQuery, WatchOverviewSnapshot>(query =>
                    FakeHostReply.Return(CreateOverview(query.MesAreas ?? []))),
                DemandSeries = FakeHostReply.Select<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>(query =>
                    FakeHostReply.Return(CreateEmptyDemandSeriesList(query))),
                ReadabilityAudit = FakeHostReply.Select<ReadabilityAuditQuery, ReadabilityAuditListSnapshot>(query =>
                {
                    if (query.Filter.ReadabilityStates.Contains(
                            ExternalReadabilityStates.NotReadable,
                            StringComparer.Ordinal))
                    {
                        queryReceived.TrySetResult(query);
                    }
                    if (query.Filter.ReadabilityStates.SequenceEqual(
                            [ExternalReadabilityStates.NotReadable],
                            StringComparer.Ordinal)
                        && query.Filter.WorkTypes.Count == 2
                        && query.Filter.Blockers.Count == 2)
                    {
                        multiFilterReceived.TrySetResult(query);
                        if (query is { PageNumber: 3, SnapshotReference: not null })
                        {
                            directPageReceived.TrySetResult(query);
                        }

                        return FakeHostReply.Return(CreatePagedAuditList(
                            query,
                            snapshotReference,
                            totalPages: 3));
                    }
                    return FakeHostReply.Return(CreateAuditList(query, snapshotReference));
                }),
                ReadabilityAuditDetail = FakeHostReply.Select<FakeHostV2DetailRequest, ReadabilityAuditDetailSnapshot>(request =>
                {
                    detailReceived.TrySetResult(request);
                    return FakeHostReply.Return(CreateAuditDetail(snapshotReference));
                }),
            },
            TestContext.Current.CancellationToken);
        using var files = new TemporaryWatchFiles();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl, credential),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                await window.InitializeAsync(timeout.Token);
                await window.ApplyAreaContextAsync(
                    new WatchAreaDisplayContext(
                        "东区",
                        ["A1-1"],
                        "本机已应用",
                        DateTimeOffset.Parse("2026-08-14T05:05:00Z")),
                    timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ReadabilityAudit,
                    PageNumber: 1,
                    MesAreas: ["A1-1"],
                    ReadabilityStates: [ExternalReadabilityStates.NotReadable],
                    Cursor: null));

                await window.ReadabilityAuditNavigationTask.WaitAsync(timeout.Token);
                var query = await queryReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(1, query.PageNumber);
                Assert.Null(query.SnapshotReference);
                Assert.Null(query.Cursor);
                Assert.Equal(["A1-1"], query.Filter.MesAreas);
                Assert.Equal(
                    [ExternalReadabilityStates.NotReadable],
                    query.Filter.ReadabilityStates);
                Assert.Equal(WatchWorkspacePage.ReadabilityAudit, window.ActivePage);

                var committed = Assert.IsType<ReadabilityAuditListSnapshot>(
                    window.WorkspaceState.ReadabilityAudit.Snapshot);
                Assert.Equal(snapshotReference, committed.SnapshotReference);
                Assert.Equal(3, committed.ExactTotalDemandCount);
                Assert.Equal(2, committed.Facets.Blockers.Count);

                var pageSummary = Find<TextBlock>(window, "ReadabilityPageSummaryText");
                Assert.Equal("精确 3 个 Demand 世代 · 第 1 / 1 页", pageSummary.Text);
                Assert.Contains(
                    pageSummary.Text,
                    AutomationProperties.GetName(pageSummary),
                    StringComparison.Ordinal);
                var stateFacets = Find<DataGrid>(window, "ReadabilityStateFacetGrid");
                var blockerFacets = Find<DataGrid>(window, "ReadabilityBlockerFacetGrid");
                Assert.Equal(2, stateFacets.Items.Count);
                Assert.Equal(2, blockerFacets.Items.Count);
                var grid = Find<DataGrid>(window, "ReadabilityAuditGrid");
                Assert.Equal("资格审计 Demand 世代列表", AutomationProperties.GetName(grid));
                Assert.Single(grid.Items);

                await window.SelectReadabilityDemandAndRenderAsync(demandId, timeout.Token);
                var detailRequest = await detailReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(demandId, detailRequest.ObjectId);
                Assert.Equal(snapshotReference, detailRequest.SnapshotReference);

                var detail = Assert.IsType<ReadabilityAuditDetailSnapshot>(
                    window.WorkspaceState.ReadabilityAudit.Detail);
                Assert.Equal(snapshotReference, detail.SnapshotReference);
                Assert.Equal(
                    committed.Snapshot.ProjectionCommitId,
                    detail.Snapshot.ProjectionCommitId);
                Assert.Equal(7, detail.Snapshot.CatalogRevision);
                Assert.Equal(2, detail.QualificationChecks.Count);
                Assert.Equal(2, detail.Blockers.Count);

                var heading = Find<TextBlock>(window, "ReadabilityDetailHeadingText");
                Assert.Contains(demandId, heading.Text, StringComparison.Ordinal);
                var facts = Find<TextBlock>(window, "ReadabilityDetailFactsText");
                Assert.Contains("CatalogRevision 7", facts.Text, StringComparison.Ordinal);
                Assert.Contains("audit-poll-21", facts.Text, StringComparison.Ordinal);
                Assert.Contains("audit-commit-21", facts.Text, StringComparison.Ordinal);
                var checks = Find<DataGrid>(window, "ReadabilityQualificationGrid");
                var blockers = Find<DataGrid>(window, "ReadabilityBlockerEvidenceGrid");
                var raw = Find<DataGrid>(window, "ReadabilityRawObservationGrid");
                Assert.Equal(2, checks.Items.Count);
                Assert.Equal(2, blockers.Items.Count);
                Assert.Equal(2, raw.Items.Count);
                Assert.Equal(
                    "Catalog Revision 7",
                    Find<TextBlock>(window, "ReadabilityCatalogRevisionText").Text);
                Assert.Contains(
                    "更新于",
                    Find<TextBlock>(window, "ReadabilityHeaderFactsText").Text,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "Host 投影提交",
                    Find<TextBlock>(window, "ReadabilityCompactFactsText").Text,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "不可见",
                    Find<TextBlock>(window, "ReadabilityNotReadableCountText").Text,
                    StringComparison.Ordinal);
                var conclusion = Find<Wpf.Ui.Controls.InfoBar>(
                    window,
                    "ReadabilityDetailInfoBar");
                Assert.True(conclusion.IsOpen);
                Assert.Contains(demandId, conclusion.Title, StringComparison.Ordinal);
                Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Warning, conclusion.Severity);

                var stateAll = Find<ButtonBase>(window, "ReadabilityStateAllButton");
                var stateReadable = Find<ButtonBase>(
                    window,
                    "ReadabilityStateReadableButton");
                var stateNotReadable = Find<ButtonBase>(
                    window,
                    "ReadabilityStateNotReadableButton");
                Assert.Equal("未选择", AutomationProperties.GetItemStatus(stateAll));
                Assert.Equal("未选择", AutomationProperties.GetItemStatus(stateReadable));
                Assert.Equal("已选择", AutomationProperties.GetItemStatus(stateNotReadable));

                stateReadable.RaiseEvent(
                    new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("未选择", AutomationProperties.GetItemStatus(stateAll));
                Assert.Equal("已选择", AutomationProperties.GetItemStatus(stateReadable));
                Assert.Equal("未选择", AutomationProperties.GetItemStatus(stateNotReadable));
                stateNotReadable.RaiseEvent(
                    new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("未选择", AutomationProperties.GetItemStatus(stateAll));
                Assert.Equal("未选择", AutomationProperties.GetItemStatus(stateReadable));
                Assert.Equal("已选择", AutomationProperties.GetItemStatus(stateNotReadable));
                Find<ComboBox>(window, "ReadabilityWorkTypeFilter").Text =
                    "WIRE_TO_GATE, WIRE_TO_NITROGEN";
                Find<ComboBox>(window, "ReadabilityBlockerFilter").Text =
                    "REQUIRED_MES_FIELD_MISSING, INVALID_MES_FIELD_FORMAT";
                Find<ComboBox>(window, "ReadabilityPageSizeInput").SelectedIndex = 1;
                Find<Wpf.Ui.Controls.Button>(window, "ReadabilityApplyFilterButton")
                    .RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                var multiFilter = await multiFilterReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(
                    [ExternalReadabilityStates.NotReadable],
                    multiFilter.Filter.ReadabilityStates);
                Assert.Equal(
                    ["WIRE_TO_GATE", "WIRE_TO_NITROGEN"],
                    multiFilter.Filter.WorkTypes);
                Assert.Equal(
                    ["INVALID_MES_FIELD_FORMAT", "REQUIRED_MES_FIELD_MISSING"],
                    multiFilter.Filter.Blockers);
                Assert.Equal(ReadabilityAuditQuery.MaximumPageSize, multiFilter.PageSize);
                Assert.Equal(1, multiFilter.PageNumber);
                Assert.Null(multiFilter.SnapshotReference);
                Assert.Null(multiFilter.Cursor);

                while (window.WorkspaceState.ReadabilityAudit.Snapshot?.TotalPages != 3)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }

                Find<TextBox>(window, "ReadabilityPageNumberInput").Text = "3";
                Find<Wpf.Ui.Controls.Button>(window, "ReadabilityGoToPageButton")
                    .RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var directPage = await directPageReceived.Task.WaitAsync(timeout.Token);
                Assert.Equal(3, directPage.PageNumber);
                Assert.Equal(snapshotReference, directPage.SnapshotReference);
                Assert.Null(directPage.Cursor);
                Assert.Equal(ReadabilityAuditQuery.MaximumPageSize, directPage.PageSize);
                Assert.Equal(
                    multiFilter.Filter.ReadabilityStates,
                    directPage.Filter.ReadabilityStates);
                Assert.Equal(multiFilter.Filter.WorkTypes, directPage.Filter.WorkTypes);
                Assert.Equal(multiFilter.Filter.Blockers, directPage.Filter.Blockers);
                Assert.Equal(multiFilter.Filter.MesAreas, directPage.Filter.MesAreas);
            }
            finally
            {
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

    private static WatchOverviewSnapshot CreateOverview(IReadOnlyList<string> areas)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:00:00Z");
        var identity = new OperationalSnapshotIdentity(
            "commit-overview-21",
            210,
            at,
            "poll-overview-21",
            210,
            7,
            at);
        var series = new OverviewNavigationIntent(
            OverviewNavigationTargets.DemandSeries,
            MesAreas: areas,
            Cursor: null);
        var audit = new OverviewNavigationIntent(
            OverviewNavigationTargets.ReadabilityAudit,
            MesAreas: areas,
            Cursor: null);
        var errors = new OverviewNavigationIntent(
            OverviewNavigationTargets.ErrorSearch,
            ErrorWindow: ErrorSearchWindowKinds.Last7Days,
            Cursor: null);
        var attention = new OverviewNavigationIntent(
            OverviewNavigationTargets.CurrentIngestAttention,
            Cursor: null);
        return new WatchOverviewSnapshot(
            identity,
            areas,
            new WatchOverviewSeriesSummary(2, 2, 0, 0, 0, series, series, series, series, series),
            new WatchOverviewReadabilitySummary(3, 1, 2, audit, audit, audit),
            new WatchOverviewErrorSummary(0, 0, errors, errors, errors),
            new WatchOverviewAttentionSummary(0, [], [], attention),
            [],
            WatchOverviewRecentActivityStates.NoRecentHighlights,
            WatchOverviewRecentActivityStates.NoRecentHighlightsMessage);
    }

    internal static ReadabilityAuditListSnapshot CreateAuditList(
        ReadabilityAuditQuery query,
        string snapshotReference)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var identity = AuditIdentity(at);
        var item = AuditItem(at);
        return new ReadabilityAuditListSnapshot(
            identity,
            snapshotReference,
            query.Filter,
            query.Order,
            ExactTotalDemandCount: 3,
            new ReadabilityAuditFacets(
                [
                    new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.Readable, 1),
                    new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.NotReadable, 2),
                ],
                [
                    new ReadabilityBlockerFacetSnapshot("REQUIRED_MES_FIELD_MISSING", 2),
                    new ReadabilityBlockerFacetSnapshot("INVALID_MES_FIELD_FORMAT", 1),
                ]),
            query.PageSize,
            query.PageNumber,
            TotalPages: 1,
            [item],
            NextCursor: null,
            HasMore: false);
    }

    private static DemandSeriesListSnapshot CreateEmptyDemandSeriesList(
        DemandSeriesBrowseQuery query)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        return new DemandSeriesListSnapshot(
            new DemandSeriesSnapshotIdentity(
                "demand-area-commit-21",
                211,
                at,
                "demand-area-poll-21"),
            "demand-area-snapshot-21",
            query.Filter,
            query.Order,
            ExactTotalCount: 0,
            new DemandSeriesFacets(0, 0, 0, 0, 0),
            query.PageSize,
            query.PageNumber,
            TotalPages: 0,
            Items: [],
            NextCursor: null,
            HasMore: false);
    }

    private static ReadabilityAuditListSnapshot CreatePagedAuditList(
        ReadabilityAuditQuery query,
        string snapshotReference,
        int totalPages)
    {
        var snapshot = CreateAuditList(query, snapshotReference);
        return snapshot with
        {
            PageNumber = query.PageNumber,
            TotalPages = totalPages,
            NextCursor = query.PageNumber < totalPages
                ? $"audit-cursor-page-{query.PageNumber + 1}"
                : null,
            HasMore = query.PageNumber < totalPages,
        };
    }

    internal static ReadabilityAuditDetailSnapshot CreateAuditDetail(string snapshotReference)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var item = AuditItem(at);
        return new ReadabilityAuditDetailSnapshot(
            AuditIdentity(at),
            snapshotReference,
            item,
            new ReadabilityAuditSeriesSnapshot(
                item.SeriesId,
                item.WorkType,
                item.Sublot,
                item.SeriesLifecycle,
                item.SeriesCurrentPresence,
                at.AddHours(-1),
                ArchivedAt: null,
                item.DemandId),
            [
                new ReadabilityQualificationCheckSnapshot(
                    "DEMAND_VISIBLE",
                    "DEMAND_GONE",
                    ReadabilityQualificationCheckResults.Passed),
                new ReadabilityQualificationCheckSnapshot(
                    "REQUIRED_MES_FIELDS_PRESENT",
                    "REQUIRED_MES_FIELD_MISSING",
                    ReadabilityQualificationCheckResults.Failed),
            ],
            [
                CreateBlocker("REQUIRED_MES_FIELD_MISSING", 40, "AREA", at),
                CreateBlocker("INVALID_MES_FIELD_FORMAT", 50, "AREA=A01-01", at),
            ],
            [
                CreateRawObservation(1, area: null, at),
                CreateRawObservation(2, "A01-01", at),
            ],
            new ReadabilityAuditPollTraceSnapshot(
                "audit-poll-21",
                "MES_TASK_UNION_V2",
                "SUCCESS",
                at.AddSeconds(-2),
                at,
                RowCount: 2,
                "digest-audit-21",
                "audit-commit-21",
                ProjectionSequence: 211));
    }

    private static ReadabilityAuditSnapshotIdentity AuditIdentity(DateTimeOffset at) => new(
        "audit-commit-21",
        211,
        at,
        "audit-poll-21",
        CatalogRevision: 7);

    private static ReadabilityAuditListItemSnapshot AuditItem(DateTimeOffset at) => new(
        "demand-audit-21",
        "series-audit-21",
        "WIRE_TO_GATE",
        "SL-AUDIT-21",
        Generation: 2,
        PredecessorDemandId: "demand-audit-20",
        DemandStatus: "VISIBLE",
        SeriesLifecycle: DemandSeriesLifecycleContract.Tracking,
        SeriesCurrentPresence: DemandSeriesLifecycleContract.Visible,
        IsCurrentGeneration: true,
        DemandCreatedAt: at.AddHours(-1),
        DemandLastSeenAt: at,
        GoneConfirmedAt: null,
        LiveMesFields: null,
        CurrentRawObservationCount: 2,
        ExternalReadabilityState: ExternalReadabilityStates.NotReadable,
        LeadReadabilityBlocker: "REQUIRED_MES_FIELD_MISSING",
        ReadabilityBlockers:
        [
            "REQUIRED_MES_FIELD_MISSING",
            "INVALID_MES_FIELD_FORMAT",
        ],
        LatestObservationPollTraceId: "audit-poll-21",
        LatestObservationProjectionCommitId: "audit-commit-21",
        LatestObservationAt: at);

    private static ReadabilityBlockerEvidenceSnapshot CreateBlocker(
        string code,
        int priority,
        string observedValue,
        DateTimeOffset at) => new(
            code,
            priority,
            [new ReadabilityEvidenceItemSnapshot(
                "MES_FIELD",
                observedValue,
                "canonical MesArea",
                at,
                "audit-poll-21",
                "audit-commit-21")]);

    private static DemandRawObservationSnapshot CreateRawObservation(
        int ordinal,
        string? area,
        DateTimeOffset at) => new(
            ordinal,
            "audit-poll-21",
            "audit-commit-21",
            MesObservationAssignment.Assigned,
            "series-audit-21",
            "demand-audit-21",
            "WIRE_TO_GATE",
            "SL-AUDIT-21",
            area,
            "EQP-AUDIT",
            "STEP-AUDIT",
            at,
            "PKG-AUDIT",
            at);

    private static T Find<T>(FrameworkElement root, string name)
        where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(root.FindName(name));

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
            Name = "Watch Readability production integration STA",
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
                $"watch-readability-production-{Guid.NewGuid():N}");
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
