using MesIngest.Core;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Host;

public sealed class MesIngestHostOptions
{
    public const string SectionName = "MesIngest";

    /// <summary>Round source value that hosts no MES round source at all.</summary>
    public const string NoRoundSource = "None";

    /// <summary>Round source value that hosts the production Oracle MES_TASK_UNION source.</summary>
    public const string OracleRoundSource = "Oracle";

    /// <summary>
    /// Round source adapter. <c>Oracle</c> hosts the production MES_TASK_UNION round source;
    /// <c>None</c> hosts no round source and is only for API-surface fixtures.
    /// </summary>
    public string SnapshotSource { get; set; } = NoRoundSource;

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

    /// <summary>
    /// Fixed start-to-start poll interval. Slow rounds skip missed slots instead of
    /// overlapping or catching up; failures use the fixed 60/120/300 second policy.
    /// </summary>
    public int PollStartIntervalSeconds { get; set; } = 60;

    /// <summary>Per-round snapshot read timeout in seconds.</summary>
    public int QueryTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Host-owned history cleanup check interval. The production default is one
    /// hour; tests may shorten it through configuration without changing the
    /// 30-day retention policy.
    /// </summary>
    public int HistoryCleanupCheckIntervalSeconds { get; set; } = 60 * 60;

    /// <summary>
    /// Maximum raw-observation rows committed by one scheduled cleanup check.
    /// The 210,000 default covers the measured 600 rows per 14-second round for
    /// one hour with more than 30 percent headroom.
    /// </summary>
    public int HistoryCleanupMaximumRawObservationRowsPerBatch { get; set; } = 210_000;

    /// <summary>
    /// Maximum whole Series graphs committed by one scheduled cleanup check.
    /// One Series is always one indivisible tombstone transaction.
    /// </summary>
    public int HistoryCleanupMaximumSeriesPerBatch { get; set; } = 25;

    /// <summary>
    /// Host-UTC elapsed budget checked between cleanup transactions. A started
    /// Series transaction is allowed to finish atomically.
    /// </summary>
    public int HistoryCleanupTimeBudgetSeconds { get; set; } = 15;

    /// <summary>
    /// Enter PAUSED_ZERO_DROP when prior healthy non-zero count for a TASK_TYPE
    /// is at/above this threshold and the next successful count is 0.
    /// </summary>
    public int ZeroDropEnterThreshold { get; set; } = 10;

    /// <summary>
    /// Consecutive successful non-zero rounds required to clear PAUSED_ZERO_DROP.
    /// </summary>
    public int ZeroDropClearStreak { get; set; } = TaskTypeProtectionPolicy.RequiredRecoveryStreak;

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
    /// SQL Server connection string for the MesIngest projection schema. Required by the
    /// production Host; only API-surface fixtures may omit it. The key keeps its
    /// <c>New</c> prefix on purpose: a configuration file written for the retired contract
    /// cannot carry it, so a stale file fails startup instead of pointing this Host at the
    /// retired database. Put real credentials in local configuration or environment
    /// variables only.
    /// </summary>
    public string NewSqlServerConnectionString { get; set; } = "";

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
        SnapshotSource.Equals(OracleRoundSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Configuration keys that belonged to the retired MesIngest contract. They are rejected
    /// rather than ignored so a stale deployment file cannot look accepted while the value it
    /// carries — a retired database, a change-feed retention, a frozen-field switch — silently
    /// does nothing.
    /// </summary>
    public static readonly string[] RetiredConfigurationKeys =
    [
        "AlertRetentionDays",
        "ChangeFeedRetentionHours",
        "DisappearThreshold",
        "EnableLegacyDevelopmentEndpoints",
        "GoLiveBaseline",
        "SnapshotCsvPath",
        "SqlServerConnectionString",
        "PostPollDelaySeconds",
    ];

    /// <summary>
    /// Fails startup when the bound configuration still carries a retired key, or names a
    /// round source this release does not have.
    /// </summary>
    public void ValidateContractShape(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var present = RetiredConfigurationKeys
            .Where(key => section.GetSection(key).Exists())
            .ToArray();
        if (present.Length > 0)
        {
            throw new InvalidOperationException(
                $"{SectionName} configuration still contains retired keys: "
                + string.Join(", ", present)
                + ". They belonged to the replaced MesIngest contract; remove them and use the "
                + "current appsettings template.");
        }

        if (!SnapshotSource.Equals(OracleRoundSource, StringComparison.OrdinalIgnoreCase)
            && !SnapshotSource.Equals(NoRoundSource, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{SectionName}:SnapshotSource must be {OracleRoundSource} or {NoRoundSource}.");
        }
    }

    public void ValidateHistoryCleanupPolicy()
    {
        ValidateBoundedPositive(
            HistoryCleanupCheckIntervalSeconds,
            24 * 60 * 60,
            nameof(HistoryCleanupCheckIntervalSeconds));
        ValidateBounded(
            HistoryCleanupMaximumRawObservationRowsPerBatch,
            HistoryRetentionPolicy.MaximumRawObservationsPerPollTrace,
            1_000_000,
            nameof(HistoryCleanupMaximumRawObservationRowsPerBatch));
        ValidateBoundedPositive(
            HistoryCleanupMaximumSeriesPerBatch,
            1_000,
            nameof(HistoryCleanupMaximumSeriesPerBatch));
        ValidateBoundedPositive(
            HistoryCleanupTimeBudgetSeconds,
            5 * 60,
            nameof(HistoryCleanupTimeBudgetSeconds));
    }

    private static void ValidateBoundedPositive(int value, int maximum, string name)
    {
        if (value is < 1 || value > maximum)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must be between 1 and {maximum}.");
        }
    }

    private static void ValidateBounded(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must be between {minimum} and {maximum}.");
        }
    }

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
