using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MesIngest.Watch;
using WpfTextBlock = Wpf.Ui.Controls.TextBlock;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchOverviewSelectedVariantTests
{
    [Fact]
    public async Task Overview_summary_cards_keep_selected_equal_width_gaps_and_responsive_rows()
    {
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 1440;
                window.Height = 900;
                window.Show();
                window.UpdateLayout();

                var viewport = Find<Grid>(window, "OverviewSummaryViewport");
                var panel = Find<UniformGrid>(window, "OverviewSummaryCards");
                var cards = SummaryCards(window);
                Assert.Equal(5, panel.Columns);
                Assert.Equal(new Thickness(-5, -6, -5, -6), panel.Margin);
                Assert.All(cards, card => Assert.Equal(new Thickness(5, 6, 5, 6), card.Margin));
                AssertCardsShareWidth(cards);
                AssertHorizontalRun(viewport, cards, 0, 5, expectedGap: 10);

                window.Width = 1000;
                window.UpdateLayout();

                Assert.Equal(3, panel.Columns);
                cards = SummaryCards(window);
                AssertCardsShareWidth(cards);
                AssertHorizontalRun(viewport, cards, 0, 3, expectedGap: 10);
                AssertVerticalGap(cards[0], cards[3], expectedGap: 12);

                window.Width = 720;
                window.UpdateLayout();

                Assert.Equal(2, panel.Columns);
                cards = SummaryCards(window);
                AssertCardsShareWidth(cards);
                AssertHorizontalRun(viewport, cards, 0, 2, expectedGap: 10);
                AssertVerticalGap(cards[0], cards[2], expectedGap: 12);
                AssertVerticalGap(cards[2], cards[4], expectedGap: 12);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Overview_uses_live_refresh_preference_caption_metadata_and_one_series_navigation()
    {
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();
        WatchV2PreferencesStore.Save(
            files.WorkspacePath,
            new WatchV2Preferences(
                WatchV2AutoRefreshSettings.Default with
                {
                    Overview = new WatchV2AutoRefreshSetting(30),
                },
                WatchV2DisplayPreferences.Default,
                WatchDisplayLanguage.SimplifiedChinese));

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                CreateOptions(),
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Show();
                window.UpdateLayout();

                var context = Find<WpfTextBlock>(window, "OverviewContextText");
                Assert.Contains("自动刷新 30 秒", context.Text, StringComparison.Ordinal);
                Assert.Contains(context.Text, AutomationProperties.GetName(context), StringComparison.Ordinal);
                window.ApplyLocalPreferences(
                    WatchV2AutoRefreshSettings.Default with
                    {
                        Overview = new WatchV2AutoRefreshSetting(60),
                    },
                    WatchV2DisplayPreferences.Default);
                Assert.Contains("自动刷新 60 秒", context.Text, StringComparison.Ordinal);
                Assert.Contains(context.Text, AutomationProperties.GetName(context), StringComparison.Ordinal);

                var captionStyle = Assert.IsType<Style>(window.FindResource("CaptionText"));
                Assert.Equal(typeof(WpfTextBlock), captionStyle.TargetType);
                Assert.All(
                    new[]
                    {
                        "SeriesSummaryDetail",
                        "ReadabilitySummaryDetail",
                        "ErrorsSummaryDetail",
                        "LocalAreaDetailText",
                        "HostAreaScopeText",
                        "AttentionSummaryDetail",
                    },
                    name =>
                    {
                        var text = Find<WpfTextBlock>(window, name);
                        Assert.Same(captionStyle, text.Style);
                        Assert.Equal(12, text.FontSize);
                        Assert.Same(
                            window.FindResource("TextFillColorTertiaryBrush"),
                            text.Foreground);
                        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                    });

                var seriesNavigation = Find<Wpf.Ui.Controls.Button>(
                    window,
                    "SeriesSummaryAction");
                Assert.Equal("查看全部需求系列第一页", AutomationProperties.GetName(seriesNavigation));
                Assert.Null(window.FindName("SeriesTrackingAction"));
                Assert.Null(window.FindName("SeriesArchivedAction"));
                Assert.Null(window.FindName("SeriesGoneAction"));
                Assert.Null(window.FindName("SeriesLongGoneVisibleAction"));

                Assert.Null(window.FindName("SnapshotFactsText"));
                Assert.Null(window.FindName("ClientAttemptFactsText"));
                Assert.Null(window.FindName("AttentionSummaryActions"));
                var attentionFacetSummary = Find<WpfTextBlock>(
                    window,
                    "AttentionSummaryFacetText");
                Assert.Same(captionStyle, attentionFacetSummary.Style);
                Assert.Equal(TextWrapping.NoWrap, attentionFacetSummary.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, attentionFacetSummary.TextTrimming);
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    private static WatchOptions CreateOptions() => new()
    {
        BaseUrl = "http://127.0.0.1:5088",
        RenderingMode = WatchRenderingMode.SoftwareOnly,
    };

    private static Border[] SummaryCards(FrameworkElement window) =>
    [
        Find<Border>(window, "SeriesSummaryCard"),
        Find<Border>(window, "ReadabilitySummaryCard"),
        Find<Border>(window, "ErrorsSummaryCard"),
        Find<Border>(window, "AreaSummaryCard"),
        Find<Border>(window, "AttentionSummaryCard"),
    ];

    private static void AssertCardsShareWidth(IReadOnlyList<Border> cards)
    {
        var expected = cards[0].ActualWidth;
        Assert.True(expected > 0);
        Assert.All(cards, card => AssertClose(expected, card.ActualWidth));
    }

    private static void AssertHorizontalRun(
        FrameworkElement viewport,
        IReadOnlyList<Border> cards,
        int start,
        int count,
        double expectedGap)
    {
        var first = cards[start];
        AssertClose(0, first.TranslatePoint(new Point(), viewport).X);
        for (var index = start + 1; index < start + count; index++)
        {
            var previous = cards[index - 1];
            var previousLeft = previous.TranslatePoint(new Point(), viewport).X;
            var currentLeft = cards[index].TranslatePoint(new Point(), viewport).X;
            AssertClose(expectedGap, currentLeft - previousLeft - previous.ActualWidth);
        }

        var last = cards[start + count - 1];
        var lastRight = last.TranslatePoint(new Point(), viewport).X + last.ActualWidth;
        AssertClose(viewport.ActualWidth, lastRight);
    }

    private static void AssertVerticalGap(
        FrameworkElement upper,
        FrameworkElement lower,
        double expectedGap)
    {
        var lowerTop = lower.TranslatePoint(new Point(), upper).Y;
        AssertClose(expectedGap, lowerTop - upper.ActualHeight);
    }

    private static void AssertClose(double expected, double actual) =>
        // The local diagnostic desktop may be at 150% DPI. Allow two physical
        // pixels of layout rounding while keeping the effective-pixel contract.
        Assert.InRange(Math.Abs(actual - expected), 0, 1.5);

    private static T Find<T>(FrameworkElement root, string name)
        where T : class => WatchErrorSearchProductionIntegrationTests.Find<T>(root, name);
}
