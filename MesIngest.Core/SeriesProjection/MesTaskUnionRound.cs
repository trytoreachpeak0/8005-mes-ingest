using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MesIngest.Core.SeriesProjection;

public enum MesTaskUnionRoundOutcome
{
    Success,
    Failure,
    Incomplete,
}

/// <summary>One raw row returned by the MES_TASK_UNION statement.</summary>
public sealed record MesTaskUnionObservation(
    string? WorkType,
    string? Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package);

/// <summary>A complete causal result of one MES_TASK_UNION execution.</summary>
public sealed record MesTaskUnionRound(
    string PollTraceId,
    string QueryVersion,
    MesTaskUnionRoundOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<MesTaskUnionObservation> Observations);

/// <summary>One authoritative implementation of TransportDemandKey identity.</summary>
public static class TransportDemandKeyIdentity
{
    public static string CreateToken(string workType, string sublot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sublot);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Domain order is SUBLOT + WorkType. Length framing avoids delimiter ambiguity.
        Sha256LengthFraming.Append(hash, sublot);
        Sha256LengthFraming.Append(hash, workType);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>Stable content evidence for a complete round.</summary>
public static class MesTaskUnionRoundDigest
{
    public static string Compute(IReadOnlyList<MesTaskUnionObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var normalized = observations
            .Select(Normalize)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in normalized)
        {
            Sha256LengthFraming.Append(hash, value);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Normalize(MesTaskUnionObservation observation)
    {
        var builder = new StringBuilder();
        AppendValue(builder, observation.WorkType);
        AppendValue(builder, observation.Sublot);
        AppendValue(builder, observation.Area);
        AppendValue(builder, observation.Eqp);
        AppendValue(builder, observation.Step);
        AppendValue(
            builder,
            observation.MesSourceDate?.ToString("O", CultureInfo.InvariantCulture));
        AppendValue(builder, observation.Package);
        return builder.ToString();
    }

    private static void AppendValue(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        builder.Append(byteCount.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
    }

}

internal static class Sha256LengthFraming
{
    public static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
