namespace MesIngest.Core;

public sealed record PollHealth(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double DurationMs,
    int RowCount,
    bool Success,
    string Outcome);

public interface ITransportDemandStore
{
    ProjectionState GetState();
    void ReplaceState(ProjectionState state);
    TransportDemand? GetById(string demandId);
    IReadOnlyList<TransportDemand> List(DemandStatus? status = null);
    void AppendAlerts(IReadOnlyList<IngestAlert> alerts);
    IReadOnlyList<IngestAlert> ListAlerts();
    void SetLatestPollHealth(PollHealth health);
    PollHealth? GetLatestPollHealth();
}

public sealed class InMemoryTransportDemandStore : ITransportDemandStore
{
    private ProjectionState _state = ProjectionState.Empty;
    private readonly List<IngestAlert> _alerts = new();
    private PollHealth? _latestPollHealth;

    public ProjectionState GetState() => _state;

    public void ReplaceState(ProjectionState state) => _state = state;

    public TransportDemand? GetById(string demandId) =>
        _state.Demands.FirstOrDefault(d => d.DemandId == demandId);

    public IReadOnlyList<TransportDemand> List(DemandStatus? status = null) =>
        status is null
            ? _state.Demands
            : _state.Demands.Where(d => d.Status == status).ToList();

    public void AppendAlerts(IReadOnlyList<IngestAlert> alerts) => _alerts.AddRange(alerts);

    public IReadOnlyList<IngestAlert> ListAlerts() => _alerts.ToList();

    public void SetLatestPollHealth(PollHealth health) => _latestPollHealth = health;

    public PollHealth? GetLatestPollHealth() => _latestPollHealth;
}

public sealed class IngestRoundRunner
{
    private readonly IMesSnapshotSource _source;
    private readonly TransportDemandReconciler _reconciler;
    private readonly ITransportDemandStore _store;
    private readonly DateTimeOffset _goLiveBaseline;
    private readonly int _disappearThreshold;
    private readonly int _zeroDropEnterThreshold;
    private readonly Func<DateTimeOffset> _clock;

    public IngestRoundRunner(
        IMesSnapshotSource source,
        TransportDemandReconciler reconciler,
        ITransportDemandStore store,
        DateTimeOffset goLiveBaseline,
        int disappearThreshold = 2,
        int zeroDropEnterThreshold = 10,
        Func<DateTimeOffset>? clock = null)
    {
        _source = source;
        _reconciler = reconciler;
        _store = store;
        _goLiveBaseline = goLiveBaseline;
        _disappearThreshold = disappearThreshold;
        _zeroDropEnterThreshold = zeroDropEnterThreshold;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task<ProjectionState> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = _clock();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = await _source.ReadAsync(cancellationToken);
        var result = _reconciler.Reconcile(
            _store.GetState(),
            snapshot,
            _clock(),
            _goLiveBaseline,
            _disappearThreshold,
            _zeroDropEnterThreshold);
        sw.Stop();
        var endedAt = _clock();

        _store.ReplaceState(result.State);
        _store.AppendAlerts(result.Alerts);
        _store.SetLatestPollHealth(new PollHealth(
            StartedAt: startedAt,
            EndedAt: endedAt,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            RowCount: snapshot.Kind == SnapshotOutcomeKind.Success ? snapshot.Rows.Count : 0,
            Success: snapshot.Kind == SnapshotOutcomeKind.Success,
            Outcome: snapshot.Kind switch
            {
                SnapshotOutcomeKind.Success => "SUCCESS",
                SnapshotOutcomeKind.Failure => "FAILURE",
                SnapshotOutcomeKind.Incomplete => "INCOMPLETE",
                _ => "UNKNOWN",
            }));
        return result.State;
    }
}

public sealed class FixedMesSnapshotSource : IMesSnapshotSource
{
    private readonly MesSnapshotOutcome _outcome;

    public FixedMesSnapshotSource(MesSnapshotOutcome outcome)
    {
        _outcome = outcome;
    }

    public Task<MesSnapshotOutcome> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_outcome);
}
