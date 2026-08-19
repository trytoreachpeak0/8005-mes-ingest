using System.Text;
using MesIngest.Core;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;

namespace MesIngest.Tests;

public class OracleProbeTests
{
    [Fact]
    public async Task Caller_supplied_live_identity_is_rejected_without_running_the_fake_source()
    {
        var source = new FixedRoundSource(new MesTaskUnionRound(
            "poll-live",
            CanonicalMesTaskUnionQuery.QueryVersion,
            MesTaskUnionRoundOutcome.Success,
            DateTimeOffset.Parse("2026-08-14T01:02:03+00:00"),
            DateTimeOffset.Parse("2026-08-14T01:02:04+00:00"),
            [new MesTaskUnionObservation("WB", "S-1", "A1-1", "EQ-1", "STEP", null, "PKG")]));
        var output = new StringWriter();

        var code = await OracleProbe.RunRoundAsync(
            source,
            output,
            OracleRoundProbeIdentity.Live(
                OracleClientMode.Thin,
                OracleClientMode.Thin,
                "Oracle.ManagedDataAccess.Core"));

        Assert.Equal(3, code);
        Assert.Equal(0, source.ReadCount);
        var text = output.ToString();
        Assert.Contains("execution_scope=LIVE_ORACLE", text);
        Assert.Contains("connection_attempted=true", text);
        Assert.Contains("requested_mode=Thin", text);
        Assert.Contains("actual_mode=Thin", text);
        Assert.Contains("driver=Oracle.ManagedDataAccess.Core", text);
        Assert.Contains($"query_id={CanonicalMesTaskUnionQuery.Id}", text);
        Assert.Contains($"query_version={CanonicalMesTaskUnionQuery.QueryVersion}", text);
        Assert.Contains($"query_sha256={CanonicalMesTaskUnionQuery.ExpectedSha256}", text);
        Assert.Contains("outcome=SIMULATED_IDENTITY_REJECTED", text);
        Assert.Contains("row_count=0", text);
        Assert.Contains("duration_ms=", text);
        Assert.Contains("result=NOT_EXECUTED", text);
        Assert.DoesNotContain("result=PASSED", text);
    }

    [Theory]
    [InlineData(OracleProbeExecutionScope.OfflineArtifactOnly)]
    [InlineData(OracleProbeExecutionScope.Simulated)]
    public async Task Non_live_probe_is_not_executed_and_cannot_be_reported_as_a_live_pass(
        OracleProbeExecutionScope scope)
    {
        var source = new FixedRoundSource(new MesTaskUnionRound(
            "poll-fake",
            CanonicalMesTaskUnionQuery.QueryVersion,
            MesTaskUnionRoundOutcome.Success,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            []));
        var output = new StringWriter();

        var code = await OracleProbe.RunRoundAsync(
            source,
            output,
            OracleRoundProbeIdentity.NotExecuted(
                scope,
                OracleClientMode.Thick,
                "Oracle ODBC (factory not connected)"));

        Assert.Equal(3, code);
        Assert.Equal(0, source.ReadCount);
        var text = output.ToString();
        Assert.Contains("connection_attempted=false", text);
        Assert.Contains("result=NOT_EXECUTED", text);
        Assert.DoesNotContain("result=PASSED", text);
    }

    [Fact]
    public async Task Caller_cannot_turn_a_simulated_probe_into_PASSED_by_claiming_a_connection_attempt()
    {
        var source = new FixedRoundSource(new MesTaskUnionRound(
            "poll-forged-simulated",
            CanonicalMesTaskUnionQuery.QueryVersion,
            MesTaskUnionRoundOutcome.Success,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            []));
        var output = new StringWriter();
        var forgedIdentity = new OracleRoundProbeIdentity(
            OracleProbeExecutionScope.Simulated,
            ConnectionAttempted: true,
            OracleClientMode.Thin,
            OracleClientMode.Thin,
            "forged-test-driver");

        var code = await OracleProbe.RunRoundAsync(source, output, forgedIdentity);

        Assert.Equal(3, code);
        Assert.Equal(0, source.ReadCount);
        Assert.Contains("outcome=SIMULATED_IDENTITY_REJECTED", output.ToString());
        Assert.Contains("result=NOT_EXECUTED", output.ToString());
        Assert.DoesNotContain("result=PASSED", output.ToString());
    }

    [Fact]
    public async Task Caller_supplied_live_identity_cannot_expose_fake_round_diagnostics()
    {
        const string secret = "SuperSecret123";
        var source = new FixedRoundSource(new MesTaskUnionRound(
            "poll-failed",
            CanonicalMesTaskUnionQuery.QueryVersion,
            MesTaskUnionRoundOutcome.Failure,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [],
            new MesTaskUnionRoundDiagnostic(
                "ORACLE_EXECUTION",
                "ORACLE_QUERY_EXECUTION_FAILED",
                $"Password={secret}; raw SUBLOT=S-SECRET")));
        var output = new StringWriter();

        var code = await OracleProbe.RunRoundAsync(
            source,
            output,
            OracleRoundProbeIdentity.Live(
                OracleClientMode.Thick,
                OracleClientMode.Thick,
                "System.Data.Odbc"));

        Assert.Equal(3, code);
        Assert.Equal(0, source.ReadCount);
        var text = output.ToString();
        Assert.Contains("outcome=SIMULATED_IDENTITY_REJECTED", text);
        Assert.Contains("result=NOT_EXECUTED", text);
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("S-SECRET", text);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_supplied_live_identity_cannot_execute_a_throwing_fake_source()
    {
        const string secret = "NeverPrintThis";
        var source = new ThrowingRoundSource(new InvalidOperationException(
            $"User Id=mes;Password={secret};Data Source=host/orcl"));
        var output = new StringWriter();

        var code = await OracleProbe.RunRoundAsync(
            source,
            output,
            OracleRoundProbeIdentity.Live(
                OracleClientMode.Thin,
                OracleClientMode.Thin,
                "Oracle.ManagedDataAccess.Core"));

        Assert.Equal(3, code);
        Assert.Equal(0, source.ReadCount);
        var text = output.ToString();
        Assert.Contains("outcome=SIMULATED_IDENTITY_REJECTED", text);
        Assert.Contains("result=NOT_EXECUTED", text);
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("host/orcl", text);
        Assert.DoesNotContain("User Id", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Thin_and_thick_probe_states_are_independent_factory_evidence()
    {
        var thin = OracleRoundProbeManifestState.FromOutput(
            "probe-thin.txt",
            $"execution_scope=LIVE_ORACLE\nconnection_attempted=true\nrequested_mode=Thin\nactual_mode=Thin\nquery_id={CanonicalMesTaskUnionQuery.Id}\nquery_version={CanonicalMesTaskUnionQuery.QueryVersion}\nquery_sha256={CanonicalMesTaskUnionQuery.ExpectedSha256}\noutcome=Success\nresult=PASSED\n");
        var thick = OracleRoundProbeManifestState.FromOutput(
            "probe-thick.txt",
            "execution_scope=OFFLINE_ARTIFACT_ONLY\nconnection_attempted=false\nrequested_mode=Thick\nactual_mode=NOT_AVAILABLE\nresult=NOT_EXECUTED\n");

        Assert.Equal("PASSED", thin.Result);
        Assert.True(thin.ConnectionAttempted);
        Assert.Equal("Thin", thin.RequestedMode);
        Assert.Equal("NOT_EXECUTED", thick.Result);
        Assert.False(thick.ConnectionAttempted);
        Assert.Equal("Thick", thick.RequestedMode);
        Assert.Equal("probe-thick.txt", thick.Log);
    }

    [Fact]
    public void Imported_live_pass_requires_success_and_the_exact_canonical_query_identity()
    {
        var missingIdentity = OracleRoundProbeManifestState.FromOutput(
            "probe-thin.txt",
            "execution_scope=LIVE_ORACLE\nconnection_attempted=true\nrequested_mode=Thin\nactual_mode=Thin\nresult=PASSED\n");
        var wrongDigest = OracleRoundProbeManifestState.FromOutput(
            "probe-thin.txt",
            $"execution_scope=LIVE_ORACLE\nconnection_attempted=true\nrequested_mode=Thin\nactual_mode=Thin\nquery_id={CanonicalMesTaskUnionQuery.Id}\nquery_version={CanonicalMesTaskUnionQuery.QueryVersion}\nquery_sha256={new string('0', 64)}\noutcome=Success\nresult=PASSED\n");

        Assert.Equal("FAILED", missingIdentity.Result);
        Assert.Equal("FAILED", wrongDigest.Result);
    }

    [Fact]
    public void Imported_non_live_pass_is_demoted_to_not_executed()
    {
        var state = OracleRoundProbeManifestState.FromOutput(
            "probe-thin.txt",
            "execution_scope=SIMULATED\nconnection_attempted=false\nrequested_mode=Thin\nactual_mode=Thin\nresult=PASSED\n");

        Assert.Equal("NOT_EXECUTED", state.Result);
    }

    [Fact]
    public async Task Production_probe_does_not_treat_an_injected_executor_identity_as_a_live_attempt()
    {
        var executor = new ProbeExecutor(
            new OracleExecutorIdentity(
                OracleClientMode.Thick,
                OracleClientMode.Thick,
                "System.Data.Odbc (Oracle OCI/Instant Client)"));
        var source = new OracleMesTaskUnionRoundSource(
            new OracleSnapshotOptions
            {
                Mode = OracleClientMode.Thick,
                QuerySqlPath = Path.Combine(
                    AppContext.BaseDirectory,
                    "queries",
                    "mes-task-union",
                    "query.sql"),
            },
            executor);
        var output = new StringWriter();

        var code = await OracleProbe.RunAsync(source, output);

        Assert.Equal(3, code);
        var text = output.ToString();
        Assert.Contains("execution_scope=NOT_EXECUTED", text);
        Assert.Contains("connection_attempted=false", text);
        Assert.Contains("requested_mode=Thick", text);
        Assert.Contains("actual_mode=Thick", text);
        Assert.Contains("driver=System.Data.Odbc (Oracle OCIInstant Client)", text);
        Assert.Contains("result=NOT_EXECUTED", text);
        Assert.DoesNotContain("result=PASSED", text);
    }

    [Fact]
    public async Task Production_probe_reports_NOT_EXECUTED_when_provider_configuration_prevents_a_connection_attempt()
    {
        var source = new OracleMesTaskUnionRoundSource(new OracleSnapshotOptions
        {
            Mode = OracleClientMode.Thick,
            QuerySqlPath = Path.Combine(
                AppContext.BaseDirectory,
                "queries",
                "mes-task-union",
                "query.sql"),
            InstantClientDir = "",
            ThickOdbcDriver = "Oracle in instantclient",
            User = "MES",
            Password = "secret",
            DataSource = "private-host",
        });
        var output = new StringWriter();

        var code = await OracleProbe.RunAsync(source, output);

        Assert.Equal(3, code);
        var text = output.ToString();
        Assert.Contains("execution_scope=NOT_EXECUTED", text);
        Assert.Contains("connection_attempted=false", text);
        Assert.Contains("result=NOT_EXECUTED", text);
        Assert.DoesNotContain("result=PASSED", text);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-host", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_probe_rejects_an_executor_identity_that_does_not_match_the_configured_mode()
    {
        var source = new OracleMesTaskUnionRoundSource(
            new OracleSnapshotOptions
            {
                Mode = OracleClientMode.Thick,
                QuerySqlPath = Path.Combine(
                    AppContext.BaseDirectory,
                    "queries",
                    "mes-task-union",
                    "query.sql"),
            },
            new ProbeExecutor(new OracleExecutorIdentity(
                OracleClientMode.Thin,
                OracleClientMode.Thin,
                "Oracle.ManagedDataAccess.Core")));
        var output = new StringWriter();

        var code = await OracleProbe.RunAsync(source, output);

        Assert.Equal(3, code);
        Assert.Contains("requested_mode=Thick", output.ToString());
        Assert.Contains("actual_mode=Thin", output.ToString());
        Assert.Contains("result=NOT_EXECUTED", output.ToString());
        Assert.DoesNotContain("result=PASSED", output.ToString());
    }

    [Fact]
    public void Published_queries_match_repo_mes_task_union_manuscript()
    {
        var published = Path.Combine(
            AppContext.BaseDirectory, "queries", "mes-task-union", "query.sql");
        Assert.True(File.Exists(published), $"Missing published SQL at {published}");

        var manuscript = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "queries", "mes-task-union", "query.sql"));
        Assert.True(File.Exists(manuscript), $"Missing manuscript at {manuscript}");

        var publishedText = File.ReadAllText(published, Encoding.UTF8);
        var manuscriptText = File.ReadAllText(manuscript, Encoding.UTF8);
        Assert.Equal(manuscriptText, publishedText);
        Assert.Contains("UNION ALL", publishedText, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedRoundSource(MesTaskUnionRound round) : IMesTaskUnionRoundSource
    {
        public int ReadCount { get; private set; }

        public Task<MesTaskUnionRound> ReadRoundAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(round);
        }
    }

    private sealed class ThrowingRoundSource(Exception error) : IMesTaskUnionRoundSource
    {
        public int ReadCount { get; private set; }

        public Task<MesTaskUnionRound> ReadRoundAsync(CancellationToken cancellationToken = default) =>
            throw RecordRead();

        private Exception RecordRead()
        {
            ReadCount++;
            return error;
        }
    }

    private sealed class ProbeExecutor(OracleExecutorIdentity identity) : IOracleStatementExecutor
    {
        public OracleExecutorIdentity Identity { get; } = identity;

        public Task<OracleStatementResult> ExecuteAsync(
            OracleStatementRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OracleStatementResult(
                [
                    new("TASK_TYPE", "VARCHAR2", OracleColumnKind.Text),
                    new("SUBLOT", "VARCHAR2", OracleColumnKind.Text),
                    new("AREA", "VARCHAR2", OracleColumnKind.Text),
                    new("EQP", "VARCHAR2", OracleColumnKind.Text),
                    new("STEP", "VARCHAR2", OracleColumnKind.Text),
                    new("DATES", "DATE", OracleColumnKind.DateTime),
                    new("PACKAGE", "VARCHAR2", OracleColumnKind.Text),
                ],
                []));
    }
}
