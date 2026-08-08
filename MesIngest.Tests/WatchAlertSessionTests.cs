using System.Net;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchAlertSessionTests
{
    [Fact]
    public async Task Initial_load_uses_active_default_priority_window_without_explicit_sort()
    {
        WatchAlertBrowseQuery? requested = null;
        var queries = new StubReadQueries
        {
            AlertPage = (query, _) =>
            {
                requested = query;
                return Task.FromResult(new WatchAlertPage([], null, false));
            },
        };
        using var session = new WatchAlertSession(queries);

        var outcome = await session.LoadInitialAsync();

        Assert.Equal(WatchAlertBrowseOutcome.Succeeded, outcome);
        Assert.Equal(WatchAlertBrowseQuery.Default, requested);
        var url = requested!.ToRelativeUrl();
        Assert.Contains("active=true", url, StringComparison.Ordinal);
        Assert.Contains("limit=100", url, StringComparison.Ordinal);
        Assert.DoesNotContain("sortBy=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("direction=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Browse_query_serializes_only_the_supported_server_filters()
    {
        var from = DateTimeOffset.Parse("2026-08-08T08:00:00+08:00");
        var to = DateTimeOffset.Parse("2026-08-08T09:00:00+08:00");
        var url = new WatchAlertBrowseQuery(
            SortBy: "firstSeenAt",
            Direction: "asc",
            Limit: 100,
            Cursor: "opaque",
            Active: false,
            Code: "FIELD_DRIFT",
            Severity: "ERROR",
            From: from,
            To: to).ToRelativeUrl();

        Assert.Contains("active=false", url, StringComparison.Ordinal);
        Assert.Contains("code=FIELD_DRIFT", url, StringComparison.Ordinal);
        Assert.Contains("severity=ERROR", url, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(from.ToString("O")), url, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(to.ToString("O")), url, StringComparison.Ordinal);
        Assert.Contains("sortBy=firstSeenAt", url, StringComparison.Ordinal);
        Assert.Contains("direction=asc", url, StringComparison.Ordinal);
        Assert.Contains("cursor=opaque", url, StringComparison.Ordinal);
        Assert.DoesNotContain("firstSeenAtFrom", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_draft_commits_activity_production_filters_and_last_seen_range()
    {
        WatchAlertBrowseQuery? requested = null;
        var queries = new StubReadQueries
        {
            AlertPage = (query, _) =>
            {
                requested = query;
                return Task.FromResult(new WatchAlertPage([Alert("filtered")], null, false));
            },
        };
        using var session = new WatchAlertSession(queries);
        session.UpdateDraft(new WatchAlertDraft(
            Active: false,
            Code: "FIELD_DRIFT",
            Severity: "ERROR",
            LastSeenAtFrom: "2026-08-08T08:00:00+08:00",
            LastSeenAtTo: "2026-08-08T09:00:00+08:00"));

        var outcome = await session.SubmitDraftAsync();

        Assert.Equal(WatchAlertBrowseOutcome.Succeeded, outcome);
        Assert.NotNull(requested);
        Assert.False(requested.Active);
        Assert.Equal("FIELD_DRIFT", requested.Code);
        Assert.Equal("ERROR", requested.Severity);
        Assert.Equal(DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"), requested.From);
        Assert.Equal(DateTimeOffset.Parse("2026-08-08T09:00:00+08:00"), requested.To);
        Assert.Equal(100, requested.Limit);
        Assert.Null(requested.SortBy);
        Assert.Equal(requested, session.State.CommittedQuery);
        Assert.Equal(["filtered"], session.State.Items.Select(alert => alert.AlertId));
    }

    [Theory]
    [InlineData("HOST_UNREACHABLE", null, null, "Code")]
    [InlineData("POLL_FAILURE", "CRITICAL", null, "Severity")]
    [InlineData("POLL_FAILURE", "ERROR", "2026-08-08T09:00:00+08:00|2026-08-08T08:00:00+08:00", "起始")]
    public async Task Invalid_draft_is_rejected_without_calling_host(
        string code,
        string? severity,
        string? range,
        string expectedError)
    {
        var calls = 0;
        var queries = new StubReadQueries
        {
            AlertPage = (_, _) =>
            {
                calls++;
                return Task.FromResult(new WatchAlertPage([], null, false));
            },
        };
        using var session = new WatchAlertSession(queries);
        var parts = range?.Split('|');
        session.UpdateDraft(new WatchAlertDraft(
            Active: true,
            Code: code,
            Severity: severity,
            LastSeenAtFrom: parts?[0],
            LastSeenAtTo: parts?[1]));

        var outcome = await session.SubmitDraftAsync();

        Assert.Equal(WatchAlertBrowseOutcome.ValidationFailed, outcome);
        Assert.Equal(0, calls);
        Assert.Contains(expectedError, session.State.ValidationError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paging_sort_and_reset_commit_fixed_server_windows_atomically()
    {
        var requests = new List<WatchAlertBrowseQuery>();
        var queries = new StubReadQueries
        {
            AlertPage = (query, _) =>
            {
                requests.Add(query);
                return Task.FromResult(query switch
                {
                    { Cursor: "cursor-2" } => new WatchAlertPage([Alert("page-2")], null, false),
                    { SortBy: "code", Direction: "asc" } => new WatchAlertPage([Alert("code-asc")], null, false),
                    _ => new WatchAlertPage([Alert("page-1")], "cursor-2", true),
                });
            },
        };
        using var session = new WatchAlertSession(queries);

        await session.LoadInitialAsync();
        await session.MoveNextAsync();
        Assert.Equal(2, session.State.PageNumber);
        Assert.Equal(["page-2"], session.State.Items.Select(alert => alert.AlertId));

        await session.MovePreviousAsync();
        Assert.Equal(1, session.State.PageNumber);
        Assert.Equal(["page-1"], session.State.Items.Select(alert => alert.AlertId));

        await session.ApplySortAsync("Code");
        Assert.Equal("code", session.State.CommittedQuery.SortBy);
        Assert.Equal("asc", session.State.CommittedQuery.Direction);
        Assert.Equal(["code-asc"], session.State.Items.Select(alert => alert.AlertId));

        await session.ResetAsync();
        Assert.Equal(WatchAlertBrowseQuery.Default, session.State.CommittedQuery);
        Assert.Equal(WatchAlertDraft.Default, session.State.Draft);
        Assert.Equal(1, session.State.PageNumber);
        Assert.All(requests, query => Assert.Equal(100, query.Limit));
    }

    [Fact]
    public async Task Current_page_refresh_updates_selected_alert_or_clears_it_with_notice()
    {
        var calls = 0;
        var queries = new StubReadQueries
        {
            AlertPage = (_, _) => Task.FromResult(++calls switch
            {
                1 => new WatchAlertPage([Alert("selected", "old")], null, false),
                2 => new WatchAlertPage([Alert("selected", "new")], null, false),
                _ => new WatchAlertPage([Alert("other")], null, false),
            }),
        };
        using var session = new WatchAlertSession(queries);
        await session.LoadInitialAsync();
        session.SelectAlert("selected");

        await session.RefreshCurrentAsync();

        Assert.Equal("selected", session.State.SelectedAlertId);
        Assert.Equal("new", session.State.SelectedAlert?.Message);

        await session.RefreshCurrentAsync();

        Assert.Null(session.State.SelectedAlertId);
        Assert.Equal("所选 IngestAlert 已不在当前页", session.State.Notice);
    }

    [Fact]
    public async Task Cursor_bad_request_retries_page_one_once_without_mixing_windows()
    {
        var requests = new List<string?>();
        var calls = 0;
        var queries = new StubReadQueries
        {
            AlertPage = (query, _) =>
            {
                requests.Add(query.Cursor);
                calls++;
                return calls switch
                {
                    1 => Task.FromResult(new WatchAlertPage([Alert("page-1")], "cursor-2", true)),
                    2 => Task.FromResult(new WatchAlertPage([Alert("page-2")], "cursor-3", true)),
                    3 => Task.FromException<WatchAlertPage>(CursorExpired()),
                    _ => Task.FromResult(new WatchAlertPage([Alert("recovered")], null, false)),
                };
            },
        };
        using var session = new WatchAlertSession(queries);
        await session.LoadInitialAsync();
        await session.MoveNextAsync();

        var outcome = await session.RefreshCurrentAsync();

        Assert.Equal(WatchAlertBrowseOutcome.Succeeded, outcome);
        Assert.Equal([null, "cursor-2", "cursor-2", null], requests);
        Assert.Equal(1, session.State.PageNumber);
        Assert.Equal(["recovered"], session.State.Items.Select(alert => alert.AlertId));
        Assert.Equal("数据已变化，已返回第 1 页", session.State.Notice);
    }

    [Fact]
    public async Task Watch_connection_codes_are_never_projected_as_ingest_alert_rows()
    {
        var queries = new StubReadQueries
        {
            AlertPage = (_, _) => Task.FromResult(new WatchAlertPage(
                [Alert("production"), Alert("watch-only") with { Code = "HOST_UNREACHABLE" }],
                null,
                false)),
        };
        using var session = new WatchAlertSession(queries);

        await session.LoadInitialAsync();

        Assert.Equal(["production"], session.State.Items.Select(alert => alert.AlertId));
    }

    private static WatchEndpointFetchException CursorExpired() => new(
        "/api/alerts",
        MesIngest.Core.LatencyStages.HttpStatus,
        TimeSpan.Zero,
        new HttpRequestException("cursor expired", null, HttpStatusCode.BadRequest));

    private static WatchAlertDto Alert(string id, string message = "message") => new(
        id,
        "POLL_FAILURE",
        "ERROR",
        null,
        null,
        null,
        message,
        "{}",
        DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"),
        DateTimeOffset.Parse("2026-08-08T09:00:00+08:00"),
        1,
        true,
        null,
        DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"));

    private sealed class StubReadQueries : IWatchReadQueries
    {
        public required Func<WatchAlertBrowseQuery, CancellationToken, Task<WatchAlertPage>> AlertPage { get; init; }

        public Task<WatchAlertPage> FetchAlertPageAsync(
            WatchAlertBrowseQuery query,
            CancellationToken cancellationToken = default) => AlertPage(query, cancellationToken);

        public Task<WatchDemandPage> FetchDemandPageAsync(
            WatchDemandBrowseQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WatchDemandPage([], null, false));

        public Task<WatchPollHealthDto?> FetchPollHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<WatchPollHealthDto?>(null);

        public Task<WatchSnapshot> FetchSnapshotAsync(
            WatchDemandBrowseQuery demandQuery,
            WatchAlertBrowseQuery alertQuery,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WatchDemandDto?> FetchDemandByIdAsync(
            string demandId,
            CancellationToken cancellationToken = default) => Task.FromResult<WatchDemandDto?>(null);
    }
}
