using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MesIngest.Core;

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

internal static class WatchHttpStageClassifier
{
    public static string Classify(Exception ex)
    {
        if (ex is TaskCanceledException)
        {
            return ex.InnerException is TimeoutException
                ? LatencyStages.WatchTimeout
                : LatencyStages.HostAbort;
        }

        if (ex.InnerException is TimeoutException)
        {
            return LatencyStages.WatchTimeout;
        }

        if (ex is JsonException)
        {
            return LatencyStages.HttpJson;
        }

        if (ex is HttpRequestException http)
        {
            return http.StatusCode is not null ? LatencyStages.HttpStatus : LatencyStages.HttpConnect;
        }

        if (ex is InvalidOperationException)
        {
            return LatencyStages.HttpStatus;
        }

        return LatencyStages.HttpError;
    }
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
    private readonly ILatencyTelemetry _telemetry;

    public MesIngestApiClient(
        HttpClient http,
        int requestTimeoutSeconds = 30,
        ILatencyTelemetry? telemetry = null)
    {
        _http = http;
        _requestTimeoutSeconds = requestTimeoutSeconds;
        _telemetry = telemetry ?? NullLatencyTelemetry.Instance;
    }

    public Task<WatchSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) =>
        FetchSnapshotAsync(WatchDemandBrowseQuery.Default, WatchAlertBrowseQuery.Default, cancellationToken);

    public Task<WatchSnapshot> FetchSnapshotAsync(
        WatchDemandBrowseQuery demandQuery,
        CancellationToken cancellationToken = default) =>
        FetchSnapshotAsync(demandQuery, WatchAlertBrowseQuery.Default, cancellationToken);

    public async Task<WatchSnapshot> FetchSnapshotAsync(
        WatchDemandBrowseQuery demandQuery,
        WatchAlertBrowseQuery alertQuery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demandQuery);
        ArgumentNullException.ThrowIfNull(alertQuery);

        var correlationId = Guid.NewGuid().ToString("N");
        IReadOnlyList<WatchDemandDto> demands = [];
        IReadOnlyList<WatchAlertDto> alerts = [];
        WatchPollHealthDto? health = null;
        string? nextCursor = null;
        var hasMore = false;
        string? alertsNextCursor = null;
        var alertsHasMore = false;
        var demandsOk = false;
        var alertsOk = false;
        var healthOk = false;

        string? fetchError = null;
        string? failedEndpoint = null;
        string? failedStage = null;
        TimeSpan? failedElapsed = null;

        try
        {
            await EnsureContractCompatibleAsync(correlationId, cancellationToken).ConfigureAwait(false);
        }
        catch (WatchEndpointFetchException ex)
        {
            return new WatchSnapshot(
                Demands: [],
                Alerts: [],
                PollHealth: null,
                FetchError: ex.FormatForBanner(_requestTimeoutSeconds, correlationId),
                FailedEndpoint: ex.Endpoint,
                FailedStage: ex.Stage,
                FailedElapsed: ex.Elapsed,
                CorrelationId: correlationId);
        }

        try
        {
            var demandPage = await FetchDemandPageWithCursorRecoveryAsync(
                    demandQuery,
                    correlationId,
                    cancellationToken)
                .ConfigureAwait(false);
            demands = demandPage.Items;
            nextCursor = demandPage.NextCursor;
            hasMore = demandPage.HasMore;
            demandsOk = true;
        }
        catch (WatchEndpointFetchException ex)
        {
            fetchError = ex.FormatForBanner(_requestTimeoutSeconds, correlationId);
            failedEndpoint = ex.Endpoint;
            failedStage = ex.Stage;
            failedElapsed = ex.Elapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var stage = WatchHttpStageClassifier.Classify(ex);
            fetchError =
                $"endpoint=(unknown) stage={stage} timeoutSeconds={_requestTimeoutSeconds} elapsedMs=0 correlationId={correlationId} {ex.Message}";
            failedStage = stage;
            failedElapsed = TimeSpan.Zero;
        }

        try
        {
            var alertPage = await FetchAlertPageAsync(alertQuery, correlationId, cancellationToken)
                .ConfigureAwait(false);
            alerts = alertPage.Items;
            alertsNextCursor = alertPage.NextCursor;
            alertsHasMore = alertPage.HasMore;
            alertsOk = true;
        }
        catch (WatchEndpointFetchException ex)
        {
            fetchError ??= ex.FormatForBanner(_requestTimeoutSeconds, correlationId);
            failedEndpoint ??= ex.Endpoint;
            failedStage ??= ex.Stage;
            failedElapsed ??= ex.Elapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var stage = WatchHttpStageClassifier.Classify(ex);
            fetchError ??=
                $"endpoint=(unknown) stage={stage} timeoutSeconds={_requestTimeoutSeconds} elapsedMs=0 correlationId={correlationId} {ex.Message}";
            failedStage ??= stage;
            failedElapsed ??= TimeSpan.Zero;
        }

        try
        {
            health = await FetchPollHealthAsync(correlationId, cancellationToken)
                .ConfigureAwait(false);
            healthOk = true;
        }
        catch (WatchEndpointFetchException ex)
        {
            fetchError ??= ex.FormatForBanner(_requestTimeoutSeconds, correlationId);
            failedEndpoint ??= ex.Endpoint;
            failedStage ??= ex.Stage;
            failedElapsed ??= ex.Elapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var stage = WatchHttpStageClassifier.Classify(ex);
            fetchError ??=
                $"endpoint=(unknown) stage={stage} timeoutSeconds={_requestTimeoutSeconds} elapsedMs=0 correlationId={correlationId} {ex.Message}";
            failedStage ??= stage;
            failedElapsed ??= TimeSpan.Zero;
        }

        return new WatchSnapshot(
            Demands: demands,
            Alerts: alerts,
            PollHealth: health,
            FetchError: fetchError,
            FailedEndpoint: failedEndpoint,
            FailedStage: failedStage,
            FailedElapsed: failedElapsed,
            CorrelationId: correlationId,
            DemandsNextCursor: nextCursor,
            DemandsHasMore: hasMore,
            AlertsNextCursor: alertsNextCursor,
            AlertsHasMore: alertsHasMore,
            DemandsSucceeded: demandsOk,
            AlertsSucceeded: alertsOk,
            PollHealthSucceeded: healthOk);
    }

    public Task<WatchDemandPage> FetchDemandPageAsync(
        WatchDemandBrowseQuery query,
        CancellationToken cancellationToken = default) =>
        FetchDemandPageCoreAsync(query, Guid.NewGuid().ToString("N"), cancellationToken);

    public Task<WatchAlertPage> FetchAlertPageAsync(
        WatchAlertBrowseQuery query,
        CancellationToken cancellationToken = default) =>
        FetchAlertPageAsync(query, Guid.NewGuid().ToString("N"), cancellationToken);

    /// <summary>
    /// Exact DemandId lookup via GET /api/demands/{demandId}. Returns null on 404.
    /// </summary>
    public async Task<WatchDemandDto?> FetchDemandByIdAsync(
        string demandId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        var endpoint = "/api/demands/" + Uri.EscapeDataString(demandId.Trim());
        var correlationId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = CreateRequest(HttpMethod.Get, endpoint, correlationId);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var bytes = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(body);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RecordWatch(correlationId, endpoint, sw.ElapsedMilliseconds, 404, 0, bytes);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    null,
                    response.StatusCode);
            }

            var demand = JsonSerializer.Deserialize<WatchDemandDto>(body, JsonOptions)
                ?? throw new JsonException("Demand by-id deserialize returned null.");
            RecordWatch(correlationId, endpoint, sw.ElapsedMilliseconds, (int)response.StatusCode, 1, bytes);
            return demand;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var stage = WatchHttpStageClassifier.Classify(ex);
            RecordWatch(
                correlationId,
                endpoint,
                sw.ElapsedMilliseconds,
                statusCode: null,
                rowCount: 0,
                bytes: 0,
                stage: stage,
                detail: LatencyLogFormatter.Sanitize(ex.Message));
            throw Classify(endpoint, sw.Elapsed, ex);
        }
    }

    private async Task<WatchDemandPage> FetchDemandPageWithCursorRecoveryAsync(
        WatchDemandBrowseQuery query,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FetchDemandPageCoreAsync(query, correlationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WatchEndpointFetchException ex) when (
            !string.IsNullOrWhiteSpace(query.Cursor)
            && ex.InnerException is HttpRequestException http
            && http.StatusCode == HttpStatusCode.BadRequest)
        {
            // Stale/mismatched cursor → safe reload of first page.
            return await FetchDemandPageCoreAsync(
                    query with { Cursor = null },
                    correlationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<WatchDemandPage> FetchDemandPageCoreAsync(
        WatchDemandBrowseQuery query,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var endpoint = query.ToRelativeUrl();
        var pathForTelemetry = "/api/demands";
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = CreateRequest(HttpMethod.Get, endpoint, correlationId);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var bytes = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(body);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    null,
                    response.StatusCode);
            }

            var page = DeserializeDemandPage(body);
            RecordWatch(
                correlationId,
                pathForTelemetry,
                sw.ElapsedMilliseconds,
                (int)response.StatusCode,
                page.Items.Count,
                bytes);
            return page;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var stage = WatchHttpStageClassifier.Classify(ex);
            RecordWatch(
                correlationId,
                pathForTelemetry,
                sw.ElapsedMilliseconds,
                statusCode: null,
                rowCount: 0,
                bytes: 0,
                stage: stage,
                detail: LatencyLogFormatter.Sanitize(ex.Message));
            throw Classify(pathForTelemetry, sw.Elapsed, ex);
        }
    }

    private static WatchDemandPage DeserializeDemandPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new JsonException("Demand page body is empty; expected items/nextCursor/hasMore envelope.");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("items", out _))
        {
            throw new JsonException("Demand page must be an object with items/nextCursor/hasMore (bare array is no longer supported).");
        }

        var page = JsonSerializer.Deserialize<WatchDemandPage>(body, JsonOptions)
            ?? throw new JsonException("Demand page deserialize returned null.");
        return page;
    }

    private async Task<WatchAlertPage> FetchAlertPageAsync(
        WatchAlertBrowseQuery query,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var endpoint = query.ToRelativeUrl();
        const string pathForTelemetry = "/api/alerts";
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = CreateRequest(HttpMethod.Get, endpoint, correlationId);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var bytes = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(body);

            if (!response.IsSuccessStatusCode)
            {
                RecordWatch(
                    correlationId,
                    pathForTelemetry,
                    sw.ElapsedMilliseconds,
                    (int)response.StatusCode,
                    rowCount: 0,
                    bytes,
                    stage: LatencyStages.HttpStatus,
                    detail: LatencyLogFormatter.Sanitize(body));
                throw Classify(pathForTelemetry, sw.Elapsed, new HttpRequestException(
                    $"HTTP {(int)response.StatusCode}: {LatencyLogFormatter.Sanitize(body)}",
                    null,
                    response.StatusCode));
            }

            var page = DeserializeAlertPage(body);
            RecordWatch(
                correlationId,
                pathForTelemetry,
                sw.ElapsedMilliseconds,
                (int)response.StatusCode,
                rowCount: page.Items.Count,
                bytes,
                stage: LatencyStages.HttpOk);
            return page;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var stage = WatchHttpStageClassifier.Classify(ex);
            RecordWatch(
                correlationId,
                pathForTelemetry,
                sw.ElapsedMilliseconds,
                statusCode: null,
                rowCount: 0,
                bytes: 0,
                stage: stage,
                detail: LatencyLogFormatter.Sanitize(ex.Message));
            throw Classify(pathForTelemetry, sw.Elapsed, ex);
        }
    }

    private static WatchAlertPage DeserializeAlertPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new JsonException("Alert page body is empty; expected items/nextCursor/hasMore envelope.");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("items", out _))
        {
            throw new JsonException("Alert page must be an object with items/nextCursor/hasMore (bare array is no longer supported).");
        }

        return JsonSerializer.Deserialize<WatchAlertPage>(body, JsonOptions)
            ?? throw new JsonException("Alert page deserialize returned null.");
    }

    private async Task<IReadOnlyList<T>> FetchListAsync<T>(
        string endpoint,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = CreateRequest(HttpMethod.Get, endpoint, correlationId);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var bytes = response.Content.Headers.ContentLength;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            bytes ??= Encoding.UTF8.GetByteCount(body);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    null,
                    response.StatusCode);
            }

            var items = string.IsNullOrWhiteSpace(body)
                ? []
                : JsonSerializer.Deserialize<List<T>>(body, JsonOptions) ?? [];

            RecordWatch(correlationId, endpoint, sw.ElapsedMilliseconds, (int)response.StatusCode, items.Count, bytes.Value);
            return items;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var stage = WatchHttpStageClassifier.Classify(ex);
            RecordWatch(
                correlationId,
                endpoint,
                sw.ElapsedMilliseconds,
                statusCode: null,
                rowCount: 0,
                bytes: 0,
                stage: stage,
                detail: LatencyLogFormatter.Sanitize(ex.Message));
            throw Classify(endpoint, sw.Elapsed, ex);
        }
    }

    private async Task EnsureContractCompatibleAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        const string endpoint = "/api/contract";
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = CreateRequest(HttpMethod.Get, endpoint, correlationId);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var bytes = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(body);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RecordWatch(correlationId, endpoint, sw.ElapsedMilliseconds, 404, rowCount: 0, bytes: bytes);
                throw new WatchEndpointFetchException(
                    endpoint,
                    LatencyStages.HttpStatus,
                    sw.Elapsed,
                    $"{MesIngestApiContract.MismatchErrorCode}: Host has no /api/contract (expected {MesIngestApiContract.Version}). Upgrade Host and Watch together.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    null,
                    response.StatusCode);
            }

            var info = string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize<MesIngestContractInfo>(body, JsonOptions);

            RecordWatch(correlationId, endpoint, sw.ElapsedMilliseconds, (int)response.StatusCode, rowCount: 1, bytes: bytes);

            if (info is null
                || !string.Equals(info.ContractVersion, MesIngestApiContract.Version, StringComparison.Ordinal))
            {
                var hostVersion = info?.ContractVersion ?? "(missing)";
                throw new WatchEndpointFetchException(
                    endpoint,
                    LatencyStages.HttpStatus,
                    sw.Elapsed,
                    $"{MesIngestApiContract.MismatchErrorCode}: Host contractVersion={hostVersion}; Watch expects {MesIngestApiContract.Version}. Upgrade Host and Watch from the same install package.");
            }
        }
        catch (WatchEndpointFetchException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var stage = WatchHttpStageClassifier.Classify(ex);
            RecordWatch(
                correlationId,
                endpoint,
                sw.ElapsedMilliseconds,
                statusCode: null,
                rowCount: 0,
                bytes: 0,
                stage: stage,
                detail: LatencyLogFormatter.Sanitize(ex.Message));
            throw Classify(endpoint, sw.Elapsed, ex);
        }
    }

    private async Task<WatchPollHealthDto?> FetchPollHealthAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        const string endpoint = "/api/poll-health";
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = CreateRequest(HttpMethod.Get, endpoint, correlationId);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var bytes = response.Content.Headers.ContentLength ?? 0;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RecordWatch(correlationId, endpoint, sw.ElapsedMilliseconds, 404, rowCount: 0, bytes: bytes);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            bytes = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(body);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    null,
                    response.StatusCode);
            }

            var health = string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize<WatchPollHealthDto>(body, JsonOptions);

            RecordWatch(correlationId, endpoint, sw.ElapsedMilliseconds, (int)response.StatusCode, rowCount: health is null ? 0 : 1, bytes: bytes);
            return health;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var stage = WatchHttpStageClassifier.Classify(ex);
            RecordWatch(
                correlationId,
                endpoint,
                sw.ElapsedMilliseconds,
                statusCode: null,
                rowCount: 0,
                bytes: 0,
                stage: stage,
                detail: LatencyLogFormatter.Sanitize(ex.Message));
            throw Classify(endpoint, sw.Elapsed, ex);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint, string correlationId)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.TryAddWithoutValidation(LatencyHeaders.CorrelationId, correlationId);
        return request;
    }

    private void RecordWatch(
        string correlationId,
        string endpoint,
        long elapsedMs,
        int? statusCode,
        int rowCount,
        long bytes,
        string? stage = null,
        string? detail = null)
    {
        var resolvedStage = stage
            ?? (statusCode is >= 400 ? LatencyStages.HttpStatus : LatencyStages.HttpOk);
        try
        {
            _telemetry.Record(new LatencyEvent(
                CorrelationId: correlationId,
                Component: LatencyComponents.Watch,
                Stage: resolvedStage,
                ElapsedMs: elapsedMs,
                StatusCode: statusCode,
                RowCount: rowCount,
                Bytes: bytes,
                Endpoint: endpoint,
                Detail: detail));
        }
        catch (Exception)
        {
            // Telemetry must never turn a completed Host fetch into a UI failure.
        }
    }

    private static WatchEndpointFetchException Classify(string endpoint, TimeSpan elapsed, Exception ex)
    {
        var stage = WatchHttpStageClassifier.Classify(ex);
        return new WatchEndpointFetchException(endpoint, stage, elapsed, ex);
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

    public WatchEndpointFetchException(string endpoint, string stage, TimeSpan elapsed, string message)
        : base(message)
    {
        Endpoint = endpoint;
        Stage = stage;
        Elapsed = elapsed;
    }

    public string Endpoint { get; }
    public string Stage { get; }
    public TimeSpan Elapsed { get; }

    public string FormatForBanner(int timeoutSeconds, string correlationId) =>
        $"endpoint={Endpoint} stage={Stage} timeoutSeconds={timeoutSeconds} elapsedMs={(long)Elapsed.TotalMilliseconds} correlationId={correlationId} {Message}";
}

internal sealed record WatchSnapshot(
    IReadOnlyList<WatchDemandDto> Demands,
    IReadOnlyList<WatchAlertDto> Alerts,
    WatchPollHealthDto? PollHealth,
    string? FetchError,
    string? FailedEndpoint = null,
    string? FailedStage = null,
    TimeSpan? FailedElapsed = null,
    string? CorrelationId = null,
    string? DemandsNextCursor = null,
    bool DemandsHasMore = false,
    string? AlertsNextCursor = null,
    bool AlertsHasMore = false,
    bool DemandsSucceeded = false,
    bool AlertsSucceeded = false,
    bool PollHealthSucceeded = false)
{
    /// <summary>
    /// True when at least one endpoint failed. Successful endpoint payloads may still be applied;
    /// failed endpoints must keep prior data and leave the connection banner visible.
    /// </summary>
    public bool HasFetchError => FetchError is not null;

    public bool AllEndpointsSucceeded =>
        DemandsSucceeded && AlertsSucceeded && PollHealthSucceeded && FetchError is null;
}
