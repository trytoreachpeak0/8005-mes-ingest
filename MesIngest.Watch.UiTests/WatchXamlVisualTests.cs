using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VerifyTests;
using VerifyXunit;

namespace MesIngest.Watch.UiTests;

public sealed class WatchXamlVisualTests
{
    [Fact]
    [Trait("Category", "watch-vm-tests")]
    public void Fixed_visual_data_covers_the_approved_domain_matrix()
    {
        var visible = WatchVisualScenario.FakeDemands(visible: true);
        var gone = WatchVisualScenario.FakeDemands(visible: false);
        var alerts = WatchVisualScenario.FakeAlerts(active: true);

        Assert.Equal(
            new[]
            {
                "DIE_TO_WIRE_STAGING",
                "DIE_TO_OVEN",
                "WIRE_TO_GATE",
                "WIRE_TO_OPTICAL",
                "STAGING_TO_WIRE",
                "WIRE_TO_NITROGEN",
            },
            visible.Select(item => item.TaskType));
        Assert.All(visible, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Sublot));
            Assert.False(string.IsNullOrWhiteSpace(item.Area));
            Assert.False(string.IsNullOrWhiteSpace(item.Eqp));
            Assert.False(string.IsNullOrWhiteSpace(item.Step));
            Assert.False(string.IsNullOrWhiteSpace(item.Package));
            Assert.Equal("VISIBLE", item.Status);
            Assert.Null(item.GoneAt);
        });
        Assert.Contains(visible, item => item.LocationRisk && item.LocationRiskCode is not null);
        Assert.All(
            visible.Where(item => item.LocationRisk),
            item => Assert.Contains(item.LocationRiskCode, new[] { "AREA_EMPTY", "AREA_UNPARSEABLE" }));
        Assert.All(gone, item =>
        {
            Assert.Equal("GONE", item.Status);
            Assert.NotNull(item.GoneAt);
        });
        Assert.Equal(
            new[]
            {
                "POLL_FAILURE",
                "POLL_INCOMPLETE",
                "DUPLICATE_RECONCILE_KEY",
                "PAUSED_ZERO_DROP",
                "FIELD_DRIFT",
                "REAPPEAR_AFTER_GONE",
            },
            alerts.Select(item => item.Code));
        Assert.Equal(
            new[] { "ERROR", "ERROR", "ERROR", "ERROR", "ERROR", "WARNING" },
            alerts.Select(item => item.Severity));
        Assert.All(alerts, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Message));
            Assert.False(string.IsNullOrWhiteSpace(item.Details));
        });
    }

    private static readonly WatchVisualCase[] Required1440Cases =
    [
        WatchVisualCase.At1440("overview-healthy", WatchVisualState.OverviewHealthy),
        WatchVisualCase.At1440("overview-degraded-active-alert", WatchVisualState.OverviewDegraded),
        WatchVisualCase.At1440("overview-offline-stale", WatchVisualState.OverviewOfflineStale),
        WatchVisualCase.At1440("demands-visible-selected", WatchVisualState.DemandsVisibleSelected),
        WatchVisualCase.At1440("demands-gone-selected", WatchVisualState.DemandsGoneSelected),
        WatchVisualCase.At1440("demands-empty", WatchVisualState.DemandsEmpty),
        WatchVisualCase.At1440("demands-loading", WatchVisualState.DemandsLoading),
        WatchVisualCase.At1440("demands-failure-retains-results", WatchVisualState.DemandsFailureRetainsResults),
        WatchVisualCase.At1440("alerts-active-selected", WatchVisualState.AlertsActiveSelected),
        WatchVisualCase.At1440("alerts-resolved", WatchVisualState.AlertsResolved),
        WatchVisualCase.At1440("alerts-empty", WatchVisualState.AlertsEmpty),
        WatchVisualCase.At1440("alerts-loading", WatchVisualState.AlertsLoading),
        WatchVisualCase.At1440("alerts-failure-retains-results", WatchVisualState.AlertsFailureRetainsResults),
        WatchVisualCase.At1440("settings-default", WatchVisualState.SettingsDefault),
        WatchVisualCase.At1440("settings-validation-error", WatchVisualState.SettingsValidationError),
    ];

    public static TheoryData<WatchVisualCase> RequiredBaselines => TheoryDataFor(
    [
        .. Required1440Cases,
        WatchVisualCase.At2560("overview-loaded", WatchVisualState.OverviewHealthy),
        WatchVisualCase.At2560("demands-loaded", WatchVisualState.DemandsVisibleSelected),
        WatchVisualCase.At2560("alerts-loaded", WatchVisualState.AlertsActiveSelected),
        WatchVisualCase.At2560("settings-loaded", WatchVisualState.SettingsDefault),
    ]);

    public static TheoryData<WatchVisualCase> RequiredScenarioStates =>
        TheoryDataFor(Required1440Cases);

    [Fact]
    [Trait("Category", "watch-vm-tests")]
    public async Task Offline_visual_waits_for_the_completed_deterministic_failure()
    {
        await WatchVisualSta.RunAsync(async () =>
        {
            using var scenario = WatchVisualScenario.Create(
                WatchVisualCase.At1440("offline-final-state", WatchVisualState.OverviewOfflineStale));
            try
            {
                await scenario.PrepareAsync();

                AssertVisible(scenario.Window, "ErrorBanner");
                Assert.Equal(
                    Visibility.Collapsed,
                    ((FrameworkElement)scenario.Window.FindName("OverviewBusyText")).Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    ((FrameworkElement)scenario.Window.FindName("OverviewCancelButton")).Visibility);
                Assert.True(((Button)scenario.Window.FindName("OverviewRefreshButton")).IsEnabled);
                Assert.Contains(
                    "endpoint=/api/demands",
                    ((TextBlock)scenario.Window.FindName("ErrorBannerText")).Text,
                    StringComparison.Ordinal);
            }
            finally
            {
                scenario.Window.Close();
            }
        });
    }

    [Theory]
    [InlineData(WatchVisualState.DemandsVisibleSelected)]
    [InlineData(WatchVisualState.AlertsActiveSelected)]
    [Trait("Category", "watch-vm-tests")]
    public async Task Selected_ui_is_built_on_the_wpf_ui_control_system(
        WatchVisualState state)
    {
        await WatchVisualSta.RunAsync(async () =>
        {
            using var scenario = WatchVisualScenario.Create(
                WatchVisualCase.At1440("wpf-ui-control-system", state));
            try
            {
                await scenario.PrepareAsync();

                Assert.Equal(
                    "Wpf.Ui.Controls.FluentWindow",
                    scenario.Window.GetType().BaseType?.FullName);
                var refreshButton = Assert.IsAssignableFrom<Button>(scenario.Window.FindName(
                    state == WatchVisualState.DemandsVisibleSelected
                        ? "DemandRefreshButton"
                        : "AlertRefreshButton"));
                Assert.Equal("Wpf.Ui.Controls.Button", refreshButton.GetType().FullName);
                var queryButton = Assert.IsAssignableFrom<Button>(scenario.Window.FindName(
                    state == WatchVisualState.DemandsVisibleSelected
                        ? "DemandQueryButton"
                        : "AlertQueryButton"));
                Assert.Equal("Wpf.Ui.Controls.Button", queryButton.GetType().FullName);
                var autoRefresh = Assert.IsAssignableFrom<ToggleButton>(scenario.Window.FindName(
                    state == WatchVisualState.DemandsVisibleSelected
                        ? "DemandAutoRefreshCheckBox"
                        : "AlertAutoRefreshCheckBox"));
                Assert.Equal("Wpf.Ui.Controls.ToggleSwitch", autoRefresh.GetType().FullName);
                if (state == WatchVisualState.DemandsVisibleSelected)
                {
                    var fullDetails = Assert.IsAssignableFrom<FrameworkElement>(
                        scenario.Window.FindName("DemandFullDetailsPanel"));
                    Assert.Equal(Visibility.Collapsed, fullDetails.Visibility);
                    Assert.Equal(
                        7,
                        Assert.IsAssignableFrom<ItemsControl>(scenario.Window.FindName(
                            "DemandMesInputFields")).Items.Count);
                    Assert.Equal(
                        8,
                        Assert.IsAssignableFrom<ItemsControl>(scenario.Window.FindName(
                            "DemandLocalProjectionFields")).Items.Count);
                    Assert.IsType<Wpf.Ui.Controls.Button>(scenario.Window.FindName(
                        "DemandFullDetailsCloseButton"));
                    Assert.IsType<Wpf.Ui.Controls.Button>(scenario.Window.FindName(
                        "DemandCopyFullRowButton"));
                }
            }
            finally
            {
                scenario.Window.Close();
            }
        });
    }

    [Theory]
    [InlineData(WatchVisualState.DemandsVisibleSelected)]
    [InlineData(WatchVisualState.AlertsActiveSelected)]
    [Trait("Category", "watch-vm-tests")]
    public async Task Selected_ui_matches_the_approved_d_e_workbench_structure(
        WatchVisualState state)
    {
        await WatchVisualSta.RunAsync(async () =>
        {
            using var scenario = WatchVisualScenario.Create(
                WatchVisualCase.At1440("approved-d-e-workbench", state));
            try
            {
                await scenario.PrepareAsync();

                var root = Assert.IsType<Grid>(scenario.Window.FindName("RootLayout"));
                var titleBar = Assert.IsType<Wpf.Ui.Controls.TitleBar>(
                    scenario.Window.FindName("WindowTitleBar"));
                Assert.Null(scenario.Window.FindName("GlobalHeader"));
                var connectionStatus = Assert.IsType<Border>(
                    scenario.Window.FindName("ConnectionStatusChip"));
                var navigation = Assert.IsType<Border>(scenario.Window.FindName("NavigationRail"));
                var filter = Assert.IsType<ScrollViewer>(scenario.Window.FindName(
                    state == WatchVisualState.DemandsVisibleSelected
                        ? "DemandFiltersScrollViewer"
                        : "AlertFiltersScrollViewer"));
                var workspace = Assert.IsAssignableFrom<FrameworkElement>(scenario.Window.FindName(
                    state == WatchVisualState.DemandsVisibleSelected
                        ? "DemandWorkspace"
                        : "AlertWorkspace"));
                var resultBadge = Assert.IsType<Border>(scenario.Window.FindName(
                    state == WatchVisualState.DemandsVisibleSelected
                        ? "DemandResultBadge"
                        : "AlertResultBadge"));

                var headerActions = Assert.IsType<StackPanel>(scenario.Window.FindName(
                    state == WatchVisualState.DemandsVisibleSelected
                        ? "DemandHeaderActions"
                        : "AlertHeaderActions"));

                Assert.Equal(3, root.RowDefinitions.Count);
                Assert.InRange(root.RowDefinitions[0].ActualHeight, 47.99, 48.01);
                Assert.InRange(root.RowDefinitions[2].ActualHeight, 27.99, 28.01);
                Assert.Equal(0, Grid.GetRow(titleBar));
                Assert.Equal(3, Grid.GetColumnSpan(titleBar));
                Assert.True(scenario.Window.ExtendsContentIntoTitleBar);
                Assert.Null(titleBar.TrailingContent);
                Assert.Equal(1, Grid.GetRow(navigation));
                Assert.InRange(navigation.ActualWidth, 193.99, 194.01);
                var filterWidth = state == WatchVisualState.DemandsVisibleSelected
                    ? filter.ActualWidth
                    : ((Grid)((Border)filter.Parent).Parent).ColumnDefinitions[0].ActualWidth;
                Assert.InRange(filterWidth, 259.99, 260.01);
                var workspaceOrigin = workspace.TranslatePoint(new Point(0, 0), root);
                Assert.InRange(workspaceOrigin.X, 473.99, 474.01);
                Assert.True(headerActions.IsVisible);
                Assert.True(resultBadge.IsVisible);
                var statusBar = Assert.IsType<StatusBar>(scenario.Window.FindName("WatchStatusBar"));
                var statusLayout = Assert.IsType<DockPanel>(Assert.Single(statusBar.Items));
                Assert.Contains(connectionStatus, statusLayout.Children.Cast<UIElement>());
                AssertVisible(scenario.Window, "StatusBarSummaryText");
                AssertVisible(scenario.Window, "StatusBarShortcutText");

                var grid = Assert.IsType<DataGrid>(scenario.Window.FindName(
                    state == WatchVisualState.DemandsVisibleSelected ? "DemandsGrid" : "AlertsGrid"));
                var headers = grid.Columns.Select(column => column.Header?.ToString()).ToArray();
                Assert.Equal(
                    state == WatchVisualState.DemandsVisibleSelected
                        ? ["TASK_TYPE", "SUBLOT", "AREA", "EQP", "DATES"]
                        : ["SEVERITY", "CODE", "SCOPE", "TASK_TYPE", "COUNT"],
                    headers.Take(5));
            }
            finally
            {
                scenario.Window.Close();
            }
        });
    }

    [Theory]
    [InlineData(WatchVisualState.DemandsVisibleSelected)]
    [InlineData(WatchVisualState.AlertsActiveSelected)]
    [Trait("Category", "watch-vm-tests")]
    public async Task Selected_ui_shell_keeps_the_fixed_design_anchors_at_1440x900(
        WatchVisualState state)
    {
        await WatchVisualSta.RunAsync(async () =>
        {
            using var scenario = WatchVisualScenario.Create(
                WatchVisualCase.At1440("selected-ui-shell", state));
            try
            {
                await scenario.PrepareAsync();

                var root = Assert.IsType<Grid>(scenario.Window.Content);
                var filters = (ScrollViewer)scenario.Window.FindName(
                    state == WatchVisualState.DemandsVisibleSelected
                        ? "DemandFiltersScrollViewer"
                        : "AlertFiltersScrollViewer");
                var statusBar = (StatusBar)scenario.Window.FindName("WatchStatusBar");
                var filterWidth = state == WatchVisualState.DemandsVisibleSelected
                    ? filters.ActualWidth
                    : ((Grid)((Border)filters.Parent).Parent).ColumnDefinitions[0].ActualWidth;

                Assert.InRange(root.ColumnDefinitions[0].ActualWidth, 193.99, 194.01);
                Assert.InRange(root.RowDefinitions[0].ActualHeight, 47.99, 48.01);
                Assert.InRange(filterWidth, 259.99, 260.01);
                Assert.InRange(root.RowDefinitions[2].ActualHeight, 27.99, 28.01);
                Assert.InRange(statusBar.ActualHeight, 27.99, 28.01);
            }
            finally
            {
                scenario.Window.Close();
            }
        });
    }

    [Theory]
    [InlineData(WatchVisualState.DemandsVisibleSelected)]
    [InlineData(WatchVisualState.AlertsActiveSelected)]
    [Trait("Category", "watch-vm-tests")]
    public async Task Selected_ui_keeps_results_and_relationship_cards_visible_at_1440x900(
        WatchVisualState state)
    {
        await WatchVisualSta.RunAsync(async () =>
        {
            using var scenario = WatchVisualScenario.Create(WatchVisualCase.At1440("selected-ui", state));
            try
            {
                await scenario.PrepareAsync();

                if (state == WatchVisualState.DemandsVisibleSelected)
                {
                    AssertVisible(scenario.Window, "DemandsGrid");
                    AssertVisible(scenario.Window, "DemandDetailsPanel");
                    AssertVisible(scenario.Window, "DemandRelatedAlertsGrid");
                }
                else
                {
                    AssertVisible(scenario.Window, "AlertsGrid");
                    AssertVisible(scenario.Window, "AlertRelationshipPanel");
                    AssertVisible(scenario.Window, "AlertRelationshipContent");
                    AssertVisible(scenario.Window, "AlertTargetOnePanel");
                    AssertVisible(scenario.Window, "AlertTargetTwoPanel");
                }
            }
            finally
            {
                scenario.Window.Close();
            }
        });
    }

    [Theory]
    [InlineData(WatchVisualState.DemandsVisibleSelected)]
    [InlineData(WatchVisualState.AlertsActiveSelected)]
    [Trait("Category", "watch-vm-tests")]
    public async Task Selected_ui_exposes_core_copy_controls_and_relationship_meaning(
        WatchVisualState state)
    {
        await WatchVisualSta.RunAsync(async () =>
        {
            using var scenario = WatchVisualScenario.Create(WatchVisualCase.At1440("selected-ui-copy", state));
            try
            {
                await scenario.PrepareAsync();
                var visibleText = VisualDescendants<TextBlock>(scenario.Window)
                    .Where(text => text.IsVisible)
                    .Select(text => text.Text)
                    .ToArray();

                if (state == WatchVisualState.DemandsVisibleSelected)
                {
                    Assert.Contains("FILTERS", visibleText);
                    Assert.Contains("任务浏览", visibleText);
                    Assert.Contains("当前任务", visibleText);
                    Assert.Contains("相关告警", visibleText);
                    Assert.Contains("关联规则", visibleText);
                    Assert.True(((Button)scenario.Window.FindName("DemandRefreshButton")).IsVisible);
                    Assert.NotNull(scenario.Window.FindName("DemandCancelButton"));
                    Assert.True(((ToggleButton)scenario.Window.FindName("DemandAutoRefreshCheckBox")).IsVisible);
                }
                else
                {
                    Assert.Contains("FILTERS", visibleText);
                    Assert.Contains("接入告警", visibleText);
                    Assert.Contains("当前告警", visibleText);
                    Assert.Contains("相关任务", visibleText);
                    Assert.Contains("先前 GONE", visibleText);
                    Assert.Contains("当前再现", visibleText);
                    Assert.True(((Button)scenario.Window.FindName("AlertRefreshButton")).IsVisible);
                    Assert.NotNull(scenario.Window.FindName("AlertCancelButton"));
                    Assert.True(((ToggleButton)scenario.Window.FindName("AlertAutoRefreshCheckBox")).IsVisible);
                    foreach (var name in new[]
                             {
                                 "AlertTargetOneButton",
                                 "AlertTargetTwoButton",
                                 "AlertBusinessKeyButton",
                             })
                    {
                        var action = (Button)scenario.Window.FindName(name);
                        Assert.Equal(name, AutomationProperties.GetAutomationId(action));
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(action)));
                    }
                }
            }
            finally
            {
                scenario.Window.Close();
            }
        });
    }

    [Fact]
    [Trait("Category", "watch-vm-tests")]
    public async Task Demand_status_selector_wraps_its_two_tabs_instead_of_drawing_a_page_wide_empty_frame()
    {
        await WatchVisualSta.RunAsync(async () =>
        {
            using var scenario = WatchVisualScenario.Create(
                WatchVisualCase.At1440("demand-status-selector-shape", WatchVisualState.DemandsVisibleSelected));
            try
            {
                await scenario.PrepareAsync();
                var selector = (TabControl)scenario.Window.FindName("DemandStatusTabs");

                Assert.InRange(selector.ActualWidth, 80, 180);
                Assert.Null(selector.Template.FindName("PART_SelectedContentHost", selector));
            }
            finally
            {
                scenario.Window.Close();
            }
        });
    }

    [Theory]
    [MemberData(nameof(RequiredBaselines))]
    [Trait("Category", "watch-vm-tests")]
    public async Task Required_visual_scenario_creates_an_exact_offscreen_capture_surface(
        WatchVisualCase visualCase)
    {
        await WatchVisualSta.RunAsync(async () =>
        {
            using var scenario = WatchVisualScenario.Create(visualCase);
            try
            {
                await scenario.PrepareAsync();
                var captureTarget = scenario.CreateCaptureTarget();

                Assert.Null(scenario.Window.Content);
                Assert.Same(captureTarget, scenario.CreateCaptureTarget());
                Assert.InRange(Math.Abs(captureTarget.ActualWidth - visualCase.Width), 0, 0.01);
                Assert.InRange(Math.Abs(captureTarget.ActualHeight - visualCase.Height), 0, 0.01);
                Assert.False(string.IsNullOrWhiteSpace(XamlWriter.Save(captureTarget)));
                using var png = WatchVisualCaptureConverter.CapturePng(captureTarget);
                var decoder = new PngBitmapDecoder(
                    png,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                Assert.Equal(visualCase.Width, decoder.Frames[0].PixelWidth);
                Assert.Equal(visualCase.Height, decoder.Frames[0].PixelHeight);
            }
            finally
            {
                scenario.Window.Close();
            }
        });
    }

    [Theory]
    [MemberData(nameof(RequiredScenarioStates))]
    [Trait("Category", "watch-vm-tests")]
    public async Task Required_visual_scenario_reaches_its_state_without_a_live_host(
        WatchVisualCase visualCase)
    {
        await WatchVisualSta.RunAsync(async () =>
        {
            using var scenario = WatchVisualScenario.Create(visualCase);
            try
            {
                await scenario.PrepareAsync();
                Assert.True(scenario.Window.IsVisible);
                var conclusion = ((TextBlock)scenario.Window.FindName("OverviewConclusionText")).Text;
                if (visualCase.State == WatchVisualState.OverviewHealthy)
                {
                    Assert.Equal("✓ 健康", conclusion);
                }
                else if (visualCase.State == WatchVisualState.OverviewDegraded)
                {
                    Assert.Equal("✕ 存在活动 ERROR", conclusion);
                }

                if (visualCase.State is WatchVisualState.OverviewHealthy
                    or WatchVisualState.OverviewDegraded)
                {
                    var errorBanner = (Border)scenario.Window.FindName("ErrorBanner");
                    Assert.Equal(Visibility.Collapsed, errorBanner.Visibility);
                }

                if (visualCase.State == WatchVisualState.DemandsVisibleSelected)
                {
                    var rowCount = ((TextBlock)scenario.Window.FindName("RowCountText")).Text;
                    Assert.Contains("2026-08-08 12:42:22", rowCount);
                }
                else if (visualCase.State == WatchVisualState.AlertsActiveSelected)
                {
                    var alertCount = ((TextBlock)scenario.Window.FindName("AlertCountText")).Text;
                    Assert.Contains("2026-08-08 12:42:22", alertCount);
                    var alertGrid = (DataGrid)scenario.Window.FindName("AlertsGrid");
                    var detailButton = VisualDescendants<Button>(alertGrid).First(button =>
                        AutomationProperties.GetAutomationId(button) == "AlertDetailsButton");
                    var code = Assert.IsType<TextBlock>(detailButton.Content);
                    Assert.Equal("POLL_FAILURE", code.Text);
                }
            }
            finally
            {
                scenario.Window.Close();
            }
        });
    }

    [Theory]
    [MemberData(nameof(RequiredBaselines))]
    [Trait("Category", "watch-xaml-visual")]
    public async Task Required_page_baseline(WatchVisualCase visualCase)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var environment = WatchVisualEnvironment.Evaluate(WatchVisualEnvironment.Capture());
        Assert.SkipWhen(!environment.IsCompatible, environment.FormatReport());

        await WatchVisualSta.RunAsync(async () =>
        {
            using var scenario = WatchVisualScenario.Create(visualCase);
            await scenario.PrepareAsync();
            try
            {
                var settings = new VerifySettings();
                settings.UseFileName(visualCase.BaselineName);
                var captureTarget = scenario.CreateCaptureTarget();
                Assert.InRange(Math.Abs(captureTarget.ActualWidth - visualCase.Width), 0, 0.01);
                Assert.InRange(Math.Abs(captureTarget.ActualHeight - visualCase.Height), 0, 0.01);
                await Verifier.Verify(captureTarget, settings);
            }
            finally
            {
                scenario.Window.Close();
            }
        });
    }

    private static TheoryData<WatchVisualCase> TheoryDataFor(
        IEnumerable<WatchVisualCase> visualCases)
    {
        var data = new TheoryData<WatchVisualCase>();
        foreach (var visualCase in visualCases)
        {
            data.Add(visualCase);
        }

        return data;
    }

    private static void AssertVisible(FrameworkElement window, string name)
    {
        var element = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
        Assert.True(element.IsVisible, $"Expected {name} to remain visible in the selected UI.");
        Assert.True(element.ActualWidth > 0, $"Expected {name} to have a rendered width.");
        Assert.True(element.ActualHeight > 0, $"Expected {name} to have a rendered height.");
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
}

public sealed record WatchVisualCase(
    string BaselineName,
    WatchVisualState State,
    int Width,
    int Height)
{
    public static WatchVisualCase At1440(string name, WatchVisualState state) =>
        new($"{name}-1440x900", state, 1440, 900);

    public static WatchVisualCase At2560(string name, WatchVisualState state) =>
        new($"{name}-2560x1440", state, 2560, 1440);

    public override string ToString() => BaselineName;
}

public enum WatchVisualState
{
    OverviewHealthy,
    OverviewDegraded,
    OverviewOfflineStale,
    DemandsVisibleSelected,
    DemandsGoneSelected,
    DemandsEmpty,
    DemandsLoading,
    DemandsFailureRetainsResults,
    AlertsActiveSelected,
    AlertsResolved,
    AlertsEmpty,
    AlertsLoading,
    AlertsFailureRetainsResults,
    SettingsDefault,
    SettingsValidationError,
}

internal static class WatchVisualSta
{
    public static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Watch XAML visual STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
