using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchTicket22ResponsiveIntegrationTests
{
    [Fact]
    public async Task Error_search_and_current_attention_expose_keyboard_non_color_semantics_and_error_search_reflows_three_aligned_cards_at_720_epx()
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
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.ErrorSearch,
                    PageNumber: 1,
                    Cursor: null));
                window.Show();
                window.Width = 1440;
                window.Height = 900;
                window.UpdateLayout();

                var errorPage = Find<Grid>(window, "ErrorSearchPage");
                var errorBodyScroll = Find<ScrollViewer>(window, "ErrorSearchBodyScrollViewer");
                Assert.Equal(Visibility.Visible, errorPage.Visibility);
                Assert.Equal(ScrollBarVisibility.Disabled, errorBodyScroll.HorizontalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Auto, errorBodyScroll.VerticalScrollBarVisibility);
                AssertAutomation(errorPage, "ErrorSearchPage", "错误检索页面");
                AssertAutomation(
                    errorBodyScroll,
                    "ErrorSearchBodyScrollViewer",
                    "错误检索三列工作区滚动区域");

                var categoryCard = Find<Wpf.Ui.Controls.Card>(window, "ErrorSearchCategoryCard");
                var resultsCard = Find<Wpf.Ui.Controls.Card>(window, "ErrorSearchResultsCard");
                var detailCard = Find<Wpf.Ui.Controls.Card>(window, "ErrorSearchDetailCard");
                AssertAutomation(categoryCard, "ErrorSearchCategoryCard", "错误分类与精确分面");
                AssertAutomation(resultsCard, "ErrorSearchResultsCard", "错误检索 Series 结果");
                AssertAutomation(detailCard, "ErrorSearchDetailCard", "错误检索命中证据详情");
                AssertCardsShareTopAndBottom(errorPage, categoryCard, resultsCard, detailCard);

                var errorBody = Find<Grid>(window, "ErrorSearchBodyGrid");
                var seriesGrid = Find<DataGrid>(window, "ErrorSearchSeriesGrid");
                seriesGrid.ItemsSource = Enumerable.Range(1, 200)
                    .Select(index => new { SeriesId = $"SERIES-{index:000}" })
                    .ToArray();
                window.UpdateLayout();
                Assert.Equal(200, seriesGrid.Items.Count);
                Assert.True(
                    Math.Abs(errorBody.ActualHeight - errorBodyScroll.ActualHeight) <= 0.5,
                    $"body={errorBody.ActualHeight}, height={errorBody.Height}, scroll={errorBodyScroll.ActualHeight}");
                Assert.InRange(resultsCard.ActualHeight, 1, errorBodyScroll.ActualHeight + 0.5);
                var seriesScroll = FindVisualDescendant<ScrollViewer>(seriesGrid);
                Assert.NotNull(seriesScroll);
                Assert.True(seriesScroll.ScrollableHeight > 0);

                var errorInputs = new (string Name, Type Type, string AutomationName)[]
                {
                    ("ErrorSearchCategoryFilter", typeof(ComboBox), "错误分类筛选"),
                    ("ErrorSearchCodeFilter", typeof(ComboBox), "错误码筛选"),
                    ("ErrorSearchActivityStateFilter", typeof(ComboBox), "错误活动状态筛选"),
                    ("ErrorSearchWindowFilter", typeof(ComboBox), "错误检索时间范围"),
                    ("ErrorSearchSeriesIdFilter", typeof(TextBox), "错误检索 SeriesId 精确筛选"),
                    ("ErrorSearchDemandIdFilter", typeof(TextBox), "错误检索 DemandId 精确筛选"),
                    ("ErrorSearchSublotFilter", typeof(TextBox), "错误检索 SUBLOT 包含筛选"),
                    ("ErrorSearchPageSizeInput", typeof(ComboBox), "错误检索每页数量"),
                    ("ErrorSearchPageNumberInput", typeof(TextBox), "错误检索目标页码"),
                };
                foreach (var (name, type, automationName) in errorInputs)
                {
                    var control = Assert.IsAssignableFrom<Control>(window.FindName(name));
                    Assert.True(type.IsInstanceOfType(control), $"{name} must be a {type.Name}");
                    AssertInteractiveAutomation(control, name, automationName);
                }

                var errorCommands = new Dictionary<string, string>
                {
                    ["ErrorSearchApplyFilterButton"] = "应用错误检索条件",
                    ["ErrorSearchClearFilterButton"] = "清除错误检索条件",
                    ["ErrorSearchPreviousPageButton"] = "错误检索上一页",
                    ["ErrorSearchNextPageButton"] = "错误检索下一页",
                    ["ErrorSearchGoToPageButton"] = "错误检索直接页码跳转",
                    ["ErrorSearchLoadRawEvidenceButton"] = "按需加载受限原始证据",
                };
                foreach (var (name, automationName) in errorCommands)
                {
                    var command = Find<ButtonBase>(window, name);
                    AssertInteractiveAutomation(command, name, automationName);
                }

                var errorGrids = new Dictionary<string, string>
                {
                    ["ErrorSearchCategoryFacetGrid"] = "错误分类 Host 精确分面",
                    ["ErrorSearchActivityStateFacetGrid"] = "错误活动状态 Host 精确分面",
                    ["ErrorSearchSeriesGrid"] = "错误检索去重 Series 结果",
                    ["ErrorSearchPeriodGrid"] = "错误检索真正命中期间",
                    ["ErrorSearchEvidenceGrid"] = "错误检索可解释证据",
                    ["ErrorSearchRawEvidenceGrid"] = "错误检索受限原始证据",
                };
                foreach (var (name, automationName) in errorGrids)
                {
                    var grid = Find<DataGrid>(window, name);
                    AssertInteractiveAutomation(grid, name, automationName);
                    Assert.True(grid.IsReadOnly);
                    Assert.False(grid.AutoGenerateColumns);
                }
                Assert.Contains(
                    "字段 / 主体",
                    Find<DataGrid>(window, "ErrorSearchEvidenceGrid")
                        .Columns.Select(column => column.Header?.ToString()));
                Assert.Contains(
                    "主体种类",
                    Find<DataGrid>(window, "ErrorSearchPeriodGrid")
                        .Columns.Select(column => column.Header?.ToString()));
                Assert.Contains(
                    "结束原因",
                    Find<DataGrid>(window, "ErrorSearchPeriodGrid")
                        .Columns.Select(column => column.Header?.ToString()));

                var stateOptions = ItemText(Find<ComboBox>(window, "ErrorSearchActivityStateFilter"));
                Assert.Contains(ErrorSearchActivityStates.Active, stateOptions, StringComparison.Ordinal);
                Assert.Contains(ErrorSearchActivityStates.Ended, stateOptions, StringComparison.Ordinal);
                Assert.True(string.IsNullOrWhiteSpace(
                    Find<ComboBox>(window, "ErrorSearchCategoryFilter").Text));
                Assert.True(string.IsNullOrWhiteSpace(
                    Find<ComboBox>(window, "ErrorSearchCodeFilter").Text));
                var categoryOptions = ItemText(Find<ComboBox>(window, "ErrorSearchCategoryFilter"));
                Assert.All(
                    SeriesErrorCatalog.Definitions
                        .Select(definition => definition.Category)
                        .Distinct(StringComparer.Ordinal),
                    category => Assert.Contains(category, categoryOptions, StringComparison.Ordinal));
                var codeOptions = ItemText(Find<ComboBox>(window, "ErrorSearchCodeFilter"));
                Assert.All(
                    SeriesErrorCatalog.Definitions,
                    definition => Assert.Contains(
                        definition.Code,
                        codeOptions,
                        StringComparison.Ordinal));
                AssertNonColorText(window, "ErrorSearchSnapshotText");
                AssertNonColorText(window, "ErrorSearchWindowText");
                AssertNonColorText(window, "ErrorSearchNormalizedFilterText");
                AssertNonColorText(window, "ErrorSearchPageSummaryText");

                window.Width = 720;
                window.Height = 900;
                window.UpdateLayout();
                AssertStacked(categoryCard, expectedRow: 0, errorPage);
                AssertStacked(resultsCard, expectedRow: 2, errorPage);
                AssertStacked(detailCard, expectedRow: 4, errorPage);
                Assert.True(errorBodyScroll.ScrollableHeight > 0);
                foreach (var commandName in errorCommands.Keys)
                {
                    AssertHorizontallyDiscoverable(Find<FrameworkElement>(window, commandName), errorPage);
                }

                WatchErrorSearchProductionIntegrationTests.Click(
                    Find<Wpf.Ui.Controls.NavigationViewItem>(window, "CurrentAttentionNavigationItem"));
                window.UpdateLayout();
                var attentionPage = Find<ScrollViewer>(window, "CurrentAttentionPage");
                Assert.Equal(Visibility.Visible, attentionPage.Visibility);
                Assert.Equal(ScrollBarVisibility.Disabled, attentionPage.HorizontalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Auto, attentionPage.VerticalScrollBarVisibility);
                AssertAutomation(attentionPage, "CurrentAttentionPage", "接入告警页面");

                var attentionCards = new Dictionary<string, string>
                {
                    ["CurrentAttentionFacetCard"] = "接入告警精确分面",
                    ["CurrentAttentionResultsCard"] = "当前接入告警结果",
                    ["CurrentAttentionEvidenceCard"] = "接入告警结构化证据",
                };
                foreach (var (name, automationName) in attentionCards)
                {
                    AssertAutomation(Find<Wpf.Ui.Controls.Card>(window, name), name, automationName);
                }

                var attentionInputs = new (string Name, Type Type, string AutomationName)[]
                {
                    ("CurrentAttentionKindFilter", typeof(ComboBox), "接入告警类型筛选"),
                    ("CurrentAttentionSeverityFilter", typeof(ComboBox), "接入告警严重度筛选"),
                    ("CurrentAttentionPageSizeInput", typeof(ComboBox), "接入告警每页数量"),
                    ("CurrentAttentionPageNumberInput", typeof(TextBox), "接入告警目标页码"),
                };
                foreach (var (name, type, automationName) in attentionInputs)
                {
                    var control = Assert.IsAssignableFrom<Control>(window.FindName(name));
                    Assert.True(type.IsInstanceOfType(control), $"{name} must be a {type.Name}");
                    AssertInteractiveAutomation(control, name, automationName);
                }

                var attentionCommands = new Dictionary<string, string>
                {
                    ["CurrentAttentionApplyFilterButton"] = "应用接入告警条件",
                    ["CurrentAttentionClearFilterButton"] = "清除接入告警条件",
                    ["CurrentAttentionPreviousPageButton"] = "接入告警上一页",
                    ["CurrentAttentionNextPageButton"] = "接入告警下一页",
                    ["CurrentAttentionGoToPageButton"] = "接入告警直接页码跳转",
                    ["CurrentAttentionOpenErrorSearchButton"] = "从当前 Series 错误下钻错误检索",
                };
                foreach (var (name, automationName) in attentionCommands)
                {
                    var command = Find<ButtonBase>(window, name);
                    AssertInteractiveAutomation(command, name, automationName);
                    AssertHorizontallyDiscoverable(command, attentionPage);
                }

                var attentionGrids = new Dictionary<string, string>
                {
                    ["CurrentAttentionKindFacetGrid"] = "接入告警类型 Host 精确分面",
                    ["CurrentAttentionSeverityFacetGrid"] = "接入告警严重度 Host 精确分面",
                    ["CurrentAttentionGrid"] = "当前仍需关注的接入告警",
                    ["CurrentAttentionEvidenceGrid"] = "接入告警结构化证据字段",
                };
                foreach (var (name, automationName) in attentionGrids)
                {
                    var grid = Find<DataGrid>(window, name);
                    AssertInteractiveAutomation(grid, name, automationName);
                    Assert.True(grid.IsReadOnly);
                    Assert.False(grid.AutoGenerateColumns);
                }

                var kindOptions = ItemText(Find<ComboBox>(window, "CurrentAttentionKindFilter"));
                Assert.Contains("Series 错误", kindOptions, StringComparison.Ordinal);
                Assert.Contains("轮询失败", kindOptions, StringComparison.Ordinal);
                Assert.Contains("任务类型保护", kindOptions, StringComparison.Ordinal);
                Assert.Contains("未分配观测", kindOptions, StringComparison.Ordinal);
                Assert.True(string.IsNullOrWhiteSpace(
                    Find<ComboBox>(window, "CurrentAttentionKindFilter").Text));
                Assert.True(string.IsNullOrWhiteSpace(
                    Find<ComboBox>(window, "CurrentAttentionSeverityFilter").Text));
                AssertNonColorText(window, "CurrentAttentionSnapshotText");
                AssertNonColorText(window, "CurrentAttentionScopeText");
                AssertNonColorText(window, "CurrentAttentionPageSummaryText");
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    private static void AssertCardsShareTopAndBottom(
        FrameworkElement page,
        params FrameworkElement[] cards)
    {
        var rectangles = cards.Select(card =>
        {
            var origin = card.TranslatePoint(new Point(0, 0), page);
            return (Top: origin.Y, Bottom: origin.Y + card.ActualHeight);
        }).ToArray();
        Assert.All(rectangles, rectangle =>
            Assert.InRange(Math.Abs(rectangle.Top - rectangles[0].Top), 0, 0.5));
        Assert.All(rectangles, rectangle =>
            Assert.InRange(Math.Abs(rectangle.Bottom - rectangles[0].Bottom), 0, 0.5));
    }

    private static void AssertStacked(
        FrameworkElement card,
        int expectedRow,
        FrameworkElement page)
    {
        Assert.Equal(0, Grid.GetColumn(card));
        Assert.Equal(expectedRow, Grid.GetRow(card));
        Assert.True(card.ActualWidth > 0);
        Assert.InRange(card.ActualWidth, 1, page.ActualWidth);
        AssertHorizontallyDiscoverable(card, page);
    }

    private static void AssertAutomation(
        FrameworkElement element,
        string automationId,
        string automationName)
    {
        Assert.Equal(automationId, AutomationProperties.GetAutomationId(element));
        Assert.Equal(automationName, AutomationProperties.GetName(element));
    }

    private static void AssertInteractiveAutomation(
        Control control,
        string automationId,
        string automationName)
    {
        AssertAutomation(control, automationId, automationName);
        Assert.True(control.Focusable, $"{automationId} must accept keyboard focus");
        Assert.True(
            KeyboardNavigation.GetIsTabStop(control),
            $"{automationId} must participate in tab navigation");
    }

    private static void AssertNonColorText(FrameworkElement root, string name)
    {
        var text = Find<TextBlock>(root, name);
        Assert.False(string.IsNullOrWhiteSpace(text.Text));
        Assert.Contains(
            text.Text,
            AutomationProperties.GetName(text),
            StringComparison.Ordinal);
    }

    private static void AssertHorizontallyDiscoverable(
        FrameworkElement element,
        FrameworkElement page)
    {
        Assert.True(element.IsVisible, $"{element.Name} must remain visible at 720 epx");
        Assert.True(element.ActualWidth > 0, $"{element.Name} must be measured at 720 epx");
        var origin = element.TranslatePoint(new Point(0, 0), page);
        Assert.True(origin.X >= -0.5, $"{element.Name} starts outside the page at {origin.X}");
        Assert.True(
            origin.X + element.ActualWidth <= page.ActualWidth + 0.5,
            $"{element.Name} ends outside the page at {origin.X + element.ActualWidth}");
    }

    private static string ItemText(ItemsControl control) => string.Join(
        " | ",
        control.Items.Cast<object>().Select(item => item switch
        {
            ComboBoxItem comboBoxItem => comboBoxItem.Content?.ToString(),
            _ => item.ToString(),
        }));

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static T Find<T>(FrameworkElement root, string name)
        where T : class => WatchErrorSearchProductionIntegrationTests.Find<T>(root, name);
}
