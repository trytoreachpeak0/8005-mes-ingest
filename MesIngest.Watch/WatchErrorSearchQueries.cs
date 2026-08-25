using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

/// <summary>
/// Builds Error Search requests at the UI boundary. Frozen requests are always
/// rebuilt from the visible committed Host snapshot, so a failed filter draft
/// cannot rewrite the paging fence.
/// </summary>
internal static class WatchErrorSearchQueries
{
    public static ErrorSearchQuery StartLatest(
        ErrorSearchFilter? filter = null,
        ErrorSearchWindowSelection? window = null,
        int pageSize = ErrorSearchQuery.DefaultPageSize) =>
        new ErrorSearchQuery(
                filter ?? new ErrorSearchFilter(),
                window ?? ErrorSearchWindowSelection.Last7Days,
                pageSize,
                SnapshotReference: null,
                Cursor: null,
                ErrorSearchOrder.Default)
            .NormalizeAndValidate();

    /// <summary>
    /// Opens a cursor already issued for <paramref name="targetPageNumber"/>.
    /// Error Search cursors carry their target page, so the Host remains the
    /// authority that validates the cursor-to-page binding.
    /// </summary>
    public static ErrorSearchQuery OpenFrozenPage(
        ErrorSearchListSnapshot snapshot,
        int targetPageNumber,
        string? cursor)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TotalPages == 0
            || targetPageNumber < 1
            || targetPageNumber > snapshot.TotalPages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetPageNumber),
                targetPageNumber,
                "The target page must exist in the visible frozen Error Search snapshot.");
        }

        if (targetPageNumber == 1 && cursor is not null)
        {
            throw new ArgumentException(
                "The first frozen page must not carry a cursor.",
                nameof(cursor));
        }

        if (targetPageNumber > 1 && string.IsNullOrWhiteSpace(cursor))
        {
            throw new ArgumentException(
                "A frozen page after page one requires its Host-issued cursor.",
                nameof(cursor));
        }

        return new ErrorSearchQuery(
                snapshot.Filter,
                ToWindowSelection(snapshot.Window),
                snapshot.PageSize,
                snapshot.SnapshotReference,
                cursor,
                snapshot.Order)
            .NormalizeAndValidate();
    }

    public static ErrorSearchQuery? OpenNextFrozenPage(ErrorSearchListSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.HasMore
            || snapshot.PageNumber >= snapshot.TotalPages
            || string.IsNullOrWhiteSpace(snapshot.NextCursor))
        {
            return null;
        }

        return OpenFrozenPage(snapshot, snapshot.PageNumber + 1, snapshot.NextCursor);
    }

    private static ErrorSearchWindowSelection ToWindowSelection(
        ErrorSearchResolvedWindow window) => window.Kind switch
        {
            ErrorSearchWindowKinds.Last24Hours => ErrorSearchWindowSelection.Last24Hours,
            ErrorSearchWindowKinds.Last7Days => ErrorSearchWindowSelection.Last7Days,
            ErrorSearchWindowKinds.Last15Days => ErrorSearchWindowSelection.Last15Days,
            ErrorSearchWindowKinds.AllHistory => ErrorSearchWindowSelection.AllHistory,
            ErrorSearchWindowKinds.Custom =>
                ErrorSearchWindowSelection.Custom(window.FromUtc, window.ToUtc),
            _ => new ErrorSearchWindowSelection(window.Kind),
        };
}
