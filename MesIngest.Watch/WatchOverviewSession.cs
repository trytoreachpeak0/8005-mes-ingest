namespace MesIngest.Watch;

internal sealed record WatchOverviewPollHealthState(
    WatchPollHealthDto? Value,
    DateTimeOffset? LastSuccessfulAt,
    bool IsStale,
    string? Error)
{
    public static WatchOverviewPollHealthState Empty { get; } = new(null, null, false, null);
}

internal sealed record WatchOverviewAlertState(
    IReadOnlyList<WatchAlertDto> Items,
    bool HasMore,
    DateTimeOffset? LastSuccessfulAt,
    bool IsStale,
    string? Error)
{
    public static WatchOverviewAlertState Empty { get; } = new([], false, null, false, null);

    public string CountLabel => HasMore ? "100+" : Items.Count.ToString(
        System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed record WatchOverviewDemandState(
    IReadOnlyList<WatchDemandDto> Items,
    bool HasMore,
    DateTimeOffset? LastSuccessfulAt,
    bool IsStale,
    string? Error)
{
    private static readonly string[] TaskTypeOrder =
    [
        "DIE_TO_WIRE_STAGING",
        "DIE_TO_OVEN",
        "WIRE_TO_GATE",
        "WIRE_TO_OPTICAL",
        "STAGING_TO_WIRE",
        "WIRE_TO_NITROGEN",
    ];

    public static WatchOverviewDemandState Empty { get; } = new([], false, null, false, null);

    public string CountLabel => HasMore ? "100+" : Items.Count.ToString(
        System.Globalization.CultureInfo.InvariantCulture);

    public string TaskTypeSummary
    {
        get
        {
            var counts = Items
                .GroupBy(item => item.TaskType, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var parts = TaskTypeOrder
                .Where(counts.ContainsKey)
                .Select(taskType => $"{taskType} {counts[taskType]}")
                .Concat(counts.Keys
                    .Except(TaskTypeOrder, StringComparer.Ordinal)
                    .OrderBy(taskType => taskType, StringComparer.Ordinal)
                    .Select(taskType => $"{taskType} {counts[taskType]}"));
            var summary = string.Join(" · ", parts);
            return string.IsNullOrEmpty(summary) ? "当前页：无" : $"当前页：{summary}";
        }
    }
}

internal sealed record WatchOverviewState(
    WatchOverviewPollHealthState PollHealth,
    WatchOverviewAlertState Alerts,
    WatchOverviewDemandState Demands)
{
    public static WatchOverviewState Empty { get; } = new(
        WatchOverviewPollHealthState.Empty,
        WatchOverviewAlertState.Empty,
        WatchOverviewDemandState.Empty);

    public bool IsPartialFailure =>
        new[] { PollHealth.Error, Alerts.Error, Demands.Error }.Any(error => error is not null)
        && new[] { PollHealth.Error, Alerts.Error, Demands.Error }.Any(error => error is null);

    public bool AllResourcesFailed =>
        new[] { PollHealth.Error, Alerts.Error, Demands.Error }.All(error => error is not null);
}

/// <summary>
/// Owns the overview's three independently committed resource cards. A refresh is one
/// request generation at the query seam, while failed resources keep their own last
/// successful window and timestamp.
/// </summary>
internal sealed class WatchOverviewSession
{
    private readonly IWatchReadQueries _queries;
    private readonly Func<DateTimeOffset> _getNow;

    public WatchOverviewSession(
        IWatchReadQueries queries,
        Func<DateTimeOffset>? getNow = null)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _getNow = getNow ?? (() => DateTimeOffset.UtcNow);
    }

    public WatchOverviewState State { get; private set; } = WatchOverviewState.Empty;
    public WatchSnapshot? LastSnapshot { get; private set; }

    public void SeedPollHealth(WatchPollHealthDto? health, DateTimeOffset? lastSuccessfulAt)
    {
        if (lastSuccessfulAt is null)
        {
            return;
        }

        State = State with
        {
            PollHealth = new WatchOverviewPollHealthState(
                health,
                lastSuccessfulAt,
                false,
                null),
        };
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _queries.FetchSnapshotAsync(
                WatchDemandBrowseQuery.Default,
                WatchAlertBrowseQuery.Default,
                cancellationToken)
            .ConfigureAwait(false);
        var now = _getNow();
        LastSnapshot = snapshot;

        State = new WatchOverviewState(
            ApplyPollHealth(State.PollHealth, snapshot, now),
            ApplyAlerts(State.Alerts, snapshot, now),
            ApplyDemands(State.Demands, snapshot, now));
    }

    private static WatchOverviewPollHealthState ApplyPollHealth(
        WatchOverviewPollHealthState current,
        WatchSnapshot snapshot,
        DateTimeOffset now) =>
        snapshot.PollHealthSucceeded
            ? new(snapshot.PollHealth, now, false, null)
            : current with
            {
                IsStale = current.LastSuccessfulAt is not null,
                Error = snapshot.PollHealthError ?? snapshot.FetchError ?? "最近轮询健康读取失败",
            };

    private static WatchOverviewAlertState ApplyAlerts(
        WatchOverviewAlertState current,
        WatchSnapshot snapshot,
        DateTimeOffset now) =>
        snapshot.AlertsSucceeded
            ? new(snapshot.Alerts, snapshot.AlertsHasMore, now, false, null)
            : current with
            {
                IsStale = current.LastSuccessfulAt is not null,
                Error = snapshot.AlertsError ?? snapshot.FetchError ?? "活动 IngestAlert 读取失败",
            };

    private static WatchOverviewDemandState ApplyDemands(
        WatchOverviewDemandState current,
        WatchSnapshot snapshot,
        DateTimeOffset now) =>
        snapshot.DemandsSucceeded
            ? new(snapshot.Demands, snapshot.DemandsHasMore, now, false, null)
            : current with
            {
                IsStale = current.LastSuccessfulAt is not null,
                Error = snapshot.DemandsError ?? snapshot.FetchError ?? "VISIBLE TransportDemand 读取失败",
            };
}
