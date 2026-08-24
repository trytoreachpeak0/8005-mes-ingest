namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Stable persisted and API vocabulary for the Ticket06 lifecycle transition.
/// </summary>
public static class DemandSeriesLifecycleContract
{
    public const string Tracking = "TRACKING";
    public const string Archived = "ARCHIVED";
    public const string Visible = "VISIBLE";
    public const string Gone = "GONE";
    public const string LongGoneButVisible = "LONG_GONE_BUT_VISIBLE";
    public const string GoneTimeoutArchivedEvent = "GONE_TIMEOUT_ARCHIVED";
    public const string ArchivedSeriesVisibilitySubject = "ARCHIVED_SERIES_VISIBILITY";
    public const string PostarchiveReappearanceReason = "POSTARCHIVE_REAPPEARANCE";
    public const string SeriesArchivedBlocker = "SERIES_ARCHIVED";
}

/// <summary>
/// Stable safety contract retained after an archived DemandSeries graph expires.
/// </summary>
public static class ArchivedDemandKeyTombstoneContract
{
    public const int Version = 1;
    public const string ArchiveConclusion = DemandSeriesLifecycleContract.Archived;
    public const string ReappearedEvent = "ARCHIVED_DEMAND_KEY_REAPPEARED";
}
