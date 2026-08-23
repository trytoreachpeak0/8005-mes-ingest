namespace MesIngest.Infrastructure.SqlServer;

/// <summary>
/// An explicit request to establish a new history identity while bootstrapping
/// an empty database. These intents never rotate an existing database in place.
/// </summary>
public enum HistoryEpochBootstrapIntent
{
    PlannedEmptyDatabase,
    UnrecoverableRebuild,
}
