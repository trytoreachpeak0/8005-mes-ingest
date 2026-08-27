using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchBilingualNotificationTests
{
    [Fact]
    public void Continuing_fault_reprojects_every_visible_field_from_the_typed_catalog()
    {
        var clock = new ManualTimerTimeProvider(DateTimeOffset.Parse("2026-08-27T09:00:00+08:00"));
        var language = new WatchDisplayLanguageState(WatchDisplayLanguage.SimplifiedChinese);
        using var coordinator = new WatchWindowNotificationCoordinator(clock, language);
        var localized = WatchFeedbackText.ContinuingFault(
            "readability.refresh",
            WatchHostFailureKind.Timeout);
        coordinator.Present(new WatchNotificationEvent(
            new WatchNotificationSource(
                "readability.refresh",
                WatchNotificationScope.ForPage(WatchWorkspacePage.ReadabilityAudit),
                "continuing"),
            WatchNotificationSeverity.Error,
            localized.SeverityText.SimplifiedChinese,
            localized.Title.SimplifiedChinese,
            localized.Message.SimplifiedChinese,
            localized.ActionLabel?.SimplifiedChinese,
            static () => { },
            LocalizedContent: localized));

        language.ApplyCommitted(WatchDisplayLanguage.English);

        var projected = coordinator.GetSnapshot().Single();
        Assert.Equal("Error", projected.SeverityText);
        Assert.Equal("Eligibility-audit read keeps failing", projected.Title);
        Assert.Contains("timed out", projected.Message, StringComparison.Ordinal);
        Assert.Equal("View page", projected.ActionLabel);
        Assert.DoesNotContain("持续失败", projected.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("查看页面", projected.ActionLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void Language_reprojection_keeps_occurrence_expiry_action_and_suppresses_announcement()
    {
        var clock = new ManualTimerTimeProvider(DateTimeOffset.Parse("2026-08-27T09:00:00+08:00"));
        var language = new WatchDisplayLanguageState(WatchDisplayLanguage.SimplifiedChinese);
        using var coordinator = new WatchWindowNotificationCoordinator(clock, language);
        var changes = new List<WatchNotificationSnapshotChangedEventArgs>();
        var actionCount = 0;
        coordinator.SnapshotChanged += (_, change) => changes.Add(change);

        coordinator.Present(new WatchNotificationEvent(
            new WatchNotificationSource(
                "test.localized",
                WatchNotificationScope.Global,
                "same-event"),
            WatchNotificationSeverity.Warning,
            "警告",
            "AREA 配置已更改",
            "当前显示范围仍保持不变。",
            "查看配置",
            () => actionCount++,
            WatchLocalizedNotificationContent.Create(
                severityText: ("警告", "Warning"),
                title: ("AREA 配置已更改", "AREA profile changed"),
                message: ("当前显示范围仍保持不变。", "The current display scope remains unchanged."),
                actionLabel: ("查看配置", "View profile"))));
        clock.Advance(TimeSpan.FromSeconds(2));
        var before = coordinator.GetSnapshot().Single();

        language.ApplyCommitted(WatchDisplayLanguage.English);

        var after = coordinator.GetSnapshot().Single();
        Assert.Equal(1, after.Occurrences);
        Assert.Equal(before.RemainingSeconds, after.RemainingSeconds);
        Assert.Equal("Warning", after.SeverityText);
        Assert.Equal("AREA profile changed", after.Title);
        Assert.Equal("The current display scope remains unchanged.", after.Message);
        Assert.Equal("View profile", after.ActionLabel);
        Assert.Equal("First occurrence", after.OccurrenceText);
        Assert.Equal($"{after.RemainingSeconds} seconds", after.TimerText);
        Assert.Null(changes.Last().Announcement);
        Assert.False(changes.Last().ShouldAnimate);

        coordinator.ExecuteAction(after.SourceKey);
        Assert.Equal(1, actionCount);
    }
}
