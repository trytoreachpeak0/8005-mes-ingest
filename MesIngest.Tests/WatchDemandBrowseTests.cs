using System.Net;
using System.Text;
using MesIngest.Core;
using MesIngest.Host;
using MesIngest.Watch;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 07 seams: WatchDemandBrowseQuery → Host query string, and
/// MesIngestApiClient paginated /api/demands envelope + cursor recovery.
/// </summary>
public class WatchDemandBrowseTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WatchDemandBrowseTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Default_browse_query_requests_visible_dates_desc_without_cursor()
    {
        var url = WatchDemandBrowseQuery.Default.ToRelativeUrl();

        Assert.StartsWith("/api/demands?", url, StringComparison.Ordinal);
        Assert.Contains("status=VISIBLE", url, StringComparison.Ordinal);
        Assert.Contains("sortBy=dates", url, StringComparison.Ordinal);
        Assert.Contains("direction=desc", url, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Browse_query_includes_filters_cursor_and_gone_window()
    {
        var from = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.FromHours(8));
        var url = new WatchDemandBrowseQuery(
                Status: "GONE",
                TaskType: "DIE_TO_OVEN",
                Sublot: "Q-1",
                DemandId: "abcdef",
                GoneAtFrom: from,
                SortBy: "goneAt",
                Direction: "asc",
                Limit: 50,
                Cursor: "opaque-cursor")
            .ToRelativeUrl();

        Assert.Contains("status=GONE", url, StringComparison.Ordinal);
        Assert.Contains("taskType=DIE_TO_OVEN", url, StringComparison.Ordinal);
        Assert.Contains("sublot=Q-1", url, StringComparison.Ordinal);
        Assert.Contains("demandId=abcdef", url, StringComparison.Ordinal);
        Assert.Contains("goneAtFrom=", url, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(from.ToString("O")), url, StringComparison.Ordinal);
        Assert.Contains("sortBy=goneAt", url, StringComparison.Ordinal);
        Assert.Contains("direction=asc", url, StringComparison.Ordinal);
        Assert.Contains("limit=50", url, StringComparison.Ordinal);
        Assert.Contains("cursor=opaque-cursor", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_parses_demand_page_envelope_and_sends_browse_query()
    {
        string? demandedPathAndQuery = null;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                demandedPathAndQuery = request.RequestUri.PathAndQuery;
                return Task.FromResult(JsonResponse(
                    path,
                    """
                    {
                      "items": [
                        {
                          "demandId": "abcdef0123456789abcdef0123456789",
                          "taskType": "DIE_TO_OVEN",
                          "sublot": "Q1",
                          "area": null,
                          "eqp": null,
                          "step": null,
                          "dates": "2026-07-30T10:00:00+08:00",
                          "package": null,
                          "status": "VISIBLE",
                          "mesLastSeenAt": "2026-07-30T11:00:00+08:00",
                          "disappearCount": 0,
                          "locationRisk": false,
                          "locationRiskCode": null,
                          "createdAt": "2026-07-30T09:00:00+08:00",
                          "goneAt": null,
                          "alerts": []
                        }
                      ],
                      "nextCursor": "next-page",
                      "hasMore": true
                    }
                    """));
            }

            if (path.EndsWith("/api/poll-health", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(JsonResponse(path, "[]"));
        });

        using var http = CreateHttp(handler);
        var client = new MesIngestApiClient(http);
        var query = new WatchDemandBrowseQuery(Status: "VISIBLE", SortBy: "dates", Direction: "desc", Limit: 100);

        var snapshot = await client.FetchSnapshotAsync(query);

        Assert.Null(snapshot.FetchError);
        Assert.Single(snapshot.Demands);
        Assert.Equal("abcdef0123456789abcdef0123456789", snapshot.Demands[0].DemandId);
        Assert.Equal("next-page", snapshot.DemandsNextCursor);
        Assert.True(snapshot.DemandsHasMore);
        Assert.Contains("status=VISIBLE", demandedPathAndQuery, StringComparison.Ordinal);
        Assert.Contains("sortBy=dates", demandedPathAndQuery, StringComparison.Ordinal);
        Assert.Contains("direction=desc", demandedPathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_rejects_legacy_bare_array_demands_body()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(path, """[{"demandId":"a"}]"""));
            }

            if (path.EndsWith("/api/poll-health", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(JsonResponse(path, "[]"));
        });

        using var http = CreateHttp(handler);
        var client = new MesIngestApiClient(http);

        var snapshot = await client.FetchSnapshotAsync(WatchDemandBrowseQuery.Default);

        Assert.NotNull(snapshot.FetchError);
        Assert.True(snapshot.HasFetchError);
        Assert.Empty(snapshot.Demands);
    }

    [Fact]
    public async Task Client_reloads_first_page_when_cursor_is_rejected()
    {
        var demandCalls = 0;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                demandCalls++;
                var hasCursor = request.RequestUri.Query.Contains("cursor=", StringComparison.Ordinal);
                if (hasCursor)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":"cursor does not match sortBy/direction"}""", Encoding.UTF8, "application/json"),
                    });
                }

                return Task.FromResult(JsonResponse(
                    path,
                    """{"items":[{"demandId":"abcdef0123456789abcdef0123456789","taskType":"T","sublot":"S","area":null,"eqp":null,"step":null,"dates":"2026-07-30T10:00:00+08:00","package":null,"status":"VISIBLE","mesLastSeenAt":"2026-07-30T11:00:00+08:00","disappearCount":0,"locationRisk":false,"locationRiskCode":null,"createdAt":"2026-07-30T09:00:00+08:00","goneAt":null,"alerts":[]}],"nextCursor":null,"hasMore":false}"""));
            }

            if (path.EndsWith("/api/poll-health", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(JsonResponse(path, "[]"));
        });

        using var http = CreateHttp(handler);
        var client = new MesIngestApiClient(http);
        var query = WatchDemandBrowseQuery.Default with { Cursor = "stale-cursor" };

        var snapshot = await client.FetchSnapshotAsync(query);

        Assert.Null(snapshot.FetchError);
        Assert.Single(snapshot.Demands);
        Assert.False(snapshot.DemandsHasMore);
        Assert.Null(snapshot.DemandsNextCursor);
        Assert.Equal(2, demandCalls);
    }

    [Fact]
    public async Task Client_fetches_next_page_with_cursor_only()
    {
        string? demandedQuery = null;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                demandedQuery = request.RequestUri.Query;
                return Task.FromResult(JsonResponse(
                    path,
                    """{"items":[{"demandId":"bbbbbb0123456789abcdef0123456789","taskType":"T","sublot":"S","area":null,"eqp":null,"step":null,"dates":"2026-07-29T10:00:00+08:00","package":null,"status":"VISIBLE","mesLastSeenAt":"2026-07-29T11:00:00+08:00","disappearCount":0,"locationRisk":false,"locationRiskCode":null,"createdAt":"2026-07-29T09:00:00+08:00","goneAt":null,"alerts":[]}],"nextCursor":null,"hasMore":false}"""));
            }

            throw new InvalidOperationException("unexpected endpoint");
        });

        using var http = CreateHttp(handler);
        var client = new MesIngestApiClient(http);
        var query = new WatchDemandBrowseQuery(
            Status: "VISIBLE",
            SortBy: "dates",
            Direction: "desc",
            Limit: 100,
            Cursor: "page-2");

        var page = await client.FetchDemandPageAsync(query);

        Assert.Single(page.Items);
        Assert.Equal("bbbbbb0123456789abcdef0123456789", page.Items[0].DemandId);
        Assert.False(page.HasMore);
        Assert.Contains("cursor=page-2", demandedQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void DemandId_input_is_ready_only_when_empty_or_valid_hex_prefix()
    {
        Assert.True(WatchDemandBrowseQuery.IsDemandIdFilterReady(null));
        Assert.True(WatchDemandBrowseQuery.IsDemandIdFilterReady(""));
        Assert.True(WatchDemandBrowseQuery.IsDemandIdFilterReady("   "));
        Assert.False(WatchDemandBrowseQuery.IsDemandIdFilterReady("abc"));
        Assert.False(WatchDemandBrowseQuery.IsDemandIdFilterReady("abcde"));
        Assert.True(WatchDemandBrowseQuery.IsDemandIdFilterReady("abcdef"));
        Assert.True(WatchDemandBrowseQuery.IsDemandIdFilterReady("ABCDEF0123456789ABCDEF0123456789"));
        Assert.False(WatchDemandBrowseQuery.IsDemandIdFilterReady("abcdez"));
    }

    [Fact]
    public async Task Watch_client_pages_refilters_and_keeps_sort_against_live_host()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("id-01", DemandStatus.Visible, now.AddMinutes(1), "DIE_TO_OVEN", "Q-1"),
            Demand("id-02", DemandStatus.Visible, now.AddMinutes(2), "DIE_TO_OVEN", "Q-2"),
            Demand("id-03", DemandStatus.Visible, now.AddMinutes(3), "DIE_TO_WIRE", "Q-3"),
            Demand("id-04", DemandStatus.Visible, now.AddMinutes(4), "DIE_TO_OVEN", "Q-4"),
        ]));

        await using var factory = await CreateFactoryAsync(store);
        using var http = factory.CreateClient();
        var watch = new MesIngestApiClient(http);

        var first = await watch.FetchSnapshotAsync(new WatchDemandBrowseQuery(
            Status: "VISIBLE",
            SortBy: "dates",
            Direction: "desc",
            Limit: 2));

        Assert.Null(first.FetchError);
        Assert.Equal(2, first.Demands.Count);
        Assert.True(first.DemandsHasMore);
        Assert.False(string.IsNullOrWhiteSpace(first.DemandsNextCursor));
        Assert.Equal(new[] { "id-04", "id-03" }, first.Demands.Select(d => d.DemandId).ToArray());

        var second = await watch.FetchDemandPageAsync(new WatchDemandBrowseQuery(
            Status: "VISIBLE",
            SortBy: "dates",
            Direction: "desc",
            Limit: 2,
            Cursor: first.DemandsNextCursor));

        Assert.Equal(2, second.Items.Count);
        Assert.False(second.HasMore);
        Assert.Equal(new[] { "id-02", "id-01" }, second.Items.Select(d => d.DemandId).ToArray());

        var filtered = await watch.FetchSnapshotAsync(new WatchDemandBrowseQuery(
            Status: "VISIBLE",
            TaskType: "DIE_TO_OVEN",
            SortBy: "dates",
            Direction: "desc",
            Limit: 10));

        Assert.Null(filtered.FetchError);
        Assert.Equal(3, filtered.Demands.Count);
        Assert.All(filtered.Demands, d => Assert.Equal("DIE_TO_OVEN", d.TaskType));
        Assert.Equal(new[] { "id-04", "id-02", "id-01" }, filtered.Demands.Select(d => d.DemandId).ToArray());
    }

    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(ITransportDemandStore store)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mes-ingest-watch-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n", Encoding.UTF8);

        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new MesIngestHostOptions
                {
                    SnapshotCsvPath = path,
                    GoLiveBaseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8)),
                    RunOneShotOnStartup = false,
                });
                services.AddSingleton(store);
            });
        });
    }

    private static TransportDemand Demand(
        string id,
        DemandStatus status,
        DateTimeOffset dates,
        string taskType,
        string sublot) =>
        new()
        {
            DemandId = id,
            TaskType = taskType,
            Sublot = sublot,
            Dates = dates,
            Status = status,
            MesLastSeenAt = dates,
            CreatedAt = dates,
        };

    private static HttpClient CreateHttp(HttpMessageHandler handler) =>
        new(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5088/"),
            Timeout = TimeSpan.FromSeconds(30),
        };

    private static HttpResponseMessage JsonResponse(string path, string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5088" + path),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
