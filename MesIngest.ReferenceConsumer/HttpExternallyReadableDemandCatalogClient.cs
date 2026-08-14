using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.ReferenceConsumer;

/// <summary>
/// Reads the complete MesIngest catalog over its public HTTP contract. The
/// caller owns the supplied <see cref="HttpClient"/> and should configure its
/// base address and transport lifetime.
/// </summary>
public sealed class HttpExternallyReadableDemandCatalogClient
    : IExternallyReadableDemandCatalogClient
{
    public const string ContractPath = "/api/v2/contract";

    public const string CatalogPath = "/api/v2/externally-readable-demand-catalog";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public HttpExternallyReadableDemandCatalogClient(HttpClient httpClient) =>
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<ExternallyReadableDemandCatalogRead> ReadAsync(
        long? knownRevision,
        CancellationToken cancellationToken = default)
    {
        if (knownRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(knownRevision),
                "A catalog revision cannot be negative.");
        }

        await RequireCompatibleContractAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogPath);
        if (knownRevision is long revision)
        {
            request.Headers.IfNoneMatch.Add(CreateCatalogEtag(revision));
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK
            && response.StatusCode != HttpStatusCode.NotModified)
        {
            throw new HttpRequestException(
                $"The catalog endpoint returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                inner: null,
                response.StatusCode);
        }

        var responseRevision = ParseCatalogRevision(response.Headers.ETag);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (knownRevision is null || knownRevision.Value != responseRevision)
            {
                throw new InvalidDataException(
                    "A 304 catalog response must identify the requested catalog revision.");
            }

            return ExternallyReadableDemandCatalogRead.Unchanged(responseRevision);
        }

        var body = await response.Content.ReadFromJsonAsync<CatalogDto>(
            SerializerOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The catalog endpoint returned an empty JSON body.");
        if (!string.Equals(
                body.ContractVersion,
                NewMesIngestContract.Version,
                StringComparison.Ordinal))
        {
            throw ContractMismatch(
                $"The catalog contractVersion must be '{NewMesIngestContract.Version}'.");
        }
        if (body.CatalogRevision != responseRevision)
        {
            throw new InvalidDataException(
                "The catalog body revision does not match the response ETag.");
        }
        if (body.Count != body.Items.Count)
        {
            throw new InvalidDataException(
                "The catalog body count does not match its item collection.");
        }

        var items = body.Items.Select(MapItem).ToArray();
        if (!items.Select(item => item.DemandId)
                .SequenceEqual(
                    items.Select(item => item.DemandId).Order(StringComparer.Ordinal),
                    StringComparer.Ordinal)
            || items.Select(item => item.DemandId).Distinct(StringComparer.Ordinal).Count()
                != items.Length)
        {
            throw new InvalidDataException(
                "The catalog items must be unique and sorted by demandId.");
        }

        var snapshot = new ExternallyReadableDemandCatalogSnapshot(
            body.CatalogRevision,
            body.ProjectionCommitId,
            body.ProjectionSequence,
            body.ProjectionCommittedAt,
            items).Validate();
        return ExternallyReadableDemandCatalogRead.Complete(snapshot);
    }

    private async Task RequireCompatibleContractAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ContractPath);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ContractDto>(
            SerializerOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw ContractMismatch("The contract discovery response returned an empty JSON body.");
        var capabilities = body.Capabilities ?? [];

        try
        {
            NewMesIngestContract.RequireExactCompatibility(
                body.ContractVersion,
                body.SchemaVersion,
                capabilities.Select(capability => capability?.Id ?? string.Empty));
        }
        catch (NewMesIngestContractMismatchException error)
        {
            throw ContractMismatch(error.Message, error);
        }

        var actualById = capabilities
            .Select(capability => capability!)
            .ToDictionary(
                capability => capability.Id!,
                StringComparer.Ordinal);
        foreach (var expected in NewMesIngestContract.Capabilities)
        {
            if (!string.Equals(
                    actualById[expected.Id].Version,
                    expected.Version,
                    StringComparison.Ordinal))
            {
                throw ContractMismatch(
                    $"Capability '{expected.Id}' must have version '{expected.Version}'.");
            }
        }
    }

    private static InvalidDataException ContractMismatch(
        string detail,
        Exception? innerException = null) =>
        new(
            $"{NewMesIngestContractMismatchException.ErrorCode}: {detail}",
            innerException);

    private static ExternallyReadableDemandSnapshot MapItem(CatalogItemDto item)
    {
        var key = item.TransportDemandKey
            ?? throw new InvalidDataException("A catalog item is missing transportDemandKey.");
        var fields = item.LiveMesFields
            ?? throw new InvalidDataException("A catalog item is missing liveMesFields.");
        return new ExternallyReadableDemandSnapshot(
            RequireText(item.DemandId, "demandId"),
            RequireText(item.SeriesId, "seriesId"),
            RequireText(key.WorkType, "transportDemandKey.workType"),
            RequireText(key.Sublot, "transportDemandKey.sublot"),
            item.Generation,
            item.DemandRevision,
            item.CreatedAt,
            item.ValueObservedAt,
            RequireText(item.ValuePollTraceId, "valuePollTraceId"),
            RequireText(item.ValueProjectionCommitId, "valueProjectionCommitId"),
            new LiveMesFieldSetSnapshot(
                fields.Area,
                fields.Eqp,
                fields.Step,
                fields.MesSourceDate,
                fields.Package));
    }

    private static EntityTagHeaderValue CreateCatalogEtag(long revision) =>
        new($"\"catalog-r{revision.ToString(CultureInfo.InvariantCulture)}\"", isWeak: true);

    private static long ParseCatalogRevision(EntityTagHeaderValue? etag)
    {
        if (etag is null || !etag.IsWeak)
        {
            throw new InvalidDataException(
                "The catalog response must carry a weak catalog ETag.");
        }

        var opaqueTag = etag.Tag;
        const string prefix = "\"catalog-r";
        if (!opaqueTag.StartsWith(prefix, StringComparison.Ordinal)
            || opaqueTag[^1] != '\"'
            || !long.TryParse(
                opaqueTag.AsSpan(prefix.Length, opaqueTag.Length - prefix.Length - 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var revision)
            || revision < 0)
        {
            throw new InvalidDataException(
                "The catalog response ETag is not a catalog revision.");
        }

        return revision;
    }

    private static string RequireText(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"The catalog field '{fieldName}' is required.")
            : value;

    private sealed record CatalogDto(
        string? ContractVersion,
        long CatalogRevision,
        string? ProjectionCommitId,
        long? ProjectionSequence,
        DateTimeOffset? ProjectionCommittedAt,
        int Count,
        IReadOnlyList<CatalogItemDto> Items);

    private sealed record ContractDto(
        string? ContractVersion,
        int SchemaVersion,
        IReadOnlyList<CapabilityDto?>? Capabilities);

    private sealed record CapabilityDto(
        string? Id,
        string? Version);

    private sealed record CatalogItemDto(
        string? DemandId,
        string? SeriesId,
        TransportDemandKeyDto? TransportDemandKey,
        int Generation,
        long DemandRevision,
        DateTimeOffset CreatedAt,
        DateTimeOffset ValueObservedAt,
        string? ValuePollTraceId,
        string? ValueProjectionCommitId,
        LiveMesFieldSetDto? LiveMesFields);

    private sealed record TransportDemandKeyDto(
        string? WorkType,
        string? Sublot);

    private sealed record LiveMesFieldSetDto(
        string? Area,
        string? Eqp,
        string? Step,
        DateTimeOffset? MesSourceDate,
        string? Package);
}
