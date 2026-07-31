using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesIngest.Watch;

internal sealed class WatchOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:5088";
    public int RefreshSeconds { get; set; } = 2;

    /// <summary>
    /// HttpClient timeout for each Watch refresh request (seconds). Default 30; legal range 1–300.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Local WatchConnectionEvent JSONL retention in days. Default 30.
    /// </summary>
    public int ConnectionLogRetentionDays { get; set; } = 30;

    /// <summary>
    /// Local WatchConnectionEvent JSONL directory size cap in MiB. Default 100.
    /// </summary>
    public int ConnectionLogMaxSizeMb { get; set; } = 100;

    /// <summary>
    /// Same value as Host MesIngest:SharedSecret when API is bound beyond localhost.
    /// Sent as Authorization: Bearer.
    /// </summary>
    public string SharedSecret { get; set; } = "";
}

internal sealed class MesIngestApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private readonly int _requestTimeoutSeconds;

    public MesIngestApiClient(HttpClient http, int requestTimeoutSeconds = 30)
    {
        _http = http;
        _requestTimeoutSeconds = requestTimeoutSeconds;
    }

    public async Task<WatchSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var demands = await FetchListAsync<WatchDemandDto>("/api/demands", cancellationToken)
                .ConfigureAwait(false);
            var alerts = await FetchListAsync<WatchAlertDto>("/api/alerts", cancellationToken)
                .ConfigureAwait(false);
            var health = await FetchPollHealthAsync(cancellationToken).ConfigureAwait(false);

            return new WatchSnapshot(
                Demands: demands,
                Alerts: alerts,
                PollHealth: health,
                FetchError: null,
                FailedEndpoint: null,
                FailedStage: null,
                FailedElapsed: null);
        }
        catch (WatchEndpointFetchException ex)
        {
            return new WatchSnapshot(
                Demands: [],
                Alerts: [],
                PollHealth: null,
                FetchError: ex.FormatForBanner(_requestTimeoutSeconds),
                FailedEndpoint: ex.Endpoint,
                FailedStage: ex.Stage,
                FailedElapsed: ex.Elapsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new WatchSnapshot(
                Demands: [],
                Alerts: [],
                PollHealth: null,
                FetchError: $"endpoint=(unknown) stage=HTTP_ERROR timeoutSeconds={_requestTimeoutSeconds} elapsedMs=0 {ex.Message}",
                FailedEndpoint: null,
                FailedStage: "HTTP_ERROR",
                FailedElapsed: TimeSpan.Zero);
        }
    }

    private async Task<IReadOnlyList<T>> FetchListAsync<T>(string endpoint, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var items = await _http.GetFromJsonAsync<List<T>>(endpoint, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return items ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            throw Classify(endpoint, sw.Elapsed, ex);
        }
    }

    private async Task<WatchPollHealthDto?> FetchPollHealthAsync(CancellationToken cancellationToken)
    {
        const string endpoint = "/api/poll-health";
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await _http.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<WatchPollHealthDto>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            throw Classify(endpoint, sw.Elapsed, ex);
        }
    }

    private static WatchEndpointFetchException Classify(string endpoint, TimeSpan elapsed, Exception ex)
    {
        var stage = ClassifyStage(ex);
        return new WatchEndpointFetchException(endpoint, stage, elapsed, ex);
    }

    private static string ClassifyStage(Exception ex)
    {
        if (ex is TaskCanceledException || ex.InnerException is TimeoutException)
        {
            return "HTTP_TIMEOUT";
        }

        if (ex is JsonException)
        {
            return "HTTP_JSON";
        }

        if (ex is HttpRequestException http)
        {
            return http.StatusCode is not null ? "HTTP_STATUS" : "HTTP_CONNECT";
        }

        if (ex is InvalidOperationException)
        {
            return "HTTP_STATUS";
        }

        return "HTTP_ERROR";
    }
}

internal sealed class WatchEndpointFetchException : Exception
{
    public WatchEndpointFetchException(string endpoint, string stage, TimeSpan elapsed, Exception inner)
        : base(inner.Message, inner)
    {
        Endpoint = endpoint;
        Stage = stage;
        Elapsed = elapsed;
    }

    public string Endpoint { get; }
    public string Stage { get; }
    public TimeSpan Elapsed { get; }

    public string FormatForBanner(int timeoutSeconds) =>
        $"endpoint={Endpoint} stage={Stage} timeoutSeconds={timeoutSeconds} elapsedMs={(long)Elapsed.TotalMilliseconds} {Message}";
}

internal sealed record WatchSnapshot(
    IReadOnlyList<WatchDemandDto> Demands,
    IReadOnlyList<WatchAlertDto> Alerts,
    WatchPollHealthDto? PollHealth,
    string? FetchError,
    string? FailedEndpoint = null,
    string? FailedStage = null,
    TimeSpan? FailedElapsed = null)
{
    /// <summary>
    /// When <see cref="FetchError"/> is set, Demands/Alerts/PollHealth are placeholders —
    /// callers must keep the last successful projection instead of applying these lists.
    /// </summary>
    public bool HasFetchError => FetchError is not null;
}
