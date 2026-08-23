namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Host-side filters for the frozen DemandSeries browse projection. Collection
/// dimensions use OR within the dimension and AND across dimensions.
/// </summary>
public sealed record DemandSeriesBrowseFilter
{
    public IReadOnlyList<string> Lifecycles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CurrentPresences { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> WorkTypes { get; init; } = Array.Empty<string>();

    public string? SublotContains { get; init; }

    /// <summary>
    /// Exact business-key component used by the Host's direct locator. Watch
    /// search continues to use <see cref="SublotContains"/>.
    /// </summary>
    public string? Sublot { get; init; }

    public string? SeriesId { get; init; }

    public string? DemandId { get; init; }

    /// <summary>
    /// Temporary read-only AREA scope supplied by Watch. It never changes the
    /// business projection or external-readability decision.
    /// </summary>
    public IReadOnlyList<string> MesAreas { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Produces the one canonical representation used by querying and token
    /// binding. Values themselves are preserved; only set ordering and exact
    /// duplicates are normalized.
    /// </summary>
    public DemandSeriesBrowseFilter Normalize() => this with
    {
        Lifecycles = NormalizeSet(Lifecycles),
        CurrentPresences = NormalizeSet(CurrentPresences),
        WorkTypes = NormalizeSet(WorkTypes),
        MesAreas = NormalizeSet(MesAreas),
        SublotContains = EmptyToNull(SublotContains),
        Sublot = EmptyToNull(Sublot),
        SeriesId = EmptyToNull(SeriesId),
        DemandId = EmptyToNull(DemandId),
    };

    private static IReadOnlyList<string> NormalizeSet(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string? EmptyToNull(string? value) => value is { Length: > 0 } ? value : null;
}

public static class DemandSeriesBrowseOrder
{
    /// <summary>
    /// Newest locally-started Series first, with stable SeriesId tie breaking.
    /// MesSourceDate is deliberately not a lifecycle ordering key.
    /// </summary>
    public const string Default = "STARTED_AT_DESC_SERIES_ID_ASC";
}

/// <summary>
/// A request for one bounded page. PageNumber is one-based. The first request
/// omits SnapshotReference; subsequent pages and detail reads retain it.
/// </summary>
public sealed record DemandSeriesBrowseQuery(
    DemandSeriesBrowseFilter Filter,
    int PageSize = 100,
    int PageNumber = 1,
    string? SnapshotReference = null,
    string? Cursor = null,
    string Order = DemandSeriesBrowseOrder.Default)
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    public DemandSeriesBrowseQuery NormalizeAndValidate()
    {
        if (PageSize is < 1 or > MaximumPageSize)
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                $"PageSize must be between 1 and {MaximumPageSize}.");
        }

        if (PageNumber < 1)
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                "PageNumber must be one or greater.");
        }

        if (PageNumber > int.MaxValue / PageSize)
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                "PageNumber is too large for the requested page size.");
        }

        if (!string.Equals(Order, DemandSeriesBrowseOrder.Default, StringComparison.Ordinal))
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                $"Order must be {DemandSeriesBrowseOrder.Default}.");
        }

        if (Cursor is not null && SnapshotReference is null)
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                "A cursor must be accompanied by its snapshot reference.");
        }

        if (Cursor is not null && PageNumber != 1)
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                "PageNumber cannot be combined with a cursor.");
        }

        if (Cursor is null && PageNumber > 1 && SnapshotReference is null)
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                "A direct page after page one must retain its snapshot reference.");
        }

        return this with { Filter = (Filter ?? new DemandSeriesBrowseFilter()).Normalize() };
    }
}

/// <summary>
/// Stable identity of a successful projection commit used as an as-of boundary.
/// ProjectionSequence, rather than time or GUID ordering, defines commit order.
/// </summary>
public sealed record DemandSeriesSnapshotIdentity(
    HistoryEpoch HistoryEpoch,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    string ContractVersion = NewMesIngestContract.Version);

/// <summary>
/// Lifecycle and presence counts are intentionally separate. GONE and
/// LONG_GONE_BUT_VISIBLE are attention counts and must not be mechanically added
/// to the mutually exclusive lifecycle counts.
/// </summary>
public sealed record DemandSeriesFacets(
    long TrackingCount,
    long ArchivedCount,
    long VisibleCount,
    long GoneCount,
    long LongGoneButVisibleCount);

public sealed record DemandSeriesListItemSnapshot(
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string CurrentPresence,
    DateTimeOffset StartedAt,
    DateTimeOffset? ArchivedAt,
    string CurrentDemandId,
    int CurrentGeneration,
    string CurrentDemandStatus,
    DateTimeOffset DemandLastSeenAt,
    DateTimeOffset? GoneConfirmedAt,
    LiveMesFieldSetSnapshot? LiveMesFields,
    string ExternalReadabilityState,
    IReadOnlyList<string> ReadabilityBlockers,
    long LastSeriesSequence,
    string LatestPollTraceId,
    string LatestProjectionCommitId);

public sealed record DemandSeriesListSnapshot(
    DemandSeriesSnapshotIdentity Snapshot,
    string SnapshotReference,
    DemandSeriesBrowseFilter Filter,
    string Order,
    long ExactTotalCount,
    DemandSeriesFacets Facets,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<DemandSeriesListItemSnapshot> Items,
    string? NextCursor,
    bool HasMore);

public sealed record DemandSeriesDetailSnapshot(
    DemandSeriesSnapshotIdentity Snapshot,
    string SnapshotReference,
    DemandSeriesSnapshot Series);

/// <summary>
/// Decoded cursor data. TargetPageNumber identifies the page for which the
/// token was issued; the optional anchor supports stable keyset navigation for
/// the fixed StartedAt/SeriesId order.
/// </summary>
public sealed record DemandSeriesBrowseCursor(
    string ContractVersion,
    HistoryEpoch HistoryEpoch,
    string ProjectionCommitId,
    long ProjectionSequence,
    string FilterHash,
    string Order,
    int PageSize,
    int TargetPageNumber,
    DateTimeOffset? AfterStartedAt,
    string? AfterSeriesId);

public static class DemandSeriesBrowseErrorCodes
{
    public const string InvalidQuery = "INVALID_DEMAND_SERIES_QUERY";
    public const string ProjectionNotAvailable = "DEMAND_SERIES_PROJECTION_NOT_AVAILABLE";
    public const string InvalidSnapshotReference = "INVALID_DEMAND_SERIES_SNAPSHOT_REFERENCE";
    public const string SnapshotNotFound = "DEMAND_SERIES_SNAPSHOT_NOT_FOUND";
    public const string SnapshotMismatch = "DEMAND_SERIES_SNAPSHOT_MISMATCH";
    public const string InvalidCursor = "INVALID_DEMAND_SERIES_CURSOR";
    public const string CursorMismatch = "DEMAND_SERIES_CURSOR_MISMATCH";
    public const string ObjectNotInSnapshot = "DEMAND_SERIES_OBJECT_NOT_IN_SNAPSHOT";
}

public sealed class DemandSeriesBrowseException : Exception
{
    public DemandSeriesBrowseException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public DemandSeriesBrowseException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public sealed record DemandSeriesBrowseTokenError(string Code, string Message);
