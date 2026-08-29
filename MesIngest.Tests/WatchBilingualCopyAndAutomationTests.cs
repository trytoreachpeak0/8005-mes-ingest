using System.Windows.Automation;
using System.Windows.Controls;
using MesIngest.Watch;

namespace MesIngest.Tests;

[Collection("WpfDesktop")]
public sealed class WatchBilingualCopyAndAutomationTests
{
    [Fact]
    public void Inspector_copy_commands_follow_shared_language_and_keep_grid_automation_ids()
    {
        StaTestRunner.Run(() =>
        {
            var language = new WatchDisplayLanguageState(WatchDisplayLanguage.SimplifiedChinese);
            var inspector = new WatchDemandSeriesInspectorWindow(language);
            try
            {
                var grids = new[]
                {
                    Assert.IsType<DataGrid>(inspector.FindName("DemandSeriesInspectorAfterObservationGrid")),
                    Assert.IsType<DataGrid>(inspector.FindName("DemandSeriesInspectorEventGrid")),
                };
                var ids = grids.Select(AutomationProperties.GetAutomationId).ToArray();

                language.ApplyCommitted(WatchDisplayLanguage.English);

                foreach (var grid in grids)
                {
                    Assert.Equal(
                        ["Copy cell", "Copy row", "Copy row with headers"],
                        grid.ContextMenu!.Items.Cast<MenuItem>()
                            .TakeLast(3)
                            .Select(item => item.Header?.ToString())
                            .ToArray());
                }
                Assert.Equal(ids, grids.Select(AutomationProperties.GetAutomationId));
            }
            finally
            {
                inspector.Close();
            }
        });
    }

    [Fact]
    public void English_reprojection_updates_copy_commands_and_uia_without_changing_automation_ids()
    {
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-copy-uia-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
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
                    areaFilterProfilesDirectoryPath: Path.Combine(root, "area-filters"),
                    displayLanguageState: language);
                window.Show();
                window.UpdateLayout();

                var grids = new[]
                {
                    "DemandSeriesGrid",
                    "ReadabilityAuditGrid",
                    "ErrorSearchSeriesGrid",
                    "AreaProfileValidationGrid",
                    "CurrentAttentionGrid",
                }.Select(name => Assert.IsType<DataGrid>(window.FindName(name))).ToArray();
                var automationIds = grids.Select(AutomationProperties.GetAutomationId).ToArray();

                language.ApplyCommitted(WatchDisplayLanguage.English);

                foreach (var grid in grids)
                {
                    Assert.Equal(
                        ["Copy cell", "Copy row", "Copy row with headers"],
                        grid.ContextMenu!.Items.Cast<MenuItem>()
                            .TakeLast(3)
                            .Select(item => item.Header?.ToString())
                            .ToArray());
                }

                Assert.Equal(automationIds, grids.Select(AutomationProperties.GetAutomationId));
                Assert.Equal("Window notifications", AutomationProperties.GetName(
                    Assert.IsType<Grid>(window.FindName("NotificationOverlay"))));
                Assert.Equal("AREA filter profiles page", AutomationProperties.GetName(
                    Assert.IsType<ScrollViewer>(window.FindName("AreaFilterPage"))));
            }
            finally
            {
                window?.Close();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }
}
