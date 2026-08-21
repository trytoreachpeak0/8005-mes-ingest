using MesIngest.Core.SeriesProjection;
using System.Text.Json;

namespace MesIngest.Watch;

internal interface IWatchV2ApiClient : IDisposable
{
    Task VerifyContractAsync(CancellationToken cancellationToken);

    Task<WatchOverviewSnapshot> FetchOverviewAsync(
        WatchOverviewQuery query,
        CancellationToken cancellationToken = default);

    Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
        DemandSeriesBrowseQuery query,
        CancellationToken cancellationToken = default);

    Task<DemandSeriesDetailSnapshot> FetchDemandSeriesDetailAsync(
        string seriesId,
        string snapshotReference,
        CancellationToken cancellationToken = default);

    Task<ReadabilityAuditListSnapshot> FetchReadabilityAuditAsync(
        ReadabilityAuditQuery query,
        CancellationToken cancellationToken = default);

    Task<ReadabilityAuditDetailSnapshot> FetchReadabilityAuditDetailAsync(
        string demandId,
        string snapshotReference,
        CancellationToken cancellationToken = default);

    Task<ErrorSearchListSnapshot> FetchErrorSearchAsync(
        ErrorSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<ErrorSearchDetailSnapshot> FetchErrorSearchDetailAsync(
        string seriesId,
        string snapshotReference,
        CancellationToken cancellationToken = default);

    Task<ErrorSearchRawEvidenceSnapshot> FetchErrorRawEvidenceAsync(
        string seriesId,
        string evidenceId,
        string snapshotReference,
        ErrorSearchRawEvidenceQuery query,
        CancellationToken cancellationToken = default);

    Task<CurrentIngestAttentionSnapshot> FetchCurrentAttentionAsync(
        CurrentIngestAttentionQuery query,
        CancellationToken cancellationToken = default);
}

internal readonly record struct WatchNoDetail;

internal sealed record WatchV2ViewState<TSnapshot, TDetail>(
    long HostGeneration,
    long RequestGeneration,
    long SelectionGeneration,
    bool IsRefreshing,
    string? PendingQueryKey,
    string? CommittedQueryKey,
    TSnapshot? Snapshot,
    DateTimeOffset? LastSuccessfulAt,
    DateTimeOffset? LastFailureAt,
    string? FailedQueryKey,
    WatchHostFailureKind FailureKind,
    string? FailureCode,
    string? ErrorMessage,
    string? Endpoint,
    string? CorrelationId,
    string? SelectedId,
    string? DetailFocusId,
    TDetail? Detail,
    string? SelectionNotice,
    bool IsDetailLoading,
    DateTimeOffset? DetailLastFailureAt,
    WatchHostFailureKind DetailFailureKind,
    string? DetailFailureCode,
    string? DetailErrorMessage,
    string? DetailEndpoint,
    string? DetailCorrelationId)
{
    public bool IsStale => Snapshot is not null
        && (LastFailureAt is not null
            || !string.Equals(PendingQueryKey, CommittedQueryKey, StringComparison.Ordinal));

    public static WatchV2ViewState<TSnapshot, TDetail> Empty(long hostGeneration) => new(
        hostGeneration,
        RequestGeneration: 0,
        SelectionGeneration: 0,
        IsRefreshing: false,
        PendingQueryKey: null,
        CommittedQueryKey: null,
        Snapshot: default,
        LastSuccessfulAt: null,
        LastFailureAt: null,
        FailedQueryKey: null,
        WatchHostFailureKind.None,
        FailureCode: null,
        ErrorMessage: null,
        Endpoint: null,
        CorrelationId: null,
        SelectedId: null,
        DetailFocusId: null,
        Detail: default,
        SelectionNotice: null,
        IsDetailLoading: false,
        DetailLastFailureAt: null,
        DetailFailureKind: WatchHostFailureKind.None,
        DetailFailureCode: null,
        DetailErrorMessage: null,
        DetailEndpoint: null,
        DetailCorrelationId: null);
}

internal sealed record WatchV2WorkspaceState(
    long HostGeneration,
    string? BaseUrl,
    WatchHostConnectionStatus ConnectionStatus,
    WatchHostFailureKind FailureKind,
    string? FailureCode,
    string? ErrorMessage,
    string? Endpoint,
    string? CorrelationId,
    WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail> Overview,
    WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot> DemandSeries,
    WatchV2ViewState<ReadabilityAuditListSnapshot, ReadabilityAuditDetailSnapshot> ReadabilityAudit,
    WatchV2ViewState<ErrorSearchListSnapshot, ErrorSearchDetailSnapshot> ErrorSearch,
    WatchV2ViewState<CurrentIngestAttentionSnapshot, WatchNoDetail> CurrentAttention)
{
    public static WatchV2WorkspaceState Empty { get; } = Reset(
        hostGeneration: 0,
        baseUrl: null,
        WatchHostConnectionStatus.NotConfigured);

    public static WatchV2WorkspaceState Reset(
        long hostGeneration,
        string? baseUrl,
        WatchHostConnectionStatus connectionStatus) => new(
        hostGeneration,
        baseUrl,
        connectionStatus,
        WatchHostFailureKind.None,
        FailureCode: null,
        ErrorMessage: null,
        Endpoint: null,
        CorrelationId: null,
        WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail>.Empty(hostGeneration),
        WatchV2ViewState<DemandSeriesListSnapshot, DemandSeriesDetailSnapshot>.Empty(hostGeneration),
        WatchV2ViewState<ReadabilityAuditListSnapshot, ReadabilityAuditDetailSnapshot>.Empty(hostGeneration),
        WatchV2ViewState<ErrorSearchListSnapshot, ErrorSearchDetailSnapshot>.Empty(hostGeneration),
        WatchV2ViewState<CurrentIngestAttentionSnapshot, WatchNoDetail>.Empty(hostGeneration));
}

/// <summary>
/// Owns the one replacement-contract Host connection and every Host-derived
/// business view. Applying settings publishes an empty new generation before
/// contract I/O starts, so no prior Host snapshot can survive a failed switch.
/// </summary>
internal sealed class WatchV2WorkspaceSession : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<WatchHostSettings, IWatchV2ApiClient> _clientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<RequestSlot, CancellationTokenSource> _requestCancellations = [];
    private readonly Dictionary<RequestSlot, CancellationTokenSource> _detailCancellations = [];
    private CancellationTokenSource? _hostCancellation;
    private IWatchV2ApiClient? _client;
    private WatchV2WorkspaceState _state = WatchV2WorkspaceState.Empty;
    private long _hostGeneration;
    private bool _disposed;

    public WatchV2WorkspaceSession(
        Func<WatchHostSettings, IWatchV2ApiClient>? clientFactory = null,
        TimeProvider? timeProvider = null)
    {
        _clientFactory = clientFactory
            ?? (settings => MesIngestV2ApiClient.CreateForHost(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public WatchV2WorkspaceState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public async Task ApplyAsync(
        WatchHostSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        CancellationTokenSource hostCancellation;
        CancellationTokenSource? previousCancellation;
        CancellationTokenSource[] previousRequestCancellations;
        CancellationTokenSource[] previousDetailCancellations;
        IWatchV2ApiClient client;
        IWatchV2ApiClient? previousClient;
        CancellationTokenSource linked;
        long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = ++_hostGeneration;
            hostCancellation = new CancellationTokenSource();
            previousCancellation = _hostCancellation;
            previousRequestCancellations = [.. _requestCancellations.Values];
            _requestCancellations.Clear();
            previousDetailCancellations = [.. _detailCancellations.Values];
            _detailCancellations.Clear();
            previousClient = _client;
            _hostCancellation = hostCancellation;
            _client = null;
            _state = WatchV2WorkspaceState.Reset(
                generation,
                settings.BaseUrl,
                WatchHostConnectionStatus.Connecting);
        }

        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        CancelAndDispose(previousRequestCancellations);
        CancelAndDispose(previousDetailCancellations);
        previousClient?.Dispose();

        try
        {
            client = _clientFactory(settings)
                ?? throw new InvalidOperationException("The V2 client factory returned null.");
        }
        catch (Exception exception)
        {
            CommitConnectionIfCurrent(
                generation,
                state => state with
                {
                    ConnectionStatus = WatchHostConnectionStatus.Failed,
                    FailureKind = WatchHostFailureKind.Unknown,
                    ErrorMessage = exception.Message,
                });
            return;
        }

        lock (_gate)
        {
            if (_disposed || generation != _hostGeneration)
            {
                client.Dispose();
                return;
            }

            _client = client;
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                hostCancellation.Token,
                cancellationToken);
        }

        using var linkedLease = linked;
        try
        {
            await client.VerifyContractAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            CommitConnectionIfCurrent(
                generation,
                state => state with { ConnectionStatus = WatchHostConnectionStatus.Connected });
        }
        catch (OperationCanceledException) when (
            hostCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            // A replacement owns the newer generation and its connection state.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EndCallerCanceledConnection(generation, client, hostCancellation);
            throw;
        }
        catch (WatchHostQueryException exception)
        {
            CommitConnectionIfCurrent(
                generation,
                state => state with
                {
                    ConnectionStatus = WatchHostConnectionStatus.Failed,
                    FailureKind = exception.Kind,
                    FailureCode = exception.ErrorCode,
                    ErrorMessage = exception.Message,
                    Endpoint = exception.Endpoint,
                    CorrelationId = exception.CorrelationId,
                });
        }
        catch (Exception exception)
        {
            CommitConnectionIfCurrent(
                generation,
                state => state with
                {
                    ConnectionStatus = WatchHostConnectionStatus.Failed,
                    FailureKind = WatchHostFailureKind.Unknown,
                    ErrorMessage = exception.Message,
                });
        }
    }

    public async Task RefreshOverviewAsync(
        WatchOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        await RefreshViewAsync(
            RequestSlot.Overview,
            WatchV2QueryKeys.Overview(normalized),
            normalized,
            state => state.Overview,
            (state, view) => state with { Overview = view },
            static (client, value, token) => client.FetchOverviewAsync(value, token),
            static snapshot => snapshot.Snapshot.ContractVersion,
            "/api/v2/watch-overview",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshDemandSeriesAsync(
        DemandSeriesBrowseQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        await RefreshViewAsync(
            RequestSlot.DemandSeries,
            WatchV2QueryKeys.DemandSeries(normalized),
            normalized,
            state => state.DemandSeries,
            (state, view) => state with { DemandSeries = view },
            static (client, value, token) => client.FetchDemandSeriesAsync(value, token),
            static snapshot => snapshot.Snapshot.ContractVersion,
            "/api/v2/demand-series",
            static (snapshot, selectedId) => snapshot.Items.Any(item =>
                string.Equals(item.SeriesId, selectedId, StringComparison.Ordinal)),
            retainDetailWithoutFetch: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes the requested page against the newest available projection.
    /// Pages after one require a snapshot reference at the HTTP boundary, so
    /// this method first acquires the newest page-one snapshot and then opens
    /// the requested page inside that same frozen snapshot. Only the final page
    /// is committed to the workspace.
    /// </summary>
    public async Task RefreshLatestDemandSeriesPageAsync(
        DemandSeriesBrowseQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageNumber < 1)
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                "PageNumber must be one or greater.");
        }

        if (query.Cursor is not null)
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                "Latest page refresh does not accept a frozen-snapshot cursor.");
        }

        var firstPage = (query with
        {
            PageNumber = 1,
            SnapshotReference = null,
            Cursor = null,
        }).NormalizeAndValidate();
        var request = new DemandSeriesLatestPageRequest(
            firstPage.Filter,
            firstPage.PageSize,
            query.PageNumber,
            firstPage.Order);
        await RefreshViewAsync(
            RequestSlot.DemandSeries,
            WatchV2QueryKeys.LatestDemandSeries(request),
            request,
            state => state.DemandSeries,
            (state, view) => state with { DemandSeries = view },
            FetchLatestDemandSeriesPageAsync,
            static snapshot => snapshot.Snapshot.ContractVersion,
            "/api/v2/demand-series",
            static (snapshot, selectedId) => snapshot.Items.Any(item =>
                string.Equals(item.SeriesId, selectedId, StringComparison.Ordinal)),
            retainDetailWithoutFetch: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshReadabilityAuditAsync(
        ReadabilityAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        await RefreshViewAsync(
            RequestSlot.ReadabilityAudit,
            WatchV2QueryKeys.ReadabilityAudit(normalized),
            normalized,
            state => state.ReadabilityAudit,
            (state, view) => state with { ReadabilityAudit = view },
            static (client, value, token) => client.FetchReadabilityAuditAsync(value, token),
            static snapshot => snapshot.Snapshot.ContractVersion,
            "/api/v2/readability-audit",
            static (snapshot, selectedId) => snapshot.Items.Any(item =>
                string.Equals(item.DemandId, selectedId, StringComparison.Ordinal)),
            static (client, selectedId, snapshot, token) =>
                client.FetchReadabilityAuditDetailAsync(
                    selectedId,
                    snapshot.SnapshotReference,
                    token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes the requested audit page against the newest available
    /// projection. Pages after one require a snapshot reference at the HTTP
    /// boundary, so this method first acquires the newest page-one snapshot
    /// and then opens the requested page inside that same frozen snapshot.
    /// Only the final page is committed to the workspace.
    /// </summary>
    public async Task RefreshLatestReadabilityAuditPageAsync(
        ReadabilityAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageNumber < 1)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "PageNumber must be one or greater.");
        }

        if (query.Cursor is not null)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "Latest page refresh does not accept a frozen-snapshot cursor.");
        }

        var firstPage = (query with
        {
            PageNumber = 1,
            SnapshotReference = null,
            Cursor = null,
        }).NormalizeAndValidate();
        var request = new ReadabilityAuditLatestPageRequest(
            firstPage.Filter,
            firstPage.PageSize,
            query.PageNumber,
            firstPage.Order);
        await RefreshViewAsync(
            RequestSlot.ReadabilityAudit,
            WatchV2QueryKeys.LatestReadabilityAudit(request),
            request,
            state => state.ReadabilityAudit,
            (state, view) => state with { ReadabilityAudit = view },
            FetchLatestReadabilityAuditPageAsync,
            static snapshot => snapshot.Snapshot.ContractVersion,
            "/api/v2/readability-audit",
            static (snapshot, selectedId) => snapshot.Items.Any(item =>
                string.Equals(item.DemandId, selectedId, StringComparison.Ordinal)),
            static (client, selectedId, snapshot, token) =>
                client.FetchReadabilityAuditDetailAsync(
                    selectedId,
                    snapshot.SnapshotReference,
                    token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshErrorSearchAsync(
        ErrorSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        await RefreshViewAsync(
            RequestSlot.ErrorSearch,
            WatchV2QueryKeys.ErrorSearch(normalized),
            normalized,
            state => state.ErrorSearch,
            (state, view) => state with { ErrorSearch = view },
            static (client, value, token) => client.FetchErrorSearchAsync(value, token),
            static snapshot => snapshot.Snapshot.ContractVersion,
            "/api/v2/error-search",
            static (snapshot, selectedId) => snapshot.Items.Any(item =>
                string.Equals(item.SeriesId, selectedId, StringComparison.Ordinal)),
            static (client, selectedId, snapshot, token) =>
                client.FetchErrorSearchDetailAsync(
                    selectedId,
                    snapshot.SnapshotReference,
                    token),
            cancellationToken,
            validateDetail: RequireMatchingErrorSearchDetail).ConfigureAwait(false);
    }

    public async Task RefreshCurrentAttentionAsync(
        CurrentIngestAttentionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();
        await RefreshViewAsync(
            RequestSlot.CurrentAttention,
            WatchV2QueryKeys.CurrentAttention(normalized),
            normalized,
            state => state.CurrentAttention,
            (state, view) => state with { CurrentAttention = view },
            static (client, value, token) => client.FetchCurrentAttentionAsync(value, token),
            static snapshot => snapshot.Snapshot.ContractVersion,
            "/api/v2/current-ingest-attention",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public void SetDemandSeriesSelection(string? seriesId) =>
        SetDetailSelection(
            RequestSlot.DemandSeries,
            seriesId,
            state => state.DemandSeries,
            (state, view) => state with { DemandSeries = view },
            static (snapshot, selectedId) => snapshot.Items.Any(item =>
                string.Equals(item.SeriesId, selectedId, StringComparison.Ordinal)));

    public void SetDemandSeriesFocus(string? demandId)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = _state.DemandSeries;
            var normalized = string.IsNullOrWhiteSpace(demandId) ? null : demandId;
            if (string.Equals(current.DetailFocusId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _detailCancellations.Remove(RequestSlot.DemandSeries, out cancellation);
            _state = _state with
            {
                DemandSeries = current with
                {
                    SelectionGeneration = current.SelectionGeneration + 1,
                    DetailFocusId = normalized,
                    IsDetailLoading = false,
                },
            };
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    public Task LoadSelectedDemandSeriesDetailAsync(
        CancellationToken cancellationToken = default)
    {
        string? seriesId;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            seriesId = _state.DemandSeries.SelectedId;
        }

        return LoadDemandSeriesDetailCoreAsync(seriesId, cancellationToken);
    }

    private async Task LoadDemandSeriesDetailCoreAsync(
        string? seriesId,
        CancellationToken cancellationToken)
    {
        await SelectDetailAsync(
            RequestSlot.DemandSeries,
            seriesId,
            state => state.DemandSeries,
            (state, view) => state with { DemandSeries = view },
            static (snapshot, selectedId) => snapshot.Items.Any(item =>
                string.Equals(item.SeriesId, selectedId, StringComparison.Ordinal)),
            static snapshot => snapshot.SnapshotReference,
            static (client, selectedId, snapshotReference, token) =>
                client.FetchDemandSeriesDetailAsync(selectedId, snapshotReference, token),
            static detail => detail.Snapshot.ContractVersion,
            "/api/v2/demand-series/{seriesId}",
            cancellationToken,
            retainDetailWhileLoading: true).ConfigureAwait(false);

        lock (_gate)
        {
            if (_disposed
                || _state.DemandSeries is not { Detail: { } detail } current
                || !string.Equals(current.SelectedId, seriesId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(current.DetailFocusId)
                || detail.Series.Demands.Any(demand => string.Equals(
                    demand.DemandId,
                    current.DetailFocusId,
                    StringComparison.Ordinal)))
            {
                return;
            }

            _state = _state with
            {
                DemandSeries = current with
                {
                    DetailFocusId = detail.Series.CurrentDemand.DemandId,
                },
            };
        }
    }

    public void CancelDemandSeriesDetail()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _detailCancellations.Remove(RequestSlot.DemandSeries, out cancellation);
            var current = _state.DemandSeries;
            _state = _state with
            {
                DemandSeries = current with
                {
                    SelectionGeneration = current.SelectionGeneration + 1,
                    Detail = null,
                    DetailFocusId = null,
                    IsDetailLoading = false,
                    DetailLastFailureAt = null,
                    DetailFailureKind = WatchHostFailureKind.None,
                    DetailFailureCode = null,
                    DetailErrorMessage = null,
                    DetailEndpoint = null,
                    DetailCorrelationId = null,
                },
            };
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void SetDetailSelection<TSnapshot, TDetail>(
        RequestSlot slot,
        string? selectedId,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>> getView,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>, WatchV2WorkspaceState> setView,
        Func<TSnapshot, string, bool> containsSelected)
    {
        CancellationTokenSource? previousDetailCancellation;
        lock (_gate)
        {
            EnsureConnected();
            var current = getView(_state);
            var normalizedSelectedId = string.IsNullOrWhiteSpace(selectedId)
                || current.Snapshot is null
                || !containsSelected(current.Snapshot, selectedId)
                    ? null
                    : selectedId;
            if (string.Equals(
                    current.SelectedId,
                    normalizedSelectedId,
                    StringComparison.Ordinal))
            {
                return;
            }

            _detailCancellations.Remove(slot, out previousDetailCancellation);
            _state = setView(
                _state,
                current with
                {
                    SelectionGeneration = current.SelectionGeneration + 1,
                    SelectedId = normalizedSelectedId,
                    DetailFocusId = null,
                    Detail = default,
                    SelectionNotice = normalizedSelectedId is null && selectedId is not null
                        ? WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot
                        : null,
                    IsDetailLoading = false,
                    DetailLastFailureAt = null,
                    DetailFailureKind = WatchHostFailureKind.None,
                    DetailFailureCode = null,
                    DetailErrorMessage = null,
                    DetailEndpoint = null,
                    DetailCorrelationId = null,
                });
        }

        previousDetailCancellation?.Cancel();
        previousDetailCancellation?.Dispose();
    }

    public Task SelectReadabilityDemandAsync(
        string? demandId,
        CancellationToken cancellationToken = default) =>
        SelectDetailAsync(
            RequestSlot.ReadabilityAudit,
            demandId,
            state => state.ReadabilityAudit,
            (state, view) => state with { ReadabilityAudit = view },
            static (snapshot, selectedId) => snapshot.Items.Any(item =>
                string.Equals(item.DemandId, selectedId, StringComparison.Ordinal)),
            static snapshot => snapshot.SnapshotReference,
            static (client, selectedId, snapshotReference, token) =>
                client.FetchReadabilityAuditDetailAsync(selectedId, snapshotReference, token),
            static detail => detail.Snapshot.ContractVersion,
            "/api/v2/readability-audit/{demandId}",
            cancellationToken);

    public Task SelectErrorSeriesAsync(
        string? seriesId,
        CancellationToken cancellationToken = default) =>
        SelectDetailAsync(
            RequestSlot.ErrorSearch,
            seriesId,
            state => state.ErrorSearch,
            (state, view) => state with { ErrorSearch = view },
            static (snapshot, selectedId) => snapshot.Items.Any(item =>
                string.Equals(item.SeriesId, selectedId, StringComparison.Ordinal)),
            static snapshot => snapshot.SnapshotReference,
            static (client, selectedId, snapshotReference, token) =>
                client.FetchErrorSearchDetailAsync(selectedId, snapshotReference, token),
            static detail => detail.Snapshot.ContractVersion,
            "/api/v2/error-search/{seriesId}",
            cancellationToken,
            isolateDetailFailure: true,
            validateDetail: RequireMatchingErrorSearchDetail);

    public async Task<ErrorSearchRawEvidenceSnapshot> ReadErrorRawEvidenceAsync(
        string seriesId,
        string evidenceId,
        ErrorSearchRawEvidenceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.NormalizeAndValidate();

        IWatchV2ApiClient client;
        CancellationToken hostToken;
        long hostGeneration;
        long requestGeneration;
        string snapshotReference;
        lock (_gate)
        {
            EnsureConnected();
            client = _client!;
            hostToken = _hostCancellation!.Token;
            hostGeneration = _hostGeneration;
            requestGeneration = _state.ErrorSearch.RequestGeneration;
            snapshotReference = _state.ErrorSearch.Snapshot?.SnapshotReference
                ?? throw new InvalidOperationException("Error Search has no committed snapshot.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            hostToken,
            cancellationToken);
        var result = await client.FetchErrorRawEvidenceAsync(
                seriesId,
                evidenceId,
                snapshotReference,
                normalized,
                linked.Token)
            .ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        RequireSnapshotContract(result.Snapshot.ContractVersion, "/api/v2/error-search/raw-evidence");

        lock (_gate)
        {
            if (_disposed
                || hostGeneration != _hostGeneration
                || requestGeneration != _state.ErrorSearch.RequestGeneration
                || !string.Equals(
                    snapshotReference,
                    _state.ErrorSearch.Snapshot?.SnapshotReference,
                    StringComparison.Ordinal))
            {
                throw new OperationCanceledException("The Error Search snapshot was replaced.");
            }
        }

        return result;
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        CancellationTokenSource[] requestCancellations;
        CancellationTokenSource[] detailCancellations;
        IWatchV2ApiClient? client;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hostGeneration++;
            cancellation = _hostCancellation;
            requestCancellations = [.. _requestCancellations.Values];
            _requestCancellations.Clear();
            detailCancellations = [.. _detailCancellations.Values];
            _detailCancellations.Clear();
            client = _client;
            _hostCancellation = null;
            _client = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        CancelAndDispose(requestCancellations);
        CancelAndDispose(detailCancellations);
        client?.Dispose();
    }

    private void EndCallerCanceledConnection(
        long generation,
        IWatchV2ApiClient client,
        CancellationTokenSource hostCancellation)
    {
        var ownsConnection = false;
        lock (_gate)
        {
            if (!_disposed
                && generation == _hostGeneration
                && ReferenceEquals(_client, client)
                && ReferenceEquals(_hostCancellation, hostCancellation))
            {
                _client = null;
                _hostCancellation = null;
                _state = _state with
                {
                    ConnectionStatus = WatchHostConnectionStatus.Failed,
                    FailureKind = WatchHostFailureKind.Canceled,
                    FailureCode = "REQUEST_CANCELED",
                    ErrorMessage = "Host contract verification was canceled.",
                    Endpoint = "/api/v2/contract",
                };
                ownsConnection = true;
            }
        }

        if (!ownsConnection)
        {
            return;
        }

        hostCancellation.Cancel();
        hostCancellation.Dispose();
        client.Dispose();
    }

    private static async Task<DemandSeriesListSnapshot> FetchLatestDemandSeriesPageAsync(
        IWatchV2ApiClient client,
        DemandSeriesLatestPageRequest request,
        CancellationToken cancellationToken)
    {
        var firstPage = await client.FetchDemandSeriesAsync(
                new DemandSeriesBrowseQuery(
                    request.Filter,
                    request.PageSize,
                    PageNumber: 1,
                    Order: request.Order),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RequireSnapshotContract(firstPage.Snapshot.ContractVersion, "/api/v2/demand-series");

        var targetPage = firstPage.TotalPages <= 1
            ? 1
            : Math.Min(request.PageNumber, firstPage.TotalPages);
        if (targetPage == 1)
        {
            return firstPage;
        }

        return await client.FetchDemandSeriesAsync(
                new DemandSeriesBrowseQuery(
                    request.Filter,
                    request.PageSize,
                    targetPage,
                    firstPage.SnapshotReference,
                    Order: request.Order),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ReadabilityAuditListSnapshot> FetchLatestReadabilityAuditPageAsync(
        IWatchV2ApiClient client,
        ReadabilityAuditLatestPageRequest request,
        CancellationToken cancellationToken)
    {
        var firstPage = await client.FetchReadabilityAuditAsync(
                new ReadabilityAuditQuery(
                    request.Filter,
                    request.PageSize,
                    PageNumber: 1,
                    Order: request.Order),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RequireSnapshotContract(
            firstPage.Snapshot.ContractVersion,
            "/api/v2/readability-audit");

        var targetPage = firstPage.TotalPages <= 1
            ? 1
            : Math.Min(request.PageNumber, firstPage.TotalPages);
        if (targetPage == 1)
        {
            return firstPage;
        }

        return await client.FetchReadabilityAuditAsync(
                new ReadabilityAuditQuery(
                    request.Filter,
                    request.PageSize,
                    targetPage,
                    firstPage.SnapshotReference,
                    Order: request.Order),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void CommitConnectionIfCurrent(
        long generation,
        Func<WatchV2WorkspaceState, WatchV2WorkspaceState> update)
    {
        lock (_gate)
        {
            if (_disposed || generation != _hostGeneration)
            {
                return;
            }

            _state = update(_state);
        }
    }

    private async Task RefreshViewAsync<TQuery, TSnapshot, TDetail>(
        RequestSlot slot,
        string queryKey,
        TQuery query,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>> getView,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>, WatchV2WorkspaceState> setView,
        Func<IWatchV2ApiClient, TQuery, CancellationToken, Task<TSnapshot>> fetch,
        Func<TSnapshot, string> getContractVersion,
        string endpoint,
        Func<TSnapshot, string, bool>? containsSelected = null,
        Func<IWatchV2ApiClient, string, TSnapshot, CancellationToken, Task<TDetail>>? fetchDetail = null,
        CancellationToken cancellationToken = default,
        Action<TSnapshot, string, TDetail>? validateDetail = null,
        bool retainDetailWithoutFetch = false)
    {
        var lease = BeginRequest(slot, queryKey, getView, setView);
        lease.PreviousCancellation?.Cancel();
        lease.PreviousCancellation?.Dispose();
        lease.PreviousDetailCancellation?.Cancel();
        lease.PreviousDetailCancellation?.Dispose();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lease.HostToken,
            lease.RequestCancellation.Token,
            cancellationToken);
        try
        {
            var snapshot = await fetch(lease.Client, query, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            RequireSnapshotContract(getContractVersion(snapshot), endpoint);

            while (true)
            {
                var selection = CaptureSelectionIfCurrent(
                    lease.HostGeneration,
                    lease.RequestGeneration,
                    queryKey,
                    getView);
                if (!selection.IsCurrent)
                {
                    return;
                }

                var selectedId = selection.SelectedId;
                TDetail? detail = default;
                string? selectionNotice = null;
                if (selectedId is not null && containsSelected is not null)
                {
                    if (fetchDetail is null)
                    {
                        if (!containsSelected(snapshot, selectedId))
                        {
                            selectedId = null;
                            selectionNotice =
                                WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot;
                        }
                    }
                    else
                    {
                        try
                        {
                            detail = await fetchDetail(
                                    lease.Client,
                                    selectedId,
                                    snapshot,
                                    linked.Token)
                                .ConfigureAwait(false);
                            linked.Token.ThrowIfCancellationRequested();
                            validateDetail?.Invoke(snapshot, selectedId, detail);
                        }
                        catch when (!IsSelectionCurrent(
                            lease.HostGeneration,
                            lease.RequestGeneration,
                            queryKey,
                            selection.SelectionGeneration,
                            getView))
                        {
                            continue;
                        }
                        catch (WatchHostQueryException exception) when (
                            IsObjectNotInSnapshot(exception))
                        {
                            selectedId = null;
                            selectionNotice =
                                WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot;
                        }
                    }
                }

                if (CommitViewIfCurrent(
                    lease.HostGeneration,
                    lease.RequestGeneration,
                    queryKey,
                    getView,
                    setView,
                    current => current with
                    {
                        SelectionGeneration = selectedId is null
                            && selection.SelectedId is not null
                                ? current.SelectionGeneration + 1
                                : current.SelectionGeneration,
                        IsRefreshing = false,
                        CommittedQueryKey = queryKey,
                        Snapshot = snapshot,
                        LastSuccessfulAt = _timeProvider.GetUtcNow(),
                        LastFailureAt = null,
                        FailedQueryKey = null,
                        FailureKind = WatchHostFailureKind.None,
                        FailureCode = null,
                        ErrorMessage = null,
                        Endpoint = null,
                        CorrelationId = null,
                        SelectedId = selectedId,
                        Detail = selectedId is not null
                            && fetchDetail is null
                            && retainDetailWithoutFetch
                                ? current.Detail
                                : detail,
                        SelectionNotice = selectionNotice,
                        IsDetailLoading = false,
                        DetailLastFailureAt = null,
                        DetailFailureKind = WatchHostFailureKind.None,
                        DetailFailureCode = null,
                        DetailErrorMessage = null,
                        DetailEndpoint = null,
                        DetailCorrelationId = null,
                    },
                    selection.SelectionGeneration))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            CommitViewIfCurrent(
                lease.HostGeneration,
                lease.RequestGeneration,
                queryKey,
                getView,
                setView,
                current => current with { IsRefreshing = false });
        }
        catch (WatchHostQueryException exception)
        {
            CommitViewFailure(
                lease,
                queryKey,
                exception,
                getView,
                setView);
        }
        catch (Exception exception)
        {
            CommitViewFailure(
                lease,
                queryKey,
                new WatchHostQueryException(
                    WatchHostFailureKind.Unknown,
                    endpoint,
                    Guid.NewGuid().ToString("N"),
                    exception.Message,
                    exception),
                getView,
                setView);
        }
        finally
        {
            ReleaseRequest(slot, lease.RequestCancellation);
        }
    }

    private Task SelectDetailAsync<TSnapshot, TDetail>(
        RequestSlot slot,
        string? selectedId,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>> getView,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>, WatchV2WorkspaceState> setView,
        Func<TSnapshot, string, bool> containsSelected,
        Func<TSnapshot, string> getSnapshotReference,
        Func<IWatchV2ApiClient, string, string, CancellationToken, Task<TDetail>> fetchDetail,
        Func<TDetail, string> getContractVersion,
        string endpoint,
        CancellationToken cancellationToken,
        bool isolateDetailFailure = false,
        Action<TSnapshot, string, TDetail>? validateDetail = null,
        bool retainDetailWhileLoading = false)
    {
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            CancellationTokenSource? previousDetailCancellation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var current = getView(_state);
                _detailCancellations.Remove(slot, out previousDetailCancellation);
                _state = setView(
                    _state,
                    current with
                    {
                        SelectionGeneration = current.SelectionGeneration + 1,
                        SelectedId = null,
                        Detail = default,
                        SelectionNotice = null,
                        IsDetailLoading = false,
                        DetailLastFailureAt = null,
                        DetailFailureKind = WatchHostFailureKind.None,
                        DetailFailureCode = null,
                        DetailErrorMessage = null,
                        DetailEndpoint = null,
                        DetailCorrelationId = null,
                    });
            }

            previousDetailCancellation?.Cancel();
            previousDetailCancellation?.Dispose();

            return Task.CompletedTask;
        }

        return SelectDetailCoreAsync(
            slot,
            selectedId,
            getView,
            setView,
            containsSelected,
            getSnapshotReference,
            fetchDetail,
            getContractVersion,
            endpoint,
            cancellationToken,
            isolateDetailFailure,
            validateDetail,
            retainDetailWhileLoading);
    }

    private async Task SelectDetailCoreAsync<TSnapshot, TDetail>(
        RequestSlot slot,
        string selectedId,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>> getView,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>, WatchV2WorkspaceState> setView,
        Func<TSnapshot, string, bool> containsSelected,
        Func<TSnapshot, string> getSnapshotReference,
        Func<IWatchV2ApiClient, string, string, CancellationToken, Task<TDetail>> fetchDetail,
        Func<TDetail, string> getContractVersion,
        string endpoint,
        CancellationToken cancellationToken,
        bool isolateDetailFailure,
        Action<TSnapshot, string, TDetail>? validateDetail,
        bool retainDetailWhileLoading)
    {
        long selectionGeneration;
        CancellationTokenSource? previousDetailCancellation;
        DetailRequestLease? detailLease = null;
        lock (_gate)
        {
            EnsureConnected();
            var current = getView(_state);
            selectionGeneration = current.SelectionGeneration + 1;
            _detailCancellations.Remove(slot, out previousDetailCancellation);
            if (current.Snapshot is null || !containsSelected(current.Snapshot, selectedId))
            {
                _state = setView(
                    _state,
                    current with
                    {
                        SelectionGeneration = selectionGeneration,
                        SelectedId = null,
                        Detail = default,
                        SelectionNotice = WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
                        IsDetailLoading = false,
                        DetailLastFailureAt = null,
                        DetailFailureKind = WatchHostFailureKind.None,
                        DetailFailureCode = null,
                        DetailErrorMessage = null,
                        DetailEndpoint = null,
                        DetailCorrelationId = null,
                    });
            }
            else
            {
                var detailCancellation = new CancellationTokenSource();
                _detailCancellations.Add(slot, detailCancellation);
                detailLease = new DetailRequestLease(
                    _client!,
                    _hostCancellation!.Token,
                    detailCancellation,
                    _hostGeneration,
                    current.RequestGeneration,
                    getSnapshotReference(current.Snapshot),
                    current.DetailFocusId);
                _state = setView(
                    _state,
                    current with
                    {
                        SelectionGeneration = selectionGeneration,
                        SelectedId = selectedId,
                        Detail = retainDetailWhileLoading ? current.Detail : default,
                        SelectionNotice = null,
                        IsDetailLoading = true,
                        DetailLastFailureAt = null,
                        DetailFailureKind = WatchHostFailureKind.None,
                        DetailFailureCode = null,
                        DetailErrorMessage = null,
                        DetailEndpoint = null,
                        DetailCorrelationId = null,
                    });
            }
        }

        previousDetailCancellation?.Cancel();
        previousDetailCancellation?.Dispose();
        if (detailLease is null)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            detailLease.HostToken,
            detailLease.Cancellation.Token,
            cancellationToken);
        try
        {
            var detail = await fetchDetail(
                    detailLease.Client,
                    selectedId,
                    detailLease.SnapshotReference,
                    linked.Token)
                .ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            RequireSnapshotContract(getContractVersion(detail), endpoint);
            lock (_gate)
            {
                var current = getView(_state);
                if (_disposed
                    || detailLease.HostGeneration != _hostGeneration
                    || detailLease.RequestGeneration != current.RequestGeneration
                    || selectionGeneration != current.SelectionGeneration
                    || !string.Equals(current.SelectedId, selectedId, StringComparison.Ordinal)
                    || !string.Equals(
                        current.DetailFocusId,
                        detailLease.FocusId,
                        StringComparison.Ordinal)
                    || current.Snapshot is null
                    || !string.Equals(
                        getSnapshotReference(current.Snapshot),
                        detailLease.SnapshotReference,
                        StringComparison.Ordinal))
                {
                    return;
                }

                validateDetail?.Invoke(current.Snapshot, selectedId, detail);
                _state = setView(
                    _state,
                    current with
                    {
                        Detail = detail,
                        IsDetailLoading = false,
                        DetailLastFailureAt = null,
                        DetailFailureKind = WatchHostFailureKind.None,
                        DetailFailureCode = null,
                        DetailErrorMessage = null,
                        DetailEndpoint = null,
                        DetailCorrelationId = null,
                    });
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            CommitDetailFailureIfCurrent(
                detailLease,
                selectionGeneration,
                selectedId,
                getSnapshotReference,
                getView,
                setView,
                new WatchHostQueryException(
                    WatchHostFailureKind.Canceled,
                    endpoint,
                    Guid.NewGuid().ToString("N"),
                    "The detail request was canceled.",
                    errorCode: "REQUEST_CANCELED"),
                isolateDetailFailure);
        }
        catch (WatchHostQueryException exception)
        {
            CommitDetailFailureIfCurrent(
                detailLease,
                selectionGeneration,
                selectedId,
                getSnapshotReference,
                getView,
                setView,
                exception,
                isolateDetailFailure);
        }
        catch (Exception exception)
        {
            CommitDetailFailureIfCurrent(
                detailLease,
                selectionGeneration,
                selectedId,
                getSnapshotReference,
                getView,
                setView,
                new WatchHostQueryException(
                    WatchHostFailureKind.Unknown,
                    endpoint,
                    Guid.NewGuid().ToString("N"),
                    exception.Message,
                    exception),
                isolateDetailFailure);
        }
        finally
        {
            ReleaseDetail(slot, detailLease.Cancellation);
        }
    }

    private void CommitDetailFailureIfCurrent<TSnapshot, TDetail>(
        DetailRequestLease detailLease,
        long selectionGeneration,
        string selectedId,
        Func<TSnapshot, string> getSnapshotReference,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>> getView,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>, WatchV2WorkspaceState> setView,
        WatchHostQueryException exception,
        bool isolateDetailFailure)
    {
        lock (_gate)
        {
            var current = getView(_state);
            if (_disposed
                || detailLease.HostGeneration != _hostGeneration
                || detailLease.RequestGeneration != current.RequestGeneration
                || selectionGeneration != current.SelectionGeneration
                || !string.Equals(current.SelectedId, selectedId, StringComparison.Ordinal)
                || !string.Equals(
                    current.DetailFocusId,
                    detailLease.FocusId,
                    StringComparison.Ordinal)
                || current.Snapshot is null
                || !string.Equals(
                    getSnapshotReference(current.Snapshot),
                    detailLease.SnapshotReference,
                    StringComparison.Ordinal))
            {
                return;
            }

            var failedAt = _timeProvider.GetUtcNow();
            var canceledLegacyDetail = exception.Kind == WatchHostFailureKind.Canceled
                && !isolateDetailFailure;
            var updated = canceledLegacyDetail
                ? current with { IsDetailLoading = false }
                : current with
                {
                    IsDetailLoading = false,
                    DetailLastFailureAt = failedAt,
                    DetailFailureKind = exception.Kind,
                    DetailFailureCode = exception.ErrorCode,
                    DetailErrorMessage = exception.Message,
                    DetailEndpoint = exception.Endpoint,
                    DetailCorrelationId = exception.CorrelationId,
                };
            if (!isolateDetailFailure && !canceledLegacyDetail)
            {
                updated = updated with
                {
                    LastFailureAt = failedAt,
                    FailedQueryKey = current.CommittedQueryKey,
                    FailureKind = exception.Kind,
                    FailureCode = exception.ErrorCode,
                    ErrorMessage = exception.Message,
                    Endpoint = exception.Endpoint,
                    CorrelationId = exception.CorrelationId,
                };
            }

            _state = setView(_state, updated);
        }
    }

    private RequestLease<TSnapshot, TDetail> BeginRequest<TSnapshot, TDetail>(
        RequestSlot slot,
        string queryKey,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>> getView,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>, WatchV2WorkspaceState> setView)
    {
        lock (_gate)
        {
            EnsureConnected();
            var current = getView(_state);
            var requestGeneration = current.RequestGeneration + 1;
            var requestCancellation = new CancellationTokenSource();
            _requestCancellations.Remove(slot, out var previousCancellation);
            _requestCancellations.Add(slot, requestCancellation);
            _detailCancellations.Remove(slot, out var previousDetailCancellation);
            _state = setView(
                _state,
                current with
                {
                    RequestGeneration = requestGeneration,
                    IsRefreshing = true,
                    PendingQueryKey = queryKey,
                    IsDetailLoading = false,
                });
            return new RequestLease<TSnapshot, TDetail>(
                _client!,
                _hostCancellation!.Token,
                requestCancellation,
                previousCancellation,
                previousDetailCancellation,
                _hostGeneration,
                requestGeneration,
                current.SelectionGeneration);
        }
    }

    private void CommitViewFailure<TSnapshot, TDetail>(
        RequestLease<TSnapshot, TDetail> lease,
        string queryKey,
        WatchHostQueryException exception,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>> getView,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>, WatchV2WorkspaceState> setView) =>
        CommitViewIfCurrent(
            lease.HostGeneration,
            lease.RequestGeneration,
            queryKey,
            getView,
            setView,
            current => current with
            {
                IsRefreshing = false,
                LastFailureAt = _timeProvider.GetUtcNow(),
                FailedQueryKey = queryKey,
                FailureKind = exception.Kind,
                FailureCode = exception.ErrorCode,
                ErrorMessage = exception.Message,
                Endpoint = exception.Endpoint,
                CorrelationId = exception.CorrelationId,
            });

    private bool CommitViewIfCurrent<TSnapshot, TDetail>(
        long hostGeneration,
        long requestGeneration,
        string queryKey,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>> getView,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>, WatchV2WorkspaceState> setView,
        Func<WatchV2ViewState<TSnapshot, TDetail>, WatchV2ViewState<TSnapshot, TDetail>> update,
        long? selectionGeneration = null)
    {
        lock (_gate)
        {
            var current = getView(_state);
            if (_disposed
                || hostGeneration != _hostGeneration
                || hostGeneration != current.HostGeneration
                || requestGeneration != current.RequestGeneration
                || selectionGeneration is not null
                    && selectionGeneration.Value != current.SelectionGeneration
                || !string.Equals(current.PendingQueryKey, queryKey, StringComparison.Ordinal))
            {
                return false;
            }

            _state = setView(_state, update(current));
            return true;
        }
    }

    private SelectionLease CaptureSelectionIfCurrent<TSnapshot, TDetail>(
        long hostGeneration,
        long requestGeneration,
        string queryKey,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>> getView)
    {
        lock (_gate)
        {
            var current = getView(_state);
            var isCurrent = !_disposed
                && hostGeneration == _hostGeneration
                && hostGeneration == current.HostGeneration
                && requestGeneration == current.RequestGeneration
                && string.Equals(current.PendingQueryKey, queryKey, StringComparison.Ordinal);
            return new SelectionLease(
                isCurrent,
                current.SelectionGeneration,
                current.SelectedId);
        }
    }

    private bool IsSelectionCurrent<TSnapshot, TDetail>(
        long hostGeneration,
        long requestGeneration,
        string queryKey,
        long selectionGeneration,
        Func<WatchV2WorkspaceState, WatchV2ViewState<TSnapshot, TDetail>> getView) =>
        CaptureSelectionIfCurrent(
            hostGeneration,
            requestGeneration,
            queryKey,
            getView) is { IsCurrent: true } selection
        && selection.SelectionGeneration == selectionGeneration;

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state.ConnectionStatus != WatchHostConnectionStatus.Connected
            || _client is null
            || _hostCancellation is null)
        {
            throw new InvalidOperationException("The V2 Host session is not connected.");
        }
    }

    private void ReleaseRequest(RequestSlot slot, CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (_requestCancellations.TryGetValue(slot, out var current)
                && ReferenceEquals(current, cancellation))
            {
                _requestCancellations.Remove(slot);
            }
        }

        cancellation.Dispose();
    }

    private void ReleaseDetail(RequestSlot slot, CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (_detailCancellations.TryGetValue(slot, out var current)
                && ReferenceEquals(current, cancellation))
            {
                _detailCancellations.Remove(slot);
            }
        }

        cancellation.Dispose();
    }

    private static void RequireSnapshotContract(string actual, string endpoint)
    {
        if (string.Equals(actual, NewMesIngestContract.Version, StringComparison.Ordinal))
        {
            return;
        }

        throw new WatchHostQueryException(
            WatchHostFailureKind.Contract,
            endpoint,
            Guid.NewGuid().ToString("N"),
            $"{NewMesIngestContractMismatchException.ErrorCode}: "
            + $"Snapshot contractVersion={actual}.",
            errorCode: NewMesIngestContractMismatchException.ErrorCode);
    }

    private static void RequireMatchingErrorSearchDetail(
        ErrorSearchListSnapshot snapshot,
        string selectedId,
        ErrorSearchDetailSnapshot detail)
    {
        if (WatchErrorSearchDetailConsistency.Matches(snapshot, selectedId, detail))
        {
            return;
        }

        throw new WatchHostQueryException(
            WatchHostFailureKind.Decode,
            "/api/v2/error-search/{seriesId}",
            Guid.NewGuid().ToString("N"),
            "The Host Error Search detail does not match the selected Series or frozen snapshot.");
    }

    private static bool IsObjectNotInSnapshot(WatchHostQueryException exception) =>
        exception.ErrorCode is DemandSeriesBrowseErrorCodes.ObjectNotInSnapshot
            or ReadabilityAuditErrorCodes.ObjectNotInSnapshot
            or ErrorSearchErrorCodes.ObjectNotInSnapshot;

    private static void CancelAndDispose(IEnumerable<CancellationTokenSource> cancellations)
    {
        foreach (var cancellation in cancellations)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Inspector close, Host replacement, and application shutdown
                // can converge on the same request. Cancellation is already
                // complete when its source was disposed by the first path.
            }

            cancellation.Dispose();
        }
    }

    private enum RequestSlot
    {
        Overview,
        DemandSeries,
        ReadabilityAudit,
        ErrorSearch,
        CurrentAttention,
    }

    private sealed record RequestLease<TSnapshot, TDetail>(
        IWatchV2ApiClient Client,
        CancellationToken HostToken,
        CancellationTokenSource RequestCancellation,
        CancellationTokenSource? PreviousCancellation,
        CancellationTokenSource? PreviousDetailCancellation,
        long HostGeneration,
        long RequestGeneration,
        long SelectionGeneration);

    private readonly record struct SelectionLease(
        bool IsCurrent,
        long SelectionGeneration,
        string? SelectedId);

    private sealed record DetailRequestLease(
        IWatchV2ApiClient Client,
        CancellationToken HostToken,
        CancellationTokenSource Cancellation,
        long HostGeneration,
        long RequestGeneration,
        string SnapshotReference,
        string? FocusId);

    internal sealed record DemandSeriesLatestPageRequest(
        DemandSeriesBrowseFilter Filter,
        int PageSize,
        int PageNumber,
        string Order);

    internal sealed record ReadabilityAuditLatestPageRequest(
        ReadabilityAuditFilter Filter,
        int PageSize,
        int PageNumber,
        string Order);
}

internal static class WatchV2SelectionNotices
{
    public const string NoLongerMatchesRefreshedSnapshot =
        "NO_LONGER_MATCHES_REFRESHED_SNAPSHOT";
}

internal static class WatchV2QueryKeys
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Overview(WatchOverviewQuery query) => string.Join(
        "&",
        (query.MesAreas ?? Array.Empty<string>())
            .Select(area => $"area={Uri.EscapeDataString(area)}"));

    public static string DemandSeries(DemandSeriesBrowseQuery query) =>
        Serialize("demand-series", query);

    public static string LatestDemandSeries(
        WatchV2WorkspaceSession.DemandSeriesLatestPageRequest request) =>
        Serialize("demand-series-latest", request);

    public static string ReadabilityAudit(ReadabilityAuditQuery query) =>
        Serialize("readability-audit", query);

    public static string LatestReadabilityAudit(
        WatchV2WorkspaceSession.ReadabilityAuditLatestPageRequest request) =>
        Serialize("readability-audit-latest", request);

    public static string ErrorSearch(ErrorSearchQuery query) =>
        Serialize("error-search", query);

    public static string CurrentAttention(CurrentIngestAttentionQuery query) =>
        Serialize("current-ingest-attention", query);

    private static string Serialize<T>(string view, T value) =>
        $"{view}:{JsonSerializer.Serialize(value, Options)}";
}
