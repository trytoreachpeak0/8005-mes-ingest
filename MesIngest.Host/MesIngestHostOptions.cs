namespace MesIngest.Host;

public sealed class MesIngestHostOptions
{
    public const string SectionName = "MesIngest";

    /// <summary>Path to a recorded MES_TASK_UNION CSV. Required for ticket-01 file mode.</summary>
    public string SnapshotCsvPath { get; set; } = "";

    /// <summary>Go-live baseline; rows with DATES earlier are not created as VISIBLE.</summary>
    public DateTimeOffset GoLiveBaseline { get; set; } =
        new(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8));

    public bool RunOneShotOnStartup { get; set; } = true;

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
}
