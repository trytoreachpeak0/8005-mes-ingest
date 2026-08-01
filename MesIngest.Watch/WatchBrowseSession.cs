namespace MesIngest.Watch;

internal enum WatchBrowseRefreshKind
{
    /// <summary>Filter/sort change — replace with the first page only.</summary>
    Reset,

    /// <summary>Timer refresh — re-fetch the currently loaded demand window.</summary>
    PreserveWindow,

    /// <summary>Load more — append the next cursor page.</summary>
    Append,
}

/// <summary>
/// Demand/Alert browse state for Watch: Load-more accumulation, preserve-window
/// auto-refresh, and server-side alert sort parameters.
/// </summary>
internal sealed class WatchBrowseSession
{
    private readonly MesIngestApiClient _client;
    private readonly int _pageSize;

    public WatchBrowseSession(MesIngestApiClient client, int pageSize = 100)
    {
        _client = client;
        _pageSize = pageSize < 1 ? 100 : pageSize;
    }

    public IReadOnlyList<WatchDemandDto> Demands { get; private set; } = [];
    public IReadOnlyList<WatchAlertDto> Alerts { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public bool HasMore { get; private set; }
    public WatchSnapshot? LastSnapshot { get; private set; }
    public WatchAlertBrowseQuery AlertQuery { get; private set; } = WatchAlertBrowseQuery.Default;

    /// <summary>
    /// True when the last refresh replaced alerts/poll-health (Reset, PreserveWindow,
    /// or Append that recovered from a bad cursor).
    /// </summary>
    public bool LastRefreshIncludedSnapshot { get; private set; }

    public async Task RefreshAsync(
        WatchBrowseRefreshKind kind,
        WatchDemandBrowseQuery filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        LastRefreshIncludedSnapshot = false;

        switch (kind)
        {
            case WatchBrowseRefreshKind.Append:
                await AppendAsync(filter, cancellationToken).ConfigureAwait(false);
                return;
            case WatchBrowseRefreshKind.PreserveWindow:
                await PreserveWindowAsync(filter, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await ResetAsync(filter, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    public bool TryApplyAlertSort(string? header)
    {
        var token = WatchAlertBrowseQuery.SortToken(header);
        if (token is null)
        {
            return false;
        }

        if (string.Equals(AlertQuery.SortBy, token, StringComparison.Ordinal))
        {
            var nextDirection = string.Equals(AlertQuery.Direction, "asc", StringComparison.Ordinal)
                ? "desc"
                : "asc";
            AlertQuery = AlertQuery with { SortBy = token, Direction = nextDirection };
        }
        else
        {
            AlertQuery = AlertQuery with { SortBy = token, Direction = "asc" };
        }

        return true;
    }

    private async Task ResetAsync(WatchDemandBrowseQuery filter, CancellationToken cancellationToken)
    {
        var query = WithPaging(filter, cursor: null, _pageSize);
        var snapshot = await _client.FetchSnapshotAsync(query, AlertQuery, cancellationToken)
            .ConfigureAwait(false);
        ApplySnapshotDemands(snapshot);
        ApplySnapshotAlerts(snapshot);
        LastSnapshot = snapshot;
        LastRefreshIncludedSnapshot = true;
    }

    private async Task PreserveWindowAsync(WatchDemandBrowseQuery filter, CancellationToken cancellationToken)
    {
        var targetCount = Math.Max(Demands.Count, _pageSize);
        var collected = new List<WatchDemandDto>();
        string? cursor = null;
        var hasMore = false;
        string? nextCursor = null;

        var snapshot = await _client.FetchSnapshotAsync(
                WithPaging(filter, cursor: null, _pageSize),
                AlertQuery,
                cancellationToken)
            .ConfigureAwait(false);
        if (!snapshot.DemandsSucceeded)
        {
            // Keep prior demand window; still apply alerts/health from the snapshot round.
            ApplySnapshotAlerts(snapshot);
            LastSnapshot = snapshot;
            LastRefreshIncludedSnapshot = true;
            return;
        }

        collected.AddRange(snapshot.Demands);
        cursor = snapshot.DemandsNextCursor;
        hasMore = snapshot.DemandsHasMore;
        nextCursor = snapshot.DemandsNextCursor;

        while (collected.Count < targetCount
               && hasMore
               && !string.IsNullOrWhiteSpace(cursor))
        {
            var page = await _client.FetchDemandPageAsync(
                    WithPaging(filter, cursor, _pageSize),
                    cancellationToken)
                .ConfigureAwait(false);
            collected.AddRange(page.Items);
            cursor = page.NextCursor;
            hasMore = page.HasMore;
            nextCursor = page.NextCursor;
        }

        Demands = collected;
        NextCursor = nextCursor;
        HasMore = hasMore;
        ApplySnapshotAlerts(snapshot);
        LastSnapshot = snapshot;
        LastRefreshIncludedSnapshot = true;
    }

    private async Task AppendAsync(WatchDemandBrowseQuery filter, CancellationToken cancellationToken)
    {
        if (!HasMore || string.IsNullOrWhiteSpace(NextCursor))
        {
            return;
        }

        try
        {
            var page = await _client.FetchDemandPageAsync(
                    WithPaging(filter, NextCursor, _pageSize),
                    cancellationToken)
                .ConfigureAwait(false);
            Demands = Demands.Concat(page.Items).ToList();
            NextCursor = page.NextCursor;
            HasMore = page.HasMore;
            LastSnapshot = LastSnapshot is null
                ? null
                : LastSnapshot with
                {
                    DemandsNextCursor = NextCursor,
                    DemandsHasMore = HasMore,
                };
        }
        catch (WatchEndpointFetchException ex) when (
            ex.InnerException is HttpRequestException http
            && http.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            await ResetAsync(filter, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplySnapshotDemands(WatchSnapshot snapshot)
    {
        if (!snapshot.DemandsSucceeded)
        {
            return;
        }

        Demands = snapshot.Demands;
        NextCursor = snapshot.DemandsNextCursor;
        HasMore = snapshot.DemandsHasMore;
    }

    private void ApplySnapshotAlerts(WatchSnapshot snapshot)
    {
        if (snapshot.AlertsSucceeded)
        {
            Alerts = snapshot.Alerts;
        }
    }

    private static WatchDemandBrowseQuery WithPaging(
        WatchDemandBrowseQuery filter,
        string? cursor,
        int limit) =>
        filter with { Cursor = cursor, Limit = limit };
}
