using System.IO;
using System.Text;
using System.Text.Json;
using MesIngest.Core;
using IOPath = System.IO.Path;

namespace MesIngest.Watch;

/// <summary>
/// Makes WatchConnectionEvent records immediately readable from memory, then appends them
/// as daily JSON Lines on the bounded local-log worker. Retention uses age and total size.
/// </summary>
internal sealed class WatchConnectionEventJournal
{
    private const int RecentCapacity = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly long _maxSizeBytes;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<Exception>? _onWriteFailure;
    private readonly WatchLocalLogDispatcher _dispatcher;
    private readonly List<WatchConnectionEvent> _recent = [];
    private readonly object _recentGate = new();

    public WatchConnectionEventJournal(
        string directory,
        int retentionDays,
        long maxSizeBytes,
        Func<DateTimeOffset>? utcNow = null,
        Action<Exception>? onWriteFailure = null,
        WatchLocalLogDispatcher? dispatcher = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Directory is required.", nameof(directory));
        }

        if (retentionDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }

        if (maxSizeBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSizeBytes));
        }

        _directory = directory;
        _retentionDays = retentionDays;
        _maxSizeBytes = maxSizeBytes;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _onWriteFailure = onWriteFailure;
        _dispatcher = dispatcher ?? new WatchLocalLogDispatcher();
        _dispatcher.TryEnqueue(
            () => AddRecent(LoadRecentFromDisk(RecentCapacity)),
            _onWriteFailure);
    }

    public static string DefaultDirectory =>
        IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MesIngest.Watch",
            "logs");

    public static WatchConnectionEventJournal FromOptions(
        WatchOptions options,
        string? directory = null,
        Action<Exception>? onWriteFailure = null,
        WatchLocalLogDispatcher? dispatcher = null) =>
        new(
            directory ?? DefaultDirectory,
            options.ConnectionLogRetentionDays,
            options.ConnectionLogMaxSizeMb * 1024L * 1024L,
            onWriteFailure: onWriteFailure,
            dispatcher: dispatcher);

    public string DirectoryPath => _directory;

    public void Append(WatchConnectionEvent connectionEvent)
    {
        var sanitized = connectionEvent with
        {
            Message = Sanitize(connectionEvent.Message),
        };
        AddRecent([sanitized]);
        _dispatcher.TryEnqueue(() => AppendCore(sanitized), _onWriteFailure);
    }

    /// <summary>
    /// Reads the bounded in-memory view (newest first). Persisted history is loaded by the
    /// local-log worker so this method never performs filesystem IO on the UI thread.
    /// </summary>
    public IReadOnlyList<WatchConnectionEvent> ReadRecent(int maxCount = 200)
    {
        if (maxCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        }

        lock (_recentGate)
        {
            return _recent.Take(maxCount).ToList();
        }
    }

    internal Task DrainAsync() => _dispatcher.DrainAsync();

    private void AppendCore(WatchConnectionEvent connectionEvent)
    {
        Directory.CreateDirectory(_directory);
        EnforceRetention();

        var payload = new
        {
            kind = connectionEvent.Kind.ToString(),
            at = connectionEvent.At,
            endpoint = connectionEvent.Endpoint,
            stage = connectionEvent.Stage,
            elapsedMs = connectionEvent.ElapsedMs,
            timeoutSeconds = connectionEvent.TimeoutSeconds,
            message = connectionEvent.Message,
            failureCount = connectionEvent.FailureCount,
            outageDurationMs = connectionEvent.OutageDurationMs,
            correlationId = connectionEvent.CorrelationId,
        };

        var line = JsonSerializer.Serialize(payload, JsonOptions);
        var fileName = $"watch-connection-{_utcNow():yyyyMMdd}.jsonl";
        var path = IOPath.Combine(_directory, fileName);
        File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);

        EnforceRetention();
    }

    private IReadOnlyList<WatchConnectionEvent> LoadRecentFromDisk(int maxCount)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        string[] paths;
        try
        {
            paths = Directory.GetFiles(_directory, "watch-connection-*.jsonl");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WatchIoFailureReporter.TryReport(_onWriteFailure, ex);
            return [];
        }

        var files = new List<(FileInfo Info, DateTime LastWriteTimeUtc)>();
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    files.Add((info, info.LastWriteTimeUtc));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WatchIoFailureReporter.TryReport(_onWriteFailure, ex);
            }
        }

        var collected = new List<WatchConnectionEvent>();
        foreach (var file in files.OrderByDescending(item => item.LastWriteTimeUtc))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file.Info.FullName, Encoding.UTF8);
            }
            catch (IOException ex)
            {
                WatchIoFailureReporter.TryReport(_onWriteFailure, ex);
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                WatchIoFailureReporter.TryReport(_onWriteFailure, ex);
                continue;
            }

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (TryParse(line, out var parsed))
                {
                    collected.Add(parsed);
                    if (collected.Count >= maxCount)
                    {
                        return collected;
                    }
                }
            }
        }

        return collected;
    }

    private void AddRecent(IEnumerable<WatchConnectionEvent> events)
    {
        lock (_recentGate)
        {
            _recent.AddRange(events);
            _recent.Sort(static (left, right) => right.At.CompareTo(left.At));
            if (_recent.Count > RecentCapacity)
            {
                _recent.RemoveRange(RecentCapacity, _recent.Count - RecentCapacity);
            }
        }
    }

    private static bool TryParse(string line, out WatchConnectionEvent connectionEvent)
    {
        connectionEvent = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement)
                || !Enum.TryParse<WatchConnectionEventKind>(kindElement.GetString(), ignoreCase: true, out var kind)
                || !root.TryGetProperty("at", out var atElement)
                || !atElement.TryGetDateTimeOffset(out var at))
            {
                return false;
            }

            connectionEvent = new WatchConnectionEvent(
                Kind: kind,
                At: at,
                Endpoint: root.TryGetProperty("endpoint", out var endpoint) ? endpoint.GetString() : null,
                Stage: root.TryGetProperty("stage", out var stage) ? stage.GetString() : null,
                ElapsedMs: root.TryGetProperty("elapsedMs", out var elapsed) && elapsed.ValueKind == JsonValueKind.Number
                    ? elapsed.GetInt64()
                    : null,
                TimeoutSeconds: root.TryGetProperty("timeoutSeconds", out var timeout)
                    && timeout.ValueKind == JsonValueKind.Number
                    ? timeout.GetInt32()
                    : null,
                Message: root.TryGetProperty("message", out var message) ? message.GetString() : null,
                FailureCount: root.TryGetProperty("failureCount", out var count) && count.ValueKind == JsonValueKind.Number
                    ? count.GetInt32()
                    : 0,
                OutageDurationMs: root.TryGetProperty("outageDurationMs", out var outage)
                    && outage.ValueKind == JsonValueKind.Number
                    ? outage.GetInt64()
                    : null,
                CorrelationId: root.TryGetProperty("correlationId", out var correlation)
                    ? correlation.GetString()
                    : null);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void EnforceRetention() =>
        WatchLocalLogRetention.Enforce(
            _directory,
            _retentionDays,
            _maxSizeBytes,
            _utcNow,
            _onWriteFailure);

    internal static string? Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        return LatencyLogFormatter.Sanitize(message);
    }
}
