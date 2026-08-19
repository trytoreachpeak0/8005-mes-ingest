using System.Net.Http.Json;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.Infrastructure.SqlServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class Ticket15RoundEvidenceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public Ticket15RoundEvidenceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Diagnostic_fields_enforce_the_SQL_evidence_contract_lengths_before_connecting()
    {
        var projection = new SqlServerMesIngestProjection(
            "Server=invalid.example;Database=invalid;User Id=invalid;Password=invalid;Encrypt=false");

        foreach (var diagnostic in new[]
        {
            new MesTaskUnionRoundDiagnostic(new string('S', 65), "CODE", "Safe detail."),
            new MesTaskUnionRoundDiagnostic("EXECUTION", new string('C', 129), "Safe detail."),
            new MesTaskUnionRoundDiagnostic("EXECUTION", "CODE", new string('D', 513)),
        })
        {
            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                projection.CommitRoundAsync(CreateFailure("poll-length", diagnostic)));
            Assert.Contains("SQL contract maximum", exception.Message, StringComparison.Ordinal);
        }
    }

    [Ticket01SqlServerFact]
    public async Task Failure_and_incomplete_diagnostics_are_durable_and_never_create_projection_commits()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var failure = CreateFailure(
            "poll-ticket15-failure",
            new MesTaskUnionRoundDiagnostic(
                "ORACLE_EXECUTION",
                "ORACLE_QUERY_TIMEOUT",
                "Oracle query exceeded the configured timeout."));
        var incomplete = failure with
        {
            PollTraceId = "poll-ticket15-incomplete",
            Outcome = MesTaskUnionRoundOutcome.Incomplete,
            Diagnostic = new MesTaskUnionRoundDiagnostic(
                "RESULT_MAPPING",
                "ORACLE_RESULT_STRUCTURE_INVALID",
                "Oracle result metadata does not match the required contract."),
        };

        var failureReceipt = await ingestor.IngestAsync(failure);
        var incompleteReceipt = await ingestor.IngestAsync(incomplete);

        Assert.Null(failureReceipt.ProjectionCommitId);
        Assert.Null(incompleteReceipt.ProjectionCommitId);
        AssertDiagnostic(
            await client.GetFromJsonAsync<JsonElement>("/api/v2/poll-traces/poll-ticket15-failure"),
            "ORACLE_EXECUTION",
            "ORACLE_QUERY_TIMEOUT",
            "Oracle query exceeded the configured timeout.");
        AssertDiagnostic(
            await client.GetFromJsonAsync<JsonElement>("/api/v2/poll-traces/poll-ticket15-incomplete"),
            "RESULT_MAPPING",
            "ORACLE_RESULT_STRUCTURE_INVALID",
            "Oracle result metadata does not match the required contract.");

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mesingest.ProjectionCommits;";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Ticket01SqlServerFact]
    public async Task Same_poll_trace_with_different_diagnostic_is_a_content_conflict()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var projection = new SqlServerMesIngestProjection(database.ConnectionString);
        var accepted = CreateFailure(
            "poll-ticket15-diagnostic-idempotency",
            new MesTaskUnionRoundDiagnostic("ORACLE_EXECUTION", "TIMEOUT", "Timeout."));
        await projection.CommitRoundAsync(accepted);

        var conflict = await Assert.ThrowsAsync<PollTraceConflictException>(() =>
            projection.CommitRoundAsync(accepted with
            {
                Diagnostic = accepted.Diagnostic! with { Code = "CANCELLED" },
            }));

        Assert.Equal("POLL_TRACE_CONTENT_CONFLICT", conflict.Code);
    }

    [Ticket01SqlServerFact]
    public async Task Invalid_dates_raw_is_durable_success_evidence_while_live_date_remains_untrusted()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var completedAt = new DateTimeOffset(2026, 8, 14, 8, 30, 0, TimeSpan.Zero);

        var receipt = await ingestor.IngestAsync(new MesTaskUnionRound(
            "poll-ticket15-invalid-dates",
            "MES_TASK_UNION/sha256:test",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-1),
            completedAt,
            [new MesTaskUnionObservation(
                "WIRE_TO_NITROGEN",
                "SL-TICKET15-INVALID-DATES",
                "N3-3",
                "EQP-15",
                "STEP-15",
                MesSourceDate: null,
                Package: "PKG-15",
                MesSourceDateRaw: "31-FOO-2026 25:61:00")]));

        Assert.NotNull(receipt.ProjectionCommitId);
        var series = await client.GetFromJsonAsync<JsonElement>(
            "/api/v2/demand-series/by-key?workType=WIRE_TO_NITROGEN&sublot=SL-TICKET15-INVALID-DATES");
        var live = series.GetProperty("currentDemand").GetProperty("liveMesFields");
        Assert.Equal(JsonValueKind.Null, live.GetProperty("mesSourceDate").ValueKind);
        var raw = Assert.Single(series.GetProperty("rawObservations").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, raw.GetProperty("mesSourceDate").ValueKind);
        Assert.Equal("31-FOO-2026 25:61:00", raw.GetProperty("mesSourceDateRaw").GetString());
        var issue = Assert.Single(series.GetProperty("currentConditions").EnumerateArray());
        Assert.Equal("INVALID_MES_FIELD_FORMAT", issue.GetProperty("code").GetString());
        Assert.Equal("DATES", issue.GetProperty("subjectKind").GetString());
        Assert.Equal("31-FOO-2026 25:61:00", issue.GetProperty("observedValue").GetString());

        var trace = await client.GetFromJsonAsync<JsonElement>(
            "/api/v2/poll-traces/poll-ticket15-invalid-dates");
        Assert.Equal(JsonValueKind.Null, trace.GetProperty("diagnostic").ValueKind);
        Assert.Equal(
            "31-FOO-2026 25:61:00",
            trace.GetProperty("observations")[0].GetProperty("mesSourceDateRaw").GetString());
    }

    private static MesTaskUnionRound CreateFailure(
        string pollTraceId,
        MesTaskUnionRoundDiagnostic diagnostic)
    {
        var startedAt = new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.Zero);
        return new MesTaskUnionRound(
            pollTraceId,
            "MES_TASK_UNION/sha256:test",
            MesTaskUnionRoundOutcome.Failure,
            startedAt,
            startedAt.AddSeconds(30),
            [],
            diagnostic);
    }

    private static void AssertDiagnostic(
        JsonElement trace,
        string stage,
        string code,
        string safeDetail)
    {
        Assert.Equal(JsonValueKind.Null, trace.GetProperty("projectionCommit").ValueKind);
        var diagnostic = trace.GetProperty("diagnostic");
        Assert.Equal(stage, diagnostic.GetProperty("stage").GetString());
        Assert.Equal(code, diagnostic.GetProperty("code").GetString());
        Assert.Equal(safeDetail, diagnostic.GetProperty("safeDetail").GetString());
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Production));

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__EnableLegacyDevelopmentEndpoints"] = "false",
            [$"{MesIngestHostOptions.SectionName}__SnapshotSource"] = MesIngestHostOptions.NoRoundSource,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
        });
}
