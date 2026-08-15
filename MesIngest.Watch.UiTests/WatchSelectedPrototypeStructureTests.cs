using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchSelectedPrototypeStructureTests
{
    [Fact]
    public async Task Production_shell_and_global_tokens_match_the_selected_compact_fluent_contract()
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
                var navigation = Find<Wpf.Ui.Controls.NavigationView>(
                    window,
                    "WorkspaceNavigation");
                Assert.Equal("LeftMinimal", navigation.PaneDisplayMode.ToString());
                Assert.Equal(48, navigation.CompactPaneLength);
                Assert.Equal(232, navigation.OpenPaneLength);
                Assert.False(navigation.IsPaneOpen);
                Assert.False(WatchV2DisplayPreferences.Default.IsNavigationPaneOpen);

                window.Show();
                window.UpdateLayout();
                var navigationToggle = Assert.IsAssignableFrom<FrameworkElement>(
                    FindVisualDescendant<FrameworkElement>(
                        navigation,
                        element => string.Equals(
                            AutomationProperties.GetAutomationId(element),
                            "NavigationToggleButton",
                            StringComparison.Ordinal)));
                Assert.Equal(
                    "展开或折叠主导航",
                    AutomationProperties.GetName(navigationToggle));
                Assert.Equal(
                    "在 48 epx 紧凑导航与 232 epx 展开导航之间切换",
                    AutomationProperties.GetHelpText(navigationToggle));
                Assert.Equal(
                    "展开或折叠主导航",
                    ToolTipService.GetToolTip(navigationToggle));

                var productionPageTitle = Assert.IsType<Style>(
                    window.FindResource("PageTitleText"));
                Assert.Equal(typeof(Wpf.Ui.Controls.TextBlock), productionPageTitle.TargetType);
                Assert.Equal(
                    Wpf.Ui.Controls.FontTypography.Title,
                    SetterValue<Wpf.Ui.Controls.FontTypography>(
                        productionPageTitle,
                        Wpf.Ui.Controls.TextBlock.FontTypographyProperty));
                Assert.DoesNotContain(
                    productionPageTitle.Setters.OfType<Setter>(),
                    setter => setter.Property == TextBlock.FontSizeProperty);
                var pageTitleSample = Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                    FindVisualDescendant<Wpf.Ui.Controls.TextBlock>(
                        Find<Grid>(window, "OverviewLayoutGrid"),
                        text => ReferenceEquals(text.Style, productionPageTitle)));
                Assert.Equal(28, pageTitleSample.FontSize);
                Assert.Equal(FontWeights.SemiBold, pageTitleSample.FontWeight);

                var bodyStrong = Assert.IsType<Style>(window.FindResource("BodyStrong"));
                Assert.Equal(typeof(Wpf.Ui.Controls.TextBlock), bodyStrong.TargetType);
                Assert.Equal(
                    Wpf.Ui.Controls.FontTypography.BodyStrong,
                    SetterValue<Wpf.Ui.Controls.FontTypography>(
                        bodyStrong,
                        Wpf.Ui.Controls.TextBlock.FontTypographyProperty));
                var sectionTitle = Assert.IsType<Style>(window.FindResource("SectionTitleText"));
                Assert.Equal(
                    Wpf.Ui.Controls.FontTypography.BodyStrong,
                    SetterValue<Wpf.Ui.Controls.FontTypography>(
                        sectionTitle,
                        Wpf.Ui.Controls.TextBlock.FontTypographyProperty));
                Assert.Equal(18, SetterValue<double>(sectionTitle, TextBlock.FontSizeProperty));
                var secondary = Assert.IsType<Style>(window.FindResource("SecondaryText"));
                Assert.Equal(
                    Wpf.Ui.Controls.FontTypography.Body,
                    SetterValue<Wpf.Ui.Controls.FontTypography>(
                        secondary,
                        Wpf.Ui.Controls.TextBlock.FontTypographyProperty));
                Assert.Equal(13, SetterValue<double>(secondary, TextBlock.FontSizeProperty));
                var caption = Assert.IsType<Style>(window.FindResource("CaptionText"));
                Assert.Equal(
                    Wpf.Ui.Controls.FontTypography.Caption,
                    SetterValue<Wpf.Ui.Controls.FontTypography>(
                        caption,
                        Wpf.Ui.Controls.TextBlock.FontTypographyProperty));

                Assert.Null(window.TryFindResource("PageTitle"));
                Assert.Null(window.TryFindResource("Subtitle"));
                Assert.Null(window.TryFindResource("BodySecondary"));
                Assert.Null(window.TryFindResource("Caption"));

                var dataGridToken = Assert.IsType<Style>(window.FindResource("WatchDataGrid"));
                Assert.Equal(typeof(DataGrid), dataGridToken.TargetType);
                Assert.Equal(42, SetterValue<double>(dataGridToken, DataGrid.RowHeightProperty));
                Assert.Equal(
                    38,
                    SetterValue<double>(dataGridToken, DataGrid.ColumnHeaderHeightProperty));
                Assert.Equal(
                    DataGridGridLinesVisibility.Horizontal,
                    SetterValue<DataGridGridLinesVisibility>(
                        dataGridToken,
                        DataGrid.GridLinesVisibilityProperty));
                Assert.Equal(
                    DataGridHeadersVisibility.Column,
                    SetterValue<DataGridHeadersVisibility>(
                        dataGridToken,
                        DataGrid.HeadersVisibilityProperty));
                var dataGridCell = Assert.IsType<Style>(
                    window.FindResource(typeof(DataGridCell)));
                Assert.Equal(
                    new Thickness(8, 0, 8, 0),
                    SetterValue<Thickness>(dataGridCell, Control.PaddingProperty));

                var readOnlyGrid = Assert.IsType<Style>(
                    window.FindResource("DemandSeriesReadOnlyGrid"));
                Assert.Same(dataGridToken, readOnlyGrid.BasedOn);
                var demandSeriesGrid = Find<DataGrid>(window, "DemandSeriesGrid");
                Assert.Same(readOnlyGrid, demandSeriesGrid.Style);
                Assert.Equal(42, demandSeriesGrid.RowHeight);
                Assert.Equal(38, demandSeriesGrid.ColumnHeaderHeight);
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Overview_settings_and_demand_skeletons_match_the_selected_page_geometry()
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
                var overview = Find<Grid>(window, "OverviewLayoutGrid");
                AssertSelectedPageRows(overview);
                AssertPixel(Find<RowDefinition>(window, "OverviewSummaryRow").Height, 174);
                Assert.Equal(5, Find<UniformGrid>(window, "OverviewSummaryCards").Columns);
                var overviewBody = Find<Grid>(window, "OverviewBodyGrid");
                Assert.Equal(3, overviewBody.ColumnDefinitions.Count);
                AssertStar(overviewBody.ColumnDefinitions[0].Width, 1.55);
                AssertPixel(overviewBody.ColumnDefinitions[1].Width, 12);
                AssertStar(overviewBody.ColumnDefinitions[2].Width, 1);
                var primaryMetric = Assert.IsType<Style>(
                    window.FindResource("OverviewPrimaryMetric"));
                Assert.Equal(
                    Wpf.Ui.Controls.FontTypography.Title,
                    SetterValue<Wpf.Ui.Controls.FontTypography>(
                        primaryMetric,
                        Wpf.Ui.Controls.TextBlock.FontTypographyProperty));
                Assert.Equal(30, SetterValue<double>(primaryMetric, TextBlock.FontSizeProperty));
                var ratioMetric = Assert.IsType<Style>(
                    window.FindResource("OverviewRatioMetric"));
                Assert.Equal(
                    Wpf.Ui.Controls.FontTypography.Title,
                    SetterValue<Wpf.Ui.Controls.FontTypography>(
                        ratioMetric,
                        Wpf.Ui.Controls.TextBlock.FontTypographyProperty));
                Assert.Equal(25, SetterValue<double>(ratioMetric, TextBlock.FontSizeProperty));
                Assert.Equal(30, Find<Wpf.Ui.Controls.TextBlock>(
                    window,
                    "SeriesSummaryValue").FontSize);
                Assert.Equal(25, Find<Wpf.Ui.Controls.TextBlock>(
                    window,
                    "ReadabilitySummaryValue").FontSize);
                Assert.Equal(30, Find<Wpf.Ui.Controls.TextBlock>(
                    window,
                    "ErrorsSummaryValue").FontSize);
                Assert.Equal(25, Find<Wpf.Ui.Controls.TextBlock>(
                    window,
                    "LocalAreaHeadingText").FontSize);
                Assert.Equal(30, Find<Wpf.Ui.Controls.TextBlock>(
                    window,
                    "AttentionSummaryValue").FontSize);
                var inlineIntent = Find<Wpf.Ui.Controls.Button>(
                    window,
                    "SeriesSummaryAction");
                Assert.Equal(new Thickness(0), inlineIntent.BorderThickness);
                Assert.Equal(new Thickness(4, 0, 4, 0), inlineIntent.Padding);
                Assert.Equal(32, inlineIntent.MinWidth);
                Assert.Equal(32, inlineIntent.MinHeight);
                Assert.Equal(12, inlineIntent.FontSize);

                var settingsPage = Find<ScrollViewer>(window, "SettingsPage");
                var settingsRoot = Assert.IsType<Grid>(settingsPage.Content);
                Assert.Equal(new Thickness(0), settingsRoot.Margin);
                AssertSelectedPageRows(settingsRoot);
                var settingsColumns = Find<Grid>(window, "SettingsColumns");
                Assert.Equal(4, Grid.GetRow(settingsColumns));
                Assert.Equal(3, settingsColumns.ColumnDefinitions.Count);
                AssertStar(settingsColumns.ColumnDefinitions[0].Width, 1);
                AssertPixel(settingsColumns.ColumnDefinitions[1].Width, 16);
                AssertStar(settingsColumns.ColumnDefinitions[2].Width, 1);

                var demand = Find<Grid>(window, "DemandSeriesLayoutGrid");
                AssertSelectedPageRows(demand);
                var demandBody = Find<Grid>(window, "DemandSeriesMasterDetailGrid");
                Assert.Equal(4, Grid.GetRow(demandBody));
                Assert.Equal(3, demandBody.RowDefinitions.Count);
                AssertStar(demandBody.RowDefinitions[0].Height, 0.9);
                AssertPixel(demandBody.RowDefinitions[1].Height, 16);
                AssertStar(demandBody.RowDefinitions[2].Height, 1.1);

                var demandMaster = Find<Border>(window, "DemandSeriesMasterPanel");
                var demandDetail = Find<Border>(window, "DemandSeriesDetailPanel");
                Assert.Equal(0, Grid.GetRow(demandMaster));
                Assert.Equal(2, Grid.GetRow(demandDetail));
                var demandMasterGrid = Assert.IsType<Grid>(demandMaster.Child);
                Assert.Equal(3, demandMasterGrid.RowDefinitions.Count);
                AssertAuto(demandMasterGrid.RowDefinitions[0].Height);
                AssertStar(demandMasterGrid.RowDefinitions[1].Height, 1);
                AssertPixel(demandMasterGrid.RowDefinitions[2].Height, 40);

                var lifecycleEvidence = Find<Grid>(
                    window,
                    "DemandSeriesLifecycleEvidencePanel");
                Assert.Equal(Visibility.Visible, lifecycleEvidence.Visibility);
                Assert.Equal(3, lifecycleEvidence.ColumnDefinitions.Count);
                AssertStar(lifecycleEvidence.ColumnDefinitions[0].Width, 0.95);
                AssertPixel(lifecycleEvidence.ColumnDefinitions[1].Width, 1);
                AssertStar(lifecycleEvidence.ColumnDefinitions[2].Width, 1.35);
                Assert.Equal(
                    Visibility.Visible,
                    Find<DataGrid>(window, "DemandSeriesGenerationGrid").Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    Find<DataGrid>(window, "DemandSeriesEventGrid").Visibility);

                var fullEvidence = Find<TabControl>(
                    window,
                    "DemandSeriesFullEvidenceTabs");
                Assert.Equal(Visibility.Collapsed, fullEvidence.Visibility);
                Assert.Equal(2, fullEvidence.Items.Count);
                var evidenceToggle = Find<Wpf.Ui.Controls.Button>(
                    window,
                    "DemandSeriesFullEvidenceButton");
                Assert.Equal("完整证据", evidenceToggle.Content);
                Assert.Equal("Secondary", evidenceToggle.Appearance.ToString());

                evidenceToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.Equal(Visibility.Collapsed, lifecycleEvidence.Visibility);
                Assert.Equal(Visibility.Visible, fullEvidence.Visibility);
                Assert.Equal("返回生命周期", evidenceToggle.Content);

                evidenceToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.Equal(Visibility.Visible, lifecycleEvidence.Visibility);
                Assert.Equal(Visibility.Collapsed, fullEvidence.Visibility);
                Assert.Equal("完整证据", evidenceToggle.Content);
            }
            finally
            {
                window.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Readability_area_error_and_attention_match_the_selected_wide_workspace_geometry()
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

                Assert.InRange(window.ActualWidth, 1439.5, 1440.5);
                Assert.InRange(window.ActualHeight, 899.5, 900.5);

                var readabilityBody = Find<Grid>(window, "ReadabilityBodyGrid");
                Assert.Equal(3, readabilityBody.ColumnDefinitions.Count);
                AssertPixel(readabilityBody.ColumnDefinitions[0].Width, 430);
                AssertPixel(readabilityBody.ColumnDefinitions[1].Width, 16);
                AssertStar(readabilityBody.ColumnDefinitions[2].Width, 1);
                var readabilityDetail = Find<Grid>(window, "ReadabilityDetailRegion");
                Assert.Equal(3, readabilityDetail.RowDefinitions.Count);
                AssertAuto(readabilityDetail.RowDefinitions[0].Height);
                AssertPixel(readabilityDetail.RowDefinitions[1].Height, 12);
                AssertStar(readabilityDetail.RowDefinitions[2].Height, 1);
                var readabilityDetailCards = Find<Grid>(
                    window,
                    "ReadabilityDetailCardsGrid");
                Assert.Equal(3, readabilityDetailCards.RowDefinitions.Count);
                AssertStar(readabilityDetailCards.RowDefinitions[0].Height, 0.95);
                AssertPixel(readabilityDetailCards.RowDefinitions[1].Height, 12);
                AssertStar(readabilityDetailCards.RowDefinitions[2].Height, 1.05);

                var areaRoot = Find<Grid>(window, "AreaFilterLayoutGrid");
                AssertSelectedPageRows(areaRoot, secondGap: 12);
                var areaBody = Find<Grid>(window, "AreaProfileBodyGrid");
                Assert.Equal(3, areaBody.ColumnDefinitions.Count);
                AssertPixel(areaBody.ColumnDefinitions[0].Width, 318);
                AssertPixel(areaBody.ColumnDefinitions[1].Width, 16);
                AssertStar(areaBody.ColumnDefinitions[2].Width, 1);
                Assert.Equal(new Thickness(0), areaRoot.Margin);
                var areaEditorWorkspace = Find<Grid>(window, "AreaProfileEditorWorkspaceGrid");
                Assert.Equal(6, areaEditorWorkspace.RowDefinitions.Count);
                AssertAuto(areaEditorWorkspace.RowDefinitions[0].Height);
                AssertPixel(areaEditorWorkspace.RowDefinitions[1].Height, 10);
                AssertStar(areaEditorWorkspace.RowDefinitions[2].Height, 1);
                AssertPixel(areaEditorWorkspace.RowDefinitions[3].Height, 12);
                AssertAuto(areaEditorWorkspace.RowDefinitions[4].Height);
                AssertAuto(areaEditorWorkspace.RowDefinitions[5].Height);
                var areaEditorBorder = Find<Border>(window, "AreaProfileEditorFrame");
                Assert.Equal(2, Grid.GetRow(areaEditorBorder));
                var areaEditor = Assert.IsType<Grid>(areaEditorBorder.Child);
                Assert.Equal(3, areaEditor.ColumnDefinitions.Count);
                AssertPixel(areaEditor.ColumnDefinitions[0].Width, 44);
                AssertPixel(areaEditor.ColumnDefinitions[1].Width, 1);
                AssertStar(areaEditor.ColumnDefinitions[2].Width, 1);
                Assert.Equal(
                    4,
                    Grid.GetRow(Find<Grid>(window, "AreaProfileEditorStatusGrid")));
                Assert.Equal(
                    4,
                    Grid.GetRow(Find<Grid>(window, "AreaProfileAppliedCommandRow")));
                Assert.Equal(
                    "Secondary",
                    Find<Wpf.Ui.Controls.Button>(
                        window,
                        "AreaProfileApplyButton").Appearance.ToString());

                var errorRoot = Find<Grid>(window, "ErrorSearchPage");
                AssertSelectedPageRows(errorRoot);
                var errorBody = Find<Grid>(window, "ErrorSearchBodyGrid");
                Assert.Equal(5, errorBody.ColumnDefinitions.Count);
                AssertPixel(errorBody.ColumnDefinitions[0].Width, 244);
                AssertPixel(errorBody.ColumnDefinitions[1].Width, 12);
                AssertStar(errorBody.ColumnDefinitions[2].Width, 1);
                AssertPixel(errorBody.ColumnDefinitions[3].Width, 12);
                AssertPixel(errorBody.ColumnDefinitions[4].Width, 370);
                var errorSeriesGrid = Find<DataGrid>(window, "ErrorSearchSeriesGrid");
                var visibleTemplateHost = Find<Grid>(window, "OverviewLayoutGrid");
                var activityColumn = Assert.IsType<DataGridTemplateColumn>(
                    errorSeriesGrid.Columns[3]);
                var activityPill = Assert.IsType<Border>(
                    activityColumn.CellTemplate.LoadContent());
                Assert.Equal(new Thickness(2, 0, 2, 0), activityPill.Margin);
                AssertSemanticPill(activityPill, "ActivityState", "ACTIVE", "ENDED");
                AssertSemanticPillForeground(
                    window,
                    visibleTemplateHost,
                    activityColumn.CellTemplate,
                    new SemanticPillContext(ActivityState: "ACTIVE"),
                    "SystemFillColorCriticalBrush");
                AssertSemanticPillForeground(
                    window,
                    visibleTemplateHost,
                    activityColumn.CellTemplate,
                    new SemanticPillContext(ActivityState: "ENDED"),
                    "SystemFillColorSuccessBrush");

                var attentionBody = Find<Grid>(window, "CurrentAttentionBodyGrid");
                Assert.Equal(5, attentionBody.RowDefinitions.Count);
                AssertAuto(attentionBody.RowDefinitions[0].Height);
                AssertPixel(attentionBody.RowDefinitions[1].Height, 16);
                AssertStar(attentionBody.RowDefinitions[2].Height, 1);
                AssertPixel(attentionBody.RowDefinitions[3].Height, 0);
                AssertPixel(attentionBody.RowDefinitions[4].Height, 0);
                Assert.Equal(3, attentionBody.ColumnDefinitions.Count);
                AssertPixel(attentionBody.ColumnDefinitions[0].Width, 520);
                AssertPixel(attentionBody.ColumnDefinitions[1].Width, 16);
                AssertStar(attentionBody.ColumnDefinitions[2].Width, 1);
                Assert.Same(
                    attentionBody.ColumnDefinitions[0],
                    Find<ColumnDefinition>(window, "CurrentAttentionMasterColumn"));
                Assert.Same(
                    attentionBody.ColumnDefinitions[1],
                    Find<ColumnDefinition>(window, "CurrentAttentionMasterDetailGapColumn"));
                Assert.Same(
                    attentionBody.ColumnDefinitions[2],
                    Find<ColumnDefinition>(window, "CurrentAttentionDetailColumn"));
                Assert.Equal(
                    new GridLength(520),
                    Assert.IsType<GridLength>(
                        window.FindResource("CurrentAttentionMasterColumnWidth")));
                Assert.Null(window.TryFindResource("CurrentAttentionFacetColumnWidth"));
                Assert.Null(window.TryFindResource("CurrentAttentionEvidenceColumnWidth"));

                var attentionFilter = Find<Wpf.Ui.Controls.Card>(
                    window,
                    "CurrentAttentionFacetCard");
                var attentionResults = Find<Wpf.Ui.Controls.Card>(
                    window,
                    "CurrentAttentionResultsCard");
                var attentionDetailCard = Find<Wpf.Ui.Controls.Card>(
                    window,
                    "CurrentAttentionEvidenceCard");
                Assert.Equal((0, 0, 3), (
                    Grid.GetColumn(attentionFilter),
                    Grid.GetRow(attentionFilter),
                    Grid.GetColumnSpan(attentionFilter)));
                Assert.Equal((0, 2), (
                    Grid.GetColumn(attentionResults),
                    Grid.GetRow(attentionResults)));
                Assert.Equal((2, 2), (
                    Grid.GetColumn(attentionDetailCard),
                    Grid.GetRow(attentionDetailCard)));
                var attentionDetail = Assert.IsType<Grid>(attentionDetailCard.Content);
                Assert.Equal(3, attentionDetail.RowDefinitions.Count);
                AssertStar(attentionDetail.RowDefinitions[0].Height, 1.1);
                AssertPixel(attentionDetail.RowDefinitions[1].Height, 1);
                AssertStar(attentionDetail.RowDefinitions[2].Height, 0.9);
                var attentionGrid = Find<DataGrid>(window, "CurrentAttentionGrid");
                Assert.Equal(42, attentionGrid.RowHeight);
                Assert.Equal(38, attentionGrid.ColumnHeaderHeight);
                Assert.Equal(DataGridHeadersVisibility.Column, attentionGrid.HeadersVisibility);
                Assert.Equal(5, attentionGrid.Columns.Count);
                AssertTextColumn(attentionGrid.Columns[0], "对象", "SubjectSummary");
                AssertTextColumn(attentionGrid.Columns[1], "类型", "KindLabel");
                var attentionSeverityColumn = Assert.IsType<DataGridTemplateColumn>(
                    attentionGrid.Columns[2]);
                Assert.Equal("严重度", attentionSeverityColumn.Header);
                var severityPill = Assert.IsType<Border>(
                    attentionSeverityColumn.CellTemplate.LoadContent());
                AssertSemanticPill(severityPill, "Severity", "ERROR", "WARNING");
                var severityText = Assert.IsType<Wpf.Ui.Controls.TextBlock>(severityPill.Child);
                var severityTextBinding = Assert.IsType<Binding>(
                    BindingOperations.GetBinding(severityText, TextBlock.TextProperty));
                Assert.Equal("Severity", severityTextBinding.Path.Path);
                AssertSemanticPillForeground(
                    window,
                    visibleTemplateHost,
                    attentionSeverityColumn.CellTemplate,
                    new SemanticPillContext(Severity: "ERROR"),
                    "SystemFillColorCriticalBrush");
                AssertSemanticPillForeground(
                    window,
                    visibleTemplateHost,
                    attentionSeverityColumn.CellTemplate,
                    new SemanticPillContext(Severity: "WARNING"),
                    "SystemFillColorCautionBrush");
                AssertTextColumn(attentionGrid.Columns[3], "发生时间", "OccurredAt");
                AssertTextColumn(attentionGrid.Columns[4], "稳定标识", "StableIdentity");
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

    public sealed record SemanticPillContext(
        string? ActivityState = null,
        string? Severity = null);

    private static T Find<T>(FrameworkElement root, string name)
        where T : class => WatchErrorSearchProductionIntegrationTests.Find<T>(root, name);

    private static T? FindVisualDescendant<T>(
        DependencyObject root,
        Predicate<T> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate && predicate(candidate))
            {
                return candidate;
            }

            var descendant = FindVisualDescendant(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T SetterValue<T>(Style style, DependencyProperty property)
    {
        var setter = Assert.Single(
            style.Setters.OfType<Setter>(),
            candidate => candidate.Property == property);
        return Assert.IsType<T>(setter.Value);
    }

    private static void AssertSemanticPill(
        Border pill,
        string bindingPath,
        params string[] triggerValues)
    {
        var nameBinding = Assert.IsType<Binding>(
            BindingOperations.GetBinding(pill, AutomationProperties.NameProperty));
        Assert.Equal(bindingPath, nameBinding.Path.Path);
        Assert.NotNull(pill.Style);
        Assert.All(
            triggerValues,
            expected => Assert.Contains(
                pill.Style.Triggers.OfType<DataTrigger>(),
                trigger => trigger.Binding is Binding binding
                    && binding.Path.Path == bindingPath
                    && string.Equals(trigger.Value?.ToString(), expected, StringComparison.Ordinal)));
    }

    private static void AssertSemanticPillForeground(
        FrameworkElement resourceOwner,
        Grid visualParent,
        DataTemplate template,
        object dataContext,
        string foregroundResourceKey)
    {
        var presenter = new ContentPresenter
        {
            Content = dataContext,
            ContentTemplate = template,
        };
        visualParent.Children.Add(presenter);
        try
        {
            resourceOwner.UpdateLayout();
            var pill = Assert.IsType<Border>(
                FindVisualDescendant<Border>(presenter, _ => true));
            var text = Assert.IsType<Wpf.Ui.Controls.TextBlock>(pill.Child);

            var expected = Assert.IsAssignableFrom<Brush>(
                resourceOwner.FindResource(foregroundResourceKey));
            var inherited = Assert.IsAssignableFrom<Brush>(
                pill.GetValue(System.Windows.Documents.TextElement.ForegroundProperty));
            Assert.Same(expected, inherited);
            Assert.Same(inherited, text.Foreground);
            if (expected is SolidColorBrush expectedSolid
                && text.Foreground is SolidColorBrush actualSolid)
            {
                Assert.Equal(expectedSolid.Color, actualSolid.Color);
            }
        }
        finally
        {
            visualParent.Children.Remove(presenter);
        }
    }

    private static void AssertTextColumn(
        DataGridColumn column,
        string header,
        string bindingPath)
    {
        var textColumn = Assert.IsType<DataGridTextColumn>(column);
        Assert.Equal(header, textColumn.Header);
        var binding = Assert.IsType<Binding>(textColumn.Binding);
        Assert.Equal(bindingPath, binding.Path.Path);
    }

    private static void AssertSelectedPageRows(Grid grid, double secondGap = 16)
    {
        Assert.Equal(5, grid.RowDefinitions.Count);
        AssertAuto(grid.RowDefinitions[0].Height);
        AssertPixel(grid.RowDefinitions[1].Height, 12);
        AssertAuto(grid.RowDefinitions[2].Height);
        AssertPixel(grid.RowDefinitions[3].Height, secondGap);
        AssertStar(grid.RowDefinitions[4].Height, 1);
    }

    private static void AssertAuto(GridLength actual) =>
        Assert.Equal(GridUnitType.Auto, actual.GridUnitType);

    private static void AssertPixel(GridLength actual, double expected)
    {
        Assert.Equal(GridUnitType.Pixel, actual.GridUnitType);
        Assert.InRange(Math.Abs(actual.Value - expected), 0, 0.001);
    }

    private static void AssertStar(GridLength actual, double expected)
    {
        Assert.Equal(GridUnitType.Star, actual.GridUnitType);
        Assert.InRange(Math.Abs(actual.Value - expected), 0, 0.001);
    }
}
