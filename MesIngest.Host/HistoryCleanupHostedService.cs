namespace MesIngest.Host;

public interface IHistoryCleanupBatchRunner
{
    Task<DateTimeOffset> RunBatchAsync(
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken);
}

internal static class HistoryCleanupSchedule
{
    public static DateTimeOffset NextCheck(
        DateTimeOffset scheduledAt,
        DateTimeOffset completedAt,
        TimeSpan interval)
    {
        var candidate = scheduledAt.ToUniversalTime().Add(interval);
        var completedUtc = completedAt.ToUniversalTime();
        return candidate > completedUtc ? candidate : completedUtc.Add(interval);
    }
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

                nextScheduledAt = await _runner.RunBatchAsync(
                    scheduledAt,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "History cleanup scheduler failed ({ExceptionType}); the Host will retry.",
                    exception.GetType().Name);
                nextScheduledAt = HistoryCleanupSchedule.NextCheck(
                    nextScheduledAt,
                    _timeProvider.GetUtcNow(),
                    interval);
            }
        }
    }
}
