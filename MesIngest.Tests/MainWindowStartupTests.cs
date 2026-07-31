using MesIngest.Watch;
using System.Windows.Controls;
using System.Windows.Input;

namespace MesIngest.Tests;

/// <summary>
/// Repro for Watch crash during XAML load: FilterStatus SelectionChanged fires
/// OnFilterChanged before later-named controls (grids, GoneWindowHours, …) exist.
/// Also covers ticket 08 clipboard wiring on both grids.
/// </summary>
public class MainWindowStartupTests
{
    [Fact]
    public void MainWindow_ctor_completes_without_null_reference()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
                var client = new MesIngestApiClient(http);
                var options = new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:9",
                    RefreshSeconds = 60,
                };
                var window = new MainWindow(client, options);
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(thread.Join(0), "STA constructor thread did not finish");
        Assert.Null(caught);
    }

    [Fact]
    public void MainWindow_attaches_clipboard_copy_to_both_grids()
    {
        Exception? caught = null;
        var ok = false;

        var thread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
                var client = new MesIngestApiClient(http);
                var options = new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:9",
                    RefreshSeconds = 60,
                };
                var window = new MainWindow(client, options);
                var demands = (DataGrid)window.FindName("DemandsGrid");
                var alerts = (DataGrid)window.FindName("AlertsGrid");

                if (demands.SelectionUnit != DataGridSelectionUnit.CellOrRowHeader
                    || alerts.SelectionUnit != DataGridSelectionUnit.CellOrRowHeader
                    || demands.ClipboardCopyMode != DataGridClipboardCopyMode.None
                    || alerts.ClipboardCopyMode != DataGridClipboardCopyMode.None
                    || demands.ContextMenu is null
                    || demands.ContextMenu.Items.Count != 3
                    || alerts.ContextMenu is null
                    || alerts.ContextMenu.Items.Count != 3
                    || !demands.CommandBindings.OfType<CommandBinding>()
                        .Any(b => ReferenceEquals(b.Command, ApplicationCommands.Copy))
                    || !alerts.CommandBindings.OfType<CommandBinding>()
                        .Any(b => ReferenceEquals(b.Command, ApplicationCommands.Copy)))
                {
                    throw new InvalidOperationException("Clipboard wiring incomplete on Demands/Alerts grids.");
                }

                ok = true;
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.Null(caught);
        Assert.True(ok);
    }
}
