namespace MesIngest.Core.SeriesProjection;

public static class ErrorSearchActivityStates
{
    public const string Active = "ACTIVE";
    public const string Ended = "ENDED";

    public static IReadOnlyList<string> All { get; } = [Active, Ended];
}

public static class ErrorSearchWindowKinds
{
    public const string Last24Hours = "LAST_24_HOURS";
    public const string Last7Days = "LAST_7_DAYS";
    public const string Last30Days = "LAST_30_DAYS";
    public const string AllHistory = "ALL_HISTORY";
    public const string Custom = "CUSTOM";
}

/// <summary>
/// A caller's requested error-history range. Rolling presets are resolved only
/// after the Host freezes ErrorSearchAsOf; custom values are normalized to UTC.
/// </summary>
public sealed record ErrorSearchWindowSelection(
    string Kind,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null)
{
    public static ErrorSearchWindowSelection Last24Hours { get; } =
        new(ErrorSearchWindowKinds.Last24Hours);

    public static ErrorSearchWindowSelection Last7Days { get; } =
        new(ErrorSearchWindowKinds.Last7Days);

    public static ErrorSearchWindowSelection Last30Days { get; } =
        new(ErrorSearchWindowKinds.Last30Days);

    public static ErrorSearchWindowSelection AllHistory { get; } =
        new(ErrorSearchWindowKinds.AllHistory);

    public static ErrorSearchWindowSelection Custom(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc) =>
        new(ErrorSearchWindowKinds.Custom, fromUtc, toUtc);

    public ErrorSearchResolvedWindow Resolve(DateTimeOffset errorSearchAsOf)
    {
        var asOfUtc = errorSearchAsOf.ToUniversalTime();
        var normalizedKind = Kind?.Trim().ToUpperInvariant();
        var resolved = normalizedKind switch
        {
            ErrorSearchWindowKinds.Last24Hours =>
                new ErrorSearchResolvedWindow(normalizedKind, asOfUtc.AddHours(-24), asOfUtc),
            ErrorSearchWindowKinds.Last7Days =>
                new ErrorSearchResolvedWindow(normalizedKind, asOfUtc.AddHours(-7 * 24), asOfUtc),
            ErrorSearchWindowKinds.Last30Days =>
                new ErrorSearchResolvedWindow(normalizedKind, asOfUtc.AddHours(-30 * 24), asOfUtc),
            ErrorSearchWindowKinds.AllHistory =>
                new ErrorSearchResolvedWindow(normalizedKind, null, asOfUtc),
            ErrorSearchWindowKinds.Custom => ResolveCustom(asOfUtc),
            _ => throw InvalidQuery("window contains an unsupported exact value."),
        };

        return resolved;
    }

    public ErrorSearchWindowSelection Normalize()
    {
        var normalizedKind = Kind?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalizedKind))
        {
            throw InvalidQuery("window is required.");
        }

        if (!string.Equals(normalizedKind, ErrorSearchWindowKinds.Custom, StringComparison.Ordinal)
            && (FromUtc is not null || ToUtc is not null))
        {
            throw InvalidQuery("Rolling and all-history windows cannot carry custom boundaries.");
        }

        return this with
        {
            Kind = normalizedKind,
            FromUtc = FromUtc?.ToUniversalTime(),
            ToUtc = ToUtc?.ToUniversalTime(),
        };
    }

    private ErrorSearchResolvedWindow ResolveCustom(DateTimeOffset asOfUtc)
    {
        if (FromUtc is null && ToUtc is null)
        {
            throw InvalidQuery("A custom window requires from or to.");
        }

        var fromUtc = FromUtc?.ToUniversalTime();
        var toUtc = ToUtc?.ToUniversalTime() ?? asOfUtc;
        if (fromUtc is not null && fromUtc.Value >= toUtc)
        {
            throw InvalidQuery("from must be earlier than to.");
        }
        if (toUtc > asOfUtc)
        {
            throw InvalidQuery("to cannot be later than ErrorSearchAsOf.");
        }

        return new ErrorSearchResolvedWindow(ErrorSearchWindowKinds.Custom, fromUtc, toUtc);
    }

    private static ErrorSearchException InvalidQuery(string message) =>
        new(ErrorSearchErrorCodes.InvalidQuery, message);
}

public sealed record ErrorSearchResolvedWindow(
    string Kind,
    DateTimeOffset? FromUtc,
    DateTimeOffset ToUtc);

public static class ErrorSearchInterval
{
    /// <summary>
    /// Applies strict UTC half-open overlap: periodStart &lt; queryTo and
    /// effectivePeriodEnd &gt; queryFrom. An active period ends temporarily at
    /// ErrorSearchAsOf for this comparison.
    /// </summary>
    public static bool Overlaps(
        DateTimeOffset periodStartUtc,
        DateTimeOffset? periodEndUtc,
        ErrorSearchResolvedWindow queryWindow,
        DateTimeOffset errorSearchAsOf)
    {
        ArgumentNullException.ThrowIfNull(queryWindow);
        var asOfUtc = errorSearchAsOf.ToUniversalTime();
        var startUtc = periodStartUtc.ToUniversalTime();
        var effectiveEndUtc = periodEndUtc is null || periodEndUtc.Value > asOfUtc
            ? asOfUtc
            : periodEndUtc.Value.ToUniversalTime();
        return startUtc < queryWindow.ToUtc
            && (queryWindow.FromUtc is null || effectiveEndUtc > queryWindow.FromUtc.Value);
    }
}

/// <summary>
/// OR is used within collection dimensions and AND across dimensions. Error
/// identifiers are trim-normalized and compared case-insensitively. SUBLOT is
/// a trim-normalized, case-insensitive contains term.
/// </summary>
public sealed record ErrorSearchFilter
{
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ErrorCodes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ActivityStates { get; init; } = Array.Empty<string>();

    public string? SeriesId { get; init; }

    public string? DemandId { get; init; }

    public string? SublotContains { get; init; }

    public ErrorSearchFilter Normalize() => this with
    {
        Categories = NormalizeSet(Categories),
        ErrorCodes = NormalizeSet(ErrorCodes),
        ActivityStates = NormalizeSet(ActivityStates),
        SeriesId = NormalizeIdentifier(SeriesId),
        DemandId = NormalizeIdentifier(DemandId),
        SublotContains = NormalizeIdentifier(SublotContains),
    };

    private static IReadOnlyList<string> NormalizeSet(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string? NormalizeIdentifier(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}

public static class ErrorSearchOrder
{
    public const string Default =
        "ACTIVE_FIRST_LATEST_MATCHED_EVIDENCE_DESC_SERIES_ID_ASC";
}

public sealed record ErrorSearchQuery(
    ErrorSearchFilter Filter,
    ErrorSearchWindowSelection Window,
    int PageSize = 100,
    string? SnapshotReference = null,
    string? Cursor = null,
    string Order = ErrorSearchOrder.Default)
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    public ErrorSearchQuery NormalizeAndValidate()
    {
        var normalizedFilter = (Filter ?? new ErrorSearchFilter()).Normalize();
        var normalizedWindow = (Window ?? ErrorSearchWindowSelection.Last7Days).Normalize();

        if (PageSize is < 1 or > MaximumPageSize)
        {
            throw InvalidQuery($"PageSize must be between 1 and {MaximumPageSize}.");
        }
        if (!string.Equals(Order, ErrorSearchOrder.Default, StringComparison.Ordinal))
        {
            throw InvalidQuery($"Order must be {ErrorSearchOrder.Default}.");
        }
        if (Cursor is not null && SnapshotReference is null)
        {
            throw InvalidQuery("A cursor must be accompanied by its snapshot reference.");
        }
        ValidateLength(normalizedFilter.SeriesId, 64, "SeriesId");
        ValidateLength(normalizedFilter.DemandId, 64, "DemandId");
        ValidateLength(normalizedFilter.SublotContains, 256, "SUBLOT contains text");

        var knownCategories = SeriesErrorCatalog.Definitions
            .Select(definition => definition.Category)
            .ToHashSet(StringComparer.Ordinal);
        var knownCodes = SeriesErrorCatalog.Definitions
            .Select(definition => definition.Code)
            .ToHashSet(StringComparer.Ordinal);
        var knownStates = ErrorSearchActivityStates.All.ToHashSet(StringComparer.Ordinal);
        if (normalizedFilter.Categories.Any(category => !knownCategories.Contains(category)))
        {
            throw InvalidQuery("category contains an unsupported exact value.");
        }
        if (normalizedFilter.ErrorCodes.Any(code => !knownCodes.Contains(code)))
        {
            throw InvalidQuery("code contains an unsupported exact value.");
        }
        if (normalizedFilter.ActivityStates.Any(state => !knownStates.Contains(state)))
        {
            throw InvalidQuery("state contains an unsupported exact value.");
        }
        if (normalizedFilter.Categories.Count > 0 && normalizedFilter.ErrorCodes.Count > 0)
        {
            var selectedCodeCategories = normalizedFilter.ErrorCodes
                .Select(code => SeriesErrorCatalog.GetRequired(code).Category)
                .ToHashSet(StringComparer.Ordinal);
            if (!normalizedFilter.Categories.Any(selectedCodeCategories.Contains))
            {
                throw InvalidQuery("The selected categories and SeriesErrorCodes cannot match.");
            }
        }

        return this with { Filter = normalizedFilter, Window = normalizedWindow };
    }

    private static void ValidateLength(string? value, int maximum, string name)
    {
        if (value is { Length: > 0 } && value.Length > maximum)
        {
            throw InvalidQuery($"{name} must not exceed {maximum} characters.");
        }
    }

    private static ErrorSearchException InvalidQuery(string message) =>
        new(ErrorSearchErrorCodes.InvalidQuery, message);
}

public sealed record ErrorSearchSnapshotIdentity(
    DateTimeOffset ErrorSearchAsOf,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    string ContractVersion = NewMesIngestContract.Version);

public sealed record ErrorSearchSnapshotReference(
    ErrorSearchSnapshotIdentity Snapshot,
    ErrorSearchFilter Filter,
    ErrorSearchResolvedWindow Window,
    string Order);

public sealed record ErrorSearchCategoryFacetSnapshot(
    string Category,
    long SeriesCount);

public sealed record ErrorSearchActivityStateFacetSnapshot(
    string State,
    long SeriesCount);

public sealed record ErrorSearchFacets(
    IReadOnlyList<ErrorSearchCategoryFacetSnapshot> Categories,
    IReadOnlyList<ErrorSearchActivityStateFacetSnapshot> ActivityStates);

public sealed record ErrorSearchMatchedErrorSnapshot(
    string Code,
    string Category,
    string Severity);

public static class ErrorSearchMesAreaAvailability
{
    public const string CurrentTrusted = "CURRENT_TRUSTED";
    public const string LastTrusted = "LAST_TRUSTED";
    public const string Unknown = "UNKNOWN";
    public const string Invalid = "INVALID";
}

public sealed record ErrorSearchListItemSnapshot(
    string SeriesId,
    string WorkType,
    string Sublot,
    string ActivityState,
    IReadOnlyList<ErrorSearchMatchedErrorSnapshot> MatchedErrors,
    DateTimeOffset LatestMatchedEvidenceAt,
    int MatchedPeriodCount,
    int MatchedDemandGenerationCount,
    string? MesArea,
    string MesAreaAvailability);

public sealed record ErrorSearchListSnapshot(
    string SnapshotReference,
    ErrorSearchSnapshotIdentity Snapshot,
    ErrorSearchFilter Filter,
    ErrorSearchResolvedWindow Window,
    string Order,
    long TotalSeriesCount,
    ErrorSearchFacets Facets,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<ErrorSearchListItemSnapshot> Items,
    string? NextCursor,
    bool HasMore);

public sealed record ErrorSearchCursor(
    string ContractVersion,
    string SnapshotHash,
    string FilterHash,
    string WindowHash,
    string Order,
    int PageSize,
    int TargetPageNumber,
    int? AfterActivityRank,
    DateTimeOffset? AfterLatestMatchedEvidenceAt,
    string? AfterSeriesId);

public static class ErrorSearchErrorCodes
{
    public const string InvalidQuery = "INVALID_ERROR_SEARCH_QUERY";
    public const string ProjectionNotAvailable = "ERROR_SEARCH_PROJECTION_NOT_AVAILABLE";
    public const string InvalidSnapshotReference = "INVALID_ERROR_SEARCH_SNAPSHOT_REFERENCE";
    public const string SnapshotNotFound = "ERROR_SEARCH_SNAPSHOT_NOT_FOUND";
    public const string SnapshotMismatch = "ERROR_SEARCH_SNAPSHOT_MISMATCH";
    public const string InvalidCursor = "INVALID_ERROR_SEARCH_CURSOR";
    public const string ObjectNotInSnapshot = "ERROR_SEARCH_OBJECT_NOT_IN_SNAPSHOT";
    public const string RawAccessDenied = "RAW_EVIDENCE_ACCESS_DENIED";
    public const string RawFieldNotAllowed = "RAW_EVIDENCE_FIELD_NOT_ALLOWED";
    public const string RawLimitExceeded = "RAW_EVIDENCE_LIMIT_EXCEEDED";
}

public sealed class ErrorSearchException : Exception
{
    public ErrorSearchException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public ErrorSearchException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public sealed record ErrorSearchTokenError(string Code, string Message);
