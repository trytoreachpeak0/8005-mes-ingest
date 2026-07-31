using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 11 seam: UnifiedWatchEvent list — one viewing surface, two sources,
/// filterable without merging persistence.
/// </summary>
public class UnifiedWatchEventTests
{
    private static readonly TimeZoneInfo Beijing = ResolveTz("China Standard Time", "Asia/Shanghai");

    private static TimeZoneInfo ResolveTz(string windowsId, string ianaId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
    }

    [Fact]
    public void FromAlert_sets_host_source_label()
    {
        var evt = UnifiedWatchEvent.FromAlert(
            new WatchAlertDto(
                AlertId: "a1",
                Code: "FIELD_DRIFT",
                Severity: "ERROR",
                TaskType: "T",
                Sublot: "S",
                DemandId: "d1",
                Message: "drift",
                Details: null,
                FirstSeenAt: new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero),
                LastSeenAt: new DateTimeOffset(2026, 7, 15, 2, 1, 0, TimeSpan.Zero),
                OccurrenceCount: 1,
                IsActive: true,
                ResolvedAt: null,
                CreatedAt: new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero)),
            Beijing);

        Assert.Equal(UnifiedEventSource.HostIngestAlert, evt.Source);
        Assert.Equal("Host IngestAlert", evt.SourceLabel);
        Assert.Equal("FIELD_DRIFT", evt.CodeOrKind);
        Assert.Equal("ERROR", evt.SeverityOrStage);
        Assert.Equal("d1", evt.DemandId);
        Assert.Equal("2026-07-15 10:01:00 +08:00", evt.AtText);
    }

    [Fact]
    public void FromConnectionEvent_sets_watch_source_label()
    {
        var evt = UnifiedWatchEvent.FromConnectionEvent(
            new WatchConnectionEvent(
                Kind: WatchConnectionEventKind.Failure,
                At: new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero),
                Endpoint: "/api/demands",
                Stage: "HTTP_TIMEOUT",
                ElapsedMs: 30_000,
                TimeoutSeconds: 30,
                Message: "timeout",
                FailureCount: 2,
                OutageDurationMs: null,
                CorrelationId: "c1"),
            Beijing);

        Assert.Equal(UnifiedEventSource.WatchConnectionEvent, evt.Source);
        Assert.Equal("Watch Connection Event", evt.SourceLabel);
        Assert.Equal("Failure", evt.CodeOrKind);
        Assert.Equal("HTTP_TIMEOUT", evt.SeverityOrStage);
        Assert.Equal("/api/demands", evt.EndpointOrTaskType);
        Assert.Null(evt.DemandId);
        Assert.Contains("timeout", evt.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_keeps_only_selected_source()
    {
        var host = UnifiedWatchEvent.FromAlert(
            new WatchAlertDto(
                "a1", "POLL_FAILURE", "ERROR", null, null, null, "m", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, true, null, DateTimeOffset.UtcNow),
            Beijing);
        var watch = UnifiedWatchEvent.FromConnectionEvent(
            new WatchConnectionEvent(
                WatchConnectionEventKind.Recovered,
                DateTimeOffset.UtcNow,
                "/api/alerts",
                "HTTP_CONNECT",
                10,
                30,
                "ok",
                1,
                1000),
            Beijing);

        var mixed = new[] { host, watch };

        Assert.Equal(
            [host],
            UnifiedWatchEvent.Filter(mixed, UnifiedEventSourceFilter.HostIngestAlert));
        Assert.Equal(
            [watch],
            UnifiedWatchEvent.Filter(mixed, UnifiedEventSourceFilter.WatchConnectionEvent));
        Assert.Equal(
            mixed,
            UnifiedWatchEvent.Filter(mixed, UnifiedEventSourceFilter.All));
    }
}
