using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

public sealed class WatchWindowFakeHostTests
{
    [Fact]
    [Trait("Category", "watch-vm-tests")]
    public async Task Production_http_client_reads_scripted_window_scenario_without_sensitive_timeline_values()
    {
        var demand = WatchWindowScenarioData.VisibleDemand("journey-demand-sensitive");
        var alert = WatchWindowScenarioData.ActiveAlert("journey-alert", demand.DemandId);
        var scenario = WatchWindowScenario.Healthy(
            visiblePages:
            [
                new WatchDemandPage([demand], "journey-cursor-sensitive", true),
                new WatchDemandPage([], null, false),
            ],
            alerts: [alert]);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await WatchWindowFakeHost.StartAsync(
            scenario,
            cancellationToken: cancellationToken);
        using var client = MesIngestApiClient.CreateForHost(
            new WatchHostSettings(host.BaseUrl, "fake-shared-secret", 10));

        var snapshot = await client.FetchSnapshotAsync(cancellationToken);
        var next = await client.FetchDemandPageAsync(
            WatchDemandBrowseQuery.Default with { Cursor = snapshot.DemandsNextCursor },
            cancellationToken);

        Assert.Equal(demand.DemandId, Assert.Single(snapshot.Demands).DemandId);
        Assert.Equal(alert.AlertId, Assert.Single(snapshot.Alerts).AlertId);
        Assert.True(snapshot.DemandsHasMore);
        Assert.Empty(next.Items);
        Assert.Contains(host.Timeline, static entry => entry.Contains("/api/contract", StringComparison.Ordinal));
        Assert.DoesNotContain(
            host.Timeline,
            entry => entry.Contains("journey-cursor-sensitive", StringComparison.Ordinal)
                || entry.Contains("journey-demand-sensitive", StringComparison.Ordinal)
                || entry.Contains("fake-shared-secret", StringComparison.Ordinal));
    }
}
