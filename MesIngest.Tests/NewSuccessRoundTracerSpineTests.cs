using System.Net.Http.Json;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class NewSuccessRoundTracerSpineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public NewSuccessRoundTracerSpineTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Ticket01SqlServerFact]
    public async Task First_success_round_is_read_back_with_atomic_series_demand_and_round_evidence()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var csvPath = await WriteEmptyLegacyCsvAsync();
        var startedAt = new DateTimeOffset(2026, 8, 12, 1, 2, 3, TimeSpan.Zero);
        var completedAt = startedAt.AddSeconds(2);
        var mesSourceDate = new DateTimeOffset(2026, 8, 12, 8, 58, 0, TimeSpan.FromHours(8));
        const string pollTraceId = "poll-ticket01-first";
        const string queryVersion = "mes-task-union-test-v1";

        try
        {
            using (ConfigureProductionWithoutV2Environment())
            await using (var unconfiguredFactory = CreateFactory())
            {
                var exception = Assert.Throws<InvalidOperationException>(
                    unconfiguredFactory.CreateClient);
                Assert.Contains(
                    "NewSqlServerConnectionString is required",
                    exception.ToString(),
                    StringComparison.Ordinal);
            }

            using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
            await using var factory = CreateFactory();
            var client = factory.CreateClient();
            var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
            Assert.Equal(
                Environments.Production,
                factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);

            var receipt = await ingestor.IngestAsync(
                new MesTaskUnionRound(
                    PollTraceId: pollTraceId,
                    QueryVersion: queryVersion,
                    Outcome: MesTaskUnionRoundOutcome.Success,
                    StartedAt: startedAt,
                    CompletedAt: completedAt,
                    Observations:
                    [
                        new MesTaskUnionObservation(
                            WorkType: "WIRE_TO_NITROGEN",
                            Sublot: "SL-TICKET01-001",
                            Area: "N3-3",
                            Eqp: "WB-03",
                            Step: "焊线2",
                            MesSourceDate: mesSourceDate,
                            Package: "QFN"),
                    ]));

            var contract = await client.GetFromJsonAsync<JsonElement>("/api/v2/contract");
            Assert.Equal(NewMesIngestContract.Version, contract.GetProperty("contractVersion").GetString());
            Assert.Equal(NewMesIngestContract.SchemaVersion, contract.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(NewMesIngestContract.KeyComparison, contract.GetProperty("transportDemandKeyComparison").GetString());
            using var legacyContract = await client.GetAsync("/api/contract");
            Assert.Equal(HttpStatusCode.NotFound, legacyContract.StatusCode);
            using var legacyOpenApi = await client.GetAsync("/openapi/v1.json");
            Assert.Equal(HttpStatusCode.NotFound, legacyOpenApi.StatusCode);
            Assert.Null(factory.Services.GetService<MesIngest.Core.ITransportDemandStore>());

            var series = await client.GetFromJsonAsync<JsonElement>(
                "/api/v2/demand-series/by-key?workType=WIRE_TO_NITROGEN&sublot=SL-TICKET01-001");
            Assert.Equal(receipt.SeriesIds.Single(), series.GetProperty("seriesId").GetString());
            Assert.Equal("WIRE_TO_NITROGEN", series.GetProperty("workType").GetString());
            Assert.Equal("SL-TICKET01-001", series.GetProperty("sublot").GetString());
            Assert.Equal("TRACKING", series.GetProperty("lifecycle").GetString());
            Assert.Equal("VISIBLE", series.GetProperty("currentPresence").GetString());
            Assert.Equal(pollTraceId, series.GetProperty("createdPollTraceId").GetString());
            Assert.Equal(receipt.ProjectionCommitId, series.GetProperty("createdProjectionCommitId").GetString());
            Assert.Equal(receipt.ProjectionCommitId, series.GetProperty("latestProjectionCommitId").GetString());

            using var caseVariant = await client.GetAsync(
                "/api/v2/demand-series/by-key?workType=wire_to_nitrogen&sublot=SL-TICKET01-001");
            Assert.Equal(HttpStatusCode.NotFound, caseVariant.StatusCode);
            using var whitespaceVariant = await client.GetAsync(
                "/api/v2/demand-series/by-key?workType=%20WIRE_TO_NITROGEN%20&sublot=SL-TICKET01-001");
            Assert.Equal(HttpStatusCode.NotFound, whitespaceVariant.StatusCode);
            using var invalidKey = await client.GetAsync(
                "/api/v2/demand-series/by-key?workType=%20&sublot=SL-TICKET01-001");
            Assert.Equal(HttpStatusCode.BadRequest, invalidKey.StatusCode);

            var demand = series.GetProperty("currentDemand");
            Assert.Equal(receipt.DemandIds.Single(), demand.GetProperty("demandId").GetString());
            Assert.Equal(1, demand.GetProperty("generation").GetInt32());
            Assert.Equal("VISIBLE", demand.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, demand.GetProperty("predecessorDemandId").ValueKind);
            Assert.Equal(pollTraceId, demand.GetProperty("createdPollTraceId").GetString());
            Assert.Equal(receipt.ProjectionCommitId, demand.GetProperty("createdProjectionCommitId").GetString());
            Assert.Equal(receipt.ProjectionCommitId, demand.GetProperty("latestProjectionCommitId").GetString());

            var fields = demand.GetProperty("liveMesFields");
            Assert.Equal("N3-3", fields.GetProperty("area").GetString());
            Assert.Equal("WB-03", fields.GetProperty("eqp").GetString());
            Assert.Equal("焊线2", fields.GetProperty("step").GetString());
            Assert.Equal(mesSourceDate, fields.GetProperty("mesSourceDate").GetDateTimeOffset());
            Assert.Equal("QFN", fields.GetProperty("package").GetString());

            var observations = series.GetProperty("rawObservations");
            Assert.Equal(1, observations.GetArrayLength());
            Assert.Equal(0, observations[0].GetProperty("ordinal").GetInt32());
            Assert.Equal(pollTraceId, observations[0].GetProperty("pollTraceId").GetString());
            Assert.Equal(receipt.ProjectionCommitId, observations[0].GetProperty("projectionCommitId").GetString());
            Assert.Equal(receipt.SeriesIds.Single(), observations[0].GetProperty("seriesId").GetString());
            Assert.Equal(receipt.DemandIds.Single(), observations[0].GetProperty("demandId").GetString());
            Assert.Equal("WIRE_TO_NITROGEN", observations[0].GetProperty("workType").GetString());
            Assert.Equal("SL-TICKET01-001", observations[0].GetProperty("sublot").GetString());
            Assert.Equal("N3-3", observations[0].GetProperty("area").GetString());
            Assert.Equal("WB-03", observations[0].GetProperty("eqp").GetString());
            Assert.Equal("焊线2", observations[0].GetProperty("step").GetString());
            Assert.Equal(mesSourceDate, observations[0].GetProperty("mesSourceDate").GetDateTimeOffset());
            Assert.Equal("QFN", observations[0].GetProperty("package").GetString());

            var events = series.GetProperty("events");
            Assert.True(events.GetArrayLength() >= 2);
            Assert.Equal(
                Enumerable.Range(1, events.GetArrayLength()).Select(value => (long)value),
                events.EnumerateArray().Select(item => item.GetProperty("seriesSequence").GetInt64()));
            Assert.All(events.EnumerateArray(), item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("eventType").GetString()));
                Assert.Equal(pollTraceId, item.GetProperty("pollTraceId").GetString());
                Assert.Equal(receipt.ProjectionCommitId, item.GetProperty("projectionCommitId").GetString());
                Assert.True(item.GetProperty("payloadVersion").GetInt32() >= 1);
            });
            Assert.Contains(
                events.EnumerateArray(),
                item => item.GetProperty("subjectKind").GetString() == "SERIES"
                    && item.GetProperty("subjectId").GetString() == receipt.SeriesIds.Single());
            Assert.Contains(
                events.EnumerateArray(),
                item => item.GetProperty("subjectKind").GetString() == "DEMAND"
                    && item.GetProperty("subjectId").GetString() == receipt.DemandIds.Single());

            var trace = await client.GetFromJsonAsync<JsonElement>(
                $"/api/v2/poll-traces/{Uri.EscapeDataString(pollTraceId)}");
            Assert.Equal(pollTraceId, trace.GetProperty("pollTraceId").GetString());
            Assert.Equal(queryVersion, trace.GetProperty("queryVersion").GetString());
            Assert.Equal("SUCCESS", trace.GetProperty("outcome").GetString());
            Assert.Equal(1, trace.GetProperty("rowCount").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(trace.GetProperty("contentDigest").GetString()));
            Assert.Equal(receipt.ProjectionCommitId, trace.GetProperty("projectionCommit").GetProperty("projectionCommitId").GetString());
            Assert.Equal(receipt.ProjectionCommitId, trace.GetProperty("observations")[0].GetProperty("projectionCommitId").GetString());

            var seriesById = await client.GetFromJsonAsync<JsonElement>(
                $"/api/v2/demand-series/{Uri.EscapeDataString(receipt.SeriesIds.Single())}");
            Assert.Equal(series.GetProperty("seriesId").GetString(), seriesById.GetProperty("seriesId").GetString());
            Assert.Equal(
                demand.GetProperty("demandId").GetString(),
                seriesById.GetProperty("currentDemand").GetProperty("demandId").GetString());

            AssertDatabaseEvidence(database);
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Ticket01SqlServerFact]
    public async Task Equivalent_success_round_preserves_identity_and_does_not_append_business_events()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var csvPath = await WriteEmptyLegacyCsvAsync();
        var firstStartedAt = new DateTimeOffset(2026, 8, 12, 2, 0, 0, TimeSpan.Zero);
        var firstCompletedAt = firstStartedAt.AddSeconds(2);
        var secondStartedAt = firstStartedAt.AddMinutes(1);
        var secondCompletedAt = secondStartedAt.AddSeconds(2);

        try
        {
            using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
            await using var factory = CreateFactory();
            var client = factory.CreateClient();
            var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

            var firstReceipt = await ingestor.IngestAsync(CreateRound(
                "poll-ticket01-equivalent-1",
                firstStartedAt,
                firstCompletedAt));
            var before = await client.GetFromJsonAsync<JsonElement>(SeriesByKeyUri);
            var beforeEventIds = before.GetProperty("events")
                .EnumerateArray()
                .Select(item => item.GetProperty("eventId").GetString())
                .ToArray();

            var secondRound = CreateRound(
                "poll-ticket01-equivalent-2",
                secondStartedAt,
                secondCompletedAt);
            secondRound = secondRound with
            {
                Observations =
                [
                    secondRound.Observations[0] with
                    {
                        MesSourceDate = secondRound.Observations[0].MesSourceDate!.Value.ToUniversalTime(),
                    },
                ],
            };
            var secondReceipt = await ingestor.IngestAsync(secondRound);
            var after = await client.GetFromJsonAsync<JsonElement>(SeriesByKeyUri);

            Assert.Equal(firstReceipt.SeriesIds, secondReceipt.SeriesIds);
            Assert.Equal(firstReceipt.DemandIds, secondReceipt.DemandIds);
            Assert.NotEqual(firstReceipt.ProjectionCommitId, secondReceipt.ProjectionCommitId);
            Assert.Equal(before.GetProperty("seriesId").GetString(), after.GetProperty("seriesId").GetString());
            Assert.Equal(
                before.GetProperty("currentDemand").GetProperty("demandId").GetString(),
                after.GetProperty("currentDemand").GetProperty("demandId").GetString());
            Assert.Equal(1, after.GetProperty("currentDemand").GetProperty("generation").GetInt32());
            Assert.Equal(
                before.GetProperty("currentDemand").GetProperty("liveMesFields").GetRawText(),
                after.GetProperty("currentDemand").GetProperty("liveMesFields").GetRawText());
            Assert.Equal(firstReceipt.ProjectionCommitId, after.GetProperty("createdProjectionCommitId").GetString());
            Assert.Equal(secondReceipt.ProjectionCommitId, after.GetProperty("latestProjectionCommitId").GetString());
            Assert.Equal(
                secondReceipt.ProjectionCommitId,
                after.GetProperty("currentDemand").GetProperty("latestProjectionCommitId").GetString());

            var afterEventIds = after.GetProperty("events")
                .EnumerateArray()
                .Select(item => item.GetProperty("eventId").GetString())
                .ToArray();
            Assert.Equal(beforeEventIds, afterEventIds);

            var observations = after.GetProperty("rawObservations");
            Assert.Equal(2, observations.GetArrayLength());
            Assert.Equal(
                ["poll-ticket01-equivalent-1", "poll-ticket01-equivalent-2"],
                observations.EnumerateArray()
                    .Select(item => item.GetProperty("pollTraceId").GetString())
                    .ToArray());
            Assert.Equal(
                [firstReceipt.ProjectionCommitId, secondReceipt.ProjectionCommitId],
                observations.EnumerateArray()
                    .Select(item => item.GetProperty("projectionCommitId").GetString())
                    .ToArray());

            var secondTrace = await client.GetFromJsonAsync<JsonElement>(
                "/api/v2/poll-traces/poll-ticket01-equivalent-2");
            Assert.Equal(
                secondReceipt.ProjectionCommitId,
                secondTrace.GetProperty("projectionCommit").GetProperty("projectionCommitId").GetString());
            Assert.Equal(
                secondReceipt.ProjectionCommitId,
                secondTrace.GetProperty("observations")[0].GetProperty("projectionCommitId").GetString());

            AssertDatabaseEvidence(database);
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Ticket01SqlServerFact]
    public async Task Restarted_host_reads_the_same_persisted_projection()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var csvPath = await WriteEmptyLegacyCsvAsync();
        var startedAt = new DateTimeOffset(2026, 8, 12, 3, 0, 0, TimeSpan.Zero);
        const string pollTraceId = "poll-ticket01-restart";

        try
        {
            using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
            string seriesId;
            string demandId;
            string projectionCommitId;
            string[] eventIds;
            long[] eventSequences;
            string[] rawEvidenceLinks;

            await using (var firstFactory = CreateFactory())
            {
                var firstClient = firstFactory.CreateClient();
                var ingestor = firstFactory.Services.GetRequiredService<RoundIngestor>();
                var receipt = await ingestor.IngestAsync(CreateRound(
                    pollTraceId,
                    startedAt,
                    startedAt.AddSeconds(2)));
                var beforeRestart = await firstClient.GetFromJsonAsync<JsonElement>(SeriesByKeyUri);

                seriesId = beforeRestart.GetProperty("seriesId").GetString()!;
                demandId = beforeRestart.GetProperty("currentDemand").GetProperty("demandId").GetString()!;
                projectionCommitId = receipt.ProjectionCommitId!;
                eventIds = beforeRestart.GetProperty("events")
                    .EnumerateArray()
                    .Select(item => item.GetProperty("eventId").GetString()!)
                    .ToArray();
                eventSequences = beforeRestart.GetProperty("events")
                    .EnumerateArray()
                    .Select(item => item.GetProperty("seriesSequence").GetInt64())
                    .ToArray();
                rawEvidenceLinks = ReadRawEvidenceLinks(beforeRestart);
            }

            await using (var restartedFactory = CreateFactory())
            {
                var restartedClient = restartedFactory.CreateClient();
                var afterRestart = await restartedClient.GetFromJsonAsync<JsonElement>(
                    $"/api/v2/demand-series/{Uri.EscapeDataString(seriesId)}");

                Assert.Equal(seriesId, afterRestart.GetProperty("seriesId").GetString());
                Assert.Equal(
                    demandId,
                    afterRestart.GetProperty("currentDemand").GetProperty("demandId").GetString());
                Assert.Equal(1, afterRestart.GetProperty("currentDemand").GetProperty("generation").GetInt32());
                Assert.Equal("TRACKING", afterRestart.GetProperty("lifecycle").GetString());
                Assert.Equal("VISIBLE", afterRestart.GetProperty("currentPresence").GetString());
                Assert.Equal(projectionCommitId, afterRestart.GetProperty("latestProjectionCommitId").GetString());
                Assert.Equal(
                    eventIds,
                    afterRestart.GetProperty("events")
                        .EnumerateArray()
                        .Select(item => item.GetProperty("eventId").GetString())
                        .ToArray());
                Assert.Equal(
                    eventSequences,
                    afterRestart.GetProperty("events")
                        .EnumerateArray()
                        .Select(item => item.GetProperty("seriesSequence").GetInt64())
                        .ToArray());
                Assert.Equal(rawEvidenceLinks, ReadRawEvidenceLinks(afterRestart));

                var trace = await restartedClient.GetFromJsonAsync<JsonElement>(
                    $"/api/v2/poll-traces/{Uri.EscapeDataString(pollTraceId)}");
                Assert.Equal(
                    projectionCommitId,
                    trace.GetProperty("projectionCommit").GetProperty("projectionCommitId").GetString());
                Assert.Equal(
                    projectionCommitId,
                    trace.GetProperty("observations")[0].GetProperty("projectionCommitId").GetString());
            }

            AssertDatabaseEvidence(database);
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    private static string[] ReadRawEvidenceLinks(JsonElement series) =>
        series.GetProperty("rawObservations")
            .EnumerateArray()
            .Select(item => string.Join(
                '|',
                item.GetProperty("pollTraceId").GetString(),
                item.GetProperty("projectionCommitId").GetString(),
                item.GetProperty("seriesId").GetString(),
                item.GetProperty("demandId").GetString(),
                item.GetProperty("ordinal").GetInt32()))
            .ToArray();

    private void AssertDatabaseEvidence(Ticket01SqlServerDatabase database)
    {
        Assert.False(database.IsLocalDb);
        Assert.Equal(database.ExpectedProductMajor, database.ProductMajor);
        Assert.Equal(database.ExpectedCompatibilityLevel, database.CompatibilityLevel);
        Assert.InRange(database.EngineEdition, 1, 4);
        _output.WriteLine(
            $"Real SQL Server ProductVersion={database.ProductVersion}; "
            + $"ProductMajor={database.ProductMajor}; "
            + $"EngineEdition={database.EngineEdition}; "
            + $"CompatibilityLevel={database.CompatibilityLevel}");
    }

    private const string SeriesByKeyUri =
        "/api/v2/demand-series/by-key?workType=WIRE_TO_NITROGEN&sublot=SL-TICKET01-001";

    private static MesTaskUnionRound CreateRound(
        string pollTraceId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt) =>
        new(
            PollTraceId: pollTraceId,
            QueryVersion: "mes-task-union-test-v1",
            Outcome: MesTaskUnionRoundOutcome.Success,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            Observations:
            [
                new MesTaskUnionObservation(
                    WorkType: "WIRE_TO_NITROGEN",
                    Sublot: "SL-TICKET01-001",
                    Area: "N3-3",
                    Eqp: "WB-03",
                    Step: "焊线2",
                    MesSourceDate: new DateTimeOffset(
                        2026,
                        8,
                        12,
                        8,
                        58,
                        0,
                        TimeSpan.FromHours(8)),
                    Package: "QFN"),
            ]);

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
        });

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__EnableLegacyDevelopmentEndpoints"] = "true",
        });

    private static IDisposable ConfigureProductionWithoutV2Environment() =>
        new ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = null,
            [$"{MesIngestHostOptions.SectionName}__EnableLegacyDevelopmentEndpoints"] = "true",
        });

    private static async Task<string> WriteEmptyLegacyCsvAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mes-ticket01-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(
            path,
            "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n",
            Encoding.UTF8);
        return path;
    }

    private sealed class ProcessEnvironmentScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> _originalValues;
        private bool _disposed;

        public ProcessEnvironmentScope(IReadOnlyDictionary<string, string?> values)
        {
            _originalValues = values.Keys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in values)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var (key, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}

[CollectionDefinition("Ticket01SqlServer", DisableParallelization = true)]
public sealed class Ticket01SqlServerCollectionDefinition;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class Ticket01SqlServerFactAttribute : FactAttribute
{
    public Ticket01SqlServerFactAttribute()
    {
        if (!Ticket01SqlServerDatabase.IsAvailable)
        {
            Skip = "Real SQL Server unavailable for Ticket 01 tracer-spine gate";
        }
    }
}

internal sealed partial class Ticket01SqlServerDatabase : IAsyncDisposable
{
    private const string DatabasePrefix = "MesIngest_Ticket01_";
    private static readonly Lazy<ServerConnection?> AvailableServer = new(ResolveServer);
    private bool _disposed;

    private Ticket01SqlServerDatabase(
        string databaseName,
        string masterConnectionString,
        string connectionString,
        string productVersion,
        int productMajor,
        int engineEdition,
        int compatibilityLevel,
        int expectedProductMajor,
        int expectedCompatibilityLevel)
    {
        DatabaseName = databaseName;
        MasterConnectionString = masterConnectionString;
        ConnectionString = connectionString;
        ProductVersion = productVersion;
        ProductMajor = productMajor;
        EngineEdition = engineEdition;
        CompatibilityLevel = compatibilityLevel;
        ExpectedProductMajor = expectedProductMajor;
        ExpectedCompatibilityLevel = expectedCompatibilityLevel;
    }

    public static bool IsAvailable => AvailableServer.Value is not null;

    public string DatabaseName { get; }
    public string ConnectionString { get; }
    public string ProductVersion { get; }
    public int ProductMajor { get; }
    public int EngineEdition { get; }
    public int CompatibilityLevel { get; }
    public int ExpectedProductMajor { get; }
    public int ExpectedCompatibilityLevel { get; }
    public bool IsLocalDb => false;
    private string MasterConnectionString { get; }

    public static async Task<Ticket01SqlServerDatabase> CreateAsync()
    {
        var server = AvailableServer.Value
            ?? throw new InvalidOperationException("A real SQL Server is required for Ticket 01.");
        var expectedProductMajor = ReadRequiredPositiveInteger(
            "MES_INGEST_TICKET01_EXPECTED_PRODUCT_MAJOR");
        var expectedCompatibilityLevel = ReadRequiredPositiveInteger(
            "MES_INGEST_TICKET01_EXPECTED_COMPATIBILITY_LEVEL");
        if (!Version.TryParse(server.ProductVersion, out var productVersion))
        {
            throw new InvalidOperationException(
                $"SQL Server reported an invalid ProductVersion '{server.ProductVersion}'.");
        }
        if (productVersion.Major != expectedProductMajor)
        {
            throw new InvalidOperationException(
                $"Ticket 01 expected SQL Server product major {expectedProductMajor}, "
                + $"but the connected server reports {productVersion.Major} "
                + $"(ProductVersion {server.ProductVersion}).");
        }

        var databaseName = DatabasePrefix + Guid.NewGuid().ToString("N");
        EnsureOwnedDatabaseName(databaseName);
        var databaseCreated = false;
        try
        {
            await using (var connection = new SqlConnection(server.MasterConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{databaseName}];";
                await command.ExecuteNonQueryAsync();
                databaseCreated = true;
            }

            var builder = new SqlConnectionStringBuilder(server.MasterConnectionString)
            {
                InitialCatalog = databaseName,
            };
            await using var databaseConnection = new SqlConnection(builder.ConnectionString);
            await databaseConnection.OpenAsync();
            await using var compatibilityCommand = databaseConnection.CreateCommand();
            compatibilityCommand.CommandText =
                "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME();";
            var compatibilityLevel = Convert.ToInt32(
                await compatibilityCommand.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture);
            if (compatibilityLevel != expectedCompatibilityLevel)
            {
                throw new InvalidOperationException(
                    $"Ticket 01 expected database compatibility level "
                    + $"{expectedCompatibilityLevel}, but the created database reports "
                    + $"{compatibilityLevel}.");
            }

            return new Ticket01SqlServerDatabase(
                databaseName,
                server.MasterConnectionString,
                builder.ConnectionString,
                server.ProductVersion,
                productVersion.Major,
                server.EngineEdition,
                compatibilityLevel,
                expectedProductMajor,
                expectedCompatibilityLevel);
        }
        catch (Exception exception)
        {
            if (databaseCreated)
            {
                try
                {
                    await DropOwnedDatabaseAsync(server.MasterConnectionString, databaseName);
                }
                catch (Exception cleanupException)
                {
                    exception.Data["MesIngest.Ticket01DatabaseCleanupFailure"] =
                        cleanupException.ToString();
                }
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DropOwnedDatabaseAsync(MasterConnectionString, DatabaseName);
    }

    private static async Task DropOwnedDatabaseAsync(
        string masterConnectionString,
        string databaseName)
    {
        EnsureOwnedDatabaseName(databaseName);
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(@databaseName) IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """;
        command.Parameters.AddWithValue("@databaseName", databaseName);
        await command.ExecuteNonQueryAsync();
    }

    private static ServerConnection? ResolveServer()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("MES_INGEST_TICKET01_SQLSERVER"),
        };

        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(candidate)
                {
                    InitialCatalog = "master",
                    ConnectTimeout = 5,
                };
                var isLocalDb = builder.DataSource.Contains("(localdb)", StringComparison.OrdinalIgnoreCase);
                if (isLocalDb)
                {
                    continue;
                }

                using var connection = new SqlConnection(builder.ConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT
                        CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
                        CONVERT(int, SERVERPROPERTY('EngineEdition')),
                        CONVERT(int, HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE'));
                    """;
                using var reader = command.ExecuteReader();
                if (!reader.Read()
                    || reader.GetInt32(1) is < 1 or > 4
                    || reader.GetInt32(2) != 1)
                {
                    continue;
                }

                return new ServerConnection(
                    builder.ConnectionString,
                    reader.GetString(0),
                    reader.GetInt32(1));
            }
            catch (SqlException)
            {
                // Try the next explicitly bounded candidate.
            }
            catch (InvalidOperationException)
            {
                // Try the next explicitly bounded candidate.
            }
            catch (ArgumentException)
            {
                // Ignore malformed opt-in configuration during discovery.
            }
        }

        return null;
    }

    private static void EnsureOwnedDatabaseName(string databaseName)
    {
        if (!OwnedDatabaseNameRegex().IsMatch(databaseName))
        {
            throw new InvalidOperationException(
                $"Refusing database operation outside the owned {DatabasePrefix}<guid> namespace.");
        }
    }

    private static int ReadRequiredPositiveInteger(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            || parsed <= 0)
        {
            throw new InvalidOperationException(
                $"Set {variableName} to the explicitly approved SQL Server value.");
        }
        return parsed;
    }

    private sealed record ServerConnection(
        string MasterConnectionString,
        string ProductVersion,
        int EngineEdition);

    [GeneratedRegex("^MesIngest_Ticket01_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnedDatabaseNameRegex();
}
