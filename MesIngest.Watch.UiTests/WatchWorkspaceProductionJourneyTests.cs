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
    private static readonly TimeSpan PreviewStateTimeout = TimeSpan.FromSeconds(35);
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
    public void Area_preview_profiles_are_real_txt_inputs_for_the_selected_variant_states()
    {
        var localAppData = Path.Combine(
            Path.GetTempPath(),
            "MesIngest.Watch.UiTests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            PrepareAreaProfiles(localAppData);
            var profileDirectory = Path.Combine(
                localAppData,
                "MesIngest.Watch",
                "area-filters");
            var store = new WatchAreaFilterProfileStore(profileDirectory);

            var summaries = store.EnumerateProfiles();
            Assert.Equal(4, summaries.Count);
            Assert.Equal(
                ["东区", "临时范围", "焊线区域", "西区"],
                summaries.Select(summary => summary.ProfileName));
            Assert.Equal("东区", Assert.Single(summaries, summary => summary.IsApplied).ProfileName);

            var applied = store.Load("东区");
            Assert.True(applied.IsValid);
            Assert.Equal(12, applied.MesAreas.Count);
            Assert.Equal(9, store.Load("西区").MesAreas.Count);
            Assert.Equal(24, store.Load("焊线区域").MesAreas.Count);

            var invalid = store.Load("临时范围");
            Assert.False(invalid.IsValid);
            var diagnostic = Assert.Single(invalid.Diagnostics);
            Assert.Equal(WatchAreaFilterProfileDiagnosticCodes.InvalidMesArea, diagnostic.Code);
            Assert.Equal(3, diagnostic.LineNumber);
            Assert.Equal("AREA-INVALID", diagnostic.Value);

            var appliedState = store.LoadApplied();
            Assert.Equal("东区", appliedState.ProfileName);
            Assert.Equal(applied.MesAreas, appliedState.MesAreas);
            Assert.Equal(
                DateTimeOffset.Parse("2026-08-14T05:00:00Z", CultureInfo.InvariantCulture),
                appliedState.AppliedAt);
        }
        finally
        {
            if (Directory.Exists(localAppData))
            {
                Directory.Delete(localAppData, recursive: true);
            }
        }
    }

    [Fact]
    public void Overview_preview_reconciles_the_same_page_fixtures_and_keeps_review_density()
    {
        string[] mesAreas =
        [
            "A1-1", "A1-2", "A2-1", "A2-2",
            "B1-1", "B1-2", "B2-1", "B2-2",
            "C1-1", "C1-2", "C2-1", "C2-2",
        ];
        var demand = CreateJourneyDemandSeriesList(
            new DemandSeriesBrowseQuery(
                new DemandSeriesBrowseFilter { MesAreas = mesAreas }));
        var demandDetail = CreateJourneyDemandSeriesDetail(
            DemandSeriesId,
            DemandSnapshotReference);
        var audit = CreateJourneyAuditList(
            new ReadabilityAuditQuery(
                new ReadabilityAuditFilter { MesAreas = mesAreas }));
        var errors = CreateJourneyErrorPage(
            new ErrorSearchQuery(
                new ErrorSearchFilter(),
                ErrorSearchWindowSelection.Last7Days));
        var attention = WatchCurrentAttentionProductionIntegrationTests
            .CreateAttentionSnapshot(new CurrentIngestAttentionQuery());

        var overview = CreateJourneyOverview(mesAreas);

        Assert.Equal(demand.ExactTotalCount, demand.Items.Count);
        Assert.InRange(demand.Items.Count, 5, 10);
        Assert.InRange(demandDetail.Series.Demands.Count, 2, 5);
        Assert.InRange(demandDetail.Series.Events.Count, 3, 8);
        Assert.Equal(
            demand.Items.LongCount(item => item.Lifecycle == DemandSeriesLifecycleContract.Tracking),
            demand.Facets.TrackingCount);
        Assert.Equal(
            demand.Items.LongCount(item => item.Lifecycle == DemandSeriesLifecycleContract.Archived),
            demand.Facets.ArchivedCount);
        Assert.Equal(
            demand.Items.LongCount(item => item.CurrentPresence == DemandSeriesLifecycleContract.Visible),
            demand.Facets.VisibleCount);
        Assert.Equal(
            demand.Items.LongCount(item => item.CurrentPresence == DemandSeriesLifecycleContract.Gone),
            demand.Facets.GoneCount);
        Assert.Equal(
            demand.Items.LongCount(item =>
                item.CurrentPresence == DemandSeriesLifecycleContract.LongGoneButVisible),
            demand.Facets.LongGoneButVisibleCount);
        Assert.Equal(DemandSeriesId, demand.Items[0].SeriesId);
        Assert.Equal(DemandSeriesId, demandDetail.Series.SeriesId);
        Assert.Contains(
            demandDetail.Series.Demands,
            item => item.DemandId == demandDetail.Series.CurrentDemand.DemandId);
        Assert.All(
            demandDetail.Series.Events,
            item => Assert.Equal(DemandSeriesId, item.SeriesId));
        Assert.Equal(
            demandDetail.Series.Events.Count,
            demandDetail.Series.Events.Select(item => item.SeriesSequence).Distinct().Count());
        Assert.Equal(audit.ExactTotalDemandCount, audit.Items.Count);
        Assert.InRange(audit.Items.Count, 5, 10);
        Assert.All(
            audit.Facets.ReadabilityStates,
            facet => Assert.Equal(
                audit.Items.LongCount(item => item.ExternalReadabilityState == facet.State),
                facet.DemandCount));
        Assert.All(
            audit.Facets.Blockers,
            facet => Assert.Equal(
                audit.Items.LongCount(item => item.ReadabilityBlockers.Contains(
                    facet.Code,
                    StringComparer.Ordinal)),
                facet.DemandCount));

        Assert.Equal(mesAreas, overview.MesAreas);
        Assert.Equal(demand.ExactTotalCount, overview.Series.ExactTotalSeriesCount);
        Assert.Equal(demand.Facets.TrackingCount, overview.Series.TrackingCount);
        Assert.Equal(demand.Facets.ArchivedCount, overview.Series.ArchivedCount);
        Assert.Equal(demand.Facets.GoneCount, overview.Series.GoneCount);
        Assert.Equal(
            demand.Facets.LongGoneButVisibleCount,
            overview.Series.LongGoneButVisibleCount);

        Assert.Equal(
            audit.ExactTotalDemandCount,
            overview.Readability.ExactTotalDemandGenerationCount);
        Assert.Equal(
            Assert.Single(
                audit.Facets.ReadabilityStates,
                facet => facet.State == ExternalReadabilityStates.Readable).DemandCount,
            overview.Readability.ReadableCount);
        Assert.Equal(
            Assert.Single(
                audit.Facets.ReadabilityStates,
                facet => facet.State == ExternalReadabilityStates.NotReadable).DemandCount,
            overview.Readability.NotReadableCount);

        Assert.Equal(
            errors.Items.LongCount(item => item.ActivityState == ErrorSearchActivityStates.Active),
            overview.Errors.ActiveSeriesCount);
        Assert.Equal(errors.TotalSeriesCount, overview.Errors.Prior7DaysSeriesCount);
        Assert.Equal(attention.ExactTotalItemCount, overview.Attention.ExactTotalItemCount);
        Assert.Equal(
            attention.Facets.Types.Select(facet => (facet.Value, facet.ItemCount)),
            overview.Attention.Types.Select(facet => (facet.Value, facet.Count)));
        Assert.Equal(
            attention.Facets.Severities.Select(facet => (facet.Value, facet.ItemCount)),
            overview.Attention.Severities.Select(facet => (facet.Value, facet.Count)));
        Assert.All(
            new[]
            {
                overview.Series.ExactTotalSeriesCount,
                overview.Readability.ExactTotalDemandGenerationCount,
                overview.Errors.ActiveSeriesCount,
                overview.Attention.ExactTotalItemCount,
            },
            count => Assert.True(count > 0, "Every overview summary card needs real fixture density."));

        Assert.Equal(WatchOverviewRecentActivityStates.HasRecentHighlights, overview.RecentActivityState);
        Assert.Null(overview.EmptyStateMessage);
        Assert.Equal(5, overview.RecentActivity.Count);
        Assert.Contains(
            overview.RecentActivity,
            activity => activity.SeriesId == DemandSeriesId
                && activity.Navigation.Target == OverviewNavigationTargets.DemandSeriesDetail);
        Assert.Contains(
            overview.RecentActivity,
            activity => activity.SeriesId == PreviewErrorSeriesId
                && activity.Navigation.Target == OverviewNavigationTargets.ErrorSearch);
        Assert.All(overview.RecentActivity, activity => Assert.Null(activity.Navigation.Cursor));
    }

    [Fact]
    public void Overview_preview_script_fails_exactly_one_refresh_then_recovers()
    {
        var availability = new JourneyOverviewAvailability();
        var scenario = CreateScenario(_ => { }, availability);
        var query = new WatchOverviewQuery(["A1-1", "A1-2"]);

        var first = scenario.Overview!.Next(query);
        availability.FailNextRefresh();
        var failure = scenario.Overview.Next(query);
        var recovery = scenario.Overview.Next(query);
        var steady = scenario.Overview.Next(query);

        Assert.Equal(FakeHostFailureShape.None, first.FailureShape);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Equal(FakeHostFailureShape.Query, failure.FailureShape);
        Assert.Equal(FakeHostFailureShape.None, recovery.FailureShape);
        Assert.Equal(FakeHostFailureShape.None, steady.FailureShape);
        Assert.Equal(
            first.Value.Snapshot.ProjectionCommitId,
            recovery.Value.Snapshot.ProjectionCommitId);
        Assert.Equal(first.Value.MesAreas, recovery.Value.MesAreas);
        Assert.True(availability.HasObservedFailure);
        Assert.True(availability.HasObservedRecovery);
        Assert.Equal(
            availability.FailureRequestNumber + 1,
            availability.RecoveryRequestNumber);
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
        var overviewAvailability = new JourneyOverviewAvailability();
        var scenario = CreateScenario(
            query => latestErrorQuery = query,
            overviewAvailability);
        await using var host = await ScriptedFakeHost.StartV2Async(scenario, cancellationToken);

        var artifactRoot = WatchWindowJourneyTests.ResolveArtifactRoot();
        var journeyName = "production-workspace-19-22";
        var runtimeRoot = Path.Combine(artifactRoot, "runtime", journeyName);
        var logDirectory = Path.Combine(runtimeRoot, "logs");
        var localAppData = Path.Combine(runtimeRoot, "local-app-data");
        Directory.CreateDirectory(logDirectory);
        PrepareAreaProfiles(localAppData);
        var connectionPreferencesPath = Path.Combine(
            localAppData,
            "MesIngest.Watch",
            "connection-preferences.json");
        WatchConnectionPreferencesStore.Save(
            connectionPreferencesPath,
            new WatchConnectionPreferences(
                host.BaseUrl,
                RequestTimeoutSeconds: 30,
                WatchCredentialReference.ExternalConfiguration));
        var validConnectionPreferences = File.ReadAllBytes(connectionPreferencesPath);

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
            SetNavigationPaneExpanded(window, expanded: false);
            Capture(evidence, process.MainWindowHandle, "01-overview");

            failedStep = "fluent-window-chrome";
            var chromeUiaEvidence = ExerciseWindowChrome(
                window,
                automation,
                evidence,
                process.MainWindowHandle);
            WatchWindowNative.SetClientSize(process.MainWindowHandle, 1440, 900);

            failedStep = "overview-navigation-expanded";
            SetNavigationPaneExpanded(window, expanded: true);
            Capture(
                evidence,
                process.MainWindowHandle,
                "01e-overview-navigation-expanded");
            SetNavigationPaneExpanded(window, expanded: false);

            failedStep = "overview-offline-retained";
            var retainedOverviewFacts = CaptureOverviewFacts(window);
            overviewAvailability.FailNextRefresh();
            WaitUntil(
                () => overviewAvailability.HasObservedFailure
                    && IsVisibleInWindow(window, FindById(window, "OverviewInfoBar"))
                    && TextValue(FindRequiredById(window, "OverviewInfoBar"))
                        .Contains("概览刷新失败，已保留上次完整快照", StringComparison.Ordinal)
                    && TextValue(FindRequiredById(window, "OverviewInfoBar"))
                        .Contains("失败于", StringComparison.Ordinal)
                    && TextValue(FindRequiredById(window, "OverviewInfoBar"))
                        .Contains("继续显示 Host 快照", StringComparison.Ordinal)
                    && TextValue(FindRequiredById(window, "OverviewInfoBar"))
                        .Contains("Host 暂时离线", StringComparison.Ordinal)
                    && TextValue(FindRequiredById(window, "OverviewHostStatusPill"))
                        .Contains("Host 已连接 · 读取失败", StringComparison.Ordinal)
                    && TextValue(FindRequiredById(window, "StaleNoticeText"))
                        .Contains("数据可能已过期", StringComparison.Ordinal),
                "the real Host refresh failure with retained Overview facts",
                PreviewStateTimeout);
            AssertOverviewFacts(window, retainedOverviewFacts);
            Assert.Contains(
                host.Timeline,
                entry => entry.Operation == FakeHostOperation.OverviewV2
                    && entry.State == FakeHostRequestState.Failed);
            var failedOverviewTimelineSequence = host.Timeline
                .Where(entry => entry.Operation == FakeHostOperation.OverviewV2
                    && entry.State == FakeHostRequestState.Failed)
                .Max(entry => entry.Sequence);
            Capture(evidence, process.MainWindowHandle, "01f-overview-offline-retained");

            failedStep = "overview-recovery";
            WaitUntil(
                () => overviewAvailability.HasObservedRecovery
                    && FindById(window, "OverviewInfoBar") is { } infoBar
                    && infoBar.Properties.IsOffscreen.ValueOrDefault
                    && TextValue(infoBar).Contains("当前无活动通知", StringComparison.Ordinal)
                    && TextValue(FindRequiredById(window, "OverviewHostStatusPill"))
                        .Contains("Host 已连接", StringComparison.Ordinal)
                    && !TextValue(FindRequiredById(window, "OverviewHostStatusPill"))
                        .Contains("读取失败", StringComparison.Ordinal)
                    && TextValue(FindRequiredById(window, "StaleNoticeText"))
                        .Contains("概览数据未标记为陈旧", StringComparison.Ordinal),
                "the deterministic Overview auto-refresh recovery",
                PreviewStateTimeout);
            AssertOverviewFacts(window, retainedOverviewFacts);
            Assert.Contains(
                host.Timeline,
                entry => entry.Operation == FakeHostOperation.OverviewV2
                    && entry.State == FakeHostRequestState.Completed
                    && entry.Sequence > failedOverviewTimelineSequence);

            failedStep = "settings";
            Navigate(window, "SettingsNavigationItem", "SettingsPage");
            WaitUntil(
                () => FindById(window, "SaveRefreshIntervalsButton") is not null,
                "production settings commands",
                StepTimeout);
            Capture(evidence, process.MainWindowHandle, "02-settings");

            failedStep = "settings-timeout-validation";
            var contractRequestsBeforeInvalidTimeout = host.Timeline.Count(entry =>
                entry.Operation == FakeHostOperation.ContractV2
                && entry.State == FakeHostRequestState.Started);
            var requestTimeoutInput = FindRequiredById(window, "RequestTimeoutInput")
                .AsTextBox();
            Assert.Equal("30", requestTimeoutInput.Text);
            requestTimeoutInput.Text = "0";
            FindRequiredById(window, "ApplyHostButton").AsButton().Invoke();
            WaitUntil(
                () => FindById(window, "SettingsInfoBar") is { } infoBar
                    && IsVisibleInWindow(window, infoBar)
                    && TextValue(infoBar).Contains("无法应用 Host 设置", StringComparison.Ordinal)
                    && TextValue(infoBar).Contains("1", StringComparison.Ordinal)
                    && TextValue(infoBar).Contains("300", StringComparison.Ordinal)
                    && TextValue(FindRequiredById(window, "SettingsHostStatusPill"))
                        .Contains("Host 已连接", StringComparison.Ordinal),
                "the real Settings request-timeout validation error",
                StepTimeout);
            Assert.Equal(
                contractRequestsBeforeInvalidTimeout,
                host.Timeline.Count(entry =>
                    entry.Operation == FakeHostOperation.ContractV2
                    && entry.State == FakeHostRequestState.Started));
            Assert.Equal(
                validConnectionPreferences,
                File.ReadAllBytes(connectionPreferencesPath));
            Capture(evidence, process.MainWindowHandle, "02v-settings-timeout-validation");

            requestTimeoutInput.Text = "30";
            CloseInfoBar(window, "SettingsInfoBar");
            WaitUntil(
                () => requestTimeoutInput.Text == "30"
                    && FindById(window, "SettingsInfoBar") is { } infoBar
                    && infoBar.Properties.IsOffscreen.ValueOrDefault,
                "the restored valid Settings draft",
                StepTimeout);
            Assert.Equal(
                validConnectionPreferences,
                File.ReadAllBytes(connectionPreferencesPath));

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
            var qualificationGrid = WaitForRows(
                window,
                "ReadabilityQualificationGrid",
                "readability checks");
            EnsureVisibleIfOffscreen(
                window,
                qualificationGrid,
                "readability qualification detail for the production candidate");
            Capture(evidence, process.MainWindowHandle, "04-readability-audit-detail");

            failedStep = "area-filter";
            Navigate(window, "AreaFilterNavigationItem", "AreaFilterPage");
            var profileList = FindRequiredById(window, "AreaProfileList").AsListBox();
            WaitUntil(
                () => profileList.Items.Length == 4,
                "the four local AREA TXT profiles",
                StepTimeout);
            var invalidProfile = Assert.Single(
                profileList.Items,
                item => TextValue(item).Contains("临时范围", StringComparison.Ordinal));
            invalidProfile.Select();
            WaitUntil(
                () => TextValue(FindRequiredById(window, "AreaProfileValidationSummaryText"))
                    .Contains("1 项问题", StringComparison.Ordinal),
                "the invalid AREA TXT profile parsed by production",
                StepTimeout);
            var validationToggle = FindRequiredButtonByName(window, "查看逐项校验");
            validationToggle.Toggle();
            Assert.Single(
                WaitForRows(
                    window,
                    "AreaProfileValidationGrid",
                    "the invalid AREA TXT diagnostic")
                    .Rows);
            validationToggle.Toggle();

            var appliedProfile = Assert.Single(
                profileList.Items,
                item => TextValue(item).Contains("东区", StringComparison.Ordinal));
            appliedProfile.Select();
            WaitUntil(
                    () => TextValue(FindRequiredById(window, "AreaProfileAppliedStateText"))
                        .Contains("东区", StringComparison.Ordinal)
                    && TextValue(FindRequiredById(window, "AreaProfileValidCountText"))
                        .Contains("12 个有效 AREA", StringComparison.Ordinal),
                "the applied AREA profile state",
                StepTimeout);
            EnsureVisibleIfOffscreen(
                window,
                FindRequiredById(window, "AreaProfileNameInput"),
                "AREA editor for the production candidate");
            Capture(evidence, process.MainWindowHandle, "05-area-filter-profile");

            failedStep = "error-search";
            Navigate(
                window,
                "ErrorSearchNavigationItem",
                "ErrorSearchNormalizedFilterText");
            AssertRepresentativeAnchorVisible(
                window,
                "ErrorSearchSnapshotText",
                "Error Search immediately after navigation");
            var errorGrid = WaitForRows(window, "ErrorSearchSeriesGrid", "error Series rows");
            errorGrid.Select(0);
            var periodGrid = WaitForRows(
                window,
                "ErrorSearchPeriodGrid",
                "matched error periods");
            periodGrid.Select(0);
            WaitForRows(window, "ErrorSearchEvidenceGrid", "matched error evidence");
            PrepareRepresentativeFirstScreen(
                window,
                "ErrorSearchSnapshotText",
                "ErrorSearchCategoryList",
                "Error Search before the production candidate capture");
            Capture(evidence, process.MainWindowHandle, "06-error-search-variant-a");

            failedStep = "current-attention";
            Navigate(window, "CurrentAttentionNavigationItem", "CurrentAttentionPage");
            AssertRepresentativeAnchorVisible(
                window,
                "CurrentAttentionSnapshotText",
                "Current Attention immediately after navigation");
            var attentionGrid = WaitForRows(
                window,
                "CurrentAttentionGrid",
                "current ingest attention rows");
            attentionGrid.Select(0);
            WaitForRows(window, "CurrentAttentionEvidenceGrid", "current attention evidence");
            PrepareRepresentativeFirstScreen(
                window,
                "CurrentAttentionSnapshotText",
                "CurrentAttentionKindFilter",
                "Current Attention before the production candidate capture");
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
            PrepareRepresentativeFirstScreen(
                window,
                "ErrorSearchSnapshotText",
                "ErrorSearchCategoryList",
                "drilled Error Search before the production candidate capture");
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

    internal static DemandSeriesListSnapshot CreateJourneyDemandSeriesList(
        DemandSeriesBrowseQuery query)
    {
        var normalized = query.NormalizeAndValidate();
        var snapshot = WatchDemandSeriesProductionIntegrationTests.CreateDemandSeriesList(
            normalized,
            DemandSeriesId,
            DemandSnapshotReference);
        var at = snapshot.Snapshot.ProjectionCommittedAt;
        var baseItem = Assert.Single(snapshot.Items) with
        {
            StartedAt = at.AddMinutes(-20),
            DemandLastSeenAt = at.AddMinutes(-1),
        };
        var areas = normalized.Filter.MesAreas.Count == 0
            ? ["A1-1", "A1-2", "A2-1", "B1-1", "B2-1", "C1-1"]
            : normalized.Filter.MesAreas;

        LiveMesFieldSetSnapshot Fields(int ordinal) => new(
            areas[(ordinal - 1) % areas.Count],
            $"EQP-{ordinal:00}",
            $"STEP-{ordinal:00}",
            at.AddDays(-ordinal),
            $"PKG-{ordinal:00}");

        var items = new DemandSeriesListItemSnapshot[]
        {
            baseItem with { LiveMesFields = Fields(1) },
            baseItem with
            {
                SeriesId = "SERIES-PREVIEW-20-B",
                WorkType = "WIRE_TO_NITROGEN",
                Sublot = "SL-PREVIEW-B",
                StartedAt = at.AddHours(-1),
                CurrentDemandId = "demand-preview-20-b",
                CurrentGeneration = 1,
                DemandLastSeenAt = at.AddMinutes(-4),
                LiveMesFields = Fields(2),
                LastSeriesSequence = 4,
                LatestPollTraceId = "poll-demand-preview-20-b",
                LatestProjectionCommitId = "commit-demand-preview-20-b",
            },
            baseItem with
            {
                SeriesId = "SERIES-PREVIEW-20-C",
                WorkType = "WIRE_TO_BUFFER",
                Sublot = "SL-PREVIEW-C",
                CurrentPresence = DemandSeriesLifecycleContract.Gone,
                StartedAt = at.AddHours(-3),
                CurrentDemandId = "demand-preview-20-c",
                CurrentGeneration = 3,
                CurrentDemandStatus = DemandSeriesLifecycleContract.Gone,
                DemandLastSeenAt = at.AddMinutes(-18),
                GoneConfirmedAt = at.AddMinutes(-15),
                LiveMesFields = null,
                ExternalReadabilityState = ExternalReadabilityStates.NotReadable,
                ReadabilityBlockers = ["DEMAND_GONE"],
                LastSeriesSequence = 11,
                LatestPollTraceId = "poll-demand-preview-20-c",
                LatestProjectionCommitId = "commit-demand-preview-20-c",
            },
            baseItem with
            {
                SeriesId = "SERIES-PREVIEW-20-D",
                WorkType = "WIRE_TO_GATE",
                Sublot = "SL-PREVIEW-D",
                Lifecycle = DemandSeriesLifecycleContract.Archived,
                CurrentPresence = DemandSeriesLifecycleContract.Gone,
                StartedAt = at.AddHours(-8),
                ArchivedAt = at.AddHours(-1),
                CurrentDemandId = "demand-preview-20-d",
                CurrentGeneration = 2,
                CurrentDemandStatus = DemandSeriesLifecycleContract.Gone,
                DemandLastSeenAt = at.AddHours(-2),
                GoneConfirmedAt = at.AddHours(-1.5),
                LiveMesFields = null,
                ExternalReadabilityState = ExternalReadabilityStates.NotReadable,
                ReadabilityBlockers = ["SERIES_ARCHIVED", "DEMAND_GONE"],
                LastSeriesSequence = 9,
                LatestPollTraceId = "poll-demand-preview-20-d",
                LatestProjectionCommitId = "commit-demand-preview-20-d",
            },
            baseItem with
            {
                SeriesId = "SERIES-PREVIEW-20-E",
                WorkType = "WIRE_TO_STORAGE",
                Sublot = "SL-PREVIEW-E",
                Lifecycle = DemandSeriesLifecycleContract.Archived,
                CurrentPresence = DemandSeriesLifecycleContract.LongGoneButVisible,
                StartedAt = at.AddHours(-12),
                ArchivedAt = at.AddHours(-2),
                CurrentDemandId = "demand-preview-20-e",
                CurrentGeneration = 4,
                DemandLastSeenAt = at.AddMinutes(-7),
                GoneConfirmedAt = null,
                LiveMesFields = Fields(5),
                ExternalReadabilityState = ExternalReadabilityStates.NotReadable,
                ReadabilityBlockers = ["LONG_GONE_BUT_VISIBLE", "SERIES_ARCHIVED"],
                LastSeriesSequence = 14,
                LatestPollTraceId = "poll-demand-preview-20-e",
                LatestProjectionCommitId = "commit-demand-preview-20-e",
            },
            baseItem with
            {
                SeriesId = "SERIES-PREVIEW-20-F",
                WorkType = "WIRE_TO_GATE",
                Sublot = "SL-PREVIEW-F",
                StartedAt = at.AddHours(-16),
                CurrentDemandId = "demand-preview-20-f",
                CurrentGeneration = 1,
                DemandLastSeenAt = at.AddMinutes(-9),
                LiveMesFields = Fields(6),
                LastSeriesSequence = 3,
                LatestPollTraceId = "poll-demand-preview-20-f",
                LatestProjectionCommitId = "commit-demand-preview-20-f",
            },
        };

        return snapshot with
        {
            ExactTotalCount = items.LongLength,
            Facets = new DemandSeriesFacets(
                TrackingCount: 4,
                ArchivedCount: 2,
                VisibleCount: 3,
                GoneCount: 2,
                LongGoneButVisibleCount: 1),
            Items = items,
        };
    }

    internal static DemandSeriesDetailSnapshot CreateJourneyDemandSeriesDetail(
        string seriesId,
        string snapshotReference)
    {
        var detail = WatchDemandSeriesProductionIntegrationTests.CreateDemandSeriesDetail(
            seriesId,
            snapshotReference);
        var series = detail.Series;
        var current = series.CurrentDemand;
        var prior = current with
        {
            DemandId = "demand-preview-20-generation-1",
            Generation = 1,
            PredecessorDemandId = null,
            Status = DemandSeriesLifecycleContract.Gone,
            CreatedAt = current.CreatedAt.AddHours(-6),
            DemandLastSeenAt = current.CreatedAt.AddHours(-2),
            GoneConfirmedAt = current.CreatedAt.AddHours(-1.9),
            CreatedPollTraceId = "poll-demand-preview-generation-1",
            CreatedProjectionCommitId = "commit-demand-preview-generation-1",
            LatestProjectionCommitId = "commit-demand-preview-generation-1-gone",
            LiveMesFields = null,
            ExternalReadabilityState = ExternalReadabilityStates.NotReadable,
            ReadabilityBlockers = ["DEMAND_GONE"],
            LatestObservationPollTraceId = "poll-demand-preview-generation-1",
            LatestObservationProjectionCommitId = "commit-demand-preview-generation-1-gone",
            LatestObservationAt = current.CreatedAt.AddHours(-2),
        };
        var observed = Assert.Single(series.Events);
        DemandSeriesEventSnapshot Event(
            long sequence,
            string eventType,
            DateTimeOffset occurredAt,
            string subjectId,
            string pollTraceId) => new(
                $"event-preview-20-{sequence}",
                seriesId,
                sequence,
                eventType,
                occurredAt,
                "DEMAND",
                subjectId,
                pollTraceId,
                $"commit-preview-20-{sequence}",
                1,
                "{}");
        var events = new[]
        {
            Event(1, "DEMAND_CREATED", prior.CreatedAt, prior.DemandId, prior.CreatedPollTraceId),
            Event(2, "DEMAND_GONE_CONFIRMED", prior.GoneConfirmedAt!.Value, prior.DemandId,
                "poll-demand-preview-generation-1-gone"),
            Event(3, "DEMAND_REAPPEARED", current.CreatedAt.AddMinutes(-1), current.DemandId,
                current.CreatedPollTraceId),
            Event(4, "DEMAND_CREATED", current.CreatedAt, current.DemandId,
                current.CreatedPollTraceId),
            observed with { EventId = "event-preview-20-5", SeriesSequence = 5 },
        };

        return detail with
        {
            Series = series with
            {
                CurrentDemand = current,
                Demands = [current, prior],
                Events = events,
                LastSeriesSequence = 5,
            },
        };
    }

    internal static ReadabilityAuditListSnapshot CreateJourneyAuditList(
        ReadabilityAuditQuery query)
    {
        var normalized = query.NormalizeAndValidate();
        var snapshot = WatchReadabilityAuditProductionIntegrationTests.CreateAuditList(
            normalized,
            AuditSnapshotReference);
        var at = snapshot.Snapshot.ProjectionCommittedAt;
        var baseItem = Assert.Single(snapshot.Items);
        var areas = normalized.Filter.MesAreas.Count == 0
            ? ["A1-1", "A1-2", "A2-1", "B1-1", "B2-1"]
            : normalized.Filter.MesAreas;

        LiveMesFieldSetSnapshot Fields(int ordinal) => new(
            areas[(ordinal - 1) % areas.Count],
            $"EQP-AUDIT-{ordinal:00}",
            $"STEP-AUDIT-{ordinal:00}",
            at.AddDays(-ordinal),
            $"PKG-AUDIT-{ordinal:00}");

        var items = new ReadabilityAuditListItemSnapshot[]
        {
            baseItem,
            baseItem with
            {
                DemandId = "demand-audit-format-22",
                SeriesId = "series-audit-format-22",
                WorkType = "WIRE_TO_NITROGEN",
                Sublot = "SL-AUDIT-FORMAT",
                Generation = 1,
                PredecessorDemandId = null,
                DemandCreatedAt = at.AddHours(-2),
                DemandLastSeenAt = at.AddMinutes(-3),
                LiveMesFields = Fields(2),
                CurrentRawObservationCount = 1,
                LeadReadabilityBlocker = "INVALID_MES_FIELD_FORMAT",
                ReadabilityBlockers = ["INVALID_MES_FIELD_FORMAT"],
                LatestObservationPollTraceId = "audit-poll-format-22",
                LatestObservationProjectionCommitId = "audit-commit-format-22",
                LatestObservationAt = at.AddMinutes(-3),
            },
            baseItem with
            {
                DemandId = "demand-audit-gone-22",
                SeriesId = "series-audit-gone-22",
                WorkType = "WIRE_TO_BUFFER",
                Sublot = "SL-AUDIT-GONE",
                Generation = 3,
                DemandStatus = DemandSeriesLifecycleContract.Gone,
                SeriesCurrentPresence = DemandSeriesLifecycleContract.Gone,
                DemandCreatedAt = at.AddHours(-6),
                DemandLastSeenAt = at.AddMinutes(-20),
                GoneConfirmedAt = at.AddMinutes(-18),
                LiveMesFields = null,
                CurrentRawObservationCount = 0,
                LeadReadabilityBlocker = "DEMAND_GONE",
                ReadabilityBlockers = ["DEMAND_GONE"],
                LatestObservationPollTraceId = "audit-poll-gone-22",
                LatestObservationProjectionCommitId = "audit-commit-gone-22",
                LatestObservationAt = at.AddMinutes(-20),
            },
            baseItem with
            {
                DemandId = "demand-audit-readable-22",
                SeriesId = "series-audit-readable-22",
                WorkType = "WIRE_TO_STORAGE",
                Sublot = "SL-AUDIT-READABLE",
                Generation = 1,
                PredecessorDemandId = null,
                DemandCreatedAt = at.AddHours(-4),
                DemandLastSeenAt = at.AddMinutes(-5),
                LiveMesFields = Fields(4),
                CurrentRawObservationCount = 1,
                ExternalReadabilityState = ExternalReadabilityStates.Readable,
                LeadReadabilityBlocker = null,
                ReadabilityBlockers = [],
                LatestObservationPollTraceId = "audit-poll-readable-22",
                LatestObservationProjectionCommitId = "audit-commit-readable-22",
                LatestObservationAt = at.AddMinutes(-5),
            },
            baseItem with
            {
                DemandId = "demand-audit-readable-23",
                SeriesId = "series-audit-readable-23",
                WorkType = "WIRE_TO_GATE",
                Sublot = "SL-AUDIT-READABLE-2",
                Generation = 2,
                PredecessorDemandId = "demand-audit-readable-22-prior",
                DemandCreatedAt = at.AddHours(-10),
                DemandLastSeenAt = at.AddMinutes(-12),
                LiveMesFields = Fields(5),
                CurrentRawObservationCount = 1,
                ExternalReadabilityState = ExternalReadabilityStates.Readable,
                LeadReadabilityBlocker = null,
                ReadabilityBlockers = [],
                LatestObservationPollTraceId = "audit-poll-readable-23",
                LatestObservationProjectionCommitId = "audit-commit-readable-23",
                LatestObservationAt = at.AddMinutes(-12),
            },
        };

        return snapshot with
        {
            ExactTotalDemandCount = items.LongLength,
            Facets = new ReadabilityAuditFacets(
                [
                    new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.Readable, 2),
                    new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.NotReadable, 3),
                ],
                [
                    new ReadabilityBlockerFacetSnapshot("REQUIRED_MES_FIELD_MISSING", 1),
                    new ReadabilityBlockerFacetSnapshot("INVALID_MES_FIELD_FORMAT", 2),
                    new ReadabilityBlockerFacetSnapshot("DEMAND_GONE", 1),
                ]),
            Items = items,
        };
    }

    internal static WatchOverviewSnapshot CreateJourneyOverview(
        IReadOnlyList<string> mesAreas)
    {
        var normalizedAreas = new WatchOverviewQuery(mesAreas)
            .NormalizeAndValidate()
            .MesAreas
            ?? [];
        var demand = CreateJourneyDemandSeriesList(
            new DemandSeriesBrowseQuery(
                new DemandSeriesBrowseFilter { MesAreas = normalizedAreas }));
        var demandDetail = CreateJourneyDemandSeriesDetail(
            DemandSeriesId,
            DemandSnapshotReference);
        var audit = CreateJourneyAuditList(
            new ReadabilityAuditQuery(
                new ReadabilityAuditFilter { MesAreas = normalizedAreas }));
        var errors = CreateJourneyErrorPage(
            new ErrorSearchQuery(
                new ErrorSearchFilter(),
                ErrorSearchWindowSelection.Last7Days));
        var attention = WatchCurrentAttentionProductionIntegrationTests
            .CreateAttentionSnapshot(new CurrentIngestAttentionQuery());

        var seriesNavigation = new OverviewNavigationIntent(
            OverviewNavigationTargets.DemandSeries,
            MesAreas: normalizedAreas,
            Cursor: null);
        var trackingNavigation = seriesNavigation with
        {
            Lifecycles = [DemandSeriesLifecycleContract.Tracking],
        };
        var archivedNavigation = seriesNavigation with
        {
            Lifecycles = [DemandSeriesLifecycleContract.Archived],
        };
        var goneNavigation = seriesNavigation with
        {
            CurrentPresences = [DemandSeriesLifecycleContract.Gone],
        };
        var longGoneNavigation = seriesNavigation with
        {
            CurrentPresences = [DemandSeriesLifecycleContract.LongGoneButVisible],
        };
        var auditNavigation = new OverviewNavigationIntent(
            OverviewNavigationTargets.ReadabilityAudit,
            MesAreas: normalizedAreas,
            Cursor: null);
        var readableNavigation = auditNavigation with
        {
            ReadabilityStates = [ExternalReadabilityStates.Readable],
        };
        var notReadableNavigation = auditNavigation with
        {
            ReadabilityStates = [ExternalReadabilityStates.NotReadable],
        };
        var errorNavigation = new OverviewNavigationIntent(
            OverviewNavigationTargets.ErrorSearch,
            ErrorWindow: ErrorSearchWindowKinds.Last7Days,
            Cursor: null);
        var activeErrorNavigation = errorNavigation with
        {
            ErrorActivityStates = [ErrorSearchActivityStates.Active],
        };
        var attentionNavigation = new OverviewNavigationIntent(
            OverviewNavigationTargets.CurrentIngestAttention,
            Cursor: null);

        var readableCount = audit.Facets.ReadabilityStates
            .Single(facet => facet.State == ExternalReadabilityStates.Readable)
            .DemandCount;
        var notReadableCount = audit.Facets.ReadabilityStates
            .Single(facet => facet.State == ExternalReadabilityStates.NotReadable)
            .DemandCount;
        var attentionTypes = attention.Facets.Types
            .Select(facet => new OverviewFacetSnapshot(
                facet.Value,
                facet.ItemCount,
                attentionNavigation with { AttentionKinds = [facet.Value] }))
            .ToArray();
        var attentionSeverities = attention.Facets.Severities
            .Select(facet => new OverviewFacetSnapshot(
                facet.Value,
                facet.ItemCount,
                attentionNavigation with { AttentionSeverities = [facet.Value] }))
            .ToArray();

        var demandEvent = demandDetail.Series.Events
            .OrderByDescending(item => item.OccurredAt)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .First();
        var recentActivity = attention.Items
            .Select(item => new WatchOverviewActivitySnapshot(
                item.StableIdentity,
                item.Kind,
                item.ErrorCode ?? item.Kind,
                item.Severity,
                item.OccurredAt,
                item.SeriesId,
                item.WorkType,
                item.Evidence.PollTraceId,
                item.Evidence.ProjectionCommitId,
                item.Navigation))
            .Append(new WatchOverviewActivitySnapshot(
                demandEvent.EventId,
                "SERIES_LIFECYCLE",
                demandEvent.EventType,
                CurrentIngestAttentionSeverities.Warning,
                demandEvent.OccurredAt,
                demandEvent.SeriesId,
                demandDetail.Series.WorkType,
                demandEvent.PollTraceId,
                demandEvent.ProjectionCommitId,
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeriesDetail,
                    MesAreas: normalizedAreas,
                    SeriesId: demandEvent.SeriesId,
                    Cursor: null)))
            .OrderByDescending(activity => activity.OccurredAt)
            .ThenBy(activity => activity.EventId, StringComparer.Ordinal)
            .Take(5)
            .ToArray();

        return new WatchOverviewSnapshot(
            new OperationalSnapshotIdentity(
                "overview-preview-commit-19-22",
                232,
                PreviewErrorAsOf,
                "overview-preview-poll-19-22",
                232,
                22,
                PreviewErrorAsOf),
            normalizedAreas,
            new WatchOverviewSeriesSummary(
                demand.ExactTotalCount,
                demand.Facets.TrackingCount,
                demand.Facets.ArchivedCount,
                demand.Facets.GoneCount,
                demand.Facets.LongGoneButVisibleCount,
                seriesNavigation,
                trackingNavigation,
                archivedNavigation,
                goneNavigation,
                longGoneNavigation),
            new WatchOverviewReadabilitySummary(
                audit.ExactTotalDemandCount,
                readableCount,
                notReadableCount,
                auditNavigation,
                readableNavigation,
                notReadableNavigation),
            new WatchOverviewErrorSummary(
                errors.Items.LongCount(item =>
                    item.ActivityState == ErrorSearchActivityStates.Active),
                errors.TotalSeriesCount,
                errorNavigation,
                activeErrorNavigation,
                errorNavigation),
            new WatchOverviewAttentionSummary(
                attention.ExactTotalItemCount,
                attentionTypes,
                attentionSeverities,
                attentionNavigation),
            recentActivity,
            WatchOverviewRecentActivityStates.HasRecentHighlights,
            EmptyStateMessage: null);
    }

    private static FakeHostV2Scenario CreateScenario(
        Action<ErrorSearchQuery> rememberErrorQuery,
        JourneyOverviewAvailability? overviewAvailability = null)
    {
        var availability = overviewAvailability ?? new JourneyOverviewAvailability();
        var errorBindings = new ConcurrentDictionary<
            string,
            JourneyErrorSnapshotBinding>(StringComparer.Ordinal);
        var errorSnapshotSequence = 0;
        return new("production-preview-19-22", Credential)
        {
            Overview = FakeHostReply.Select<WatchOverviewQuery, WatchOverviewSnapshot>(query =>
                availability.Next(query)),
            DemandSeries = FakeHostReply.Select<
                DemandSeriesBrowseQuery,
                DemandSeriesListSnapshot>(query => FakeHostReply.Return(
                    CreateJourneyDemandSeriesList(query))),
            DemandSeriesDetail = FakeHostReply.Select<
                FakeHostV2DetailRequest,
                DemandSeriesDetailSnapshot>(request => FakeHostReply.Return(
                    CreateJourneyDemandSeriesDetail(
                        request.ObjectId,
                        request.SnapshotReference))),
            ReadabilityAudit = FakeHostReply.Select<
                ReadabilityAuditQuery,
                ReadabilityAuditListSnapshot>(query => FakeHostReply.Return(
                    CreateJourneyAuditList(query))),
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

    private sealed class JourneyOverviewAvailability
    {
        private readonly object _sync = new();
        private bool _failNextRefresh;
        private bool _awaitingRecovery;
        private int _requestNumber;
        private int? _failureRequestNumber;
        private int? _recoveryRequestNumber;

        public bool HasObservedFailure
        {
            get
            {
                lock (_sync)
                {
                    return _failureRequestNumber is not null;
                }
            }
        }

        public bool HasObservedRecovery
        {
            get
            {
                lock (_sync)
                {
                    return _recoveryRequestNumber is not null;
                }
            }
        }

        public int FailureRequestNumber
        {
            get
            {
                lock (_sync)
                {
                    return _failureRequestNumber ?? 0;
                }
            }
        }

        public int RecoveryRequestNumber
        {
            get
            {
                lock (_sync)
                {
                    return _recoveryRequestNumber ?? 0;
                }
            }
        }

        public void FailNextRefresh()
        {
            lock (_sync)
            {
                if (_failNextRefresh || _awaitingRecovery)
                {
                    throw new InvalidOperationException(
                        "An Overview preview failure is already armed or awaiting recovery.");
                }

                _failNextRefresh = true;
                _failureRequestNumber = null;
                _recoveryRequestNumber = null;
            }
        }

        public FakeHostReply<WatchOverviewSnapshot> Next(WatchOverviewQuery query)
        {
            bool fail;
            lock (_sync)
            {
                _requestNumber++;
                fail = _failNextRefresh;
                if (fail)
                {
                    _failNextRefresh = false;
                    _awaitingRecovery = true;
                    _failureRequestNumber = _requestNumber;
                }
                else if (_awaitingRecovery)
                {
                    _awaitingRecovery = false;
                    _recoveryRequestNumber = _requestNumber;
                }
            }

            return fail
                ? FakeHostReply.HttpFailure<WatchOverviewSnapshot>(
                    HttpStatusCode.ServiceUnavailable,
                    "HOST_UNAVAILABLE",
                    "Host 暂时离线；自动刷新会继续重试。")
                : FakeHostReply.Return(CreateJourneyOverview(query.MesAreas ?? []));
        }
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

    private static IReadOnlyDictionary<string, string> CaptureOverviewFacts(
        FlaUI.Core.AutomationElements.Window window) =>
        new[]
        {
            "SeriesSummaryValue",
            "ReadabilitySummaryValue",
            "ErrorsSummaryValue",
            "AttentionSummaryValue",
            "HostAreaScopeText",
        }.ToDictionary(
            automationId => automationId,
            automationId => TextValue(FindRequiredById(window, automationId)),
            StringComparer.Ordinal);

    private static void AssertOverviewFacts(
        FlaUI.Core.AutomationElements.Window window,
        IReadOnlyDictionary<string, string> expected)
    {
        foreach (var (automationId, expectedValue) in expected)
        {
            Assert.Equal(expectedValue, TextValue(FindRequiredById(window, automationId)));
        }
    }

    private static void CloseInfoBar(
        FlaUI.Core.AutomationElements.Window window,
        string automationId)
    {
        var infoBar = FindRequiredById(window, automationId);
        var visibleButtons = infoBar
            .FindAllDescendants(
                window.ConditionFactory.ByControlType(ControlType.Button))
            .Where(button => !button.Properties.IsOffscreen.ValueOrDefault)
            .ToArray();
        var namedCloseButton = visibleButtons.FirstOrDefault(button =>
            (button.Properties.Name.ValueOrDefault ?? string.Empty)
                .Contains("关闭", StringComparison.OrdinalIgnoreCase)
            || (button.Properties.Name.ValueOrDefault ?? string.Empty)
                .Contains("Close", StringComparison.OrdinalIgnoreCase)
            || (button.Properties.AutomationId.ValueOrDefault ?? string.Empty)
                .Contains("Close", StringComparison.OrdinalIgnoreCase));
        (namedCloseButton ?? Assert.Single(visibleButtons)).AsButton().Invoke();
    }

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
        SetNavigationPaneExpanded(window, expanded: false);
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

    private static void AssertRepresentativeAnchorVisible(
        FlaUI.Core.AutomationElements.Window window,
        string automationId,
        string context)
    {
        var anchor = FindRequiredById(window, automationId);
        var anchorBounds = anchor.BoundingRectangle;
        var windowBounds = window.BoundingRectangle;
        var intersectsWindow = anchorBounds.Width > 0
            && anchorBounds.Height > 0
            && anchorBounds.Right > windowBounds.Left
            && anchorBounds.Left < windowBounds.Right
            && anchorBounds.Bottom > windowBounds.Top
            && anchorBounds.Top < windowBounds.Bottom;
        var isInRepresentativeTopRegion = anchorBounds.Top
            < windowBounds.Top + (windowBounds.Height * 0.4);
        Assert.False(
            anchor.Properties.IsOffscreen.ValueOrDefault
                || !intersectsWindow
                || !isInRepresentativeTopRegion,
            $"{context}: {automationId} is not in the representative top region. "
            + $"anchor={anchorBounds}; window={windowBounds}; "
            + DescribeScrollAncestors(anchor));
    }

    private static string DescribeScrollAncestors(AutomationElement element)
    {
        var descriptions = new List<string>();
        for (var current = element.Parent; current is not null; current = current.Parent)
        {
            if (!current.Patterns.Scroll.IsSupported)
            {
                continue;
            }

            var scroll = current.Patterns.Scroll.PatternOrDefault;
            if (scroll is null)
            {
                continue;
            }

            var automationId = current.Properties.AutomationId.ValueOrDefault;
            descriptions.Add(
                $"scrollAncestor={automationId ?? "<none>"} "
                + $"vertical={scroll.VerticalScrollPercent.ValueOrDefault:0.##} "
                + $"view={scroll.VerticalViewSize.ValueOrDefault:0.##} "
                + $"scrollable={scroll.VerticallyScrollable.ValueOrDefault}");
        }

        return descriptions.Count == 0
            ? "No UIA ScrollPattern ancestor was exposed."
            : string.Join("; ", descriptions);
    }

    private static void PrepareRepresentativeFirstScreen(
        FlaUI.Core.AutomationElements.Window window,
        string pageAnchorAutomationId,
        string firstContentAutomationId,
        string context)
    {
        ResetVisibleVerticalScrollBars(window);
        WaitUntil(
            () => IsVisibleInWindow(
                window,
                FindById(window, firstContentAutomationId)),
            $"{context}: visible first content {firstContentAutomationId}",
            StepTimeout);
        AssertRepresentativeAnchorVisible(window, pageAnchorAutomationId, context);
    }

    private static void EnsureVisibleIfOffscreen(
        FlaUI.Core.AutomationElements.Window window,
        AutomationElement element,
        string context)
    {
        if (IsVisibleInWindow(window, element))
        {
            return;
        }

        element.Focus();
        WaitUntil(
            () => IsVisibleInWindow(window, element),
            context,
            StepTimeout);
    }

    private static bool IsVisibleInWindow(
        FlaUI.Core.AutomationElements.Window window,
        AutomationElement? element)
    {
        if (element is null || element.Properties.IsOffscreen.ValueOrDefault)
        {
            return false;
        }

        var bounds = element.BoundingRectangle;
        var windowBounds = window.BoundingRectangle;
        return bounds.Width > 0
            && bounds.Height > 0
            && bounds.Right > windowBounds.Left
            && bounds.Left < windowBounds.Right
            && bounds.Bottom > windowBounds.Top
            && bounds.Top < windowBounds.Bottom;
    }

    private static void ResetVisibleVerticalScrollBars(
        FlaUI.Core.AutomationElements.Window window)
    {
        var scrollBars = window.FindAllDescendants(
            window.ConditionFactory.ByControlType(ControlType.ScrollBar));
        foreach (var scrollBar in scrollBars)
        {
            var bounds = scrollBar.BoundingRectangle;
            if (scrollBar.Properties.IsOffscreen.ValueOrDefault
                || bounds.Height <= bounds.Width
                || !scrollBar.Patterns.RangeValue.IsSupported)
            {
                continue;
            }

            var range = scrollBar.Patterns.RangeValue.PatternOrDefault;
            if (range is not null && !range.IsReadOnly.ValueOrDefault)
            {
                range.SetValue(range.Minimum.ValueOrDefault);
            }
        }
    }

    private static void SetNavigationPaneExpanded(
        FlaUI.Core.AutomationElements.Window window,
        bool expanded)
    {
        var navigationItem = FindRequiredById(window, "OverviewNavigationItem");
        bool IsExpanded() => navigationItem.BoundingRectangle.Width >= 120;
        if (IsExpanded() == expanded)
        {
            return;
        }

        FindRequiredById(window, "NavigationToggleButton").AsButton().Invoke();
        WaitUntil(
            () => IsExpanded() == expanded,
            expanded
                ? "the expanded production navigation pane"
                : "the compact production navigation pane",
            StepTimeout);
    }

    private static AutomationElement FindRequiredByName(
        FlaUI.Core.AutomationElements.Window window,
        string automationName) =>
        window.FindFirstDescendant(window.ConditionFactory.ByName(automationName))
        ?? throw new Xunit.Sdk.XunitException($"UIA element not found by name: {automationName}");

    private static FlaUI.Core.AutomationElements.ToggleButton FindRequiredButtonByName(
        FlaUI.Core.AutomationElements.Window window,
        string automationName) =>
        window.FindFirstDescendant(
                window.ConditionFactory.ByControlType(ControlType.Button)
                    .And(window.ConditionFactory.ByName(automationName)))
            ?.AsToggleButton()
        ?? throw new Xunit.Sdk.XunitException(
            $"UIA button not found by name: {automationName}");

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

    private static void PrepareAreaProfiles(string localAppData)
    {
        var directory = Path.Combine(localAppData, "MesIngest.Watch", "area-filters");
        Directory.CreateDirectory(directory);

        WriteAreaProfile(
            directory,
            "东区",
            "# 东区生产范围\n"
            + "A1-1\nA1-2\nA2-1\nA2-2\n"
            + "B1-1\nB1-2\nB2-1\nB2-2\n"
            + "C1-1\nC1-2\nC2-1\nC2-2\n",
            "2026-08-14T05:02:00Z");
        WriteAreaProfile(
            directory,
            "西区",
            "# 西区生产范围\nD1-1\nD1-2\nD1-3\nD2-1\nD2-2\nD2-3\nD3-1\nD3-2\nD3-3\n",
            "2026-08-13T09:30:00Z");
        WriteAreaProfile(
            directory,
            "焊线区域",
            "# 焊线区域\n"
            + "W1-1\nW1-2\nW1-3\nW1-4\nW1-5\nW1-6\nW1-7\nW1-8\n"
            + "W2-1\nW2-2\nW2-3\nW2-4\nW2-5\nW2-6\nW2-7\nW2-8\n"
            + "W3-1\nW3-2\nW3-3\nW3-4\nW3-5\nW3-6\nW3-7\nW3-8\n",
            "2026-08-10T00:15:00Z");
        WriteAreaProfile(
            directory,
            "临时范围",
            "# 待修复的临时范围\nA1-1\nAREA-INVALID\n",
            "2026-08-14T04:58:00Z");

        File.WriteAllText(
            Path.Combine(directory, ".active-profile"),
            "{\"version\":1,\"profileName\":\"东区\","
            + "\"mesAreas\":["
            + "\"A1-1\",\"A1-2\",\"A2-1\",\"A2-2\","
            + "\"B1-1\",\"B1-2\",\"B2-1\",\"B2-2\","
            + "\"C1-1\",\"C1-2\",\"C2-1\",\"C2-2\"],"
            + "\"appliedAt\":\"2026-08-14T05:00:00+00:00\"}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteAreaProfile(
        string directory,
        string profileName,
        string content,
        string lastModifiedAt)
    {
        var path = Path.Combine(directory, $"{profileName}.txt");
        File.WriteAllText(
            path,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.SetLastWriteTimeUtc(
            path,
            DateTimeOffset.Parse(lastModifiedAt, CultureInfo.InvariantCulture).UtcDateTime);
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
