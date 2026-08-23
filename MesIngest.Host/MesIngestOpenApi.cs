using Microsoft.OpenApi.Models;
using MesIngest.Core.SeriesProjection;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MesIngest.Host;

/// <summary>
/// Read-only OpenAPI/Swagger contract for MesIngest Host.
/// Documentation routes are public; <c>/api/*</c> still uses SharedSecret when required.
/// </summary>
public static class MesIngestOpenApi
{
    public const string V2DocumentName = "v2";
    public const string V2OpenApiJsonPath = "/openapi/v2.json";
    public const string SwaggerUiPathPrefix = "swagger";
    public const string BearerSchemeId = "Bearer";

    public static readonly string[] V2ApiPaths = NewMesIngestContract.Capabilities
        .SelectMany(capability => capability.Operations)
        .Select(operation => operation.Path)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    public static readonly string V2InfoDescription =
        $$"""
        MesIngest V2 read contract. Every business operation is GET; no TransportDemand,
        error, projection, or Watch business write is accepted by this surface.

        Compatibility is {{NewMesIngestContract.CompatibilityPolicy}}. A consumer must read
        /api/v2/contract and require the exact contractVersion, schemaVersion, and complete capability
        set before interpreting any business response. Missing fields, unknown states, and client-side
        single-page filtering are not compatibility fallbacks.

        Host timestamps and ProjectionCommit timestamps are ISO-8601 date-time values in UTC.
        mesSourceDate preserves its MES source offset. Snapshot references and cursors are opaque,
        purpose-bound credentials; catalog conditional reads bind HistoryEpoch plus CatalogRevision
        in a weak ETag and use 304 only when both identities match.
        List totals and facets are exact within the named snapshot. Default page size is 100 and the
        hard maximum is 200 unless an operation documents a stricter diagnostic limit.

        Bearer SharedSecret is required for non-loopback business reads. Restricted raw evidence always
        requires explicit Bearer authorization, including on loopback. This document is the only
        published MesIngest read contract; no earlier surface is served or supported.
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
            options.SwaggerDoc(V2DocumentName, new OpenApiInfo
            {
                Title = "MesIngest V2 Read Contract",
                Version = NewMesIngestContract.Version,
                Description = V2InfoDescription,
            });

            options.DocInclusionPredicate((documentName, api) =>
                string.Equals(documentName, V2DocumentName, StringComparison.Ordinal)
                && string.Equals(api.GroupName, V2DocumentName, StringComparison.Ordinal));
            options.SupportNonNullableReferenceTypes();

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
                if (path.StartsWith("api/v2/demand-series", StringComparison.OrdinalIgnoreCase))
                {
                    return ["DemandSeries"];
                }

                if (path.StartsWith("api/v2/error-search", StringComparison.OrdinalIgnoreCase))
                {
                    return ["ErrorSearch"];
                }

                if (path.StartsWith("api/v2/readability-audit", StringComparison.OrdinalIgnoreCase))
                {
                    return ["ReadabilityAudit"];
                }

                if (path.StartsWith("api/v2/externally-readable", StringComparison.OrdinalIgnoreCase))
                {
                    return ["DemandCatalog"];
                }

                if (path.StartsWith("api/v2/current-ingest-attention", StringComparison.OrdinalIgnoreCase))
                {
                    return ["CurrentIngestAttention"];
                }

                if (path.StartsWith("api/v2/watch-overview", StringComparison.OrdinalIgnoreCase))
                {
                    return ["WatchOverview"];
                }

                if (path.StartsWith("api/v2/contract", StringComparison.OrdinalIgnoreCase))
                {
                    return ["Contract"];
                }

                return ["PollEvidence"];
            });

            options.DocumentFilter<NewMesIngestOpenApiDocumentFilter>();
            options.SchemaFilter<NewMesIngestOpenApiSchemaFilter>();
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
            options.SwaggerEndpoint(V2OpenApiJsonPath, "MesIngest V2 Read Contract");
            options.RoutePrefix = SwaggerUiPathPrefix;
        });
    }
}
