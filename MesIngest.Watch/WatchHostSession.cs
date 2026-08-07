using System.Net;
using MesIngest.Core;

namespace MesIngest.Watch;

internal enum WatchHostConnectionStatus
{
    NotConfigured,
    Connecting,
    Connected,
    Failed,
}

internal enum WatchHostFailureKind
{
    None,
    Authentication,
    Network,
    Timeout,
    Contract,
    Decode,
    Http,
    Unknown,
}

internal sealed record WatchHostSettings
{
    public WatchHostSettings(string baseUrl, string credential, int requestTimeoutSeconds)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Host base address must be an absolute HTTP or HTTPS URL.", nameof(baseUrl));
        }

        if (requestTimeoutSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeoutSeconds),
                requestTimeoutSeconds,
                "Request timeout must be between 1 and 300 seconds.");
        }

        BaseUrl = baseUrl.TrimEnd('/');
        Credential = credential ?? string.Empty;
        RequestTimeoutSeconds = requestTimeoutSeconds;
    }

    public string BaseUrl { get; }
    public string Credential { get; }
    public int RequestTimeoutSeconds { get; }

    public override string ToString() =>
        $"BaseUrl={BaseUrl}, Credential=(masked), RequestTimeoutSeconds={RequestTimeoutSeconds}";
}

internal sealed record WatchHostSessionState(
    long Generation,
    string? BaseUrl,
    WatchHostConnectionStatus Status,
    WatchPollHealthDto? PollHealth,
    DateTimeOffset? LastSuccessfulAt,
    WatchHostFailureKind FailureKind,
    string? ErrorMessage,
    string? Endpoint,
    string? CorrelationId)
{
    public static WatchHostSessionState Empty { get; } = new(
        0,
        null,
        WatchHostConnectionStatus.NotConfigured,
        null,
        null,
        WatchHostFailureKind.None,
        null,
        null,
        null);
}

internal sealed class WatchHostQueryException : Exception
{
    public WatchHostQueryException(
        WatchHostFailureKind kind,
        string endpoint,
        string correlationId,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Endpoint = endpoint;
        CorrelationId = correlationId;
    }

    public WatchHostFailureKind Kind { get; }
    public string Endpoint { get; }
    public string CorrelationId { get; }
}

internal interface IWatchReadQueries
{
    Task<WatchSnapshot> FetchSnapshotAsync(
        WatchDemandBrowseQuery demandQuery,
        WatchAlertBrowseQuery alertQuery,
        CancellationToken cancellationToken = default);

    Task<WatchDemandPage> FetchDemandPageAsync(
        WatchDemandBrowseQuery query,
        CancellationToken cancellationToken = default);

    Task<WatchAlertPage> FetchAlertPageAsync(
        WatchAlertBrowseQuery query,
        CancellationToken cancellationToken = default);

    Task<WatchDemandDto?> FetchDemandByIdAsync(
        string demandId,
        CancellationToken cancellationToken = default);
}

internal interface IWatchHostQueryAdapter : IWatchReadQueries, IDisposable
{
    Task VerifyContractAsync(CancellationToken cancellationToken);
    Task<WatchPollHealthDto?> FetchPollHealthAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Owns the single active Host generation. Applying settings synchronously invalidates
/// the old generation and clears all Host-derived state before any new I/O can finish.
/// </summary>
internal sealed class WatchHostSession : IWatchReadQueries, IDisposable
{
    private readonly object _gate = new();
    private readonly Func<WatchHostSettings, IWatchHostQueryAdapter> _adapterFactory;
    private CancellationTokenSource? _activeCancellation;
    private IWatchHostQueryAdapter? _activeAdapter;
    private long _generation;
    private bool _disposed;

    public WatchHostSession(Func<WatchHostSettings, IWatchHostQueryAdapter> adapterFactory)
    {
        _adapterFactory = adapterFactory ?? throw new ArgumentNullException(nameof(adapterFactory));
    }

    public WatchHostSessionState State { get; private set; } = WatchHostSessionState.Empty;

    public async Task ApplyAsync(WatchHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        CancellationTokenSource cancellation;
        IWatchHostQueryAdapter adapter;
        CancellationTokenSource? previousCancellation;
        IWatchHostQueryAdapter? previousAdapter;
        long generation;
        WatchHostSessionState connecting;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = ++_generation;
            cancellation = new CancellationTokenSource();
            adapter = _adapterFactory(settings);
            previousCancellation = _activeCancellation;
            previousAdapter = _activeAdapter;
            _activeCancellation = cancellation;
            _activeAdapter = adapter;
            connecting = new WatchHostSessionState(
                generation,
                settings.BaseUrl,
                WatchHostConnectionStatus.Connecting,
                PollHealth: null,
                LastSuccessfulAt: null,
                WatchHostFailureKind.None,
                ErrorMessage: null,
                Endpoint: null,
                CorrelationId: null);
            State = connecting;
        }

        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        previousAdapter?.Dispose();
        try
        {
            await adapter.VerifyContractAsync(cancellation.Token).ConfigureAwait(false);
            var health = await adapter.FetchPollHealthAsync(cancellation.Token).ConfigureAwait(false);
            CommitIfCurrent(
                generation,
                connecting with
                {
                    Status = WatchHostConnectionStatus.Connected,
                    PollHealth = health,
                    LastSuccessfulAt = DateTimeOffset.UtcNow,
                });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Replaced Host generations and explicit cancellation never mutate state.
        }
        catch (WatchHostQueryException ex)
        {
            CommitIfCurrent(
                generation,
                connecting with
                {
                    Status = WatchHostConnectionStatus.Failed,
                    FailureKind = ex.Kind,
                    ErrorMessage = ex.Message,
                    Endpoint = ex.Endpoint,
                    CorrelationId = ex.CorrelationId,
                });
        }
        catch (Exception ex)
        {
            CommitIfCurrent(
                generation,
                connecting with
                {
                    Status = WatchHostConnectionStatus.Failed,
                    FailureKind = WatchHostFailureKind.Unknown,
                    ErrorMessage = ex.Message,
                });
        }
    }

    public Task<WatchSnapshot> FetchSnapshotAsync(
        WatchDemandBrowseQuery demandQuery,
        WatchAlertBrowseQuery alertQuery,
        CancellationToken cancellationToken = default) =>
        ExecuteCurrentAsync(
            (adapter, token) => adapter.FetchSnapshotAsync(demandQuery, alertQuery, token),
            cancellationToken);

    public Task<WatchDemandPage> FetchDemandPageAsync(
        WatchDemandBrowseQuery query,
        CancellationToken cancellationToken = default) =>
        ExecuteCurrentAsync(
            (adapter, token) => adapter.FetchDemandPageAsync(query, token),
            cancellationToken);

    public Task<WatchAlertPage> FetchAlertPageAsync(
        WatchAlertBrowseQuery query,
        CancellationToken cancellationToken = default) =>
        ExecuteCurrentAsync(
            (adapter, token) => adapter.FetchAlertPageAsync(query, token),
            cancellationToken);

    public Task<WatchDemandDto?> FetchDemandByIdAsync(
        string demandId,
        CancellationToken cancellationToken = default) =>
        ExecuteCurrentAsync(
            (adapter, token) => adapter.FetchDemandByIdAsync(demandId, token),
            cancellationToken);

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        IWatchHostQueryAdapter? adapter;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            cancellation = _activeCancellation;
            adapter = _activeAdapter;
            _activeCancellation = null;
            _activeAdapter = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        adapter?.Dispose();
    }

    private void CommitIfCurrent(long generation, WatchHostSessionState next)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            State = next;
        }

    }

    private async Task<T> ExecuteCurrentAsync<T>(
        Func<IWatchHostQueryAdapter, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        IWatchHostQueryAdapter adapter;
        CancellationToken sessionToken;
        long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            adapter = _activeAdapter
                ?? throw new InvalidOperationException("No Host session has been applied.");
            sessionToken = _activeCancellation!.Token;
            generation = _generation;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            sessionToken);
        var result = await operation(adapter, linked.Token).ConfigureAwait(false);

        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                throw new OperationCanceledException("The Host session was replaced.", linked.Token);
            }
        }

        return result;
    }
}

internal static class WatchHostQueryFailure
{
    public static WatchHostQueryException From(
        WatchEndpointFetchException exception,
        string correlationId,
        Func<string, string>? redact = null)
    {
        var kind = exception switch
        {
            { Message: var message } when message.Contains(
                MesIngestApiContract.MismatchErrorCode,
                StringComparison.Ordinal) => WatchHostFailureKind.Contract,
            { InnerException: HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } }
                => WatchHostFailureKind.Authentication,
            { Stage: LatencyStages.WatchTimeout } => WatchHostFailureKind.Timeout,
            { Stage: LatencyStages.HttpConnect } => WatchHostFailureKind.Network,
            { Stage: LatencyStages.HttpJson } => WatchHostFailureKind.Decode,
            { Stage: LatencyStages.HttpStatus } => WatchHostFailureKind.Http,
            _ => WatchHostFailureKind.Unknown,
        };

        var safeMessage = redact?.Invoke(exception.Message) ?? exception.Message;
        return new WatchHostQueryException(
            kind,
            exception.Endpoint,
            correlationId,
            safeMessage);
    }
}
