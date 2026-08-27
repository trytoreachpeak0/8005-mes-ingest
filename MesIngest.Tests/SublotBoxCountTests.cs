using System.Net;
using System.Text.Json;
using MesIngest.Core;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesIngest.Tests;

public sealed class SublotBoxCountTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public SublotBoxCountTests(WebApplicationFactory<Program> factory) =>
        this.factory = factory;

    [Fact]
    public async Task Endpoint_returns_exact_identity_bound_result_and_forwards_untrimmed_sublot()
    {
        var observedAt = new DateTimeOffset(2026, 8, 27, 9, 30, 0, TimeSpan.Zero);
        var reader = new StubReader(SublotBoxCountReadResult.Success(17, observedAt));
        await using var app = CreateFactory(reader);
        var client = app.CreateClient();

        using var response = await client.GetAsync("/api/v2/sublot-box-count?sublot=%20SL-001%20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("SUBLOT_BOX_COUNT", root.GetProperty("queryId").GetString());
        Assert.Equal(" SL-001 ", root.GetProperty("sublot").GetString());
        Assert.Equal(17, root.GetProperty("maxBoxCount").GetInt32());
        Assert.Equal(observedAt, root.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal([" SL-001 "], reader.Sublots);
    }

    [Theory]
    [InlineData("/api/v2/sublot-box-count", "SUBLOT_BOX_COUNT_INVALID_QUERY")]
    [InlineData("/api/v2/sublot-box-count?sublot=%20%20", "SUBLOT_BOX_COUNT_INVALID_QUERY")]
    [InlineData("/api/v2/sublot-box-count?sublot=A&sublot=B", "SUBLOT_BOX_COUNT_INVALID_QUERY")]
    [InlineData("/api/v2/sublot-box-count?sublot=A&extra=1", "SUBLOT_BOX_COUNT_INVALID_QUERY")]
    public async Task Endpoint_rejects_missing_blank_duplicate_or_unsupported_query_without_reading(
        string path,
        string expectedCode)
    {
        var reader = new StubReader(SublotBoxCountReadResult.Success(1, DateTimeOffset.UtcNow));
        await using var app = CreateFactory(reader);
        var client = app.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
        Assert.Empty(reader.Sublots);
    }

    [Fact]
    public async Task Endpoint_rejects_sublot_longer_than_the_frozen_limit_without_reading()
    {
        var reader = new StubReader(SublotBoxCountReadResult.Success(1, DateTimeOffset.UtcNow));
        await using var app = CreateFactory(reader);
        var client = app.CreateClient();
        var sublot = new string('S', CanonicalSublotBoxCountQuery.MaximumSublotLength + 1);

        using var response = await client.GetAsync(
            "/api/v2/sublot-box-count?sublot=" + Uri.EscapeDataString(sublot));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "SUBLOT_BOX_COUNT_INVALID_QUERY",
            document.RootElement.GetProperty("code").GetString());
        Assert.Empty(reader.Sublots);
    }

    [Theory]
    [InlineData((int)SublotBoxCountReadOutcome.InvalidResult, 422, "SUBLOT_BOX_COUNT_NOT_AVAILABLE")]
    [InlineData((int)SublotBoxCountReadOutcome.Unavailable, 503, "SUBLOT_BOX_COUNT_SOURCE_UNAVAILABLE")]
    [InlineData((int)SublotBoxCountReadOutcome.Timeout, 504, "SUBLOT_BOX_COUNT_TIMEOUT")]
    public async Task Endpoint_maps_fail_closed_outcomes_to_stable_http_errors(
        int outcomeValue,
        int expectedStatus,
        string expectedCode)
    {
        var reader = new StubReader(SublotBoxCountReadResult.Failure(
            (SublotBoxCountReadOutcome)outcomeValue,
            expectedCode,
            "safe error"));
        await using var app = CreateFactory(reader);
        var client = app.CreateClient();

        using var response = await client.GetAsync("/api/v2/sublot-box-count?sublot=SL-001");

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
        Assert.Equal("safe error", document.RootElement.GetProperty("error").GetString());
        Assert.Equal(["SL-001"], reader.Sublots);
    }

    [Fact]
    public async Task Reader_executes_only_the_pinned_query_with_one_exact_bound_sublot()
    {
        var observedAt = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var executor = new RecordingExecutor(Result(12m));
        var options = Options();
        var reader = new OracleSublotBoxCountReader(
            options,
            new StubExecutorFactory(executor),
            new FixedTimeProvider(observedAt),
            NullLogger<OracleSublotBoxCountReader>.Instance);

        var result = await reader.ReadAsync("  SL-EXACT  ");

        Assert.Equal(SublotBoxCountReadOutcome.Success, result.Outcome);
        Assert.Equal(12, result.MaxBoxCount);
        Assert.Equal(observedAt, result.ObservedAt);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(CanonicalSublotBoxCountQuery.QueryVersion, request.QueryVersion);
        Assert.Equal(CanonicalSublotBoxCountQuery.ExpectedSha256, request.QuerySha256);
        Assert.True(CanonicalSublotBoxCountQuery.IsApprovedSql(request.Sql));
        Assert.Equal(options.CommandTimeoutSeconds, request.CommandTimeoutSeconds);
        Assert.Equal([new OracleBindParameter("sublot", "  SL-EXACT  ")], request.BindParameters);
        Assert.DoesNotContain("SL-EXACT", request.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_maps_canonical_query_sharing_lock_to_stable_query_invalid_result()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "MesIngestSublotBoxCountTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var queryPath = Path.Combine(directory, "query.sql");
        File.Copy(Options().QuerySqlPath, queryPath);
        var executor = new RecordingExecutor(Result(1m));
        var options = Options();
        options.QuerySqlPath = queryPath;

        try
        {
            using var lockStream = new FileStream(
                queryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            var reader = new OracleSublotBoxCountReader(
                options,
                new StubExecutorFactory(executor),
                new FixedTimeProvider(DateTimeOffset.UtcNow),
                NullLogger<OracleSublotBoxCountReader>.Instance);

            var result = await reader.ReadAsync("SL-LOCKED");

            Assert.Equal(SublotBoxCountReadOutcome.Unavailable, result.Outcome);
            Assert.Equal("SUBLOT_BOX_COUNT_QUERY_INVALID", result.ErrorCode);
            Assert.Null(result.MaxBoxCount);
            Assert.Empty(executor.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static IEnumerable<object?[]> InvalidResults()
    {
        yield return [null];
        yield return [0m];
        yield return [-1m];
        yield return [1.5m];
        yield return [(decimal)int.MaxValue + 1];
        yield return ["not-a-number"];
    }

    [Theory]
    [MemberData(nameof(InvalidResults))]
    public async Task Reader_rejects_null_nonpositive_fractional_overflow_or_nonnumeric_results(object? value)
    {
        var reader = Reader(Result(value));

        var result = await reader.ReadAsync("SL-INVALID");

        Assert.Equal(SublotBoxCountReadOutcome.InvalidResult, result.Outcome);
        Assert.Null(result.MaxBoxCount);
        Assert.Equal("SUBLOT_BOX_COUNT_NOT_AVAILABLE", result.ErrorCode);
        Assert.Equal(default, result.ObservedAt);
    }

    public static IEnumerable<object[]> InvalidShapes()
    {
        yield return
        [
            new OracleStatementResult(
                [new OracleResultColumn("WRONG_COLUMN", "NUMBER", OracleColumnKind.Unsupported)],
                [[1m]])
        ];
        yield return
        [
            new OracleStatementResult(
                [new OracleResultColumn("MAX_BOX_COUNT", "NUMBER", OracleColumnKind.Unsupported)],
                [])
        ];
        yield return
        [
            new OracleStatementResult(
                [new OracleResultColumn("MAX_BOX_COUNT", "NUMBER", OracleColumnKind.Unsupported)],
                [[1m], [2m]])
        ];
        yield return
        [
            new OracleStatementResult(
                [
                    new OracleResultColumn("MAX_BOX_COUNT", "NUMBER", OracleColumnKind.Unsupported),
                    new OracleResultColumn("EXTRA", "NUMBER", OracleColumnKind.Unsupported),
                ],
                [[1m, 2m]])
        ];
    }

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public async Task Reader_rejects_wrong_column_empty_multiple_or_extra_result_shapes(
        OracleStatementResult statementResult)
    {
        var reader = Reader(statementResult);

        var result = await reader.ReadAsync("SL-SHAPE");

        Assert.Equal(SublotBoxCountReadOutcome.InvalidResult, result.Outcome);
        Assert.Equal("SUBLOT_BOX_COUNT_NOT_AVAILABLE", result.ErrorCode);
        Assert.Null(result.MaxBoxCount);
    }

    [Fact]
    public async Task Reader_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = Reader(Result(1m));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync("SL-CANCELLED", cancellation.Token));
    }

    private OracleSublotBoxCountReader Reader(OracleStatementResult result) =>
        new(
            Options(),
            new StubExecutorFactory(new RecordingExecutor(result)),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero)),
            NullLogger<OracleSublotBoxCountReader>.Instance);

    private static OracleSnapshotOptions Options() => new()
    {
        Mode = OracleClientMode.Thin,
        User = "read-only-user",
        Password = "test-only-password",
        DataSource = "oracle.test",
        CommandTimeoutSeconds = 13,
        QuerySqlPath = Path.Combine(
            AppContext.BaseDirectory,
            "queries",
            "sublot-box-count",
            "query.sql"),
    };

    private static OracleStatementResult Result(object? value) => new(
        [new OracleResultColumn("MAX_BOX_COUNT", "NUMBER", OracleColumnKind.Unsupported)],
        [[value]]);

    private WebApplicationFactory<Program> CreateFactory(ISublotBoxCountReader reader) =>
        factory.WithWebHostBuilder(builder =>
        {
            // This is an API-surface fixture with an injected reader, not a
            // long-lived production Host with a current MES producer.
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting(
                $"{MesIngestHostOptions.SectionName}:NewSqlServerConnectionString",
                "Server=contract.invalid;Database=contract;Integrated Security=true;Encrypt=false");
            builder.UseSetting(
                $"{MesIngestHostOptions.SectionName}:SnapshotSource",
                MesIngestHostOptions.NoRoundSource);
            builder.UseSetting($"{MesIngestHostOptions.SectionName}:ContinuousPollEnabled", "false");
            builder.UseSetting($"{MesIngestHostOptions.SectionName}:RunOneShotOnStartup", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ISublotBoxCountReader>();
                services.AddSingleton(reader);
            });
        });

    private sealed class StubReader(SublotBoxCountReadResult result) : ISublotBoxCountReader
    {
        public List<string> Sublots { get; } = [];

        public Task<SublotBoxCountReadResult> ReadAsync(
            string sublot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sublots.Add(sublot);
            return Task.FromResult(result);
        }
    }

    private sealed class StubExecutorFactory(IOracleStatementExecutor executor)
        : IOracleStatementExecutorFactory
    {
        public IOracleStatementExecutor Create(OracleSnapshotOptions options)
        {
            Assert.Equal(Options().QuerySqlPath, options.QuerySqlPath);
            return executor;
        }
    }

    private sealed class RecordingExecutor(OracleStatementResult result) : IOracleStatementExecutor
    {
        public OracleExecutorIdentity Identity { get; } = new(
            OracleClientMode.Thin,
            OracleClientMode.Thin,
            "test");

        public List<OracleStatementRequest> Requests { get; } = [];

        public Task<OracleStatementResult> ExecuteAsync(
            OracleStatementRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
