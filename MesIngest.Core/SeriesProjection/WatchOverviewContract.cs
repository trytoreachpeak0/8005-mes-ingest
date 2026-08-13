using System.Text.RegularExpressions;

namespace MesIngest.Core.SeriesProjection;

public static class OverviewNavigationTargets
{
    public const string DemandSeries = "DEMAND_SERIES";
    public const string ReadabilityAudit = "READABILITY_AUDIT";
    public const string ErrorSearch = "ERROR_SEARCH";
    public const string CurrentIngestAttention = "CURRENT_INGEST_ATTENTION";
    public const string DemandSeriesDetail = "DEMAND_SERIES_DETAIL";
    public const string TaskTypeProtection = "TASK_TYPE_PROTECTION";
    public const string PollTrace = "POLL_TRACE";
}

/// <summary>
/// A structured first-page intent. Null/empty dimensions are deliberate, not
/// implicit target-page defaults; Cursor is always null for overview drills.
/// </summary>
public sealed record OverviewNavigationIntent(
    string Target,
    int PageNumber = 1,
    IReadOnlyList<string>? MesAreas = null,
    IReadOnlyList<string>? Lifecycles = null,
    IReadOnlyList<string>? CurrentPresences = null,
    IReadOnlyList<string>? ReadabilityStates = null,
    IReadOnlyList<string>? ErrorActivityStates = null,
    string? ErrorWindow = null,
    IReadOnlyList<string>? AttentionKinds = null,
    IReadOnlyList<string>? AttentionSeverities = null,
    string? SeriesId = null,
    string? WorkType = null,
    string? PollTraceId = null,
    string? Cursor = null);

public sealed record WatchOverviewQuery(IReadOnlyList<string>? MesAreas = null)
{
    public const int MaximumAreaCount = 100;
    public const int MaximumAreaLength = 128;

    private static readonly Regex AreaFormat = new(
        "^[A-Z][1-9][0-9]?-[1-9][0-9]?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public WatchOverviewQuery NormalizeAndValidate()
    {
        if (MesAreas?.Any(value => string.IsNullOrWhiteSpace(value)) == true)
        {
            throw new WatchOverviewException(
                WatchOverviewErrorCodes.InvalidQuery,
                "AREA values cannot be empty.");
        }

        var normalized = (MesAreas ?? Array.Empty<string>())
            .Select(value => value?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length > MaximumAreaCount)
        {
            throw new WatchOverviewException(
                WatchOverviewErrorCodes.InvalidQuery,
                $"At most {MaximumAreaCount} AREA values are supported.");
        }

        if (normalized.Any(value =>
                value.Length > MaximumAreaLength || !AreaFormat.IsMatch(value)))
        {
            throw new WatchOverviewException(
                WatchOverviewErrorCodes.InvalidQuery,
                "Every AREA must match the MES AREA domain format.");
        }

        return this with { MesAreas = normalized };
    }
}

public sealed record OverviewFacetSnapshot(
    string Value,
    long Count,
    OverviewNavigationIntent Navigation);

public sealed record WatchOverviewSeriesSummary(
    long ExactTotalSeriesCount,
    long TrackingCount,
    long ArchivedCount,
    long GoneCount,
    long LongGoneButVisibleCount,
    OverviewNavigationIntent Navigation,
    OverviewNavigationIntent TrackingNavigation,
    OverviewNavigationIntent ArchivedNavigation,
    OverviewNavigationIntent GoneNavigation,
    OverviewNavigationIntent LongGoneButVisibleNavigation);

public sealed record WatchOverviewReadabilitySummary(
    long ExactTotalDemandGenerationCount,
    long ReadableCount,
    long NotReadableCount,
    OverviewNavigationIntent Navigation,
    OverviewNavigationIntent ReadableNavigation,
    OverviewNavigationIntent NotReadableNavigation);

public sealed record WatchOverviewErrorSummary(
    long ActiveSeriesCount,
    long Prior7DaysSeriesCount,
    OverviewNavigationIntent Navigation,
    OverviewNavigationIntent ActiveNavigation,
    OverviewNavigationIntent Prior7DaysNavigation);

public sealed record WatchOverviewAttentionSummary(
    long ExactTotalItemCount,
    IReadOnlyList<OverviewFacetSnapshot> Types,
    IReadOnlyList<OverviewFacetSnapshot> Severities,
    OverviewNavigationIntent Navigation);

public sealed record WatchOverviewActivitySnapshot(
    string EventId,
    string Kind,
    string EventType,
    string Severity,
    DateTimeOffset OccurredAt,
    string? SeriesId,
    string? WorkType,
    string? PollTraceId,
    string? ProjectionCommitId,
    OverviewNavigationIntent Navigation);

public static class WatchOverviewRecentActivityStates
{
    public const string HasRecentHighlights = "HAS_RECENT_HIGHLIGHTS";
    public const string NoRecentHighlights = "NO_RECENT_HIGHLIGHTS";
    public const string NoRecentHighlightsMessage = "近期无重点动态";
}

public sealed record WatchOverviewSnapshot(
    OperationalSnapshotIdentity Snapshot,
    IReadOnlyList<string> MesAreas,
    WatchOverviewSeriesSummary Series,
    WatchOverviewReadabilitySummary Readability,
    WatchOverviewErrorSummary Errors,
    WatchOverviewAttentionSummary Attention,
    IReadOnlyList<WatchOverviewActivitySnapshot> RecentActivity,
    string RecentActivityState,
    string? EmptyStateMessage);

public static class WatchOverviewErrorCodes
{
    public const string InvalidQuery = "WATCH_OVERVIEW_INVALID_QUERY";
    public const string ProjectionNotAvailable = "WATCH_OVERVIEW_PROJECTION_NOT_AVAILABLE";
}

public sealed class WatchOverviewException : Exception
{
    public WatchOverviewException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Observable production boundary used to prove deterministic old-or-new reads.
/// Production registers the no-op implementation; integration tests may pause
/// after the complete fence is selected without bypassing the public HTTP seam.
/// </summary>
public interface IWatchOverviewReadBoundaryObserver
{
    Task OnFenceSelectedAsync(
        OperationalSnapshotIdentity snapshot,
        CancellationToken cancellationToken);
}

public sealed class NoopWatchOverviewReadBoundaryObserver : IWatchOverviewReadBoundaryObserver
{
    public static NoopWatchOverviewReadBoundaryObserver Instance { get; } = new();

    private NoopWatchOverviewReadBoundaryObserver()
    {
    }

    public Task OnFenceSelectedAsync(
        OperationalSnapshotIdentity snapshot,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
