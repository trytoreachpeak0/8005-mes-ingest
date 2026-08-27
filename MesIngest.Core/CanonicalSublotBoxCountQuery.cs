using System.Security.Cryptography;
using System.Text;

namespace MesIngest.Core;

/// <summary>
/// Compiled authority for the read-only SUBLOT_BOX_COUNT query artifact.
/// </summary>
public static class CanonicalSublotBoxCountQuery
{
    public const string Id = "SUBLOT_BOX_COUNT";
    public const int MaximumSublotLength = 256;
    public const string ExpectedSha256 =
        "4d2784513bfb85506c190cf138d833ad38dc27102dc33df8161c3f41fbaf26ff";
    public const string QueryVersion = Id + "/sha256:" + ExpectedSha256;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static CanonicalQueryArtifact Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new CanonicalQueryArtifactException(
                CanonicalQueryArtifactFailure.Missing,
                "The canonical SUBLOT_BOX_COUNT query artifact is missing.");
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
        {
            throw new CanonicalQueryArtifactException(
                CanonicalQueryArtifactFailure.Empty,
                "The canonical SUBLOT_BOX_COUNT query artifact is empty.");
        }

        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, ExpectedSha256, StringComparison.Ordinal))
        {
            throw new CanonicalQueryArtifactException(
                CanonicalQueryArtifactFailure.DigestMismatch,
                "The canonical SUBLOT_BOX_COUNT query artifact digest is not approved.");
        }

        try
        {
            var sql = StrictUtf8.GetString(bytes);
            return new CanonicalQueryArtifact(Id, QueryVersion, digest, bytes.LongLength, sql);
        }
        catch (DecoderFallbackException exception)
        {
            throw new CanonicalQueryArtifactException(
                CanonicalQueryArtifactFailure.InvalidUtf8,
                "The canonical SUBLOT_BOX_COUNT query artifact is not valid UTF-8.",
                exception);
        }
    }

    public static bool IsApprovedSql(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var digest = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(sql)))
            .ToLowerInvariant();
        return string.Equals(digest, ExpectedSha256, StringComparison.Ordinal);
    }
}

internal static class ApprovedOracleStatementRequest
{
    public static bool IsValid(OracleStatementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CommandTimeoutSeconds <= 0)
        {
            return false;
        }

        var binds = request.BindParameters ?? [];
        if (string.Equals(
                request.QuerySha256,
                CanonicalMesTaskUnionQuery.ExpectedSha256,
                StringComparison.Ordinal)
            && string.Equals(
                request.QueryVersion,
                CanonicalMesTaskUnionQuery.QueryVersion,
                StringComparison.Ordinal)
            && CanonicalMesTaskUnionQuery.IsApprovedSql(request.Sql))
        {
            return binds.Count == 0;
        }

        return string.Equals(
                   request.QuerySha256,
                   CanonicalSublotBoxCountQuery.ExpectedSha256,
                   StringComparison.Ordinal)
               && string.Equals(
                   request.QueryVersion,
                   CanonicalSublotBoxCountQuery.QueryVersion,
                   StringComparison.Ordinal)
               && CanonicalSublotBoxCountQuery.IsApprovedSql(request.Sql)
               && binds is [{ Name: "sublot", Value: var sublot }]
               && !string.IsNullOrWhiteSpace(sublot)
               && sublot.Length <= CanonicalSublotBoxCountQuery.MaximumSublotLength;
    }
}
