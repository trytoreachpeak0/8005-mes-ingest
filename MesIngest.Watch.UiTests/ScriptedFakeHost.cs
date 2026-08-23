using System.Net;
using System.Net.Http;
using System.Globalization;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MesIngest.Watch.UiTests;

internal enum FakeHostOperation
{
    ContractV2,
    OverviewV2,
    DemandSeriesV2,
    DemandSeriesDetailV2,
    ReadabilityAuditV2,
    ReadabilityAuditDetailV2,
    ErrorSearchV2,
    ErrorSearchDetailV2,
    ErrorSearchRawEvidenceV2,
    CurrentAttentionV2,
}

internal enum FakeHostRequestState
{
    Started,
    Completed,
    CompletedAfterCancellation,
    Canceled,
    Failed,
}

internal readonly record struct FakeHostRequestMatch(
    string SessionId,
    FakeHostOperation Operation,
    FakeHostRequestState State);

internal sealed record FakeHostRequestEvent(
    long Sequence,
    FakeHostRequestMatch Request,
    string Endpoint)
{
    public string SessionId => Request.SessionId;
    public FakeHostOperation Operation => Request.Operation;
    public FakeHostRequestState State => Request.State;
}

internal sealed class FakeHostGate
{
    private readonly TaskCompletionSource _released = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _released.TrySetResult();

    internal Task WaitAsync(CancellationToken cancellationToken, bool completeAfterCancellation) =>
        completeAfterCancellation
            ? _released.Task
            : _released.Task.WaitAsync(cancellationToken);
}

internal readonly record struct FakeHostUnit;

internal sealed record FakeHostV2DetailRequest(
    string ObjectId,
    string SnapshotReference);

internal sealed record FakeHostV2RawEvidenceRequest(
    string SeriesId,
    string EvidenceId,
    string SnapshotReference,
    ErrorSearchRawEvidenceQuery Query);

internal sealed record FakeHostV2ContractSnapshot(
    string ContractVersion,
    int SchemaVersion,
    IReadOnlyList<string> CapabilityIds)
{
    public static FakeHostV2ContractSnapshot Exact { get; } = new(
        NewMesIngestContract.Version,
        NewMesIngestContract.SchemaVersion,
        NewMesIngestContract.Capabilities.Select(capability => capability.Id).ToArray());
}

internal enum FakeHostFailureShape
{
    None,
    Query,
    CursorExpired,
}

internal sealed record FakeHostReply<T>(
    T Value,
    FakeHostFailureShape FailureShape = FakeHostFailureShape.None,
    WatchHostFailureKind? FailureKind = null,
    string Endpoint = "fake-host",
    string FailureMessage = "fake Host failure",
    FakeHostGate? Gate = null,
    bool CompleteAfterCancellation = false,
    HttpStatusCode? StatusCode = null,
    object? ResponseBody = null,
    TimeSpan? Delay = null);

internal static class FakeHostReply
{
    public static FakeHostReply<FakeHostUnit> Success() =>
        Return(new FakeHostUnit());

    public static FakeHostReply<T> Return<T>(T value) => new(value);

    public static FakeHostReply<T> Fail<T>(
        WatchHostFailureKind kind,
        string endpoint,
        string message = "fake Host failure") =>
        new(
            default!,
            FakeHostFailureShape.Query,
            kind,
            endpoint,
            message);

    public static FakeHostReply<T> CursorExpired<T>(string endpoint) =>
        new(
            default!,
            FakeHostFailureShape.CursorExpired,
            Endpoint: endpoint,
            FailureMessage: "fake Host rejected an expired cursor");

    public static FakeHostReply<T> After<T>(
        FakeHostGate gate,
        T value,
        bool completeAfterCancellation = false) =>
        new(
            value,
            Gate: gate ?? throw new ArgumentNullException(nameof(gate)),
            CompleteAfterCancellation: completeAfterCancellation);

    public static FakeHostReply<T> AfterDelay<T>(
        TimeSpan delay,
        T value,
        bool completeAfterCancellation = false) =>
        new(
            value,
            CompleteAfterCancellation: completeAfterCancellation,
            Delay: delay >= TimeSpan.Zero
                ? delay
                : throw new ArgumentOutOfRangeException(nameof(delay)));

    public static FakeHostReply<T> HttpFailure<T>(
        HttpStatusCode statusCode,
        string code,
        string message) =>
        new(
            default!,
            FailureShape: FakeHostFailureShape.Query,
            FailureMessage: message,
            StatusCode: statusCode,
            ResponseBody: new { code, error = message });

    public static FakeHostScript<TRequest, TResponse> Sequence<TRequest, TResponse>(
        params FakeHostReply<TResponse>[] replies) =>
        FakeHostScript<TRequest, TResponse>.Sequence(replies);

    public static FakeHostScript<TRequest, TResponse> Select<TRequest, TResponse>(
        Func<TRequest, FakeHostReply<TResponse>> selector) =>
        FakeHostScript<TRequest, TResponse>.Select(selector);
}

internal sealed class FakeHostScript<TRequest, TResponse>
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<FakeHostReply<TResponse>>? _sequence;
    private readonly Func<TRequest, FakeHostReply<TResponse>>? _selector;
    private int _nextIndex;

    private FakeHostScript(IReadOnlyList<FakeHostReply<TResponse>> sequence)
    {
        _sequence = sequence.Count > 0
            ? sequence
            : throw new ArgumentException("A fake Host response sequence cannot be empty.", nameof(sequence));
    }

    private FakeHostScript(Func<TRequest, FakeHostReply<TResponse>> selector)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
    }

    public static FakeHostScript<TRequest, TResponse> Sequence(
        params FakeHostReply<TResponse>[] replies) =>
        new(replies ?? throw new ArgumentNullException(nameof(replies)));

    public static FakeHostScript<TRequest, TResponse> Select(
        Func<TRequest, FakeHostReply<TResponse>> selector) =>
        new(selector);

    public FakeHostReply<TResponse> Next(TRequest request)
    {
        if (_selector is not null)
        {
            return _selector(request);
        }

        lock (_sync)
        {
            var index = Math.Min(_nextIndex, _sequence!.Count - 1);
            _nextIndex++;
            return _sequence[index];
        }
    }

    public static implicit operator FakeHostScript<TRequest, TResponse>(
        FakeHostReply<TResponse> reply) =>
        Sequence(reply);
}

internal sealed record FakeHostV2Scenario
{
    public FakeHostV2Scenario(string sessionId, string credential)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A fake Host session id is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new ArgumentException("A fake Host credential is required.", nameof(credential));
        }

        SessionId = sessionId;
        Credential = credential;
    }

    public string SessionId { get; }

    public string Credential { get; }

    public FakeHostScript<FakeHostUnit, FakeHostV2ContractSnapshot> Contract { get; init; } =
        FakeHostReply.Return(FakeHostV2ContractSnapshot.Exact);

    public FakeHostScript<WatchOverviewQuery, WatchOverviewSnapshot>? Overview { get; init; }

    public FakeHostScript<DemandSeriesBrowseQuery, DemandSeriesListSnapshot>? DemandSeries { get; init; }

    public FakeHostScript<FakeHostV2DetailRequest, DemandSeriesDetailSnapshot>? DemandSeriesDetail { get; init; }

    public FakeHostScript<ReadabilityAuditQuery, ReadabilityAuditListSnapshot>? ReadabilityAudit { get; init; }

    public FakeHostScript<FakeHostV2DetailRequest, ReadabilityAuditDetailSnapshot>? ReadabilityAuditDetail { get; init; }

    public FakeHostScript<ErrorSearchQuery, ErrorSearchListSnapshot>? ErrorSearch { get; init; }

    public FakeHostScript<FakeHostV2DetailRequest, ErrorSearchDetailSnapshot>? ErrorSearchDetail { get; init; }

    public FakeHostScript<FakeHostV2RawEvidenceRequest, ErrorSearchRawEvidenceSnapshot>? ErrorSearchRawEvidence { get; init; }

    public FakeHostScript<CurrentIngestAttentionQuery, CurrentIngestAttentionSnapshot>? CurrentAttention { get; init; }
}

internal sealed class ScriptedFakeHost : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly List<FakeHostRequestEvent> _timeline = [];
    private readonly List<TimelineWaiter> _waiters = [];
    private readonly WebApplication? _application;
    private readonly FakeHostV2Scenario? _v2Scenario;
    private long _nextSequence;

    private ScriptedFakeHost(WebApplication application, FakeHostV2Scenario scenario)
    {
        _application = application;
        _v2Scenario = scenario;
    }

    public string BaseUrl { get; private set; } = string.Empty;

    public IReadOnlyList<FakeHostRequestEvent> Timeline
    {
        get
        {
            lock (_sync)
            {
                return _timeline.ToArray();
            }
        }
    }

    public static async Task<ScriptedFakeHost> StartV2Async(
        FakeHostV2Scenario scenario,
        CancellationToken cancellationToken = default,
        string? listenUrl = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(ResolveListenUrl(listenUrl));
        var application = builder.Build();
        var host = new ScriptedFakeHost(application, scenario);
        host.MapV2Endpoints();

        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            host.BaseUrl = addresses?.SingleOrDefault()
                ?? throw new InvalidOperationException("Fake Host did not publish a loopback address.");
            return host;
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static string ResolveListenUrl(string? listenUrl)
    {
        if (string.IsNullOrWhiteSpace(listenUrl))
        {
            return "http://127.0.0.1:0";
        }

        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
            || uri.Port <= 0
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "A fixed fake Host endpoint must be an HTTP 127.0.0.1 origin with an explicit port.",
                nameof(listenUrl));
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    public Task WaitForAsync(
        string sessionId,
        FakeHostOperation operation,
        FakeHostRequestState state,
        CancellationToken cancellationToken = default)
    {
        var request = new FakeHostRequestMatch(sessionId, operation, state);
        lock (_sync)
        {
            if (_timeline.Any(entry => entry.Request == request))
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new TimelineWaiter(request, completion));
            return cancellationToken.CanBeCanceled
                ? completion.Task.WaitAsync(cancellationToken)
                : completion.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is null)
        {
            return;
        }

        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }

    private void MapV2Endpoints()
    {
        var application = _application!;
        application.MapGet("/api/v2/contract", HandleV2ContractAsync);
        application.MapGet("/api/v2/watch-overview", HandleV2OverviewAsync);
        application.MapGet("/api/v2/demand-series", HandleV2DemandSeriesAsync);
        application.MapGet("/api/v2/demand-series/{seriesId}", HandleV2DemandSeriesDetailAsync);
        application.MapGet("/api/v2/readability-audit", HandleV2ReadabilityAuditAsync);
        application.MapGet(
            "/api/v2/readability-audit/{demandId}",
            HandleV2ReadabilityAuditDetailAsync);
        application.MapGet("/api/v2/error-search", HandleV2ErrorSearchAsync);
        application.MapGet("/api/v2/error-search/{seriesId}", HandleV2ErrorSearchDetailAsync);
        application.MapGet(
            "/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations",
            HandleV2ErrorSearchRawEvidenceAsync);
        application.MapGet(
            "/api/v2/current-ingest-attention",
            HandleV2CurrentAttentionAsync);
    }

    private Task HandleV2ContractAsync(HttpContext context) => ExecuteV2Async(
        context,
        FakeHostOperation.ContractV2,
        _v2Scenario!.Contract,
        () =>
        {
            EnsureAllowedQuery(context.Request.Query);
            return new FakeHostUnit();
        },
        ToV2ContractWire,
        "INVALID_CONTRACT_QUERY");

    private Task HandleV2OverviewAsync(HttpContext context) => ExecuteV2Async(
        context,
        FakeHostOperation.OverviewV2,
        _v2Scenario!.Overview,
        () => ParseOverviewQuery(context.Request.Query),
        static snapshot => snapshot,
        WatchOverviewErrorCodes.InvalidQuery);

    private Task HandleV2DemandSeriesAsync(HttpContext context) => ExecuteV2Async(
        context,
        FakeHostOperation.DemandSeriesV2,
        _v2Scenario!.DemandSeries,
        () => ParseDemandSeriesQuery(context.Request.Query),
        ToDemandSeriesListWire,
        DemandSeriesBrowseErrorCodes.InvalidQuery);

    private Task HandleV2DemandSeriesDetailAsync(HttpContext context) => ExecuteV2Async(
        context,
        FakeHostOperation.DemandSeriesDetailV2,
        _v2Scenario!.DemandSeriesDetail,
        () => ParseDetailRequest(context, "seriesId"),
        ToDemandSeriesDetailWire,
        DemandSeriesBrowseErrorCodes.InvalidQuery);

    private Task HandleV2ReadabilityAuditAsync(HttpContext context) => ExecuteV2Async(
        context,
        FakeHostOperation.ReadabilityAuditV2,
        _v2Scenario!.ReadabilityAudit,
        () => ParseReadabilityAuditQuery(context.Request.Query),
        ToReadabilityAuditListWire,
        ReadabilityAuditErrorCodes.InvalidQuery);

    private Task HandleV2ReadabilityAuditDetailAsync(HttpContext context) => ExecuteV2Async(
        context,
        FakeHostOperation.ReadabilityAuditDetailV2,
        _v2Scenario!.ReadabilityAuditDetail,
        () => ParseDetailRequest(context, "demandId"),
        ToReadabilityAuditDetailWire,
        ReadabilityAuditErrorCodes.InvalidQuery);

    private Task HandleV2ErrorSearchAsync(HttpContext context) => ExecuteV2Async(
        context,
        FakeHostOperation.ErrorSearchV2,
        _v2Scenario!.ErrorSearch,
        () => ParseErrorSearchQuery(context.Request.Query),
        ToErrorSearchListWire,
        ErrorSearchErrorCodes.InvalidQuery);

    private Task HandleV2ErrorSearchDetailAsync(HttpContext context) => ExecuteV2Async(
        context,
        FakeHostOperation.ErrorSearchDetailV2,
        _v2Scenario!.ErrorSearchDetail,
        () => ParseDetailRequest(context, "seriesId"),
        ToErrorSearchDetailWire,
        ErrorSearchErrorCodes.InvalidQuery);

    private Task HandleV2ErrorSearchRawEvidenceAsync(HttpContext context) => ExecuteV2Async(
        context,
        FakeHostOperation.ErrorSearchRawEvidenceV2,
        _v2Scenario!.ErrorSearchRawEvidence,
        () => ParseRawEvidenceRequest(context),
        static snapshot => snapshot,
        ErrorSearchErrorCodes.InvalidQuery,
        StatusCodes.Status403Forbidden,
        ErrorSearchErrorCodes.RawAccessDenied);

    private Task HandleV2CurrentAttentionAsync(HttpContext context) => ExecuteV2Async(
        context,
        FakeHostOperation.CurrentAttentionV2,
        _v2Scenario!.CurrentAttention,
        () => ParseCurrentAttentionQuery(context.Request.Query),
        static snapshot => snapshot,
        CurrentIngestAttentionErrorCodes.InvalidQuery);

    private async Task ExecuteV2Async<TRequest, TResponse>(
        HttpContext context,
        FakeHostOperation operation,
        FakeHostScript<TRequest, TResponse>? script,
        Func<TRequest> parseRequest,
        Func<TResponse, object> toWire,
        string invalidQueryCode,
        int unauthorizedStatusCode = StatusCodes.Status401Unauthorized,
        string unauthorizedErrorCode = "UNAUTHORIZED")
    {
        var scenario = _v2Scenario!;
        var endpoint = context.Request.Path + context.Request.QueryString;
        Record(
            new FakeHostRequestMatch(
                scenario.SessionId,
                operation,
                FakeHostRequestState.Started),
            endpoint);

        try
        {
            if (!HasV2Authorization(context, scenario))
            {
                await WriteV2FailureAsync(
                    context,
                    unauthorizedStatusCode,
                    new
                    {
                        code = unauthorizedErrorCode,
                        error = unauthorizedStatusCode == StatusCodes.Status403Forbidden
                            ? "Raw evidence access is denied."
                            : "Bearer credential is required.",
                    },
                    context.RequestAborted).ConfigureAwait(false);
                RecordV2Terminal(scenario.SessionId, operation, FakeHostRequestState.Failed, endpoint);
                return;
            }

            if (script is null)
            {
                await WriteV2FailureAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    new { code = "FAKE_HOST_SCRIPT_MISSING", error = $"No script exists for {endpoint}." },
                    context.RequestAborted).ConfigureAwait(false);
                RecordV2Terminal(scenario.SessionId, operation, FakeHostRequestState.Failed, endpoint);
                return;
            }

            TRequest request;
            try
            {
                request = parseRequest();
            }
            catch (Exception exception) when (IsV2QueryException(exception))
            {
                await WriteV2FailureAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new { code = invalidQueryCode, error = exception.Message },
                    context.RequestAborted).ConfigureAwait(false);
                RecordV2Terminal(scenario.SessionId, operation, FakeHostRequestState.Failed, endpoint);
                return;
            }

            var reply = script.Next(request);
            var completionToken = reply.CompleteAfterCancellation
                ? CancellationToken.None
                : context.RequestAborted;
            if (reply.Gate is not null)
            {
                await reply.Gate.WaitAsync(
                    context.RequestAborted,
                    reply.CompleteAfterCancellation).ConfigureAwait(false);
            }
            if (reply.Delay is { } delay && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, completionToken).ConfigureAwait(false);
            }

            if (reply.FailureShape != FakeHostFailureShape.None || reply.StatusCode is not null)
            {
                var statusCode = reply.StatusCode is { } explicitStatus
                    ? (int)explicitStatus
                    : reply.FailureShape == FakeHostFailureShape.CursorExpired
                        ? StatusCodes.Status410Gone
                        : StatusCodes.Status400BadRequest;
                await WriteV2FailureAsync(
                    context,
                    statusCode,
                    reply.ResponseBody ?? new { code = invalidQueryCode, error = reply.FailureMessage },
                    completionToken).ConfigureAwait(false);
                RecordV2Terminal(scenario.SessionId, operation, FakeHostRequestState.Failed, endpoint);
                return;
            }

            if (context.RequestAborted.IsCancellationRequested && reply.CompleteAfterCancellation)
            {
                RecordV2Terminal(
                    scenario.SessionId,
                    operation,
                    FakeHostRequestState.CompletedAfterCancellation,
                    endpoint);
                try
                {
                    await context.Response.WriteAsJsonAsync(toWire(reply.Value), completionToken)
                        .ConfigureAwait(false);
                }
                catch when (context.RequestAborted.IsCancellationRequested)
                {
                    // The caller deliberately closed the HTTP request. The fake work still
                    // completed so generation admission can be asserted from the timeline.
                }
                return;
            }

            await context.Response.WriteAsJsonAsync(toWire(reply.Value), completionToken)
                .ConfigureAwait(false);
            RecordV2Terminal(scenario.SessionId, operation, FakeHostRequestState.Completed, endpoint);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            RecordV2Terminal(scenario.SessionId, operation, FakeHostRequestState.Canceled, endpoint);
        }
        catch
        {
            RecordV2Terminal(scenario.SessionId, operation, FakeHostRequestState.Failed, endpoint);
            throw;
        }
    }

    private static Task WriteV2FailureAsync(
        HttpContext context,
        int statusCode,
        object body,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(body, cancellationToken);
    }

    private void RecordV2Terminal(
        string sessionId,
        FakeHostOperation operation,
        FakeHostRequestState state,
        string endpoint) =>
        Record(new FakeHostRequestMatch(sessionId, operation, state), endpoint);

    private static object ToV2ContractWire(FakeHostV2ContractSnapshot snapshot) => new
    {
        snapshot.ContractVersion,
        snapshot.SchemaVersion,
        NewMesIngestContract.CompatibilityPolicy,
        NewMesIngestContract.OpenApiDocumentPath,
        businessSurface = "READ_ONLY_GET",
        legacySurfacePolicy = "DEVELOPMENT_ONLY_EXCLUDED_FROM_V2",
        transportDemandKeyComparison = NewMesIngestContract.KeyComparison,
        capabilities = snapshot.CapabilityIds.Select(id =>
        {
            var capability = NewMesIngestContract.Capabilities
                .SingleOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            return new
            {
                id,
                version = capability?.Version ?? "fake-v2",
                operations = capability?.Operations.Select(operation => new
                {
                    operation.Method,
                    operation.Path,
                }).ToArray() ?? [],
            };
        }).ToArray(),
        seriesErrorCatalog = SeriesErrorCatalog.Definitions,
        readabilityBlockerCatalog = ReadabilityBlockerCatalog.Definitions,
        readabilityQualificationCheckCatalog =
            ReadabilityQualificationCheckCatalog.Definitions,
    };

    private static object ToDemandSeriesListWire(DemandSeriesListSnapshot snapshot) => new
    {
        snapshot.SnapshotReference,
        snapshot.Snapshot,
        snapshot.ExactTotalCount,
        snapshot.Facets,
        snapshot.Order,
        snapshot.PageSize,
        snapshot.PageNumber,
        snapshot.TotalPages,
        snapshot.Items,
        snapshot.NextCursor,
        snapshot.HasMore,
    };

    private static object ToDemandSeriesDetailWire(DemandSeriesDetailSnapshot detail)
    {
        var series = detail.Series;
        return new
        {
            detail.SnapshotReference,
            detail.Snapshot,
            series.SeriesId,
            series.WorkType,
            series.Sublot,
            series.Lifecycle,
            series.CurrentPresence,
            series.StartedAt,
            series.ArchivedAt,
            series.CreatedPollTraceId,
            series.CreatedProjectionCommitId,
            series.LatestProjectionCommitId,
            series.LastSeriesSequence,
            series.CurrentDemand,
            series.Demands,
            RawObservations = series.RawObservations.Select(ToDemandRawObservationWire).ToArray(),
            series.Events,
            series.CurrentConditions,
            series.ErrorPeriods,
        };
    }

    private static object ToReadabilityAuditListWire(ReadabilityAuditListSnapshot snapshot) => new
    {
        snapshot.SnapshotReference,
        Snapshot = ToReadabilityAuditSnapshotWire(snapshot.Snapshot),
        snapshot.Filter,
        snapshot.ExactTotalDemandCount,
        snapshot.Facets,
        snapshot.Order,
        snapshot.PageSize,
        snapshot.PageNumber,
        snapshot.TotalPages,
        Items = snapshot.Items.Select(ToReadabilityAuditItemWire).ToArray(),
        snapshot.NextCursor,
        snapshot.HasMore,
    };

    private static object ToReadabilityAuditDetailWire(ReadabilityAuditDetailSnapshot detail) => new
    {
        detail.SnapshotReference,
        Snapshot = ToReadabilityAuditSnapshotWire(detail.Snapshot),
        Demand = ToReadabilityAuditItemWire(detail.Demand),
        Series = new
        {
            detail.Series.SeriesId,
            TransportDemandKey = new
            {
                detail.Series.WorkType,
                detail.Series.Sublot,
            },
            detail.Series.Lifecycle,
            detail.Series.CurrentPresence,
            detail.Series.StartedAt,
            detail.Series.ArchivedAt,
            detail.Series.CurrentDemandId,
        },
        detail.QualificationChecks,
        detail.Blockers,
        LatestRawObservations = detail.LatestRawObservations
            .Select(ToDemandRawObservationWire)
            .ToArray(),
        detail.LatestObservationPollTrace,
    };

    private static object ToReadabilityAuditSnapshotWire(
        ReadabilityAuditSnapshotIdentity snapshot) => new
        {
            HistoryEpoch = snapshot.HistoryEpoch.ToString(),
            snapshot.ProjectionCommitId,
            snapshot.ProjectionSequence,
            snapshot.ProjectionCommittedAt,
            snapshot.PollTraceId,
            snapshot.CatalogRevision,
            snapshot.ContractVersion,
        };

    private static object ToErrorSearchListWire(ErrorSearchListSnapshot snapshot) => new
    {
        snapshot.SnapshotReference,
        snapshot.Snapshot,
        snapshot.Filter,
        snapshot.Window,
        snapshot.Order,
        snapshot.TotalSeriesCount,
        snapshot.Facets,
        snapshot.PageSize,
        snapshot.PageNumber,
        snapshot.TotalPages,
        Items = snapshot.Items.Select(ToErrorSearchItemWire).ToArray(),
        snapshot.NextCursor,
        snapshot.HasMore,
    };

    private static object ToErrorSearchDetailWire(ErrorSearchDetailSnapshot detail) => new
    {
        detail.SnapshotReference,
        detail.Snapshot,
        detail.Filter,
        detail.Window,
        detail.Order,
        Series = ToErrorSearchItemWire(detail.Series),
        detail.Periods,
    };

    private static object ToReadabilityAuditItemWire(ReadabilityAuditListItemSnapshot item) => new
    {
        item.DemandId,
        item.SeriesId,
        TransportDemandKey = new { item.WorkType, item.Sublot },
        item.Generation,
        item.PredecessorDemandId,
        item.DemandStatus,
        item.SeriesLifecycle,
        item.SeriesCurrentPresence,
        item.IsCurrentGeneration,
        item.DemandCreatedAt,
        item.DemandLastSeenAt,
        item.GoneConfirmedAt,
        item.LiveMesFields,
        item.CurrentRawObservationCount,
        item.ExternalReadabilityState,
        item.LeadReadabilityBlocker,
        item.ReadabilityBlockers,
        item.LatestObservationPollTraceId,
        item.LatestObservationProjectionCommitId,
        item.LatestObservationAt,
    };

    private static object ToErrorSearchItemWire(ErrorSearchListItemSnapshot item) => new
    {
        item.SeriesId,
        TransportDemandKey = new { item.WorkType, item.Sublot },
        item.ActivityState,
        item.MatchedErrors,
        item.LatestMatchedEvidenceAt,
        item.MatchedPeriodCount,
        item.MatchedDemandGenerationCount,
        item.MesArea,
        item.MesAreaAvailability,
    };

    private static object ToDemandRawObservationWire(DemandRawObservationSnapshot observation) => new
    {
        observation.Ordinal,
        observation.PollTraceId,
        observation.ProjectionCommitId,
        Assignment = observation.Assignment switch
        {
            MesObservationAssignment.Assigned => "ASSIGNED",
            MesObservationAssignment.Unassigned => "UNASSIGNED",
            _ => throw new ArgumentOutOfRangeException(nameof(observation.Assignment)),
        },
        observation.SeriesId,
        observation.DemandId,
        observation.WorkType,
        observation.Sublot,
        observation.Area,
        observation.Eqp,
        observation.Step,
        observation.MesSourceDate,
        observation.Package,
        observation.ObservedAt,
        observation.MesSourceDateRaw,
    };

    private static WatchOverviewQuery ParseOverviewQuery(IQueryCollection query)
    {
        EnsureAllowedQuery(query, "area");
        return new WatchOverviewQuery(ReadSet(query, "area")).NormalizeAndValidate();
    }

    private static DemandSeriesBrowseQuery ParseDemandSeriesQuery(IQueryCollection query)
    {
        EnsureAllowedQuery(
            query,
            "lifecycle", "presence", "workType", "area", "sublot", "seriesId",
            "demandId", "pageSize", "page", "snapshot", "cursor", "order");
        return new DemandSeriesBrowseQuery(
            new DemandSeriesBrowseFilter
            {
                Lifecycles = ReadSet(query, "lifecycle"),
                CurrentPresences = ReadSet(query, "presence"),
                WorkTypes = ReadSet(query, "workType"),
                MesAreas = ReadSet(query, "area"),
                SublotContains = ReadSingle(query, "sublot"),
                SeriesId = ReadSingle(query, "seriesId"),
                DemandId = ReadSingle(query, "demandId"),
            },
            ReadInt(query, "pageSize", DemandSeriesBrowseQuery.DefaultPageSize),
            ReadInt(query, "page", 1),
            ReadSingle(query, "snapshot"),
            ReadSingle(query, "cursor"),
            ReadSingle(query, "order") ?? DemandSeriesBrowseOrder.Default)
            .NormalizeAndValidate();
    }

    private static ReadabilityAuditQuery ParseReadabilityAuditQuery(IQueryCollection query)
    {
        EnsureAllowedQuery(
            query,
            "state", "workType", "blocker", "demandId", "sublot", "area",
            "pageSize", "page", "snapshot", "cursor", "order");
        return new ReadabilityAuditQuery(
            new ReadabilityAuditFilter
            {
                ReadabilityStates = ReadSet(query, "state"),
                WorkTypes = ReadSet(query, "workType"),
                Blockers = ReadSet(query, "blocker"),
                DemandId = ReadSingle(query, "demandId"),
                SublotContains = ReadSingle(query, "sublot"),
                MesAreas = ReadSet(query, "area"),
            },
            ReadInt(query, "pageSize", ReadabilityAuditQuery.DefaultPageSize),
            ReadInt(query, "page", 1),
            ReadSingle(query, "snapshot"),
            ReadSingle(query, "cursor"),
            ReadSingle(query, "order") ?? ReadabilityAuditOrder.Default)
            .NormalizeAndValidate();
    }

    private static ErrorSearchQuery ParseErrorSearchQuery(IQueryCollection query)
    {
        EnsureAllowedQuery(
            query,
            "category", "code", "state", "seriesId", "demandId", "sublot",
            "window", "from", "to", "pageSize", "snapshot", "cursor");
        var window = ReadSingle(query, "window");
        var from = ReadDateTimeOffset(query, "from");
        var to = ReadDateTimeOffset(query, "to");
        if (window is not null && (from is not null || to is not null))
        {
            throw new ArgumentException("window cannot be combined with from or to.");
        }

        var selection = window is null
            ? from is null && to is null
                ? ErrorSearchWindowSelection.Last7Days
                : ErrorSearchWindowSelection.Custom(from, to)
            : new ErrorSearchWindowSelection(window);
        return new ErrorSearchQuery(
            new ErrorSearchFilter
            {
                Categories = ReadSet(query, "category"),
                ErrorCodes = ReadSet(query, "code"),
                ActivityStates = ReadSet(query, "state"),
                SeriesId = ReadSingle(query, "seriesId"),
                DemandId = ReadSingle(query, "demandId"),
                SublotContains = ReadSingle(query, "sublot"),
            },
            selection,
            ReadInt(query, "pageSize", ErrorSearchQuery.DefaultPageSize),
            ReadSingle(query, "snapshot"),
            ReadSingle(query, "cursor"))
            .NormalizeAndValidate();
    }

    private static CurrentIngestAttentionQuery ParseCurrentAttentionQuery(IQueryCollection query)
    {
        EnsureAllowedQuery(query, "pageSize", "pageNumber", "kind", "severity");
        return new CurrentIngestAttentionQuery(
            ReadInt(query, "pageSize", CurrentIngestAttentionQuery.DefaultPageSize),
            ReadInt(query, "pageNumber", 1),
            CurrentIngestAttentionOrder.Default,
            ReadSet(query, "kind"),
            ReadSet(query, "severity"))
            .NormalizeAndValidate();
    }

    private static FakeHostV2DetailRequest ParseDetailRequest(
        HttpContext context,
        string routeName)
    {
        EnsureAllowedQuery(context.Request.Query, "snapshot");
        var objectId = ReadRouteValue(context, routeName);
        var snapshot = ReadSingle(context.Request.Query, "snapshot");
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            throw new ArgumentException("A detail read requires its list snapshot.");
        }
        return new FakeHostV2DetailRequest(objectId, snapshot);
    }

    private static FakeHostV2RawEvidenceRequest ParseRawEvidenceRequest(HttpContext context)
    {
        EnsureAllowedQuery(context.Request.Query, "snapshot", "fields", "maxItems");
        var snapshot = ReadSingle(context.Request.Query, "snapshot");
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            throw new ArgumentException("A raw evidence read requires its error-search snapshot.");
        }
        var query = new ErrorSearchRawEvidenceQuery(
            context.Request.Query.ContainsKey("fields")
                ? ReadSet(context.Request.Query, "fields")
                : ErrorSearchRawEvidenceFields.All,
            ReadInt(
                context.Request.Query,
                "maxItems",
                ErrorSearchRawEvidenceLimits.MaximumItems))
            .NormalizeAndValidate();
        return new FakeHostV2RawEvidenceRequest(
            ReadRouteValue(context, "seriesId"),
            ReadRouteValue(context, "evidenceId"),
            snapshot,
            query);
    }

    private static void EnsureAllowedQuery(IQueryCollection query, params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var unsupported = query.Keys.FirstOrDefault(key => !allowedSet.Contains(key));
        if (unsupported is not null)
        {
            throw new ArgumentException($"Unsupported V2 query parameter '{unsupported}'.");
        }
    }

    private static IReadOnlyList<string> ReadSet(IQueryCollection query, string name) =>
        !query.TryGetValue(name, out var values)
            ? []
            : values
                .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.None))
                .Where(value => value.Length > 0)
                .ToArray();

    private static string? ReadSingle(IQueryCollection query, string name)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return null;
        }
        if (values.Count != 1)
        {
            throw new ArgumentException($"{name} may be supplied once.");
        }
        return values[0];
    }

    private static int ReadInt(IQueryCollection query, string name, int defaultValue)
    {
        var raw = ReadSingle(query, name);
        if (raw is null)
        {
            return defaultValue;
        }
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"{name} must be an integer.");
    }

    private static DateTimeOffset? ReadDateTimeOffset(IQueryCollection query, string name)
    {
        var raw = ReadSingle(query, name);
        if (raw is null)
        {
            return null;
        }
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : throw new ArgumentException($"{name} must be an ISO-8601 timestamp.");
    }

    private static string ReadRouteValue(HttpContext context, string name) =>
        context.Request.RouteValues.TryGetValue(name, out var value)
        && value is string text
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new ArgumentException($"{name} is required.");

    private static bool IsV2QueryException(Exception exception) =>
        exception is ArgumentException
            or FormatException
            or DemandSeriesBrowseException
            or ReadabilityAuditException
            or ErrorSearchException
            or CurrentIngestAttentionException;

    private static bool HasV2Authorization(HttpContext context, FakeHostV2Scenario scenario) =>
        string.Equals(
            context.Request.Headers.Authorization.ToString(),
            $"Bearer {scenario.Credential}",
            StringComparison.Ordinal);

    private void Record(
        FakeHostRequestMatch request,
        string endpoint)
    {
        List<TaskCompletionSource> completed = [];
        lock (_sync)
        {
            var entry = new FakeHostRequestEvent(
                ++_nextSequence,
                request,
                endpoint);
            _timeline.Add(entry);

            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                var waiter = _waiters[index];
                if (entry.Request != waiter.Request)
                {
                    continue;
                }

                completed.Add(waiter.Completion);
                _waiters.RemoveAt(index);
            }
        }

        foreach (var completion in completed)
        {
            completion.TrySetResult();
        }
    }

    private sealed record TimelineWaiter(
        FakeHostRequestMatch Request,
        TaskCompletionSource Completion);
}
