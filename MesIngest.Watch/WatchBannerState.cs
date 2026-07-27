namespace MesIngest.Watch;

internal sealed record WatchBannerState(
    bool ShowFetchFailure,
    string? FetchFailureMessage,
    bool ShowPausedZeroDrop,
    IReadOnlyList<string> PausedTaskTypes)
{
    /// <summary>
    /// Banner state from the latest poll health and optional HTTP fetch error.
    /// PAUSED_ZERO_DROP uses current <see cref="WatchPollHealthDto.TaskTypePauses"/> only —
    /// historical alerts are not used (pause can auto-clear while alerts remain).
    /// Null health with no fetch error means poll-health is not ready yet (e.g. Host 404
    /// before the first poll) — still show a prominent banner so an empty board is not
    /// mistaken for “no work”.
    /// </summary>
    public static WatchBannerState From(WatchPollHealthDto? health, string? fetchError)
    {
        string? failureMessage = null;
        if (!string.IsNullOrWhiteSpace(fetchError))
        {
            failureMessage = $"HTTP fetch failed — {fetchError}";
        }
        else if (health is null)
        {
            failureMessage =
                "Not ready — no poll-health yet (first poll round not completed).";
        }
        else if (health is { Success: false })
        {
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

        return new WatchBannerState(
            ShowFetchFailure: failureMessage is not null,
            FetchFailureMessage: failureMessage,
            ShowPausedZeroDrop: paused.Count > 0,
            PausedTaskTypes: paused);
    }
}
