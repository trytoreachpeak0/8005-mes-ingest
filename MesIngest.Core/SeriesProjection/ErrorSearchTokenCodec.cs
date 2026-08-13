using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MesIngest.Core.SeriesProjection;

public static class ErrorSearchTokenCodec
{
    private const string SnapshotPurpose = "error-search-snapshot-v1";
    private const string CursorPurpose = "error-search-cursor-v1";
    private const int MinimumKeyLength = 32;
    private const int MaximumTokenLength = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string CreateSnapshotReference(
        ErrorSearchSnapshotReference snapshot,
        ReadOnlySpan<byte> persistentKey)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        return CreateToken(SnapshotPurpose, snapshot, persistentKey);
    }

    public static bool TryReadSnapshotReference(
        string token,
        ReadOnlySpan<byte> persistentKey,
        out ErrorSearchSnapshotReference? snapshot,
        out ErrorSearchTokenError? error)
    {
        if (!TryReadToken(token, SnapshotPurpose, persistentKey, out snapshot, out error))
        {
            return false;
        }

        try
        {
            ValidateSnapshot(snapshot!);
        }
        catch (ArgumentException exception)
        {
            snapshot = null;
            error = new(ErrorSearchErrorCodes.InvalidSnapshotReference, exception.Message);
            return false;
        }
        catch (ErrorSearchException exception)
        {
            snapshot = null;
            error = new(ErrorSearchErrorCodes.InvalidSnapshotReference, exception.Message);
            return false;
        }

        if (!string.Equals(
                snapshot!.Snapshot.ContractVersion,
                NewMesIngestContract.Version,
                StringComparison.Ordinal))
        {
            snapshot = null;
            error = new(
                ErrorSearchErrorCodes.SnapshotMismatch,
                "The Error Search snapshot belongs to another contract version.");
            return false;
        }
        return true;
    }

    public static string CreateCursor(
        ErrorSearchSnapshotReference snapshot,
        int pageSize,
        int targetPageNumber,
        int? afterActivityRank,
        DateTimeOffset? afterLatestMatchedEvidenceAt,
        string? afterSeriesId,
        ReadOnlySpan<byte> persistentKey)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        ValidatePageBinding(pageSize, targetPageNumber);
        ValidateAnchor(
            targetPageNumber,
            afterActivityRank,
            afterLatestMatchedEvidenceAt,
            afterSeriesId);

        return CreateToken(
            CursorPurpose,
            new ErrorSearchCursor(
                snapshot.Snapshot.ContractVersion,
                ComputeSnapshotHash(snapshot.Snapshot),
                ComputeFilterHash(snapshot.Filter),
                ComputeWindowHash(snapshot.Window),
                snapshot.Order,
                pageSize,
                targetPageNumber,
                afterActivityRank,
                afterLatestMatchedEvidenceAt?.ToUniversalTime(),
                afterSeriesId),
            persistentKey);
    }

    public static bool TryReadCursor(
        string token,
        ErrorSearchSnapshotReference expectedSnapshot,
        int expectedPageSize,
        ReadOnlySpan<byte> persistentKey,
        out ErrorSearchCursor? cursor,
        out ErrorSearchTokenError? error)
    {
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        if (!TryReadToken(token, CursorPurpose, persistentKey, out cursor, out error))
        {
            return false;
        }

        try
        {
            ValidateSnapshot(expectedSnapshot);
            ValidatePageBinding(cursor!.PageSize, cursor.TargetPageNumber);
            ValidateAnchor(
                cursor.TargetPageNumber,
                cursor.AfterActivityRank,
                cursor.AfterLatestMatchedEvidenceAt,
                cursor.AfterSeriesId);
        }
        catch (ArgumentException exception)
        {
            cursor = null;
            error = new(ErrorSearchErrorCodes.InvalidCursor, exception.Message);
            return false;
        }
        catch (ErrorSearchException exception)
        {
            cursor = null;
            error = new(ErrorSearchErrorCodes.InvalidCursor, exception.Message);
            return false;
        }

        if (!string.Equals(cursor!.ContractVersion, NewMesIngestContract.Version, StringComparison.Ordinal)
            || !string.Equals(cursor.ContractVersion, expectedSnapshot.Snapshot.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(cursor.SnapshotHash, ComputeSnapshotHash(expectedSnapshot.Snapshot), StringComparison.Ordinal)
            || !string.Equals(cursor.FilterHash, ComputeFilterHash(expectedSnapshot.Filter), StringComparison.Ordinal)
            || !string.Equals(cursor.WindowHash, ComputeWindowHash(expectedSnapshot.Window), StringComparison.Ordinal)
            || !string.Equals(cursor.Order, ErrorSearchOrder.Default, StringComparison.Ordinal)
            || !string.Equals(cursor.Order, expectedSnapshot.Order, StringComparison.Ordinal)
            || cursor.PageSize != expectedPageSize)
        {
            cursor = null;
            error = new(
                ErrorSearchErrorCodes.InvalidCursor,
                "The cursor does not belong to this Error Search snapshot, filter, window, order, page size, or contract version.");
            return false;
        }
        return true;
    }

    public static string ComputeSnapshotHash(ErrorSearchSnapshotIdentity snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateIdentity(snapshot);
        return Hash(snapshot);
    }

    public static string ComputeFilterHash(ErrorSearchFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var normalized = filter.Normalize();
        return Hash(new CanonicalFilter(
            normalized.Categories,
            normalized.ErrorCodes,
            normalized.ActivityStates,
            normalized.SeriesId,
            normalized.DemandId,
            normalized.SublotContains));
    }

    public static string ComputeWindowHash(ErrorSearchResolvedWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ValidateResolvedWindow(window);
        return Hash(window with
        {
            FromUtc = window.FromUtc?.ToUniversalTime(),
            ToUtc = window.ToUtc.ToUniversalTime(),
        });
    }

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)));

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
        out ErrorSearchTokenError? error)
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

    private static ErrorSearchTokenError InvalidToken(string purpose, string message) =>
        new(
            string.Equals(purpose, SnapshotPurpose, StringComparison.Ordinal)
                ? ErrorSearchErrorCodes.InvalidSnapshotReference
                : ErrorSearchErrorCodes.InvalidCursor,
            message);

    private static void ValidateSnapshot(ErrorSearchSnapshotReference snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot.Snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Filter);
        ArgumentNullException.ThrowIfNull(snapshot.Window);
        ValidateIdentity(snapshot.Snapshot);
        ValidateResolvedWindow(snapshot.Window);
        if (snapshot.Window.ToUtc > snapshot.Snapshot.ErrorSearchAsOf)
        {
            throw new ArgumentException(
                "The resolved Error Search window cannot extend past ErrorSearchAsOf.",
                nameof(snapshot));
        }
        if (!string.Equals(snapshot.Order, ErrorSearchOrder.Default, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Order must be {ErrorSearchOrder.Default}.", nameof(snapshot));
        }
        var normalizedFilter = new ErrorSearchQuery(
            snapshot.Filter,
            ErrorSearchWindowSelection.Last7Days)
            .NormalizeAndValidate().Filter;
        if (!snapshot.Filter.Categories.SequenceEqual(normalizedFilter.Categories, StringComparer.Ordinal)
            || !snapshot.Filter.ErrorCodes.SequenceEqual(normalizedFilter.ErrorCodes, StringComparer.Ordinal)
            || !snapshot.Filter.ActivityStates.SequenceEqual(
                normalizedFilter.ActivityStates,
                StringComparer.Ordinal)
            || !string.Equals(snapshot.Filter.SeriesId, normalizedFilter.SeriesId, StringComparison.Ordinal)
            || !string.Equals(snapshot.Filter.DemandId, normalizedFilter.DemandId, StringComparison.Ordinal)
            || !string.Equals(
                snapshot.Filter.SublotContains,
                normalizedFilter.SublotContains,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Error Search snapshot filter is not canonical.",
                nameof(snapshot));
        }
    }

    private static void ValidateIdentity(ErrorSearchSnapshotIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ProjectionCommitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.PollTraceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ContractVersion);
        if (identity.ProjectionSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), "ProjectionSequence must be one or greater.");
        }
        if (identity.ErrorSearchAsOf.Offset != TimeSpan.Zero
            || identity.ProjectionCommittedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Error Search snapshot times must be UTC.", nameof(identity));
        }
    }

    private static void ValidateResolvedWindow(ErrorSearchResolvedWindow window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(window.Kind);
        if (window.Kind is not (
                ErrorSearchWindowKinds.Last24Hours
                or ErrorSearchWindowKinds.Last7Days
                or ErrorSearchWindowKinds.Last30Days
                or ErrorSearchWindowKinds.AllHistory
                or ErrorSearchWindowKinds.Custom))
        {
            throw new ArgumentException(
                "The resolved Error Search window kind is unsupported.",
                nameof(window));
        }
        if (window.ToUtc.Offset != TimeSpan.Zero
            || (window.FromUtc is not null && window.FromUtc.Value.Offset != TimeSpan.Zero))
        {
            throw new ArgumentException("Resolved Error Search window times must be UTC.", nameof(window));
        }
        if (window.FromUtc is not null && window.FromUtc.Value >= window.ToUtc)
        {
            throw new ArgumentException("The resolved Error Search window is invalid.", nameof(window));
        }
        if (window.Kind == ErrorSearchWindowKinds.AllHistory && window.FromUtc is not null)
        {
            throw new ArgumentException(
                "An all-history Error Search window cannot have a lower bound.",
                nameof(window));
        }
        if (window.Kind is (
                ErrorSearchWindowKinds.Last24Hours
                or ErrorSearchWindowKinds.Last7Days
                or ErrorSearchWindowKinds.Last30Days)
            && window.FromUtc is null)
        {
            throw new ArgumentException(
                "A bounded Error Search window requires a lower bound.",
                nameof(window));
        }
    }

    private static void ValidatePageBinding(int pageSize, int targetPageNumber)
    {
        if (pageSize is < 1 or > ErrorSearchQuery.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        if (targetPageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPageNumber));
        }
    }

    private static void ValidateAnchor(
        int targetPageNumber,
        int? activityRank,
        DateTimeOffset? evidenceAt,
        string? seriesId)
    {
        var hasAny = activityRank is not null || evidenceAt is not null || seriesId is not null;
        var hasAll = activityRank is not null && evidenceAt is not null && seriesId is not null;
        if (hasAny != hasAll || (targetPageNumber > 1 && !hasAll))
        {
            throw new ArgumentException("A cursor keyset anchor must be complete after page one.");
        }
        if (activityRank is not null && activityRank is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(activityRank));
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
            if (!string.Equals(Base64UrlEncode(bytes), text, StringComparison.Ordinal))
            {
                bytes = [];
                return false;
            }
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private sealed record CanonicalFilter(
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> ErrorCodes,
        IReadOnlyList<string> ActivityStates,
        string? SeriesId,
        string? DemandId,
        string? SublotContains);
}
