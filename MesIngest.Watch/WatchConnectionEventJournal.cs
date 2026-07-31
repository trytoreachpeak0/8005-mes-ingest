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
        };

        var line = JsonSerializer.Serialize(payload, JsonOptions);
        var fileName = $"watch-connection-{_utcNow():yyyyMMdd}.jsonl";
        var path = IOPath.Combine(_directory, fileName);
        File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);

        EnforceRetention();
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
