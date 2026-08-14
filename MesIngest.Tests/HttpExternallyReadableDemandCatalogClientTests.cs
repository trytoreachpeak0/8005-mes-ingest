using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.ReferenceConsumer;

namespace MesIngest.Tests;

public sealed class HttpExternallyReadableDemandCatalogClientTests
{
    private const string CompleteCatalogBody = """
        {
          "contractVersion": "2026.08.new-mes-ingest.v2.0",
          "catalogRevision": 12,
          "projectionCommitId": "commit-12",
          "projectionSequence": 34,
          "projectionCommittedAt": "2026-08-13T08:12:00+00:00",
          "count": 1,
          "items": [
            {
              "demandId": "demand-a",
              "seriesId": "series-a",
              "transportDemandKey": {
                "workType": "CUT",
                "sublot": "SUBLOT-A"
              },
              "generation": 2,
              "demandRevision": 5,
              "createdAt": "2026-08-13T07:00:00+00:00",
              "valueObservedAt": "2026-08-13T08:00:00+00:00",
              "valuePollTraceId": "poll-value",
              "valueProjectionCommitId": "commit-value",
              "liveMesFields": {
                "area": "N3-3",
                "eqp": "WB-03",
                "step": "STEP-2",
                "mesSourceDate": "2026-08-12T07:00:00+00:00",
                "package": "QFN-G1"
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task Complete_200_maps_the_host_contract_and_weak_etag_without_query_parameters()
    {
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == HttpExternallyReadableDemandCatalogClient.ContractPath)
            {
                return JsonResponse(HttpStatusCode.OK, CreateContractBody());
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CompleteCatalogBody, Encoding.UTF8, "application/json"),
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"catalog-r12\"", isWeak: true);
            return response;
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mes.example/ignored-base-path/"),
        };
        var client = new HttpExternallyReadableDemandCatalogClient(httpClient);

        var read = await client.ReadAsync(knownRevision: null);

        Assert.False(read.NotModified);
        Assert.Equal(12, read.CatalogRevision);
        var snapshot = Assert.IsType<MesIngest.Core.SeriesProjection.ExternallyReadableDemandCatalogSnapshot>(
            read.Snapshot);
        Assert.Equal("commit-12", snapshot.ProjectionCommitId);
        Assert.Equal(34, snapshot.ProjectionSequence);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 13, 8, 12, 0, TimeSpan.Zero),
            snapshot.ProjectionCommittedAt);
        var demand = Assert.Single(snapshot.Items);
        Assert.Equal("demand-a", demand.DemandId);
        Assert.Equal("series-a", demand.SeriesId);
        Assert.Equal("CUT", demand.WorkType);
        Assert.Equal("SUBLOT-A", demand.Sublot);
        Assert.Equal(2, demand.Generation);
        Assert.Equal(5, demand.DemandRevision);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 13, 7, 0, 0, TimeSpan.Zero),
            demand.CreatedAt);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero),
            demand.ValueObservedAt);
        Assert.Equal("poll-value", demand.ValuePollTraceId);
        Assert.Equal("commit-value", demand.ValueProjectionCommitId);
        Assert.Equal("N3-3", demand.LiveMesFields.Area);
        Assert.Equal("WB-03", demand.LiveMesFields.Eqp);
        Assert.Equal("STEP-2", demand.LiveMesFields.Step);
        Assert.Equal("QFN-G1", demand.LiveMesFields.Package);

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/api/v2/contract", request.PathAndQuery);
                Assert.Empty(request.IfNoneMatch);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/api/v2/externally-readable-demand-catalog", request.PathAndQuery);
                Assert.Empty(request.IfNoneMatch);
            });
    }

    [Fact]
    public async Task Conditional_read_sends_the_weak_etag_and_maps_304_without_reading_a_body()
    {
        var content = new ThrowIfReadContent();
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == HttpExternallyReadableDemandCatalogClient.ContractPath)
            {
                return JsonResponse(HttpStatusCode.OK, CreateContractBody());
            }

            var response = new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Content = content,
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"catalog-r12\"", isWeak: true);
            return response;
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mes.example/"),
        };
        var client = new HttpExternallyReadableDemandCatalogClient(httpClient);

        var read = await client.ReadAsync(knownRevision: 12);

        Assert.True(read.NotModified);
        Assert.Equal(12, read.CatalogRevision);
        Assert.Null(read.Snapshot);
        Assert.False(content.WasRead);
        Assert.Equal(2, handler.Requests.Count);
        var request = handler.Requests[1];
        Assert.Equal("/api/v2/externally-readable-demand-catalog", request.PathAndQuery);
        Assert.Equal(["W/\"catalog-r12\""], request.IfNoneMatch);
    }

    [Fact]
    public async Task Initial_empty_catalog_has_revision_zero_and_no_fabricated_projection_commit()
    {
        const string body = """
            {
              "contractVersion": "2026.08.new-mes-ingest.v2.0",
              "catalogRevision": 0,
              "projectionCommitId": null,
              "projectionSequence": null,
              "projectionCommittedAt": null,
              "count": 0,
              "items": []
            }
            """;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == HttpExternallyReadableDemandCatalogClient.ContractPath)
            {
                return JsonResponse(HttpStatusCode.OK, CreateContractBody());
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"catalog-r0\"", isWeak: true);
            return response;
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mes.example/"),
        };
        var client = new HttpExternallyReadableDemandCatalogClient(httpClient);

        var read = await client.ReadAsync(knownRevision: null);

        var snapshot = Assert.IsType<MesIngest.Core.SeriesProjection.ExternallyReadableDemandCatalogSnapshot>(
            read.Snapshot);
        Assert.Equal(0, snapshot.CatalogRevision);
        Assert.Null(snapshot.ProjectionCommitId);
        Assert.Null(snapshot.ProjectionSequence);
        Assert.Null(snapshot.ProjectionCommittedAt);
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public async Task Complete_200_rejects_a_mismatched_contract_version()
    {
        var mismatchedBody = CompleteCatalogBody.Replace(
            "2026.08.new-mes-ingest.v2.0",
            "2026.08.new-mes-ingest.tracer.8",
            StringComparison.Ordinal);
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == HttpExternallyReadableDemandCatalogClient.ContractPath)
            {
                return JsonResponse(HttpStatusCode.OK, CreateContractBody());
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mismatchedBody, Encoding.UTF8, "application/json"),
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"catalog-r12\"", isWeak: true);
            return response;
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mes.example/"),
        };
        var client = new HttpExternallyReadableDemandCatalogClient(httpClient);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.ReadAsync(knownRevision: null));

        Assert.StartsWith("CONTRACT_VERSION_MISMATCH:", error.Message, StringComparison.Ordinal);
        Assert.Contains("contractVersion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_response_without_an_etag_preserves_the_http_status()
    {
        var handler = new RecordingHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath == HttpExternallyReadableDemandCatalogClient.ContractPath
                ? JsonResponse(HttpStatusCode.OK, CreateContractBody())
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mes.example/"),
        };
        var client = new HttpExternallyReadableDemandCatalogClient(httpClient);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ReadAsync(knownRevision: null));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    [Theory]
    [InlineData("contract-version")]
    [InlineData("schema-version")]
    [InlineData("missing-capability")]
    [InlineData("additional-capability")]
    [InlineData("duplicate-capability")]
    [InlineData("capability-version")]
    public async Task Contract_discovery_mismatch_refuses_business_interpretation_before_catalog_read(
        string mismatch)
    {
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == HttpExternallyReadableDemandCatalogClient.ContractPath)
            {
                return JsonResponse(HttpStatusCode.OK, CreateContractBody(mismatch));
            }

            return JsonResponse(HttpStatusCode.OK, CompleteCatalogBody);
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mes.example/"),
        };
        var client = new HttpExternallyReadableDemandCatalogClient(httpClient);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.ReadAsync(knownRevision: null));

        Assert.StartsWith("CONTRACT_VERSION_MISMATCH:", error.Message, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v2/contract", request.PathAndQuery);
    }

    private static string CreateContractBody(string? mismatch = null)
    {
        var contractVersion = NewMesIngestContract.Version;
        var schemaVersion = NewMesIngestContract.SchemaVersion;
        var capabilities = NewMesIngestContract.Capabilities
            .Select(capability => new ContractCapabilityBody(capability.Id, capability.Version))
            .ToList();

        switch (mismatch)
        {
            case "contract-version":
                contractVersion += ".incompatible";
                break;
            case "schema-version":
                schemaVersion++;
                break;
            case "missing-capability":
                capabilities.RemoveAt(capabilities.Count - 1);
                break;
            case "additional-capability":
                capabilities.Add(new ContractCapabilityBody("UNRECOGNIZED_CAPABILITY", "1.0"));
                break;
            case "duplicate-capability":
                capabilities.Add(capabilities[0]);
                break;
            case "capability-version":
                capabilities[0] = capabilities[0] with { Version = "2.0" };
                break;
        }

        return JsonSerializer.Serialize(
            new
            {
                contractVersion,
                schemaVersion,
                capabilities,
            });
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.IfNoneMatch.Select(value => value.ToString()).ToArray()));
            return Task.FromResult(respond(request));
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string PathAndQuery,
        IReadOnlyList<string> IfNoneMatch);

    private sealed record ContractCapabilityBody(string Id, string Version);

    private sealed class ThrowIfReadContent : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            WasRead = true;
            throw new InvalidOperationException("A 304 response body must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }
    }
}
