using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MesIngest.Core;
using MesIngest.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MesIngest.Tests;

public class ReadApiContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReadApiContractTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task List_and_get_return_visible_demands_after_one_shot_from_csv()
    {
        var csv = """
            TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE
            DIE_TO_WIRE_STAGING,Q26079458-1,N09-01,2ZPB76,焊线,2026-08-01T13:55:40,TO-247APlus-4L
            DIE_TO_OVEN,Q-OLD-1,N01-01,EQ1,烘箱,2026-07-15T10:00:00,OLD-PKG
            WIRE_TO_NITROGEN,Q999-1,N03-03,EQ9,焊线,2026-08-02T08:00:00,UNKNOWN-PACKAGE-XYZ
            """;

        var path = Path.Combine(Path.GetTempPath(), $"mes-ingest-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        try
        {
            await using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton(new MesIngestHostOptions
                    {
                        SnapshotCsvPath = path,
                        GoLiveBaseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8)),
                        RunOneShotOnStartup = true,
                    });
                });
            });

            var client = factory.CreateClient();

            var list = await client.GetFromJsonAsync<JsonElement>("/api/demands");
            Assert.Equal(JsonValueKind.Array, list.ValueKind);
            Assert.Equal(2, list.GetArrayLength());

            var ids = list.EnumerateArray().Select(d => d.GetProperty("demandId").GetString()).ToArray();
            Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));

            var nitrogen = list.EnumerateArray()
                .Single(d => d.GetProperty("taskType").GetString() == "WIRE_TO_NITROGEN");
            Assert.Equal("UNKNOWN-PACKAGE-XYZ", nitrogen.GetProperty("package").GetString());
            Assert.Equal("VISIBLE", nitrogen.GetProperty("status").GetString());

            var demandId = nitrogen.GetProperty("demandId").GetString()!;
            var get = await client.GetAsync($"/api/demands/{demandId}");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var one = await get.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Q999-1", one.GetProperty("sublot").GetString());

            var missing = await client.GetAsync("/api/demands/does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task List_filters_demands_by_visible_or_gone_status()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "visible-1",
                TaskType = "DIE_TO_WIRE_STAGING",
                Sublot = "Q-VISIBLE",
                Area = "N09-01",
                Eqp = "EQ1",
                Step = "焊线",
                Dates = now,
                Package = "PKG-V",
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                DisappearCount = 0,
            },
            new TransportDemand
            {
                DemandId = "gone-1",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q-GONE",
                Area = "N01-01",
                Eqp = "EQ2",
                Step = "烘箱",
                Dates = now,
                Package = "PKG-G",
                Status = DemandStatus.Gone,
                MesLastSeenAt = now.AddMinutes(-10),
                DisappearCount = 2,
            },
        ]));

        var path = Path.Combine(Path.GetTempPath(), $"mes-ingest-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n", Encoding.UTF8);

        try
        {
            await using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton(new MesIngestHostOptions
                    {
                        SnapshotCsvPath = path,
                        GoLiveBaseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8)),
                        RunOneShotOnStartup = false,
                    });
                    services.AddSingleton<ITransportDemandStore>(store);
                });
            });

            var client = factory.CreateClient();

            var all = await client.GetFromJsonAsync<JsonElement>("/api/demands");
            Assert.Equal(2, all.GetArrayLength());

            var visible = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=VISIBLE");
            Assert.Equal(1, visible.GetArrayLength());
            Assert.Equal("visible-1", visible[0].GetProperty("demandId").GetString());
            Assert.Equal("VISIBLE", visible[0].GetProperty("status").GetString());

            var gone = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=GONE");
            Assert.Equal(1, gone.GetArrayLength());
            Assert.Equal("gone-1", gone[0].GetProperty("demandId").GetString());
            Assert.Equal("GONE", gone[0].GetProperty("status").GetString());

            var bad = await client.GetAsync("/api/demands?status=PENDING");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Http_projection_reflects_gone_and_reappear_after_reconcile_rounds()
    {
        var baseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var now = baseline.AddHours(12);
        var store = new InMemoryTransportDemandStore();
        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("d1", "d2"));

        var row = new MesSnapshotRow(
            "WIRE_TO_GATE",
            "Q-LIFE-1",
            "N02-02",
            "EQ",
            "关卡",
            baseline.AddHours(1),
            "PKG");

        store.ReplaceState(reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success([row]),
            now,
            baseline).State);
        store.ReplaceState(reconciler.Reconcile(
            store.GetState(),
            MesSnapshotOutcome.Success([]),
            now.AddMinutes(1),
            baseline).State);
        store.ReplaceState(reconciler.Reconcile(
            store.GetState(),
            MesSnapshotOutcome.Success([]),
            now.AddMinutes(2),
            baseline).State);
        store.ReplaceState(reconciler.Reconcile(
            store.GetState(),
            MesSnapshotOutcome.Success([row with { Package = "PKG-REAPPEAR" }]),
            now.AddMinutes(3),
            baseline).State);

        var path = Path.Combine(Path.GetTempPath(), $"mes-ingest-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n", Encoding.UTF8);

        try
        {
            await using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton(new MesIngestHostOptions
                    {
                        SnapshotCsvPath = path,
                        GoLiveBaseline = baseline,
                        RunOneShotOnStartup = false,
                    });
                    services.AddSingleton<ITransportDemandStore>(store);
                });
            });

            var client = factory.CreateClient();

            var gone = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=GONE");
            Assert.Equal(1, gone.GetArrayLength());
            Assert.Equal("d1", gone[0].GetProperty("demandId").GetString());

            var visible = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=VISIBLE");
            Assert.Equal(1, visible.GetArrayLength());
            Assert.Equal("d2", visible[0].GetProperty("demandId").GetString());
            Assert.Equal("PKG-REAPPEAR", visible[0].GetProperty("package").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Failed_poll_round_does_not_advance_disappear_count_via_http_projection()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "d1",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q-FAIL-1",
                Area = "N01-01",
                Eqp = "EQ1",
                Step = "烘箱",
                Dates = now,
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                DisappearCount = 1,
            },
        ]));

        var path = Path.Combine(Path.GetTempPath(), $"mes-ingest-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n", Encoding.UTF8);

        try
        {
            await using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton(new MesIngestHostOptions
                    {
                        SnapshotCsvPath = path,
                        GoLiveBaseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8)),
                        RunOneShotOnStartup = false,
                        DisappearThreshold = 2,
                    });
                    services.AddSingleton<ITransportDemandStore>(store);
                    services.AddSingleton<IMesSnapshotSource>(
                        new FixedMesSnapshotSource(MesSnapshotOutcome.Failure()));
                });
            });

            var runner = factory.Services.GetRequiredService<IngestRoundRunner>();
            await runner.RunOnceAsync();

            var client = factory.CreateClient();
            var visible = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=VISIBLE");
            Assert.Equal(1, visible.GetArrayLength());
            Assert.Equal("d1", visible[0].GetProperty("demandId").GetString());
            Assert.Equal(1, visible[0].GetProperty("disappearCount").GetInt32());
            Assert.Equal("VISIBLE", visible[0].GetProperty("status").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
