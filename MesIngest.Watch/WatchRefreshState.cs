namespace MesIngest.Watch;

/// <summary>
/// Tracks last successful Watch refresh and stale duration across HTTP failures.
/// </summary>
internal sealed record WatchRefreshState(
    DateTimeOffset? LastSuccessAt,
    string? FetchError)
{
    public static WatchRefreshState Empty { get; } = new(null, null);

    public WatchRefreshState ApplySuccess(DateTimeOffset at) =>
        new(LastSuccessAt: at, FetchError: null);

    public WatchRefreshState ApplyFailure(string fetchError) =>
        new(LastSuccessAt: LastSuccessAt, FetchError: fetchError);

    public TimeSpan? StaleDuration(DateTimeOffset now) =>
        LastSuccessAt is { } success ? now - success : null;

    public string FormatWatchRefreshLine(DateTimeOffset now)
    {
        if (LastSuccessAt is null)
        {
            return "lastSuccess=(none) stale=(n/a)";
        }

        var stale = StaleDuration(now) ?? TimeSpan.Zero;
        return $"lastSuccess={WatchTimeDisplay.Format(LastSuccessAt.Value)} stale={FormatDuration(stale)}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}h{value.Minutes}m";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"{(int)value.TotalMinutes}m{value.Seconds}s";
        }

        return $"{(int)value.TotalSeconds}s";
    }
}
