using System.Reflection;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MesIngest.Host;

internal sealed class NewMesIngestOpenApiDocumentFilter : IDocumentFilter
{
    private const string JsonMediaType = "application/json";

    private static readonly IReadOnlyDictionary<string, V2Operation> Operations =
        CreateOperations().ToDictionary(operation => operation.Path, StringComparer.Ordinal);

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        if (!string.Equals(
                document.Info.Version,
                NewMesIngestContract.Version,
                StringComparison.Ordinal))
        {
            return;
        }

        var expectedPaths = MesIngestOpenApi.V2ApiPaths.ToHashSet(StringComparer.Ordinal);
        var unexpected = document.Paths.Keys.Where(path => !expectedPaths.Contains(path)).ToArray();
        if (unexpected.Length != 0)
        {
            throw new InvalidOperationException(
                $"The V2 OpenAPI document contains unregistered paths: {string.Join(", ", unexpected)}.");
        }

        foreach (var path in expectedPaths)
        {
            if (!document.Paths.TryGetValue(path, out var pathItem)
                || pathItem.Operations is null
                || !pathItem.Operations.TryGetValue(OperationType.Get, out var operation))
            {
                throw new InvalidOperationException(
                    $"The frozen V2 GET operation '{path}' is missing from API Explorer.");
            }

            var contract = Operations[path];
            operation.OperationId = contract.OperationId;
            operation.Summary = contract.Summary;
            operation.Description = contract.Description.ReplaceLineEndings("\n");
            operation.Tags = [new OpenApiTag { Name = contract.Tag }];
            operation.Parameters = contract.Parameters.Select(ToOpenApiParameter).ToList();
            operation.Responses = CreateResponses(contract, context);
            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = MesIngestOpenApi.BearerSchemeId,
                        },
                    }] = Array.Empty<string>(),
                },
            ];
        }

        document.Tags = Operations.Values
            .Select(operation => operation.Tag)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(tag => new OpenApiTag { Name = tag })
            .ToList();
        document.Extensions["x-mes-series-error-catalog"] = SeriesErrorCatalogExtension();
    }

    private static OpenApiResponses CreateResponses(
        V2Operation operation,
        DocumentFilterContext context)
    {
        var responses = new OpenApiResponses
        {
            ["200"] = JsonResponse(
                "Success. Response identity and exact totals belong to the returned snapshot.",
                operation.ResponseType,
                context),
            ["401"] = ErrorResponse(
                "UNAUTHORIZED — Bearer SharedSecret is required for this network boundary.",
                context),
        };

        foreach (var (status, description) in operation.Errors)
        {
            responses[status] = operation.Path == "/api/v2/poll-traces/{pollTraceId}"
                && status is "404" or "410"
                    ? JsonResponse(description, typeof(HistoricalReadErrorDto), context)
                    : status == "410" && operation.SupportsHistoricalExpiration
                        ? HistoricalOrCapabilityErrorResponse(description, context)
                        : ErrorResponse(description, context);
        }

        if (!responses.ContainsKey("400"))
        {
            responses["400"] = ErrorResponse(
                "An unsupported query parameter or invalid path identity; the error body carries the capability-specific stable code.",
                context);
        }

        if (operation.Path == "/api/v2/externally-readable-demand-catalog")
        {
            responses["200"].Headers = CatalogHeaders();
            responses["304"] = new OpenApiResponse
            {
                Description =
                    "HistoryEpoch and CatalogRevision are unchanged. No response body; ETag identifies both.",
                Headers = CatalogHeaders(),
            };
        }

        return responses;
    }

    private static OpenApiResponse JsonResponse(
        string description,
        Type responseType,
        DocumentFilterContext context) =>
        new()
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                [JsonMediaType] = new()
                {
                    Schema = context.SchemaGenerator.GenerateSchema(
                        responseType,
                        context.SchemaRepository),
                },
            },
        };

    private static OpenApiResponse ErrorResponse(
        string description,
        DocumentFilterContext context) =>
        JsonResponse(description, typeof(NewMesIngestErrorDto), context);

    private static OpenApiResponse HistoricalOrCapabilityErrorResponse(
        string description,
        DocumentFilterContext context) =>
        new()
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                [JsonMediaType] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        OneOf =
                        [
                            context.SchemaGenerator.GenerateSchema(
                                typeof(NewMesIngestErrorDto),
                                context.SchemaRepository),
                            context.SchemaGenerator.GenerateSchema(
                                typeof(HistoricalReadErrorDto),
                                context.SchemaRepository),
                        ],
                    },
                },
            },
        };

    private static Dictionary<string, OpenApiHeader> CatalogHeaders() =>
        new(StringComparer.Ordinal)
        {
            ["ETag"] = new()
            {
                Description =
                    "Weak catalog validator W/\"catalog-h{HistoryEpoch:N}-r{CatalogRevision}\". This is not a paging cursor.",
                Schema = new OpenApiSchema { Type = "string" },
                Example = new OpenApiString("W/\"catalog-h11111111111111111111111111111111-r42\""),
            },
            ["Cache-Control"] = new()
            {
                Description = "Always private, no-cache.",
                Schema = new OpenApiSchema { Type = "string" },
                Example = new OpenApiString("private, no-cache"),
            },
        };

    private static OpenApiArray SeriesErrorCatalogExtension()
    {
        var catalog = new OpenApiArray();
        foreach (var definition in SeriesErrorCatalog.Definitions)
        {
            catalog.Add(new OpenApiObject
            {
                ["code"] = new OpenApiString(definition.Code),
                ["category"] = new OpenApiString(definition.Category),
                ["severity"] = new OpenApiString(definition.Severity),
                ["scope"] = new OpenApiString(definition.Scope),
            });
        }

        return catalog;
    }

    private static OpenApiParameter ToOpenApiParameter(V2Parameter parameter)
    {
        var itemSchema = parameter.ArrayItemType is null
            ? null
            : StringSchema(parameter.Values);
        var schema = parameter.ArrayItemType is null
            ? parameter.Type switch
            {
                "integer" => new OpenApiSchema { Type = "integer", Format = "int32" },
                _ => StringSchema(parameter.Values),
            }
            : new OpenApiSchema { Type = "array", Items = itemSchema };
        schema.Format = parameter.Format ?? schema.Format;
        schema.Minimum = parameter.Minimum;
        schema.Maximum = parameter.Maximum;
        schema.Default = parameter.DefaultValue switch
        {
            int value => new OpenApiInteger(value),
            string value => new OpenApiString(value),
            _ => null,
        };

        return new OpenApiParameter
        {
            Name = parameter.Name,
            In = parameter.Location,
            Required = parameter.Required,
            Description = parameter.Description,
            Schema = schema,
            Style = parameter.ArrayItemType is null ? null : ParameterStyle.Form,
            Explode = parameter.ArrayItemType is not null,
            Example = parameter.Example is null ? null : new OpenApiString(parameter.Example),
        };
    }

    private static OpenApiSchema StringSchema(IReadOnlyList<string>? values) =>
        new()
        {
            Type = "string",
            Enum = values?.Select(value => (IOpenApiAny)new OpenApiString(value)).ToList()
                ?? [],
        };

    private static IEnumerable<V2Operation> CreateOperations()
    {
        yield return Operation(
            "/api/v2/contract",
            "GetV2Contract",
            "Contract",
            "Discover the exact replacement contract",
            "Returns the comparable contract identity, exact-match policy, read-only capability set, SeriesErrorCatalog, and readability vocabularies. Consumers refuse business interpretation until this identity matches exactly.",
            typeof(NewMesIngestContractDto));
        yield return Operation(
            "/api/v2/demand-series",
            "ListDemandSeries",
            "DemandSeries",
            "List DemandSeries in an immutable snapshot",
            "Freezes one DemandSeries snapshot, applies server-side filters, returns exact totals/facets, and pages in STARTED_AT_DESC_SERIES_ID_ASC order. snapshotReference and cursor are separate opaque credentials.",
            typeof(DemandSeriesListDto),
            [
                ArrayQuery("lifecycle", "Exact lifecycle filter.", ["TRACKING", "ARCHIVED"]),
                ArrayQuery("presence", "Exact current-presence filter.", ["VISIBLE", "GONE", "LONG_GONE_BUT_VISIBLE"]),
                ArrayQuery("workType", "Exact WorkType; ordinal and case-sensitive."),
                ArrayQuery("area", "Exact valid MES AREA values."),
                Query("sublot", "Case-insensitive contains filter."),
                Query("seriesId", "Exact SeriesId."),
                Query("demandId", "Exact DemandId in any generation."),
                PageSize(), PageNumber("page"), Snapshot(), Cursor(),
                Query("order", "Frozen stable order.", values: [DemandSeriesBrowseOrder.Default], defaultValue: DemandSeriesBrowseOrder.Default),
            ],
            BrowseErrors(),
            supportsHistoricalExpiration: true);
        yield return Operation(
            "/api/v2/demand-series/by-key",
            "GetDemandSeriesByKey",
            "DemandSeries",
            "Get one DemandSeries by TransportDemandKey",
            "Exact ordinal, case-sensitive, whitespace-preserving WorkType + SUBLOT lookup at one immutable snapshot.",
            typeof(FrozenDemandSeriesDto),
            [RequiredQuery("workType", "Exact WorkType."), RequiredQuery("sublot", "Exact SUBLOT."), Snapshot()],
            DetailErrors("DEMAND_SERIES"),
            supportsHistoricalExpiration: true);
        yield return Operation(
            "/api/v2/demand-series/{seriesId}",
            "GetDemandSeries",
            "DemandSeries",
            "Get one complete DemandSeries",
            "Returns generations, raw-observation summaries, events, current conditions, error periods, and ProjectionCommit evidence at one immutable snapshot.",
            typeof(FrozenDemandSeriesDto),
            [Path("seriesId", "Exact SeriesId."), Snapshot()],
            DetailErrors("DEMAND_SERIES"),
            supportsHistoricalExpiration: true);
        yield return Operation(
            "/api/v2/externally-readable-demand-catalog",
            "GetExternallyReadableDemandCatalog",
            "DemandCatalog",
            "Read the complete externally readable Demand catalog",
            "A full-range catalog resource. It accepts no query parameters. If-None-Match carries the weak HistoryEpoch plus CatalogRevision ETag and yields 304 only when both are unchanged.",
            typeof(ExternallyReadableDemandCatalogDto),
            [Header("If-None-Match", "One weak catalog ETag, for example W/\"catalog-h11111111111111111111111111111111-r42\".")],
            Errors(("400", "CATALOG_QUERY_NOT_SUPPORTED or INVALID_CATALOG_CONDITION.")));
        yield return Operation(
            "/api/v2/readability-audit",
            "ListReadabilityAudit",
            "ReadabilityAudit",
            "List external-readability decisions",
            "Freezes ReadabilityAuditSnapshot independently of CatalogRevision, evaluates the published qualification checks, and returns exact totals/facets in the fixed audit order.",
            typeof(ReadabilityAuditListDto),
            [
                ArrayQuery("state", "Exact readability state.", ["READABLE", "NOT_READABLE"]),
                ArrayQuery("workType", "Exact WorkType."),
                ArrayQuery("blocker", "Exact published ReadabilityBlocker code.", ReadabilityBlockerCatalog.Definitions.Select(x => x.Code).ToArray()),
                Query("demandId", "Exact DemandId."), Query("sublot", "SUBLOT contains filter."),
                ArrayQuery("area", "Exact valid MES AREA values."), PageSize(), PageNumber("page"),
                Snapshot(), Cursor(),
                Query("order", "Frozen stable order.", values: [ReadabilityAuditOrder.Default], defaultValue: ReadabilityAuditOrder.Default),
            ],
            AuditErrors(),
            supportsHistoricalExpiration: true);
        yield return Operation(
            "/api/v2/readability-audit/{demandId}",
            "GetReadabilityAuditDetail",
            "ReadabilityAudit",
            "Get one readability decision with evidence",
            "Requires the list snapshotReference and returns qualification results, blockers, bounded observation summaries, and PollTrace evidence from that same audit snapshot.",
            typeof(ReadabilityAuditDetailDto),
            [Path("demandId", "Exact DemandId."), Snapshot(required: true)],
            DetailErrors("READABILITY_AUDIT"),
            supportsHistoricalExpiration: true);
        yield return Operation(
            "/api/v2/error-search",
            "ListErrorSearch",
            "ErrorSearch",
            "Search SeriesError history at ErrorSearchAsOf",
            "Freezes ErrorSearchAsOf, applies exact category/code/state filters and a half-open UTC window, returns exact facets/counts, and pages in ACTIVE_FIRST_LATEST_MATCHED_EVIDENCE_DESC_SERIES_ID_ASC order.",
            typeof(ErrorSearchListDto),
            [
                ArrayQuery("category", "Exact SeriesError primary category.", SeriesErrorCatalog.Definitions.Select(x => x.Category).Distinct().Order().ToArray()),
                ArrayQuery("code", "Exact SeriesError code.", SeriesErrorCatalog.Definitions.Select(x => x.Code).ToArray()),
                ArrayQuery("state", "Exact activity state.", ErrorSearchActivityStates.All),
                Query("seriesId", "Exact SeriesId."), Query("demandId", "Exact DemandId."), Query("sublot", "SUBLOT contains filter."),
                Query("window", "Rolling/all-history preset; mutually exclusive with from/to.", values: [ErrorSearchWindowKinds.Last24Hours, ErrorSearchWindowKinds.Last7Days, ErrorSearchWindowKinds.Last30Days, ErrorSearchWindowKinds.AllHistory], defaultValue: ErrorSearchWindowKinds.Last7Days),
                Query("from", "Custom lower boundary with an explicit UTC offset.", format: "date-time"),
                Query("to", "Custom upper boundary with an explicit UTC offset.", format: "date-time"),
                PageSize(), Snapshot(), Cursor(),
            ],
            SearchErrors(),
            supportsHistoricalExpiration: true);
        yield return Operation(
            "/api/v2/error-search/{seriesId}",
            "GetErrorSearchDetail",
            "ErrorSearch",
            "Get SeriesError periods and diagnostic evidence",
            "Requires the list snapshotReference. Newly committed evidence cannot enter this detail until an explicit refreshed search creates a new ErrorSearchAsOf.",
            typeof(ErrorSearchDetailDto),
            [Path("seriesId", "Exact SeriesId."), Snapshot(required: true)],
            DetailErrors("ERROR_SEARCH"),
            supportsHistoricalExpiration: true);
        yield return Operation(
            "/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations",
            "GetErrorSearchRawEvidence",
            "ErrorSearch",
            "Read restricted raw observation fields",
            "Always requires explicit Bearer authorization. The response is snapshot-bound, field-whitelisted, and limited to 20 items, 2048 bytes per item, and 65536 bytes total.",
            typeof(ErrorSearchRawEvidenceDto),
            [
                Path("seriesId", "Exact SeriesId."), Path("evidenceId", "Exact EvidenceId."),
                Snapshot(required: true),
                ArrayQuery("fields", "Requested sensitive-field allow-list.", ErrorSearchRawEvidenceFields.All),
                Query("maxItems", "Maximum returned items; hard maximum 20.", type: "integer", defaultValue: 20, minimum: 1, maximum: 20),
            ],
            Errors(
                ("400", "INVALID_ERROR_SEARCH_QUERY, INVALID_ERROR_SEARCH_SNAPSHOT_REFERENCE, or RAW_EVIDENCE_FIELD_NOT_ALLOWED."),
                ("403", "RAW_EVIDENCE_ACCESS_DENIED — explicit Bearer authorization is absent or wrong."),
                ("404", "ERROR_SEARCH_OBJECT_NOT_IN_SNAPSHOT."),
                ("410", "ERROR_SEARCH_SNAPSHOT_NOT_FOUND, or MES_INGEST_HISTORY_EXPIRED with HistoryEpoch and earliestAvailableHostUtc."),
                ("413", "RAW_EVIDENCE_LIMIT_EXCEEDED.")),
            supportsHistoricalExpiration: true);
        yield return Operation(
            "/api/v2/current-ingest-attention",
            "ListCurrentIngestAttention",
            "CurrentIngestAttention",
            "List current operator attention items",
            "Returns one operational snapshot with exact totals/facets, fixed severity/time/identity order, and durable bounded-history cleanup progress. This is a read model, not an alert acknowledgement channel.",
            typeof(CurrentIngestAttentionDto),
            [
                PageSize(), PageNumber("pageNumber"),
                ArrayQuery("kind", "Exact attention kind.", CurrentIngestAttentionKinds.All),
                ArrayQuery("severity", "Exact attention severity.", CurrentIngestAttentionSeverities.All),
            ],
            Errors(("400", "CURRENT_INGEST_ATTENTION_INVALID_QUERY."), ("409", "CURRENT_INGEST_ATTENTION_PROJECTION_NOT_AVAILABLE.")));
        yield return Operation(
            "/api/v2/watch-overview",
            "GetWatchOverview",
            "WatchOverview",
            "Read one atomic Watch overview snapshot",
            "Returns series, readability, error, attention, recent activity, and typed navigation intents from one OperationalSnapshotIdentity. AREA scopes display only and do not change qualification.",
            typeof(WatchOverviewDto),
            [ArrayQuery("area", "Exact valid MES AREA values; at most 100.")],
            Errors(("400", "WATCH_OVERVIEW_INVALID_QUERY."), ("409", "WATCH_OVERVIEW_PROJECTION_NOT_AVAILABLE.")));
        yield return Operation(
            "/api/v2/poll-traces/{pollTraceId}",
            "GetPollTrace",
            "PollEvidence",
            "Get one PollTrace and ProjectionCommit evidence",
            "Reads the exact immutable poll outcome and its complete raw multiset by PollTraceId plus ProjectionCommit. The response carries HistoryEpoch and the shared earliest available Host UTC boundary. FAILURE and INCOMPLETE have no business ProjectionCommit.",
            typeof(PollTraceDto),
            [Path("pollTraceId", "Exact PollTraceId.")],
            Errors(
                ("400", "INVALID_POLL_TRACE_ID."),
                ("404", "POLL_TRACE_NOT_FOUND; response includes HistoryEpoch and earliestAvailableHostUtc."),
                ("410", "MES_INGEST_HISTORY_EXPIRED; response includes HistoryEpoch and earliestAvailableHostUtc.")));
        yield return Operation(
            "/api/v2/absence-authority",
            "GetCurrentAbsenceAuthority",
            "PollEvidence",
            "Get current absence authority",
            "Reads RestartBarrier and host-session absence-authority evidence used to explain why absence transitions can or cannot occur.",
            typeof(AbsenceAuthorityDto));
        yield return Operation(
            "/api/v2/absence-authority/{hostSessionId}",
            "GetAbsenceAuthority",
            "PollEvidence",
            "Get absence authority for one host session",
            "Reads immutable RestartBarrier transitions and restoration evidence for the exact HostSessionId.",
            typeof(AbsenceAuthorityDto),
            [Path("hostSessionId", "Exact HostSessionId.")],
            Errors(("400", "INVALID_HOST_SESSION_ID."), ("404", "ABSENCE_AUTHORITY_NOT_FOUND.")));
        yield return Operation(
            "/api/v2/task-type-protections",
            "ListTaskTypeProtections",
            "PollEvidence",
            "List task-type protection state",
            "Reads current PAUSED_ZERO_DROP/recovery evidence for all protected WorkTypes.",
            typeof(TaskTypeProtectionListDto));
        yield return Operation(
            "/api/v2/task-type-protections/{workType}",
            "GetTaskTypeProtection",
            "PollEvidence",
            "Get one task-type protection state",
            "Reads decisions, events, counts, and ProjectionCommit evidence for the exact WorkType.",
            typeof(TaskTypeProtectionDto),
            [Path("workType", "Exact WorkType; ordinal and case-sensitive.")],
            Errors(("400", "INVALID_WORK_TYPE."), ("404", "TASK_TYPE_PROTECTION_NOT_FOUND.")));
    }

    private static V2Operation Operation(
        string path,
        string operationId,
        string tag,
        string summary,
        string description,
        Type responseType,
        IReadOnlyList<V2Parameter>? parameters = null,
        IReadOnlyDictionary<string, string>? errors = null,
        bool supportsHistoricalExpiration = false) =>
        new(
            path,
            operationId,
            tag,
            summary,
            description,
            responseType,
            parameters ?? [],
            errors ?? Errors(),
            supportsHistoricalExpiration);

    private static IReadOnlyDictionary<string, string> BrowseErrors() => Errors(
        ("400", "INVALID_DEMAND_SERIES_QUERY, INVALID_DEMAND_SERIES_SNAPSHOT_REFERENCE, DEMAND_SERIES_SNAPSHOT_MISMATCH, INVALID_DEMAND_SERIES_CURSOR, or DEMAND_SERIES_CURSOR_MISMATCH."),
        ("409", "DEMAND_SERIES_PROJECTION_NOT_AVAILABLE."),
        ("410", "DEMAND_SERIES_SNAPSHOT_NOT_FOUND, or MES_INGEST_HISTORY_EXPIRED with HistoryEpoch and earliestAvailableHostUtc."));

    private static IReadOnlyDictionary<string, string> AuditErrors() => Errors(
        ("400", "INVALID_READABILITY_AUDIT_QUERY, INVALID_READABILITY_AUDIT_SNAPSHOT_REFERENCE, READABILITY_AUDIT_SNAPSHOT_MISMATCH, INVALID_READABILITY_AUDIT_CURSOR, or READABILITY_AUDIT_CURSOR_MISMATCH."),
        ("409", "READABILITY_AUDIT_PROJECTION_NOT_AVAILABLE."),
        ("410", "READABILITY_AUDIT_SNAPSHOT_NOT_FOUND, or MES_INGEST_HISTORY_EXPIRED with HistoryEpoch and earliestAvailableHostUtc."));

    private static IReadOnlyDictionary<string, string> SearchErrors() => Errors(
        ("400", "INVALID_ERROR_SEARCH_QUERY, INVALID_ERROR_SEARCH_SNAPSHOT_REFERENCE, ERROR_SEARCH_SNAPSHOT_MISMATCH, INVALID_ERROR_SEARCH_CURSOR, or ERROR_SEARCH_CURSOR_MISMATCH."),
        ("409", "ERROR_SEARCH_PROJECTION_NOT_AVAILABLE."),
        ("410", "ERROR_SEARCH_SNAPSHOT_NOT_FOUND, or MES_INGEST_HISTORY_EXPIRED with HistoryEpoch and earliestAvailableHostUtc."));

    private static IReadOnlyDictionary<string, string> DetailErrors(string prefix) => prefix switch
    {
        "DEMAND_SERIES" => BrowseErrors().Concat(Errors(("404", "DEMAND_SERIES_OBJECT_NOT_IN_SNAPSHOT."))).ToDictionary(),
        "READABILITY_AUDIT" => AuditErrors().Concat(Errors(("404", "READABILITY_AUDIT_OBJECT_NOT_IN_SNAPSHOT."))).ToDictionary(),
        _ => SearchErrors().Concat(Errors(("404", "ERROR_SEARCH_OBJECT_NOT_IN_SNAPSHOT."))).ToDictionary(),
    };

    private static IReadOnlyDictionary<string, string> Errors(params (string Status, string Description)[] values) =>
        values.ToDictionary(value => value.Status, value => value.Description, StringComparer.Ordinal);

    private static V2Parameter Path(string name, string description) =>
        new(name, ParameterLocation.Path, description, Required: true);

    private static V2Parameter Header(string name, string description) =>
        new(name, ParameterLocation.Header, description);

    private static V2Parameter RequiredQuery(string name, string description) =>
        Query(name, description) with { Required = true };

    private static V2Parameter Query(
        string name,
        string description,
        IReadOnlyList<string>? values = null,
        string type = "string",
        object? defaultValue = null,
        decimal? minimum = null,
        decimal? maximum = null,
        string? format = null) =>
        new(name, ParameterLocation.Query, description, Values: values, Type: type,
            DefaultValue: defaultValue, Minimum: minimum, Maximum: maximum, Format: format);

    private static V2Parameter ArrayQuery(
        string name,
        string description,
        IReadOnlyList<string>? values = null) =>
        Query(name, description, values) with { ArrayItemType = "string" };

    private static V2Parameter PageSize() =>
        Query("pageSize", "Page size. Default 100; hard maximum 200.", type: "integer", defaultValue: 100, minimum: 1, maximum: 200);

    private static V2Parameter PageNumber(string name) =>
        Query(name, "One-based page number. Default 1.", type: "integer", defaultValue: 1, minimum: 1);

    private static V2Parameter Snapshot(bool required = false) =>
        Query("snapshot", "Opaque immutable snapshotReference. It is not a cursor or catalog ETag.") with { Required = required };

    private static V2Parameter Cursor() =>
        Query("cursor", "Opaque signed keyset position bound to contract, snapshot, normalized filters, order, and page size.");

    private sealed record V2Operation(
        string Path,
        string OperationId,
        string Tag,
        string Summary,
        string Description,
        Type ResponseType,
        IReadOnlyList<V2Parameter> Parameters,
        IReadOnlyDictionary<string, string> Errors,
        bool SupportsHistoricalExpiration);

    private sealed record V2Parameter(
        string Name,
        ParameterLocation Location,
        string Description,
        bool Required = false,
        IReadOnlyList<string>? Values = null,
        string Type = "string",
        object? DefaultValue = null,
        decimal? Minimum = null,
        decimal? Maximum = null,
        string? Format = null,
        string? ArrayItemType = null,
        string? Example = null);
}

internal sealed class NewMesIngestOpenApiSchemaFilter : ISchemaFilter
{
    private static readonly NullabilityInfoContext Nullability = new();

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> StableValues =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["NewMesIngestOperationDto.method"] = ["GET"],
            ["NewMesIngestCapabilityDto.id"] = NewMesIngestContract.Capabilities.Select(x => x.Id).ToArray(),
            ["NewMesIngestCapabilityDto.version"] = ["1.0"],
            ["SeriesErrorDefinitionDto.code"] = SeriesErrorCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["SeriesErrorDefinitionDto.category"] = SeriesErrorCatalog.Definitions.Select(x => x.Category).Distinct().Order().ToArray(),
            ["SeriesErrorDefinitionDto.severity"] = SeriesErrorCatalog.Definitions.Select(x => x.Severity).Distinct().Order().ToArray(),
            ["SeriesErrorDefinitionDto.scope"] = SeriesErrorCatalog.Definitions.Select(x => x.Scope).Distinct().Order().ToArray(),
            ["ReadabilityBlockerDefinitionDto.code"] = ReadabilityBlockerCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["ReadabilityQualificationCheckDefinitionDto.code"] = ReadabilityQualificationCheckCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["ReadabilityQualificationCheckDefinitionDto.blockingCode"] = ReadabilityQualificationCheckCatalog.Definitions.Select(x => x.BlockingCode).Distinct().ToArray(),
            ["DemandSeriesListDto.order"] = [DemandSeriesBrowseOrder.Default],
            ["DemandSeriesListItemDto.lifecycle"] = ["TRACKING", "ARCHIVED"],
            ["DemandSeriesListItemDto.currentPresence"] = ["VISIBLE", "GONE", "LONG_GONE_BUT_VISIBLE"],
            ["DemandSeriesListItemDto.currentDemandStatus"] = ["VISIBLE", "GONE"],
            ["DemandSeriesListItemDto.externalReadabilityState"] = ["READABLE", "NOT_READABLE"],
            ["FrozenDemandSeriesDto.lifecycle"] = ["TRACKING", "ARCHIVED"],
            ["FrozenDemandSeriesDto.currentPresence"] = ["VISIBLE", "GONE", "LONG_GONE_BUT_VISIBLE"],
            ["TransportDemandV2Dto.status"] = ["VISIBLE", "GONE"],
            ["TransportDemandV2Dto.externalReadabilityState"] = ["READABLE", "NOT_READABLE"],
            ["TransportDemandV2Dto.readabilityBlockers"] = ReadabilityBlockerCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["DemandRawObservationDto.assignment"] = ["ASSIGNED", "UNASSIGNED"],
            ["DemandSeriesCurrentConditionDto.code"] = SeriesErrorCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["DemandSeriesCurrentConditionDto.category"] = SeriesErrorCatalog.Definitions.Select(x => x.Category).Distinct().Order().ToArray(),
            ["DemandSeriesCurrentConditionDto.severity"] = SeriesErrorCatalog.Definitions.Select(x => x.Severity).Distinct().Order().ToArray(),
            ["DemandSeriesCurrentConditionDto.externalReadabilityState"] = ["READABLE", "NOT_READABLE"],
            ["DemandSeriesErrorPeriodDto.code"] = SeriesErrorCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["DemandSeriesErrorPeriodDto.category"] = SeriesErrorCatalog.Definitions.Select(x => x.Category).Distinct().Order().ToArray(),
            ["DemandSeriesErrorPeriodDto.severity"] = SeriesErrorCatalog.Definitions.Select(x => x.Severity).Distinct().Order().ToArray(),
            ["ReadabilityAuditListDto.order"] = [ReadabilityAuditOrder.Default],
            ["ReadabilityAuditFilterDto.readabilityStates"] = ["READABLE", "NOT_READABLE"],
            ["ReadabilityAuditFilterDto.blockers"] = ReadabilityBlockerCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["ReadabilityStateFacetDto.state"] = ["READABLE", "NOT_READABLE"],
            ["ReadabilityBlockerFacetDto.code"] = ReadabilityBlockerCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["ReadabilityAuditListItemDto.demandStatus"] = ["VISIBLE", "GONE"],
            ["ReadabilityAuditListItemDto.seriesLifecycle"] = ["TRACKING", "ARCHIVED"],
            ["ReadabilityAuditListItemDto.seriesCurrentPresence"] = ["VISIBLE", "GONE", "LONG_GONE_BUT_VISIBLE"],
            ["ReadabilityAuditListItemDto.externalReadabilityState"] = ["READABLE", "NOT_READABLE"],
            ["ReadabilityAuditListItemDto.leadReadabilityBlocker"] = ReadabilityBlockerCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["ReadabilityAuditListItemDto.readabilityBlockers"] = ReadabilityBlockerCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["ReadabilityQualificationCheckDto.code"] = ReadabilityQualificationCheckCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["ReadabilityQualificationCheckDto.blockingCode"] = ReadabilityQualificationCheckCatalog.Definitions.Select(x => x.BlockingCode).Distinct().ToArray(),
            ["ReadabilityQualificationCheckDto.result"] = ["PASS", "FAIL", "NOT_EVALUATED"],
            ["ErrorSearchListDto.order"] = [ErrorSearchOrder.Default],
            ["ErrorSearchFilterDto.categories"] = SeriesErrorCatalog.Definitions.Select(x => x.Category).Distinct().Order().ToArray(),
            ["ErrorSearchFilterDto.errorCodes"] = SeriesErrorCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["ErrorSearchFilterDto.activityStates"] = ["ACTIVE", "ENDED"],
            ["ErrorSearchWindowDto.kind"] = ["LAST_24_HOURS", "LAST_7_DAYS", "LAST_30_DAYS", "ALL_HISTORY", "CUSTOM"],
            ["ErrorSearchCategoryFacetDto.category"] = SeriesErrorCatalog.Definitions.Select(x => x.Category).Distinct().Order().ToArray(),
            ["ErrorSearchActivityStateFacetDto.state"] = ["ACTIVE", "ENDED"],
            ["ErrorSearchListItemDto.activityState"] = ["ACTIVE", "ENDED"],
            ["ErrorSearchMatchedErrorDto.code"] = SeriesErrorCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["ErrorSearchMatchedErrorDto.category"] = SeriesErrorCatalog.Definitions.Select(x => x.Category).Distinct().Order().ToArray(),
            ["ErrorSearchMatchedErrorDto.severity"] = SeriesErrorCatalog.Definitions.Select(x => x.Severity).Distinct().Order().ToArray(),
            ["ErrorSearchListItemDto.mesAreaAvailability"] = ["CURRENT_TRUSTED", "LAST_TRUSTED", "UNKNOWN", "INVALID"],
            ["ErrorSearchDetailPeriodDto.code"] = SeriesErrorCatalog.Definitions.Select(x => x.Code).ToArray(),
            ["ErrorSearchDetailPeriodDto.category"] = SeriesErrorCatalog.Definitions.Select(x => x.Category).Distinct().Order().ToArray(),
            ["ErrorSearchDetailPeriodDto.severity"] = SeriesErrorCatalog.Definitions.Select(x => x.Severity).Distinct().Order().ToArray(),
            ["ErrorSearchRawEvidenceDto.includedFields"] = ErrorSearchRawEvidenceFields.All,
            ["CurrentIngestAttentionDto.order"] = [CurrentIngestAttentionOrder.Default],
            ["CurrentIngestAttentionDto.kinds"] = CurrentIngestAttentionKinds.All,
            ["CurrentIngestAttentionDto.severities"] = CurrentIngestAttentionSeverities.All,
            ["CurrentIngestAttentionItemDto.kind"] = CurrentIngestAttentionKinds.All,
            ["CurrentIngestAttentionItemDto.severity"] = CurrentIngestAttentionSeverities.All,
            ["HistoryCleanupStateDto.status"] = HistoryCleanupRunStatuses.All,
            ["OverviewNavigationIntentDto.target"] = ["DEMAND_SERIES", "READABILITY_AUDIT", "ERROR_SEARCH", "CURRENT_INGEST_ATTENTION", "DEMAND_SERIES_DETAIL", "TASK_TYPE_PROTECTION", "POLL_TRACE"],
            ["WatchOverviewDto.recentActivityState"] = ["HAS_RECENT_HIGHLIGHTS", "NO_RECENT_HIGHLIGHTS"],
            ["AbsenceAuthorityDto.phase"] = ["BARRIER", "POST_BARRIER", "NORMAL"],
            ["AbsenceAuthorityEventDto.phaseBefore"] = ["BARRIER", "POST_BARRIER", "NORMAL"],
            ["AbsenceAuthorityEventDto.phaseAfter"] = ["BARRIER", "POST_BARRIER", "NORMAL"],
            ["TaskTypeProtectionDto.phase"] = ["MONITORING", "PAUSED_ZERO_DROP", "RECOVERING", "AUTHORITY_PENDING"],
            ["PollTraceDto.outcome"] = ["SUCCESS", "FAILURE", "INCOMPLETE"],
            ["NewMesIngestContractDto.compatibilityPolicy"] = [NewMesIngestContract.CompatibilityPolicy],
            ["NewMesIngestContractDto.openApiDocument"] = [NewMesIngestContract.OpenApiDocumentPath],
            ["NewMesIngestContractDto.businessSurface"] = ["READ_ONLY_GET"],
            ["NewMesIngestContractDto.legacySurfacePolicy"] = ["DEVELOPMENT_ONLY_EXCLUDED_FROM_V2"],
            ["NewMesIngestContractDto.transportDemandKeyComparison"] = [NewMesIngestContract.KeyComparison],
        };

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type.Namespace != typeof(NewMesIngestErrorDto).Namespace
            || !context.Type.Name.EndsWith("Dto", StringComparison.Ordinal))
        {
            return;
        }

        schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in context.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (!schema.Properties.TryGetValue(jsonName, out var propertySchema))
            {
                continue;
            }

            var nullability = Nullability.Create(property);
            var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null
                || nullability.ReadState == NullabilityState.Nullable;
            propertySchema.Nullable = nullable;
            if (!nullable)
            {
                schema.Required.Add(jsonName);
            }

            propertySchema.Description = Describe(context.Type.Name, jsonName, property.PropertyType);
            IReadOnlyList<string>? values = string.Equals(
                jsonName,
                "contractVersion",
                StringComparison.Ordinal)
                    ? [NewMesIngestContract.Version]
                    : StableValues.GetValueOrDefault($"{context.Type.Name}.{jsonName}");
            if (values is not null)
            {
                var target = property.PropertyType != typeof(string)
                    && typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType)
                    && propertySchema.Items is not null
                        ? propertySchema.Items
                        : propertySchema;
                target.Enum = values
                    .Select(value => (IOpenApiAny)new OpenApiString(value))
                    .ToList();
            }
        }

        ConfigurePaging(schema, context.Type.Name);
        if (context.Type == typeof(NewMesIngestErrorDto))
        {
            schema.Description =
                "Stable V2 error envelope. Published codes include invalid/tampered cursor codes, "
                + "distinct *_CURSOR_MISMATCH binding errors, *_SNAPSHOT_NOT_FOUND expiry errors, "
                + "ERROR_SEARCH_CURSOR_MISMATCH, RAW_EVIDENCE_ACCESS_DENIED, and limit errors.";
        }
    }

    private static void ConfigurePaging(OpenApiSchema schema, string typeName)
    {
        if (typeName is not (nameof(DemandSeriesListDto)
            or nameof(ReadabilityAuditListDto)
            or nameof(ErrorSearchListDto)
            or nameof(CurrentIngestAttentionDto)))
        {
            return;
        }

        if (schema.Properties.TryGetValue("pageSize", out var pageSize))
        {
            pageSize.Minimum = 1;
            pageSize.Maximum = 200;
            pageSize.Default = new OpenApiInteger(100);
        }

        if (schema.Properties.TryGetValue("pageNumber", out var pageNumber))
        {
            pageNumber.Minimum = 1;
            pageNumber.Default = new OpenApiInteger(1);
        }
    }

    private static string Describe(string typeName, string propertyName, Type propertyType)
    {
        if (string.Equals(propertyName, "contractVersion", StringComparison.Ordinal))
        {
            return $"Exact comparable contract identity; must equal {NewMesIngestContract.Version}.";
        }

        if (propertyName.Contains("snapshotReference", StringComparison.OrdinalIgnoreCase))
        {
            return "Opaque immutable snapshot identity credential; not a cursor or catalog ETag.";
        }

        if (propertyName.Contains("cursor", StringComparison.OrdinalIgnoreCase))
        {
            return "Opaque signed paging position bound to its contract, snapshot, normalized filters, order, and page size.";
        }

        if (propertyName.Contains("catalogRevision", StringComparison.OrdinalIgnoreCase))
        {
            return "Monotonic CatalogRevision for full catalog conditional reads; represented by the weak catalog ETag.";
        }

        if (propertyType == typeof(DateTimeOffset)
            || Nullable.GetUnderlyingType(propertyType) == typeof(DateTimeOffset))
        {
            return string.Equals(propertyName, "mesSourceDate", StringComparison.OrdinalIgnoreCase)
                ? "ISO-8601 date-time preserving the explicit MES source offset."
                : "ISO-8601 date-time generated or normalized by Host in UTC.";
        }

        if (propertyName.Contains("projectionCommit", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("projectionSequence", StringComparison.OrdinalIgnoreCase))
        {
            return "ProjectionCommit identity from one atomic successful round.";
        }

        if (propertyName.StartsWith("exactTotal", StringComparison.Ordinal)
            || string.Equals(propertyName, "totalSeriesCount", StringComparison.Ordinal))
        {
            return "Exact server-side total inside the returned immutable snapshot; never estimated from one page.";
        }

        if (string.Equals(propertyName, "order", StringComparison.Ordinal))
        {
            return "Only the documented stable order is accepted; its final identity field is the deterministic tie-break.";
        }

        if (string.Equals(propertyName, "pageSize", StringComparison.Ordinal))
        {
            return "Page size; default 100 and hard maximum 200.";
        }

        return $"Frozen {typeName}.{propertyName} contract field.";
    }
}
