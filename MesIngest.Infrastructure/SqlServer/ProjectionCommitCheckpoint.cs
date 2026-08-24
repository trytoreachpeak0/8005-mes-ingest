using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

/// <summary>
/// Stable checkpoints within a projection or whole-Series cleanup transaction.
/// </summary>
public enum ProjectionCommitCheckpoint
{
    RoundEvidencePersisted,
    ProtectionAndUnassignedPersisted,
    DemandProjectionPersisted,
    AbsenceAndArchivePersisted,
    CatalogPersisted,
    BeforeCommit,
}

public enum SeriesCleanupCheckpoint
{
    ArchivedKeyTombstonePersisted,
    CurrentReadModelsDeleted,
    ErrorGraphDeleted,
    EventsDeleted,
    DemandsDeleted,
    SeriesRootDeleted,
    BeforeCommit,
}

public static class SeriesCleanupCheckpointContract
{
    public static IReadOnlyList<SeriesCleanupCheckpoint> TransactionFailpoints { get; } =
    [
        SeriesCleanupCheckpoint.ArchivedKeyTombstonePersisted,
        SeriesCleanupCheckpoint.CurrentReadModelsDeleted,
        SeriesCleanupCheckpoint.ErrorGraphDeleted,
        SeriesCleanupCheckpoint.EventsDeleted,
        SeriesCleanupCheckpoint.DemandsDeleted,
        SeriesCleanupCheckpoint.SeriesRootDeleted,
        SeriesCleanupCheckpoint.BeforeCommit,
    ];
}

public sealed record ProjectionCommitCheckpointContext(
    string PollTraceId,
    string ProjectionCommitId,
    HistoryEpoch HistoryEpoch);

/// <summary>
/// Injectable production seam for observing or deliberately failing a SQL
/// projection or Series-cleanup transaction without replacing its connection
/// or transaction.
/// </summary>
public interface IProjectionCommitCheckpointObserver
{
    Task OnCheckpointAsync(
        ProjectionCommitCheckpoint checkpoint,
        ProjectionCommitCheckpointContext context,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken);

    Task OnSeriesCleanupCheckpointAsync(
        SeriesCleanupCheckpoint checkpoint,
        ProjectionCommitCheckpointContext context,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class NoopProjectionCommitCheckpointObserver
    : IProjectionCommitCheckpointObserver
{
    public static NoopProjectionCommitCheckpointObserver Instance { get; } = new();

    private NoopProjectionCommitCheckpointObserver()
    {
    }

    public Task OnCheckpointAsync(
        ProjectionCommitCheckpoint checkpoint,
        ProjectionCommitCheckpointContext context,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
