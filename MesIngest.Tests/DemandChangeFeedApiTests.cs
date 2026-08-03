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

public class DemandChangeFeedApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DemandChangeFeedApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Demand_changes_returns_bounded_page_with_watermarks()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore(clock: () => now);
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
            Demand("c", DemandStatus.Visible, now, "T", "S3"),
        ]));

        await using var factory = await CreateFactoryAsync(store, new AdjustableTimeProvider(now));
        var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<JsonElement>("/api/demand-changes?limit=2");
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, page.GetProperty("nextAfterSequence").GetInt64());
        Assert.Equal(3, page.GetProperty("highWatermark").GetInt64());
        Assert.Equal(1, page.GetProperty("earliestAvailableSequence").GetInt64());

        var first = page.GetProperty("items")[0];
        Assert.Equal("CREATED", first.GetProperty("changeType").GetString());
        Assert.Equal("a", first.GetProperty("demandId").GetString());
        Assert.Equal("VISIBLE", first.GetProperty("payload").GetProperty("status").GetString());
        Assert.False(first.GetProperty("payload").TryGetProperty("mesLastSeenAt", out _));
        Assert.False(first.GetProperty("payload").TryGetProperty("disappearCount", out _));

        var next = await client.GetFromJsonAsync<JsonElement>(
            $"/api/demand-changes?afterSequence={page.GetProperty("nextAfterSequence").GetInt64()}&limit=2");
        Assert.Equal(1, next.GetProperty("items").GetArrayLength());
        Assert.False(next.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextAfterSequence").ValueKind);
        Assert.Equal("c", next.GetProperty("items")[0].GetProperty("demandId").GetString());
    }

    [Fact]
    public async Task Demand_changes_rejects_oversize_limit_and_bad_afterSequence()
    {
        var store = new InMemoryTransportDemandStore();
        await using var factory = await CreateFactoryAsync(store);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/demand-changes?limit=9999")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/demand-changes?afterSequence=-1")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/demand-changes?afterSequence=abc")).StatusCode);
    }

    [Fact]
    public async Task Demand_changes_returns_410_with_sync_cursor_expired()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var clock = now;
        var store = new InMemoryTransportDemandStore(
            changeFeedRetention: TimeSpan.FromHours(48),
            clock: () => clock);

        store.ReplaceState(new ProjectionState([Demand("a", DemandStatus.Visible, now, "T", "S1")]));
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
        ]));
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
            Demand("c", DemandStatus.Visible, now, "T", "S3"),
        ]));
        clock = now.AddHours(49);
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
            Demand("c", DemandStatus.Visible, now, "T", "S3"),
            Demand("d", DemandStatus.Visible, clock, "T", "S4"),
        ]));

        await using var factory = await CreateFactoryAsync(store, new AdjustableTimeProvider(clock));
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/demand-changes?afterSequence=0");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var fromStart = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SyncCursorExpiredException.ErrorCode, fromStart.GetProperty("code").GetString());
        Assert.Equal(0, fromStart.GetProperty("afterSequence").GetInt64());
        Assert.Equal(4, fromStart.GetProperty("earliestAvailableSequence").GetInt64());
        Assert.Equal(4, fromStart.GetProperty("highWatermark").GetInt64());

        response = await client.GetAsync("/api/demand-changes?afterSequence=2");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SyncCursorExpiredException.ErrorCode, body.GetProperty("code").GetString());
        Assert.Equal(4, body.GetProperty("earliestAvailableSequence").GetInt64());
        Assert.Equal(4, body.GetProperty("highWatermark").GetInt64());

        var catchUp = await client.GetFromJsonAsync<JsonElement>("/api/demand-changes?afterSequence=3");
        Assert.Equal(1, catchUp.GetProperty("items").GetArrayLength());
        Assert.Equal("d", catchUp.GetProperty("items")[0].GetProperty("demandId").GetString());
        Assert.Equal(4, catchUp.GetProperty("highWatermark").GetInt64());
    }

    [Fact]
    public async Task Demand_changes_full_purge_returns_monotonic_watermark_and_410_for_stale_cursors()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var clock = now;
        var store = new InMemoryTransportDemandStore(
            changeFeedRetention: TimeSpan.FromHours(48),
            clock: () => clock);

        store.ReplaceState(new ProjectionState([Demand("a", DemandStatus.Visible, now, "T", "S1")]));
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
        ]));

        var timeProvider = new AdjustableTimeProvider(now);
        await using var factory = await CreateFactoryAsync(store, timeProvider);
        var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<JsonElement>("/api/demand-changes");
        Assert.Equal(2, before.GetProperty("highWatermark").GetInt64());

        clock = now.AddHours(49);
        timeProvider.SetUtcNow(clock);
        // Same projection, no new CREATED/GONE — ReplaceState still applies retention purge.
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
        ]));

        var caughtUp = await client.GetFromJsonAsync<JsonElement>("/api/demand-changes?afterSequence=2");
        Assert.Equal(0, caughtUp.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, caughtUp.GetProperty("earliestAvailableSequence").ValueKind);
        Assert.Equal(2, caughtUp.GetProperty("highWatermark").GetInt64());

        var expired = await client.GetAsync("/api/demand-changes?afterSequence=0");
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        var body = await expired.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SyncCursorExpiredException.ErrorCode, body.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("earliestAvailableSequence").ValueKind);
        Assert.Equal(2, body.GetProperty("highWatermark").GetInt64());
    }

    [Fact]
    public async Task Bootstrap_via_demands_then_catch_up_feed_preserves_concurrent_created_and_gone()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore(clock: () => now);
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "SA"),
            Demand("b", DemandStatus.Visible, now, "T", "SB"),
            Demand("g", DemandStatus.Gone, now.AddHours(-2), "T", "SG", goneAt: now.AddHours(-1)),
        ]));

        await using var factory = await CreateFactoryAsync(store, new AdjustableTimeProvider(now));
        var client = factory.CreateClient();

        var feed = await client.GetFromJsonAsync<JsonElement>("/api/demand-changes");
        var watermark = feed.GetProperty("highWatermark").GetInt64();

        var visible = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=VISIBLE&limit=200");
        var gone = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=GONE&limit=200");
        var mirror = visible.GetProperty("items").EnumerateArray()
            .Concat(gone.GetProperty("items").EnumerateArray())
            .ToDictionary(d => d.GetProperty("demandId").GetString()!, d => d, StringComparer.Ordinal);
        Assert.Equal(3, mirror.Count);
        Assert.Equal("GONE", mirror["g"].GetProperty("status").GetString());

        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Gone, now, "T", "SA", goneAt: now.AddMinutes(1)),
            Demand("b", DemandStatus.Visible, now, "T", "SB"),
            Demand("g", DemandStatus.Gone, now.AddHours(-2), "T", "SG", goneAt: now.AddHours(-1)),
            Demand("c", DemandStatus.Visible, now.AddMinutes(1), "T", "SC"),
        ]));

        var catchUp = await client.GetFromJsonAsync<JsonElement>(
            $"/api/demand-changes?afterSequence={watermark}");
        foreach (var item in catchUp.GetProperty("items").EnumerateArray())
        {
            var id = item.GetProperty("demandId").GetString()!;
            mirror[id] = item.GetProperty("payload");
        }

        Assert.Equal("GONE", mirror["a"].GetProperty("status").GetString());
        Assert.Equal("VISIBLE", mirror["b"].GetProperty("status").GetString());
        Assert.Equal("VISIBLE", mirror["c"].GetProperty("status").GetString());
        Assert.Equal("GONE", mirror["g"].GetProperty("status").GetString());
    }

    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(
        ITransportDemandStore store,
        TimeProvider? timeProvider = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mes-ingest-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n", Encoding.UTF8);

        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new MesIngestHostOptions
                {
                    SnapshotCsvPath = path,
                    GoLiveBaseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(8)),
                    RunOneShotOnStartup = false,
                    ChangeFeedRetentionHours = 48,
                });
                if (timeProvider is not null)
                {
                    services.AddSingleton(timeProvider);
                }
                services.AddSingleton(store);
            });
        });
    }

    private static TransportDemand Demand(
        string id,
        DemandStatus status,
        DateTimeOffset dates,
        string taskType,
        string sublot,
        DateTimeOffset? goneAt = null) =>
        new()
        {
            DemandId = id,
            TaskType = taskType,
            Sublot = sublot,
            Dates = dates,
            Status = status,
            MesLastSeenAt = dates,
            GoneAt = goneAt,
            CreatedAt = dates,
            DisappearCount = status == DemandStatus.Gone ? 2 : 0,
        };
}
