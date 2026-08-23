namespace MesIngest.Core.SeriesProjection;

public static class ExternalReadabilityStates
{
    public const string Readable = "READABLE";
    public const string NotReadable = "NOT_READABLE";
}

public sealed record ReadabilityBlockerDefinition(
    string Code,
    int Priority,
    string Meaning);

/// <summary>
/// Stable contract vocabulary for explaining current external readability.
/// The order is the lead-blocker priority: diagnostic current conditions first,
/// followed by lifecycle consequences. Published codes are never repurposed.
/// </summary>
public static class ReadabilityBlockerCatalog
{
    public static IReadOnlyList<ReadabilityBlockerDefinition> Definitions { get; } =
        Array.AsReadOnly<ReadabilityBlockerDefinition>(
        [
            new("LONG_GONE_BUT_VISIBLE", 10, "An archived DemandSeries is visible in MES again."),
            new("DUPLICATE_TRANSPORT_DEMAND_KEY", 20, "The Demand has multiple current raw observations."),
            new("SUBLOT_MULTIPLE_WORK_TYPES", 30, "The SUBLOT currently appears in multiple WorkTypes."),
            new("REQUIRED_MES_FIELD_MISSING", 40, "At least one required MES field is missing."),
            new("INVALID_MES_FIELD_FORMAT", 50, "At least one MES field has an invalid domain format."),
            new("DEMAND_GONE", 60, "A complete authoritative round confirmed that the Demand is absent."),
            new("SERIES_ARCHIVED", 70, "The Demand belongs to an archived DemandSeries."),
        ]);

    private static readonly IReadOnlyDictionary<string, ReadabilityBlockerDefinition> ByCode =
        Definitions.ToDictionary(definition => definition.Code, StringComparer.Ordinal);

    public static ReadabilityBlockerDefinition GetRequired(string code) =>
        ByCode.TryGetValue(code, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown ReadabilityBlocker code '{code}'.");

    public static IReadOnlyList<string> NormalizeMatchedCodes(IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        return codes
            .Select(GetRequired)
            .DistinctBy(definition => definition.Code, StringComparer.Ordinal)
            .OrderBy(definition => definition.Priority)
            .Select(definition => definition.Code)
            .ToArray();
    }

    public static string? SelectLead(IEnumerable<string> codes) =>
        NormalizeMatchedCodes(codes).FirstOrDefault();
}

/// <summary>
/// OR is used within collection dimensions and AND across dimensions. MesAreas
/// is the local Watch display scope; it is part of the signed query identity and
/// never changes qualification or CatalogRevision.
/// </summary>
public sealed record ReadabilityAuditFilter
{
    public IReadOnlyList<string> ReadabilityStates { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> WorkTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();

    public string? DemandId { get; init; }

    public string? SublotContains { get; init; }

    public IReadOnlyList<string> MesAreas { get; init; } = Array.Empty<string>();

    public ReadabilityAuditFilter Normalize() => this with
    {
        ReadabilityStates = NormalizeTrimmedSet(ReadabilityStates),
        WorkTypes = NormalizePreservedSet(WorkTypes),
        Blockers = NormalizeTrimmedSet(Blockers),
        MesAreas = NormalizeTrimmedSet(MesAreas),
        DemandId = TrimToNull(DemandId),
        SublotContains = PreserveNonBlank(SublotContains),
    };

    private static IReadOnlyList<string> NormalizeTrimmedSet(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> NormalizePreservedSet(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? PreserveNonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

public static class ReadabilityAuditOrder
{
    public const string Default =
        "NOT_READABLE_FIRST_LEAD_PRIORITY_DEMAND_LAST_SEEN_DESC_DEMAND_ID_ASC";
}

public sealed record ReadabilityAuditQuery(
    ReadabilityAuditFilter Filter,
    int PageSize = 100,
    int PageNumber = 1,
    string? SnapshotReference = null,
    string? Cursor = null,
    string Order = ReadabilityAuditOrder.Default)
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    public ReadabilityAuditQuery NormalizeAndValidate()
    {
        var normalizedFilter = (Filter ?? new ReadabilityAuditFilter()).Normalize();
        if (PageSize is < 1 or > MaximumPageSize)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                $"PageSize must be between 1 and {MaximumPageSize}.");
        }
        if (PageNumber < 1)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "PageNumber must be one or greater.");
        }
        if (PageNumber > int.MaxValue / PageSize)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "PageNumber is too large for the requested page size.");
        }
        if (!string.Equals(Order, ReadabilityAuditOrder.Default, StringComparison.Ordinal))
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                $"Order must be {ReadabilityAuditOrder.Default}.");
        }
        if (Cursor is not null && SnapshotReference is null)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "A cursor must be accompanied by its snapshot reference.");
        }
        if (Cursor is not null && PageNumber != 1)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "PageNumber cannot be combined with a cursor.");
        }
        if (Cursor is null && PageNumber > 1 && SnapshotReference is null)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "A direct page after page one must retain its snapshot reference.");
        }
        if (normalizedFilter.DemandId is { Length: > 64 })
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "DemandId must not exceed 64 characters.");
        }
        if (normalizedFilter.SublotContains is { Length: > 256 })
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "SUBLOT contains text must not exceed 256 characters.");
        }
        if (normalizedFilter.WorkTypes.Any(workType => workType.Length > 128))
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "WorkType values must not exceed 128 characters.");
        }

        return this with { Filter = normalizedFilter };
    }
}

public sealed record ReadabilityAuditSnapshotIdentity(
    HistoryEpoch HistoryEpoch,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    long CatalogRevision,
    string ContractVersion = NewMesIngestContract.Version);

public sealed record ReadabilityStateFacetSnapshot(
    string State,
    long DemandCount);

public sealed record ReadabilityBlockerFacetSnapshot(
    string Code,
    long DemandCount);

public sealed record ReadabilityAuditFacets(
    IReadOnlyList<ReadabilityStateFacetSnapshot> ReadabilityStates,
    IReadOnlyList<ReadabilityBlockerFacetSnapshot> Blockers);

public sealed record ReadabilityAuditListItemSnapshot(
    string DemandId,
    string SeriesId,
    string WorkType,
    string Sublot,
    int Generation,
    string? PredecessorDemandId,
    string DemandStatus,
    string SeriesLifecycle,
    string SeriesCurrentPresence,
    bool IsCurrentGeneration,
    DateTimeOffset DemandCreatedAt,
    DateTimeOffset DemandLastSeenAt,
    DateTimeOffset? GoneConfirmedAt,
    LiveMesFieldSetSnapshot? LiveMesFields,
    int CurrentRawObservationCount,
    string ExternalReadabilityState,
    string? LeadReadabilityBlocker,
    IReadOnlyList<string> ReadabilityBlockers,
    string LatestObservationPollTraceId,
    string LatestObservationProjectionCommitId,
    DateTimeOffset LatestObservationAt);

public sealed record ReadabilityAuditListSnapshot(
    ReadabilityAuditSnapshotIdentity Snapshot,
    string SnapshotReference,
    ReadabilityAuditFilter Filter,
    string Order,
    long ExactTotalDemandCount,
    ReadabilityAuditFacets Facets,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<ReadabilityAuditListItemSnapshot> Items,
    string? NextCursor,
    bool HasMore);

public sealed record ReadabilityQualificationCheckDefinition(
    string Code,
    string BlockingCode,
    string Meaning);

public static class ReadabilityQualificationCheckCatalog
{
    public static IReadOnlyList<ReadabilityQualificationCheckDefinition> Definitions { get; } =
        Array.AsReadOnly<ReadabilityQualificationCheckDefinition>(
        [
            new("DEMAND_VISIBLE", "DEMAND_GONE", "The Demand is visible in the current complete source projection."),
            new("SERIES_TRACKING", "SERIES_ARCHIVED", "The owning DemandSeries has not been archived."),
            new("NOT_LONG_GONE_BUT_VISIBLE", "LONG_GONE_BUT_VISIBLE", "The Demand is not a postarchive reappearance."),
            new("UNIQUE_RAW_OBSERVATION", "DUPLICATE_TRANSPORT_DEMAND_KEY", "The current round has exactly one raw observation for the Demand key."),
            new("ONE_WORK_TYPE_PER_SUBLOT", "SUBLOT_MULTIPLE_WORK_TYPES", "The SUBLOT occurs in one WorkType in the current round."),
            new("REQUIRED_MES_FIELDS_PRESENT", "REQUIRED_MES_FIELD_MISSING", "Every required MES field is present and non-whitespace."),
            new("MES_FIELD_FORMAT_VALID", "INVALID_MES_FIELD_FORMAT", "Every present MES field matches its domain format."),
        ]);
}

public sealed record ReadabilityQualificationCheckSnapshot(
    string Code,
    string BlockingCode,
    string Result);

public static class ReadabilityQualificationCheckResults
{
    public const string Passed = "PASS";
    public const string Failed = "FAIL";
    public const string NotEvaluated = "NOT_EVALUATED";
}

public sealed record ReadabilityEvidenceItemSnapshot(
    string SubjectKind,
    string? ObservedValue,
    string ExpectedRule,
    DateTimeOffset ObservedAt,
    string PollTraceId,
    string ProjectionCommitId);

public sealed record ReadabilityBlockerEvidenceSnapshot(
    string Code,
    int Priority,
    IReadOnlyList<ReadabilityEvidenceItemSnapshot> Evidence);

public sealed record ReadabilityAuditSeriesSnapshot(
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string CurrentPresence,
    DateTimeOffset StartedAt,
    DateTimeOffset? ArchivedAt,
    string CurrentDemandId);

public sealed record ReadabilityAuditPollTraceSnapshot(
    string PollTraceId,
    string QueryVersion,
    string Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int RowCount,
    string ContentDigest,
    string ProjectionCommitId,
    long ProjectionSequence);

public sealed record ReadabilityAuditDetailSnapshot(
    ReadabilityAuditSnapshotIdentity Snapshot,
    string SnapshotReference,
    ReadabilityAuditListItemSnapshot Demand,
    ReadabilityAuditSeriesSnapshot Series,
    IReadOnlyList<ReadabilityQualificationCheckSnapshot> QualificationChecks,
    IReadOnlyList<ReadabilityBlockerEvidenceSnapshot> Blockers,
    IReadOnlyList<DemandRawObservationSnapshot> LatestRawObservations,
    ReadabilityAuditPollTraceSnapshot LatestObservationPollTrace);

public sealed record ReadabilityAuditCursor(
    string ContractVersion,
    HistoryEpoch HistoryEpoch,
    string ProjectionCommitId,
    long ProjectionSequence,
    long CatalogRevision,
    string FilterHash,
    string Order,
    int PageSize,
    int TargetPageNumber,
    int? AfterReadabilityRank,
    int? AfterLeadBlockerPriority,
    DateTimeOffset? AfterDemandLastSeenAt,
    string? AfterDemandId);

public static class ReadabilityAuditErrorCodes
{
    public const string InvalidQuery = "INVALID_READABILITY_AUDIT_QUERY";
    public const string ProjectionNotAvailable = "READABILITY_AUDIT_PROJECTION_NOT_AVAILABLE";
    public const string InvalidSnapshotReference = "INVALID_READABILITY_AUDIT_SNAPSHOT_REFERENCE";
    public const string SnapshotNotFound = "READABILITY_AUDIT_SNAPSHOT_NOT_FOUND";
    public const string SnapshotMismatch = "READABILITY_AUDIT_SNAPSHOT_MISMATCH";
    public const string InvalidCursor = "INVALID_READABILITY_AUDIT_CURSOR";
    public const string CursorMismatch = "READABILITY_AUDIT_CURSOR_MISMATCH";
    public const string ObjectNotInSnapshot = "READABILITY_AUDIT_OBJECT_NOT_IN_SNAPSHOT";
}

public sealed class ReadabilityAuditException : Exception
{
    public ReadabilityAuditException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public ReadabilityAuditException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public sealed record ReadabilityAuditTokenError(string Code, string Message);
