using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MesIngest.Watch;

namespace MesIngest.Tests;

[Collection("WpfDesktop")]
public sealed class WatchBilingualAreaFilterTests
{
    [Fact]
    public void Area_editor_language_reprojection_preserves_draft_selection_caret_and_disk()
    {
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory();
            var areaDirectory = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaDirectory);
            var profilePath = Path.Combine(areaDirectory, "line-a.txt");
            File.WriteAllText(profilePath, "# draft\nAREA-A\nAREA-B\n");
            var language = new WatchDisplayLanguageState(WatchDisplayLanguage.SimplifiedChinese);
            WatchWorkspaceWindow? window = null;
            try
            {
                window = new WatchWorkspaceWindow(
                    new WatchHostSettings("http://host-a", "secret", 30),
                    WatchV2Preferences.Default,
                    Path.Combine(root, "connection.json"),
                    Path.Combine(root, "workspace.json"),
                    initializeOnLoaded: false,
                    areaFilterProfilesDirectoryPath: areaDirectory,
                    displayLanguageState: language);
                window.Show();
                Assert.IsAssignableFrom<ButtonBase>(window.FindName("AreaFilterNavigationItem"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.UpdateLayout();

                var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
                list.SelectedItem = list.Items.Cast<WatchAreaFilterProfilePresentationRow>()
                    .Single(row => row.ProfileName == "line-a");
                var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
                editor.Text += "AREA-C\n";
                editor.Select(9, 4);
                var lastWriteBefore = File.GetLastWriteTimeUtc(profilePath);

                language.ApplyCommitted(WatchDisplayLanguage.English);
                window.UpdateLayout();

                Assert.Equal("line-a", Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem).ProfileName);
                Assert.Equal("# draft\nAREA-A\nAREA-B\nAREA-C\n", editor.Text);
                Assert.Equal(9, editor.SelectionStart);
                Assert.Equal(4, editor.SelectionLength);
                Assert.Equal(lastWriteBefore, File.GetLastWriteTimeUtc(profilePath));
                Assert.Equal(
                    "AREA filter profiles",
                    Assert.IsAssignableFrom<TextBlock>(window.FindName("AreaProfilePageTitleText")).Text);
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-bilingual-area-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
