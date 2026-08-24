using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MesIngest.Core;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

/// <summary>
/// HTTP boundary for the replacement MesIngest v2 contract. Business reads are
/// added only after the Host's complete frozen contract identity is accepted.
/// </summary>
internal sealed class MesIngestV2ApiClient : IWatchV2ApiClient
{
    private const string ContractEndpoint = "/api/v2/contract";
    private const int RawEvidenceMaximumEnvelopeBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _sensitiveValue;

    private MesIngestV2ApiClient(HttpClient http, string sensitiveValue)
    {
        _http = http;
        _sensitiveValue = sensitiveValue;
    }

    public static MesIngestV2ApiClient CreateForHost(
        WatchHostSettings settings,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(settings.BaseUrl + "/");
        http.Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(settings.Credential))
        {
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.Credential);
        }

        return new MesIngestV2ApiClient(http, settings.Credential);
    }

    public async Task VerifyContractAsync(CancellationToken cancellationToken)
    {
        var correlationId = WatchProcessCorrelationId.Create();
        try
        {
            await DiscoverAndRequireExactContractAsync(correlationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WatchEndpointFetchException ex)
        {
            throw WatchHostQueryFailure.From(ex, correlationId, Redact);
        }
    }

    public Task<WatchOverviewSnapshot> FetchOverviewAsync(
        WatchOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        var endpoint = normalized.MesAreas is { Count: > 0 } areas
            ? "/api/v2/watch-overview?" + string.Join(
                "&",
                areas.Select(area => $"area={Uri.EscapeDataString(area)}"))
            : "/api/v2/watch-overview";
        return FetchJsonAsync<WatchOverviewSnapshot, WatchOverviewSnapshot>(
            endpoint,
            snapshot =>
            {
                RequireResponseContractVersion(snapshot.Snapshot.ContractVersion);
                return snapshot;
            },
            cancellationToken);
    }

    public Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
        DemandSeriesBrowseQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        var parameters = new List<KeyValuePair<string, string>>();
        AddMany(parameters, "lifecycle", normalized.Filter.Lifecycles);
        AddMany(parameters, "presence", normalized.Filter.CurrentPresences);
        AddMany(parameters, "workType", normalized.Filter.WorkTypes);
        AddMany(parameters, "area", normalized.Filter.MesAreas);
        AddOptional(parameters, "sublot", normalized.Filter.SublotContains);
        AddOptional(parameters, "seriesId", normalized.Filter.SeriesId);
        AddOptional(parameters, "demandId", normalized.Filter.DemandId);
        parameters.Add(new("pageSize", normalized.PageSize.ToString(CultureInfo.InvariantCulture)));
        parameters.Add(new("page", normalized.PageNumber.ToString(CultureInfo.InvariantCulture)));
        AddOptional(parameters, "snapshot", normalized.SnapshotReference);
        AddOptional(parameters, "cursor", normalized.Cursor);
        parameters.Add(new("order", normalized.Order));
        var endpoint = BuildEndpoint("/api/v2/demand-series", parameters);
        return FetchJsonAsync<WatchV2DemandSeriesListWire, DemandSeriesListSnapshot>(
            endpoint,
            wire =>
            {
                RequireResponseContractVersion(wire.Snapshot.ContractVersion);
                return wire.ToCore(normalized.Filter);
            },
            cancellationToken);
    }

    public Task<DemandSeriesDetailSnapshot> FetchDemandSeriesDetailAsync(
        string seriesId,
        string snapshotReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotReference);
        var endpoint = BuildEndpoint(
            "/api/v2/demand-series/" + Uri.EscapeDataString(seriesId),
            [new("snapshot", snapshotReference)]);
        return FetchJsonAsync<WatchV2FrozenDemandSeriesWire, DemandSeriesDetailSnapshot>(
            endpoint,
            wire =>
            {
                RequireResponseContractVersion(wire.Snapshot.ContractVersion);
                return wire.ToCore();
            },
            cancellationToken);
    }

    public Task<ReadabilityAuditListSnapshot> FetchReadabilityAuditAsync(
        ReadabilityAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        var parameters = new List<KeyValuePair<string, string>>();
        AddMany(parameters, "state", normalized.Filter.ReadabilityStates);
        AddMany(parameters, "workType", normalized.Filter.WorkTypes);
        AddMany(parameters, "blocker", normalized.Filter.Blockers);
        AddOptional(parameters, "demandId", normalized.Filter.DemandId);
        AddOptional(parameters, "sublot", normalized.Filter.SublotContains);
        AddMany(parameters, "area", normalized.Filter.MesAreas);
        parameters.Add(new("pageSize", normalized.PageSize.ToString(CultureInfo.InvariantCulture)));
        parameters.Add(new("page", normalized.PageNumber.ToString(CultureInfo.InvariantCulture)));
        AddOptional(parameters, "snapshot", normalized.SnapshotReference);
        AddOptional(parameters, "cursor", normalized.Cursor);
        parameters.Add(new("order", normalized.Order));
        var endpoint = BuildEndpoint("/api/v2/readability-audit", parameters);
        return FetchJsonAsync<WatchV2ReadabilityAuditListWire, ReadabilityAuditListSnapshot>(
            endpoint,
            wire =>
            {
                RequireResponseContractVersion(wire.Snapshot.ContractVersion);
                return wire.ToCore();
            },
            cancellationToken);
    }

    public Task<ReadabilityAuditDetailSnapshot> FetchReadabilityAuditDetailAsync(
        string demandId,
        string snapshotReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotReference);
        var endpoint = BuildEndpoint(
            "/api/v2/readability-audit/" + Uri.EscapeDataString(demandId),
            [new("snapshot", snapshotReference)]);
        return FetchJsonAsync<WatchV2ReadabilityAuditDetailWire, ReadabilityAuditDetailSnapshot>(
            endpoint,
            wire =>
            {
                RequireResponseContractVersion(wire.Snapshot.ContractVersion);
                return wire.ToCore();
            },
            cancellationToken);
    }

    public Task<ErrorSearchListSnapshot> FetchErrorSearchAsync(
        ErrorSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        if (string.Equals(
                normalized.Window.Kind,
                ErrorSearchWindowKinds.Custom,
                StringComparison.Ordinal)
            && normalized.Window.FromUtc is null
            && normalized.Window.ToUtc is null)
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.InvalidQuery,
                "A custom error-search window requires from or to.");
        }
        var parameters = new List<KeyValuePair<string, string>>();
        AddMany(parameters, "category", normalized.Filter.Categories);
        AddMany(parameters, "code", normalized.Filter.ErrorCodes);
        AddMany(parameters, "state", normalized.Filter.ActivityStates);
        AddOptional(parameters, "seriesId", normalized.Filter.SeriesId);
        AddOptional(parameters, "demandId", normalized.Filter.DemandId);
        AddOptional(parameters, "sublot", normalized.Filter.SublotContains);
        if (string.Equals(
                normalized.Window.Kind,
                ErrorSearchWindowKinds.Custom,
                StringComparison.Ordinal))
        {
            AddOptional(
                parameters,
                "from",
                normalized.Window.FromUtc?.ToString("O", CultureInfo.InvariantCulture));
            AddOptional(
                parameters,
                "to",
                normalized.Window.ToUtc?.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            parameters.Add(new("window", normalized.Window.Kind));
        }
        parameters.Add(new("pageSize", normalized.PageSize.ToString(CultureInfo.InvariantCulture)));
        AddOptional(parameters, "snapshot", normalized.SnapshotReference);
        AddOptional(parameters, "cursor", normalized.Cursor);
        var endpoint = BuildEndpoint("/api/v2/error-search", parameters);
        return FetchJsonAsync<WatchV2ErrorSearchListWire, ErrorSearchListSnapshot>(
            endpoint,
            wire =>
            {
                RequireResponseContractVersion(wire.Snapshot.ContractVersion);
                return wire.ToCore();
            },
            cancellationToken);
    }

    public Task<ErrorSearchDetailSnapshot> FetchErrorSearchDetailAsync(
        string seriesId,
        string snapshotReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotReference);
        var endpoint = BuildEndpoint(
            "/api/v2/error-search/" + Uri.EscapeDataString(seriesId),
            [new("snapshot", snapshotReference)]);
        return FetchJsonAsync<WatchV2ErrorSearchDetailWire, ErrorSearchDetailSnapshot>(
            endpoint,
            wire =>
            {
                RequireResponseContractVersion(wire.Snapshot.ContractVersion);
                var detail = wire.ToCore();
                if (!string.Equals(
                        detail.Series.SeriesId,
                        seriesId,
                        StringComparison.Ordinal))
                {
                    throw new JsonException(
                        "The Error Search detail SeriesId does not match the requested route.");
                }
                if (!string.Equals(
                        detail.SnapshotReference,
                        snapshotReference,
                        StringComparison.Ordinal))
                {
                    throw new JsonException(
                        "The Error Search detail snapshot does not match the requested snapshot.");
                }
                return detail;
            },
            cancellationToken);
    }

    public Task<ErrorSearchRawEvidenceSnapshot> FetchErrorRawEvidenceAsync(
        string seriesId,
        string evidenceId,
        string snapshotReference,
        ErrorSearchRawEvidenceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotReference);
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        var endpoint = BuildEndpoint(
            "/api/v2/error-search/" + Uri.EscapeDataString(seriesId)
            + "/evidence/" + Uri.EscapeDataString(evidenceId)
            + "/raw-observations",
            [
                new("snapshot", snapshotReference),
                new("fields", string.Join(',', normalized.Fields)),
                new("maxItems", normalized.MaxItems.ToString(CultureInfo.InvariantCulture)),
            ]);
        return FetchJsonAsync<WatchV2ErrorSearchRawEvidenceWire, ErrorSearchRawEvidenceSnapshot>(
            endpoint,
            wire =>
            {
                RequireResponseContractVersion(wire.Snapshot.ContractVersion);
                return wire.ToCore();
            },
            cancellationToken,
            RawEvidenceMaximumEnvelopeBytes);
    }

    public Task<CurrentIngestAttentionSnapshot> FetchCurrentAttentionAsync(
        CurrentIngestAttentionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("pageSize", normalized.PageSize.ToString(CultureInfo.InvariantCulture)),
            new("pageNumber", normalized.PageNumber.ToString(CultureInfo.InvariantCulture)),
        };
        AddMany(parameters, "kind", normalized.Kinds ?? []);
        AddMany(parameters, "severity", normalized.Severities ?? []);
        var endpoint = BuildEndpoint("/api/v2/current-ingest-attention", parameters);
        return FetchJsonAsync<WatchV2CurrentAttentionWire, CurrentIngestAttentionSnapshot>(
            endpoint,
            wire =>
            {
                RequireResponseContractVersion(wire.Snapshot.ContractVersion);
                return wire.ToCore();
            },
            cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private async Task<TResult> FetchJsonAsync<TWire, TResult>(
        string endpoint,
        Func<TWire, TResult> map,
        CancellationToken cancellationToken,
        int? maximumResponseBytes = null)
    {
        var correlationId = WatchProcessCorrelationId.Create();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation(LatencyHeaders.CorrelationId, correlationId);
            using var response = await _http.SendAsync(
                    request,
                    maximumResponseBytes is null
                        ? HttpCompletionOption.ResponseContentRead
                        : HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            var body = await ReadResponseBodyAsync(
                    response.Content,
                    maximumResponseBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                V2ErrorPayload? error = null;
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        error = JsonSerializer.Deserialize<V2ErrorPayload>(body, JsonOptions);
                    }
                    catch (JsonException)
                    {
                        // A malformed error is still observable as a server-query failure.
                    }
                }

                var kind = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? WatchHostFailureKind.Authentication
                    : WatchHostFailureKind.ServerQuery;
                var message = error?.Error
                    ?? $"Response status code does not indicate success: {(int)response.StatusCode} "
                    + $"({response.ReasonPhrase}).";
                throw new WatchHostQueryException(
                    kind,
                    endpoint,
                    correlationId,
                    Redact(message),
                    errorCode: error?.Code,
                    statusCode: response.StatusCode);
            }

            var wire = JsonSerializer.Deserialize<TWire>(body, JsonOptions)
                ?? throw new JsonException($"{endpoint} returned an empty JSON document.");
            try
            {
                return map(wire);
            }
            catch (NewMesIngestContractMismatchException ex)
            {
                throw new WatchHostQueryException(
                    WatchHostFailureKind.Contract,
                    endpoint,
                    correlationId,
                    Redact($"{ex.Code}: {ex.Message}"),
                    errorCode: ex.Code);
            }
            catch (Exception ex)
            {
                throw new JsonException($"{endpoint} returned an invalid V2 wire document.", ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var fetch = new WatchEndpointFetchException(
                endpoint,
                WatchHttpStageClassifier.Classify(ex),
                stopwatch.Elapsed,
                ex);
            throw WatchHostQueryFailure.From(fetch, correlationId, Redact);
        }
    }

    private static async Task<string> ReadResponseBodyAsync(
        HttpContent content,
        int? maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        if (maximumResponseBytes is null)
        {
            return await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        var maximumBytes = maximumResponseBytes.Value;
        if (content.Headers.ContentLength is > 0 and var contentLength
            && contentLength > maximumBytes)
        {
            throw new JsonException(
                $"The response body exceeds the {maximumBytes}-byte bounded envelope.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var body = new MemoryStream(Math.Min(maximumBytes, 8 * 1024));
        var buffer = new byte[8 * 1024];
        var totalBytes = 0;
        while (true)
        {
            var read = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes - totalBytes + 1)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > maximumBytes)
            {
                throw new JsonException(
                    $"The response body exceeds the {maximumBytes}-byte bounded envelope.");
            }
            body.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(body.GetBuffer(), 0, totalBytes);
    }

    private static string BuildEndpoint(
        string path,
        IReadOnlyList<KeyValuePair<string, string>> parameters) =>
        parameters.Count == 0
            ? path
            : path + "?" + string.Join(
                "&",
                parameters.Select(parameter =>
                    $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));

    private static void AddMany(
        ICollection<KeyValuePair<string, string>> parameters,
        string name,
        IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            parameters.Add(new(name, value));
        }
    }

    private static void AddOptional(
        ICollection<KeyValuePair<string, string>> parameters,
        string name,
        string? value)
    {
        if (value is not null)
        {
            parameters.Add(new(name, value));
        }
    }

    private static void RequireResponseContractVersion(string? contractVersion) =>
        NewMesIngestContract.RequireExactCompatibility(
            contractVersion,
            NewMesIngestContract.SchemaVersion,
            NewMesIngestContract.Capabilities.Select(capability =>
                new NewMesIngestCapabilityIdentity(capability.Id, capability.Version)));

    private async Task DiscoverAndRequireExactContractAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ContractEndpoint);
            request.Headers.TryAddWithoutValidation(LatencyHeaders.CorrelationId, correlationId);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new WatchEndpointFetchException(
                    ContractEndpoint,
                    LatencyStages.HttpStatus,
                    stopwatch.Elapsed,
                    $"{NewMesIngestContractMismatchException.ErrorCode}: Host has no {ContractEndpoint}. "
                    + "Upgrade Host and Watch together.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = string.IsNullOrWhiteSpace(body)
                    ? string.Empty
                    : $" Host response: {body}";
                throw new HttpRequestException(
                    $"Response status code does not indicate success: {(int)response.StatusCode} "
                    + $"({response.ReasonPhrase}).{detail}",
                    inner: null,
                    response.StatusCode);
            }

            var contract = JsonSerializer.Deserialize<ContractDiscoveryPayload>(body, JsonOptions)
                ?? throw new JsonException("Contract discovery response was empty.");

            NewMesIngestContract.RequireExactCompatibility(
                contract.ContractVersion,
                contract.SchemaVersion,
                contract.Capabilities?.Select(capability =>
                    new NewMesIngestCapabilityIdentity(
                        capability?.Id ?? string.Empty,
                        capability?.Version ?? string.Empty)));
        }
        catch (WatchEndpointFetchException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NewMesIngestContractMismatchException ex)
        {
            throw new WatchEndpointFetchException(
                ContractEndpoint,
                LatencyStages.HttpStatus,
                stopwatch.Elapsed,
                $"{ex.Code}: {ex.Message}");
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            throw new WatchEndpointFetchException(
                ContractEndpoint,
                WatchHttpStageClassifier.Classify(ex),
                stopwatch.Elapsed,
                ex);
        }
    }

    private string Redact(string value) =>
        string.IsNullOrEmpty(_sensitiveValue)
            ? LatencyLogFormatter.Sanitize(value)
            : LatencyLogFormatter.Sanitize(
                value.Replace(_sensitiveValue, "(masked)", StringComparison.Ordinal));

    private sealed record ContractDiscoveryPayload(
        string? ContractVersion,
        int SchemaVersion,
        IReadOnlyList<ContractCapabilityPayload?>? Capabilities);

    private sealed record ContractCapabilityPayload(string? Id, string? Version);

    private sealed record V2ErrorPayload(string? Code, string? Error);
}
