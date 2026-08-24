using System.Net;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class PollTraceRawEvidenceCutoverTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PollTraceRawEvidenceCutoverTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Ticket01SqlServerFact]
    public async Task PollTrace_read_is_bound_to_exact_trace_and_commit_and_preserves_the_raw_multiset()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(new AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 24, 1, 2, 0, TimeSpan.Zero)));
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var targetCompletedAt = new DateTimeOffset(2026, 8, 24, 1, 0, 0, TimeSpan.Zero);

        var target = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-exact-target",
            targetCompletedAt,
            InvalidDuplicateObservation(),
            InvalidDuplicateObservation(),
            new MesTaskUnionObservation(
                WorkType: null,
                Sublot: null,
                Area: null,
                Eqp: null,
                Step: null,
                MesSourceDate: null,
                Package: null,
                MesSourceDateRaw: null)));
        var unrelated = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-exact-unrelated",
            targetCompletedAt.AddMinutes(1),
            new MesTaskUnionObservation(
                "WIRE_TO_NITROGEN",
                "SL-TICKET11-UNRELATED",
                "N3-3",
                "WB-99",
                "焊线2",
                targetCompletedAt,
                "QFN-UNRELATED")));

        await InsertForeignCommitObservationAsync(
            database.ConnectionString,
            target.PollTraceId,
            unrelated.ProjectionCommitId!);

        using var response = await client.GetAsync(
            $"/api/v2/poll-traces/{Uri.EscapeDataString(target.PollTraceId)}");
        var trace = await ReadJsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(await ReadHistoryEpochAsync(database.ConnectionString),
            trace.GetProperty("historyEpoch").GetGuid());
        Assert.Equal(targetCompletedAt,
            trace.GetProperty("earliestAvailableHostUtc").GetDateTimeOffset());
        Assert.Equal(target.PollTraceId, trace.GetProperty("pollTraceId").GetString());
        Assert.Equal(target.ProjectionCommitId,
            trace.GetProperty("projectionCommit").GetProperty("projectionCommitId").GetString());
        Assert.Equal(3, trace.GetProperty("rowCount").GetInt32());

        var observations = trace.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(3, observations.Length);
        Assert.Equal(new[] { 0, 1, 2 },
            observations.Select(item => item.GetProperty("ordinal").GetInt32()));
        Assert.All(observations, item => Assert.Equal(
            target.ProjectionCommitId,
            item.GetProperty("projectionCommitId").GetString()));

        var duplicates = observations.Where(item =>
            item.GetProperty("sublot").GetString() == "SL-TICKET11-DUPLICATE").ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.All(duplicates, item =>
        {
            Assert.Equal("A01-01", item.GetProperty("area").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("eqp").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("mesSourceDate").ValueKind);
            Assert.Equal("not-a-date", item.GetProperty("mesSourceDateRaw").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("package").ValueKind);
        });
        var unassigned = Assert.Single(observations.Where(item =>
            item.GetProperty("assignment").GetString() == "UNASSIGNED"));
        Assert.Equal(JsonValueKind.Null, unassigned.GetProperty("workType").ValueKind);
        Assert.Equal(JsonValueKind.Null, unassigned.GetProperty("sublot").ValueKind);
    }

    [Ticket01SqlServerFact]
    public async Task PollTrace_read_distinguishes_expired_from_never_existing_and_shares_the_earliest_boundary()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(new AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 23, 1, 0, 0, TimeSpan.Zero)));
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var expiredCompletedAt = new DateTimeOffset(2026, 7, 24, 1, 0, 0, TimeSpan.Zero);
        var earliestAvailableHostUtc = new DateTimeOffset(2026, 7, 25, 1, 0, 0, TimeSpan.Zero);
        var expired = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-expired",
            expiredCompletedAt,
            ValidObservation("SL-TICKET11-EXPIRED")));
        var retained = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-retained",
            earliestAvailableHostUtc,
            ValidObservation("SL-TICKET11-RETAINED")));
        var historyEpoch = await ReadHistoryEpochAsync(database.ConnectionString);

        // Preserve the PollTrace identity while making its formerly non-empty raw
        // evidence unavailable, as the later retention job will do.
        await ExpireRawObservationsAsync(
            database.ConnectionString,
            expired.PollTraceId,
            earliestAvailableHostUtc);

        using (var expiredResponse = await client.GetAsync(
            $"/api/v2/poll-traces/{Uri.EscapeDataString(expired.PollTraceId)}"))
        {
            var body = await ReadJsonAsync(expiredResponse);
            Assert.Equal(HttpStatusCode.Gone, expiredResponse.StatusCode);
            Assert.Equal("MES_INGEST_HISTORY_EXPIRED", body.GetProperty("code").GetString());
            Assert.Equal(historyEpoch, body.GetProperty("historyEpoch").GetGuid());
            Assert.Equal(earliestAvailableHostUtc,
                body.GetProperty("earliestAvailableHostUtc").GetDateTimeOffset());
        }

        using (var missingResponse = await client.GetAsync(
            "/api/v2/poll-traces/poll-ticket11-never-existed"))
        {
            var body = await ReadJsonAsync(missingResponse);
            Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
            Assert.Equal(PollEvidenceErrorCodes.PollTraceNotFound,
                body.GetProperty("code").GetString());
            Assert.Equal(historyEpoch, body.GetProperty("historyEpoch").GetGuid());
            Assert.Equal(earliestAvailableHostUtc,
                body.GetProperty("earliestAvailableHostUtc").GetDateTimeOffset());
        }

        using (var retainedResponse = await client.GetAsync(
            $"/api/v2/poll-traces/{Uri.EscapeDataString(retained.PollTraceId)}"))
        {
            var body = await ReadJsonAsync(retainedResponse);
            Assert.Equal(HttpStatusCode.OK, retainedResponse.StatusCode);
            Assert.Equal(historyEpoch, body.GetProperty("historyEpoch").GetGuid());
            Assert.Equal(earliestAvailableHostUtc,
                body.GetProperty("earliestAvailableHostUtc").GetDateTimeOffset());
            Assert.Equal(retained.ProjectionCommitId,
                body.GetProperty("projectionCommit").GetProperty("projectionCommitId").GetString());
            Assert.Single(body.GetProperty("observations").EnumerateArray());
        }
    }

    [Fact]
    public void Packaged_scale_gate_requires_real_SQL_bounds_for_PollTrace_and_raw_evidence_reads()
    {
        var gatePath = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var gate = File.ReadAllText(gatePath);

        Assert.Contains("'PollTrace'", gate, StringComparison.Ordinal);
        Assert.Contains("PollTrace = 'POLL_TRACE'", gate, StringComparison.Ordinal);
        Assert.Contains("scope = 'PollTrace'", gate, StringComparison.Ordinal);
        Assert.Contains("/api/v2/poll-traces/scale-poll-", gate, StringComparison.Ordinal);
        Assert.Contains("scope = 'RawEvidence'", gate, StringComparison.Ordinal);
        Assert.Contains("responseBytes", gate, StringComparison.Ordinal);
        Assert.Contains("maxResponseBytes", gate, StringComparison.Ordinal);
        Assert.Contains("_RESPONSE_SIZE", gate, StringComparison.Ordinal);
        Assert.Contains("_SPILL", gate, StringComparison.Ordinal);
        Assert.Contains("_ABNORMAL_MEMORY_GRANT", gate, StringComparison.Ordinal);
        Assert.Contains("_LOGICAL_READ_GROWTH", gate, StringComparison.Ordinal);
        Assert.Contains("_MEMORY_GRANT_GROWTH", gate, StringComparison.Ordinal);
        Assert.Contains("objectKeySeekComplete", gate, StringComparison.Ordinal);
        Assert.Contains("unrelatedHistoryScanCount", gate, StringComparison.Ordinal);
        Assert.Contains("_OBJECT_KEY_SEEK", gate, StringComparison.Ordinal);
        Assert.Contains("_UNRELATED_HISTORY_SCAN", gate, StringComparison.Ordinal);
        Assert.Contains("_EARLIEST_IDENTITY", gate, StringComparison.Ordinal);
        Assert.Contains("allowedRawObservationLogicalReads", gate, StringComparison.Ordinal);
    }

    private static MesTaskUnionObservation InvalidDuplicateObservation() =>
        new(
            "WIRE_TO_NITROGEN",
            "SL-TICKET11-DUPLICATE",
            "A01-01",
            Eqp: null,
            Step: "焊线2",
            MesSourceDate: null,
            Package: null,
            MesSourceDateRaw: "not-a-date");

    private static MesTaskUnionObservation ValidObservation(string sublot) =>
        new(
            "WIRE_TO_NITROGEN",
            sublot,
            "N3-3",
            "WB-03",
            "焊线2",
            new DateTimeOffset(2026, 7, 24, 9, 58, 0, TimeSpan.FromHours(8)),
            "QFN");

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket11-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static async Task InsertForeignCommitObservationAsync(
        string connectionString,
        string targetPollTraceId,
        string foreignProjectionCommitId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mesingest.DemandRawObservations
                (PollTraceId, Ordinal, ProjectionCommitId, SeriesId, DemandId,
                 WorkType, Sublot, Area, Eqp, Step, MesSourceDate, Package, MesSourceDateRaw)
            VALUES
                (@pollTraceId, 999, @projectionCommitId, NULL, NULL,
                 N'WIRE_TO_NITROGEN', N'SL-TICKET11-FOREIGN-COMMIT', N'N3-3',
                 N'WB-FOREIGN', N'焊线2', NULL, N'QFN-FOREIGN', NULL);
            """;
        command.Parameters.AddWithValue("@pollTraceId", targetPollTraceId);
        command.Parameters.AddWithValue("@projectionCommitId", foreignProjectionCommitId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExpireRawObservationsAsync(
        string connectionString,
        string pollTraceId,
        DateTimeOffset earliestAvailableHostUtc)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM mesingest.DemandRawObservations
            WHERE PollTraceId = @pollTraceId;

            UPDATE mesingest.SchemaInfo
            SET EarliestAvailableHostUtc = @earliestAvailableHostUtc
            WHERE Id = 1;
            """;
        command.Parameters.AddWithValue("@pollTraceId", pollTraceId);
        command.Parameters.AddWithValue(
            "@earliestAvailableHostUtc",
            earliestAvailableHostUtc);
        Assert.True(await command.ExecuteNonQueryAsync() > 0);
    }

    private static async Task<Guid> ReadHistoryEpochAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT HistoryEpoch FROM mesingest.SchemaInfo WHERE Id = 1;";
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private WebApplicationFactory<Program> CreateFactory(AdjustableTimeProvider clock) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
        });
}
