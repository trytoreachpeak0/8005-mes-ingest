using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Creates tamper-evident, restart-stable snapshot references and cursors. The
/// caller owns and persists the HMAC key; tokens contain no secret material.
/// </summary>
public static class DemandSeriesSnapshotTokenCodec
{
    private const string SnapshotPurpose = "demand-series-snapshot-v3";
    private const string CursorPurpose = "demand-series-cursor-v2";
    private const int MinimumKeyLength = 32;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static string CreateSnapshotReference(
        DemandSeriesSnapshotIdentity identity,
        ReadOnlySpan<byte> persistentKey) =>
        CreateSnapshotReference(identity, DateTimeOffset.MinValue, persistentKey);

    public static string CreateSnapshotReference(
        DemandSeriesSnapshotIdentity identity,
        DateTimeOffset rawAvailabilityCutoff,
        ReadOnlySpan<byte> persistentKey)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateIdentity(identity);
        return CreateToken(
            SnapshotPurpose,
            new SnapshotEnvelope(identity, rawAvailabilityCutoff.ToUniversalTime()),
            persistentKey);
    }

    public static bool TryReadSnapshotReference(
        string token,
        ReadOnlySpan<byte> persistentKey,
        out DemandSeriesSnapshotIdentity? identity,
        out DemandSeriesBrowseTokenError? error) =>
        TryReadSnapshotReference(
            token,
            persistentKey,
            out identity,
            out _,
            out error);

    public static bool TryReadSnapshotReference(
        string token,
        ReadOnlySpan<byte> persistentKey,
        out DemandSeriesSnapshotIdentity? identity,
        out DateTimeOffset rawAvailabilityCutoff,
        out DemandSeriesBrowseTokenError? error)
    {
        identity = null;
        rawAvailabilityCutoff = default;
        if (!TryReadToken<SnapshotEnvelope>(
                token,
                SnapshotPurpose,
                persistentKey,
                out var envelope,
                out error))
        {
            return false;
        }

        identity = envelope!.Identity;
        rawAvailabilityCutoff = envelope.RawAvailabilityCutoff;

        try
        {
            ValidateIdentity(identity!);
        }
        catch (ArgumentException exception)
        {
            identity = null;
            error = new(
                DemandSeriesBrowseErrorCodes.InvalidSnapshotReference,
                exception.Message);
            return false;
        }

        if (!string.Equals(identity!.ContractVersion, NewMesIngestContract.Version, StringComparison.Ordinal))
        {
            identity = null;
            error = new(
                DemandSeriesBrowseErrorCodes.SnapshotMismatch,
                "The snapshot reference belongs to another contract version.");
            return false;
        }

        return true;
    }

    public static string CreateCursor(
        DemandSeriesSnapshotIdentity snapshot,
        DemandSeriesBrowseFilter filter,
        string order,
        int pageSize,
        int targetPageNumber,
        DateTimeOffset? afterStartedAt,
        string? afterSeriesId,
        ReadOnlySpan<byte> persistentKey)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(filter);
        ValidateIdentity(snapshot);
        ValidatePageBinding(order, pageSize, targetPageNumber);
        if (afterStartedAt.HasValue != (afterSeriesId is not null))
        {
            throw new ArgumentException(
                "A keyset cursor must contain both AfterStartedAt and AfterSeriesId, or neither.");
        }
        if (targetPageNumber > 1 && afterStartedAt is null)
        {
            throw new ArgumentException("A cursor after page one must contain a keyset anchor.");
        }

        var cursor = new DemandSeriesBrowseCursor(
            snapshot.ContractVersion,
            snapshot.HistoryEpoch,
            snapshot.ProjectionCommitId,
            snapshot.ProjectionSequence,
            ComputeFilterHash(filter),
            order,
            pageSize,
            targetPageNumber,
            afterStartedAt,
            afterSeriesId);
        return CreateToken(CursorPurpose, cursor, persistentKey);
    }

    public static bool TryReadCursor(
        string token,
        DemandSeriesSnapshotIdentity expectedSnapshot,
        DemandSeriesBrowseFilter expectedFilter,
        string expectedOrder,
        int expectedPageSize,
        ReadOnlySpan<byte> persistentKey,
        out DemandSeriesBrowseCursor? cursor,
        out DemandSeriesBrowseTokenError? error)
    {
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        ArgumentNullException.ThrowIfNull(expectedFilter);
        if (!TryReadToken(token, CursorPurpose, persistentKey, out cursor, out error))
        {
            return false;
        }

        try
        {
            ValidatePageBinding(cursor!.Order, cursor.PageSize, cursor.TargetPageNumber);
        }
        catch (ArgumentException exception)
        {
            cursor = null;
            error = new(DemandSeriesBrowseErrorCodes.InvalidCursor, exception.Message);
            return false;
        }

        var expectedFilterHash = ComputeFilterHash(expectedFilter);
        if (!string.Equals(cursor!.ContractVersion, NewMesIngestContract.Version, StringComparison.Ordinal)
            || !string.Equals(cursor.ContractVersion, expectedSnapshot.ContractVersion, StringComparison.Ordinal)
            || cursor.HistoryEpoch != expectedSnapshot.HistoryEpoch
            || !string.Equals(cursor.ProjectionCommitId, expectedSnapshot.ProjectionCommitId, StringComparison.Ordinal)
            || cursor.ProjectionSequence != expectedSnapshot.ProjectionSequence
            || !string.Equals(cursor.FilterHash, expectedFilterHash, StringComparison.Ordinal)
            || !string.Equals(cursor.Order, expectedOrder, StringComparison.Ordinal)
            || cursor.PageSize != expectedPageSize)
        {
            cursor = null;
            error = new(
                DemandSeriesBrowseErrorCodes.CursorMismatch,
                "The cursor does not belong to this snapshot, filter, order, page size, or contract version.");
            return false;
        }

        if (cursor.AfterStartedAt.HasValue != (cursor.AfterSeriesId is not null))
        {
            cursor = null;
            error = new(
                DemandSeriesBrowseErrorCodes.InvalidCursor,
                "The cursor contains an incomplete keyset anchor.");
            return false;
        }
        if (cursor.TargetPageNumber > 1 && cursor.AfterStartedAt is null)
        {
            cursor = null;
            error = new(
                DemandSeriesBrowseErrorCodes.InvalidCursor,
                "A cursor after page one has no keyset anchor.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stable SHA-256 binding for the normalized filter. The hash is safe to put
    /// in a token and avoids exposing potentially sensitive business identifiers.
    /// </summary>
    public static string ComputeFilterHash(DemandSeriesBrowseFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var normalized = filter.Normalize();
        var canonical = JsonSerializer.SerializeToUtf8Bytes(
            new CanonicalFilter(
                normalized.Lifecycles,
                normalized.CurrentPresences,
                normalized.WorkTypes,
                normalized.SublotContains,
                normalized.Sublot,
                normalized.SeriesId,
                normalized.DemandId,
                normalized.MesAreas),
            JsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static string CreateToken<T>(
        string purpose,
        T payload,
        ReadOnlySpan<byte> persistentKey)
    {
        ValidateKey(persistentKey);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var payloadText = Base64UrlEncode(payloadBytes);
        var signedText = $"{purpose}.{payloadText}";
        var signature = HMACSHA256.HashData(persistentKey, Encoding.UTF8.GetBytes(signedText));
        return $"{payloadText}.{Base64UrlEncode(signature)}";
    }

    private static bool TryReadToken<T>(
        string token,
        string purpose,
        ReadOnlySpan<byte> persistentKey,
        out T? value,
        out DemandSeriesBrowseTokenError? error)
        where T : class
    {
        value = null;
        error = null;
        ValidateKey(persistentKey);
        if (string.IsNullOrWhiteSpace(token))
        {
            error = InvalidToken(purpose, "The token is empty.");
            return false;
        }

        var separator = token.IndexOf('.');
        if (separator <= 0 || separator != token.LastIndexOf('.') || separator == token.Length - 1)
        {
            error = InvalidToken(purpose, "The token has an invalid envelope.");
            return false;
        }

        var payloadText = token[..separator];
        var signatureText = token[(separator + 1)..];
        if (!TryBase64UrlDecode(payloadText, out var payloadBytes)
            || !TryBase64UrlDecode(signatureText, out var signature)
            || signature.Length != 32)
        {
            error = InvalidToken(purpose, "The token has invalid base64url data.");
            return false;
        }

        var signedText = $"{purpose}.{payloadText}";
        var expected = HMACSHA256.HashData(persistentKey, Encoding.UTF8.GetBytes(signedText));
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

    private static DemandSeriesBrowseTokenError InvalidToken(string purpose, string message) =>
        new(
            string.Equals(purpose, SnapshotPurpose, StringComparison.Ordinal)
                ? DemandSeriesBrowseErrorCodes.InvalidSnapshotReference
                : DemandSeriesBrowseErrorCodes.InvalidCursor,
            message);

    private static void ValidateIdentity(DemandSeriesSnapshotIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity.HistoryEpoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ProjectionCommitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.PollTraceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ContractVersion);
        if (identity.ProjectionSequence < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(identity),
                "ProjectionSequence must be one or greater.");
        }
    }

    private static void ValidatePageBinding(string order, int pageSize, int targetPageNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(order);
        if (pageSize is < 1 or > DemandSeriesBrowseQuery.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                $"Page size must be between 1 and {DemandSeriesBrowseQuery.MaximumPageSize}.");
        }

        if (targetPageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetPageNumber),
                "Target page number must be one or greater.");
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

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new HistoryEpochJsonConverter());
        return options;
    }

    private static bool TryBase64UrlDecode(string text, out byte[] bytes)
    {
        if (text.Any(character =>
                !(character is >= 'A' and <= 'Z'
                  or >= 'a' and <= 'z'
                  or >= '0' and <= '9'
                  or '-'
                  or '_')))
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        var padding = (4 - text.Length % 4) % 4;
        var base64 = text.Replace('-', '+').Replace('_', '/') + new string('=', padding);
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private sealed record CanonicalFilter(
        IReadOnlyList<string> Lifecycles,
        IReadOnlyList<string> CurrentPresences,
        IReadOnlyList<string> WorkTypes,
        string? SublotContains,
        string? Sublot,
        string? SeriesId,
        string? DemandId,
        IReadOnlyList<string> MesAreas);

    private sealed record SnapshotEnvelope(
        DemandSeriesSnapshotIdentity Identity,
        DateTimeOffset RawAvailabilityCutoff);
}
