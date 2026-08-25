using System.Net.Http;
using System.Text.Json;
using MesIngest.Core;

namespace MesIngest.Watch;

internal enum WatchRenderingMode
{
    SoftwareOnly,
    Auto,
}

internal sealed class WatchOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:5088";
    public int RefreshSeconds { get; set; } = 2;
    public WatchRenderingMode RenderingMode { get; set; } = WatchRenderingMode.SoftwareOnly;

    /// <summary>
    /// HttpClient timeout for each Watch refresh request (seconds). Default 30; legal range 1–300.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Local WatchConnectionEvent JSONL retention in days. Default 15.
    /// </summary>
    public int ConnectionLogRetentionDays { get; set; } = 15;

    /// <summary>
    /// Local WatchConnectionEvent JSONL directory size cap in MiB. Default 100.
    /// </summary>
    public int ConnectionLogMaxSizeMb { get; set; } = 100;

    /// <summary>
    /// Same value as Host MesIngest:SharedSecret when API is bound beyond localhost.
    /// Sent as Authorization: Bearer.
    /// </summary>
    public string SharedSecret { get; set; } = "";

    /// <summary>
    /// Optional directory for connection and latency logs. Empty keeps the
    /// per-user LocalApplicationData location.
    /// </summary>
    public string LogDirectory { get; set; } = "";
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
