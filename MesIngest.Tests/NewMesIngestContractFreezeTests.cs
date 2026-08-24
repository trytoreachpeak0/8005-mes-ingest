using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

public sealed class NewMesIngestContractFreezeTests
{
    [Fact]
    public void Contract_discovery_returns_exact_version_and_only_the_frozen_v2_capability_set()
    {
        Assert.Equal("2026.08.new-mes-ingest.v2.0", NewMesIngestContract.Version);
        Assert.Equal(27, NewMesIngestContract.SchemaVersion);
        Assert.Equal(
            "EXACT_VERSION_SCHEMA_AND_CAPABILITIES",
            NewMesIngestContract.CompatibilityPolicy);
        Assert.Equal("/openapi/v2.json", NewMesIngestContract.OpenApiDocumentPath);
        Assert.Equal(
            "ORDINAL_CASE_SENSITIVE_WHITESPACE_PRESERVING",
            NewMesIngestContract.KeyComparison);

        Assert.Equal(
            [
                "CONTRACT_DISCOVERY|1.0|GET /api/v2/contract",
                "CURRENT_INGEST_ATTENTION|1.0|GET /api/v2/current-ingest-attention",
                "DEMAND_SERIES|1.0|GET /api/v2/demand-series;GET /api/v2/demand-series/by-key;GET /api/v2/demand-series/{seriesId}",
                "ERROR_SEARCH|1.0|GET /api/v2/error-search;GET /api/v2/error-search/{seriesId};GET /api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations",
                "EXTERNALLY_READABLE_DEMAND_CATALOG|1.0|GET /api/v2/externally-readable-demand-catalog",
                "POLL_HEALTH_AND_EVIDENCE|1.0|GET /api/v2/poll-traces/{pollTraceId};GET /api/v2/absence-authority;GET /api/v2/absence-authority/{hostSessionId};GET /api/v2/task-type-protections;GET /api/v2/task-type-protections/{workType}",
                "READABILITY_AUDIT|1.0|GET /api/v2/readability-audit;GET /api/v2/readability-audit/{demandId}",
                "SERIES_ERROR_CATALOG|1.0|GET /api/v2/contract",
                "WATCH_OVERVIEW|1.0|GET /api/v2/watch-overview",
            ],
            NewMesIngestContract.Capabilities.Select(capability =>
                $"{capability.Id}|{capability.Version}|{string.Join(';', capability.Operations.Select(operation => $"{operation.Method} {operation.Path}"))}"));

        var operations = NewMesIngestContract.Capabilities
            .SelectMany(capability => capability.Operations)
            .Distinct()
            .OrderBy(operation => operation.Path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(17, operations.Length);
        Assert.All(operations, operation => Assert.Equal("GET", operation.Method));
        Assert.All(
            operations,
            operation => Assert.StartsWith("/api/v2/", operation.Path, StringComparison.Ordinal));
        Assert.DoesNotContain(
            operations,
            operation => operation.Path.Contains("demand-changes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Contract_compatibility_requires_an_exact_version_and_schema_match()
    {
        var capabilityIds = NewMesIngestContract.Capabilities
            .Select(capability => capability.Id)
            .ToArray();

        NewMesIngestContract.RequireExactCompatibility(
            NewMesIngestContract.Version,
            NewMesIngestContract.SchemaVersion,
            capabilityIds);

        var version = Assert.Throws<NewMesIngestContractMismatchException>(() =>
            NewMesIngestContract.RequireExactCompatibility(
                "2026.08.new-mes-ingest.v2.1",
                NewMesIngestContract.SchemaVersion,
                capabilityIds));
        var schema = Assert.Throws<NewMesIngestContractMismatchException>(() =>
            NewMesIngestContract.RequireExactCompatibility(
                NewMesIngestContract.Version,
                NewMesIngestContract.SchemaVersion + 1,
                capabilityIds));
        var missingCapability = Assert.Throws<NewMesIngestContractMismatchException>(() =>
            NewMesIngestContract.RequireExactCompatibility(
                NewMesIngestContract.Version,
                NewMesIngestContract.SchemaVersion,
                capabilityIds.Skip(1)));
        var additionalCapability = Assert.Throws<NewMesIngestContractMismatchException>(() =>
            NewMesIngestContract.RequireExactCompatibility(
                NewMesIngestContract.Version,
                NewMesIngestContract.SchemaVersion,
                capabilityIds.Append("UNDECIDED_ADDITION")));

        Assert.All(
            new[] { version, schema, missingCapability, additionalCapability },
            error => Assert.Equal("CONTRACT_VERSION_MISMATCH", error.Code));
    }
}
