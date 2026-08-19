using MesIngest.Core.SeriesProjection;
using MesIngest.Infrastructure.SqlServer;
using Microsoft.Data.SqlClient;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 25: the final schema is established from an empty database only. There is no
/// upgrade path, so a database that already carries anything else is refused rather than
/// converted, and nothing the Host runs deletes what it found.
/// </summary>
[Collection("Ticket01SqlServer")]
public sealed class EmptyDatabaseBootstrapTests
{
    [Ticket01SqlServerFact]
    public async Task An_empty_database_bootstraps_the_whole_schema_once()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var projection = new SqlServerMesIngestProjection(database.ConnectionString);

        await projection.CommitRoundAsync(EmptySuccessRound("poll-empty-bootstrap-1"));
        var afterFirst = await ReadUserTableCountAsync(database.ConnectionString);

        // A second commit revalidates the same contract instead of rebuilding it.
        await projection.CommitRoundAsync(EmptySuccessRound("poll-empty-bootstrap-2"));
        var afterSecond = await ReadUserTableCountAsync(database.ConnectionString);

        Assert.True(afterFirst > 0, "Bootstrapping an empty database created no tables.");
        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(
            NewMesIngestContract.SchemaVersion,
            await ReadSchemaVersionAsync(database.ConnectionString));
    }

    [Ticket01SqlServerFact]
    public async Task A_database_that_already_holds_other_tables_is_refused_and_left_alone()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await ExecuteAsync(
            database.ConnectionString,
            "CREATE TABLE dbo.SomethingElse (Id INT NOT NULL PRIMARY KEY);");

        var projection = new SqlServerMesIngestProjection(database.ConnectionString);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => projection.CommitRoundAsync(EmptySuccessRound("poll-non-empty")));

        Assert.Contains(
            "new-MesIngest",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        // Refusing must not have converted, dropped, or partially created anything.
        Assert.Equal(1, await ReadUserTableCountAsync(database.ConnectionString));
        Assert.False(await SchemaExistsAsync(database.ConnectionString, "mesingest"));
    }

    private static MesTaskUnionRound EmptySuccessRound(string pollTraceId) => new(
        pollTraceId,
        "MES_TASK_UNION/sha256:test",
        MesTaskUnionRoundOutcome.Success,
        DateTimeOffset.Parse("2026-08-19T00:00:00Z"),
        DateTimeOffset.Parse("2026-08-19T00:00:01Z"),
        []);

    private static async Task<int> ReadUserTableCountAsync(string connectionString) =>
        Convert.ToInt32(
            await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;"),
            System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<int> ReadSchemaVersionAsync(string connectionString) =>
        Convert.ToInt32(
            await ScalarAsync(connectionString, "SELECT SchemaVersion FROM mesingest.SchemaInfo WHERE Id = 1;"),
            System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<bool> SchemaExistsAsync(string connectionString, string schemaName) =>
        Convert.ToInt32(
            await ScalarAsync(
                connectionString,
                $"SELECT COUNT(*) FROM sys.schemas WHERE name = '{schemaName}';"),
            System.Globalization.CultureInfo.InvariantCulture) > 0;

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
