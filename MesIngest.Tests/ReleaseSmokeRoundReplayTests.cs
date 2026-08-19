using System.Text.Json;
using MesIngest.Core;
using MesIngest.Host;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 24: the packaged release smoke drives rounds from a recording when no
/// factory Oracle is reachable. The switch has to be impossible to enable by
/// accident and impossible to mistake for live plant evidence.
/// </summary>
public sealed class ReleaseSmokeRoundReplayTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mes-ingest-replay-options-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void Unconfigured_hosts_get_no_replay_executor()
    {
        Assert.Null(ReleaseSmokeRoundReplay.Resolve(new MesIngestHostOptions()));
    }

    [Fact]
    public void Acknowledged_recording_resolves_a_replay_executor_that_cannot_claim_live_oracle()
    {
        var options = ConfiguredOptions();

        var executor = ReleaseSmokeRoundReplay.Resolve(options);

        Assert.NotNull(executor);
        Assert.Equal(ReplayedMesTaskUnionStatementExecutor.DriverName, executor.Identity.Driver);
        Assert.False(executor.RuntimeState.CanAttestLiveOracle);
    }

    [Fact]
    public void Recording_without_the_exact_acknowledgement_refuses_to_start()
    {
        var options = ConfiguredOptions();
        options.ReplayRoundsAcknowledgement = "yes";

        var failure = Assert.Throws<InvalidOperationException>(() => ReleaseSmokeRoundReplay.Resolve(options));

        Assert.Contains(ReleaseSmokeRoundReplay.RequiredAcknowledgement, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Replayed_rounds_are_refused_for_the_live_oracle_probe()
    {
        var options = ConfiguredOptions();

        var failure = Assert.Throws<InvalidOperationException>(
            () => ReleaseSmokeRoundReplay.ValidateProbeIsLive(options));

        Assert.Contains("--probe-oracle", failure.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private MesIngestHostOptions ConfiguredOptions()
    {
        var path = Path.Combine(_directory, "rounds.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                queryVersion = CanonicalMesTaskUnionQuery.QueryVersion,
                columns = new[]
                {
                    new { name = "TASK_TYPE", kind = "Text" },
                    new { name = "SUBLOT", kind = "Text" },
                    new { name = "AREA", kind = "Text" },
                    new { name = "EQP", kind = "Text" },
                    new { name = "STEP", kind = "Text" },
                    new { name = "DATES", kind = "DateTimeOffset" },
                    new { name = "PACKAGE", kind = "Text" },
                },
                rounds = new[]
                {
                    new
                    {
                        rows = new[]
                        {
                            new[]
                            {
                                "PKG", "SUBLOT-1", "AREA-1", "EQP-1", "STEP-1",
                                "2026-08-01T09:00:00+08:00", "PACKAGE-1",
                            },
                        },
                    },
                },
            }));

        return new MesIngestHostOptions
        {
            ReplayRoundsFromRecordingPath = path,
            ReplayRoundsAcknowledgement = ReleaseSmokeRoundReplay.RequiredAcknowledgement,
        };
    }
}
