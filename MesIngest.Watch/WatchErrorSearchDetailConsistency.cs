using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal static class WatchErrorSearchDetailConsistency
{
    public static bool Matches(
        ErrorSearchListSnapshot list,
        string? selectedId,
        ErrorSearchDetailSnapshot detail) =>
        !string.IsNullOrWhiteSpace(selectedId)
        && string.Equals(selectedId, detail.Series.SeriesId, StringComparison.Ordinal)
        && string.Equals(
            list.SnapshotReference,
            detail.SnapshotReference,
            StringComparison.Ordinal)
        && list.Snapshot == detail.Snapshot
        && SameFilter(list.Filter, detail.Filter)
        && list.Window == detail.Window
        && string.Equals(list.Order, detail.Order, StringComparison.Ordinal);

    private static bool SameFilter(ErrorSearchFilter left, ErrorSearchFilter right) =>
        left.Categories.SequenceEqual(right.Categories, StringComparer.Ordinal)
        && left.ErrorCodes.SequenceEqual(right.ErrorCodes, StringComparer.Ordinal)
        && left.ActivityStates.SequenceEqual(right.ActivityStates, StringComparer.Ordinal)
        && string.Equals(left.SeriesId, right.SeriesId, StringComparison.Ordinal)
        && string.Equals(left.DemandId, right.DemandId, StringComparison.Ordinal)
        && string.Equals(left.SublotContains, right.SublotContains, StringComparison.Ordinal);
}
