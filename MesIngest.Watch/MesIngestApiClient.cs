using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesIngest.Watch;

internal sealed class WatchOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:5088";
    public int RefreshSeconds { get; set; } = 2;
}

internal sealed class MesIngestApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;

    public MesIngestApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<WatchSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var demandsTask = _http.GetFromJsonAsync<List<WatchDemandDto>>("/api/demands", JsonOptions, cancellationToken);
            var alertsTask = _http.GetFromJsonAsync<List<WatchAlertDto>>("/api/alerts", JsonOptions, cancellationToken);
            var healthTask = FetchPollHealthAsync(cancellationToken);

            await Task.WhenAll(demandsTask, alertsTask, healthTask).ConfigureAwait(false);

            return new WatchSnapshot(
                Demands: await demandsTask.ConfigureAwait(false) ?? [],
                Alerts: await alertsTask.ConfigureAwait(false) ?? [],
                PollHealth: await healthTask.ConfigureAwait(false),
                FetchError: null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new WatchSnapshot(
                Demands: [],
                Alerts: [],
                PollHealth: null,
                FetchError: ex.Message);
        }
    }

    private async Task<WatchPollHealthDto?> FetchPollHealthAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/api/poll-health", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WatchPollHealthDto>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed record WatchSnapshot(
    IReadOnlyList<WatchDemandDto> Demands,
    IReadOnlyList<WatchAlertDto> Alerts,
    WatchPollHealthDto? PollHealth,
    string? FetchError);
