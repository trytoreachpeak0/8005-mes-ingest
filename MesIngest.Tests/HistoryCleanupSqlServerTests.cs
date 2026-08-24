using System.Net;
using System.Diagnostics;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.Infrastructure.SqlServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class HistoryCleanupSqlServerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HistoryCleanupSqlServerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Ticket01SqlServerFact]
    public async Task Cleanup_failure_is_current_attention_then_the_next_hour_recovers_and_clears_it()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var now = new DateTimeOffset(2026, 8, 24, 5, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        FailOnceHistoryCleanupOperations? failOnce = null;
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.RemoveAll<IHistoryCleanupOperations>();
                services.AddSingleton<IHistoryCleanupOperations>(sp =>
                {
                    failOnce = new FailOnceHistoryCleanupOperations(
                        sp.GetRequiredService<SqlServerMesIngestProjection>());
                    return failOnce;
                });
            });
        });
        using var client = factory.CreateClient();
        Assert.Single(factory.Services.GetServices<IHostedService>()
            .OfType<HistoryCleanupHostedService>());
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        await ingestor.IngestAsync(SuccessRound("poll-ticket16-attention-baseline", now));
        var runner = factory.Services.GetRequiredService<IHistoryCleanupBatchRunner>();

        await runner.RunBatchAsync(now.AddHours(1), CancellationToken.None);

        var failed = await GetJsonAsync(client, "/api/v2/current-ingest-attention");
        var failedStatus = failed.GetProperty("historyCleanup");
        Assert.Equal(HistoryCleanupRunStatuses.Failed, failedStatus.GetProperty("status").GetString());
        Assert.Equal(
            HistoryCleanupFailureCodes.BatchFailed,
            failedStatus.GetProperty("lastFailureCode").GetString());
        Assert.Equal("InvalidOperationException", failedStatus.GetProperty("lastFailureReason").GetString());
        Assert.Equal(now.AddHours(2), failedStatus.GetProperty("nextCheckAt").GetDateTimeOffset());
        var cleanupAttention = Assert.Single(
            failed.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("kind").GetString()
                == CurrentIngestAttentionKinds.HistoryCleanupFailure);
        Assert.Equal(CurrentIngestAttentionSeverities.Error, cleanupAttention.GetProperty("severity").GetString());
        Assert.Equal("HISTORY_CLEANUP", cleanupAttention.GetProperty("subjectKind").GetString());
        Assert.DoesNotContain("secret detail", failed.GetRawText(), StringComparison.Ordinal);
        using (await factory.Services.GetRequiredService<IngestWorkPriorityGate>()
                   .EnterPollAsync(CancellationToken.None))
        {
            var continued = await ingestor.IngestAsync(
                SuccessRound("poll-ticket16-after-cleanup-failure", now.AddHours(1).AddMinutes(1)));
            Assert.Equal(MesTaskUnionRoundOutcome.Success, continued.Outcome);
            Assert.NotNull(continued.ProjectionCommitId);
        }

        Assert.NotNull(failOnce);
        clock.SetUtcNow(now.AddHours(2));
        await runner.RunBatchAsync(now.AddHours(2), CancellationToken.None);

        var recovered = await GetJsonAsync(client, "/api/v2/current-ingest-attention");
        var recoveredStatus = recovered.GetProperty("historyCleanup");
        Assert.Equal(HistoryCleanupRunStatuses.Succeeded, recoveredStatus.GetProperty("status").GetString());
        Assert.Equal(now.AddHours(2), recoveredStatus.GetProperty("lastSuccessfulAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, recoveredStatus.GetProperty("lastFailureCode").ValueKind);
        Assert.DoesNotContain(
            recovered.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("kind").GetString()
                == CurrentIngestAttentionKinds.HistoryCleanupFailure);
    }

    [Ticket01SqlServerFact]
    public async Task Normal_hourly_batch_cleans_minimum_due_history_and_publishes_complete_progress()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var firstSeenAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimerTimeProvider(firstSeenAt);
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var projection = factory.Services.GetRequiredService<IMesIngestProjection>();
        await SeedMinimumArchivedSeriesAsync(ingestor, projection, firstSeenAt);
        var cleanupAt = firstSeenAt
            .AddMinutes(2)
            .Add(DemandSeriesArchivePolicy.MinimumGoneDuration)
            .Add(HistoryRetentionPolicy.RetentionEligibleDemandSeriesWindow);

        clock.Advance(cleanupAt - firstSeenAt);
        var operations = factory.Services.GetRequiredService<IHistoryCleanupOperations>();
        var completed = await WaitForCleanupAsync(
            operations,
            state => state.LastSuccessfulAt == cleanupAt);

        Assert.Equal(HistoryCleanupRunStatuses.Succeeded, completed.Status);
        Assert.Equal(4, completed.LastExpiredPollTraceCount);
        Assert.Equal(2, completed.LastDeletedRawObservationCount);
        Assert.Equal(1, completed.LastDeletedSeriesCount);
        Assert.Equal(4, completed.TotalExpiredPollTraceCount);
        Assert.Equal(2, completed.TotalDeletedRawObservationCount);
        Assert.Equal(1, completed.TotalDeletedSeriesCount);
        Assert.Equal(
            cleanupAt.Subtract(HistoryRetentionPolicy.RawObservationAvailabilityWindow),
            completed.EarliestAvailableHostUtc);
        Assert.Equal(cleanupAt.AddHours(1), completed.NextCheckAt);
        Assert.Equal(1, await CountAsync(
            database.ConnectionString,
            "SELECT COUNT(*) FROM mesingest.ArchivedDemandKeyTombstones;"));
        Assert.Equal(0, await CountAsync(
            database.ConnectionString,
            "SELECT COUNT(*) FROM mesingest.DemandRawObservations;"));

        var response = await GetJsonAsync(client, "/api/v2/current-ingest-attention");
        Assert.Equal(
            cleanupAt,
            response.GetProperty("historyCleanup").GetProperty("lastSuccessfulAt").GetDateTimeOffset());
        Assert.DoesNotContain(
            response.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("kind").GetString()
                == CurrentIngestAttentionKinds.HistoryCleanupFailure);

        clock.Advance(TimeSpan.FromHours(1));
        var repeated = await WaitForCleanupAsync(
            operations,
            state => state.LastStartedAt == cleanupAt.AddHours(1));
        Assert.Equal(0, repeated.LastExpiredPollTraceCount);
        Assert.Equal(0, repeated.LastDeletedRawObservationCount);
        Assert.Equal(0, repeated.LastDeletedSeriesCount);
        Assert.Equal(completed.TotalExpiredPollTraceCount, repeated.TotalExpiredPollTraceCount);
        Assert.Equal(completed.TotalDeletedRawObservationCount, repeated.TotalDeletedRawObservationCount);
        Assert.Equal(completed.TotalDeletedSeriesCount, repeated.TotalDeletedSeriesCount);
    }

    [Ticket01SqlServerFact]
    public async Task Real_sql_baseline_cleans_the_default_25_whole_series_inside_the_15_second_budget()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var firstSeenAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(firstSeenAt);
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var projection = factory.Services.GetRequiredService<IMesIngestProjection>();
        await SeedArchivedSeriesBatchAsync(ingestor, projection, firstSeenAt, seriesCount: 25);
        var cleanupAt = firstSeenAt
            .AddMinutes(2)
            .Add(DemandSeriesArchivePolicy.MinimumGoneDuration)
            .Add(HistoryRetentionPolicy.RetentionEligibleDemandSeriesWindow);
        clock.SetUtcNow(cleanupAt);

        var stopwatch = Stopwatch.StartNew();
        await factory.Services.GetRequiredService<IHistoryCleanupBatchRunner>()
            .RunBatchAsync(cleanupAt, CancellationToken.None);
        stopwatch.Stop();
        var state = await factory.Services.GetRequiredService<IHistoryCleanupOperations>()
            .ReadHistoryCleanupStateAsync();

        Assert.Equal(HistoryCleanupRunStatuses.BudgetExhausted, state.Status);
        Assert.Equal(25, state.LastDeletedSeriesCount);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"The real-SQL baseline took {stopwatch.Elapsed}.");
    }

    private static async Task SeedMinimumArchivedSeriesAsync(
        RoundIngestor ingestor,
        IMesIngestProjection projection,
        DateTimeOffset firstSeenAt)
    {
        await ingestor.IngestAsync(SuccessRoundWithObservations(
            "poll-ticket16-invalid",
            firstSeenAt,
            new MesTaskUnionObservation(
                "WIRE_TO_NITROGEN", "SL-TICKET16-NORMAL", "N3-3", null,
                "焊线2", null, null, "not-a-date")));
        await ingestor.IngestAsync(SuccessRoundWithObservations(
            "poll-ticket16-valid",
            firstSeenAt.AddMinutes(1),
            new MesTaskUnionObservation(
                "WIRE_TO_NITROGEN", "SL-TICKET16-NORMAL", "N3-3", "WB-03",
                "焊线2", firstSeenAt, "QFN")));
        await ingestor.IngestAsync(SuccessRoundWithObservations(
            "poll-ticket16-gone",
            firstSeenAt.AddMinutes(2)));
        var archivedAt = firstSeenAt
            .AddMinutes(2)
            .Add(DemandSeriesArchivePolicy.MinimumGoneDuration);
        await ingestor.IngestAsync(SuccessRoundWithObservations(
            "poll-ticket16-archived",
            archivedAt));
        var series = await projection.GetDemandSeriesByKeyAsync(
            "WIRE_TO_NITROGEN",
            "SL-TICKET16-NORMAL");
        Assert.NotNull(series);
        Assert.Equal(DemandSeriesLifecycleContract.Archived, series.Lifecycle);
    }

    private static async Task SeedArchivedSeriesBatchAsync(
        RoundIngestor ingestor,
        IMesIngestProjection projection,
        DateTimeOffset firstSeenAt,
        int seriesCount)
    {
        var invalid = Enumerable.Range(0, seriesCount)
            .Select(index => new MesTaskUnionObservation(
                $"WIRE_TO_NITROGEN_{index:D2}", $"SL-TICKET16-BASELINE-{index:D2}", "N3-3", null,
                "焊线2", null, null, "not-a-date"))
            .ToArray();
        var valid = Enumerable.Range(0, seriesCount)
            .Select(index => new MesTaskUnionObservation(
                $"WIRE_TO_NITROGEN_{index:D2}", $"SL-TICKET16-BASELINE-{index:D2}", "N3-3", $"WB-{index:D2}",
                "焊线2", firstSeenAt, "QFN"))
            .ToArray();
        await ingestor.IngestAsync(SuccessRoundWithObservations(
            "poll-ticket16-baseline-invalid", firstSeenAt, invalid));
        await ingestor.IngestAsync(SuccessRoundWithObservations(
            "poll-ticket16-baseline-valid", firstSeenAt.AddMinutes(1), valid));
        await ingestor.IngestAsync(SuccessRoundWithObservations(
            "poll-ticket16-baseline-gone", firstSeenAt.AddMinutes(2)));
        var archivedAt = firstSeenAt
            .AddMinutes(2)
            .Add(DemandSeriesArchivePolicy.MinimumGoneDuration);
        await ingestor.IngestAsync(SuccessRoundWithObservations(
            "poll-ticket16-baseline-archived", archivedAt));

        var sample = await projection.GetDemandSeriesByKeyAsync(
            "WIRE_TO_NITROGEN_00",
            "SL-TICKET16-BASELINE-00");
        Assert.NotNull(sample);
        Assert.Equal(DemandSeriesLifecycleContract.Archived, sample.Lifecycle);
    }

    private static MesTaskUnionRound SuccessRoundWithObservations(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) => new(
            pollTraceId,
            "mes-task-union-ticket16-cleanup-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-1),
            completedAt,
            observations);

    private static async Task<HistoryCleanupStateSnapshot> WaitForCleanupAsync(
        IHistoryCleanupOperations operations,
        Func<HistoryCleanupStateSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var state = await operations.ReadHistoryCleanupStateAsync(timeout.Token);
            if (predicate(state))
            {
                return state;
            }

            await Task.Delay(1, timeout.Token);
        }
    }

    private static async Task<int> CountAsync(string connectionString, string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static MesTaskUnionRound SuccessRound(string pollTraceId, DateTimeOffset completedAt) =>
        new(
            pollTraceId,
            "mes-task-union-ticket16-cleanup-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-1),
            completedAt,
            [new MesTaskUnionObservation(
                "WIRE_TO_NITROGEN",
                "SL-TICKET16-ATTENTION",
                "N3-3",
                "WB-03",
                "焊线2",
                completedAt,
                "QFN")]);

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__SnapshotSource"] = MesIngestHostOptions.NoRoundSource,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
        });

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private sealed class FailOnceHistoryCleanupOperations(
        SqlServerMesIngestProjection inner) : IHistoryCleanupOperations
    {
        private int _failuresRemaining = 1;

        public Task<HistoryCleanupStateSnapshot> BeginHistoryCleanupRunAsync(
            string runId,
            DateTimeOffset startedAt,
            DateTimeOffset nextCheckAt,
            CancellationToken cancellationToken = default) =>
            inner.BeginHistoryCleanupRunAsync(runId, startedAt, nextCheckAt, cancellationToken);

        public Task<HistoryRawCleanupBatchResult> AdvanceHistoryRetentionBatchAsync(
            string runId,
            int maximumRawObservationRows,
            int maximumPollTraces,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failuresRemaining, 0) == 1)
            {
                throw new InvalidOperationException("secret detail must not become attention");
            }

            return inner.AdvanceHistoryRetentionBatchAsync(
                runId,
                maximumRawObservationRows,
                maximumPollTraces,
                cancellationToken);
        }

        public Task<RetentionEligibleSeriesCleanupResult?> CleanupNextRetentionEligibleSeriesAsync(
            string runId,
            CancellationToken cancellationToken = default) =>
            inner.CleanupNextRetentionEligibleSeriesAsync(runId, cancellationToken);

        public Task<HistoryCleanupStateSnapshot> CompleteHistoryCleanupRunAsync(
            string runId,
            string status,
            DateTimeOffset completedAt,
            DateTimeOffset nextCheckAt,
            CancellationToken cancellationToken = default) =>
            inner.CompleteHistoryCleanupRunAsync(
                runId,
                status,
                completedAt,
                nextCheckAt,
                cancellationToken);

        public Task<HistoryCleanupStateSnapshot> FailHistoryCleanupRunAsync(
            string runId,
            DateTimeOffset failedAt,
            DateTimeOffset nextCheckAt,
            string failureCode,
            string failureReason,
            CancellationToken cancellationToken = default) =>
            inner.FailHistoryCleanupRunAsync(
                runId,
                failedAt,
                nextCheckAt,
                failureCode,
                failureReason,
                cancellationToken);

        public Task<HistoryCleanupStateSnapshot> ReadHistoryCleanupStateAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadHistoryCleanupStateAsync(cancellationToken);
    }
}
