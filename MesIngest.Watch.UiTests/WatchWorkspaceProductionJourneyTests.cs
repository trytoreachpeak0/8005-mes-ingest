using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;
using FlaUIApplication = FlaUI.Core.Application;

namespace MesIngest.Watch.UiTests;

/// <summary>
/// Golden-machine preview for the production tickets 19-22 workspace. The
/// child process is the real MesIngest.Watch executable; only its loopback
/// Host boundary and per-user files are deterministic test fixtures.
/// </summary>
public sealed class WatchWorkspaceProductionJourneyTests
{
    private const string Credential = "ticket-19-22-production-preview-secret";
    private const string DemandSeriesId = "SERIES-PREVIEW-20";
    private const string DemandSnapshotReference = "demand-preview-snapshot-20";
    private const string AuditSnapshotReference = "audit-preview-snapshot-21";
    private const string ErrorSnapshotReference = "error-search-snapshot-22";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    [Trait("Category", "watch-ui-journeys")]
    public async Task Operator_reviews_the_complete_production_workspace_and_records_the_shared_preview()
    {
        Assert.SkipUnless(
            string.Equals(
                Environment.GetEnvironmentVariable("MESINGEST_WATCH_RUN_REAL_WINDOWS"),
                "1",
                StringComparison.Ordinal),
            "Run through Invoke-WatchUiTests.ps1 so desktop checks and serial execution are enforced.");

        var cancellationToken = TestContext.Current.CancellationToken;
        ErrorSearchQuery? latestErrorQuery = null;
        var scenario = CreateScenario(query => latestErrorQuery = query);
        await using var host = await ScriptedFakeHost.StartV2Async(scenario, cancellationToken);

        var artifactRoot = WatchWindowJourneyTests.ResolveArtifactRoot();
        var journeyName = "production-workspace-19-22";
        var runtimeRoot = Path.Combine(artifactRoot, "runtime", journeyName);
        var logDirectory = Path.Combine(runtimeRoot, "logs");
        var localAppData = Path.Combine(runtimeRoot, "local-app-data");
        Directory.CreateDirectory(logDirectory);
        PrepareAreaProfile(localAppData);

        var evidence = new WatchJourneyEvidence(
            artifactRoot,
            journeyName,
            [Credential]);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        evidence.RecordEnvironment(
            WatchWindowJourneyTests.FormatEnvironment(WatchVisualEnvironment.Capture()));

        var startInfo = new ProcessStartInfo
        {
            FileName = WatchWindowJourneyTests.ResolveWatchExecutable(),
            WorkingDirectory = Path.GetDirectoryName(
                WatchWindowJourneyTests.ResolveWatchExecutable())!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
        };
        startInfo.Environment["LOCALAPPDATA"] = localAppData;
        startInfo.Environment["MesIngestWatch__BaseUrl"] = host.BaseUrl;
        startInfo.Environment["MesIngestWatch__SharedSecret"] = Credential;
        startInfo.Environment["MesIngestWatch__RequestTimeoutSeconds"] = "30";
        startInfo.Environment["MesIngestWatch__RenderingMode"] = "SoftwareOnly";
        startInfo.Environment["MesIngestWatch__LogDirectory"] = logDirectory;
        startInfo.Environment["MESINGEST_WATCH_UI_TEST_MODE"] = "1";
        startInfo.Environment["MESINGEST_WATCH_UI_FIXED_UTC_NOW"] =
            "2026-08-14T05:08:00.0000000+00:00";

        using var process = Process.Start(startInfo)
            ?? throw new Xunit.Sdk.XunitException("MesIngest.Watch process did not start.");
        using var application = FlaUIApplication.Attach(process.Id);
#pragma warning disable xUnit1051 // Evidence drains must survive cancellation of the test body.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
#pragma warning restore xUnit1051
        using var automation = new UIA3Automation();
        FlaUI.Core.AutomationElements.Window? window = null;
        Exception? failure = null;
        var failedStep = "launch";
        var failureRecorded = false;

        try
        {
            window = application.GetMainWindow(automation, StepTimeout)
                ?? throw new Xunit.Sdk.XunitException(
                    "The production MesIngest.Watch window did not appear.");
            WatchWindowNative.SetClientSize(process.MainWindowHandle, 1440, 900);
            WaitUntil(
                () => FindById(window, "OverviewPage") is not null,
                "production overview UIA tree",
                StepTimeout);

            failedStep = "overview";
            WaitUntil(
                () => TextValue(FindRequiredById(window, "SeriesSummaryValue"))
                    is not "" and not "—",
                "the first committed production overview",
                StepTimeout);
            Capture(evidence, process.MainWindowHandle, "01-overview");

            failedStep = "fluent-window-chrome";
            var chromeUiaEvidence = ExerciseWindowChrome(
                window,
                automation,
                evidence,
                process.MainWindowHandle);
            WatchWindowNative.SetClientSize(process.MainWindowHandle, 1440, 900);

            failedStep = "settings";
            Navigate(window, "SettingsNavigationItem", "SettingsPage");
            WaitUntil(
                () => FindById(window, "SaveRefreshIntervalsButton") is not null,
                "production settings commands",
                StepTimeout);
            Capture(evidence, process.MainWindowHandle, "02-settings");

            failedStep = "demand-series";
            Navigate(window, "DemandSeriesNavigationItem", "DemandSeriesScrollViewer");
            var demandGrid = WaitForRows(window, "DemandSeriesGrid", "DemandSeries rows");
            demandGrid.Select(0);
            WaitForRows(window, "DemandSeriesGenerationGrid", "DemandSeries generations");
            Capture(evidence, process.MainWindowHandle, "03-demand-series-detail");

            failedStep = "readability-audit";
            Navigate(window, "ReadabilityAuditNavigationItem", "ReadabilityAuditPage");
            var auditGrid = WaitForRows(window, "ReadabilityAuditGrid", "readability rows");
            auditGrid.Select(0);
            WaitForRows(window, "ReadabilityQualificationGrid", "readability checks");
            Capture(evidence, process.MainWindowHandle, "04-readability-audit-detail");

            failedStep = "area-filter";
            Navigate(window, "AreaFilterNavigationItem", "AreaFilterPage");
            var profileList = FindRequiredById(window, "AreaProfileList").AsListBox();
            WaitUntil(
                () => profileList.Items.Length > 0,
                "the local AREA profile list",
                StepTimeout);
            profileList.Select(0);
            WaitUntil(
                () => TextValue(FindRequiredById(window, "AreaProfileAppliedStateText"))
                    .Contains("Factory-East", StringComparison.Ordinal),
                "the applied AREA profile state",
                StepTimeout);
            Capture(evidence, process.MainWindowHandle, "05-area-filter-profile");

            failedStep = "error-search";
            Navigate(window, "ErrorSearchNavigationItem", "ErrorSearchPage");
            var errorGrid = WaitForRows(window, "ErrorSearchSeriesGrid", "error Series rows");
            errorGrid.Select(0);
            WaitForRows(window, "ErrorSearchPeriodGrid", "matched error periods");
            WaitForRows(window, "ErrorSearchEvidenceGrid", "matched error evidence");
            Capture(evidence, process.MainWindowHandle, "06-error-search-variant-a");

            failedStep = "current-attention";
            Navigate(window, "CurrentAttentionNavigationItem", "CurrentAttentionPage");
            var attentionGrid = WaitForRows(
                window,
                "CurrentAttentionGrid",
                "current ingest attention rows");
            attentionGrid.Select(0);
            WaitForRows(window, "CurrentAttentionEvidenceGrid", "current attention evidence");
            Capture(evidence, process.MainWindowHandle, "07-current-ingest-attention");

            failedStep = "current-attention-error-drill";
            var drill = FindRequiredById(window, "CurrentAttentionOpenErrorSearchButton")
                .AsButton();
            WaitUntil(() => drill.IsEnabled, "Series error drill command", StepTimeout);
            drill.Invoke();
            WaitUntil(
                () => FindById(window, "ErrorSearchPage") is not null
                    && TextValue(FindRequiredById(window, "ErrorSearchNormalizedFilterText"))
                        .Contains("SERIES-ATTENTION-22", StringComparison.Ordinal),
                "explicit CurrentIngestAttention to Error Search drill",
                StepTimeout);
            Capture(evidence, process.MainWindowHandle, "08-current-attention-error-drill");
            Capture(evidence, process.MainWindowHandle, "final");

            Assert.NotNull(latestErrorQuery);
            Assert.Equal("SERIES-ATTENTION-22", latestErrorQuery.Filter.SeriesId);
            Assert.Null(latestErrorQuery.SnapshotReference);
            Assert.Null(latestErrorQuery.Cursor);
            evidence.RecordUiaTree(
                chromeUiaEvidence
                + Environment.NewLine
                + WatchWindowJourneyTests.DumpUiaTree(window, automation));
        }
        catch (Exception exception)
        {
            failure = exception;
            failureRecorded = WatchWindowJourneyTests.TryRecordFailure(
                evidence,
                failedStep,
                failure,
                StepTimeout);
            if (window is not null)
            {
                try
                {
                    TryRecordFailureWindow(
                        evidence,
                        window,
                        process.MainWindowHandle,
                        automation);
                }
                catch (Exception)
                {
                    // The original journey failure remains authoritative.
                }
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
                    // Bounded process-tree cleanup below is authoritative.
                }
            }

            try
            {
                await WatchWindowJourneyTests.WaitForExitAsync(
                    process,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                WatchWindowJourneyTests.CaptureCleanupFailure(
                    ref failure,
                    ref failedStep,
                    exception,
                    "process-cleanup");
            }

            try
            {
                evidence.RecordProcessOutput(await stdout, await stderr);
            }
            catch (Exception exception)
            {
                WatchWindowJourneyTests.CaptureCleanupFailure(
                    ref failure,
                    ref failedStep,
                    exception,
                    "process-output-evidence");
            }

            try
            {
                evidence.RecordFakeHostTimeline(
                    FormatTimeline(host.Timeline),
                    FormatTimelineSummary(host.Timeline));
            }
            catch (Exception exception)
            {
                WatchWindowJourneyTests.CaptureCleanupFailure(
                    ref failure,
                    ref failedStep,
                    exception,
                    "fake-host-evidence");
            }

            try
            {
                evidence.RecordWatchLogs(logDirectory);
            }
            catch (Exception exception)
            {
                WatchWindowJourneyTests.CaptureCleanupFailure(
                    ref failure,
                    ref failedStep,
                    exception,
                    "watch-log-evidence");
            }
        }

        if (failure is not null)
        {
            if (!failureRecorded)
            {
                WatchWindowJourneyTests.TryRecordFailure(
                    evidence,
                    failedStep,
                    failure,
                    StepTimeout);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static FakeHostV2Scenario CreateScenario(
        Action<ErrorSearchQuery> rememberErrorQuery) =>
        new("production-preview-19-22", Credential)
        {
            Overview = FakeHostReply.Return(
                WatchErrorSearchProductionIntegrationTests.CreateOverview()),
            DemandSeries = FakeHostReply.Select<
                DemandSeriesBrowseQuery,
                DemandSeriesListSnapshot>(query => FakeHostReply.Return(
                    WatchDemandSeriesProductionIntegrationTests.CreateDemandSeriesList(
                        query,
                        DemandSeriesId,
                        DemandSnapshotReference))),
            DemandSeriesDetail = FakeHostReply.Select<
                FakeHostV2DetailRequest,
                DemandSeriesDetailSnapshot>(request => FakeHostReply.Return(
                    WatchDemandSeriesProductionIntegrationTests.CreateDemandSeriesDetail(
                        request.ObjectId,
                        request.SnapshotReference))),
            ReadabilityAudit = FakeHostReply.Select<
                ReadabilityAuditQuery,
                ReadabilityAuditListSnapshot>(query => FakeHostReply.Return(
                    WatchReadabilityAuditProductionIntegrationTests.CreateAuditList(
                        query,
                        AuditSnapshotReference))),
            ReadabilityAuditDetail = FakeHostReply.Select<
                FakeHostV2DetailRequest,
                ReadabilityAuditDetailSnapshot>(request => FakeHostReply.Return(
                    WatchReadabilityAuditProductionIntegrationTests.CreateAuditDetail(
                        request.SnapshotReference))),
            ErrorSearch = FakeHostReply.Select<ErrorSearchQuery, ErrorSearchListSnapshot>(query =>
            {
                rememberErrorQuery(query);
                var item = WatchErrorSearchProductionIntegrationTests.CreateErrorItem() with
                {
                    SeriesId = query.Filter.SeriesId ?? "SERIES-ERROR-22",
                };
                return FakeHostReply.Return(
                    WatchErrorSearchProductionIntegrationTests.CreateErrorPage(
                        query,
                        ErrorSnapshotReference,
                        pageNumber: 1,
                        totalPages: 1,
                        totalSeriesCount: 1,
                        item: item));
            }),
            ErrorSearchDetail = FakeHostReply.Select<
                FakeHostV2DetailRequest,
                ErrorSearchDetailSnapshot>(_ => FakeHostReply.Return(
                    WatchErrorSearchProductionIntegrationTests.CreateErrorDetail(
                        new ErrorSearchFilter(),
                        ErrorSearchWindowKinds.Last7Days))),
            CurrentAttention = FakeHostReply.Select<
                CurrentIngestAttentionQuery,
                CurrentIngestAttentionSnapshot>(query => FakeHostReply.Return(
                    WatchCurrentAttentionProductionIntegrationTests.CreateAttentionSnapshot(
                        query))),
        };

    private static string ExerciseWindowChrome(
        FlaUI.Core.AutomationElements.Window window,
        UIA3Automation automation,
        WatchJourneyEvidence evidence,
        IntPtr windowHandle)
    {
        var windowPattern = window.Patterns.Window.Pattern;
        var minimize = FindRequiredByName(window, "最小化窗口");
        var maximize = FindRequiredByName(window, "最大化窗口");
        var close = FindRequiredByName(window, "关闭窗口");
        Assert.True(windowPattern.CanMaximize.ValueOrDefault);
        Assert.True(windowPattern.CanMinimize.ValueOrDefault);

        foreach (var button in new[] { minimize, maximize, close })
        {
            button.Focus();
            WaitUntil(
                () => button.Properties.HasKeyboardFocus.ValueOrDefault,
                $"keyboard focus for {button.Name}",
                StepTimeout);
        }

        var normalBounds = window.BoundingRectangle;
        Capture(evidence, windowHandle, "01a-chrome-normal");
        maximize.AsButton().Invoke();
        WaitUntil(
            () => windowPattern.WindowVisualState.ValueOrDefault
                == WindowVisualState.Maximized,
            "UIA maximize",
            StepTimeout);
        Capture(evidence, windowHandle, "01b-chrome-maximized", exact1440By900: false);

        FindRequiredByName(window, "还原窗口").AsButton().Invoke();
        WaitUntil(
            () => windowPattern.WindowVisualState.ValueOrDefault == WindowVisualState.Normal,
            "UIA restore",
            StepTimeout);
        WaitUntil(
            () => Math.Abs(window.BoundingRectangle.Width - normalBounds.Width) <= 2
                && Math.Abs(window.BoundingRectangle.Height - normalBounds.Height) <= 2,
            "restored window bounds",
            StepTimeout);
        Capture(evidence, windowHandle, "01c-chrome-restored");

        var restoredBounds = window.BoundingRectangle;
        var systemMenuPoint = new System.Drawing.Point(
            restoredBounds.Left + (restoredBounds.Width / 2),
            restoredBounds.Top + 24);
        Mouse.RightClick(systemMenuPoint);
        AutomationElement? systemMenu = null;
        WaitUntil(
            () =>
            {
                systemMenu = FindVisibleSystemMenu(automation);
                return systemMenu is not null;
            },
            "caption right-click system menu",
            StepTimeout);
        var systemMenuItems = systemMenu!
            .FindAllDescendants(
                automation.ConditionFactory.ByControlType(ControlType.MenuItem))
            .Select(item => item.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        Assert.Contains(
            systemMenuItems,
            name => name.Contains("还原", StringComparison.Ordinal)
                || name.Contains("关闭", StringComparison.Ordinal)
                || name.Contains("Restore", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Close", StringComparison.OrdinalIgnoreCase));
        CaptureWindowIncludingPopups(evidence, window, "01d-chrome-system-menu");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil(
            () => FindVisibleSystemMenu(automation) is null,
            "Escape to dismiss the system menu",
            StepTimeout);

        var bounds = window.BoundingRectangle;
        var captionPoint = new System.Drawing.Point(
            bounds.Left + (bounds.Width / 2),
            bounds.Top + 24);
        Mouse.LeftDoubleClick(captionPoint);
        WaitUntil(
            () => windowPattern.WindowVisualState.ValueOrDefault
                == WindowVisualState.Maximized,
            "caption double-click maximize",
            StepTimeout);
        bounds = window.BoundingRectangle;
        Mouse.LeftDoubleClick(new System.Drawing.Point(
            bounds.Left + (bounds.Width / 2),
            bounds.Top + 24));
        WaitUntil(
            () => windowPattern.WindowVisualState.ValueOrDefault == WindowVisualState.Normal,
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
            () => windowPattern.WindowVisualState.ValueOrDefault == WindowVisualState.Minimized,
            "UIA minimize",
            StepTimeout);
        windowPattern.SetWindowVisualState(WindowVisualState.Normal);
        WaitUntil(
            () => windowPattern.WindowVisualState.ValueOrDefault == WindowVisualState.Normal,
            "UIA restore after minimize",
            StepTimeout);

        return "CAPTION_SYSTEM_MENU_UIA"
            + Environment.NewLine
            + string.Join(Environment.NewLine, systemMenuItems.Select(name => $"menuItem={name}"));
    }

    private static AutomationElement? FindVisibleSystemMenu(UIA3Automation automation) =>
        automation.GetDesktop()
            .FindAllDescendants(
                automation.ConditionFactory.ByControlType(ControlType.Menu))
            .FirstOrDefault(menu => !menu.Properties.IsOffscreen.ValueOrDefault
                && menu.FindAllDescendants(
                        automation.ConditionFactory.ByControlType(ControlType.MenuItem))
                    .Any(item => item.Name.Contains("还原", StringComparison.Ordinal)
                        || item.Name.Contains("关闭", StringComparison.Ordinal)
                        || item.Name.Contains("Restore", StringComparison.OrdinalIgnoreCase)
                        || item.Name.Contains("Close", StringComparison.OrdinalIgnoreCase)));

    private static void Navigate(
        FlaUI.Core.AutomationElements.Window window,
        string navigationAutomationId,
        string pageAutomationId)
    {
        var navigation = FindRequiredById(window, navigationAutomationId);
        navigation.Focus();
        navigation.Click();
        WaitUntil(
            () => FindById(window, pageAutomationId) is { } page
                && !page.Properties.IsOffscreen.ValueOrDefault,
            $"visible page {pageAutomationId}",
            StepTimeout);
    }

    private static Grid WaitForRows(
        FlaUI.Core.AutomationElements.Window window,
        string automationId,
        string description)
    {
        Grid? grid = null;
        WaitUntil(
            () =>
            {
                grid = FindById(window, automationId)?.AsGrid();
                return grid?.Rows.Length > 0;
            },
            description,
            StepTimeout);
        return grid!;
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

    private static AutomationElement FindRequiredByName(
        FlaUI.Core.AutomationElements.Window window,
        string automationName) =>
        window.FindFirstDescendant(window.ConditionFactory.ByName(automationName))
        ?? throw new Xunit.Sdk.XunitException($"UIA element not found by name: {automationName}");

    private static string TextValue(AutomationElement element) => string.Join(
        Environment.NewLine,
        element.Properties.Name.ValueOrDefault ?? string.Empty,
        element.Properties.HelpText.ValueOrDefault ?? string.Empty,
        element.Properties.ItemStatus.ValueOrDefault ?? string.Empty);

    private static void WaitUntil(
        Func<bool> condition,
        string description,
        TimeSpan timeout)
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
            catch (Exception exception)
            {
                lastException = exception;
            }

            Thread.Sleep(100);
        }

        throw new Xunit.Sdk.XunitException(
            lastException is null
                ? $"Timed out after {timeout.TotalSeconds:0.#}s waiting for {description}."
                : $"Timed out after {timeout.TotalSeconds:0.#}s waiting for {description}. "
                  + $"Last error: {lastException.Message}");
    }

    private static void Capture(
        WatchJourneyEvidence evidence,
        IntPtr windowHandle,
        string step,
        bool exact1440By900 = true)
    {
        WatchWindowNative.MovePointerOffWindow();
        Thread.Sleep(250);
        evidence.RecordStep(
            step,
            exact1440By900
                ? WatchWindowNative.CaptureClientArea(windowHandle)
                : WatchWindowNative.CaptureClientAreaAtCurrentSize(windowHandle));
    }

    private static void CaptureWindowIncludingPopups(
        WatchJourneyEvidence evidence,
        FlaUI.Core.AutomationElements.Window window,
        string step)
    {
        var temporaryPath = Path.Combine(evidence.DirectoryPath, $".{step}.capture.png");
        try
        {
            window.CaptureToFile(temporaryPath);
            evidence.RecordStep(step, File.ReadAllBytes(temporaryPath));
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void TryRecordFailureWindow(
        WatchJourneyEvidence evidence,
        FlaUI.Core.AutomationElements.Window window,
        IntPtr windowHandle,
        UIA3Automation automation)
    {
        try
        {
            evidence.RecordStep(
                "failure",
                WatchWindowNative.CaptureClientAreaAtCurrentSize(windowHandle));
        }
        catch (Exception captureFailure)
        {
            evidence.RecordUiaTree($"Screenshot capture failed: {captureFailure.Message}");
        }

        try
        {
            evidence.RecordUiaTree(
                WatchWindowJourneyTests.DumpUiaTree(window, automation));
        }
        catch (Exception treeFailure)
        {
            evidence.RecordUiaTree($"UIA tree capture failed: {treeFailure.Message}");
        }
    }

    private static void PrepareAreaProfile(string localAppData)
    {
        var directory = Path.Combine(localAppData, "MesIngest.Watch", "area-filters");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "Factory-East.txt"),
            "# Shared preview\nA1-1\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            Path.Combine(directory, ".active-profile"),
            "{\"version\":1,\"profileName\":\"Factory-East\","
            + "\"mesAreas\":[\"A1-1\"],"
            + "\"appliedAt\":\"2026-08-14T05:00:00+00:00\"}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static IReadOnlyList<string> FormatTimeline(
        IReadOnlyList<FakeHostRequestEvent> timeline) => timeline
        .Select(entry => string.Join(
            ' ',
            entry.Sequence.ToString("D4", CultureInfo.InvariantCulture),
            $"session={entry.SessionId}",
            $"operation={entry.Operation}",
            $"state={entry.State}",
            $"endpoint={RedactedEndpointShape(entry.Operation)}"))
        .ToArray();

    internal static string FormatTimelineSummary(
        IReadOnlyList<FakeHostRequestEvent> timeline) => string.Join(
        Environment.NewLine,
        timeline
            .GroupBy(entry => (entry.SessionId, entry.Operation, entry.State))
            .OrderBy(group => group.Key.SessionId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Operation)
            .ThenBy(group => group.Key.State)
            .Select(group =>
                $"session={group.Key.SessionId} operation={group.Key.Operation} "
                + $"state={group.Key.State} count="
                + group.Count().ToString(CultureInfo.InvariantCulture)));

    private static string RedactedEndpointShape(FakeHostOperation operation) => operation switch
    {
        FakeHostOperation.Contract => "/api/contract",
        FakeHostOperation.ContractV2 => "/api/v2/contract",
        FakeHostOperation.OverviewV2 => "/api/v2/watch-overview{?redacted-query}",
        FakeHostOperation.DemandSeriesV2 => "/api/v2/demand-series{?redacted-query}",
        FakeHostOperation.DemandSeriesDetailV2 =>
            "/api/v2/demand-series/{seriesId}{?redacted-query}",
        FakeHostOperation.ReadabilityAuditV2 =>
            "/api/v2/readability-audit{?redacted-query}",
        FakeHostOperation.ReadabilityAuditDetailV2 =>
            "/api/v2/readability-audit/{demandId}{?redacted-query}",
        FakeHostOperation.ErrorSearchV2 => "/api/v2/error-search{?redacted-query}",
        FakeHostOperation.ErrorSearchDetailV2 =>
            "/api/v2/error-search/{seriesId}{?redacted-query}",
        FakeHostOperation.ErrorSearchRawEvidenceV2 =>
            "/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations{?redacted-query}",
        FakeHostOperation.CurrentAttentionV2 =>
            "/api/v2/current-ingest-attention{?redacted-query}",
        FakeHostOperation.PollHealth => "/api/poll-health",
        FakeHostOperation.Snapshot => "watch-snapshot",
        FakeHostOperation.DemandPage => "/api/demands{?redacted-query}",
        FakeHostOperation.AlertPage => "/api/alerts{?redacted-query}",
        FakeHostOperation.ExactDemand => "/api/demands/{demandId}",
        _ => "(redacted-endpoint)",
    };
}
