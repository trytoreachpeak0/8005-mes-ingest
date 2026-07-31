using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Repro for Watch crash during XAML load: FilterStatus SelectionChanged fires
/// OnFilterChanged before later-named controls (grids, GoneWindowHours, …) exist.
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
}
