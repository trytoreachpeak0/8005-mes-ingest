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

    public string Urls { get; set; } = "http://127.0.0.1:5088";
}
