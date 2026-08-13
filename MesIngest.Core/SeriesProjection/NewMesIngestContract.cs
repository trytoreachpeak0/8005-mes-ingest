namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Identity of the isolated new-MesIngest tracer contract. The complete product
/// contract is frozen by ticket 17; this version covers the round-evidence spine.
/// </summary>
public static class NewMesIngestContract
{
    public const string Version = "2026.08.new-mes-ingest.tracer.6";

    public const int SchemaVersion = 6;

    /// <summary>
    /// WorkType and SUBLOT are compared ordinally and case-sensitively. Their
    /// exact non-blank values, including leading and trailing whitespace, are
    /// preserved and participate in identity.
    /// </summary>
    public const string KeyComparison = "ORDINAL_CASE_SENSITIVE_WHITESPACE_PRESERVING";
}
