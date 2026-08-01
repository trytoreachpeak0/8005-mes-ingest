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
    bool HasGoneTransportDemandKey(TransportDemandKey key);

    /// <summary>
    /// Latest GONE DemandId for a TransportDemandKey, without loading the full GONE history.
    /// Used to populate REAPPEAR_AFTER_GONE alert details when hot GetState() excludes GONE.
    /// </summary>
    string? GetLatestGoneDemandId(TransportDemandKey key);

    TransportDemand? GetById(string demandId);
    IReadOnlyList<TransportDemand> List(
        DemandStatus? status = null,
        string? taskType = null,
        string? sublot = null,
        string? demandId = null);
    DemandListPage QueryPage(DemandListQuery query);
    DemandChangeFeedPage QueryChangeFeed(DemandChangeFeedQuery query);
    void AppendAlerts(IReadOnlyList<IngestAlert> alerts);
    IReadOnlyList<IngestAlert> ListAlerts(int? limit = null);
    AlertListPage QueryAlerts(AlertListQuery query);
    void SetLatestPollHealth(PollHealth health);
    PollHealth? GetLatestPollHealth();
}

public sealed class InMemoryTransportDemandStore : ITransportDemandStore
{
    public const int DefaultAlertLimit = 100;

    private readonly object _gate = new();
    private ProjectionState _state = ProjectionState.Empty;
    private readonly List<IngestAlert> _alerts = new();
    private readonly List<DemandChangeFeedEntry> _changeFeed = new();
    private readonly TimeSpan _changeFeedRetention;
    private readonly TimeSpan _alertRetention;
    private readonly Func<DateTimeOffset> _clock;
    private long _nextSequence = 1;
    private PollHealth? _latestPollHealth;

    public InMemoryTransportDemandStore(
        TimeSpan? changeFeedRetention = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? alertRetention = null)
    {
        _changeFeedRetention = changeFeedRetention ?? DemandChangeFeedQuery.DefaultRetention;
        _alertRetention = alertRetention ?? AlertIncidentSync.DefaultResolvedRetention;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ProjectionState GetState()
    {
        lock (_gate)
        {
            return new(
                _state.Demands.Where(d => d.Status == DemandStatus.Visible).ToList(),
                _state.TaskTypePauses);
        }
    }

    public void ReplaceState(ProjectionState state, IReadOnlyList<IngestAlert>? alerts = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            var priorById = _state.Demands.ToDictionary(d => d.DemandId, StringComparer.Ordinal);
            var now = _clock();
            var incomingIds = state.Demands.Select(d => d.DemandId).ToHashSet(StringComparer.Ordinal);
            var retainedGone = _state.Demands
                .Where(d => d.Status == DemandStatus.Gone && !incomingIds.Contains(d.DemandId))
                .ToList();

            foreach (var demand in state.Demands)
            {
                if (!priorById.TryGetValue(demand.DemandId, out var prior))
                {
                    AppendChange(DemandChangeType.Created, demand, now);
                    continue;
                }

                if (prior.Status != DemandStatus.Gone
                    && demand.Status == DemandStatus.Gone)
                {
                    AppendChange(DemandChangeType.Gone, demand, now);
                }
            }

            _state = new ProjectionState(
                state.Demands.Concat(retainedGone).ToList(),
                state.TaskTypePauses);
            PurgeChangeFeed(now);
            ApplyAlertObservations(alerts ?? Array.Empty<IngestAlert>(), IngestAlertCatalog.SuccessRoundManagedCodes, now);
        }
    }

    public bool HasGoneTransportDemandKey(TransportDemandKey key) =>
        GetLatestGoneDemandId(key) is not null;

    public string? GetLatestGoneDemandId(TransportDemandKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return _state.Demands
                .Where(d =>
                    d.Status == DemandStatus.Gone
                    && d.Key == key)
                .OrderByDescending(d => d.GoneAt ?? d.CreatedAt)
                .Select(d => d.DemandId)
                .FirstOrDefault();
        }
    }

    public TransportDemand? GetById(string demandId)
    {
        lock (_gate)
        {
            return _state.Demands.FirstOrDefault(d => d.DemandId == demandId);
        }
    }

    public IReadOnlyList<TransportDemand> List(
        DemandStatus? status = null,
        string? taskType = null,
        string? sublot = null,
        string? demandId = null)
    {
        lock (_gate)
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
        lock (_gate)
        {
            return DemandListPaging.Page(_state.Demands, query, cursorPayload);
        }
    }

    public DemandChangeFeedPage QueryChangeFeed(DemandChangeFeedQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            PurgeChangeFeed(query.AsOf);

            // Monotonic even when retention has emptied the ledger (_nextSequence - 1).
            long highWatermark = _nextSequence - 1;
            long? earliest = _changeFeed.Count == 0 ? null : _changeFeed[0].Sequence;
            long contiguousFrom = earliest ?? highWatermark + 1;
            if (query.AfterSequence < contiguousFrom - 1)
            {
                throw new SyncCursorExpiredException(query.AfterSequence, earliest, highWatermark);
            }

            var matched = _changeFeed
                .Where(e => e.Sequence > query.AfterSequence)
                .Take(query.Limit + 1)
                .ToList();
            var hasMore = matched.Count > query.Limit;
            if (hasMore)
            {
                matched.RemoveAt(matched.Count - 1);
            }

            return new DemandChangeFeedPage(
                matched,
                hasMore ? matched[^1].Sequence : null,
                hasMore,
                highWatermark,
                earliest);
        }
    }

    private void AppendChange(DemandChangeType changeType, TransportDemand demand, DateTimeOffset changedAt)
    {
        var sequence = _nextSequence++;
        _changeFeed.Add(new DemandChangeFeedEntry(
            sequence,
            demand.DemandId,
            changeType,
            changedAt,
            DemandChangePayload.From(demand)));
    }

    private void PurgeChangeFeed(DateTimeOffset asOf)
    {
        if (_changeFeedRetention <= TimeSpan.Zero)
        {
            return;
        }

        var cutoff = asOf - _changeFeedRetention;
        _changeFeed.RemoveAll(e => e.ChangedAt < cutoff);
    }

    public void AppendAlerts(IReadOnlyList<IngestAlert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        lock (_gate)
        {
            ApplyAlertObservations(alerts, IngestAlertCatalog.PollCodes, _clock());
        }
    }

    public IReadOnlyList<IngestAlert> ListAlerts(int? limit = null)
    {
        var take = Math.Max(1, limit ?? DefaultAlertLimit);
        return QueryAlerts(new AlertListQuery
        {
            Limit = take,
            UseDefaultPrioritySort = true,
        }).Items;
    }

    public AlertListPage QueryAlerts(AlertListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!AlertListCursor.TryDecode(query.Cursor, query.SortBy, query.Direction, out var cursor, out var error))
        {
            throw new ArgumentException(error ?? "cursor is invalid", nameof(query));
        }

        lock (_gate)
        {
            // Snapshot so paging cannot observe a mid-mutation alert list.
            return AlertListPaging.Page(_alerts.ToList(), query, cursor);
        }
    }

    private void ApplyAlertObservations(
        IReadOnlyList<IngestAlert> observations,
        IReadOnlySet<string> managedCodes,
        DateTimeOffset asOf)
    {
        var next = AlertIncidentSync.Apply(
            _alerts,
            observations,
            asOf,
            managedCodes,
            _alertRetention);
        _alerts.Clear();
        _alerts.AddRange(next);
    }

    public void SetLatestPollHealth(PollHealth health)
    {
        lock (_gate)
        {
            _latestPollHealth = health;
        }
    }

    public PollHealth? GetLatestPollHealth()
    {
        lock (_gate)
        {
            return _latestPollHealth;
        }
    }
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
            getLatestGoneDemandId: _store.GetLatestGoneDemandId);
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
            var timeoutSeconds = (int)Math.Max(1, _queryTimeout.TotalSeconds);
            var reason = $"MES snapshot round failed; stage={stage}";
            _store.AppendAlerts(
            [
                new IngestAlert(
                    Code: AlertCodes.PollFailure,
                    Message:
                    $"MES snapshot round failed; projection left unchanged. stage={stage} durationMs={(long)oracleMs} rowCount=0",
                    Details: AlertDetailsBuilder.Poll(
                        stage,
                        oracleMs,
                        rowCount: 0,
                        reason: reason,
                        timeoutSeconds: timeoutSeconds),
                    DetailsFingerprint: AlertDetailsBuilder.PollFingerprint(
                        stage,
                        reason,
                        timeoutSeconds)),
            ]);
        }
        else if (snapshot.Kind == SnapshotOutcomeKind.Incomplete)
        {
            var timeoutSeconds = (int)Math.Max(1, _queryTimeout.TotalSeconds);
            const string reason = "MES snapshot round incomplete; projection left unchanged.";
            _store.AppendAlerts(
            [
                new IngestAlert(
                    Code: AlertCodes.PollIncomplete,
                    Message: reason,
                    Details: AlertDetailsBuilder.Poll(
                        failureStage: null,
                        durationMs: snapshot.OracleDurationMs ?? sw.Elapsed.TotalMilliseconds,
                        rowCount: 0,
                        reason: reason,
                        timeoutSeconds: timeoutSeconds),
                    DetailsFingerprint: AlertDetailsBuilder.PollFingerprint(
                        failureStage: null,
                        reason: reason,
                        timeoutSeconds: timeoutSeconds)),
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
            _barrierRoundCountsByType = TransportDemandReconciler.CountProjectedRowsByType(
                snapshot.Rows,
                _goLiveBaseline);
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
