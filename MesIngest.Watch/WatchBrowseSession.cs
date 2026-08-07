namespace MesIngest.Watch;

internal enum WatchBrowseRefreshKind
{
    /// <summary>Filter/sort change — replace with the first page only.</summary>
    Reset,

    /// <summary>Timer refresh — re-fetch the currently loaded demand window.</summary>
    PreserveWindow,

    /// <summary>Load more — append the next cursor page.</summary>
    Append,

    /// <summary>Load more Alerts — append the next alert cursor page.</summary>
    AppendAlerts,
}

/// <summary>
/// Demand/Alert browse state for Watch: Load-more accumulation, preserve-window
/// auto-refresh, and server-side alert sort parameters.
/// </summary>
internal sealed class WatchBrowseSession
{
    private readonly IWatchReadQueries _client;
    private readonly int _pageSize;

    public WatchBrowseSession(IWatchReadQueries client, int pageSize = 100)
    {
        _client = client;
        _pageSize = pageSize < 1 ? 100 : pageSize;
    }

    public IReadOnlyList<WatchDemandDto> Demands { get; private set; } = [];
    public IReadOnlyList<WatchAlertDto> Alerts { get; private set; } = [];
    public string? DemandsNextCursor { get; private set; }
    public bool DemandsHasMore { get; private set; }
    public string? AlertsNextCursor { get; private set; }
    public bool AlertsHasMore { get; private set; }
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
            case WatchBrowseRefreshKind.AppendAlerts:
                await AppendAlertsAsync(cancellationToken).ConfigureAwait(false);
                return;
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
        var snapshot = await _client.FetchSnapshotAsync(
                query,
                WithPaging(AlertQuery, cursor: null, _pageSize),
                cancellationToken)
            .ConfigureAwait(false);
        ApplySnapshotDemands(snapshot);
        ApplySnapshotAlerts(snapshot);
        LastSnapshot = snapshot;
        LastRefreshIncludedSnapshot = true;
    }

    private async Task PreserveWindowAsync(WatchDemandBrowseQuery filter, CancellationToken cancellationToken)
    {
        var targetCount = Math.Max(Demands.Count, _pageSize);
        var alertTargetCount = Math.Max(Alerts.Count, _pageSize);
        var collected = new List<WatchDemandDto>();
        var collectedAlerts = new List<WatchAlertDto>();
        string? cursor = null;
        var hasMore = false;
        string? nextCursor = null;
        string? alertCursor = null;
        var alertsHasMore = false;
        string? alertsNextCursor = null;

        var snapshot = await _client.FetchSnapshotAsync(
                WithPaging(filter, cursor: null, _pageSize),
                WithPaging(AlertQuery, cursor: null, _pageSize),
                cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.DemandsSucceeded)
        {
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
        DemandsNextCursor = nextCursor;
        DemandsHasMore = hasMore;
        }

        if (snapshot.AlertsSucceeded)
        {
            collectedAlerts.AddRange(snapshot.Alerts);
            alertCursor = snapshot.AlertsNextCursor;
            alertsHasMore = snapshot.AlertsHasMore;
            alertsNextCursor = snapshot.AlertsNextCursor;

            while (collectedAlerts.Count < alertTargetCount
                   && alertsHasMore
                   && !string.IsNullOrWhiteSpace(alertCursor))
            {
                WatchAlertPage page;
                try
                {
                    page = await _client.FetchAlertPageAsync(
                            WithPaging(AlertQuery, alertCursor, _pageSize),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (WatchEndpointFetchException ex) when (IsBadRequestCursor(ex))
                {
                    page = await _client.FetchAlertPageAsync(
                            WithPaging(AlertQuery, cursor: null, _pageSize),
                            cancellationToken)
                        .ConfigureAwait(false);
                    collectedAlerts.Clear();
                }

                collectedAlerts.AddRange(page.Items);
                alertCursor = page.NextCursor;
                alertsHasMore = page.HasMore;
                alertsNextCursor = page.NextCursor;
            }

            Alerts = collectedAlerts;
            AlertsNextCursor = alertsNextCursor;
            AlertsHasMore = alertsHasMore;
        }

        LastSnapshot = snapshot with
        {
            AlertsNextCursor = AlertsNextCursor,
            AlertsHasMore = AlertsHasMore,
        };
        LastRefreshIncludedSnapshot = true;
    }

    private async Task AppendAsync(WatchDemandBrowseQuery filter, CancellationToken cancellationToken)
    {
        if (!DemandsHasMore || string.IsNullOrWhiteSpace(DemandsNextCursor))
        {
            return;
        }

        try
        {
            var page = await _client.FetchDemandPageAsync(
                    WithPaging(filter, DemandsNextCursor, _pageSize),
                    cancellationToken)
                .ConfigureAwait(false);
            Demands = Demands.Concat(page.Items).ToList();
            DemandsNextCursor = page.NextCursor;
            DemandsHasMore = page.HasMore;
            LastSnapshot = LastSnapshot is null
                ? null
                : LastSnapshot with
                {
                    DemandsNextCursor = DemandsNextCursor,
                    DemandsHasMore = DemandsHasMore,
                };
        }
        catch (WatchEndpointFetchException ex) when (
            ex.InnerException is HttpRequestException http
            && http.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            await ResetAsync(filter, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AppendAlertsAsync(CancellationToken cancellationToken)
    {
        if (!AlertsHasMore || string.IsNullOrWhiteSpace(AlertsNextCursor))
        {
            return;
        }

        WatchAlertPage page;
        var replaceWindow = false;
        try
        {
            page = await _client.FetchAlertPageAsync(
                    WithPaging(AlertQuery, AlertsNextCursor, _pageSize),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WatchEndpointFetchException ex) when (IsBadRequestCursor(ex))
        {
            page = await _client.FetchAlertPageAsync(
                    WithPaging(AlertQuery, cursor: null, _pageSize),
                    cancellationToken)
                .ConfigureAwait(false);
            replaceWindow = true;
        }

        Alerts = replaceWindow ? page.Items : Alerts.Concat(page.Items).ToList();
        AlertsNextCursor = page.NextCursor;
        AlertsHasMore = page.HasMore;
        LastSnapshot = LastSnapshot is null
            ? null
            : LastSnapshot with
            {
                AlertsNextCursor = AlertsNextCursor,
                AlertsHasMore = AlertsHasMore,
            };
    }

    private void ApplySnapshotDemands(WatchSnapshot snapshot)
    {
        if (!snapshot.DemandsSucceeded)
        {
            return;
        }

        Demands = snapshot.Demands;
        DemandsNextCursor = snapshot.DemandsNextCursor;
        DemandsHasMore = snapshot.DemandsHasMore;
    }

    private void ApplySnapshotAlerts(WatchSnapshot snapshot)
    {
        if (snapshot.AlertsSucceeded)
        {
            Alerts = snapshot.Alerts;
            AlertsNextCursor = snapshot.AlertsNextCursor;
            AlertsHasMore = snapshot.AlertsHasMore;
        }
    }

    private static WatchDemandBrowseQuery WithPaging(
        WatchDemandBrowseQuery filter,
        string? cursor,
        int limit) =>
        filter with { Cursor = cursor, Limit = limit };

    private static WatchAlertBrowseQuery WithPaging(
        WatchAlertBrowseQuery query,
        string? cursor,
        int limit) =>
        query with { Cursor = cursor, Limit = limit };

    private static bool IsBadRequestCursor(WatchEndpointFetchException exception) =>
        exception.InnerException is HttpRequestException http
        && http.StatusCode == System.Net.HttpStatusCode.BadRequest;
}
