namespace MesIngest.Core;

public interface ITransportDemandStore
{
    ProjectionState GetState();
    void ReplaceState(ProjectionState state);
    TransportDemand? GetById(string demandId);
    IReadOnlyList<TransportDemand> List(DemandStatus? status = null);
}

public sealed class InMemoryTransportDemandStore : ITransportDemandStore
{
    private ProjectionState _state = ProjectionState.Empty;

    public ProjectionState GetState() => _state;

    public void ReplaceState(ProjectionState state) => _state = state;

    public TransportDemand? GetById(string demandId) =>
        _state.Demands.FirstOrDefault(d => d.DemandId == demandId);

    public IReadOnlyList<TransportDemand> List(DemandStatus? status = null) =>
        status is null
            ? _state.Demands
            : _state.Demands.Where(d => d.Status == status).ToList();
}

public sealed class IngestRoundRunner
{
    private readonly IMesSnapshotSource _source;
    private readonly TransportDemandReconciler _reconciler;
    private readonly ITransportDemandStore _store;
    private readonly DateTimeOffset _goLiveBaseline;
    private readonly Func<DateTimeOffset> _clock;

    public IngestRoundRunner(
        IMesSnapshotSource source,
        TransportDemandReconciler reconciler,
        ITransportDemandStore store,
        DateTimeOffset goLiveBaseline,
        Func<DateTimeOffset>? clock = null)
    {
        _source = source;
        _reconciler = reconciler;
        _store = store;
        _goLiveBaseline = goLiveBaseline;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task<ProjectionState> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _source.ReadAsync(cancellationToken);
        var result = _reconciler.Reconcile(_store.GetState(), snapshot, _clock(), _goLiveBaseline);
        _store.ReplaceState(result.State);
        return result.State;
    }
}
