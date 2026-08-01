using MesIngest.Watch;

namespace MesIngest.Tests;

public class WatchStatusBarStateTests
{
    private static readonly TimeZoneInfo Tz =
        TimeZoneInfo.CreateCustomTimeZone("Test+08", TimeSpan.FromHours(8), "Test+08", "Test+08");

    [Fact]
    public void Compact_line_shows_host_poll_and_watch_refresh_separately()
    {
        var health = HealthyPoll(endedAt: DateTimeOffset.Parse("2026-08-01T02:00:10Z"));
        var refresh = WatchRefreshState.Empty.ApplySuccess(DateTimeOffset.Parse("2026-08-01T02:01:00Z"));
        var now = DateTimeOffset.Parse("2026-08-01T02:01:30Z");

        var state = WatchStatusBarState.Project(
            health,
            refresh,
            alerts: [],
            baseUrl: "http://factory-host:5088/",
            now: now,
            timeZone: Tz);

        Assert.Contains("watchLastSuccess=", state.CompactLine, StringComparison.Ordinal);
        Assert.Contains("hostPollEnd=", state.CompactLine, StringComparison.Ordinal);
        Assert.Contains("outcome=SUCCESS", state.CompactLine, StringComparison.Ordinal);
        Assert.Contains("rows=12", state.CompactLine, StringComparison.Ordinal);
        Assert.Contains("durationMs=3000", state.CompactLine, StringComparison.Ordinal);
        Assert.DoesNotContain("http://factory-host:5088/", state.CompactLine, StringComparison.Ordinal);
        Assert.Contains("http://factory-host:5088/", state.Tooltip, StringComparison.Ordinal);
        Assert.Contains("hostPollStart=", state.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_active_alerts_and_paused_types_from_current_state()
    {
        var health = HealthyPoll(endedAt: DateTimeOffset.Parse("2026-08-01T02:00:10Z")) with
        {
            TaskTypePauses =
            [
                new WatchTaskTypePauseDto("DIE_TO_OVEN", true, 15, 0),
                new WatchTaskTypePauseDto("WIRE_TO_GATE", false, 3, 0),
            ],
        };
        var alerts = new[]
        {
            Alert("a1", "ERROR", isActive: true),
            Alert("a2", "WARNING", isActive: true),
            Alert("a3", "ERROR", isActive: false),
        };

        var state = WatchStatusBarState.Project(
            health,
            WatchRefreshState.Empty,
            alerts,
            baseUrl: "http://localhost:5088/",
            now: DateTimeOffset.Parse("2026-08-01T02:00:10Z"),
            timeZone: Tz);

        Assert.Equal(2, state.ActiveAlertCount);
        Assert.Equal(1, state.PausedTypeCount);
        Assert.Contains("activeAlerts=2", state.CompactLine, StringComparison.Ordinal);
        Assert.Contains("paused=1", state.CompactLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_connection_and_fetch_error_live_in_tooltip_not_compact_growth()
    {
        var refresh = WatchRefreshState.Empty
            .ApplySuccess(DateTimeOffset.Parse("2026-08-01T02:00:00Z"))
            .ApplyFailure(
                "endpoint=/api/alerts stage=WATCH_TIMEOUT timeoutSeconds=30 elapsedMs=30000 correlationId=abc");
        var now = DateTimeOffset.Parse("2026-08-01T02:05:00Z");

        var state = WatchStatusBarState.Project(
            health: null,
            refresh,
            alerts: [],
            baseUrl: "http://very-long-factory-host.example.local:5088/mes-ingest/",
            now: now,
            timeZone: Tz);

        Assert.Contains("stale=", state.CompactLine, StringComparison.Ordinal);
        Assert.Contains("5m", state.CompactLine, StringComparison.Ordinal);
        Assert.Contains("connection=stale", state.CompactLine, StringComparison.Ordinal);
        Assert.Contains("/api/alerts", state.Tooltip, StringComparison.Ordinal);
        Assert.Contains("WATCH_TIMEOUT", state.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("correlationId=abc", state.CompactLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_message_appears_briefly_on_status_bar()
    {
        var state = WatchStatusBarState.Project(
            HealthyPoll(endedAt: DateTimeOffset.Parse("2026-08-01T02:00:10Z")),
            WatchRefreshState.Empty.ApplySuccess(DateTimeOffset.Parse("2026-08-01T02:00:10Z")),
            alerts: [],
            baseUrl: "http://localhost:5088/",
            now: DateTimeOffset.Parse("2026-08-01T02:00:10Z"),
            timeZone: Tz,
            recoveryMessage: "已恢复");

        Assert.Contains("已恢复", state.CompactLine, StringComparison.Ordinal);
    }

    private static WatchPollHealthDto HealthyPoll(DateTimeOffset endedAt) =>
        new(
            StartedAt: endedAt.AddSeconds(-3),
            EndedAt: endedAt,
            DurationMs: 3000,
            RowCount: 12,
            Success: true,
            Outcome: "SUCCESS",
            TaskTypePauses: []);

    private static WatchAlertDto Alert(string id, string severity, bool isActive) =>
        new(
            AlertId: id,
            Code: severity == "WARNING" ? "REAPPEAR_AFTER_GONE" : "POLL_FAILURE",
            Severity: severity,
            TaskType: "DIE_TO_OVEN",
            Sublot: "S1",
            DemandId: null,
            Message: "x",
            Details: null,
            FirstSeenAt: DateTimeOffset.Parse("2026-08-01T01:00:00Z"),
            LastSeenAt: DateTimeOffset.Parse("2026-08-01T01:00:00Z"),
            OccurrenceCount: 1,
            IsActive: isActive,
            ResolvedAt: isActive ? null : DateTimeOffset.Parse("2026-08-01T01:05:00Z"),
            CreatedAt: DateTimeOffset.Parse("2026-08-01T01:00:00Z"));
}
