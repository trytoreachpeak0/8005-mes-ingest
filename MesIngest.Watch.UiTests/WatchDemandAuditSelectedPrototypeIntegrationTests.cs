using System.Windows;
using System.Windows.Controls;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchDemandAuditSelectedPrototypeIntegrationTests
{
    [Fact]
    public async Task Demand_wide_real_window_keeps_header_filters_master_and_detail_in_one_viewport()
    {
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 1440;
                window.Height = 900;
                window.Show();
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeries,
                    PageNumber: 1,
                    Cursor: null));
                Find<Border>(window, "DemandSeriesAllAreasConfirmPanel").Visibility =
                    Visibility.Visible;
                window.UpdateLayout();

                var viewport = Find<ScrollViewer>(window, "DemandSeriesScrollViewer");
                Assert.Equal(ScrollBarVisibility.Disabled, viewport.VerticalScrollBarVisibility);
                Assert.Equal(0, viewport.VerticalOffset);

                var workspace = Find<Grid>(window, "DemandSeriesMasterDetailGrid");
                Assert.Empty(workspace.ColumnDefinitions);
                Assert.Equal(3, workspace.RowDefinitions.Count);
                AssertStar(workspace.RowDefinitions[0].Height, 0.9);
                AssertPixel(workspace.RowDefinitions[1].Height, 16);
                AssertStar(workspace.RowDefinitions[2].Height, 1.1);

                var master = Find<Border>(window, "DemandSeriesMasterPanel");
                var detail = Find<Border>(window, "DemandSeriesDetailPanel");
                Assert.Equal((0, 0), (Grid.GetColumn(master), Grid.GetRow(master)));
                Assert.Equal((0, 2), (Grid.GetColumn(detail), Grid.GetRow(detail)));

                AssertFullyWithin(
                    Find<Wpf.Ui.Controls.TextBlock>(window, "DemandSeriesContextText"),
                    viewport,
                    "Demand header");
                AssertFullyWithin(
                    Find<ScrollViewer>(window, "DemandSeriesFilterScroller"),
                    viewport,
                    "Demand filters");
                AssertFullyWithin(master, viewport, "Demand master");
                AssertFullyWithin(detail, viewport, "Demand detail");
                AssertFullyWithin(
                    Find<Border>(window, "DemandSeriesAllAreasConfirmPanel"),
                    viewport,
                    "Demand all-AREA confirmation");
                AssertFullyWithin(
                    Find<DataGrid>(window, "DemandSeriesEventGrid"),
                    viewport,
                    "Demand immutable-event evidence");
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Audit_wide_real_window_keeps_header_filters_master_and_both_detail_cards_in_one_viewport()
    {
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 1440;
                window.Height = 900;
                window.Show();
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ReadabilityAudit,
                    PageNumber: 1,
                    Cursor: null));
                var status = Find<Wpf.Ui.Controls.InfoBar>(window, "ReadabilityAuditInfoBar");
                status.Title = "资格审计刷新失败";
                status.Message = "继续显示上一份成功快照；筛选、分页与详情仍绑定同一 SnapshotReference。";
                status.IsOpen = true;
                window.UpdateLayout();

                var viewport = Find<ScrollViewer>(window, "ReadabilityAuditPage");
                Assert.Equal(ScrollBarVisibility.Disabled, viewport.VerticalScrollBarVisibility);
                Assert.Equal(0, viewport.VerticalOffset);

                var compactFacts = Find<TextBlock>(window, "ReadabilityCompactFactsText");
                Assert.Equal(TextWrapping.NoWrap, compactFacts.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, compactFacts.TextTrimming);
                Assert.Equal(compactFacts.Text, compactFacts.ToolTip);
                Assert.Contains("阻断原因精确分面", compactFacts.Text, StringComparison.Ordinal);
                Assert.Equal(
                    Visibility.Collapsed,
                    Find<TextBlock>(window, "ReadabilityBlockerFacetSummaryText").Visibility);

                var detailFacts = Find<TextBlock>(window, "ReadabilityDetailFactsText");
                Assert.Equal(TextWrapping.NoWrap, detailFacts.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, detailFacts.TextTrimming);
                Assert.Equal(detailFacts.Text, detailFacts.ToolTip);

                AssertFullyWithin(
                    Find<Wpf.Ui.Controls.TextBlock>(window, "ReadabilityHeaderFactsText"),
                    viewport,
                    "Audit header");
                AssertFullyWithin(
                    Find<ScrollViewer>(window, "ReadabilityFilterScroller"),
                    viewport,
                    "Audit filters");
                AssertFullyWithin(
                    Find<Wpf.Ui.Controls.Card>(window, "ReadabilityMasterCard"),
                    viewport,
                    "Audit master");
                AssertFullyWithin(
                    Find<Grid>(window, "ReadabilityDetailCardsGrid"),
                    viewport,
                    "Audit detail cards");
                AssertFullyWithin(status, viewport, "Audit status InfoBar");
                AssertFullyWithin(
                    Find<DataGrid>(window, "ReadabilityRawObservationGrid"),
                    viewport,
                    "Audit raw-observation evidence");
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Current_attention_wide_real_window_keeps_header_filters_master_and_both_detail_sections_in_one_viewport()
    {
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 1440;
                window.Height = 900;
                window.Show();
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.CurrentIngestAttention,
                    PageNumber: 1,
                    Cursor: null));
                var status = Find<Wpf.Ui.Controls.InfoBar>(
                    window,
                    "CurrentAttentionStatusInfoBar");
                status.Title = "当前关注刷新失败";
                status.Message = "继续保留上一份成功快照，当前筛选与结构化证据没有被失败轮次改写。";
                status.IsOpen = true;
                window.UpdateLayout();

                var viewport = Find<ScrollViewer>(window, "CurrentAttentionPage");
                Assert.Equal(ScrollBarVisibility.Disabled, viewport.VerticalScrollBarVisibility);
                Assert.Equal(0, viewport.VerticalOffset);

                var snapshotFacts = Find<TextBlock>(window, "CurrentAttentionSnapshotText");
                Assert.Equal(TextWrapping.NoWrap, snapshotFacts.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, snapshotFacts.TextTrimming);
                Assert.Equal(snapshotFacts.Text, snapshotFacts.ToolTip);
                var selectedContext = Find<TextBlock>(
                    window,
                    "CurrentAttentionSelectedContextText");
                Assert.Equal(TextWrapping.NoWrap, selectedContext.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, selectedContext.TextTrimming);
                Assert.Equal(selectedContext.Text, selectedContext.ToolTip);

                AssertFullyWithin(
                    Find<Border>(window, "CurrentAttentionHeaderStatusPill"),
                    viewport,
                    "Current Attention status pill");
                AssertFullyWithin(
                    Find<Wpf.Ui.Controls.Card>(window, "CurrentAttentionFacetCard"),
                    viewport,
                    "Current Attention filters");
                AssertFullyWithin(
                    Find<Wpf.Ui.Controls.Card>(window, "CurrentAttentionResultsCard"),
                    viewport,
                    "Current Attention master");
                AssertFullyWithin(
                    Find<Wpf.Ui.Controls.Card>(window, "CurrentAttentionEvidenceCard"),
                    viewport,
                    "Current Attention detail");
                AssertFullyWithin(status, viewport, "Current Attention status InfoBar");
                AssertFullyWithin(
                    Find<DataGrid>(window, "CurrentAttentionEvidenceGrid"),
                    viewport,
                    "Current Attention structured evidence");
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Wide_minimum_height_restores_outer_scrolling_and_reaches_each_lower_evidence_region()
    {
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 1440;
                window.Height = 600;
                window.Show();

                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeries,
                    PageNumber: 1,
                    Cursor: null));
                Find<Border>(window, "DemandSeriesAllAreasConfirmPanel").Visibility =
                    Visibility.Visible;
                window.UpdateLayout();
                AssertWideScrollablePageReaches(
                    window,
                    Find<ScrollViewer>(window, "DemandSeriesScrollViewer"),
                    Find<DataGrid>(window, "DemandSeriesEventGrid"),
                    "Demand immutable-event evidence");
                Assert.Equal(
                    (0, 2),
                    (Grid.GetColumn(Find<Border>(window, "DemandSeriesDetailPanel")),
                        Grid.GetRow(Find<Border>(window, "DemandSeriesDetailPanel"))));

                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ReadabilityAudit,
                    PageNumber: 1,
                    Cursor: null));
                var auditStatus = Find<Wpf.Ui.Controls.InfoBar>(
                    window,
                    "ReadabilityAuditInfoBar");
                auditStatus.Title = "资格审计刷新失败";
                auditStatus.Message = "继续显示上一份成功快照。";
                auditStatus.IsOpen = true;
                window.UpdateLayout();
                AssertWideScrollablePageReaches(
                    window,
                    Find<ScrollViewer>(window, "ReadabilityAuditPage"),
                    Find<DataGrid>(window, "ReadabilityRawObservationGrid"),
                    "Audit raw-observation evidence");
                Assert.Equal(
                    (2, 0),
                    (Grid.GetColumn(Find<Grid>(window, "ReadabilityDetailRegion")),
                        Grid.GetRow(Find<Grid>(window, "ReadabilityDetailRegion"))));

                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.CurrentIngestAttention,
                    PageNumber: 1,
                    Cursor: null));
                var attentionStatus = Find<Wpf.Ui.Controls.InfoBar>(
                    window,
                    "CurrentAttentionStatusInfoBar");
                attentionStatus.Title = "当前关注刷新失败";
                attentionStatus.Message = "继续保留上一份成功快照。";
                attentionStatus.IsOpen = true;
                window.UpdateLayout();
                AssertWideScrollablePageReaches(
                    window,
                    Find<ScrollViewer>(window, "CurrentAttentionPage"),
                    Find<DataGrid>(window, "CurrentAttentionEvidenceGrid"),
                    "Current Attention structured evidence");
                Assert.Equal(
                    (2, 2),
                    (Grid.GetColumn(Find<Wpf.Ui.Controls.Card>(
                            window,
                            "CurrentAttentionEvidenceCard")),
                        Grid.GetRow(Find<Wpf.Ui.Controls.Card>(
                            window,
                            "CurrentAttentionEvidenceCard"))));
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Navigation_pane_width_change_reflows_without_a_window_size_change()
    {
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 1300;
                window.Height = 900;
                window.Show();
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeries,
                    PageNumber: 1,
                    Cursor: null));
                window.UpdateLayout();

                var navigation = Find<Wpf.Ui.Controls.NavigationView>(
                    window,
                    "WorkspaceNavigation");
                navigation.IsPaneOpen = false;
                window.UpdateLayout();
                var windowWidth = window.ActualWidth;
                var demandWorkspace = Find<Grid>(window, "DemandSeriesMasterDetailGrid");
                AssertStar(demandWorkspace.RowDefinitions[0].Height, 0.9);
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    Find<ScrollViewer>(window, "DemandSeriesScrollViewer")
                        .VerticalScrollBarVisibility);

                navigation.IsPaneOpen = true;
                window.UpdateLayout();

                Assert.Equal(windowWidth, window.ActualWidth);
                Assert.True(demandWorkspace.RowDefinitions[0].Height.IsAuto);
                Assert.True(demandWorkspace.RowDefinitions[2].Height.IsAuto);
                Assert.Equal(
                    ScrollBarVisibility.Auto,
                    Find<ScrollViewer>(window, "DemandSeriesScrollViewer")
                        .VerticalScrollBarVisibility);

                navigation.IsPaneOpen = false;
                window.UpdateLayout();

                Assert.Equal(windowWidth, window.ActualWidth);
                AssertStar(demandWorkspace.RowDefinitions[0].Height, 0.9);
                AssertStar(demandWorkspace.RowDefinitions[2].Height, 1.1);
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    Find<ScrollViewer>(window, "DemandSeriesScrollViewer")
                        .VerticalScrollBarVisibility);
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Narrow_real_window_stacks_the_three_workspaces_and_restores_page_scrolling()
    {
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 720;
                window.Height = 600;
                window.Show();

                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeries,
                    PageNumber: 1,
                    Cursor: null));
                window.UpdateLayout();
                Assert.Equal(
                    ScrollBarVisibility.Auto,
                    Find<ScrollViewer>(window, "DemandSeriesScrollViewer")
                        .VerticalScrollBarVisibility);
                var demandMaster = Find<Border>(window, "DemandSeriesMasterPanel");
                var demandDetail = Find<Border>(window, "DemandSeriesDetailPanel");
                Assert.Equal((0, 0), (Grid.GetColumn(demandMaster), Grid.GetRow(demandMaster)));
                Assert.Equal((0, 2), (Grid.GetColumn(demandDetail), Grid.GetRow(demandDetail)));
                var demandWorkspace = Find<Grid>(window, "DemandSeriesMasterDetailGrid");
                Assert.True(demandWorkspace.RowDefinitions[0].Height.IsAuto);
                AssertPixel(demandWorkspace.RowDefinitions[1].Height, 16);
                Assert.True(demandWorkspace.RowDefinitions[2].Height.IsAuto);

                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ReadabilityAudit,
                    PageNumber: 1,
                    Cursor: null));
                window.UpdateLayout();
                Assert.Equal(
                    ScrollBarVisibility.Auto,
                    Find<ScrollViewer>(window, "ReadabilityAuditPage")
                        .VerticalScrollBarVisibility);
                Assert.Equal(
                    (0, 2),
                    (Grid.GetColumn(Find<Grid>(window, "ReadabilityDetailRegion")),
                        Grid.GetRow(Find<Grid>(window, "ReadabilityDetailRegion"))));

                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.CurrentIngestAttention,
                    PageNumber: 1,
                    Cursor: null));
                window.UpdateLayout();
                Assert.Equal(
                    ScrollBarVisibility.Auto,
                    Find<ScrollViewer>(window, "CurrentAttentionPage")
                        .VerticalScrollBarVisibility);
                Assert.Equal(
                    (0, 4),
                    (Grid.GetColumn(Find<Wpf.Ui.Controls.Card>(
                            window,
                            "CurrentAttentionEvidenceCard")),
                        Grid.GetRow(Find<Wpf.Ui.Controls.Card>(
                            window,
                            "CurrentAttentionEvidenceCard"))));
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Demand_and_audit_restore_the_selected_filters_facts_and_detail_hierarchy()
    {
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 1440;
                window.Height = 900;
                window.Show();
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeries,
                    PageNumber: 1,
                    Cursor: null));
                window.UpdateLayout();

                var demandRoot = Find<Grid>(window, "DemandSeriesLayoutGrid");
                Assert.Equal(new Thickness(0), demandRoot.Margin);
                Assert.Equal(
                    Resource<Style>(window, "CaptionText"),
                    Find<Wpf.Ui.Controls.TextBlock>(
                        window,
                        "DemandSeriesLifecycleFilterLabel").Style);

                var demandLifecycle = Find<StackPanel>(
                    window,
                    "DemandSeriesLifecycleSegment");
                Assert.Equal(3, demandLifecycle.Children.OfType<Button>().Count());
                Assert.Null(window.FindName("DemandSeriesLifecycleFilter"));
                Assert.NotNull(Find<Border>(window, "DemandSeriesTrackingFacetPill"));
                Assert.NotNull(Find<Border>(window, "DemandSeriesArchivedFacetPill"));
                Assert.NotNull(Find<ItemsControl>(window, "DemandSeriesLifecycleMilestones"));
                var demandFilters = Find<Grid>(window, "DemandSeriesFilterPanel");
                Assert.Equal(13, demandFilters.ColumnDefinitions.Count);
                AssertPixel(demandFilters.ColumnDefinitions[1].Width, 12);
                AssertPixel(demandFilters.ColumnDefinitions[2].Width, 180);
                AssertPixel(demandFilters.ColumnDefinitions[3].Width, 12);
                AssertPixel(demandFilters.ColumnDefinitions[4].Width, 155);
                AssertPixel(demandFilters.ColumnDefinitions[5].Width, 12);
                AssertPixel(demandFilters.ColumnDefinitions[6].Width, 170);
                AssertPixel(demandFilters.ColumnDefinitions[7].Width, 12);
                AssertPixel(demandFilters.ColumnDefinitions[8].Width, 180);
                AssertPixel(demandFilters.ColumnDefinitions[9].Width, 12);
                AssertPixel(demandFilters.ColumnDefinitions[10].Width, 180);
                AssertStar(demandFilters.ColumnDefinitions[11].Width, 1);
                Assert.True(demandFilters.ColumnDefinitions[12].Width.IsAuto);
                Assert.Empty(demandFilters.RowDefinitions);
                var filterFields = demandFilters.Children
                    .OfType<StackPanel>()
                    .ToArray();
                Assert.Equal(6, filterFields.Length);
                Assert.All(filterFields, field => Assert.Equal(0, Grid.GetRow(field)));
                Assert.All(
                    filterFields,
                    field => Assert.InRange(
                        field.TranslatePoint(new Point(0, 0), demandFilters).Y,
                        -0.5,
                        0.5));
                var filterScroller = Find<ScrollViewer>(
                    window,
                    "DemandSeriesFilterScroller");
                Assert.Equal(
                    ScrollBarVisibility.Auto,
                    filterScroller.HorizontalScrollBarVisibility);
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    filterScroller.VerticalScrollBarVisibility);

                var demandAreaSelector = Find<ComboBox>(
                    window,
                    "DemandSeriesAreaProfileSelector");
                var auditAreaSelector = Find<ComboBox>(
                    window,
                    "ReadabilityAreaProfileSelector");
                Assert.Same(demandAreaSelector.ItemsSource, auditAreaSelector.ItemsSource);

                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ReadabilityAudit,
                    PageNumber: 1,
                    Cursor: null));
                window.UpdateLayout();

                var auditRoot = Find<Grid>(window, "ReadabilityAuditLayoutGrid");
                Assert.Equal(new Thickness(0), auditRoot.Margin);
                Assert.Equal(
                    Resource<Style>(window, "CaptionText"),
                    Find<Wpf.Ui.Controls.TextBlock>(
                        window,
                        "ReadabilityStateFilterLabel").Style);

                var auditState = Find<StackPanel>(window, "ReadabilityStateSegment");
                Assert.Equal(3, auditState.Children.OfType<Button>().Count());
                Assert.Null(window.FindName("ReadabilityStateFilter"));
                Assert.Equal(
                    Visibility.Visible,
                    Find<TextBlock>(window, "ReadabilityCompactFactsText").Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    Find<TextBlock>(window, "ReadabilityBlockerFacetSummaryText").Visibility);
                Assert.NotNull(Find<Border>(window, "ReadabilityCatalogRevisionPill"));
                Assert.NotNull(Find<Border>(window, "ReadabilityNotReadableCountPill"));
                var auditFilters = Find<Grid>(window, "ReadabilityFilterPanel");
                Assert.Equal(11, auditFilters.ColumnDefinitions.Count);
                AssertPixel(auditFilters.ColumnDefinitions[0].Width, 290);
                AssertPixel(auditFilters.ColumnDefinitions[1].Width, 12);
                AssertPixel(auditFilters.ColumnDefinitions[2].Width, 165);
                AssertPixel(auditFilters.ColumnDefinitions[3].Width, 12);
                AssertPixel(auditFilters.ColumnDefinitions[4].Width, 155);
                AssertPixel(auditFilters.ColumnDefinitions[5].Width, 12);
                AssertPixel(auditFilters.ColumnDefinitions[6].Width, 220);
                AssertPixel(auditFilters.ColumnDefinitions[7].Width, 12);
                AssertPixel(auditFilters.ColumnDefinitions[8].Width, 180);
                AssertStar(auditFilters.ColumnDefinitions[9].Width, 1);
                Assert.True(auditFilters.ColumnDefinitions[10].Width.IsAuto);
                Assert.Empty(auditFilters.RowDefinitions);
                Assert.All(
                    auditFilters.Children.OfType<StackPanel>(),
                    field => Assert.Equal(0, Grid.GetRow(field)));
                var auditScroller = Find<ScrollViewer>(
                    window,
                    "ReadabilityFilterScroller");
                Assert.Equal(
                    ScrollBarVisibility.Auto,
                    auditScroller.HorizontalScrollBarVisibility);
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    auditScroller.VerticalScrollBarVisibility);

                var detailRegion = Find<Grid>(window, "ReadabilityDetailRegion");
                Assert.Equal(3, detailRegion.RowDefinitions.Count);
                Assert.True(detailRegion.RowDefinitions[0].Height.IsAuto);
                AssertPixel(detailRegion.RowDefinitions[1].Height, 12);
                AssertStar(detailRegion.RowDefinitions[2].Height, 1);
                var conclusion = Find<Wpf.Ui.Controls.InfoBar>(
                    window,
                    "ReadabilityDetailInfoBar");
                Assert.Equal(0, Grid.GetRow(conclusion));
                var detailCards = Find<Grid>(window, "ReadabilityDetailCardsGrid");
                Assert.Equal(2, Grid.GetRow(detailCards));
                Assert.Equal(3, detailCards.RowDefinitions.Count);
                AssertStar(detailCards.RowDefinitions[0].Height, 0.95);
                AssertPixel(detailCards.RowDefinitions[1].Height, 12);
                AssertStar(detailCards.RowDefinitions[2].Height, 1.05);
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    private static T Find<T>(FrameworkElement root, string name)
        where T : class => WatchErrorSearchProductionIntegrationTests.Find<T>(root, name);

    private static T Resource<T>(FrameworkElement root, object resourceKey)
        where T : class => Assert.IsAssignableFrom<T>(root.FindResource(resourceKey));

    private static void AssertPixel(GridLength length, double value)
    {
        Assert.Equal(GridUnitType.Pixel, length.GridUnitType);
        Assert.Equal(value, length.Value);
    }

    private static void AssertStar(GridLength length, double value)
    {
        Assert.Equal(GridUnitType.Star, length.GridUnitType);
        Assert.Equal(value, length.Value);
    }

    private static void AssertFullyWithin(
        FrameworkElement element,
        FrameworkElement viewport,
        string description)
    {
        Assert.Equal(Visibility.Visible, element.Visibility);
        Assert.True(element.IsVisible, $"{description} must be visible in the rendered window.");
        Assert.True(
            element.ActualWidth > 0 && element.ActualHeight > 0,
            $"{description} must render at a positive size; "
            + $"actual=({element.ActualWidth:0.##},{element.ActualHeight:0.##}).");
        var origin = element.TranslatePoint(new Point(0, 0), viewport);
        var tolerance = 0.5;
        Assert.True(
            origin.X >= -tolerance
                && origin.Y >= -tolerance
                && origin.X + element.ActualWidth <= viewport.ActualWidth + tolerance
                && origin.Y + element.ActualHeight <= viewport.ActualHeight + tolerance,
            $"{description} must remain fully inside the production viewport. "
            + $"element=({origin.X:0.##},{origin.Y:0.##},"
            + $"{element.ActualWidth:0.##},{element.ActualHeight:0.##}); "
            + $"viewport=({viewport.ActualWidth:0.##},{viewport.ActualHeight:0.##}).");
    }

    private static void AssertWideScrollablePageReaches(
        Window window,
        ScrollViewer viewport,
        FrameworkElement lowerEvidence,
        string description)
    {
        Assert.Equal(ScrollBarVisibility.Auto, viewport.VerticalScrollBarVisibility);
        Assert.True(
            viewport.ScrollableHeight > 0,
            $"{description} requires an outer scrolling fallback at 1440x600; "
            + $"extent={viewport.ExtentHeight:0.##}, viewport={viewport.ViewportHeight:0.##}.");

        viewport.ScrollToEnd();
        window.UpdateLayout();

        AssertFullyWithin(lowerEvidence, viewport, description);
    }
}
