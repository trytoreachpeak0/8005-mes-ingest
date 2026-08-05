namespace MesIngest.Watch;

internal sealed record WatchBannerState(
    bool ShowFetchFailure,
    string? FetchFailureMessage,
    bool ShowPausedZeroDrop,
    IReadOnlyList<string> PausedTaskTypes,
    string? ErrorKey = null,
    string? ErrorMessage = null,
    string? WarningKey = null,
    string? WarningMessage = null)
{
    /// <summary>
    /// Banner conditions from latest poll health and an optional HTTP fetch error.
    /// PAUSED_ZERO_DROP uses current <see cref="WatchPollHealthDto.TaskTypePauses"/> only —
    /// Host alerts remain in the Alerts grid and never participate in notification banners.
    /// Null health with no fetch error means poll-health is not ready yet.
    /// </summary>
    public static WatchBannerState From(WatchPollHealthDto? health, string? fetchError) =>
        From(health, fetchError, alerts: []);

    public static WatchBannerState From(
        WatchPollHealthDto? health,
        string? fetchError,
        IReadOnlyList<WatchAlertDto> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);

        string? failureMessage = null;
        string? failureKey = null;
        if (!string.IsNullOrWhiteSpace(fetchError))
        {
            failureKey = StableFetchErrorKey(fetchError);
            failureMessage = $"HTTP fetch failed — {fetchError}";
        }
        else if (health is null)
        {
            failureKey = "not-ready";
            failureMessage =
                "Not ready — no poll-health yet (first poll round not completed).";
        }
        else if (health is { Success: false })
        {
            failureKey = "poll:" + health.Outcome;
            failureMessage =
                $"Poll failure — latest poll did not succeed (outcome={health.Outcome}).";
        }

        var paused = health?.TaskTypePauses
            .Where(p => p.PausedZeroDrop && !string.IsNullOrWhiteSpace(p.TaskType))
            .Select(p => p.TaskType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];

        string? errorKey = failureKey;
        string? errorMessage = failureMessage;
        if (paused.Count > 0)
        {
            var pauseKey = "pause:" + string.Join(",", paused);
            var pauseMessage = $"PAUSED_ZERO_DROP — types: {string.Join(", ", paused)}";
            if (errorKey is null)
            {
                errorKey = pauseKey;
                errorMessage = pauseMessage;
            }
            else
            {
                errorKey += "|" + pauseKey;
                errorMessage = failureMessage + " | " + pauseMessage;
            }
        }

        return new WatchBannerState(
            ShowFetchFailure: failureMessage is not null,
            FetchFailureMessage: failureMessage,
            ShowPausedZeroDrop: paused.Count > 0,
            PausedTaskTypes: paused,
            ErrorKey: errorKey,
            ErrorMessage: errorMessage,
            WarningKey: null,
            WarningMessage: null);
    }

    private static string StableFetchErrorKey(string fetchError)
    {
        // Keep identity stable across refreshes (ignore elapsedMs / correlationId churn).
        string? endpoint = null;
        string? stage = null;
        foreach (var part in fetchError.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("endpoint=", StringComparison.Ordinal))
            {
                endpoint = part["endpoint=".Length..];
            }
            else if (part.StartsWith("stage=", StringComparison.Ordinal))
            {
                stage = part["stage=".Length..];
            }
        }

        if (endpoint is not null || stage is not null)
        {
            return $"fetch:{endpoint ?? "(unknown)"}:{stage ?? "(unknown)"}";
        }

        return "fetch:" + fetchError;
    }
}
