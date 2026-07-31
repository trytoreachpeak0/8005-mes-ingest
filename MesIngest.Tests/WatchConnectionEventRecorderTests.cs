using MesIngest.Watch;

namespace MesIngest.Tests;

public class WatchConnectionEventRecorderTests
{
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(5);

    [Fact]
    public void First_failure_emits_failure_event_immediately()
    {
        var recorder = new WatchConnectionEventRecorder(SummaryInterval);
        var at = DateTimeOffset.Parse("2026-07-31T10:00:00Z");

        var emitted = recorder.ObserveFailure(
            at,
            endpoint: "/api/demands",
            stage: "HTTP_TIMEOUT",
            elapsed: TimeSpan.FromSeconds(30),
            timeoutSeconds: 30,
            message: "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.");

        Assert.NotNull(emitted);
        Assert.Equal(WatchConnectionEventKind.Failure, emitted!.Kind);
        Assert.Equal("/api/demands", emitted.Endpoint);
        Assert.Equal("HTTP_TIMEOUT", emitted.Stage);
        Assert.Equal(1, emitted.FailureCount);
        Assert.Null(emitted.OutageDurationMs);
    }

    [Fact]
    public void Repeated_failures_within_summary_interval_are_deduped()
    {
        var recorder = new WatchConnectionEventRecorder(SummaryInterval);
        var t0 = DateTimeOffset.Parse("2026-07-31T10:00:00Z");

        Assert.NotNull(recorder.ObserveFailure(t0, "/api/demands", "HTTP_TIMEOUT", TimeSpan.FromSeconds(30), 30, "timeout"));
        Assert.Null(recorder.ObserveFailure(t0.AddSeconds(2), "/api/demands", "HTTP_TIMEOUT", TimeSpan.FromSeconds(30), 30, "timeout"));
        Assert.Null(recorder.ObserveFailure(t0.AddMinutes(4), "/api/demands", "HTTP_TIMEOUT", TimeSpan.FromSeconds(30), 30, "timeout"));
    }

    [Fact]
    public void Persistent_failure_emits_summary_every_five_minutes_with_count_and_duration()
    {
        var recorder = new WatchConnectionEventRecorder(SummaryInterval);
        var t0 = DateTimeOffset.Parse("2026-07-31T10:00:00Z");

        recorder.ObserveFailure(t0, "/api/alerts", "HTTP_CONNECT", TimeSpan.FromSeconds(1), 30, "Connection refused");
        // Intermediate failures still count toward the streak.
        recorder.ObserveFailure(t0.AddMinutes(1), "/api/alerts", "HTTP_CONNECT", TimeSpan.FromSeconds(1), 30, "Connection refused");
        recorder.ObserveFailure(t0.AddMinutes(2), "/api/alerts", "HTTP_CONNECT", TimeSpan.FromSeconds(1), 30, "Connection refused");

        var summary = recorder.ObserveFailure(
            t0.AddMinutes(5),
            "/api/alerts",
            "HTTP_CONNECT",
            TimeSpan.FromSeconds(1),
            30,
            "Connection refused");

        Assert.NotNull(summary);
        Assert.Equal(WatchConnectionEventKind.Summary, summary!.Kind);
        Assert.Equal(4, summary.FailureCount);
        Assert.Equal((long)TimeSpan.FromMinutes(5).TotalMilliseconds, summary.OutageDurationMs);
        Assert.Equal("/api/alerts", summary.Endpoint);
    }

    [Fact]
    public void Success_after_failures_emits_recovered_with_count_and_duration()
    {
        var recorder = new WatchConnectionEventRecorder(SummaryInterval);
        var t0 = DateTimeOffset.Parse("2026-07-31T10:00:00Z");

        recorder.ObserveFailure(t0, "/api/poll-health", "HTTP_CONNECT", TimeSpan.FromSeconds(1), 30, "Connection refused");
        recorder.ObserveFailure(t0.AddSeconds(2), "/api/poll-health", "HTTP_CONNECT", TimeSpan.FromSeconds(1), 30, "Connection refused");

        var recovered = recorder.ObserveSuccess(t0.AddMinutes(3));

        Assert.NotNull(recovered);
        Assert.Equal(WatchConnectionEventKind.Recovered, recovered!.Kind);
        Assert.Equal(2, recovered.FailureCount);
        Assert.Equal((long)TimeSpan.FromMinutes(3).TotalMilliseconds, recovered.OutageDurationMs);
    }

    [Fact]
    public void Success_with_no_prior_failure_emits_nothing()
    {
        var recorder = new WatchConnectionEventRecorder(SummaryInterval);

        Assert.Null(recorder.ObserveSuccess(DateTimeOffset.Parse("2026-07-31T10:00:00Z")));
    }

    [Fact]
    public void Host_restart_style_new_outage_after_recovery_emits_fresh_failure()
    {
        var recorder = new WatchConnectionEventRecorder(SummaryInterval);
        var t0 = DateTimeOffset.Parse("2026-07-31T10:00:00Z");

        recorder.ObserveFailure(t0, "/api/demands", "HTTP_CONNECT", TimeSpan.FromSeconds(1), 30, "Connection refused");
        Assert.NotNull(recorder.ObserveSuccess(t0.AddMinutes(1)));

        var next = recorder.ObserveFailure(
            t0.AddMinutes(2),
            "/api/demands",
            "HTTP_CONNECT",
            TimeSpan.FromSeconds(1),
            30,
            "Connection refused");

        Assert.NotNull(next);
        Assert.Equal(WatchConnectionEventKind.Failure, next!.Kind);
        Assert.Equal(1, next.FailureCount);
    }

    [Fact]
    public void Endpoint_change_during_outage_still_counts_as_same_outage_for_summary_timing()
    {
        var recorder = new WatchConnectionEventRecorder(SummaryInterval);
        var t0 = DateTimeOffset.Parse("2026-07-31T10:00:00Z");

        recorder.ObserveFailure(t0, "/api/demands", "HTTP_TIMEOUT", TimeSpan.FromSeconds(30), 30, "timeout");
        Assert.Null(recorder.ObserveFailure(t0.AddMinutes(1), "/api/alerts", "HTTP_CONNECT", TimeSpan.FromSeconds(1), 30, "refused"));

        var summary = recorder.ObserveFailure(
            t0.AddMinutes(5),
            "/api/poll-health",
            "HTTP_CONNECT",
            TimeSpan.FromSeconds(1),
            30,
            "refused");

        Assert.NotNull(summary);
        Assert.Equal(WatchConnectionEventKind.Summary, summary!.Kind);
        Assert.Equal(3, summary.FailureCount);
        Assert.Equal("/api/poll-health", summary.Endpoint);
    }
}
