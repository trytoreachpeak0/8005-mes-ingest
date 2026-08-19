using System.Text.Json;
using MesIngest.Core;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 24: a release smoke has to drive repeatable rounds through the same
/// production entry when no factory Oracle is reachable. The recorded executor is
/// the only substituted part, so the canonical artifact, the round source, and the
/// projection boundary all stay production code.
/// </summary>
public sealed class ReplayedMesTaskUnionRoundSourceTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mes-ingest-replay-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task Recorded_rounds_drive_the_production_round_source_in_order()
    {
        var path = WriteRecording(
            CanonicalMesTaskUnionQuery.QueryVersion,
            [
                [Row("PKG", "SUBLOT-1", "AREA-1", "EQP-1", "STEP-1", "2026-08-01T09:00:00+08:00", "PACKAGE-1")],
                [
                    Row("PKG", "SUBLOT-1", "AREA-1", "EQP-1", "STEP-2", "2026-08-01T10:00:00+08:00", "PACKAGE-1"),
                    Row("PKG", "SUBLOT-2", "AREA-1", "EQP-2", "STEP-1", "2026-08-01T10:05:00+08:00", "PACKAGE-2"),
                ],
            ]);
        var source = CreateSource(path);

        var first = await source.ReadRoundAsync();
        var second = await source.ReadRoundAsync();
        var afterLast = await source.ReadRoundAsync();

        Assert.Equal(MesTaskUnionRoundOutcome.Success, first.Outcome);
        Assert.Equal(MesTaskUnionRoundOutcome.Success, second.Outcome);
        Assert.Equal(MesTaskUnionRoundOutcome.Success, afterLast.Outcome);
        Assert.Single(first.Observations);
        Assert.Equal(2, second.Observations.Count);
        // A continuous poll loop outlives the recording. Holding the final round
        // keeps the projection stable while PollTrace identity still advances.
        Assert.Equal(2, afterLast.Observations.Count);
        Assert.NotEqual(first.PollTraceId, second.PollTraceId);
        Assert.NotEqual(second.PollTraceId, afterLast.PollTraceId);
        Assert.Equal(CanonicalMesTaskUnionQuery.QueryVersion, first.QueryVersion);
    }

    [Fact]
    public async Task Replayed_rounds_never_attest_a_live_oracle_connection()
    {
        var path = WriteRecording(
            CanonicalMesTaskUnionQuery.QueryVersion,
            [[Row("PKG", "SUBLOT-1", "AREA-1", "EQP-1", "STEP-1", "2026-08-01T09:00:00+08:00", "PACKAGE-1")]]);
        var source = CreateSource(path);

        await source.ReadRoundAsync();

        Assert.Equal("FILE_REPLAY", source.ExecutorIdentity!.Driver);
        Assert.False(source.ExecutorRuntimeState.ConnectionAttempted);
        Assert.False(source.ExecutorRuntimeState.CanAttestLiveOracle);
    }

    [Fact]
    public async Task Recording_for_another_query_version_fails_the_round_instead_of_projecting_it()
    {
        var path = WriteRecording(
            "MES_TASK_UNION/sha256:" + new string('0', 64),
            [[Row("PKG", "SUBLOT-1", "AREA-1", "EQP-1", "STEP-1", "2026-08-01T09:00:00+08:00", "PACKAGE-1")]]);
        var source = CreateSource(path);

        var round = await source.ReadRoundAsync();

        Assert.Equal(MesTaskUnionRoundOutcome.Failure, round.Outcome);
        Assert.Empty(round.Observations);
    }

    [Fact]
    public void Missing_or_invalid_recording_is_refused_with_a_safe_configuration_error()
    {
        var missing = Path.Combine(_directory, "absent.json");
        var invalid = Path.Combine(_directory, "invalid.json");
        File.WriteAllText(invalid, "{ not json");

        var missingFailure = Assert.Throws<OracleProviderConfigurationException>(
            () => ReplayedMesTaskUnionStatementExecutor.Load(missing, OracleClientMode.Thin));
        var invalidFailure = Assert.Throws<OracleProviderConfigurationException>(
            () => ReplayedMesTaskUnionStatementExecutor.Load(invalid, OracleClientMode.Thin));

        Assert.Equal("REPLAY_RECORDING_INVALID", missingFailure.Code);
        Assert.Equal("REPLAY_RECORDING_INVALID", invalidFailure.Code);
        Assert.DoesNotContain(missing, missingFailure.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private OracleMesTaskUnionRoundSource CreateSource(string recordingPath) =>
        new(
            new OracleSnapshotOptions
            {
                Mode = OracleClientMode.Thin,
                CommandTimeoutSeconds = 30,
                QuerySqlPath = RepositoryCanonicalQueryPath,
            },
            ReplayedMesTaskUnionStatementExecutor.Load(recordingPath, OracleClientMode.Thin));

    private static string[] Row(
        string taskType,
        string sublot,
        string area,
        string eqp,
        string step,
        string dates,
        string package) => [taskType, sublot, area, eqp, step, dates, package];

    private string WriteRecording(string queryVersion, string[][][] rounds)
    {
        var path = Path.Combine(_directory, "rounds.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                queryVersion,
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
                rounds = rounds.Select(rows => new { rows }).ToArray(),
            }));
        return path;
    }

    private static string RepositoryCanonicalQueryPath => Path.GetFullPath(
        Path.Combine(CSharpRoot, "..", "..", "queries", "mes-task-union", "query.sql"));

    private static string CSharpRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "pack", "Publish-MesIngest.ps1")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate mes/ingest/csharp root.");
        }
    }
}
