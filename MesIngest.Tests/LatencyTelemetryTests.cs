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
    public async Task Watch_refresh_sends_same_correlation_id_on_all_three_requests()
    {
        var seen = new List<string?>();
        var handler = new StubHandler((request, _) =>
        {
            seen.Add(request.Headers.TryGetValues(LatencyHeaders.CorrelationId, out var values)
                ? values.Single()
                : null);

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/poll-health", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(EmptyDemandsOrList(path));
        });

        using var http = CreateHttp(handler);
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30);

        var snapshot = await client.FetchSnapshotAsync();

        Assert.Null(snapshot.FetchError);
        Assert.Equal(4, seen.Count);
        Assert.All(seen, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.True(seen.Distinct(StringComparer.Ordinal).Count() == 1);
        Assert.Equal(seen[0], snapshot.CorrelationId);
    }

    [Fact]
    public async Task Watch_timeout_failure_includes_correlation_id_and_WATCH_TIMEOUT_stage()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                throw new TaskCanceledException("canceled", new TimeoutException());
            }

            return Task.FromResult(EmptyDemandsOrList(request.RequestUri.AbsolutePath));
        });

        using var http = CreateHttp(handler);
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30);

        var snapshot = await client.FetchSnapshotAsync();

        Assert.NotNull(snapshot.FetchError);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CorrelationId));
        Assert.Contains($"correlationId={snapshot.CorrelationId}", snapshot.FetchError, StringComparison.Ordinal);
        Assert.Equal(LatencyStages.WatchTimeout, snapshot.FailedStage);
        Assert.Contains(LatencyStages.WatchTimeout, snapshot.FetchError, StringComparison.Ordinal);
        Assert.Contains("/api/demands", snapshot.FetchError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_json_parse_failure_reports_HTTP_JSON_stage()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{not-json", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(EmptyDemandsOrList(request.RequestUri.AbsolutePath));
        });

        using var http = CreateHttp(handler);
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30);

        var snapshot = await client.FetchSnapshotAsync();

        Assert.Equal(LatencyStages.HttpJson, snapshot.FailedStage);
        Assert.Contains(LatencyStages.HttpJson, snapshot.FetchError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_success_records_per_endpoint_status_rows_and_bytes_without_secrets()
    {
        var sink = new RecordingLatencyTelemetry();
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (WatchHttpTestStubs.IsContractPath(path))
            {
                return Task.FromResult(WatchHttpTestStubs.MatchingContract(path));
            }

            if (path.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    path,
                    """{"items":[{"demandId":"a","taskType":"T","sublot":"S","area":null,"eqp":null,"step":null,"dates":"2026-07-30T10:00:00+08:00","package":null,"status":"VISIBLE","mesLastSeenAt":"2026-07-30T11:00:00+08:00","disappearCount":0,"locationRisk":false,"locationRiskCode":null,"createdAt":"2026-07-30T09:00:00+08:00","goneAt":null,"alerts":[]}],"nextCursor":null,"hasMore":false}"""));
            }

            if (path.EndsWith("/api/alerts", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(path, """{"items":[],"nextCursor":null,"hasMore":false}"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var http = CreateHttp(handler);
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30, telemetry: sink);

        var snapshot = await client.FetchSnapshotAsync();

        Assert.Null(snapshot.FetchError);
        Assert.Equal(4, sink.Events.Count);
        Assert.All(sink.Events, e => Assert.Equal(snapshot.CorrelationId, e.CorrelationId));
        Assert.All(sink.Events, e => Assert.Equal(LatencyComponents.Watch, e.Component));
        Assert.Contains(sink.Events, e => e.Endpoint == "/api/contract" && e.StatusCode == 200);
        Assert.Contains(sink.Events, e =>
            e.Endpoint == "/api/demands" && e.StatusCode == 200 && e.RowCount == 1 && e.Bytes > 0);
        Assert.Contains(sink.Events, e => e.Endpoint == "/api/alerts" && e.StatusCode == 200);
        Assert.Contains(sink.Events, e => e.Endpoint == "/api/poll-health" && e.StatusCode == 404);
        Assert.DoesNotContain(
            sink.Events.Select(e => LatencyLogFormatter.Format(e)),
            line => line.Contains("SharedSecret", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Watch_success_survives_throwing_latency_telemetry()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (WatchHttpTestStubs.IsContractPath(path))
            {
                return Task.FromResult(WatchHttpTestStubs.MatchingContract(path));
            }

            if (path.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    path,
                    """{"items":[],"nextCursor":null,"hasMore":false}"""));
            }

            if (path.EndsWith("/api/alerts", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(path, """{"items":[],"nextCursor":null,"hasMore":false}"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var http = CreateHttp(handler);
        var client = new MesIngestApiClient(
            http,
            requestTimeoutSeconds: 30,
            telemetry: new ThrowingLatencyTelemetry());

        var snapshot = await client.FetchSnapshotAsync();

        Assert.Null(snapshot.FetchError);
        Assert.Empty(snapshot.Demands);
    }

    [Fact]
    public async Task Watch_success_survives_unavailable_latency_directory_and_reports_TELEMETRY_IO()
    {
        using var dir = new TempLatencyDir();
        Directory.Delete(dir.Path);
        File.WriteAllText(dir.Path, "not-a-directory");
        var diagnostics = new WatchTelemetryIoDiagnosticBuffer();
        var telemetry = new WatchLatencyFileTelemetry(
            dir.Path,
            onWriteFailure: ex => diagnostics.Record("watch-latency", ex));
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/poll-health", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(EmptyDemandsOrList(path));
        });

        using var http = CreateHttp(handler);
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30, telemetry);

        var snapshot = await client.FetchSnapshotAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await telemetry.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(snapshot.FetchError);
        Assert.Empty(snapshot.Demands);
        Assert.NotEmpty(diagnostics.ReadRecent());
        Assert.All(
            diagnostics.ReadRecent(),
            diagnostic => Assert.Equal(WatchTelemetryIoDiagnosticBuffer.Stage, diagnostic.Stage));
    }

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
            Endpoint: "/api/demands",
            Detail: null));
        await telemetry.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(observed);
        Assert.True(observed is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public async Task Watch_latency_file_telemetry_enforces_log_retention_by_age()
    {
        using var dir = new TempLatencyDir();
        var oldLog = Path.Combine(dir.Path, "watch-latency-20260101.log");
        File.WriteAllText(oldLog, "old\n");
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-40));

        var now = DateTimeOffset.Parse("2026-07-31T10:00:00Z");
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
            Endpoint: "/api/demands",
            Detail: null));
        await telemetry.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(Path.Combine(dir.Path, "watch-latency-20260731.log")));
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
    public async Task Oracle_query_timeout_sets_poll_health_and_alert_stage_ORACLE_QUERY()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        var runner = new IngestRoundRunner(
            new HangingMesSnapshotSource(),
            new TransportDemandReconciler(new SequentialDemandIdAllocator("new")),
            store,
            goLiveBaseline: now.AddHours(-12),
            queryTimeout: TimeSpan.FromMilliseconds(30),
            clock: () => now);

        await runner.RunOnceAsync();

        var health = store.GetLatestPollHealth();
        Assert.NotNull(health);
        Assert.Equal("FAILURE", health!.Outcome);
        Assert.Equal(LatencyStages.OracleQuery, health.FailureStage);
        Assert.True(health.OracleDurationMs >= 0);

        var alert = Assert.Single(store.ListAlerts());
        Assert.Equal("POLL_FAILURE", alert.Code);
        Assert.Contains($"stage={LatencyStages.OracleQuery}", alert.Message!, StringComparison.Ordinal);
        Assert.Contains("durationMs=", alert.Message!, StringComparison.Ordinal);
        Assert.Contains("rowCount=", alert.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_store_observer_records_query_write_and_transaction_stages()
    {
        var sink = new RecordingLatencyTelemetry();
        LatencyCorrelation.Id = "corr-sql-1";
        try
        {
            var store = new ObservingTransportDemandStore(new InMemoryTransportDemandStore(), sink);
            store.ReplaceState(ProjectionState.Empty);
            _ = store.GetState();
            store.AppendAlerts(
            [
                new IngestAlert(Code: "POLL_FAILURE", Message: "x"),
            ]);
            store.SetLatestPollHealth(new PollHealth(
                StartedAt: DateTimeOffset.UtcNow,
                EndedAt: DateTimeOffset.UtcNow,
                DurationMs: 1,
                RowCount: 0,
                Success: false,
                Outcome: "FAILURE",
                FailureStage: LatencyStages.OracleQuery,
                OracleDurationMs: 1));
        }
        finally
        {
            LatencyCorrelation.Id = null;
        }

        Assert.Contains(sink.Events, e => e.Stage == LatencyStages.SqlWrite && e.CorrelationId == "corr-sql-1");
        Assert.Contains(sink.Events, e => e.Stage == LatencyStages.SqlQuery && e.CorrelationId == "corr-sql-1");
        Assert.Contains(sink.Events, e => e.Stage == LatencyStages.SqlTransaction && e.CorrelationId == "corr-sql-1");
        Assert.All(sink.Events, e => Assert.Equal(LatencyComponents.SqlServer, e.Component));
    }

    [Fact]
    public async Task Watch_timeout_records_failure_stage_in_telemetry_sink()
    {
        var sink = new RecordingLatencyTelemetry();
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (WatchHttpTestStubs.IsContractPath(path))
            {
                return Task.FromResult(WatchHttpTestStubs.MatchingContract(path));
            }

            if (path.EndsWith("/api/demands", StringComparison.Ordinal))
            {
                throw new TaskCanceledException("canceled", new TimeoutException());
            }

            return Task.FromResult(EmptyDemandsOrList(path));
        });

        using var http = CreateHttp(handler);
        var client = new MesIngestApiClient(http, requestTimeoutSeconds: 30, telemetry: sink);

        var snapshot = await client.FetchSnapshotAsync();

        Assert.Equal(LatencyStages.WatchTimeout, snapshot.FailedStage);
        Assert.Contains(
            sink.Events,
            e => e.Stage == LatencyStages.WatchTimeout
                && e.Endpoint == "/api/demands"
                && e.CorrelationId == snapshot.CorrelationId);
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
            Endpoint: "/api/demands",
            Detail: "Authorization: Bearer super-secret SharedSecret=topsecret Password=dbpass");

        var line = LatencyLogFormatter.Format(evt);

        Assert.Contains("correlationId=abc123", line, StringComparison.Ordinal);
        Assert.Contains("component=Host", line, StringComparison.Ordinal);
        Assert.Contains($"stage={LatencyStages.HostAbort}", line, StringComparison.Ordinal);
        Assert.Contains("endpoint=/api/demands", line, StringComparison.Ordinal);
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

    private static HttpResponseMessage EmptyDemandsOrList(string path)
    {
        if (WatchHttpTestStubs.IsContractPath(path))
        {
            return WatchHttpTestStubs.MatchingContract(path);
        }

        return path.EndsWith("/api/demands", StringComparison.Ordinal)
               || path.EndsWith("/api/alerts", StringComparison.Ordinal)
            ? JsonResponse(path, """{"items":[],"nextCursor":null,"hasMore":false}""")
            : JsonResponse(path, "[]");
    }
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

    private sealed class HangingMesSnapshotSource : IMesSnapshotSource
    {
        public async Task<MesSnapshotOutcome> ReadAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return MesSnapshotOutcome.Success([]);
        }
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
