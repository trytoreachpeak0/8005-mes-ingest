namespace MesIngest.Core.SeriesProjection;

public static class ErrorSearchDiagnosticValueKinds
{
    public const string Scalar = "SCALAR";
    public const string WorkTypeMembership = "WORK_TYPE_MEMBERSHIP";
    public const string RawObservationSet = "RAW_OBSERVATION_SET";
}

/// <summary>
/// A deliberately bounded explanation of one diagnostic value. Raw observation
/// sets are represented only by their count and canonical SHA-256 digest.
/// </summary>
public sealed record ErrorSearchDiagnosticValueSnapshot(
    string Kind,
    string? ScalarValue = null,
    int? ObservationCount = null,
    string? Sha256Digest = null);

public sealed record ErrorSearchDetailEvidenceSnapshot(
    string EvidenceId,
    string EvidenceKind,
    string SubjectKind,
    DateTimeOffset ObservedAt,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    IReadOnlyList<string> RelatedWorkTypes,
    ErrorSearchDiagnosticValueSnapshot DiagnosticValue,
    string ExpectedRule,
    bool RawEvidenceAvailable);

public sealed record ErrorSearchDetailPeriodSnapshot(
    string PeriodId,
    string Code,
    string Category,
    string Severity,
    string Target,
    string SubjectKind,
    string StartReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    bool StartsBeforeWindow,
    bool EndsAfterWindow,
    bool ActiveAtAsOf,
    IReadOnlyList<ErrorSearchDetailEvidenceSnapshot> Evidence);

public sealed record ErrorSearchDetailSnapshot(
    string SnapshotReference,
    ErrorSearchSnapshotIdentity Snapshot,
    ErrorSearchFilter Filter,
    ErrorSearchResolvedWindow Window,
    string Order,
    ErrorSearchListItemSnapshot Series,
    IReadOnlyList<ErrorSearchDetailPeriodSnapshot> Periods);

public static class ErrorSearchRawEvidenceFields
{
    public const string WorkType = "workType";
    public const string Sublot = "sublot";
    public const string Area = "area";
    public const string Eqp = "eqp";
    public const string Step = "step";
    public const string MesSourceDate = "mesSourceDate";
    public const string Package = "package";

    public static IReadOnlyList<string> All { get; } =
    [
        WorkType,
        Sublot,
        Area,
        Eqp,
        Step,
        MesSourceDate,
        Package,
    ];
}

public static class ErrorSearchRawEvidenceLimits
{
    public const int MaximumItems = 20;
    public const int MaximumItemBytes = 2_048;
    public const int MaximumTotalBytes = 65_536;
}

public sealed record ErrorSearchRawEvidenceQuery(
    IReadOnlyList<string> Fields,
    int MaxItems = ErrorSearchRawEvidenceLimits.MaximumItems)
{
    public ErrorSearchRawEvidenceQuery NormalizeAndValidate()
    {
        if (MaxItems is < 1 or > ErrorSearchRawEvidenceLimits.MaximumItems)
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.RawLimitExceeded,
                $"MaxItems must be between 1 and {ErrorSearchRawEvidenceLimits.MaximumItems}.");
        }

        var requested = Fields ?? Array.Empty<string>();
        var allowed = ErrorSearchRawEvidenceFields.All.ToHashSet(StringComparer.Ordinal);
        if (requested.Any(field => string.IsNullOrWhiteSpace(field) || !allowed.Contains(field)))
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.RawFieldNotAllowed,
                "The raw evidence request contains a field that is not allowed.");
        }

        return this with
        {
            Fields = requested.Distinct(StringComparer.Ordinal).ToArray(),
        };
    }
}

public sealed record ErrorSearchRawEvidenceLimitsSnapshot(
    int MaxItems,
    int MaxItemBytes,
    int MaxTotalBytes);

public sealed record ErrorSearchRawEvidenceItemSnapshot(
    int Ordinal,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    DateTimeOffset ObservedAt,
    IReadOnlyDictionary<string, string?> Fields);

public sealed record ErrorSearchRawEvidenceSnapshot(
    string SnapshotReference,
    ErrorSearchSnapshotIdentity Snapshot,
    string SeriesId,
    string PeriodId,
    string EvidenceId,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    IReadOnlyList<string> IncludedFields,
    int ItemCount,
    ErrorSearchRawEvidenceLimitsSnapshot Limits,
    int PayloadBytes,
    IReadOnlyList<ErrorSearchRawEvidenceItemSnapshot> Items);
