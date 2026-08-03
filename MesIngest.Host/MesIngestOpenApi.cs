using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MesIngest.Host;

/// <summary>
/// Read-only OpenAPI/Swagger contract for MesIngest Host.
/// Documentation routes are public; <c>/api/*</c> still uses SharedSecret when required.
/// </summary>
public static class MesIngestOpenApi
{
    public const string DocumentName = "v1";
    public const string OpenApiJsonPath = "/openapi/v1.json";
    public const string SwaggerUiPathPrefix = "swagger";
    public const string BearerSchemeId = "Bearer";

    public static readonly string[] ApiPaths =
    [
        "/api/contract",
        "/api/demands",
        "/api/demands/{demandId}",
        "/api/alerts",
        "/api/poll-health",
        "/api/demand-changes",
    ];

    public static readonly string InfoDescription =
        """
        MesIngest formal read-only HTTP API (GET only). No write, inject, or state-mutation endpoints.

        Auth boundary: OpenAPI/Swagger documentation metadata is publicly readable so operators can open
        /swagger and /openapi/v1.json without a secret. Actual /api/* data requests follow SharedSecret
        rules — when the host binds beyond localhost, send Authorization: Bearer <MesIngest:SharedSecret>.
        Use Swagger UI Authorize (Bearer) before Try it out.

        Time field semantics and timezone:
        - dates (MesCurrentStepEnteredAt): current-step entered time from MES DATES. Oracle/CSV source
          offset is preserved (plant DATES are typically UTC+08:00); API returns DateTimeOffset as stored.
        - step: next process label from MES STEP (not the time of entering the current step).
        - mesLastSeenAt: Host observation time when the demand was last present in a successful snapshot
          (UTC clock on Host).
        - createdAt: Host projection creation time (UTC clock).
        - goneAt: Host time when the demand transitioned to GONE (UTC clock).
        - Query range filters (datesFrom/datesTo, goneAtFrom/goneAtTo, alert from/to) accept ISO-8601
          DateTimeOffset strings. Watch displays local TimeZoneInfo.Local; this API does not rewrite offsets.

        Pagination: list endpoints return { items, nextCursor, hasMore }. Default page size 100; hard max 200.
        Invalid filter/sort/cursor/limit → 400. Missing demand/poll-health → 404. SharedSecret required but
        missing/wrong → 401. DemandChangeFeed cursor older than retained ledger → 410 SYNC_CURSOR_EXPIRED.

        DemandChangeFeed (/api/demand-changes): CREATED and GONE events only (not MesLastSeenAt / pause /
        alert churn). Default retention 48 hours (MesIngest:ChangeFeedRetentionHours; 0 = permanent).
        Bootstrap is a client procedure: capture highWatermark, replace local mirror with full VISIBLE plus
        GoneAt>=now-24h GONE via /api/demands, then catch up /api/demand-changes after that watermark —
        do not merge into a stale mirror.
        """.ReplaceLineEndings("\n");

    public static bool IsPublicDocumentationPath(PathString path)
    {
        var value = path.Value ?? "";
        return value.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase);
    }

    public static void AddMesIngestOpenApi(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = "MesIngest Read API",
                Version = "v1",
                Description = InfoDescription,
            });

            options.AddSecurityDefinition(BearerSchemeId, new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "SharedSecret",
                In = ParameterLocation.Header,
                Description =
                    "Bearer SharedSecret required for /api/* when the host binds beyond localhost. "
                    + "Documentation routes (/swagger, /openapi) stay public.",
            });

            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = BearerSchemeId,
                    },
                }] = Array.Empty<string>(),
            });

            options.TagActionsBy(api =>
            {
                var path = api.RelativePath ?? "";
                if (path.StartsWith("api/demands", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith("api/demand-changes", StringComparison.OrdinalIgnoreCase))
                {
                    return ["Demands"];
                }

                if (path.StartsWith("api/alerts", StringComparison.OrdinalIgnoreCase))
                {
                    return ["Alerts"];
                }

                if (path.StartsWith("api/poll-health", StringComparison.OrdinalIgnoreCase))
                {
                    return ["PollHealth"];
                }

                if (path.StartsWith("api/demand-changes", StringComparison.OrdinalIgnoreCase))
                {
                    return ["DemandChangeFeed"];
                }

                return ["Other"];
            });

            options.DocumentFilter<MesIngestOpenApiDocumentFilter>();
        });
    }

    public static void UseMesIngestOpenApi(this WebApplication app)
    {
        app.UseSwagger(options =>
        {
            options.RouteTemplate = "openapi/{documentName}.json";
        });
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint(OpenApiJsonPath, "MesIngest Read API v1");
            options.RoutePrefix = SwaggerUiPathPrefix;
        });
    }
}

/// <summary>
/// Enriches the generated document with summaries, examples, and error responses.
/// </summary>
internal sealed class MesIngestOpenApiDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        Describe(
            swaggerDoc,
            "/api/contract",
            "API contract version",
            """
            Returns contractVersion and schemaVersion. Watch compares contractVersion to its expected
            MesIngestApiContract.Version and shows CONTRACT_VERSION_MISMATCH when Host/Watch packages diverge.
            """,
            exampleQuery: null,
            notFound: false,
            syncExpired: false,
            badRequest: false);

        Describe(
            swaggerDoc,
            "/api/demands",
            "List transport demands (paginated)",
            """
            Default status=VISIBLE&sortBy=dates&direction=desc with DemandId tie-break.
            Filters: status, taskType, sublot, demandId (exact or >=6 lowercase hex prefix),
            datesFrom/datesTo, goneAtFrom/goneAtTo, sortBy allow-list
            (dates|demandId|goneAt|taskType|sublot|createdAt|mesLastSeenAt|status|area|eqp|step|package|locationRisk|disappearCount),
            direction, limit (1-200), cursor.
            GONE defaults to GoneAt >= now-24h unless an explicit GoneAt range is supplied.
            """,
            exampleQuery: "?status=VISIBLE&sortBy=dates&direction=desc&limit=100",
            notFound: false,
            syncExpired: false);

        Describe(
            swaggerDoc,
            "/api/demands/{demandId}",
            "Get one transport demand by DemandId",
            "Exact DemandId lookup.",
            exampleQuery: null,
            notFound: true,
            syncExpired: false);

        Describe(
            swaggerDoc,
            "/api/alerts",
            "List ingest alert incidents (paginated)",
            """
            Filters: active, code, severity (ERROR|WARNING), from/to, sortBy allow-list
            (lastSeenAt|firstSeenAt|code|severity|alertId|taskType|sublot|demandId|message),
            direction, limit (1-200), cursor. AlertId is the stable tie-break for every primary sort.
            Default sort prefers active ERROR/WARNING then LastSeenAt.
            """,
            exampleQuery: "?active=true&severity=ERROR&limit=100",
            notFound: false,
            syncExpired: false);

        Describe(
            swaggerDoc,
            "/api/poll-health",
            "Latest poll health snapshot",
            "Returns the most recent Host poll outcome and task-type pause states.",
            exampleQuery: null,
            notFound: true,
            syncExpired: false);

        Describe(
            swaggerDoc,
            "/api/demand-changes",
            "DemandChangeFeed page (CREATED/GONE)",
            """
            Query: afterSequence (default 0), limit (1-200). Response includes nextAfterSequence, hasMore,
            highWatermark, earliestAvailableSequence. Only CREATED and GONE.
            Use with /api/demands for authoritative Bootstrap (see API description).
            """,
            exampleQuery: "?afterSequence=0&limit=100",
            notFound: false,
            syncExpired: true);
    }

    private static void Describe(
        OpenApiDocument doc,
        string path,
        string summary,
        string description,
        string? exampleQuery,
        bool notFound,
        bool syncExpired,
        bool badRequest = true)
    {
        if (!doc.Paths.TryGetValue(path, out var item)
            || item.Operations is null
            || !item.Operations.TryGetValue(OperationType.Get, out var operation))
        {
            return;
        }

        operation.Summary = summary;
        operation.Description = description.ReplaceLineEndings("\n");
        if (!string.IsNullOrWhiteSpace(exampleQuery))
        {
            operation.Description += "\n\nExample: GET " + path + exampleQuery;
        }

        EnsureResponse(operation, "200", "Success");
        if (badRequest)
        {
            EnsureResponse(operation, "400", "Invalid filter, sort, cursor, or limit");
        }

        EnsureResponse(operation, "401", "SharedSecret required or incorrect (non-localhost bind)");
        if (notFound)
        {
            EnsureResponse(operation, "404", "Resource not found");
        }

        if (syncExpired)
        {
            EnsureResponse(operation, "410", "SYNC_CURSOR_EXPIRED — afterSequence older than retained ChangeFeed");
        }

        if (operation.Parameters is not null)
        {
            foreach (var parameter in operation.Parameters)
            {
                if (NameEquals(parameter.Name, "limit"))
                {
                    parameter.Description = "Page size. Default 100; hard maximum 200.";
                    parameter.Example = new OpenApiInteger(100);
                    parameter.Schema ??= new OpenApiSchema { Type = "integer" };
                    parameter.Schema.Minimum = 1;
                    parameter.Schema.Maximum = 200;
                }
                else if (NameEquals(parameter.Name, "status"))
                {
                    parameter.Description = "VISIBLE (default) or GONE.";
                    parameter.Example = new OpenApiString("VISIBLE");
                }
                else if (NameEquals(parameter.Name, "sortBy"))
                {
                    if (string.Equals(path, "/api/alerts", StringComparison.Ordinal))
                    {
                        parameter.Description =
                            "Allow-list: lastSeenAt (default), firstSeenAt, code, severity, alertId, taskType, sublot, demandId, message. AlertId is the stable tie-break.";
                        parameter.Example = new OpenApiString("lastSeenAt");
                    }
                    else
                    {
                        parameter.Description =
                            "Allow-list: dates (default), demandId, goneAt, taskType, sublot, createdAt, mesLastSeenAt, status, area, eqp, step, package, locationRisk, disappearCount. DemandId is the stable ascending tie-break. Status sorting operates within the required VISIBLE or GONE status partition, so its primary values are tied.";
                        parameter.Example = new OpenApiString("dates");
                    }
                }
                else if (NameEquals(parameter.Name, "direction"))
                {
                    parameter.Description = "asc or desc (default desc).";
                    parameter.Example = new OpenApiString("desc");
                }
                else if (NameEquals(parameter.Name, "afterSequence"))
                {
                    parameter.Description = "Return changes with sequence greater than this value. Default 0.";
                    parameter.Example = new OpenApiString("0");
                }
                else if (NameEquals(parameter.Name, "cursor"))
                {
                    parameter.Description = "Opaque keyset cursor from a previous page's nextCursor.";
                }
                else if (NameEquals(parameter.Name, "demandId") && parameter.In == ParameterLocation.Query)
                {
                    parameter.Description = "Exact DemandId or >=6 lowercase hex prefix.";
                    parameter.Example = new OpenApiString("a1b2c3");
                }
            }
        }
    }

    private static void EnsureResponse(OpenApiOperation operation, string statusCode, string description)
    {
        operation.Responses ??= new OpenApiResponses();
        if (!operation.Responses.TryGetValue(statusCode, out var response))
        {
            response = new OpenApiResponse();
            operation.Responses[statusCode] = response;
        }

        response.Description = description;
    }

    private static bool NameEquals(string? left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
