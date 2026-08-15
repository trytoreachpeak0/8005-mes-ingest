using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchErrorFilterLabelLayoutTests
{
    [Fact]
    public async Task At_1440x900_the_series_id_filter_label_is_fully_visible()
    {
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(async () =>
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
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ErrorSearch,
                    PageNumber: 1,
                    Cursor: null));
                window.Show();
                window.UpdateLayout();
                var navigation = Find<Wpf.Ui.Controls.NavigationView>(
                    window,
                    "WorkspaceNavigation");
                Assert.False(navigation.IsPaneOpen);
                Assert.True(VisualStateManager.GoToState(
                    navigation,
                    "PaneCompact",
                    useTransitions: false));
                await window.Dispatcher.InvokeAsync(
                    window.UpdateLayout,
                    DispatcherPriority.ApplicationIdle);

                Assert.InRange(window.ActualWidth, 1439.5, 1440.5);
                Assert.InRange(window.ActualHeight, 899.5, 900.5);

                var seriesIdInput = Find<TextBox>(window, "ErrorSearchSeriesIdFilter");
                var field = Assert.IsType<StackPanel>(seriesIdInput.Parent);
                var filterGrid = Assert.IsType<Grid>(field.Parent);
                var label = Assert.IsAssignableFrom<TextBlock>(field.Children[0]);
                var applyButton = Find<FrameworkElement>(window, "ErrorSearchApplyFilterButton");
                var inlineFilterMinimumWidth =
                    (double)window.FindResource("ErrorSearchInlineFilterMinimumWidth");
                Assert.Equal("SeriesId（精确）", label.Text);
                Assert.Equal(TextTrimming.None, label.TextTrimming);

                var dpi = VisualTreeHelper.GetDpi(label);
                var naturalText = new FormattedText(
                    label.Text,
                    label.Language.GetSpecificCulture(),
                    label.FlowDirection,
                    new Typeface(
                        label.FontFamily,
                        label.FontStyle,
                        label.FontWeight,
                        label.FontStretch),
                    label.FontSize,
                    label.Foreground,
                    numberSubstitution: null,
                    TextOptions.GetTextFormattingMode(label),
                    dpi.PixelsPerDip);
                var unobscuredLabelWidth = applyButton.TranslatePoint(
                    new Point(),
                    label).X;

                Assert.True(
                    field.ActualWidth >= 95.5,
                    $"The SeriesId exact-filter star column must expose about 96 epx; "
                    + $"the realized field exposes only {field.ActualWidth:F2} epx "
                    + $"(filter={filterGrid.ActualWidth:F2}, "
                    + $"results={Find<FrameworkElement>(window, "ErrorSearchResultsCard").ActualWidth:F2}, "
                    + $"body={Find<Grid>(window, "ErrorSearchBodyGrid").ActualWidth:F2}, "
                    + $"page={Find<Grid>(window, "ErrorSearchPage").ActualWidth:F2}, "
                    + $"nav={Find<FrameworkElement>(window, "WorkspaceNavigation").ActualWidth:F2}).");
                Assert.True(
                    naturalText.WidthIncludingTrailingWhitespace <= unobscuredLabelWidth + 0.5,
                    $"{label.Text} requires "
                    + $"{naturalText.WidthIncludingTrailingWhitespace:F2} epx, but the next control "
                    + $"starts after only {unobscuredLabelWidth:F2} epx "
                    + $"(field={field.ActualWidth:F2}, label={label.ActualWidth:F2}, "
                    + $"dpi={dpi.PixelsPerInchX:F0}).");

                Assert.True(
                    filterGrid.ActualWidth >= inlineFilterMinimumWidth,
                    $"The settled 48 epx compact rail must preserve the selected inline filter; "
                    + $"filter={filterGrid.ActualWidth:F2}, page={Find<Grid>(window, "ErrorSearchPage").ActualWidth:F2}.");
                Assert.Equal(0, Grid.GetRow(field));
                Assert.Equal(1, Grid.GetColumnSpan(field));
                Assert.Equal(Grid.GetRow(field), Grid.GetRow(applyButton));

                var errorBody = Find<Grid>(window, "ErrorSearchBodyGrid");
                Assert.Equal(5, errorBody.ColumnDefinitions.Count);
                Assert.Equal(new GridLength(244), errorBody.ColumnDefinitions[0].Width);
                Assert.Equal(new GridLength(12), errorBody.ColumnDefinitions[1].Width);
                Assert.Equal(new GridLength(1, GridUnitType.Star), errorBody.ColumnDefinitions[2].Width);
                Assert.Equal(new GridLength(12), errorBody.ColumnDefinitions[3].Width);
                Assert.Equal(new GridLength(370), errorBody.ColumnDefinitions[4].Width);

                window.Width = 1390;
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(
                    window.UpdateLayout,
                    DispatcherPriority.ApplicationIdle);

                Assert.True(
                    filterGrid.ActualWidth < inlineFilterMinimumWidth,
                    $"The constrained middle card must exercise the two-row filter; "
                    + $"filter={filterGrid.ActualWidth:F2}.");
                Assert.Equal(2, Grid.GetRow(field));
                Assert.Equal(7, Grid.GetColumnSpan(field));
                Assert.Equal(Grid.GetRow(field), Grid.GetRow(applyButton));
                Assert.True(
                    field.ActualWidth >= 95.5,
                    $"The reflowed exact SeriesId field exposes only {field.ActualWidth:F2} epx.");
                Assert.True(
                    naturalText.WidthIncludingTrailingWhitespace
                        <= applyButton.TranslatePoint(new Point(), label).X + 0.5,
                    $"The reflowed {label.Text} label is still obscured by the apply command.");
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    private static T Find<T>(FrameworkElement root, string name)
        where T : class => WatchErrorSearchProductionIntegrationTests.Find<T>(root, name);
}
