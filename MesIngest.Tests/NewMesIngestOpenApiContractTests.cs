using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Primitives;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class NewMesIngestOpenApiContractTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] ForbiddenV2Terms =
    [
        "DemandChangeFeed",
        "SYNC_CURSOR_EXPIRED",
        "IngestAlert",
        "OccurrenceCount",
        "frozenMesFields",
        "REAPPEAR_AFTER_GONE",
    ];

    private readonly WebApplicationFactory<Program> _factory;

    public NewMesIngestOpenApiContractTests(WebApplicationFactory<Program> factory) =>
        _factory = factory;

    [Fact]
    public async Task Contract_discovery_returns_the_frozen_runtime_identity_and_excludes_legacy()
    {
        await using var factory = CreateV2Factory();
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v2/contract");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(NewMesIngestContract.Version, root.GetProperty("contractVersion").GetString());
        Assert.Equal(NewMesIngestContract.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            NewMesIngestContract.CompatibilityPolicy,
            root.GetProperty("compatibilityPolicy").GetString());
        Assert.Equal(
            NewMesIngestContract.OpenApiDocumentPath,
            root.GetProperty("openApiDocument").GetString());
        Assert.Equal(
            "READ_ONLY_GET",
            root.GetProperty("businessSurface").GetString());
        Assert.Equal(
            "DEVELOPMENT_ONLY_EXCLUDED_FROM_V2",
            root.GetProperty("legacySurfacePolicy").GetString());
        Assert.Equal(
            "ORDINAL_CASE_SENSITIVE_WHITESPACE_PRESERVING",
            root.GetProperty("transportDemandKeyComparison").GetString());

        var capabilities = root.GetProperty("capabilities").EnumerateArray().ToArray();
        Assert.Equal(
            NewMesIngestContract.Capabilities.Select(capability => capability.Id),
            capabilities.Select(capability => capability.GetProperty("id").GetString()));
        Assert.Equal(
            NewMesIngestContract.Capabilities.Select(FlattenCapability),
            capabilities.Select(FlattenCapability));
        Assert.DoesNotContain(
            capabilities,
            capability => capability.GetRawText().Contains("/api/demands", StringComparison.Ordinal));

        foreach (var legacyPath in new[]
        {
            "/api/contract",
            "/api/demands",
            "/api/demands/legacy-probe",
            "/api/alerts",
            "/api/poll-health",
            "/api/demand-changes",
            "/openapi/v1.json",
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(legacyPath)).StatusCode);
        }
    }

    [Fact]
    public async Task Published_v2_openapi_contains_the_exact_read_surface_and_no_business_write_operations()
    {
        await using var factory = CreateV2Factory();
        var client = factory.CreateClient();

        using var response = await client.GetAsync(NewMesIngestContract.OpenApiDocumentPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "application/json",
            response.Content.Headers.ContentType?.MediaType,
            StringComparison.OrdinalIgnoreCase);

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(NewMesIngestContract.Version, root.GetProperty("info").GetProperty("version").GetString());
        Assert.Contains(
            NewMesIngestContract.CompatibilityPolicy,
            root.GetProperty("info").GetProperty("description").GetString(),
            StringComparison.Ordinal);

        var paths = root.GetProperty("paths");
        Assert.Equal(
            MesIngestOpenApi.V2ApiPaths.Order(StringComparer.Ordinal),
            paths.EnumerateObject().Select(path => path.Name).Order(StringComparer.Ordinal));
        var operationIds = new List<string>();
        foreach (var path in paths.EnumerateObject())
        {
            var methods = path.Value.EnumerateObject().Select(method => method.Name).ToArray();
            Assert.Equal(["get"], methods);
            var operation = path.Value.GetProperty("get");
            operationIds.Add(operation.GetProperty("operationId").GetString()!);
            Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
        }

        Assert.Equal(operationIds.Count, operationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.True(root.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _));
        Assert.All(
            ForbiddenV2Terms,
            term => Assert.DoesNotContain(term, json, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Runtime_exposes_no_business_write_method_outside_the_frozen_document()
    {
        await using var factory = CreateV2Factory();
        var client = factory.CreateClient();
        var writeMethods = new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete };

        foreach (var template in MesIngestOpenApi.V2ApiPaths)
        {
            var path = MaterializePath(template);
            foreach (var method in writeMethods)
            {
                using var request = new HttpRequestMessage(method, path);
                using var response = await client.SendAsync(request);
                Assert.Contains(
                    response.StatusCode,
                    new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
            }
        }
    }

    [Fact]
    public async Task Runtime_query_allow_lists_are_identical_to_the_published_openapi()
    {
        await using var factory = CreateV2Factory();
        var client = factory.CreateClient();
        using var document = JsonDocument.Parse(
            await client.GetStringAsync(NewMesIngestContract.OpenApiDocumentPath));

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            var operation = path.Value.GetProperty("get");
            var operationId = operation.GetProperty("operationId").GetString()!;
            var documentedQueryNames = operation.TryGetProperty("parameters", out var parameters)
                ? parameters.EnumerateArray()
                    .Where(parameter => string.Equals(
                        parameter.GetProperty("in").GetString(),
                        "query",
                        StringComparison.Ordinal))
                    .Select(parameter => parameter.GetProperty("name").GetString()!)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : [];

            Assert.Equal(
                documentedQueryNames,
                NewMesIngestEndpoints.AllowedQueryParameters(operationId)
                    .Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Published_v2_openapi_freezes_required_nullable_enum_time_snapshot_and_error_semantics()
    {
        await using var factory = CreateV2Factory();
        var client = factory.CreateClient();
        using var document = JsonDocument.Parse(
            await client.GetStringAsync(NewMesIngestContract.OpenApiDocumentPath));
        var root = document.RootElement;

        var error = Schema(root, "NewMesIngestErrorDto");
        AssertRequired(error, "code", "error");
        var historicalReadError = Schema(root, "HistoricalReadErrorDto");
        AssertRequired(
            historicalReadError,
            "code",
            "error",
            "historyEpoch");
        Assert.True(
            historicalReadError.GetProperty("properties")
                .GetProperty("earliestAvailableHostUtc").GetProperty("nullable").GetBoolean());
        Assert.Equal(
            "date-time",
            historicalReadError.GetProperty("properties")
                .GetProperty("earliestAvailableHostUtc").GetProperty("format").GetString());
        var contract = Schema(root, "NewMesIngestContractDto").GetProperty("properties");
        Assert.Equal(
            [NewMesIngestContract.Version],
            contract.GetProperty("contractVersion").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            [NewMesIngestContract.SchemaVersion],
            contract.GetProperty("schemaVersion").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(
            ["2.0", "2.1", "2.2", "1.0"],
            Schema(root, "NewMesIngestCapabilityDto").GetProperty("properties")
                .GetProperty("version").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            ["ORDINAL_CASE_SENSITIVE_WHITESPACE_PRESERVING"],
            contract.GetProperty("transportDemandKeyComparison").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            SeriesErrorCatalog.Definitions.Select(definition => definition.Code),
            Schema(root, "SeriesErrorDefinitionDto").GetProperty("properties")
                .GetProperty("code").GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal(
            SeriesErrorCatalog.Definitions.Select(definition => definition.Category)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            Schema(root, "SeriesErrorDefinitionDto").GetProperty("properties")
                .GetProperty("category").GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString()));
        var seriesErrorCatalog = root.GetProperty("x-mes-series-error-catalog")
            .EnumerateArray()
            .Select(item => $"{item.GetProperty("code").GetString()}|{item.GetProperty("category").GetString()}|{item.GetProperty("severity").GetString()}|{item.GetProperty("scope").GetString()}");
        Assert.Equal(
            SeriesErrorCatalog.Definitions.Select(definition =>
                $"{definition.Code}|{definition.Category}|{definition.Severity}|{definition.Scope}"),
            seriesErrorCatalog);

        var demandSeriesErrorPeriod = Schema(root, "DemandSeriesErrorPeriodDto")
            .GetProperty("properties");
        Assert.Equal(
            SeriesErrorCatalog.Definitions.Select(definition => definition.Code),
            demandSeriesErrorPeriod.GetProperty("code").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            SeriesErrorCatalog.Definitions.Select(definition => definition.Category)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            demandSeriesErrorPeriod.GetProperty("category").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));

        var seriesList = Schema(root, "DemandSeriesListDto");
        AssertRequired(
            seriesList,
            "snapshotReference",
            "snapshot",
            "exactTotalCount",
            "facets",
            "order",
            "pageSize",
            "pageNumber",
            "totalPages",
            "items",
            "hasMore");
        var pageSize = seriesList.GetProperty("properties").GetProperty("pageSize");
        Assert.Equal(100, pageSize.GetProperty("default").GetInt32());
        Assert.Equal(1, pageSize.GetProperty("minimum").GetInt32());
        Assert.Equal(200, pageSize.GetProperty("maximum").GetInt32());
        Assert.Equal(
            ["STARTED_AT_DESC_SERIES_ID_ASC"],
            seriesList.GetProperty("properties").GetProperty("order")
                .GetProperty("enum").EnumerateArray().Select(value => value.GetString()));

        var catalog = Schema(root, "ExternallyReadableDemandCatalogDto");
        AssertRequired(catalog, "contractVersion", "catalogRevision", "count", "items");
        Assert.True(
            catalog.GetProperty("properties").GetProperty("projectionCommitId")
                .GetProperty("nullable").GetBoolean());

        var snapshot = Schema(root, "DemandSeriesSnapshotIdentityDto");
        Assert.Equal(
            [NewMesIngestContract.Version],
            snapshot.GetProperty("properties").GetProperty("contractVersion")
                .GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            "date-time",
            snapshot.GetProperty("properties").GetProperty("projectionCommittedAt")
                .GetProperty("format").GetString());
        Assert.Contains(
            "UTC",
            snapshot.GetProperty("properties").GetProperty("projectionCommittedAt")
                .GetProperty("description").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "offset",
            Schema(root, "LiveMesFieldSetDto").GetProperty("properties")
                .GetProperty("mesSourceDate").GetProperty("description").GetString(),
            StringComparison.OrdinalIgnoreCase);

        var rawOperation = root.GetProperty("paths")
            .GetProperty("/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations")
            .GetProperty("get");
        AssertParameter(rawOperation, "maxItems", maximum: 20);
        AssertParameterEnum(
            rawOperation,
            "fields",
            "workType",
            "sublot",
            "area",
            "eqp",
            "step",
            "mesSourceDate",
            "package");
        var fieldsParameter = rawOperation.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "fields");
        Assert.Equal("form", fieldsParameter.GetProperty("style").GetString());
        Assert.True(
            !fieldsParameter.TryGetProperty("explode", out var explode)
            || explode.GetBoolean());
        Assert.Contains(
            "repeated",
            fieldsParameter.GetProperty("description").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "comma",
            fieldsParameter.GetProperty("description").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(rawOperation.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(rawOperation.GetProperty("responses").TryGetProperty("413", out _));

        var attention = Schema(root, "CurrentIngestAttentionOperationalDto");
        AssertRequired(attention, "pollScheduler");
        var scheduler = Schema(root, "PollSchedulerStateDto");
        AssertRequired(scheduler, "consecutiveFailures", "backoffLevel");
        Assert.Equal(
            0,
            scheduler.GetProperty("properties").GetProperty("consecutiveFailures")
                .GetProperty("minimum").GetInt32());
        var backoffLevel = scheduler.GetProperty("properties").GetProperty("backoffLevel");
        Assert.Equal(0, backoffLevel.GetProperty("minimum").GetInt32());
        Assert.Equal(3, backoffLevel.GetProperty("maximum").GetInt32());
        foreach (var nullableField in new[] { "nextAllowedStart", "lastSuccessAt", "pollTraceId" })
        {
            Assert.True(
                scheduler.GetProperty("properties").GetProperty(nullableField)
                    .GetProperty("nullable").GetBoolean(),
                $"PollSchedulerStateDto.{nullableField} must be explicitly nullable.");
        }
        Assert.Equal(
            StoragePressureStatuses.All,
            Schema(root, "StoragePressureStateDto").GetProperty("properties")
                .GetProperty("status").GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString()));

        var pollTrace = Schema(root, "PollTraceDto");
        AssertRequired(
            pollTrace,
            "historyEpoch",
            "earliestAvailableHostUtc",
            "pollTraceId",
            "observations");
        var pollTraceOperation = root.GetProperty("paths")
            .GetProperty("/api/v2/poll-traces/{pollTraceId}")
            .GetProperty("get");
        Assert.True(pollTraceOperation.GetProperty("responses").TryGetProperty("410", out var expired));
        Assert.Contains(
            "MES_INGEST_HISTORY_EXPIRED",
            expired.GetProperty("description").GetString(),
            StringComparison.Ordinal);
        foreach (var path in new[]
        {
            "/api/v2/demand-series/{seriesId}",
            "/api/v2/readability-audit/{demandId}",
            "/api/v2/error-search/{seriesId}",
            "/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations",
        })
        {
            var gone = root.GetProperty("paths")
                .GetProperty(path)
                .GetProperty("get")
                .GetProperty("responses")
                .GetProperty("410");
            Assert.Contains(
                PollEvidenceErrorCodes.MesIngestHistoryExpired,
                gone.GetProperty("description").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(
                2,
                gone.GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema")
                    .GetProperty("oneOf")
                    .GetArrayLength());
        }

        var catalogOperation = root.GetProperty("paths")
            .GetProperty("/api/v2/externally-readable-demand-catalog")
            .GetProperty("get");
        Assert.True(catalogOperation.GetProperty("responses").TryGetProperty("304", out var notModified));
        Assert.True(notModified.GetProperty("headers").TryGetProperty("ETag", out _));
        Assert.True(catalogOperation.GetProperty("responses").TryGetProperty("409", out var epochMismatch));
        Assert.Contains(
            HistoryEpochMismatchException.ErrorCode,
            epochMismatch.GetProperty("description").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            "#/components/schemas/HistoryEpochMismatchErrorDto",
            epochMismatch.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
        var epochMismatchSchema = Schema(root, "HistoryEpochMismatchErrorDto");
        AssertRequired(
            epochMismatchSchema,
            "code",
            "error",
            "currentHistoryEpoch",
            "suppliedHistoryEpoch");
        Assert.Equal(
            "uuid",
            epochMismatchSchema.GetProperty("properties").GetProperty("currentHistoryEpoch")
                .GetProperty("format").GetString());
        Assert.Equal(
            "uuid",
            epochMismatchSchema.GetProperty("properties").GetProperty("suppliedHistoryEpoch")
                .GetProperty("format").GetString());
        var ingestNotCurrent = catalogOperation.GetProperty("responses").GetProperty("503")
            .GetProperty("description").GetString();
        Assert.Contains("StoragePressurePause", ingestNotCurrent, StringComparison.Ordinal);
        Assert.Contains("HistoryReset", ingestNotCurrent, StringComparison.Ordinal);
        Assert.DoesNotContain(
            catalogOperation.GetProperty("description").GetString() ?? "",
            "cursor",
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "ERROR_SEARCH_CURSOR_MISMATCH",
            Schema(root, "NewMesIngestErrorDto").GetProperty("description").GetString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/v2/demand-series/by-key")]
    [InlineData("/api/v2/demand-series/by-key?workType=&sublot=SL")]
    [InlineData("/api/v2/demand-series/by-key?workType=CUT&sublot=")]
    [InlineData("/api/v2/demand-series/by-key?workType=CUT&workType=WIRE&sublot=SL")]
    [InlineData("/api/v2/demand-series/by-key?workType=CUT&sublot=SL&sublot=SL2")]
    public async Task Demand_series_by_key_missing_empty_and_repeated_keys_use_the_stable_typed_error(
        string path)
    {
        await using var factory = CreateV2Factory();
        var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(body);
        Assert.Equal(
            DemandSeriesBrowseErrorCodes.InvalidQuery,
            error.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Catalog_old_epoch_exception_is_a_typed_http_409_response()
    {
        var currentEpoch = HistoryEpoch.FromGuid(
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var suppliedEpoch = HistoryEpoch.FromGuid(
            Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var projection = CatalogMismatchProjection.Create(currentEpoch, suppliedEpoch);
        await using var configuredFactory = CreateV2Factory();
        await using var factory = configuredFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMesIngestProjection>();
                services.AddSingleton(projection);
            }));
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v2/externally-readable-demand-catalog");
        request.Headers.TryAddWithoutValidation(
            "If-None-Match",
            $"W/\"{ExternallyReadableDemandCatalogEtagCodec.FormatOpaqueTag(
                new ExternallyReadableDemandCatalogIdentity(suppliedEpoch, 7))}\"");

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            HistoryEpochMismatchException.ErrorCode,
            body.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            currentEpoch.Value,
            body.RootElement.GetProperty("currentHistoryEpoch").GetGuid());
        Assert.Equal(
            suppliedEpoch.Value,
            body.RootElement.GetProperty("suppliedHistoryEpoch").GetGuid());
    }

    [Fact]
    public void Raw_evidence_fields_parse_comma_repeated_and_mixed_query_forms_identically()
    {
        var variants = new[]
        {
            new StringValues("workType,package"),
            new StringValues(["workType", "package"]),
            new StringValues(["workType", "package,workType"]),
        };

        foreach (var fields in variants)
        {
            var query = new QueryCollection(new Dictionary<string, StringValues>
            {
                ["snapshot"] = "snapshot-ticket4",
                ["fields"] = fields,
                ["maxItems"] = "7",
            });

            var parsed = NewMesIngestEndpoints.ParseErrorSearchRawEvidenceRequest(query);

            Assert.Equal("snapshot-ticket4", parsed.SnapshotReference);
            Assert.Equal(["workType", "package"], parsed.Query.Fields);
            Assert.Equal(7, parsed.Query.MaxItems);
        }
    }

    [Fact]
    public async Task Runtime_rejects_every_undocumented_v2_query_parameter_with_a_structured_error()
    {
        await using var factory = CreateV2Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "contract-secret");

        var expectedCodes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v2/contract"] = "INVALID_CONTRACT_QUERY",
            ["/api/v2/demand-series"] = "INVALID_DEMAND_SERIES_QUERY",
            ["/api/v2/demand-series/by-key"] = "INVALID_DEMAND_SERIES_QUERY",
            ["/api/v2/demand-series/{seriesId}"] = "INVALID_DEMAND_SERIES_QUERY",
            ["/api/v2/externally-readable-demand-catalog"] = "CATALOG_QUERY_NOT_SUPPORTED",
            ["/api/v2/readability-audit"] = "INVALID_READABILITY_AUDIT_QUERY",
            ["/api/v2/readability-audit/{demandId}"] = "INVALID_READABILITY_AUDIT_QUERY",
            ["/api/v2/error-search"] = "INVALID_ERROR_SEARCH_QUERY",
            ["/api/v2/error-search/{seriesId}"] = "INVALID_ERROR_SEARCH_QUERY",
            ["/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations"] = "INVALID_ERROR_SEARCH_QUERY",
            ["/api/v2/current-ingest-attention"] = "CURRENT_INGEST_ATTENTION_INVALID_QUERY",
            ["/api/v2/watch-overview"] = "WATCH_OVERVIEW_INVALID_QUERY",
            ["/api/v2/sublot-box-count"] = "SUBLOT_BOX_COUNT_INVALID_QUERY",
            ["/api/v2/poll-traces/{pollTraceId}"] = "INVALID_POLL_EVIDENCE_QUERY",
            ["/api/v2/absence-authority"] = "INVALID_POLL_EVIDENCE_QUERY",
            ["/api/v2/absence-authority/{hostSessionId}"] = "INVALID_POLL_EVIDENCE_QUERY",
            ["/api/v2/task-type-protections"] = "INVALID_POLL_EVIDENCE_QUERY",
            ["/api/v2/task-type-protections/{workType}"] = "INVALID_POLL_EVIDENCE_QUERY",
        };
        Assert.Equal(
            MesIngestOpenApi.V2ApiPaths.Order(StringComparer.Ordinal),
            expectedCodes.Keys.Order(StringComparer.Ordinal));

        foreach (var (template, expectedCode) in expectedCodes)
        {
            var path = MaterializePath(template);
            using var response = await client.GetAsync(path + "?undocumented=1");
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.StatusCode == HttpStatusCode.BadRequest,
                $"{template} returned {(int)response.StatusCode}: {body}");
            using var error = JsonDocument.Parse(body);
            Assert.Equal(expectedCode, error.RootElement.GetProperty("code").GetString());
            var errorMessage = error.RootElement.GetProperty("error").GetString();
            if (string.Equals(
                    template,
                    "/api/v2/externally-readable-demand-catalog",
                    StringComparison.Ordinal))
            {
                Assert.False(string.IsNullOrWhiteSpace(errorMessage));
            }
            else
            {
                Assert.Contains("undocumented", errorMessage, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task V2_authentication_and_restricted_evidence_access_match_openapi()
    {
        await using (var remoteFactory = CreateV2Factory(remoteBinding: true))
        {
            var remoteClient = remoteFactory.CreateClient();
            Assert.Equal(
                HttpStatusCode.OK,
                (await remoteClient.GetAsync(NewMesIngestContract.OpenApiDocumentPath)).StatusCode);

            using var unauthorized = await remoteClient.GetAsync("/api/v2/contract");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using var unauthorizedBody = JsonDocument.Parse(
                await unauthorized.Content.ReadAsStringAsync());
            Assert.Equal("UNAUTHORIZED", unauthorizedBody.RootElement.GetProperty("code").GetString());

            remoteClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "contract-secret");
            Assert.Equal(
                HttpStatusCode.OK,
                (await remoteClient.GetAsync("/api/v2/contract")).StatusCode);
        }

        await using (var loopbackFactory = CreateV2Factory())
        {
            var loopbackClient = loopbackFactory.CreateClient();
            const string rawPath =
                "/api/v2/error-search/series-contract/evidence/evidence-contract/raw-observations";
            using var denied = await loopbackClient.GetAsync(rawPath);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var deniedBody = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
            Assert.Equal(
                "RAW_EVIDENCE_ACCESS_DENIED",
                deniedBody.RootElement.GetProperty("code").GetString());

            loopbackClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "contract-secret");
            using var authorizedButInvalid = await loopbackClient.GetAsync(rawPath);
            Assert.Equal(HttpStatusCode.BadRequest, authorizedButInvalid.StatusCode);

            using var overLimit = await loopbackClient.GetAsync(
                rawPath + "?snapshot=opaque&maxItems=21");
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overLimit.StatusCode);
            using var overLimitBody = JsonDocument.Parse(await overLimit.Content.ReadAsStringAsync());
            Assert.Equal(
                "RAW_EVIDENCE_LIMIT_EXCEEDED",
                overLimitBody.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Path_identity_validation_returns_capability_specific_error_codes()
    {
        await using var factory = CreateV2Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "contract-secret");

        var tooLong64 = new string('x', 65);
        var tooLong128 = new string('x', 129);
        var cases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"/api/v2/demand-series/{tooLong64}"] = "INVALID_DEMAND_SERIES_QUERY",
            [$"/api/v2/demand-series/by-key?workType={tooLong128}&sublot=s"] = "INVALID_DEMAND_SERIES_QUERY",
            [$"/api/v2/readability-audit/{tooLong64}?snapshot=opaque"] = "INVALID_READABILITY_AUDIT_QUERY",
            [$"/api/v2/error-search/{tooLong64}?snapshot=opaque"] = "INVALID_ERROR_SEARCH_QUERY",
            [$"/api/v2/error-search/{tooLong64}/evidence/evidence/raw-observations?snapshot=opaque"] = "INVALID_ERROR_SEARCH_QUERY",
            [$"/api/v2/poll-traces/{tooLong128}"] = "INVALID_POLL_TRACE_ID",
            [$"/api/v2/absence-authority/{tooLong64}"] = "INVALID_HOST_SESSION_ID",
            [$"/api/v2/task-type-protections/{tooLong128}"] = "INVALID_WORK_TYPE",
        };

        foreach (var (path, expectedCode) in cases)
        {
            await AssertErrorAsync(client, path, HttpStatusCode.BadRequest, expectedCode);
        }
    }

    [Ticket01SqlServerFact]
    public async Task Runtime_round_sql_api_responses_conform_to_the_published_contract()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await using var factory = CreateV2Factory(connectionString: database.ConnectionString);
        var client = factory.CreateClient();

        using (var unavailable = await client.GetAsync("/api/v2/demand-series"))
        {
            Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
            using var error = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
            Assert.Equal(
                "DEMAND_SERIES_PROJECTION_NOT_AVAILABLE",
                error.RootElement.GetProperty("code").GetString());
        }

        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var startedAt = new DateTimeOffset(2026, 8, 14, 1, 2, 3, TimeSpan.Zero);
        var receipt = await ingestor.IngestAsync(new MesTaskUnionRound(
            "poll-ticket17-contract",
            "mes-task-union-ticket17",
            MesTaskUnionRoundOutcome.Success,
            startedAt,
            startedAt.AddSeconds(2),
            [
                new MesTaskUnionObservation(
                    "CUT",
                    "SUBLOT-TICKET17",
                    "N3-3",
                    "EQ-17",
                    "STEP-17",
                    new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.FromHours(8)),
                    "QFN"),
            ]));

        using (var empty = JsonDocument.Parse(
            await client.GetStringAsync("/api/v2/demand-series?workType=NO_MATCH")))
        {
            Assert.Equal(0, empty.RootElement.GetProperty("exactTotalCount").GetInt64());
            Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(100, empty.RootElement.GetProperty("pageSize").GetInt32());
            Assert.Equal("STARTED_AT_DESC_SERIES_ID_ASC", empty.RootElement.GetProperty("order").GetString());
        }

        string demandSnapshotReference;
        using (var nonEmpty = JsonDocument.Parse(
            await client.GetStringAsync("/api/v2/demand-series")))
        {
            demandSnapshotReference = nonEmpty.RootElement.GetProperty("snapshotReference").GetString()!;
            Assert.Equal(1, nonEmpty.RootElement.GetProperty("exactTotalCount").GetInt64());
            Assert.Single(nonEmpty.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(
                NewMesIngestContract.Version,
                nonEmpty.RootElement.GetProperty("snapshot").GetProperty("contractVersion").GetString());
            Assert.Equal(
                receipt.ProjectionCommitId,
                nonEmpty.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
            Assert.Equal(
                TimeSpan.Zero,
                nonEmpty.RootElement.GetProperty("snapshot").GetProperty("projectionCommittedAt")
                    .GetDateTimeOffset().Offset);
        }

        await AssertErrorAsync(
            client,
            "/api/v2/demand-series/missing-series?snapshot="
                + Uri.EscapeDataString(demandSnapshotReference),
            HttpStatusCode.NotFound,
            "DEMAND_SERIES_OBJECT_NOT_IN_SNAPSHOT");
        await AssertErrorAsync(
            client,
            "/api/v2/demand-series/by-key?workType=MISSING&sublot=MISSING&snapshot="
                + Uri.EscapeDataString(demandSnapshotReference),
            HttpStatusCode.NotFound,
            "DEMAND_SERIES_OBJECT_NOT_IN_SNAPSHOT");

        using var readability = JsonDocument.Parse(
            await client.GetStringAsync("/api/v2/readability-audit"));
        var readabilitySnapshotReference = readability.RootElement
            .GetProperty("snapshotReference").GetString()!;
        await AssertErrorAsync(
            client,
            "/api/v2/readability-audit/missing-demand?snapshot="
                + Uri.EscapeDataString(readabilitySnapshotReference),
            HttpStatusCode.NotFound,
            "READABILITY_AUDIT_OBJECT_NOT_IN_SNAPSHOT");
        await AssertErrorAsync(
            client,
            "/api/v2/poll-traces/missing-poll-trace",
            HttpStatusCode.NotFound,
            "POLL_TRACE_NOT_FOUND");
        await AssertErrorAsync(
            client,
            "/api/v2/absence-authority/missing-host-session",
            HttpStatusCode.NotFound,
            "ABSENCE_AUTHORITY_NOT_FOUND");
        await AssertErrorAsync(
            client,
            "/api/v2/task-type-protections/MISSING_WORK_TYPE",
            HttpStatusCode.NotFound,
            "TASK_TYPE_PROTECTION_NOT_FOUND");

        using var catalog = await client.GetAsync("/api/v2/externally-readable-demand-catalog");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.NotNull(catalog.Headers.ETag);
        using var catalogJson = JsonDocument.Parse(await catalog.Content.ReadAsStringAsync());
        Assert.Equal(1, catalogJson.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(
            NewMesIngestContract.Version,
            catalogJson.RootElement.GetProperty("contractVersion").GetString());

        using var conditional = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v2/externally-readable-demand-catalog");
        conditional.Headers.IfNoneMatch.Add(catalog.Headers.ETag!);
        using var notModified = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());

        using var invalidPage = await client.GetAsync("/api/v2/demand-series?pageSize=201");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        using var invalidPageJson = JsonDocument.Parse(await invalidPage.Content.ReadAsStringAsync());
        Assert.Equal(
            "INVALID_DEMAND_SERIES_QUERY",
            invalidPageJson.RootElement.GetProperty("code").GetString());

        var signingKey = await ReadSnapshotSigningKeyAsync(database.ConnectionString);
        var historyEpoch = await ReadHistoryEpochAsync(database.ConnectionString);
        var missingSnapshot = DemandSeriesSnapshotTokenCodec.CreateSnapshotReference(
            new DemandSeriesSnapshotIdentity(
                historyEpoch,
                "commit-ticket17-not-retained",
                999_999,
                startedAt,
                "poll-ticket17-not-retained"),
            signingKey);
        using var stale = await client.GetAsync(
            "/api/v2/demand-series?snapshot=" + Uri.EscapeDataString(missingSnapshot));
        Assert.Equal(HttpStatusCode.Gone, stale.StatusCode);
        using var staleJson = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal(
            "DEMAND_SERIES_SNAPSHOT_NOT_FOUND",
            staleJson.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Live_v2_openapi_matches_the_canonical_pack_snapshot()
    {
        await using var factory = CreateV2Factory();
        var client = factory.CreateClient();
        var live = JsonNode.Parse(
            await client.GetStringAsync(NewMesIngestContract.OpenApiDocumentPath));
        var path = Path.Combine(PackRoot, "openapi", "v2.json");
        Assert.True(File.Exists(path), $"Missing canonical V2 OpenAPI snapshot: {path}");
        var packed = JsonNode.Parse(await File.ReadAllTextAsync(path));

        Assert.True(
            JsonNode.DeepEquals(packed, live),
            "Canonical pack/openapi/v2.json must match live info, paths, components, and security exactly.");
    }

    [Fact]
    public async Task Export_canonical_v2_openapi_when_MES_INGEST_EXPORT_V2_OPENAPI_is_1()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MES_INGEST_EXPORT_V2_OPENAPI"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        await using var factory = CreateV2Factory();
        var client = factory.CreateClient();
        var live = JsonNode.Parse(
            await client.GetStringAsync(NewMesIngestContract.OpenApiDocumentPath))
            ?? throw new InvalidDataException("The live V2 OpenAPI document was empty.");
        var canonical = live.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            .ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(PackRoot, "openapi", "v2.json");
        await File.WriteAllTextAsync(path, canonical);
        Assert.True(File.Exists(path));
    }

    private WebApplicationFactory<Program> CreateV2Factory(
        bool remoteBinding = false,
        string? connectionString = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            var isolatedContractFixture = connectionString is null;
            builder.UseEnvironment(
                isolatedContractFixture ? Environments.Production : Environments.Development);
            builder.UseSetting(
                $"{MesIngestHostOptions.SectionName}:NewSqlServerConnectionString",
                connectionString
                    ?? "Server=contract.invalid;Database=contract;Integrated Security=true;Encrypt=false");
            builder.UseSetting(
                $"{MesIngestHostOptions.SectionName}:SnapshotSource",
                isolatedContractFixture
                    ? MesIngestHostOptions.OracleRoundSource
                    : MesIngestHostOptions.NoRoundSource);
            builder.UseSetting(
                $"{MesIngestHostOptions.SectionName}:ContinuousPollEnabled",
                isolatedContractFixture ? "true" : "false");
            builder.UseSetting($"{MesIngestHostOptions.SectionName}:RunOneShotOnStartup", "false");
            builder.UseSetting(
                $"{MesIngestHostOptions.SectionName}:SharedSecret",
                "contract-secret");
            builder.UseSetting(
                $"{MesIngestHostOptions.SectionName}:Urls",
                remoteBinding ? "http://0.0.0.0:5088" : "http://127.0.0.1:5088");
            if (connectionString is null)
            {
                builder.ConfigureTestServices(services =>
                {
                    // The startup configuration is a valid Production producer,
                    // but this in-memory contract fixture must not contact SQL or Oracle.
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<MesIngestHostOptions>();
                    services.AddSingleton(new MesIngestHostOptions
                    {
                        NewSqlServerConnectionString =
                            "Server=contract.invalid;Database=contract;Integrated Security=true;Encrypt=false",
                        SnapshotSource = MesIngestHostOptions.NoRoundSource,
                        ContinuousPollEnabled = false,
                        RunOneShotOnStartup = false,
                        SharedSecret = "contract-secret",
                        Urls = remoteBinding
                            ? "http://0.0.0.0:5088"
                            : "http://127.0.0.1:5088",
                    });
                });
            }
        });

    private static async Task<byte[]> ReadSnapshotSigningKeyAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SnapshotTokenSigningKey FROM mesingest.SchemaInfo WHERE Id = 1;";
        return await command.ExecuteScalarAsync() as byte[]
            ?? throw new InvalidOperationException("Snapshot token signing key is unavailable.");
    }

    private static async Task<HistoryEpoch> ReadHistoryEpochAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT HistoryEpoch FROM mesingest.SchemaInfo WHERE Id = 1;";
        return HistoryEpoch.FromGuid((Guid)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("HistoryEpoch is unavailable.")));
    }

    private static async Task AssertErrorAsync(
        HttpClient client,
        string path,
        HttpStatusCode status,
        string code)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
    }

    private static string FlattenCapability(NewMesIngestCapability capability) =>
        $"{capability.Id}|{capability.Version}|{string.Join(';', capability.Operations.Select(operation => $"{operation.Method} {operation.Path}"))}";

    private static string FlattenCapability(JsonElement capability) =>
        $"{capability.GetProperty("id").GetString()}|{capability.GetProperty("version").GetString()}|{string.Join(';', capability.GetProperty("operations").EnumerateArray().Select(operation => $"{operation.GetProperty("method").GetString()} {operation.GetProperty("path").GetString()}"))}";

    private static string MaterializePath(string template) => template
        .Replace("{seriesId}", "series-contract", StringComparison.Ordinal)
        .Replace("{evidenceId}", "evidence-contract", StringComparison.Ordinal)
        .Replace("{demandId}", "demand-contract", StringComparison.Ordinal)
        .Replace("{pollTraceId}", "poll-contract", StringComparison.Ordinal)
        .Replace("{hostSessionId}", "host-contract", StringComparison.Ordinal)
        .Replace("{workType}", "CUT", StringComparison.Ordinal);

    private static JsonElement Schema(JsonElement root, string name) =>
        root.GetProperty("components").GetProperty("schemas").GetProperty(name);

    private static string PackRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var pack = Path.Combine(directory.FullName, "pack");
                if (File.Exists(Path.Combine(pack, "Publish-MesIngest.ps1")))
                {
                    return pack;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate the MesIngest pack directory.");
        }
    }

    private static void AssertRequired(JsonElement schema, params string[] names)
    {
        var required = schema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(names, name => Assert.Contains(name, required));
    }

    private static void AssertParameter(JsonElement operation, string name, int maximum)
    {
        var parameter = operation.GetProperty("parameters").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == name);
        Assert.Equal(maximum, parameter.GetProperty("schema").GetProperty("maximum").GetInt32());
    }

    private static void AssertParameterEnum(
        JsonElement operation,
        string name,
        params string[] expected)
    {
        var parameter = operation.GetProperty("parameters").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == name);
        Assert.Equal(
            expected,
            parameter.GetProperty("schema").GetProperty("items").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
    }

    public class CatalogMismatchProjection : DispatchProxy
    {
        private HistoryEpoch _currentEpoch = null!;
        private HistoryEpoch _suppliedEpoch = null!;

        public static IMesIngestProjection Create(
            HistoryEpoch currentEpoch,
            HistoryEpoch suppliedEpoch)
        {
            var projection = DispatchProxy.Create<
                IMesIngestProjection,
                CatalogMismatchProjection>();
            var proxy = (CatalogMismatchProjection)(object)projection;
            proxy._currentEpoch = currentEpoch;
            proxy._suppliedEpoch = suppliedEpoch;
            return projection;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(
                    IMesIngestProjection.ReadExternallyReadableDemandCatalogAsync))
            {
                return Task.FromException<ExternallyReadableDemandCatalogRead>(
                    new HistoryEpochMismatchException(_currentEpoch, _suppliedEpoch));
            }

            throw new NotSupportedException(
                $"Unexpected projection call '{targetMethod?.Name ?? "<missing>"}'.");
        }
    }
}
