using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

public sealed class NewMesIngestContractFreezeTests
{
    [Fact]
    public void Contract_discovery_returns_exact_version_and_only_the_frozen_v2_capability_set()
    {
        Assert.Equal("2026.08.new-mes-ingest.v2.1", NewMesIngestContract.Version);
        Assert.Equal(28, NewMesIngestContract.SchemaVersion);
        Assert.Equal(
            "EXACT_VERSION_SCHEMA_AND_CAPABILITIES",
            NewMesIngestContract.CompatibilityPolicy);
        Assert.Equal("/openapi/v2.json", NewMesIngestContract.OpenApiDocumentPath);
        Assert.Equal(
            "ORDINAL_CASE_SENSITIVE_WHITESPACE_PRESERVING",
            NewMesIngestContract.KeyComparison);

        Assert.Equal(
            [
                "CONTRACT_DISCOVERY|2.0|GET /api/v2/contract",
                "CURRENT_INGEST_ATTENTION|2.0|GET /api/v2/current-ingest-attention",
                "DEMAND_SERIES|2.0|GET /api/v2/demand-series;GET /api/v2/demand-series/by-key;GET /api/v2/demand-series/{seriesId}",
                "ERROR_SEARCH|2.0|GET /api/v2/error-search;GET /api/v2/error-search/{seriesId};GET /api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations",
                "EXTERNALLY_READABLE_DEMAND_CATALOG|2.0|GET /api/v2/externally-readable-demand-catalog",
                "POLL_HEALTH_AND_EVIDENCE|2.0|GET /api/v2/poll-traces/{pollTraceId};GET /api/v2/absence-authority;GET /api/v2/absence-authority/{hostSessionId};GET /api/v2/task-type-protections;GET /api/v2/task-type-protections/{workType}",
                "READABILITY_AUDIT|2.0|GET /api/v2/readability-audit;GET /api/v2/readability-audit/{demandId}",
                "SERIES_ERROR_CATALOG|2.0|GET /api/v2/contract",
                "WATCH_OVERVIEW|2.0|GET /api/v2/watch-overview",
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
    public void Contract_compatibility_requires_exact_version_schema_and_capability_versions()
    {
        var capabilities = NewMesIngestContract.Capabilities
            .Select(capability => new NewMesIngestCapabilityIdentity(
                capability.Id,
                capability.Version))
            .ToArray();

        NewMesIngestContract.RequireExactCompatibility(
            NewMesIngestContract.Version,
            NewMesIngestContract.SchemaVersion,
            capabilities);

        var version = Assert.Throws<NewMesIngestContractMismatchException>(() =>
            NewMesIngestContract.RequireExactCompatibility(
                "2026.08.new-mes-ingest.v2.0",
                NewMesIngestContract.SchemaVersion,
                capabilities));
        var schema = Assert.Throws<NewMesIngestContractMismatchException>(() =>
            NewMesIngestContract.RequireExactCompatibility(
                NewMesIngestContract.Version,
                NewMesIngestContract.SchemaVersion + 1,
                capabilities));
        var missingCapability = Assert.Throws<NewMesIngestContractMismatchException>(() =>
            NewMesIngestContract.RequireExactCompatibility(
                NewMesIngestContract.Version,
                NewMesIngestContract.SchemaVersion,
                capabilities.Skip(1)));
        var additionalCapability = Assert.Throws<NewMesIngestContractMismatchException>(() =>
            NewMesIngestContract.RequireExactCompatibility(
                NewMesIngestContract.Version,
                NewMesIngestContract.SchemaVersion,
                capabilities.Append(new NewMesIngestCapabilityIdentity(
                    "UNDECIDED_ADDITION",
                    "1.0"))));
        var capabilityVersion = Assert.Throws<NewMesIngestContractMismatchException>(() =>
            NewMesIngestContract.RequireExactCompatibility(
                NewMesIngestContract.Version,
                NewMesIngestContract.SchemaVersion,
                capabilities.Select(capability =>
                    capability.Id == "DEMAND_SERIES"
                        ? capability with { Version = "1.0" }
                        : capability)));
        var nullCapability = Assert.Throws<NewMesIngestContractMismatchException>(() =>
            NewMesIngestContract.RequireExactCompatibility(
                NewMesIngestContract.Version,
                NewMesIngestContract.SchemaVersion,
                capabilities.Cast<NewMesIngestCapabilityIdentity?>().Append(null)));

        Assert.All(
            new[]
            {
                version,
                schema,
                missingCapability,
                additionalCapability,
                capabilityVersion,
                nullCapability,
            },
            error => Assert.Equal("CONTRACT_VERSION_MISMATCH", error.Code));
    }
}
