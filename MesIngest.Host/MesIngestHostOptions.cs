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

    public string Urls { get; set; } = "http://127.0.0.1:5088";

    /// <summary>
    /// SQL Server connection string for durable projection. When empty, Host uses in-memory store
    /// (tests / local CSV demos). Put real credentials in appsettings.Local.json or env vars — never commit.
    /// </summary>
    public string SqlServerConnectionString { get; set; } = "";

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
        return new OracleSnapshotOptions
        {
            User = OracleUser,
            Password = OraclePassword,
            DataSource = OracleDataSource,
            Mode = ParseOracleMode(),
            InstantClientDir = OracleInstantClientDir,
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
