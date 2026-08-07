using System.Globalization;
using System.Net;

namespace MesIngest.Watch;

internal enum WatchDemandBrowseOutcome
{
    Succeeded,
    Canceled,
    Superseded,
    ValidationFailed,
    Failed,
}

internal sealed record WatchVisibleDemandDraft(
    string? TaskType = null,
    string? Sublot = null,
    string? DemandId = null,
    string? DatesFrom = null,
    string? DatesTo = null)
{
    public static WatchVisibleDemandDraft Default { get; } = new();

    public static IReadOnlyList<string> ProductionTaskTypes { get; } =
    [
        "DIE_TO_WIRE_STAGING",
        "DIE_TO_OVEN",
        "WIRE_TO_GATE",
        "WIRE_TO_OPTICAL",
        "STAGING_TO_WIRE",
        "WIRE_TO_NITROGEN",
    ];
}

internal sealed record WatchVisibleDemandState(
    WatchDemandBrowseQuery CommittedQuery,
    IReadOnlyList<WatchDemandDto> Items,
    int PageNumber,
    string? NextCursor,
    bool HasMore,
    DateTimeOffset? LastSuccessfulAt,
    WatchVisibleDemandDraft Draft,
    string? ValidationError,
    Exception? Failure,
    bool IsRefreshing,
    string? Notice,
    string? SelectedDemandId)
{
    public bool CanMovePrevious => PageNumber > 1;
    public bool CanMoveNext => HasMore && !string.IsNullOrWhiteSpace(NextCursor);

    public static WatchVisibleDemandState Empty { get; } = new(
        WatchDemandBrowseQuery.Default,
        [],
        1,
        null,
        false,
        null,
        WatchVisibleDemandDraft.Default,
        null,
        null,
        false,
        null,
        null);
}

internal sealed class WatchVisibleDemandSession : IDisposable
{
    private readonly object _gate = new();
    private readonly IWatchReadQueries _queries;
    private readonly List<string?> _arrivalCursors = [null];
    private CancellationTokenSource? _activeCancellation;
    private long _requestGeneration;
    private bool _disposed;

    public WatchVisibleDemandSession(IWatchReadQueries queries)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
    }

    public WatchVisibleDemandState State { get; private set; } = WatchVisibleDemandState.Empty;

    public void UpdateDraft(WatchVisibleDemandDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        State = State with { Draft = draft, ValidationError = null };
    }

    public void SelectDemand(string? demandId)
    {
        var selected = string.IsNullOrWhiteSpace(demandId) ? null : demandId;
        State = State with { SelectedDemandId = selected };
    }

    public async Task<WatchDemandBrowseOutcome> SubmitDraftAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildQuery(State.Draft, out var query, out var validationError))
        {
            State = State with { ValidationError = validationError };
            return WatchDemandBrowseOutcome.ValidationFailed;
        }

        return await FetchAndCommitAsync(
                query,
                page => CommitFirstPage(query, page),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WatchDemandBrowseOutcome> LoadInitialAsync(
        CancellationToken cancellationToken = default)
    {
        var query = WatchDemandBrowseQuery.Default;
        return await FetchAndCommitAsync(
                query,
                page => CommitFirstPage(query, page),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WatchDemandBrowseOutcome> MoveNextAsync(
        CancellationToken cancellationToken = default)
    {
        if (!State.CanMoveNext)
        {
            return WatchDemandBrowseOutcome.ValidationFailed;
        }

        var cursor = State.NextCursor;
        var committedQuery = State.CommittedQuery;
        var nextPage = State.PageNumber + 1;
        return await FetchAndCommitAsync(
                committedQuery with { Cursor = cursor },
                page =>
                {
                    _arrivalCursors.RemoveRange(
                        nextPage - 1,
                        _arrivalCursors.Count - (nextPage - 1));
                    _arrivalCursors.Add(cursor);
                    CommitPage(committedQuery, page, nextPage);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WatchDemandBrowseOutcome> MovePreviousAsync(
        CancellationToken cancellationToken = default)
    {
        if (!State.CanMovePrevious)
        {
            return WatchDemandBrowseOutcome.ValidationFailed;
        }

        var targetPage = State.PageNumber - 1;
        var cursor = _arrivalCursors[targetPage - 1];
        var committedQuery = State.CommittedQuery;
        return await FetchAndCommitAsync(
                committedQuery with { Cursor = cursor },
                page =>
                {
                    _arrivalCursors.RemoveRange(targetPage, _arrivalCursors.Count - targetPage);
                    CommitPage(committedQuery, page, targetPage);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WatchDemandBrowseOutcome> ApplySortAsync(
        string? header,
        CancellationToken cancellationToken = default)
    {
        if (!State.CommittedQuery.TryApplySort(header, out var query))
        {
            return WatchDemandBrowseOutcome.ValidationFailed;
        }

        query = query with { Cursor = null, Limit = 100 };
        return await FetchAndCommitAsync(
                query,
                page => CommitFirstPage(query, page),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WatchDemandBrowseOutcome> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        var query = WatchDemandBrowseQuery.Default;
        return await FetchAndCommitAsync(
                query,
                page =>
                {
                    CommitFirstPage(query, page);
                    State = State with { Draft = WatchVisibleDemandDraft.Default };
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<WatchDemandBrowseOutcome> RefreshCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        var pageNumber = State.PageNumber;
        var committedQuery = State.CommittedQuery;
        var cursor = _arrivalCursors[pageNumber - 1];
        return FetchAndCommitAsync(
            committedQuery with { Cursor = cursor },
            page =>
            {
                _arrivalCursors.RemoveRange(pageNumber, _arrivalCursors.Count - pageNumber);
                CommitPage(committedQuery, page, pageNumber, preserveSelection: true);
            },
            cancellationToken);
    }

    public void CancelActive(bool userInitiated)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _activeCancellation;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        State = State with
        {
            IsRefreshing = false,
            Notice = userInitiated ? "已取消" : State.Notice,
        };
    }

    private void CommitPage(
        WatchDemandBrowseQuery committedQuery,
        WatchDemandPage page,
        int pageNumber,
        bool preserveSelection = false)
    {
        var selectedDemandId = preserveSelection
            && State.SelectedDemandId is { } selected
            && page.Items.Any(item => string.Equals(
                item.DemandId,
                selected,
                StringComparison.Ordinal))
                ? selected
                : null;
        var selectionWasLost = preserveSelection
            && State.SelectedDemandId is not null
            && selectedDemandId is null;
        State = new WatchVisibleDemandState(
            committedQuery,
            page.Items,
            pageNumber,
            page.NextCursor,
            page.HasMore,
            DateTimeOffset.UtcNow,
            State.Draft,
            null,
            null,
            false,
            selectionWasLost ? "所选 TransportDemand 已不在当前页" : null,
            selectedDemandId);
    }

    private void CommitFirstPage(
        WatchDemandBrowseQuery committedQuery,
        WatchDemandPage page)
    {
        _arrivalCursors.Clear();
        _arrivalCursors.Add(null);
        CommitPage(committedQuery, page, pageNumber: 1);
    }

    private async Task<WatchDemandBrowseOutcome> FetchAndCommitAsync(
        WatchDemandBrowseQuery requestQuery,
        Action<WatchDemandPage> commit,
        CancellationToken cancellationToken)
    {
        var request = BeginRequest(cancellationToken);
        State = State with { IsRefreshing = true, Notice = null };
        try
        {
            var (page, recoveredFromCursor) = await FetchPageWithCursorRecoveryAsync(
                    requestQuery,
                    request.Cancellation.Token)
                .ConfigureAwait(false);
            if (!IsCurrent(request))
            {
                return WatchDemandBrowseOutcome.Superseded;
            }

            if (request.Cancellation.IsCancellationRequested)
            {
                State = State with { IsRefreshing = false };
                return WatchDemandBrowseOutcome.Canceled;
            }

            if (recoveredFromCursor)
            {
                CommitFirstPage(State.CommittedQuery, page);
                State = State with { Notice = "数据已变化，已返回第 1 页" };
            }
            else
            {
                commit(page);
            }

            return WatchDemandBrowseOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
            if (!IsCurrent(request))
            {
                return WatchDemandBrowseOutcome.Superseded;
            }

            State = State with { IsRefreshing = false };
            return WatchDemandBrowseOutcome.Canceled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!IsCurrent(request))
            {
                return WatchDemandBrowseOutcome.Superseded;
            }

            State = State with { Failure = ex, IsRefreshing = false };
            return WatchDemandBrowseOutcome.Failed;
        }
        finally
        {
            EndRequest(request);
        }
    }

    private async Task<(WatchDemandPage Page, bool RecoveredFromCursor)>
        FetchPageWithCursorRecoveryAsync(
            WatchDemandBrowseQuery query,
            CancellationToken cancellationToken)
    {
        try
        {
            var page = await _queries.FetchDemandPageAsync(query, cancellationToken)
                .ConfigureAwait(false);
            return (page, false);
        }
        catch (WatchEndpointFetchException ex) when (IsCursorBadRequest(query, ex))
        {
            var firstPage = await _queries.FetchDemandPageAsync(
                    query with { Cursor = null },
                    cancellationToken)
                .ConfigureAwait(false);
            return (firstPage, true);
        }
    }

    private static bool IsCursorBadRequest(
        WatchDemandBrowseQuery query,
        WatchEndpointFetchException exception) =>
        !string.IsNullOrWhiteSpace(query.Cursor)
        && exception.InnerException is HttpRequestException
        {
            StatusCode: HttpStatusCode.BadRequest,
        }
        && exception.Message.Contains("cursor", StringComparison.OrdinalIgnoreCase);

    private static bool TryBuildQuery(
        WatchVisibleDemandDraft draft,
        out WatchDemandBrowseQuery query,
        out string? validationError)
    {
        query = WatchDemandBrowseQuery.Default;
        validationError = null;

        var taskType = NullIfBlank(draft.TaskType);
        if (taskType is not null
            && !WatchVisibleDemandDraft.ProductionTaskTypes.Contains(
                taskType,
                StringComparer.Ordinal))
        {
            validationError = "TASK_TYPE 必须从六类生产值中选择。";
            return false;
        }

        var demandId = NullIfBlank(draft.DemandId)?.ToLowerInvariant();
        if (!WatchDemandBrowseQuery.IsDemandIdFilterReady(demandId))
        {
            validationError = "DemandId 必须是 6–32 位小写十六进制前缀或完整值。";
            return false;
        }

        if (!TryParseDate(draft.DatesFrom, out var datesFrom)
            || !TryParseDate(draft.DatesTo, out var datesTo))
        {
            validationError = "DATES 起止必须是包含时区的有效日期时间。";
            return false;
        }

        if (datesFrom is not null && datesTo is not null && datesFrom > datesTo)
        {
            validationError = "DATES 起始时间不能晚于结束时间。";
            return false;
        }

        query = WatchDemandBrowseQuery.Default with
        {
            TaskType = taskType,
            Sublot = NullIfBlank(draft.Sublot),
            DemandId = demandId,
            DatesFrom = datesFrom,
            DatesTo = datesTo,
        };
        return true;
    }

    private static bool TryParseDate(string? raw, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(
                raw.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _requestGeneration++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
        }

        cancellation?.Cancel();
    }

    private ActiveRequest BeginRequest(CancellationToken cancellationToken)
    {
        CancellationTokenSource? previous;
        ActiveRequest request;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _activeCancellation;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            request = new ActiveRequest(++_requestGeneration, cancellation);
            _activeCancellation = cancellation;
        }

        previous?.Cancel();
        return request;
    }

    private bool IsCurrent(ActiveRequest request)
    {
        lock (_gate)
        {
            return !_disposed
                && request.Generation == _requestGeneration
                && ReferenceEquals(request.Cancellation, _activeCancellation);
        }
    }

    private void EndRequest(ActiveRequest request)
    {
        lock (_gate)
        {
            if (ReferenceEquals(request.Cancellation, _activeCancellation))
            {
                _activeCancellation = null;
            }
        }

        request.Cancellation.Dispose();
    }

    private readonly record struct ActiveRequest(
        long Generation,
        CancellationTokenSource Cancellation);
}
