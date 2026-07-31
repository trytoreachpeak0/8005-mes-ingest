using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MesIngest.Core;
using MesIngest.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MesIngest.Tests;

[Collection("SqlServer")]
public class SqlServerReadApiPersistenceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SqlServerReadApiPersistenceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [SqlServerAvailabilityFact]
    public async Task Http_reads_projection_alerts_and_health_after_store_restart()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var baseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var ticks = 0;
        DateTimeOffset Clock() => ticks++ == 0 ? now : now.AddSeconds(2);

        var store = new SqlServerTransportDemandStore(cs);
        var runner = new IngestRoundRunner(
            new FixedMesSnapshotSource(MesSnapshotOutcome.Success(
            [
                new MesSnapshotRow(
                    "WIRE_TO_NITROGEN",
                    "Q-HTTP-1",
                    "N03-03",
                    "EQ9",
                    "焊线",
                    now,
                    "PKG"),
            ])),
            new TransportDemandReconciler(new SequentialDemandIdAllocator("persist-1")),
            store,
            baseline,
            clock: Clock);
        await runner.RunOnceAsync();
        store.AppendAlerts(
        [
            new IngestAlert(
                Code: "REAPPEAR_AFTER_GONE",
                TaskType: "WIRE_TO_NITROGEN",
                Sublot: "Q-HTTP-1",
                DemandId: "persist-1"),
        ]);

        var path = Path.Combine(Path.GetTempPath(), $"mes-ingest-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n", Encoding.UTF8);

        try
        {
            // New Host process / DI graph: fresh SqlServerTransportDemandStore, no one-shot mutate.
            await using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton(new MesIngestHostOptions
                    {
                        SnapshotCsvPath = path,
                        GoLiveBaseline = baseline,
                        RunOneShotOnStartup = false,
                        SqlServerConnectionString = cs,
                    });
                    services.AddSingleton<ITransportDemandStore>(
                        _ => new SqlServerTransportDemandStore(cs));
                });
            });

            var client = factory.CreateClient();

            var demands = await client.GetFromJsonAsync<JsonElement>("/api/demands");
            Assert.Equal(1, demands.GetProperty("items").GetArrayLength());
            Assert.Equal("persist-1", demands.GetProperty("items")[0].GetProperty("demandId").GetString());
            Assert.Equal("VISIBLE", demands.GetProperty("items")[0].GetProperty("status").GetString());

            var alerts = await client.GetFromJsonAsync<JsonElement>("/api/alerts");
            Assert.Equal(1, alerts.GetArrayLength());
            Assert.Equal("REAPPEAR_AFTER_GONE", alerts[0].GetProperty("code").GetString());

            var health = await client.GetFromJsonAsync<JsonElement>("/api/poll-health");
            Assert.Equal("SUCCESS", health.GetProperty("outcome").GetString());
            Assert.Equal(1, health.GetProperty("rowCount").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
