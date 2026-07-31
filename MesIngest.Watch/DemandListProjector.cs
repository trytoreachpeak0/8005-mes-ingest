namespace MesIngest.Watch;

internal enum DemandSortField
{
    TaskType,
    Sublot,
    Status,
    MesLastSeenAt,
    Dates,
}

internal sealed record DemandListQuery(
    string? TaskType = null,
    string? Sublot = null,
    string? Status = null,
    DemandSortField SortBy = DemandSortField.Dates,
    bool Ascending = false);

internal static class DemandListProjector
{
    public static IReadOnlyList<WatchDemandDto> FilterSort(
        IEnumerable<WatchDemandDto> source,
        DemandListQuery query)
    {
        IEnumerable<WatchDemandDto> q = source;

        if (!string.IsNullOrWhiteSpace(query.TaskType))
        {
            q = q.Where(d => ContainsIgnoreCase(d.TaskType, query.TaskType));
        }

        if (!string.IsNullOrWhiteSpace(query.Sublot))
        {
            q = q.Where(d => ContainsIgnoreCase(d.Sublot, query.Sublot));
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            q = q.Where(d => ContainsIgnoreCase(d.Status, query.Status));
        }

        Func<WatchDemandDto, string> stringKey = query.SortBy switch
        {
            DemandSortField.TaskType => static d => d.TaskType,
            DemandSortField.Sublot => static d => d.Sublot,
            DemandSortField.Status => static d => d.Status,
            _ => static _ => string.Empty,
        };

        if (query.SortBy is DemandSortField.MesLastSeenAt or DemandSortField.Dates)
        {
            Func<WatchDemandDto, DateTimeOffset> timeKey = query.SortBy == DemandSortField.Dates
                ? static d => d.Dates
                : static d => d.MesLastSeenAt;

            q = query.Ascending
                ? q.OrderBy(timeKey).ThenBy(d => d.DemandId, StringComparer.Ordinal)
                : q.OrderByDescending(timeKey).ThenBy(d => d.DemandId, StringComparer.Ordinal);
        }
        else
        {
            q = query.Ascending
                ? q.OrderBy(stringKey, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.DemandId, StringComparer.Ordinal)
                : q.OrderByDescending(stringKey, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.DemandId, StringComparer.Ordinal);
        }

        return q.ToList();
    }

    private static bool ContainsIgnoreCase(string value, string fragment) =>
        value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
