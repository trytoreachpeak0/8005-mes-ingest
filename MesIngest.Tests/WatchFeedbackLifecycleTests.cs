using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchFeedbackLifecycleTests
{
    [Fact]
    public void First_fault_repeats_recovery_and_reoccurrence_form_distinct_notification_cycles()
    {
        var lifecycle = new WatchFeedbackLifecycle();
        var fault = Fault("overview.refresh", WatchWorkspacePage.Overview);

        var first = lifecycle.Update([fault]);
        var repeated = lifecycle.Update([fault with { Message = "latest controlled detail" }]);
        var recovered = lifecycle.Update([]);
        var reoccurred = lifecycle.Update([fault]);

        Assert.Equal([fault], first.Started);
        Assert.Empty(repeated.Started);
        Assert.Empty(repeated.Recovered);
        Assert.Single(repeated.Active);
        Assert.Equal("latest controlled detail", repeated.Active[0].Message);
        Assert.Equal([fault with { Message = "latest controlled detail" }], recovered.Recovered);
        Assert.Equal([fault], reoccurred.Started);
    }

    [Fact]
    public void Background_summary_contains_only_new_faults_that_are_still_active_and_is_emitted_once()
    {
        var lifecycle = new WatchFeedbackLifecycle();
        var existing = Fault("host.connection", page: null);
        var recoveredInBackground = Fault("overview.refresh", WatchWorkspacePage.Overview);
        var stillActive = Fault("error-search.refresh", WatchWorkspacePage.ErrorSearch);
        lifecycle.Update([existing]);
        lifecycle.SetForeground(false);

        var backgroundChange = lifecycle.Update(
            [existing, recoveredInBackground, stillActive]);
        lifecycle.Update([existing, stillActive]);
        var resumed = lifecycle.SetForeground(true);
        var resumedAgain = lifecycle.SetForeground(true);

        Assert.Empty(backgroundChange.Started);
        Assert.Empty(backgroundChange.Recovered);
        Assert.Equal([stillActive], resumed.ForegroundSummary);
        Assert.Empty(resumedAgain.ForegroundSummary);
        Assert.Contains(existing, resumed.Active);
    }

    [Fact]
    public void Page_and_global_fault_scopes_remain_part_of_stable_active_state()
    {
        var lifecycle = new WatchFeedbackLifecycle();
        var global = Fault("host.connection", page: null);
        var page = Fault("readability.refresh", WatchWorkspacePage.ReadabilityAudit);

        var change = lifecycle.Update([global, page]);

        Assert.Contains(change.Active, item => item.Scope.IsGlobal);
        Assert.Contains(
            change.Active,
            item => item.Scope.Page == WatchWorkspacePage.ReadabilityAudit);
    }

    [Fact]
    public void Host_generation_reset_discards_old_fault_cycles_without_reporting_false_recovery()
    {
        var lifecycle = new WatchFeedbackLifecycle();
        lifecycle.Update([Fault("old-host.overview", WatchWorkspacePage.Overview)]);

        var reset = lifecycle.Reset([]);
        var newHostFailure = lifecycle.Update([Fault("host.connection", page: null)]);

        Assert.Empty(reset.Started);
        Assert.Empty(reset.Recovered);
        Assert.Empty(reset.Active);
        Assert.Single(newHostFailure.Started);
        Assert.Equal("host.connection", newHostFailure.Started[0].SourceKey);
    }

    private static WatchContinuingFault Fault(
        string key,
        WatchWorkspacePage? page) => new(
            key,
            page is null
                ? WatchNotificationScope.Global
                : WatchNotificationScope.ForPage(page.Value),
            WatchNotificationSeverity.Error,
            $"{key} failed",
            "controlled detail",
            page is null ? "打开设置" : "查看页面");
}
