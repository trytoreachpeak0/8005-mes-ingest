using System.Windows;
using System.Windows.Controls;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchDemandAuditSelectedPrototypeIntegrationTests
{
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
                    Visibility.Visible,
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
}
