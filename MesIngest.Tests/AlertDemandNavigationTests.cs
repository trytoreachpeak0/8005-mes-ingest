using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 08 seam: alert-to-demand navigation through exact Host lookup and the
/// committed VISIBLE/GONE browse sessions observed by the Watch UI.
/// </summary>
public class AlertDemandNavigationTests
{
    [Fact]
    public async Task Exact_navigation_reads_id_first_then_commits_actual_status_page_and_selection()
    {
        var calls = new List<string>();
        var gone = Demand(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "GONE",
            goneAt: DateTimeOffset.Parse("2026-08-08T07:00:00+08:00"));
        var queries = new StubReadQueries
        {
            DemandById = (demandId, _) =>
            {
                calls.Add($"exact:{demandId}");
                return Task.FromResult<WatchDemandDto?>(gone);
            },
            DemandPage = (query, _) =>
            {
                calls.Add($"page:{query.Status}:{query.DemandId}:{query.Cursor ?? "null"}");
                return Task.FromResult(new WatchDemandPage([gone], null, false));
            },
        };
        using var visibleSession = new WatchDemandSession(queries);
        using var goneSession = new WatchDemandSession(queries, WatchDemandViewKind.Gone);
        var navigator = new AlertDemandNavigator(queries, visibleSession, goneSession);

        var result = await navigator.LocateExactAsync(
            new AlertDemandTarget(gone.DemandId, AlertDemandTargetKind.PreviousGone));

        Assert.Equal(AlertDemandNavigationOutcome.Succeeded, result.Outcome);
        Assert.Equal(WatchDemandViewKind.Gone, result.ViewKind);
        Assert.Equal(
            [
                $"exact:{gone.DemandId}",
                $"page:GONE:{gone.DemandId}:null",
            ],
            calls);
        Assert.Equal(1, goneSession.State.PageNumber);
        Assert.Equal("GONE", goneSession.State.CommittedQuery.Status);
        Assert.Equal(gone.DemandId, goneSession.State.CommittedQuery.DemandId);
        Assert.Null(goneSession.State.CommittedQuery.GoneAtFrom);
        Assert.Null(goneSession.State.CommittedQuery.GoneAtTo);
        Assert.Equal(gone.DemandId, goneSession.State.SelectedDemandId);
        Assert.Same(gone, goneSession.State.SelectedDemand);
    }

    [Fact]
    public async Task Failed_follow_up_query_keeps_the_existing_page_query_and_selection()
    {
        var navigating = false;
        var existing = Demand(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "GONE",
            goneAt: DateTimeOffset.Parse("2026-08-08T07:00:00+08:00"));
        var target = Demand(
            "cccccccccccccccccccccccccccccccc",
            "GONE",
            goneAt: DateTimeOffset.Parse("2026-08-08T07:30:00+08:00"));
        var queries = new StubReadQueries
        {
            DemandById = (_, _) => Task.FromResult<WatchDemandDto?>(target),
            DemandPage = (_, _) => navigating
                ? throw new InvalidOperationException("page endpoint unavailable")
                : Task.FromResult(new WatchDemandPage([existing], null, false)),
        };
        using var visibleSession = new WatchDemandSession(queries);
        using var goneSession = new WatchDemandSession(queries, WatchDemandViewKind.Gone);
        Assert.Equal(WatchDemandBrowseOutcome.Succeeded, await goneSession.LoadInitialAsync());
        goneSession.SelectDemand(existing.DemandId);
        var before = goneSession.State;
        var navigator = new AlertDemandNavigator(queries, visibleSession, goneSession);
        navigating = true;

        var result = await navigator.LocateExactAsync(
            new AlertDemandTarget(target.DemandId, AlertDemandTargetKind.PreviousGone));

        Assert.Equal(AlertDemandNavigationOutcome.Failed, result.Outcome);
        Assert.Contains("page endpoint unavailable", result.Message, StringComparison.Ordinal);
        Assert.Equal(before.CommittedQuery, goneSession.State.CommittedQuery);
        Assert.Equal(before.Items, goneSession.State.Items);
        Assert.Equal(before.PageNumber, goneSession.State.PageNumber);
        Assert.Equal(before.SelectedDemandId, goneSession.State.SelectedDemandId);
        Assert.Same(before.SelectedDemand, goneSession.State.SelectedDemand);
    }

    [Fact]
    public async Task Exact_target_missing_from_follow_up_page_does_not_commit_or_fake_selection()
    {
        var navigating = false;
        var existing = Demand(
            "dddddddddddddddddddddddddddddddd",
            "VISIBLE");
        var target = Demand(
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            "VISIBLE");
        var unrelated = Demand(
            "ffffffffffffffffffffffffffffffff",
            "VISIBLE");
        var queries = new StubReadQueries
        {
            DemandById = (_, _) => Task.FromResult<WatchDemandDto?>(target),
            DemandPage = (_, _) => Task.FromResult(new WatchDemandPage(
                navigating ? [unrelated] : [existing],
                null,
                false)),
        };
        using var visibleSession = new WatchDemandSession(queries);
        using var goneSession = new WatchDemandSession(queries, WatchDemandViewKind.Gone);
        Assert.Equal(WatchDemandBrowseOutcome.Succeeded, await visibleSession.LoadInitialAsync());
        visibleSession.SelectDemand(existing.DemandId);
        var before = visibleSession.State;
        var navigator = new AlertDemandNavigator(queries, visibleSession, goneSession);
        navigating = true;

        var result = await navigator.LocateExactAsync(
            new AlertDemandTarget(target.DemandId, AlertDemandTargetKind.Generic));

        Assert.Equal(AlertDemandNavigationOutcome.MissingFromPage, result.Outcome);
        Assert.Equal(before.CommittedQuery, visibleSession.State.CommittedQuery);
        Assert.Equal(before.Items, visibleSession.State.Items);
        Assert.Equal(before.PageNumber, visibleSession.State.PageNumber);
        Assert.Equal(existing.DemandId, visibleSession.State.SelectedDemandId);
        Assert.DoesNotContain(visibleSession.State.Items, item => item.DemandId == target.DemandId);
    }

    [Fact]
    public async Task Business_key_search_submits_visible_first_page_without_claiming_unique_history()
    {
        WatchDemandBrowseQuery? requested = null;
        var match = Demand("abababababababababababababababab", "VISIBLE");
        var queries = new StubReadQueries
        {
            DemandById = (_, _) => throw new Xunit.Sdk.XunitException(
                "Business-key search must not perform an exact DemandId lookup."),
            DemandPage = (query, _) =>
            {
                requested = query;
                return Task.FromResult(new WatchDemandPage([match], null, false));
            },
        };
        using var visibleSession = new WatchDemandSession(queries);
        using var goneSession = new WatchDemandSession(queries, WatchDemandViewKind.Gone);
        var navigator = new AlertDemandNavigator(queries, visibleSession, goneSession);

        var result = await navigator.SearchBusinessKeyAsync(
            new AlertDemandBusinessKey("DIE_TO_OVEN", "S1"));

        Assert.Equal(AlertDemandNavigationOutcome.Succeeded, result.Outcome);
        Assert.Equal(WatchDemandViewKind.Visible, result.ViewKind);
        Assert.NotNull(requested);
        Assert.Equal("VISIBLE", requested.Status);
        Assert.Equal("DIE_TO_OVEN", requested.TaskType);
        Assert.Equal("S1", requested.Sublot);
        Assert.Null(requested.DemandId);
        Assert.Null(requested.Cursor);
        Assert.Equal(1, visibleSession.State.PageNumber);
        Assert.Null(visibleSession.State.SelectedDemandId);
        Assert.Contains("不代表唯一历史实例", result.Message, StringComparison.Ordinal);
    }

    private static WatchDemandDto Demand(
        string demandId,
        string status,
        DateTimeOffset? goneAt = null) =>
        new(
            DemandId: demandId,
            TaskType: "DIE_TO_OVEN",
            Sublot: "S1",
            Area: "A",
            Eqp: "EQ-1",
            Step: "STEP-1",
            Dates: DateTimeOffset.Parse("2026-08-08T06:00:00+08:00"),
            Package: "PKG-1",
            Status: status,
            MesLastSeenAt: DateTimeOffset.Parse("2026-08-08T06:05:00+08:00"),
            DisappearCount: status == "GONE" ? 1 : 0,
            LocationRisk: false,
            LocationRiskCode: null,
            CreatedAt: DateTimeOffset.Parse("2026-08-08T05:00:00+08:00"),
            GoneAt: goneAt);

    private sealed class StubReadQueries : IWatchReadQueries
    {
        public required Func<string, CancellationToken, Task<WatchDemandDto?>> DemandById { get; init; }
        public required Func<WatchDemandBrowseQuery, CancellationToken, Task<WatchDemandPage>> DemandPage { get; init; }

        public Task<WatchDemandDto?> FetchDemandByIdAsync(
            string demandId,
            CancellationToken cancellationToken = default) =>
            DemandById(demandId, cancellationToken);

        public Task<WatchDemandPage> FetchDemandPageAsync(
            WatchDemandBrowseQuery query,
            CancellationToken cancellationToken = default) =>
            DemandPage(query, cancellationToken);

        public Task<WatchPollHealthDto?> FetchPollHealthAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WatchAlertPage> FetchAlertPageAsync(
            WatchAlertBrowseQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WatchSnapshot> FetchSnapshotAsync(
            WatchDemandBrowseQuery demandQuery,
            WatchAlertBrowseQuery alertQuery,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
