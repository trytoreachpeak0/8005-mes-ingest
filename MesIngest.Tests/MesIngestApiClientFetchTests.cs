using System.Net;
using System.Text;
using MesIngest.Watch;

namespace MesIngest.Tests;

public class MesIngestApiClientFetchTests
{
    [Fact]
    public async Task Timeout_on_demands_reports_endpoint_stage_elapsed_and_timeout()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                throw new TaskCanceledException("canceled", new TimeoutException());
            }

            return Task.FromResult(JsonResponse(request.RequestUri.AbsolutePath, "[]"));
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
        Assert.Contains("HTTP_TIMEOUT", snapshot.FetchError, StringComparison.Ordinal);
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

            return Task.FromResult(JsonResponse(request.RequestUri.AbsolutePath, "[]"));
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

            return Task.FromResult(JsonResponse(request.RequestUri.AbsolutePath, "[]"));
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

            return Task.FromResult(JsonResponse(request.RequestUri.AbsolutePath, "[]"));
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

            return Task.FromResult(JsonResponse(path, "[]"));
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
