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
            retentionDays: 30,
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
    public async Task Unavailable_primary_directory_is_visible_as_sanitized_TELEMETRY_IO_in_event_feed()
    {
        using var dir = new TempDirectory();
        Directory.Delete(dir.Path);
        File.WriteAllText(dir.Path, "not-a-directory");
        var at = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var diagnostics = new WatchTelemetryIoDiagnosticBuffer(capacity: 20);
        var telemetry = new WatchLatencyFileTelemetry(
            dir.Path,
            retentionDays: 30,
            maxSizeBytes: 1024,
            utcNow: () => at,
            onWriteFailure: _ => diagnostics.Record(
                endpoint: "watch-latency",
                new IOException(
                    "disk full SharedSecret=\"shared tail\" Authorization: Bearer \"bearer value\" "
                    + "Password=\"db pass\" ConnectionString=DSN=prod;Trusted_Connection=yes"),
                at));

        telemetry.Record(new LatencyEvent(
            CorrelationId: "c1",
            Component: LatencyComponents.Watch,
            Stage: LatencyStages.HttpOk,
            ElapsedMs: 1,
            StatusCode: 200,
            RowCount: 0,
            Bytes: 0,
            Endpoint: "/api/demands",
            Detail: null));
        await telemetry.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        var journal = new WatchConnectionEventJournal(
            dir.Path,
            retentionDays: 30,
            maxSizeBytes: 1024,
            utcNow: () => at);
        var feed = new WatchUnifiedEventFeed(journal, diagnostics);

        var visible = Assert.Single(feed.Load([], TimeZoneInfo.Utc));

        Assert.Equal(UnifiedEventSource.WatchConnectionEvent, visible.Source);
        Assert.Equal("TELEMETRY_IO", visible.SeverityOrStage);
        Assert.Equal("watch-latency", visible.EndpointOrTaskType);
        Assert.DoesNotContain("shared tail", visible.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer value", visible.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("db pass", visible.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("DSN=prod", visible.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("Trusted_Connection=yes", visible.Message!, StringComparison.Ordinal);
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
            retentionDays: 30,
            maxSizeBytes: 1024,
            utcNow: () => DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
            onWriteFailure: ex => diagnostics.Record("watch-connection", ex));

        var append = Task.Run(() => journal.Append(new WatchConnectionEvent(
            WatchConnectionEventKind.Recovered,
            DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
            "/api/demands",
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
    public async Task Successful_HTTP_refresh_and_connection_event_append_ignore_a_stalled_log_worker()
    {
        using var dir = new TempDirectory();
        using var dispatcher = new WatchLocalLogDispatcher(capacity: 8);
        using var release = new ManualResetEventSlim();
        Assert.True(dispatcher.TryEnqueue(release.Wait));
        var telemetry = new WatchLatencyFileTelemetry(dir.Path, dispatcher: dispatcher);
        var journal = new WatchConnectionEventJournal(dir.Path, 30, 1024 * 1024, dispatcher: dispatcher);
        using var http = new HttpClient(new SuccessfulWatchHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1:5088/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30, telemetry);

        try
        {
            var sw = Stopwatch.StartNew();
            var snapshot = await client.FetchSnapshotAsync().WaitAsync(TimeSpan.FromSeconds(1));
            journal.Append(new WatchConnectionEvent(
                WatchConnectionEventKind.Recovered,
                DateTimeOffset.UtcNow,
                "/api/demands",
                LatencyStages.HttpOk,
                1,
                30,
                "Host refresh succeeded",
                1,
                10));
            sw.Stop();

            Assert.Null(snapshot.FetchError);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Contains(journal.ReadRecent(), evt => evt.Kind == WatchConnectionEventKind.Recovered);
        }
        finally
        {
            release.Set();
        }

        await dispatcher.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
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

    private sealed class SuccessfulWatchHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (WatchHttpTestStubs.IsContractPath(path))
            {
                return Task.FromResult(WatchHttpTestStubs.MatchingContract(path));
            }

            if (path.EndsWith("/api/demands", StringComparison.Ordinal)
                || path.EndsWith("/api/alerts", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"items":[],"nextCursor":null,"hasMore":false}""",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
