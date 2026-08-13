namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// The complete technical fence for one operational read. Business facts use
/// ProjectionSequence; unsuccessful polls, which deliberately have no commit,
/// use PollTraceHighWater.
/// </summary>
public sealed record OperationalSnapshotIdentity(
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    long PollTraceHighWater,
    long CatalogRevision,
    DateTimeOffset SnapshotAsOf,
    string ContractVersion = NewMesIngestContract.Version);

public static class CurrentIngestAttentionKinds
{
    public const string SeriesError = "SERIES_ERROR";
    public const string PollRunFailure = "POLL_RUN_FAILURE";
    public const string TaskTypeProtection = "TASK_TYPE_PROTECTION";
    public const string UnassignedMesObservation = "UNASSIGNED_MES_OBSERVATION";

    public static IReadOnlyList<string> All { get; } =
        [SeriesError, PollRunFailure, TaskTypeProtection, UnassignedMesObservation];
}

public static class CurrentIngestAttentionSeverities
{
    public const string Error = "ERROR";
    public const string Warning = "WARNING";

    public static IReadOnlyList<string> All { get; } = [Error, Warning];
}

public static class CurrentIngestAttentionOrder
{
    public const string Default = "SEVERITY_DESC_OCCURRED_AT_DESC_STABLE_IDENTITY_ASC";
}

public sealed record CurrentIngestAttentionQuery(
    int PageSize = 100,
    int PageNumber = 1,
    string Order = CurrentIngestAttentionOrder.Default,
    IReadOnlyList<string>? Kinds = null,
    IReadOnlyList<string>? Severities = null)
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    public CurrentIngestAttentionQuery NormalizeAndValidate()
    {
        if (PageSize is < 1 or > MaximumPageSize)
        {
            throw new CurrentIngestAttentionException(
                CurrentIngestAttentionErrorCodes.InvalidQuery,
                $"PageSize must be between 1 and {MaximumPageSize}.");
        }

        if (PageNumber < 1 || PageNumber > int.MaxValue / PageSize)
        {
            throw new CurrentIngestAttentionException(
                CurrentIngestAttentionErrorCodes.InvalidQuery,
                "PageNumber is outside the supported range.");
        }

        if (!string.Equals(Order, CurrentIngestAttentionOrder.Default, StringComparison.Ordinal))
        {
            throw new CurrentIngestAttentionException(
                CurrentIngestAttentionErrorCodes.InvalidQuery,
                $"Order must be {CurrentIngestAttentionOrder.Default}.");
        }

        var normalizedKinds = NormalizeSet(Kinds);
        var normalizedSeverities = NormalizeSet(Severities);
        if (normalizedKinds.Any(value => !CurrentIngestAttentionKinds.All.Contains(
                value,
                StringComparer.Ordinal)))
        {
            throw new CurrentIngestAttentionException(
                CurrentIngestAttentionErrorCodes.InvalidQuery,
                "Kinds contains an unsupported exact value.");
        }

        if (normalizedSeverities.Any(value => !CurrentIngestAttentionSeverities.All.Contains(
                value,
                StringComparer.Ordinal)))
        {
            throw new CurrentIngestAttentionException(
                CurrentIngestAttentionErrorCodes.InvalidQuery,
                "Severities contains an unsupported exact value.");
        }

        return this with { Kinds = normalizedKinds, Severities = normalizedSeverities };
    }

    private static IReadOnlyList<string> NormalizeSet(IReadOnlyList<string>? values) =>
        (values ?? Array.Empty<string>())
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}

public sealed record CurrentIngestAttentionFacetSnapshot(
    string Value,
    long ItemCount);

public sealed record CurrentIngestAttentionFacets(
    IReadOnlyList<CurrentIngestAttentionFacetSnapshot> Types,
    IReadOnlyList<CurrentIngestAttentionFacetSnapshot> Severities);

public sealed record CurrentIngestAttentionEvidenceSnapshot(
    string? ProjectionCommitId = null,
    long? ProjectionSequence = null,
    string? PollTraceId = null,
    long? PollTraceSequence = null,
    string? SeriesId = null,
    string? DemandId = null,
    string? WorkType = null,
    int? ObservationOrdinal = null,
    string? EvidenceId = null,
    string? ContentDigest = null,
    string? Phase = null,
    string? Outcome = null);

public sealed record CurrentIngestAttentionItemSnapshot(
    string Kind,
    string Severity,
    DateTimeOffset OccurredAt,
    string StableIdentity,
    string? SeriesId,
    string? WorkType,
    string? ErrorCode,
    string? Target,
    string? SubjectKind,
    CurrentIngestAttentionEvidenceSnapshot Evidence,
    OverviewNavigationIntent Navigation);

public sealed record CurrentIngestAttentionSnapshot(
    OperationalSnapshotIdentity Snapshot,
    long ExactTotalItemCount,
    CurrentIngestAttentionFacets Facets,
    string Order,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Severities,
    IReadOnlyList<CurrentIngestAttentionItemSnapshot> Items);

public static class CurrentIngestAttentionErrorCodes
{
    public const string InvalidQuery = "CURRENT_INGEST_ATTENTION_INVALID_QUERY";
    public const string ProjectionNotAvailable = "CURRENT_INGEST_ATTENTION_PROJECTION_NOT_AVAILABLE";
}

public sealed class CurrentIngestAttentionException : Exception
{
    public CurrentIngestAttentionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
