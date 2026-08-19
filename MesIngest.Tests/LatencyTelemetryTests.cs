using System.Net;
using System.Text;
using MesIngest.Core;
using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 03 seams: Watch HTTP correlation/stages, Oracle failure stage,
/// SQL store stage observer, and secret-free latency log formatting.
/// Assertions use stable classification — never wall-clock network delay.
/// </summary>
public class LatencyTelemetryTests
{
    [Fact]
    public async Task Watch_latency_file_telemetry_swallows_io_and_reports_write_failure()
    {
        using var dir = new TempLatencyDir();
        Exception? observed = null;
        var telemetry = new WatchLatencyFileTelemetry(
            dir.Path,
            retentionDays: 30,
            maxSizeBytes: 1024,
            onWriteFailure: ex => observed = ex);

        // Make the directory a file so CreateDirectory / AppendAllText fails.
        Directory.Delete(dir.Path);
        File.WriteAllText(dir.Path, "not-a-directory");

        telemetry.Record(new LatencyEvent(
            CorrelationId: "c1",
            Component: LatencyComponents.Watch,
            Stage: LatencyStages.HttpOk,
            ElapsedMs: 1,
            StatusCode: 200,
            RowCount: 0,
            Bytes: 0,
            Endpoint: "/api/v2/demand-series",
            Detail: null));
        await telemetry.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(observed);
        Assert.True(observed is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public async Task Watch_latency_file_telemetry_enforces_log_retention_by_age()
    {
        using var dir = new TempLatencyDir();
        var now = DateTimeOffset.Parse("2026-07-31T10:00:00Z");
        var oldLog = Path.Combine(dir.Path, "watch-latency-20260101.log");
        File.WriteAllText(oldLog, "old\n");

        // Age the file against the injected clock, not the wall clock: retention is
        // measured from `now`, so a wall-clock offset silently stops being older
        // than the cutoff once real time moves past it.
        File.SetLastWriteTimeUtc(oldLog, now.UtcDateTime.AddDays(-40));

        var telemetry = new WatchLatencyFileTelemetry(
            dir.Path,
            retentionDays: 30,
            maxSizeBytes: 100 * 1024 * 1024,
            utcNow: () => now);

        telemetry.Record(new LatencyEvent(
            CorrelationId: "c1",
            Component: LatencyComponents.Watch,
            Stage: LatencyStages.HttpOk,
            ElapsedMs: 1,
            StatusCode: 200,
            RowCount: 0,
            Bytes: 0,
            Endpoint: "/api/v2/demand-series",
            Detail: null));
        await telemetry.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(File.Exists(oldLog));
        var currentLog = Path.Combine(dir.Path, "watch-latency-20260731.log");
        Assert.True(File.Exists(currentLog));
        Assert.StartsWith(
            "recordedAt=2026-07-31T10:00:00.0000000+00:00 ",
            File.ReadAllText(currentLog));
    }

    [Fact]
    public void Latency_write_failure_is_recorded_in_memory_without_secrets()
    {
        var at = DateTimeOffset.Parse("2026-07-31T10:00:00Z");
        var diagnostics = new WatchTelemetryIoDiagnosticBuffer();

        diagnostics.Record(
            "watch-latency",
            new IOException("disk full SharedSecret=leak-token Authorization: Bearer abc"),
            at);

        var evt = Assert.Single(diagnostics.ReadRecent(10));
        Assert.Equal(WatchConnectionEventKind.Failure, evt.Kind);
        Assert.Equal("watch-latency", evt.Endpoint);
        Assert.Equal(WatchTelemetryIoDiagnosticBuffer.Stage, evt.Stage);
        Assert.DoesNotContain("leak-token", evt.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer abc", evt.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SharedSecret=[redacted]", evt.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Latency_log_formatter_redacts_secrets_and_keeps_attribution_fields()
    {
        var evt = new LatencyEvent(
            CorrelationId: "abc123",
            Component: LatencyComponents.Host,
            Stage: LatencyStages.HostAbort,
            ElapsedMs: 12,
            StatusCode: 499,
            RowCount: 0,
            Bytes: 0,
            Endpoint: "/api/v2/demand-series",
            Detail: "Authorization: Bearer super-secret SharedSecret=topsecret Password=dbpass");

        var line = LatencyLogFormatter.Format(evt);

        Assert.Contains("correlationId=abc123", line, StringComparison.Ordinal);
        Assert.Contains("component=Host", line, StringComparison.Ordinal);
        Assert.Contains($"stage={LatencyStages.HostAbort}", line, StringComparison.Ordinal);
        Assert.Contains("endpoint=/api/v2/demand-series", line, StringComparison.Ordinal);
        Assert.Contains("statusCode=499", line, StringComparison.Ordinal);
        Assert.Contains("elapsedMs=12", line, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", line, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", line, StringComparison.Ordinal);
        Assert.DoesNotContain("dbpass", line, StringComparison.Ordinal);
        Assert.Contains("[redacted]", line, StringComparison.Ordinal);

        var withConn = LatencyLogFormatter.Sanitize(
            "Data Source=sql01;Initial Catalog=mes;User ID=sa;Password=dbpass");
        Assert.DoesNotContain("sql01", withConn, StringComparison.Ordinal);
        Assert.DoesNotContain("dbpass", withConn, StringComparison.Ordinal);
        Assert.Contains("[redacted]", withConn, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_http_exception_distinguishes_watch_timeout_host_abort_and_json()
    {
        Assert.Equal(
            LatencyStages.WatchTimeout,
            WatchHttpStageClassifier.Classify(new TaskCanceledException("x", new TimeoutException())));
        Assert.Equal(
            LatencyStages.HostAbort,
            WatchHttpStageClassifier.Classify(new TaskCanceledException("canceled by caller")));
        Assert.Equal(
            LatencyStages.HttpJson,
            WatchHttpStageClassifier.Classify(new System.Text.Json.JsonException("bad")));
        Assert.Equal(
            LatencyStages.HttpConnect,
            WatchHttpStageClassifier.Classify(new HttpRequestException("refused")));
        Assert.Equal(
            LatencyStages.HttpStatus,
            WatchHttpStageClassifier.Classify(new HttpRequestException("boom", null, HttpStatusCode.InternalServerError)));
    }

    [Fact]
    public void Sql_failure_classifier_maps_client_timeout_number_to_SQL_TIMEOUT()
    {
        Assert.Equal(LatencyStages.SqlTimeout, SqlFailureClassifier.ClassifyNumber(-2));
        Assert.Equal(LatencyStages.SqlQuery, SqlFailureClassifier.ClassifyNumber(50000));
        Assert.Equal(LatencyStages.SqlQuery, SqlFailureClassifier.Classify(new InvalidOperationException("x")));
    }

    private static HttpClient CreateHttp(HttpMessageHandler handler) =>
        new(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5088/"),
            Timeout = TimeSpan.FromSeconds(30),
        };

    private static HttpResponseMessage JsonResponse(string path, string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5088" + path),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }

    private sealed class ThrowingLatencyTelemetry : ILatencyTelemetry
    {
        public void Record(LatencyEvent evt) =>
            throw new IOException("simulated latency log write failure");
    }

    private sealed class TempLatencyDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MesIngestLatencyTests-" + Guid.NewGuid().ToString("N"));

        public TempLatencyDir() => Directory.CreateDirectory(Path);

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
            catch (IOException)
            {
                // best-effort cleanup
            }
            catch (UnauthorizedAccessException)
            {
                // best-effort cleanup
            }
        }
    }
}
