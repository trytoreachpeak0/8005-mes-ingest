namespace MesIngest.Core;

public enum OracleClientMode
{
    Thin,
    Thick,
}

/// <summary>
/// Connection and SQL path settings for the production Oracle MES_TASK_UNION round source.
/// Credentials belong in local config / env only — never commit secrets.
/// </summary>
public sealed class OracleSnapshotOptions
{
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string DataSource { get; set; } = "";
    public OracleClientMode Mode { get; set; } = OracleClientMode.Thin;
    public string InstantClientDir { get; set; } = "";
    /// <summary>Registered Oracle ODBC driver used by the OCI/Instant Client adapter.</summary>
    public string ThickOdbcDriver { get; set; } = "";
    public int ConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>ODP.NET command timeout in seconds (statement-level).</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    public int MinPoolSize { get; set; }
    public int MaxPoolSize { get; set; } = 4;

    /// <summary>Absolute or relative path to the published mes-task-union query.sql.</summary>
    public string QuerySqlPath { get; set; } = "";
}
