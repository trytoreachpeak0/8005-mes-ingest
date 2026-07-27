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
    IReadOnlyList<TransportDemand> List(
        DemandStatus? status = null,
        string? taskType = null,
        string? sublot = null,
        string? demandId = null);
    void AppendAlerts(IReadOnlyList<IngestAlert> alerts);
    IReadOnlyList<IngestAlert> ListAlerts(int? limit = null);
    void SetLatestPollHealth(PollHealth health);
    PollHealth? GetLatestPollHealth();
}

public sealed class InMemoryTransportDemandStore : ITransportDemandStore
{
    public const int DefaultAlertLimit = 100;

    private ProjectionState _state = ProjectionState.Empty;
    private readonly List<IngestAlert> _alerts = new();
    private PollHealth? _latestPollHealth;

    public ProjectionState GetState() => _state;

    public void ReplaceState(ProjectionState state) => _state = state;

    public TransportDemand? GetById(string demandId) =>
        _state.Demands.FirstOrDefault(d => d.DemandId == demandId);

    public IReadOnlyList<TransportDemand> List(
        DemandStatus? status = null,
        string? taskType = null,
        string? sublot = null,
        string? demandId = null)
    {
        IEnumerable<TransportDemand> query = _state.Demands;
        if (status is not null)
        {
            query = query.Where(d => d.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(taskType))
        {
            query = query.Where(d => string.Equals(d.TaskType, taskType, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(sublot))
        {
            query = query.Where(d => string.Equals(d.Sublot, sublot, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(demandId))
        {
            query = query.Where(d => string.Equals(d.DemandId, demandId, StringComparison.Ordinal));
        }

        return query.ToList();
    }

    public void AppendAlerts(IReadOnlyList<IngestAlert> alerts)
    {
        var stamped = DateTimeOffset.Now;
        foreach (var alert in alerts)
        {
            _alerts.Add(alert.CreatedAt is null ? alert with { CreatedAt = stamped } : alert);
        }
    }

    public IReadOnlyList<IngestAlert> ListAlerts(int? limit = null)
    {
        var take = Math.Max(1, limit ?? DefaultAlertLimit);
        // Newest first: reverse append order (CreatedAt may collide within a batch).
        return _alerts
            .AsEnumerable()
            .Reverse()
            .Take(take)
            .ToList();
    }

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
    private readonly int _zeroDropClearStreak;
    private readonly TimeSpan _queryTimeout;
    private readonly Func<DateTimeOffset> _clock;
    private RestartRecoveryPhase _nextPhase = RestartRecoveryPhase.BarrierRound;
    private IReadOnlyDictionary<string, int>? _barrierRoundCountsByType;

    public IngestRoundRunner(
        IMesSnapshotSource source,
        TransportDemandReconciler reconciler,
        ITransportDemandStore store,
        DateTimeOffset goLiveBaseline,
        int disappearThreshold = 2,
        int zeroDropEnterThreshold = 10,
        int zeroDropClearStreak = TransportDemandReconciler.DefaultZeroDropClearStreak,
        TimeSpan? queryTimeout = null,
        Func<DateTimeOffset>? clock = null)
    {
        _source = source;
        _reconciler = reconciler;
        _store = store;
        _goLiveBaseline = goLiveBaseline;
        _disappearThreshold = disappearThreshold;
        _zeroDropEnterThreshold = zeroDropEnterThreshold;
        _zeroDropClearStreak = zeroDropClearStreak;
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task<ProjectionState> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = _clock();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = await ReadSnapshotAsync(cancellationToken);
        var restartRecovery = ResolveRestartRecovery();
        var result = _reconciler.Reconcile(
            _store.GetState(),
            snapshot,
            _clock(),
            _goLiveBaseline,
            _disappearThreshold,
            _zeroDropEnterThreshold,
            _zeroDropClearStreak,
            restartRecovery: restartRecovery);
        sw.Stop();
        var endedAt = _clock();

        if (snapshot.Kind == SnapshotOutcomeKind.Success)
        {
            _store.ReplaceState(result.State);
            _store.AppendAlerts(result.Alerts);
        }
        else if (snapshot.Kind == SnapshotOutcomeKind.Failure)
        {
            _store.AppendAlerts(
            [
                new IngestAlert(
                    Code: "POLL_FAILURE",
                    Message: "MES snapshot round failed; projection left unchanged."),
            ]);
        }
        else if (snapshot.Kind == SnapshotOutcomeKind.Incomplete)
        {
            _store.AppendAlerts(
            [
                new IngestAlert(
                    Code: "POLL_INCOMPLETE",
                    Message: "MES snapshot round incomplete; projection left unchanged."),
            ]);
        }

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

        if (snapshot.Kind == SnapshotOutcomeKind.Success)
        {
            RecordSuccessfulRound(snapshot);
        }

        return result.State;
    }

    private async Task<MesSnapshotOutcome> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_queryTimeout);
        try
        {
            return await _source.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return MesSnapshotOutcome.Failure();
        }
        catch (Exception)
        {
            return MesSnapshotOutcome.Failure();
        }
    }

    private RestartRecovery ResolveRestartRecovery() =>
        _nextPhase switch
        {
            RestartRecoveryPhase.BarrierRound => RestartRecovery.BarrierRound(),
            RestartRecoveryPhase.PostBarrierRound => RestartRecovery.PostBarrierRound(
                _barrierRoundCountsByType ?? new Dictionary<string, int>(StringComparer.Ordinal)),
            _ => RestartRecovery.Normal,
        };

    private void RecordSuccessfulRound(MesSnapshotOutcome snapshot)
    {
        if (_nextPhase == RestartRecoveryPhase.BarrierRound)
        {
            _barrierRoundCountsByType = snapshot.Rows
                .GroupBy(r => r.TaskType)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            _nextPhase = RestartRecoveryPhase.PostBarrierRound;
            return;
        }

        if (_nextPhase == RestartRecoveryPhase.PostBarrierRound)
        {
            _barrierRoundCountsByType = null;
            _nextPhase = RestartRecoveryPhase.Normal;
        }
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
