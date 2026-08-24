namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Frozen identity and capability inventory of the replacement MesIngest V2
/// product contract. Consumers must compare the whole identity before they
/// interpret any business response.
/// </summary>
public static class NewMesIngestContract
{
    public const string Version = "2026.08.new-mes-ingest.v2.0";

    public const int SchemaVersion = 27;

    public const string CompatibilityPolicy = "EXACT_VERSION_SCHEMA_AND_CAPABILITIES";

    public const string OpenApiDocumentPath = "/openapi/v2.json";

    /// <summary>
    /// WorkType and SUBLOT are compared ordinally and case-sensitively. Their
    /// exact non-blank values, including leading and trailing whitespace, are
    /// preserved and participate in identity.
    /// </summary>
    public const string KeyComparison = "ORDINAL_CASE_SENSITIVE_WHITESPACE_PRESERVING";

    public static IReadOnlyList<NewMesIngestCapability> Capabilities { get; } =
    [
        new(
            "CONTRACT_DISCOVERY",
            "1.0",
            [Get("/api/v2/contract")]),
        new(
            "CURRENT_INGEST_ATTENTION",
            "1.0",
            [Get("/api/v2/current-ingest-attention")]),
        new(
            "DEMAND_SERIES",
            "1.0",
            [
                Get("/api/v2/demand-series"),
                Get("/api/v2/demand-series/by-key"),
                Get("/api/v2/demand-series/{seriesId}"),
            ]),
        new(
            "ERROR_SEARCH",
            "1.0",
            [
                Get("/api/v2/error-search"),
                Get("/api/v2/error-search/{seriesId}"),
                Get("/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations"),
            ]),
        new(
            "EXTERNALLY_READABLE_DEMAND_CATALOG",
            "1.0",
            [Get("/api/v2/externally-readable-demand-catalog")]),
        new(
            "POLL_HEALTH_AND_EVIDENCE",
            "1.0",
            [
                Get("/api/v2/poll-traces/{pollTraceId}"),
                Get("/api/v2/absence-authority"),
                Get("/api/v2/absence-authority/{hostSessionId}"),
                Get("/api/v2/task-type-protections"),
                Get("/api/v2/task-type-protections/{workType}"),
            ]),
        new(
            "READABILITY_AUDIT",
            "1.0",
            [
                Get("/api/v2/readability-audit"),
                Get("/api/v2/readability-audit/{demandId}"),
            ]),
        new(
            "SERIES_ERROR_CATALOG",
            "1.0",
            [Get("/api/v2/contract")]),
        new(
            "WATCH_OVERVIEW",
            "1.0",
            [Get("/api/v2/watch-overview")]),
    ];

    /// <summary>
    /// Refuses business interpretation unless version, schema and the complete
    /// capability ID set are identical. There is deliberately no additive or
    /// missing-field fallback at this boundary.
    /// </summary>
    public static void RequireExactCompatibility(
        string? contractVersion,
        int schemaVersion,
        IEnumerable<string>? capabilityIds)
    {
        var expectedCapabilities = Capabilities
            .Select(capability => capability.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualCapabilities = capabilityIds?
            .Where(id => id is not null)
            .Order(StringComparer.Ordinal)
            .ToArray()
            ?? [];

        if (!string.Equals(contractVersion, Version, StringComparison.Ordinal)
            || schemaVersion != SchemaVersion
            || actualCapabilities.Length != actualCapabilities.Distinct(StringComparer.Ordinal).Count()
            || !actualCapabilities.SequenceEqual(expectedCapabilities, StringComparer.Ordinal))
        {
            throw new NewMesIngestContractMismatchException(
                contractVersion,
                schemaVersion,
                actualCapabilities);
        }
    }

    private static NewMesIngestOperation Get(string path) => new("GET", path);
}

public sealed record NewMesIngestOperation(string Method, string Path);

public sealed record NewMesIngestCapability(
    string Id,
    string Version,
    IReadOnlyList<NewMesIngestOperation> Operations);

public static class PollEvidenceErrorCodes
{
    public const string InvalidQuery = "INVALID_POLL_EVIDENCE_QUERY";
    public const string InvalidPollTraceId = "INVALID_POLL_TRACE_ID";
    public const string InvalidHostSessionId = "INVALID_HOST_SESSION_ID";
    public const string InvalidWorkType = "INVALID_WORK_TYPE";
    public const string MesIngestHistoryExpired = "MES_INGEST_HISTORY_EXPIRED";
    public const string PollTraceNotFound = "POLL_TRACE_NOT_FOUND";
    public const string AbsenceAuthorityNotFound = "ABSENCE_AUTHORITY_NOT_FOUND";
    public const string TaskTypeProtectionNotFound = "TASK_TYPE_PROTECTION_NOT_FOUND";
}

public sealed class NewMesIngestContractMismatchException : Exception
{
    public const string ErrorCode = "CONTRACT_VERSION_MISMATCH";

    public NewMesIngestContractMismatchException(
        string? actualVersion,
        int actualSchemaVersion,
        IReadOnlyList<string> actualCapabilities)
        : base(
            $"MesIngest business interpretation was refused: expected contract "
            + $"'{NewMesIngestContract.Version}' schema {NewMesIngestContract.SchemaVersion} "
            + "and the exact frozen capability set, but received "
            + $"'{actualVersion ?? "<missing>"}' schema {actualSchemaVersion} with "
            + $"[{string.Join(", ", actualCapabilities)}].")
    {
    }

    public string Code => ErrorCode;
}
