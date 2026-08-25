namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Exact Host-UTC boundaries for RawObservationAvailabilityWindow and
/// RetentionEligibleDemandSeries. Calendar months and local dates are not part
/// of either contract.
/// </summary>
public static class HistoryRetentionPolicy
{
    public const int MaximumRawObservationsPerPollTrace = 25_000;

    public static readonly TimeSpan RawObservationAvailabilityWindow = TimeSpan.FromDays(15);

    public static readonly TimeSpan RetentionEligibleDemandSeriesWindow = TimeSpan.FromDays(15);

    public static DateTimeOffset RawObservationExpiresAt(DateTimeOffset completedAt) =>
        completedAt.ToUniversalTime().Add(RawObservationAvailabilityWindow);

    public static bool IsRawObservationExpired(
        DateTimeOffset completedAt,
        DateTimeOffset asOf) =>
        asOf.ToUniversalTime() >= RawObservationExpiresAt(completedAt);

    public static DateTimeOffset RetentionEligibleDemandSeriesCleanupDueAt(
        DateTimeOffset eligibilityAt) =>
        eligibilityAt.ToUniversalTime().Add(RetentionEligibleDemandSeriesWindow);

}

public sealed record HistoryRetentionAdvanceResult(
    DateTimeOffset AdvancedAt,
    DateTimeOffset RawObservationCutoff,
    int ExpiredPollTraceCount,
    int DeletedRawObservationCount,
    DateTimeOffset EarliestAvailableHostUtc);

/// <summary>
/// The durable identity left by one committed whole-Series cleanup. Detailed
/// generations, observations, events and error evidence are deliberately not
/// part of this result or the permanent tombstone.
/// </summary>
public sealed record RetentionEligibleSeriesCleanupResult(
    DateTimeOffset CleanedAt,
    string SeriesId,
    string WorkType,
    string Sublot,
    DateTimeOffset ArchivedAt,
    int TombstoneVersion);
