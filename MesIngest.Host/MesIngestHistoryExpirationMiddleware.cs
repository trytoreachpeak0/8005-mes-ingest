using MesIngest.Core.SeriesProjection;

namespace MesIngest.Host;

internal sealed class MesIngestHistoryExpirationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (MesIngestHistoryExpiredException exception)
        {
            context.Response.StatusCode = StatusCodes.Status410Gone;
            await context.Response.WriteAsJsonAsync(
                HistoricalReadErrorDto.From(
                    PollEvidenceErrorCodes.MesIngestHistoryExpired,
                    exception.Message,
                    exception.Boundary),
                cancellationToken: context.RequestAborted);
        }
    }
}
