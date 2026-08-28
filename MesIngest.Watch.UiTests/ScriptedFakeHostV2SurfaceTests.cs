using System.Net;
using System.Net.Http;
using MesIngest.Core;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

public sealed class ScriptedFakeHostV2SurfaceTests
{
    [Fact]
    public async Task Every_v2_watch_read_rejects_requests_without_the_session_bearer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("v2-auth-surface", "surface-secret"),
            cancellationToken);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
        (string Endpoint, HttpStatusCode ExpectedStatus)[] endpoints =
        [
            ("/api/v2/contract", HttpStatusCode.Unauthorized),
            ("/api/v2/watch-overview", HttpStatusCode.Unauthorized),
            ("/api/v2/demand-series", HttpStatusCode.Unauthorized),
            ("/api/v2/demand-series/series-18?snapshot=snapshot-18", HttpStatusCode.Unauthorized),
            ("/api/v2/readability-audit", HttpStatusCode.Unauthorized),
            ("/api/v2/readability-audit/demand-18?snapshot=snapshot-18", HttpStatusCode.Unauthorized),
            ("/api/v2/error-search", HttpStatusCode.Unauthorized),
            ("/api/v2/error-search/series-18?snapshot=snapshot-18", HttpStatusCode.Unauthorized),
            (
                "/api/v2/error-search/series-18/evidence/evidence-18/raw-observations"
                    + "?snapshot=snapshot-18&fields=area&maxItems=1",
                HttpStatusCode.Forbidden),
            ("/api/v2/current-ingest-attention", HttpStatusCode.Unauthorized),
        ];

        foreach (var (endpoint, expectedStatus) in endpoints)
        {
            using var response = await client.GetAsync(endpoint, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            Assert.Equal(expectedStatus, response.StatusCode);
            Assert.Contains(
                expectedStatus == HttpStatusCode.Forbidden
                    ? $"\"code\":\"{ErrorSearchErrorCodes.RawAccessDenied}\""
                    : "\"code\":\"UNAUTHORIZED\"",
                body,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Production_contract_gate_rejects_a_scripted_non_exact_host_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("v2-contract-mismatch", "surface-secret")
            {
                Contract = FakeHostReply.Return(
                    FakeHostV2ContractSnapshot.Exact with
                    {
                        ContractVersion = "2026.08.new-mes-ingest.v2-incompatible",
                    }),
            },
            cancellationToken);
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings(host.BaseUrl, "surface-secret", 30));

        var failure = await Assert.ThrowsAsync<WatchHostQueryException>(
            () => client.VerifyContractAsync(cancellationToken));

        Assert.Equal(WatchHostFailureKind.Contract, failure.Kind);
        Assert.Equal(NewMesIngestContractMismatchException.ErrorCode, failure.ErrorCode);
        Assert.DoesNotContain("surface-secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_contract_gate_also_rejects_schema_and_capability_drift()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        FakeHostV2ContractSnapshot[] incompatibleContracts =
        [
            FakeHostV2ContractSnapshot.Exact with
            {
                SchemaVersion = NewMesIngestContract.SchemaVersion + 1,
            },
            FakeHostV2ContractSnapshot.Exact with
            {
                CapabilityIds = FakeHostV2ContractSnapshot.Exact.CapabilityIds.Skip(1).ToArray(),
            },
        ];

        foreach (var contract in incompatibleContracts)
        {
            await using var host = await ScriptedFakeHost.StartV2Async(
                new FakeHostV2Scenario("v2-contract-identity-drift", "surface-secret")
                {
                    Contract = FakeHostReply.Return(contract),
                },
                cancellationToken);
            using var client = MesIngestV2ApiClient.CreateForHost(
                new WatchHostSettings(host.BaseUrl, "surface-secret", 30));

            var failure = await Assert.ThrowsAsync<WatchHostQueryException>(
                () => client.VerifyContractAsync(cancellationToken));

            Assert.Equal(WatchHostFailureKind.Contract, failure.Kind);
            Assert.Equal(NewMesIngestContractMismatchException.ErrorCode, failure.ErrorCode);
        }
    }

    [Fact]
    public async Task Demand_series_page_and_detail_use_production_http_json_and_session_auth()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (page, detail) = CreateDemandSeriesSurface();
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("v2-demand-surface", "surface-secret")
            {
                DemandSeries = FakeHostReply.Return(page),
                DemandSeriesDetail = FakeHostReply.Return(detail),
            },
            cancellationToken);
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings(host.BaseUrl, "surface-secret", 30));
        var query = new DemandSeriesBrowseQuery(
            new DemandSeriesBrowseFilter
            {
                Lifecycles = [DemandSeriesLifecycleContract.Tracking],
                MesAreas = ["A1-1"],
            },
            PageSize: 2);

        await client.VerifyContractAsync(cancellationToken);
        var actualPage = await client.FetchDemandSeriesAsync(query, cancellationToken);
        var actualDetail = await client.FetchDemandSeriesDetailAsync(
            "series-18",
            "snapshot+/=",
            cancellationToken);

        Assert.Equal("series-18", Assert.Single(actualPage.Items).SeriesId);
        Assert.Equal("series-18", actualDetail.Series.SeriesId);
        Assert.Equal(
            MesObservationAssignment.Assigned,
            Assert.Single(actualDetail.Series.RawObservations).Assignment);
        Assert.Contains(
            host.Timeline,
            entry => entry is
            {
                Operation: FakeHostOperation.DemandSeriesV2,
                State: FakeHostRequestState.Completed,
                Endpoint: "/api/v2/demand-series?lifecycle=TRACKING&area=A1-1"
                    + "&pageSize=2&page=1&order=STARTED_AT_DESC_SERIES_ID_ASC",
            });
        Assert.Contains(
            host.Timeline,
            entry => entry is
            {
                Operation: FakeHostOperation.DemandSeriesDetailV2,
                State: FakeHostRequestState.Completed,
                Endpoint: "/api/v2/demand-series/series-18?snapshot=snapshot%2B%2F%3D",
            });
    }

    [Fact]
    public async Task Failed_frozen_demand_page_keeps_the_committed_query_without_first_page_fallback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (page, _) = CreateDemandSeriesSurface();
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("v2-demand-page-failure", "surface-secret")
            {
                DemandSeries = FakeHostReply.Sequence<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>(
                    FakeHostReply.Return(page),
                    FakeHostReply.HttpFailure<DemandSeriesListSnapshot>(
                        HttpStatusCode.Gone,
                        DemandSeriesBrowseErrorCodes.SnapshotNotFound,
                        "The selected snapshot no longer exists.")),
            },
            cancellationToken);
        using var session = new WatchV2WorkspaceSession();
        var firstQuery = new DemandSeriesBrowseQuery(
            new DemandSeriesBrowseFilter
            {
                Lifecycles = [DemandSeriesLifecycleContract.Tracking],
                MesAreas = ["A1-1"],
            },
            PageSize: 2);
        var failedPageQuery = firstQuery with
        {
            PageNumber = 2,
            SnapshotReference = page.SnapshotReference,
        };

        await session.ApplyAsync(
            new WatchHostSettings(host.BaseUrl, "surface-secret", 30),
            cancellationToken);
        await session.RefreshDemandSeriesAsync(firstQuery, cancellationToken);
        var committedSnapshot = session.State.DemandSeries.Snapshot;
        await session.RefreshDemandSeriesAsync(failedPageQuery, cancellationToken);

        var view = session.State.DemandSeries;
        Assert.Same(committedSnapshot, view.Snapshot);
        Assert.Equal(
            WatchV2QueryKeys.DemandSeries(firstQuery.NormalizeAndValidate()),
            view.CommittedQueryKey);
        Assert.Equal(
            WatchV2QueryKeys.DemandSeries(failedPageQuery.NormalizeAndValidate()),
            view.PendingQueryKey);
        Assert.Equal(WatchHostFailureKind.ServerQuery, view.FailureKind);
        Assert.Equal(DemandSeriesBrowseErrorCodes.SnapshotNotFound, view.FailureCode);
        Assert.Equal(
            2,
            host.Timeline.Count(entry => entry is
            {
                Operation: FakeHostOperation.DemandSeriesV2,
                State: FakeHostRequestState.Started,
            }));
    }

    [Fact]
    public async Task Production_client_reaches_every_v2_watch_read_through_one_authenticated_http_surface()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (demandPage, demandDetail) = CreateDemandSeriesSurface();
        var (auditPage, auditDetail) = CreateReadabilityAuditSurface();
        var (errorPage, errorDetail, rawEvidence) = CreateErrorSearchSurface();
        var overview = CreateOverviewSurface();
        var attention = CreateCurrentAttentionSurface();
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("v2-all-reads", "surface-secret")
            {
                Overview = FakeHostReply.Return(overview),
                DemandSeries = FakeHostReply.Return(demandPage),
                DemandSeriesDetail = FakeHostReply.Return(demandDetail),
                ReadabilityAudit = FakeHostReply.Return(auditPage),
                ReadabilityAuditDetail = FakeHostReply.Return(auditDetail),
                ErrorSearch = FakeHostReply.Return(errorPage),
                ErrorSearchDetail = FakeHostReply.Return(errorDetail),
                ErrorSearchRawEvidence = FakeHostReply.Return(rawEvidence),
                CurrentAttention = FakeHostReply.Return(attention),
            },
            cancellationToken);
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings(host.BaseUrl, "surface-secret", 30));
        var demandQuery = new DemandSeriesBrowseQuery(
            new DemandSeriesBrowseFilter
            {
                Lifecycles = [DemandSeriesLifecycleContract.Tracking],
                MesAreas = ["A1-1"],
            },
            PageSize: 2);
        var auditQuery = new ReadabilityAuditQuery(
            new ReadabilityAuditFilter
            {
                ReadabilityStates = [ExternalReadabilityStates.NotReadable],
                MesAreas = ["A1-1"],
            },
            PageSize: 3);
        var errorQuery = new ErrorSearchQuery(
            new ErrorSearchFilter(),
            ErrorSearchWindowSelection.Last7Days,
            PageSize: 3);
        var rawQuery = new ErrorSearchRawEvidenceQuery(
            [ErrorSearchRawEvidenceFields.Area, ErrorSearchRawEvidenceFields.Sublot],
            MaxItems: 2);
        var attentionQuery = new CurrentIngestAttentionQuery(
            PageSize: 3,
            Kinds: [CurrentIngestAttentionKinds.SeriesError],
            Severities: [CurrentIngestAttentionSeverities.Error]);

        await client.VerifyContractAsync(cancellationToken);
        var actualOverview = await client.FetchOverviewAsync(
            new WatchOverviewQuery(["A1-1"]),
            cancellationToken);
        var actualDemandPage = await client.FetchDemandSeriesAsync(demandQuery, cancellationToken);
        var actualDemandDetail = await client.FetchDemandSeriesDetailAsync(
            "series-18",
            demandDetail.SnapshotReference,
            cancellationToken);
        var actualAuditPage = await client.FetchReadabilityAuditAsync(auditQuery, cancellationToken);
        var actualAuditDetail = await client.FetchReadabilityAuditDetailAsync(
            "demand-audit-18",
            auditDetail.SnapshotReference,
            cancellationToken);
        var actualErrorPage = await client.FetchErrorSearchAsync(errorQuery, cancellationToken);
        var actualErrorDetail = await client.FetchErrorSearchDetailAsync(
            "series-error-18",
            errorDetail.SnapshotReference,
            cancellationToken);
        var actualRaw = await client.FetchErrorRawEvidenceAsync(
            "series-error-18",
            "evidence-18",
            rawEvidence.SnapshotReference,
            rawQuery,
            cancellationToken);
        var actualAttention = await client.FetchCurrentAttentionAsync(
            attentionQuery,
            cancellationToken);

        Assert.Equal("overview-commit-18", actualOverview.Snapshot.ProjectionCommitId);
        Assert.Equal("series-18", Assert.Single(actualDemandPage.Items).SeriesId);
        Assert.Equal("series-18", actualDemandDetail.Series.SeriesId);
        Assert.Equal("demand-audit-18", Assert.Single(actualAuditPage.Items).DemandId);
        Assert.Equal("demand-audit-18", actualAuditDetail.Demand.DemandId);
        Assert.Equal("series-error-18", Assert.Single(actualErrorPage.Items).SeriesId);
        Assert.Equal("series-error-18", actualErrorDetail.Series.SeriesId);
        Assert.Equal("evidence-18", actualRaw.EvidenceId);
        Assert.Equal("attention-commit-18", actualAttention.Snapshot.ProjectionCommitId);
        var completedOperations = host.Timeline
            .Where(entry => entry.State == FakeHostRequestState.Completed)
            .Select(entry => entry.Operation)
            .ToHashSet();
        FakeHostOperation[] requiredOperations =
        [
            FakeHostOperation.ContractV2,
            FakeHostOperation.OverviewV2,
            FakeHostOperation.DemandSeriesV2,
            FakeHostOperation.DemandSeriesDetailV2,
            FakeHostOperation.ReadabilityAuditV2,
            FakeHostOperation.ReadabilityAuditDetailV2,
            FakeHostOperation.ErrorSearchV2,
            FakeHostOperation.ErrorSearchDetailV2,
            FakeHostOperation.ErrorSearchRawEvidenceV2,
            FakeHostOperation.CurrentAttentionV2,
        ];
        Assert.All(requiredOperations, operation => Assert.Contains(operation, completedOperations));
        Assert.Equal(
            [
                "/api/v2/contract",
                "/api/v2/watch-overview?area=A1-1",
                "/api/v2/demand-series?lifecycle=TRACKING&area=A1-1&pageSize=2&page=1"
                    + "&order=STARTED_AT_DESC_SERIES_ID_ASC",
                "/api/v2/demand-series/series-18?snapshot=snapshot%2B%2F%3D",
                "/api/v2/readability-audit?state=NOT_READABLE&area=A1-1&pageSize=3&page=1"
                    + "&order=NOT_READABLE_FIRST_LEAD_PRIORITY_DEMAND_LAST_SEEN_DESC_DEMAND_ID_ASC",
                "/api/v2/readability-audit/demand-audit-18?snapshot=audit-snapshot-18",
                "/api/v2/error-search?window=LAST_7_DAYS&pageSize=3",
                "/api/v2/error-search/series-error-18?snapshot=error-snapshot-18",
                "/api/v2/error-search/series-error-18/evidence/evidence-18/raw-observations"
                    + "?snapshot=error-snapshot-18&fields=area%2Csublot&maxItems=2",
                "/api/v2/current-ingest-attention?pageSize=3&pageNumber=1"
                    + "&kind=SERIES_ERROR&severity=ERROR",
            ],
            host.Timeline
                .Where(entry => entry.State == FakeHostRequestState.Completed)
                .Select(entry => entry.Endpoint));
    }

    [Fact]
    public async Task Scripted_host_drives_all_storage_and_history_protection_states_then_retains_recovery_when_offline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var normal = CreateProtectionSurface(StoragePressureStatuses.Healthy, 20m);
        var warning = CreateProtectionSurface(StoragePressureStatuses.Warning, 14.5m);
        var paused = CreateProtectionSurface(StoragePressureStatuses.Paused, 9.5m);
        var reset = CreateProtectionSurface(
            StoragePressureStatuses.Healthy,
            20m,
            historyResetRequired: true);
        var recovered = CreateProtectionSurface(StoragePressureStatuses.Healthy, 22m);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("ticket-21-protection", "ticket-21-secret")
            {
                CurrentAttention = FakeHostReply.Sequence<
                    CurrentIngestAttentionQuery,
                    CurrentIngestAttentionSnapshot>(
                    FakeHostReply.Return(normal),
                    FakeHostReply.Return(warning),
                    FakeHostReply.Return(paused),
                    FakeHostReply.Return(reset),
                    FakeHostReply.Return(recovered),
                    FakeHostReply.Fail<CurrentIngestAttentionSnapshot>(
                        WatchHostFailureKind.Network,
                        "/api/v2/current-ingest-attention",
                        "scripted Host offline")),
            },
            cancellationToken);
        using var session = new WatchV2WorkspaceSession();
        await session.ApplyAsync(
            new WatchHostSettings(host.BaseUrl, "ticket-21-secret", 30),
            cancellationToken);
        var expectedStatuses = new[]
        {
            "存储与历史保护正常",
            "存储空间严重告警",
            "StoragePressurePause",
            "历史重置待确认",
            "存储与历史保护正常",
        };

        foreach (var expectedStatus in expectedStatuses)
        {
            await session.RefreshCurrentAttentionAsync(
                new CurrentIngestAttentionQuery(),
                cancellationToken);
            Assert.True(
                session.State.CurrentAttention.LastFailureAt is null,
                session.State.CurrentAttention.ErrorMessage);
            var presentation = WatchOverviewPresentation.Project(
                session.State,
                WatchAreaDisplayContext.AllAreas);
            Assert.Equal(expectedStatus, presentation.Protection.Status);
        }

        var recoveredSnapshot = Assert.IsType<CurrentIngestAttentionSnapshot>(
            session.State.CurrentAttention.Snapshot);
        Assert.Equal(22m, recoveredSnapshot.StoragePressure?.Space.AvailablePercent);

        await session.RefreshCurrentAttentionAsync(
            new CurrentIngestAttentionQuery(),
            cancellationToken);

        Assert.Same(recoveredSnapshot, session.State.CurrentAttention.Snapshot);
        Assert.NotNull(session.State.CurrentAttention.LastFailureAt);
        Assert.Contains(
            "scripted Host offline",
            session.State.CurrentAttention.ErrorMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_host_b_cancels_slow_host_a_http_work_and_rejects_its_late_overview()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var gate = new FakeHostGate();
        var hostBOverview = CreateOverviewSurface() with
        {
            Snapshot = CreateOverviewSurface().Snapshot with
            {
                ProjectionCommitId = "host-b-overview",
            },
        };
        await using var hostA = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("v2-host-a", "secret-a")
            {
                Overview = FakeHostReply.After(
                    gate,
                    CreateOverviewSurface(),
                    completeAfterCancellation: true),
            },
            cancellationToken);
        await using var hostB = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("v2-host-b", "secret-b")
            {
                Overview = FakeHostReply.Return(hostBOverview),
            },
            cancellationToken);
        using var session = new WatchV2WorkspaceSession();
        await session.ApplyAsync(
            new WatchHostSettings(hostA.BaseUrl, "secret-a", 30),
            cancellationToken);

        var slowRefresh = session.RefreshOverviewAsync(
            new WatchOverviewQuery(["A1-1"]),
            cancellationToken);
        await hostA.WaitForAsync(
            "v2-host-a",
            FakeHostOperation.OverviewV2,
            FakeHostRequestState.Started,
            cancellationToken);
        await session.ApplyAsync(
            new WatchHostSettings(hostB.BaseUrl, "secret-b", 30),
            cancellationToken);

        Assert.Equal(2, session.State.HostGeneration);
        Assert.Equal(hostB.BaseUrl, session.State.BaseUrl);
        Assert.Equal(WatchHostConnectionStatus.Connected, session.State.ConnectionStatus);
        Assert.Null(session.State.Overview.Snapshot);
        await session.RefreshOverviewAsync(
            new WatchOverviewQuery(["A1-1"]),
            cancellationToken);
        Assert.Equal(
            "host-b-overview",
            session.State.Overview.Snapshot!.Snapshot.ProjectionCommitId);
        gate.Release();
        await slowRefresh;
        await hostA.WaitForAsync(
            "v2-host-a",
            FakeHostOperation.OverviewV2,
            FakeHostRequestState.CompletedAfterCancellation,
            cancellationToken);

        Assert.Equal(2, session.State.HostGeneration);
        Assert.Equal(
            "host-b-overview",
            session.State.Overview.Snapshot!.Snapshot.ProjectionCommitId);
        Assert.Equal("area=A1-1", session.State.Overview.CommittedQueryKey);
        Assert.Contains(
            hostB.Timeline,
            entry => entry.Operation == FakeHostOperation.OverviewV2
                && entry.State == FakeHostRequestState.Completed);
    }

    private static (DemandSeriesListSnapshot Page, DemandSeriesDetailSnapshot Detail)
        CreateDemandSeriesSurface()
    {
        var at = DateTimeOffset.Parse("2026-08-14T02:03:04Z");
        var identity = new DemandSeriesSnapshotIdentity(
            HistoryEpoch.CreateNew(),
            "commit-18",
            18,
            at,
            "poll-18");
        var item = new DemandSeriesListItemSnapshot(
            "series-18",
            "WORK-18",
            "SUBLOT-18",
            DemandSeriesLifecycleContract.Tracking,
            DemandSeriesLifecycleContract.Visible,
            at,
            null,
            "demand-18",
            1,
            "VISIBLE",
            at,
            null,
            new LiveMesFieldSetSnapshot("A1-1", "EQP-18", "STEP-18", at, "PKG-18"),
            ExternalReadabilityStates.Readable,
            [],
            1,
            "poll-18",
            "commit-18");
        var page = new DemandSeriesListSnapshot(
            identity,
            "snapshot+/=",
            new DemandSeriesBrowseFilter
            {
                Lifecycles = [DemandSeriesLifecycleContract.Tracking],
                MesAreas = ["A1-1"],
            }.Normalize(),
            DemandSeriesBrowseOrder.Default,
            1,
            new DemandSeriesFacets(1, 0, 1, 0, 0),
            2,
            1,
            1,
            [item],
            null,
            false);
        var demand = new TransportDemandSnapshot(
            "demand-18",
            "series-18",
            1,
            null,
            "VISIBLE",
            at,
            at,
            null,
            "poll-18",
            "commit-18",
            "commit-18",
            item.LiveMesFields,
            ExternalReadabilityStates.Readable,
            [],
            "poll-18",
            "commit-18",
            at);
        var series = new DemandSeriesSnapshot(
            "series-18",
            "WORK-18",
            "SUBLOT-18",
            DemandSeriesLifecycleContract.Tracking,
            DemandSeriesLifecycleContract.Visible,
            at,
            "poll-18",
            "commit-18",
            "commit-18",
            demand,
            [demand],
            [
                new DemandRawObservationSnapshot(
                    0,
                    "poll-18",
                    "commit-18",
                    MesObservationAssignment.Assigned,
                    "series-18",
                    "demand-18",
                    "WORK-18",
                    "SUBLOT-18",
                    "A1-1",
                    "EQP-18",
                    "STEP-18",
                    at,
                    "PKG-18",
                    at),
            ],
            [],
            [],
            [],
            LastSeriesSequence: 1);
        return (page, new DemandSeriesDetailSnapshot(identity, "snapshot+/=", series));
    }

    private static (ReadabilityAuditListSnapshot Page, ReadabilityAuditDetailSnapshot Detail)
        CreateReadabilityAuditSurface()
    {
        var at = DateTimeOffset.Parse("2026-08-14T03:04:05Z");
        var identity = new ReadabilityAuditSnapshotIdentity(
            HistoryEpoch.CreateNew(),
            "audit-commit-18",
            19,
            at,
            "audit-poll-18",
            7);
        var filter = new ReadabilityAuditFilter
        {
            ReadabilityStates = [ExternalReadabilityStates.NotReadable],
            MesAreas = ["A1-1"],
        }.Normalize();
        var item = new ReadabilityAuditListItemSnapshot(
            "demand-audit-18",
            "series-audit-18",
            "WORK-AUDIT-18",
            "SUBLOT-AUDIT-18",
            1,
            null,
            "VISIBLE",
            DemandSeriesLifecycleContract.Tracking,
            DemandSeriesLifecycleContract.Visible,
            true,
            at.AddMinutes(-5),
            at,
            null,
            new LiveMesFieldSetSnapshot("A1-1", "EQP-AUDIT", "STEP-AUDIT", at, "PKG-AUDIT"),
            1,
            ExternalReadabilityStates.NotReadable,
            "REQUIRED_MES_FIELD_MISSING",
            ["REQUIRED_MES_FIELD_MISSING"],
            "audit-poll-18",
            "audit-commit-18",
            at);
        var page = new ReadabilityAuditListSnapshot(
            identity,
            "audit-snapshot-18",
            filter,
            ReadabilityAuditOrder.Default,
            1,
            new ReadabilityAuditFacets(
                [new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.NotReadable, 1)],
                [new ReadabilityBlockerFacetSnapshot("REQUIRED_MES_FIELD_MISSING", 1)]),
            3,
            1,
            1,
            [item],
            null,
            false);
        var detail = new ReadabilityAuditDetailSnapshot(
            identity,
            page.SnapshotReference,
            item,
            new ReadabilityAuditSeriesSnapshot(
                item.SeriesId,
                item.WorkType,
                item.Sublot,
                item.SeriesLifecycle,
                item.SeriesCurrentPresence,
                at.AddHours(-1),
                null,
                item.DemandId),
            [],
            [],
            [],
            new ReadabilityAuditPollTraceSnapshot(
                "audit-poll-18",
                "query-v18",
                "SUCCESS",
                at.AddSeconds(-1),
                at,
                1,
                "audit-digest-18",
                "audit-commit-18",
                19));
        return (page, detail);
    }

    private static (
        ErrorSearchListSnapshot Page,
        ErrorSearchDetailSnapshot Detail,
        ErrorSearchRawEvidenceSnapshot RawEvidence) CreateErrorSearchSurface()
    {
        var at = DateTimeOffset.Parse("2026-08-14T04:05:06Z");
        var identity = new ErrorSearchSnapshotIdentity(
            HistoryEpoch.FromGuid(Guid.Parse("88888888-8888-8888-8888-888888888888")),
            at,
            "error-commit-18",
            20,
            at,
            "error-poll-18");
        var filter = new ErrorSearchFilter().Normalize();
        var window = new ErrorSearchResolvedWindow(
            ErrorSearchWindowKinds.Last7Days,
            at.AddDays(-7),
            at);
        var item = new ErrorSearchListItemSnapshot(
            "series-error-18",
            "WORK-ERROR-18",
            "SUBLOT-ERROR-18",
            ErrorSearchActivityStates.Active,
            [new ErrorSearchMatchedErrorSnapshot("REQUIRED_MES_FIELD_MISSING", "DATA_QUALITY", "ERROR")],
            at,
            1,
            1,
            "A1-1",
            ErrorSearchMesAreaAvailability.CurrentTrusted);
        var page = new ErrorSearchListSnapshot(
            "error-snapshot-18",
            identity,
            filter,
            window,
            ErrorSearchOrder.Default,
            1,
            new ErrorSearchFacets([], []),
            3,
            1,
            1,
            [item],
            null,
            false);
        var detail = new ErrorSearchDetailSnapshot(
            page.SnapshotReference,
            identity,
            filter,
            window,
            ErrorSearchOrder.Default,
            item,
            []);
        var raw = new ErrorSearchRawEvidenceSnapshot(
            page.SnapshotReference,
            identity,
            item.SeriesId,
            "period-18",
            "evidence-18",
            "error-poll-18",
            "error-commit-18",
            "demand-error-18",
            [ErrorSearchRawEvidenceFields.Area, ErrorSearchRawEvidenceFields.Sublot],
            0,
            new ErrorSearchRawEvidenceLimitsSnapshot(
                ErrorSearchRawEvidenceLimits.MaximumItems,
                2_048,
                65_536),
            0,
            []);
        return (page, detail, raw);
    }

    private static WatchOverviewSnapshot CreateOverviewSurface()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var identity = new OperationalSnapshotIdentity(
            "overview-commit-18",
            21,
            at,
            "overview-poll-18",
            28,
            7,
            at);
        var series = new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries);
        var audit = new OverviewNavigationIntent(OverviewNavigationTargets.ReadabilityAudit);
        var errors = new OverviewNavigationIntent(OverviewNavigationTargets.ErrorSearch);
        var attention = new OverviewNavigationIntent(OverviewNavigationTargets.CurrentIngestAttention);
        return new WatchOverviewSnapshot(
            identity,
            ["A1-1"],
            new WatchOverviewSeriesSummary(1, 1, 0, 0, 0, series, series, series, series, series),
            new WatchOverviewReadabilitySummary(1, 0, 1, audit, audit, audit),
            new WatchOverviewErrorSummary(1, 1, errors, errors, errors),
            new WatchOverviewAttentionSummary(1, [], [], attention),
            [],
            WatchOverviewRecentActivityStates.NoRecentHighlights,
            WatchOverviewRecentActivityStates.NoRecentHighlightsMessage);
    }

    private static CurrentIngestAttentionSnapshot CreateCurrentAttentionSurface()
    {
        var at = DateTimeOffset.Parse("2026-08-14T06:07:08Z");
        var epoch = HistoryEpoch.FromGuid(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        return new CurrentIngestAttentionSnapshot(
            new OperationalSnapshotIdentity(
                "attention-commit-18",
                22,
                at,
                "attention-poll-18",
                29,
                7,
                at,
                HistoryEpoch: epoch),
            0,
            new CurrentIngestAttentionFacets([], []),
            CurrentIngestAttentionOrder.Default,
            3,
            1,
            0,
            [CurrentIngestAttentionKinds.SeriesError],
            [CurrentIngestAttentionSeverities.Error],
            [],
            HistoryCleanupStateSnapshot.NotRun,
            new StoragePressureStateSnapshot(
                StoragePressureStatuses.Healthy,
                epoch,
                "MesIngest",
                @"D:\SqlData\MesIngest.mdf",
                VolumeSpaceSample.FromPercent(@"D:\", 1_000_000, 25m),
                at,
                PausedAt: null,
                PauseId: null,
                PauseReason: null,
                RecoveryAuditId: null));
    }

    internal static CurrentIngestAttentionSnapshot CreateProtectionSurface(
        string storageStatus,
        decimal availablePercent,
        bool historyResetRequired = false)
    {
        var at = DateTimeOffset.Parse("2026-08-24T06:07:08Z");
        var epoch = HistoryEpoch.FromGuid(
            Guid.Parse("99999999-9999-9999-9999-999999999999"));
        var historyResetItem = new CurrentIngestAttentionItemSnapshot(
            CurrentIngestAttentionKinds.HistoryReset,
            CurrentIngestAttentionSeverities.Error,
            at,
            $"HISTORY_RESET:{epoch}",
            SeriesId: null,
            WorkType: null,
            ErrorCode: HistoryResetStatuses.AcknowledgementRequired,
            Target: "MesIngest",
            SubjectKind: "HISTORY_EPOCH",
            new CurrentIngestAttentionEvidenceSnapshot(
                Phase: HistoryResetStatuses.AcknowledgementRequired,
                FailureReason: "prior history and tombstones are unrecoverable",
                DatabaseName: "MesIngest"),
            new OverviewNavigationIntent(
                OverviewNavigationTargets.CurrentIngestAttention,
                AttentionKinds: [CurrentIngestAttentionKinds.HistoryReset]));
        var storageItem = new CurrentIngestAttentionItemSnapshot(
            CurrentIngestAttentionKinds.StoragePressure,
            storageStatus == StoragePressureStatuses.Warning
                ? CurrentIngestAttentionSeverities.Warning
                : CurrentIngestAttentionSeverities.Error,
            at,
            "STORAGE_PRESSURE",
            SeriesId: null,
            WorkType: null,
            ErrorCode: storageStatus,
            Target: "MesIngest",
            SubjectKind: "DATABASE_VOLUME",
            new CurrentIngestAttentionEvidenceSnapshot(
                EvidenceId: storageStatus == StoragePressureStatuses.Paused ? "pause-21" : null,
                Phase: storageStatus,
                FailureReason: storageStatus == StoragePressureStatuses.Paused
                    ? "database volume below pause threshold"
                    : null,
                DatabaseName: "MesIngest",
                VolumeRoot: @"D:\",
                AvailablePercent: availablePercent),
            new OverviewNavigationIntent(
                OverviewNavigationTargets.CurrentIngestAttention,
                AttentionKinds: [CurrentIngestAttentionKinds.StoragePressure]));
        IReadOnlyList<CurrentIngestAttentionItemSnapshot> items = historyResetRequired
            ? [historyResetItem]
            : storageStatus == StoragePressureStatuses.Healthy
                ? []
                : [storageItem];
        return new CurrentIngestAttentionSnapshot(
            new OperationalSnapshotIdentity(
                "attention-commit-21",
                23,
                at,
                "attention-poll-21",
                30,
                8,
                at,
                HistoryEpoch: epoch),
            items.Count,
            new CurrentIngestAttentionFacets(
                items.Select(item => new CurrentIngestAttentionFacetSnapshot(item.Kind, 1)).ToArray(),
                items.Select(item => new CurrentIngestAttentionFacetSnapshot(item.Severity, 1)).ToArray()),
            CurrentIngestAttentionOrder.Default,
            100,
            1,
            items.Count == 0 ? 0 : 1,
            [],
            [],
            items,
            HistoryCleanupStateSnapshot.NotRun with
            {
                EarliestAvailableHostUtc = at.AddDays(-15),
            },
            new StoragePressureStateSnapshot(
                storageStatus,
                epoch,
                "MesIngest",
                @"D:\SqlData\MesIngest.mdf",
                VolumeSpaceSample.FromPercent(@"D:\", 1_000_000, availablePercent),
                at,
                storageStatus == StoragePressureStatuses.Paused ? at : null,
                storageStatus == StoragePressureStatuses.Paused ? "pause-21" : null,
                storageStatus == StoragePressureStatuses.Paused
                    ? "database volume below pause threshold"
                    : null,
                RecoveryAuditId: null),
            PollSchedulerStateSnapshot.NotStarted);
    }
}
