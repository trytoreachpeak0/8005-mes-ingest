using System.Text;
using MesIngest.Core;
using MesIngest.Host;
using MesIngest.Watch;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 15 HTTP + Watch seam: every newly sortable TransportDemands column
/// is ordered by Host across the complete result set and remains gap-free across cursors.
/// </summary>
public class DemandVisibleColumnSortHttpTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DemandVisibleColumnSortHttpTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    public static TheoryData<string, string> NewSortableHeaders => new()
    {
        { "status", "status" },
        { "AREA", "area" },
        { "EQP", "eqp" },
        { "STEP", "step" },
        { "PACKAGE", "package" },
        { "locationRisk", "locationRisk" },
        { "disappear", "disappearCount" },
    };

    [Theory]
    [MemberData(nameof(NewSortableHeaders))]
    public void Every_new_visible_demand_column_toggles_asc_then_desc(string header, string token)
    {
        var initial = WatchDemandBrowseQuery.Default;

        Assert.True(initial.TryApplySort(header, out var ascending));
        Assert.Equal(token, ascending.SortBy);
        Assert.Equal("asc", ascending.Direction);

        Assert.True(ascending.TryApplySort(header, out var descending));
        Assert.Equal(token, descending.SortBy);
        Assert.Equal("desc", descending.Direction);
    }

    [Fact]
    public async Task Watch_loads_every_new_demand_sort_from_real_host_without_gaps_or_duplicates()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore();
        store.ReplaceState(new ProjectionState(CreateDemands(now)));
        var csvPath = Path.Combine(Path.GetTempPath(), $"mes-ingest-demand-sort-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(
            csvPath,
            "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n",
            Encoding.UTF8);

        try
        {
            await using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton(new MesIngestHostOptions
                    {
                        SnapshotCsvPath = csvPath,
                        GoLiveBaseline = now.AddDays(-1),
                        RunOneShotOnStartup = false,
                    });
                    services.AddSingleton<ITransportDemandStore>(store);
                });
            });
            using var http = factory.CreateClient();

            foreach (var sortCase in SortCases())
            {
                var session = new WatchBrowseSession(new MesIngestApiClient(http), pageSize: 2);
                var query = WatchDemandBrowseQuery.Default with
                {
                    SortBy = sortCase.SortBy,
                    Direction = sortCase.Direction,
                    Limit = 2,
                };

                await session.RefreshAsync(WatchBrowseRefreshKind.Reset, query);
                for (var pageNumber = 0; session.DemandsHasMore && pageNumber < 10; pageNumber++)
                {
                    await session.RefreshAsync(WatchBrowseRefreshKind.Append, query);
                }

                var actualIds = session.Demands.Select(demand => demand.DemandId).ToArray();
                Assert.Equal(sortCase.ExpectedIds, actualIds);
                Assert.Equal(actualIds.Length, actualIds.Distinct(StringComparer.Ordinal).Count());
                Assert.False(session.DemandsHasMore);
            }
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    private static IReadOnlyList<SortCase> SortCases() =>
    [
        // /api/demands requires one VISIBLE or GONE partition. Status sorting therefore
        // exercises Host token acceptance and stable cursor paging with a tied primary.
        new("status", "asc", ["a1", "a2", "a3", "a4", "a5"]),
        new("status", "desc", ["a1", "a2", "a3", "a4", "a5"]),
        new("area", "asc", ["a2", "a5", "a3", "a4", "a1"]),
        new("area", "desc", ["a1", "a3", "a4", "a2", "a5"]),
        new("eqp", "asc", ["a1", "a3", "a4", "a2", "a5"]),
        new("eqp", "desc", ["a5", "a2", "a3", "a4", "a1"]),
        new("step", "asc", ["a3", "a5", "a2", "a1", "a4"]),
        new("step", "desc", ["a4", "a1", "a2", "a3", "a5"]),
        new("package", "asc", ["a2", "a5", "a1", "a4", "a3"]),
        new("package", "desc", ["a3", "a1", "a4", "a2", "a5"]),
        new("locationRisk", "asc", ["a2", "a4", "a5", "a1", "a3"]),
        new("locationRisk", "desc", ["a1", "a3", "a2", "a4", "a5"]),
        new("disappearCount", "asc", ["a4", "a2", "a3", "a1", "a5"]),
        new("disappearCount", "desc", ["a1", "a5", "a2", "a3", "a4"]),
    ];

    private static IReadOnlyList<TransportDemand> CreateDemands(DateTimeOffset now) =>
    [
        Demand("a1", now, area: "B", eqp: null, step: "2", package: "P1", locationRisk: true, disappearCount: 2),
        Demand("a2", now, area: null, eqp: "E2", step: "1", package: null, locationRisk: false, disappearCount: 1),
        Demand("a3", now, area: "A", eqp: "E1", step: null, package: "P2", locationRisk: true, disappearCount: 1),
        Demand("a4", now, area: "A", eqp: "E1", step: "3", package: "P1", locationRisk: false, disappearCount: 0),
        Demand("a5", now, area: null, eqp: "E3", step: null, package: null, locationRisk: false, disappearCount: 2),
    ];

    private static TransportDemand Demand(
        string demandId,
        DateTimeOffset now,
        string? area,
        string? eqp,
        string? step,
        string? package,
        bool locationRisk,
        int disappearCount) =>
        new()
        {
            DemandId = demandId,
            TaskType = "DIE_TO_OVEN",
            Sublot = "SAME_SUBLOT",
            Area = area,
            Eqp = eqp,
            Step = step,
            Dates = now,
            Package = package,
            Status = DemandStatus.Visible,
            MesLastSeenAt = now,
            DisappearCount = disappearCount,
            LocationRisk = locationRisk,
            CreatedAt = now,
        };

    private sealed record SortCase(string SortBy, string Direction, string[] ExpectedIds);
}
