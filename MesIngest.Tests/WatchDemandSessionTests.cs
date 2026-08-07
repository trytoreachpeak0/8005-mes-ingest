using MesIngest.Watch;
using System.Net;

namespace MesIngest.Tests;

public sealed class WatchDemandSessionTests
{
    [Fact]
    public async Task Gone_initial_load_uses_a_fixed_recent_24_hour_window_and_gone_sort()
    {
        WatchDemandBrowseQuery? requested = null;
        var now = DateTimeOffset.Parse("2026-08-08T10:00:00+08:00");
        var queries = new StubReadQueries
        {
            DemandPage = (query, _) =>
            {
                requested = query;
                return Task.FromResult(new WatchDemandPage([], null, HasMore: false));
            },
        };
        using var session = new WatchDemandSession(
            queries,
            WatchDemandViewKind.Gone,
            new AdjustableTimeProvider(now));

        var outcome = await session.LoadInitialAsync();

        Assert.Equal(WatchDemandBrowseOutcome.Succeeded, outcome);
        Assert.NotNull(requested);
        Assert.Equal("GONE", requested.Status);
        Assert.Equal(now.AddHours(-24), requested.GoneAtFrom);
        Assert.Null(requested.GoneAtTo);
        Assert.Equal("goneAt", requested.SortBy);
        Assert.Equal("desc", requested.Direction);
        Assert.Equal(100, requested.Limit);
        Assert.Equal(requested, session.State.CommittedQuery);
    }

    [Fact]
    public async Task Gone_query_commits_its_filters_and_explicit_gone_at_range_only_after_success()
    {
        WatchDemandBrowseQuery? requested = null;
        var queries = new StubReadQueries
        {
            DemandPage = (query, _) =>
            {
                requested = query;
                return Task.FromResult(new WatchDemandPage([Demand("filtered")], null, false));
            },
        };
        using var session = new WatchDemandSession(queries, WatchDemandViewKind.Gone);
        session.UpdateDraft(new WatchDemandDraft(
            TaskType: "DIE_TO_OVEN",
            Sublot: " G-2 ",
            DemandId: "ABCDEF012345",
            GoneAtFrom: "2026-08-07T08:00:00+08:00",
            GoneAtTo: "2026-08-08T10:00:00+08:00"));

        var outcome = await session.SubmitDraftAsync();

        Assert.Equal(WatchDemandBrowseOutcome.Succeeded, outcome);
        Assert.NotNull(requested);
        Assert.Equal("GONE", requested.Status);
        Assert.Equal("DIE_TO_OVEN", requested.TaskType);
        Assert.Equal("G-2", requested.Sublot);
        Assert.Equal("abcdef012345", requested.DemandId);
        Assert.Equal(DateTimeOffset.Parse("2026-08-07T08:00:00+08:00"), requested.GoneAtFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-08-08T10:00:00+08:00"), requested.GoneAtTo);
        Assert.Null(requested.DatesFrom);
        Assert.Null(requested.DatesTo);
        Assert.Equal("goneAt", requested.SortBy);
        Assert.Equal(requested, session.State.CommittedQuery);
        Assert.Equal(["filtered"], session.State.Items.Select(item => item.DemandId));
    }

    [Fact]
    public async Task Gone_reset_recomputes_and_exposes_the_recent_24_hour_default_draft()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-08-08T02:00:00Z"));
        var requests = new List<WatchDemandBrowseQuery>();
        var queries = new StubReadQueries
        {
            DemandPage = (query, _) =>
            {
                requests.Add(query);
                return Task.FromResult(new WatchDemandPage([], null, false));
            },
        };
        using var session = new WatchDemandSession(
            queries,
            WatchDemandViewKind.Gone,
            clock);
        await session.LoadInitialAsync();
        session.UpdateDraft(new WatchDemandDraft(GoneAtFrom: "2020-01-01T00:00:00Z"));
        clock.SetUtcNow(DateTimeOffset.Parse("2026-08-09T02:00:00Z"));

        var outcome = await session.ResetAsync();

        Assert.Equal(WatchDemandBrowseOutcome.Succeeded, outcome);
        Assert.Equal(DateTimeOffset.Parse("2026-08-08T02:00:00Z"), requests[^1].GoneAtFrom);
        Assert.Equal("2026-08-08T02:00:00.0000000+00:00", session.State.Draft.GoneAtFrom);
        Assert.Null(session.State.Draft.GoneAtTo);
    }

    [Fact]
    public async Task Initial_load_uses_visible_default_query_and_commits_one_successful_page()
    {
        WatchDemandBrowseQuery? requested = null;
        var expected = Demand("abcdef0123456789abcdef0123456789");
        var queries = new StubReadQueries
        {
            DemandPage = (query, _) =>
            {
                requested = query;
                return Task.FromResult(new WatchDemandPage([expected], "page-2", HasMore: true));
            },
        };
        using var session = new WatchDemandSession(queries);

        var outcome = await session.LoadInitialAsync();

        Assert.Equal(WatchDemandBrowseOutcome.Succeeded, outcome);
        Assert.NotNull(requested);
        Assert.Equal("VISIBLE", requested.Status);
        Assert.Equal("dates", requested.SortBy);
        Assert.Equal("desc", requested.Direction);
        Assert.Equal(100, requested.Limit);
        Assert.Null(requested.Cursor);
        Assert.Null(requested.DatesFrom);
        Assert.Null(requested.DatesTo);
        Assert.Equal(1, session.State.PageNumber);
        Assert.Equal([expected], session.State.Items);
        Assert.True(session.State.CanMoveNext);
        Assert.False(session.State.CanMovePrevious);
        Assert.Equal(WatchDemandBrowseQuery.Default, session.State.CommittedQuery);
    }

    [Fact]
    public async Task Next_and_previous_replay_saved_arrival_cursors_without_accumulating_rows()
    {
        var requestedCursors = new List<string?>();
        var queries = new StubReadQueries
        {
            DemandPage = (query, _) =>
            {
                requestedCursors.Add(query.Cursor);
                return Task.FromResult(query.Cursor switch
                {
                    null => new WatchDemandPage([Demand("page-1")], "cursor-2", HasMore: true),
                    "cursor-2" => new WatchDemandPage([Demand("page-2")], "cursor-3", HasMore: true),
                    "cursor-3" => new WatchDemandPage([Demand("page-3")], null, HasMore: false),
                    _ => throw new InvalidOperationException("Unexpected cursor."),
                });
            },
        };
        using var session = new WatchDemandSession(queries);

        await session.LoadInitialAsync();
        await session.MoveNextAsync();
        await session.MoveNextAsync();

        Assert.Equal(3, session.State.PageNumber);
        Assert.Equal(["page-3"], session.State.Items.Select(item => item.DemandId));
        Assert.False(session.State.CanMoveNext);
        Assert.True(session.State.CanMovePrevious);

        await session.MovePreviousAsync();

        Assert.Equal(2, session.State.PageNumber);
        Assert.Equal(["page-2"], session.State.Items.Select(item => item.DemandId));
        Assert.Equal([null, "cursor-2", "cursor-3", "cursor-2"], requestedCursors);
    }

    [Fact]
    public async Task Invalid_draft_never_calls_the_host_or_replaces_the_committed_window()
    {
        var requestCount = 0;
        var original = Demand("original");
        var queries = new StubReadQueries
        {
            DemandPage = (_, _) =>
            {
                requestCount++;
                return Task.FromResult(new WatchDemandPage([original], null, HasMore: false));
            },
        };
        using var session = new WatchDemandSession(queries);
        await session.LoadInitialAsync();
        session.UpdateDraft(new WatchDemandDraft(
            TaskType: "NOT_A_PRODUCTION_TYPE",
            Sublot: "S-2",
            DemandId: "abc",
            DatesFrom: "2026-08-08T10:00:00+08:00",
            DatesTo: "2026-08-08T09:00:00+08:00"));

        var outcome = await session.SubmitDraftAsync();

        Assert.Equal(WatchDemandBrowseOutcome.ValidationFailed, outcome);
        Assert.Equal(1, requestCount);
        Assert.Equal(WatchDemandBrowseQuery.Default, session.State.CommittedQuery);
        Assert.Equal([original], session.State.Items);
        Assert.False(string.IsNullOrWhiteSpace(session.State.ValidationError));
    }

    [Fact]
    public async Task Successful_query_commits_normalized_filters_and_returns_to_page_one()
    {
        WatchDemandBrowseQuery? submitted = null;
        var queries = new StubReadQueries
        {
            DemandPage = (query, _) =>
            {
                if (query.TaskType is not null)
                {
                    submitted = query;
                }

                return Task.FromResult(new WatchDemandPage(
                    [Demand(query.TaskType is null ? "default" : "filtered")],
                    null,
                    HasMore: false));
            },
        };
        using var session = new WatchDemandSession(queries);
        await session.LoadInitialAsync();
        session.UpdateDraft(new WatchDemandDraft(
            TaskType: "DIE_TO_OVEN",
            Sublot: " S-2 ",
            DemandId: "ABCDEF012345",
            DatesFrom: "2026-08-08T08:00:00+08:00",
            DatesTo: "2026-08-08T10:00:00+08:00"));

        var outcome = await session.SubmitDraftAsync();

        Assert.Equal(WatchDemandBrowseOutcome.Succeeded, outcome);
        Assert.NotNull(submitted);
        Assert.Equal("VISIBLE", submitted.Status);
        Assert.Equal("DIE_TO_OVEN", submitted.TaskType);
        Assert.Equal("S-2", submitted.Sublot);
        Assert.Equal("abcdef012345", submitted.DemandId);
        Assert.Equal(DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"), submitted.DatesFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-08-08T10:00:00+08:00"), submitted.DatesTo);
        Assert.Equal(100, submitted.Limit);
        Assert.Null(submitted.Cursor);
        Assert.Equal(submitted, session.State.CommittedQuery);
        Assert.Equal(1, session.State.PageNumber);
        Assert.Equal(["filtered"], session.State.Items.Select(item => item.DemandId));
        Assert.Null(session.State.ValidationError);
    }

    [Fact]
    public async Task Sort_and_reset_are_server_queries_that_commit_only_page_one()
    {
        var requests = new List<WatchDemandBrowseQuery>();
        var queries = new StubReadQueries
        {
            DemandPage = (query, _) =>
            {
                requests.Add(query);
                return Task.FromResult(new WatchDemandPage(
                    [Demand($"{query.SortBy}-{query.Direction}")],
                    "next",
                    HasMore: true));
            },
        };
        using var session = new WatchDemandSession(queries);
        await session.LoadInitialAsync();
        await session.MoveNextAsync();

        var sortOutcome = await session.ApplySortAsync("TASK_TYPE");

        Assert.Equal(WatchDemandBrowseOutcome.Succeeded, sortOutcome);
        Assert.Equal("taskType", requests[^1].SortBy);
        Assert.Equal("asc", requests[^1].Direction);
        Assert.Null(requests[^1].Cursor);
        Assert.Equal(1, session.State.PageNumber);
        Assert.Equal(["taskType-asc"], session.State.Items.Select(item => item.DemandId));

        var resetOutcome = await session.ResetAsync();

        Assert.Equal(WatchDemandBrowseOutcome.Succeeded, resetOutcome);
        Assert.Equal(WatchDemandBrowseQuery.Default, requests[^1]);
        Assert.Equal(WatchDemandBrowseQuery.Default, session.State.CommittedQuery);
        Assert.Equal(WatchDemandDraft.Default, session.State.Draft);
        Assert.Equal(1, session.State.PageNumber);
    }

    [Fact]
    public async Task Failed_query_preserves_the_entire_committed_page_and_cursor_path()
    {
        var failure = new WatchHostQueryException(
            WatchHostFailureKind.Http,
            "/api/demands",
            "correlation-query-failed",
            "Host rejected the query");
        var requestCount = 0;
        var queries = new StubReadQueries
        {
            DemandPage = (_, _) =>
            {
                requestCount++;
                if (requestCount == 1)
                {
                    return Task.FromResult(new WatchDemandPage(
                        [Demand("committed")],
                        "cursor-2",
                        HasMore: true));
                }

                return Task.FromException<WatchDemandPage>(failure);
            },
        };
        using var session = new WatchDemandSession(queries);
        await session.LoadInitialAsync();
        var committed = session.State;
        session.UpdateDraft(new WatchDemandDraft(TaskType: "DIE_TO_OVEN"));

        var outcome = await session.SubmitDraftAsync();

        Assert.Equal(WatchDemandBrowseOutcome.Failed, outcome);
        Assert.Equal(committed.CommittedQuery, session.State.CommittedQuery);
        Assert.Equal(committed.PageNumber, session.State.PageNumber);
        Assert.Equal(committed.Items, session.State.Items);
        Assert.Equal(committed.NextCursor, session.State.NextCursor);
        Assert.Equal(committed.HasMore, session.State.HasMore);
        Assert.Same(failure, session.State.Failure);
    }

    [Fact]
    public async Task Superseded_late_response_cannot_replace_the_newer_successful_query()
    {
        var slowGate = new TaskCompletionSource<WatchDemandPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var queries = new StubReadQueries
        {
            DemandPage = (query, _) =>
            {
                requestCount++;
                if (requestCount == 1)
                {
                    return Task.FromResult(new WatchDemandPage([Demand("initial")], null, false));
                }

                if (query.TaskType == "DIE_TO_OVEN")
                {
                    return slowGate.Task;
                }

                return Task.FromResult(new WatchDemandPage([Demand("newer")], null, false));
            },
        };
        using var session = new WatchDemandSession(queries);
        await session.LoadInitialAsync();
        session.UpdateDraft(new WatchDemandDraft(TaskType: "DIE_TO_OVEN"));
        var older = session.SubmitDraftAsync();
        session.UpdateDraft(new WatchDemandDraft(TaskType: "WIRE_TO_GATE"));

        var newerOutcome = await session.SubmitDraftAsync();
        slowGate.SetResult(new WatchDemandPage([Demand("late")], null, false));
        var olderOutcome = await older;

        Assert.Equal(WatchDemandBrowseOutcome.Succeeded, newerOutcome);
        Assert.Equal(WatchDemandBrowseOutcome.Superseded, olderOutcome);
        Assert.Equal("WIRE_TO_GATE", session.State.CommittedQuery.TaskType);
        Assert.Equal(["newer"], session.State.Items.Select(item => item.DemandId));
        Assert.Null(session.State.Failure);
    }

    [Fact]
    public async Task User_cancel_keeps_the_successful_window_even_when_the_host_returns_late()
    {
        var slowGate = new TaskCompletionSource<WatchDemandPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var queries = new StubReadQueries
        {
            DemandPage = (_, _) =>
            {
                requestCount++;
                return requestCount == 1
                    ? Task.FromResult(new WatchDemandPage([Demand("committed")], "next", true))
                    : slowGate.Task;
            },
        };
        using var session = new WatchDemandSession(queries);
        await session.LoadInitialAsync();

        var refresh = session.RefreshCurrentAsync();
        Assert.True(session.State.IsRefreshing);
        session.CancelActive(userInitiated: true);
        slowGate.SetResult(new WatchDemandPage([Demand("late")], null, false));
        var outcome = await refresh;

        Assert.Equal(WatchDemandBrowseOutcome.Canceled, outcome);
        Assert.False(session.State.IsRefreshing);
        Assert.Equal("已取消", session.State.Notice);
        Assert.Equal(["committed"], session.State.Items.Select(item => item.DemandId));
        Assert.True(session.State.CanMoveNext);
        Assert.Null(session.State.Failure);
    }

    [Fact]
    public async Task Expired_cursor_retries_page_one_once_and_commits_the_recovered_window()
    {
        var requests = new List<string?>();
        var cursorFailures = 0;
        var queries = new StubReadQueries
        {
            DemandPage = (query, _) =>
            {
                requests.Add(query.Cursor);
                if (query.Cursor == "cursor-2" && cursorFailures++ == 1)
                {
                    return Task.FromException<WatchDemandPage>(CursorExpired());
                }

                return Task.FromResult(query.Cursor switch
                {
                    null when requests.Count == 1 =>
                        new WatchDemandPage([Demand("page-1")], "cursor-2", true),
                    "cursor-2" when cursorFailures == 1 =>
                        new WatchDemandPage([Demand("page-2")], "cursor-3", true),
                    null => new WatchDemandPage([Demand("recovered")], "new-cursor-2", true),
                    _ => throw new InvalidOperationException("Unexpected cursor."),
                });
            },
        };
        using var session = new WatchDemandSession(queries);
        await session.LoadInitialAsync();
        await session.MoveNextAsync();

        var outcome = await session.RefreshCurrentAsync();

        Assert.Equal(WatchDemandBrowseOutcome.Succeeded, outcome);
        Assert.Equal([null, "cursor-2", "cursor-2", null], requests);
        Assert.Equal(1, session.State.PageNumber);
        Assert.Equal(["recovered"], session.State.Items.Select(item => item.DemandId));
        Assert.Equal("数据已变化，已返回第 1 页", session.State.Notice);
        Assert.True(session.State.CanMoveNext);
        Assert.Null(session.State.Failure);
    }

    [Fact]
    public async Task Failed_page_one_cursor_recovery_does_not_loop_or_replace_the_old_page()
    {
        var requests = new List<string?>();
        var requestCount = 0;
        var retryFailure = new WatchHostQueryException(
            WatchHostFailureKind.Network,
            "/api/demands",
            "correlation-retry",
            "recovery failed");
        var queries = new StubReadQueries
        {
            DemandPage = (query, _) =>
            {
                requests.Add(query.Cursor);
                requestCount++;
                return requestCount switch
                {
                    1 => Task.FromResult(new WatchDemandPage([Demand("page-1")], "cursor-2", true)),
                    2 => Task.FromResult(new WatchDemandPage([Demand("page-2")], "cursor-3", true)),
                    3 => Task.FromException<WatchDemandPage>(CursorExpired()),
                    4 => Task.FromException<WatchDemandPage>(retryFailure),
                    _ => throw new InvalidOperationException("Cursor recovery must retry only once."),
                };
            },
        };
        using var session = new WatchDemandSession(queries);
        await session.LoadInitialAsync();
        await session.MoveNextAsync();

        var outcome = await session.RefreshCurrentAsync();

        Assert.Equal(WatchDemandBrowseOutcome.Failed, outcome);
        Assert.Equal([null, "cursor-2", "cursor-2", null], requests);
        Assert.Equal(2, session.State.PageNumber);
        Assert.Equal(["page-2"], session.State.Items.Select(item => item.DemandId));
        Assert.Same(retryFailure, session.State.Failure);
    }

    [Fact]
    public async Task Current_page_refresh_preserves_selection_by_demand_id_or_clears_it_with_notice()
    {
        var requestCount = 0;
        var selectedId = "abcdef0123456789abcdef0123456789";
        var queries = new StubReadQueries
        {
            DemandPage = (_, _) =>
            {
                requestCount++;
                return Task.FromResult(requestCount switch
                {
                    1 => new WatchDemandPage([Demand(selectedId), Demand("other")], null, false),
                    2 => new WatchDemandPage([Demand(selectedId)], null, false),
                    _ => new WatchDemandPage([Demand("other")], null, false),
                });
            },
        };
        using var session = new WatchDemandSession(queries);
        await session.LoadInitialAsync();
        session.SelectDemand(selectedId);

        await session.RefreshCurrentAsync();

        Assert.Equal(selectedId, session.State.SelectedDemandId);
        Assert.Null(session.State.Notice);

        await session.RefreshCurrentAsync();

        Assert.Null(session.State.SelectedDemandId);
        Assert.Equal("所选 TransportDemand 已不在当前页", session.State.Notice);
    }

    [Fact]
    public async Task Non_cursor_bad_request_is_not_retried_as_cursor_recovery()
    {
        var requests = new List<string?>();
        var requestCount = 0;
        var validationFailure = new WatchEndpointFetchException(
            "/api/demands",
            MesIngest.Core.LatencyStages.HttpStatus,
            TimeSpan.Zero,
            new HttpRequestException("datesFrom is invalid", null, HttpStatusCode.BadRequest));
        var queries = new StubReadQueries
        {
            DemandPage = (query, _) =>
            {
                requests.Add(query.Cursor);
                requestCount++;
                return requestCount switch
                {
                    1 => Task.FromResult(new WatchDemandPage([Demand("page-1")], "cursor-2", true)),
                    2 => Task.FromResult(new WatchDemandPage([Demand("page-2")], "cursor-3", true)),
                    _ => Task.FromException<WatchDemandPage>(validationFailure),
                };
            },
        };
        using var session = new WatchDemandSession(queries);
        await session.LoadInitialAsync();
        await session.MoveNextAsync();

        var outcome = await session.RefreshCurrentAsync();

        Assert.Equal(WatchDemandBrowseOutcome.Failed, outcome);
        Assert.Equal([null, "cursor-2", "cursor-2"], requests);
        Assert.Equal(2, session.State.PageNumber);
        Assert.Equal(["page-2"], session.State.Items.Select(item => item.DemandId));
        Assert.Same(validationFailure, session.State.Failure);
    }

    private static WatchEndpointFetchException CursorExpired() => new(
        "/api/demands",
        MesIngest.Core.LatencyStages.HttpStatus,
        TimeSpan.Zero,
        new HttpRequestException("cursor expired", null, HttpStatusCode.BadRequest));

    private static WatchDemandDto Demand(string id) => new(
        id,
        "DIE_TO_OVEN",
        "S-1",
        "A01-01",
        "EQP-1",
        "STEP-1",
        DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"),
        "PKG",
        "VISIBLE",
        DateTimeOffset.Parse("2026-08-08T09:00:00+08:00"),
        0,
        false,
        null,
        DateTimeOffset.Parse("2026-08-08T08:05:00+08:00"),
        null);

    private sealed class StubReadQueries : IWatchReadQueries
    {
        public required Func<WatchDemandBrowseQuery, CancellationToken, Task<WatchDemandPage>> DemandPage { get; init; }

        public Task<WatchDemandPage> FetchDemandPageAsync(
            WatchDemandBrowseQuery query,
            CancellationToken cancellationToken = default) => DemandPage(query, cancellationToken);

        public Task<WatchPollHealthDto?> FetchPollHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<WatchPollHealthDto?>(null);

        public Task<WatchAlertPage> FetchAlertPageAsync(
            WatchAlertBrowseQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WatchAlertPage([], null, false));

        public Task<WatchSnapshot> FetchSnapshotAsync(
            WatchDemandBrowseQuery demandQuery,
            WatchAlertBrowseQuery alertQuery,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WatchDemandDto?> FetchDemandByIdAsync(
            string demandId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WatchDemandDto?>(null);
    }
}
