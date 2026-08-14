using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

/// <summary>
/// Builds CurrentIngestAttention requests at the UI boundary. This surface is
/// deliberately global: local AREA profiles are not an input to any helper.
/// </summary>
internal static class WatchCurrentIngestAttentionQueries
{
    public static CurrentIngestAttentionQuery StartLatest(
        IReadOnlyList<string>? kinds = null,
        IReadOnlyList<string>? severities = null,
        int pageSize = CurrentIngestAttentionQuery.DefaultPageSize,
        int pageNumber = 1) =>
        new CurrentIngestAttentionQuery(
                pageSize,
                pageNumber,
                CurrentIngestAttentionOrder.Default,
                kinds,
                severities)
            .NormalizeAndValidate();

    public static CurrentIngestAttentionQuery FromNavigation(
        OverviewNavigationIntent intent,
        int pageSize = CurrentIngestAttentionQuery.DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!string.Equals(
                intent.Target,
                OverviewNavigationTargets.CurrentIngestAttention,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The navigation intent must target CurrentIngestAttention.",
                nameof(intent));
        }

        return StartLatest(
            intent.AttentionKinds,
            intent.AttentionSeverities,
            pageSize,
            intent.PageNumber);
    }

    public static CurrentIngestAttentionQuery OpenPage(
        CurrentIngestAttentionSnapshot snapshot,
        int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TotalPages == 0 || pageNumber < 1 || pageNumber > snapshot.TotalPages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                "The target page must exist in the visible Host result.");
        }

        return new CurrentIngestAttentionQuery(
                snapshot.PageSize,
                pageNumber,
                snapshot.Order,
                snapshot.Kinds,
                snapshot.Severities)
            .NormalizeAndValidate();
    }

    public static CurrentIngestAttentionQuery? OpenPreviousPage(
        CurrentIngestAttentionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.TotalPages > 0 && snapshot.PageNumber > 1
            ? OpenPage(snapshot, snapshot.PageNumber - 1)
            : null;
    }

    public static CurrentIngestAttentionQuery? OpenNextPage(
        CurrentIngestAttentionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.TotalPages > 0 && snapshot.PageNumber < snapshot.TotalPages
            ? OpenPage(snapshot, snapshot.PageNumber + 1)
            : null;
    }

    /// <summary>
    /// Rebuilds the explicit historical query for a current Series error. The
    /// current item contributes its stable domain identity; a fresh query has
    /// no snapshot reference or cursor and therefore starts on the first page.
    /// </summary>
    public static ErrorSearchQuery? CreateErrorSearchDrill(
        CurrentIngestAttentionItemSnapshot item,
        int pageSize = ErrorSearchQuery.DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!string.Equals(
                item.Kind,
                CurrentIngestAttentionKinds.SeriesError,
                StringComparison.Ordinal))
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(item.SeriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.ErrorCode);
        var definition = SeriesErrorCatalog.GetRequired(item.ErrorCode);
        var window = string.IsNullOrWhiteSpace(item.Navigation.ErrorWindow)
            ? ErrorSearchWindowSelection.Last7Days
            : new ErrorSearchWindowSelection(item.Navigation.ErrorWindow);

        return new ErrorSearchQuery(
                new ErrorSearchFilter
                {
                    Categories = [definition.Category],
                    ErrorCodes = [definition.Code],
                    ActivityStates = [ErrorSearchActivityStates.Active],
                    SeriesId = item.SeriesId,
                },
                window,
                pageSize,
                SnapshotReference: null,
                Cursor: null,
                ErrorSearchOrder.Default)
            .NormalizeAndValidate();
    }
}
