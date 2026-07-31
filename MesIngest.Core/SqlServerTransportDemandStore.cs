using Microsoft.Data.SqlClient;

namespace MesIngest.Core;

public sealed class SqlServerTransportDemandStore : ITransportDemandStore
{
    private readonly string _connectionString;
    private readonly ILatencyTelemetry _telemetry;
    private readonly object _gate = new();

    public SqlServerTransportDemandStore(string connectionString, ILatencyTelemetry? telemetry = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQL Server connection string is required.", nameof(connectionString));
        }

        _connectionString = connectionString;
        _telemetry = telemetry ?? NullLatencyTelemetry.Instance;
        EnsureSchema();
    }

    public ProjectionState GetState()
    {
        lock (_gate)
        {
            using var conn = Open();
            var demands = LoadDemands(conn, visibleOnly: true);
            var pauses = LoadPauses(conn);
            return new ProjectionState(demands, pauses);
        }
    }

    public void ReplaceState(ProjectionState state, IReadOnlyList<IngestAlert>? alerts = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var existingVisible = LoadDemands(conn, visibleOnly: true, tx)
                .ToDictionary(d => d.DemandId, StringComparer.Ordinal);
            var existingPauses = LoadPauses(conn, tx)
                .ToDictionary(p => p.TaskType, StringComparer.Ordinal);

            if (alerts is { Count: > 0 })
            {
                AppendAlertsCore(conn, tx, alerts);
            }

            foreach (var demand in state.Demands)
            {
                if (existingVisible.TryGetValue(demand.DemandId, out var visiblePrior))
                {
                    if (visiblePrior != demand)
                    {
                        UpdateDemand(conn, tx, demand);
                    }

                    continue;
                }

                var prior = LoadDemandById(conn, tx, demand.DemandId);
                if (prior is null)
                {
                    InsertDemand(conn, tx, demand);
                    continue;
                }

                // Permanent GONE rows are immutable — never rewrite after they form.
                if (prior.Status == DemandStatus.Gone)
                {
                    continue;
                }

                if (prior != demand)
                {
                    UpdateDemand(conn, tx, demand);
                }
            }

            foreach (var pause in state.TaskTypePauses)
            {
                if (existingPauses.TryGetValue(pause.TaskType, out var prior) && prior == pause)
                {
                    continue;
                }

                UpsertPause(conn, tx, pause);
            }

            tx.Commit();
        }
    }

    public bool HasGoneTransportDemandKey(string taskType, string sublot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sublot);

        lock (_gate)
        {
            using var conn = Open();
            using var cmd = new SqlCommand(
                """
                SELECT TOP (1) 1
                FROM dbo.TransportDemands
                WHERE TaskType = @TaskType
                  AND Sublot = @Sublot
                  AND Status = N'GONE';
                """,
                conn);
            cmd.Parameters.AddWithValue("@TaskType", taskType);
            cmd.Parameters.AddWithValue("@Sublot", sublot);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public TransportDemand? GetById(string demandId)
    {
        lock (_gate)
        {
            using var conn = Open();
            using var cmd = new SqlCommand(
                """
                SELECT DemandId, TaskType, Sublot, Area, Eqp, Step, Dates, Package, Status,
                       MesLastSeenAt, DisappearCount, LocationRisk, LocationRiskCode, CreatedAt, GoneAt
                FROM dbo.TransportDemands
                WHERE DemandId = @DemandId;
                """,
                conn);
            cmd.Parameters.AddWithValue("@DemandId", demandId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDemand(reader) : null;
        }
    }

    public IReadOnlyList<TransportDemand> List(
        DemandStatus? status = null,
        string? taskType = null,
        string? sublot = null,
        string? demandId = null)
    {
        lock (_gate)
        {
            using var conn = Open();
            var sql = """
                SELECT DemandId, TaskType, Sublot, Area, Eqp, Step, Dates, Package, Status,
                       MesLastSeenAt, DisappearCount, LocationRisk, LocationRiskCode, CreatedAt, GoneAt
                FROM dbo.TransportDemands
                WHERE 1 = 1
                """;
            if (status is not null)
            {
                sql += " AND Status = @Status";
            }

            if (!string.IsNullOrWhiteSpace(taskType))
            {
                sql += " AND TaskType = @TaskType";
            }

            if (!string.IsNullOrWhiteSpace(sublot))
            {
                sql += " AND Sublot = @Sublot";
            }

            if (!string.IsNullOrWhiteSpace(demandId))
            {
                sql += " AND DemandId = @DemandId";
            }

            sql += " ORDER BY Dates DESC, DemandId ASC;";
            using var cmd = new SqlCommand(sql, conn);
            if (status is not null)
            {
                cmd.Parameters.AddWithValue("@Status", ToStatusText(status.Value));
            }

            if (!string.IsNullOrWhiteSpace(taskType))
            {
                cmd.Parameters.AddWithValue("@TaskType", taskType);
            }

            if (!string.IsNullOrWhiteSpace(sublot))
            {
                cmd.Parameters.AddWithValue("@Sublot", sublot);
            }

            if (!string.IsNullOrWhiteSpace(demandId))
            {
                cmd.Parameters.AddWithValue("@DemandId", demandId);
            }

            return ReadDemands(cmd);
        }
    }

    public void AppendAlerts(IReadOnlyList<IngestAlert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        if (alerts.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            AppendAlertsCore(conn, tx, alerts);
            tx.Commit();
        }
    }

    private static void AppendAlertsCore(
        SqlConnection conn,
        SqlTransaction tx,
        IReadOnlyList<IngestAlert> alerts)
    {
        var stamped = DateTimeOffset.UtcNow;
        foreach (var alert in alerts)
        {
            using var insert = new SqlCommand(
                """
                INSERT INTO dbo.IngestAlerts (Code, TaskType, Sublot, DemandId, Message, CreatedAt)
                VALUES (@Code, @TaskType, @Sublot, @DemandId, @Message, @CreatedAt);
                """,
                conn,
                tx);
            insert.Parameters.AddWithValue("@Code", alert.Code);
            insert.Parameters.AddWithValue("@TaskType", (object?)alert.TaskType ?? DBNull.Value);
            insert.Parameters.AddWithValue("@Sublot", (object?)alert.Sublot ?? DBNull.Value);
            insert.Parameters.AddWithValue("@DemandId", (object?)alert.DemandId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@Message", (object?)alert.Message ?? DBNull.Value);
            insert.Parameters.AddWithValue("@CreatedAt", alert.CreatedAt ?? stamped);
            insert.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<IngestAlert> ListAlerts(int? limit = null)
    {
        lock (_gate)
        {
            using var conn = Open();
            var take = Math.Max(1, limit ?? InMemoryTransportDemandStore.DefaultAlertLimit);
            using var cmd = new SqlCommand(
                """
                SELECT TOP (@Limit) Code, TaskType, Sublot, DemandId, Message, CreatedAt
                FROM dbo.IngestAlerts
                ORDER BY CreatedAt DESC, Id DESC;
                """,
                conn);
            cmd.Parameters.AddWithValue("@Limit", take);
            using var reader = cmd.ExecuteReader();
            var list = new List<IngestAlert>();
            while (reader.Read())
            {
                list.Add(new IngestAlert(
                    Code: reader.GetString(0),
                    TaskType: reader.IsDBNull(1) ? null : reader.GetString(1),
                    Sublot: reader.IsDBNull(2) ? null : reader.GetString(2),
                    DemandId: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Message: reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt: reader.GetDateTimeOffset(5)));
            }

            return list;
        }
    }

    public void SetLatestPollHealth(PollHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        lock (_gate)
        {
            using var conn = Open();
            using var cmd = new SqlCommand(
                """
                DELETE FROM dbo.PollHealth WHERE Id = 1;
                INSERT INTO dbo.PollHealth
                    (Id, StartedAt, EndedAt, DurationMs, [RowCount], Success, Outcome, FailureStage, OracleDurationMs)
                VALUES
                    (1, @StartedAt, @EndedAt, @DurationMs, @RowCount, @Success, @Outcome, @FailureStage, @OracleDurationMs);
                """,
                conn);
            cmd.Parameters.AddWithValue("@StartedAt", health.StartedAt);
            cmd.Parameters.AddWithValue("@EndedAt", health.EndedAt);
            cmd.Parameters.AddWithValue("@DurationMs", health.DurationMs);
            cmd.Parameters.AddWithValue("@RowCount", health.RowCount);
            cmd.Parameters.AddWithValue("@Success", health.Success);
            cmd.Parameters.AddWithValue("@Outcome", health.Outcome);
            cmd.Parameters.AddWithValue("@FailureStage", (object?)health.FailureStage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@OracleDurationMs", (object?)health.OracleDurationMs ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public PollHealth? GetLatestPollHealth()
    {
        lock (_gate)
        {
            using var conn = Open();
            using var cmd = new SqlCommand(
                """
                SELECT StartedAt, EndedAt, DurationMs, [RowCount], Success, Outcome, FailureStage, OracleDurationMs
                FROM dbo.PollHealth
                WHERE Id = 1;
                """,
                conn);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new PollHealth(
                StartedAt: reader.GetDateTimeOffset(0),
                EndedAt: reader.GetDateTimeOffset(1),
                DurationMs: reader.GetDouble(2),
                RowCount: reader.GetInt32(3),
                Success: reader.GetBoolean(4),
                Outcome: reader.GetString(5),
                FailureStage: reader.IsDBNull(6) ? null : reader.GetString(6),
                OracleDurationMs: reader.IsDBNull(7) ? null : reader.GetDouble(7));
        }
    }

    private SqlConnection Open()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var conn = new SqlConnection(_connectionString);
        try
        {
            conn.Open();
            sw.Stop();
            _telemetry.Record(new LatencyEvent(
                CorrelationId: LatencyCorrelation.Id ?? "none",
                Component: LatencyComponents.SqlServer,
                Stage: LatencyStages.SqlOpen,
                ElapsedMs: sw.ElapsedMilliseconds));
            return conn;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _telemetry.Record(new LatencyEvent(
                CorrelationId: LatencyCorrelation.Id ?? "none",
                Component: LatencyComponents.SqlServer,
                Stage: SqlFailureClassifier.Classify(ex),
                ElapsedMs: sw.ElapsedMilliseconds,
                Detail: LatencyLogFormatter.Sanitize(ex.Message)));
            conn.Dispose();
            throw;
        }
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // Dynamic SQL so CREATE is not compile-validated when the table already exists.
        cmd.CommandText = """
            IF OBJECT_ID(N'dbo.TransportDemands', N'U') IS NULL
            EXEC(N'
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
                    LocationRiskCode NVARCHAR(64) NULL,
                    CreatedAt DATETIMEOFFSET NOT NULL,
                    GoneAt DATETIMEOFFSET NULL
                );
            ');

            IF COL_LENGTH(N'dbo.TransportDemands', N'CreatedAt') IS NULL
            EXEC(N'ALTER TABLE dbo.TransportDemands ADD CreatedAt DATETIMEOFFSET NOT NULL CONSTRAINT DF_TransportDemands_CreatedAt DEFAULT (SYSDATETIMEOFFSET());');

            IF COL_LENGTH(N'dbo.TransportDemands', N'GoneAt') IS NULL
            EXEC(N'ALTER TABLE dbo.TransportDemands ADD GoneAt DATETIMEOFFSET NULL;');

            IF OBJECT_ID(N'dbo.TaskTypePauses', N'U') IS NULL
            EXEC(N'
                CREATE TABLE dbo.TaskTypePauses
                (
                    TaskType NVARCHAR(128) NOT NULL CONSTRAINT PK_TaskTypePauses PRIMARY KEY,
                    PausedZeroDrop BIT NOT NULL,
                    LastHealthyNonZeroCount INT NOT NULL,
                    RecoveryStreak INT NOT NULL
                );
            ');

            IF OBJECT_ID(N'dbo.IngestAlerts', N'U') IS NULL
            EXEC(N'
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
            ');

            IF OBJECT_ID(N'dbo.IngestAlerts', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.IngestAlerts', N'CreatedAt') IS NULL
            EXEC(N'ALTER TABLE dbo.IngestAlerts ADD CreatedAt DATETIMEOFFSET NOT NULL CONSTRAINT DF_IngestAlerts_CreatedAt DEFAULT (SYSDATETIMEOFFSET());');

            IF OBJECT_ID(N'dbo.PollHealth', N'U') IS NULL
            EXEC(N'
                CREATE TABLE dbo.PollHealth
                (
                    Id INT NOT NULL CONSTRAINT PK_PollHealth PRIMARY KEY,
                    StartedAt DATETIMEOFFSET NOT NULL,
                    EndedAt DATETIMEOFFSET NOT NULL,
                    DurationMs FLOAT NOT NULL,
                    [RowCount] INT NOT NULL,
                    Success BIT NOT NULL,
                    Outcome NVARCHAR(32) NOT NULL,
                    FailureStage NVARCHAR(64) NULL,
                    OracleDurationMs FLOAT NULL,
                    CONSTRAINT CK_PollHealth_SingleRow CHECK (Id = 1)
                );
            ');

            IF OBJECT_ID(N'dbo.PollHealth', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.PollHealth', N'FailureStage') IS NULL
            EXEC(N'ALTER TABLE dbo.PollHealth ADD FailureStage NVARCHAR(64) NULL;');

            IF OBJECT_ID(N'dbo.PollHealth', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.PollHealth', N'OracleDurationMs') IS NULL
            EXEC(N'ALTER TABLE dbo.PollHealth ADD OracleDurationMs FLOAT NULL;');

            IF OBJECT_ID(N'dbo.TransportDemands', N'U') IS NOT NULL
               AND NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = N'IX_TransportDemands_TaskType_Sublot'
                      AND object_id = OBJECT_ID(N'dbo.TransportDemands'))
            EXEC(N'CREATE INDEX IX_TransportDemands_TaskType_Sublot ON dbo.TransportDemands (TaskType, Sublot);');

            IF OBJECT_ID(N'dbo.TransportDemands', N'U') IS NOT NULL
               AND NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = N'IX_TransportDemands_Status_Dates_DemandId'
                      AND object_id = OBJECT_ID(N'dbo.TransportDemands'))
            EXEC(N'CREATE INDEX IX_TransportDemands_Status_Dates_DemandId ON dbo.TransportDemands (Status, Dates DESC, DemandId);');

            IF OBJECT_ID(N'dbo.TransportDemands', N'U') IS NOT NULL
               AND NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = N'IX_TransportDemands_GoneAt_DemandId'
                      AND object_id = OBJECT_ID(N'dbo.TransportDemands'))
            EXEC(N'CREATE INDEX IX_TransportDemands_GoneAt_DemandId ON dbo.TransportDemands (GoneAt, DemandId) WHERE GoneAt IS NOT NULL;');
            """;
        cmd.ExecuteNonQuery();
    }

    private static void InsertDemand(SqlConnection conn, SqlTransaction tx, TransportDemand demand)
    {
        using var insert = new SqlCommand(
            """
            INSERT INTO dbo.TransportDemands
            (DemandId, TaskType, Sublot, Area, Eqp, Step, Dates, Package, Status,
             MesLastSeenAt, DisappearCount, LocationRisk, LocationRiskCode, CreatedAt, GoneAt)
            VALUES
            (@DemandId, @TaskType, @Sublot, @Area, @Eqp, @Step, @Dates, @Package, @Status,
             @MesLastSeenAt, @DisappearCount, @LocationRisk, @LocationRiskCode, @CreatedAt, @GoneAt);
            """,
            conn,
            tx);
        BindDemand(insert, demand);
        insert.ExecuteNonQuery();
    }

    private static void UpdateDemand(SqlConnection conn, SqlTransaction tx, TransportDemand demand)
    {
        using var update = new SqlCommand(
            """
            UPDATE dbo.TransportDemands
            SET TaskType = @TaskType,
                Sublot = @Sublot,
                Area = @Area,
                Eqp = @Eqp,
                Step = @Step,
                Dates = @Dates,
                Package = @Package,
                Status = @Status,
                MesLastSeenAt = @MesLastSeenAt,
                DisappearCount = @DisappearCount,
                LocationRisk = @LocationRisk,
                LocationRiskCode = @LocationRiskCode,
                CreatedAt = @CreatedAt,
                GoneAt = @GoneAt
            WHERE DemandId = @DemandId;
            """,
            conn,
            tx);
        BindDemand(update, demand);
        update.ExecuteNonQuery();
    }

    private static void UpsertPause(SqlConnection conn, SqlTransaction tx, TaskTypePauseState pause)
    {
        using var upsert = new SqlCommand(
            """
            MERGE dbo.TaskTypePauses AS target
            USING (SELECT @TaskType AS TaskType) AS source
            ON target.TaskType = source.TaskType
            WHEN MATCHED THEN
                UPDATE SET
                    PausedZeroDrop = @PausedZeroDrop,
                    LastHealthyNonZeroCount = @LastHealthyNonZeroCount,
                    RecoveryStreak = @RecoveryStreak
            WHEN NOT MATCHED THEN
                INSERT (TaskType, PausedZeroDrop, LastHealthyNonZeroCount, RecoveryStreak)
                VALUES (@TaskType, @PausedZeroDrop, @LastHealthyNonZeroCount, @RecoveryStreak);
            """,
            conn,
            tx);
        upsert.Parameters.AddWithValue("@TaskType", pause.TaskType);
        upsert.Parameters.AddWithValue("@PausedZeroDrop", pause.PausedZeroDrop);
        upsert.Parameters.AddWithValue("@LastHealthyNonZeroCount", pause.LastHealthyNonZeroCount);
        upsert.Parameters.AddWithValue("@RecoveryStreak", pause.RecoveryStreak);
        upsert.ExecuteNonQuery();
    }

    private static void BindDemand(SqlCommand cmd, TransportDemand demand)
    {
        cmd.Parameters.AddWithValue("@DemandId", demand.DemandId);
        cmd.Parameters.AddWithValue("@TaskType", demand.TaskType);
        cmd.Parameters.AddWithValue("@Sublot", demand.Sublot);
        cmd.Parameters.AddWithValue("@Area", (object?)demand.Area ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Eqp", (object?)demand.Eqp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Step", (object?)demand.Step ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Dates", demand.Dates);
        cmd.Parameters.AddWithValue("@Package", (object?)demand.Package ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", ToStatusText(demand.Status));
        cmd.Parameters.AddWithValue("@MesLastSeenAt", demand.MesLastSeenAt);
        cmd.Parameters.AddWithValue("@DisappearCount", demand.DisappearCount);
        cmd.Parameters.AddWithValue("@LocationRisk", demand.LocationRisk);
        cmd.Parameters.AddWithValue("@LocationRiskCode", (object?)demand.LocationRiskCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", demand.CreatedAt == default ? demand.MesLastSeenAt : demand.CreatedAt);
        cmd.Parameters.AddWithValue("@GoneAt", (object?)demand.GoneAt ?? DBNull.Value);
    }

    private static TransportDemand? LoadDemandById(
        SqlConnection conn,
        SqlTransaction tx,
        string demandId)
    {
        using var cmd = new SqlCommand(
            """
            SELECT DemandId, TaskType, Sublot, Area, Eqp, Step, Dates, Package, Status,
                   MesLastSeenAt, DisappearCount, LocationRisk, LocationRiskCode, CreatedAt, GoneAt
            FROM dbo.TransportDemands
            WHERE DemandId = @DemandId;
            """,
            conn,
            tx);
        cmd.Parameters.AddWithValue("@DemandId", demandId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDemand(reader) : null;
    }

    private static IReadOnlyList<TransportDemand> LoadDemands(
        SqlConnection conn,
        bool visibleOnly,
        SqlTransaction? tx = null)
    {
        var sql = """
            SELECT DemandId, TaskType, Sublot, Area, Eqp, Step, Dates, Package, Status,
                   MesLastSeenAt, DisappearCount, LocationRisk, LocationRiskCode, CreatedAt, GoneAt
            FROM dbo.TransportDemands
            """;
        if (visibleOnly)
        {
            sql += " WHERE Status = N'VISIBLE'";
        }

        sql += " ORDER BY DemandId;";
        using var cmd = new SqlCommand(sql, conn, tx);
        return ReadDemands(cmd);
    }

    private static IReadOnlyList<TransportDemand> ReadDemands(SqlCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<TransportDemand>();
        while (reader.Read())
        {
            list.Add(ReadDemand(reader));
        }

        return list;
    }

    private static IReadOnlyList<TaskTypePauseState> LoadPauses(SqlConnection conn, SqlTransaction? tx = null)
    {
        using var cmd = new SqlCommand(
            """
            SELECT TaskType, PausedZeroDrop, LastHealthyNonZeroCount, RecoveryStreak
            FROM dbo.TaskTypePauses
            ORDER BY TaskType;
            """,
            conn,
            tx);
        using var reader = cmd.ExecuteReader();
        var list = new List<TaskTypePauseState>();
        while (reader.Read())
        {
            list.Add(new TaskTypePauseState(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        return list;
    }

    private static TransportDemand ReadDemand(SqlDataReader reader) =>
        new()
        {
            DemandId = reader.GetString(0),
            TaskType = reader.GetString(1),
            Sublot = reader.GetString(2),
            Area = reader.IsDBNull(3) ? null : reader.GetString(3),
            Eqp = reader.IsDBNull(4) ? null : reader.GetString(4),
            Step = reader.IsDBNull(5) ? null : reader.GetString(5),
            Dates = reader.GetDateTimeOffset(6),
            Package = reader.IsDBNull(7) ? null : reader.GetString(7),
            Status = ParseStatus(reader.GetString(8)),
            MesLastSeenAt = reader.GetDateTimeOffset(9),
            DisappearCount = reader.GetInt32(10),
            LocationRisk = reader.GetBoolean(11),
            LocationRiskCode = reader.IsDBNull(12) ? null : reader.GetString(12),
            CreatedAt = reader.GetDateTimeOffset(13),
            GoneAt = reader.IsDBNull(14) ? null : reader.GetDateTimeOffset(14),
        };

    private static string ToStatusText(DemandStatus status) =>
        status == DemandStatus.Visible ? "VISIBLE" : "GONE";

    private static DemandStatus ParseStatus(string raw)
    {
        if (raw.Equals("VISIBLE", StringComparison.OrdinalIgnoreCase))
        {
            return DemandStatus.Visible;
        }

        if (raw.Equals("GONE", StringComparison.OrdinalIgnoreCase))
        {
            return DemandStatus.Gone;
        }

        throw new InvalidOperationException($"Unknown TransportDemand status in store: '{raw}'.");
    }
}
