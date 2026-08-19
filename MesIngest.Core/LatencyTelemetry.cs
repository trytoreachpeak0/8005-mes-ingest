using System.Text.RegularExpressions;

namespace MesIngest.Core;

public static class LatencyHeaders
{
    public const string CorrelationId = "X-Correlation-Id";
}

public static class LatencyComponents
{
    public const string Watch = "Watch";
    public const string Host = "Host";
    public const string SqlServer = "SqlServer";
    public const string Oracle = "Oracle";
}

public static class LatencyStages
{
    public const string WatchTimeout = "WATCH_TIMEOUT";
    public const string HostAbort = "HOST_ABORT";
    public const string SqlTimeout = "SQL_TIMEOUT";
    public const string OracleQuery = "ORACLE_QUERY";
    public const string HttpJson = "HTTP_JSON";
    public const string HttpConnect = "HTTP_CONNECT";
    public const string HttpStatus = "HTTP_STATUS";
    public const string HttpError = "HTTP_ERROR";
    public const string HttpOk = "HTTP_OK";

    public const string SqlOpen = "SQL_OPEN";
    public const string SqlQuery = "SQL_QUERY";
    public const string SqlWrite = "SQL_WRITE";
    public const string SqlTransaction = "SQL_TRANSACTION";
}

/// <summary>
/// Async-local correlation id for a Watch refresh or Host request spanning store ops.
/// </summary>
public static class LatencyCorrelation
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? Id
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    public static string Ensure()
    {
        if (string.IsNullOrWhiteSpace(Current.Value))
        {
            Current.Value = Guid.NewGuid().ToString("N");
        }

        return Current.Value!;
    }
}

public sealed record LatencyEvent(
    string CorrelationId,
    string Component,
    string Stage,
    long ElapsedMs,
    int? StatusCode = null,
    int? RowCount = null,
    long? Bytes = null,
    string? Endpoint = null,
    string? Detail = null);

public interface ILatencyTelemetry
{
    void Record(LatencyEvent evt);
}

public sealed class NullLatencyTelemetry : ILatencyTelemetry
{
    public static NullLatencyTelemetry Instance { get; } = new();

    public void Record(LatencyEvent evt)
    {
    }
}

public sealed class RecordingLatencyTelemetry : ILatencyTelemetry
{
    private readonly List<LatencyEvent> _events = new();

    public IReadOnlyList<LatencyEvent> Events => _events;

    public void Record(LatencyEvent evt) => _events.Add(evt);
}

public static class SqlFailureClassifier
{
    /// <summary>
    /// SqlClient timeout is Number -2; other SQL failures stay as SQL_QUERY for attribution.
    /// </summary>
    public static string Classify(Exception ex) =>
        ex is Microsoft.Data.SqlClient.SqlException sql
            ? ClassifyNumber(sql.Number)
            : LatencyStages.SqlQuery;

    public static string ClassifyNumber(int number) =>
        number == -2 ? LatencyStages.SqlTimeout : LatencyStages.SqlQuery;
}

/// <summary>
/// Field-comparable latency lines for A/B/C plant triage. Never emits secrets.
/// </summary>
public static class LatencyLogFormatter
{
    private static readonly Regex BearerToken = new(
        @"Bearer\s+(?:""[^""]*""|'[^']*'|\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SharedSecretAssignment = new(
        @"(SharedSecret\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^;\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PasswordAssignment = new(
        @"((?:Password|Pwd)\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^;\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExplicitConnectionString = new(
        @"((?:Connection\s*String|ConnectionString)\s*[:=]\s*)(?:""[^""]*""|'[^']*'|.+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ConnectionStringShape = new(
        @"((?:Data\s*Source|Server|Initial\s*Catalog|Database|User\s*ID|UID|DSN|Trusted_Connection|Integrated\s*Security)\s*=\s*)(?:""[^""]*""|'[^']*'|[^;\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Format(LatencyEvent evt)
    {
        var parts = new List<string>
        {
            $"correlationId={evt.CorrelationId}",
            $"component={evt.Component}",
            $"stage={evt.Stage}",
            $"elapsedMs={evt.ElapsedMs}",
        };

        if (evt.Endpoint is not null)
        {
            parts.Add($"endpoint={evt.Endpoint}");
        }

        if (evt.StatusCode is not null)
        {
            parts.Add($"statusCode={evt.StatusCode}");
        }

        if (evt.RowCount is not null)
        {
            parts.Add($"rowCount={evt.RowCount}");
        }

        if (evt.Bytes is not null)
        {
            parts.Add($"bytes={evt.Bytes}");
        }

        if (!string.IsNullOrEmpty(evt.Detail))
        {
            parts.Add($"detail={Sanitize(evt.Detail)}");
        }

        return string.Join(' ', parts);
    }

    public static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var cleaned = BearerToken.Replace(message, "Bearer [redacted]");
        cleaned = SharedSecretAssignment.Replace(cleaned, "$1[redacted]");
        cleaned = PasswordAssignment.Replace(cleaned, "$1[redacted]");
        cleaned = ExplicitConnectionString.Replace(cleaned, "$1[redacted]");
        cleaned = ConnectionStringShape.Replace(cleaned, "$1[redacted]");
        return cleaned;
    }
}
