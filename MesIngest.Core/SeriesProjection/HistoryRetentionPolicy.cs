namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Exact Host-UTC boundaries for RawObservationAvailabilityWindow and
/// RetentionEligibleDemandSeries. Calendar months and local dates are not part
/// of either contract.
/// </summary>
public static class HistoryRetentionPolicy
{
    public static readonly TimeSpan RawObservationAvailabilityWindow = TimeSpan.FromDays(30);

    public static readonly TimeSpan RetentionEligibleDemandSeriesWindow = TimeSpan.FromDays(30);

    public static DateTimeOffset RawObservationExpiresAt(DateTimeOffset completedAt) =>
        completedAt.ToUniversalTime().Add(RawObservationAvailabilityWindow);

    public static bool IsRawObservationExpired(
        DateTimeOffset completedAt,
        DateTimeOffset asOf) =>
        asOf.ToUniversalTime() >= RawObservationExpiresAt(completedAt);

    public static DateTimeOffset RetentionEligibleDemandSeriesCleanupDueAt(
        DateTimeOffset eligibilityAt) =>
        eligibilityAt.ToUniversalTime().Add(RetentionEligibleDemandSeriesWindow);

    public static bool IsRetentionEligibleDemandSeriesCleanupDue(
        DateTimeOffset eligibilityAt,
        DateTimeOffset asOf) =>
        asOf.ToUniversalTime() >= RetentionEligibleDemandSeriesCleanupDueAt(eligibilityAt);
}

public sealed record HistoryRetentionAdvanceResult(
    DateTimeOffset AdvancedAt,
    DateTimeOffset RawObservationCutoff,
    int ExpiredPollTraceCount,
    int DeletedRawObservationCount,
    DateTimeOffset EarliestAvailableHostUtc);
