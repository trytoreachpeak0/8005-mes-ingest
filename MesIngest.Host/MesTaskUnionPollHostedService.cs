using MesIngest.Core;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Host;

/// <summary>
/// V2 Windows Service / console poll owner. Its lifetime is the Host lifetime;
/// no Watch process, window, or client cancellation token participates.
/// </summary>
public sealed class MesTaskUnionPollHostedService : BackgroundService
{
    private readonly StoragePressureGuardedPollRunner _runner;
    private readonly MesIngestHostOptions _options;
    private readonly ILogger<MesTaskUnionPollHostedService> _logger;
    private readonly IngestWorkPriorityGate _workPriorityGate;

    public MesTaskUnionPollHostedService(
        StoragePressureGuardedPollRunner runner,
        MesIngestHostOptions options,
        ILogger<MesTaskUnionPollHostedService> logger,
        IngestWorkPriorityGate? workPriorityGate = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workPriorityGate = workPriorityGate ?? new IngestWorkPriorityGate();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ContinuousPollEnabled)
        {
            _logger.LogInformation("Continuous V2 Oracle poll disabled; service idle.");
            return;
        }

        var pollStartInterval = TimeSpan.FromSeconds(_options.PollStartIntervalSeconds);
        _logger.LogInformation(
            "Starting V2 Oracle single-flight poll loop (start-to-start={Interval}, failure-backoff=60/120/300s, command-timeout={Timeout}s).",
            pollStartInterval,
            _options.QueryTimeoutSeconds);

        await SingleFlightPollLoop.RunAsync(
            runRound: async cancellationToken =>
            {
                using var priorityLease = await _workPriorityGate.EnterPollAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    var receipt = await _runner.RunOnceIfAllowedAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (receipt is null)
                    {
                        _logger.LogCritical(
                            "MES query skipped because the durable StoragePressurePause gate is closed.");
                        return true;
                    }

                    _logger.LogInformation(
                        "V2 Oracle round {PollTraceId} completed as {Outcome}; projectionCommit={ProjectionCommitId}.",
                        receipt.PollTraceId,
                        receipt.Outcome,
                        receipt.ProjectionCommitId);
                    return receipt.Outcome is MesTaskUnionRoundOutcome.Success;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Source failures are durable round diagnostics. Failures after the
                    // source (SQL open/schema/commit) have no PollTrace, so retain a
                    // sanitized operational signal before the shared loop continues.
                    _logger.LogError(
                        "V2 Oracle round could not be persisted ({ExceptionType}); continuing single-flight poll loop.",
                        exception.GetType().Name);
                    throw;
                }
            },
            pollStartInterval: pollStartInterval,
            cancellationToken: stoppingToken).ConfigureAwait(false);
    }
}
