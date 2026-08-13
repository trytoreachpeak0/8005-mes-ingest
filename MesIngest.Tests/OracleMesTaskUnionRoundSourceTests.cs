using MesIngest.Core;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

public sealed class OracleMesTaskUnionRoundSourceTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 14, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task One_round_passes_the_complete_six_branch_artifact_to_exactly_one_executor_call_and_one_result()
    {
        var executor = new RecordingOracleStatementExecutor(CompleteResult(
        [
            ["TYPE-A", "SL-1", " A1-1 ", "", null, "not-a-date", " PKG "],
            ["TYPE-A", "SL-1", " A1-1 ", "", null, "not-a-date", " PKG "],
        ]));
        var source = CreateSource(executor, commandTimeoutSeconds: 47);

        var round = await source.ReadRoundAsync();

        Assert.Equal(MesTaskUnionRoundOutcome.Success, round.Outcome);
        Assert.Equal("poll-ticket15", round.PollTraceId);
        Assert.Equal(CanonicalMesTaskUnionQuery.QueryVersion, round.QueryVersion);
        Assert.Equal(2, round.Observations.Count);
        Assert.Equal(round.Observations[0], round.Observations[1]);
        var observation = round.Observations[0];
        Assert.Equal("TYPE-A", observation.WorkType);
        Assert.Equal("SL-1", observation.Sublot);
        Assert.Equal(" A1-1 ", observation.Area);
        Assert.Equal("", observation.Eqp);
        Assert.Null(observation.Step);
        Assert.Null(observation.MesSourceDate);
        Assert.Equal("not-a-date", observation.MesSourceDateRaw);
        Assert.Equal(" PKG ", observation.Package);
        Assert.Null(round.Diagnostic);

        var request = Assert.Single(executor.Requests);
        Assert.Equal(47, request.CommandTimeoutSeconds);
        Assert.Equal(CanonicalMesTaskUnionQuery.ExpectedSha256, request.QuerySha256);
        Assert.Equal(CanonicalMesTaskUnionQuery.QueryVersion, request.QueryVersion);
        Assert.Equal(
            File.ReadAllText(QueryPath()),
            request.Sql);
        var executableSql = System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(
                request.Sql,
                @"/\*.*?\*/",
                " ",
                System.Text.RegularExpressions.RegexOptions.Singleline),
            @"--[^\r\n]*",
            " ");
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(
            executableSql,
            "UNION\\s+ALL",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
    }

    [Fact]
    public async Task Complete_metadata_with_null_blank_invalid_and_duplicate_values_is_SUCCESS_and_preserves_exact_observations()
    {
        var date = new DateTime(2026, 8, 14, 9, 30, 0, DateTimeKind.Unspecified);
        var executor = new RecordingOracleStatementExecutor(CompleteResult(
        [
            [null, " ", "bad-area", DBNull.Value, "", date, null],
            [null, " ", "bad-area", DBNull.Value, "", date, null],
        ]));

        var round = await CreateSource(executor).ReadRoundAsync();

        Assert.Equal(MesTaskUnionRoundOutcome.Success, round.Outcome);
        Assert.Equal(2, round.Observations.Count);
        Assert.Null(round.Observations[0].WorkType);
        Assert.Equal(" ", round.Observations[0].Sublot);
        Assert.Equal("bad-area", round.Observations[0].Area);
        Assert.Null(round.Observations[0].Eqp);
        Assert.Equal("", round.Observations[0].Step);
        Assert.Equal(new DateTimeOffset(date, TimeSpan.FromHours(8)), round.Observations[0].MesSourceDate);
        Assert.Null(round.Observations[0].MesSourceDateRaw);
        Assert.Null(round.Observations[0].Package);
        Assert.Equal(round.Observations[0], round.Observations[1]);
    }

    [Fact]
    public async Task Timestamp_with_time_zone_metadata_is_compatible_with_round_date_mapping()
    {
        var offset = new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.FromHours(8));
        var columns = CompleteColumns()
            .Select(column => column.Name == "DATES"
                ? new OracleResultColumn(
                    "DATES",
                    "TimeStampTZ",
                    OdpNetOracleStatementExecutor.Classify("TimeStampTZ"))
                : column)
            .ToArray();
        var executor = new RecordingOracleStatementExecutor(new OracleStatementResult(
            columns,
            [["TYPE-A", "SL-1", "A1-1", "EQ-1", "STEP", offset, "PKG"]]));

        var round = await CreateSource(executor).ReadRoundAsync();

        Assert.Equal(MesTaskUnionRoundOutcome.Success, round.Outcome);
        Assert.Equal(offset, Assert.Single(round.Observations).MesSourceDate);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unsupported")]
    [InlineData("dates-text-type")]
    [InlineData("short-row")]
    public async Task Missing_duplicate_unsupported_or_short_required_structure_is_INCOMPLETE_without_raw_rows(
        string defect)
    {
        var result = defect switch
        {
            "missing" => new OracleStatementResult(
                CompleteColumns().Where(column => column.Name != "DATES").ToArray(),
                []),
            "duplicate" => new OracleStatementResult(
                [.. CompleteColumns(), new("DATES", "DATE", OracleColumnKind.DateTime)],
                []),
            "unsupported" => new OracleStatementResult(
                CompleteColumns().Select(column => column.Name == "AREA"
                    ? new OracleResultColumn("AREA", "NUMBER", OracleColumnKind.Unsupported)
                    : column).ToArray(),
                []),
            "dates-text-type" => new OracleStatementResult(
                CompleteColumns().Select(column => column.Name == "DATES"
                    ? new OracleResultColumn("DATES", "VARCHAR2", OracleColumnKind.Text)
                    : column).ToArray(),
                []),
            "short-row" => CompleteResult([["T", "S"]]),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        var round = await CreateSource(new RecordingOracleStatementExecutor(result)).ReadRoundAsync();

        Assert.Equal(MesTaskUnionRoundOutcome.Incomplete, round.Outcome);
        Assert.Empty(round.Observations);
        Assert.Equal("ORACLE_RESULT_STRUCTURE_INVALID", round.Diagnostic?.Code);
        Assert.Equal("RESULT_MAPPING", round.Diagnostic?.Stage);
        Assert.DoesNotContain("T", round.Diagnostic?.SafeDetail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_and_execution_fault_return_safe_FAILURE_without_leaking_secret_sql_datasource_or_raw_values()
    {
        const string sensitive = "Password=plant-secret;Data Source=mes-11g;raw=SENSITIVE-SUBLOT";
        var timeout = await CreateSource(new RecordingOracleStatementExecutor(
            new TimeoutException(sensitive))).ReadRoundAsync();
        var failure = await CreateSource(new RecordingOracleStatementExecutor(
            new InvalidOperationException(sensitive))).ReadRoundAsync();

        AssertFailure(timeout, "ORACLE_QUERY_TIMEOUT");
        AssertFailure(failure, "ORACLE_QUERY_EXECUTION_FAILED");
        foreach (var round in new[] { timeout, failure })
        {
            Assert.Empty(round.Observations);
            Assert.NotNull(round.Diagnostic);
            Assert.DoesNotContain("plant-secret", round.Diagnostic!.SafeDetail, StringComparison.Ordinal);
            Assert.DoesNotContain("mes-11g", round.Diagnostic.SafeDetail, StringComparison.Ordinal);
            Assert.DoesNotContain("SENSITIVE-SUBLOT", round.Diagnostic.SafeDetail, StringComparison.Ordinal);
            Assert.DoesNotContain("SELECT", round.Diagnostic.SafeDetail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Command_cancellation_is_FAILURE_but_host_cancellation_propagates()
    {
        var commandCancelled = await CreateSource(new RecordingOracleStatementExecutor(
            new OperationCanceledException("driver cancelled"))).ReadRoundAsync();

        AssertFailure(commandCancelled, "ORACLE_QUERY_CANCELLED");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateSource(new RecordingOracleStatementExecutor(CompleteResult([])))
                .ReadRoundAsync(cts.Token));
    }

    [Fact]
    public async Task Tampered_artifact_returns_FAILURE_before_executor_call()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mes-task-union-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, "SELECT 1 FROM DUAL");
        var executor = new RecordingOracleStatementExecutor(CompleteResult([]));

        try
        {
            var round = await CreateSource(executor, queryPath: path).ReadRoundAsync();

            AssertFailure(round, "CANONICAL_QUERY_INVALID", "CANONICAL_ARTIFACT");
            Assert.Empty(executor.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static OracleMesTaskUnionRoundSource CreateSource(
        IOracleStatementExecutor executor,
        int commandTimeoutSeconds = 30,
        string? queryPath = null) =>
        new(
            new OracleSnapshotOptions
            {
                Mode = OracleClientMode.Thin,
                QuerySqlPath = queryPath ?? QueryPath(),
                CommandTimeoutSeconds = commandTimeoutSeconds,
            },
            executor,
            new AdjustableTimeProvider(StartedAt),
            () => "poll-ticket15");

    private static OracleStatementResult CompleteResult(IReadOnlyList<IReadOnlyList<object?>> rows) =>
        new(CompleteColumns(), rows);

    private static IReadOnlyList<OracleResultColumn> CompleteColumns() =>
    [
        new("TASK_TYPE", "VARCHAR2", OracleColumnKind.Text),
        new("SUBLOT", "VARCHAR2", OracleColumnKind.Text),
        new("AREA", "VARCHAR2", OracleColumnKind.Text),
        new("EQP", "VARCHAR2", OracleColumnKind.Text),
        new("STEP", "VARCHAR2", OracleColumnKind.Text),
        new("DATES", "DATE", OracleColumnKind.DateTime),
        new("PACKAGE", "VARCHAR2", OracleColumnKind.Text),
    ];

    private static string QueryPath() => Path.Combine(
        AppContext.BaseDirectory,
        "queries",
        "mes-task-union",
        "query.sql");

    private static void AssertFailure(
        MesTaskUnionRound round,
        string code,
        string stage = "ORACLE_EXECUTION")
    {
        Assert.Equal(MesTaskUnionRoundOutcome.Failure, round.Outcome);
        Assert.Equal(code, round.Diagnostic?.Code);
        Assert.Equal(stage, round.Diagnostic?.Stage);
    }

    private sealed class RecordingOracleStatementExecutor : IOracleStatementExecutor
    {
        private readonly OracleStatementResult? _result;
        private readonly Exception? _exception;

        public RecordingOracleStatementExecutor(OracleStatementResult result) => _result = result;
        public RecordingOracleStatementExecutor(Exception exception) => _exception = exception;

        public OracleExecutorIdentity Identity { get; } =
            new(OracleClientMode.Thin, OracleClientMode.Thin, "fake-thin");

        public List<OracleStatementRequest> Requests { get; } = [];

        public Task<OracleStatementResult> ExecuteAsync(
            OracleStatementRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return _exception is null
                ? Task.FromResult(_result!)
                : Task.FromException<OracleStatementResult>(_exception);
        }
    }
}
