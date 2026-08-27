using MesIngest.Core.SeriesProjection;
using MesIngest.Infrastructure.SqlServer;
using Microsoft.Data.SqlClient;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 25: the final schema is established from an empty database only. Unknown or
/// structurally different databases are refused rather than converted. The one bounded
/// v2.2-to-v2.3 contract-identity transition preserves the already-qualified schema and
/// history in place.
/// </summary>
[Collection("Ticket01SqlServer")]
public sealed class EmptyDatabaseBootstrapTests
{
    [Ticket01SqlServerFact]
    public async Task An_empty_database_bootstraps_the_whole_schema_once()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await ExecuteAsync(
            database.ConnectionString,
            "ALTER DATABASE CURRENT SET RECOVERY FULL;");
        var projection = new SqlServerMesIngestProjection(database.ConnectionString);

        await projection.CommitRoundAsync(EmptySuccessRound("poll-empty-bootstrap-1"));
        var afterFirst = await ReadUserTableCountAsync(database.ConnectionString);

        // The existing projection reuses its validated schema.
        await projection.CommitRoundAsync(EmptySuccessRound("poll-empty-bootstrap-2"));
        // A fresh projection models a Host process restart and must validate the
        // exact existing schema rather than relying on the first instance's cache.
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .CommitRoundAsync(EmptySuccessRound("poll-empty-bootstrap-restart"));
        var afterSecond = await ReadUserTableCountAsync(database.ConnectionString);

        Assert.True(afterFirst > 0, "Bootstrapping an empty database created no tables.");
        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(
            NewMesIngestContract.SchemaVersion,
            await ReadSchemaVersionAsync(database.ConnectionString));
        Assert.Equal("SIMPLE", await ReadRecoveryModelAsync(database.ConnectionString));
        Assert.True(await HasRetentionStateSchemaAsync(database.ConnectionString));
        Assert.Equal(
            new[]
            {
                "IX_MesIngest_DemandRawObservations_Demand:PAGE",
                "IX_MesIngest_DemandRawObservations_Series:PAGE",
                "PK_MesIngest_DemandRawObservations:PAGE",
            },
            await ReadRawObservationIndexCompressionAsync(database.ConnectionString));
    }

    [Ticket01SqlServerFact]
    public async Task Exact_v2_2_schema_identity_migrates_to_v2_3_without_replacing_history()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var projection = new SqlServerMesIngestProjection(database.ConnectionString);
        await projection.CommitRoundAsync(EmptySuccessRound("poll-v2-2-migration-before"));
        var historyEpoch = await ReadHistoryEpochAsync(database.ConnectionString);
        var signingKeyHash = await ReadSigningKeyHashAsync(database.ConnectionString);
        var tableCount = await ReadUserTableCountAsync(database.ConnectionString);
        await ExecuteAsync(
            database.ConnectionString,
            "UPDATE mesingest.SchemaInfo SET ContractVersion = N'2026.08.new-mes-ingest.v2.2' WHERE Id = 1;");

        await new SqlServerMesIngestProjection(database.ConnectionString)
            .CommitRoundAsync(EmptySuccessRound("poll-v2-2-migration-after"));

        Assert.Equal(NewMesIngestContract.Version, await ReadContractVersionAsync(database.ConnectionString));
        Assert.Equal(historyEpoch, await ReadHistoryEpochAsync(database.ConnectionString));
        Assert.Equal(signingKeyHash, await ReadSigningKeyHashAsync(database.ConnectionString));
        Assert.Equal(tableCount, await ReadUserTableCountAsync(database.ConnectionString));
        Assert.Equal(2, await ReadPollTraceCountAsync(database.ConnectionString));
    }

    [Ticket01SqlServerFact]
    public async Task Unapproved_contract_identity_is_rejected_and_left_unchanged()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .CommitRoundAsync(EmptySuccessRound("poll-unapproved-contract-bootstrap"));
        const string unapprovedVersion = "2026.08.new-mes-ingest.v2.1";
        await ExecuteAsync(
            database.ConnectionString,
            $"UPDATE mesingest.SchemaInfo SET ContractVersion = N'{unapprovedVersion}' WHERE Id = 1;");

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new SqlServerMesIngestProjection(database.ConnectionString)
                .CommitRoundAsync(EmptySuccessRound("poll-unapproved-contract-restart")));

        Assert.Contains("contract identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unapprovedVersion, await ReadContractVersionAsync(database.ConnectionString));
        Assert.Equal(1, await ReadPollTraceCountAsync(database.ConnectionString));
    }

    [Ticket01SqlServerFact]
    public async Task Structurally_drifted_v2_2_schema_is_rejected_before_identity_migration()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .CommitRoundAsync(EmptySuccessRound("poll-v2-2-drift-bootstrap"));
        await ExecuteAsync(
            database.ConnectionString,
            """
            UPDATE mesingest.SchemaInfo
            SET ContractVersion = N'2026.08.new-mes-ingest.v2.2'
            WHERE Id = 1;
            ALTER TABLE mesingest.CatalogItems ALTER COLUMN Area NVARCHAR(MAX) NOT NULL;
            """);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new SqlServerMesIngestProjection(database.ConnectionString)
                .CommitRoundAsync(EmptySuccessRound("poll-v2-2-drift-restart")));

        Assert.Contains("column", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "2026.08.new-mes-ingest.v2.2",
            await ReadContractVersionAsync(database.ConnectionString));
        Assert.Equal(1, await ReadPollTraceCountAsync(database.ConnectionString));
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

    [Ticket01SqlServerFact]
    public async Task A_database_that_already_holds_an_unknown_view_is_refused_and_left_alone()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await ExecuteAsync(
            database.ConnectionString,
            "CREATE VIEW dbo.SomethingElse AS SELECT CONVERT(INT, 1) AS Id;");

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new SqlServerMesIngestProjection(database.ConnectionString)
                .CommitRoundAsync(EmptySuccessRound("poll-unknown-view")));

        Assert.Contains("new-MesIngest", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            Convert.ToInt32(
                await ScalarAsync(
                    database.ConnectionString,
                    "SELECT COUNT(*) FROM sys.views WHERE name = N'SomethingElse';"),
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(await SchemaExistsAsync(database.ConnectionString, "mesingest"));
    }

    [Ticket01SqlServerFact]
    public async Task Existing_schema_with_full_recovery_is_rejected_without_repair()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .CommitRoundAsync(EmptySuccessRound("poll-recovery-bootstrap"));
        await ExecuteAsync(database.ConnectionString, "ALTER DATABASE CURRENT SET RECOVERY FULL;");

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new SqlServerMesIngestProjection(database.ConnectionString)
                .CommitRoundAsync(EmptySuccessRound("poll-recovery-drift")));

        Assert.Contains("SIMPLE", exception.Message, StringComparison.Ordinal);
        Assert.Equal("FULL", await ReadRecoveryModelAsync(database.ConnectionString));
    }

    [Ticket01SqlServerFact]
    public async Task Existing_schema_with_an_unknown_object_is_rejected_without_deleting_it()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .CommitRoundAsync(EmptySuccessRound("poll-unknown-object-bootstrap"));
        await ExecuteAsync(
            database.ConnectionString,
            "CREATE VIEW dbo.UnexpectedView AS SELECT CONVERT(INT, 1) AS Id;");

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new SqlServerMesIngestProjection(database.ConnectionString)
                .CommitRoundAsync(EmptySuccessRound("poll-unknown-object-drift")));

        Assert.Contains("unexpected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            Convert.ToInt32(
                await ScalarAsync(
                    database.ConnectionString,
                    "SELECT COUNT(*) FROM sys.views WHERE name = N'UnexpectedView';"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Ticket01SqlServerFact]
    public async Task Existing_schema_with_an_unknown_nonunique_index_is_rejected_without_deleting_it()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .CommitRoundAsync(EmptySuccessRound("poll-unknown-index-bootstrap"));
        await ExecuteAsync(
            database.ConnectionString,
            "CREATE INDEX IX_UnexpectedPollOutcome ON mesingest.PollTraces (Outcome);");

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new SqlServerMesIngestProjection(database.ConnectionString)
                .CommitRoundAsync(EmptySuccessRound("poll-unknown-index-drift")));

        Assert.Contains("index", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            Convert.ToInt32(
                await ScalarAsync(
                    database.ConnectionString,
                    """
                    SELECT COUNT(*)
                    FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'mesingest.PollTraces')
                      AND name = N'IX_UnexpectedPollOutcome';
                    """),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Ticket01SqlServerFact]
    public async Task Existing_schema_with_a_mismatched_hot_field_boundary_is_rejected_without_repair()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .CommitRoundAsync(EmptySuccessRound("poll-field-boundary-bootstrap"));
        await ExecuteAsync(
            database.ConnectionString,
            "ALTER TABLE mesingest.CatalogItems ALTER COLUMN Area NVARCHAR(MAX) NOT NULL;");

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new SqlServerMesIngestProjection(database.ConnectionString)
                .CommitRoundAsync(EmptySuccessRound("poll-field-boundary-drift")));

        Assert.Contains("column", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            -1,
            Convert.ToInt16(
                await ScalarAsync(
                    database.ConnectionString,
                    """
                    SELECT c.max_length
                    FROM sys.columns AS c
                    WHERE c.object_id = OBJECT_ID(N'mesingest.CatalogItems')
                      AND c.name = N'Area';
                    """),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Ticket01SqlServerTheory]
    [InlineData("PK_MesIngest_DemandRawObservations")]
    [InlineData("IX_MesIngest_DemandRawObservations_Series")]
    [InlineData("IX_MesIngest_DemandRawObservations_Demand")]
    public async Task Existing_schema_with_uncompressed_raw_index_is_rejected_without_repair(
        string indexName)
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .CommitRoundAsync(EmptySuccessRound("poll-compression-bootstrap"));
        await ExecuteAsync(
            database.ConnectionString,
            $"ALTER INDEX [{indexName}] ON mesingest.DemandRawObservations REBUILD WITH (DATA_COMPRESSION = NONE);");

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new SqlServerMesIngestProjection(database.ConnectionString)
                .CommitRoundAsync(EmptySuccessRound("poll-compression-drift")));

        Assert.Contains("compression", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"{indexName}:NONE",
            await ReadRawObservationIndexCompressionAsync(database.ConnectionString));
    }

    [Ticket01SqlServerFact]
    public async Task Existing_schema_with_nonclustered_raw_primary_key_is_rejected_without_repair()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .CommitRoundAsync(EmptySuccessRound("poll-clustered-key-bootstrap"));
        await ExecuteAsync(
            database.ConnectionString,
            """
            ALTER TABLE mesingest.DemandRawObservations
                DROP CONSTRAINT PK_MesIngest_DemandRawObservations;
            ALTER TABLE mesingest.DemandRawObservations
                ADD CONSTRAINT PK_MesIngest_DemandRawObservations
                PRIMARY KEY NONCLUSTERED (PollTraceId, Ordinal)
                WITH (DATA_COMPRESSION = PAGE);
            """);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new SqlServerMesIngestProjection(database.ConnectionString)
                .CommitRoundAsync(EmptySuccessRound("poll-clustered-key-drift")));

        Assert.Contains("clustered", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            2,
            Convert.ToInt32(
                await ScalarAsync(
                    database.ConnectionString,
                    """
                    SELECT [type]
                    FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'mesingest.DemandRawObservations')
                      AND name = N'PK_MesIngest_DemandRawObservations';
                    """),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Ticket01SqlServerFact]
    public async Task Bounded_fields_and_raw_evidence_round_trip_without_truncation()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var maximum = new string('\u754c', 512);
        var projection = new SqlServerMesIngestProjection(database.ConnectionString);

        await projection.CommitRoundAsync(SuccessRound(
            "poll-bounded-hot-fields",
            new MesTaskUnionObservation(
                "WORK-TYPE",
                "SUBLOT-BOUNDARY",
                maximum,
                maximum,
                maximum,
                DateTimeOffset.Parse("2026-08-19T00:00:00Z"),
                maximum,
                "2026-08-19T08:00:00+08:00")));

        Assert.Equal(
            new[]
            {
                "CatalogItems.Area:1024",
                "CatalogItems.Eqp:1024",
                "CatalogItems.Package:1024",
                "CatalogItems.Step:1024",
                "DemandRawObservations.Area:1024",
                "DemandRawObservations.Eqp:1024",
                "DemandRawObservations.MesSourceDateRaw:256",
                "DemandRawObservations.Package:1024",
                "DemandRawObservations.Step:1024",
                "TransportDemands.Area:1024",
                "TransportDemands.Eqp:1024",
                "TransportDemands.Package:1024",
                "TransportDemands.Step:1024",
            },
            await ReadMesFieldColumnWidthsAsync(database.ConnectionString));
        Assert.Equal(
            new[] { maximum, maximum, maximum, maximum, "2026-08-19T08:00:00+08:00" },
            await ReadRawMesFieldsAsync(database.ConnectionString, "poll-bounded-hot-fields"));
        Assert.Single((await projection.ListDemandSeriesAsync(
            new DemandSeriesBrowseQuery(new DemandSeriesBrowseFilter()))).Items);
        Assert.Single((await projection.ListReadabilityAuditAsync(
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()))).Items);
    }

    [Ticket01SqlServerFact]
    public async Task Over_bound_hot_field_is_rejected_diagnostically_before_bootstrap_or_evidence_write()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var projection = new SqlServerMesIngestProjection(database.ConnectionString);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => projection.CommitRoundAsync(SuccessRound(
                "poll-over-bound-hot-field",
                new MesTaskUnionObservation(
                    "WORK-TYPE",
                    "SUBLOT-OVER-BOUND",
                    "A1-1",
                    new string('X', 513),
                    "STEP",
                    DateTimeOffset.Parse("2026-08-19T00:00:00Z"),
                    "PACKAGE"))));

        Assert.Equal("Eqp", exception.ParamName);
        Assert.Contains("512", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ReadUserTableCountAsync(database.ConnectionString));
        Assert.False(await SchemaExistsAsync(database.ConnectionString, "mesingest"));
    }

    [Ticket01SqlServerFact]
    public async Task Over_bound_demand_series_filter_payload_is_rejected_before_query_execution()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var projection = new SqlServerMesIngestProjection(database.ConnectionString);
        await projection.CommitRoundAsync(EmptySuccessRound("poll-browse-filter-bootstrap"));
        var workTypes = OversizedFilterValues();

        var exception = await Assert.ThrowsAsync<DemandSeriesBrowseException>(
            () => projection.ListDemandSeriesAsync(new DemandSeriesBrowseQuery(
                new DemandSeriesBrowseFilter { WorkTypes = workTypes })));

        Assert.Contains("4000", exception.Message, StringComparison.Ordinal);
    }

    [Ticket01SqlServerFact]
    public async Task Over_bound_readability_filter_payload_is_rejected_before_query_execution()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var projection = new SqlServerMesIngestProjection(database.ConnectionString);
        await projection.CommitRoundAsync(EmptySuccessRound("poll-readability-filter-bootstrap"));
        var workTypes = OversizedFilterValues();

        var exception = await Assert.ThrowsAsync<ReadabilityAuditException>(
            () => projection.ListReadabilityAuditAsync(new ReadabilityAuditQuery(
                new ReadabilityAuditFilter { WorkTypes = workTypes })));

        Assert.Contains("4000", exception.Message, StringComparison.Ordinal);
    }

    [Ticket01SqlServerFact]
    public async Task Simple_recovery_reuses_log_without_creating_database_backups()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var projection = new SqlServerMesIngestProjection(database.ConnectionString);
        await projection.CommitRoundAsync(SuccessRound(
            "poll-log-reuse",
            new MesTaskUnionObservation(
                "WORK-TYPE",
                "SUBLOT-LOG-REUSE",
                "A1-1",
                "EQP-1",
                "STEP-1",
                DateTimeOffset.Parse("2026-08-19T00:00:00Z"),
                "PACKAGE-1")));
        await ExecuteAsync(database.ConnectionString, "CHECKPOINT;");

        Assert.Equal("SIMPLE", await ReadRecoveryModelAsync(database.ConnectionString));
        Assert.Equal(
            "NOTHING",
            Convert.ToString(
                await ScalarAsync(
                    database.ConnectionString,
                    "SELECT log_reuse_wait_desc FROM sys.databases WHERE name = DB_NAME();"),
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(
            0,
            Convert.ToInt32(
                await ScalarAsync(
                    database.ConnectionString,
                    "SELECT COUNT(*) FROM msdb.dbo.backupset WHERE database_name = DB_NAME();"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Ticket01SqlServerFact]
    public async Task Page_compression_has_measurable_savings_for_one_production_sized_round()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var observations = Enumerable.Range(0, 600)
            .Select(_ => new MesTaskUnionObservation(
                "WORK-TYPE",
                "SUBLOT-COMPRESSION",
                "A1-1",
                "EQP-COMPRESSION",
                "STEP-COMPRESSION",
                DateTimeOffset.Parse("2026-08-19T00:00:00Z"),
                "PACKAGE-COMPRESSION",
                "2026-08-19T08:00:00+08:00"))
            .ToArray();
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .CommitRoundAsync(SuccessRound("poll-compression-savings", observations));

        var (pageCompressedKilobytes, uncompressedKilobytes) =
            await MeasureRawObservationClusteredIndexSizesAsync(database.ConnectionString);

        Assert.True(pageCompressedKilobytes > 0);
        Assert.True(
            uncompressedKilobytes > pageCompressedKilobytes,
            $"Expected NONE to exceed PAGE, observed PAGE={pageCompressedKilobytes} KB and NONE={uncompressedKilobytes} KB.");
    }

    private static MesTaskUnionRound EmptySuccessRound(string pollTraceId) => new(
        pollTraceId,
        "MES_TASK_UNION/sha256:test",
        MesTaskUnionRoundOutcome.Success,
        DateTimeOffset.Parse("2026-08-19T00:00:00Z"),
        DateTimeOffset.Parse("2026-08-19T00:00:01Z"),
        []);

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        params MesTaskUnionObservation[] observations) => new(
        pollTraceId,
        "MES_TASK_UNION/sha256:test",
        MesTaskUnionRoundOutcome.Success,
        DateTimeOffset.Parse("2026-08-19T00:00:00Z"),
        DateTimeOffset.Parse("2026-08-19T00:00:01Z"),
        observations);

    private static IReadOnlyList<string> OversizedFilterValues() =>
        Enumerable.Range(0, 40)
            .Select(index => $"WT-{index:D2}-" + new string('X', 121))
            .ToArray();

    private static async Task<int> ReadUserTableCountAsync(string connectionString) =>
        Convert.ToInt32(
            await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;"),
            System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<int> ReadSchemaVersionAsync(string connectionString) =>
        Convert.ToInt32(
            await ScalarAsync(connectionString, "SELECT SchemaVersion FROM mesingest.SchemaInfo WHERE Id = 1;"),
            System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> ReadContractVersionAsync(string connectionString) =>
        Convert.ToString(
            await ScalarAsync(connectionString, "SELECT ContractVersion FROM mesingest.SchemaInfo WHERE Id = 1;"),
            System.Globalization.CultureInfo.InvariantCulture)!;

    private static async Task<Guid> ReadHistoryEpochAsync(string connectionString) =>
        (Guid)(await ScalarAsync(
            connectionString,
            "SELECT HistoryEpoch FROM mesingest.SchemaInfo WHERE Id = 1;"))!;

    private static async Task<string> ReadSigningKeyHashAsync(string connectionString) =>
        Convert.ToString(
            await ScalarAsync(
                connectionString,
                "SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', SnapshotTokenSigningKey), 2) FROM mesingest.SchemaInfo WHERE Id = 1;"),
            System.Globalization.CultureInfo.InvariantCulture)!;

    private static async Task<int> ReadPollTraceCountAsync(string connectionString) =>
        Convert.ToInt32(
            await ScalarAsync(connectionString, "SELECT COUNT(*) FROM mesingest.PollTraces;"),
            System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<bool> HasRetentionStateSchemaAsync(string connectionString) =>
        Convert.ToBoolean(
            await ScalarAsync(
                connectionString,
                """
                SELECT CONVERT(BIT, CASE
                    WHEN COL_LENGTH(N'mesingest.PollTraces', N'RawObservationsExpiredAt') IS NOT NULL
                     AND COL_LENGTH(N'mesingest.DemandSeries', N'RetentionEligibilityAt') IS NOT NULL
                     AND COL_LENGTH(N'mesingest.HistoryCleanupState', N'HistoryCleanupStatus') IS NOT NULL
                     AND COL_LENGTH(N'mesingest.HistoryCleanupState', N'HistoryCleanupNextCheckAt') IS NOT NULL
                     AND COL_LENGTH(N'mesingest.HistoryCleanupState', N'HistoryCleanupTotalDeletedRawObservationCount') IS NOT NULL
                     AND COL_LENGTH(N'mesingest.HistoryCleanupState', N'HistoryCleanupLastFailureReason') IS NOT NULL
                     AND (SELECT HistoryCleanupStatus FROM mesingest.HistoryCleanupState WHERE Id = 1) = N'NOT_RUN'
                     AND EXISTS
                     (
                         SELECT 1
                         FROM sys.indexes
                         WHERE object_id = OBJECT_ID(N'mesingest.DemandSeries')
                           AND name = N'IX_MesIngest_DemandSeries_RetentionEligibilityAt'
                           AND has_filter = 1
                     )
                     AND EXISTS
                     (
                         SELECT 1
                         FROM sys.indexes
                         WHERE object_id = OBJECT_ID(N'mesingest.PollTraces')
                           AND name = N'IX_MesIngest_PollTraces_RawRetentionDue'
                           AND has_filter = 1
                     )
                    THEN 1 ELSE 0 END);
                """),
            System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> ReadRecoveryModelAsync(string connectionString) =>
        Convert.ToString(
            await ScalarAsync(
                connectionString,
                "SELECT recovery_model_desc FROM sys.databases WHERE name = DB_NAME();"),
            System.Globalization.CultureInfo.InvariantCulture)!;

    private static async Task<IReadOnlyList<string>> ReadRawObservationIndexCompressionAsync(
        string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.name, MIN(p.data_compression_desc)
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.partitions AS p
                ON p.object_id = i.object_id AND p.index_id = i.index_id
            WHERE s.name = N'mesingest'
              AND t.name = N'DemandRawObservations'
              AND i.index_id > 0
            GROUP BY i.name
            ORDER BY i.name;
            """;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        }
        return values;
    }

    private static async Task<IReadOnlyList<string>> ReadMesFieldColumnWidthsAsync(
        string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONCAT(t.name, N'.', c.name), c.max_length
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND t.name IN (N'TransportDemands', N'CatalogItems', N'DemandRawObservations')
              AND c.name IN (N'Area', N'Eqp', N'Step', N'Package', N'MesSourceDateRaw')
            ORDER BY t.name, c.name;
            """;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add($"{reader.GetString(0)}:{reader.GetInt16(1)}");
        }
        return values;
    }

    private static async Task<IReadOnlyList<string>> ReadRawMesFieldsAsync(
        string connectionString,
        string pollTraceId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Area, Eqp, Step, Package, MesSourceDateRaw
            FROM mesingest.DemandRawObservations
            WHERE PollTraceId = @pollTraceId AND Ordinal = 0;
            """;
        command.Parameters.Add("@pollTraceId", System.Data.SqlDbType.NVarChar, 128).Value = pollTraceId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return Enumerable.Range(0, 5).Select(reader.GetString).ToArray();
    }

    private static async Task<(long PageCompressedKilobytes, long UncompressedKilobytes)>
        MeasureRawObservationClusteredIndexSizesAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            INTO dbo.Ticket04UncompressedRawObservations
            FROM mesingest.DemandRawObservations;
            ALTER TABLE dbo.Ticket04UncompressedRawObservations
            ADD CONSTRAINT PK_Ticket04UncompressedRawObservations
                PRIMARY KEY CLUSTERED (PollTraceId, Ordinal)
                WITH (DATA_COMPRESSION = NONE);

            SELECT
                compressed.used_page_count * 8,
                uncompressed.used_page_count * 8
            FROM sys.dm_db_partition_stats AS compressed
            CROSS JOIN sys.dm_db_partition_stats AS uncompressed
            WHERE compressed.object_id = OBJECT_ID(N'mesingest.DemandRawObservations')
              AND compressed.index_id = 1
              AND uncompressed.object_id = OBJECT_ID(N'dbo.Ticket04UncompressedRawObservations')
              AND uncompressed.index_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

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
