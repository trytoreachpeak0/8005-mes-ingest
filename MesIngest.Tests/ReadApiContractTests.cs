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
            Assert.Equal(JsonValueKind.Object, list.ValueKind);
            var items = list.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());

            var ids = items.EnumerateArray().Select(d => d.GetProperty("demandId").GetString()).ToArray();
            Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));

            var nitrogen = items.EnumerateArray()
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
    public async Task List_demands_defaults_to_dates_desc_then_demand_id_asc()
    {
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "b",
                TaskType = "DIE_TO_OVEN",
                Sublot = "S",
                Dates = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.FromHours(8)),
                Status = DemandStatus.Visible,
                MesLastSeenAt = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
            },
            new TransportDemand
            {
                DemandId = "a",
                TaskType = "DIE_TO_OVEN",
                Sublot = "S",
                Dates = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.FromHours(8)),
                Status = DemandStatus.Visible,
                MesLastSeenAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new TransportDemand
            {
                DemandId = "c",
                TaskType = "DIE_TO_OVEN",
                Sublot = "S",
                Dates = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.FromHours(8)),
                Status = DemandStatus.Visible,
                MesLastSeenAt = new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero),
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
            var list = await client.GetFromJsonAsync<JsonElement>("/api/demands");
            var ids = list.GetProperty("items").EnumerateArray()
                .Select(d => d.GetProperty("demandId").GetString())
                .ToArray();
            Assert.Equal(new[] { "c", "a", "b" }, ids);
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
                GoneAt = now.AddMinutes(-5),
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

            var defaults = await client.GetFromJsonAsync<JsonElement>("/api/demands");
            Assert.Equal(1, defaults.GetProperty("items").GetArrayLength());
            Assert.Equal("visible-1", defaults.GetProperty("items")[0].GetProperty("demandId").GetString());

            var visible = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=VISIBLE");
            Assert.Equal(1, visible.GetProperty("items").GetArrayLength());
            Assert.Equal("visible-1", visible.GetProperty("items")[0].GetProperty("demandId").GetString());
            Assert.Equal("VISIBLE", visible.GetProperty("items")[0].GetProperty("status").GetString());

            var gone = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=GONE");
            Assert.Equal(1, gone.GetProperty("items").GetArrayLength());
            Assert.Equal("gone-1", gone.GetProperty("items")[0].GetProperty("demandId").GetString());
            Assert.Equal("GONE", gone.GetProperty("items")[0].GetProperty("status").GetString());

            var bad = await client.GetAsync("/api/demands?status=PENDING");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            var byType = await client.GetFromJsonAsync<JsonElement>(
                "/api/demands?status=GONE&taskType=DIE_TO_OVEN");
            Assert.Equal(1, byType.GetProperty("items").GetArrayLength());
            Assert.Equal("gone-1", byType.GetProperty("items")[0].GetProperty("demandId").GetString());

            var bySublot = await client.GetFromJsonAsync<JsonElement>("/api/demands?sublot=Q-VISIBLE");
            Assert.Equal(1, bySublot.GetProperty("items").GetArrayLength());
            Assert.Equal("visible-1", bySublot.GetProperty("items")[0].GetProperty("demandId").GetString());

            var byDemandId = await client.GetAsync("/api/demands/gone-1");
            Assert.Equal(HttpStatusCode.OK, byDemandId.StatusCode);
            var one = await byDemandId.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("GONE", one.GetProperty("status").GetString());
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
            baseline,
            isGoneTransportDemandKey: store.HasGoneTransportDemandKey).State);

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
            Assert.Equal(1, gone.GetProperty("items").GetArrayLength());
            Assert.Equal("d1", gone.GetProperty("items")[0].GetProperty("demandId").GetString());

            var visible = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=VISIBLE");
            Assert.Equal(1, visible.GetProperty("items").GetArrayLength());
            Assert.Equal("d2", visible.GetProperty("items")[0].GetProperty("demandId").GetString());
            Assert.Equal("PKG-REAPPEAR", visible.GetProperty("items")[0].GetProperty("package").GetString());
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
            Assert.Equal(1, visible.GetProperty("items").GetArrayLength());
            Assert.Equal("d1", visible.GetProperty("items")[0].GetProperty("demandId").GetString());
            Assert.Equal(1, visible.GetProperty("items")[0].GetProperty("disappearCount").GetInt32());
            Assert.Equal("VISIBLE", visible.GetProperty("items")[0].GetProperty("status").GetString());

            var alerts = await client.GetFromJsonAsync<JsonElement>("/api/alerts");
            Assert.Equal(1, alerts.GetArrayLength());
            Assert.Equal("POLL_FAILURE", alerts[0].GetProperty("code").GetString());

            var health = await client.GetFromJsonAsync<JsonElement>("/api/poll-health");
            Assert.False(health.GetProperty("success").GetBoolean());
            Assert.Equal("FAILURE", health.GetProperty("outcome").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Alerts_endpoint_lists_recent_ingest_alerts()
    {
        var store = new InMemoryTransportDemandStore();
        store.AppendAlerts(
        [
            new IngestAlert(
                Code: "FIELD_DRIFT",
                TaskType: "DIE_TO_WIRE_STAGING",
                Sublot: "Q1",
                DemandId: "d1",
                Message: "drift"),
            new IngestAlert(
                Code: "DUPLICATE_RECONCILE_KEY",
                TaskType: "DIE_TO_OVEN",
                Sublot: "Q2",
                Message: "dup"),
        ]);

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
            var alerts = await client.GetFromJsonAsync<JsonElement>("/api/alerts");
            Assert.Equal(JsonValueKind.Array, alerts.ValueKind);
            Assert.Equal(2, alerts.GetArrayLength());
            var codes = alerts.EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToHashSet();
            Assert.Contains("FIELD_DRIFT", codes);
            Assert.Contains("DUPLICATE_RECONCILE_KEY", codes);
            Assert.All(alerts.EnumerateArray(), a =>
            {
                Assert.True(a.TryGetProperty("createdAt", out var created));
                Assert.NotEqual(JsonValueKind.Null, created.ValueKind);
            });

            store.AppendAlerts(
            [
                new IngestAlert(Code: "PAUSED_ZERO_DROP", TaskType: "DIE_TO_OVEN", Message: "paused"),
            ]);
            var limited = await client.GetFromJsonAsync<JsonElement>("/api/alerts?limit=2");
            Assert.Equal(2, limited.GetArrayLength());
            Assert.Equal("PAUSED_ZERO_DROP", limited[0].GetProperty("code").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Demand_dto_embeds_relevant_alerts_by_demand_id_or_reconcile_key()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "d1",
                TaskType = "DIE_TO_WIRE_STAGING",
                Sublot = "Q1",
                Area = "N09-01",
                Eqp = "EQ1",
                Step = "焊线",
                Dates = now,
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                DisappearCount = 0,
            },
            new TransportDemand
            {
                DemandId = "d2",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q2",
                Area = "N01-01",
                Eqp = "EQ2",
                Step = "烘箱",
                Dates = now,
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                DisappearCount = 0,
            },
            new TransportDemand
            {
                DemandId = "d3",
                TaskType = "WIRE_TO_NITROGEN",
                Sublot = "Q3",
                Area = "N03-03",
                Eqp = "EQ3",
                Step = "焊线",
                Dates = now,
                Package = "PKG",
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                DisappearCount = 0,
            },
            new TransportDemand
            {
                DemandId = "d1-gone",
                TaskType = "DIE_TO_WIRE_STAGING",
                Sublot = "Q1",
                Area = "N09-01",
                Eqp = "EQ1",
                Step = "焊线",
                Dates = now.AddHours(-1),
                Package = "PKG",
                Status = DemandStatus.Gone,
                MesLastSeenAt = now.AddHours(-1),
                DisappearCount = 2,
                GoneAt = now.AddMinutes(-30),
            },
        ]));
        store.AppendAlerts(
        [
            new IngestAlert(
                Code: "FIELD_DRIFT",
                TaskType: "DIE_TO_WIRE_STAGING",
                Sublot: "Q1",
                DemandId: "d1",
                Message: "drift"),
            new IngestAlert(
                Code: "REAPPEAR_AFTER_GONE",
                TaskType: "DIE_TO_WIRE_STAGING",
                Sublot: "Q1",
                DemandId: "d1",
                Message: "reappear"),
            new IngestAlert(
                Code: "DUPLICATE_RECONCILE_KEY",
                TaskType: "DIE_TO_OVEN",
                Sublot: "Q2",
                Message: "dup"),
            new IngestAlert(
                Code: "POLL_FAILURE",
                Message: "global"),
            new IngestAlert(
                Code: "PAUSED_ZERO_DROP",
                TaskType: "DIE_TO_OVEN",
                Message: "type-only"),
        ]);

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
            var list = await client.GetFromJsonAsync<JsonElement>("/api/demands");
            var items = list.GetProperty("items");
            Assert.Equal(3, items.GetArrayLength());

            var d1 = items.EnumerateArray().Single(d => d.GetProperty("demandId").GetString() == "d1");
            Assert.True(d1.TryGetProperty("alerts", out var d1Alerts));
            Assert.Equal(JsonValueKind.Array, d1Alerts.ValueKind);
            Assert.Equal(2, d1Alerts.GetArrayLength());
            var d1Codes = d1Alerts.EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToHashSet();
            Assert.Contains("FIELD_DRIFT", d1Codes);
            Assert.Contains("REAPPEAR_AFTER_GONE", d1Codes);

            var gonePage = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=GONE");
            var d1Gone = gonePage.GetProperty("items").EnumerateArray()
                .Single(d => d.GetProperty("demandId").GetString() == "d1-gone");
            Assert.Equal(0, d1Gone.GetProperty("alerts").GetArrayLength());

            var d2 = items.EnumerateArray().Single(d => d.GetProperty("demandId").GetString() == "d2");
            var d2Alerts = d2.GetProperty("alerts");
            Assert.Equal(1, d2Alerts.GetArrayLength());
            Assert.Equal("DUPLICATE_RECONCILE_KEY", d2Alerts[0].GetProperty("code").GetString());
            Assert.Equal("DIE_TO_OVEN", d2Alerts[0].GetProperty("taskType").GetString());
            Assert.Equal("Q2", d2Alerts[0].GetProperty("sublot").GetString());

            var d3 = items.EnumerateArray().Single(d => d.GetProperty("demandId").GetString() == "d3");
            Assert.Equal(0, d3.GetProperty("alerts").GetArrayLength());

            var get = await client.GetFromJsonAsync<JsonElement>("/api/demands/d1");
            Assert.Equal(2, get.GetProperty("alerts").GetArrayLength());
            Assert.Equal("VISIBLE", get.GetProperty("status").GetString());

            var global = await client.GetFromJsonAsync<JsonElement>("/api/alerts");
            Assert.Equal(5, global.GetArrayLength());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Poll_health_endpoint_reports_latest_round_after_runner()
    {
        var baseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var started = baseline.AddHours(12);
        var ended = started.AddSeconds(3);
        var ticks = 0;
        DateTimeOffset Clock() => ticks++ == 0 ? started : ended;

        var store = new InMemoryTransportDemandStore();
        var row = new MesSnapshotRow(
            "WIRE_TO_NITROGEN",
            "Q-HEALTH-1",
            "N03-03",
            "EQ9",
            "焊线",
            baseline.AddHours(1),
            "PKG");
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
                    services.AddSingleton<IMesSnapshotSource>(
                        new FixedMesSnapshotSource(MesSnapshotOutcome.Success([row])));
                    services.AddSingleton(sp => new IngestRoundRunner(
                        sp.GetRequiredService<IMesSnapshotSource>(),
                        sp.GetRequiredService<TransportDemandReconciler>(),
                        sp.GetRequiredService<ITransportDemandStore>(),
                        baseline,
                        clock: Clock));
                });
            });

            var runner = factory.Services.GetRequiredService<IngestRoundRunner>();
            await runner.RunOnceAsync();

            var client = factory.CreateClient();
            var health = await client.GetFromJsonAsync<JsonElement>("/api/poll-health");
            Assert.Equal("SUCCESS", health.GetProperty("outcome").GetString());
            Assert.True(health.GetProperty("success").GetBoolean());
            Assert.Equal(1, health.GetProperty("rowCount").GetInt32());
            Assert.Equal(started, health.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(ended, health.GetProperty("endedAt").GetDateTimeOffset());
            Assert.True(health.GetProperty("durationMs").GetDouble() >= 0);

            var demands = await client.GetFromJsonAsync<JsonElement>("/api/demands");
            Assert.Equal(1, demands.GetProperty("items").GetArrayLength());
            Assert.False(demands.GetProperty("items")[0].GetProperty("locationRisk").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Poll_health_endpoint_reports_failure_outcome()
    {
        var baseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
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
                    services.AddSingleton<IMesSnapshotSource>(
                        new FixedMesSnapshotSource(MesSnapshotOutcome.Failure()));
                    services.AddSingleton(sp => new IngestRoundRunner(
                        sp.GetRequiredService<IMesSnapshotSource>(),
                        sp.GetRequiredService<TransportDemandReconciler>(),
                        sp.GetRequiredService<ITransportDemandStore>(),
                        baseline,
                        clock: () => baseline.AddHours(1)));
                });
            });

            await factory.Services.GetRequiredService<IngestRoundRunner>().RunOnceAsync();

            var client = factory.CreateClient();
            var health = await client.GetFromJsonAsync<JsonElement>("/api/poll-health");
            Assert.Equal("FAILURE", health.GetProperty("outcome").GetString());
            Assert.False(health.GetProperty("success").GetBoolean());
            Assert.Equal(0, health.GetProperty("rowCount").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Demand_list_exposes_location_risk_for_empty_area()
    {
        var baseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var now = baseline.AddHours(12);
        var store = new InMemoryTransportDemandStore();
        var reconciler = new TransportDemandReconciler(new SequentialDemandIdAllocator("d1"));
        store.ReplaceState(reconciler.Reconcile(
            ProjectionState.Empty,
            MesSnapshotOutcome.Success(
            [
                new MesSnapshotRow("DIE_TO_OVEN", "Q-EMPTY", null, "EQ1", "烘箱", now, "PKG"),
            ]),
            now,
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
            var list = await client.GetFromJsonAsync<JsonElement>("/api/demands");
            Assert.Equal(1, list.GetProperty("items").GetArrayLength());
            Assert.True(list.GetProperty("items")[0].GetProperty("locationRisk").GetBoolean());
            Assert.Equal("AREA_EMPTY", list.GetProperty("items")[0].GetProperty("locationRiskCode").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Poll_health_endpoint_reports_task_type_paused_zero_drop_state()
    {
        var baseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var now = baseline.AddHours(12);
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d1",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-1",
                    Area = "N01-01",
                    Eqp = "EQ1",
                    Step = "烘箱",
                    Dates = now,
                    Package = "PKG",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = now,
                    DisappearCount = 0,
                },
            ],
            [
                new TaskTypePauseState(
                    "DIE_TO_OVEN",
                    PausedZeroDrop: false,
                    LastHealthyNonZeroCount: 10,
                    RecoveryStreak: 0),
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
                        GoLiveBaseline = baseline,
                        RunOneShotOnStartup = false,
                        ZeroDropEnterThreshold = 10,
                    });
                    services.AddSingleton<ITransportDemandStore>(store);
                    services.AddSingleton<IMesSnapshotSource>(
                        new FixedMesSnapshotSource(MesSnapshotOutcome.Success([])));
                    services.AddSingleton(sp => new IngestRoundRunner(
                        sp.GetRequiredService<IMesSnapshotSource>(),
                        sp.GetRequiredService<TransportDemandReconciler>(),
                        sp.GetRequiredService<ITransportDemandStore>(),
                        baseline,
                        zeroDropEnterThreshold: 10,
                        clock: () => now.AddMinutes(1)));
                });
            });

            var runner = factory.Services.GetRequiredService<IngestRoundRunner>();
            // First successful post-start round is the restart barrier; pause enters on the second zero.
            await runner.RunOnceAsync();
            await runner.RunOnceAsync();

            var client = factory.CreateClient();
            var health = await client.GetFromJsonAsync<JsonElement>("/api/poll-health");
            Assert.Equal("SUCCESS", health.GetProperty("outcome").GetString());

            var pauses = health.GetProperty("taskTypePauses");
            Assert.Equal(1, pauses.GetArrayLength());
            Assert.Equal("DIE_TO_OVEN", pauses[0].GetProperty("taskType").GetString());
            Assert.True(pauses[0].GetProperty("pausedZeroDrop").GetBoolean());
            Assert.Equal(10, pauses[0].GetProperty("lastHealthyNonZeroCount").GetInt32());

            var alerts = await client.GetFromJsonAsync<JsonElement>("/api/alerts");
            Assert.Contains(
                alerts.EnumerateArray(),
                a => a.GetProperty("code").GetString() == "PAUSED_ZERO_DROP");

            var visible = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=VISIBLE");
            Assert.Equal(1, visible.GetProperty("items").GetArrayLength());
            Assert.Equal(0, visible.GetProperty("items")[0].GetProperty("disappearCount").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Read_api_has_no_write_command_endpoints()
    {
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
                });
            });

            var client = factory.CreateClient();
            Assert.True(IsWriteRejected(await client.PostAsync("/api/demands", null)));
            Assert.True(IsWriteRejected(await client.DeleteAsync("/api/alerts")));
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/alerts/clear", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/demands/d1/suppress", null)).StatusCode);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.PostAsync("/api/poll-health/clear-pause", null)).StatusCode);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.PostAsync("/api/task-types/DIE_TO_OVEN/clear-pause", null)).StatusCode);
        }
        finally
        {
            File.Delete(path);
        }

        static bool IsWriteRejected(HttpResponseMessage response) =>
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;
    }
}
