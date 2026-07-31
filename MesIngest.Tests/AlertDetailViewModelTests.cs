using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 11 seam: AlertDetailViewModel — public header fields, summary text,
/// live vs historical snapshot, and DemandId helpers.
/// </summary>
public class AlertDetailViewModelTests
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

    private static WatchAlertDto SampleAlert(
        string? details = """{"failureStage":"ORACLE_QUERY","timeoutSeconds":30,"durationMs":1,"rowCount":0,"reason":"global"}""",
        bool isActive = true) =>
        new(
            AlertId: "alert-1",
            Code: "POLL_FAILURE",
            Severity: "ERROR",
            TaskType: null,
            Sublot: null,
            DemandId: "deadbeef01",
            Message: "poll failed",
            Details: details,
            FirstSeenAt: new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero),
            LastSeenAt: new DateTimeOffset(2026, 7, 15, 2, 5, 0, TimeSpan.Zero),
            OccurrenceCount: 3,
            IsActive: isActive,
            ResolvedAt: isActive ? null : new DateTimeOffset(2026, 7, 15, 2, 10, 0, TimeSpan.Zero),
            CreatedAt: new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Exposes_public_header_fields_and_host_source()
    {
        var vm = AlertDetailViewModel.From(SampleAlert(), Beijing);

        Assert.Equal("alert-1", vm.AlertId);
        Assert.Equal("POLL_FAILURE", vm.Code);
        Assert.Equal("ERROR", vm.Severity);
        Assert.Equal(UnifiedEventSource.HostIngestAlert, vm.Source);
        Assert.Equal("Host IngestAlert", vm.SourceLabel);
        Assert.True(vm.IsActive);
        Assert.Equal("active", vm.LifecycleLabel);
        Assert.Equal(3, vm.OccurrenceCount);
        Assert.Equal("deadbeef01", vm.DemandId);
        Assert.Equal("2026-07-15 10:00:00 +08:00", vm.FirstSeenAtText);
        Assert.Equal("2026-07-15 10:05:00 +08:00", vm.LastSeenAtText);
        Assert.False(vm.IsHistoricalSnapshot);
    }

    [Fact]
    public void Resolved_alert_shows_resolved_lifecycle()
    {
        var vm = AlertDetailViewModel.From(SampleAlert(isActive: false), Beijing);

        Assert.False(vm.IsActive);
        Assert.Equal("resolved", vm.LifecycleLabel);
        Assert.Equal("2026-07-15 10:10:00 +08:00", vm.ResolvedAtText);
    }

    [Fact]
    public void BuildSummary_includes_identity_and_lifecycle()
    {
        var summary = AlertDetailViewModel.From(SampleAlert(), Beijing).BuildSummary();

        Assert.Contains("AlertId=alert-1", summary, StringComparison.Ordinal);
        Assert.Contains("Code=POLL_FAILURE", summary, StringComparison.Ordinal);
        Assert.Contains("Severity=ERROR", summary, StringComparison.Ordinal);
        Assert.Contains("Source=Host IngestAlert", summary, StringComparison.Ordinal);
        Assert.Contains("DemandId=deadbeef01", summary, StringComparison.Ordinal);
        Assert.Contains("OccurrenceCount=3", summary, StringComparison.Ordinal);
        Assert.Contains("active", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkHistorical_sets_snapshot_flag_without_clearing_fields()
    {
        var vm = AlertDetailViewModel.From(SampleAlert(), Beijing);
        var historical = vm.MarkHistorical();

        Assert.True(historical.IsHistoricalSnapshot);
        Assert.Equal(vm.AlertId, historical.AlertId);
        Assert.Equal(vm.Code, historical.Code);
        Assert.Contains("historical snapshot", historical.SnapshotStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyUpdate_replaces_fields_and_clears_historical_flag()
    {
        var vm = AlertDetailViewModel.From(SampleAlert(), Beijing).MarkHistorical();
        var updated = SampleAlert(isActive: false) with { OccurrenceCount = 9, Message = "cleared" };

        var next = vm.ApplyUpdate(updated);

        Assert.False(next.IsHistoricalSnapshot);
        Assert.Equal(9, next.OccurrenceCount);
        Assert.Equal("cleared", next.Message);
        Assert.Equal("resolved", next.LifecycleLabel);
    }

    [Fact]
    public void DetailsJson_returns_raw_details_or_empty()
    {
        Assert.Equal(
            """{"failureStage":"ORACLE_QUERY","timeoutSeconds":30,"durationMs":1,"rowCount":0,"reason":"global"}""",
            AlertDetailViewModel.From(SampleAlert(), Beijing).DetailsJson);
        Assert.Equal(string.Empty, AlertDetailViewModel.From(SampleAlert(details: null), Beijing).DetailsJson);
    }
}
