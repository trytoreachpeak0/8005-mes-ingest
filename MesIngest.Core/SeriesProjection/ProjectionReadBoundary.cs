namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Production read surfaces whose response bodies are assembled behind a
/// previously selected, immutable projection fence.
/// </summary>
public enum ProjectionReadSurface
{
    DemandSeries,
    Catalog,
    CurrentIngestAttention,
}

/// <summary>
/// The complete high-water identity selected before a projection body is read.
/// Fields that do not apply to a surface, and the initial empty catalog, are null.
/// </summary>
public sealed record ProjectionReadFence(
    HistoryEpoch HistoryEpoch,
    string? ProjectionCommitId,
    long? ProjectionSequence,
    long? CatalogRevision = null,
    long? PollTraceHighWater = null);

/// <summary>
/// Observable production boundary for proving that a read is wholly assembled
/// from the selected old-or-new fence while commits continue concurrently.
/// </summary>
public interface IProjectionReadBoundaryObserver
{
    Task OnFenceSelectedAsync(
        ProjectionReadSurface surface,
        ProjectionReadFence fence,
        CancellationToken cancellationToken);
}

public sealed class NoopProjectionReadBoundaryObserver : IProjectionReadBoundaryObserver
{
    public static NoopProjectionReadBoundaryObserver Instance { get; } = new();

    private NoopProjectionReadBoundaryObserver()
    {
    }

    public Task OnFenceSelectedAsync(
        ProjectionReadSurface surface,
        ProjectionReadFence fence,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
