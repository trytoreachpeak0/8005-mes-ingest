using MesIngest.Watch;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch.UiTests;

public sealed class WatchV2WorkspaceSessionTests
{
    [Fact]
    public async Task Scripted_host_connects_through_the_production_v2_http_session_entry()
    {
        const string credential = "scripted-v2-secret";
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("host-a", credential),
            TestContext.Current.CancellationToken);
        using var session = new WatchV2WorkspaceSession();

        await session.ApplyAsync(
            new WatchHostSettings(host.BaseUrl, credential, 30),
            TestContext.Current.CancellationToken);

        Assert.Equal(WatchHostConnectionStatus.Connected, session.State.ConnectionStatus);
        Assert.Equal(1, session.State.HostGeneration);
        Assert.Equal(host.BaseUrl, session.State.BaseUrl);
        var contract = Assert.Single(host.Timeline, entry =>
            entry.Operation == FakeHostOperation.ContractV2
            && entry.State == FakeHostRequestState.Completed);
        Assert.Equal("/api/v2/contract", contract.Endpoint);
        Assert.DoesNotContain(host.Timeline, entry =>
            entry.Endpoint.StartsWith("/api/", StringComparison.Ordinal)
            && !entry.Endpoint.StartsWith("/api/v2/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overview_refresh_uses_the_production_area_query_and_commits_one_snapshot()
    {
        var now = DateTimeOffset.Parse("2026-08-14T09:00:00+08:00");
        var clock = new FixedTimeProvider(now);
        var overview = OverviewSnapshot("commit-a", ["A1-1", "B2-2"]);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("host-a", "secret")
            {
                Overview = FakeHostReply.Return(overview),
            },
            TestContext.Current.CancellationToken);
        using var session = new WatchV2WorkspaceSession(timeProvider: clock);
        await session.ApplyAsync(
            new WatchHostSettings(host.BaseUrl, "secret", 30),
            TestContext.Current.CancellationToken);

        await session.RefreshOverviewAsync(
            new WatchOverviewQuery([" B2-2 ", "A1-1", "B2-2"]),
            TestContext.Current.CancellationToken);

        Assert.Equal("commit-a", session.State.Overview.Snapshot!.Snapshot.ProjectionCommitId);
        Assert.Equal(now, session.State.Overview.LastSuccessfulAt);
        Assert.Equal("area=A1-1&area=B2-2", session.State.Overview.CommittedQueryKey);
        Assert.False(session.State.Overview.IsRefreshing);
        Assert.False(session.State.Overview.IsStale);
        Assert.Contains(host.Timeline, entry =>
            entry.Operation == FakeHostOperation.OverviewV2
            && entry.State == FakeHostRequestState.Completed
            && entry.Endpoint == "/api/v2/watch-overview?area=A1-1&area=B2-2");
    }

    [Fact]
    public async Task Failed_new_query_keeps_the_old_snapshot_and_marks_its_attempt_stale()
    {
        const string credential = "query-secret";
        var successAt = DateTimeOffset.Parse("2026-08-14T09:00:00+08:00");
        var failureAt = successAt.AddMinutes(2);
        var clock = new ManualTimeProvider(successAt);
        await using var host = await ScriptedFakeHost.StartV2Async(
            new FakeHostV2Scenario("host-a", credential)
            {
                Overview = FakeHostReply.Sequence<WatchOverviewQuery, WatchOverviewSnapshot>(
                    FakeHostReply.Return(OverviewSnapshot("commit-old", ["A1-1"])),
                    FakeHostReply.Fail<WatchOverviewSnapshot>(
                        WatchHostFailureKind.ServerQuery,
                        "/api/v2/watch-overview",
                        $"snapshot cursor rejected {credential}")),
            },
            TestContext.Current.CancellationToken);
        using var session = new WatchV2WorkspaceSession(timeProvider: clock);
        await session.ApplyAsync(
            new WatchHostSettings(host.BaseUrl, credential, 30),
            TestContext.Current.CancellationToken);
        await session.RefreshOverviewAsync(
            new WatchOverviewQuery(["A1-1"]),
            TestContext.Current.CancellationToken);
        clock.Set(failureAt);

        await session.RefreshOverviewAsync(
            new WatchOverviewQuery(["B2-2"]),
            TestContext.Current.CancellationToken);

        var state = session.State.Overview;
        Assert.Equal("commit-old", state.Snapshot!.Snapshot.ProjectionCommitId);
        Assert.Equal("area=A1-1", state.CommittedQueryKey);
        Assert.Equal("area=B2-2", state.PendingQueryKey);
        Assert.Equal("area=B2-2", state.FailedQueryKey);
        Assert.Equal(successAt, state.LastSuccessfulAt);
        Assert.Equal(failureAt, state.LastFailureAt);
        Assert.True(state.IsStale);
        Assert.Equal(WatchHostFailureKind.ServerQuery, state.FailureKind);
        Assert.Equal(WatchOverviewErrorCodes.InvalidQuery, state.FailureCode);
        Assert.DoesNotContain(credential, state.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(2, host.Timeline.Count(entry =>
            entry.Operation == FakeHostOperation.OverviewV2
            && entry.State == FakeHostRequestState.Started));
    }

    [Fact]
    public async Task Applying_a_failed_new_host_clears_every_view_and_rejects_the_old_hosts_late_response()
    {
        var testToken = TestContext.Current.CancellationToken;
        var lateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLate = new TaskCompletionSource<WatchOverviewSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var overviewCalls = 0;
        var hostA = new DelegatingV2Client
        {
            Overview = (_, _) => ++overviewCalls == 1
                ? Task.FromResult(OverviewSnapshot("host-a-initial", ["A1-1"]))
                : StartLateAsync(lateStarted, releaseLate),
            DemandSeries = (query, _) => Task.FromResult(DemandSeriesSnapshot(
                "host-a-series",
                query,
                "series-a")),
            DemandSeriesDetail = (seriesId, snapshot, _) => Task.FromResult(
                DemandSeriesDetail(snapshot, seriesId)),
            Audit = (query, _) => Task.FromResult(AuditSnapshot(
                "host-a-audit",
                query,
                "demand-a")),
            ErrorSearch = (query, _) => Task.FromResult(ErrorSnapshot("host-a-error", query)),
            Attention = (query, _) => Task.FromResult(AttentionSnapshot("host-a-attention", query)),
        };
        var hostB = new DelegatingV2Client
        {
            Verify = _ => Task.FromException(new WatchHostQueryException(
                WatchHostFailureKind.Contract,
                "/api/v2/contract",
                "contract-b",
                "CONTRACT_VERSION_MISMATCH",
                errorCode: NewMesIngestContractMismatchException.ErrorCode)),
        };
        using var session = new WatchV2WorkspaceSession(settings =>
            settings.BaseUrl.EndsWith("host-a", StringComparison.Ordinal) ? hostA : hostB);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        await session.RefreshOverviewAsync(new WatchOverviewQuery(["A1-1"]), testToken);
        await session.RefreshDemandSeriesAsync(
            new DemandSeriesBrowseQuery(new DemandSeriesBrowseFilter()),
            testToken);
        session.SetDemandSeriesSelection("series-a");
        await session.LoadSelectedDemandSeriesDetailAsync(testToken);
        await session.RefreshReadabilityAuditAsync(
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
            testToken);
        await session.RefreshErrorSearchAsync(
            new ErrorSearchQuery(new ErrorSearchFilter(), ErrorSearchWindowSelection.Last7Days),
            testToken);
        await session.RefreshCurrentAttentionAsync(new CurrentIngestAttentionQuery(), testToken);
        var lateRefresh = session.RefreshOverviewAsync(
            new WatchOverviewQuery(["B2-2"]),
            testToken);
        await lateStarted.Task.WaitAsync(testToken);

        await session.ApplyAsync(new WatchHostSettings("http://host-b", "b", 30), testToken);

        var failed = session.State;
        Assert.Equal(2, failed.HostGeneration);
        Assert.Equal(WatchHostConnectionStatus.Failed, failed.ConnectionStatus);
        Assert.Equal(WatchHostFailureKind.Contract, failed.FailureKind);
        Assert.Equal(NewMesIngestContractMismatchException.ErrorCode, failed.FailureCode);
        Assert.Null(failed.Overview.Snapshot);
        Assert.Null(failed.DemandSeries.Snapshot);
        Assert.Null(failed.DemandSeries.SelectedId);
        Assert.Null(failed.DemandSeries.Detail);
        Assert.Null(failed.ReadabilityAudit.Snapshot);
        Assert.Null(failed.ErrorSearch.Snapshot);
        Assert.Null(failed.CurrentAttention.Snapshot);
        Assert.All(
            new[]
            {
                failed.Overview.CommittedQueryKey,
                failed.DemandSeries.CommittedQueryKey,
                failed.ReadabilityAudit.CommittedQueryKey,
                failed.ErrorSearch.CommittedQueryKey,
                failed.CurrentAttention.CommittedQueryKey,
            },
            Assert.Null);

        releaseLate.SetResult(OverviewSnapshot("host-a-too-late", ["B2-2"]));
        await lateRefresh;
        Assert.Null(session.State.Overview.Snapshot);
        Assert.Equal(2, session.State.HostGeneration);
    }

    [Fact]
    public async Task A_cancelled_late_request_cannot_overwrite_a_newer_normalized_query()
    {
        var testToken = TestContext.Current.CancellationToken;
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource<WatchOverviewSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DelegatingV2Client
        {
            Overview = (query, _) => query.MesAreas!.Single() switch
            {
                "A1-1" => Task.FromResult(OverviewSnapshot("initial", ["A1-1"])),
                "B2-2" => StartLateAsync(slowStarted, releaseSlow),
                _ => Task.FromResult(OverviewSnapshot("newest", ["C3-3"])),
            },
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        await session.RefreshOverviewAsync(new WatchOverviewQuery(["A1-1"]), testToken);
        var slow = session.RefreshOverviewAsync(new WatchOverviewQuery([" B2-2 "]), testToken);
        await slowStarted.Task.WaitAsync(testToken);
        Assert.True(session.State.Overview.IsRefreshing);
        Assert.True(session.State.Overview.IsStale);
        Assert.Equal("initial", session.State.Overview.Snapshot!.Snapshot.ProjectionCommitId);

        await session.RefreshOverviewAsync(new WatchOverviewQuery(["C3-3"]), testToken);
        releaseSlow.SetResult(OverviewSnapshot("late", ["B2-2"]));
        await slow;

        Assert.Equal("newest", session.State.Overview.Snapshot!.Snapshot.ProjectionCommitId);
        Assert.Equal("area=C3-3", session.State.Overview.CommittedQueryKey);
        Assert.Equal("area=C3-3", session.State.Overview.PendingQueryKey);
        Assert.Equal(3, session.State.Overview.RequestGeneration);
    }

    [Fact]
    public async Task The_same_query_and_request_generation_from_an_old_host_is_still_rejected()
    {
        var testToken = TestContext.Current.CancellationToken;
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource<WatchOverviewSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hostA = new DelegatingV2Client
        {
            Overview = (_, _) => StartLateAsync(oldStarted, releaseOld),
        };
        var hostB = new DelegatingV2Client
        {
            Overview = (_, _) => Task.FromResult(OverviewSnapshot("host-b", ["A1-1"])),
        };
        using var session = new WatchV2WorkspaceSession(settings =>
            settings.BaseUrl.EndsWith("host-a", StringComparison.Ordinal) ? hostA : hostB);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        var oldRefresh = session.RefreshOverviewAsync(
            new WatchOverviewQuery(["A1-1"]),
            testToken);
        await oldStarted.Task.WaitAsync(testToken);

        await session.ApplyAsync(new WatchHostSettings("http://host-b", "b", 30), testToken);
        await session.RefreshOverviewAsync(new WatchOverviewQuery(["A1-1"]), testToken);
        releaseOld.SetResult(OverviewSnapshot("host-a-late", ["A1-1"]));
        await oldRefresh;

        Assert.Equal("host-b", session.State.Overview.Snapshot!.Snapshot.ProjectionCommitId);
        Assert.Equal(2, session.State.HostGeneration);
        Assert.Equal(1, session.State.Overview.RequestGeneration);
        Assert.Equal("area=A1-1", session.State.Overview.CommittedQueryKey);
    }

    [Fact]
    public async Task An_older_request_for_the_same_normalized_query_cannot_overwrite_its_successor()
    {
        var testToken = TestContext.Current.CancellationToken;
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource<WatchOverviewSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var client = new DelegatingV2Client
        {
            Overview = (_, _) => ++calls == 1
                ? StartLateAsync(oldStarted, releaseOld)
                : Task.FromResult(OverviewSnapshot("same-query-newest", ["A1-1"])),
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        var oldRefresh = session.RefreshOverviewAsync(
            new WatchOverviewQuery(["A1-1"]),
            testToken);
        await oldStarted.Task.WaitAsync(testToken);

        await session.RefreshOverviewAsync(new WatchOverviewQuery(["A1-1"]), testToken);
        releaseOld.SetResult(OverviewSnapshot("same-query-late", ["A1-1"]));
        await oldRefresh;

        Assert.Equal(
            "same-query-newest",
            session.State.Overview.Snapshot!.Snapshot.ProjectionCommitId);
        Assert.Equal(2, session.State.Overview.RequestGeneration);
        Assert.Equal("area=A1-1", session.State.Overview.CommittedQueryKey);
    }

    [Fact]
    public async Task Caller_cancellation_is_neutral_and_keeps_the_last_success()
    {
        var testToken = TestContext.Current.CancellationToken;
        var calls = 0;
        var client = new DelegatingV2Client
        {
            Overview = async (_, token) =>
            {
                if (++calls == 1)
                {
                    return OverviewSnapshot("success", ["A1-1"]);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The cancelled delay unexpectedly completed.");
            },
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        await session.RefreshOverviewAsync(new WatchOverviewQuery(["A1-1"]), testToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        var refresh = session.RefreshOverviewAsync(
            new WatchOverviewQuery(["B2-2"]),
            cancellation.Token);

        cancellation.Cancel();
        await refresh;

        var state = session.State.Overview;
        Assert.Equal("success", state.Snapshot!.Snapshot.ProjectionCommitId);
        Assert.False(state.IsRefreshing);
        Assert.Null(state.LastFailureAt);
        Assert.Equal(WatchHostFailureKind.None, state.FailureKind);
    }

    [Fact]
    public async Task Caller_cancellation_while_connecting_terminates_and_disposes_that_generation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DelegatingV2Client
        {
            Verify = async _ =>
            {
                started.SetResult();
                await release.Task;
            },
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var apply = session.ApplyAsync(
            new WatchHostSettings("http://host-a", "a", 30),
            cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        Assert.Equal(WatchHostConnectionStatus.Failed, session.State.ConnectionStatus);
        Assert.Equal(WatchHostFailureKind.Canceled, session.State.FailureKind);
        Assert.Equal("REQUEST_CANCELED", session.State.FailureCode);
        Assert.True(client.WasDisposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RefreshOverviewAsync(
            new WatchOverviewQuery(["A1-1"]),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_business_snapshot_with_another_contract_is_rejected_without_replacing_success()
    {
        var calls = 0;
        var client = new DelegatingV2Client
        {
            Overview = (_, _) => Task.FromResult(++calls == 1
                ? OverviewSnapshot("compatible", ["A1-1"])
                : OverviewSnapshot("incompatible", ["A1-1"], "future-contract")),
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        var cancellationToken = TestContext.Current.CancellationToken;
        await session.ApplyAsync(
            new WatchHostSettings("http://host-a", "a", 30),
            cancellationToken);
        await session.RefreshOverviewAsync(new WatchOverviewQuery(["A1-1"]), cancellationToken);

        await session.RefreshOverviewAsync(new WatchOverviewQuery(["A1-1"]), cancellationToken);

        var state = session.State.Overview;
        Assert.Equal("compatible", state.Snapshot!.Snapshot.ProjectionCommitId);
        Assert.True(state.IsStale);
        Assert.Equal(WatchHostFailureKind.Contract, state.FailureKind);
        Assert.Equal(NewMesIngestContractMismatchException.ErrorCode, state.FailureCode);
    }

    [Fact]
    public async Task Latest_series_refresh_acquires_a_new_snapshot_before_reopening_the_current_page()
    {
        var token = TestContext.Current.CancellationToken;
        var requests = new List<DemandSeriesBrowseQuery>();
        var targetPageStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTargetPage = new TaskCompletionSource<DemandSeriesListSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DelegatingV2Client
        {
            DemandSeries = (query, _) =>
            {
                requests.Add(query);
                return requests.Count switch
                {
                    1 => Task.FromResult(DemandSeriesSnapshot("old", query, "series-old") with
                    {
                        PageNumber = 3,
                        TotalPages = 5,
                    }),
                    2 => Task.FromResult(DemandSeriesSnapshot("latest", query, "series-first") with
                    {
                        PageNumber = 1,
                        TotalPages = 5,
                        HasMore = true,
                        NextCursor = "latest-page-two",
                    }),
                    3 => WaitForTargetPageAsync(query),
                    _ => throw new InvalidOperationException("Unexpected DemandSeries request."),
                };
            },
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), token);
        var oldPage = new DemandSeriesBrowseQuery(
            new DemandSeriesBrowseFilter { MesAreas = ["A1-1"] },
            PageSize: 100,
            PageNumber: 3,
            SnapshotReference: "snapshot-old");
        await session.RefreshDemandSeriesAsync(oldPage, token);

        var refresh = session.RefreshLatestDemandSeriesPageAsync(
            oldPage with { SnapshotReference = null },
            token);
        await targetPageStarted.Task.WaitAsync(token);

        Assert.Equal("old", session.State.DemandSeries.Snapshot!.Snapshot.ProjectionCommitId);
        Assert.True(session.State.DemandSeries.IsRefreshing);
        Assert.Equal(3, requests.Count);
        Assert.Equal(1, requests[1].PageNumber);
        Assert.Null(requests[1].SnapshotReference);
        Assert.Equal(3, requests[2].PageNumber);
        Assert.Equal("snapshot-latest", requests[2].SnapshotReference);

        releaseTargetPage.SetResult(DemandSeriesSnapshot(
            "latest",
            requests[2],
            "series-latest-page-three") with
        {
            PageNumber = 3,
            TotalPages = 5,
        });
        await refresh;

        Assert.Equal("latest", session.State.DemandSeries.Snapshot!.Snapshot.ProjectionCommitId);
        Assert.Equal(3, session.State.DemandSeries.Snapshot.PageNumber);
        Assert.Equal("series-latest-page-three", session.State.DemandSeries.Snapshot.Items.Single().SeriesId);
        Assert.False(session.State.DemandSeries.IsRefreshing);

        async Task<DemandSeriesListSnapshot> WaitForTargetPageAsync(
            DemandSeriesBrowseQuery query)
        {
            Assert.Equal("snapshot-latest", query.SnapshotReference);
            targetPageStarted.SetResult();
            return await releaseTargetPage.Task.ConfigureAwait(false);
        }
    }

    [Theory]
    [InlineData("Authentication", "UNAUTHORIZED")]
    [InlineData("Contract", "CONTRACT_VERSION_MISMATCH")]
    [InlineData("Network", null)]
    public async Task Connection_failures_are_distinct_observable_states(
        string kindName,
        string? code)
    {
        var kind = Enum.Parse<WatchHostFailureKind>(kindName);
        var client = new DelegatingV2Client
        {
            Verify = _ => Task.FromException(new WatchHostQueryException(
                kind,
                "/api/v2/contract",
                "failure-correlation",
                "safe failure",
                errorCode: code)),
        };
        using var session = new WatchV2WorkspaceSession(_ => client);

        await session.ApplyAsync(
            new WatchHostSettings("http://failed-host", "secret", 30),
            TestContext.Current.CancellationToken);

        Assert.Equal(WatchHostConnectionStatus.Failed, session.State.ConnectionStatus);
        Assert.Equal(kind, session.State.FailureKind);
        Assert.Equal(code, session.State.FailureCode);
        Assert.Equal("failure-correlation", session.State.CorrelationId);
    }

    [Fact]
    public async Task Series_and_demand_selection_relocate_or_clear_after_an_atomic_refresh()
    {
        var testToken = TestContext.Current.CancellationToken;
        var demandSeriesCalls = 0;
        var auditCalls = 0;
        var client = new DelegatingV2Client
        {
            DemandSeries = (query, _) => Task.FromResult(++demandSeriesCalls switch
            {
                1 => DemandSeriesSnapshot("ds-1", query, "series-a"),
                2 => DemandSeriesSnapshot("ds-2", query, "series-a"),
                _ => DemandSeriesSnapshot("ds-3", query, "series-b"),
            }),
            DemandSeriesDetail = (seriesId, snapshot, _) => snapshot == "snapshot-ds-3"
                ? Task.FromException<DemandSeriesDetailSnapshot>(ObjectNotInSnapshot(
                    "/api/v2/demand-series/{seriesId}",
                    DemandSeriesBrowseErrorCodes.ObjectNotInSnapshot))
                : Task.FromResult(DemandSeriesDetail(snapshot, seriesId)),
            Audit = (query, _) => Task.FromResult(++auditCalls switch
            {
                1 => AuditSnapshot("audit-1", query, "demand-a"),
                2 => AuditSnapshot("audit-2", query, "demand-a"),
                _ => AuditSnapshot("audit-3", query, "demand-b"),
            }),
            AuditDetail = (demandId, snapshot, _) => snapshot == "snapshot-audit-3"
                ? Task.FromException<ReadabilityAuditDetailSnapshot>(ObjectNotInSnapshot(
                    "/api/v2/readability-audit/{demandId}",
                    ReadabilityAuditErrorCodes.ObjectNotInSnapshot))
                : Task.FromResult(AuditDetail(snapshot, demandId)),
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        var seriesQuery = new DemandSeriesBrowseQuery(new DemandSeriesBrowseFilter());
        var auditQuery = new ReadabilityAuditQuery(new ReadabilityAuditFilter());
        await session.RefreshDemandSeriesAsync(seriesQuery, testToken);
        session.SetDemandSeriesSelection("series-a");
        await session.LoadSelectedDemandSeriesDetailAsync(testToken);
        await session.RefreshReadabilityAuditAsync(auditQuery, testToken);
        await session.SelectReadabilityDemandAsync("demand-a", testToken);

        await session.RefreshDemandSeriesAsync(seriesQuery, testToken);
        await session.LoadSelectedDemandSeriesDetailAsync(testToken);
        await session.RefreshReadabilityAuditAsync(auditQuery, testToken);

        Assert.Equal("series-a", session.State.DemandSeries.SelectedId);
        Assert.Equal("ds-2", session.State.DemandSeries.Detail!.Snapshot.ProjectionCommitId);
        Assert.Equal("demand-a", session.State.ReadabilityAudit.SelectedId);
        Assert.Equal("audit-2", session.State.ReadabilityAudit.Detail!.Snapshot.ProjectionCommitId);

        await session.RefreshDemandSeriesAsync(seriesQuery, testToken);
        await session.RefreshReadabilityAuditAsync(auditQuery, testToken);

        Assert.Null(session.State.DemandSeries.SelectedId);
        Assert.Null(session.State.DemandSeries.Detail);
        Assert.Equal(
            WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
            session.State.DemandSeries.SelectionNotice);
        Assert.Null(session.State.ReadabilityAudit.SelectedId);
        Assert.Null(session.State.ReadabilityAudit.Detail);
        Assert.Equal(
            WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
            session.State.ReadabilityAudit.SelectionNotice);
    }

    [Fact]
    public async Task Audit_selection_is_retained_when_same_snapshot_detail_exists_outside_the_refreshed_page()
    {
        var testToken = TestContext.Current.CancellationToken;
        var auditCalls = 0;
        var detailSnapshots = new List<string>();
        var client = new DelegatingV2Client
        {
            Audit = (query, _) => Task.FromResult(++auditCalls == 1
                ? AuditSnapshot("audit-1", query, "demand-selected")
                : AuditSnapshot("audit-2", query, "demand-current-page")),
            AuditDetail = (demandId, snapshot, _) =>
            {
                detailSnapshots.Add(snapshot);
                return Task.FromResult(AuditDetail(snapshot, demandId));
            },
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        var query = new ReadabilityAuditQuery(new ReadabilityAuditFilter());
        await session.RefreshReadabilityAuditAsync(query, testToken);
        await session.SelectReadabilityDemandAsync("demand-selected", testToken);

        await session.RefreshReadabilityAuditAsync(query, testToken);

        var refreshed = Assert.IsType<ReadabilityAuditListSnapshot>(
            session.State.ReadabilityAudit.Snapshot);
        Assert.Equal("snapshot-audit-2", refreshed.SnapshotReference);
        Assert.DoesNotContain(refreshed.Items, item => item.DemandId == "demand-selected");
        Assert.Equal("demand-selected", session.State.ReadabilityAudit.SelectedId);
        var detail = Assert.IsType<ReadabilityAuditDetailSnapshot>(
            session.State.ReadabilityAudit.Detail);
        Assert.Equal("demand-selected", detail.Demand.DemandId);
        Assert.Equal(refreshed.SnapshotReference, detail.SnapshotReference);
        Assert.Equal(["snapshot-audit-1", "snapshot-audit-2"], detailSnapshots);
        Assert.Null(session.State.ReadabilityAudit.SelectionNotice);
    }

    [Fact]
    public async Task Audit_refresh_retains_previous_snapshot_when_off_page_detail_fails_without_object_not_in_snapshot()
    {
        var testToken = TestContext.Current.CancellationToken;
        var auditCalls = 0;
        var client = new DelegatingV2Client
        {
            Audit = (query, _) => Task.FromResult(++auditCalls == 1
                ? AuditSnapshot("audit-1", query, "demand-selected")
                : AuditSnapshot("audit-2", query, "demand-current-page")),
            AuditDetail = (demandId, snapshot, _) => snapshot == "snapshot-audit-2"
                ? Task.FromException<ReadabilityAuditDetailSnapshot>(new WatchHostQueryException(
                    WatchHostFailureKind.ServerQuery,
                    "/api/v2/readability-audit/{demandId}",
                    "audit-detail-failure",
                    "The audit snapshot is no longer retained.",
                    errorCode: ReadabilityAuditErrorCodes.SnapshotNotFound))
                : Task.FromResult(AuditDetail(snapshot, demandId)),
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        var query = new ReadabilityAuditQuery(new ReadabilityAuditFilter());
        await session.RefreshReadabilityAuditAsync(query, testToken);
        await session.SelectReadabilityDemandAsync("demand-selected", testToken);

        await session.RefreshReadabilityAuditAsync(query, testToken);

        Assert.True(session.State.ReadabilityAudit.IsStale);
        Assert.Equal(
            ReadabilityAuditErrorCodes.SnapshotNotFound,
            session.State.ReadabilityAudit.FailureCode);
        Assert.Equal(
            "snapshot-audit-1",
            session.State.ReadabilityAudit.Snapshot!.SnapshotReference);
        Assert.Equal("demand-selected", session.State.ReadabilityAudit.SelectedId);
        Assert.Equal(
            "snapshot-audit-1",
            session.State.ReadabilityAudit.Detail!.SnapshotReference);
        Assert.Null(session.State.ReadabilityAudit.SelectionNotice);
    }

    [Fact]
    public async Task A_selection_changed_during_detail_refresh_is_relocated_on_the_new_snapshot()
    {
        var testToken = TestContext.Current.CancellationToken;
        var oldSelectionDetailStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldSelectionDetail = new TaskCompletionSource<DemandSeriesDetailSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var listCalls = 0;
        var client = new DelegatingV2Client
        {
            DemandSeries = (query, _) => Task.FromResult(++listCalls == 1
                ? DemandSeriesSnapshot("ds-1", query, "series-a", "series-b")
                : DemandSeriesSnapshot("ds-2", query, "series-a", "series-b")),
            DemandSeriesDetail = (seriesId, snapshot, _) =>
                snapshot == "snapshot-ds-2" && seriesId == "series-a"
                    ? StartLateDetailAsync(oldSelectionDetailStarted, releaseOldSelectionDetail)
                    : Task.FromResult(DemandSeriesDetail(snapshot, seriesId)),
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        var query = new DemandSeriesBrowseQuery(new DemandSeriesBrowseFilter());
        await session.RefreshDemandSeriesAsync(query, testToken);
        session.SetDemandSeriesSelection("series-a");
        await session.LoadSelectedDemandSeriesDetailAsync(testToken);

        await session.RefreshDemandSeriesAsync(query, testToken);
        var refresh = session.LoadSelectedDemandSeriesDetailAsync(testToken);
        await oldSelectionDetailStarted.Task.WaitAsync(testToken);
        session.SetDemandSeriesSelection("series-b");
        await session.LoadSelectedDemandSeriesDetailAsync(testToken);
        Assert.Equal("series-b", session.State.DemandSeries.SelectedId);
        Assert.Equal(
            "ds-2",
            session.State.DemandSeries.Detail!.Snapshot.ProjectionCommitId);

        releaseOldSelectionDetail.SetResult(
            DemandSeriesDetail("snapshot-ds-2", "series-a"));
        await refresh;

        Assert.Equal("series-b", session.State.DemandSeries.SelectedId);
        Assert.Equal(
            "ds-2",
            session.State.DemandSeries.Detail!.Snapshot.ProjectionCommitId);
        Assert.Null(session.State.DemandSeries.SelectionNotice);
    }

    [Fact]
    public async Task An_older_detail_for_the_same_selection_cannot_overwrite_its_successor()
    {
        var testToken = TestContext.Current.CancellationToken;
        var olderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOlder = new TaskCompletionSource<DemandSeriesDetailSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new DemandSeriesBrowseQuery(new DemandSeriesBrowseFilter());
        var older = DemandSeriesDetail("snapshot-ds-1", "series-a");
        var newer = DemandSeriesDetail("snapshot-ds-1", "series-a");
        var detailCalls = 0;
        var client = new DelegatingV2Client
        {
            DemandSeries = (_, _) => Task.FromResult(
                DemandSeriesSnapshot("ds-1", query, "series-a")),
            DemandSeriesDetail = (_, _, _) => ++detailCalls == 1
                ? StartLateDetailAsync(olderStarted, releaseOlder)
                : Task.FromResult(newer),
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        await session.RefreshDemandSeriesAsync(query, testToken);
        session.SetDemandSeriesSelection("series-a");
        var first = session.LoadSelectedDemandSeriesDetailAsync(testToken);
        await olderStarted.Task.WaitAsync(testToken);

        await session.LoadSelectedDemandSeriesDetailAsync(testToken);
        releaseOlder.SetResult(older);
        await first;

        Assert.Same(newer, session.State.DemandSeries.Detail);
        Assert.Equal(3, session.State.DemandSeries.SelectionGeneration);
    }

    [Fact]
    public async Task Error_detail_failure_is_isolated_from_the_list_and_a_retry_clears_detail_failure()
    {
        var testToken = TestContext.Current.CancellationToken;
        var detailStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new ErrorSearchQuery(
            new ErrorSearchFilter(),
            ErrorSearchWindowSelection.Last15Days);
        var page = WatchErrorSearchProductionIntegrationTests.CreateErrorPage(
            query,
            "error-detail-session-22",
            pageNumber: 1,
            totalPages: 1,
            totalSeriesCount: 1);
        var successfulDetail = WatchErrorSearchProductionIntegrationTests.CreateErrorDetail(
            page.Filter,
            ErrorSearchWindowKinds.Last15Days) with
        {
            SnapshotReference = page.SnapshotReference,
            Snapshot = page.Snapshot,
            Window = page.Window,
        };
        var detailCalls = 0;
        var client = new DelegatingV2Client
        {
            ErrorSearch = (_, _) => Task.FromResult(page),
            ErrorDetail = async (_, _, _) =>
            {
                if (Interlocked.Increment(ref detailCalls) == 1)
                {
                    detailStarted.SetResult();
                    await releaseFailure.Task.ConfigureAwait(false);
                    throw new WatchHostQueryException(
                        WatchHostFailureKind.ServerQuery,
                        "/api/v2/error-search/{seriesId}",
                        "correlation-error-detail-22",
                        "The selected Error Search detail failed.",
                        errorCode: "DETAIL_FAILED");
                }

                return successfulDetail;
            },
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        await session.RefreshErrorSearchAsync(query, testToken);
        var seriesId = page.Items.Single().SeriesId;

        var firstSelection = session.SelectErrorSeriesAsync(seriesId, testToken);
        await detailStarted.Task.WaitAsync(testToken);
        var loading = session.State.ErrorSearch;
        Assert.True(loading.IsDetailLoading);
        Assert.Null(loading.DetailLastFailureAt);
        Assert.Null(loading.LastFailureAt);
        Assert.False(loading.IsStale);

        releaseFailure.SetResult();
        await firstSelection;
        var failed = session.State.ErrorSearch;
        Assert.False(failed.IsDetailLoading);
        Assert.Null(failed.Detail);
        Assert.NotNull(failed.DetailLastFailureAt);
        Assert.Equal(WatchHostFailureKind.ServerQuery, failed.DetailFailureKind);
        Assert.Equal("DETAIL_FAILED", failed.DetailFailureCode);
        Assert.Equal("correlation-error-detail-22", failed.DetailCorrelationId);
        Assert.Null(failed.LastFailureAt);
        Assert.Equal(WatchHostFailureKind.None, failed.FailureKind);
        Assert.False(failed.IsStale);
        Assert.Same(page, failed.Snapshot);

        await session.SelectErrorSeriesAsync(seriesId, testToken);
        var recovered = session.State.ErrorSearch;
        Assert.Same(successfulDetail, recovered.Detail);
        Assert.False(recovered.IsDetailLoading);
        Assert.Null(recovered.DetailLastFailureAt);
        Assert.Equal(WatchHostFailureKind.None, recovered.DetailFailureKind);
        Assert.Null(recovered.DetailFailureCode);
        Assert.Null(recovered.DetailErrorMessage);
        Assert.Null(recovered.DetailEndpoint);
        Assert.Null(recovered.DetailCorrelationId);
        Assert.Null(recovered.LastFailureAt);
        Assert.False(recovered.IsStale);
    }

    [Fact]
    public async Task Error_selection_rejects_a_detail_for_another_series_without_staling_the_list()
    {
        var testToken = TestContext.Current.CancellationToken;
        var query = new ErrorSearchQuery(
            new ErrorSearchFilter(),
            ErrorSearchWindowSelection.Last15Days);
        var page = WatchErrorSearchProductionIntegrationTests.CreateErrorPage(
            query,
            "error-detail-identity-22",
            pageNumber: 1,
            totalPages: 1,
            totalSeriesCount: 1);
        var mismatchedDetail = WatchErrorSearchProductionIntegrationTests.CreateErrorDetail(
            page.Filter,
            ErrorSearchWindowKinds.Last15Days) with
        {
            SnapshotReference = page.SnapshotReference,
            Snapshot = page.Snapshot,
            Window = page.Window,
            Order = page.Order,
            Series = page.Items.Single() with { SeriesId = "another-series-22" },
        };
        var client = new DelegatingV2Client
        {
            ErrorSearch = (_, _) => Task.FromResult(page),
            ErrorDetail = (_, _, _) => Task.FromResult(mismatchedDetail),
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        await session.RefreshErrorSearchAsync(query, testToken);
        var selectedId = page.Items.Single().SeriesId;

        await session.SelectErrorSeriesAsync(selectedId, testToken);

        var state = session.State.ErrorSearch;
        Assert.Same(page, state.Snapshot);
        Assert.Equal(selectedId, state.SelectedId);
        Assert.Null(state.Detail);
        Assert.False(state.IsDetailLoading);
        Assert.NotNull(state.DetailLastFailureAt);
        Assert.Equal(WatchHostFailureKind.Decode, state.DetailFailureKind);
        Assert.Contains("does not match", state.DetailErrorMessage, StringComparison.Ordinal);
        Assert.Null(state.LastFailureAt);
        Assert.Equal(WatchHostFailureKind.None, state.FailureKind);
        Assert.False(state.IsStale);
    }

    [Theory]
    [InlineData("snapshot-reference")]
    [InlineData("snapshot-identity")]
    [InlineData("filter")]
    [InlineData("window")]
    [InlineData("order")]
    public async Task Error_refresh_rejects_a_relocated_detail_that_does_not_match_the_new_atomic_snapshot(
        string mismatch)
    {
        var testToken = TestContext.Current.CancellationToken;
        var query = new ErrorSearchQuery(
            new ErrorSearchFilter(),
            ErrorSearchWindowSelection.Last15Days);
        var oldPage = WatchErrorSearchProductionIntegrationTests.CreateErrorPage(
            query,
            "error-atomic-old-22",
            pageNumber: 1,
            totalPages: 1,
            totalSeriesCount: 1);
        var newPage = WatchErrorSearchProductionIntegrationTests.CreateErrorPage(
            query,
            "error-atomic-new-22",
            pageNumber: 1,
            totalPages: 1,
            totalSeriesCount: 1);
        var oldDetail = MatchingErrorDetail(oldPage);
        var matchingNewDetail = MatchingErrorDetail(newPage);
        var mismatchedNewDetail = mismatch switch
        {
            "snapshot-reference" => matchingNewDetail with
            {
                SnapshotReference = "error-atomic-unrequested-22",
            },
            "snapshot-identity" => matchingNewDetail with
            {
                Snapshot = matchingNewDetail.Snapshot with
                {
                    ProjectionSequence = matchingNewDetail.Snapshot.ProjectionSequence + 1,
                },
            },
            "filter" => matchingNewDetail with
            {
                Filter = matchingNewDetail.Filter with { SeriesId = "another-filter-series-22" },
            },
            "window" => matchingNewDetail with
            {
                Window = matchingNewDetail.Window with
                {
                    ToUtc = matchingNewDetail.Window.ToUtc.AddTicks(1),
                },
            },
            "order" => matchingNewDetail with { Order = "UNREQUESTED_ERROR_ORDER" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch), mismatch, null),
        };
        var listCalls = 0;
        var client = new DelegatingV2Client
        {
            ErrorSearch = (_, _) => Task.FromResult(
                Interlocked.Increment(ref listCalls) == 1 ? oldPage : newPage),
            ErrorDetail = (_, snapshotReference, _) => Task.FromResult(
                snapshotReference == oldPage.SnapshotReference
                    ? oldDetail
                    : mismatchedNewDetail),
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        await session.RefreshErrorSearchAsync(query, testToken);
        var selectedId = oldPage.Items.Single().SeriesId;
        await session.SelectErrorSeriesAsync(selectedId, testToken);

        await session.RefreshErrorSearchAsync(query, testToken);

        var state = session.State.ErrorSearch;
        Assert.Same(oldPage, state.Snapshot);
        Assert.Same(oldDetail, state.Detail);
        Assert.Equal(selectedId, state.SelectedId);
        Assert.False(state.IsRefreshing);
        Assert.True(state.IsStale);
        Assert.NotNull(state.LastFailureAt);
        Assert.Equal(WatchHostFailureKind.Decode, state.FailureKind);
        Assert.Contains("does not match", state.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(state.DetailLastFailureAt);
        Assert.Equal(WatchHostFailureKind.None, state.DetailFailureKind);

        static ErrorSearchDetailSnapshot MatchingErrorDetail(ErrorSearchListSnapshot page) =>
            WatchErrorSearchProductionIntegrationTests.CreateErrorDetail(
                page.Filter,
                ErrorSearchWindowKinds.Last15Days) with
            {
                SnapshotReference = page.SnapshotReference,
                Snapshot = page.Snapshot,
                Filter = page.Filter,
                Window = page.Window,
                Order = page.Order,
                Series = page.Items.Single(),
            };
    }

    [Fact]
    public async Task Demand_detail_caller_cancellation_remains_neutral_with_new_detail_state()
    {
        var testToken = TestContext.Current.CancellationToken;
        var detailStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new DemandSeriesBrowseQuery(new DemandSeriesBrowseFilter());
        var client = new DelegatingV2Client
        {
            DemandSeries = (_, _) => Task.FromResult(
                DemandSeriesSnapshot("detail-cancel", query, "series-a")),
            DemandSeriesDetail = async (_, _, token) =>
            {
                detailStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                throw new InvalidOperationException("The canceled detail unexpectedly completed.");
            },
        };
        using var session = new WatchV2WorkspaceSession(_ => client);
        await session.ApplyAsync(new WatchHostSettings("http://host-a", "a", 30), testToken);
        await session.RefreshDemandSeriesAsync(query, testToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testToken);

        session.SetDemandSeriesSelection("series-a");
        var selection = session.LoadSelectedDemandSeriesDetailAsync(cancellation.Token);
        await detailStarted.Task.WaitAsync(testToken);
        Assert.True(session.State.DemandSeries.IsDetailLoading);
        cancellation.Cancel();
        await selection;

        var canceled = session.State.DemandSeries;
        Assert.False(canceled.IsDetailLoading);
        Assert.Null(canceled.LastFailureAt);
        Assert.Equal(WatchHostFailureKind.None, canceled.FailureKind);
        Assert.Null(canceled.DetailLastFailureAt);
        Assert.Equal(WatchHostFailureKind.None, canceled.DetailFailureKind);
        Assert.Null(canceled.FailureCode);
        Assert.Null(canceled.DetailFailureCode);
    }

    private static WatchOverviewSnapshot OverviewSnapshot(
        string projectionCommitId,
        IReadOnlyList<string> areas,
        string contractVersion = NewMesIngestContract.Version)
    {
        var at = DateTimeOffset.Parse("2026-08-14T01:00:00Z");
        var identity = new OperationalSnapshotIdentity(
            projectionCommitId,
            18,
            at,
            "poll-a",
            19,
            5,
            at,
            contractVersion);
        var series = new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries);
        var audit = new OverviewNavigationIntent(OverviewNavigationTargets.ReadabilityAudit);
        var errors = new OverviewNavigationIntent(OverviewNavigationTargets.ErrorSearch);
        var attention = new OverviewNavigationIntent(OverviewNavigationTargets.CurrentIngestAttention);
        return new WatchOverviewSnapshot(
            identity,
            areas,
            new WatchOverviewSeriesSummary(2, 2, 0, 0, 0, series, series, series, series, series),
            new WatchOverviewReadabilitySummary(2, 2, 0, audit, audit, audit),
            new WatchOverviewErrorSummary(0, 0, errors, errors, errors),
            new WatchOverviewAttentionSummary(0, [], [], attention),
            [],
            WatchOverviewRecentActivityStates.NoRecentHighlights,
            WatchOverviewRecentActivityStates.NoRecentHighlightsMessage);
    }

    private static DemandSeriesListSnapshot DemandSeriesSnapshot(
        string commit,
        DemandSeriesBrowseQuery query,
        params string[] seriesIds)
    {
        var at = DateTimeOffset.Parse("2026-08-14T01:00:00Z");
        var identity = new DemandSeriesSnapshotIdentity(
            HistoryEpoch.CreateNew(), commit, 1, at, "poll-a");
        var items = seriesIds.Select(seriesId => new DemandSeriesListItemSnapshot(
                seriesId,
                "WT",
                "SUBLOT",
                "TRACKING",
                "VISIBLE",
                at,
                null,
                $"demand-{seriesId}",
                1,
                "ACTIVE",
                at,
                null,
                null,
                ExternalReadabilityStates.Readable,
                [],
                1,
                "poll-a",
                commit))
            .ToArray();
        return new DemandSeriesListSnapshot(
            identity,
            $"snapshot-{commit}",
            query.Filter,
            query.Order,
            items.Length,
            new DemandSeriesFacets(items.Length, 0, items.Length, 0, 0),
            query.PageSize,
            query.PageNumber,
            1,
            items,
            null,
            false);
    }

    private static DemandSeriesDetailSnapshot DemandSeriesDetail(
        string snapshotReference,
        string seriesId)
    {
        var commit = snapshotReference["snapshot-".Length..];
        var identity = new DemandSeriesSnapshotIdentity(
            HistoryEpoch.CreateNew(),
            commit,
            1,
            DateTimeOffset.Parse("2026-08-14T01:00:00Z"),
            "poll-a");
        return new DemandSeriesDetailSnapshot(identity, snapshotReference, null!);
    }

    private static ReadabilityAuditListSnapshot AuditSnapshot(
        string commit,
        ReadabilityAuditQuery query,
        string demandId)
    {
        var at = DateTimeOffset.Parse("2026-08-14T01:00:00Z");
        var identity = new ReadabilityAuditSnapshotIdentity(
            HistoryEpoch.CreateNew(), commit, 1, at, "poll-a", 1);
        var item = new ReadabilityAuditListItemSnapshot(
            demandId,
            "series-a",
            "WT",
            "SUBLOT",
            1,
            null,
            "ACTIVE",
            "TRACKING",
            "VISIBLE",
            true,
            at,
            at,
            null,
            null,
            1,
            ExternalReadabilityStates.Readable,
            null,
            [],
            "poll-a",
            commit,
            at);
        return new ReadabilityAuditListSnapshot(
            identity,
            $"snapshot-{commit}",
            query.Filter,
            query.Order,
            1,
            new ReadabilityAuditFacets([], []),
            query.PageSize,
            query.PageNumber,
            1,
            [item],
            null,
            false);
    }

    private static ReadabilityAuditDetailSnapshot AuditDetail(
        string snapshotReference,
        string demandId)
    {
        var commit = snapshotReference["snapshot-".Length..];
        var at = DateTimeOffset.Parse("2026-08-14T01:00:00Z");
        var identity = new ReadabilityAuditSnapshotIdentity(
            HistoryEpoch.CreateNew(), commit, 1, at, "poll-a", 1);
        var item = AuditSnapshot(
            commit,
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
            demandId).Items.Single();
        return new ReadabilityAuditDetailSnapshot(
            identity,
            snapshotReference,
            item,
            null!,
            [],
            [],
            [],
            null!);
    }

    private static ErrorSearchListSnapshot ErrorSnapshot(
        string commit,
        ErrorSearchQuery query)
    {
        var at = DateTimeOffset.Parse("2026-08-14T01:00:00Z");
        return new ErrorSearchListSnapshot(
            $"snapshot-{commit}",
            new ErrorSearchSnapshotIdentity(
                HistoryEpoch.FromGuid(Guid.Parse("99999999-9999-9999-9999-999999999999")),
                at,
                commit,
                1,
                at,
                "poll-a"),
            query.Filter,
            new ErrorSearchResolvedWindow(query.Window.Kind, at.AddDays(-7), at),
            query.Order,
            0,
            new ErrorSearchFacets([], []),
            query.PageSize,
            1,
            0,
            [],
            null,
            false);
    }

    private static CurrentIngestAttentionSnapshot AttentionSnapshot(
        string commit,
        CurrentIngestAttentionQuery query)
    {
        var at = DateTimeOffset.Parse("2026-08-14T01:00:00Z");
        return new CurrentIngestAttentionSnapshot(
            new OperationalSnapshotIdentity(commit, 1, at, "poll-a", 1, 1, at),
            0,
            new CurrentIngestAttentionFacets([], []),
            query.Order,
            query.PageSize,
            query.PageNumber,
            0,
            query.Kinds ?? [],
            query.Severities ?? [],
            []);
    }

    private static async Task<WatchOverviewSnapshot> StartLateAsync(
        TaskCompletionSource started,
        TaskCompletionSource<WatchOverviewSnapshot> release)
    {
        started.SetResult();
        return await release.Task.ConfigureAwait(false);
    }

    private static async Task<DemandSeriesDetailSnapshot> StartLateDetailAsync(
        TaskCompletionSource started,
        TaskCompletionSource<DemandSeriesDetailSnapshot> release)
    {
        started.SetResult();
        return await release.Task.ConfigureAwait(false);
    }

    private static WatchHostQueryException ObjectNotInSnapshot(
        string endpoint,
        string errorCode) => new(
        WatchHostFailureKind.ServerQuery,
        endpoint,
        "object-not-in-snapshot",
        "The selected object is not present in the frozen snapshot.",
        errorCode: errorCode);

    private sealed class DelegatingV2Client : IWatchV2ApiClient
    {
        public bool WasDisposed { get; private set; }
        public Func<CancellationToken, Task> Verify { get; init; } = _ => Task.CompletedTask;
        public Func<WatchOverviewQuery, CancellationToken, Task<WatchOverviewSnapshot>>? Overview { get; init; }
        public Func<DemandSeriesBrowseQuery, CancellationToken, Task<DemandSeriesListSnapshot>>? DemandSeries { get; init; }
        public Func<string, string, CancellationToken, Task<DemandSeriesDetailSnapshot>>? DemandSeriesDetail { get; init; }
        public Func<ReadabilityAuditQuery, CancellationToken, Task<ReadabilityAuditListSnapshot>>? Audit { get; init; }
        public Func<string, string, CancellationToken, Task<ReadabilityAuditDetailSnapshot>>? AuditDetail { get; init; }
        public Func<ErrorSearchQuery, CancellationToken, Task<ErrorSearchListSnapshot>>? ErrorSearch { get; init; }
        public Func<string, string, CancellationToken, Task<ErrorSearchDetailSnapshot>>? ErrorDetail { get; init; }
        public Func<CurrentIngestAttentionQuery, CancellationToken, Task<CurrentIngestAttentionSnapshot>>? Attention { get; init; }

        public Task VerifyContractAsync(CancellationToken cancellationToken) => Verify(cancellationToken);

        public Task<WatchOverviewSnapshot> FetchOverviewAsync(
            WatchOverviewQuery query,
            CancellationToken cancellationToken = default) => Overview is null
                ? Task.FromException<WatchOverviewSnapshot>(new NotSupportedException())
                : Overview(query, cancellationToken);

        public Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
            DemandSeriesBrowseQuery query,
            CancellationToken cancellationToken = default) => DemandSeries is null
                ? Task.FromException<DemandSeriesListSnapshot>(new NotSupportedException())
                : DemandSeries(query, cancellationToken);

        public Task<DemandSeriesDetailSnapshot> FetchDemandSeriesDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) =>
            (DemandSeriesDetail ?? MissingDetail<DemandSeriesDetailSnapshot>)(
                seriesId,
                snapshotReference,
                cancellationToken);

        public Task<ReadabilityAuditListSnapshot> FetchReadabilityAuditAsync(
            ReadabilityAuditQuery query,
            CancellationToken cancellationToken = default) => Audit is null
                ? Task.FromException<ReadabilityAuditListSnapshot>(new NotSupportedException())
                : Audit(query, cancellationToken);

        public Task<ReadabilityAuditDetailSnapshot> FetchReadabilityAuditDetailAsync(
            string demandId,
            string snapshotReference,
            CancellationToken cancellationToken = default) =>
            (AuditDetail ?? MissingDetail<ReadabilityAuditDetailSnapshot>)(
                demandId,
                snapshotReference,
                cancellationToken);

        public Task<ErrorSearchListSnapshot> FetchErrorSearchAsync(
            ErrorSearchQuery query,
            CancellationToken cancellationToken = default) => ErrorSearch is null
                ? Task.FromException<ErrorSearchListSnapshot>(new NotSupportedException())
                : ErrorSearch(query, cancellationToken);

        public Task<ErrorSearchDetailSnapshot> FetchErrorSearchDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) =>
            (ErrorDetail ?? MissingDetail<ErrorSearchDetailSnapshot>)(
                seriesId,
                snapshotReference,
                cancellationToken);

        public Task<ErrorSearchRawEvidenceSnapshot> FetchErrorRawEvidenceAsync(
            string seriesId,
            string evidenceId,
            string snapshotReference,
            ErrorSearchRawEvidenceQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ErrorSearchRawEvidenceSnapshot>(new NotSupportedException());

        public Task<CurrentIngestAttentionSnapshot> FetchCurrentAttentionAsync(
            CurrentIngestAttentionQuery query,
            CancellationToken cancellationToken = default) => Attention is null
                ? Task.FromException<CurrentIngestAttentionSnapshot>(new NotSupportedException())
                : Attention(query, cancellationToken);

        public void Dispose()
        {
            WasDisposed = true;
        }

        private static Task<T> MissingDetail<T>(string _, string __, CancellationToken ___) =>
            Task.FromException<T>(new NotSupportedException());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Set(DateTimeOffset value) => _utcNow = value;
    }
}
