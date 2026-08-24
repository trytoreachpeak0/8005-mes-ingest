using System.Net;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.Infrastructure.SqlServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class ArchivedDemandKeyTombstoneTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string WorkType = "WIRE_TO_NITROGEN";
    private const string Sublot = "SL-TICKET15-TOMBSTONE";
    private readonly WebApplicationFactory<Program> _factory;

    public ArchivedDemandKeyTombstoneTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Ticket01SqlServerFact]
    public async Task Series_cleanup_failpoints_roll_back_then_retry_writes_one_minimal_tombstone_and_deletes_graph_once()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var firstSeenAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(firstSeenAt);
        var observer = new SeriesCleanupFailpointObserver();
        await using var factory = CreateFactory(clock, observer);
        var projection = factory.Services.GetRequiredService<IMesIngestProjection>();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var seriesId = await SeedMinimumCompleteArchivedGraphAsync(ingestor, projection, firstSeenAt);
        var completeGraph = await ReadSeriesGraphCountsAsync(database.ConnectionString, seriesId);

        Assert.True(completeGraph.DemandCount >= 1);
        Assert.True(completeGraph.EventCount >= 1);
        Assert.True(completeGraph.ErrorPeriodCount >= 1);
        Assert.True(completeGraph.ErrorEvidenceCount >= 1);
        Assert.True(completeGraph.RawObservationCount >= 1);
        Assert.Equal(0, completeGraph.CurrentConditionCount);
        Assert.Equal(0, completeGraph.CatalogItemCount);

        clock.SetUtcNow(firstSeenAt
            .AddMinutes(2)
            .Add(DemandSeriesArchivePolicy.MinimumGoneDuration)
            .Add(HistoryRetentionPolicy.RetentionEligibleDemandSeriesWindow));
        Assert.Null(await projection.CleanupNextRetentionEligibleSeriesAsync());
        var rawRetention = await projection.AdvanceHistoryRetentionAsync();
        Assert.True(rawRetention.DeletedRawObservationCount >= completeGraph.RawObservationCount);
        var before = await ReadSeriesGraphCountsAsync(database.ConnectionString, seriesId);
        Assert.Equal(completeGraph with { RawObservationCount = 0 }, before);

        foreach (var checkpoint in SeriesCleanupCheckpointContract.TransactionFailpoints)
        {
            observer.Arm(checkpoint);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => projection.CleanupNextRetentionEligibleSeriesAsync());

            Assert.Contains(checkpoint.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.Equal(before, await ReadSeriesGraphCountsAsync(database.ConnectionString, seriesId));
            Assert.Equal(0, await CountTombstonesAsync(database.ConnectionString));
        }

        var cleaned = await projection.CleanupNextRetentionEligibleSeriesAsync();

        Assert.NotNull(cleaned);
        Assert.Equal(seriesId, cleaned.SeriesId);
        Assert.Equal(WorkType, cleaned.WorkType);
        Assert.Equal(Sublot, cleaned.Sublot);
        Assert.Equal(1, cleaned.TombstoneVersion);
        Assert.Equal(1, await CountTombstonesAsync(database.ConnectionString));
        Assert.Equal(
            new TombstoneFacts(
                WorkType,
                Sublot,
                seriesId,
                cleaned.ArchivedAt,
                "ARCHIVED",
                1),
            await ReadTombstoneFactsAsync(database.ConnectionString));
        Assert.Equal(SeriesGraphCounts.Empty, await ReadSeriesGraphCountsAsync(
            database.ConnectionString,
            seriesId));
        Assert.Null(await projection.CleanupNextRetentionEligibleSeriesAsync());
        Assert.Equal(1, await CountTombstonesAsync(database.ConnectionString));
        Assert.Equal(
            new[]
            {
                "KeyToken:char(64):required",
                "WorkType:nvarchar(128):required",
                "Sublot:nvarchar(256):required",
                "OriginalSeriesId:nvarchar(64):required",
                "ArchivedAt:datetimeoffset:required",
                "ArchiveConclusion:nvarchar(32):required",
                "TombstoneVersion:int:required",
            },
            await ReadTombstoneColumnContractAsync(database.ConnectionString));
    }

    [Ticket01SqlServerFact]
    public async Task Tombstoned_key_reappears_under_original_series_after_restart_and_never_enters_catalog()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var firstSeenAt = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(firstSeenAt);
        string originalSeriesId;
        await using (var factory = CreateFactory(clock))
        {
            var projection = factory.Services.GetRequiredService<IMesIngestProjection>();
            var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
            originalSeriesId = await SeedMinimumCompleteArchivedGraphAsync(
                ingestor,
                projection,
                firstSeenAt);
            clock.SetUtcNow(firstSeenAt
                .AddMinutes(2)
                .Add(DemandSeriesArchivePolicy.MinimumGoneDuration)
                .Add(HistoryRetentionPolicy.RetentionEligibleDemandSeriesWindow));
            await projection.AdvanceHistoryRetentionAsync();
            Assert.NotNull(await projection.CleanupNextRetentionEligibleSeriesAsync());
        }

        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(1));
        await using var restartedFactory = CreateFactory(clock);
        using var client = restartedFactory.CreateClient();
        var restartedIngestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
        await restartedIngestor.IngestAsync(SuccessRound(
            "poll-ticket15-reappeared",
            clock.GetUtcNow(),
            ValidObservation()));

        using var response = await client.GetAsync(
            "/api/v2/demand-series/by-key"
            + $"?workType={Uri.EscapeDataString(WorkType)}"
            + $"&sublot={Uri.EscapeDataString(Sublot)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(originalSeriesId, body.GetProperty("seriesId").GetString());
        Assert.Equal("ARCHIVED", body.GetProperty("lifecycle").GetString());
        Assert.Equal("LONG_GONE_BUT_VISIBLE", body.GetProperty("currentPresence").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("startedAt").ValueKind);
        Assert.Equal(
            "LONG_GONE_BUT_VISIBLE",
            body.GetProperty("currentDemand").GetProperty("status").GetString());
        Assert.Contains(
            body.GetProperty("currentConditions").EnumerateArray(),
            condition => condition.GetProperty("code").GetString() == "LONG_GONE_BUT_VISIBLE");
        var rebuiltEvents = await ReadSeriesEventTypesAsync(
            database.ConnectionString,
            originalSeriesId);
        Assert.Contains(ArchivedDemandKeyTombstoneContract.ReappearedEvent, rebuiltEvents);
        Assert.DoesNotContain(DemandSeriesLifecycleContract.GoneTimeoutArchivedEvent, rebuiltEvents);

        using var listResponse = await client.GetAsync(
            "/api/v2/demand-series?pageSize=100&page=1&workType="
            + Uri.EscapeDataString(WorkType));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await ReadJsonAsync(listResponse);
        var listItem = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, listItem.GetProperty("startedAt").ValueKind);
        var snapshotReference = list.GetProperty("snapshotReference").GetString()!;

        using var frozenResponse = await client.GetAsync(
            $"/api/v2/demand-series/{Uri.EscapeDataString(originalSeriesId)}"
            + $"?snapshot={Uri.EscapeDataString(snapshotReference)}");
        Assert.Equal(HttpStatusCode.OK, frozenResponse.StatusCode);
        var frozen = await ReadJsonAsync(frozenResponse);
        Assert.Equal("ARCHIVED", frozen.GetProperty("lifecycle").GetString());
        Assert.Equal(
            "LONG_GONE_BUT_VISIBLE",
            frozen.GetProperty("currentPresence").GetString());
        Assert.Equal(JsonValueKind.Null, frozen.GetProperty("startedAt").ValueKind);
        Assert.Equal(
            await ReadTombstoneArchivedAtAsync(database.ConnectionString),
            frozen.GetProperty("archivedAt").GetDateTimeOffset());
        Assert.Contains(
            frozen.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("eventType").GetString()
                == ArchivedDemandKeyTombstoneContract.ReappearedEvent);

        var catalog = await restartedFactory.Services
            .GetRequiredService<IMesIngestProjection>()
            .ReadExternallyReadableDemandCatalogAsync();
        Assert.NotNull(catalog.Snapshot);
        Assert.Empty(catalog.Snapshot.Items);
        Assert.Equal(1, await CountTombstonesAsync(database.ConnectionString));
    }

    private WebApplicationFactory<Program> CreateFactory(
        AdjustableTimeProvider clock,
        IProjectionCommitCheckpointObserver? checkpointObserver = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                if (checkpointObserver is not null)
                {
                    services.RemoveAll<IProjectionCommitCheckpointObserver>();
                    services.AddSingleton(checkpointObserver);
                }
            });
        });

    private static async Task<string> SeedMinimumCompleteArchivedGraphAsync(
        RoundIngestor ingestor,
        IMesIngestProjection projection,
        DateTimeOffset firstSeenAt)
    {
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket15-invalid",
            firstSeenAt,
            InvalidObservation()));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket15-valid",
            firstSeenAt.AddMinutes(1),
            ValidObservation()));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket15-gone",
            firstSeenAt.AddMinutes(2)));
        var archivedAt = firstSeenAt
            .AddMinutes(2)
            .Add(DemandSeriesArchivePolicy.MinimumGoneDuration);
        await ingestor.IngestAsync(SuccessRound("poll-ticket15-archived", archivedAt));

        var series = await projection.GetDemandSeriesByKeyAsync(WorkType, Sublot);
        Assert.NotNull(series);
        Assert.Equal("ARCHIVED", series.Lifecycle);
        Assert.Equal("GONE", series.CurrentPresence);
        return series.SeriesId;
    }

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket15-tombstone-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-1),
            completedAt,
            observations);

    private static MesTaskUnionObservation ValidObservation() =>
        new(WorkType, Sublot, "N3-3", "WB-03", "焊线2", new DateTimeOffset(
            2026, 8, 1, 1, 0, 0, TimeSpan.Zero), "QFN");

    private static MesTaskUnionObservation InvalidObservation() =>
        new(WorkType, Sublot, "N3-3", null, "焊线2", null, null, "not-a-date");

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
        });

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private static async Task<int> CountTombstonesAsync(string connectionString) =>
        Convert.ToInt32(await ScalarAsync(
            connectionString,
            "SELECT COUNT(*) FROM mesingest.ArchivedDemandKeyTombstones;"));

    private static async Task<IReadOnlyList<string>> ReadSeriesEventTypesAsync(
        string connectionString,
        string seriesId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventType
            FROM mesingest.DemandSeriesEvents
            WHERE SeriesId = @seriesId
            ORDER BY SeriesSequence;
            """;
        command.Parameters.AddWithValue("@seriesId", seriesId);
        var eventTypes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            eventTypes.Add(reader.GetString(0));
        }
        return eventTypes;
    }

    private static async Task<DateTimeOffset> ReadTombstoneArchivedAtAsync(
        string connectionString) =>
        (DateTimeOffset)(await ScalarAsync(
            connectionString,
            "SELECT ArchivedAt FROM mesingest.ArchivedDemandKeyTombstones;"))!;

    private static async Task<TombstoneFacts> ReadTombstoneFactsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT WorkType, Sublot, OriginalSeriesId, ArchivedAt,
                   ArchiveConclusion, TombstoneVersion
            FROM mesingest.ArchivedDemandKeyTombstones;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new TombstoneFacts(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetString(4),
            reader.GetInt32(5));
        Assert.False(await reader.ReadAsync());
        return result;
    }

    private static async Task<SeriesGraphCounts> ReadSeriesGraphCountsAsync(
        string connectionString,
        string seriesId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM mesingest.DemandSeries WHERE SeriesId = @seriesId),
                (SELECT COUNT(*) FROM mesingest.TransportDemands WHERE SeriesId = @seriesId),
                (SELECT COUNT(*) FROM mesingest.DemandSeriesEvents WHERE SeriesId = @seriesId),
                (SELECT COUNT(*) FROM mesingest.DemandSeriesErrorPeriods WHERE SeriesId = @seriesId),
                (SELECT COUNT(*)
                 FROM mesingest.SeriesErrorPeriodEvidence AS evidence
                 INNER JOIN mesingest.DemandSeriesErrorPeriods AS period
                    ON period.PeriodId = evidence.PeriodId
                 WHERE period.SeriesId = @seriesId),
                (SELECT COUNT(*) FROM mesingest.DemandSeriesCurrentConditions WHERE SeriesId = @seriesId),
                (SELECT COUNT(*) FROM mesingest.DemandRawObservations WHERE SeriesId = @seriesId),
                (SELECT COUNT(*) FROM mesingest.CatalogItems WHERE SeriesId = @seriesId);
            """;
        command.Parameters.AddWithValue("@seriesId", seriesId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new SeriesGraphCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7));
    }

    private static async Task<IReadOnlyList<string>> ReadTombstoneColumnContractAsync(
        string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONCAT(
                columnRow.name, N':', typeRow.name,
                CASE
                    WHEN typeRow.name IN (N'char', N'nvarchar')
                    THEN CONCAT(N'(', columnRow.max_length /
                        CASE WHEN typeRow.name = N'nvarchar' THEN 2 ELSE 1 END, N')')
                    ELSE N''
                END,
                N':', CASE WHEN columnRow.is_nullable = 0 THEN N'required' ELSE N'optional' END)
            FROM sys.columns AS columnRow
            INNER JOIN sys.types AS typeRow ON typeRow.user_type_id = columnRow.user_type_id
            WHERE columnRow.object_id = OBJECT_ID(N'mesingest.ArchivedDemandKeyTombstones')
            ORDER BY columnRow.column_id;
            """;
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }
        return columns;
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private sealed record SeriesGraphCounts(
        int SeriesCount,
        int DemandCount,
        int EventCount,
        int ErrorPeriodCount,
        int ErrorEvidenceCount,
        int CurrentConditionCount,
        int RawObservationCount,
        int CatalogItemCount)
    {
        public static SeriesGraphCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed record TombstoneFacts(
        string WorkType,
        string Sublot,
        string OriginalSeriesId,
        DateTimeOffset ArchivedAt,
        string ArchiveConclusion,
        int TombstoneVersion);

    private sealed class SeriesCleanupFailpointObserver : IProjectionCommitCheckpointObserver
    {
        private SeriesCleanupCheckpoint? _armed;

        public void Arm(SeriesCleanupCheckpoint checkpoint)
        {
            Assert.Null(_armed);
            _armed = checkpoint;
        }

        public Task OnCheckpointAsync(
            ProjectionCommitCheckpoint checkpoint,
            ProjectionCommitCheckpointContext context,
            SqlConnection connection,
            SqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task OnSeriesCleanupCheckpointAsync(
            SeriesCleanupCheckpoint checkpoint,
            ProjectionCommitCheckpointContext context,
            SqlConnection connection,
            SqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            if (checkpoint != _armed)
            {
                return Task.CompletedTask;
            }

            _armed = null;
            throw new InvalidOperationException($"Controlled failure at {checkpoint}.");
        }
    }
}
