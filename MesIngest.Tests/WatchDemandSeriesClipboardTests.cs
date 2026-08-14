using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchDemandSeriesClipboardTests
{
    [Fact]
    public void Focused_demand_time_copy_keeps_offsets_and_labels_mes_source_date_as_evidence()
    {
        var series = Series();
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+08",
            TimeSpan.FromHours(8),
            "UTC+08",
            "UTC+08");

        var text = WatchDemandSeriesClipboard.FormatFocusedDemandTimes(
            series,
            "demand-current",
            zone);

        Assert.Contains("DemandId\tdemand-current", text, StringComparison.Ordinal);
        Assert.Contains("CreatedAt\t2026-08-14 12:00:00 +08:00", text, StringComparison.Ordinal);
        Assert.Contains("DemandLastSeenAt\t2026-08-14 13:00:00 +08:00", text, StringComparison.Ordinal);
        Assert.Contains("GoneConfirmedAt\t—", text, StringComparison.Ordinal);
        Assert.Contains("DATES / MesSourceDate（MES 证据，不是生命周期时间）\t2026-08-14 11:30:00 +08:00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Series_evidence_copy_names_the_stable_poll_and_projection_fences()
    {
        var text = WatchDemandSeriesClipboard.FormatSeriesEvidence(Series());

        Assert.Contains("SeriesId\tseries-20", text, StringComparison.Ordinal);
        Assert.Contains("LatestPollTraceId\tpoll-observed", text, StringComparison.Ordinal);
        Assert.Contains("LatestProjectionCommitId\tcommit-latest", text, StringComparison.Ordinal);
        Assert.Contains("LastSeriesSequence\t9", text, StringComparison.Ordinal);
        Assert.Contains("CurrentConditions\t1", text, StringComparison.Ordinal);
        Assert.Contains("ImmutableEvents\t1", text, StringComparison.Ordinal);
    }

    private static DemandSeriesSnapshot Series()
    {
        var current = new TransportDemandSnapshot(
            "demand-current",
            "series-20",
            2,
            "demand-prior",
            "VISIBLE",
            DateTimeOffset.Parse("2026-08-14T04:00:00Z"),
            DateTimeOffset.Parse("2026-08-14T05:00:00Z"),
            GoneConfirmedAt: null,
            "poll-created",
            "commit-created",
            "commit-latest",
            new LiveMesFieldSetSnapshot(
                "A1-1",
                "EQP-20",
                "STEP-20",
                DateTimeOffset.Parse("2026-08-14T03:30:00Z"),
                "PKG-20"),
            "READABLE",
            [],
            LatestObservationPollTraceId: "poll-observed",
            LatestObservationProjectionCommitId: "commit-latest",
            LatestObservationAt: DateTimeOffset.Parse("2026-08-14T05:00:00Z"));
        return new DemandSeriesSnapshot(
            "series-20",
            "WIRE_TO_GATE",
            "SL-20",
            "TRACKING",
            "VISIBLE",
            DateTimeOffset.Parse("2026-08-14T02:00:00Z"),
            "poll-created",
            "commit-created",
            "commit-latest",
            current,
            [current],
            [],
            [new DemandSeriesEventSnapshot(
                "event-9",
                "series-20",
                9,
                "DEMAND_SEEN",
                DateTimeOffset.Parse("2026-08-14T05:00:00Z"),
                "DEMAND",
                "demand-current",
                "poll-observed",
                "commit-latest",
                1,
                "{}")],
            [new DemandSeriesCurrentConditionSnapshot(
                "period-1",
                "ATTENTION",
                "READABILITY",
                "WARNING",
                "DEMAND:demand-current",
                "DEMAND",
                DateTimeOffset.Parse("2026-08-14T05:00:00Z"),
                DateTimeOffset.Parse("2026-08-14T05:00:00Z"),
                "poll-observed",
                "commit-latest",
                "demand-current",
                "value",
                "rule")],
            [],
            LastSeriesSequence: 9);
    }
}
