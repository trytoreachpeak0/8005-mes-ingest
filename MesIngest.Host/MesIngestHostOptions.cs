using MesIngest.Core;

namespace MesIngest.Host;

public sealed class MesIngestHostOptions
{
    public const string SectionName = "MesIngest";

    /// <summary>Snapshot adapter: File (recorded CSV) or Oracle (production MES_TASK_UNION).</summary>
    public string SnapshotSource { get; set; } = "File";

    /// <summary>Path to a recorded MES_TASK_UNION CSV. Required for file snapshot mode.</summary>
    public string SnapshotCsvPath { get; set; } = "";

    /// <summary>
    /// Directory containing published query folders (default: queries beside the host).
    /// Production MES_TASK_UNION SQL is read from {QueriesDirectory}/mes-task-union/query.sql.
    /// </summary>
    public string QueriesDirectory { get; set; } = "queries";

    /// <summary>Oracle user. Local/env only — never commit.</summary>
    public string OracleUser { get; set; } = "";

    /// <summary>Oracle password. Local/env only — never commit.</summary>
    public string OraclePassword { get; set; } = "";

    /// <summary>Oracle data source (host:port/service or TNS alias).</summary>
    public string OracleDataSource { get; set; } = "";

    /// <summary>Thin (default) or Thick. Thick requires Instant Client dir.</summary>
    public string OracleMode { get; set; } = "Thin";

    /// <summary>Instant Client directory for Thick mode (or set ORACLE_CLIENT_LIB_DIR).</summary>
    public string OracleInstantClientDir { get; set; } = "";

    /// <summary>Registered Oracle ODBC driver name for the Thick/OCI adapter.</summary>
    public string OracleThickOdbcDriver { get; set; } = "";

    public int OracleConnectTimeoutSeconds { get; set; } = 30;

    public int OracleMinPoolSize { get; set; }

    public int OracleMaxPoolSize { get; set; } = 4;

    /// <summary>Go-live baseline; rows with DATES earlier are not created as VISIBLE.</summary>
    public DateTimeOffset GoLiveBaseline { get; set; } =
        new(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8));

    /// <summary>
    /// When true, run one ingest round during host startup (before listening).
    /// Prefer continuous poll for production; keep one-shot for demos/tests.
    /// </summary>
    public bool RunOneShotOnStartup { get; set; }

    /// <summary>
    /// When true, host a single-flight continuous poll loop (Windows Service / console).
    /// Class default is false so WebApplicationFactory tests stay quiet; production appsettings enables it.
    /// </summary>
    public bool ContinuousPollEnabled { get; set; }

    /// <summary>Seconds to wait after each completed poll round before starting the next.</summary>
    public int PostPollDelaySeconds { get; set; } = 10;

    /// <summary>Per-round snapshot read timeout in seconds.</summary>
    public int QueryTimeoutSeconds { get; set; } = 30;

    /// <summary>Consecutive successful absences before a VISIBLE demand becomes GONE.</summary>
    public int DisappearThreshold { get; set; } = 2;

    /// <summary>
    /// Enter PAUSED_ZERO_DROP when prior healthy non-zero count for a TASK_TYPE
    /// is at/above this threshold and the next successful count is 0.
    /// </summary>
    public int ZeroDropEnterThreshold { get; set; } = 10;

    /// <summary>
    /// Consecutive successful non-zero rounds required to clear PAUSED_ZERO_DROP.
    /// </summary>
    public int ZeroDropClearStreak { get; set; } = TransportDemandReconciler.DefaultZeroDropClearStreak;

    /// <summary>
    /// Kestrel listen URLs. Default is localhost-only. Binding beyond localhost requires SharedSecret.
    /// </summary>
    public string Urls { get; set; } = "http://127.0.0.1:5088";

    /// <summary>
    /// Shared secret for read-only HTTP when Urls binds beyond localhost and for
    /// restricted raw-evidence reads on every binding, including localhost.
    /// Callers send <c>Authorization: Bearer &lt;SharedSecret&gt;</c>. Local/env only — never commit a real value.
    /// </summary>
    public string SharedSecret { get; set; } = "";

    /// <summary>
    /// SQL Server connection string for durable projection. When empty, Host uses in-memory store
    /// (tests / local CSV demos). Put real credentials in appsettings.Local.json or env vars — never commit.
    /// </summary>
    public string SqlServerConnectionString { get; set; } = "";

    /// <summary>
    /// Allows the legacy schema/API alongside v2 only while running in the ASP.NET Core
    /// Development environment. Production ignores this flag whenever v2 is configured.
    /// </summary>
    public bool EnableLegacyDevelopmentEndpoints { get; set; }

    /// <summary>
    /// Dedicated SQL Server connection string for the isolated new-MesIngest schema.
    /// Required by the production Host. Development may omit it only to run the explicitly
    /// enabled legacy surface. Put real credentials in local configuration or environment
    /// variables only.
    /// </summary>
    public string NewSqlServerConnectionString { get; set; } = "";

    /// <summary>
    /// DemandChangeFeed retention in hours. Default 48. 0 keeps the ledger permanently.
    /// </summary>
    public int ChangeFeedRetentionHours { get; set; } = 48;

    /// <summary>
    /// Resolved IngestAlert retention in days. Default 365. 0 keeps resolved incidents permanently.
    /// Active incidents are never purged by retention.
    /// </summary>
    public int AlertRetentionDays { get; set; } = 365;

    /// <summary>
    /// Path to a recorded MES_TASK_UNION rounds file. When set, the production round
    /// source reads its statement results from that file instead of the plant database,
    /// so a release smoke can drive repeatable rounds with no factory Oracle. Requires
    /// <see cref="ReplayRoundsAcknowledgement"/>; see <c>ReleaseSmokeRoundReplay</c>.
    /// </summary>
    public string ReplayRoundsFromRecordingPath { get; set; } = "";

    /// <summary>
    /// Must equal <c>ReleaseSmokeRoundReplay.RequiredAcknowledgement</c> whenever
    /// <see cref="ReplayRoundsFromRecordingPath"/> is set. Recorded rounds are never
    /// factory acceptance evidence.
    /// </summary>
    public string ReplayRoundsAcknowledgement { get; set; } = "";

    public bool IsOracleSnapshotSource() =>
        SnapshotSource.Equals("Oracle", StringComparison.OrdinalIgnoreCase);

    public OracleClientMode ParseOracleMode()
    {
        if (OracleMode.Equals("Thick", StringComparison.OrdinalIgnoreCase))
        {
            return OracleClientMode.Thick;
        }

        if (OracleMode.Equals("Thin", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(OracleMode))
        {
            return OracleClientMode.Thin;
        }

        throw new InvalidOperationException(
            "MesIngest:OracleMode must be Thin or Thick.");
    }

    public OracleSnapshotOptions ToOracleSnapshotOptions(string? contentRoot = null)
    {
        var queriesRoot = ResolveQueriesDirectory(contentRoot);
        var instantClientDir = string.IsNullOrWhiteSpace(OracleInstantClientDir)
            ? Environment.GetEnvironmentVariable("ORACLE_CLIENT_LIB_DIR")?.Trim() ?? string.Empty
            : OracleInstantClientDir.Trim();
        return new OracleSnapshotOptions
        {
            User = OracleUser,
            Password = OraclePassword,
            DataSource = OracleDataSource,
            Mode = ParseOracleMode(),
            InstantClientDir = instantClientDir,
            ThickOdbcDriver = OracleThickOdbcDriver,
            ConnectTimeoutSeconds = OracleConnectTimeoutSeconds,
            CommandTimeoutSeconds = Math.Max(1, QueryTimeoutSeconds),
            MinPoolSize = OracleMinPoolSize,
            MaxPoolSize = OracleMaxPoolSize,
            QuerySqlPath = Path.Combine(queriesRoot, "mes-task-union", "query.sql"),
        };
    }

    /// <summary>
    /// Prefer the host base directory (where build copies queries/), then content root.
    /// </summary>
    public string ResolveQueriesDirectory(string? contentRoot = null)
    {
        if (Path.IsPathRooted(QueriesDirectory))
        {
            return QueriesDirectory;
        }

        var fromBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, QueriesDirectory));
        if (Directory.Exists(fromBase)
            || File.Exists(Path.Combine(fromBase, "mes-task-union", "query.sql")))
        {
            return fromBase;
        }

        if (!string.IsNullOrWhiteSpace(contentRoot))
        {
            return Path.GetFullPath(Path.Combine(contentRoot, QueriesDirectory));
        }

        return fromBase;
    }
}
