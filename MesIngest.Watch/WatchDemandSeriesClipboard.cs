using System.Globalization;
using System.Text;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal static class WatchDemandSeriesClipboard
{
    public static string FormatFocusedDemandTimes(
        DemandSeriesSnapshot series,
        string? focusedDemandId,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        var demand = series.Demands.FirstOrDefault(candidate => string.Equals(
                candidate.DemandId,
                focusedDemandId,
                StringComparison.Ordinal))
            ?? series.CurrentDemand;
        var builder = new StringBuilder();
        Append(builder, "SeriesId", series.SeriesId);
        Append(builder, "DemandId", demand.DemandId);
        Append(builder, "CreatedAt", WatchTimeDisplay.Format(demand.CreatedAt, timeZone));
        Append(builder, "DemandLastSeenAt", WatchTimeDisplay.Format(demand.DemandLastSeenAt, timeZone));
        Append(
            builder,
            "GoneConfirmedAt",
            demand.GoneConfirmedAt is { } gone
                ? WatchTimeDisplay.Format(gone, timeZone)
                : "—");
        Append(
            builder,
            "DATES / MesSourceDate（MES 证据，不是生命周期时间）",
            demand.LiveMesFields?.MesSourceDate is { } mesSourceDate
                ? WatchTimeDisplay.Format(mesSourceDate, timeZone)
                : "—");
        return builder.ToString().TrimEnd();
    }

    public static string FormatSeriesEvidence(DemandSeriesSnapshot series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var latestPollTraceId = series.CurrentDemand.LatestObservationPollTraceId
            ?? series.CurrentDemand.CreatedPollTraceId;
        var builder = new StringBuilder();
        Append(builder, "SeriesId", series.SeriesId);
        Append(builder, "CurrentDemandId", series.CurrentDemand.DemandId);
        Append(builder, "Lifecycle", series.Lifecycle);
        Append(builder, "CurrentPresence", series.CurrentPresence);
        Append(builder, "LastSeriesSequence", series.LastSeriesSequence.ToString(CultureInfo.InvariantCulture));
        Append(builder, "LatestPollTraceId", latestPollTraceId);
        Append(builder, "LatestProjectionCommitId", series.LatestProjectionCommitId);
        Append(builder, "Generations", series.Demands.Count.ToString(CultureInfo.InvariantCulture));
        Append(builder, "RawObservations", series.RawObservations.Count.ToString(CultureInfo.InvariantCulture));
        Append(builder, "CurrentConditions", series.CurrentConditions.Count.ToString(CultureInfo.InvariantCulture));
        Append(builder, "ErrorPeriods", series.ErrorPeriods.Count.ToString(CultureInfo.InvariantCulture));
        Append(builder, "ImmutableEvents", series.Events.Count.ToString(CultureInfo.InvariantCulture));
        return builder.ToString().TrimEnd();
    }

    private static void Append(StringBuilder builder, string name, string value) =>
        builder.Append(name).Append('\t').AppendLine(value);
}
