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

/// <summary>
/// Ticket 14 HTTP seam: every Alerts grid column is sorted by Host across the
/// complete result set, with AlertId as the stable tie-break across cursors.
/// </summary>
public class AlertVisibleColumnSortHttpTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AlertVisibleColumnSortHttpTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    public static TheoryData<string, string, string[]> SortCases => new()
    {
        { "code", "asc", ["a2", "a3", "a1", "a5", "a4"] },
        { "code", "desc", ["a4", "a1", "a5", "a2", "a3"] },
        { "severity", "asc", ["a1", "a4", "a2", "a3", "a5"] },
        { "severity", "desc", ["a2", "a3", "a5", "a1", "a4"] },
        { "alertId", "asc", ["a1", "a2", "a3", "a4", "a5"] },
        { "alertId", "desc", ["a5", "a4", "a3", "a2", "a1"] },
        { "firstSeenAt", "asc", ["a1", "a5", "a2", "a3", "a4"] },
        { "firstSeenAt", "desc", ["a4", "a2", "a3", "a1", "a5"] },
        { "lastSeenAt", "asc", ["a3", "a2", "a4", "a1", "a5"] },
        { "lastSeenAt", "desc", ["a1", "a5", "a2", "a4", "a3"] },
        { "taskType", "asc", ["a1", "a5", "a3", "a4", "a2"] },
        { "taskType", "desc", ["a2", "a3", "a4", "a1", "a5"] },
        { "sublot", "asc", ["a2", "a5", "a3", "a4", "a1"] },
        { "sublot", "desc", ["a1", "a3", "a4", "a2", "a5"] },
        { "demandId", "asc", ["a3", "a5", "a2", "a1", "a4"] },
        { "demandId", "desc", ["a4", "a1", "a2", "a3", "a5"] },
        { "message", "asc", ["a2", "a5", "a3", "a1", "a4"] },
        { "message", "desc", ["a1", "a4", "a3", "a2", "a5"] },
    };

    [Theory]
    [MemberData(nameof(SortCases))]
    public async Task Host_pages_all_visible_alert_columns_without_gaps_or_duplicates(
        string sortBy,
        string direction,
        string[] expectedAlertIds)
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new FixedAlertStore(CreateAlerts(now));
        var csvPath = Path.Combine(Path.GetTempPath(), $"mes-ingest-alert-sort-{Guid.NewGuid():N}.csv");
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

            var actualAlertIds = new List<string>();
            string? cursor = null;
            for (var pageNumber = 0; pageNumber < 10; pageNumber++)
            {
                var url = $"/api/alerts?limit=2&sortBy={sortBy}&direction={direction}";
                if (cursor is not null)
                {
                    url += $"&cursor={Uri.EscapeDataString(cursor)}";
                }

                using var response = await http.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var page = await response.Content.ReadFromJsonAsync<JsonElement>();
                actualAlertIds.AddRange(page.GetProperty("items")
                    .EnumerateArray()
                    .Select(item => item.GetProperty("alertId").GetString()!));

                if (!page.GetProperty("hasMore").GetBoolean())
                {
                    Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
                    break;
                }

                cursor = page.GetProperty("nextCursor").GetString();
                Assert.False(string.IsNullOrWhiteSpace(cursor));
            }

            Assert.Equal(expectedAlertIds, actualAlertIds);
            Assert.Equal(actualAlertIds.Count, actualAlertIds.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    private static IReadOnlyList<IngestAlert> CreateAlerts(DateTimeOffset now) =>
    [
        Alert("a1", "B", AlertSeverities.Error, null, "S2", "D2", "same", now, now.AddHours(2)),
        Alert("a2", "A", AlertSeverities.Warning, "T2", null, "D1", null, now.AddHours(1), now.AddHours(1)),
        Alert("a3", "A", AlertSeverities.Warning, "T1", "S1", null, "alpha", now.AddHours(1), now),
        Alert("a4", "C", AlertSeverities.Error, "T1", "S1", "D3", "same", now.AddHours(2), now.AddHours(1)),
        Alert("a5", "B", AlertSeverities.Warning, null, null, null, null, now, now.AddHours(2)),
    ];

    private static IngestAlert Alert(
        string alertId,
        string code,
        string severity,
        string? taskType,
        string? sublot,
        string? demandId,
        string? message,
        DateTimeOffset firstSeenAt,
        DateTimeOffset lastSeenAt) =>
        new(
            Code: code,
            TaskType: taskType,
            Sublot: sublot,
            DemandId: demandId,
            Message: message,
            CreatedAt: firstSeenAt,
            AlertId: alertId,
            Severity: severity,
            FirstSeenAt: firstSeenAt,
            LastSeenAt: lastSeenAt);

    private sealed class FixedAlertStore : ITransportDemandStore
    {
        private readonly IReadOnlyList<IngestAlert> _alerts;
        private readonly InMemoryTransportDemandStore _otherState = new();

        public FixedAlertStore(IReadOnlyList<IngestAlert> alerts)
        {
            _alerts = alerts;
        }

        public IReadOnlyList<IngestAlert> ListAlerts(int? limit = null) =>
            QueryAlerts(new AlertListQuery
            {
                Limit = limit ?? AlertListQuery.DefaultLimit,
                UseDefaultPrioritySort = true,
            }).Items;

        public AlertListPage QueryAlerts(AlertListQuery query)
        {
            if (!AlertListCursor.TryDecode(
                    query.Cursor,
                    query.SortBy,
                    query.Direction,
                    out var cursor,
                    out var error))
            {
                throw new ArgumentException(error ?? "cursor is invalid", nameof(query));
            }

            return AlertListPaging.Page(_alerts, query, cursor);
        }

        public ProjectionState GetState() => _otherState.GetState();
        public void ReplaceState(ProjectionState state, IReadOnlyList<IngestAlert>? alerts = null) =>
            _otherState.ReplaceState(state, alerts);
        public bool HasGoneTransportDemandKey(TransportDemandKey key) =>
            _otherState.HasGoneTransportDemandKey(key);
        public string? GetLatestGoneDemandId(TransportDemandKey key) =>
            _otherState.GetLatestGoneDemandId(key);
        public TransportDemand? GetById(string demandId) => _otherState.GetById(demandId);
        public IReadOnlyList<TransportDemand> List(
            DemandStatus? status = null,
            string? taskType = null,
            string? sublot = null,
            string? demandId = null) =>
            _otherState.List(status, taskType, sublot, demandId);
        public DemandListPage QueryPage(DemandListQuery query) => _otherState.QueryPage(query);
        public DemandChangeFeedPage QueryChangeFeed(DemandChangeFeedQuery query) =>
            _otherState.QueryChangeFeed(query);
        public void AppendAlerts(IReadOnlyList<IngestAlert> alerts) =>
            throw new NotSupportedException();
        public void SetLatestPollHealth(PollHealth health) => _otherState.SetLatestPollHealth(health);
        public PollHealth? GetLatestPollHealth() => _otherState.GetLatestPollHealth();
    }
}
