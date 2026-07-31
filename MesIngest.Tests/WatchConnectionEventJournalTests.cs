using System.Text;
using System.Text.Json;
using MesIngest.Watch;

namespace MesIngest.Tests;

public class WatchConnectionEventJournalTests
{
    [Fact]
    public void Append_writes_jsonl_without_shared_secret_or_bearer()
    {
        using var dir = new TempDirectory();
        var journal = new WatchConnectionEventJournal(
            dir.Path,
            retentionDays: 30,
            maxSizeBytes: 100 * 1024 * 1024,
            utcNow: () => DateTimeOffset.Parse("2026-07-31T10:00:00Z"));

        journal.Append(new WatchConnectionEvent(
            Kind: WatchConnectionEventKind.Failure,
            At: DateTimeOffset.Parse("2026-07-31T10:00:00Z"),
            Endpoint: "/api/demands",
            Stage: "HTTP_TIMEOUT",
            ElapsedMs: 30_000,
            TimeoutSeconds: 30,
            Message: "Authorization: Bearer super-secret-token timed out; SharedSecret=leak",
            FailureCount: 1,
            OutageDurationMs: null));

        var files = Directory.GetFiles(dir.Path, "*.jsonl");
        Assert.Single(files);

        var line = File.ReadAllText(files[0], Encoding.UTF8).Trim();
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.Equal("Failure", root.GetProperty("kind").GetString());
        Assert.Equal("/api/demands", root.GetProperty("endpoint").GetString());
        Assert.DoesNotContain("super-secret-token", line, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedSecret=leak", line, StringComparison.Ordinal);
        Assert.Contains("Bearer [redacted]", root.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SharedSecret=[redacted]", root.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Retention_deletes_files_older_than_retention_days()
    {
        using var dir = new TempDirectory();
        var oldFile = Path.Combine(dir.Path, "watch-connection-20260101.jsonl");
        File.WriteAllText(oldFile, "{}\n");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-40));

        var journal = new WatchConnectionEventJournal(
            dir.Path,
            retentionDays: 30,
            maxSizeBytes: 100 * 1024 * 1024,
            utcNow: () => DateTimeOffset.UtcNow);

        journal.Append(new WatchConnectionEvent(
            WatchConnectionEventKind.Failure,
            DateTimeOffset.UtcNow,
            "/api/demands",
            "HTTP_CONNECT",
            100,
            30,
            "Connection refused",
            1,
            null));

        Assert.False(File.Exists(oldFile));
        Assert.NotEmpty(Directory.GetFiles(dir.Path, "*.jsonl"));
    }

    [Fact]
    public void Size_cap_deletes_oldest_files_first()
    {
        using var dir = new TempDirectory();
        var older = Path.Combine(dir.Path, "watch-connection-20260701.jsonl");
        var newer = Path.Combine(dir.Path, "watch-connection-20260715.jsonl");
        File.WriteAllBytes(older, new byte[4_000]);
        File.WriteAllBytes(newer, new byte[4_000]);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));

        var journal = new WatchConnectionEventJournal(
            dir.Path,
            retentionDays: 30,
            maxSizeBytes: 5_000,
            utcNow: () => DateTimeOffset.UtcNow);

        journal.Append(new WatchConnectionEvent(
            WatchConnectionEventKind.Failure,
            DateTimeOffset.UtcNow,
            "/api/alerts",
            "HTTP_CONNECT",
            50,
            30,
            "refused",
            1,
            null));

        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newer) || Directory.GetFiles(dir.Path, "*.jsonl").Length >= 1);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MesIngestWatchJournalTests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
