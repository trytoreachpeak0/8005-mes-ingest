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
        Func<DateTimeOffset> utcNow)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var cutoff = utcNow().UtcDateTime.AddDays(-retentionDays);
        foreach (var file in EnumerateFiles(directory))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                TryDelete(file);
            }
        }

        while (true)
        {
            var files = EnumerateFiles(directory)
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists)
                .OrderBy(info => info.LastWriteTimeUtc)
                .ToList();

            var total = files.Sum(info => info.Length);
            if (total <= maxSizeBytes || files.Count == 0)
            {
                break;
            }

            TryDelete(files[0].FullName);
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        foreach (var pattern in Patterns)
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                yield return file;
            }
        }
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
