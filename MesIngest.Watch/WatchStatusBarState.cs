using System.Globalization;
using System.Text;

namespace MesIngest.Watch;

/// <summary>
/// Bottom status-bar projection: compact line + tooltip for long details.
/// </summary>
internal sealed record WatchStatusBarState(
    string CompactLine,
    string Tooltip,
    int ActiveAlertCount,
    int PausedTypeCount)
{
    public static WatchStatusBarState Project(
        WatchPollHealthDto? health,
        WatchRefreshState refresh,
        IReadOnlyList<WatchAlertDto> alerts,
        string baseUrl,
        DateTimeOffset now,
        TimeZoneInfo? timeZone = null,
        string? recoveryMessage = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var activeAlerts = alerts.Count(a => a.IsActive);
        var paused = health?.TaskTypePauses.Count(p => p.PausedZeroDrop) ?? 0;
        var connection = refresh.FetchError is null
            ? (refresh.LastSuccessAt is null ? "pending" : "ok")
            : (refresh.LastSuccessAt is null ? "down" : "stale");

        var compact = new StringBuilder();
        compact.Append("connection=").Append(connection);
        if (health is null)
        {
            compact.Append(" outcome=(none) rows=(n/a) durationMs=(n/a) hostPollEnd=(none)");
        }
        else
        {
            compact.Append(" outcome=").Append(health.Outcome)
                .Append(" rows=").Append(health.RowCount)
                .Append(" durationMs=").Append(health.DurationMs.ToString("0", CultureInfo.InvariantCulture))
                .Append(" hostPollEnd=").Append(WatchTimeDisplay.Format(health.EndedAt, zone));
        }

        compact.Append(' ').Append(FormatWatchSuccess(refresh, now, zone));
        compact.Append(" timezone=").Append(FormatTimezone(zone, now));
        compact.Append(" activeAlerts=").Append(activeAlerts);
        compact.Append(" paused=").Append(paused);
        if (!string.IsNullOrWhiteSpace(recoveryMessage))
        {
            compact.Append(' ').Append(recoveryMessage.Trim());
        }

        var tooltip = new StringBuilder();
        tooltip.Append("BaseUrl=").Append(baseUrl);
        if (health is not null)
        {
            tooltip.AppendLine()
                .Append("hostPollStart=").Append(WatchTimeDisplay.Format(health.StartedAt, zone));
            if (!string.IsNullOrWhiteSpace(health.FailureStage))
            {
                tooltip.AppendLine()
                    .Append("failureStage=").Append(health.FailureStage);
            }
        }

        if (!string.IsNullOrWhiteSpace(refresh.FetchError))
        {
            tooltip.AppendLine().Append("fetchError=").Append(refresh.FetchError);
        }

        return new WatchStatusBarState(
            CompactLine: compact.ToString(),
            Tooltip: tooltip.ToString(),
            ActiveAlertCount: activeAlerts,
            PausedTypeCount: paused);
    }

    private static string FormatWatchSuccess(
        WatchRefreshState refresh,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        if (refresh.LastSuccessAt is null)
        {
            return "watchLastSuccess=(none) stale=(n/a)";
        }

        var stale = refresh.StaleDuration(now) ?? TimeSpan.Zero;
        return $"watchLastSuccess={WatchTimeDisplay.Format(refresh.LastSuccessAt.Value, zone)} stale={FormatDuration(stale)}";
    }

    private static string FormatTimezone(TimeZoneInfo zone, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        return $"{zone.Id} (UTC{local:zzz})";
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
