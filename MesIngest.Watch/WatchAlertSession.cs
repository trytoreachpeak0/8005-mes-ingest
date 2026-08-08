using System.Globalization;
using System.Net;

namespace MesIngest.Watch;

internal enum WatchAlertBrowseOutcome
{
    Succeeded,
    Canceled,
    Superseded,
    ValidationFailed,
    Failed,
}

internal sealed record WatchAlertDraft(
    bool Active = true,
    string? Code = null,
    string? Severity = null,
    string? LastSeenAtFrom = null,
    string? LastSeenAtTo = null)
{
    public static WatchAlertDraft Default { get; } = new();

    public static IReadOnlyList<string> ProductionCodes { get; } =
    [
        "POLL_FAILURE",
        "POLL_INCOMPLETE",
        "DUPLICATE_RECONCILE_KEY",
        "PAUSED_ZERO_DROP",
        "FIELD_DRIFT",
        "REAPPEAR_AFTER_GONE",
    ];

    public static IReadOnlyList<string> Severities { get; } = ["ERROR", "WARNING"];
}

internal sealed record WatchAlertState(
    WatchAlertBrowseQuery CommittedQuery,
    IReadOnlyList<WatchAlertDto> Items,
    int PageNumber,
    string? NextCursor,
    bool HasMore,
    DateTimeOffset? LastSuccessfulAt,
    WatchAlertDraft Draft,
    string? ValidationError,
    Exception? Failure,
    bool IsRefreshing,
    string? Notice,
    string? SelectedAlertId)
{
    public bool CanMovePrevious => PageNumber > 1;
    public bool CanMoveNext => HasMore && !string.IsNullOrWhiteSpace(NextCursor);
    public WatchAlertDto? SelectedAlert => SelectedAlertId is null
        ? null
        : Items.FirstOrDefault(item => string.Equals(
            item.AlertId,
            SelectedAlertId,
            StringComparison.Ordinal));

    public static WatchAlertState Empty { get; } = new(
        WatchAlertBrowseQuery.Default,
        [],
        1,
        null,
        false,
        null,
        WatchAlertDraft.Default,
        null,
        null,
        false,
        null,
        null);
}

internal sealed class WatchAlertSession : IDisposable
{
    private readonly object _gate = new();
    private readonly IWatchReadQueries _queries;
    private readonly TimeProvider _timeProvider;
    private readonly List<string?> _arrivalCursors = [null];
    private CancellationTokenSource? _activeCancellation;
    private long _requestGeneration;
    private bool _disposed;

    public WatchAlertSession(IWatchReadQueries queries, TimeProvider? timeProvider = null)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public WatchAlertState State { get; private set; } = WatchAlertState.Empty;

    public void UpdateDraft(WatchAlertDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        State = State with { Draft = draft, ValidationError = null };
    }

    public void SelectAlert(string? alertId)
    {
        State = State with
        {
            SelectedAlertId = string.IsNullOrWhiteSpace(alertId) ? null : alertId,
        };
    }

    public Task<WatchAlertBrowseOutcome> LoadInitialAsync(
        CancellationToken cancellationToken = default) =>
        FetchAndCommitAsync(
            WatchAlertBrowseQuery.Default,
            page =>
            {
                CommitFirstPage(WatchAlertBrowseQuery.Default, page);
                State = State with { Draft = WatchAlertDraft.Default };
            },
            cancellationToken);

    public async Task<WatchAlertBrowseOutcome> SubmitDraftAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildQuery(State.Draft, out var query, out var error))
        {
            State = State with { ValidationError = error };
            return WatchAlertBrowseOutcome.ValidationFailed;
        }

        return await FetchAndCommitAsync(
                query,
                page => CommitFirstPage(query, page),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<WatchAlertBrowseOutcome> ResetAsync(
        CancellationToken cancellationToken = default) =>
        FetchAndCommitAsync(
            WatchAlertBrowseQuery.Default,
            page =>
            {
                CommitFirstPage(WatchAlertBrowseQuery.Default, page);
                State = State with { Draft = WatchAlertDraft.Default };
            },
            cancellationToken);

    public Task<WatchAlertBrowseOutcome> ApplySortAsync(
        string? header,
        CancellationToken cancellationToken = default)
    {
        if (!State.CommittedQuery.TryApplySort(header, out var query))
        {
            return Task.FromResult(WatchAlertBrowseOutcome.ValidationFailed);
        }

        query = query with { Cursor = null, Limit = 100 };
        return FetchAndCommitAsync(
            query,
            page => CommitFirstPage(query, page),
            cancellationToken);
    }

    public Task<WatchAlertBrowseOutcome> MoveNextAsync(
        CancellationToken cancellationToken = default)
    {
        if (!State.CanMoveNext)
        {
            return Task.FromResult(WatchAlertBrowseOutcome.ValidationFailed);
        }

        var cursor = State.NextCursor;
        var committed = State.CommittedQuery;
        var targetPage = State.PageNumber + 1;
        return FetchAndCommitAsync(
            committed with { Cursor = cursor },
            page =>
            {
                _arrivalCursors.RemoveRange(
                    targetPage - 1,
                    _arrivalCursors.Count - (targetPage - 1));
                _arrivalCursors.Add(cursor);
                CommitPage(committed, page, targetPage);
            },
            cancellationToken);
    }

    public Task<WatchAlertBrowseOutcome> MovePreviousAsync(
        CancellationToken cancellationToken = default)
    {
        if (!State.CanMovePrevious)
        {
            return Task.FromResult(WatchAlertBrowseOutcome.ValidationFailed);
        }

        var targetPage = State.PageNumber - 1;
        var cursor = _arrivalCursors[targetPage - 1];
        var committed = State.CommittedQuery;
        return FetchAndCommitAsync(
            committed with { Cursor = cursor },
            page =>
            {
                _arrivalCursors.RemoveRange(targetPage, _arrivalCursors.Count - targetPage);
                CommitPage(committed, page, targetPage);
            },
            cancellationToken);
    }

    public Task<WatchAlertBrowseOutcome> RefreshCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        var pageNumber = State.PageNumber;
        var committed = State.CommittedQuery;
        var cursor = _arrivalCursors[pageNumber - 1];
        return FetchAndCommitAsync(
            committed with { Cursor = cursor },
            page =>
            {
                _arrivalCursors.RemoveRange(pageNumber, _arrivalCursors.Count - pageNumber);
                CommitPage(committed, page, pageNumber, preserveSelection: true);
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

    private void CommitFirstPage(WatchAlertBrowseQuery query, WatchAlertPage page)
    {
        _arrivalCursors.Clear();
        _arrivalCursors.Add(null);
        CommitPage(query, page, 1);
    }

    private void CommitPage(
        WatchAlertBrowseQuery query,
        WatchAlertPage page,
        int pageNumber,
        bool preserveSelection = false)
    {
        var items = page.Items
            .Where(item => WatchAlertDraft.ProductionCodes.Contains(item.Code, StringComparer.Ordinal))
            .ToList();
        var selectedAlertId = preserveSelection
            && State.SelectedAlertId is { } selected
            && items.Any(item => string.Equals(item.AlertId, selected, StringComparison.Ordinal))
                ? selected
                : null;
        var selectionWasLost = preserveSelection
            && State.SelectedAlertId is not null
            && selectedAlertId is null;
        State = new WatchAlertState(
            query,
            items,
            pageNumber,
            page.NextCursor,
            page.HasMore,
            _timeProvider.GetUtcNow(),
            State.Draft,
            null,
            null,
            false,
            selectionWasLost ? "所选 IngestAlert 已不在当前页" : null,
            selectedAlertId);
    }

    private bool TryBuildQuery(
        WatchAlertDraft draft,
        out WatchAlertBrowseQuery query,
        out string? error)
    {
        query = WatchAlertBrowseQuery.Default;
        error = null;
        var code = NullIfBlank(draft.Code);
        if (code is not null
            && !WatchAlertDraft.ProductionCodes.Contains(code, StringComparer.Ordinal))
        {
            error = "Code 必须从六类生产 IngestAlert 中选择。";
            return false;
        }

        var severity = NullIfBlank(draft.Severity);
        if (severity is not null
            && !WatchAlertDraft.Severities.Contains(severity, StringComparer.Ordinal))
        {
            error = "Severity 必须是 ERROR 或 WARNING。";
            return false;
        }

        if (!TryParseDate(draft.LastSeenAtFrom, out var from)
            || !TryParseDate(draft.LastSeenAtTo, out var to))
        {
            error = "LastSeenAt 起止必须是包含时区的有效日期时间。";
            return false;
        }

        if (from is not null && to is not null && from > to)
        {
            error = "LastSeenAt 起始时间不能晚于结束时间。";
            return false;
        }

        query = WatchAlertBrowseQuery.Default with
        {
            Active = draft.Active,
            Code = code,
            Severity = severity,
            From = from,
            To = to,
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

    private async Task<WatchAlertBrowseOutcome> FetchAndCommitAsync(
        WatchAlertBrowseQuery requestQuery,
        Action<WatchAlertPage> commit,
        CancellationToken cancellationToken)
    {
        var request = BeginRequest(cancellationToken);
        State = State with { IsRefreshing = true, Notice = null };
        try
        {
            var (page, recovered) = await FetchPageWithCursorRecoveryAsync(
                    requestQuery,
                    request.Cancellation.Token)
                .ConfigureAwait(false);
            if (!IsCurrent(request))
            {
                return WatchAlertBrowseOutcome.Superseded;
            }

            if (request.Cancellation.IsCancellationRequested)
            {
                State = State with { IsRefreshing = false };
                return WatchAlertBrowseOutcome.Canceled;
            }

            if (recovered)
            {
                CommitFirstPage(State.CommittedQuery, page);
                State = State with { Notice = "数据已变化，已返回第 1 页" };
            }
            else
            {
                commit(page);
            }

            return WatchAlertBrowseOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
            if (!IsCurrent(request))
            {
                return WatchAlertBrowseOutcome.Superseded;
            }

            State = State with { IsRefreshing = false };
            return WatchAlertBrowseOutcome.Canceled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!IsCurrent(request))
            {
                return WatchAlertBrowseOutcome.Superseded;
            }

            State = State with { Failure = ex, IsRefreshing = false };
            return WatchAlertBrowseOutcome.Failed;
        }
        finally
        {
            EndRequest(request);
        }
    }

    private async Task<(WatchAlertPage Page, bool Recovered)> FetchPageWithCursorRecoveryAsync(
        WatchAlertBrowseQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await _queries.FetchAlertPageAsync(query, cancellationToken).ConfigureAwait(false), false);
        }
        catch (WatchEndpointFetchException ex) when (IsCursorBadRequest(query, ex))
        {
            var firstPage = await _queries.FetchAlertPageAsync(
                    query with { Cursor = null },
                    cancellationToken)
                .ConfigureAwait(false);
            return (firstPage, true);
        }
    }

    private static bool IsCursorBadRequest(
        WatchAlertBrowseQuery query,
        WatchEndpointFetchException exception) =>
        !string.IsNullOrWhiteSpace(query.Cursor)
        && exception.InnerException is HttpRequestException
        {
            StatusCode: HttpStatusCode.BadRequest,
        }
        && exception.Message.Contains("cursor", StringComparison.OrdinalIgnoreCase);

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

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct ActiveRequest(
        long Generation,
        CancellationTokenSource Cancellation);
}
