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
                Assert.Same(window.FindResource("AccentTextFillColorPrimaryBrush"), scopeTint.Background);
                Assert.Equal(0.10, scopeTint.Opacity, precision: 2);

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
                    Find<ButtonBase>(window, "SaveRefreshIntervalsButton"),
                    settingsPage,
                    "Settings save command");

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
