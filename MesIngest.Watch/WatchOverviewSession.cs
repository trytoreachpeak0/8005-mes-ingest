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
    private readonly object _gate = new();
    private readonly IWatchOverviewQueries _queries;
    private readonly Func<DateTimeOffset> _getNow;

    public WatchOverviewSession(
        IWatchOverviewQueries queries,
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

    public async Task RefreshAsync(
        Func<WatchOverviewState, Task>? onResourceCommitted = null,
        CancellationToken cancellationToken = default)
    {
        var healthTask = FetchPollHealthAsync(onResourceCommitted, cancellationToken);
        var alertsTask = FetchAlertsAsync(onResourceCommitted, cancellationToken);
        var demandsTask = FetchDemandsAsync(onResourceCommitted, cancellationToken);
        await Task.WhenAll(healthTask, alertsTask, demandsTask).ConfigureAwait(false);

        var health = await healthTask.ConfigureAwait(false);
        var alerts = await alertsTask.ConfigureAwait(false);
        var demands = await demandsTask.ConfigureAwait(false);
        var firstFailure = new[] { demands.Failure, alerts.Failure, health.Failure }
            .FirstOrDefault(failure => failure is not null);
        LastSnapshot = new WatchSnapshot(
            demands.Value?.Items ?? [],
            alerts.Value?.Items ?? [],
            health.Value,
            firstFailure?.Message,
            FailedEndpoint: firstFailure?.Endpoint,
            FailedStage: firstFailure?.Stage,
            FailedElapsed: firstFailure?.Elapsed,
            DemandsNextCursor: demands.Value?.NextCursor,
            DemandsHasMore: demands.Value?.HasMore ?? false,
            AlertsNextCursor: alerts.Value?.NextCursor,
            AlertsHasMore: alerts.Value?.HasMore ?? false,
            DemandsSucceeded: demands.Succeeded,
            AlertsSucceeded: alerts.Succeeded,
            PollHealthSucceeded: health.Succeeded,
            DemandsError: demands.Failure?.Message,
            AlertsError: alerts.Failure?.Message,
            PollHealthError: health.Failure?.Message);
    }

    private async Task<WatchOverviewFetch<WatchPollHealthDto?>> FetchPollHealthAsync(
        Func<WatchOverviewState, Task>? onCommitted,
        CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(
                () => _queries.FetchPollHealthAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        WatchOverviewState committed;
        lock (_gate)
        {
            var current = State.PollHealth;
            State = State with
            {
                PollHealth = result.Succeeded
                    ? new(result.Value, _getNow(), false, null)
                    : current with
                    {
                        IsStale = current.LastSuccessfulAt is not null,
                        Error = result.Failure?.Message ?? "最近轮询健康读取失败",
                    },
            };
            committed = State;
        }

        await NotifyAsync(onCommitted, committed).ConfigureAwait(false);
        return result;
    }

    private async Task<WatchOverviewFetch<WatchAlertPage>> FetchAlertsAsync(
        Func<WatchOverviewState, Task>? onCommitted,
        CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(
                () => _queries.FetchAlertPageAsync(WatchAlertBrowseQuery.Default, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        WatchOverviewState committed;
        lock (_gate)
        {
            var current = State.Alerts;
            State = State with
            {
                Alerts = result.Succeeded
                    ? new(result.Value!.Items, result.Value.HasMore, _getNow(), false, null)
                    : current with
                    {
                        IsStale = current.LastSuccessfulAt is not null,
                        Error = result.Failure?.Message ?? "活动 IngestAlert 读取失败",
                    },
            };
            committed = State;
        }

        await NotifyAsync(onCommitted, committed).ConfigureAwait(false);
        return result;
    }

    private async Task<WatchOverviewFetch<WatchDemandPage>> FetchDemandsAsync(
        Func<WatchOverviewState, Task>? onCommitted,
        CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(
                () => _queries.FetchDemandPageAsync(WatchDemandBrowseQuery.Default, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        WatchOverviewState committed;
        lock (_gate)
        {
            var current = State.Demands;
            State = State with
            {
                Demands = result.Succeeded
                    ? new(result.Value!.Items, result.Value.HasMore, _getNow(), false, null)
                    : current with
                    {
                        IsStale = current.LastSuccessfulAt is not null,
                        Error = result.Failure?.Message ?? "VISIBLE TransportDemand 读取失败",
                    },
            };
            committed = State;
        }

        await NotifyAsync(onCommitted, committed).ConfigureAwait(false);
        return result;
    }

    private static async Task<WatchOverviewFetch<T>> CaptureAsync<T>(
        Func<Task<T>> fetch,
        CancellationToken cancellationToken)
    {
        try
        {
            return WatchOverviewFetch<T>.Success(await fetch().ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WatchHostQueryException ex)
        {
            return WatchOverviewFetch<T>.Failed(new WatchOverviewFailure(
                $"endpoint={ex.Endpoint} correlationId={ex.CorrelationId} {ex.Message}",
                ex.Endpoint,
                ex.Kind.ToString(),
                TimeSpan.Zero));
        }
        catch (WatchEndpointFetchException ex)
        {
            return WatchOverviewFetch<T>.Failed(new WatchOverviewFailure(
                $"endpoint={ex.Endpoint} stage={ex.Stage} {ex.Message}",
                ex.Endpoint,
                ex.Stage,
                ex.Elapsed));
        }
        catch (Exception ex)
        {
            return WatchOverviewFetch<T>.Failed(new WatchOverviewFailure(
                ex.Message,
                null,
                "UNKNOWN",
                TimeSpan.Zero));
        }
    }

    private static Task NotifyAsync(
        Func<WatchOverviewState, Task>? onCommitted,
        WatchOverviewState state) =>
        onCommitted?.Invoke(state) ?? Task.CompletedTask;
}

internal sealed record WatchOverviewFailure(
    string Message,
    string? Endpoint,
    string Stage,
    TimeSpan Elapsed);

internal sealed record WatchOverviewFetch<T>(
    bool Succeeded,
    T? Value,
    WatchOverviewFailure? Failure)
{
    public static WatchOverviewFetch<T> Success(T value) => new(true, value, null);

    public static WatchOverviewFetch<T> Failed(WatchOverviewFailure failure) =>
        new(false, default, failure);
}
