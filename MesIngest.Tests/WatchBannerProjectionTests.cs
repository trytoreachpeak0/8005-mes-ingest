using MesIngest.Watch;

namespace MesIngest.Tests;

public class WatchBannerProjectionTests
{
    private static readonly TimeSpan MinHold = TimeSpan.FromSeconds(5);

    [Fact]
    public void Not_ready_is_error_severity_red()
    {
        var projected = WatchBannerProjection.Project(
            WatchBannerHoldState.Empty,
            health: null,
            fetchError: null,
            alerts: [],
            now: T(0));

        Assert.True(projected.ShowError);
        Assert.Equal("ERROR", projected.ErrorSeverity);
        Assert.Contains("not ready", projected.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(projected.ShowWarning);
    }

    [Fact]
    public void Timeout_fetch_error_is_error_banner()
    {
        var projected = WatchBannerProjection.Project(
            WatchBannerHoldState.Empty,
            health: null,
            fetchError: "endpoint=/api/demands stage=WATCH_TIMEOUT timeoutSeconds=30 elapsedMs=30000",
            alerts: [],
            now: T(0));

        Assert.True(projected.ShowError);
        Assert.Contains("WATCH_TIMEOUT", projected.ErrorMessage, StringComparison.Ordinal);
        Assert.False(projected.ShowWarning);
    }

    [Fact]
    public void Poll_failure_is_error_banner()
    {
        var health = new WatchPollHealthDto(
            StartedAt: T(0),
            EndedAt: T(30),
            DurationMs: 30000,
            RowCount: 0,
            Success: false,
            Outcome: "FAILURE",
            TaskTypePauses: []);

        var projected = WatchBannerProjection.Project(
            WatchBannerHoldState.Empty,
            health,
            fetchError: null,
            alerts: [],
            now: T(30));

        Assert.True(projected.ShowError);
        Assert.Contains("Poll failure", projected.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Pause_uses_current_pause_flags_not_resolved_alert_history()
    {
        var health = Healthy() with
        {
            TaskTypePauses = [new WatchTaskTypePauseDto("DIE_TO_OVEN", false, 12, 2)],
        };
        var alerts = new[]
        {
            new WatchAlertDto(
                AlertId: "old",
                Code: "PAUSED_ZERO_DROP",
                Severity: "ERROR",
                TaskType: "DIE_TO_OVEN",
                Sublot: null,
                DemandId: null,
                Message: "paused",
                Details: null,
                FirstSeenAt: T(-60),
                LastSeenAt: T(-30),
                OccurrenceCount: 3,
                IsActive: false,
                ResolvedAt: T(-10),
                CreatedAt: T(-60)),
        };

        var projected = WatchBannerProjection.Project(
            WatchBannerHoldState.Empty,
            health,
            fetchError: null,
            alerts,
            now: T(0));

        Assert.False(projected.ShowError);
        Assert.False(projected.ShowWarning);
        Assert.Null(projected.RecoveryMessage);
    }

    [Fact]
    public void Active_pause_is_error_not_warning()
    {
        var health = Healthy() with
        {
            TaskTypePauses = [new WatchTaskTypePauseDto("DIE_TO_OVEN", true, 15, 0)],
        };

        var projected = WatchBannerProjection.Project(
            WatchBannerHoldState.Empty,
            health,
            fetchError: null,
            alerts: [],
            now: T(0));

        Assert.True(projected.ShowError);
        Assert.Equal("ERROR", projected.ErrorSeverity);
        Assert.Contains("DIE_TO_OVEN", projected.ErrorMessage, StringComparison.Ordinal);
        Assert.False(projected.ShowWarning);
    }

    [Fact]
    public void Active_warning_alert_stays_in_alerts_grid_not_notification_banner()
    {
        var alerts = new[]
        {
            new WatchAlertDto(
                AlertId: "w1",
                Code: "REAPPEAR_AFTER_GONE",
                Severity: "WARNING",
                TaskType: "DIE_TO_OVEN",
                Sublot: "S1",
                DemandId: "deadbeef",
                Message: "reappeared",
                Details: null,
                FirstSeenAt: T(0),
                LastSeenAt: T(0),
                OccurrenceCount: 1,
                IsActive: true,
                ResolvedAt: null,
                CreatedAt: T(0)),
        };

        var projected = WatchBannerProjection.Project(
            WatchBannerHoldState.Empty,
            Healthy(),
            fetchError: null,
            alerts,
            now: T(0));

        Assert.False(projected.ShowError);
        Assert.False(projected.ShowWarning);
    }

    [Fact]
    public void Banner_holds_at_least_five_seconds_after_condition_clears()
    {
        var paused = Healthy() with
        {
            TaskTypePauses = [new WatchTaskTypePauseDto("DIE_TO_OVEN", true, 15, 0)],
        };
        var first = WatchBannerProjection.Project(
            WatchBannerHoldState.Empty,
            paused,
            fetchError: null,
            alerts: [],
            now: T(0),
            minHold: MinHold);

        var clearedEarly = WatchBannerProjection.Project(
            first.HoldState,
            Healthy(),
            fetchError: null,
            alerts: [],
            now: T(3),
            minHold: MinHold);

        Assert.True(clearedEarly.ShowError);
        Assert.Contains("DIE_TO_OVEN", clearedEarly.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(clearedEarly.RecoveryMessage);

        var afterHold = WatchBannerProjection.Project(
            clearedEarly.HoldState,
            Healthy(),
            fetchError: null,
            alerts: [],
            now: T(6),
            minHold: MinHold);

        Assert.False(afterHold.ShowError);
        Assert.Equal("已恢复", afterHold.RecoveryMessage);
    }

    [Fact]
    public void Condition_that_persists_keeps_banner_without_recovery()
    {
        var paused = Healthy() with
        {
            TaskTypePauses = [new WatchTaskTypePauseDto("DIE_TO_OVEN", true, 15, 0)],
        };
        var first = WatchBannerProjection.Project(
            WatchBannerHoldState.Empty,
            paused,
            fetchError: null,
            alerts: [],
            now: T(0),
            minHold: MinHold);
        var still = WatchBannerProjection.Project(
            first.HoldState,
            paused,
            fetchError: null,
            alerts: [],
            now: T(30),
            minHold: MinHold);

        Assert.True(still.ShowError);
        Assert.Null(still.RecoveryMessage);
    }

    [Fact]
    public void Stale_fetch_error_keeps_error_banner_while_present()
    {
        var projected = WatchBannerProjection.Project(
            WatchBannerHoldState.Empty,
            Healthy(),
            fetchError: "endpoint=/api/alerts stage=HTTP_CONNECT timeoutSeconds=30 elapsedMs=12",
            alerts: [],
            now: T(0));

        Assert.True(projected.ShowError);
        Assert.Contains("/api/alerts", projected.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Active_error_alert_stays_in_alerts_grid_not_notification_banner()
    {
        var alerts = new[]
        {
            new WatchAlertDto(
                AlertId: "e1",
                Code: "FIELD_DRIFT",
                Severity: "ERROR",
                TaskType: "DIE_TO_OVEN",
                Sublot: "S1",
                DemandId: "deadbeef",
                Message: "drift",
                Details: null,
                FirstSeenAt: T(0),
                LastSeenAt: T(0),
                OccurrenceCount: 1,
                IsActive: true,
                ResolvedAt: null,
                CreatedAt: T(0)),
        };

        var projected = WatchBannerProjection.Project(
            WatchBannerHoldState.Empty,
            Healthy(),
            fetchError: null,
            alerts,
            now: T(0));

        Assert.False(projected.ShowError);
        Assert.False(projected.ShowWarning);
    }

    [Fact]
    public void Alert_does_not_block_recovery_after_fault_banner_clears()
    {
        var paused = Healthy() with
        {
            TaskTypePauses = [new WatchTaskTypePauseDto("DIE_TO_OVEN", true, 15, 0)],
        };
        var warningAlerts = new[]
        {
            new WatchAlertDto(
                AlertId: "w1",
                Code: "REAPPEAR_AFTER_GONE",
                Severity: "WARNING",
                TaskType: "DIE_TO_OVEN",
                Sublot: "S1",
                DemandId: "deadbeef",
                Message: "reappeared",
                Details: null,
                FirstSeenAt: T(0),
                LastSeenAt: T(0),
                OccurrenceCount: 1,
                IsActive: true,
                ResolvedAt: null,
                CreatedAt: T(0)),
        };

        var pausedWithAlert = WatchBannerProjection.Project(
            WatchBannerHoldState.Empty,
            paused,
            fetchError: null,
            warningAlerts,
            now: T(0),
            minHold: MinHold);

        var pauseCleared = WatchBannerProjection.Project(
            pausedWithAlert.HoldState,
            Healthy(),
            fetchError: null,
            warningAlerts,
            now: T(6),
            minHold: MinHold);

        Assert.False(pauseCleared.ShowError);
        Assert.False(pauseCleared.ShowWarning);
        Assert.Equal("已恢复", pauseCleared.RecoveryMessage);
    }

    [Fact]
    public void Fetch_error_key_stays_stable_when_elapsed_and_correlation_change()
    {
        var first = WatchBannerState.From(
            Healthy(),
            "endpoint=/api/alerts stage=WATCH_TIMEOUT timeoutSeconds=30 elapsedMs=30000 correlationId=aaa");
        var second = WatchBannerState.From(
            Healthy(),
            "endpoint=/api/alerts stage=WATCH_TIMEOUT timeoutSeconds=30 elapsedMs=30110 correlationId=bbb");

        Assert.Equal(first.ErrorKey, second.ErrorKey);
        Assert.Equal("fetch:/api/alerts:WATCH_TIMEOUT", first.ErrorKey);
    }

    private static DateTimeOffset T(int seconds) =>
        DateTimeOffset.Parse("2026-08-01T02:00:00Z").AddSeconds(seconds);

    private static WatchPollHealthDto Healthy() =>
        new(
            StartedAt: T(0),
            EndedAt: T(3),
            DurationMs: 3000,
            RowCount: 12,
            Success: true,
            Outcome: "SUCCESS",
            TaskTypePauses: []);
}
