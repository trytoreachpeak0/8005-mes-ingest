using MesIngest.Core;
using MesIngest.Watch;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace MesIngest.Tests;

public class WatchLocalLogFailureIsolationTests
{
    [Fact]
    public async Task Size_retention_skips_a_locked_oldest_file_and_finishes_within_one_second()
    {
        using var dir = new TempDirectory();
        var lockedOldest = Path.Combine(dir.Path, "watch-latency-20260701.log");
        var deletableNext = Path.Combine(dir.Path, "watch-connection-20260702.jsonl");
        var newest = Path.Combine(dir.Path, "watch-latency-20260703.log");
        File.WriteAllBytes(lockedOldest, new byte[4_000]);
        File.WriteAllBytes(deletableNext, new byte[4_000]);
        File.WriteAllBytes(newest, new byte[500]);
        File.SetLastWriteTimeUtc(lockedOldest, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(deletableNext, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddDays(-1));

        using var lockHandle = new FileStream(
            lockedOldest,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var failures = new List<Exception>();

        var enforce = Task.Run(() => WatchLocalLogRetention.Enforce(
            dir.Path,
            retentionDays: 15,
            maxSizeBytes: 5_000,
            utcNow: () => DateTimeOffset.UtcNow,
            onFailure: failures.Add));

        await enforce.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(File.Exists(lockedOldest));
        Assert.False(File.Exists(deletableNext));
        Assert.True(File.Exists(newest));
        Assert.Contains(failures, failure => failure is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public async Task Unavailable_connection_journal_returns_without_throwing_and_reports_TELEMETRY_IO()
    {
        using var dir = new TempDirectory();
        Directory.Delete(dir.Path);
        File.WriteAllText(dir.Path, "not-a-directory");
        var diagnostics = new WatchTelemetryIoDiagnosticBuffer(capacity: 20);
        var journal = new WatchConnectionEventJournal(
            dir.Path,
            retentionDays: 15,
            maxSizeBytes: 1024,
            utcNow: () => DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
            onWriteFailure: ex => diagnostics.Record("watch-connection", ex));

        var append = Task.Run(() => journal.Append(new WatchConnectionEvent(
            WatchConnectionEventKind.Recovered,
            DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
            "/api/v2/demand-series",
            LatencyStages.HttpOk,
            1,
            30,
            "Host refresh succeeded",
            1,
            10)));

        await append.WaitAsync(TimeSpan.FromSeconds(1));
        await journal.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        var failure = Assert.Single(diagnostics.ReadRecent());
        Assert.Equal("TELEMETRY_IO", failure.Stage);
        Assert.Equal("watch-connection", failure.Endpoint);
    }

    [Fact]
    public void TELEMETRY_IO_buffer_is_bounded_and_keeps_newest_events()
    {
        var diagnostics = new WatchTelemetryIoDiagnosticBuffer(capacity: 2);
        diagnostics.Record("first", new IOException("one"));
        diagnostics.Record("second", new IOException("two"));
        diagnostics.Record("third", new IOException("three"));

        var recent = diagnostics.ReadRecent();

        Assert.Equal(2, recent.Count);
        Assert.Equal("third", recent[0].Endpoint);
        Assert.Equal("second", recent[1].Endpoint);
    }

    [Fact]
    public async Task Permission_and_metadata_failures_are_reported_and_do_not_stop_later_log_work()
    {
        using var dispatcher = new WatchLocalLogDispatcher();
        var diagnostics = new WatchTelemetryIoDiagnosticBuffer();
        var laterWorkRan = false;
        Action<Exception> report = ex => diagnostics.Record("watch-local-log", ex);

        dispatcher.TryEnqueue(
            () => throw new UnauthorizedAccessException("read-only directory"),
            report);
        dispatcher.TryEnqueue(
            () => throw new IOException("file metadata unavailable"),
            report);
        dispatcher.TryEnqueue(() => laterWorkRan = true, report);

        await dispatcher.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(laterWorkRan);
        Assert.Equal(2, diagnostics.ReadRecent().Count);
        Assert.All(
            diagnostics.ReadRecent(),
            diagnostic => Assert.Equal(WatchTelemetryIoDiagnosticBuffer.Stage, diagnostic.Stage));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MesIngestWatchLogFailureTests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
                else if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
