using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

/// <summary>
/// Builds ReadabilityAudit requests at the UI boundary. Frozen paging is
/// always derived from the snapshot that is actually visible, never from a
/// pending filter draft that may have failed while the old page remains on
/// screen.
/// </summary>
internal static class WatchReadabilityAuditQueries
{
    public static ReadabilityAuditQuery StartLatest(
        ReadabilityAuditFilter filter,
        int pageSize,
        int pageNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var firstPage = new ReadabilityAuditQuery(
                filter,
                pageSize,
                PageNumber: 1,
                SnapshotReference: null,
                Cursor: null,
                ReadabilityAuditOrder.Default)
            .NormalizeAndValidate();
        if (pageNumber < 1 || pageNumber > int.MaxValue / firstPage.PageSize)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "PageNumber is outside the supported range for the requested page size.");
        }

        return firstPage with { PageNumber = pageNumber };
    }

    public static ReadabilityAuditQuery OpenFrozenPage(
        ReadabilityAuditListSnapshot snapshot,
        int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TotalPages == 0 || pageNumber < 1 || pageNumber > snapshot.TotalPages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                "The target page must exist in the visible frozen audit snapshot.");
        }

        return new ReadabilityAuditQuery(
                snapshot.Filter,
                snapshot.PageSize,
                pageNumber,
                snapshot.SnapshotReference,
                Cursor: null,
                snapshot.Order)
            .NormalizeAndValidate();
    }

    public static ReadabilityAuditQuery? OpenNextFrozenPage(
        ReadabilityAuditListSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.HasMore
            || snapshot.PageNumber >= snapshot.TotalPages
            || string.IsNullOrWhiteSpace(snapshot.NextCursor))
        {
            return null;
        }

        return new ReadabilityAuditQuery(
                snapshot.Filter,
                snapshot.PageSize,
                PageNumber: 1,
                snapshot.SnapshotReference,
                snapshot.NextCursor,
                snapshot.Order)
            .NormalizeAndValidate();
    }
}
