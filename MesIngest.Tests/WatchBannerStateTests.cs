using MesIngest.Watch;

namespace MesIngest.Tests;

public class WatchBannerStateTests
{
    [Fact]
    public void No_banner_when_fetch_ok_health_success_and_no_pause()
    {
        var health = new WatchPollHealthDto(
            StartedAt: DateTimeOffset.Parse("2026-08-02T10:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-08-02T10:00:03Z"),
            DurationMs: 3000,
            RowCount: 12,
            Success: true,
            Outcome: "SUCCESS",
            TaskTypePauses: Array.Empty<WatchTaskTypePauseDto>());

        var state = WatchBannerState.From(health, fetchError: null);

        Assert.False(state.ShowFetchFailure);
        Assert.False(state.ShowPausedZeroDrop);
        Assert.Empty(state.PausedTaskTypes);
        Assert.Null(state.FetchFailureMessage);
    }

    [Fact]
    public void Shows_fetch_failure_banner_when_http_client_reports_error()
    {
        var state = WatchBannerState.From(health: null, fetchError: "Connection refused");

        Assert.True(state.ShowFetchFailure);
        Assert.Contains("HTTP", state.FetchFailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection refused", state.FetchFailureMessage, StringComparison.Ordinal);
        Assert.False(state.ShowPausedZeroDrop);
    }

    [Fact]
    public void Shows_not_ready_banner_when_health_null_and_no_fetch_error()
    {
        var state = WatchBannerState.From(health: null, fetchError: null);

        Assert.True(state.ShowFetchFailure);
        Assert.Contains("not ready", state.FetchFailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("poll-health", state.FetchFailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HTTP", state.FetchFailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(state.ShowPausedZeroDrop);
    }

    [Fact]
    public void Shows_fetch_failure_when_poll_health_success_is_false()
    {
        var health = new WatchPollHealthDto(
            StartedAt: DateTimeOffset.Parse("2026-08-02T10:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-08-02T10:00:30Z"),
            DurationMs: 30000,
            RowCount: 0,
            Success: false,
            Outcome: "FAILURE",
            TaskTypePauses: Array.Empty<WatchTaskTypePauseDto>());

        var state = WatchBannerState.From(health, fetchError: null);

        Assert.True(state.ShowFetchFailure);
        Assert.Contains("Poll failure", state.FetchFailureMessage, StringComparison.Ordinal);
        Assert.Contains("FAILURE", state.FetchFailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("not ready", state.FetchFailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HTTP", state.FetchFailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shows_paused_zero_drop_from_task_type_pauses()
    {
        var health = new WatchPollHealthDto(
            StartedAt: DateTimeOffset.Parse("2026-08-02T10:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-08-02T10:00:03Z"),
            DurationMs: 3000,
            RowCount: 0,
            Success: true,
            Outcome: "SUCCESS",
            TaskTypePauses:
            [
                new WatchTaskTypePauseDto("DIE_TO_OVEN", true, 15, 0),
                new WatchTaskTypePauseDto("DIE_TO_WIRE_STAGING", false, 3, 0),
            ]);

        var state = WatchBannerState.From(health, fetchError: null);

        Assert.True(state.ShowPausedZeroDrop);
        Assert.Equal(new[] { "DIE_TO_OVEN" }, state.PausedTaskTypes);
    }

    [Fact]
    public void Ignores_cleared_pause_flags_even_if_caller_still_has_old_alerts()
    {
        var health = new WatchPollHealthDto(
            StartedAt: DateTimeOffset.Parse("2026-08-02T10:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-08-02T10:00:03Z"),
            DurationMs: 3000,
            RowCount: 5,
            Success: true,
            Outcome: "SUCCESS",
            TaskTypePauses:
            [
                new WatchTaskTypePauseDto("WIRE_TO_NITROGEN", false, 12, 2),
            ]);

        var state = WatchBannerState.From(health, fetchError: null);

        Assert.False(state.ShowPausedZeroDrop);
        Assert.Empty(state.PausedTaskTypes);
    }

    [Fact]
    public void Shows_both_banners_together()
    {
        var health = new WatchPollHealthDto(
            StartedAt: DateTimeOffset.Parse("2026-08-02T10:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-08-02T10:00:03Z"),
            DurationMs: 3000,
            RowCount: 0,
            Success: false,
            Outcome: "INCOMPLETE",
            TaskTypePauses: [new WatchTaskTypePauseDto("DIE_TO_OVEN", true, 10, 0)]);

        var state = WatchBannerState.From(health, fetchError: null);

        Assert.True(state.ShowFetchFailure);
        Assert.True(state.ShowPausedZeroDrop);
        Assert.Equal(new[] { "DIE_TO_OVEN" }, state.PausedTaskTypes);
    }
}
