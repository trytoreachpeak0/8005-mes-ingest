namespace MesIngest.Host;

internal static class SublotBoxCountEndpoints
{
    internal static async Task<IResult> GetAsync(
        HttpRequest request,
        ISublotBoxCountReader reader,
        CancellationToken cancellationToken)
    {
        if (!request.Query.TryGetValue("sublot", out var values)
            || values.Count != 1
            || string.IsNullOrWhiteSpace(values[0])
            || values[0]!.Length > Core.CanonicalSublotBoxCountQuery.MaximumSublotLength)
        {
            return Results.BadRequest(new NewMesIngestErrorDto(
                "SUBLOT_BOX_COUNT_INVALID_QUERY",
                $"Exactly one non-blank sublot of at most {Core.CanonicalSublotBoxCountQuery.MaximumSublotLength} characters is required."));
        }

        var sublot = values[0]!;
        var result = await reader.ReadAsync(sublot, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            SublotBoxCountReadOutcome.Success => Results.Ok(new SublotBoxCountDto(
                Core.CanonicalSublotBoxCountQuery.Id,
                sublot,
                result.MaxBoxCount!.Value,
                result.ObservedAt)),
            SublotBoxCountReadOutcome.InvalidResult => Results.UnprocessableEntity(
                new NewMesIngestErrorDto(result.ErrorCode, result.Error)),
            SublotBoxCountReadOutcome.Timeout => Results.Json(
                new NewMesIngestErrorDto(result.ErrorCode, result.Error),
                statusCode: StatusCodes.Status504GatewayTimeout),
            _ => Results.Json(
                new NewMesIngestErrorDto(result.ErrorCode, result.Error),
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }
}

/// <summary>One fresh, identity-bound result from the approved read-only SUBLOT_BOX_COUNT query.</summary>
internal sealed record SublotBoxCountDto(
    string QueryId,
    string Sublot,
    int MaxBoxCount,
    DateTimeOffset ObservedAt);
