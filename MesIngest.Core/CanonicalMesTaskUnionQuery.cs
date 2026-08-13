using System.Security.Cryptography;
using System.Text;

namespace MesIngest.Core;

public enum CanonicalQueryArtifactFailure
{
    Missing,
    Empty,
    DigestMismatch,
    InvalidUtf8,
}

public sealed class CanonicalQueryArtifactException : IOException
{
    public CanonicalQueryArtifactException(
        CanonicalQueryArtifactFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public CanonicalQueryArtifactFailure Failure { get; }
}

public sealed record CanonicalQueryArtifact(
    string Id,
    string QueryVersion,
    string Sha256,
    long ByteLength,
    string Sql);

/// <summary>
/// The sole authority for the approved MES_TASK_UNION statement. The digest is
/// deliberately compiled into the service, so replacing both SQL and a package
/// manifest cannot authorize a different statement.
/// </summary>
public static class CanonicalMesTaskUnionQuery
{
    public const string Id = "MES_TASK_UNION";
    public const string ExpectedSha256 =
        "54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae";
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
                "The canonical MES_TASK_UNION query artifact is missing.");
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
        {
            throw new CanonicalQueryArtifactException(
                CanonicalQueryArtifactFailure.Empty,
                "The canonical MES_TASK_UNION query artifact is empty.");
        }

        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, ExpectedSha256, StringComparison.Ordinal))
        {
            throw new CanonicalQueryArtifactException(
                CanonicalQueryArtifactFailure.DigestMismatch,
                "The canonical MES_TASK_UNION query artifact digest is not approved.");
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
                "The canonical MES_TASK_UNION query artifact is not valid UTF-8.",
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
