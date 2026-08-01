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

public class PaginatedDemandApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PaginatedDemandApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task List_returns_paginated_envelope_defaulting_to_visible_dates_desc()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("visible-b", DemandStatus.Visible, now.AddHours(1), "DIE_TO_OVEN", "Q-B"),
            Demand("visible-a", DemandStatus.Visible, now.AddHours(1), "DIE_TO_OVEN", "Q-A"),
            Demand("visible-c", DemandStatus.Visible, now.AddHours(2), "DIE_TO_OVEN", "Q-C"),
            Demand("gone-old", DemandStatus.Gone, now, "DIE_TO_OVEN", "Q-GONE", goneAt: now.AddHours(-1)),
        ]));

        await using var factory = await CreateFactoryAsync(store);
        var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<JsonElement>("/api/demands");
        Assert.Equal(JsonValueKind.Object, page.ValueKind);
        Assert.True(page.TryGetProperty("items", out var items));
        Assert.True(page.TryGetProperty("nextCursor", out _));
        Assert.True(page.TryGetProperty("hasMore", out var hasMore));
        Assert.False(hasMore.GetBoolean());
        Assert.False(page.ValueKind == JsonValueKind.Array);

        var ids = items.EnumerateArray().Select(d => d.GetProperty("demandId").GetString()).ToArray();
        Assert.Equal(new[] { "visible-c", "visible-a", "visible-b" }, ids);
        Assert.All(items.EnumerateArray(), d => Assert.Equal("VISIBLE", d.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task List_default_dates_desc_pages_through_tied_primary_values_without_gap_or_dup()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("id-00", DemandStatus.Visible, now, "DIE_TO_OVEN", "Q0"),
            Demand("id-01", DemandStatus.Visible, now, "DIE_TO_OVEN", "Q1"),
            Demand("id-02", DemandStatus.Visible, now, "DIE_TO_OVEN", "Q2"),
            Demand("id-03", DemandStatus.Visible, now, "DIE_TO_OVEN", "Q3"),
            Demand("id-04", DemandStatus.Visible, now, "DIE_TO_OVEN", "Q4"),
        ]));

        await using var factory = await CreateFactoryAsync(store);
        var client = factory.CreateClient();

        var collected = new List<string>();
        string? cursor = null;
        for (var pages = 0; pages < 10; pages++)
        {
            var url = cursor is null
                ? "/api/demands?limit=2"
                : $"/api/demands?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var page = await client.GetFromJsonAsync<JsonElement>(url);
            collected.AddRange(page.GetProperty("items").EnumerateArray()
                .Select(d => d.GetProperty("demandId").GetString()!));
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
                break;
            }

            cursor = page.GetProperty("nextCursor").GetString();
            Assert.False(string.IsNullOrWhiteSpace(cursor));
        }

        Assert.Equal(new[] { "id-00", "id-01", "id-02", "id-03", "id-04" }, collected);
        Assert.Equal(collected.Count, collected.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task List_enforces_page_size_hard_cap_and_paginates_without_dup_or_gap()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var demands = Enumerable.Range(0, 5)
            .Select(i => Demand(
                $"id-{i:D2}",
                DemandStatus.Visible,
                now.AddMinutes(i),
                "DIE_TO_OVEN",
                $"Q-{i}"))
            .ToArray();
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(demands));

        await using var factory = await CreateFactoryAsync(store);
        var client = factory.CreateClient();

        var overCap = await client.GetAsync("/api/demands?limit=9999");
        Assert.Equal(HttpStatusCode.BadRequest, overCap.StatusCode);

        var first = await client.GetFromJsonAsync<JsonElement>("/api/demands?limit=2");
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var second = await client.GetFromJsonAsync<JsonElement>($"/api/demands?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
        Assert.True(second.GetProperty("hasMore").GetBoolean());

        var third = await client.GetFromJsonAsync<JsonElement>(
            $"/api/demands?limit=2&cursor={Uri.EscapeDataString(second.GetProperty("nextCursor").GetString()!)}");
        Assert.Equal(1, third.GetProperty("items").GetArrayLength());
        Assert.False(third.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, third.GetProperty("nextCursor").ValueKind);

        var allIds = first.GetProperty("items").EnumerateArray()
            .Concat(second.GetProperty("items").EnumerateArray())
            .Concat(third.GetProperty("items").EnumerateArray())
            .Select(d => d.GetProperty("demandId").GetString())
            .ToArray();
        Assert.Equal(new[] { "id-04", "id-03", "id-02", "id-01", "id-00" }, allIds);
        Assert.Equal(allIds.Length, allIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task List_rejects_cursor_that_does_not_match_sort()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S"),
            Demand("b", DemandStatus.Visible, now.AddHours(1), "T", "S"),
        ]));

        await using var factory = await CreateFactoryAsync(store);
        var client = factory.CreateClient();

        var first = await client.GetFromJsonAsync<JsonElement>("/api/demands?limit=1&sortBy=dates&direction=desc");
        var cursor = first.GetProperty("nextCursor").GetString()!;

        var mismatch = await client.GetAsync(
            $"/api/demands?limit=1&sortBy=dates&direction=asc&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        var bad = await client.GetAsync("/api/demands?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task List_gone_defaults_to_recent_24h_window_and_allows_explicit_widen()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("gone-recent", DemandStatus.Gone, now.AddDays(-2), "T", "R", goneAt: now.AddHours(-2)),
            Demand("gone-old", DemandStatus.Gone, now.AddDays(-10), "T", "O", goneAt: now.AddDays(-3)),
            Demand("visible", DemandStatus.Visible, now, "T", "V"),
        ]));

        await using var factory = await CreateFactoryAsync(store);
        var client = factory.CreateClient();

        var goneDefault = await client.GetFromJsonAsync<JsonElement>("/api/demands?status=GONE");
        var defaultIds = goneDefault.GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("demandId").GetString())
            .ToArray();
        Assert.Equal(new[] { "gone-recent" }, defaultIds);

        var goneWide = await client.GetFromJsonAsync<JsonElement>(
            $"/api/demands?status=GONE&goneAtFrom={Uri.EscapeDataString(now.AddDays(-10).ToString("O"))}");
        var wideIds = goneWide.GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("demandId").GetString())
            .ToArray();
        Assert.Equal(new[] { "gone-recent", "gone-old" }, wideIds);
    }

    [Fact]
    public async Task List_demand_id_supports_exact_and_hex_prefix_and_rejects_illegal_input()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("abcdef0123456789abcdef0123456789", DemandStatus.Visible, now, "T", "A"),
            Demand("abcdef9999999999abcdef9999999999", DemandStatus.Visible, now.AddHours(1), "T", "B"),
            Demand("deadbeefdeadbeefdeadbeefdeadbeef", DemandStatus.Visible, now.AddHours(2), "T", "C"),
        ]));

        await using var factory = await CreateFactoryAsync(store);
        var client = factory.CreateClient();

        var exact = await client.GetFromJsonAsync<JsonElement>(
            "/api/demands?demandId=abcdef0123456789abcdef0123456789");
        Assert.Equal(
            "abcdef0123456789abcdef0123456789",
            exact.GetProperty("items")[0].GetProperty("demandId").GetString());

        var prefix = await client.GetFromJsonAsync<JsonElement>("/api/demands?demandId=abcdef");
        var prefixIds = prefix.GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("demandId").GetString())
            .ToArray();
        Assert.Equal(
            new[]
            {
                "abcdef9999999999abcdef9999999999",
                "abcdef0123456789abcdef0123456789",
            },
            prefixIds);

        var shortPrefix = await client.GetAsync("/api/demands?demandId=abcde");
        Assert.Equal(HttpStatusCode.BadRequest, shortPrefix.StatusCode);

        var illegal = await client.GetAsync("/api/demands?demandId=ab%cd");
        Assert.Equal(HttpStatusCode.BadRequest, illegal.StatusCode);

        var nonHexExact = await client.GetAsync("/api/demands?demandId=gone-1");
        Assert.Equal(HttpStatusCode.BadRequest, nonHexExact.StatusCode);
    }

    [Fact]
    public async Task List_supports_combined_filters_and_allow_list_sort()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now.AddHours(1), "DIE_TO_OVEN", "Q1"),
            Demand("b", DemandStatus.Visible, now.AddHours(2), "DIE_TO_OVEN", "Q2"),
            Demand("c", DemandStatus.Visible, now.AddHours(3), "WIRE_TO_GATE", "Q1"),
        ]));

        await using var factory = await CreateFactoryAsync(store);
        var client = factory.CreateClient();

        var filtered = await client.GetFromJsonAsync<JsonElement>(
            "/api/demands?taskType=DIE_TO_OVEN&sublot=Q1&sortBy=taskType&direction=asc");
        Assert.Equal(
            new[] { "a" },
            filtered.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("demandId").GetString()).ToArray());

        var badSort = await client.GetAsync("/api/demands?sortBy=package");
        Assert.Equal(HttpStatusCode.BadRequest, badSort.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_remains_exact_lookup()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("abcdef0123456789abcdef0123456789", DemandStatus.Visible, now, "T", "A"),
        ]));

        await using var factory = await CreateFactoryAsync(store);
        var client = factory.CreateClient();

        var ok = await client.GetAsync("/api/demands/abcdef0123456789abcdef0123456789");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/demands/missing")).StatusCode);
    }

    [Fact]
    public async Task List_supports_dates_range_filter()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("early", DemandStatus.Visible, now.AddHours(-5), "T", "E"),
            Demand("mid", DemandStatus.Visible, now, "T", "M"),
            Demand("late", DemandStatus.Visible, now.AddHours(5), "T", "L"),
        ]));

        await using var factory = await CreateFactoryAsync(store);
        var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/demands?datesFrom={Uri.EscapeDataString(now.AddHours(-1).ToString("O"))}&datesTo={Uri.EscapeDataString(now.AddHours(1).ToString("O"))}");
        Assert.Equal(
            new[] { "mid" },
            page.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("demandId").GetString()).ToArray());
    }

    [Fact]
    public async Task List_keyset_pagination_does_not_duplicate_when_newer_row_appears()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(
        [
            Demand("id-01", DemandStatus.Visible, now.AddMinutes(1), "T", "S1"),
            Demand("id-02", DemandStatus.Visible, now.AddMinutes(2), "T", "S2"),
            Demand("id-03", DemandStatus.Visible, now.AddMinutes(3), "T", "S3"),
        ]));

        await using var factory = await CreateFactoryAsync(store);
        var client = factory.CreateClient();

        var first = await client.GetFromJsonAsync<JsonElement>("/api/demands?limit=2");
        Assert.Equal(new[] { "id-03", "id-02" }, first.GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("demandId").GetString()).ToArray());
        var cursor = first.GetProperty("nextCursor").GetString()!;

        store.ReplaceState(new ProjectionState(
        [
            Demand("id-01", DemandStatus.Visible, now.AddMinutes(1), "T", "S1"),
            Demand("id-02", DemandStatus.Visible, now.AddMinutes(2), "T", "S2"),
            Demand("id-03", DemandStatus.Visible, now.AddMinutes(3), "T", "S3"),
            Demand("id-99", DemandStatus.Visible, now.AddMinutes(9), "T", "S9"),
        ]));

        var second = await client.GetFromJsonAsync<JsonElement>(
            $"/api/demands?limit=2&cursor={Uri.EscapeDataString(cursor)}");
        var secondIds = second.GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("demandId").GetString())
            .ToArray();
        Assert.Equal(new[] { "id-01" }, secondIds);
        Assert.DoesNotContain("id-03", secondIds);
        Assert.DoesNotContain("id-02", secondIds);
        Assert.DoesNotContain("id-99", secondIds);
    }

    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(ITransportDemandStore store)
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
                });
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
        };
}
