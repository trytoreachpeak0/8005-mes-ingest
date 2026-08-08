using MesIngest.Core;

namespace MesIngest.Watch;

internal enum AlertDemandNavigationOutcome
{
    Succeeded,
    NotFound,
    UnsupportedStatus,
    MissingFromPage,
    Canceled,
    Superseded,
    Failed,
}

internal sealed record AlertDemandNavigationResult(
    AlertDemandNavigationOutcome Outcome,
    WatchDemandViewKind? ViewKind = null,
    WatchDemandDto? Demand = null,
    string? Message = null);

/// <summary>
/// Coordinates exact Alert-to-Demand navigation without treating a business key
/// as the identity of a TransportDemand instance.
/// </summary>
internal sealed class AlertDemandNavigator
{
    private readonly IWatchReadQueries _queries;
    private readonly WatchDemandSession _visibleSession;
    private readonly WatchDemandSession _goneSession;

    public AlertDemandNavigator(
        IWatchReadQueries queries,
        WatchDemandSession visibleSession,
        WatchDemandSession goneSession)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _visibleSession = visibleSession ?? throw new ArgumentNullException(nameof(visibleSession));
        _goneSession = goneSession ?? throw new ArgumentNullException(nameof(goneSession));
    }

    public async Task<AlertDemandNavigationResult> LocateExactAsync(
        AlertDemandTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        WatchDemandDto? exact;
        try
        {
            exact = await _queries.FetchDemandByIdAsync(target.DemandId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AlertDemandNavigationResult(AlertDemandNavigationOutcome.Canceled);
        }
        catch (Exception ex)
        {
            return new AlertDemandNavigationResult(
                AlertDemandNavigationOutcome.Failed,
                Message: $"Exact DemandId lookup failed: {ex.Message}");
        }

        if (exact is null)
        {
            return new AlertDemandNavigationResult(
                AlertDemandNavigationOutcome.NotFound,
                Message: AlertDemandLocateHints.NotFound(target));
        }

        var (viewKind, session) = exact.Status.ToUpperInvariant() switch
        {
            "VISIBLE" => (WatchDemandViewKind.Visible, _visibleSession),
            "GONE" => (WatchDemandViewKind.Gone, _goneSession),
            _ => ((WatchDemandViewKind?)null, (WatchDemandSession?)null),
        };
        if (viewKind is null || session is null)
        {
            return new AlertDemandNavigationResult(
                AlertDemandNavigationOutcome.UnsupportedStatus,
                Message: $"DemandId {exact.DemandId} returned unsupported status '{exact.Status}'.");
        }

        var locate = await session.LocateExactAsync(exact.DemandId, cancellationToken)
            .ConfigureAwait(false);
        return locate.Outcome switch
        {
            WatchDemandExactLocateOutcome.Succeeded => new(
                AlertDemandNavigationOutcome.Succeeded,
                viewKind,
                locate.Demand),
            WatchDemandExactLocateOutcome.MissingFromPage => new(
                AlertDemandNavigationOutcome.MissingFromPage,
                Message: AlertDemandLocateHints.OutsideCurrentBrowse(target, exact.Status)),
            WatchDemandExactLocateOutcome.Canceled => new(AlertDemandNavigationOutcome.Canceled),
            WatchDemandExactLocateOutcome.Superseded => new(AlertDemandNavigationOutcome.Superseded),
            _ => new(
                AlertDemandNavigationOutcome.Failed,
                Message: $"DemandId {exact.DemandId} page query failed: {locate.Failure?.Message ?? "unknown error"}"),
        };
    }

    public async Task<AlertDemandNavigationResult> SearchBusinessKeyAsync(
        TransportDemandKey businessKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(businessKey);
        _visibleSession.UpdateDraft(new WatchDemandDraft(
            TaskType: businessKey.TaskType,
            Sublot: businessKey.Sublot));
        var outcome = await _visibleSession.SubmitDraftAsync(cancellationToken)
            .ConfigureAwait(false);
        return outcome switch
        {
            WatchDemandBrowseOutcome.Succeeded => new AlertDemandNavigationResult(
                AlertDemandNavigationOutcome.Succeeded,
                WatchDemandViewKind.Visible,
                Message: "已按 TASK_TYPE + SUBLOT 查询当前 VISIBLE 任务；结果不代表唯一历史实例。"),
            WatchDemandBrowseOutcome.Canceled => new(AlertDemandNavigationOutcome.Canceled),
            WatchDemandBrowseOutcome.Superseded => new(AlertDemandNavigationOutcome.Superseded),
            _ => new AlertDemandNavigationResult(
                AlertDemandNavigationOutcome.Failed,
                Message: $"按业务键查询失败: {_visibleSession.State.ValidationError
                    ?? _visibleSession.State.Failure?.Message
                    ?? "unknown error"}"),
        };
    }
}
