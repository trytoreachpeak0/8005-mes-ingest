using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

public sealed class ScriptedFakeHostTests
{
    [Fact]
    public async Task Fake_host_returns_every_scripted_read_shape()
    {
        var demand = FakeDemand("fake-demand-01");
        var alert = FakeAlert("fake-alert-01", demand.DemandId);
        var health = FakeHealth();
        var scenario = new FakeHostScenario("fake-session-alpha")
        {
            PollHealth = FakeHostReply.Return<WatchPollHealthDto?>(health),
            Snapshot = FakeHostReply.Return(new WatchSnapshot(
                [demand],
                [alert],
                health,
                FetchError: null,
                DemandsSucceeded: true,
                AlertsSucceeded: true,
                PollHealthSucceeded: true)),
            DemandPage = FakeHostReply.Return(new WatchDemandPage([demand], "fake-demand-cursor", true)),
            AlertPage = FakeHostReply.Return(new WatchAlertPage([alert], "fake-alert-cursor", true)),
            ExactDemand = FakeHostReply.Return<WatchDemandDto?>(demand),
        };
        var host = new ScriptedFakeHost(scenario);
        using var adapter = host.CreateAdapter(FakeSettings("fake-secret-alpha"));
        var cancellationToken = TestContext.Current.CancellationToken;

        await adapter.VerifyContractAsync(cancellationToken);
        Assert.Same(health, await adapter.FetchPollHealthAsync(cancellationToken));
        Assert.Equal(demand, (await adapter.FetchSnapshotAsync(
            WatchDemandBrowseQuery.Default,
            WatchAlertBrowseQuery.Default,
            cancellationToken)).Demands.Single());
        Assert.Equal("fake-demand-cursor", (await adapter.FetchDemandPageAsync(
            WatchDemandBrowseQuery.Default,
            cancellationToken)).NextCursor);
        Assert.Equal("fake-alert-cursor", (await adapter.FetchAlertPageAsync(
            WatchAlertBrowseQuery.Default,
            cancellationToken)).NextCursor);
        Assert.Equal(demand, await adapter.FetchDemandByIdAsync(demand.DemandId, cancellationToken));
    }

    [Theory]
    [InlineData((int)WatchHostFailureKind.Contract)]
    [InlineData((int)WatchHostFailureKind.Http)]
    [InlineData((int)WatchHostFailureKind.Decode)]
    public async Task Fake_host_surfaces_scripted_failure_categories(int kindValue)
    {
        var kind = (WatchHostFailureKind)kindValue;
        const string secret = "fake-secret-must-be-masked";
        var scenario = new FakeHostScenario("fake-session-failure")
        {
            DemandPage = FakeHostReply.Fail<WatchDemandPage>(
                kind,
                "/api/demands",
                $"fake failure contains {secret}"),
        };
        var host = new ScriptedFakeHost(scenario);
        using var adapter = host.CreateAdapter(FakeSettings(secret));

        var exception = await Assert.ThrowsAsync<WatchHostQueryException>(
            () => adapter.FetchDemandPageAsync(
                WatchDemandBrowseQuery.Default,
                TestContext.Current.CancellationToken));

        Assert.Equal(kind, exception.Kind);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("(masked)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_host_can_report_cursor_expiry_without_recording_cursor_or_credential()
    {
        const string secret = "fake-secret-cursor";
        var scenario = new FakeHostScenario("fake-session-cursor")
        {
            DemandPage = FakeHostReply.CursorExpired<WatchDemandPage>("/api/demands"),
        };
        var host = new ScriptedFakeHost(scenario);
        using var adapter = host.CreateAdapter(FakeSettings(secret));

        var exception = await Assert.ThrowsAsync<WatchHostQueryException>(
            () => adapter.FetchDemandPageAsync(
                WatchDemandBrowseQuery.Default with { Cursor = "fake-sensitive-cursor" },
                TestContext.Current.CancellationToken));

        Assert.Equal(WatchHostFailureKind.Http, exception.Kind);
        var timeline = string.Join(Environment.NewLine, host.Timeline);
        Assert.DoesNotContain(secret, timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-sensitive-cursor", timeline, StringComparison.Ordinal);
        Assert.All(host.Timeline, entry => Assert.Equal("fake-session-cursor", entry.SessionId));
    }

    [Fact]
    public async Task Fake_host_can_fail_contract_before_poll_health()
    {
        var scenario = new FakeHostScenario("fake-session-contract")
        {
            Contract = FakeHostReply.Fail<FakeHostUnit>(
                WatchHostFailureKind.Contract,
                "/api/contract",
                "fake incompatible contract"),
        };
        var host = new ScriptedFakeHost(scenario);
        using var adapter = host.CreateAdapter(FakeSettings("fake-contract-secret"));

        var exception = await Assert.ThrowsAsync<WatchHostQueryException>(
            () => adapter.VerifyContractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(WatchHostFailureKind.Contract, exception.Kind);
        Assert.DoesNotContain(
            host.Timeline,
            entry => entry.Operation == FakeHostOperation.PollHealth);
    }

    [Fact]
    public async Task Fake_host_delay_observes_cancellation()
    {
        var gate = new FakeHostGate();
        var scenario = new FakeHostScenario("fake-session-cancel")
        {
            AlertPage = FakeHostReply.After(
                gate,
                new WatchAlertPage([], null, false)),
        };
        var host = new ScriptedFakeHost(scenario);
        using var adapter = host.CreateAdapter(FakeSettings("fake-cancel-secret"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var request = adapter.FetchAlertPageAsync(
            WatchAlertBrowseQuery.Default,
            cancellation.Token);
        await host.WaitForAsync(
            "fake-session-cancel",
            FakeHostOperation.AlertPage,
            FakeHostRequestState.Started,
            TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Contains(
            host.Timeline,
            entry => entry.SessionId == "fake-session-cancel"
                && entry.Operation == FakeHostOperation.AlertPage
                && entry.State == FakeHostRequestState.Canceled);
    }

    [Fact]
    public async Task Host_session_rejects_a_late_response_from_the_replaced_fake_session()
    {
        var lateGate = new FakeHostGate();
        var oldDemand = FakeDemand("fake-old-demand");
        var oldScenario = new FakeHostScenario("fake-session-old")
        {
            DemandPage = FakeHostReply.After(
                lateGate,
                new WatchDemandPage([oldDemand], null, false),
                completeAfterCancellation: true),
        };
        var newScenario = new FakeHostScenario("fake-session-new");
        var host = new ScriptedFakeHost(oldScenario, newScenario);
        using var session = new WatchHostSession(host.CreateAdapter);
        await session.ApplyAsync(FakeSettings("fake-old-secret", "http://fake-old.test"));
        var lateRequest = session.FetchDemandPageAsync(
            WatchDemandBrowseQuery.Default,
            TestContext.Current.CancellationToken);
        await host.WaitForAsync(
            "fake-session-old",
            FakeHostOperation.DemandPage,
            FakeHostRequestState.Started,
            TestContext.Current.CancellationToken);

        await session.ApplyAsync(FakeSettings("fake-new-secret", "http://fake-new.test"));
        lateGate.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lateRequest);
        Assert.Contains(
            host.Timeline,
            entry => entry.SessionId == "fake-session-old"
                && entry.State == FakeHostRequestState.CompletedAfterCancellation);
        Assert.Contains(
            host.Timeline,
            entry => entry.SessionId == "fake-session-new"
                && entry.Operation == FakeHostOperation.PollHealth
                && entry.State == FakeHostRequestState.Completed);
    }

    private static WatchHostSettings FakeSettings(
        string secret,
        string baseUrl = "http://fake-watch.test") =>
        new(baseUrl, secret, 30);

    private static WatchPollHealthDto FakeHealth() => new(
        StartedAt: DateTimeOffset.Parse("2026-01-02T03:04:05+08:00"),
        EndedAt: DateTimeOffset.Parse("2026-01-02T03:04:06+08:00"),
        DurationMs: 1000,
        RowCount: 1,
        Success: true,
        Outcome: "fake-success",
        TaskTypePauses: []);

    private static WatchDemandDto FakeDemand(string id) => new(
        DemandId: id,
        TaskType: "FAKE_TASK_TYPE",
        Sublot: "FAKE_SUBLOT",
        Area: "FAKE_AREA",
        Eqp: "FAKE_EQP",
        Step: "FAKE_STEP",
        Dates: DateTimeOffset.Parse("2026-01-02T03:04:05+08:00"),
        Package: "FAKE_PACKAGE",
        Status: "VISIBLE",
        MesLastSeenAt: DateTimeOffset.Parse("2026-01-02T03:04:06+08:00"),
        DisappearCount: 0,
        LocationRisk: false,
        LocationRiskCode: null,
        CreatedAt: DateTimeOffset.Parse("2026-01-02T03:04:05+08:00"),
        GoneAt: null);

    private static WatchAlertDto FakeAlert(string id, string demandId) => new(
        AlertId: id,
        Code: "FAKE_ALERT",
        Severity: "WARNING",
        TaskType: "FAKE_TASK_TYPE",
        Sublot: "FAKE_SUBLOT",
        DemandId: demandId,
        Message: "fake alert message",
        Details: "fake alert details",
        FirstSeenAt: DateTimeOffset.Parse("2026-01-02T03:04:05+08:00"),
        LastSeenAt: DateTimeOffset.Parse("2026-01-02T03:04:06+08:00"),
        OccurrenceCount: 1,
        IsActive: true,
        ResolvedAt: null,
        CreatedAt: DateTimeOffset.Parse("2026-01-02T03:04:05+08:00"));
}
