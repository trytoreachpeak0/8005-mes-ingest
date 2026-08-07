using System.Net;
using System.Net.Http;
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

internal readonly record struct FakeHostRequestMatch(
    string SessionId,
    FakeHostOperation Operation,
    FakeHostRequestState State);

internal sealed record FakeHostRequestEvent(
    long Sequence,
    FakeHostRequestMatch Request,
    string Endpoint)
{
    public string SessionId => Request.SessionId;
    public FakeHostOperation Operation => Request.Operation;
    public FakeHostRequestState State => Request.State;
}

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

internal sealed record FakeHostSnapshotRequest(
    WatchDemandBrowseQuery DemandQuery,
    WatchAlertBrowseQuery AlertQuery);

internal enum FakeHostFailureShape
{
    None,
    Query,
    CursorExpired,
}

internal sealed record FakeHostReply<T>(
    T Value,
    FakeHostFailureShape FailureShape = FakeHostFailureShape.None,
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
        new(
            default!,
            FakeHostFailureShape.Query,
            kind,
            endpoint,
            message);

    public static FakeHostReply<T> CursorExpired<T>(string endpoint) =>
        new(
            default!,
            FakeHostFailureShape.CursorExpired,
            Endpoint: endpoint,
            FailureMessage: "fake Host rejected an expired cursor");

    public static FakeHostReply<T> After<T>(
        FakeHostGate gate,
        T value,
        bool completeAfterCancellation = false) =>
        new(
            value,
            Gate: gate ?? throw new ArgumentNullException(nameof(gate)),
            CompleteAfterCancellation: completeAfterCancellation);

    public static FakeHostScript<TRequest, TResponse> Sequence<TRequest, TResponse>(
        params FakeHostReply<TResponse>[] replies) =>
        FakeHostScript<TRequest, TResponse>.Sequence(replies);

    public static FakeHostScript<TRequest, TResponse> Select<TRequest, TResponse>(
        Func<TRequest, FakeHostReply<TResponse>> selector) =>
        FakeHostScript<TRequest, TResponse>.Select(selector);
}

internal sealed class FakeHostScript<TRequest, TResponse>
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<FakeHostReply<TResponse>>? _sequence;
    private readonly Func<TRequest, FakeHostReply<TResponse>>? _selector;
    private int _nextIndex;

    private FakeHostScript(IReadOnlyList<FakeHostReply<TResponse>> sequence)
    {
        _sequence = sequence.Count > 0
            ? sequence
            : throw new ArgumentException("A fake Host response sequence cannot be empty.", nameof(sequence));
    }

    private FakeHostScript(Func<TRequest, FakeHostReply<TResponse>> selector)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
    }

    public static FakeHostScript<TRequest, TResponse> Sequence(
        params FakeHostReply<TResponse>[] replies) =>
        new(replies ?? throw new ArgumentNullException(nameof(replies)));

    public static FakeHostScript<TRequest, TResponse> Select(
        Func<TRequest, FakeHostReply<TResponse>> selector) =>
        new(selector);

    public FakeHostReply<TResponse> Next(TRequest request)
    {
        if (_selector is not null)
        {
            return _selector(request);
        }

        lock (_sync)
        {
            var index = Math.Min(_nextIndex, _sequence!.Count - 1);
            _nextIndex++;
            return _sequence[index];
        }
    }

    public static implicit operator FakeHostScript<TRequest, TResponse>(
        FakeHostReply<TResponse> reply) =>
        Sequence(reply);
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

    public FakeHostScript<FakeHostUnit, FakeHostUnit> Contract { get; init; } =
        FakeHostReply.Success();

    public FakeHostScript<FakeHostUnit, WatchPollHealthDto?> PollHealth { get; init; } =
        FakeHostReply.Return<WatchPollHealthDto?>(null);

    public FakeHostScript<FakeHostSnapshotRequest, WatchSnapshot> Snapshot { get; init; } =
        FakeHostReply.Return(new WatchSnapshot(
            [],
            [],
            PollHealth: null,
            FetchError: null,
            DemandsSucceeded: true,
            AlertsSucceeded: true,
            PollHealthSucceeded: true));

    public FakeHostScript<WatchDemandBrowseQuery, WatchDemandPage> DemandPage { get; init; } =
        FakeHostReply.Return(new WatchDemandPage([], null, false));

    public FakeHostScript<WatchAlertBrowseQuery, WatchAlertPage> AlertPage { get; init; } =
        FakeHostReply.Return(new WatchAlertPage([], null, false));

    public FakeHostScript<string, WatchDemandDto?> ExactDemand { get; init; } =
        FakeHostReply.Return<WatchDemandDto?>(null);
}

internal sealed class ScriptedFakeHost
{
    private readonly object _sync = new();
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
            lock (_sync)
            {
                return _timeline.ToArray();
            }
        }
    }

    public IWatchHostQueryAdapter CreateAdapter(WatchHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FakeHostScenario scenario;
        lock (_sync)
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
        var request = new FakeHostRequestMatch(sessionId, operation, state);
        lock (_sync)
        {
            if (_timeline.Any(entry => entry.Request == request))
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new TimelineWaiter(request, completion));
            return cancellationToken.CanBeCanceled
                ? completion.Task.WaitAsync(cancellationToken)
                : completion.Task;
        }
    }

    private void Record(
        FakeHostRequestMatch request,
        string endpoint)
    {
        List<TaskCompletionSource> completed = [];
        lock (_sync)
        {
            var entry = new FakeHostRequestEvent(
                ++_nextSequence,
                request,
                endpoint);
            _timeline.Add(entry);

            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                var waiter = _waiters[index];
                if (entry.Request != waiter.Request)
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
                    _scenario.Contract.Next(new FakeHostUnit()),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<WatchPollHealthDto?> FetchPollHealthAsync(CancellationToken cancellationToken) =>
            ExecuteAsync(
                FakeHostOperation.PollHealth,
                "/api/poll-health",
                _scenario.PollHealth.Next(new FakeHostUnit()),
                cancellationToken);

        public Task<WatchSnapshot> FetchSnapshotAsync(
            WatchDemandBrowseQuery demandQuery,
            WatchAlertBrowseQuery alertQuery,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                FakeHostOperation.Snapshot,
                "watch-snapshot",
                _scenario.Snapshot.Next(new FakeHostSnapshotRequest(demandQuery, alertQuery)),
                cancellationToken);

        public Task<WatchDemandPage> FetchDemandPageAsync(
            WatchDemandBrowseQuery query,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                FakeHostOperation.DemandPage,
                "/api/demands",
                _scenario.DemandPage.Next(query),
                cancellationToken);

        public Task<WatchAlertPage> FetchAlertPageAsync(
            WatchAlertBrowseQuery query,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                FakeHostOperation.AlertPage,
                "/api/alerts",
                _scenario.AlertPage.Next(query),
                cancellationToken);

        public Task<WatchDemandDto?> FetchDemandByIdAsync(
            string demandId,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                FakeHostOperation.ExactDemand,
                "/api/demands/{demand-id}",
                _scenario.ExactDemand.Next(demandId),
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
                new FakeHostRequestMatch(
                    _scenario.SessionId,
                    operation,
                    FakeHostRequestState.Started),
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

                if (reply.FailureShape == FakeHostFailureShape.CursorExpired)
                {
                    throw new WatchEndpointFetchException(
                        reply.Endpoint,
                        MesIngest.Core.LatencyStages.HttpStatus,
                        TimeSpan.Zero,
                        new HttpRequestException(
                            Redact(reply.FailureMessage),
                            inner: null,
                            HttpStatusCode.BadRequest));
                }

                if (reply is { FailureShape: FakeHostFailureShape.Query, FailureKind: { } failureKind })
                {
                    throw new WatchHostQueryException(
                        failureKind,
                        reply.Endpoint,
                        $"fake-{_scenario.SessionId}-{operation}",
                        Redact(reply.FailureMessage));
                }

                _owner.Record(
                    new FakeHostRequestMatch(
                        _scenario.SessionId,
                        operation,
                        cancellationToken.IsCancellationRequested
                            ? FakeHostRequestState.CompletedAfterCancellation
                            : FakeHostRequestState.Completed),
                    endpoint);
                return reply.Value;
            }
            catch (OperationCanceledException)
            {
                _owner.Record(
                    new FakeHostRequestMatch(
                        _scenario.SessionId,
                        operation,
                        FakeHostRequestState.Canceled),
                    endpoint);
                throw;
            }
            catch (Exception ex) when (ex is WatchHostQueryException or WatchEndpointFetchException)
            {
                _owner.Record(
                    new FakeHostRequestMatch(
                        _scenario.SessionId,
                        operation,
                        FakeHostRequestState.Failed),
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
        FakeHostRequestMatch Request,
        TaskCompletionSource Completion);
}
