using MesIngest.Core.SeriesProjection;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MesIngest.Host;

internal static class NewMesIngestEndpoints
{
    public static IEndpointRouteBuilder MapNewMesIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v2/contract", GetContract)
            .DescribeV2("GetV2Contract", "Contract");

        endpoints.MapGet("/api/v2/demand-series", ListDemandSeriesAsync)
            .DescribeV2("ListDemandSeries", "DemandSeries");

        endpoints.MapGet("/api/v2/demand-series/by-key", GetDemandSeriesByKeyAsync)
            .DescribeV2("GetDemandSeriesByKey", "DemandSeries");

        endpoints.MapGet("/api/v2/demand-series/{seriesId}", GetDemandSeriesAsync)
            .DescribeV2("GetDemandSeries", "DemandSeries");

        endpoints.MapGet(
                "/api/v2/externally-readable-demand-catalog",
                GetExternallyReadableDemandCatalogAsync)
            .DescribeV2("GetExternallyReadableDemandCatalog", "DemandCatalog");

        endpoints.MapGet("/api/v2/readability-audit", ListReadabilityAuditAsync)
            .DescribeV2("ListReadabilityAudit", "ReadabilityAudit");

        endpoints.MapGet(
                "/api/v2/readability-audit/{demandId}",
                GetReadabilityAuditDetailAsync)
            .DescribeV2("GetReadabilityAuditDetail", "ReadabilityAudit");

        endpoints.MapGet("/api/v2/error-search", ListErrorSearchAsync)
            .DescribeV2("ListErrorSearch", "ErrorSearch");

        endpoints.MapGet("/api/v2/error-search/{seriesId}", GetErrorSearchDetailAsync)
            .DescribeV2("GetErrorSearchDetail", "ErrorSearch");

        endpoints.MapGet(
                "/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations",
                GetErrorSearchRawEvidenceAsync)
            .DescribeV2("GetErrorSearchRawEvidence", "ErrorSearch");

        endpoints.MapGet(
                "/api/v2/current-ingest-attention",
                ListCurrentIngestAttentionAsync)
            .DescribeV2("ListCurrentIngestAttention", "CurrentIngestAttention");

        endpoints.MapGet("/api/v2/watch-overview", GetWatchOverviewAsync)
            .DescribeV2("GetWatchOverview", "WatchOverview");

        endpoints.MapGet("/api/v2/poll-traces/{pollTraceId}", GetPollTraceAsync)
            .DescribeV2("GetPollTrace", "PollEvidence");

        endpoints.MapGet("/api/v2/absence-authority", GetAbsenceAuthorityAsync)
            .DescribeV2("GetCurrentAbsenceAuthority", "PollEvidence");

        endpoints.MapGet(
                "/api/v2/absence-authority/{hostSessionId}",
                GetAbsenceAuthorityByHostSessionIdAsync)
            .DescribeV2("GetAbsenceAuthority", "PollEvidence");

        endpoints.MapGet(
                "/api/v2/task-type-protections",
                ListTaskTypeProtectionsAsync)
            .DescribeV2("ListTaskTypeProtections", "PollEvidence");

        endpoints.MapGet(
                "/api/v2/task-type-protections/{workType}",
                GetTaskTypeProtectionAsync)
            .DescribeV2("GetTaskTypeProtection", "PollEvidence");

        return endpoints;
    }

    private static Ok<NewMesIngestContractDto> GetContract() =>
        TypedResults.Ok(new NewMesIngestContractDto(
            NewMesIngestContract.Version,
            NewMesIngestContract.SchemaVersion,
            NewMesIngestContract.CompatibilityPolicy,
            NewMesIngestContract.OpenApiDocumentPath,
            "READ_ONLY_GET",
            "DEVELOPMENT_ONLY_EXCLUDED_FROM_V2",
            NewMesIngestContract.KeyComparison,
            NewMesIngestContract.Capabilities.Select(NewMesIngestCapabilityDto.From).ToArray(),
            SeriesErrorCatalog.Definitions.Select(SeriesErrorDefinitionDto.From).ToArray(),
            ReadabilityBlockerCatalog.Definitions.Select(ReadabilityBlockerDefinitionDto.From).ToArray(),
            ReadabilityQualificationCheckCatalog.Definitions
                .Select(ReadabilityQualificationCheckDefinitionDto.From).ToArray()));

    private static RouteHandlerBuilder DescribeV2(
        this RouteHandlerBuilder endpoint,
        string operationName,
        string tag)
    {
        endpoint = endpoint
            .WithGroupName(MesIngestOpenApi.V2DocumentName)
            .WithName(operationName)
            .WithTags(tag);

        // Catalog owns its CATALOG_QUERY_NOT_SUPPORTED response and restricted
        // raw evidence must authorize before validating any request shape.
        if (operationName is "GetExternallyReadableDemandCatalog" or "GetErrorSearchRawEvidence")
        {
            return endpoint;
        }

        return endpoint.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            var allowed = AllowedQueryParameters(operationName);
            var unsupported = request.Query.Keys.FirstOrDefault(key => !allowed.Contains(key));
            if (unsupported is not null)
            {
                return Results.BadRequest(new NewMesIngestErrorDto(
                    InvalidQueryCode(operationName),
                    $"Unsupported V2 query parameter '{unsupported}'."));
            }

            return await next(context);
        });
    }

    internal static IReadOnlySet<string> AllowedQueryParameters(string operationName) =>
        operationName switch
        {
            "ListDemandSeries" => Set(
                "lifecycle", "presence", "workType", "area", "sublot", "seriesId",
                "demandId", "pageSize", "page", "snapshot", "cursor", "order"),
            "GetDemandSeriesByKey" => Set("workType", "sublot", "snapshot"),
            "GetDemandSeries" => Set("snapshot"),
            "ListReadabilityAudit" => Set(
                "state", "workType", "blocker", "demandId", "sublot", "area",
                "pageSize", "page", "snapshot", "cursor", "order"),
            "GetReadabilityAuditDetail" => Set("snapshot"),
            "ListErrorSearch" => Set(
                "category", "code", "state", "seriesId", "demandId", "sublot",
                "window", "from", "to", "pageSize", "snapshot", "cursor"),
            "GetErrorSearchDetail" => Set("snapshot"),
            "GetErrorSearchRawEvidence" => Set("snapshot", "fields", "maxItems"),
            "ListCurrentIngestAttention" => Set("pageSize", "pageNumber", "kind", "severity"),
            "GetWatchOverview" => Set("area"),
            _ => Set(),
        };

    private static string InvalidQueryCode(string operationName) => operationName switch
    {
        "ListDemandSeries" or "GetDemandSeriesByKey" or "GetDemandSeries" =>
            DemandSeriesBrowseErrorCodes.InvalidQuery,
        "ListReadabilityAudit" or "GetReadabilityAuditDetail" =>
            ReadabilityAuditErrorCodes.InvalidQuery,
        "ListErrorSearch" or "GetErrorSearchDetail" => ErrorSearchErrorCodes.InvalidQuery,
        "ListCurrentIngestAttention" => CurrentIngestAttentionErrorCodes.InvalidQuery,
        "GetWatchOverview" => WatchOverviewErrorCodes.InvalidQuery,
        "GetV2Contract" => "INVALID_CONTRACT_QUERY",
        _ => PollEvidenceErrorCodes.InvalidQuery,
    };

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private static async Task<IResult> GetExternallyReadableDemandCatalogAsync(
        HttpRequest request,
        HttpResponse response,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        // This is a full-range fact resource. Silently accepting query filters
        // would let Dispatch scope leak into MesIngest's catalog contract.
        if (request.Query.Count != 0)
        {
            return Results.BadRequest(new NewMesIngestErrorDto(
                "CATALOG_QUERY_NOT_SUPPORTED",
                "The externally readable Demand catalog is complete and accepts no query parameters."));
        }

        if (!TryParseCatalogCondition(
                request.Headers.IfNoneMatch,
                out var knownIdentity,
                out var conditionError))
        {
            return Results.BadRequest(new NewMesIngestErrorDto(
                "INVALID_CATALOG_CONDITION",
                conditionError!));
        }

        var read = await projection.ReadExternallyReadableDemandCatalogAsync(
            knownIdentity,
            cancellationToken);
        var etag = CreateCatalogEtag(read.Identity);
        response.Headers.ETag = etag;
        response.Headers.CacheControl = "private, no-cache";
        if (read.NotModified)
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Ok(ExternallyReadableDemandCatalogDto.From(read.Snapshot!));
    }

    private static bool TryParseCatalogCondition(
        Microsoft.Extensions.Primitives.StringValues values,
        out ExternallyReadableDemandCatalogIdentity? knownIdentity,
        out string? error)
    {
        knownIdentity = null;
        error = null;
        if (values.Count == 0)
        {
            return true;
        }

        // A catalog condition must identify exactly one semantic revision.
        // Multiple HTTP header lines and comma lists are normalized here.
        var tags = values
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries))
            .Where(value => value.Length > 0)
            .ToArray();
        if (tags.Length != 1 || tags[0] == "*")
        {
            error = "If-None-Match must contain one catalog ETag.";
            return false;
        }

        var tag = tags[0];
        if (tag.StartsWith("W/", StringComparison.Ordinal))
        {
            tag = tag[2..];
        }
        if (tag.Length < 12 || tag[0] != '"' || tag[^1] != '"')
        {
            error = "If-None-Match is not a catalog ETag.";
            return false;
        }

        var opaque = tag[1..^1];
        if (!ExternallyReadableDemandCatalogEtagCodec.TryParseOpaqueTag(
                opaque,
                out knownIdentity))
        {
            error = "If-None-Match is not a catalog ETag.";
            return false;
        }

        return true;
    }

    private static string CreateCatalogEtag(ExternallyReadableDemandCatalogIdentity identity) =>
        $"W/\"{ExternallyReadableDemandCatalogEtagCodec.FormatOpaqueTag(identity)}\"";

    private static async Task<IResult> GetDemandSeriesByKeyAsync(
        string workType,
        string sublot,
        HttpRequest request,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(
                workType,
                128,
                nameof(workType),
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                out var workTypeError))
        {
            return Results.BadRequest(workTypeError);
        }
        if (!TryValidateRequiredText(
                sublot,
                256,
                nameof(sublot),
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                out var sublotError))
        {
            return Results.BadRequest(sublotError);
        }

        try
        {
            var requestedSnapshot = ReadSingle(request.Query, "snapshot");
            // Select (or validate) the immutable fence before consulting the
            // permanent key index. Invalid credentials must not be masked by a
            // mutable key miss, and no second list read may advance the fence.
            var frozen = await projection.ListDemandSeriesAsync(
                new DemandSeriesBrowseQuery(
                    new DemandSeriesBrowseFilter
                    {
                        WorkTypes = [workType],
                        Sublot = sublot,
                    },
                    PageSize: 1,
                    SnapshotReference: requestedSnapshot),
                cancellationToken);
            var located = frozen.Items.SingleOrDefault();
            if (located is null)
            {
                return Results.NotFound(new NewMesIngestErrorDto(
                    DemandSeriesBrowseErrorCodes.ObjectNotInSnapshot,
                    "The requested DemandSeries is not available in the selected snapshot."));
            }

            var detail = await projection.GetDemandSeriesAtSnapshotAsync(
                located.SeriesId,
                frozen.SnapshotReference,
                cancellationToken);
            return detail is null
                ? Results.NotFound(new NewMesIngestErrorDto(
                    DemandSeriesBrowseErrorCodes.ObjectNotInSnapshot,
                    "The requested DemandSeries is not available in the selected snapshot."))
                : Results.Ok(FrozenDemandSeriesDto.From(detail));
        }
        catch (DemandSeriesBrowseException exception)
        {
            return ToBrowseError(exception);
        }
    }

    private static async Task<IResult> GetDemandSeriesAsync(
        string seriesId,
        HttpRequest request,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(
                seriesId,
                64,
                nameof(seriesId),
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                out var error))
        {
            return Results.BadRequest(error);
        }

        try
        {
            var snapshotReference = ReadSingle(request.Query, "snapshot");
            if (snapshotReference is null)
            {
                var frozen = await projection.ListDemandSeriesAsync(
                    new DemandSeriesBrowseQuery(
                        new DemandSeriesBrowseFilter { SeriesId = seriesId },
                        PageSize: 1),
                    cancellationToken);
                if (frozen.Items.Count == 0)
                {
                    return Results.NotFound(new NewMesIngestErrorDto(
                        DemandSeriesBrowseErrorCodes.ObjectNotInSnapshot,
                        "The requested DemandSeries is not available in the selected snapshot."));
                }

                snapshotReference = frozen.SnapshotReference;
            }

            var detail = await projection.GetDemandSeriesAtSnapshotAsync(
                seriesId,
                snapshotReference,
                cancellationToken);
            return detail is null
                ? Results.NotFound(new NewMesIngestErrorDto(
                    DemandSeriesBrowseErrorCodes.ObjectNotInSnapshot,
                    "The requested DemandSeries is not available in the selected snapshot."))
                : Results.Ok(FrozenDemandSeriesDto.From(detail));
        }
        catch (DemandSeriesBrowseException exception)
        {
            return ToBrowseError(exception);
        }
    }

    private static async Task<IResult> ListDemandSeriesAsync(
        HttpRequest request,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = ParseBrowseQuery(request.Query);
            var snapshot = await projection.ListDemandSeriesAsync(query, cancellationToken);
            return Results.Ok(DemandSeriesListDto.From(snapshot));
        }
        catch (DemandSeriesBrowseException exception)
        {
            return ToBrowseError(exception);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new NewMesIngestErrorDto(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                exception.Message));
        }
    }

    private static DemandSeriesBrowseQuery ParseBrowseQuery(IQueryCollection query)
    {
        var pageSize = ParseBoundedInt(query, "pageSize", DemandSeriesBrowseQuery.DefaultPageSize);
        var pageNumber = ParseBoundedInt(query, "page", 1);
        var filter = new DemandSeriesBrowseFilter
        {
            Lifecycles = ReadSet(query, "lifecycle"),
            CurrentPresences = ReadSet(query, "presence"),
            WorkTypes = ReadSet(query, "workType"),
            MesAreas = ReadSet(query, "area"),
            SublotContains = ReadSingle(query, "sublot"),
            SeriesId = ReadSingle(query, "seriesId"),
            DemandId = ReadSingle(query, "demandId"),
        };
        ValidateFilterVocabulary(filter);
        return new DemandSeriesBrowseQuery(
            filter,
            pageSize,
            pageNumber,
            ReadSingle(query, "snapshot"),
            ReadSingle(query, "cursor"),
            ReadSingle(query, "order") ?? DemandSeriesBrowseOrder.Default)
            .NormalizeAndValidate();
    }

    private static int ParseBoundedInt(IQueryCollection query, string name, int defaultValue)
    {
        var raw = ReadSingle(query, name);
        if (raw is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                $"{name} must be an integer.");
        }

        return value;
    }

    private static string? ReadSingle(IQueryCollection query, string name)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return null;
        }
        if (values.Count != 1)
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                $"{name} may be supplied once.");
        }
        return values[0];
    }

    private static IReadOnlyList<string> ReadSet(IQueryCollection query, string name)
    {
        if (!query.TryGetValue(name, out var values))
        {
            return Array.Empty<string>();
        }

        return values
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.None))
            .Where(value => value.Length > 0)
            .ToArray();
    }

    private static void ValidateFilterVocabulary(DemandSeriesBrowseFilter filter)
    {
        var allowedLifecycle = new HashSet<string>(
            [DemandSeriesLifecycleContract.Tracking, DemandSeriesLifecycleContract.Archived],
            StringComparer.Ordinal);
        var allowedPresence = new HashSet<string>(
            [
                DemandSeriesLifecycleContract.Visible,
                DemandSeriesLifecycleContract.Gone,
                DemandSeriesLifecycleContract.LongGoneButVisible,
            ],
            StringComparer.Ordinal);
        if (filter.Lifecycles.Any(value => !allowedLifecycle.Contains(value)))
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                "lifecycle contains an unsupported exact value.");
        }
        if (filter.CurrentPresences.Any(value => !allowedPresence.Contains(value)))
        {
            throw new DemandSeriesBrowseException(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                "presence contains an unsupported exact value.");
        }
    }

    private static IResult ToBrowseError(DemandSeriesBrowseException exception)
    {
        var error = new NewMesIngestErrorDto(exception.Code, exception.Message);
        return exception.Code switch
        {
            DemandSeriesBrowseErrorCodes.ProjectionNotAvailable => Results.Conflict(error),
            DemandSeriesBrowseErrorCodes.SnapshotNotFound => Results.Json(error, statusCode: StatusCodes.Status410Gone),
            _ => Results.BadRequest(error),
        };
    }

    private static async Task<IResult> ListReadabilityAuditAsync(
        HttpRequest request,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = ParseReadabilityAuditQuery(request.Query);
            var snapshot = await projection.ListReadabilityAuditAsync(query, cancellationToken);
            return Results.Ok(ReadabilityAuditListDto.From(snapshot));
        }
        catch (ReadabilityAuditException exception)
        {
            return ToReadabilityAuditError(exception);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new NewMesIngestErrorDto(
                ReadabilityAuditErrorCodes.InvalidQuery,
                exception.Message));
        }
    }

    private static async Task<IResult> GetReadabilityAuditDetailAsync(
        string demandId,
        HttpRequest request,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(
                demandId,
                64,
                nameof(demandId),
                ReadabilityAuditErrorCodes.InvalidQuery,
                out var requestError))
        {
            return Results.BadRequest(requestError);
        }
        try
        {
            var snapshotReference = ReadReadabilityAuditSingle(request.Query, "snapshot");
            if (snapshotReference is null)
            {
                return Results.BadRequest(new NewMesIngestErrorDto(
                    ReadabilityAuditErrorCodes.InvalidQuery,
                    "A readability audit detail requires its list snapshot."));
            }
            if (request.Query.Keys.Any(key => !string.Equals(key, "snapshot", StringComparison.Ordinal)))
            {
                return Results.BadRequest(new NewMesIngestErrorDto(
                    ReadabilityAuditErrorCodes.InvalidQuery,
                    "A readability audit detail accepts only its snapshot parameter."));
            }
            var detail = await projection.GetReadabilityAuditDetailAsync(
                demandId,
                snapshotReference,
                cancellationToken);
            return detail is null
                ? Results.NotFound(new NewMesIngestErrorDto(
                    ReadabilityAuditErrorCodes.ObjectNotInSnapshot,
                    "The requested Demand is not available in the selected readability snapshot."))
                : Results.Ok(ReadabilityAuditDetailDto.From(detail));
        }
        catch (ReadabilityAuditException exception)
        {
            return ToReadabilityAuditError(exception);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new NewMesIngestErrorDto(
                ReadabilityAuditErrorCodes.InvalidQuery,
                exception.Message));
        }
    }

    private static ReadabilityAuditQuery ParseReadabilityAuditQuery(IQueryCollection query)
    {
        var allowedKeys = new HashSet<string>(
        [
            "state", "workType", "blocker", "demandId", "sublot", "area",
            "pageSize", "page", "snapshot", "cursor", "order",
        ],
            StringComparer.Ordinal);
        var unsupported = query.Keys.FirstOrDefault(key => !allowedKeys.Contains(key));
        if (unsupported is not null)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                $"Unsupported readability audit query parameter '{unsupported}'.");
        }

        var filter = new ReadabilityAuditFilter
        {
            ReadabilityStates = ReadSet(query, "state"),
            WorkTypes = ReadSet(query, "workType"),
            Blockers = ReadSet(query, "blocker"),
            DemandId = ReadReadabilityAuditSingle(query, "demandId"),
            SublotContains = ReadReadabilityAuditSingle(query, "sublot"),
            MesAreas = ReadSet(query, "area"),
        }.Normalize();
        ValidateReadabilityAuditVocabulary(filter);
        return new ReadabilityAuditQuery(
            filter,
            ParseReadabilityAuditInt(query, "pageSize", ReadabilityAuditQuery.DefaultPageSize),
            ParseReadabilityAuditInt(query, "page", 1),
            ReadReadabilityAuditSingle(query, "snapshot"),
            ReadReadabilityAuditSingle(query, "cursor"),
            ReadReadabilityAuditSingle(query, "order") ?? ReadabilityAuditOrder.Default)
            .NormalizeAndValidate();
    }

    private static int ParseReadabilityAuditInt(
        IQueryCollection query,
        string name,
        int defaultValue)
    {
        var raw = ReadReadabilityAuditSingle(query, name);
        if (raw is null)
        {
            return defaultValue;
        }
        if (!int.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                $"{name} must be an integer.");
        }
        return value;
    }

    private static string? ReadReadabilityAuditSingle(IQueryCollection query, string name)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return null;
        }
        if (values.Count != 1)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                $"{name} may be supplied once.");
        }
        return values[0];
    }

    private static void ValidateReadabilityAuditVocabulary(ReadabilityAuditFilter filter)
    {
        var validStates = new HashSet<string>(
            [ExternalReadabilityStates.Readable, ExternalReadabilityStates.NotReadable],
            StringComparer.Ordinal);
        if (filter.ReadabilityStates.Any(state => !validStates.Contains(state)))
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "state contains an unsupported exact value.");
        }
        var validBlockers = ReadabilityBlockerCatalog.Definitions
            .Select(definition => definition.Code)
            .ToHashSet(StringComparer.Ordinal);
        if (filter.Blockers.Any(blocker => !validBlockers.Contains(blocker)))
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.InvalidQuery,
                "blocker contains an unsupported exact value.");
        }
        foreach (var area in filter.MesAreas)
        {
            var validation = MesFieldValidation.Evaluate(new LiveMesFieldSetSnapshot(
                area,
                "VALID",
                "VALID",
                DateTimeOffset.UnixEpoch,
                "VALID"));
            if (validation.Issues.Any(issue =>
                    string.Equals(issue.SubjectKind, "AREA", StringComparison.Ordinal)))
            {
                throw new ReadabilityAuditException(
                    ReadabilityAuditErrorCodes.InvalidQuery,
                    $"area contains invalid MesArea '{area}'.");
            }
        }
    }

    private static IResult ToReadabilityAuditError(ReadabilityAuditException exception)
    {
        var error = new NewMesIngestErrorDto(exception.Code, exception.Message);
        return exception.Code switch
        {
            ReadabilityAuditErrorCodes.ProjectionNotAvailable => Results.Conflict(error),
            ReadabilityAuditErrorCodes.SnapshotNotFound =>
                Results.Json(error, statusCode: StatusCodes.Status410Gone),
            _ => Results.BadRequest(error),
        };
    }

    private static async Task<IResult> ListErrorSearchAsync(
        HttpRequest request,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = ParseErrorSearchQuery(request.Query);
            var snapshot = await projection.ListErrorSearchAsync(query, cancellationToken);
            return Results.Ok(ErrorSearchListDto.From(snapshot));
        }
        catch (ErrorSearchException exception)
        {
            return ToErrorSearchError(exception);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new NewMesIngestErrorDto(
                ErrorSearchErrorCodes.InvalidQuery,
                exception.Message));
        }
    }

    private static ErrorSearchQuery ParseErrorSearchQuery(IQueryCollection query)
    {
        var allowedKeys = new HashSet<string>(
        [
            "category", "code", "state", "seriesId", "demandId", "sublot",
            "window", "from", "to", "pageSize", "snapshot", "cursor",
        ],
            StringComparer.Ordinal);
        var unsupported = query.Keys.FirstOrDefault(key => !allowedKeys.Contains(key));
        if (unsupported is not null)
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.InvalidQuery,
                $"Unsupported error search query parameter '{unsupported}'.");
        }

        var window = ReadErrorSearchSingle(query, "window");
        var from = ReadErrorSearchSingle(query, "from");
        var to = ReadErrorSearchSingle(query, "to");
        if (window is not null && (from is not null || to is not null))
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.InvalidQuery,
                "window cannot be combined with from or to.");
        }

        var windowSelection = window is not null
            ? ParseErrorSearchWindow(window)
            : from is not null || to is not null
                ? ErrorSearchWindowSelection.Custom(
                    ParseErrorSearchTimestamp(from, "from"),
                    ParseErrorSearchTimestamp(to, "to"))
                : ErrorSearchWindowSelection.Last7Days;
        var filter = new ErrorSearchFilter
        {
            Categories = ReadSet(query, "category"),
            ErrorCodes = ReadSet(query, "code"),
            ActivityStates = ReadSet(query, "state"),
            SeriesId = ReadErrorSearchSingle(query, "seriesId"),
            DemandId = ReadErrorSearchSingle(query, "demandId"),
            SublotContains = ReadErrorSearchSingle(query, "sublot"),
        };
        return new ErrorSearchQuery(
            filter,
            windowSelection,
            ParseErrorSearchInt(query, "pageSize", ErrorSearchQuery.DefaultPageSize),
            ReadErrorSearchSingle(query, "snapshot"),
            ReadErrorSearchSingle(query, "cursor"))
            .NormalizeAndValidate();
    }

    private static ErrorSearchWindowSelection ParseErrorSearchWindow(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            ErrorSearchWindowKinds.Last24Hours => ErrorSearchWindowSelection.Last24Hours,
            ErrorSearchWindowKinds.Last7Days => ErrorSearchWindowSelection.Last7Days,
            ErrorSearchWindowKinds.Last30Days => ErrorSearchWindowSelection.Last30Days,
            ErrorSearchWindowKinds.AllHistory => ErrorSearchWindowSelection.AllHistory,
            _ => throw new ErrorSearchException(
                ErrorSearchErrorCodes.InvalidQuery,
                "window contains an unsupported exact value."),
        };

    private static DateTimeOffset? ParseErrorSearchTimestamp(string? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        var hasExplicitOffset = System.Text.RegularExpressions.Regex.IsMatch(
            value,
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        string[] formats =
        [
            "yyyy-MM-dd'T'HH:mm:ssK",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        ];
        if (!hasExplicitOffset
            || !DateTimeOffset.TryParseExact(
                value,
                formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var timestamp))
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.InvalidQuery,
                $"{name} must be an ISO-8601 timestamp with an explicit UTC offset.");
        }

        return timestamp.ToUniversalTime();
    }

    private static int ParseErrorSearchInt(
        IQueryCollection query,
        string name,
        int defaultValue)
    {
        var raw = ReadErrorSearchSingle(query, name);
        if (raw is null)
        {
            return defaultValue;
        }
        if (!int.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.InvalidQuery,
                $"{name} must be an integer.");
        }
        return value;
    }

    private static string? ReadErrorSearchSingle(IQueryCollection query, string name)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return null;
        }
        if (values.Count != 1)
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.InvalidQuery,
                $"{name} may be supplied once.");
        }
        return values[0];
    }

    private static IResult ToErrorSearchError(ErrorSearchException exception)
    {
        var error = new NewMesIngestErrorDto(exception.Code, exception.Message);
        return exception.Code switch
        {
            ErrorSearchErrorCodes.ProjectionNotAvailable => Results.Conflict(error),
            ErrorSearchErrorCodes.SnapshotNotFound =>
                Results.Json(error, statusCode: StatusCodes.Status410Gone),
            ErrorSearchErrorCodes.ObjectNotInSnapshot =>
                Results.Json(error, statusCode: StatusCodes.Status404NotFound),
            ErrorSearchErrorCodes.RawLimitExceeded =>
                Results.Json(error, statusCode: StatusCodes.Status413PayloadTooLarge),
            _ => Results.BadRequest(error),
        };
    }

    private static async Task<IResult> GetErrorSearchDetailAsync(
        string seriesId,
        HttpRequest request,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(
                seriesId,
                64,
                nameof(seriesId),
                ErrorSearchErrorCodes.InvalidQuery,
                out var requestError))
        {
            return Results.BadRequest(requestError);
        }

        string snapshotReference;
        try
        {
            snapshotReference = ParseRequiredErrorSearchSnapshot(
                request.Query,
                new HashSet<string>(["snapshot"], StringComparer.Ordinal));
        }
        catch (ErrorSearchException exception)
        {
            return ToErrorSearchError(exception);
        }

        try
        {
            var detail = await projection.GetErrorSearchDetailAsync(
                seriesId,
                snapshotReference,
                cancellationToken);
            return detail is null
                ? Results.NotFound(new NewMesIngestErrorDto(
                    ErrorSearchErrorCodes.ObjectNotInSnapshot,
                    "The requested object is not available in this error-search snapshot."))
                : Results.Ok(ErrorSearchDetailDto.From(detail));
        }
        catch (ErrorSearchException exception)
        {
            return ToErrorSearchError(exception);
        }
    }

    private static async Task<IResult> GetErrorSearchRawEvidenceAsync(
        string seriesId,
        string evidenceId,
        HttpRequest request,
        MesIngestHostOptions options,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        // Authorization intentionally precedes route validation, token parsing,
        // and every projection call so this restricted endpoint cannot become an
        // object-existence oracle (including on localhost).
        if (!SharedSecretAuth.IsExplicitlyAuthorized(request, options))
        {
            return Results.Json(
                new NewMesIngestErrorDto(
                    ErrorSearchErrorCodes.RawAccessDenied,
                    "Raw evidence access is denied."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!TryValidateRequiredText(
                seriesId,
                64,
                nameof(seriesId),
                ErrorSearchErrorCodes.InvalidQuery,
                out var seriesError))
        {
            return Results.BadRequest(seriesError);
        }
        if (!TryValidateRequiredText(
                evidenceId,
                128,
                nameof(evidenceId),
                ErrorSearchErrorCodes.InvalidQuery,
                out var evidenceError))
        {
            return Results.BadRequest(evidenceError);
        }

        ParsedErrorSearchRawEvidenceRequest requestContract;
        try
        {
            requestContract = ParseErrorSearchRawEvidenceRequest(request.Query);
        }
        catch (ErrorSearchException exception)
        {
            return ToErrorSearchError(exception);
        }

        try
        {
            var result = await projection.GetErrorSearchRawEvidenceAsync(
                seriesId,
                evidenceId,
                requestContract.SnapshotReference,
                requestContract.Query,
                cancellationToken);
            return result is null
                ? Results.NotFound(new NewMesIngestErrorDto(
                    ErrorSearchErrorCodes.ObjectNotInSnapshot,
                    "The requested object is not available in this error-search snapshot."))
                : Results.Ok(ErrorSearchRawEvidenceDto.From(result));
        }
        catch (ErrorSearchException exception)
        {
            return ToErrorSearchError(exception);
        }
    }

    private static string ParseRequiredErrorSearchSnapshot(
        IQueryCollection query,
        IReadOnlySet<string> allowedKeys)
    {
        var unsupported = query.Keys.FirstOrDefault(key => !allowedKeys.Contains(key));
        if (unsupported is not null)
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.InvalidQuery,
                $"Unsupported error detail query parameter '{unsupported}'.");
        }

        var snapshot = ReadErrorSearchSingle(query, "snapshot");
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.InvalidQuery,
                "snapshot is required.");
        }

        return snapshot;
    }

    private static ParsedErrorSearchRawEvidenceRequest ParseErrorSearchRawEvidenceRequest(
        IQueryCollection query)
    {
        var allowedKeys = AllowedQueryParameters("GetErrorSearchRawEvidence");
        var snapshot = ParseRequiredErrorSearchSnapshot(query, allowedKeys);

        var fieldsValue = ReadErrorSearchSingle(query, "fields");
        IReadOnlyList<string> fields = fieldsValue is null
            ? ErrorSearchRawEvidenceFields.All
            : fieldsValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        var unsupportedField = fields.FirstOrDefault(field => !ErrorSearchRawEvidenceFields.All.Contains(
            field,
            StringComparer.Ordinal));
        if (fields.Count == 0 || unsupportedField is not null)
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.RawFieldNotAllowed,
                "fields contains a field that is not allowed for raw evidence.");
        }

        var maxItems = ParseErrorSearchInt(
            query,
            "maxItems",
            ErrorSearchRawEvidenceLimits.MaximumItems);
        if (maxItems is < 1 or > ErrorSearchRawEvidenceLimits.MaximumItems)
        {
            throw new ErrorSearchException(
                ErrorSearchErrorCodes.RawLimitExceeded,
                $"maxItems must be between 1 and {ErrorSearchRawEvidenceLimits.MaximumItems}.");
        }

        return new ParsedErrorSearchRawEvidenceRequest(
            snapshot,
            new ErrorSearchRawEvidenceQuery(fields, maxItems).NormalizeAndValidate());
    }

    private static async Task<IResult> ListCurrentIngestAttentionAsync(
        HttpRequest request,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = ParseCurrentIngestAttentionQuery(request.Query);
            var snapshot = await projection.ReadCurrentIngestAttentionAsync(
                query,
                cancellationToken);
            return Results.Ok(CurrentIngestAttentionDto.From(snapshot));
        }
        catch (CurrentIngestAttentionException exception)
        {
            return ToCurrentIngestAttentionError(exception);
        }
    }

    private static CurrentIngestAttentionQuery ParseCurrentIngestAttentionQuery(
        IQueryCollection query)
    {
        var allowedKeys = new HashSet<string>(
            ["pageSize", "pageNumber", "kind", "severity"],
            StringComparer.Ordinal);
        var unsupported = query.Keys.FirstOrDefault(key => !allowedKeys.Contains(key));
        if (unsupported is not null)
        {
            throw new CurrentIngestAttentionException(
                CurrentIngestAttentionErrorCodes.InvalidQuery,
                $"Unsupported current-ingest-attention query parameter '{unsupported}'.");
        }

        return new CurrentIngestAttentionQuery(
                ParseCurrentIngestAttentionInt(
                    query,
                    "pageSize",
                    CurrentIngestAttentionQuery.DefaultPageSize),
                ParseCurrentIngestAttentionInt(query, "pageNumber", 1),
                Kinds: ReadSet(query, "kind"),
                Severities: ReadSet(query, "severity"))
            .NormalizeAndValidate();
    }

    private static int ParseCurrentIngestAttentionInt(
        IQueryCollection query,
        string name,
        int defaultValue)
    {
        var raw = ReadCurrentIngestAttentionSingle(query, name);
        if (raw is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            throw new CurrentIngestAttentionException(
                CurrentIngestAttentionErrorCodes.InvalidQuery,
                $"{name} must be an integer.");
        }

        return value;
    }

    private static string? ReadCurrentIngestAttentionSingle(
        IQueryCollection query,
        string name)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new CurrentIngestAttentionException(
                CurrentIngestAttentionErrorCodes.InvalidQuery,
                $"{name} may be supplied once.");
        }

        return values[0];
    }

    private static IResult ToCurrentIngestAttentionError(
        CurrentIngestAttentionException exception)
    {
        var error = new NewMesIngestErrorDto(exception.Code, exception.Message);
        return exception.Code == CurrentIngestAttentionErrorCodes.ProjectionNotAvailable
            ? Results.Conflict(error)
            : Results.BadRequest(error);
    }

    private static async Task<IResult> GetWatchOverviewAsync(
        HttpRequest request,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = ParseWatchOverviewQuery(request.Query);
            var snapshot = await projection.ReadWatchOverviewAsync(query, cancellationToken);
            return Results.Ok(WatchOverviewDto.From(snapshot));
        }
        catch (WatchOverviewException exception)
        {
            return ToWatchOverviewError(exception);
        }
    }

    private static WatchOverviewQuery ParseWatchOverviewQuery(IQueryCollection query)
    {
        var unsupported = query.Keys.FirstOrDefault(
            key => !string.Equals(key, "area", StringComparison.Ordinal));
        if (unsupported is not null)
        {
            throw new WatchOverviewException(
                WatchOverviewErrorCodes.InvalidQuery,
                $"Unsupported watch-overview query parameter '{unsupported}'.");
        }

        if (query.TryGetValue("area", out var rawAreas)
            && (rawAreas.Count == 0
                || rawAreas.Any(raw => string.IsNullOrWhiteSpace(raw)
                    || raw!.Split(',', StringSplitOptions.None)
                        .Any(value => string.IsNullOrWhiteSpace(value)))))
        {
            throw new WatchOverviewException(
                WatchOverviewErrorCodes.InvalidQuery,
                "AREA values cannot be empty.");
        }

        return new WatchOverviewQuery(ReadSet(query, "area")).NormalizeAndValidate();
    }

    private static IResult ToWatchOverviewError(WatchOverviewException exception)
    {
        var error = new NewMesIngestErrorDto(exception.Code, exception.Message);
        return exception.Code == WatchOverviewErrorCodes.ProjectionNotAvailable
            ? Results.Conflict(error)
            : Results.BadRequest(error);
    }

    private static async Task<Results<
        Ok<PollTraceDto>,
        BadRequest<NewMesIngestErrorDto>,
        NotFound<NewMesIngestErrorDto>>> GetPollTraceAsync(
        string pollTraceId,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(
                pollTraceId,
                128,
                nameof(pollTraceId),
                PollEvidenceErrorCodes.InvalidPollTraceId,
                out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var snapshot = await projection.GetPollTraceAsync(
            pollTraceId,
            cancellationToken);
        return snapshot is null
            ? TypedResults.NotFound(new NewMesIngestErrorDto(
                PollEvidenceErrorCodes.PollTraceNotFound,
                "The requested PollTrace was not found."))
            : TypedResults.Ok(PollTraceDto.From(snapshot));
    }

    private static async Task<Ok<AbsenceAuthorityDto>> GetAbsenceAuthorityAsync(
        IMesIngestProjection projection,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(AbsenceAuthorityDto.From(
            await projection.GetAbsenceAuthorityAsync(cancellationToken)));

    private static async Task<Results<
        Ok<AbsenceAuthorityDto>,
        BadRequest<NewMesIngestErrorDto>,
        NotFound<NewMesIngestErrorDto>>>
        GetAbsenceAuthorityByHostSessionIdAsync(
            string hostSessionId,
            IMesIngestProjection projection,
            CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(
                hostSessionId,
                64,
                nameof(hostSessionId),
                PollEvidenceErrorCodes.InvalidHostSessionId,
                out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var snapshot = await projection.GetAbsenceAuthorityAsync(
            hostSessionId,
            cancellationToken);
        return snapshot is null
            ? TypedResults.NotFound(new NewMesIngestErrorDto(
                PollEvidenceErrorCodes.AbsenceAuthorityNotFound,
                "AbsenceAuthority for the requested HostSession was not found."))
            : TypedResults.Ok(AbsenceAuthorityDto.From(snapshot));
    }

    private static async Task<Ok<TaskTypeProtectionListDto>> ListTaskTypeProtectionsAsync(
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        var snapshots = await projection.ListTaskTypeProtectionsAsync(cancellationToken);
        var items = snapshots
            .OrderBy(snapshot => snapshot.WorkType, StringComparer.Ordinal)
            .Select(TaskTypeProtectionDto.From)
            .ToArray();

        return TypedResults.Ok(new TaskTypeProtectionListDto(items.Length, items));
    }

    private static async Task<Results<
        Ok<TaskTypeProtectionDto>,
        BadRequest<NewMesIngestErrorDto>,
        NotFound<NewMesIngestErrorDto>>>
        GetTaskTypeProtectionAsync(
            string workType,
            IMesIngestProjection projection,
            CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(
                workType,
                128,
                nameof(workType),
                PollEvidenceErrorCodes.InvalidWorkType,
                out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var snapshot = await projection.GetTaskTypeProtectionAsync(workType, cancellationToken);
        return snapshot is null
            ? TypedResults.NotFound(new NewMesIngestErrorDto(
                PollEvidenceErrorCodes.TaskTypeProtectionNotFound,
                "TaskTypeProtection for the requested WorkType was not found."))
            : TypedResults.Ok(TaskTypeProtectionDto.From(snapshot));
    }

    private static bool TryValidateRequiredText(
        string value,
        int maximumLength,
        string field,
        string errorCode,
        out NewMesIngestErrorDto error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = new NewMesIngestErrorDto(
                errorCode,
                $"{field} must contain a non-whitespace value.");
            return false;
        }
        if (value.Length > maximumLength)
        {
            error = new NewMesIngestErrorDto(
                errorCode,
                $"{field} must not exceed {maximumLength} characters.");
            return false;
        }

        error = null!;
        return true;
    }

}

internal sealed record ParsedErrorSearchRawEvidenceRequest(
    string SnapshotReference,
    ErrorSearchRawEvidenceQuery Query);

internal sealed record NewMesIngestErrorDto(string Code, string Error);

internal sealed record OperationalSnapshotIdentityDto(
    string HistoryEpoch,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    long PollTraceHighWater,
    long CatalogRevision,
    DateTimeOffset SnapshotAsOf,
    string ContractVersion)
{
    public static OperationalSnapshotIdentityDto From(OperationalSnapshotIdentity snapshot) =>
        new(
            (snapshot.HistoryEpoch ?? throw new InvalidOperationException(
                "The operational snapshot is missing HistoryEpoch.")).Value.ToString("D"),
            snapshot.ProjectionCommitId,
            snapshot.ProjectionSequence,
            snapshot.ProjectionCommittedAt,
            snapshot.PollTraceId,
            snapshot.PollTraceHighWater,
            snapshot.CatalogRevision,
            snapshot.SnapshotAsOf,
            snapshot.ContractVersion);
}

internal sealed record OverviewNavigationIntentDto(
    string Target,
    int PageNumber,
    IReadOnlyList<string>? MesAreas,
    IReadOnlyList<string>? Lifecycles,
    IReadOnlyList<string>? CurrentPresences,
    IReadOnlyList<string>? ReadabilityStates,
    IReadOnlyList<string>? ErrorActivityStates,
    string? ErrorWindow,
    IReadOnlyList<string>? AttentionKinds,
    IReadOnlyList<string>? AttentionSeverities,
    string? SeriesId,
    string? WorkType,
    string? PollTraceId,
    string? Cursor)
{
    public static OverviewNavigationIntentDto From(OverviewNavigationIntent intent) =>
        new(
            intent.Target,
            intent.PageNumber,
            intent.MesAreas,
            intent.Lifecycles,
            intent.CurrentPresences,
            intent.ReadabilityStates,
            intent.ErrorActivityStates,
            intent.ErrorWindow,
            intent.AttentionKinds,
            intent.AttentionSeverities,
            intent.SeriesId,
            intent.WorkType,
            intent.PollTraceId,
            intent.Cursor);
}

internal sealed record CurrentIngestAttentionFacetDto(
    string Value,
    long ItemCount)
{
    public static CurrentIngestAttentionFacetDto From(
        CurrentIngestAttentionFacetSnapshot facet) =>
        new(facet.Value, facet.ItemCount);
}

internal sealed record CurrentIngestAttentionFacetsDto(
    IReadOnlyList<CurrentIngestAttentionFacetDto> Types,
    IReadOnlyList<CurrentIngestAttentionFacetDto> Severities)
{
    public static CurrentIngestAttentionFacetsDto From(CurrentIngestAttentionFacets facets) =>
        new(
            facets.Types.Select(CurrentIngestAttentionFacetDto.From).ToArray(),
            facets.Severities.Select(CurrentIngestAttentionFacetDto.From).ToArray());
}

internal sealed record CurrentIngestAttentionEvidenceDto(
    string? ProjectionCommitId,
    long? ProjectionSequence,
    string? PollTraceId,
    long? PollTraceSequence,
    string? SeriesId,
    string? DemandId,
    string? WorkType,
    int? ObservationOrdinal,
    string? EvidenceId,
    string? ContentDigest,
    string? Phase,
    string? Outcome,
    int? ObservationCount)
{
    public static CurrentIngestAttentionEvidenceDto From(
        CurrentIngestAttentionEvidenceSnapshot evidence) =>
        new(
            evidence.ProjectionCommitId,
            evidence.ProjectionSequence,
            evidence.PollTraceId,
            evidence.PollTraceSequence,
            evidence.SeriesId,
            evidence.DemandId,
            evidence.WorkType,
            evidence.ObservationOrdinal,
            evidence.EvidenceId,
            evidence.ContentDigest,
            evidence.Phase,
            evidence.Outcome,
            evidence.ObservationCount);
}

internal sealed record CurrentIngestAttentionItemDto(
    string Kind,
    string Severity,
    DateTimeOffset OccurredAt,
    string StableIdentity,
    string? SeriesId,
    string? WorkType,
    string? ErrorCode,
    string? Target,
    string? SubjectKind,
    CurrentIngestAttentionEvidenceDto Evidence,
    OverviewNavigationIntentDto Navigation)
{
    public static CurrentIngestAttentionItemDto From(CurrentIngestAttentionItemSnapshot item) =>
        new(
            item.Kind,
            item.Severity,
            item.OccurredAt,
            item.StableIdentity,
            item.SeriesId,
            item.WorkType,
            item.ErrorCode,
            item.Target,
            item.SubjectKind,
            CurrentIngestAttentionEvidenceDto.From(item.Evidence),
            OverviewNavigationIntentDto.From(item.Navigation));
}

internal sealed record CurrentIngestAttentionDto(
    OperationalSnapshotIdentityDto Snapshot,
    long ExactTotalItemCount,
    CurrentIngestAttentionFacetsDto Facets,
    string Order,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Severities,
    IReadOnlyList<CurrentIngestAttentionItemDto> Items)
{
    public static CurrentIngestAttentionDto From(CurrentIngestAttentionSnapshot snapshot) =>
        new(
            OperationalSnapshotIdentityDto.From(snapshot.Snapshot),
            snapshot.ExactTotalItemCount,
            CurrentIngestAttentionFacetsDto.From(snapshot.Facets),
            snapshot.Order,
            snapshot.PageSize,
            snapshot.PageNumber,
            snapshot.TotalPages,
            snapshot.Kinds,
            snapshot.Severities,
            snapshot.Items.Select(CurrentIngestAttentionItemDto.From).ToArray());
}

internal sealed record WatchOverviewFacetDto(
    string Value,
    long Count,
    OverviewNavigationIntentDto Navigation)
{
    public static WatchOverviewFacetDto From(OverviewFacetSnapshot facet) =>
        new(facet.Value, facet.Count, OverviewNavigationIntentDto.From(facet.Navigation));
}

internal sealed record WatchOverviewSeriesSummaryDto(
    long ExactTotalSeriesCount,
    long TrackingCount,
    long ArchivedCount,
    long GoneCount,
    long LongGoneButVisibleCount,
    OverviewNavigationIntentDto Navigation,
    OverviewNavigationIntentDto TrackingNavigation,
    OverviewNavigationIntentDto ArchivedNavigation,
    OverviewNavigationIntentDto GoneNavigation,
    OverviewNavigationIntentDto LongGoneButVisibleNavigation)
{
    public static WatchOverviewSeriesSummaryDto From(WatchOverviewSeriesSummary summary) =>
        new(
            summary.ExactTotalSeriesCount,
            summary.TrackingCount,
            summary.ArchivedCount,
            summary.GoneCount,
            summary.LongGoneButVisibleCount,
            OverviewNavigationIntentDto.From(summary.Navigation),
            OverviewNavigationIntentDto.From(summary.TrackingNavigation),
            OverviewNavigationIntentDto.From(summary.ArchivedNavigation),
            OverviewNavigationIntentDto.From(summary.GoneNavigation),
            OverviewNavigationIntentDto.From(summary.LongGoneButVisibleNavigation));
}

internal sealed record WatchOverviewReadabilitySummaryDto(
    long ExactTotalDemandGenerationCount,
    long ReadableCount,
    long NotReadableCount,
    OverviewNavigationIntentDto Navigation,
    OverviewNavigationIntentDto ReadableNavigation,
    OverviewNavigationIntentDto NotReadableNavigation)
{
    public static WatchOverviewReadabilitySummaryDto From(
        WatchOverviewReadabilitySummary summary) =>
        new(
            summary.ExactTotalDemandGenerationCount,
            summary.ReadableCount,
            summary.NotReadableCount,
            OverviewNavigationIntentDto.From(summary.Navigation),
            OverviewNavigationIntentDto.From(summary.ReadableNavigation),
            OverviewNavigationIntentDto.From(summary.NotReadableNavigation));
}

internal sealed record WatchOverviewErrorSummaryDto(
    long ActiveSeriesCount,
    long Prior7DaysSeriesCount,
    OverviewNavigationIntentDto Navigation,
    OverviewNavigationIntentDto ActiveNavigation,
    OverviewNavigationIntentDto Prior7DaysNavigation)
{
    public static WatchOverviewErrorSummaryDto From(WatchOverviewErrorSummary summary) =>
        new(
            summary.ActiveSeriesCount,
            summary.Prior7DaysSeriesCount,
            OverviewNavigationIntentDto.From(summary.Navigation),
            OverviewNavigationIntentDto.From(summary.ActiveNavigation),
            OverviewNavigationIntentDto.From(summary.Prior7DaysNavigation));
}

internal sealed record WatchOverviewAttentionSummaryDto(
    long ExactTotalItemCount,
    IReadOnlyList<WatchOverviewFacetDto> Types,
    IReadOnlyList<WatchOverviewFacetDto> Severities,
    OverviewNavigationIntentDto Navigation)
{
    public static WatchOverviewAttentionSummaryDto From(
        WatchOverviewAttentionSummary summary) =>
        new(
            summary.ExactTotalItemCount,
            summary.Types.Select(WatchOverviewFacetDto.From).ToArray(),
            summary.Severities.Select(WatchOverviewFacetDto.From).ToArray(),
            OverviewNavigationIntentDto.From(summary.Navigation));
}

internal sealed record WatchOverviewActivityDto(
    string EventId,
    string Kind,
    string EventType,
    string Severity,
    DateTimeOffset OccurredAt,
    string? SeriesId,
    string? WorkType,
    string? PollTraceId,
    string? ProjectionCommitId,
    OverviewNavigationIntentDto Navigation)
{
    public static WatchOverviewActivityDto From(WatchOverviewActivitySnapshot activity) =>
        new(
            activity.EventId,
            activity.Kind,
            activity.EventType,
            activity.Severity,
            activity.OccurredAt,
            activity.SeriesId,
            activity.WorkType,
            activity.PollTraceId,
            activity.ProjectionCommitId,
            OverviewNavigationIntentDto.From(activity.Navigation));
}

internal sealed record WatchOverviewDto(
    OperationalSnapshotIdentityDto Snapshot,
    IReadOnlyList<string> MesAreas,
    WatchOverviewSeriesSummaryDto Series,
    WatchOverviewReadabilitySummaryDto Readability,
    WatchOverviewErrorSummaryDto Errors,
    WatchOverviewAttentionSummaryDto Attention,
    IReadOnlyList<WatchOverviewActivityDto> RecentActivity,
    string RecentActivityState,
    string? EmptyStateMessage)
{
    public static WatchOverviewDto From(WatchOverviewSnapshot snapshot) =>
        new(
            OperationalSnapshotIdentityDto.From(snapshot.Snapshot),
            snapshot.MesAreas,
            WatchOverviewSeriesSummaryDto.From(snapshot.Series),
            WatchOverviewReadabilitySummaryDto.From(snapshot.Readability),
            WatchOverviewErrorSummaryDto.From(snapshot.Errors),
            WatchOverviewAttentionSummaryDto.From(snapshot.Attention),
            snapshot.RecentActivity.Select(WatchOverviewActivityDto.From).ToArray(),
            snapshot.RecentActivityState,
            snapshot.EmptyStateMessage);
}

internal sealed record NewMesIngestContractDto(
    string ContractVersion,
    int SchemaVersion,
    string CompatibilityPolicy,
    string OpenApiDocument,
    string BusinessSurface,
    string LegacySurfacePolicy,
    string TransportDemandKeyComparison,
    IReadOnlyList<NewMesIngestCapabilityDto> Capabilities,
    IReadOnlyList<SeriesErrorDefinitionDto> SeriesErrorCatalog,
    IReadOnlyList<ReadabilityBlockerDefinitionDto> ReadabilityBlockerCatalog,
    IReadOnlyList<ReadabilityQualificationCheckDefinitionDto> ReadabilityQualificationCheckCatalog);

internal sealed record NewMesIngestOperationDto(string Method, string Path)
{
    public static NewMesIngestOperationDto From(NewMesIngestOperation operation) =>
        new(operation.Method, operation.Path);
}

internal sealed record NewMesIngestCapabilityDto(
    string Id,
    string Version,
    IReadOnlyList<NewMesIngestOperationDto> Operations)
{
    public static NewMesIngestCapabilityDto From(NewMesIngestCapability capability) =>
        new(
            capability.Id,
            capability.Version,
            capability.Operations.Select(NewMesIngestOperationDto.From).ToArray());
}

internal sealed record TransportDemandKeyDto(string WorkType, string Sublot);

internal sealed record ExternallyReadableDemandDto(
    string DemandId,
    string SeriesId,
    TransportDemandKeyDto TransportDemandKey,
    int Generation,
    long DemandRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset ValueObservedAt,
    string ValuePollTraceId,
    string ValueProjectionCommitId,
    LiveMesFieldSetDto LiveMesFields)
{
    public static ExternallyReadableDemandDto From(ExternallyReadableDemandSnapshot snapshot) =>
        new(
            snapshot.DemandId,
            snapshot.SeriesId,
            new TransportDemandKeyDto(snapshot.WorkType, snapshot.Sublot),
            snapshot.Generation,
            snapshot.DemandRevision,
            snapshot.CreatedAt,
            snapshot.ValueObservedAt,
            snapshot.ValuePollTraceId,
            snapshot.ValueProjectionCommitId,
            LiveMesFieldSetDto.From(snapshot.LiveMesFields));
}

internal sealed record ExternallyReadableDemandCatalogDto(
    string ContractVersion,
    string HistoryEpoch,
    long CatalogRevision,
    string? ProjectionCommitId,
    long? ProjectionSequence,
    DateTimeOffset? ProjectionCommittedAt,
    int Count,
    IReadOnlyList<ExternallyReadableDemandDto> Items)
{
    public static ExternallyReadableDemandCatalogDto From(
        ExternallyReadableDemandCatalogSnapshot snapshot) =>
        new(
            NewMesIngestContract.Version,
            snapshot.HistoryEpoch.Value.ToString("D"),
            snapshot.CatalogRevision,
            snapshot.ProjectionCommitId,
            snapshot.ProjectionSequence,
            snapshot.ProjectionCommittedAt,
            snapshot.Items.Count,
            snapshot.Items.Select(ExternallyReadableDemandDto.From).ToArray());
}

internal sealed record SeriesErrorDefinitionDto(
    string Code,
    string Category,
    string Severity,
    string Scope,
    string Meaning)
{
    public static SeriesErrorDefinitionDto From(SeriesErrorDefinition definition) =>
        new(definition.Code, definition.Category, definition.Severity, definition.Scope, definition.Meaning);
}

internal sealed record ReadabilityBlockerDefinitionDto(
    string Code,
    int Priority,
    string Meaning)
{
    public static ReadabilityBlockerDefinitionDto From(ReadabilityBlockerDefinition definition) =>
        new(definition.Code, definition.Priority, definition.Meaning);
}

internal sealed record ReadabilityQualificationCheckDefinitionDto(
    string Code,
    string BlockingCode,
    string Meaning)
{
    public static ReadabilityQualificationCheckDefinitionDto From(
        ReadabilityQualificationCheckDefinition definition) =>
        new(definition.Code, definition.BlockingCode, definition.Meaning);
}

internal sealed record ReadabilityAuditSnapshotIdentityDto(
    string HistoryEpoch,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    long CatalogRevision,
    string ContractVersion)
{
    public static ReadabilityAuditSnapshotIdentityDto From(ReadabilityAuditSnapshotIdentity snapshot) =>
        new(
            snapshot.HistoryEpoch.ToString(),
            snapshot.ProjectionCommitId,
            snapshot.ProjectionSequence,
            snapshot.ProjectionCommittedAt,
            snapshot.PollTraceId,
            snapshot.CatalogRevision,
            snapshot.ContractVersion);
}

internal sealed record ReadabilityAuditFilterDto(
    IReadOnlyList<string> ReadabilityStates,
    IReadOnlyList<string> WorkTypes,
    IReadOnlyList<string> Blockers,
    string? DemandId,
    string? SublotContains,
    IReadOnlyList<string> MesAreas)
{
    public static ReadabilityAuditFilterDto From(ReadabilityAuditFilter snapshot) =>
        new(
            snapshot.ReadabilityStates,
            snapshot.WorkTypes,
            snapshot.Blockers,
            snapshot.DemandId,
            snapshot.SublotContains,
            snapshot.MesAreas);
}

internal sealed record ReadabilityStateFacetDto(string State, long DemandCount);

internal sealed record ReadabilityBlockerFacetDto(string Code, long DemandCount);

internal sealed record ReadabilityAuditFacetsDto(
    IReadOnlyList<ReadabilityStateFacetDto> ReadabilityStates,
    IReadOnlyList<ReadabilityBlockerFacetDto> Blockers)
{
    public static ReadabilityAuditFacetsDto From(ReadabilityAuditFacets snapshot) =>
        new(
            snapshot.ReadabilityStates
                .Select(facet => new ReadabilityStateFacetDto(facet.State, facet.DemandCount))
                .ToArray(),
            snapshot.Blockers
                .Select(facet => new ReadabilityBlockerFacetDto(facet.Code, facet.DemandCount))
                .ToArray());
}

internal sealed record ReadabilityAuditListItemDto(
    string DemandId,
    string SeriesId,
    TransportDemandKeyDto TransportDemandKey,
    int Generation,
    string? PredecessorDemandId,
    string DemandStatus,
    string SeriesLifecycle,
    string SeriesCurrentPresence,
    bool IsCurrentGeneration,
    DateTimeOffset DemandCreatedAt,
    DateTimeOffset DemandLastSeenAt,
    DateTimeOffset? GoneConfirmedAt,
    LiveMesFieldSetDto? LiveMesFields,
    int CurrentRawObservationCount,
    string ExternalReadabilityState,
    string? LeadReadabilityBlocker,
    IReadOnlyList<string> ReadabilityBlockers,
    string LatestObservationPollTraceId,
    string LatestObservationProjectionCommitId,
    DateTimeOffset LatestObservationAt)
{
    public static ReadabilityAuditListItemDto From(ReadabilityAuditListItemSnapshot snapshot) =>
        new(
            snapshot.DemandId,
            snapshot.SeriesId,
            new TransportDemandKeyDto(snapshot.WorkType, snapshot.Sublot),
            snapshot.Generation,
            snapshot.PredecessorDemandId,
            snapshot.DemandStatus,
            snapshot.SeriesLifecycle,
            snapshot.SeriesCurrentPresence,
            snapshot.IsCurrentGeneration,
            snapshot.DemandCreatedAt,
            snapshot.DemandLastSeenAt,
            snapshot.GoneConfirmedAt,
            snapshot.LiveMesFields is null ? null : LiveMesFieldSetDto.From(snapshot.LiveMesFields),
            snapshot.CurrentRawObservationCount,
            snapshot.ExternalReadabilityState,
            snapshot.LeadReadabilityBlocker,
            snapshot.ReadabilityBlockers,
            snapshot.LatestObservationPollTraceId,
            snapshot.LatestObservationProjectionCommitId,
            snapshot.LatestObservationAt);
}

internal sealed record ReadabilityAuditListDto(
    string SnapshotReference,
    ReadabilityAuditSnapshotIdentityDto Snapshot,
    ReadabilityAuditFilterDto Filter,
    long ExactTotalDemandCount,
    ReadabilityAuditFacetsDto Facets,
    string Order,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<ReadabilityAuditListItemDto> Items,
    string? NextCursor,
    bool HasMore)
{
    public static ReadabilityAuditListDto From(ReadabilityAuditListSnapshot snapshot) =>
        new(
            snapshot.SnapshotReference,
            ReadabilityAuditSnapshotIdentityDto.From(snapshot.Snapshot),
            ReadabilityAuditFilterDto.From(snapshot.Filter),
            snapshot.ExactTotalDemandCount,
            ReadabilityAuditFacetsDto.From(snapshot.Facets),
            snapshot.Order,
            snapshot.PageSize,
            snapshot.PageNumber,
            snapshot.TotalPages,
            snapshot.Items.Select(ReadabilityAuditListItemDto.From).ToArray(),
            snapshot.NextCursor,
            snapshot.HasMore);
}

internal sealed record ReadabilityAuditSeriesDto(
    string SeriesId,
    TransportDemandKeyDto TransportDemandKey,
    string Lifecycle,
    string CurrentPresence,
    DateTimeOffset StartedAt,
    DateTimeOffset? ArchivedAt,
    string CurrentDemandId)
{
    public static ReadabilityAuditSeriesDto From(ReadabilityAuditSeriesSnapshot snapshot) =>
        new(
            snapshot.SeriesId,
            new TransportDemandKeyDto(snapshot.WorkType, snapshot.Sublot),
            snapshot.Lifecycle,
            snapshot.CurrentPresence,
            snapshot.StartedAt,
            snapshot.ArchivedAt,
            snapshot.CurrentDemandId);
}

internal sealed record ReadabilityQualificationCheckDto(
    string Code,
    string BlockingCode,
    string Result);

internal sealed record ReadabilityEvidenceItemDto(
    string SubjectKind,
    string? ObservedValue,
    string ExpectedRule,
    DateTimeOffset ObservedAt,
    string PollTraceId,
    string ProjectionCommitId);

internal sealed record ReadabilityBlockerEvidenceDto(
    string Code,
    int Priority,
    IReadOnlyList<ReadabilityEvidenceItemDto> Evidence)
{
    public static ReadabilityBlockerEvidenceDto From(ReadabilityBlockerEvidenceSnapshot snapshot) =>
        new(
            snapshot.Code,
            snapshot.Priority,
            snapshot.Evidence.Select(item => new ReadabilityEvidenceItemDto(
                item.SubjectKind,
                item.ObservedValue,
                item.ExpectedRule,
                item.ObservedAt,
                item.PollTraceId,
                item.ProjectionCommitId)).ToArray());
}

internal sealed record ReadabilityAuditPollTraceDto(
    string PollTraceId,
    string QueryVersion,
    string Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int RowCount,
    string ContentDigest,
    string ProjectionCommitId,
    long ProjectionSequence)
{
    public static ReadabilityAuditPollTraceDto From(ReadabilityAuditPollTraceSnapshot snapshot) =>
        new(
            snapshot.PollTraceId,
            snapshot.QueryVersion,
            snapshot.Outcome,
            snapshot.StartedAt,
            snapshot.CompletedAt,
            snapshot.RowCount,
            snapshot.ContentDigest,
            snapshot.ProjectionCommitId,
            snapshot.ProjectionSequence);
}

internal sealed record ReadabilityAuditDetailDto(
    string SnapshotReference,
    ReadabilityAuditSnapshotIdentityDto Snapshot,
    ReadabilityAuditListItemDto Demand,
    ReadabilityAuditSeriesDto Series,
    IReadOnlyList<ReadabilityQualificationCheckDto> QualificationChecks,
    IReadOnlyList<ReadabilityBlockerEvidenceDto> Blockers,
    IReadOnlyList<DemandRawObservationDto> LatestRawObservations,
    ReadabilityAuditPollTraceDto LatestObservationPollTrace)
{
    public static ReadabilityAuditDetailDto From(ReadabilityAuditDetailSnapshot snapshot) =>
        new(
            snapshot.SnapshotReference,
            ReadabilityAuditSnapshotIdentityDto.From(snapshot.Snapshot),
            ReadabilityAuditListItemDto.From(snapshot.Demand),
            ReadabilityAuditSeriesDto.From(snapshot.Series),
            snapshot.QualificationChecks.Select(check => new ReadabilityQualificationCheckDto(
                check.Code,
                check.BlockingCode,
                check.Result)).ToArray(),
            snapshot.Blockers.Select(ReadabilityBlockerEvidenceDto.From).ToArray(),
            snapshot.LatestRawObservations.Select(DemandRawObservationDto.From).ToArray(),
            ReadabilityAuditPollTraceDto.From(snapshot.LatestObservationPollTrace));
}

internal sealed record ErrorSearchSnapshotIdentityDto(
    string HistoryEpoch,
    DateTimeOffset ErrorSearchAsOf,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    string ContractVersion)
{
    public static ErrorSearchSnapshotIdentityDto From(ErrorSearchSnapshotIdentity snapshot) =>
        new(
            snapshot.HistoryEpoch.Value.ToString("D"),
            snapshot.ErrorSearchAsOf,
            snapshot.ProjectionCommitId,
            snapshot.ProjectionSequence,
            snapshot.ProjectionCommittedAt,
            snapshot.PollTraceId,
            snapshot.ContractVersion);
}

internal sealed record ErrorSearchFilterDto(
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> ErrorCodes,
    IReadOnlyList<string> ActivityStates,
    string? SeriesId,
    string? DemandId,
    string? SublotContains)
{
    public static ErrorSearchFilterDto From(ErrorSearchFilter filter) =>
        new(
            filter.Categories,
            filter.ErrorCodes,
            filter.ActivityStates,
            filter.SeriesId,
            filter.DemandId,
            filter.SublotContains);
}

internal sealed record ErrorSearchWindowDto(
    string Kind,
    DateTimeOffset? FromUtc,
    DateTimeOffset ToUtc)
{
    public static ErrorSearchWindowDto From(ErrorSearchResolvedWindow window) =>
        new(window.Kind, window.FromUtc, window.ToUtc);
}

internal sealed record ErrorSearchCategoryFacetDto(string Category, long SeriesCount);

internal sealed record ErrorSearchActivityStateFacetDto(string State, long SeriesCount);

internal sealed record ErrorSearchFacetsDto(
    IReadOnlyList<ErrorSearchCategoryFacetDto> Categories,
    IReadOnlyList<ErrorSearchActivityStateFacetDto> ActivityStates)
{
    public static ErrorSearchFacetsDto From(ErrorSearchFacets facets) =>
        new(
            facets.Categories.Select(facet =>
                new ErrorSearchCategoryFacetDto(facet.Category, facet.SeriesCount)).ToArray(),
            facets.ActivityStates.Select(facet =>
                new ErrorSearchActivityStateFacetDto(facet.State, facet.SeriesCount)).ToArray());
}

internal sealed record ErrorSearchMatchedErrorDto(
    string Code,
    string Category,
    string Severity)
{
    public static ErrorSearchMatchedErrorDto From(ErrorSearchMatchedErrorSnapshot error) =>
        new(error.Code, error.Category, error.Severity);
}

internal sealed record ErrorSearchListItemDto(
    string SeriesId,
    TransportDemandKeyDto TransportDemandKey,
    string ActivityState,
    IReadOnlyList<ErrorSearchMatchedErrorDto> MatchedErrors,
    DateTimeOffset LatestMatchedEvidenceAt,
    int MatchedPeriodCount,
    int MatchedDemandGenerationCount,
    string? MesArea,
    string MesAreaAvailability)
{
    public static ErrorSearchListItemDto From(ErrorSearchListItemSnapshot item) =>
        new(
            item.SeriesId,
            new TransportDemandKeyDto(item.WorkType, item.Sublot),
            item.ActivityState,
            item.MatchedErrors.Select(ErrorSearchMatchedErrorDto.From).ToArray(),
            item.LatestMatchedEvidenceAt,
            item.MatchedPeriodCount,
            item.MatchedDemandGenerationCount,
            item.MesArea,
            item.MesAreaAvailability);
}

internal sealed record ErrorSearchListDto(
    string SnapshotReference,
    ErrorSearchSnapshotIdentityDto Snapshot,
    ErrorSearchFilterDto Filter,
    ErrorSearchWindowDto Window,
    string Order,
    long TotalSeriesCount,
    ErrorSearchFacetsDto Facets,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<ErrorSearchListItemDto> Items,
    string? NextCursor,
    bool HasMore)
{
    public static ErrorSearchListDto From(ErrorSearchListSnapshot snapshot) =>
        new(
            snapshot.SnapshotReference,
            ErrorSearchSnapshotIdentityDto.From(snapshot.Snapshot),
            ErrorSearchFilterDto.From(snapshot.Filter),
            ErrorSearchWindowDto.From(snapshot.Window),
            snapshot.Order,
            snapshot.TotalSeriesCount,
            ErrorSearchFacetsDto.From(snapshot.Facets),
            snapshot.PageSize,
            snapshot.PageNumber,
            snapshot.TotalPages,
            snapshot.Items.Select(ErrorSearchListItemDto.From).ToArray(),
            snapshot.NextCursor,
            snapshot.HasMore);
}

internal sealed record ErrorSearchDiagnosticValueDto(
    string Kind,
    string? ScalarValue,
    int? ObservationCount,
    string? Sha256Digest)
{
    public static ErrorSearchDiagnosticValueDto From(ErrorSearchDiagnosticValueSnapshot value) =>
        new(value.Kind, value.ScalarValue, value.ObservationCount, value.Sha256Digest);
}

internal sealed record ErrorSearchDetailEvidenceDto(
    string EvidenceId,
    string EvidenceKind,
    string SubjectKind,
    DateTimeOffset ObservedAt,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    IReadOnlyList<string> RelatedWorkTypes,
    ErrorSearchDiagnosticValueDto DiagnosticValue,
    string ExpectedRule,
    bool RawEvidenceAvailable)
{
    public static ErrorSearchDetailEvidenceDto From(ErrorSearchDetailEvidenceSnapshot evidence) =>
        new(
            evidence.EvidenceId,
            evidence.EvidenceKind,
            evidence.SubjectKind,
            evidence.ObservedAt,
            evidence.PollTraceId,
            evidence.ProjectionCommitId,
            evidence.DemandId,
            evidence.RelatedWorkTypes,
            ErrorSearchDiagnosticValueDto.From(evidence.DiagnosticValue),
            evidence.ExpectedRule,
            evidence.RawEvidenceAvailable);
}

internal sealed record ErrorSearchDetailPeriodDto(
    string PeriodId,
    string Code,
    string Category,
    string Severity,
    string Target,
    string SubjectKind,
    string StartReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    bool StartsBeforeWindow,
    bool EndsAfterWindow,
    bool ActiveAtAsOf,
    IReadOnlyList<ErrorSearchDetailEvidenceDto> Evidence)
{
    public static ErrorSearchDetailPeriodDto From(ErrorSearchDetailPeriodSnapshot period) =>
        new(
            period.PeriodId,
            period.Code,
            period.Category,
            period.Severity,
            period.Target,
            period.SubjectKind,
            period.StartReason,
            period.StartedAt,
            period.EndedAt,
            period.EndReason,
            period.StartsBeforeWindow,
            period.EndsAfterWindow,
            period.ActiveAtAsOf,
            period.Evidence.Select(ErrorSearchDetailEvidenceDto.From).ToArray());
}

internal sealed record ErrorSearchDetailDto(
    string SnapshotReference,
    ErrorSearchSnapshotIdentityDto Snapshot,
    ErrorSearchFilterDto Filter,
    ErrorSearchWindowDto Window,
    string Order,
    ErrorSearchListItemDto Series,
    IReadOnlyList<ErrorSearchDetailPeriodDto> Periods)
{
    public static ErrorSearchDetailDto From(ErrorSearchDetailSnapshot detail) =>
        new(
            detail.SnapshotReference,
            ErrorSearchSnapshotIdentityDto.From(detail.Snapshot),
            ErrorSearchFilterDto.From(detail.Filter),
            ErrorSearchWindowDto.From(detail.Window),
            detail.Order,
            ErrorSearchListItemDto.From(detail.Series),
            detail.Periods.Select(ErrorSearchDetailPeriodDto.From).ToArray());
}

internal sealed record ErrorSearchRawEvidenceLimitsDto(
    int MaxItems,
    int MaxItemBytes,
    int MaxTotalBytes)
{
    public static ErrorSearchRawEvidenceLimitsDto From(ErrorSearchRawEvidenceLimitsSnapshot limits) =>
        new(limits.MaxItems, limits.MaxItemBytes, limits.MaxTotalBytes);
}

internal sealed record ErrorSearchRawEvidenceItemDto(
    int Ordinal,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    DateTimeOffset ObservedAt,
    IReadOnlyDictionary<string, string?> Fields)
{
    public static ErrorSearchRawEvidenceItemDto From(ErrorSearchRawEvidenceItemSnapshot item) =>
        new(
            item.Ordinal,
            item.PollTraceId,
            item.ProjectionCommitId,
            item.DemandId,
            item.ObservedAt,
            item.Fields);
}

internal sealed record ErrorSearchRawEvidenceDto(
    string SnapshotReference,
    ErrorSearchSnapshotIdentityDto Snapshot,
    string SeriesId,
    string PeriodId,
    string EvidenceId,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    IReadOnlyList<string> IncludedFields,
    int ItemCount,
    ErrorSearchRawEvidenceLimitsDto Limits,
    int PayloadBytes,
    IReadOnlyList<ErrorSearchRawEvidenceItemDto> Items)
{
    public static ErrorSearchRawEvidenceDto From(ErrorSearchRawEvidenceSnapshot evidence) =>
        new(
            evidence.SnapshotReference,
            ErrorSearchSnapshotIdentityDto.From(evidence.Snapshot),
            evidence.SeriesId,
            evidence.PeriodId,
            evidence.EvidenceId,
            evidence.PollTraceId,
            evidence.ProjectionCommitId,
            evidence.DemandId,
            evidence.IncludedFields,
            evidence.ItemCount,
            ErrorSearchRawEvidenceLimitsDto.From(evidence.Limits),
            evidence.PayloadBytes,
            evidence.Items.Select(ErrorSearchRawEvidenceItemDto.From).ToArray());
}

internal sealed record ProjectionCommitDto(
    string ProjectionCommitId,
    long ProjectionSequence,
    string PollTraceId,
    DateTimeOffset CommittedAt,
    string HostSessionId,
    string RestartPhaseBefore,
    string RestartPhaseAfter,
    bool AbsenceAuthority,
    IReadOnlyList<TaskTypeProtectionDecisionDto> TaskTypeProtectionDecisions)
{
    public static ProjectionCommitDto From(ProjectionCommitSnapshot snapshot) =>
        new(
            snapshot.ProjectionCommitId,
            snapshot.ProjectionSequence,
            snapshot.PollTraceId,
            snapshot.CommittedAt,
            snapshot.HostSessionId,
            snapshot.RestartPhaseBefore,
            snapshot.RestartPhaseAfter,
            snapshot.AbsenceAuthority,
            snapshot.TaskTypeProtectionDecisions
                .OrderBy(decision => decision.WorkType, StringComparer.Ordinal)
                .Select(TaskTypeProtectionDecisionDto.From)
                .ToArray());
}

internal sealed record TaskTypeProtectionDecisionDto(
    string WorkType,
    string PhaseBefore,
    string PhaseAfter,
    int ObservedCount,
    int LastHealthyNonZeroCount,
    int RecoveryStreakBefore,
    int RecoveryStreakAfter,
    bool ProtectionAllowsAbsenceAuthority,
    bool EffectiveAbsenceAuthorityAvailable,
    IReadOnlyList<string> EventIds)
{
    public static TaskTypeProtectionDecisionDto From(TaskTypeProtectionDecisionSnapshot snapshot) =>
        new(
            snapshot.WorkType,
            snapshot.PhaseBefore,
            snapshot.PhaseAfter,
            snapshot.ObservedCount,
            snapshot.LastHealthyNonZeroCount,
            snapshot.RecoveryStreakBefore,
            snapshot.RecoveryStreakAfter,
            snapshot.ProtectionAllowsAbsenceAuthority,
            snapshot.EffectiveAbsenceAuthorityAvailable,
            snapshot.EventIds);
}

internal sealed record TaskTypeProtectionEventDto(
    string EventId,
    string EpisodeId,
    string WorkType,
    long WorkTypeSequence,
    string EventType,
    DateTimeOffset OccurredAt,
    string PollTraceId,
    string ProjectionCommitId,
    string PhaseBefore,
    string PhaseAfter,
    int ObservedCount,
    int LastHealthyNonZeroCount,
    int RecoveryStreak,
    int RequiredRecoveryStreak,
    int EnterThreshold)
{
    public static TaskTypeProtectionEventDto From(TaskTypeProtectionEventSnapshot snapshot) =>
        new(
            snapshot.EventId,
            snapshot.EpisodeId,
            snapshot.WorkType,
            snapshot.WorkTypeSequence,
            snapshot.EventType,
            snapshot.OccurredAt,
            snapshot.PollTraceId,
            snapshot.ProjectionCommitId,
            snapshot.PhaseBefore,
            snapshot.PhaseAfter,
            snapshot.ObservedCount,
            snapshot.LastHealthyNonZeroCount,
            snapshot.RecoveryStreak,
            snapshot.RequiredRecoveryStreak,
            snapshot.EnterThreshold);
}

internal sealed record TaskTypeProtectionDto(
    string WorkType,
    string Phase,
    bool IsCurrentAttention,
    int LastHealthyNonZeroCount,
    int LatestObservedCount,
    int RecoveryStreak,
    int RequiredRecoveryStreak,
    int EnterThreshold,
    string? EpisodeId,
    DateTimeOffset? EnteredAt,
    bool ProtectionAllowsAbsenceAuthority,
    bool EffectiveAbsenceAuthorityAvailable,
    string LatestPollTraceId,
    string LatestProjectionCommitId,
    IReadOnlyList<TaskTypeProtectionEventDto> Events)
{
    public static TaskTypeProtectionDto From(TaskTypeProtectionSnapshot snapshot) =>
        new(
            snapshot.WorkType,
            snapshot.Phase,
            snapshot.IsCurrentAttention,
            snapshot.LastHealthyNonZeroCount,
            snapshot.LatestObservedCount,
            snapshot.RecoveryStreak,
            snapshot.RequiredRecoveryStreak,
            snapshot.EnterThreshold,
            snapshot.EpisodeId,
            snapshot.EnteredAt,
            snapshot.ProtectionAllowsAbsenceAuthority,
            snapshot.EffectiveAbsenceAuthorityAvailable,
            snapshot.LatestPollTraceId,
            snapshot.LatestProjectionCommitId,
            snapshot.Events
                .OrderBy(item => item.WorkTypeSequence)
                .Select(TaskTypeProtectionEventDto.From)
                .ToArray());
}

internal sealed record TaskTypeProtectionListDto(
    int Total,
    IReadOnlyList<TaskTypeProtectionDto> Items);

internal sealed record AbsenceAuthorityEventDto(
    string EventId,
    string HostSessionId,
    string EventType,
    DateTimeOffset OccurredAt,
    string? PollTraceId,
    string? ProjectionCommitId,
    string PhaseBefore,
    string PhaseAfter)
{
    public static AbsenceAuthorityEventDto From(AbsenceAuthorityEventSnapshot snapshot) =>
        new(
            snapshot.EventId,
            snapshot.HostSessionId,
            snapshot.EventType,
            snapshot.OccurredAt,
            snapshot.PollTraceId,
            snapshot.ProjectionCommitId,
            snapshot.PhaseBefore,
            snapshot.PhaseAfter);
}

internal sealed record AbsenceAuthorityDto(
    string HostSessionId,
    DateTimeOffset StartedAt,
    string Phase,
    bool IsCurrent,
    bool AbsenceAuthorityAvailable,
    IReadOnlyList<AbsenceAuthorityEventDto> Events)
{
    public static AbsenceAuthorityDto From(AbsenceAuthoritySnapshot snapshot) =>
        new(
            snapshot.HostSessionId,
            snapshot.StartedAt,
            snapshot.Phase,
            snapshot.IsCurrent,
            snapshot.AbsenceAuthorityAvailable,
            snapshot.Events.Select(AbsenceAuthorityEventDto.From).ToArray());
}

internal sealed record LiveMesFieldSetDto(
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package)
{
    public static LiveMesFieldSetDto From(LiveMesFieldSetSnapshot snapshot) =>
        new(snapshot.Area, snapshot.Eqp, snapshot.Step, snapshot.MesSourceDate, snapshot.Package);
}

internal sealed record TransportDemandV2Dto(
    string DemandId,
    string SeriesId,
    int Generation,
    string? PredecessorDemandId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset DemandLastSeenAt,
    DateTimeOffset? GoneConfirmedAt,
    string CreatedPollTraceId,
    string CreatedProjectionCommitId,
    string LatestProjectionCommitId,
    LiveMesFieldSetDto? LiveMesFields,
    string ExternalReadabilityState,
    IReadOnlyList<string> ReadabilityBlockers,
    string? LatestObservationPollTraceId,
    string? LatestObservationProjectionCommitId,
    DateTimeOffset? LatestObservationAt)
{
    public static TransportDemandV2Dto From(TransportDemandSnapshot snapshot) =>
        new(
            snapshot.DemandId,
            snapshot.SeriesId,
            snapshot.Generation,
            snapshot.PredecessorDemandId,
            snapshot.Status,
            snapshot.CreatedAt,
            snapshot.DemandLastSeenAt,
            snapshot.GoneConfirmedAt,
            snapshot.CreatedPollTraceId,
            snapshot.CreatedProjectionCommitId,
            snapshot.LatestProjectionCommitId,
            snapshot.LiveMesFields is null
                ? null
                : LiveMesFieldSetDto.From(snapshot.LiveMesFields),
            snapshot.ExternalReadabilityState,
            snapshot.ReadabilityBlockers,
            snapshot.LatestObservationPollTraceId,
            snapshot.LatestObservationProjectionCommitId,
            snapshot.LatestObservationAt);
}

internal sealed record DemandRawObservationDto(
    int Ordinal,
    string PollTraceId,
    string ProjectionCommitId,
    DateTimeOffset ObservedAt,
    string Assignment,
    string? SeriesId,
    string? DemandId,
    string? WorkType,
    string? Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package,
    string? MesSourceDateRaw)
{
    public static DemandRawObservationDto From(DemandRawObservationSnapshot snapshot) =>
        new(
            snapshot.Ordinal,
            snapshot.PollTraceId,
            snapshot.ProjectionCommitId,
            snapshot.ObservedAt,
            snapshot.Assignment switch
            {
                MesObservationAssignment.Assigned => "ASSIGNED",
                MesObservationAssignment.Unassigned => "UNASSIGNED",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(snapshot),
                    snapshot.Assignment,
                    "Unknown MES observation assignment."),
            },
            snapshot.SeriesId,
            snapshot.DemandId,
            snapshot.WorkType,
            snapshot.Sublot,
            snapshot.Area,
            snapshot.Eqp,
            snapshot.Step,
            snapshot.MesSourceDate,
            snapshot.Package,
            snapshot.MesSourceDateRaw);
}

internal sealed record MesTaskUnionRoundDiagnosticDto(
    string Stage,
    string Code,
    string SafeDetail)
{
    public static MesTaskUnionRoundDiagnosticDto From(MesTaskUnionRoundDiagnostic snapshot) =>
        new(snapshot.Stage, snapshot.Code, snapshot.SafeDetail);
}

internal sealed record DemandSeriesEventDto(
    string EventId,
    string SeriesId,
    long SeriesSequence,
    string EventType,
    DateTimeOffset OccurredAt,
    string SubjectKind,
    string? SubjectId,
    string PollTraceId,
    string ProjectionCommitId,
    int PayloadVersion,
    string PayloadJson)
{
    public static DemandSeriesEventDto From(DemandSeriesEventSnapshot snapshot) =>
        new(
            snapshot.EventId,
            snapshot.SeriesId,
            snapshot.SeriesSequence,
            snapshot.EventType,
            snapshot.OccurredAt,
            snapshot.SubjectKind,
            snapshot.SubjectId,
            snapshot.PollTraceId,
            snapshot.ProjectionCommitId,
            snapshot.PayloadVersion,
            snapshot.PayloadJson);
}

internal sealed record SeriesErrorPeriodEvidenceDto(
    string EvidenceId,
    string EvidenceKind,
    DateTimeOffset ObservedAt,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    string? ObservedValue,
    string ExpectedRule)
{
    public static SeriesErrorPeriodEvidenceDto From(SeriesErrorPeriodEvidenceSnapshot snapshot) =>
        new(
            snapshot.EvidenceId,
            snapshot.EvidenceKind,
            snapshot.ObservedAt,
            snapshot.PollTraceId,
            snapshot.ProjectionCommitId,
            snapshot.DemandId,
            snapshot.ObservedValue,
            snapshot.ExpectedRule);
}

internal sealed record DemandSeriesCurrentConditionDto(
    string PeriodId,
    string Code,
    string Category,
    string Severity,
    string Target,
    string SubjectKind,
    DateTimeOffset StartedAt,
    DateTimeOffset LatestEvidenceAt,
    string LatestPollTraceId,
    string LatestProjectionCommitId,
    string DemandId,
    string? ObservedValue,
    string ExpectedRule)
{
    public static DemandSeriesCurrentConditionDto From(DemandSeriesCurrentConditionSnapshot snapshot) =>
        new(
            snapshot.PeriodId,
            snapshot.Code,
            snapshot.Category,
            snapshot.Severity,
            snapshot.Target,
            snapshot.SubjectKind,
            snapshot.StartedAt,
            snapshot.LatestEvidenceAt,
            snapshot.LatestPollTraceId,
            snapshot.LatestProjectionCommitId,
            snapshot.DemandId,
            snapshot.ObservedValue,
            snapshot.ExpectedRule);
}

internal sealed record DemandSeriesErrorPeriodDto(
    string PeriodId,
    string Code,
    string Category,
    string Severity,
    string Target,
    string SubjectKind,
    string StartReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    IReadOnlyList<SeriesErrorPeriodEvidenceDto> Evidence)
{
    public static DemandSeriesErrorPeriodDto From(DemandSeriesErrorPeriodSnapshot snapshot) =>
        new(
            snapshot.PeriodId,
            snapshot.Code,
            snapshot.Category,
            snapshot.Severity,
            snapshot.Target,
            snapshot.SubjectKind,
            snapshot.StartReason,
            snapshot.StartedAt,
            snapshot.EndedAt,
            snapshot.EndReason,
            snapshot.Evidence.Select(SeriesErrorPeriodEvidenceDto.From).ToArray());
}

internal sealed record DemandSeriesDto(
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string CurrentPresence,
    DateTimeOffset StartedAt,
    DateTimeOffset? ArchivedAt,
    string CreatedPollTraceId,
    string CreatedProjectionCommitId,
    string LatestProjectionCommitId,
    long LastSeriesSequence,
    TransportDemandV2Dto CurrentDemand,
    IReadOnlyList<TransportDemandV2Dto> Demands,
    IReadOnlyList<DemandRawObservationDto> RawObservations,
    IReadOnlyList<DemandSeriesEventDto> Events,
    IReadOnlyList<DemandSeriesCurrentConditionDto> CurrentConditions,
    IReadOnlyList<DemandSeriesErrorPeriodDto> ErrorPeriods)
{
    public static DemandSeriesDto From(DemandSeriesSnapshot snapshot) =>
        new(
            snapshot.SeriesId,
            snapshot.WorkType,
            snapshot.Sublot,
            snapshot.Lifecycle,
            snapshot.CurrentPresence,
            snapshot.StartedAt,
            snapshot.ArchivedAt,
            snapshot.CreatedPollTraceId,
            snapshot.CreatedProjectionCommitId,
            snapshot.LatestProjectionCommitId,
            snapshot.LastSeriesSequence,
            TransportDemandV2Dto.From(snapshot.CurrentDemand),
            snapshot.Demands.Select(TransportDemandV2Dto.From).ToList(),
            snapshot.RawObservations.Select(DemandRawObservationDto.From).ToList(),
            snapshot.Events.Select(DemandSeriesEventDto.From).ToList(),
            snapshot.CurrentConditions.Select(DemandSeriesCurrentConditionDto.From).ToList(),
            snapshot.ErrorPeriods.Select(DemandSeriesErrorPeriodDto.From).ToList());
}

internal sealed record DemandSeriesSnapshotIdentityDto(
    string HistoryEpoch,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    string ContractVersion)
{
    public static DemandSeriesSnapshotIdentityDto From(
        DemandSeriesSnapshotIdentity snapshot) =>
        new(
            snapshot.HistoryEpoch.Value.ToString("D"),
            snapshot.ProjectionCommitId,
            snapshot.ProjectionSequence,
            snapshot.ProjectionCommittedAt,
            snapshot.PollTraceId,
            snapshot.ContractVersion);
}

internal sealed record DemandSeriesFacetsDto(
    long TrackingCount,
    long ArchivedCount,
    long VisibleCount,
    long GoneCount,
    long LongGoneButVisibleCount)
{
    public static DemandSeriesFacetsDto From(DemandSeriesFacets snapshot) =>
        new(
            snapshot.TrackingCount,
            snapshot.ArchivedCount,
            snapshot.VisibleCount,
            snapshot.GoneCount,
            snapshot.LongGoneButVisibleCount);
}

internal sealed record DemandSeriesListItemDto(
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string CurrentPresence,
    DateTimeOffset StartedAt,
    DateTimeOffset? ArchivedAt,
    string CurrentDemandId,
    int CurrentGeneration,
    string CurrentDemandStatus,
    DateTimeOffset DemandLastSeenAt,
    DateTimeOffset? GoneConfirmedAt,
    LiveMesFieldSetDto? LiveMesFields,
    string ExternalReadabilityState,
    IReadOnlyList<string> ReadabilityBlockers,
    long LastSeriesSequence,
    string LatestPollTraceId,
    string LatestProjectionCommitId)
{
    public static DemandSeriesListItemDto From(DemandSeriesListItemSnapshot snapshot) =>
        new(
            snapshot.SeriesId,
            snapshot.WorkType,
            snapshot.Sublot,
            snapshot.Lifecycle,
            snapshot.CurrentPresence,
            snapshot.StartedAt,
            snapshot.ArchivedAt,
            snapshot.CurrentDemandId,
            snapshot.CurrentGeneration,
            snapshot.CurrentDemandStatus,
            snapshot.DemandLastSeenAt,
            snapshot.GoneConfirmedAt,
            snapshot.LiveMesFields is null ? null : LiveMesFieldSetDto.From(snapshot.LiveMesFields),
            snapshot.ExternalReadabilityState,
            snapshot.ReadabilityBlockers,
            snapshot.LastSeriesSequence,
            snapshot.LatestPollTraceId,
            snapshot.LatestProjectionCommitId);
}

internal sealed record DemandSeriesListDto(
    string SnapshotReference,
    DemandSeriesSnapshotIdentityDto Snapshot,
    long ExactTotalCount,
    DemandSeriesFacetsDto Facets,
    string Order,
    int PageSize,
    int PageNumber,
    int TotalPages,
    IReadOnlyList<DemandSeriesListItemDto> Items,
    string? NextCursor,
    bool HasMore)
{
    public static DemandSeriesListDto From(DemandSeriesListSnapshot snapshot) =>
        new(
            snapshot.SnapshotReference,
            DemandSeriesSnapshotIdentityDto.From(snapshot.Snapshot),
            snapshot.ExactTotalCount,
            DemandSeriesFacetsDto.From(snapshot.Facets),
            snapshot.Order,
            snapshot.PageSize,
            snapshot.PageNumber,
            snapshot.TotalPages,
            snapshot.Items.Select(DemandSeriesListItemDto.From).ToArray(),
            snapshot.NextCursor,
            snapshot.HasMore);
}

internal sealed record FrozenDemandSeriesDto(
    string SnapshotReference,
    DemandSeriesSnapshotIdentityDto Snapshot,
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string CurrentPresence,
    DateTimeOffset StartedAt,
    DateTimeOffset? ArchivedAt,
    string CreatedPollTraceId,
    string CreatedProjectionCommitId,
    string LatestProjectionCommitId,
    long LastSeriesSequence,
    TransportDemandV2Dto CurrentDemand,
    IReadOnlyList<TransportDemandV2Dto> Demands,
    IReadOnlyList<DemandRawObservationDto> RawObservations,
    IReadOnlyList<DemandSeriesEventDto> Events,
    IReadOnlyList<DemandSeriesCurrentConditionDto> CurrentConditions,
    IReadOnlyList<DemandSeriesErrorPeriodDto> ErrorPeriods)
{
    public static FrozenDemandSeriesDto From(DemandSeriesDetailSnapshot detail)
    {
        var snapshot = detail.Series;
        return new(
            detail.SnapshotReference,
            DemandSeriesSnapshotIdentityDto.From(detail.Snapshot),
            snapshot.SeriesId,
            snapshot.WorkType,
            snapshot.Sublot,
            snapshot.Lifecycle,
            snapshot.CurrentPresence,
            snapshot.StartedAt,
            snapshot.ArchivedAt,
            snapshot.CreatedPollTraceId,
            snapshot.CreatedProjectionCommitId,
            snapshot.LatestProjectionCommitId,
            snapshot.LastSeriesSequence,
            TransportDemandV2Dto.From(snapshot.CurrentDemand),
            snapshot.Demands.Select(TransportDemandV2Dto.From).ToArray(),
            snapshot.RawObservations.Select(DemandRawObservationDto.From).ToArray(),
            snapshot.Events.Select(DemandSeriesEventDto.From).ToArray(),
            snapshot.CurrentConditions.Select(DemandSeriesCurrentConditionDto.From).ToArray(),
            snapshot.ErrorPeriods.Select(DemandSeriesErrorPeriodDto.From).ToArray());
    }
}

internal sealed record PollTraceDto(
    string PollTraceId,
    string QueryVersion,
    string Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int RowCount,
    string ContentDigest,
    ProjectionCommitDto? ProjectionCommit,
    IReadOnlyList<DemandRawObservationDto> Observations,
    MesTaskUnionRoundDiagnosticDto? Diagnostic)
{
    public static PollTraceDto From(PollTraceSnapshot snapshot) =>
        new(
            snapshot.PollTraceId,
            snapshot.QueryVersion,
            snapshot.Outcome,
            snapshot.StartedAt,
            snapshot.CompletedAt,
            snapshot.RowCount,
            snapshot.ContentDigest,
            snapshot.ProjectionCommit is null
                ? null
                : ProjectionCommitDto.From(snapshot.ProjectionCommit),
            snapshot.Observations.Select(DemandRawObservationDto.From).ToList(),
            snapshot.Diagnostic is null
                ? null
                : MesTaskUnionRoundDiagnosticDto.From(snapshot.Diagnostic));
}
