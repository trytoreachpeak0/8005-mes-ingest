using System.Windows.Automation;
using System.Windows.Controls;
using MesIngest.Watch;

namespace MesIngest.Tests;

[Collection("WpfDesktop")]
public sealed class WatchBilingualReleaseContractTests
{
    [Fact]
    public void One_shared_language_state_reprojects_every_production_workspace_and_inspector_surface()
    {
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-bilingual-release-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var language = new WatchDisplayLanguageState(WatchDisplayLanguage.SimplifiedChinese);
            WatchWorkspaceWindow? window = null;
            WatchDemandSeriesInspectorWindow? inspector = null;
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
                inspector = new WatchDemandSeriesInspectorWindow(language);
                window.Show();
                inspector.Show();
                window.UpdateLayout();
                inspector.UpdateLayout();

                var technicalSeriesId = Assert.IsType<TextBox>(
                    window.FindName("DemandSeriesSeriesIdFilter"));
                technicalSeriesId.Text = "SERIES-RAW-11";
                var technicalErrorCode = Assert.IsType<ComboBox>(
                    window.FindName("ErrorSearchCodeFilter"));
                technicalErrorCode.Text = "FUTURE_ERROR_CODE_11";
                var mainAutomationIds = new[]
                {
                    "OverviewPage",
                    "DemandSeriesPage",
                    "ReadabilityAuditPage",
                    "ErrorSearchPage",
                    "AreaFilterPage",
                    "CurrentAttentionPage",
                    "SettingsPage",
                }.Select(name => AutomationProperties.GetAutomationId(
                    Assert.IsAssignableFrom<System.Windows.DependencyObject>(window.FindName(name))))
                    .ToArray();

                language.ApplyCommitted(WatchDisplayLanguage.English);
                var text = language.Catalog;

                Assert.Equal(text.Overview.PageTitle, Text(window, "OverviewPageTitleText"));
                Assert.Equal(text.DemandSeries.PageTitle, Text(window, "DemandSeriesPageTitleText"));
                Assert.Equal(text.ReadabilityAudit.PageTitle, Text(window, "ReadabilityPageTitleText"));
                Assert.Equal(text.ErrorSearch.PageTitle, Text(window, "ErrorSearchPageTitleText"));
                Assert.Equal(text.AreaFilter.PageTitle, Text(window, "AreaProfilePageTitleText"));
                Assert.Equal(text.CurrentAttention.PageTitle, Text(window, "CurrentAttentionPageTitleText"));
                Assert.Equal(text.Settings.PageTitle, Text(window, "SettingsPageTitleText"));
                Assert.Equal(text.Inspector.WindowTitle, inspector.Title);
                Assert.Equal("SERIES-RAW-11", technicalSeriesId.Text);
                Assert.Equal("FUTURE_ERROR_CODE_11", technicalErrorCode.Text);
                Assert.Equal(
                    mainAutomationIds,
                    new[]
                    {
                        "OverviewPage",
                        "DemandSeriesPage",
                        "ReadabilityAuditPage",
                        "ErrorSearchPage",
                        "AreaFilterPage",
                        "CurrentAttentionPage",
                        "SettingsPage",
                    }.Select(name => AutomationProperties.GetAutomationId(
                        Assert.IsAssignableFrom<System.Windows.DependencyObject>(window.FindName(name)))));
                Assert.Same(language, window.DisplayLanguageState);
            }
            finally
            {
                inspector?.Close();
                window?.Close();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }

    private static string Text(WatchWorkspaceWindow window, string name) =>
        Assert.IsAssignableFrom<TextBlock>(window.FindName(name)).Text;
}
