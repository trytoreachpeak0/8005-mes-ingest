using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using MesIngest.Watch;

namespace MesIngest.Tests;

public class MainWindowUiAutomationTests
{
    [Fact]
    public void Alert_details_control_opens_detail_window_through_flaui_uia3()
    {
        Exception? uiFailure = null;
        MainWindow? mainWindow = null;
        Dispatcher? dispatcher = null;
        nint mainHandle = 0;
        using var ready = new ManualResetEventSlim();
        var preferencesPath = Path.Combine(
            Path.GetTempPath(),
            $"mes-ingest-watch-ui-{Guid.NewGuid():N}.json");

        var uiThread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient(new FakeWatchHandler())
                {
                    BaseAddress = new Uri("http://watch.test/"),
                };
                mainWindow = new MainWindow(
                    new MesIngestApiClient(http),
                    new WatchOptions
                    {
                        BaseUrl = "http://watch.test",
                        RefreshSeconds = 60,
                    },
                    layoutPreferencesPath: preferencesPath);
                dispatcher = Dispatcher.CurrentDispatcher;
                mainWindow.Show();
                mainHandle = new WindowInteropHelper(mainWindow).Handle;
                ready.Set();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                uiFailure = ex;
                ready.Set();
            }
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        UIA3Automation? automation = null;
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "Watch UI did not become ready");
            Assert.Null(uiFailure);
            Assert.NotEqual(0, mainHandle);

            automation = new UIA3Automation();
            var window = automation.FromHandle(mainHandle).AsWindow();
            var detailsButton = Retry.WhileNull(
                () => window.FindFirstDescendant(
                        condition => condition.ByAutomationId("AlertDetailsButton"))
                    ?.AsButton(),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(100),
                throwOnTimeout: true).Result
                ?? throw new InvalidOperationException("Alert details button did not appear");

            detailsButton.Invoke();

            Assert.True(
                SpinWait.SpinUntil(
                    () => dispatcher!.Invoke(() => mainWindow!.OwnedWindows
                        .OfType<AlertDetailWindow>()
                        .Any(detail => detail.AlertId == "alert-ui-1"
                            && detail.Title == "Alert detail — alert-ui-1")),
                    TimeSpan.FromSeconds(10)),
                "FlaUI UIA3 invocation did not open the expected Alert detail window");
        }
        finally
        {
            automation?.Dispose();
            dispatcher?.BeginInvoke(() =>
            {
                foreach (System.Windows.Window owned in mainWindow?.OwnedWindows ?? [])
                {
                    owned.Close();
                }

                mainWindow?.Close();
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            });
            Assert.True(uiThread.Join(TimeSpan.FromSeconds(30)), "Watch UI thread did not stop");

            if (File.Exists(preferencesPath))
            {
                File.Delete(preferencesPath);
            }
        }

        Assert.Null(uiFailure);
    }

    private sealed class FakeWatchHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = path switch
            {
                "/api/contract" =>
                    """{"contractVersion":"2026.08.watch-ops.1","schemaVersion":2}""",
                "/api/demands" =>
                    """{"items":[],"nextCursor":null,"hasMore":false}""",
                "/api/alerts" =>
                    """
                    {"items":[{"alertId":"alert-ui-1","code":"FIELD_DRIFT","severity":"ERROR","taskType":"DIE_TO_OVEN","sublot":"S1","demandId":"demand-ui-1","message":"drift","details":"{}","firstSeenAt":"2026-08-03T10:00:00+08:00","lastSeenAt":"2026-08-03T10:00:00+08:00","occurrenceCount":1,"isActive":true,"resolvedAt":null,"createdAt":"2026-08-03T10:00:00+08:00"}],"nextCursor":null,"hasMore":false}
                    """,
                "/api/poll-health" =>
                    """
                    {"startedAt":"2026-08-03T10:00:00+08:00","endedAt":"2026-08-03T10:00:00+08:00","durationMs":10,"rowCount":1,"success":true,"outcome":"SUCCESS","taskTypePauses":[]}
                    """,
                _ => "{}",
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
