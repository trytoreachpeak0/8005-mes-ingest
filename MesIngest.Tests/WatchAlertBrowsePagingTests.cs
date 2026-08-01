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
/// Remediation ticket 13 seam: WatchBrowseSession consumes the Alerts API cursor
/// envelope and exposes a continuous operator-visible alert window.
/// </summary>
public class WatchAlertBrowsePagingTests
{
    [Fact]
    public async Task Load_more_alerts_uses_next_cursor_and_reaches_the_next_page()
    {
        var alertQueries = new List<Uri>();
        var handler = WatchApiHandler(uri =>
        {
            var path = uri.AbsolutePath;
            alertQueries.Add(uri);
            var cursor = QueryValue(uri, "cursor");
            return string.IsNullOrEmpty(cursor)
                ? AlertPage(path, ["a-01", "a-02"], "alert-cursor-1", hasMore: true)
                : AlertPage(path, ["a-03", "a-04"], nextCursor: null, hasMore: false);
        });

        var session = new WatchBrowseSession(
            new MesIngestApiClient(CreateHttp(handler)),
            pageSize: 2);

        await session.RefreshAsync(WatchBrowseRefreshKind.Reset, WatchDemandBrowseQuery.Default);

        Assert.Equal(new[] { "a-01", "a-02" }, session.Alerts.Select(a => a.AlertId).ToArray());
        Assert.True(session.AlertsHasMore);
        Assert.Equal("alert-cursor-1", session.AlertsNextCursor);

        await session.RefreshAsync(WatchBrowseRefreshKind.AppendAlerts, WatchDemandBrowseQuery.Default);

        Assert.Equal(
            new[] { "a-01", "a-02", "a-03", "a-04" },
            session.Alerts.Select(a => a.AlertId).ToArray());
        Assert.False(session.AlertsHasMore);
        Assert.Null(session.AlertsNextCursor);
        Assert.Equal("alert-cursor-1", QueryValue(alertQueries[^1], "cursor"));
    }

    [Fact]
    public async Task Automatic_refresh_preserves_the_loaded_alert_window()
    {
        var alertQueries = new List<Uri>();
        var handler = WatchApiHandler(uri =>
        {
            var path = uri.AbsolutePath;
            alertQueries.Add(uri);
            var cursor = QueryValue(uri, "cursor");
            return string.IsNullOrEmpty(cursor)
                ? AlertPage(path, ["a-01", "a-02"], "alert-cursor-1", hasMore: true)
                : AlertPage(path, ["a-03", "a-04"], nextCursor: null, hasMore: false);
        });

        var session = new WatchBrowseSession(
            new MesIngestApiClient(CreateHttp(handler)),
            pageSize: 2);

        await session.RefreshAsync(WatchBrowseRefreshKind.Reset, WatchDemandBrowseQuery.Default);
        await session.RefreshAsync(WatchBrowseRefreshKind.AppendAlerts, WatchDemandBrowseQuery.Default);
        Assert.Equal(4, session.Alerts.Count);

        alertQueries.Clear();
        await session.RefreshAsync(WatchBrowseRefreshKind.PreserveWindow, WatchDemandBrowseQuery.Default);

        Assert.Equal(
            new[] { "a-01", "a-02", "a-03", "a-04" },
            session.Alerts.Select(a => a.AlertId).ToArray());
        Assert.Contains(alertQueries, uri => QueryValue(uri, "cursor") == "alert-cursor-1");
    }

    [Fact]
    public async Task Invalid_alert_cursor_reloads_first_page_without_mixing_windows()
    {
        var firstPageCalls = 0;
        var handler = WatchApiHandler(uri =>
        {
            var path = uri.AbsolutePath;
            var cursor = QueryValue(uri, "cursor");
            if (cursor == "stale-cursor")
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("invalid cursor", Encoding.UTF8, "text/plain"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri),
                };
            }

            firstPageCalls++;
            return firstPageCalls == 1
                ? AlertPage(path, ["old-01", "old-02"], "stale-cursor", hasMore: true)
                : AlertPage(path, ["new-01", "new-02"], "fresh-cursor", hasMore: true);
        });

        var session = new WatchBrowseSession(
            new MesIngestApiClient(CreateHttp(handler)),
            pageSize: 2);

        await session.RefreshAsync(WatchBrowseRefreshKind.Reset, WatchDemandBrowseQuery.Default);
        await session.RefreshAsync(WatchBrowseRefreshKind.AppendAlerts, WatchDemandBrowseQuery.Default);

        Assert.Equal(new[] { "new-01", "new-02" }, session.Alerts.Select(a => a.AlertId).ToArray());
        Assert.Equal("fresh-cursor", session.AlertsNextCursor);
        Assert.True(session.AlertsHasMore);
    }

    [Fact]
    public async Task Failed_alert_page_keeps_the_last_successful_window_and_cursor()
    {
        var handler = WatchApiHandler(uri =>
        {
            var path = uri.AbsolutePath;
            return QueryValue(uri, "cursor") is null
                ? AlertPage(path, ["a-01", "a-02"], "alert-cursor-1", hasMore: true)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("temporarily unavailable", Encoding.UTF8, "text/plain"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri),
                };
        });

        var session = new WatchBrowseSession(
            new MesIngestApiClient(CreateHttp(handler)),
            pageSize: 2);
        await session.RefreshAsync(WatchBrowseRefreshKind.Reset, WatchDemandBrowseQuery.Default);

        var error = await Assert.ThrowsAsync<WatchEndpointFetchException>(() =>
            session.RefreshAsync(WatchBrowseRefreshKind.AppendAlerts, WatchDemandBrowseQuery.Default));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ((HttpRequestException)error.InnerException!).StatusCode);
        Assert.Equal(new[] { "a-01", "a-02" }, session.Alerts.Select(a => a.AlertId).ToArray());
        Assert.Equal("alert-cursor-1", session.AlertsNextCursor);
        Assert.True(session.AlertsHasMore);
    }

    [Fact]
    public async Task Demand_failure_during_automatic_refresh_does_not_shrink_the_alert_window()
    {
        var failDemands = false;
        var handler = WatchApiHandler(
            alerts: uri =>
            {
                var path = uri.AbsolutePath;
                return QueryValue(uri, "cursor") is null
                    ? AlertPage(path, ["a-01", "a-02"], "alert-cursor-1", hasMore: true)
                    : AlertPage(path, ["a-03", "a-04"], nextCursor: null, hasMore: false);
            },
            demands: uri => failDemands
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("demand unavailable", Encoding.UTF8, "text/plain"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri),
                }
                : EmptyDemandPage(uri));

        var session = new WatchBrowseSession(
            new MesIngestApiClient(CreateHttp(handler)),
            pageSize: 2);
        await session.RefreshAsync(WatchBrowseRefreshKind.Reset, WatchDemandBrowseQuery.Default);
        await session.RefreshAsync(WatchBrowseRefreshKind.AppendAlerts, WatchDemandBrowseQuery.Default);
        Assert.Equal(4, session.Alerts.Count);

        failDemands = true;
        await session.RefreshAsync(WatchBrowseRefreshKind.PreserveWindow, WatchDemandBrowseQuery.Default);

        Assert.Equal(
            new[] { "a-01", "a-02", "a-03", "a-04" },
            session.Alerts.Select(a => a.AlertId).ToArray());
        Assert.NotNull(session.LastSnapshot?.FetchError);
        Assert.False(session.LastSnapshot?.DemandsSucceeded);
        Assert.True(session.LastSnapshot?.AlertsSucceeded);
    }

    [Fact]
    public async Task Invalid_alert_cursor_during_automatic_refresh_reloads_a_clean_first_page()
    {
        var firstPageCalls = 0;
        var handler = WatchApiHandler(uri =>
        {
            var path = uri.AbsolutePath;
            var cursor = QueryValue(uri, "cursor");
            if (cursor == "old-cursor")
            {
                return AlertPage(path, ["old-03", "old-04"], null, hasMore: false);
            }

            if (cursor == "stale-refresh-cursor")
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("invalid cursor", Encoding.UTF8, "text/plain"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri),
                };
            }

            firstPageCalls++;
            return firstPageCalls switch
            {
                1 => AlertPage(path, ["old-01", "old-02"], "old-cursor", hasMore: true),
                2 => AlertPage(path, ["new-01", "new-02"], "stale-refresh-cursor", hasMore: true),
                _ => AlertPage(path, ["recovered-01", "recovered-02"], null, hasMore: false),
            };
        });

        var session = new WatchBrowseSession(
            new MesIngestApiClient(CreateHttp(handler)),
            pageSize: 2);
        await session.RefreshAsync(WatchBrowseRefreshKind.Reset, WatchDemandBrowseQuery.Default);
        await session.RefreshAsync(WatchBrowseRefreshKind.AppendAlerts, WatchDemandBrowseQuery.Default);
        Assert.Equal(4, session.Alerts.Count);

        await session.RefreshAsync(WatchBrowseRefreshKind.PreserveWindow, WatchDemandBrowseQuery.Default);

        Assert.Equal(
            new[] { "recovered-01", "recovered-02" },
            session.Alerts.Select(alert => alert.AlertId).ToArray());
        Assert.False(session.AlertsHasMore);
        Assert.Null(session.AlertsNextCursor);
        Assert.Equal(3, firstPageCalls);
    }

    private static HttpResponseMessage AlertPage(
        string path,
        IReadOnlyList<string> alertIds,
        string? nextCursor,
        bool hasMore)
    {
        var items = string.Join(",", alertIds.Select(AlertJson));
        var cursorJson = nextCursor is null ? "null" : $"\"{nextCursor}\"";
        return JsonResponse(
            path,
            $"{{\"items\":[{items}],\"nextCursor\":{cursorJson},\"hasMore\":{hasMore.ToString().ToLowerInvariant()}}}");
    }

    private static StubHandler WatchApiHandler(
        Func<Uri, HttpResponseMessage> alerts,
        Func<Uri, HttpResponseMessage>? demands = null) =>
        new((request, _) =>
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            var response = WatchHttpTestStubs.IsContractPath(path)
                ? WatchHttpTestStubs.MatchingContract(path)
                : path.EndsWith("/api/demands", StringComparison.Ordinal)
                    ? demands?.Invoke(uri) ?? EmptyDemandPage(uri)
                    : path.EndsWith("/api/alerts", StringComparison.Ordinal)
                        ? alerts(uri)
                        : new HttpResponseMessage(HttpStatusCode.NotFound)
                        {
                            RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri),
                        };
            return Task.FromResult(response);
        });

    private static HttpResponseMessage EmptyDemandPage(Uri uri) =>
        JsonResponse(uri.AbsolutePath, """{"items":[],"nextCursor":null,"hasMore":false}""");

    private static string AlertJson(string id) => $$"""
        {
          "alertId":"{{id}}","code":"POLL_FAILURE","severity":"ERROR","taskType":null,
          "sublot":null,"demandId":null,"message":"m","details":null,
          "firstSeenAt":"2026-08-01T09:00:00+08:00","lastSeenAt":"2026-08-01T10:00:00+08:00",
          "occurrenceCount":1,"isActive":true,"resolvedAt":null,"createdAt":"2026-08-01T09:00:00+08:00"
        }
        """;

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.Ordinal))
            {
                return pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
            }
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

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}

/// <summary>Real Host HTTP contract for ticket 13 paging and ticket 14 server sorting.</summary>
public class WatchAlertBrowsePagingLiveHostTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WatchAlertBrowsePagingLiveHostTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Watch_reaches_all_alerts_in_server_sorted_order_and_preserves_sort_on_refresh()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var alerts = Enumerable.Range(0, 205)
            .Select(index => new IngestAlert(
                Code: AlertCodes.FieldDrift,
                TaskType: "DIE_TO_OVEN",
                Sublot: $"Q{index:D3}",
                DemandId: $"demand-{index:D3}",
                Message: $"alert {index}",
                CreatedAt: now,
                AlertId: $"alert-{index:D3}",
                FirstSeenAt: now,
                LastSeenAt: now))
            .ToList();
        var store = new InMemoryTransportDemandStore(clock: () => now);
        store.ReplaceState(ProjectionState.Empty, alerts);
        var path = Path.Combine(Path.GetTempPath(), $"mes-ingest-alert-page-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n", Encoding.UTF8);

        try
        {
            await using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton(new MesIngestHostOptions
                    {
                        SnapshotCsvPath = path,
                        GoLiveBaseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8)),
                        RunOneShotOnStartup = false,
                    });
                    services.AddSingleton<ITransportDemandStore>(store);
                });
            });
            using var http = factory.CreateClient();
            var session = new WatchBrowseSession(new MesIngestApiClient(http), pageSize: 100);

            Assert.True(session.TryApplyAlertSort("SUBLOT"));
            await session.RefreshAsync(WatchBrowseRefreshKind.Reset, WatchDemandBrowseQuery.Default);
            Assert.Equal(100, session.Alerts.Count);
            Assert.True(session.AlertsHasMore);

            await session.RefreshAsync(WatchBrowseRefreshKind.AppendAlerts, WatchDemandBrowseQuery.Default);
            await session.RefreshAsync(WatchBrowseRefreshKind.AppendAlerts, WatchDemandBrowseQuery.Default);

            var ids = session.Alerts.Select(alert => alert.AlertId).ToArray();
            Assert.Equal(205, ids.Length);
            Assert.Equal(205, ids.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(
                Enumerable.Range(0, 205).Select(index => $"Q{index:D3}"),
                session.Alerts.Select(alert => alert.Sublot));
            Assert.False(session.AlertsHasMore);
            Assert.Null(session.AlertsNextCursor);

            await session.RefreshAsync(WatchBrowseRefreshKind.PreserveWindow, WatchDemandBrowseQuery.Default);

            Assert.Equal("sublot", session.AlertQuery.SortBy);
            Assert.Equal("asc", session.AlertQuery.Direction);
            Assert.Equal(
                Enumerable.Range(0, 205).Select(index => $"Q{index:D3}"),
                session.Alerts.Select(alert => alert.Sublot));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
