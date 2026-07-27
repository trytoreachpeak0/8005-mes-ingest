using System.Text;
using MesIngest.Core;

namespace MesIngest.Tests;

public class OracleMesSnapshotSourceTests
{
    [Fact]
    public async Task Reads_sql_file_and_maps_rows_with_beijing_dates()
    {
        var sqlPath = await WriteTempSqlAsync("SELECT 1 FROM DUAL /* fixture */");
        var executor = new FakeOracleQueryExecutor(
            new OracleQueryResult(
                ["TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "DATES", "PACKAGE"],
                [
                    [
                        "STAGING_TO_WIRE",
                        "Q1-1",
                        "N01-01",
                        "EQ1",
                        "焊线",
                        new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Unspecified),
                        "PKG-A",
                    ],
                ]));

        try
        {
            var source = new OracleMesSnapshotSource(
                new OracleSnapshotOptions
                {
                    QuerySqlPath = sqlPath,
                    Mode = OracleClientMode.Thin,
                },
                executor);

            var outcome = await source.ReadAsync();

            Assert.Equal(SnapshotOutcomeKind.Success, outcome.Kind);
            var row = Assert.Single(outcome.Rows);
            Assert.Equal("STAGING_TO_WIRE", row.TaskType);
            Assert.Equal("Q1-1", row.Sublot);
            Assert.Equal("N01-01", row.Area);
            Assert.Equal("EQ1", row.Eqp);
            Assert.Equal("焊线", row.Step);
            Assert.Equal(TimeSpan.FromHours(8), row.Dates.Offset);
            Assert.Equal(10, row.Dates.Hour);
            Assert.Equal("PKG-A", row.Package);
            Assert.Equal("SELECT 1 FROM DUAL /* fixture */", executor.LastSql);
        }
        finally
        {
            File.Delete(sqlPath);
        }
    }

    [Fact]
    public async Task Missing_required_column_returns_incomplete()
    {
        var sqlPath = await WriteTempSqlAsync("SELECT 1 FROM DUAL");
        var executor = new FakeOracleQueryExecutor(
            new OracleQueryResult(
                ["TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "PACKAGE"],
                [["T", "S", "A", "E", "ST", "P"]]));

        try
        {
            var source = new OracleMesSnapshotSource(
                new OracleSnapshotOptions { QuerySqlPath = sqlPath },
                executor);

            var outcome = await source.ReadAsync();

            Assert.Equal(SnapshotOutcomeKind.Incomplete, outcome.Kind);
            Assert.Empty(outcome.Rows);
        }
        finally
        {
            File.Delete(sqlPath);
        }
    }

    [Fact]
    public async Task Unparseable_dates_returns_incomplete()
    {
        var sqlPath = await WriteTempSqlAsync("SELECT 1 FROM DUAL");
        var executor = new FakeOracleQueryExecutor(
            new OracleQueryResult(
                ["TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "DATES", "PACKAGE"],
                [["DIE_TO_OVEN", "Q1", "N01-01", "EQ1", "烘箱", "not-a-date", "PKG"]]));

        try
        {
            var source = new OracleMesSnapshotSource(
                new OracleSnapshotOptions { QuerySqlPath = sqlPath },
                executor);

            var outcome = await source.ReadAsync();

            Assert.Equal(SnapshotOutcomeKind.Incomplete, outcome.Kind);
            Assert.Empty(outcome.Rows);
        }
        finally
        {
            File.Delete(sqlPath);
        }
    }

    [Fact]
    public async Task Empty_task_type_returns_incomplete()
    {
        var sqlPath = await WriteTempSqlAsync("SELECT 1 FROM DUAL");
        var executor = new FakeOracleQueryExecutor(
            new OracleQueryResult(
                ["TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "DATES", "PACKAGE"],
                [
                    [
                        " ",
                        "Q1",
                        "N01-01",
                        "EQ1",
                        "烘箱",
                        new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Unspecified),
                        "PKG",
                    ],
                ]));

        try
        {
            var source = new OracleMesSnapshotSource(
                new OracleSnapshotOptions { QuerySqlPath = sqlPath },
                executor);

            var outcome = await source.ReadAsync();

            Assert.Equal(SnapshotOutcomeKind.Incomplete, outcome.Kind);
            Assert.Empty(outcome.Rows);
        }
        finally
        {
            File.Delete(sqlPath);
        }
    }

    [Fact]
    public async Task Executor_exception_propagates_for_runner_failure_mapping()
    {
        var sqlPath = await WriteTempSqlAsync("SELECT 1 FROM DUAL");
        var executor = new FakeOracleQueryExecutor(new InvalidOperationException("boom"));

        try
        {
            var source = new OracleMesSnapshotSource(
                new OracleSnapshotOptions { QuerySqlPath = sqlPath },
                executor);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync());
            Assert.Equal("boom", ex.Message);
        }
        finally
        {
            File.Delete(sqlPath);
        }
    }

    [Fact]
    public async Task Null_and_dbnull_optional_fields_map_to_null()
    {
        var sqlPath = await WriteTempSqlAsync("SELECT 1 FROM DUAL");
        var executor = new FakeOracleQueryExecutor(
            new OracleQueryResult(
                ["TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "DATES", "PACKAGE"],
                [
                    [
                        "WIRE_TO_NITROGEN",
                        "N-1",
                        DBNull.Value,
                        null,
                        " ",
                        new DateTime(2026, 8, 2, 8, 30, 0, DateTimeKind.Unspecified),
                        DBNull.Value,
                    ],
                ]));

        try
        {
            var source = new OracleMesSnapshotSource(
                new OracleSnapshotOptions { QuerySqlPath = sqlPath },
                executor);

            var outcome = await source.ReadAsync();
            var row = Assert.Single(outcome.Rows);
            Assert.Null(row.Area);
            Assert.Null(row.Eqp);
            Assert.Null(row.Step);
            Assert.Null(row.Package);
        }
        finally
        {
            File.Delete(sqlPath);
        }
    }

    private static async Task<string> WriteTempSqlAsync(string sql)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mes-union-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, sql, Encoding.UTF8);
        return path;
    }

    private sealed class FakeOracleQueryExecutor : IOracleQueryExecutor
    {
        private readonly OracleQueryResult? _result;
        private readonly Exception? _error;

        public FakeOracleQueryExecutor(OracleQueryResult result) => _result = result;

        public FakeOracleQueryExecutor(Exception error) => _error = error;

        public string? LastSql { get; private set; }

        public Task<OracleQueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            LastSql = sql;
            if (_error is not null)
            {
                throw _error;
            }

            return Task.FromResult(_result!);
        }
    }
}
