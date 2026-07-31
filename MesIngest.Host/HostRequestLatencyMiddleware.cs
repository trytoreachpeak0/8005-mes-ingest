using System.Diagnostics;
using MesIngest.Core;

namespace MesIngest.Host;

/// <summary>
/// Propagates X-Correlation-Id and records Host API endpoint latency (status, elapsed, no secrets).
/// </summary>
internal sealed class HostRequestLatencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILatencyTelemetry _telemetry;

    public HostRequestLatencyMiddleware(
        RequestDelegate next,
        ILatencyTelemetry telemetry)
    {
        _next = next;
        _telemetry = telemetry;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        LatencyCorrelation.Id = correlationId;
        context.Response.Headers[LatencyHeaders.CorrelationId] = correlationId;

        var sw = Stopwatch.StartNew();
        string? stageOverride = null;
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            stageOverride = LatencyStages.HostAbort;
            throw;
        }
        finally
        {
            sw.Stop();
            Record(
                context,
                correlationId,
                sw.ElapsedMilliseconds,
                stageOverride,
                statusCode: stageOverride == LatencyStages.HostAbort ? 499 : context.Response.StatusCode);
            LatencyCorrelation.Id = null;
        }
    }

    private void Record(
        HttpContext context,
        string correlationId,
        long elapsedMs,
        string? stage,
        int statusCode)
    {
        var resolvedStage = stage
            ?? (statusCode >= 500
                ? LatencyStages.HttpError
                : statusCode >= 400
                    ? LatencyStages.HttpStatus
                : LatencyStages.HttpOk);

        var endpoint = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        var evt = new LatencyEvent(
            CorrelationId: correlationId,
            Component: LatencyComponents.Host,
            Stage: resolvedStage,
            ElapsedMs: elapsedMs,
            StatusCode: statusCode,
            Endpoint: endpoint);

        _telemetry.Record(evt);
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(LatencyHeaders.CorrelationId, out var values))
        {
            var incoming = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(incoming))
            {
                return incoming.Trim();
            }
        }

        return Guid.NewGuid().ToString("N");
    }
}
