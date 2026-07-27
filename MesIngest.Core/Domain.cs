namespace MesIngest.Core;

public enum DemandStatus
{
    Visible,
    Gone,
}

public enum SnapshotOutcomeKind
{
    Success,
    Failure,
    Incomplete,
}

public sealed record MesSnapshotRow(
    string TaskType,
    string Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset Dates,
    string? Package);

public sealed class MesSnapshotOutcome
{
    private MesSnapshotOutcome(SnapshotOutcomeKind kind, IReadOnlyList<MesSnapshotRow> rows)
    {
        Kind = kind;
        Rows = rows;
    }

    public SnapshotOutcomeKind Kind { get; }
    public IReadOnlyList<MesSnapshotRow> Rows { get; }

    public static MesSnapshotOutcome Success(IReadOnlyList<MesSnapshotRow> rows) =>
        new(SnapshotOutcomeKind.Success, rows);

    public static MesSnapshotOutcome Failure() =>
        new(SnapshotOutcomeKind.Failure, Array.Empty<MesSnapshotRow>());

    public static MesSnapshotOutcome Incomplete() =>
        new(SnapshotOutcomeKind.Incomplete, Array.Empty<MesSnapshotRow>());
}

public sealed record TransportDemand
{
    public required string DemandId { get; init; }
    public required string TaskType { get; init; }
    public required string Sublot { get; init; }
    public string? Area { get; init; }
    public string? Eqp { get; init; }
    public string? Step { get; init; }
    public DateTimeOffset Dates { get; init; }
    public string? Package { get; init; }
    public required DemandStatus Status { get; init; }
    public required DateTimeOffset MesLastSeenAt { get; init; }
    public int DisappearCount { get; init; }
    public bool LocationRisk { get; init; }
    public string? LocationRiskCode { get; init; }
}

public sealed class ProjectionState
{
    public static ProjectionState Empty { get; } = new(Array.Empty<TransportDemand>());

    public ProjectionState(IReadOnlyList<TransportDemand> demands)
    {
        Demands = demands;
    }

    public IReadOnlyList<TransportDemand> Demands { get; }
}

public sealed class ReconcileResult
{
    public ReconcileResult(ProjectionState state, IReadOnlyList<IngestAlert>? alerts = null)
    {
        State = state;
        Alerts = alerts ?? Array.Empty<IngestAlert>();
    }

    public ProjectionState State { get; }
    public IReadOnlyList<IngestAlert> Alerts { get; }
}

public sealed record IngestAlert(
    string Code,
    string? TaskType = null,
    string? Sublot = null,
    string? DemandId = null,
    string? Message = null);

public interface IDemandIdAllocator
{
    string Next();
}

public sealed class SequentialDemandIdAllocator : IDemandIdAllocator
{
    private readonly Queue<string> _ids;

    public SequentialDemandIdAllocator(params string[] ids)
    {
        _ids = new Queue<string>(ids);
    }

    public string Next() =>
        _ids.Count > 0 ? _ids.Dequeue() : Guid.NewGuid().ToString("N");
}

public sealed class GuidDemandIdAllocator : IDemandIdAllocator
{
    public string Next() => Guid.NewGuid().ToString("N");
}
