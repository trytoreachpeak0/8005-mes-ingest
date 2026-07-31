using System.Text;
using Microsoft.Data.SqlClient;

namespace MesIngest.Core;

public sealed class SqlServerTransportDemandStore : ITransportDemandStore
{
    private readonly string _connectionString;
    private readonly ILatencyTelemetry _telemetry;
    private readonly TimeSpan _changeFeedRetention;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    public SqlServerTransportDemandStore(
        string connectionString,
        ILatencyTelemetry? telemetry = null,
        TimeSpan? changeFeedRetention = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQL Server connection string is required.", nameof(connectionString));
        }

        _connectionString = connectionString;
        _telemetry = telemetry ?? NullLatencyTelemetry.Instance;
        _changeFeedRetention = changeFeedRetention ?? DemandChangeFeedQuery.DefaultRetention;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
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
            var now = _clock();
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
                        if (demand.Status == DemandStatus.Gone)
                        {
                            AppendChangeFeed(conn, tx, DemandChangeType.Gone, demand, now);
                        }
                    }

                    continue;
                }

                var prior = LoadDemandById(conn, tx, demand.DemandId);
                if (prior is null)
                {
                    InsertDemand(conn, tx, demand);
                    AppendChangeFeed(conn, tx, DemandChangeType.Created, demand, now);
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
                    if (demand.Status == DemandStatus.Gone)
                    {
                        AppendChangeFeed(conn, tx, DemandChangeType.Gone, demand, now);
                    }
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

            PurgeChangeFeed(conn, tx, now);
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

    public DemandListPage QueryPage(DemandListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        DemandListCursor.CursorPayload? cursorPayload = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!DemandListCursor.TryDecode(
                    query.Cursor,
                    query.SortBy,
                    query.Direction,
                    out var decoded,
                    out var error))
            {
                throw new ArgumentException(error ?? "cursor is invalid", nameof(query));
            }

            cursorPayload = decoded;
        }

        lock (_gate)
        {
            using var conn = Open();
            using var cmd = BuildQueryPageCommand(conn, query, cursorPayload);
            var rows = ReadDemands(cmd).ToList();
            var hasMore = rows.Count > query.Limit;
            if (hasMore)
            {
                rows.RemoveRange(query.Limit, rows.Count - query.Limit);
            }

            string? nextCursor = null;
            if (hasMore && rows.Count > 0)
            {
                nextCursor = DemandListCursor.Encode(query.SortBy, query.Direction, rows[^1]);
            }

            return new DemandListPage(rows, nextCursor, hasMore);
        }
    }

    public DemandChangeFeedPage QueryChangeFeed(DemandChangeFeedQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_gate)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            PurgeChangeFeed(conn, tx, query.AsOf);

            long highWatermark;
            long? earliest;
            using (var bounds = new SqlCommand(
                       """
                       SELECT
                           ISNULL(MAX([Sequence]), 0),
                           MIN([Sequence])
                       FROM dbo.DemandChangeFeed;
                       """,
                       conn,
                       tx))
            {
                using var reader = bounds.ExecuteReader();
                reader.Read();
                highWatermark = reader.GetInt64(0);
                earliest = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }

            if (earliest is long e
                && query.AfterSequence > 0
                && query.AfterSequence < e - 1)
            {
                throw new SyncCursorExpiredException(query.AfterSequence, e, highWatermark);
            }

            using var cmd = new SqlCommand(
                """
                SELECT TOP (@Take)
                    [Sequence], DemandId, ChangeType, ChangedAt,
                    TaskType, Sublot, Area, Eqp, Step, Dates, Package, Status,
                    LocationRisk, LocationRiskCode, CreatedAt, GoneAt
                FROM dbo.DemandChangeFeed
                WHERE [Sequence] > @AfterSequence
                ORDER BY [Sequence] ASC;
                """,
                conn,
                tx);
            cmd.Parameters.AddWithValue("@Take", query.Limit + 1);
            cmd.Parameters.AddWithValue("@AfterSequence", query.AfterSequence);

            var items = new List<DemandChangeFeedEntry>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    items.Add(ReadChangeFeedEntry(reader));
                }
            }

            tx.Commit();

            var hasMore = items.Count > query.Limit;
            if (hasMore)
            {
                items.RemoveAt(items.Count - 1);
            }

            return new DemandChangeFeedPage(
                items,
                hasMore ? items[^1].Sequence : null,
                hasMore,
                highWatermark,
                earliest);
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

            IF OBJECT_ID(N'dbo.DemandChangeFeed', N'U') IS NULL
            EXEC(N'
                CREATE TABLE dbo.DemandChangeFeed
                (
                    [Sequence] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DemandChangeFeed PRIMARY KEY,
                    DemandId NVARCHAR(64) NOT NULL,
                    ChangeType NVARCHAR(16) NOT NULL,
                    ChangedAt DATETIMEOFFSET NOT NULL,
                    TaskType NVARCHAR(128) NOT NULL,
                    Sublot NVARCHAR(128) NOT NULL,
                    Area NVARCHAR(256) NULL,
                    Eqp NVARCHAR(256) NULL,
                    Step NVARCHAR(256) NULL,
                    Dates DATETIMEOFFSET NOT NULL,
                    Package NVARCHAR(256) NULL,
                    Status NVARCHAR(16) NOT NULL,
                    LocationRisk BIT NOT NULL,
                    LocationRiskCode NVARCHAR(64) NULL,
                    CreatedAt DATETIMEOFFSET NOT NULL,
                    GoneAt DATETIMEOFFSET NULL
                );
            ');

            IF OBJECT_ID(N'dbo.DemandChangeFeed', N'U') IS NOT NULL
               AND NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = N'IX_DemandChangeFeed_ChangedAt'
                      AND object_id = OBJECT_ID(N'dbo.DemandChangeFeed'))
            EXEC(N'CREATE INDEX IX_DemandChangeFeed_ChangedAt ON dbo.DemandChangeFeed (ChangedAt);');
            """;
        cmd.ExecuteNonQuery();
    }

    private static void AppendChangeFeed(
        SqlConnection conn,
        SqlTransaction tx,
        DemandChangeType changeType,
        TransportDemand demand,
        DateTimeOffset changedAt)
    {
        using var insert = new SqlCommand(
            """
            INSERT INTO dbo.DemandChangeFeed
            (DemandId, ChangeType, ChangedAt, TaskType, Sublot, Area, Eqp, Step, Dates, Package,
             Status, LocationRisk, LocationRiskCode, CreatedAt, GoneAt)
            VALUES
            (@DemandId, @ChangeType, @ChangedAt, @TaskType, @Sublot, @Area, @Eqp, @Step, @Dates, @Package,
             @Status, @LocationRisk, @LocationRiskCode, @CreatedAt, @GoneAt);
            """,
            conn,
            tx);
        insert.Parameters.AddWithValue("@DemandId", demand.DemandId);
        insert.Parameters.AddWithValue(
            "@ChangeType",
            changeType == DemandChangeType.Created ? "CREATED" : "GONE");
        insert.Parameters.AddWithValue("@ChangedAt", changedAt);
        insert.Parameters.AddWithValue("@TaskType", demand.TaskType);
        insert.Parameters.AddWithValue("@Sublot", demand.Sublot);
        insert.Parameters.AddWithValue("@Area", (object?)demand.Area ?? DBNull.Value);
        insert.Parameters.AddWithValue("@Eqp", (object?)demand.Eqp ?? DBNull.Value);
        insert.Parameters.AddWithValue("@Step", (object?)demand.Step ?? DBNull.Value);
        insert.Parameters.AddWithValue("@Dates", demand.Dates);
        insert.Parameters.AddWithValue("@Package", (object?)demand.Package ?? DBNull.Value);
        insert.Parameters.AddWithValue("@Status", ToStatusText(demand.Status));
        insert.Parameters.AddWithValue("@LocationRisk", demand.LocationRisk);
        insert.Parameters.AddWithValue("@LocationRiskCode", (object?)demand.LocationRiskCode ?? DBNull.Value);
        insert.Parameters.AddWithValue("@CreatedAt", demand.CreatedAt);
        insert.Parameters.AddWithValue("@GoneAt", (object?)demand.GoneAt ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    private void PurgeChangeFeed(SqlConnection conn, SqlTransaction tx, DateTimeOffset asOf)
    {
        if (_changeFeedRetention <= TimeSpan.Zero)
        {
            return;
        }

        using var purge = new SqlCommand(
            """
            DELETE FROM dbo.DemandChangeFeed
            WHERE ChangedAt < @Cutoff;
            """,
            conn,
            tx);
        purge.Parameters.AddWithValue("@Cutoff", asOf - _changeFeedRetention);
        purge.ExecuteNonQuery();
    }

    private static DemandChangeFeedEntry ReadChangeFeedEntry(SqlDataReader reader)
    {
        var changeTypeRaw = reader.GetString(2);
        var changeType = changeTypeRaw.Equals("GONE", StringComparison.OrdinalIgnoreCase)
            ? DemandChangeType.Gone
            : DemandChangeType.Created;
        var payload = new DemandChangePayload(
            reader.GetString(1),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            ParseStatus(reader.GetString(11)),
            reader.GetBoolean(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.GetFieldValue<DateTimeOffset>(14),
            reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15));
        return new DemandChangeFeedEntry(
            reader.GetInt64(0),
            payload.DemandId,
            changeType,
            reader.GetFieldValue<DateTimeOffset>(3),
            payload);
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

    private static SqlCommand BuildQueryPageCommand(
        SqlConnection conn,
        DemandListQuery query,
        DemandListCursor.CursorPayload? cursor)
    {
        var sql = new StringBuilder(
            """
            SELECT TOP (@Take) DemandId, TaskType, Sublot, Area, Eqp, Step, Dates, Package, Status,
                   MesLastSeenAt, DisappearCount, LocationRisk, LocationRiskCode, CreatedAt, GoneAt
            FROM dbo.TransportDemands
            WHERE Status = @Status
            """);
        var cmd = new SqlCommand { Connection = conn };
        cmd.Parameters.AddWithValue("@Take", query.Limit + 1);
        cmd.Parameters.AddWithValue("@Status", ToStatusText(query.Status));

        if (!string.IsNullOrWhiteSpace(query.TaskType))
        {
            sql.Append(" AND TaskType = @TaskType");
            cmd.Parameters.AddWithValue("@TaskType", query.TaskType);
        }

        if (!string.IsNullOrWhiteSpace(query.Sublot))
        {
            sql.Append(" AND Sublot = @Sublot");
            cmd.Parameters.AddWithValue("@Sublot", query.Sublot);
        }

        if (query.DemandId is { } demandId)
        {
            if (demandId.IsPrefix)
            {
                sql.Append(" AND DemandId LIKE @DemandIdPrefix ESCAPE N'\\'");
                cmd.Parameters.AddWithValue("@DemandIdPrefix", EscapeLikePrefix(demandId.Value) + "%");
            }
            else
            {
                sql.Append(" AND DemandId = @DemandId");
                cmd.Parameters.AddWithValue("@DemandId", demandId.Value);
            }
        }

        if (query.DatesFrom is { } datesFrom)
        {
            sql.Append(" AND Dates >= @DatesFrom");
            cmd.Parameters.AddWithValue("@DatesFrom", datesFrom);
        }

        if (query.DatesTo is { } datesTo)
        {
            sql.Append(" AND Dates <= @DatesTo");
            cmd.Parameters.AddWithValue("@DatesTo", datesTo);
        }

        if (query.EffectiveGoneAtFrom is { } goneFrom)
        {
            sql.Append(" AND GoneAt IS NOT NULL AND GoneAt >= @GoneAtFrom");
            cmd.Parameters.AddWithValue("@GoneAtFrom", goneFrom);
        }

        if (query.EffectiveGoneAtTo is { } goneTo)
        {
            sql.Append(" AND GoneAt IS NOT NULL AND GoneAt <= @GoneAtTo");
            cmd.Parameters.AddWithValue("@GoneAtTo", goneTo);
        }

        if (cursor is not null)
        {
            AppendKeysetPredicate(sql, cmd, query.SortBy, query.Direction, cursor);
        }

        sql.Append(' ').Append(BuildOrderByClause(query.SortBy, query.Direction));
        cmd.CommandText = sql.ToString();
        return cmd;
    }

    private static void AppendKeysetPredicate(
        StringBuilder sql,
        SqlCommand cmd,
        DemandSortColumn sortBy,
        SortDirection direction,
        DemandListCursor.CursorPayload cursor)
    {
        var gt = direction == SortDirection.Asc;
        cmd.Parameters.AddWithValue("@CursorDemandId", cursor.DemandId);
        switch (sortBy)
        {
            case DemandSortColumn.DemandId:
                sql.Append(gt ? " AND DemandId > @CursorDemandId" : " AND DemandId < @CursorDemandId");
                break;
            case DemandSortColumn.GoneAt:
                if (cursor.GoneAt is null)
                {
                    // Match in-memory MinValue treatment without wrapping GoneAt in a function.
                    sql.Append(gt
                        ? " AND (GoneAt IS NOT NULL OR (GoneAt IS NULL AND DemandId > @CursorDemandId))"
                        : " AND (GoneAt IS NULL AND DemandId > @CursorDemandId)");
                }
                else
                {
                    cmd.Parameters.AddWithValue("@CursorGoneAt", cursor.GoneAt.Value);
                    sql.Append(gt
                        ? " AND ((GoneAt > @CursorGoneAt) OR (GoneAt = @CursorGoneAt AND DemandId > @CursorDemandId))"
                        : " AND ((GoneAt < @CursorGoneAt) OR (GoneAt = @CursorGoneAt AND DemandId > @CursorDemandId) OR GoneAt IS NULL)");
                }

                break;
            case DemandSortColumn.CreatedAt:
                cmd.Parameters.AddWithValue("@CursorCreatedAt", cursor.CreatedAt ?? default);
                sql.Append(gt
                    ? " AND (CreatedAt > @CursorCreatedAt OR (CreatedAt = @CursorCreatedAt AND DemandId > @CursorDemandId))"
                    : " AND (CreatedAt < @CursorCreatedAt OR (CreatedAt = @CursorCreatedAt AND DemandId > @CursorDemandId))");
                break;
            case DemandSortColumn.MesLastSeenAt:
                cmd.Parameters.AddWithValue("@CursorMesLastSeenAt", cursor.MesLastSeenAt ?? default);
                sql.Append(gt
                    ? " AND (MesLastSeenAt > @CursorMesLastSeenAt OR (MesLastSeenAt = @CursorMesLastSeenAt AND DemandId > @CursorDemandId))"
                    : " AND (MesLastSeenAt < @CursorMesLastSeenAt OR (MesLastSeenAt = @CursorMesLastSeenAt AND DemandId > @CursorDemandId))");
                break;
            case DemandSortColumn.TaskType:
                cmd.Parameters.AddWithValue("@CursorTaskType", cursor.TaskType ?? "");
                sql.Append(gt
                    ? " AND (TaskType > @CursorTaskType OR (TaskType = @CursorTaskType AND DemandId > @CursorDemandId))"
                    : " AND (TaskType < @CursorTaskType OR (TaskType = @CursorTaskType AND DemandId > @CursorDemandId))");
                break;
            case DemandSortColumn.Sublot:
                cmd.Parameters.AddWithValue("@CursorSublot", cursor.Sublot ?? "");
                sql.Append(gt
                    ? " AND (Sublot > @CursorSublot OR (Sublot = @CursorSublot AND DemandId > @CursorDemandId))"
                    : " AND (Sublot < @CursorSublot OR (Sublot = @CursorSublot AND DemandId > @CursorDemandId))");
                break;
            default:
                cmd.Parameters.AddWithValue("@CursorDates", cursor.Dates ?? default);
                sql.Append(gt
                    ? " AND (Dates > @CursorDates OR (Dates = @CursorDates AND DemandId > @CursorDemandId))"
                    : " AND (Dates < @CursorDates OR (Dates = @CursorDates AND DemandId > @CursorDemandId))");
                break;
        }
    }

    private static string BuildOrderByClause(DemandSortColumn sortBy, SortDirection direction)
    {
        var dir = direction == SortDirection.Asc ? "ASC" : "DESC";
        return sortBy switch
        {
            DemandSortColumn.DemandId => $"ORDER BY DemandId {dir}",
            DemandSortColumn.GoneAt => $"ORDER BY GoneAt {dir}, DemandId ASC",
            DemandSortColumn.CreatedAt => $"ORDER BY CreatedAt {dir}, DemandId ASC",
            DemandSortColumn.MesLastSeenAt => $"ORDER BY MesLastSeenAt {dir}, DemandId ASC",
            DemandSortColumn.TaskType => $"ORDER BY TaskType {dir}, DemandId ASC",
            DemandSortColumn.Sublot => $"ORDER BY Sublot {dir}, DemandId ASC",
            _ => $"ORDER BY Dates {dir}, DemandId ASC",
        };
    }

    private static string EscapeLikePrefix(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

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
