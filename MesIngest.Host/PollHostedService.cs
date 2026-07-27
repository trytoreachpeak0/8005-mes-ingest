using MesIngest.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MesIngest.Host;

/// <summary>
/// Windows Service / console background poll: single-flight rounds with post-completion delay.
/// </summary>
public sealed class PollHostedService : BackgroundService
{
    private readonly IngestRoundRunner _runner;
    private readonly MesIngestHostOptions _options;
    private readonly ILogger<PollHostedService> _logger;

    public PollHostedService(
        IngestRoundRunner runner,
        MesIngestHostOptions options,
        ILogger<PollHostedService> logger)
    {
        _runner = runner;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ContinuousPollEnabled)
        {
            _logger.LogInformation("Continuous poll disabled; PollHostedService idle.");
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Max(0, _options.PostPollDelaySeconds));
        _logger.LogInformation(
            "Starting single-flight poll loop (post-delay={Delay}, query-timeout={Timeout}s).",
            delay,
            _options.QueryTimeoutSeconds);

        await SingleFlightPollLoop.RunAsync(
            runRound: async ct =>
            {
                try
                {
                    await _runner.RunOnceAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Ingest round failed; continuing single-flight poll loop.");
                    throw;
                }
            },
            postPollDelay: delay,
            cancellationToken: stoppingToken);
    }
}
