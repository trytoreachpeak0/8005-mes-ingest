using System.Net;
using MesIngest.Core;
using MesIngest.Core.SeriesProjection;

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
    ServerQuery,
    Canceled,
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

internal sealed class WatchHostQueryException : Exception
{
    public WatchHostQueryException(
        WatchHostFailureKind kind,
        string endpoint,
        string correlationId,
        string message,
        Exception? innerException = null,
        string? stage = null,
        TimeSpan? elapsed = null,
        string? errorCode = null,
        HttpStatusCode? statusCode = null)
        : base(message, innerException)
    {
        Kind = kind;
        Endpoint = endpoint;
        CorrelationId = correlationId;
        Stage = stage;
        Elapsed = elapsed ?? TimeSpan.Zero;
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public WatchHostFailureKind Kind { get; }
    public string Endpoint { get; }
    public string CorrelationId { get; }
    public string? Stage { get; }
    public TimeSpan Elapsed { get; }
    public string? ErrorCode { get; }
    public HttpStatusCode? StatusCode { get; }
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
                NewMesIngestContractMismatchException.ErrorCode,
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
        var errorCode = kind == WatchHostFailureKind.Contract
            ? NewMesIngestContractMismatchException.ErrorCode
            : null;
        var statusCode = exception.InnerException is HttpRequestException http
            ? http.StatusCode
            : null;
        return new WatchHostQueryException(
            kind,
            exception.Endpoint,
            correlationId,
            safeMessage,
            innerException: null,
            stage: exception.Stage,
            elapsed: exception.Elapsed,
            errorCode: errorCode,
            statusCode: statusCode);
    }
}

internal sealed class WatchEndpointFetchException : Exception
{
    public WatchEndpointFetchException(string endpoint, string stage, TimeSpan elapsed, Exception inner)
        : base(inner.Message, inner)
    {
        Endpoint = endpoint;
        Stage = stage;
        Elapsed = elapsed;
    }

    public WatchEndpointFetchException(string endpoint, string stage, TimeSpan elapsed, string message)
        : base(message)
    {
        Endpoint = endpoint;
        Stage = stage;
        Elapsed = elapsed;
    }

    public string Endpoint { get; }
    public string Stage { get; }
    public TimeSpan Elapsed { get; }

    public string FormatForBanner(int timeoutSeconds, string correlationId) =>
        $"endpoint={Endpoint} stage={Stage} timeoutSeconds={timeoutSeconds} elapsedMs={(long)Elapsed.TotalMilliseconds} correlationId={correlationId} {Message}";
}
