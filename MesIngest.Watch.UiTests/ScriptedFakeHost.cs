using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

internal enum FakeHostOperation
{
    Contract,
    PollHealth,
    Snapshot,
    DemandPage,
    AlertPage,
    ExactDemand,
}

internal enum FakeHostRequestState
{
    Started,
    Completed,
    CompletedAfterCancellation,
    Canceled,
    Failed,
}

internal sealed record FakeHostRequestEvent(
    long Sequence,
    string SessionId,
    FakeHostOperation Operation,
    FakeHostRequestState State,
    string Endpoint);

internal sealed class FakeHostGate
{
    private readonly TaskCompletionSource _released = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _released.TrySetResult();

    internal Task WaitAsync(CancellationToken cancellationToken, bool completeAfterCancellation) =>
        completeAfterCancellation
            ? _released.Task
            : _released.Task.WaitAsync(cancellationToken);
}

internal readonly record struct FakeHostUnit;

internal sealed record FakeHostReply<T>(
    T Value,
    WatchHostFailureKind? FailureKind = null,
    string Endpoint = "fake-host",
    string FailureMessage = "fake Host failure",
    FakeHostGate? Gate = null,
    bool CompleteAfterCancellation = false);

internal static class FakeHostReply
{
    public static FakeHostReply<FakeHostUnit> Success() =>
        Return(new FakeHostUnit());

    public static FakeHostReply<T> Return<T>(T value) => new(value);

    public static FakeHostReply<T> Fail<T>(
        WatchHostFailureKind kind,
        string endpoint,
        string message = "fake Host failure") =>
        new(default!, kind, endpoint, message);

    public static FakeHostReply<T> CursorExpired<T>(string endpoint) =>
        Fail<T>(
            WatchHostFailureKind.Http,
            endpoint,
            "fake Host rejected an expired cursor");

    public static FakeHostReply<T> After<T>(
        FakeHostGate gate,
        T value,
        bool completeAfterCancellation = false) =>
        new(
            value,
            Gate: gate ?? throw new ArgumentNullException(nameof(gate)),
            CompleteAfterCancellation: completeAfterCancellation);
}

internal sealed class FakeHostScenario
{
    public FakeHostScenario(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A fake Host session id is required.", nameof(sessionId));
        }

        SessionId = sessionId;
    }

    public string SessionId { get; }

    public FakeHostReply<FakeHostUnit> Contract { get; init; } = FakeHostReply.Success();

    public FakeHostReply<WatchPollHealthDto?> PollHealth { get; init; } =
        FakeHostReply.Return<WatchPollHealthDto?>(null);

    public FakeHostReply<WatchSnapshot> Snapshot { get; init; } =
        FakeHostReply.Return(new WatchSnapshot(
            [],
            [],
            PollHealth: null,
            FetchError: null,
            DemandsSucceeded: true,
            AlertsSucceeded: true,
            PollHealthSucceeded: true));

    public FakeHostReply<WatchDemandPage> DemandPage { get; init; } =
        FakeHostReply.Return(new WatchDemandPage([], null, false));

    public FakeHostReply<WatchAlertPage> AlertPage { get; init; } =
        FakeHostReply.Return(new WatchAlertPage([], null, false));

    public FakeHostReply<WatchDemandDto?> ExactDemand { get; init; } =
        FakeHostReply.Return<WatchDemandDto?>(null);
}

internal sealed class ScriptedFakeHost
{
    private readonly object _gate = new();
    private readonly Queue<FakeHostScenario> _scenarios;
    private readonly List<FakeHostRequestEvent> _timeline = [];
    private readonly List<TimelineWaiter> _waiters = [];
    private long _nextSequence;

    public ScriptedFakeHost(params FakeHostScenario[] scenarios)
    {
        if (scenarios is null || scenarios.Length == 0)
        {
            throw new ArgumentException("At least one fake Host scenario is required.", nameof(scenarios));
        }

        _scenarios = new Queue<FakeHostScenario>(scenarios);
    }

    public IReadOnlyList<FakeHostRequestEvent> Timeline
    {
        get
        {
            lock (_gate)
            {
                return _timeline.ToArray();
            }
        }
    }

    public IWatchHostQueryAdapter CreateAdapter(WatchHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FakeHostScenario scenario;
        lock (_gate)
        {
            scenario = _scenarios.Count > 0
                ? _scenarios.Dequeue()
                : throw new InvalidOperationException("No fake Host scenario remains for a new session.");
        }

        return new Adapter(this, scenario, settings.Credential);
    }

    public Task WaitForAsync(
        string sessionId,
        FakeHostOperation operation,
        FakeHostRequestState state,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_timeline.Any(entry => Matches(entry, sessionId, operation, state)))
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new TimelineWaiter(sessionId, operation, state, completion));
            return cancellationToken.CanBeCanceled
                ? completion.Task.WaitAsync(cancellationToken)
                : completion.Task;
        }
    }

    private void Record(
        string sessionId,
        FakeHostOperation operation,
        FakeHostRequestState state,
        string endpoint)
    {
        List<TaskCompletionSource> completed = [];
        lock (_gate)
        {
            var entry = new FakeHostRequestEvent(
                ++_nextSequence,
                sessionId,
                operation,
                state,
                endpoint);
            _timeline.Add(entry);

            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                var waiter = _waiters[index];
                if (!Matches(entry, waiter.SessionId, waiter.Operation, waiter.State))
                {
                    continue;
                }

                completed.Add(waiter.Completion);
                _waiters.RemoveAt(index);
            }
        }

        foreach (var completion in completed)
        {
            completion.TrySetResult();
        }
    }

    private static bool Matches(
        FakeHostRequestEvent entry,
        string sessionId,
        FakeHostOperation operation,
        FakeHostRequestState state) =>
        string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal)
        && entry.Operation == operation
        && entry.State == state;

    private sealed class Adapter : IWatchHostQueryAdapter
    {
        private readonly ScriptedFakeHost _owner;
        private readonly FakeHostScenario _scenario;
        private readonly string _credential;

        public Adapter(
            ScriptedFakeHost owner,
            FakeHostScenario scenario,
            string credential)
        {
            _owner = owner;
            _scenario = scenario;
            _credential = credential;
        }

        public async Task VerifyContractAsync(CancellationToken cancellationToken)
        {
            _ = await ExecuteAsync(
                    FakeHostOperation.Contract,
                    "/api/contract",
                    _scenario.Contract,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<WatchPollHealthDto?> FetchPollHealthAsync(CancellationToken cancellationToken) =>
            ExecuteAsync(
                FakeHostOperation.PollHealth,
                "/api/poll-health",
                _scenario.PollHealth,
                cancellationToken);

        public Task<WatchSnapshot> FetchSnapshotAsync(
            WatchDemandBrowseQuery demandQuery,
            WatchAlertBrowseQuery alertQuery,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                FakeHostOperation.Snapshot,
                "watch-snapshot",
                _scenario.Snapshot,
                cancellationToken);

        public Task<WatchDemandPage> FetchDemandPageAsync(
            WatchDemandBrowseQuery query,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                FakeHostOperation.DemandPage,
                "/api/demands",
                _scenario.DemandPage,
                cancellationToken);

        public Task<WatchAlertPage> FetchAlertPageAsync(
            WatchAlertBrowseQuery query,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                FakeHostOperation.AlertPage,
                "/api/alerts",
                _scenario.AlertPage,
                cancellationToken);

        public Task<WatchDemandDto?> FetchDemandByIdAsync(
            string demandId,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                FakeHostOperation.ExactDemand,
                "/api/demands/{demand-id}",
                _scenario.ExactDemand,
                cancellationToken);

        public void Dispose()
        {
        }

        private async Task<T> ExecuteAsync<T>(
            FakeHostOperation operation,
            string endpoint,
            FakeHostReply<T> reply,
            CancellationToken cancellationToken)
        {
            _owner.Record(
                _scenario.SessionId,
                operation,
                FakeHostRequestState.Started,
                endpoint);
            try
            {
                if (reply.Gate is not null)
                {
                    await reply.Gate.WaitAsync(
                            cancellationToken,
                            reply.CompleteAfterCancellation)
                        .ConfigureAwait(false);
                }

                if (reply.FailureKind is { } failureKind)
                {
                    throw new WatchHostQueryException(
                        failureKind,
                        reply.Endpoint,
                        $"fake-{_scenario.SessionId}-{operation}",
                        Redact(reply.FailureMessage));
                }

                _owner.Record(
                    _scenario.SessionId,
                    operation,
                    cancellationToken.IsCancellationRequested
                        ? FakeHostRequestState.CompletedAfterCancellation
                        : FakeHostRequestState.Completed,
                    endpoint);
                return reply.Value;
            }
            catch (OperationCanceledException)
            {
                _owner.Record(
                    _scenario.SessionId,
                    operation,
                    FakeHostRequestState.Canceled,
                    endpoint);
                throw;
            }
            catch (WatchHostQueryException)
            {
                _owner.Record(
                    _scenario.SessionId,
                    operation,
                    FakeHostRequestState.Failed,
                    endpoint);
                throw;
            }
        }

        private string Redact(string message) =>
            string.IsNullOrEmpty(_credential)
                ? message
                : message.Replace(_credential, "(masked)", StringComparison.Ordinal);
    }

    private sealed record TimelineWaiter(
        string SessionId,
        FakeHostOperation Operation,
        FakeHostRequestState State,
        TaskCompletionSource Completion);
}
