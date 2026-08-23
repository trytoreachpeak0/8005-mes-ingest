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
    public async Task No_host_snapshot_reports_unavailable_blocker_facets_not_a_successful_zero()
    {
        using var files = new TemporaryWatchFiles();

        await RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions("http://127.0.0.1:5088", "unused-readability-secret"),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                var compactFacts = Find<TextBlock>(window, "ReadabilityCompactFactsText");
                Assert.Contains("SnapshotReference 尚无快照", compactFacts.Text, StringComparison.Ordinal);
                Assert.Contains("阻断原因精确分面：尚无快照", compactFacts.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("阻断原因精确分面：无命中", compactFacts.Text, StringComparison.Ordinal);
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Failed_area_b_refresh_keeps_area_a_audit_snapshot_and_opens_the_global_status_before_master_at_wide_and_narrow_widths()
    {
        const string credential = "readability-retained-area-secret";
        const string snapshotReference = "snapshot-audit-area-a";
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("readability-retained-area", credential)
            {
                Overview = FakeHostReply.Select<WatchOverviewQuery, WatchOverviewSnapshot>(query =>
                    FakeHostReply.Return(CreateOverview(query.MesAreas ?? []))),
                DemandSeries = FakeHostReply.Select<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>(query =>
                    FakeHostReply.Return(
                        WatchDemandSeriesProductionIntegrationTests.CreateDemandSeriesList(
                            query,
                            "series-audit-support",
                            "snapshot-demand-support"))),
                ReadabilityAudit = FakeHostReply.Select<ReadabilityAuditQuery, ReadabilityAuditListSnapshot>(query =>
                    query.Filter.MesAreas.SequenceEqual(["B2-2"], StringComparer.Ordinal)
                        ? FakeHostReply.Fail<ReadabilityAuditListSnapshot>(
                            WatchHostFailureKind.ServerQuery,
                            "/api/v2/readability-audit",
                            "AREA B audit projection is unavailable")
                        : FakeHostReply.Return(CreateAuditList(query, snapshotReference))),
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
                window.Width = 1440;
                window.Height = 900;
                window.Show();
                await window.InitializeAsync(timeout.Token);
                await window.ApplyAreaContextAsync(
                    new WatchAreaDisplayContext(
                        "AREA A 本机筛选",
                        ["A1-1"],
                        "本机已应用",
                        DateTimeOffset.Parse("2026-08-14T05:05:00Z")),
                    timeout.Token);
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ReadabilityAudit,
                    PageNumber: 1,
                    MesAreas: ["A1-1"],
                    Cursor: null));
                await window.ReadabilityAuditNavigationTask.WaitAsync(timeout.Token);

                await window.ApplyAreaContextAsync(
                    new WatchAreaDisplayContext(
                        "AREA B 本机筛选",
                        ["B2-2"],
                        "本机已应用",
                        DateTimeOffset.Parse("2026-08-14T05:10:00Z")),
                    timeout.Token);
                window.UpdateLayout();

                var retained = Assert.IsType<ReadabilityAuditListSnapshot>(
                    window.WorkspaceState.ReadabilityAudit.Snapshot);
                Assert.True(window.WorkspaceState.ReadabilityAudit.IsStale);
                Assert.Equal(snapshotReference, retained.SnapshotReference);
                Assert.Equal(["A1-1"], retained.Filter.MesAreas);
                Assert.Equal(["B2-2"], window.AreaContext.MesAreas);
                Assert.Contains(host.Timeline, entry =>
                    entry.Operation == FakeHostOperation.ReadabilityAuditV2
                    && entry.State == FakeHostRequestState.Failed
                    && entry.Endpoint.Contains("area=B2-2", StringComparison.Ordinal));

                var header = Find<TextBlock>(window, "ReadabilityHeaderFactsText");
                Assert.True(header.IsVisible);
                Assert.Equal(TextWrapping.NoWrap, header.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, header.TextTrimming);
                Assert.Contains("本机 AREA B 本机筛选", header.Text, StringComparison.Ordinal);
                Assert.Contains("Host A1-1", header.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("Host B2-2", header.Text, StringComparison.Ordinal);
                var fullHeader = Assert.IsType<string>(header.ToolTip);
                Assert.Contains("本机 AREA：AREA B 本机筛选", fullHeader, StringComparison.Ordinal);
                Assert.Contains("Host 已提交范围：A1-1", fullHeader, StringComparison.Ordinal);
                Assert.Equal(fullHeader, AutomationProperties.GetHelpText(header));
                Assert.Contains(
                    fullHeader,
                    AutomationProperties.GetName(header),
                    StringComparison.Ordinal);
                Assert.Contains(
                    snapshotReference,
                    Find<TextBlock>(window, "ReadabilityCompactFactsText").Text,
                    StringComparison.Ordinal);

                var status = Find<Wpf.Ui.Controls.InfoBar>(window, "ReadabilityAuditInfoBar");
                var statusRegion = Find<StackPanel>(window, "ReadabilityGlobalStatusRegion");
                var layout = Find<Grid>(window, "ReadabilityAuditLayoutGrid");
                var page = Find<ScrollViewer>(window, "ReadabilityAuditPage");
                var master = Find<Wpf.Ui.Controls.Card>(window, "ReadabilityMasterCard");
                Assert.True(status.IsOpen);
                Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Warning, status.Severity);
                Assert.Equal("资格审计刷新失败，已保留上次快照", status.Title);
                Assert.Contains("继续显示 Host 快照", status.Message, StringComparison.Ordinal);
                Assert.Contains("AREA B audit projection is unavailable", status.Message, StringComparison.Ordinal);
                Assert.Equal(
                    $"{status.Title}。{status.Message}",
                    AutomationProperties.GetName(status));
                Assert.Contains(status, statusRegion.Children.Cast<UIElement>());

                AssertGlobalStatusPrecedesMaster("1440");
                window.Width = 720;
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                window.UpdateLayout();
                AssertGlobalStatusPrecedesMaster("720");

                void AssertGlobalStatusPrecedesMaster(string widthLabel)
                {
                    Assert.True(statusRegion.IsVisible);
                    Assert.True(status.IsVisible);
                    Assert.InRange(status.ActualHeight, 1, page.ActualHeight);
                    Assert.InRange(
                        Math.Abs(statusRegion.ActualWidth - layout.ActualWidth),
                        0,
                        1.5);
                    var statusTop = status.TranslatePoint(new Point(), layout).Y;
                    var statusBottom = statusTop + status.ActualHeight;
                    var masterTop = master.TranslatePoint(new Point(), layout).Y;
                    Assert.True(
                        statusBottom <= masterTop + 0.5,
                        $"At {widthLabel}px the global Audit status must precede master; statusBottom={statusBottom:0.##}, masterTop={masterTop:0.##}.");
                    var viewportTop = status.TranslatePoint(new Point(), page).Y;
                    Assert.InRange(viewportTop, 0, page.ActualHeight);
                    Assert.True(
                        viewportTop + status.ActualHeight <= page.ActualHeight + 0.5,
                        $"At {widthLabel}px the global Audit status must be initially visible; statusBottom={viewportTop + status.ActualHeight:0.##}, viewportHeight={page.ActualHeight:0.##}.");
                }
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public async Task Overview_drill_loads_host_exact_audit_facets_then_same_snapshot_detail()
    {
        const string credential = "readability-production-secret";
        const string demandId = "demand-audit-21";
        const string snapshotReference = "snapshot-audit-21";
        const string zeroSnapshotReference = "snapshot-audit-zero-21";
        var queryReceived = new TaskCompletionSource<ReadabilityAuditQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detailReceived = new TaskCompletionSource<FakeHostV2DetailRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var multiFilterReceived = new TaskCompletionSource<ReadabilityAuditQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var directPageReceived = new TaskCompletionSource<ReadabilityAuditQuery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var successfulZeroReceived = new TaskCompletionSource<ReadabilityAuditQuery>(
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
                    if (string.Equals(query.Filter.SublotContains, "NO_MATCH", StringComparison.Ordinal))
                    {
                        successfulZeroReceived.TrySetResult(query);
                        return FakeHostReply.Return(CreateEmptyAuditList(
                            query,
                            zeroSnapshotReference));
                    }

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
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        await RunInStaDispatcherAsync(async () =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(host.BaseUrl, credential),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 1440;
                window.Height = 900;
                window.Show();
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

                // The detail render invalidates layout synchronously, while measure/arrange is
                // queued at a lower Dispatcher priority. Drain that queue before reading geometry
                // so this real-window assertion is isolated from preceding suite load.
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

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
                Assert.Equal($"{demandId} · WIRE_TO_GATE", heading.Text);
                var facts = Find<TextBlock>(window, "ReadabilityDetailFactsText");
                Assert.Equal(
                    $"SL-AUDIT-21 · series-audit-21 · Demand Generation 2 · 最后看见 {WatchTimeDisplay.Format(DateTimeOffset.Parse("2026-08-14T05:06:07Z"))}",
                    facts.Text);
                Assert.DoesNotContain("Snapshot", facts.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("PollTrace", facts.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("CatalogRevision", facts.Text, StringComparison.Ordinal);
                var detailSnapshotEvidence = Assert.IsType<string>(facts.ToolTip);
                Assert.Contains("CatalogRevision 7", detailSnapshotEvidence, StringComparison.Ordinal);
                Assert.Contains("audit-poll-21", detailSnapshotEvidence, StringComparison.Ordinal);
                Assert.Contains("audit-commit-21", detailSnapshotEvidence, StringComparison.Ordinal);
                Assert.Equal(
                    detailSnapshotEvidence,
                    AutomationProperties.GetHelpText(facts));
                var checks = Find<DataGrid>(window, "ReadabilityQualificationGrid");
                var blockers = Find<DataGrid>(window, "ReadabilityBlockerEvidenceGrid");
                var raw = Find<DataGrid>(window, "ReadabilityRawObservationGrid");
                Assert.Equal(2, checks.Items.Count);
                Assert.Equal(2, blockers.Items.Count);
                Assert.Equal(2, raw.Items.Count);

                var primaryBlockerCard = Find<Border>(
                    window,
                    "ReadabilityPrimaryBlockerCard");
                var primaryBlockerCode = Find<TextBlock>(
                    window,
                    "ReadabilityPrimaryBlockerCodeText");
                var qualificationChecklist = Find<ItemsControl>(
                    window,
                    "ReadabilityQualificationChecklist");
                var qualificationConclusion = Find<TextBlock>(
                    window,
                    "ReadabilityQualificationConclusionText");
                var qualificationConclusionCard = Find<Border>(
                    window,
                    "ReadabilityQualificationConclusionCard");
                var liveMesFields = Find<Grid>(window, "ReadabilityLiveMesFieldsGrid");
                var liveMesFacts = Find<TextBlock>(window, "ReadabilityLiveMesFactsText");
                var revisionFacts = Find<TextBlock>(
                    window,
                    "ReadabilityRevisionFactsText");
                var deepEvidence = Find<Expander>(
                    window,
                    "ReadabilityDeepEvidenceExpander");
                Assert.True(
                    primaryBlockerCard.IsVisible,
                    $"Primary blocker must be visible; visibility={primaryBlockerCard.Visibility}, size={primaryBlockerCard.ActualWidth:0.##}x{primaryBlockerCard.ActualHeight:0.##}, detail-size={Find<Wpf.Ui.Controls.Card>(window, "ReadabilityDetailCard").ActualWidth:0.##}x{Find<Wpf.Ui.Controls.Card>(window, "ReadabilityDetailCard").ActualHeight:0.##}.");
                Assert.Equal("INVALID_MES_FIELD_FORMAT", primaryBlockerCode.Text);
                Assert.Equal("Blocked", primaryBlockerCard.Tag);
                Assert.Equal("Blocked", qualificationConclusionCard.Tag);
                Assert.Equal(2, qualificationChecklist.Items.Count);
                Assert.True(
                    qualificationChecklist.IsVisible,
                    $"Qualification checklist must be visible before capture; size={qualificationChecklist.ActualWidth:0.##}x{qualificationChecklist.ActualHeight:0.##}.");
                Assert.Contains("NOT_READABLE", qualificationConclusion.Text, StringComparison.Ordinal);
                Assert.Equal(3, liveMesFields.ColumnDefinitions.Count);
                Assert.Equal("—", Find<TextBlock>(window, "ReadabilityLiveMesAreaText").Text);
                Assert.Equal("—", Find<TextBlock>(window, "ReadabilityLiveMesEqpText").Text);
                Assert.Equal("—", Find<TextBlock>(window, "ReadabilityLiveMesStepText").Text);
                Assert.Equal("—", Find<TextBlock>(window, "ReadabilityLiveMesDateText").Text);
                Assert.Equal("—", Find<TextBlock>(window, "ReadabilityLiveMesPackageText").Text);
                Assert.DoesNotContain("PollTrace", liveMesFacts.Text, StringComparison.Ordinal);
                Assert.Contains(
                    "PollTrace audit-poll-21",
                    AutomationProperties.GetHelpText(liveMesFields),
                    StringComparison.Ordinal);
                Assert.Equal("Catalog Revision 7", revisionFacts.Text);
                var revisionEvidence = Assert.IsType<string>(revisionFacts.ToolTip);
                Assert.Contains("Snapshot snapshot-audit-21", revisionEvidence, StringComparison.Ordinal);
                Assert.Contains("Projection 211", revisionEvidence, StringComparison.Ordinal);
                Assert.Contains("audit-commit-21", revisionEvidence, StringComparison.Ordinal);
                Assert.Contains("PollTrace audit-poll-21", revisionEvidence, StringComparison.Ordinal);
                Assert.Equal(revisionEvidence, AutomationProperties.GetHelpText(revisionFacts));
                Assert.Equal("查看完整结构化证据", deepEvidence.Header);
                Assert.False(deepEvidence.IsExpanded);
                Assert.False(checks.IsVisible);
                Assert.False(blockers.IsVisible);
                Assert.False(raw.IsVisible);

                window.UpdateLayout();
                var filterCard = Find<Wpf.Ui.Controls.Card>(window, "ReadabilityFilterCard");
                var filterTop = filterCard.TranslatePoint(new Point(), window).Y;
                Assert.InRange(filterTop, 130, 140);
                Assert.InRange(filterCard.ActualHeight, 80, 90);

                var compactFacts = Find<TextBlock>(window, "ReadabilityCompactFactsText");
                Assert.Equal(Visibility.Visible, compactFacts.Visibility);
                Assert.Equal(TextWrapping.NoWrap, compactFacts.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, compactFacts.TextTrimming);
                Assert.Equal(compactFacts.Text, compactFacts.ToolTip);
                Assert.Equal(
                    compactFacts.Text,
                    AutomationProperties.GetHelpText(compactFacts));
                Assert.Contains("SnapshotReference snapshot-audit-21", compactFacts.Text, StringComparison.Ordinal);
                Assert.Contains("Watch 最近成功", compactFacts.Text, StringComparison.Ordinal);
                Assert.Contains("Host 固定排序", compactFacts.Text, StringComparison.Ordinal);
                Assert.Contains("阻断原因精确分面", compactFacts.Text, StringComparison.Ordinal);
                Assert.Contains("原因可重叠", compactFacts.Text, StringComparison.Ordinal);
                Assert.Contains("不可见总数按 Demand 世代去重", compactFacts.Text, StringComparison.Ordinal);
                var compactFactsBottom = compactFacts.TranslatePoint(
                    new Point(0, compactFacts.ActualHeight),
                    window).Y;
                Assert.True(
                    compactFactsBottom <= filterTop + 0.5,
                    $"Persistent Audit facts must stay in the header before the 84px filter; factsBottom={compactFactsBottom:0.##}, filterTop={filterTop:0.##}.");
                var filterPanel = Find<Grid>(window, "ReadabilityFilterPanel");
                var filterHelp = AutomationProperties.GetHelpText(filterPanel);
                Assert.Contains("Host 投影提交", filterHelp, StringComparison.Ordinal);
                Assert.Contains("阻断原因精确分面", filterHelp, StringComparison.Ordinal);

                var master = Find<Wpf.Ui.Controls.Card>(window, "ReadabilityMasterCard");
                var status = Find<Wpf.Ui.Controls.InfoBar>(window, "ReadabilityDetailInfoBar");
                var noticeHost = Find<StackPanel>(window, "ReadabilityDetailNotices");
                Assert.Contains(status, noticeHost.Children.Cast<UIElement>());
                Assert.Equal(0, Panel.GetZIndex(status));
                var masterTop = master.TranslatePoint(new Point(), window).Y;
                var statusTop = status.TranslatePoint(new Point(), window).Y;
                Assert.InRange(masterTop, 230, 244);
                Assert.InRange(Math.Abs(masterTop - statusTop), 0, 1.5);

                var detailCards = Find<Grid>(window, "ReadabilityDetailCardsGrid");
                var detailCard = Find<Wpf.Ui.Controls.Card>(
                    window,
                    "ReadabilityDetailCard");
                var summaryCard = Find<Wpf.Ui.Controls.Card>(
                    window,
                    "ReadabilityAuditSummaryCard");
                Assert.Equal(VerticalAlignment.Stretch, detailCard.VerticalAlignment);
                Assert.Equal(VerticalAlignment.Stretch, summaryCard.VerticalAlignment);
                Assert.Equal(12, detailCards.RowDefinitions[1].Height.Value);
                var detailCardBottom = detailCard.TranslatePoint(
                    new Point(0, detailCard.ActualHeight),
                    window).Y;
                var summaryCardTop = summaryCard.TranslatePoint(new Point(), window).Y;
                Assert.InRange(Math.Abs(summaryCardTop - detailCardBottom - 12), 0, 1.5);
                var detailBottom = detailCards.TranslatePoint(
                    new Point(0, detailCards.ActualHeight),
                    window).Y;
                var masterBottom = master.TranslatePoint(
                    new Point(0, master.ActualHeight),
                    window).Y;
                var summaryBottom = summaryCard.TranslatePoint(
                    new Point(0, summaryCard.ActualHeight),
                    window).Y;
                Assert.InRange(Math.Abs(masterBottom - detailBottom), 0, 1.5);
                Assert.InRange(Math.Abs(masterBottom - summaryBottom), 0, 1.5);
                Assert.Equal(
                    "Catalog Revision 7",
                    Find<TextBlock>(window, "ReadabilityCatalogRevisionText").Text);
                var headerFacts = Find<TextBlock>(window, "ReadabilityHeaderFactsText");
                Assert.Contains(
                    "更新于",
                    headerFacts.Text,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "东区",
                    headerFacts.Text,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "Host A1-1",
                    headerFacts.Text,
                    StringComparison.Ordinal);
                var headerFullFacts = Assert.IsType<string>(headerFacts.ToolTip);
                Assert.Equal(
                    headerFullFacts,
                    AutomationProperties.GetHelpText(headerFacts));
                Assert.Contains("Host 已提交范围", headerFullFacts, StringComparison.Ordinal);
                Assert.Contains("audit-commit-21", headerFullFacts, StringComparison.Ordinal);
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

                Find<TextBox>(window, "ReadabilitySublotFilter").Text = "NO_MATCH";
                Find<Wpf.Ui.Controls.Button>(window, "ReadabilityApplyFilterButton")
                    .RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var successfulZero = await successfulZeroReceived.Task.WaitAsync(timeout.Token);
                Assert.Null(successfulZero.SnapshotReference);
                Assert.Equal(1, successfulZero.PageNumber);
                while (!string.Equals(
                           window.WorkspaceState.ReadabilityAudit.Snapshot?.SnapshotReference,
                           zeroSnapshotReference,
                           StringComparison.Ordinal)
                       || !Find<TextBlock>(window, "ReadabilityCompactFactsText")
                           .Text
                           .Contains(
                               $"SnapshotReference {zeroSnapshotReference}",
                               StringComparison.Ordinal))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }

                var zeroFacts = Find<TextBlock>(window, "ReadabilityCompactFactsText").Text;
                Assert.Contains(
                    $"SnapshotReference {zeroSnapshotReference}",
                    zeroFacts,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "阻断原因精确分面：无命中",
                    zeroFacts,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "阻断原因精确分面：尚无快照",
                    zeroFacts,
                    StringComparison.Ordinal);
                Assert.True(Find<Wpf.Ui.Controls.InfoBar>(window, "ReadabilityEmptyInfoBar").IsOpen);
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

    private static ReadabilityAuditListSnapshot CreateEmptyAuditList(
        ReadabilityAuditQuery query,
        string snapshotReference) =>
        CreateAuditList(query, snapshotReference) with
        {
            ExactTotalDemandCount = 0,
            Facets = new ReadabilityAuditFacets([], []),
            PageNumber = 1,
            TotalPages = 0,
            Items = [],
            NextCursor = null,
            HasMore = false,
        };

    private static DemandSeriesListSnapshot CreateEmptyDemandSeriesList(
        DemandSeriesBrowseQuery query)
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        return new DemandSeriesListSnapshot(
            new DemandSeriesSnapshotIdentity(
                HistoryEpoch.CreateNew(),
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
        // Host lead authority deliberately differs from the numeric evidence order.
        LeadReadabilityBlocker: "INVALID_MES_FIELD_FORMAT",
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
