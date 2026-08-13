using System.Net.Http.Json;
using System.Text.Json;
using MesIngest.Core;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class OracleMesTaskUnionProductionEntryTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public OracleMesTaskUnionProductionEntryTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Ticket01SqlServerFact]
    public async Task Canonical_Oracle_result_flows_through_Production_V2_runner_to_poll_trace_and_non_success_never_mutates_business_state()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var executor = new SequencedOracleStatementExecutor(
            () => CompleteResult(
            [
                [
                    "WIRE_TO_NITROGEN",
                    "SL-TICKET15-PRODUCTION",
                    "A1-1",
                    "EQ-15",
                    "WIRE",
                    new DateTime(2026, 8, 14, 9, 15, 0, DateTimeKind.Unspecified),
                    "PKG-15",
                ],
            ]),
            () => throw new TimeoutException("Password=secret;Data Source=private-host"),
            () => new OracleStatementResult(
                CompleteColumns().Where(column => column.Name != "DATES").ToArray(),
                []));

        using var environment = new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = database.ConnectionString,
            [$"{MesIngestHostOptions.SectionName}__SnapshotSource"] = "Oracle",
            [$"{MesIngestHostOptions.SectionName}__OracleMode"] = "Thin",
            [$"{MesIngestHostOptions.SectionName}__QueriesDirectory"] = Path.Combine(AppContext.BaseDirectory, "queries"),
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
            [$"{MesIngestHostOptions.SectionName}__EnableLegacyDevelopmentEndpoints"] = "false",
        });

        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureServices(services =>
                services.AddSingleton<IOracleStatementExecutor>(executor));
        });
        var client = factory.CreateClient();
        var runner = factory.Services.GetRequiredService<MesTaskUnionPollRunner>();

        var success = await runner.RunOnceAsync();
        Assert.Equal(MesTaskUnionRoundOutcome.Success, success.Outcome);
        Assert.NotNull(success.ProjectionCommitId);

        var successTrace = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v2/poll-traces/{Uri.EscapeDataString(success.PollTraceId)}");
        Assert.Equal(CanonicalMesTaskUnionQuery.QueryVersion, successTrace.GetProperty("queryVersion").GetString());
        Assert.Equal("SUCCESS", successTrace.GetProperty("outcome").GetString());
        Assert.Equal(1, successTrace.GetProperty("rowCount").GetInt32());
        Assert.Matches("^[0-9a-f]{64}$", successTrace.GetProperty("contentDigest").GetString());
        Assert.Equal(success.ProjectionCommitId, successTrace.GetProperty("projectionCommit").GetProperty("projectionCommitId").GetString());
        Assert.Equal("SL-TICKET15-PRODUCTION", successTrace.GetProperty("observations")[0].GetProperty("sublot").GetString());

        var seriesBefore = await client.GetFromJsonAsync<JsonElement>(
            "/api/v2/demand-series/by-key?workType=WIRE_TO_NITROGEN&sublot=SL-TICKET15-PRODUCTION");
        Assert.Equal(success.ProjectionCommitId, seriesBefore.GetProperty("latestProjectionCommitId").GetString());

        var failure = await runner.RunOnceAsync();
        var incomplete = await runner.RunOnceAsync();
        Assert.Equal(MesTaskUnionRoundOutcome.Failure, failure.Outcome);
        Assert.Equal(MesTaskUnionRoundOutcome.Incomplete, incomplete.Outcome);
        Assert.Null(failure.ProjectionCommitId);
        Assert.Null(incomplete.ProjectionCommitId);

        var failureTrace = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v2/poll-traces/{Uri.EscapeDataString(failure.PollTraceId)}");
        Assert.Equal("FAILURE", failureTrace.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, failureTrace.GetProperty("projectionCommit").ValueKind);
        Assert.Equal("ORACLE_QUERY_TIMEOUT", failureTrace.GetProperty("diagnostic").GetProperty("code").GetString());
        Assert.DoesNotContain("secret", failureTrace.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-host", failureTrace.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var incompleteTrace = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v2/poll-traces/{Uri.EscapeDataString(incomplete.PollTraceId)}");
        Assert.Equal("INCOMPLETE", incompleteTrace.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, incompleteTrace.GetProperty("projectionCommit").ValueKind);
        Assert.Equal("ORACLE_RESULT_STRUCTURE_INVALID", incompleteTrace.GetProperty("diagnostic").GetProperty("code").GetString());

        var seriesAfter = await client.GetFromJsonAsync<JsonElement>(
            "/api/v2/demand-series/by-key?workType=WIRE_TO_NITROGEN&sublot=SL-TICKET15-PRODUCTION");
        Assert.Equal(
            seriesBefore.GetProperty("latestProjectionCommitId").GetString(),
            seriesAfter.GetProperty("latestProjectionCommitId").GetString());
        Assert.Equal(3, executor.Requests.Count);
        Assert.All(executor.Requests, request =>
        {
            Assert.Equal(CanonicalMesTaskUnionQuery.ExpectedSha256, request.QuerySha256);
            Assert.Equal(CanonicalMesTaskUnionQuery.QueryVersion, request.QueryVersion);
            Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(
                request.Sql,
                "UNION\\s+ALL",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count - 1); // one mention is in the approved comment
        });

        _output.WriteLine(
            $"Ticket15 ProductVersion={database.ProductVersion}; ProductMajor={database.ProductMajor}; "
            + $"EngineEdition={database.EngineEdition}; CompatibilityLevel={database.CompatibilityLevel}");
    }

    private static OracleStatementResult CompleteResult(IReadOnlyList<IReadOnlyList<object?>> rows) =>
        new(CompleteColumns(), rows);

    private static IReadOnlyList<OracleResultColumn> CompleteColumns() =>
    [
        new("TASK_TYPE", "VARCHAR2", OracleColumnKind.Text),
        new("SUBLOT", "VARCHAR2", OracleColumnKind.Text),
        new("AREA", "VARCHAR2", OracleColumnKind.Text),
        new("EQP", "VARCHAR2", OracleColumnKind.Text),
        new("STEP", "VARCHAR2", OracleColumnKind.Text),
        new("DATES", "DATE", OracleColumnKind.DateTime),
        new("PACKAGE", "VARCHAR2", OracleColumnKind.Text),
    ];

    private sealed class SequencedOracleStatementExecutor(
        params Func<OracleStatementResult>[] steps) : IOracleStatementExecutor
    {
        private readonly Queue<Func<OracleStatementResult>> _steps = new(steps);

        public OracleExecutorIdentity Identity { get; } =
            new(OracleClientMode.Thin, OracleClientMode.Thin, "fake-production-thin");

        public List<OracleStatementRequest> Requests { get; } = [];

        public Task<OracleStatementResult> ExecuteAsync(
            OracleStatementRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            try
            {
                return Task.FromResult(_steps.Dequeue()());
            }
            catch (Exception exception)
            {
                return Task.FromException<OracleStatementResult>(exception);
            }
        }
    }
}
