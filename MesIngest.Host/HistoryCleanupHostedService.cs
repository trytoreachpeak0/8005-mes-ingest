namespace MesIngest.Host;

public interface IHistoryCleanupBatchRunner
{
    Task RunBatchAsync(
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// The single Host-owned schedule for bounded history cleanup. The runner owns
/// one batch; this service owns only the hourly lifetime and cancellation.
/// </summary>
public sealed class HistoryCleanupHostedService : BackgroundService
{
    private readonly IHistoryCleanupBatchRunner _runner;
    private readonly MesIngestHostOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HistoryCleanupHostedService> _logger;

    public HistoryCleanupHostedService(
        IHistoryCleanupBatchRunner runner,
        MesIngestHostOptions options,
        TimeProvider timeProvider,
        ILogger<HistoryCleanupHostedService> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.HistoryCleanupCheckIntervalSeconds);
        var nextScheduledAt = _timeProvider.GetUtcNow().ToUniversalTime().Add(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = nextScheduledAt - _timeProvider.GetUtcNow().ToUniversalTime();
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
                }

                var scheduledAt = _timeProvider.GetUtcNow().ToUniversalTime();
                if (scheduledAt < nextScheduledAt)
                {
                    scheduledAt = nextScheduledAt;
                }

                await _runner.RunBatchAsync(
                    scheduledAt,
                    stoppingToken).ConfigureAwait(false);
                nextScheduledAt = scheduledAt;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "History cleanup batch failed; the Host will retry at the next check.");
            }

            var candidate = nextScheduledAt.Add(interval);
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            nextScheduledAt = candidate > now ? candidate : now.Add(interval);
        }
    }
}
