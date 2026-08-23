using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

/// <summary>
/// Stable checkpoints within one successful ProjectionCommit transaction.
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

public sealed record ProjectionCommitCheckpointContext(
    string PollTraceId,
    string ProjectionCommitId,
    HistoryEpoch HistoryEpoch);

/// <summary>
/// Injectable production seam for observing or deliberately failing a SQL
/// projection transaction without replacing its connection or transaction.
/// </summary>
public interface IProjectionCommitCheckpointObserver
{
    Task OnCheckpointAsync(
        ProjectionCommitCheckpoint checkpoint,
        ProjectionCommitCheckpointContext context,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken);
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
