namespace MesIngest.Core;

/// <summary>
/// Shared Watch↔Host read-API contract identity. Bump when breaking response/request shapes.
/// </summary>
public static class MesIngestApiContract
{
    /// <summary>HTTP/JSON contract version exposed by Host and expected by Watch.</summary>
    public const string Version = "2026.08.watch-ops.1";

    /// <summary>SQL projection schema generation recorded by EnsureSchema (additive upgrades).</summary>
    public const int SchemaVersion = 2;

    public const string MismatchErrorCode = "CONTRACT_VERSION_MISMATCH";
}

/// <summary>Payload for GET /api/contract.</summary>
public sealed record MesIngestContractInfo(
    string ContractVersion,
    int SchemaVersion);
