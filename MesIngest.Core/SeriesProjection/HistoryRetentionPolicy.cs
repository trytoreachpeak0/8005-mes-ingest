namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Exact Host-UTC boundaries shared by raw-observation availability and
/// retention-eligible Series cleanup. Calendar months and local dates are not
/// part of either contract.
/// </summary>
public static class HistoryRetentionPolicy
{
    public static readonly TimeSpan AvailabilityWindow = TimeSpan.FromDays(30);

    public static DateTimeOffset RawObservationExpiresAt(DateTimeOffset completedAt) =>
        completedAt.ToUniversalTime().Add(AvailabilityWindow);

    public static bool IsRawObservationExpired(
        DateTimeOffset completedAt,
        DateTimeOffset asOf) =>
        asOf.ToUniversalTime() >= RawObservationExpiresAt(completedAt);

    public static DateTimeOffset SeriesCleanupDueAt(DateTimeOffset eligibilityAt) =>
        eligibilityAt.ToUniversalTime().Add(AvailabilityWindow);

    public static bool IsSeriesCleanupDue(
        DateTimeOffset eligibilityAt,
        DateTimeOffset asOf) =>
        asOf.ToUniversalTime() >= SeriesCleanupDueAt(eligibilityAt);
}

public sealed record HistoryRetentionAdvanceResult(
    DateTimeOffset AdvancedAt,
    DateTimeOffset RawObservationCutoff,
    int ExpiredPollTraceCount,
    int DeletedRawObservationCount,
    DateTimeOffset EarliestAvailableHostUtc);
