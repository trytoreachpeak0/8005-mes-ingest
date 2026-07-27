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
}
