namespace MesIngest.Core;

public sealed record PollHealth(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double DurationMs,
    int RowCount,
    bool Success,
    string Outcome,
    string? FailureStage = null,
    double? OracleDurationMs = null);

public interface ITransportDemandStore
{
    /// <summary>
    /// Hot projection for reconcile: VISIBLE demands + pause states.
    /// Permanent GONE history is not loaded here; use <see cref="HasGoneTransportDemandKey"/>,
    /// <see cref="GetById"/>, or <see cref="List"/>.
    /// </summary>
    ProjectionState GetState();

    /// <summary>
    /// Persist the next hot projection differentially: INSERT new rows, UPDATE changed
    /// VISIBLE / newly-GONE rows, UPSERT changed pauses. Historical GONE omitted from
    /// <paramref name="state"/> are retained and never rewritten.
    /// When <paramref name="alerts"/> is provided, they are appended in the same transaction
    /// as the projection writes (SQL) or the same atomic update (in-memory).
    /// </summary>
    void ReplaceState(ProjectionState state, IReadOnlyList<IngestAlert>? alerts = null);

    /// <summary>
    /// Indexed existence check for reappear detection without loading all GONE rows.
    /// Key is TransportDemandKey (TASK_TYPE + SUBLOT).
    /// </summary>
    bool HasGoneTransportDemandKey(string taskType, string sublot);

    TransportDemand? GetById(string demandId);
    IReadOnlyList<TransportDemand> List(
        DemandStatus? status = null,
        string? taskType = null,
        string? sublot = null,
        string? demandId = null);
    DemandListPage QueryPage(DemandListQuery query);
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

    public ProjectionState GetState() =>
        new(
            _state.Demands.Where(d => d.Status == DemandStatus.Visible).ToList(),
            _state.TaskTypePauses);

    public void ReplaceState(ProjectionState state, IReadOnlyList<IngestAlert>? alerts = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var incomingIds = state.Demands.Select(d => d.DemandId).ToHashSet(StringComparer.Ordinal);
        var retainedGone = _state.Demands
            .Where(d => d.Status == DemandStatus.Gone && !incomingIds.Contains(d.DemandId))
            .ToList();
        _state = new ProjectionState(
            state.Demands.Concat(retainedGone).ToList(),
            state.TaskTypePauses);
        if (alerts is { Count: > 0 })
        {
            AppendAlerts(alerts);
        }
    }

    public bool HasGoneTransportDemandKey(string taskType, string sublot) =>
        _state.Demands.Any(d =>
            d.Status == DemandStatus.Gone
            && string.Equals(d.TaskType, taskType, StringComparison.Ordinal)
            && string.Equals(d.Sublot, sublot, StringComparison.Ordinal));

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

        return query
            .OrderByDescending(d => d.Dates)
            .ThenBy(d => d.DemandId, StringComparer.Ordinal)
            .ToList();
    }

    public DemandListPage QueryPage(DemandListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!DemandListCursor.TryDecode(query.Cursor, query.SortBy, query.Direction, out var cursor, out var error))
        {
            throw new ArgumentException(error ?? "cursor is invalid", nameof(query));
        }

        DemandListCursor.CursorPayload? cursorPayload =
            string.IsNullOrWhiteSpace(query.Cursor) ? null : cursor;
        return DemandListPaging.Page(_state.Demands, query, cursorPayload);
    }

    public void AppendAlerts(IReadOnlyList<IngestAlert> alerts)
    {
        var stamped = DateTimeOffset.UtcNow;
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
    private readonly ILatencyTelemetry _telemetry;
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
        Func<DateTimeOffset>? clock = null,
        ILatencyTelemetry? telemetry = null)
    {
        _source = source;
        _reconciler = reconciler;
        _store = store;
        _goLiveBaseline = goLiveBaseline;
        _disappearThreshold = disappearThreshold;
        _zeroDropEnterThreshold = zeroDropEnterThreshold;
        _zeroDropClearStreak = zeroDropClearStreak;
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _telemetry = telemetry ?? NullLatencyTelemetry.Instance;
    }

    public async Task<ProjectionState> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        LatencyCorrelation.Id = correlationId;
        var startedAt = _clock();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = await ReadSnapshotAsync(correlationId, cancellationToken);
        var restartRecovery = ResolveRestartRecovery();
        var result = _reconciler.Reconcile(
            _store.GetState(),
            snapshot,
            _clock(),
            _goLiveBaseline,
            _disappearThreshold,
            _zeroDropEnterThreshold,
            _zeroDropClearStreak,
            restartRecovery: restartRecovery,
            isGoneTransportDemandKey: _store.HasGoneTransportDemandKey);
        sw.Stop();
        var endedAt = _clock();

        if (snapshot.Kind == SnapshotOutcomeKind.Success)
        {
            _store.ReplaceState(result.State, result.Alerts);
        }
        else if (snapshot.Kind == SnapshotOutcomeKind.Failure)
        {
            var stage = snapshot.FailureStage ?? LatencyStages.OracleQuery;
            var oracleMs = snapshot.OracleDurationMs ?? sw.Elapsed.TotalMilliseconds;
            _store.AppendAlerts(
            [
                new IngestAlert(
                    Code: "POLL_FAILURE",
                    Message:
                    $"MES snapshot round failed; projection left unchanged. stage={stage} durationMs={(long)oracleMs} rowCount=0"),
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
            },
            FailureStage: snapshot.Kind == SnapshotOutcomeKind.Failure
                ? snapshot.FailureStage ?? LatencyStages.OracleQuery
                : null,
            OracleDurationMs: snapshot.OracleDurationMs));

        if (snapshot.Kind == SnapshotOutcomeKind.Success)
        {
            RecordSuccessfulRound(snapshot);
        }

        return result.State;
    }

    private async Task<MesSnapshotOutcome> ReadSnapshotAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_queryTimeout);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var outcome = await _source.ReadAsync(linked.Token);
            sw.Stop();
            _telemetry.Record(new LatencyEvent(
                CorrelationId: correlationId,
                Component: LatencyComponents.Oracle,
                Stage: LatencyStages.OracleQuery,
                ElapsedMs: sw.ElapsedMilliseconds,
                RowCount: outcome.Kind == SnapshotOutcomeKind.Success ? outcome.Rows.Count : 0));

            return outcome.Kind switch
            {
                SnapshotOutcomeKind.Success => MesSnapshotOutcome.Success(outcome.Rows, sw.Elapsed.TotalMilliseconds),
                SnapshotOutcomeKind.Incomplete => MesSnapshotOutcome.Incomplete(sw.Elapsed.TotalMilliseconds),
                _ => MesSnapshotOutcome.Failure(LatencyStages.OracleQuery, sw.Elapsed.TotalMilliseconds),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _telemetry.Record(new LatencyEvent(
                CorrelationId: correlationId,
                Component: LatencyComponents.Oracle,
                Stage: LatencyStages.OracleQuery,
                ElapsedMs: sw.ElapsedMilliseconds,
                RowCount: 0,
                Detail: "query timeout"));
            return MesSnapshotOutcome.Failure(LatencyStages.OracleQuery, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _telemetry.Record(new LatencyEvent(
                CorrelationId: correlationId,
                Component: LatencyComponents.Oracle,
                Stage: LatencyStages.OracleQuery,
                ElapsedMs: sw.ElapsedMilliseconds,
                RowCount: 0,
                Detail: LatencyLogFormatter.Sanitize(ex.Message)));
            return MesSnapshotOutcome.Failure(LatencyStages.OracleQuery, sw.Elapsed.TotalMilliseconds);
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
