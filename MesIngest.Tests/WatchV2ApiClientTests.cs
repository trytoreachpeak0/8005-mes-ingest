using System.Net;
using System.Text;
using System.Text.Json;
using MesIngest.Core;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchV2ApiClientTests
{
    [Fact]
    public async Task Contract_discovery_uses_bearer_and_requires_the_exact_v2_identity()
    {
        const string credential = "ticket18-secret";
        HttpRequestMessage? observed = null;
        using var handler = new DelegateHandler((request, _) =>
        {
            observed = request;
            return Task.FromResult(JsonResponse(new
            {
                contractVersion = NewMesIngestContract.Version,
                schemaVersion = NewMesIngestContract.SchemaVersion,
                compatibilityPolicy = NewMesIngestContract.CompatibilityPolicy,
                capabilities = NewMesIngestContract.Capabilities.Select(capability => new
                {
                    id = capability.Id,
                    version = capability.Version,
                    operations = capability.Operations,
                }),
            }));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", credential, 30),
            handler: handler);

        await client.VerifyContractAsync(CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal("/api/v2/contract", observed.RequestUri!.AbsolutePath);
        Assert.Equal($"Bearer {credential}", observed.Headers.Authorization!.ToString());
        Assert.True(observed.Headers.Contains(LatencyHeaders.CorrelationId));
    }

    [Fact]
    public async Task Contract_discovery_rejects_an_old_capability_version_before_business_reads()
    {
        var requests = new List<string>();
        using var handler = new DelegateHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse(new
            {
                contractVersion = NewMesIngestContract.Version,
                schemaVersion = NewMesIngestContract.SchemaVersion,
                capabilities = NewMesIngestContract.Capabilities.Select(capability => new
                {
                    id = capability.Id,
                    version = capability.Id == "DEMAND_SERIES" ? "1.0" : capability.Version,
                }),
            }));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket20-host:5088", "secret", 30),
            handler: handler);

        var error = await Assert.ThrowsAsync<WatchHostQueryException>(
            () => client.VerifyContractAsync(CancellationToken.None));

        Assert.Equal(WatchHostFailureKind.Contract, error.Kind);
        Assert.Equal(NewMesIngestContractMismatchException.ErrorCode, error.ErrorCode);
        Assert.Equal(["/api/v2/contract"], requests);
    }

    [Fact]
    public async Task Overview_normalizes_area_scope_and_decodes_one_atomic_v2_snapshot()
    {
        Uri? observed = null;
        var expected = OverviewSnapshot("commit-overview", ["A1-1", "B2-2"]);
        using var handler = new DelegateHandler((request, _) =>
        {
            observed = request.RequestUri;
            return Task.FromResult(JsonResponse(expected));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);

        var actual = await client.FetchOverviewAsync(
            new WatchOverviewQuery([" B2-2 ", "A1-1", "B2-2"]),
            CancellationToken.None);

        Assert.Equal("/api/v2/watch-overview", observed!.AbsolutePath);
        Assert.Equal("?area=A1-1&area=B2-2", observed.Query);
        Assert.Equal("commit-overview", actual.Snapshot.ProjectionCommitId);
        Assert.Equal(["A1-1", "B2-2"], actual.MesAreas);
    }

    [Fact]
    public async Task Demand_series_list_uses_the_normalized_frozen_query_and_maps_the_host_wire_shape()
    {
        const string credential = "ticket18-demand-secret";
        HttpRequestMessage? observed = null;
        var query = new DemandSeriesBrowseQuery(
            new DemandSeriesBrowseFilter
            {
                Lifecycles = ["TRACKING", "ARCHIVED", "TRACKING"],
                CurrentPresences = ["VISIBLE", "GONE"],
                WorkTypes = ["WIRE TO GATE", "A&B", "WIRE TO GATE"],
                MesAreas = ["B2-2", "A1-1"],
                SublotContains = "LOT / 7",
                SeriesId = "series?1",
                DemandId = "demand#1",
            },
            PageSize: 25,
            PageNumber: 2,
            SnapshotReference: "snapshot+/=");
        var expected = DemandSeriesList(query.NormalizeAndValidate().Filter);
        using var handler = new DelegateHandler((request, _) =>
        {
            observed = request;
            return Task.FromResult(JsonResponse(DemandSeriesListDto.From(expected)));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", credential, 30),
            handler: handler);

        var actual = await client.FetchDemandSeriesAsync(query, CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal("/api/v2/demand-series", observed.RequestUri!.AbsolutePath);
        Assert.Equal(
            "?lifecycle=ARCHIVED&lifecycle=TRACKING&presence=GONE&presence=VISIBLE"
            + "&workType=A%26B&workType=WIRE%20TO%20GATE&area=A1-1&area=B2-2"
            + "&sublot=LOT%20%2F%207&seriesId=series%3F1&demandId=demand%231"
            + "&pageSize=25&page=2&snapshot=snapshot%2B%2F%3D"
            + "&order=STARTED_AT_DESC_SERIES_ID_ASC",
            observed.RequestUri.Query);
        Assert.Equal($"Bearer {credential}", observed.Headers.Authorization!.ToString());
        Assert.True(observed.Headers.Contains(LatencyHeaders.CorrelationId));
        Assert.Equal(expected.Snapshot, actual.Snapshot);
        Assert.Equal(expected.SnapshotReference, actual.SnapshotReference);
        Assert.Equal(expected.Filter.Lifecycles, actual.Filter.Lifecycles);
        Assert.Equal(expected.Filter.WorkTypes, actual.Filter.WorkTypes);
        Assert.Equal(expected.Items[0].WorkType, actual.Items[0].WorkType);
        Assert.Equal(expected.Items[0].LiveMesFields, actual.Items[0].LiveMesFields);
    }

    [Fact]
    public async Task Demand_series_detail_keeps_the_list_snapshot_and_maps_nested_series_evidence()
    {
        Uri? observed = null;
        var expected = DemandSeriesDetail();
        using var handler = new DelegateHandler((request, _) =>
        {
            observed = request.RequestUri;
            return Task.FromResult(JsonResponse(FrozenDemandSeriesDto.From(expected)));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);

        var actual = await client.FetchDemandSeriesDetailAsync(
            "series/id?",
            "snapshot +/=",
            CancellationToken.None);

        Assert.Equal(
            "/api/v2/demand-series/series%2Fid%3F?snapshot=snapshot%20%2B%2F%3D",
            observed!.PathAndQuery);
        Assert.Equal(expected.Snapshot, actual.Snapshot);
        Assert.Equal(expected.SnapshotReference, actual.SnapshotReference);
        Assert.Equal(expected.Series.CurrentDemand.DemandId, actual.Series.CurrentDemand.DemandId);
        Assert.Equal(expected.Series.CurrentDemand.LiveMesFields, actual.Series.CurrentDemand.LiveMesFields);
        Assert.Equal(
            expected.Series.CurrentDemand.ReadabilityBlockers,
            actual.Series.CurrentDemand.ReadabilityBlockers);
        Assert.Equal(
            expected.Series.RawObservations[0].Assignment,
            actual.Series.RawObservations[0].Assignment);
        Assert.Equal(expected.Series.Events[0], actual.Series.Events[0]);
        Assert.Equal(expected.Series.ErrorPeriods[0].Code, actual.Series.ErrorPeriods[0].Code);
        Assert.Equal(
            expected.Series.ErrorPeriods[0].Evidence[0],
            actual.Series.ErrorPeriods[0].Evidence[0]);
    }

    [Fact]
    public async Task Readability_audit_list_preserves_work_type_and_sublot_semantics_in_the_frozen_query()
    {
        Uri? observed = null;
        var query = new ReadabilityAuditQuery(
            new ReadabilityAuditFilter
            {
                ReadabilityStates = [" READABLE ", "NOT_READABLE"],
                WorkTypes = ["WIRE TO GATE", "A&B", "WIRE TO GATE"],
                Blockers = [" SERIES_ARCHIVED ", "DEMAND_GONE"],
                DemandId = " demand/1 ",
                SublotContains = " lot + x ",
                MesAreas = [" B2-2 ", "A1-1"],
            },
            PageSize: 15,
            SnapshotReference: "snap/r",
            Cursor: "cursor+r");
        var expected = ReadabilityAuditList(query.NormalizeAndValidate().Filter);
        using var handler = new DelegateHandler((request, _) =>
        {
            observed = request.RequestUri;
            return Task.FromResult(JsonResponse(ReadabilityAuditListDto.From(expected)));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);

        var actual = await client.FetchReadabilityAuditAsync(query, CancellationToken.None);

        Assert.Equal("/api/v2/readability-audit", observed!.AbsolutePath);
        Assert.Equal(
            "?state=NOT_READABLE&state=READABLE&workType=A%26B&workType=WIRE%20TO%20GATE"
            + "&blocker=DEMAND_GONE&blocker=SERIES_ARCHIVED&demandId=demand%2F1"
            + "&sublot=%20lot%20%2B%20x%20&area=A1-1&area=B2-2&pageSize=15&page=1"
            + "&snapshot=snap%2Fr&cursor=cursor%2Br"
            + "&order=NOT_READABLE_FIRST_LEAD_PRIORITY_DEMAND_LAST_SEEN_DESC_DEMAND_ID_ASC",
            observed.Query);
        Assert.Equal(expected.Snapshot, actual.Snapshot);
        Assert.Equal(expected.Filter.WorkTypes, actual.Filter.WorkTypes);
        Assert.Equal(expected.Filter.SublotContains, actual.Filter.SublotContains);
        Assert.Equal(expected.Items[0].WorkType, actual.Items[0].WorkType);
        Assert.Equal(expected.Items[0].Sublot, actual.Items[0].Sublot);
        Assert.Equal(expected.Items[0].ReadabilityBlockers, actual.Items[0].ReadabilityBlockers);
    }

    [Fact]
    public async Task Readability_audit_detail_maps_checks_blocker_evidence_and_raw_observations()
    {
        Uri? observed = null;
        var expected = ReadabilityAuditDetail();
        using var handler = new DelegateHandler((request, _) =>
        {
            observed = request.RequestUri;
            return Task.FromResult(JsonResponse(ReadabilityAuditDetailDto.From(expected)));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);

        var actual = await client.FetchReadabilityAuditDetailAsync(
            "demand/id?",
            "readability +/=",
            CancellationToken.None);

        Assert.Equal(
            "/api/v2/readability-audit/demand%2Fid%3F?snapshot=readability%20%2B%2F%3D",
            observed!.PathAndQuery);
        Assert.Equal(expected.Snapshot, actual.Snapshot);
        Assert.Equal(expected.Demand.WorkType, actual.Demand.WorkType);
        Assert.Equal(expected.Demand.Sublot, actual.Demand.Sublot);
        Assert.Equal(expected.Series.WorkType, actual.Series.WorkType);
        Assert.Equal(expected.QualificationChecks[0], actual.QualificationChecks[0]);
        Assert.Equal(expected.Blockers[0].Evidence[0], actual.Blockers[0].Evidence[0]);
        Assert.Equal(
            expected.LatestRawObservations[0].Assignment,
            actual.LatestRawObservations[0].Assignment);
        Assert.Equal(expected.LatestObservationPollTrace, actual.LatestObservationPollTrace);
    }

    [Fact]
    public async Task Error_search_list_sends_the_normalized_custom_window_and_maps_transport_keys()
    {
        Uri? observed = null;
        var query = new ErrorSearchQuery(
            new ErrorSearchFilter
            {
                Categories = [" data_completeness ", "DATA_FORMAT"],
                ErrorCodes = [" required_mes_field_missing "],
                ActivityStates = [" ended ", "ACTIVE"],
                SeriesId = " series/1 ",
                DemandId = " demand+1 ",
                SublotContains = " lot x ",
            },
            ErrorSearchWindowSelection.Custom(
                DateTimeOffset.Parse("2026-08-13T04:00:00+08:00"),
                DateTimeOffset.Parse("2026-08-14T04:00:00+08:00")),
            PageSize: 17,
            SnapshotReference: "snapshot/e",
            Cursor: "cursor+e");
        var normalized = query.NormalizeAndValidate();
        var expected = ErrorSearchList(normalized.Filter, normalized.Window);
        using var handler = new DelegateHandler((request, _) =>
        {
            observed = request.RequestUri;
            return Task.FromResult(JsonResponse(ErrorSearchListDto.From(expected)));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);

        var actual = await client.FetchErrorSearchAsync(query, CancellationToken.None);

        Assert.Equal("/api/v2/error-search", observed!.AbsolutePath);
        Assert.Equal(
            "?category=DATA_COMPLETENESS&category=DATA_FORMAT&code=REQUIRED_MES_FIELD_MISSING"
            + "&state=ACTIVE&state=ENDED&seriesId=SERIES%2F1&demandId=DEMAND%2B1&sublot=LOT%20X"
            + "&from=2026-08-12T20%3A00%3A00.0000000%2B00%3A00"
            + "&to=2026-08-13T20%3A00%3A00.0000000%2B00%3A00"
            + "&pageSize=17&snapshot=snapshot%2Fe&cursor=cursor%2Be",
            observed.Query);
        Assert.Equal(expected.Snapshot, actual.Snapshot);
        Assert.Equal(expected.Filter.Categories, actual.Filter.Categories);
        Assert.Equal(expected.Window, actual.Window);
        Assert.Equal(expected.Items[0].WorkType, actual.Items[0].WorkType);
        Assert.Equal(expected.Items[0].Sublot, actual.Items[0].Sublot);
        Assert.Equal(expected.Items[0].MatchedErrors, actual.Items[0].MatchedErrors);
    }

    [Fact]
    public async Task Error_search_never_downgrades_an_empty_custom_window_to_the_default_preset()
    {
        var requestWasSent = false;
        using var handler = new DelegateHandler((_, _) =>
        {
            requestWasSent = true;
            return Task.FromResult(JsonResponse(new { }));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);
        var query = new ErrorSearchQuery(
            new ErrorSearchFilter(),
            ErrorSearchWindowSelection.Custom(null, null));

        var error = await Assert.ThrowsAsync<ErrorSearchException>(() =>
            client.FetchErrorSearchAsync(query, CancellationToken.None));

        Assert.Equal(ErrorSearchErrorCodes.InvalidQuery, error.Code);
        Assert.False(requestWasSent);
    }

    [Fact]
    public async Task Error_search_detail_keeps_the_frozen_window_and_maps_bounded_diagnostics()
    {
        Uri? observed = null;
        var expected = ErrorSearchDetail();
        expected = expected with
        {
            SnapshotReference = "error +/=",
            Series = expected.Series with { SeriesId = "series/error?" },
        };
        using var handler = new DelegateHandler((request, _) =>
        {
            observed = request.RequestUri;
            return Task.FromResult(JsonResponse(ErrorSearchDetailDto.From(expected)));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);

        var actual = await client.FetchErrorSearchDetailAsync(
            "series/error?",
            "error +/=",
            CancellationToken.None);

        Assert.Equal(
            "/api/v2/error-search/series%2Ferror%3F?snapshot=error%20%2B%2F%3D",
            observed!.PathAndQuery);
        Assert.Equal(expected.Snapshot, actual.Snapshot);
        Assert.Equal(expected.Window, actual.Window);
        Assert.Equal(expected.Series.WorkType, actual.Series.WorkType);
        Assert.Equal(expected.Series.Sublot, actual.Series.Sublot);
        Assert.Equal(expected.Periods[0].Code, actual.Periods[0].Code);
        Assert.Equal(
            expected.Periods[0].Evidence[0].DiagnosticValue,
            actual.Periods[0].Evidence[0].DiagnosticValue);
        Assert.Equal(
            expected.Periods[0].Evidence[0].RelatedWorkTypes,
            actual.Periods[0].Evidence[0].RelatedWorkTypes);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Error_search_detail_rejects_a_response_for_another_route_identity(
        bool wrongSeries,
        bool wrongSnapshot)
    {
        const string requestedSeries = "series-requested";
        const string requestedSnapshot = "snapshot-requested";
        var response = ErrorSearchDetail();
        response = response with
        {
            SnapshotReference = wrongSnapshot ? "snapshot-other" : requestedSnapshot,
            Series = response.Series with
            {
                SeriesId = wrongSeries ? "series-other" : requestedSeries,
            },
        };
        using var handler = new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(ErrorSearchDetailDto.From(response))));
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);

        var error = await Assert.ThrowsAsync<WatchHostQueryException>(() =>
            client.FetchErrorSearchDetailAsync(
                requestedSeries,
                requestedSnapshot,
                CancellationToken.None));

        Assert.Equal(WatchHostFailureKind.Decode, error.Kind);
        Assert.Equal("/api/v2/error-search/series-requested?snapshot=snapshot-requested", error.Endpoint);
    }

    [Fact]
    public async Task Error_raw_evidence_uses_the_restricted_uri_and_maps_bounded_field_values()
    {
        Uri? observed = null;
        var expected = ErrorRawEvidence();
        using var handler = new DelegateHandler((request, _) =>
        {
            observed = request.RequestUri;
            return Task.FromResult(JsonResponse(ErrorSearchRawEvidenceDto.From(expected)));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);

        var actual = await client.FetchErrorRawEvidenceAsync(
            "series/raw?",
            "evidence/raw+",
            "raw +/=",
            new ErrorSearchRawEvidenceQuery(
                [ErrorSearchRawEvidenceFields.Package, ErrorSearchRawEvidenceFields.WorkType,
                    ErrorSearchRawEvidenceFields.Package],
                7),
            CancellationToken.None);

        Assert.Equal(
            "/api/v2/error-search/series%2Fraw%3F/evidence/evidence%2Fraw%2B/raw-observations"
            + "?snapshot=raw%20%2B%2F%3D&fields=package%2CworkType&maxItems=7",
            observed!.PathAndQuery);
        Assert.Equal(expected.Snapshot, actual.Snapshot);
        Assert.Equal(expected.PeriodId, actual.PeriodId);
        Assert.Equal(expected.IncludedFields, actual.IncludedFields);
        Assert.Equal(expected.Limits, actual.Limits);
        Assert.Equal(expected.Items[0].Fields, actual.Items[0].Fields);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Error_raw_evidence_rejects_an_oversized_http_envelope_before_deserialization(
        bool includeContentLength)
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 300_000));
        using var handler = new DelegateHandler((_, _) =>
        {
            HttpContent content = includeContentLength
                ? new ByteArrayContent(payload)
                : new UnknownLengthContent(payload);
            if (includeContentLength)
            {
                content.Headers.ContentLength = payload.Length;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);

        var error = await Assert.ThrowsAsync<WatchHostQueryException>(() =>
            client.FetchErrorRawEvidenceAsync(
                "series-raw",
                "evidence-raw",
                "snapshot-raw",
                new ErrorSearchRawEvidenceQuery([ErrorSearchRawEvidenceFields.Package]),
                CancellationToken.None));

        Assert.Equal(WatchHostFailureKind.Decode, error.Kind);
        Assert.Equal(LatencyStages.HttpJson, error.Stage);
        Assert.Contains("raw-observations", error.Endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_attention_uses_normalized_page_facets_and_maps_navigation_evidence()
    {
        Uri? observed = null;
        var query = new CurrentIngestAttentionQuery(
            PageSize: 13,
            PageNumber: 3,
            Kinds: [" SERIES_ERROR ", "POLL_RUN_FAILURE", "SERIES_ERROR"],
            Severities: [" WARNING ", "ERROR"]);
        var expected = CurrentAttention();
        using var handler = new DelegateHandler((request, _) =>
        {
            observed = request.RequestUri;
            return Task.FromResult(JsonResponse(CurrentIngestAttentionDto.From(expected)));
        });
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);

        var actual = await client.FetchCurrentAttentionAsync(query, CancellationToken.None);

        Assert.Equal("/api/v2/current-ingest-attention", observed!.AbsolutePath);
        Assert.Equal(
            "?pageSize=13&pageNumber=3&kind=POLL_RUN_FAILURE&kind=SERIES_ERROR"
            + "&severity=ERROR&severity=WARNING",
            observed.Query);
        Assert.Equal(expected.Snapshot, actual.Snapshot);
        Assert.Equal(expected.Kinds, actual.Kinds);
        Assert.Equal(expected.Severities, actual.Severities);
        Assert.Equal(expected.Items[0].Evidence, actual.Items[0].Evidence);
        Assert.Equal(expected.Items[0].Navigation.Target, actual.Items[0].Navigation.Target);
        Assert.Equal(
            expected.Items[0].Navigation.ErrorActivityStates,
            actual.Items[0].Navigation.ErrorActivityStates);
    }

    [Fact]
    public async Task Business_snapshot_contract_mismatch_is_rejected_before_interpretation()
    {
        var response = OverviewSnapshot("commit-wrong-contract", []);
        response = response with
        {
            Snapshot = response.Snapshot with { ContractVersion = "wrong-v2-contract" },
        };
        using var handler = new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(response)));
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", "secret", 30),
            handler: handler);

        var error = await Assert.ThrowsAsync<WatchHostQueryException>(() =>
            client.FetchOverviewAsync(new WatchOverviewQuery(), CancellationToken.None));

        Assert.Equal(WatchHostFailureKind.Contract, error.Kind);
        Assert.Equal(NewMesIngestContractMismatchException.ErrorCode, error.ErrorCode);
        Assert.Equal("/api/v2/watch-overview", error.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(error.CorrelationId));
        Assert.Contains(NewMesIngestContractMismatchException.ErrorCode, error.Message);
    }

    [Fact]
    public async Task Structured_unauthorized_response_is_classified_and_redacts_the_host_credential()
    {
        const string credential = "ticket18-auth-secret";
        using var handler = new DelegateHandler((_, _) => Task.FromResult(ErrorResponse(
            HttpStatusCode.Unauthorized,
            "UNAUTHORIZED",
            $"Authorization: Bearer {credential} was rejected")));
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", credential, 30),
            handler: handler);

        var error = await Assert.ThrowsAsync<WatchHostQueryException>(() =>
            client.FetchCurrentAttentionAsync(
                new CurrentIngestAttentionQuery(),
                CancellationToken.None));

        Assert.Equal(WatchHostFailureKind.Authentication, error.Kind);
        Assert.Equal("UNAUTHORIZED", error.ErrorCode);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.DoesNotContain(credential, error.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_http_connection_failure_is_classified_as_network_and_redacted()
    {
        const string credential = "ticket18-network-secret";
        using var handler = new DelegateHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(
                $"Connection failed with credential {credential}.")));
        using var client = MesIngestV2ApiClient.CreateForHost(
            new WatchHostSettings("http://ticket18-host:5088", credential, 30),
            handler: handler);

        var error = await Assert.ThrowsAsync<WatchHostQueryException>(() =>
            client.FetchOverviewAsync(new WatchOverviewQuery(), CancellationToken.None));

        Assert.Equal(WatchHostFailureKind.Network, error.Kind);
        Assert.Equal(LatencyStages.HttpConnect, error.Stage);
        Assert.Equal("/api/v2/watch-overview", error.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(error.CorrelationId));
        Assert.DoesNotContain(credential, error.Message, StringComparison.Ordinal);
        Assert.Contains("(masked)", error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(value),
            Encoding.UTF8,
            "application/json"),
    };

    private static HttpResponseMessage ErrorResponse(
        HttpStatusCode statusCode,
        string code,
        string error) => new(statusCode)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { code, error }),
            Encoding.UTF8,
            "application/json"),
    };

    private static WatchOverviewSnapshot OverviewSnapshot(
        string projectionCommitId,
        IReadOnlyList<string> areas)
    {
        var at = DateTimeOffset.Parse("2026-08-14T01:02:03Z");
        var identity = new OperationalSnapshotIdentity(
            projectionCommitId,
            18,
            at,
            "poll-overview",
            27,
            9,
            at,
            NewMesIngestContract.Version);
        var series = new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries);
        var audit = new OverviewNavigationIntent(OverviewNavigationTargets.ReadabilityAudit);
        var errors = new OverviewNavigationIntent(OverviewNavigationTargets.ErrorSearch);
        var attention = new OverviewNavigationIntent(OverviewNavigationTargets.CurrentIngestAttention);
        return new WatchOverviewSnapshot(
            identity,
            areas,
            new WatchOverviewSeriesSummary(3, 2, 1, 1, 0, series, series, series, series, series),
            new WatchOverviewReadabilitySummary(4, 3, 1, audit, audit, audit),
            new WatchOverviewErrorSummary(1, 2, errors, errors, errors),
            new WatchOverviewAttentionSummary(1, [], [], attention),
            [],
            WatchOverviewRecentActivityStates.NoRecentHighlights,
            WatchOverviewRecentActivityStates.NoRecentHighlightsMessage);
    }

    private static DemandSeriesListSnapshot DemandSeriesList(DemandSeriesBrowseFilter filter)
    {
        var at = DateTimeOffset.Parse("2026-08-14T02:03:04Z");
        return new DemandSeriesListSnapshot(
            new DemandSeriesSnapshotIdentity(
                HistoryEpoch.CreateNew(),
                "commit-demand",
                42,
                at,
                "poll-demand",
                NewMesIngestContract.Version),
            "snapshot-demand",
            filter,
            DemandSeriesBrowseOrder.Default,
            1,
            new DemandSeriesFacets(1, 0, 1, 0, 0),
            25,
            2,
            3,
            [
                new DemandSeriesListItemSnapshot(
                    "series-demand",
                    "WIRE TO GATE",
                    "LOT / 7",
                    DemandSeriesLifecycleContract.Tracking,
                    DemandSeriesLifecycleContract.Visible,
                    at.AddHours(-1),
                    null,
                    "demand-current",
                    2,
                    "VISIBLE",
                    at,
                    null,
                    new LiveMesFieldSetSnapshot("A1-1", "EQP-1", "STEP-1", at, "PKG-1"),
                    ExternalReadabilityStates.Readable,
                    [],
                    9,
                    "poll-demand",
                    "commit-demand"),
            ],
            "cursor-demand",
            true);
    }

    private static DemandSeriesDetailSnapshot DemandSeriesDetail()
    {
        var at = DateTimeOffset.Parse("2026-08-14T03:04:05Z");
        var demand = new TransportDemandSnapshot(
            "demand-detail",
            "series-detail",
            2,
            "demand-old",
            DemandSeriesLifecycleContract.Visible,
            at.AddHours(-2),
            at,
            null,
            "poll-created",
            "commit-created",
            "commit-detail",
            new LiveMesFieldSetSnapshot("A1-1", "EQP-2", "STEP-2", at, "PKG-2"),
            ExternalReadabilityStates.NotReadable,
            ["REQUIRED_MES_FIELD_MISSING"],
            "poll-detail",
            "commit-detail",
            at);
        var series = new DemandSeriesSnapshot(
            "series-detail",
            "WIRE_TO_GATE",
            "LOT-DETAIL",
            DemandSeriesLifecycleContract.Tracking,
            DemandSeriesLifecycleContract.Visible,
            at.AddDays(-1),
            "poll-created",
            "commit-created",
            "commit-detail",
            demand,
            [demand],
            [
                new DemandRawObservationSnapshot(
                    0,
                    "poll-detail",
                    "commit-detail",
                    MesObservationAssignment.Assigned,
                    "series-detail",
                    "demand-detail",
                    "WIRE_TO_GATE",
                    "LOT-DETAIL",
                    "A1-1",
                    "EQP-2",
                    "STEP-2",
                    at,
                    "PKG-2",
                    at,
                    at.ToString("O")),
            ],
            [
                new DemandSeriesEventSnapshot(
                    "event-detail",
                    "series-detail",
                    7,
                    "MES_FIELD_CHANGED",
                    at,
                    "DEMAND",
                    "demand-detail",
                    "poll-detail",
                    "commit-detail",
                    1,
                    "{}"),
            ],
            [
                new DemandSeriesCurrentConditionSnapshot(
                    "period-detail",
                    "REQUIRED_MES_FIELD_MISSING",
                    "DATA_QUALITY",
                    "ERROR",
                    "DEMAND",
                    "PACKAGE",
                    at.AddMinutes(-2),
                    at,
                    "poll-detail",
                    "commit-detail",
                    "demand-detail",
                    null,
                    "PACKAGE is required"),
            ],
            [
                new DemandSeriesErrorPeriodSnapshot(
                    "period-detail",
                    "REQUIRED_MES_FIELD_MISSING",
                    "DATA_QUALITY",
                    "ERROR",
                    "DEMAND",
                    "PACKAGE",
                    "OBSERVED",
                    at.AddMinutes(-2),
                    null,
                    null,
                    [
                        new SeriesErrorPeriodEvidenceSnapshot(
                            "evidence-detail",
                            "MES_OBSERVATION",
                            at,
                            "poll-detail",
                            "commit-detail",
                            "demand-detail",
                            null,
                            "PACKAGE is required"),
                    ]),
            ],
            LastSeriesSequence: 7);
        return new DemandSeriesDetailSnapshot(
            new DemandSeriesSnapshotIdentity(
                HistoryEpoch.CreateNew(),
                "commit-detail",
                43,
                at,
                "poll-detail",
                NewMesIngestContract.Version),
            "snapshot-detail",
            series);
    }

    private static ReadabilityAuditListSnapshot ReadabilityAuditList(ReadabilityAuditFilter filter)
    {
        var at = DateTimeOffset.Parse("2026-08-14T04:05:06Z");
        return new ReadabilityAuditListSnapshot(
            new ReadabilityAuditSnapshotIdentity(
                HistoryEpoch.CreateNew(),
                "commit-readability",
                50,
                at,
                "poll-readability",
                12,
                NewMesIngestContract.Version),
            "snapshot-readability",
            filter,
            ReadabilityAuditOrder.Default,
            1,
            new ReadabilityAuditFacets(
                [new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.NotReadable, 1)],
                [new ReadabilityBlockerFacetSnapshot("DEMAND_GONE", 1)]),
            15,
            1,
            1,
            [
                new ReadabilityAuditListItemSnapshot(
                    "demand-readability",
                    "series-readability",
                    "WIRE TO GATE",
                    "LOT-READABILITY",
                    3,
                    "demand-previous",
                    DemandSeriesLifecycleContract.Gone,
                    DemandSeriesLifecycleContract.Tracking,
                    DemandSeriesLifecycleContract.Gone,
                    true,
                    at.AddHours(-2),
                    at.AddHours(-1),
                    at,
                    new LiveMesFieldSetSnapshot("A1-1", "EQP-R", "STEP-R", at, "PKG-R"),
                    1,
                    ExternalReadabilityStates.NotReadable,
                    "DEMAND_GONE",
                    ["DEMAND_GONE"],
                    "poll-readability",
                    "commit-readability",
                    at),
            ],
            "cursor-readability",
            true);
    }

    private static ReadabilityAuditDetailSnapshot ReadabilityAuditDetail()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var list = ReadabilityAuditList(new ReadabilityAuditFilter());
        var demand = list.Items[0];
        return new ReadabilityAuditDetailSnapshot(
            list.Snapshot,
            "snapshot-readability-detail",
            demand,
            new ReadabilityAuditSeriesSnapshot(
                demand.SeriesId,
                demand.WorkType,
                demand.Sublot,
                demand.SeriesLifecycle,
                demand.SeriesCurrentPresence,
                at.AddDays(-1),
                null,
                demand.DemandId),
            [
                new ReadabilityQualificationCheckSnapshot(
                    "DEMAND_VISIBLE",
                    "DEMAND_GONE",
                    ReadabilityQualificationCheckResults.Failed),
            ],
            [
                new ReadabilityBlockerEvidenceSnapshot(
                    "DEMAND_GONE",
                    60,
                    [
                        new ReadabilityEvidenceItemSnapshot(
                            "DEMAND_STATUS",
                            "GONE",
                            "Demand must be visible",
                            at,
                            "poll-readability",
                            "commit-readability"),
                    ]),
            ],
            [
                new DemandRawObservationSnapshot(
                    0,
                    "poll-readability",
                    "commit-readability",
                    MesObservationAssignment.Assigned,
                    demand.SeriesId,
                    demand.DemandId,
                    demand.WorkType,
                    demand.Sublot,
                    "A1-1",
                    "EQP-R",
                    "STEP-R",
                    at,
                    "PKG-R",
                    at),
            ],
            new ReadabilityAuditPollTraceSnapshot(
                "poll-readability",
                "query-v1",
                "SUCCESS",
                at.AddMinutes(-1),
                at,
                1,
                "digest-readability",
                "commit-readability",
                50));
    }

    private static ErrorSearchListSnapshot ErrorSearchList(
        ErrorSearchFilter filter,
        ErrorSearchWindowSelection windowSelection)
    {
        var at = DateTimeOffset.Parse("2026-08-14T06:07:08Z");
        return new ErrorSearchListSnapshot(
            "snapshot-error",
            new ErrorSearchSnapshotIdentity(
                HistoryEpoch.FromGuid(Guid.Parse("66666666-6666-6666-6666-666666666666")),
                at,
                "commit-error",
                61,
                at,
                "poll-error",
                NewMesIngestContract.Version),
            filter,
            windowSelection.Resolve(at),
            ErrorSearchOrder.Default,
            1,
            new ErrorSearchFacets(
                [new ErrorSearchCategoryFacetSnapshot("DATA_COMPLETENESS", 1)],
                [new ErrorSearchActivityStateFacetSnapshot(ErrorSearchActivityStates.Active, 1)]),
            17,
            1,
            1,
            [
                new ErrorSearchListItemSnapshot(
                    "series-error",
                    "WIRE_TO_GATE",
                    "LOT-ERROR",
                    ErrorSearchActivityStates.Active,
                    [
                        new ErrorSearchMatchedErrorSnapshot(
                            "REQUIRED_MES_FIELD_MISSING",
                            "DATA_COMPLETENESS",
                            "ERROR"),
                    ],
                    at,
                    1,
                    1,
                    "A1-1",
                    ErrorSearchMesAreaAvailability.CurrentTrusted),
            ],
            "cursor-error",
            true);
    }

    private static ErrorSearchDetailSnapshot ErrorSearchDetail()
    {
        var at = DateTimeOffset.Parse("2026-08-14T07:08:09Z");
        var list = ErrorSearchList(
            new ErrorSearchFilter
            {
                Categories = ["DATA_COMPLETENESS"],
                ActivityStates = [ErrorSearchActivityStates.Active],
            },
            ErrorSearchWindowSelection.Last7Days);
        return new ErrorSearchDetailSnapshot(
            "snapshot-error-detail",
            list.Snapshot,
            list.Filter,
            list.Window,
            list.Order,
            list.Items[0],
            [
                new ErrorSearchDetailPeriodSnapshot(
                    "period-error",
                    "REQUIRED_MES_FIELD_MISSING",
                    "DATA_COMPLETENESS",
                    "ERROR",
                    "DEMAND",
                    "PACKAGE",
                    "OBSERVED",
                    at.AddHours(-1),
                    null,
                    null,
                    false,
                    true,
                    true,
                    [
                        new ErrorSearchDetailEvidenceSnapshot(
                            "evidence-error",
                            "MES_OBSERVATION",
                            "PACKAGE",
                            at,
                            "poll-error",
                            "commit-error",
                            "demand-error",
                            ["WIRE_TO_GATE", "WIRE_TO_NITROGEN"],
                            new ErrorSearchDiagnosticValueSnapshot(
                                ErrorSearchDiagnosticValueKinds.RawObservationSet,
                                ObservationCount: 2,
                                Sha256Digest: "sha256-error"),
                            "PACKAGE is required",
                            true),
                    ]),
            ]);
    }

    private static ErrorSearchRawEvidenceSnapshot ErrorRawEvidence()
    {
        var at = DateTimeOffset.Parse("2026-08-14T08:09:10Z");
        return new ErrorSearchRawEvidenceSnapshot(
            "snapshot-raw",
            new ErrorSearchSnapshotIdentity(
                HistoryEpoch.FromGuid(Guid.Parse("77777777-7777-7777-7777-777777777777")),
                at,
                "commit-raw",
                62,
                at,
                "poll-raw",
                NewMesIngestContract.Version),
            "series-raw",
            "period-raw",
            "evidence-raw",
            "poll-raw",
            "commit-raw",
            "demand-raw",
            [ErrorSearchRawEvidenceFields.Package, ErrorSearchRawEvidenceFields.WorkType],
            1,
            new ErrorSearchRawEvidenceLimitsSnapshot(
                7,
                ErrorSearchRawEvidenceLimits.MaximumItemBytes,
                ErrorSearchRawEvidenceLimits.MaximumTotalBytes),
            123,
            [
                new ErrorSearchRawEvidenceItemSnapshot(
                    0,
                    "poll-raw",
                    "commit-raw",
                    "demand-raw",
                    at,
                    new Dictionary<string, string?>
                    {
                        [ErrorSearchRawEvidenceFields.Package] = null,
                        [ErrorSearchRawEvidenceFields.WorkType] = "WIRE_TO_GATE",
                    }),
            ]);
    }

    private static CurrentIngestAttentionSnapshot CurrentAttention()
    {
        var at = DateTimeOffset.Parse("2026-08-14T09:10:11Z");
        return new CurrentIngestAttentionSnapshot(
            new OperationalSnapshotIdentity(
                "commit-attention",
                70,
                at,
                "poll-attention",
                81,
                13,
                at,
                NewMesIngestContract.Version,
                HistoryEpoch.FromGuid(Guid.Parse("77777777-7777-7777-7777-777777777777"))),
            1,
            new CurrentIngestAttentionFacets(
                [new CurrentIngestAttentionFacetSnapshot(CurrentIngestAttentionKinds.SeriesError, 1)],
                [new CurrentIngestAttentionFacetSnapshot(CurrentIngestAttentionSeverities.Error, 1)]),
            CurrentIngestAttentionOrder.Default,
            13,
            3,
            4,
            [CurrentIngestAttentionKinds.PollRunFailure, CurrentIngestAttentionKinds.SeriesError],
            [CurrentIngestAttentionSeverities.Error, CurrentIngestAttentionSeverities.Warning],
            [
                new CurrentIngestAttentionItemSnapshot(
                    CurrentIngestAttentionKinds.SeriesError,
                    CurrentIngestAttentionSeverities.Error,
                    at,
                    "attention-stable",
                    "series-attention",
                    "WIRE_TO_GATE",
                    "REQUIRED_MES_FIELD_MISSING",
                    "DEMAND",
                    "PACKAGE",
                    new CurrentIngestAttentionEvidenceSnapshot(
                        "commit-attention",
                        70,
                        "poll-attention",
                        81,
                        "series-attention",
                        "demand-attention",
                        "WIRE_TO_GATE",
                        0,
                        "evidence-attention",
                        "digest-attention",
                        "PROJECT",
                        "SUCCESS"),
                    new OverviewNavigationIntent(
                        OverviewNavigationTargets.ErrorSearch,
                        ErrorActivityStates: [ErrorSearchActivityStates.Active],
                        SeriesId: "series-attention")),
            ],
            StoragePressure: new StoragePressureStateSnapshot(
                StoragePressureStatuses.Healthy,
                HistoryEpoch.FromGuid(Guid.Parse("77777777-7777-7777-7777-777777777777")),
                "MesIngest",
                @"D:\sql\MesIngest.mdf",
                VolumeSpaceSample.FromPercent(@"D:\", 1_000_000, 25m),
                at,
                PausedAt: null,
                PauseId: null,
                PauseReason: null,
                RecoveryAuditId: null));
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public DelegateHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
            _send = send;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _send(request, cancellationToken);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
