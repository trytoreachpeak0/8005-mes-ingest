using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using MesIngest.Watch;

namespace MesIngest.Tests;

[Collection("WpfDesktop")]
public class MainWindowUiAutomationTests
{
    [Fact]
    public void Fluent_title_bar_supports_uia_keyboard_double_click_and_mouse_drag()
    {
        Exception? uiFailure = null;
        MainWindow? mainWindow = null;
        Dispatcher? dispatcher = null;
        nint mainHandle = 0;
        using var ready = new ManualResetEventSlim();
        var preferencesPath = Path.Combine(
            Path.GetTempPath(),
            $"mes-ingest-watch-titlebar-ui-{Guid.NewGuid():N}.json");

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
                    layoutPreferencesPath: preferencesPath)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 120,
                    Top = 120,
                    Width = 1000,
                    Height = 700,
                };
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
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "Watch title bar UI did not become ready");
            Assert.Null(uiFailure);
            Assert.NotEqual(0, mainHandle);

            automation = new UIA3Automation();
            var window = automation.FromHandle(mainHandle).AsWindow();
            var minimize = FindRequiredButton(window, "TitleBarMinimizeButton", "最小化窗口");
            var maximize = FindRequiredButton(window, "TitleBarMaximizeButton", "最大化窗口");
            var close = FindRequiredButton(window, "TitleBarCloseButton", "关闭窗口");

            foreach (var button in new[] { minimize, maximize, close })
            {
                button.Focus();
                Assert.True(
                    SpinWait.SpinUntil(
                        () => button.Properties.HasKeyboardFocus.ValueOrDefault,
                        TimeSpan.FromSeconds(5)),
                    $"{button.AutomationId} was not keyboard reachable");
            }

            maximize.AsButton().Invoke();
            Assert.True(
                SpinWait.SpinUntil(
                    () => dispatcher!.Invoke(() => mainWindow!.WindowState == WindowState.Maximized),
                    TimeSpan.FromSeconds(5)),
                "UIA maximize did not maximize the window");
            Assert.Equal("还原窗口", maximize.Name);

            maximize.AsButton().Invoke();
            Assert.True(
                SpinWait.SpinUntil(
                    () => dispatcher!.Invoke(() => mainWindow!.WindowState == WindowState.Normal),
                    TimeSpan.FromSeconds(5)),
                "UIA restore did not restore the window");

            var titleBounds = window.BoundingRectangle;
            var captionPoint = new System.Drawing.Point(
                titleBounds.Left + (titleBounds.Width / 2),
                titleBounds.Top + 24);
            Mouse.LeftDoubleClick(captionPoint);
            Assert.True(
                SpinWait.SpinUntil(
                    () => dispatcher!.Invoke(() => mainWindow!.WindowState == WindowState.Maximized),
                    TimeSpan.FromSeconds(5)),
                "Caption double click did not maximize the window");
            Mouse.LeftDoubleClick(new System.Drawing.Point(
                window.BoundingRectangle.Left + (window.BoundingRectangle.Width / 2),
                window.BoundingRectangle.Top + 24));
            Assert.True(
                SpinWait.SpinUntil(
                    () => dispatcher!.Invoke(() => mainWindow!.WindowState == WindowState.Normal),
                    TimeSpan.FromSeconds(5)),
                "Caption double click did not restore the window");

            var before = dispatcher!.Invoke(() => new System.Windows.Point(mainWindow!.Left, mainWindow.Top));
            titleBounds = window.BoundingRectangle;
            captionPoint = new System.Drawing.Point(
                titleBounds.Left + (titleBounds.Width / 2),
                titleBounds.Top + 24);
            Mouse.Drag(
                captionPoint,
                new System.Drawing.Point(captionPoint.X + 80, captionPoint.Y + 50),
                MouseButton.Left);
            Assert.True(
                SpinWait.SpinUntil(
                    () => dispatcher.Invoke(() => Math.Abs(mainWindow!.Left - before.X) >= 40
                        && Math.Abs(mainWindow.Top - before.Y) >= 20),
                    TimeSpan.FromSeconds(5)),
                "Caption mouse drag did not move the window");

            minimize.AsButton().Invoke();
            Assert.True(
                SpinWait.SpinUntil(
                    () => dispatcher.Invoke(() => mainWindow!.WindowState == WindowState.Minimized),
                    TimeSpan.FromSeconds(5)),
                "UIA minimize did not minimize the window");
            dispatcher.Invoke(() => mainWindow!.WindowState = WindowState.Normal);
        }
        finally
        {
            automation?.Dispose();
            dispatcher?.BeginInvoke(() =>
            {
                mainWindow?.Close();
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            });
            Assert.True(uiThread.Join(TimeSpan.FromSeconds(30)), "Watch title bar UI thread did not stop");

            if (File.Exists(preferencesPath))
            {
                File.Delete(preferencesPath);
            }
        }

        Assert.Null(uiFailure);
    }

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
            var navigation = window.FindFirstDescendant(
                    condition => condition.ByAutomationId("PrimaryNavigation"))
                ?.AsListBox()
                ?? throw new InvalidOperationException("Primary navigation did not appear");
            navigation.Items[2].Select();
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

    private static AutomationElement FindRequiredButton(
        FlaUI.Core.AutomationElements.Window window,
        string automationId,
        string expectedName)
    {
        var button = window.FindFirstDescendant(condition => condition.ByAutomationId(automationId))
            ?? throw new InvalidOperationException($"Title bar button did not appear: {automationId}");
        Assert.Equal(expectedName, button.Name);
        Assert.Equal(FlaUI.Core.Definitions.ControlType.Button, button.ControlType);
        Assert.True(button.IsEnabled);
        return button;
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
