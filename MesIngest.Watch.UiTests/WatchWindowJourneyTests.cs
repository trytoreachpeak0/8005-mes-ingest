using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using MesIngest.Watch;
using FlaUIApplication = FlaUI.Core.Application;

namespace MesIngest.Watch.UiTests;

public sealed class WatchWindowJourneyTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(12);

    [Theory]
    [InlineData("cold-start-overview")]
    [InlineData("visible-gone-paging-details")]
    [InlineData("alert-to-demand")]
    [InlineData("slow-request-cancel")]
    [InlineData("offline-reconnect")]
    [Trait("Category", "watch-window-visual")]
    public async Task Operator_completes_high_value_real_window_journey(string journeyName)
    {
        await RunJourneyAsync(journeyName);
    }

    [Fact]
    [Trait("Category", "watch-window-nonbaseline")]
    public async Task Fluent_window_chrome_supports_keyboard_uia_double_click_and_drag()
    {
        await RunJourneyAsync("fluent-window-chrome");
    }

    private static async Task RunJourneyAsync(string journeyName)
    {
        Assert.SkipUnless(
            string.Equals(
                Environment.GetEnvironmentVariable("MESINGEST_WATCH_RUN_REAL_WINDOWS"),
                "1",
                StringComparison.Ordinal),
            "Run through Invoke-WatchUiTests.ps1 so desktop checks and serial execution are enforced.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(journeyName);
        await using var host = await WatchWindowFakeHost.StartAsync(
            fixture.Scenario,
            ShouldUseDeterministicVisualInputs() ? "http://127.0.0.1:51542" : null,
            cancellationToken);
        var root = ResolveArtifactRoot();
        var testRoot = Path.Combine(root, "runtime", journeyName);
        var logDirectory = Path.Combine(testRoot, "logs");
        var localAppData = Path.Combine(testRoot, "local-app-data");
        Directory.CreateDirectory(logDirectory);
        Directory.CreateDirectory(localAppData);
        var evidence = new WatchJourneyEvidence(
            root,
            journeyName,
            fixture.SensitiveValues.Append("fake-window-secret"));
        // Match the launched Watch process before recording the auditable environment contract.
        // The child also receives MesIngestWatch__RenderingMode=SoftwareOnly below.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        evidence.RecordEnvironment(FormatEnvironment(WatchVisualEnvironment.Capture()));

        var watchExecutable = ResolveWatchExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = watchExecutable,
            WorkingDirectory = Path.GetDirectoryName(watchExecutable)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
        };
        startInfo.Environment["LOCALAPPDATA"] = localAppData;
        startInfo.Environment["MesIngestWatch__BaseUrl"] = host.BaseUrl;
        startInfo.Environment["MesIngestWatch__SharedSecret"] = "fake-window-secret";
        startInfo.Environment["MesIngestWatch__RequestTimeoutSeconds"] = "30";
        startInfo.Environment["MesIngestWatch__RenderingMode"] = "SoftwareOnly";
        startInfo.Environment["MesIngestWatch__LogDirectory"] = logDirectory;
        if (ShouldUseDeterministicVisualInputs())
        {
            // Pixel journeys freeze display time and disable transient banner holds.
            // The separate UIA suite runs with the production clock and animations.
            startInfo.Environment["MESINGEST_WATCH_UI_TEST_MODE"] = "1";
            startInfo.Environment["MESINGEST_WATCH_UI_FIXED_UTC_NOW"] = "2026-08-08T01:30:00.0000000+00:00";
        }

        using var process = Process.Start(startInfo)
            ?? throw new Xunit.Sdk.XunitException("MesIngestWatch process did not start.");
        using var application = FlaUIApplication.Attach(process.Id);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var automation = new UIA3Automation();
        FlaUI.Core.AutomationElements.Window? window = null;
        Exception? failure = null;
        var failedStep = "launch";
        var failureRecorded = false;
        byte[]? finalCapture = null;

        try
        {
            window = application.GetMainWindow(automation, StepTimeout)
                ?? throw new Xunit.Sdk.XunitException("MesIngestWatch main window did not appear.");
            WatchWindowNative.SetClientSize(process.MainWindowHandle, 1440, 900);
            WaitUntil(
                () => FindById(window, "OverviewRefreshButton") is not null,
                "main window UIA tree",
                StepTimeout);
            RecordStep(evidence, window, "window-started");
            WaitUntil(
                () => FindById(window, "ErrorBannerText") is not { } error
                    || !DynamicText(error).Contains("Not ready", StringComparison.Ordinal),
                "initial projection to replace the startup placeholder",
                StepTimeout);

            failedStep = journeyName;
            switch (journeyName)
            {
                case "cold-start-overview":
                    RunColdStartOverview(window);
                    break;
                case "visible-gone-paging-details":
                    RunVisibleGonePaging(window);
                    break;
                case "alert-to-demand":
                    RunAlertToDemand(application, automation, window, fixture.PrimaryDemandId!);
                    break;
                case "slow-request-cancel":
                    RunSlowRequestCancel(window, fixture.PrimaryDemandId!);
                    break;
                case "offline-reconnect":
                    RunOfflineReconnect(window, host);
                    break;
                case "fluent-window-chrome":
                    RunFluentWindowChrome(window, evidence, process.MainWindowHandle);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(journeyName), journeyName, null);
            }

            NormalizeFinalVisualState(window);
            finalCapture = WatchWindowNative.CaptureClientArea(process.MainWindowHandle);
            evidence.RecordStep("final", finalCapture);
            evidence.RecordUiaTree(DumpUiaTree(window, automation));

            if (ShouldCompareWindowBaselines())
            {
                WatchWindowBaseline.Verify(journeyName, finalCapture, evidence);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            failureRecorded = TryRecordFailure(evidence, failedStep, failure, StepTimeout);
            if (window is not null)
            {
                TryRecordFailureWindow(evidence, window, process.MainWindowHandle, automation);
            }
        }
        finally
        {
            if (!process.HasExited && window is not null)
            {
                try
                {
                    window.Close();
                }
                catch (Exception)
                {
                    // WaitForExitAsync below is the authoritative bounded cleanup.
                }
            }

            try
            {
                await WaitForExitAsync(process, cancellationToken);
            }
            catch (Exception ex)
            {
                CaptureCleanupFailure(ref failure, ref failedStep, ex, "process-cleanup");
            }

            try
            {
                evidence.RecordProcessOutput(await stdout, await stderr);
            }
            catch (Exception ex)
            {
                CaptureCleanupFailure(ref failure, ref failedStep, ex, "process-output-evidence");
            }

            try
            {
                evidence.RecordFakeHostTimeline(host.Timeline, host.TimelineSummary);
            }
            catch (Exception ex)
            {
                CaptureCleanupFailure(ref failure, ref failedStep, ex, "fake-host-evidence");
            }

            try
            {
                evidence.RecordWatchLogs(logDirectory);
            }
            catch (Exception ex)
            {
                CaptureCleanupFailure(ref failure, ref failedStep, ex, "watch-log-evidence");
            }
        }

        if (failure is not null)
        {
            if (!failureRecorded)
            {
                TryRecordFailure(evidence, failedStep, failure, StepTimeout);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void RunColdStartOverview(FlaUI.Core.AutomationElements.Window window)
    {
        WaitUntil(
            () => DynamicText(FindRequiredById(window, "OverviewConclusionText"))
                .Contains("健康", StringComparison.Ordinal),
            "healthy cold-start overview",
            StepTimeout);
        WaitUntil(
            () => FindById(window, "ErrorBannerText") is null,
            "cold-start error banner cleared",
            StepTimeout);
        Assert.Contains(
            "已连接",
            DynamicText(FindRequiredById(window, "OverviewHostText")),
            StringComparison.Ordinal);
        Assert.Contains(
            "SUCCESS",
            DynamicText(FindRequiredById(window, "OverviewPollHealthText")),
            StringComparison.Ordinal);
    }

    private static void RunFluentWindowChrome(
        FlaUI.Core.AutomationElements.Window window,
        WatchJourneyEvidence evidence,
        IntPtr windowHandle)
    {
        RunColdStartOverview(window);
        var windowPattern = window.Patterns.Window.Pattern;
        var minimize = FindRequiredById(window, "TitleBarMinimizeButton");
        var maximize = FindRequiredById(window, "TitleBarMaximizeButton");
        var close = FindRequiredById(window, "TitleBarCloseButton");
        Assert.Equal("最小化窗口", minimize.Name);
        Assert.Equal("最大化窗口", maximize.Name);
        Assert.Equal("关闭窗口", close.Name);
        Assert.True(windowPattern.CanMaximize.ValueOrDefault);
        Assert.True(windowPattern.CanMinimize.ValueOrDefault);

        foreach (var button in new[] { minimize, maximize, close })
        {
            button.Focus();
            WaitUntil(
                () => button.Properties.HasKeyboardFocus.ValueOrDefault,
                $"keyboard focus for {button.AutomationId}",
                StepTimeout);
        }

        var normalBounds = window.BoundingRectangle;
        evidence.RecordStep(
            "chrome-normal",
            WatchWindowNative.CaptureClientAreaAtCurrentSize(windowHandle));
        maximize.AsButton().Invoke();
        WaitUntil(
            () => windowPattern.WindowVisualState.ValueOrDefault
                == FlaUI.Core.Definitions.WindowVisualState.Maximized,
            "UIA maximize",
            StepTimeout);
        WaitUntil(
            () => string.Equals(maximize.Name, "还原窗口", StringComparison.Ordinal),
            "restore automation name",
            StepTimeout);
        WaitUntil(
            () => window.BoundingRectangle.Width >= normalBounds.Width + 300,
            "maximized window bounds",
            StepTimeout);
        Thread.Sleep(250);
        evidence.RecordStep(
            "chrome-maximized",
            WatchWindowNative.CaptureClientAreaAtCurrentSize(windowHandle));

        maximize.AsButton().Invoke();
        WaitUntil(
            () => windowPattern.WindowVisualState.ValueOrDefault
                == FlaUI.Core.Definitions.WindowVisualState.Normal,
            "UIA restore",
            StepTimeout);
        WaitUntil(
            () => Math.Abs(window.BoundingRectangle.Width - normalBounds.Width) <= 2
                && Math.Abs(window.BoundingRectangle.Height - normalBounds.Height) <= 2,
            "restored window bounds",
            StepTimeout);
        Thread.Sleep(250);
        evidence.RecordStep(
            "chrome-restored",
            WatchWindowNative.CaptureClientAreaAtCurrentSize(windowHandle));

        var bounds = window.BoundingRectangle;
        var captionPoint = new System.Drawing.Point(
            bounds.Left + (bounds.Width / 2),
            bounds.Top + 24);
        Mouse.LeftDoubleClick(captionPoint);
        WaitUntil(
            () => windowPattern.WindowVisualState.ValueOrDefault
                == FlaUI.Core.Definitions.WindowVisualState.Maximized,
            "caption double-click maximize",
            StepTimeout);
        bounds = window.BoundingRectangle;
        Mouse.LeftDoubleClick(new System.Drawing.Point(
            bounds.Left + (bounds.Width / 2),
            bounds.Top + 24));
        WaitUntil(
            () => windowPattern.WindowVisualState.ValueOrDefault
                == FlaUI.Core.Definitions.WindowVisualState.Normal,
            "caption double-click restore",
            StepTimeout);

        var beforeDrag = window.BoundingRectangle;
        captionPoint = new System.Drawing.Point(
            beforeDrag.Left + (beforeDrag.Width / 2),
            beforeDrag.Top + 24);
        Mouse.Drag(
            captionPoint,
            new System.Drawing.Point(captionPoint.X + 80, captionPoint.Y + 50),
            MouseButton.Left);
        WaitUntil(
            () => Math.Abs(window.BoundingRectangle.Left - beforeDrag.Left) >= 40
                && Math.Abs(window.BoundingRectangle.Top - beforeDrag.Top) >= 20,
            "caption mouse drag",
            StepTimeout);

        minimize.AsButton().Invoke();
        WaitUntil(
            () => windowPattern.WindowVisualState.ValueOrDefault
                == FlaUI.Core.Definitions.WindowVisualState.Minimized,
            "UIA minimize",
            StepTimeout);
        windowPattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Normal);
        WaitUntil(
            () => windowPattern.WindowVisualState.ValueOrDefault
                == FlaUI.Core.Definitions.WindowVisualState.Normal,
            "UIA restore after minimize",
            StepTimeout);
    }

    private static void RunVisibleGonePaging(FlaUI.Core.AutomationElements.Window window)
    {
        FindRequiredById(window, "PrimaryNavigation").AsListBox().Select(1);
        var grid = FindRequiredById(window, "DemandsGrid").AsGrid();
        WaitUntil(() => grid.Rows.Length == 1, "VISIBLE first page", StepTimeout);
        grid.Select(0);
        Assert.False(FindRequiredById(window, "DemandDetailsPanel").Properties.IsOffscreen.ValueOrDefault);

        var next = FindRequiredById(window, "DemandNextButton").AsButton();
        WaitUntil(() => next.IsEnabled, "VISIBLE next page enabled", StepTimeout);
        next.Invoke();
        WaitUntil(
            () => DynamicText(FindRequiredById(window, "DemandPageText"))
                .Contains("第 2 页", StringComparison.Ordinal),
            "VISIBLE second page",
            StepTimeout);
        grid.Select(0);

        FindRequiredById(window, "DemandStatusTabs").AsTab().SelectTabItem(1);
        WaitUntil(
            () => DynamicText(FindRequiredById(window, "DemandModeText"))
                .Contains("GONE", StringComparison.Ordinal),
            "GONE independent view",
            StepTimeout);
        WaitUntil(() => grid.Rows.Length == 1, "GONE page", StepTimeout);
        grid.Select(0);
        Assert.False(FindRequiredById(window, "DemandDetailsPanel").Properties.IsOffscreen.ValueOrDefault);
    }

    private static void RunAlertToDemand(
        FlaUIApplication application,
        UIA3Automation automation,
        FlaUI.Core.AutomationElements.Window mainWindow,
        string expectedDemandId)
    {
        FindRequiredById(mainWindow, "PrimaryNavigation").AsListBox().Select(2);
        var alertGrid = FindRequiredById(mainWindow, "AlertsGrid").AsGrid();
        WaitUntil(() => alertGrid.Rows.Length == 1, "active IngestAlert page", StepTimeout);
        alertGrid.Select(0);
        FindRequiredById(mainWindow, "AlertDetailsButton").AsButton().Invoke();

        AutomationElement? locateButton = null;
        WaitUntil(
            () =>
            {
                locateButton = automation.GetDesktop().FindFirstDescendant(
                    automation.ConditionFactory.ByAutomationId("LocateDemandButton"));
                return locateButton is not null;
            },
            "IngestAlert detail window",
            StepTimeout);
        var detailWindow = FindOwningWindow(locateButton!, automation);
        locateButton!.AsButton().Invoke();

        var demandGrid = FindRequiredById(mainWindow, "DemandsGrid").AsGrid();
        WaitUntil(
            () => demandGrid.Rows.Any(row =>
                string.Equals(row.Name, expectedDemandId, StringComparison.Ordinal)),
            "exact Alert to TransportDemand navigation",
            StepTimeout);
        detailWindow!.Close();
        WaitUntil(
            () => FindRequiredById(mainWindow, "DemandDetailsPanel")
                .Properties.IsOffscreen.ValueOrDefault == false,
            "located TransportDemand details",
            StepTimeout);
    }

    private static void RunSlowRequestCancel(
        FlaUI.Core.AutomationElements.Window window,
        string expectedDemandId)
    {
        FindRequiredById(window, "PrimaryNavigation").AsListBox().Select(1);
        var grid = FindRequiredById(window, "DemandsGrid").AsGrid();
        WaitUntil(
            () => grid.Rows.Any(row => string.Equals(row.Name, expectedDemandId, StringComparison.Ordinal)),
            "initial VISIBLE result",
            StepTimeout);
        FindRequiredById(window, "DemandRefreshButton").AsButton().Invoke();
        var cancel = FindRequiredById(window, "DemandCancelButton").AsButton();
        WaitUntil(() => cancel.IsEnabled, "slow refresh cancellation affordance", StepTimeout);
        cancel.Invoke();
        WaitUntil(() => !cancel.IsEnabled, "slow refresh canceled", StepTimeout);
        WaitUntil(
            () => DynamicText(FindRequiredById(window, "DemandNoticeText"))
                .Contains("取消", StringComparison.Ordinal),
            "cancellation result notice",
            StepTimeout);
        WaitUntil(
            () => FindById(window, "ErrorBannerText") is null,
            "user cancellation not to become a Host error",
            StepTimeout);
        Assert.Contains(
            grid.Rows,
            row => string.Equals(row.Name, expectedDemandId, StringComparison.Ordinal));
    }

    private static void NormalizeFinalVisualState(
        FlaUI.Core.AutomationElements.Window window)
    {
        var focusTarget = new[]
            {
                "OverviewRefreshButton",
                "DemandRefreshButton",
                "AlertRefreshButton",
            }
            .Select(id => FindById(window, id))
            .FirstOrDefault(element => element is not null
                && element.Properties.IsEnabled.ValueOrDefault
                && !element.Properties.IsOffscreen.ValueOrDefault)
            ?? throw new Xunit.Sdk.XunitException(
                "The current Watch view has no visible refresh button for canonical screenshot focus.");

        focusTarget.Focus();
        WaitUntil(
            () => focusTarget.Properties.HasKeyboardFocus.ValueOrDefault,
            "canonical final screenshot focus",
            StepTimeout);
        WatchWindowNative.MovePointerOffWindow();
        Thread.Sleep(250);
    }

    private static void RunOfflineReconnect(
        FlaUI.Core.AutomationElements.Window window,
        WatchWindowFakeHost host)
    {
        RunColdStartOverview(window);
        host.SetOnline(false);
        FindRequiredById(window, "OverviewRefreshButton").AsButton().Invoke();
        WaitUntil(
            () => DynamicText(FindRequiredById(window, "ErrorBannerText"))
                .Contains("503", StringComparison.Ordinal),
            "offline error banner",
            StepTimeout);

        host.SetOnline(true);
        var refresh = FindRequiredById(window, "OverviewRefreshButton").AsButton();
        WaitUntil(() => refresh.IsEnabled, "offline refresh completed", StepTimeout);
        refresh.Invoke();
        WaitUntil(
            () => DynamicText(FindRequiredById(window, "OverviewConclusionText"))
                .Contains("健康", StringComparison.Ordinal),
            "reconnected healthy overview",
            StepTimeout);
        WaitUntil(
            () => FindById(window, "ErrorBannerText") is null,
            "recovered connection banner cleared",
            StepTimeout);
    }

    private static WatchWindowJourneyFixture CreateFixture(string journeyName)
    {
        var first = WatchWindowScenarioData.VisibleDemand("journey-visible-001");
        var second = WatchWindowScenarioData.VisibleDemand("journey-visible-002") with
        {
            TaskType = "STAGING_TO_WIRE",
            Sublot = "SLOT-LOT-002",
        };
        var gone = WatchWindowScenarioData.GoneDemand("journey-gone-001");
        var alert = WatchWindowScenarioData.ActiveAlert("journey-alert-001", first.DemandId);
        var visibleFirstPage = new WatchDemandPage([first], "journey-visible-cursor", true);
        var visibleSecondPage = new WatchDemandPage([second], null, false);
        var gonePage = new WatchDemandPage([gone], null, false);

        var scenario = journeyName switch
        {
            "visible-gone-paging-details" => WatchWindowScenario.Scripted(
                [
                    new WatchWindowHttpReply<WatchDemandPage>(visibleFirstPage),
                    new WatchWindowHttpReply<WatchDemandPage>(visibleFirstPage),
                    new WatchWindowHttpReply<WatchDemandPage>(visibleSecondPage),
                ],
                [new WatchWindowHttpReply<WatchDemandPage>(gonePage)],
                [new WatchWindowHttpReply<WatchAlertPage>(new WatchAlertPage([], null, false))]),
            "slow-request-cancel" => WatchWindowScenario.Scripted(
                [
                    new WatchWindowHttpReply<WatchDemandPage>(new WatchDemandPage([first], null, false)),
                    new WatchWindowHttpReply<WatchDemandPage>(new WatchDemandPage([first], null, false)),
                    new WatchWindowHttpReply<WatchDemandPage>(
                        new WatchDemandPage([second], null, false),
                        TimeSpan.FromSeconds(30)),
                ],
                [new WatchWindowHttpReply<WatchDemandPage>(gonePage)],
                [new WatchWindowHttpReply<WatchAlertPage>(new WatchAlertPage([], null, false))]),
            "alert-to-demand" => WatchWindowScenario.Healthy(
                visiblePages: [new WatchDemandPage([first], null, false)],
                gonePages: [gonePage],
                alerts: [alert]),
            _ => WatchWindowScenario.Healthy(
                visiblePages: [new WatchDemandPage([first], null, false)],
                gonePages: [gonePage],
                alerts: []),
        };

        return new WatchWindowJourneyFixture(
            scenario,
            first.DemandId,
            [first.DemandId, second.DemandId, gone.DemandId, alert.AlertId!, "journey-visible-cursor"]);
    }

    private static AutomationElement FindRequiredById(
        FlaUI.Core.AutomationElements.Window window,
        string automationId) =>
        FindById(window, automationId)
        ?? throw new Xunit.Sdk.XunitException($"UIA element not found: {automationId}");

    private static AutomationElement? FindById(
        FlaUI.Core.AutomationElements.Window window,
        string automationId) =>
        window.FindFirstDescendant(window.ConditionFactory.ByAutomationId(automationId));

    private static string DynamicText(AutomationElement element) => string.Join(
        Environment.NewLine,
        element.Properties.HelpText.ValueOrDefault ?? string.Empty,
        element.Properties.ItemStatus.ValueOrDefault ?? string.Empty);

    private static void WaitUntil(Func<bool> condition, string description, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? lastException = null;
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            Thread.Sleep(100);
        }

        throw new Xunit.Sdk.XunitException(
            lastException is null
                ? $"Timed out after {timeout.TotalSeconds:0.#}s waiting for {description}."
                : $"Timed out after {timeout.TotalSeconds:0.#}s waiting for {description}. Last error: {lastException.Message}");
    }

    private static void RecordStep(
        WatchJourneyEvidence evidence,
        FlaUI.Core.AutomationElements.Window window,
        string step)
    {
        var path = Path.Combine(evidence.DirectoryPath, $"{step}.capture.png");
        window.CaptureToFile(path);
        evidence.RecordStep(step, File.ReadAllBytes(path));
        File.Delete(path);
    }

    private static void TryRecordFailureWindow(
        WatchJourneyEvidence evidence,
        FlaUI.Core.AutomationElements.Window window,
        IntPtr handle,
        UIA3Automation automation)
    {
        try
        {
            evidence.RecordStep("failure", WatchWindowNative.CaptureClientArea(handle));
        }
        catch (Exception captureFailure)
        {
            evidence.RecordUiaTree($"Screenshot capture failed: {captureFailure.Message}");
        }

        try
        {
            evidence.RecordUiaTree(DumpUiaTree(window, automation));
        }
        catch (Exception treeFailure)
        {
            evidence.RecordUiaTree($"UIA tree capture failed: {treeFailure.Message}");
        }
    }

    internal static string DumpUiaTree(
        AutomationElement root,
        UIA3Automation automation)
    {
        var output = new StringBuilder();
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var remaining = 5000;

        void Append(AutomationElement element, int depth)
        {
            if (remaining-- <= 0)
            {
                output.AppendLine("... UIA tree truncated at 5000 elements ...");
                return;
            }

            output.Append(' ', depth * 2)
                .Append(SafeProperty(() => element.ControlType.ToString()))
                .Append(" id=").Append(SafeProperty(() => element.Properties.AutomationId.ValueOrDefault))
                .Append(" name=").Append(SafeProperty(() => element.Properties.Name.ValueOrDefault))
                .Append(" help=").Append(SafeProperty(() => element.Properties.HelpText.ValueOrDefault))
                .Append(" itemStatus=").Append(SafeProperty(() => element.Properties.ItemStatus.ValueOrDefault))
                .Append(" enabled=").Append(SafeProperty(() => element.Properties.IsEnabled.ValueOrDefault.ToString()))
                .AppendLine();
            var child = walker.GetFirstChild(element);
            while (child is not null && remaining > 0)
            {
                Append(child, depth + 1);
                child = walker.GetNextSibling(child);
            }
        }

        Append(root, 0);
        return output.ToString();
    }

    private static FlaUI.Core.AutomationElements.Window FindOwningWindow(
        AutomationElement element,
        UIA3Automation automation)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        AutomationElement? current = element;
        while (current is not null)
        {
            if (current.ControlType == FlaUI.Core.Definitions.ControlType.Window)
            {
                return current.AsWindow();
            }

            current = walker.GetParent(current);
        }

        throw new Xunit.Sdk.XunitException("LocateDemandButton has no UIA Window ancestor.");
    }

    private static string? SafeProperty(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return $"(unsupported:{ex.GetType().Name})";
        }
    }

    internal static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        OperationCanceledException? cancellation = null;
        if (!process.HasExited)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                // Bounded cleanup below is authoritative.
            }
            catch (OperationCanceledException ex)
            {
                cancellation = ex;
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        if (cancellation is not null)
        {
            ExceptionDispatchInfo.Capture(cancellation).Throw();
        }
    }

    internal static bool TryRecordFailure(
        WatchJourneyEvidence evidence,
        string step,
        Exception exception,
        TimeSpan timeout)
    {
        try
        {
            evidence.RecordFailure(step, exception, timeout);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void CaptureCleanupFailure(
        ref Exception? primaryFailure,
        ref string failedStep,
        Exception cleanupFailure,
        string cleanupStep)
    {
        if (primaryFailure is not null)
        {
            return;
        }

        primaryFailure = cleanupFailure;
        failedStep = cleanupStep;
    }

    private static bool ShouldCompareWindowBaselines() => string.Equals(
        Environment.GetEnvironmentVariable("MESINGEST_WATCH_COMPARE_WINDOW_BASELINES"),
        "1",
        StringComparison.Ordinal);

    private static bool ShouldUseDeterministicVisualInputs() =>
        ShouldCompareWindowBaselines()
        || string.Equals(
            Environment.GetEnvironmentVariable("MESINGEST_WATCH_CAPTURE_WINDOW_CANDIDATES"),
            "1",
            StringComparison.Ordinal);

    internal static string ResolveArtifactRoot()
    {
        var configured = Environment.GetEnvironmentVariable("MESINGEST_WATCH_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return Path.GetFullPath(configured);
        }

        var root = Path.Combine(Path.GetTempPath(), $"watch-window-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    internal static string ResolveWatchExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("MESINGEST_WATCH_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var explicitPath = Path.GetFullPath(configured);
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException("Configured Watch executable does not exist.", explicitPath);
            }

            return explicitPath;
        }

        var targetDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = targetDirectory.Parent?.Name
            ?? throw new InvalidOperationException("Cannot resolve UI test configuration directory.");
        var csharpDirectory = targetDirectory.Parent?.Parent?.Parent?.Parent
            ?? throw new InvalidOperationException("Cannot resolve MesIngest csharp directory.");
        var path = Path.Combine(
            csharpDirectory.FullName,
            "MesIngest.Watch",
            "bin",
            configuration,
            "net8.0-windows",
            "MesIngest.Watch.exe");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Build MesIngest.Watch before running real-window journeys.",
                path);
        }

        return path;
    }

    internal static string FormatEnvironment(WatchVisualEnvironmentSnapshot snapshot) => string.Join(
        Environment.NewLine,
        $"interactive={snapshot.HasInteractiveInputDesktop}",
        $"desktop={snapshot.DesktopWidth}x{snapshot.DesktopHeight}",
        $"dpi={snapshot.Dpi}",
        $"scale={snapshot.Dpi / 96d:P0}",
        $"lightTheme={snapshot.AppsUseLightTheme}",
        $"culture={snapshot.CultureName}",
        $"uiCulture={snapshot.UiCultureName}",
        $"timezone={snapshot.TimeZoneId}",
        $"rendering={snapshot.RenderingMode}",
        $"framework={Environment.Version}",
        $"os={Environment.OSVersion}",
        $"processArchitecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

    private sealed record WatchWindowJourneyFixture(
        WatchWindowScenario Scenario,
        string? PrimaryDemandId,
        IReadOnlyList<string> SensitiveValues);
}
