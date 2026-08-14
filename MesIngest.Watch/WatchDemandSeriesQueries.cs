using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

/// <summary>
/// Builds DemandSeries requests at the UI boundary. Frozen paging is always
/// derived from the snapshot that is actually visible, never from a pending
/// filter draft that may have failed while the old page remains on screen.
/// </summary>
internal static class WatchDemandSeriesQueries
{
    public static DemandSeriesBrowseQuery StartLatest(
        DemandSeriesBrowseFilter filter,
        int pageSize,
        int pageNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var firstPage = new DemandSeriesBrowseQuery(
                filter,
                pageSize,
                PageNumber: 1,
                SnapshotReference: null,
                Cursor: null,
                DemandSeriesBrowseOrder.Default)
            .NormalizeAndValidate();
        if (pageNumber < 1 || pageNumber > int.MaxValue / firstPage.PageSize)
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                "PageNumber is outside the supported range for the requested page size.");
        }

        return firstPage with { PageNumber = pageNumber };
    }

    public static DemandSeriesBrowseQuery OpenFrozenPage(
        DemandSeriesListSnapshot snapshot,
        int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TotalPages == 0 || pageNumber < 1 || pageNumber > snapshot.TotalPages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                "The target page must exist in the visible frozen snapshot.");
        }

        return new DemandSeriesBrowseQuery(
                snapshot.Filter,
                snapshot.PageSize,
                pageNumber,
                snapshot.SnapshotReference,
                Cursor: null,
                snapshot.Order)
            .NormalizeAndValidate();
    }

    public static DemandSeriesBrowseQuery? OpenNextFrozenPage(
        DemandSeriesListSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.HasMore
            || snapshot.PageNumber >= snapshot.TotalPages
            || string.IsNullOrWhiteSpace(snapshot.NextCursor))
        {
            return null;
        }

        return new DemandSeriesBrowseQuery(
                snapshot.Filter,
                snapshot.PageSize,
                PageNumber: 1,
                snapshot.SnapshotReference,
                snapshot.NextCursor,
                snapshot.Order)
            .NormalizeAndValidate();
    }
}
