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
/// Remediation ticket 07 seam: WatchBrowseSession preserves Load-more window on
/// auto-refresh, resets on filter change, and pushes alert sort to the Host.
/// </summary>
public class WatchBrowseSessionTests
{
    [Fact]
    public async Task Preserve_window_refresh_keeps_loaded_demand_rows_after_append()
    {
        var demandCalls = new List<string>();
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (WatchHttpTestStubs.IsContractPath(path))
            {
                return Task.FromResult(WatchHttpTestStubs.MatchingContract(path));
            }

            if (path.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                demandCalls.Add(request.RequestUri.Query);
                var cursor = QueryValue(request.RequestUri, "cursor");
                if (string.IsNullOrEmpty(cursor))
                {
                    return Task.FromResult(JsonResponse(path, """
                        {
                          "items": [
                            { "demandId": "id-01", "taskType": "T", "sublot": "S", "area": null, "eqp": null, "step": null,
                              "dates": "2026-07-30T10:00:00+08:00", "package": null, "status": "VISIBLE",
                              "mesLastSeenAt": "2026-07-30T11:00:00+08:00", "disappearCount": 0, "locationRisk": false,
                              "locationRiskCode": null, "createdAt": "2026-07-30T09:00:00+08:00", "goneAt": null },
                            { "demandId": "id-02", "taskType": "T", "sublot": "S", "area": null, "eqp": null, "step": null,
                              "dates": "2026-07-30T09:00:00+08:00", "package": null, "status": "VISIBLE",
                              "mesLastSeenAt": "2026-07-30T11:00:00+08:00", "disappearCount": 0, "locationRisk": false,
                              "locationRiskCode": null, "createdAt": "2026-07-30T09:00:00+08:00", "goneAt": null }
                          ],
                          "nextCursor": "cursor-page-1",
                          "hasMore": true
                        }
                        """));
                }

                return Task.FromResult(JsonResponse(path, """
                    {
                      "items": [
                        { "demandId": "id-03", "taskType": "T", "sublot": "S", "area": null, "eqp": null, "step": null,
                          "dates": "2026-07-30T08:00:00+08:00", "package": null, "status": "VISIBLE",
                          "mesLastSeenAt": "2026-07-30T11:00:00+08:00", "disappearCount": 0, "locationRisk": false,
                          "locationRiskCode": null, "createdAt": "2026-07-30T09:00:00+08:00", "goneAt": null },
                        { "demandId": "id-04", "taskType": "T", "sublot": "S", "area": null, "eqp": null, "step": null,
                          "dates": "2026-07-30T07:00:00+08:00", "package": null, "status": "VISIBLE",
                          "mesLastSeenAt": "2026-07-30T11:00:00+08:00", "disappearCount": 0, "locationRisk": false,
                          "locationRiskCode": null, "createdAt": "2026-07-30T09:00:00+08:00", "goneAt": null }
                      ],
                      "nextCursor": null,
                      "hasMore": false
                    }
                    """));
            }

            if (path.EndsWith("/api/alerts", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(path, """{"items":[],"nextCursor":null,"hasMore":false}"""));
            }

            if (path.EndsWith("/api/poll-health", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var client = new MesIngestApiClient(CreateHttp(handler));
        var session = new WatchBrowseSession(client, pageSize: 2);
        var filter = WatchDemandBrowseQuery.Default with { Limit = 2 };

        await session.RefreshAsync(WatchBrowseRefreshKind.Reset, filter);
        Assert.Equal(2, session.Demands.Count);
        Assert.True(session.DemandsHasMore);

        await session.RefreshAsync(WatchBrowseRefreshKind.Append, filter);
        Assert.Equal(4, session.Demands.Count);
        Assert.Equal(new[] { "id-01", "id-02", "id-03", "id-04" }, session.Demands.Select(d => d.DemandId).ToArray());

        demandCalls.Clear();
        await session.RefreshAsync(WatchBrowseRefreshKind.PreserveWindow, filter);

        Assert.True(session.Demands.Count >= 4, "auto-refresh must keep the Load-more window");
        Assert.Equal(new[] { "id-01", "id-02", "id-03", "id-04" }, session.Demands.Select(d => d.DemandId).ToArray());
        Assert.Contains(demandCalls, q => q.Contains("cursor=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Filter_reset_clears_accumulated_pages_to_first_page()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (WatchHttpTestStubs.IsContractPath(path))
            {
                return Task.FromResult(WatchHttpTestStubs.MatchingContract(path));
            }

            if (path.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                var cursor = QueryValue(request.RequestUri, "cursor");
                if (string.IsNullOrEmpty(cursor))
                {
                    return Task.FromResult(JsonResponse(path, """
                        {
                          "items": [
                            { "demandId": "id-01", "taskType": "T", "sublot": "S", "area": null, "eqp": null, "step": null,
                              "dates": "2026-07-30T10:00:00+08:00", "package": null, "status": "VISIBLE",
                              "mesLastSeenAt": "2026-07-30T11:00:00+08:00", "disappearCount": 0, "locationRisk": false,
                              "locationRiskCode": null, "createdAt": "2026-07-30T09:00:00+08:00", "goneAt": null },
                            { "demandId": "id-02", "taskType": "T", "sublot": "S", "area": null, "eqp": null, "step": null,
                              "dates": "2026-07-30T09:00:00+08:00", "package": null, "status": "VISIBLE",
                              "mesLastSeenAt": "2026-07-30T11:00:00+08:00", "disappearCount": 0, "locationRisk": false,
                              "locationRiskCode": null, "createdAt": "2026-07-30T09:00:00+08:00", "goneAt": null }
                          ],
                          "nextCursor": "cursor-page-1",
                          "hasMore": true
                        }
                        """));
                }

                return Task.FromResult(JsonResponse(path, """
                    {
                      "items": [
                        { "demandId": "id-03", "taskType": "T", "sublot": "S", "area": null, "eqp": null, "step": null,
                          "dates": "2026-07-30T08:00:00+08:00", "package": null, "status": "VISIBLE",
                          "mesLastSeenAt": "2026-07-30T11:00:00+08:00", "disappearCount": 0, "locationRisk": false,
                          "locationRiskCode": null, "createdAt": "2026-07-30T09:00:00+08:00", "goneAt": null }
                      ],
                      "nextCursor": null,
                      "hasMore": false
                    }
                    """));
            }

            if (path.EndsWith("/api/alerts", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(path, """{"items":[],"nextCursor":null,"hasMore":false}"""));
            }

            if (path.EndsWith("/api/poll-health", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var client = new MesIngestApiClient(CreateHttp(handler));
        var session = new WatchBrowseSession(client, pageSize: 2);
        var filter = WatchDemandBrowseQuery.Default with { Limit = 2 };

        await session.RefreshAsync(WatchBrowseRefreshKind.Reset, filter);
        await session.RefreshAsync(WatchBrowseRefreshKind.Append, filter);
        Assert.Equal(3, session.Demands.Count);

        await session.RefreshAsync(WatchBrowseRefreshKind.Reset, filter);
        Assert.Equal(2, session.Demands.Count);
        Assert.Equal(new[] { "id-01", "id-02" }, session.Demands.Select(d => d.DemandId).ToArray());
        Assert.True(session.DemandsHasMore);
    }

    [Fact]
    public async Task Alert_sort_change_requests_host_with_sortBy_not_local_order()
    {
        string? alertQuery = null;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (WatchHttpTestStubs.IsContractPath(path))
            {
                return Task.FromResult(WatchHttpTestStubs.MatchingContract(path));
            }

            if (path.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(path, """{"items":[],"nextCursor":null,"hasMore":false}"""));
            }

            if (path.EndsWith("/api/alerts", StringComparison.Ordinal))
            {
                alertQuery = request.RequestUri.Query;
                return Task.FromResult(JsonResponse(path, """
                    {
                      "items": [
                        {
                          "alertId": "a-1",
                          "code": "ZERO_DROP",
                          "severity": "ERROR",
                          "taskType": "T",
                          "sublot": null,
                          "demandId": null,
                          "message": "m",
                          "details": null,
                          "firstSeenAt": "2026-07-30T09:00:00+08:00",
                          "lastSeenAt": "2026-07-30T11:00:00+08:00",
                          "occurrenceCount": 1,
                          "isActive": true,
                          "resolvedAt": null,
                          "createdAt": "2026-07-30T09:00:00+08:00"
                        }
                      ],
                      "nextCursor": null,
                      "hasMore": false
                    }
                    """));
            }

            if (path.EndsWith("/api/poll-health", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var client = new MesIngestApiClient(CreateHttp(handler));
        var session = new WatchBrowseSession(client);

        Assert.True(session.TryApplyAlertSort("Code"));
        await session.RefreshAsync(WatchBrowseRefreshKind.Reset, WatchDemandBrowseQuery.Default);

        Assert.NotNull(alertQuery);
        Assert.Contains("sortBy=code", alertQuery, StringComparison.Ordinal);
        Assert.Contains("direction=asc", alertQuery, StringComparison.Ordinal);
        Assert.Single(session.Alerts);
        Assert.Equal("a-1", session.Alerts[0].AlertId);
    }

    [Theory]
    [InlineData("Code", "code")]
    [InlineData("Severity", "severity")]
    [InlineData("AlertId", "alertId")]
    [InlineData("first seen", "firstSeenAt")]
    [InlineData("last seen", "lastSeenAt")]
    [InlineData("TASK_TYPE", "taskType")]
    [InlineData("SUBLOT", "sublot")]
    [InlineData("DemandId", "demandId")]
    [InlineData("Message", "message")]
    public void Every_visible_alert_column_toggles_server_sort(string header, string token)
    {
        var session = new WatchBrowseSession(new MesIngestApiClient(CreateHttp(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))))));

        Assert.True(session.TryApplyAlertSort(header));
        Assert.Equal(token, session.AlertQuery.SortBy);
        Assert.Equal("asc", session.AlertQuery.Direction);

        Assert.True(session.TryApplyAlertSort(header));
        Assert.Equal(token, session.AlertQuery.SortBy);
        Assert.Equal("desc", session.AlertQuery.Direction);
    }

    [Fact]
    public void Demand_and_alert_sort_tokens_match_host_allow_lists()
    {
        var demandTokens = WatchDemandBrowseQuery.SortableHeaderTokens
            .Select(WatchDemandBrowseQuery.SortToken)
            .Where(t => t is not null)
            .Cast<string>()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        var hostDemand = Enum.GetValues<MesIngest.Core.DemandSortColumn>()
            .Select(MesIngest.Core.DemandListCursor.ToSortToken)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(hostDemand, demandTokens);

        var alertTokens = WatchAlertBrowseQuery.AllowListTokens
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        foreach (var token in alertTokens)
        {
            Assert.True(
                MesIngest.Core.AlertListQueryParser.TryParseSortBy(token, out _, out var error),
                error);
        }

        Assert.Equal(Enum.GetValues<MesIngest.Core.AlertSortColumn>().Length, alertTokens.Length);
        Assert.Equal("taskType", WatchAlertBrowseQuery.SortToken("TASK_TYPE"));
        Assert.Equal("package", WatchDemandBrowseQuery.SortToken("PACKAGE"));
        Assert.Equal("status", WatchDemandBrowseQuery.SortToken("status"));
        Assert.Equal("dates", WatchDemandBrowseQuery.SortToken("DATES"));
    }

    private static string? QueryValue(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 0)
            {
                continue;
            }

            if (!string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.Ordinal))
            {
                continue;
            }

            return pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
        }

        return null;
    }

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

/// <summary>
/// Live Host contract for WatchBrowseSession: tied primary sort + continuous Load more.
/// </summary>
public class WatchBrowseSessionLiveHostTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WatchBrowseSessionLiveHostTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Continuous_load_more_then_preserve_covers_tied_dates_without_gaps()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            TiedDemand("id-00", now),
            TiedDemand("id-01", now),
            TiedDemand("id-02", now),
            TiedDemand("id-03", now),
            TiedDemand("id-04", now),
        ]));

        await using var factory = await CreateFactoryAsync(store);
        using var http = factory.CreateClient();
        var session = new WatchBrowseSession(new MesIngestApiClient(http), pageSize: 2);
        var filter = new WatchDemandBrowseQuery(
            Status: "VISIBLE",
            SortBy: "dates",
            Direction: "desc",
            Limit: 2);

        await session.RefreshAsync(WatchBrowseRefreshKind.Reset, filter);
        Assert.Equal(2, session.Demands.Count);
        Assert.True(session.DemandsHasMore);

        await session.RefreshAsync(WatchBrowseRefreshKind.Append, filter);
        Assert.Equal(4, session.Demands.Count);
        Assert.True(session.DemandsHasMore);

        await session.RefreshAsync(WatchBrowseRefreshKind.Append, filter);
        Assert.Equal(5, session.Demands.Count);
        Assert.False(session.DemandsHasMore);

        var ids = session.Demands.Select(d => d.DemandId).ToArray();
        Assert.Equal(new[] { "id-00", "id-01", "id-02", "id-03", "id-04" }, ids);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

        await session.RefreshAsync(WatchBrowseRefreshKind.PreserveWindow, filter);
        Assert.Equal(5, session.Demands.Count);
        Assert.Equal(ids, session.Demands.Select(d => d.DemandId).ToArray());
    }

    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(ITransportDemandStore store)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mes-ingest-browse-{Guid.NewGuid():N}.csv");
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

    private static TransportDemand TiedDemand(string id, DateTimeOffset dates) =>
        new()
        {
            DemandId = id,
            TaskType = "DIE_TO_OVEN",
            Sublot = "Q1",
            Dates = dates,
            Status = DemandStatus.Visible,
            MesLastSeenAt = dates,
            CreatedAt = dates,
        };
}
