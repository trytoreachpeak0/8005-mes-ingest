using System.IO;

namespace MesIngest.Watch;

/// <summary>
/// Shared age + size retention for Watch local logs (.jsonl connection events and .log latency).
/// Whichever limit is hit first applies: retention days or total directory size of retained extensions.
/// </summary>
internal static class WatchLocalLogRetention
{
    private static readonly string[] Patterns = ["*.jsonl", "*.log"];

    public static void Enforce(
        string directory,
        int retentionDays,
        long maxSizeBytes,
        Func<DateTimeOffset> utcNow,
        Action<Exception>? onFailure = null)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var cutoff = utcNow().UtcDateTime.AddDays(-retentionDays);
        foreach (var file in SnapshotFiles(directory, onFailure))
        {
            if (file.LastWriteTimeUtc < cutoff)
            {
                TryDelete(file.Path, onFailure);
            }
        }

        var files = SnapshotFiles(directory, onFailure)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        var total = files.Aggregate(
            0L,
            static (sum, file) => sum > long.MaxValue - file.Length
                ? long.MaxValue
                : sum + file.Length);

        // Each candidate is attempted at most once. A locked/undeletable oldest file
        // cannot cause an unbounded no-progress loop, and later candidates still get a turn.
        foreach (var file in files)
        {
            if (total <= maxSizeBytes)
            {
                break;
            }

            if (TryDelete(file.Path, onFailure))
            {
                total -= file.Length;
            }
        }
    }

    private static IReadOnlyList<LogFile> SnapshotFiles(
        string directory,
        Action<Exception>? onFailure)
    {
        var files = new List<LogFile>();
        foreach (var pattern in Patterns)
        {
            string[] paths;
            try
            {
                paths = Directory.GetFiles(directory, pattern);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WatchIoFailureReporter.TryReport(onFailure, ex);
                continue;
            }

            foreach (var path in paths)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        files.Add(new LogFile(path, info.LastWriteTimeUtc, info.Length));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    WatchIoFailureReporter.TryReport(onFailure, ex);
                }
            }
        }

        return files;
    }

    private static bool TryDelete(string path, Action<Exception>? onFailure)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WatchIoFailureReporter.TryReport(onFailure, ex);
            return false;
        }
    }

    private sealed record LogFile(string Path, DateTime LastWriteTimeUtc, long Length);
}
