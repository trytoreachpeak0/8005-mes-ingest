using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
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
    private const string PreviewErrorSeriesId = "SERIES-ATTENTION-22";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);
    private static readonly DateTimeOffset PreviewErrorAsOf =
        DateTimeOffset.Parse("2026-08-14T07:08:10Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Error_detail_fixture_tracks_the_committed_query_and_requested_identity()
    {
        var query = new ErrorSearchQuery(
            new ErrorSearchFilter
            {
                Categories = ["DATA_COMPLETENESS"],
                ErrorCodes = ["REQUIRED_MES_FIELD_MISSING"],
                ActivityStates = [ErrorSearchActivityStates.Active],
                SeriesId = "SERIES-ATTENTION-22",
            },
            ErrorSearchWindowSelection.Last7Days);
        var request = new FakeHostV2DetailRequest(
            "SERIES-ATTENTION-22",
            ErrorSnapshotReference);

        var detail = CreateJourneyErrorDetail(query, request);
        var normalizedFilter = query.Filter.Normalize();

        Assert.Equal(request.ObjectId, detail.Series.SeriesId);
        Assert.Equal(request.SnapshotReference, detail.SnapshotReference);
        Assert.Equal(normalizedFilter.Categories, detail.Filter.Categories);
        Assert.Equal(normalizedFilter.ErrorCodes, detail.Filter.ErrorCodes);
        Assert.Equal(normalizedFilter.ActivityStates, detail.Filter.ActivityStates);
        Assert.Equal(normalizedFilter.SeriesId, detail.Filter.SeriesId);
        Assert.Equal(normalizedFilter.DemandId, detail.Filter.DemandId);
        Assert.Equal(normalizedFilter.SublotContains, detail.Filter.SublotContains);
        Assert.Equal(query.Window.Resolve(detail.Snapshot.ErrorSearchAsOf), detail.Window);
        Assert.Equal(query.Order, detail.Order);
        Assert.Equal(detail.Periods.Count, detail.Series.MatchedPeriodCount);
        Assert.Equal(
            detail.Periods.Select(period => period.Code).Distinct(StringComparer.Ordinal),
            detail.Series.MatchedErrors.Select(error => error.Code));
        Assert.Equal(
            detail.Periods.SelectMany(period => period.Evidence).Max(evidence => evidence.ObservedAt),
            detail.Series.LatestMatchedEvidenceAt);
        Assert.Equal(
            detail.Periods
                .SelectMany(period => period.Evidence)
                .Select(evidence => evidence.DemandId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            detail.Series.MatchedDemandGenerationCount);
        Assert.All(detail.Periods, period =>
        {
            Assert.Contains(period.Category, normalizedFilter.Categories);
            Assert.Contains(period.Code, normalizedFilter.ErrorCodes);
            Assert.True(period.ActiveAtAsOf);
            Assert.True(period.EndedAt is null || period.EndedAt > period.StartedAt);
        });
    }

    [Fact]
    public void Error_detail_fixture_derives_matched_errors_from_filtered_periods_not_stale_series_summary()
    {
        var query = new ErrorSearchQuery(
            new ErrorSearchFilter
            {
                ActivityStates = [ErrorSearchActivityStates.Active],
                SeriesId = PreviewErrorSeriesId,
            },
            ErrorSearchWindowSelection.Last7Days);
        var sourceDetail = WatchErrorSearchProductionIntegrationTests.CreateErrorDetail(
            query.NormalizeAndValidate().Filter,
            ErrorSearchWindowKinds.Last7Days);
        sourceDetail = sourceDetail with
        {
            Series = sourceDetail.Series with
            {
                MatchedErrors =
                [
                    .. sourceDetail.Series.MatchedErrors,
                    new ErrorSearchMatchedErrorSnapshot(
                        "REQUIRED_MES_FIELD_MISSING",
                        "DATA_COMPLETENESS",
                        "WARNING"),
                ],
            },
        };
        var detail = CreateJourneyErrorDetail(
            query,
            new FakeHostV2DetailRequest(PreviewErrorSeriesId, ErrorSnapshotReference),
            sourceDetail);

        var period = Assert.Single(detail.Periods);
        var matchedError = Assert.Single(detail.Series.MatchedErrors);

        Assert.True(period.ActiveAtAsOf);
        Assert.Equal("REQUIRED_MES_FIELD_MISSING", period.Code);
        Assert.Equal("DATA_COMPLETENESS", period.Category);
        Assert.Equal("ERROR", period.Severity);
        Assert.Equal(period.Code, matchedError.Code);
        Assert.Equal(period.Category, matchedError.Category);
        Assert.Equal(period.Severity, matchedError.Severity);
        Assert.DoesNotContain(
            detail.Series.MatchedErrors,
            error => string.Equals(
                error.Code,
                "INVALID_MES_FIELD_FORMAT",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Error_detail_fixture_keeps_an_ended_boundary_period_inside_the_half_open_window()
    {
        var query = new ErrorSearchQuery(
            new ErrorSearchFilter
            {
                Categories = ["DATA_FORMAT"],
                ErrorCodes = ["INVALID_MES_FIELD_FORMAT"],
                ActivityStates = [ErrorSearchActivityStates.Ended],
                SeriesId = PreviewErrorSeriesId,
            },
            ErrorSearchWindowSelection.Last7Days);

        var detail = CreateJourneyErrorDetail(
            query,
            new FakeHostV2DetailRequest(PreviewErrorSeriesId, ErrorSnapshotReference));
        var period = Assert.Single(detail.Periods);

        Assert.False(period.ActiveAtAsOf);
        Assert.NotNull(period.EndedAt);
        Assert.True(period.StartedAt < detail.Window.ToUtc);
        Assert.True(period.EndedAt > detail.Window.FromUtc);
        Assert.All(period.Evidence, evidence =>
        {
            Assert.True(evidence.ObservedAt >= detail.Window.FromUtc);
            Assert.True(evidence.ObservedAt < detail.Window.ToUtc);
        });
    }

    [Fact]
    public void Error_page_fixture_derives_counts_and_facets_from_its_canonical_detail_periods()
    {
        var page = CreateJourneyErrorPage(new ErrorSearchQuery(
            new ErrorSearchFilter(),
            ErrorSearchWindowSelection.Last7Days));
        var item = Assert.Single(page.Items);

        Assert.True(
            page.Snapshot.ErrorSearchAsOf
                >= WatchCurrentAttentionProductionIntegrationTests
                    .CreateAttentionSnapshot(new CurrentIngestAttentionQuery())
                    .Snapshot
                    .SnapshotAsOf);
        Assert.Equal(2, item.MatchedPeriodCount);
        Assert.Equal(2, item.MatchedDemandGenerationCount);
        Assert.Equal(
            1,
            Assert.Single(page.Facets.Categories, facet =>
                facet.Category == "DATA_COMPLETENESS").SeriesCount);
        Assert.Equal(
            1,
            Assert.Single(page.Facets.Categories, facet =>
                facet.Category == "DATA_FORMAT").SeriesCount);
        Assert.All(
            page.Facets.Categories.Where(facet =>
                facet.Category is not "DATA_COMPLETENESS" and not "DATA_FORMAT"),
            facet => Assert.Equal(0, facet.SeriesCount));
        Assert.Equal(
            SeriesErrorCatalog.Definitions
                .Select(definition => definition.Category)
                .Distinct(StringComparer.Ordinal),
            page.Facets.Categories.Select(facet => facet.Category));
        Assert.Equal(
            1,
            Assert.Single(page.Facets.ActivityStates, facet =>
                facet.State == ErrorSearchActivityStates.Active).SeriesCount);
        Assert.Equal(
            0,
            Assert.Single(page.Facets.ActivityStates, facet =>
                facet.State == ErrorSearchActivityStates.Ended).SeriesCount);

        var drilled = CreateJourneyErrorPage(new ErrorSearchQuery(
            new ErrorSearchFilter
            {
                Categories = ["DATA_COMPLETENESS"],
                ErrorCodes = ["REQUIRED_MES_FIELD_MISSING"],
                ActivityStates = [ErrorSearchActivityStates.Active],
                SeriesId = PreviewErrorSeriesId,
            },
            ErrorSearchWindowSelection.Last7Days));
        var drilledItem = Assert.Single(drilled.Items);
        Assert.NotEqual(page.SnapshotReference, drilled.SnapshotReference);
        Assert.Equal(PreviewErrorSeriesId, item.SeriesId);
        Assert.Equal(item.SeriesId, drilledItem.SeriesId);
        Assert.Equal(1, drilledItem.MatchedPeriodCount);
        Assert.Equal(1, drilledItem.MatchedDemandGenerationCount);
        Assert.Equal(
            1,
            Assert.Single(drilled.Facets.Categories, facet =>
                facet.Category == "DATA_COMPLETENESS").SeriesCount);
        Assert.Equal(
            SeriesErrorCatalog.Definitions
                .Select(definition => definition.Category)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            drilled.Facets.Categories.Count);
        Assert.All(
            drilled.Facets.Categories.Where(facet => facet.Category != "DATA_COMPLETENESS"),
            facet => Assert.Equal(0, facet.SeriesCount));
        Assert.Equal(
            1,
            Assert.Single(drilled.Facets.ActivityStates, facet =>
                facet.State == ErrorSearchActivityStates.Active).SeriesCount);
        Assert.Equal(
            0,
            Assert.Single(drilled.Facets.ActivityStates, facet =>
                facet.State == ErrorSearchActivityStates.Ended).SeriesCount);
        Assert.Equal(ErrorSearchActivityStates.All.Count, drilled.Facets.ActivityStates.Count);
    }

    [Fact]
    public void Error_detail_filters_never_rewrite_canonical_period_facts()
    {
        var unfiltered = CreateJourneyErrorDetail(
            new ErrorSearchQuery(new ErrorSearchFilter(), ErrorSearchWindowSelection.Last7Days),
            new FakeHostV2DetailRequest(PreviewErrorSeriesId, "snapshot-unfiltered"));
        var filtered = CreateJourneyErrorDetail(
            new ErrorSearchQuery(
                new ErrorSearchFilter
                {
                    Categories = ["DATA_COMPLETENESS"],
                    ErrorCodes = ["REQUIRED_MES_FIELD_MISSING"],
                    ActivityStates = [ErrorSearchActivityStates.Active],
                    SeriesId = PreviewErrorSeriesId,
                },
                ErrorSearchWindowSelection.Last7Days),
            new FakeHostV2DetailRequest(PreviewErrorSeriesId, "snapshot-filtered"));

        var canonical = Assert.Single(unfiltered.Periods, period =>
            string.Equals(
                period.Code,
                "REQUIRED_MES_FIELD_MISSING",
                StringComparison.Ordinal));
        var drilled = Assert.Single(filtered.Periods);
        Assert.Equal(canonical.PeriodId, drilled.PeriodId);
        Assert.Equal(canonical.Code, drilled.Code);
        Assert.Equal(canonical.Category, drilled.Category);
        Assert.Equal(canonical.StartedAt, drilled.StartedAt);
        Assert.Equal(canonical.EndedAt, drilled.EndedAt);
        Assert.Equal(canonical.StartsBeforeWindow, drilled.StartsBeforeWindow);
        Assert.Equal(canonical.EndsAfterWindow, drilled.EndsAfterWindow);
        Assert.Equal(canonical.ActiveAtAsOf, drilled.ActiveAtAsOf);
        Assert.Equal(
            Assert.Single(canonical.Evidence).ObservedAt,
            Assert.Single(drilled.Evidence).ObservedAt);
        Assert.True(canonical.ActiveAtAsOf);
        Assert.Null(canonical.EndedAt);
    }

    [Fact]
    public async Task Error_detail_fixture_resolves_the_query_bound_to_each_opaque_snapshot()
    {
        var observedQueries = new ConcurrentQueue<ErrorSearchQuery>();
        await using var host = await ScriptedFakeHost.StartV2Async(
            CreateScenario(observedQueries.Enqueue),
            TestContext.Current.CancellationToken);
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings(host.BaseUrl, Credential, 30));
        await client.VerifyContractAsync(TestContext.Current.CancellationToken);

        var unfiltered = await client.FetchErrorSearchAsync(
            new ErrorSearchQuery(new ErrorSearchFilter(), ErrorSearchWindowSelection.Last7Days),
            TestContext.Current.CancellationToken);
        var drilledQuery = new ErrorSearchQuery(
            new ErrorSearchFilter
            {
                Categories = ["DATA_COMPLETENESS"],
                ErrorCodes = ["REQUIRED_MES_FIELD_MISSING"],
                ActivityStates = [ErrorSearchActivityStates.Active],
                SeriesId = PreviewErrorSeriesId,
            },
            ErrorSearchWindowSelection.Last7Days);
        var drilled = await client.FetchErrorSearchAsync(
            drilledQuery,
            TestContext.Current.CancellationToken);

        var unfilteredDetail = await client.FetchErrorSearchDetailAsync(
            Assert.Single(unfiltered.Items).SeriesId,
            unfiltered.SnapshotReference,
            TestContext.Current.CancellationToken);
        var drilledDetail = await client.FetchErrorSearchDetailAsync(
            Assert.Single(drilled.Items).SeriesId,
            drilled.SnapshotReference,
            TestContext.Current.CancellationToken);
        var wrongSeries = await Assert.ThrowsAsync<WatchHostQueryException>(() =>
            client.FetchErrorSearchDetailAsync(
                "SERIES-NOT-IN-SNAPSHOT",
                unfiltered.SnapshotReference,
                TestContext.Current.CancellationToken));

        Assert.NotEqual(unfiltered.SnapshotReference, drilled.SnapshotReference);
        Assert.Empty(unfilteredDetail.Filter.Categories);
        Assert.Equal(drilledQuery.Filter.Normalize().Categories, drilledDetail.Filter.Categories);
        Assert.Equal(PreviewErrorSeriesId, drilledDetail.Series.SeriesId);
        Assert.Equal(2, observedQueries.Count);
        Assert.Equal(WatchHostFailureKind.ServerQuery, wrongSeries.Kind);
        Assert.Equal(ErrorSearchErrorCodes.ObjectNotInSnapshot, wrongSeries.ErrorCode);
    }

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
            var demandGenerationGrid = WaitForRows(
                window,
                "DemandSeriesGenerationGrid",
                "DemandSeries generations");
            demandGenerationGrid.Focus();
            WaitUntil(
                () => !demandGenerationGrid.Properties.IsOffscreen.ValueOrDefault,
                "DemandSeries detail evidence in view",
                StepTimeout);
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
            Navigate(
                window,
                "ErrorSearchNavigationItem",
                "ErrorSearchNormalizedFilterText");
            var errorGrid = WaitForRows(window, "ErrorSearchSeriesGrid", "error Series rows");
            errorGrid.Select(0);
            var periodGrid = WaitForRows(
                window,
                "ErrorSearchPeriodGrid",
                "matched error periods");
            periodGrid.Select(0);
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
                () => FindById(window, "ErrorSearchNormalizedFilterText") is { } filterText
                    && !filterText.Properties.IsOffscreen.ValueOrDefault
                    && TextValue(filterText)
                        .Contains("SERIES-ATTENTION-22", StringComparison.Ordinal),
                "explicit CurrentIngestAttention to Error Search drill",
                StepTimeout);
            var drillErrorGrid = WaitForRows(
                window,
                "ErrorSearchSeriesGrid",
                "drilled Error Search Series rows");
            drillErrorGrid.Select(0);
            WaitUntil(
                () => TextValue(FindRequiredById(window, "ErrorSearchDetailContextText"))
                    .Contains("SERIES-ATTENTION-22", StringComparison.Ordinal),
                "the drilled Error Search detail identity",
                StepTimeout);
            var drillPeriodGrid = WaitForRows(
                window,
                "ErrorSearchPeriodGrid",
                "drilled Error Search periods");
            drillPeriodGrid.Select(0);
            WaitForRows(
                window,
                "ErrorSearchEvidenceGrid",
                "drilled Error Search evidence");
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
        Action<ErrorSearchQuery> rememberErrorQuery)
    {
        var errorBindings = new ConcurrentDictionary<
            string,
            JourneyErrorSnapshotBinding>(StringComparer.Ordinal);
        var errorSnapshotSequence = 0;
        return new("production-preview-19-22", Credential)
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
                var normalized = query.NormalizeAndValidate();
                rememberErrorQuery(normalized);
                var snapshotReference =
                    $"{ErrorSnapshotReference}-{Interlocked.Increment(ref errorSnapshotSequence):D2}";
                var page = CreateJourneyErrorPage(normalized, snapshotReference);
                errorBindings[snapshotReference] = new JourneyErrorSnapshotBinding(
                    normalized,
                    page.Items
                        .Select(item => item.SeriesId)
                        .ToHashSet(StringComparer.Ordinal));
                return FakeHostReply.Return(page);
            }),
            ErrorSearchDetail = FakeHostReply.Select<
                FakeHostV2DetailRequest,
                ErrorSearchDetailSnapshot>(request =>
                {
                    if (!errorBindings.TryGetValue(request.SnapshotReference, out var binding)
                        || !binding.SeriesIds.Contains(request.ObjectId))
                    {
                        return FakeHostReply.HttpFailure<ErrorSearchDetailSnapshot>(
                            HttpStatusCode.NotFound,
                            ErrorSearchErrorCodes.ObjectNotInSnapshot,
                            "The requested Series is not part of this frozen Error Search snapshot.");
                    }

                    return FakeHostReply.Return(
                        CreateJourneyErrorDetail(binding.Query, request));
                }),
            CurrentAttention = FakeHostReply.Select<
                CurrentIngestAttentionQuery,
                CurrentIngestAttentionSnapshot>(query => FakeHostReply.Return(
                    WatchCurrentAttentionProductionIntegrationTests.CreateAttentionSnapshot(
                        query))),
        };
    }

    private sealed record JourneyErrorSnapshotBinding(
        ErrorSearchQuery Query,
        IReadOnlySet<string> SeriesIds);

    internal static ErrorSearchListSnapshot CreateJourneyErrorPage(
        ErrorSearchQuery query,
        string? snapshotReference = null)
    {
        var normalized = query.NormalizeAndValidate();
        var seriesId = normalized.Filter.SeriesId ?? PreviewErrorSeriesId;
        var resolvedSnapshotReference = snapshotReference
            ?? (string.IsNullOrWhiteSpace(normalized.Filter.SeriesId)
            ? ErrorSnapshotReference
            : $"{ErrorSnapshotReference}-series-attention-22");
        var detailRequest = new FakeHostV2DetailRequest(
            seriesId,
            resolvedSnapshotReference);
        var detail = CreateJourneyErrorDetail(
            normalized,
            detailRequest);
        var categoryFacetDetail = CreateJourneyErrorDetail(
            normalized with
            {
                Filter = normalized.Filter with
                {
                    Categories = [],
                    ErrorCodes = [],
                },
            },
            detailRequest);
        var activityFacetDetail = CreateJourneyErrorDetail(
            normalized with
            {
                Filter = normalized.Filter with { ActivityStates = [] },
            },
            detailRequest);
        var hasMatches = detail.Periods.Count > 0;
        var page = WatchErrorSearchProductionIntegrationTests.CreateErrorPage(
            normalized,
            resolvedSnapshotReference,
            pageNumber: 1,
            totalPages: hasMatches ? 1 : 0,
            totalSeriesCount: hasMatches ? 1 : 0,
            includeItem: hasMatches,
            item: detail.Series);
        return page with
        {
            Snapshot = detail.Snapshot,
            Window = detail.Window,
            Facets = new ErrorSearchFacets(
                SeriesErrorCatalog.Definitions
                    .Select(definition => definition.Category)
                    .Distinct(StringComparer.Ordinal)
                    .Select(category => new ErrorSearchCategoryFacetSnapshot(
                        category,
                        categoryFacetDetail.Periods.Any(period => string.Equals(
                            period.Category,
                            category,
                            StringComparison.Ordinal))
                            ? 1
                            : 0))
                    .ToArray(),
                ErrorSearchActivityStates.All
                    .Select(state => new ErrorSearchActivityStateFacetSnapshot(
                        state,
                        string.Equals(
                            activityFacetDetail.Periods.Any(period => period.ActiveAtAsOf)
                                ? ErrorSearchActivityStates.Active
                                : ErrorSearchActivityStates.Ended,
                            state,
                            StringComparison.Ordinal)
                        && activityFacetDetail.Periods.Count > 0
                            ? 1
                            : 0))
                    .ToArray()),
        };
    }

    internal static ErrorSearchDetailSnapshot CreateJourneyErrorDetail(
        ErrorSearchQuery query,
        FakeHostV2DetailRequest request,
        ErrorSearchDetailSnapshot? sourceDetail = null)
    {
        var normalized = query.NormalizeAndValidate();
        var detail = sourceDetail
            ?? WatchErrorSearchProductionIntegrationTests.CreateErrorDetail(
                normalized.Filter,
                ErrorSearchWindowKinds.Last7Days);
        var identity = detail.Snapshot with
        {
            ErrorSearchAsOf = PreviewErrorAsOf,
            ProjectionCommitId = "error-preview-commit-22",
            ProjectionSequence = 231,
            ProjectionCommittedAt = PreviewErrorAsOf,
            PollTraceId = "error-preview-poll-22",
        };
        var resolvedWindow = normalized.Window.Resolve(identity.ErrorSearchAsOf);
        var periods = detail.Periods
            .Where(period => MatchesErrorFilter(
                normalized.Filter,
                period.Category,
                period.Code))
            .Select(period =>
            {
                var isActive = string.Equals(
                    period.Code,
                    "REQUIRED_MES_FIELD_MISSING",
                    StringComparison.Ordinal);
                var startsBeforeWindow = resolvedWindow.FromUtc is not null;
                var startedAt = startsBeforeWindow && resolvedWindow.FromUtc is { } fromUtc
                    ? fromUtc.AddHours(-2)
                    : identity.ErrorSearchAsOf.AddHours(-2);
                DateTimeOffset? endedAt = isActive
                    ? null
                    : period.EndedAt is { } existingEnd && existingEnd > startedAt
                        ? existingEnd
                        : startedAt.AddHours(3);
                var evidenceAt = isActive
                    ? identity.ErrorSearchAsOf.AddSeconds(-1)
                    : startedAt.AddMinutes(150);
                return period with
                {
                    StartedAt = startedAt,
                    EndedAt = endedAt,
                    EndReason = isActive ? null : period.EndReason ?? "RESOLVED",
                    StartsBeforeWindow = startsBeforeWindow,
                    EndsAfterWindow = isActive,
                    ActiveAtAsOf = isActive,
                    Evidence = period.Evidence
                        .Select(evidence => evidence with
                        {
                            ObservedAt = evidenceAt,
                            DemandId = isActive
                                ? "DEMAND-ATTENTION-22"
                                : "DEMAND-ATTENTION-21",
                        })
                        .ToArray(),
                };
            })
            .Where(period => normalized.Filter.ActivityStates.Count == 0
                || normalized.Filter.ActivityStates.Contains(
                    period.ActiveAtAsOf
                        ? ErrorSearchActivityStates.Active
                        : ErrorSearchActivityStates.Ended,
                    StringComparer.Ordinal))
            .Where(period => string.IsNullOrWhiteSpace(normalized.Filter.DemandId)
                || period.Evidence.Any(evidence => string.Equals(
                    evidence.DemandId,
                    normalized.Filter.DemandId,
                    StringComparison.Ordinal)))
            .ToArray();
        var matchedDemandGenerationCount = periods
            .SelectMany(period => period.Evidence)
            .Select(evidence => evidence.DemandId)
            .Where(demandId => !string.IsNullOrWhiteSpace(demandId))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var latestMatchedEvidenceAt = periods
            .SelectMany(period => period.Evidence)
            .Select(evidence => evidence.ObservedAt)
            .DefaultIfEmpty(detail.Series.LatestMatchedEvidenceAt)
            .Max();
        return detail with
        {
            SnapshotReference = request.SnapshotReference,
            Snapshot = identity,
            Filter = normalized.Filter,
            Window = resolvedWindow,
            Order = normalized.Order,
            Series = detail.Series with
            {
                SeriesId = request.ObjectId,
                ActivityState = periods.Any(period => period.ActiveAtAsOf)
                    ? ErrorSearchActivityStates.Active
                    : ErrorSearchActivityStates.Ended,
                MatchedErrors = periods
                    .Select(period => new ErrorSearchMatchedErrorSnapshot(
                        period.Code,
                        period.Category,
                        period.Severity))
                    .Distinct()
                    .ToArray(),
                LatestMatchedEvidenceAt = latestMatchedEvidenceAt,
                MatchedPeriodCount = periods.Length,
                MatchedDemandGenerationCount = matchedDemandGenerationCount,
            },
            Periods = periods,
        };
    }

    private static bool MatchesErrorFilter(
        ErrorSearchFilter filter,
        string category,
        string code) =>
        (filter.Categories.Count == 0
            || filter.Categories.Contains(category, StringComparer.Ordinal))
        && (filter.ErrorCodes.Count == 0
            || filter.ErrorCodes.Contains(code, StringComparer.Ordinal));

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
