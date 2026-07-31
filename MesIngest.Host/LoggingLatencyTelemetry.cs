using MesIngest.Core;

namespace MesIngest.Host;

/// <summary>
/// Emits field-comparable latency lines via <see cref="ILogger"/> for A/B/C plant triage.
/// </summary>
internal sealed class LoggingLatencyTelemetry : ILatencyTelemetry
{
    private readonly ILogger _logger;

    public LoggingLatencyTelemetry(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Record(LatencyEvent evt) =>
        _logger.LogInformation("{Latency}", LatencyLogFormatter.Format(evt));
}
