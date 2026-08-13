using MesIngest.Core.SeriesProjection;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MesIngest.Host;

internal static class NewMesIngestEndpoints
{
    public static IEndpointRouteBuilder MapNewMesIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v2/contract", GetContract)
            .ExcludeFromDescription();

        endpoints.MapGet("/api/v2/demand-series", ListDemandSeriesAsync)
            .ExcludeFromDescription();

        endpoints.MapGet("/api/v2/demand-series/by-key", GetDemandSeriesByKeyAsync)
            .ExcludeFromDescription();

        endpoints.MapGet("/api/v2/demand-series/{seriesId}", GetDemandSeriesAsync)
            .ExcludeFromDescription();

        endpoints.MapGet(
                "/api/v2/externally-readable-demand-catalog",
                GetExternallyReadableDemandCatalogAsync)
            .ExcludeFromDescription();

        endpoints.MapGet("/api/v2/readability-audit", ListReadabilityAuditAsync)
            .ExcludeFromDescription();

        endpoints.MapGet(
                "/api/v2/readability-audit/{demandId}",
                GetReadabilityAuditDetailAsync)
            .ExcludeFromDescription();

        endpoints.MapGet("/api/v2/error-search", ListErrorSearchAsync)
            .ExcludeFromDescription();

        endpoints.MapGet("/api/v2/poll-traces/{pollTraceId}", GetPollTraceAsync)
            .ExcludeFromDescription();

        endpoints.MapGet("/api/v2/absence-authority", GetAbsenceAuthorityAsync)
            .ExcludeFromDescription();

        endpoints.MapGet(
                "/api/v2/absence-authority/{hostSessionId}",
                GetAbsenceAuthorityByHostSessionIdAsync)
            .ExcludeFromDescription();

        endpoints.MapGet(
                "/api/v2/task-type-protections",
                ListTaskTypeProtectionsAsync)
            .ExcludeFromDescription();

        endpoints.MapGet(
                "/api/v2/task-type-protections/{workType}",
                GetTaskTypeProtectionAsync)
            .ExcludeFromDescription();

        return endpoints;
    }

    private static Ok<NewMesIngestContractDto> GetContract() =>
        TypedResults.Ok(new NewMesIngestContractDto(
            NewMesIngestContract.Version,
            NewMesIngestContract.SchemaVersion,
            NewMesIngestContract.KeyComparison,
            SeriesErrorCatalog.Definitions.Select(SeriesErrorDefinitionDto.From).ToArray(),
            ReadabilityBlockerCatalog.Definitions.Select(ReadabilityBlockerDefinitionDto.From).ToArray(),
            ReadabilityQualificationCheckCatalog.Definitions
                .Select(ReadabilityQualificationCheckDefinitionDto.From).ToArray()));

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
                out var knownRevision,
                out var conditionError))
        {
            return Results.BadRequest(new NewMesIngestErrorDto(
                "INVALID_CATALOG_CONDITION",
                conditionError!));
        }

        var read = await projection.ReadExternallyReadableDemandCatalogAsync(
            knownRevision,
            cancellationToken);
        var etag = CreateCatalogEtag(read.CatalogRevision);
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
        out long? knownRevision,
        out string? error)
    {
        knownRevision = null;
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
        const string prefix = "catalog-r";
        if (!opaque.StartsWith(prefix, StringComparison.Ordinal)
            || !long.TryParse(
                opaque.AsSpan(prefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < 0)
        {
            error = "If-None-Match is not a catalog ETag.";
            return false;
        }

        knownRevision = parsed;
        return true;
    }

    private static string CreateCatalogEtag(long catalogRevision) =>
        $"W/\"catalog-r{catalogRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"";

    private static async Task<IResult> GetDemandSeriesByKeyAsync(
        string workType,
        string sublot,
        HttpRequest request,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(workType, 128, nameof(workType), out var workTypeError))
        {
            return Results.BadRequest(workTypeError);
        }
        if (!TryValidateRequiredText(sublot, 256, nameof(sublot), out var sublotError))
        {
            return Results.BadRequest(sublotError);
        }

        try
        {
            var requestedSnapshot = ReadSingle(request.Query, "snapshot");
            if (requestedSnapshot is not null)
            {
                // Validate and resolve the snapshot before the mutable permanent-key
                // lookup. An invalid credential must never be masked as a key 404.
                await projection.ListDemandSeriesAsync(
                    new DemandSeriesBrowseQuery(
                        new DemandSeriesBrowseFilter(),
                        PageSize: 1,
                        SnapshotReference: requestedSnapshot),
                    cancellationToken);
            }

            var current = await projection.GetDemandSeriesByKeyAsync(
                workType,
                sublot,
                cancellationToken);
            if (current is null)
            {
                return Results.NotFound();
            }

            var list = await projection.ListDemandSeriesAsync(
                new DemandSeriesBrowseQuery(
                    new DemandSeriesBrowseFilter { SeriesId = current.SeriesId },
                    PageSize: 1,
                    SnapshotReference: requestedSnapshot),
                cancellationToken);
            if (list.Items.Count == 0)
            {
                return Results.NotFound();
            }

            var detail = await projection.GetDemandSeriesAtSnapshotAsync(
                current.SeriesId,
                list.SnapshotReference,
                cancellationToken);
            return detail is null
                ? Results.NotFound()
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
        if (!TryValidateRequiredText(seriesId, 64, nameof(seriesId), out var error))
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
                    return Results.NotFound();
                }

                snapshotReference = frozen.SnapshotReference;
            }

            var detail = await projection.GetDemandSeriesAtSnapshotAsync(
                seriesId,
                snapshotReference,
                cancellationToken);
            return detail is null
                ? Results.NotFound()
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
        if (!TryValidateRequiredText(demandId, 64, nameof(demandId), out var requestError))
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
                ? Results.NotFound()
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
            _ => Results.BadRequest(error),
        };
    }

    private static async Task<Results<Ok<PollTraceDto>, BadRequest<NewMesIngestErrorDto>, NotFound>> GetPollTraceAsync(
        string pollTraceId,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(pollTraceId, 128, nameof(pollTraceId), out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var snapshot = await projection.GetPollTraceAsync(
            pollTraceId,
            cancellationToken);
        return snapshot is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(PollTraceDto.From(snapshot));
    }

    private static async Task<Ok<AbsenceAuthorityDto>> GetAbsenceAuthorityAsync(
        IMesIngestProjection projection,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(AbsenceAuthorityDto.From(
            await projection.GetAbsenceAuthorityAsync(cancellationToken)));

    private static async Task<Results<Ok<AbsenceAuthorityDto>, BadRequest<NewMesIngestErrorDto>, NotFound>>
        GetAbsenceAuthorityByHostSessionIdAsync(
            string hostSessionId,
            IMesIngestProjection projection,
            CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(hostSessionId, 64, nameof(hostSessionId), out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var snapshot = await projection.GetAbsenceAuthorityAsync(
            hostSessionId,
            cancellationToken);
        return snapshot is null
            ? TypedResults.NotFound()
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

    private static async Task<Results<Ok<TaskTypeProtectionDto>, BadRequest<NewMesIngestErrorDto>, NotFound>>
        GetTaskTypeProtectionAsync(
            string workType,
            IMesIngestProjection projection,
            CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(workType, 128, nameof(workType), out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var snapshot = await projection.GetTaskTypeProtectionAsync(workType, cancellationToken);
        return snapshot is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(TaskTypeProtectionDto.From(snapshot));
    }

    private static bool TryValidateRequiredText(
        string value,
        int maximumLength,
        string field,
        out NewMesIngestErrorDto error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = new NewMesIngestErrorDto(
                "INVALID_REQUEST",
                $"{field} must contain a non-whitespace value.");
            return false;
        }
        if (value.Length > maximumLength)
        {
            error = new NewMesIngestErrorDto(
                "INVALID_REQUEST",
                $"{field} must not exceed {maximumLength} characters.");
            return false;
        }

        error = null!;
        return true;
    }

}

internal sealed record NewMesIngestErrorDto(string Code, string Error);

internal sealed record NewMesIngestContractDto(
    string ContractVersion,
    int SchemaVersion,
    string TransportDemandKeyComparison,
    IReadOnlyList<SeriesErrorDefinitionDto> SeriesErrorCatalog,
    IReadOnlyList<ReadabilityBlockerDefinitionDto> ReadabilityBlockerCatalog,
    IReadOnlyList<ReadabilityQualificationCheckDefinitionDto> ReadabilityQualificationCheckCatalog);

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
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    long CatalogRevision,
    string ContractVersion)
{
    public static ReadabilityAuditSnapshotIdentityDto From(ReadabilityAuditSnapshotIdentity snapshot) =>
        new(
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
    DateTimeOffset ErrorSearchAsOf,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    string ContractVersion)
{
    public static ErrorSearchSnapshotIdentityDto From(ErrorSearchSnapshotIdentity snapshot) =>
        new(
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
    string? Package)
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
            snapshot.Package);
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
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId,
    string ContractVersion)
{
    public static DemandSeriesSnapshotIdentityDto From(
        DemandSeriesSnapshotIdentity snapshot) =>
        new(
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
    IReadOnlyList<DemandRawObservationDto> Observations)
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
            snapshot.Observations.Select(DemandRawObservationDto.From).ToList());
}
