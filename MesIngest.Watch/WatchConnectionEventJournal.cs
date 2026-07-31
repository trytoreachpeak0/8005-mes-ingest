using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IOPath = System.IO.Path;

namespace MesIngest.Watch;

/// <summary>
/// Appends WatchConnectionEvent records as daily JSON Lines under a local logs directory.
/// Retention is whichever limit is hit first: age (days) or total directory size.
/// </summary>
internal sealed class WatchConnectionEventJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Regex BearerToken = new(
        @"Bearer\s+\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SharedSecretAssignment = new(
        @"SharedSecret\s*=\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly long _maxSizeBytes;
    private readonly Func<DateTimeOffset> _utcNow;

    public WatchConnectionEventJournal(
        string directory,
        int retentionDays,
        long maxSizeBytes,
        Func<DateTimeOffset>? utcNow = null)
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
    }

    public static string DefaultDirectory =>
        IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MesIngest.Watch",
            "logs");

    public static WatchConnectionEventJournal FromOptions(WatchOptions options, string? directory = null) =>
        new(
            directory ?? DefaultDirectory,
            options.ConnectionLogRetentionDays,
            options.ConnectionLogMaxSizeMb * 1024L * 1024L);

    public string DirectoryPath => _directory;

    public void Append(WatchConnectionEvent connectionEvent)
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
            message = Sanitize(connectionEvent.Message),
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

    /// <summary>
    /// Reads newest JSONL connection events (newest first), best-effort across daily files.
    /// </summary>
    public IReadOnlyList<WatchConnectionEvent> ReadRecent(int maxCount = 200)
    {
        if (maxCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        }

        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var collected = new List<WatchConnectionEvent>();
        foreach (var file in Directory.EnumerateFiles(_directory, "watch-connection-*.jsonl")
                     .Select(path => new FileInfo(path))
                     .Where(info => info.Exists)
                     .OrderByDescending(info => info.LastWriteTimeUtc))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file.FullName, Encoding.UTF8);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
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

    private void EnforceRetention()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        var cutoff = _utcNow().UtcDateTime.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                TryDelete(file);
            }
        }

        while (true)
        {
            var files = Directory.EnumerateFiles(_directory, "*.jsonl")
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists)
                .OrderBy(info => info.LastWriteTimeUtc)
                .ToList();

            var total = files.Sum(info => info.Length);
            if (total <= _maxSizeBytes || files.Count == 0)
            {
                break;
            }

            TryDelete(files[0].FullName);
        }
    }

    internal static string? Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var cleaned = BearerToken.Replace(message, "Bearer [redacted]");
        cleaned = SharedSecretAssignment.Replace(cleaned, "SharedSecret=[redacted]");
        return cleaned;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best-effort
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort
        }
    }
}
