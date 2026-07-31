using MesIngest.Core;

namespace MesIngest.Tests;

public class AlertIncidentSyncTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset T1 = T0.AddMinutes(1);
    private static readonly DateTimeOffset T2 = T0.AddMinutes(2);

    [Fact]
    public void Same_identity_and_fingerprint_bumps_occurrence_instead_of_inserting()
    {
        var first = AlertIncidentSync.Apply(
            [],
            [PollFailure("stage=ORACLE_QUERY")],
            T0,
            IngestAlertCatalog.PollCodes,
            alertIdAllocator: () => "a1");

        var second = AlertIncidentSync.Apply(
            first,
            [PollFailure("stage=ORACLE_QUERY")],
            T1,
            IngestAlertCatalog.PollCodes,
            alertIdAllocator: () => "a2");

        var alert = Assert.Single(second);
        Assert.Equal("a1", alert.AlertId);
        Assert.Equal(2, alert.OccurrenceCount);
        Assert.Equal(T0, alert.FirstSeenAt);
        Assert.Equal(T1, alert.LastSeenAt);
        Assert.True(alert.IsActive);
        Assert.Equal(AlertSeverities.Error, alert.Severity);
    }

    [Fact]
    public void Fingerprint_change_resolves_old_and_creates_new_incident()
    {
        var first = AlertIncidentSync.Apply(
            [],
            [PollFailure("stage=ORACLE_QUERY")],
            T0,
            IngestAlertCatalog.PollCodes,
            alertIdAllocator: StaticIds("a1", "a2"));

        var second = AlertIncidentSync.Apply(
            first,
            [PollFailure("stage=SQL_TIMEOUT")],
            T1,
            IngestAlertCatalog.PollCodes,
            alertIdAllocator: StaticIds("a2"));

        Assert.Equal(2, second.Count);
        var resolved = Assert.Single(second, a => a.AlertId == "a1");
        Assert.False(resolved.IsActive);
        Assert.Equal(T1, resolved.ResolvedAt);

        var active = Assert.Single(second, a => a.IsActive);
        Assert.Equal("a2", active.AlertId);
        Assert.Equal(1, active.OccurrenceCount);
        Assert.Contains("SQL_TIMEOUT", active.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_observation_in_managed_scope_resolves_active_incident()
    {
        var first = AlertIncidentSync.Apply(
            [],
            [Paused("DIE_TO_OVEN")],
            T0,
            IngestAlertCatalog.SuccessRoundManagedCodes,
            alertIdAllocator: () => "p1");

        var second = AlertIncidentSync.Apply(
            first,
            [],
            T1,
            IngestAlertCatalog.SuccessRoundManagedCodes);

        var alert = Assert.Single(second);
        Assert.False(alert.IsActive);
        Assert.Equal(T1, alert.ResolvedAt);
        Assert.Equal(AlertSeverities.Error, alert.Severity);
    }

    [Fact]
    public void Failure_round_poll_scope_does_not_resolve_reconcile_incidents()
    {
        var existing = AlertIncidentSync.Apply(
            [],
            [Paused("DIE_TO_OVEN")],
            T0,
            IngestAlertCatalog.SuccessRoundManagedCodes,
            alertIdAllocator: () => "p1");

        var afterPoll = AlertIncidentSync.Apply(
            existing,
            [PollFailure("stage=ORACLE_QUERY")],
            T1,
            IngestAlertCatalog.PollCodes,
            alertIdAllocator: () => "f1");

        Assert.Contains(afterPoll, a => a.AlertId == "p1" && a.IsActive);
        Assert.Contains(afterPoll, a => a.AlertId == "f1" && a.IsActive);
    }

    [Fact]
    public void Resolved_incidents_past_retention_are_purged_active_never()
    {
        var ids = StaticIds("a1", "a2");
        var first = AlertIncidentSync.Apply(
            [],
            [PollFailure("stage=ORACLE_QUERY")],
            T0,
            IngestAlertCatalog.PollCodes,
            alertIdAllocator: ids);

        var resolved = AlertIncidentSync.Apply(
            first,
            [],
            T0.AddDays(1),
            IngestAlertCatalog.PollCodes);

        var kept = AlertIncidentSync.Apply(
            resolved,
            [PollFailure("stage=ORACLE_QUERY")],
            T0.AddDays(400),
            IngestAlertCatalog.PollCodes,
            resolvedRetention: TimeSpan.FromDays(365),
            alertIdAllocator: ids);

        Assert.DoesNotContain(kept, a => a.AlertId == "a1");
        Assert.Contains(kept, a => a.IsActive && a.Code == AlertCodes.PollFailure);
    }

    [Fact]
    public void Reappear_is_warning_severity()
    {
        var result = AlertIncidentSync.Apply(
            [],
            [
                new IngestAlert(
                    Code: AlertCodes.ReappearAfterGone,
                    TaskType: "T",
                    Sublot: "S",
                    DemandId: "d2",
                    Message: "reappear",
                    Details: AlertDetailsBuilder.Reappear("d1", "d2")),
            ],
            T0,
            IngestAlertCatalog.ReconcileCodes,
            alertIdAllocator: () => "r1");

        Assert.Equal(AlertSeverities.Warning, Assert.Single(result).Severity);
    }

    [Fact]
    public void Zero_retention_keeps_resolved_forever()
    {
        var first = AlertIncidentSync.Apply(
            [],
            [PollFailure("x")],
            T0,
            IngestAlertCatalog.PollCodes,
            alertIdAllocator: () => "a1");

        var resolved = AlertIncidentSync.Apply(
            first,
            [],
            T1,
            IngestAlertCatalog.PollCodes);

        var kept = AlertIncidentSync.Apply(
            resolved,
            [],
            T0.AddDays(900),
            IngestAlertCatalog.PollCodes,
            resolvedRetention: TimeSpan.Zero);

        Assert.Single(kept);
        Assert.False(kept[0].IsActive);
    }

    [Fact]
    public void Poll_duration_change_does_not_split_incident()
    {
        var first = AlertIncidentSync.Apply(
            [],
            [PollFailure("stage=ORACLE_QUERY", durationMs: 10)],
            T0,
            IngestAlertCatalog.PollCodes,
            alertIdAllocator: () => "a1");

        var second = AlertIncidentSync.Apply(
            first,
            [PollFailure("stage=ORACLE_QUERY", durationMs: 999)],
            T1,
            IngestAlertCatalog.PollCodes,
            alertIdAllocator: () => "a2");

        var alert = Assert.Single(second);
        Assert.Equal("a1", alert.AlertId);
        Assert.Equal(2, alert.OccurrenceCount);
        Assert.Contains("999", alert.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Paused_recovery_streak_change_keeps_same_incident()
    {
        var first = AlertIncidentSync.Apply(
            [],
            [Paused("DIE_TO_OVEN", recoveryStreak: 0)],
            T0,
            IngestAlertCatalog.SuccessRoundManagedCodes,
            alertIdAllocator: () => "p1");

        var second = AlertIncidentSync.Apply(
            first,
            [Paused("DIE_TO_OVEN", recoveryStreak: 1)],
            T1,
            IngestAlertCatalog.SuccessRoundManagedCodes,
            alertIdAllocator: () => "p2");

        var alert = Assert.Single(second);
        Assert.Equal("p1", alert.AlertId);
        Assert.True(alert.IsActive);
        Assert.Equal(2, alert.OccurrenceCount);
        Assert.Contains("\"recoveryStreak\":1", alert.Details, StringComparison.Ordinal);
    }

    private static IngestAlert PollFailure(string stage, long durationMs = 12) =>
        new(
            Code: AlertCodes.PollFailure,
            Message: $"failed {stage}",
            Details: AlertDetailsBuilder.Poll(stage, durationMs, 0, $"failed {stage}", timeoutSeconds: 30),
            DetailsFingerprint: AlertDetailsBuilder.PollFingerprint(stage, $"failed {stage}", 30));

    private static IngestAlert Paused(string taskType, int recoveryStreak = 0) =>
        new(
            Code: AlertCodes.PausedZeroDrop,
            TaskType: taskType,
            Message: "paused",
            Details: AlertDetailsBuilder.PausedZeroDrop(12, recoveryStreak, 10, 3),
            DetailsFingerprint: AlertDetailsBuilder.PausedZeroDropFingerprint(12, 10, 3));

    private static Func<string> StaticIds(params string[] ids)
    {
        var q = new Queue<string>(ids);
        return () => q.Count > 0 ? q.Dequeue() : Guid.NewGuid().ToString("N");
    }
}
