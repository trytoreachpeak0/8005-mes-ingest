using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MesIngest.Core.SeriesProjection;

public static class ReadabilityAuditTokenCodec
{
    private const string SnapshotPurpose = "readability-audit-snapshot-v1";
    private const string CursorPurpose = "readability-audit-cursor-v1";
    private const int MinimumKeyLength = 32;
    private const int MaximumTokenLength = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string CreateSnapshotReference(
        ReadabilityAuditSnapshotIdentity identity,
        ReadOnlySpan<byte> persistentKey)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateIdentity(identity);
        return CreateToken(SnapshotPurpose, identity, persistentKey);
    }

    public static bool TryReadSnapshotReference(
        string token,
        ReadOnlySpan<byte> persistentKey,
        out ReadabilityAuditSnapshotIdentity? identity,
        out ReadabilityAuditTokenError? error)
    {
        if (!TryReadToken(token, SnapshotPurpose, persistentKey, out identity, out error))
        {
            return false;
        }

        try
        {
            ValidateIdentity(identity!);
        }
        catch (ArgumentException exception)
        {
            identity = null;
            error = new(ReadabilityAuditErrorCodes.InvalidSnapshotReference, exception.Message);
            return false;
        }

        if (!string.Equals(identity!.ContractVersion, NewMesIngestContract.Version, StringComparison.Ordinal))
        {
            identity = null;
            error = new(
                ReadabilityAuditErrorCodes.SnapshotMismatch,
                "The snapshot reference belongs to another contract version.");
            return false;
        }
        return true;
    }

    public static string CreateCursor(
        ReadabilityAuditSnapshotIdentity snapshot,
        ReadabilityAuditFilter filter,
        string order,
        int pageSize,
        int targetPageNumber,
        int? afterReadabilityRank,
        int? afterLeadBlockerPriority,
        DateTimeOffset? afterDemandLastSeenAt,
        string? afterDemandId,
        ReadOnlySpan<byte> persistentKey)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(filter);
        ValidateIdentity(snapshot);
        ValidatePageBinding(order, pageSize, targetPageNumber);
        if (!string.Equals(order, ReadabilityAuditOrder.Default, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Order must be {ReadabilityAuditOrder.Default}.",
                nameof(order));
        }
        var hasAnyAnchor = afterReadabilityRank is not null
            || afterLeadBlockerPriority is not null
            || afterDemandLastSeenAt is not null
            || afterDemandId is not null;
        var hasCompleteAnchor = afterReadabilityRank is not null
            && afterLeadBlockerPriority is not null
            && afterDemandLastSeenAt is not null
            && afterDemandId is not null;
        if (hasAnyAnchor != hasCompleteAnchor)
        {
            throw new ArgumentException("A cursor keyset anchor must be complete or absent.");
        }
        if (targetPageNumber > 1 && !hasCompleteAnchor)
        {
            throw new ArgumentException("A cursor after page one must contain a keyset anchor.");
        }

        return CreateToken(
            CursorPurpose,
            new ReadabilityAuditCursor(
                snapshot.ContractVersion,
                snapshot.ProjectionCommitId,
                snapshot.ProjectionSequence,
                snapshot.CatalogRevision,
                ComputeFilterHash(filter),
                order,
                pageSize,
                targetPageNumber,
                afterReadabilityRank,
                afterLeadBlockerPriority,
                afterDemandLastSeenAt,
                afterDemandId),
            persistentKey);
    }

    public static bool TryReadCursor(
        string token,
        ReadabilityAuditSnapshotIdentity expectedSnapshot,
        ReadabilityAuditFilter expectedFilter,
        string expectedOrder,
        int expectedPageSize,
        ReadOnlySpan<byte> persistentKey,
        out ReadabilityAuditCursor? cursor,
        out ReadabilityAuditTokenError? error)
    {
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        ArgumentNullException.ThrowIfNull(expectedFilter);
        if (!TryReadToken(token, CursorPurpose, persistentKey, out cursor, out error))
        {
            return false;
        }

        try
        {
            ValidateIdentity(expectedSnapshot);
            ValidatePageBinding(cursor!.Order, cursor.PageSize, cursor.TargetPageNumber);
        }
        catch (ArgumentException exception)
        {
            cursor = null;
            error = new(ReadabilityAuditErrorCodes.InvalidCursor, exception.Message);
            return false;
        }

        if (!string.Equals(cursor!.ContractVersion, NewMesIngestContract.Version, StringComparison.Ordinal)
            || !string.Equals(cursor.ContractVersion, expectedSnapshot.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(cursor.ProjectionCommitId, expectedSnapshot.ProjectionCommitId, StringComparison.Ordinal)
            || cursor.ProjectionSequence != expectedSnapshot.ProjectionSequence
            || cursor.CatalogRevision != expectedSnapshot.CatalogRevision
            || !string.Equals(cursor.FilterHash, ComputeFilterHash(expectedFilter), StringComparison.Ordinal)
            || !string.Equals(cursor.Order, expectedOrder, StringComparison.Ordinal)
            || !string.Equals(cursor.Order, ReadabilityAuditOrder.Default, StringComparison.Ordinal)
            || cursor.PageSize != expectedPageSize)
        {
            cursor = null;
            error = new(
                ReadabilityAuditErrorCodes.CursorMismatch,
                "The cursor does not belong to this audit snapshot, filter, AREA scope, order, page size, or contract version.");
            return false;
        }

        var hasAnyAnchor = cursor.AfterReadabilityRank is not null
            || cursor.AfterLeadBlockerPriority is not null
            || cursor.AfterDemandLastSeenAt is not null
            || cursor.AfterDemandId is not null;
        var hasCompleteAnchor = cursor.AfterReadabilityRank is not null
            && cursor.AfterLeadBlockerPriority is not null
            && cursor.AfterDemandLastSeenAt is not null
            && cursor.AfterDemandId is not null;
        if (hasAnyAnchor != hasCompleteAnchor || (cursor.TargetPageNumber > 1 && !hasCompleteAnchor))
        {
            cursor = null;
            error = new(ReadabilityAuditErrorCodes.InvalidCursor, "The cursor keyset anchor is incomplete.");
            return false;
        }
        return true;
    }

    public static string ComputeFilterHash(ReadabilityAuditFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var normalized = filter.Normalize();
        var canonical = JsonSerializer.SerializeToUtf8Bytes(
            new CanonicalFilter(
                normalized.ReadabilityStates,
                normalized.WorkTypes,
                normalized.Blockers,
                normalized.DemandId,
                normalized.SublotContains,
                normalized.MesAreas),
            JsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static string CreateToken<T>(string purpose, T payload, ReadOnlySpan<byte> key)
    {
        ValidateKey(key);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var payloadText = Base64UrlEncode(payloadBytes);
        var signedText = $"{purpose}.{payloadText}";
        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signedText));
        return $"{payloadText}.{Base64UrlEncode(signature)}";
    }

    private static bool TryReadToken<T>(
        string token,
        string purpose,
        ReadOnlySpan<byte> key,
        out T? value,
        out ReadabilityAuditTokenError? error)
        where T : class
    {
        value = null;
        error = null;
        ValidateKey(key);
        if (string.IsNullOrWhiteSpace(token))
        {
            error = InvalidToken(purpose, "The token is empty.");
            return false;
        }
        if (token.Length > MaximumTokenLength)
        {
            error = InvalidToken(purpose, "The token exceeds the maximum supported length.");
            return false;
        }
        var separator = token.IndexOf('.');
        if (separator <= 0 || separator != token.LastIndexOf('.') || separator == token.Length - 1)
        {
            error = InvalidToken(purpose, "The token has an invalid envelope.");
            return false;
        }
        var payloadText = token[..separator];
        if (!TryBase64UrlDecode(payloadText, out var payloadBytes)
            || !TryBase64UrlDecode(token[(separator + 1)..], out var signature)
            || signature.Length != 32)
        {
            error = InvalidToken(purpose, "The token has invalid base64url data.");
            return false;
        }
        var expected = HMACSHA256.HashData(
            key,
            Encoding.UTF8.GetBytes($"{purpose}.{payloadText}"));
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            error = InvalidToken(purpose, "The token signature is invalid.");
            return false;
        }
        try
        {
            value = JsonSerializer.Deserialize<T>(payloadBytes, JsonOptions);
            if (value is null)
            {
                error = InvalidToken(purpose, "The token payload is empty.");
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            error = InvalidToken(purpose, "The token payload is invalid.");
            return false;
        }
    }

    private static ReadabilityAuditTokenError InvalidToken(string purpose, string message) =>
        new(
            string.Equals(purpose, SnapshotPurpose, StringComparison.Ordinal)
                ? ReadabilityAuditErrorCodes.InvalidSnapshotReference
                : ReadabilityAuditErrorCodes.InvalidCursor,
            message);

    private static void ValidateIdentity(ReadabilityAuditSnapshotIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ProjectionCommitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.PollTraceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ContractVersion);
        if (identity.ProjectionSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), "ProjectionSequence must be one or greater.");
        }
        if (identity.CatalogRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), "CatalogRevision cannot be negative.");
        }
    }

    private static void ValidatePageBinding(string order, int pageSize, int targetPageNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(order);
        if (pageSize is < 1 or > ReadabilityAuditQuery.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        if (targetPageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPageNumber));
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length < MinimumKeyLength)
        {
            throw new ArgumentException(
                $"The persistent HMAC key must contain at least {MinimumKeyLength} bytes.",
                nameof(key));
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string text, out byte[] bytes)
    {
        if (text.Any(character =>
                !(character is >= 'A' and <= 'Z'
                  or >= 'a' and <= 'z'
                  or >= '0' and <= '9'
                  or '-'
                  or '_')))
        {
            bytes = [];
            return false;
        }
        try
        {
            var padding = (4 - text.Length % 4) % 4;
            bytes = Convert.FromBase64String(
                text.Replace('-', '+').Replace('_', '/') + new string('=', padding));
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private sealed record CanonicalFilter(
        IReadOnlyList<string> ReadabilityStates,
        IReadOnlyList<string> WorkTypes,
        IReadOnlyList<string> Blockers,
        string? DemandId,
        string? SublotContains,
        IReadOnlyList<string> MesAreas);
}
