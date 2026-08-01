namespace MesIngest.Core;

public enum DemandChangeType
{
    Created,
    Gone,
}

/// <summary>
/// Stable post-change business payload for DemandChangeFeed.
/// Omits observation fields (MesLastSeenAt, DisappearCount) and alerts.
/// </summary>
public sealed record DemandChangePayload(
    string DemandId,
    string TaskType,
    string Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset Dates,
    string? Package,
    DemandStatus Status,
    bool LocationRisk,
    string? LocationRiskCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? GoneAt)
{
    public static DemandChangePayload From(TransportDemand demand) =>
        new(
            demand.DemandId,
            demand.TaskType,
            demand.Sublot,
            demand.Area,
            demand.Eqp,
            demand.Step,
            demand.Dates,
            demand.Package,
            demand.Status,
            demand.LocationRisk,
            demand.LocationRiskCode,
            demand.CreatedAt,
            demand.GoneAt);
}

public sealed record DemandChangeFeedEntry(
    long Sequence,
    string DemandId,
    DemandChangeType ChangeType,
    DateTimeOffset ChangedAt,
    DemandChangePayload Payload);

public sealed class DemandChangeFeedQuery
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 200;
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(48);

    /// <summary>Exclusive lower bound: return entries with Sequence &gt; AfterSequence.</summary>
    public long AfterSequence { get; init; }

    public int Limit { get; init; } = DefaultLimit;

    public DateTimeOffset AsOf { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record DemandChangeFeedPage(
    IReadOnlyList<DemandChangeFeedEntry> Items,
    long? NextAfterSequence,
    bool HasMore,
    long HighWatermark,
    long? EarliestAvailableSequence);

public sealed class SyncCursorExpiredException : Exception
{
    public const string ErrorCode = "SYNC_CURSOR_EXPIRED";

    public SyncCursorExpiredException(long afterSequence, long? earliestAvailableSequence, long highWatermark)
        : base(
            earliestAvailableSequence is long earliest
                ? $"{ErrorCode}: afterSequence {afterSequence} is earlier than retained feed " +
                  $"(earliestAvailableSequence={earliest}). Bootstrap required."
                : $"{ErrorCode}: afterSequence {afterSequence} is earlier than retained feed " +
                  $"(ledger empty; highWatermark={highWatermark}). Bootstrap required.")
    {
        AfterSequence = afterSequence;
        EarliestAvailableSequence = earliestAvailableSequence;
        HighWatermark = highWatermark;
    }

    public long AfterSequence { get; }
    public long? EarliestAvailableSequence { get; }
    public long HighWatermark { get; }
}

public static class DemandChangeFeedQueryParser
{
    public static bool TryParseAfterSequence(string? raw, out long afterSequence, out string? error)
    {
        afterSequence = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!long.TryParse(raw, out var parsed) || parsed < 0)
        {
            error = "afterSequence must be a non-negative integer";
            return false;
        }

        afterSequence = parsed;
        return true;
    }

    public static bool TryParseLimit(int? raw, out int limit, out string? error)
    {
        limit = DemandChangeFeedQuery.DefaultLimit;
        error = null;
        if (raw is null)
        {
            return true;
        }

        if (raw.Value < 1 || raw.Value > DemandChangeFeedQuery.MaxLimit)
        {
            error = $"limit must be between 1 and {DemandChangeFeedQuery.MaxLimit}";
            return false;
        }

        limit = raw.Value;
        return true;
    }
}
