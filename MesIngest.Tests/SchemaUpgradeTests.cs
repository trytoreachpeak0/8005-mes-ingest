using MesIngest.Core;
using Microsoft.Data.SqlClient;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 14 / remediation 04 seams: Phase-1 SQL fixture upgrade preserves projection history;
/// EnsureSchema is additive/idempotent (no DROP/rebuild of TransportDemands) and transactional
/// (mid-upgrade failure rolls back; no half-migration).
/// </summary>
[Collection("SqlServer")]
public class SchemaUpgradeTests
{
    [SqlServerAvailabilityFact]
    public void Upgrading_phase1_schema_preserves_demands_pauses_alerts_and_poll_health()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        Phase1SchemaFixture.ResetToPhase1(cs);

        var now = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.FromHours(8));
        Phase1SchemaFixture.Seed(
            cs,
            visibleDemandId: "phase1-visible-001",
            goneDemandId: "phase1-gone-001",
            pauseTaskType: "DIE_TO_OVEN",
            alertCode: "POLL_FAILURE",
            alertMessage: "oracle timeout",
            pollStarted: now.AddMinutes(-5),
            pollEnded: now);

        // Act: current store EnsureSchema + read path.
        var store = new SqlServerTransportDemandStore(cs);
        var state = store.GetState();
        var gone = store.List(DemandStatus.Gone);
        var alerts = store.ListAlerts();
        var health = store.GetLatestPollHealth();

        Assert.Contains(state.Demands, d => d.DemandId == "phase1-visible-001" && d.Status == DemandStatus.Visible);
        Assert.Contains(gone, d => d.DemandId == "phase1-gone-001" && d.Status == DemandStatus.Gone);
        Assert.Contains(state.TaskTypePauses, p => p.TaskType == "DIE_TO_OVEN" && p.PausedZeroDrop);
        Assert.Contains(alerts, a => a.Code == "POLL_FAILURE" && a.Message == "oracle timeout");
        Assert.NotNull(health);
        Assert.Equal("SUCCESS", health!.Outcome);
        Assert.Equal(2, health.RowCount);

        // Legacy rows are retained (archived + migrated), not wiped.
        Assert.True(Phase1SchemaFixture.LegacyArchiveRowCount(cs) >= 1);
        Assert.All(alerts.Where(a => a.Code == "POLL_FAILURE"), a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.AlertId));
            Assert.Equal(AlertSeverities.Error, a.Severity);
            Assert.False(a.IsActive);
            Assert.NotNull(a.ResolvedAt);
        });

        // Idempotent second open.
        _ = new SqlServerTransportDemandStore(cs);
        Assert.Equal(1, store.List(DemandStatus.Visible).Count(d => d.DemandId == "phase1-visible-001"));
        Assert.Equal(1, store.List(DemandStatus.Gone).Count(d => d.DemandId == "phase1-gone-001"));
    }

    [SqlServerAvailabilityFact]
    public void EnsureSchema_never_drops_or_truncates_transport_demands()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        Phase1SchemaFixture.ResetToPhase1(cs);
        Phase1SchemaFixture.Seed(
            cs,
            visibleDemandId: "keep-me-visible",
            goneDemandId: "keep-me-gone",
            pauseTaskType: "DIE_TO_WIRE_STAGING",
            alertCode: "PAUSED_ZERO_DROP",
            alertMessage: "zero drop",
            pollStarted: DateTimeOffset.Parse("2026-07-15T02:00:00Z"),
            pollEnded: DateTimeOffset.Parse("2026-07-15T02:00:10Z"));

        using (var conn = new SqlConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TRIGGER dbo.TR_TransportDemands_NoDrop
                ON dbo.TransportDemands
                AFTER DELETE
                AS
                BEGIN
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 50001, N'TransportDemands DELETE is forbidden during schema upgrade.', 1;
                END
                """;
            cmd.ExecuteNonQuery();
        }

        try
        {
            var store = new SqlServerTransportDemandStore(cs);
            Assert.Equal("keep-me-visible", store.GetById("keep-me-visible")?.DemandId);
            Assert.Equal("keep-me-gone", store.GetById("keep-me-gone")?.DemandId);
            Assert.True(store.HasGoneTransportDemandKey("DIE_TO_OVEN", "Q-GONE-P1"));
        }
        finally
        {
            using var conn = new SqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                IF OBJECT_ID(N'dbo.TR_TransportDemands_NoDrop', N'TR') IS NOT NULL
                    DROP TRIGGER dbo.TR_TransportDemands_NoDrop;
                """;
            cmd.ExecuteNonQuery();
        }
    }

    [SqlServerAvailabilityFact]
    public void Mid_upgrade_failure_leaves_no_half_migration_and_recovers_on_rerun()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        Phase1SchemaFixture.ResetToPhase1(cs);
        Phase1SchemaFixture.Seed(
            cs,
            visibleDemandId: "tx-visible-001",
            goneDemandId: "tx-gone-001",
            pauseTaskType: "DIE_TO_OVEN",
            alertCode: "POLL_FAILURE",
            alertMessage: "injected-upgrade-failure",
            pollStarted: DateTimeOffset.Parse("2026-07-15T03:00:00Z"),
            pollEnded: DateTimeOffset.Parse("2026-07-15T03:00:08Z"));

        Phase1SchemaFixture.InstallLateUpgradeFailureTrigger(cs);
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => _ = new SqlServerTransportDemandStore(cs));
            Assert.Contains("Injected schema upgrade failure", ex.Message, StringComparison.Ordinal);

            // Incomplete upgrade: version must not claim current schema, and early DDL must not stick.
            Assert.False(Phase1SchemaFixture.HasCurrentSchemaVersion(cs));
            Assert.Null(Phase1SchemaFixture.ColumnLength(cs, "TransportDemands", "CreatedAt"));
            Assert.Null(Phase1SchemaFixture.ColumnLength(cs, "IngestAlerts", "AlertId"));
            Assert.False(Phase1SchemaFixture.ObjectExists(cs, "DemandChangeFeed"));
            Assert.False(Phase1SchemaFixture.ObjectExists(cs, "IngestAlerts_LegacyArchive"));
            Assert.Equal(2, Phase1SchemaFixture.DemandRowCount(cs));
            Assert.Equal(1, Phase1SchemaFixture.PauseRowCount(cs));
            Assert.Equal(1, Phase1SchemaFixture.AlertRowCount(cs));
            Assert.Equal(1, Phase1SchemaFixture.PollHealthRowCount(cs));
        }
        finally
        {
            Phase1SchemaFixture.DropLateUpgradeFailureTrigger(cs);
        }

        // Condition restored: re-run completes and preserves Phase-1 history.
        var store = new SqlServerTransportDemandStore(cs);
        Assert.True(Phase1SchemaFixture.HasCurrentSchemaVersion(cs));
        Assert.Contains(store.GetState().Demands, d => d.DemandId == "tx-visible-001");
        Assert.Contains(store.List(DemandStatus.Gone), d => d.DemandId == "tx-gone-001");
        Assert.Contains(store.GetState().TaskTypePauses, p => p.TaskType == "DIE_TO_OVEN" && p.PausedZeroDrop);
        Assert.Contains(store.ListAlerts(), a => a.Code == "POLL_FAILURE" && a.Message == "injected-upgrade-failure");
        Assert.Equal("SUCCESS", store.GetLatestPollHealth()?.Outcome);

        // Success path stays idempotent.
        _ = new SqlServerTransportDemandStore(cs);
        Assert.Equal(1, store.List(DemandStatus.Visible).Count(d => d.DemandId == "tx-visible-001"));
        Assert.Equal(1, store.List(DemandStatus.Gone).Count(d => d.DemandId == "tx-gone-001"));
    }
}

/// <summary>
/// Phase-1 (ticket 05 era) SQL projection tables — no CreatedAt/GoneAt on demands,
/// no incident columns on IngestAlerts, no DemandChangeFeed / watch-ops indexes.
/// </summary>
internal static class Phase1SchemaFixture
{
    public static void ResetToPhase1(string connectionString)
    {
        DropLateUpgradeFailureTrigger(connectionString);
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID(N'dbo.TR_TransportDemands_WriteAudit', N'TR') IS NOT NULL
                DROP TRIGGER dbo.TR_TransportDemands_WriteAudit;
            IF OBJECT_ID(N'dbo.TR_TransportDemands_NoDrop', N'TR') IS NOT NULL
                DROP TRIGGER dbo.TR_TransportDemands_NoDrop;
            IF OBJECT_ID(N'dbo.DemandWriteAudit', N'U') IS NOT NULL
                DROP TABLE dbo.DemandWriteAudit;
            IF OBJECT_ID(N'dbo.DemandChangeFeed', N'U') IS NOT NULL
                DROP TABLE dbo.DemandChangeFeed;
            IF OBJECT_ID(N'dbo.IngestAlerts_LegacyArchive', N'U') IS NOT NULL
                DROP TABLE dbo.IngestAlerts_LegacyArchive;
            IF OBJECT_ID(N'dbo.MesIngestSchemaVersion', N'U') IS NOT NULL
                DROP TABLE dbo.MesIngestSchemaVersion;
            IF OBJECT_ID(N'dbo.IngestAlerts', N'U') IS NOT NULL
                DROP TABLE dbo.IngestAlerts;
            IF OBJECT_ID(N'dbo.PollHealth', N'U') IS NOT NULL
                DROP TABLE dbo.PollHealth;
            IF OBJECT_ID(N'dbo.TaskTypePauses', N'U') IS NOT NULL
                DROP TABLE dbo.TaskTypePauses;
            IF OBJECT_ID(N'dbo.TransportDemands', N'U') IS NOT NULL
                DROP TABLE dbo.TransportDemands;

            CREATE TABLE dbo.TransportDemands
            (
                DemandId NVARCHAR(64) NOT NULL CONSTRAINT PK_TransportDemands PRIMARY KEY,
                TaskType NVARCHAR(128) NOT NULL,
                Sublot NVARCHAR(128) NOT NULL,
                Area NVARCHAR(256) NULL,
                Eqp NVARCHAR(256) NULL,
                Step NVARCHAR(256) NULL,
                Dates DATETIMEOFFSET NOT NULL,
                Package NVARCHAR(256) NULL,
                Status NVARCHAR(16) NOT NULL,
                MesLastSeenAt DATETIMEOFFSET NOT NULL,
                DisappearCount INT NOT NULL,
                LocationRisk BIT NOT NULL,
                LocationRiskCode NVARCHAR(64) NULL
            );

            CREATE TABLE dbo.TaskTypePauses
            (
                TaskType NVARCHAR(128) NOT NULL CONSTRAINT PK_TaskTypePauses PRIMARY KEY,
                PausedZeroDrop BIT NOT NULL,
                LastHealthyNonZeroCount INT NOT NULL,
                RecoveryStreak INT NOT NULL
            );

            CREATE TABLE dbo.IngestAlerts
            (
                Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_IngestAlerts PRIMARY KEY,
                Code NVARCHAR(64) NOT NULL,
                TaskType NVARCHAR(128) NULL,
                Sublot NVARCHAR(128) NULL,
                DemandId NVARCHAR(64) NULL,
                Message NVARCHAR(1024) NULL,
                CreatedAt DATETIMEOFFSET NOT NULL CONSTRAINT DF_IngestAlerts_CreatedAt DEFAULT (SYSDATETIMEOFFSET())
            );

            CREATE TABLE dbo.PollHealth
            (
                Id INT NOT NULL CONSTRAINT PK_PollHealth PRIMARY KEY,
                StartedAt DATETIMEOFFSET NOT NULL,
                EndedAt DATETIMEOFFSET NOT NULL,
                DurationMs FLOAT NOT NULL,
                [RowCount] INT NOT NULL,
                Success BIT NOT NULL,
                Outcome NVARCHAR(32) NOT NULL,
                CONSTRAINT CK_PollHealth_SingleRow CHECK (Id = 1)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static void Seed(
        string connectionString,
        string visibleDemandId,
        string goneDemandId,
        string pauseTaskType,
        string alertCode,
        string alertMessage,
        DateTimeOffset pollStarted,
        DateTimeOffset pollEnded)
    {
        var dates = new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.FromHours(8));
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();

        using (var insertVisible = new SqlCommand(
                   """
                   INSERT INTO dbo.TransportDemands
                   (DemandId, TaskType, Sublot, Area, Eqp, Step, Dates, Package, Status,
                    MesLastSeenAt, DisappearCount, LocationRisk, LocationRiskCode)
                   VALUES
                   (@DemandId, N'DIE_TO_WIRE_STAGING', N'Q-VIS-P1', N'N09', N'EQ1', N'焊线', @Dates, N'PKG',
                    N'VISIBLE', @Seen, 0, 0, NULL);
                   """,
                   conn,
                   tx))
        {
            insertVisible.Parameters.AddWithValue("@DemandId", visibleDemandId);
            insertVisible.Parameters.AddWithValue("@Dates", dates);
            insertVisible.Parameters.AddWithValue("@Seen", pollEnded);
            insertVisible.ExecuteNonQuery();
        }

        using (var insertGone = new SqlCommand(
                   """
                   INSERT INTO dbo.TransportDemands
                   (DemandId, TaskType, Sublot, Area, Eqp, Step, Dates, Package, Status,
                    MesLastSeenAt, DisappearCount, LocationRisk, LocationRiskCode)
                   VALUES
                   (@DemandId, N'DIE_TO_OVEN', N'Q-GONE-P1', NULL, N'EQ2', N'烘箱', @Dates, N'PKG-G',
                    N'GONE', @Seen, 2, 1, N'AREA_EMPTY');
                   """,
                   conn,
                   tx))
        {
            insertGone.Parameters.AddWithValue("@DemandId", goneDemandId);
            insertGone.Parameters.AddWithValue("@Dates", dates.AddHours(-1));
            insertGone.Parameters.AddWithValue("@Seen", pollEnded.AddMinutes(-20));
            insertGone.ExecuteNonQuery();
        }

        using (var insertPause = new SqlCommand(
                   """
                   INSERT INTO dbo.TaskTypePauses
                   (TaskType, PausedZeroDrop, LastHealthyNonZeroCount, RecoveryStreak)
                   VALUES (@TaskType, 1, 12, 0);
                   """,
                   conn,
                   tx))
        {
            insertPause.Parameters.AddWithValue("@TaskType", pauseTaskType);
            insertPause.ExecuteNonQuery();
        }

        using (var insertAlert = new SqlCommand(
                   """
                   INSERT INTO dbo.IngestAlerts (Code, TaskType, Sublot, DemandId, Message, CreatedAt)
                   VALUES (@Code, @TaskType, NULL, NULL, @Message, @CreatedAt);
                   """,
                   conn,
                   tx))
        {
            insertAlert.Parameters.AddWithValue("@Code", alertCode);
            insertAlert.Parameters.AddWithValue("@TaskType", pauseTaskType);
            insertAlert.Parameters.AddWithValue("@Message", alertMessage);
            insertAlert.Parameters.AddWithValue("@CreatedAt", pollEnded);
            insertAlert.ExecuteNonQuery();
        }

        using (var insertHealth = new SqlCommand(
                   """
                   INSERT INTO dbo.PollHealth
                   (Id, StartedAt, EndedAt, DurationMs, [RowCount], Success, Outcome)
                   VALUES (1, @StartedAt, @EndedAt, 123.5, 2, 1, N'SUCCESS');
                   """,
                   conn,
                   tx))
        {
            insertHealth.Parameters.AddWithValue("@StartedAt", pollStarted);
            insertHealth.Parameters.AddWithValue("@EndedAt", pollEnded);
            insertHealth.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public static int LegacyArchiveRowCount(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID(N'dbo.IngestAlerts_LegacyArchive', N'U') IS NULL
                SELECT 0;
            ELSE
                SELECT COUNT(*) FROM dbo.IngestAlerts_LegacyArchive;
            """;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static void InstallLateUpgradeFailureTrigger(string connectionString)
    {
        DropLateUpgradeFailureTrigger(connectionString);
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Fail on a late additive object so earlier Phase-1→current DDL would otherwise stick.
        cmd.CommandText = """
            EXEC(N'
                CREATE TRIGGER TR_MesIngest_BlockDemandChangeFeed
                ON DATABASE
                FOR CREATE_TABLE
                AS
                BEGIN
                    DECLARE @name sysname =
                        EVENTDATA().value(N''(/EVENT_INSTANCE/ObjectName)[1]'', N''sysname'');
                    IF @name = N''DemandChangeFeed''
                        THROW 50002, N''Injected schema upgrade failure before ChangeFeed.'', 1;
                END
            ');
            """;
        cmd.ExecuteNonQuery();
    }

    public static void DropLateUpgradeFailureTrigger(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF EXISTS (
                SELECT 1
                FROM sys.triggers
                WHERE name = N'TR_MesIngest_BlockDemandChangeFeed'
                  AND parent_class_desc = N'DATABASE')
                DROP TRIGGER TR_MesIngest_BlockDemandChangeFeed ON DATABASE;
            """;
        cmd.ExecuteNonQuery();
    }

    public static bool HasCurrentSchemaVersion(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID(N'dbo.MesIngestSchemaVersion', N'U') IS NULL
                SELECT CAST(0 AS INT);
            ELSE
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM dbo.MesIngestSchemaVersion
                    WHERE Id = 1 AND SchemaVersion = @SchemaVersion)
                    THEN 1 ELSE 0 END;
            """;
        cmd.Parameters.AddWithValue("@SchemaVersion", MesIngestApiContract.SchemaVersion);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    public static int? ColumnLength(string connectionString, string table, string column)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COL_LENGTH(@Table, @Column);";
        cmd.Parameters.AddWithValue("@Table", "dbo." + table);
        cmd.Parameters.AddWithValue("@Column", column);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    public static bool ObjectExists(string connectionString, string table)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN OBJECT_ID(@Object, N'U') IS NULL THEN 0 ELSE 1 END;";
        cmd.Parameters.AddWithValue("@Object", "dbo." + table);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    public static int DemandRowCount(string connectionString) => ScalarCount(connectionString, "SELECT COUNT(*) FROM dbo.TransportDemands;");

    public static int PauseRowCount(string connectionString) => ScalarCount(connectionString, "SELECT COUNT(*) FROM dbo.TaskTypePauses;");

    public static int AlertRowCount(string connectionString) => ScalarCount(connectionString, "SELECT COUNT(*) FROM dbo.IngestAlerts;");

    public static int PollHealthRowCount(string connectionString) => ScalarCount(connectionString, "SELECT COUNT(*) FROM dbo.PollHealth;");

    private static int ScalarCount(string connectionString, string sql)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
