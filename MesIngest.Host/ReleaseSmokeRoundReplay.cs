using MesIngest.Core;

namespace MesIngest.Host;

/// <summary>
/// Development-and-release-smoke switch that lets recorded rounds drive the
/// production MES_TASK_UNION entry when no factory Oracle is reachable. It changes
/// only where the statement result comes from; the canonical artifact, the round
/// source, and the projection commit boundary stay production code.
///
/// It is deliberately awkward to enable: the recording path alone is not enough,
/// the operator must also spell out that the rounds are not factory evidence, and
/// <c>--probe-oracle</c> refuses to run against it at all.
/// </summary>
public static class ReleaseSmokeRoundReplay
{
    public const string RequiredAcknowledgement = "RELEASE_SMOKE_NOT_FACTORY_EVIDENCE";

    public static bool IsConfigured(MesIngestHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !string.IsNullOrWhiteSpace(options.ReplayRoundsFromRecordingPath);
    }

    /// <summary>
    /// Returns the recorded executor, or null when no recording is configured.
    /// Throws when a recording is configured without the exact acknowledgement.
    /// </summary>
    public static IOracleStatementExecutor? Resolve(MesIngestHostOptions options)
    {
        if (!IsConfigured(options))
        {
            return null;
        }

        if (!string.Equals(
                options.ReplayRoundsAcknowledgement?.Trim(),
                RequiredAcknowledgement,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "MesIngest:ReplayRoundsFromRecordingPath serves recorded rounds instead of the plant "
                + $"database. Set MesIngest:ReplayRoundsAcknowledgement to {RequiredAcknowledgement} "
                + "to confirm the resulting rounds are not factory acceptance evidence.");
        }

        return ReplayedMesTaskUnionStatementExecutor.Load(
            options.ReplayRoundsFromRecordingPath,
            options.ParseOracleMode());
    }

    /// <summary>
    /// The Oracle probe exists to prove a live plant connection, so a recording can
    /// never stand in for it.
    /// </summary>
    public static void ValidateProbeIsLive(MesIngestHostOptions options)
    {
        if (IsConfigured(options))
        {
            throw new InvalidOperationException(
                "MesIngest:ReplayRoundsFromRecordingPath cannot be set for --probe-oracle; "
                + "the probe must attempt a live Oracle connection.");
        }
    }
}
