using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

[Collection(WatchV2ProductionHostCollection.CollectionName)]
public sealed class WatchRejectedPrototypeLayoutTests
{
    [Fact]
    public async Task Overview_scope_accent_soft_is_exact_in_light_and_readable_in_dark_and_high_contrast()
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
                window.Show();
                AssertScopeTheme(
                    window,
                    Wpf.Ui.Appearance.ApplicationTheme.Light,
                    expectedLightColor: Color.FromRgb(0xE7, 0xF3, 0xFF));
                AssertScopeTheme(
                    window,
                    Wpf.Ui.Appearance.ApplicationTheme.Dark,
                    expectedLightColor: null);
                AssertScopeTheme(
                    window,
                    Wpf.Ui.Appearance.ApplicationTheme.HighContrast,
                    expectedLightColor: null);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Rejected_overview_settings_and_area_surfaces_restore_selected_wide_geometry()
    {
        using var files = new WatchErrorSearchProductionIntegrationTests.TemporaryWatchFiles();
        var areaDirectory = Path.Combine(files.Root, "MesIngest.Watch", "area-filters");
        var store = new WatchAreaFilterProfileStore(areaDirectory);
        Assert.True(store.Save("东区", "A1-1\nA1-2\n").Saved);
        Assert.True(store.Apply("东区").Applied);

        await WatchErrorSearchProductionIntegrationTests.RunInStaDispatcherAsync(() =>
        {
            using var composition = WatchV2ApplicationComposition.Create(
                new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:5088",
                    RenderingMode = WatchRenderingMode.SoftwareOnly,
                },
                connectionPreferencesPath: files.ConnectionPath,
                workspacePreferencesPath: files.WorkspacePath,
                areaFilterProfilesDirectoryPath: areaDirectory);
            var window = composition.CreateMainWindow(initializeOnLoaded: false);
            try
            {
                window.Width = 1440;
                window.Height = 900;
                window.Show();
                window.UpdateLayout();

                Assert.InRange(
                    Find<Wpf.Ui.Controls.Button>(window, "ReadableSummaryAction").ActualHeight,
                    1,
                    24);
                var scopeTint = Find<Border>(window, "OverviewScopeSurfaceTint");
                Assert.Same(window.FindResource("WatchAccentSoftBrush"), scopeTint.Background);
                Assert.Equal(1, scopeTint.Opacity);
                Assert.Equal(
                    Color.FromRgb(0xE7, 0xF3, 0xFF),
                    Assert.IsType<SolidColorBrush>(scopeTint.Background).Color);

                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "SettingsNavigationItem"));
                var settingsPage = Find<ScrollViewer>(window, "SettingsPage");
                Find<TextBox>(window, "RequestTimeoutInput").Text = "0";
                Click(Find<ButtonBase>(window, "ApplyHostButton"));
                var settingsInfo = Find<Wpf.Ui.Controls.InfoBar>(window, "SettingsInfoBar");
                Assert.True(settingsInfo.IsOpen);
                Assert.Equal("无法应用 Host 设置", settingsInfo.Title);
                Assert.Equal("请求超时必须是 1–300 秒之间的整数。", settingsInfo.Message);
                window.UpdateLayout();
                Assert.InRange(settingsPage.ScrollableHeight, 0, 0.5);
                AssertFullyWithin(
                    Find<ButtonBase>(window, "RestoreDefaultLayoutButton"),
                    settingsPage,
                    "Settings layout reset command");
                AssertFullyWithin(
                    Find<Expander>(window, "AdvancedLocalPreferencesExpander"),
                    settingsPage,
                    "Settings advanced preferences entry");

                Click(Find<Wpf.Ui.Controls.NavigationViewItem>(window, "AreaFilterNavigationItem"));
                window.UpdateLayout();
                var master = Find<Wpf.Ui.Controls.Card>(window, "AreaProfileMasterCard");
                var editor = Find<Wpf.Ui.Controls.Card>(window, "AreaProfileEditorCard");
                AssertClose(0, master.TranslatePoint(new Point(), editor).Y);
                AssertClose(master.ActualHeight, editor.ActualHeight);

                var directory = Find<Wpf.Ui.Controls.TextBlock>(window, "AreaProfileDirectoryText");
                var localApplicationData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                var relativeAreaDirectory = Path.GetRelativePath(
                    localApplicationData,
                    Path.GetFullPath(areaDirectory));
                Assert.Equal(
                    $"%LocalAppData%\\{relativeAreaDirectory} · UTF-8",
                    directory.Text);
                Assert.NotEqual(
                    "%LocalAppData%\\MesIngest.Watch\\area-filters · UTF-8",
                    directory.Text);
                Assert.Equal(Path.GetFullPath(areaDirectory), directory.ToolTip);
                Assert.Equal(Path.GetFullPath(areaDirectory), AutomationProperties.GetHelpText(directory));

                var selected = Assert.IsType<ListBoxItem>(
                    Find<ListBox>(window, "AreaProfileList").ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Same(window.FindResource("AccentFillColorDefaultBrush"), selected.Background);
                Assert.Same(window.FindResource("TextFillColorInverseBrush"), selected.Foreground);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(
        new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static void AssertScopeTheme(
        WatchWorkspaceWindow window,
        Wpf.Ui.Appearance.ApplicationTheme theme,
        Color? expectedLightColor)
    {
        window.ApplyWatchThemeResources(theme);
        window.UpdateLayout();

        var tint = Find<Border>(window, "OverviewScopeSurfaceTint");
        var background = Assert.IsType<SolidColorBrush>(tint.Background);
        if (expectedLightColor is { } lightColor)
        {
            Assert.Equal(lightColor, background.Color);
        }
        else
        {
            Assert.NotEqual(Color.FromRgb(0xE7, 0xF3, 0xFF), background.Color);
            Assert.Same(
                window.FindResource("ControlFillColorSecondaryBrush"),
                tint.Background);
        }

        var applicationBackground = Assert.IsType<SolidColorBrush>(
            window.FindResource("ApplicationBackgroundBrush"));
        var primaryText = Assert.IsType<SolidColorBrush>(
            window.FindResource("TextFillColorPrimaryBrush"));
        var effectiveBackground = Composite(background.Color, applicationBackground.Color);
        var effectiveForeground = Composite(primaryText.Color, effectiveBackground);
        Assert.True(
            ContrastRatio(effectiveForeground, effectiveBackground) >= 4.5,
            $"{theme} scope text must retain 4.5:1 contrast.");
    }

    private static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static double ContrastRatio(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045
                    ? value / 12.92
                    : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(color.R)
                + 0.7152 * Linear(color.G)
                + 0.0722 * Linear(color.B);
        }

        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05)
            / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static void AssertFullyWithin(
        FrameworkElement child,
        FrameworkElement viewport,
        string description)
    {
        var origin = child.TranslatePoint(new Point(), viewport);
        Assert.True(origin.Y >= -0.5, $"{description} starts above the viewport: {origin.Y}.");
        Assert.True(
            origin.Y + child.ActualHeight <= viewport.ActualHeight + 0.5,
            $"{description} ends below the viewport: {origin.Y + child.ActualHeight} > {viewport.ActualHeight}.");
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(Math.Abs(actual - expected), 0, 1.5);

    private static T Find<T>(FrameworkElement root, string name)
        where T : class => WatchErrorSearchProductionIntegrationTests.Find<T>(root, name);
}
