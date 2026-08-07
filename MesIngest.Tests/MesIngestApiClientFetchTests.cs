using System.Net;
using System.Text;
using MesIngest.Watch;

namespace MesIngest.Tests;

public class MesIngestApiClientFetchTests
{
    [Fact]
    public async Task Overview_snapshot_starts_all_three_resource_requests_concurrently()
    {
        var sync = new object();
        var started = new HashSet<string>(StringComparer.Ordinal);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (WatchHttpTestStubs.IsContractPath(path))
            {
                return WatchHttpTestStubs.MatchingContract(path);
            }

            lock (sync)
            {
                started.Add(path);
                if (started.Count == 3)
                {
                    allStarted.TrySetResult();
                }
            }

            await release.Task.WaitAsync(cancellationToken);
            return EmptyPageOrList(path);
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5088/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30);

        var fetch = client.FetchSnapshotAsync();
        try
        {
            var completed = await Task.WhenAny(allStarted.Task, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(allStarted.Task, completed);
        }
        finally
        {
            release.TrySetResult();
        }

        var snapshot = await fetch;
        Assert.True(snapshot.AllEndpointsSucceeded);
        Assert.Equal(
            new[] { "/api/alerts", "/api/demands", "/api/poll-health" },
            started.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Timeout_on_demands_reports_endpoint_stage_elapsed_and_timeout()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                throw new TaskCanceledException("canceled", new TimeoutException());
            }

            return Task.FromResult(EmptyPageOrList(request.RequestUri.AbsolutePath));
        });

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5088/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30);

        var snapshot = await client.FetchSnapshotAsync();

        Assert.NotNull(snapshot.FetchError);
        Assert.Contains("/api/demands", snapshot.FetchError, StringComparison.Ordinal);
        Assert.Contains("WATCH_TIMEOUT", snapshot.FetchError, StringComparison.Ordinal);
        Assert.Contains("timeoutSeconds=30", snapshot.FetchError, StringComparison.Ordinal);
        Assert.Contains("elapsedMs=", snapshot.FetchError, StringComparison.Ordinal);
        Assert.Empty(snapshot.Demands);
    }

    [Fact]
    public async Task Connect_failure_on_alerts_reports_endpoint_and_stage()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/alerts", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Connection refused");
            }

            return Task.FromResult(EmptyPageOrList(request.RequestUri.AbsolutePath));
        });

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5088/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30);

        var snapshot = await client.FetchSnapshotAsync();

        Assert.NotNull(snapshot.FetchError);
        Assert.Contains("/api/alerts", snapshot.FetchError!, StringComparison.Ordinal);
        Assert.Contains("HTTP_CONNECT", snapshot.FetchError, StringComparison.Ordinal);
        Assert.Contains("Connection refused", snapshot.FetchError, StringComparison.Ordinal);
        Assert.True(snapshot.DemandsSucceeded);
        Assert.False(snapshot.AlertsSucceeded);
        Assert.True(snapshot.PollHealthSucceeded);
        Assert.False(snapshot.AllEndpointsSucceeded);
    }

    [Fact]
    public async Task Partial_success_keeps_demands_when_alerts_fail()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/alerts", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Connection refused");
            }

            if (path.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    path,
                    """{"items":[{"demandId":"abcd1234","taskType":"DIE_TO_OVEN","sublot":"S1","area":"A","eqp":"E","step":"1","dates":"2026-08-01T10:00:00+08:00","package":null,"status":"VISIBLE","mesLastSeenAt":"2026-08-01T02:00:00Z","disappearCount":0,"locationRisk":false,"locationRiskCode":null,"createdAt":"2026-08-01T02:00:00Z","goneAt":null}],"nextCursor":null,"hasMore":false}"""));
            }

            if (path.EndsWith("/api/poll-health", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    path,
                    """{"startedAt":"2026-08-01T02:00:00Z","endedAt":"2026-08-01T02:00:03Z","durationMs":3000,"rowCount":1,"success":true,"outcome":"SUCCESS","taskTypePauses":[]}"""));
            }

            return Task.FromResult(EmptyPageOrList(path));
        });

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5088/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30);

        var snapshot = await client.FetchSnapshotAsync();

        Assert.NotNull(snapshot.FetchError);
        Assert.Contains("/api/alerts", snapshot.FetchError!, StringComparison.Ordinal);
        Assert.Equal("abcd1234", Assert.Single(snapshot.Demands).DemandId);
        Assert.NotNull(snapshot.PollHealth);
        Assert.True(snapshot.PollHealth!.Success);
    }

    [Fact]
    public async Task Connect_failure_on_poll_health_reports_endpoint_and_stage()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/poll-health", StringComparison.Ordinal))
            {
                throw new HttpRequestException("No connection could be made");
            }

            return Task.FromResult(EmptyPageOrList(request.RequestUri.AbsolutePath));
        });

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5088/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30);

        var snapshot = await client.FetchSnapshotAsync();

        Assert.NotNull(snapshot.FetchError);
        Assert.True(snapshot.HasFetchError);
        Assert.Contains("/api/poll-health", snapshot.FetchError!, StringComparison.Ordinal);
        Assert.Contains("HTTP_CONNECT", snapshot.FetchError, StringComparison.Ordinal);
        Assert.Equal("/api/poll-health", snapshot.FailedEndpoint);
    }

    [Fact]
    public async Task Http_status_failure_reports_HTTP_STATUS_stage()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    ReasonPhrase = "boom",
                });
            }

            return Task.FromResult(EmptyPageOrList(request.RequestUri.AbsolutePath));
        });

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5088/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30);

        var snapshot = await client.FetchSnapshotAsync();

        Assert.NotNull(snapshot.FetchError);
        Assert.Contains("/api/demands", snapshot.FetchError!, StringComparison.Ordinal);
        Assert.Contains("HTTP_STATUS", snapshot.FetchError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_fetch_has_no_error_and_populates_lists()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/poll-health", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(EmptyPageOrList(path));
        });

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5088/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30);

        var snapshot = await client.FetchSnapshotAsync();

        Assert.Null(snapshot.FetchError);
        Assert.Empty(snapshot.Demands);
        Assert.Empty(snapshot.Alerts);
        Assert.Null(snapshot.PollHealth);
        Assert.Null(snapshot.FailedEndpoint);
    }

    [Fact]
    public async Task Previous_reappear_demand_lookup_uses_exact_id_path_without_new_id_fallback()
    {
        string? requestedPath = null;
        var handler = new StubHandler((request, _) =>
        {
            requestedPath = request.RequestUri!.PathAndQuery;
            return Task.FromResult(JsonResponse(
                request.RequestUri.AbsolutePath,
                """{"demandId":"previous-gone-id","taskType":"DIE_TO_OVEN","sublot":"S1","area":"A","eqp":"E","step":"1","dates":"2026-08-01T10:00:00+08:00","package":null,"status":"GONE","mesLastSeenAt":"2026-08-01T02:00:00Z","disappearCount":2,"locationRisk":false,"locationRiskCode":null,"createdAt":"2026-08-01T02:00:00Z","goneAt":"2026-08-01T03:00:00Z"}"""));
        });

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5088/"),
        };
        var client = new MesIngestApiClient(http);

        var demand = await client.FetchDemandByIdAsync("previous-gone-id");

        Assert.Equal("/api/demands/previous-gone-id", requestedPath);
        Assert.Equal("previous-gone-id", demand!.DemandId);
        Assert.Equal("GONE", demand.Status);
    }

    private static HttpResponseMessage EmptyPageOrList(string path)
    {
        if (WatchHttpTestStubs.IsContractPath(path))
        {
            return WatchHttpTestStubs.MatchingContract(path);
        }

        if (path.EndsWith("/api/demands", StringComparison.Ordinal)
            || path.EndsWith("/api/alerts", StringComparison.Ordinal))
        {
            return JsonResponse(path, """{"items":[],"nextCursor":null,"hasMore":false}""");
        }

        if (path.EndsWith("/api/poll-health", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5088" + path),
            };
        }

        return JsonResponse(path, "[]");
    }

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
