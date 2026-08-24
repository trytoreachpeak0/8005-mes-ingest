using MesIngest.Core.SeriesProjection;

namespace MesIngest.Host;

public sealed class IngestWorkPriorityGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _waitingPolls;

    public async ValueTask<IDisposable> EnterPollAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waitingPolls);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(_gate);
        }
        finally
        {
            Interlocked.Decrement(ref _waitingPolls);
        }
    }

    public IDisposable? TryEnterCleanup()
    {
        if (Volatile.Read(ref _waitingPolls) > 0 || !_gate.Wait(0))
        {
            return null;
        }

        if (Volatile.Read(ref _waitingPolls) == 0)
        {
            return new Lease(_gate);
        }

        _gate.Release();
        return null;
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

public sealed class HistoryCleanupBatchRunner : IHistoryCleanupBatchRunner
{
    internal const int MaximumRawObservationRowsPerTransaction = 25_000;
    internal const int MaximumPollTracesPerTransaction = 50;

    private readonly IHistoryCleanupOperations _operations;
    private readonly MesIngestHostOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IngestWorkPriorityGate _priorityGate;
    private readonly ILogger<HistoryCleanupBatchRunner> _logger;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    public HistoryCleanupBatchRunner(
        IHistoryCleanupOperations operations,
        MesIngestHostOptions options,
        TimeProvider timeProvider,
        IngestWorkPriorityGate priorityGate,
        ILogger<HistoryCleanupBatchRunner> logger)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _priorityGate = priorityGate ?? throw new ArgumentNullException(nameof(priorityGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunBatchAsync(
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken)
    {
        if (!await _singleFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var runId = Guid.NewGuid().ToString("N");
        var startedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        var nextCheckAt = scheduledAt.ToUniversalTime().AddSeconds(
            _options.HistoryCleanupCheckIntervalSeconds);
        var deadline = startedAt.AddSeconds(_options.HistoryCleanupTimeBudgetSeconds);
        try
        {
            await _operations.BeginHistoryCleanupRunAsync(
                runId,
                startedAt,
                nextCheckAt,
                cancellationToken).ConfigureAwait(false);

            var deletedRawRows = 0;
            var hasMoreRaw = true;
            var yieldedToPoll = false;
            while (hasMoreRaw
                   && deletedRawRows < _options.HistoryCleanupMaximumRawObservationRowsPerBatch
                   && _timeProvider.GetUtcNow().ToUniversalTime() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var lease = _priorityGate.TryEnterCleanup();
                if (lease is null)
                {
                    yieldedToPoll = true;
                    break;
                }

                var remaining = _options.HistoryCleanupMaximumRawObservationRowsPerBatch
                    - deletedRawRows;
                var result = await _operations.AdvanceHistoryRetentionBatchAsync(
                    runId,
                    Math.Min(MaximumRawObservationRowsPerTransaction, remaining),
                    MaximumPollTracesPerTransaction,
                    cancellationToken).ConfigureAwait(false);
                deletedRawRows = checked(deletedRawRows + result.DeletedRawObservationCount);
                hasMoreRaw = result.HasMoreExpiredPollTraces;
                if (result.ExpiredPollTraceCount == 0 && result.DeletedRawObservationCount == 0)
                {
                    break;
                }
            }

            var deletedSeries = 0;
            var seriesExhausted = false;
            if (!hasMoreRaw && !yieldedToPoll)
            {
                while (deletedSeries < _options.HistoryCleanupMaximumSeriesPerBatch
                       && _timeProvider.GetUtcNow().ToUniversalTime() < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var lease = _priorityGate.TryEnterCleanup();
                    if (lease is null)
                    {
                        yieldedToPoll = true;
                        break;
                    }

                    var cleaned = await _operations.CleanupNextRetentionEligibleSeriesAsync(
                        runId,
                        cancellationToken).ConfigureAwait(false);
                    if (cleaned is null)
                    {
                        seriesExhausted = true;
                        break;
                    }

                    deletedSeries++;
                }
            }

            var completedAt = _timeProvider.GetUtcNow().ToUniversalTime();
            var status = yieldedToPoll
                ? HistoryCleanupRunStatuses.YieldedToPoll
                : hasMoreRaw
                  || (!seriesExhausted
                      && (deletedSeries >= _options.HistoryCleanupMaximumSeriesPerBatch
                          || completedAt >= deadline))
                    ? HistoryCleanupRunStatuses.BudgetExhausted
                    : HistoryCleanupRunStatuses.Succeeded;
            var state = await _operations.CompleteHistoryCleanupRunAsync(
                runId,
                status,
                completedAt,
                nextCheckAt,
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "History cleanup {Status}; rawRows={RawRows}, series={Series}, nextCheck={NextCheckAt}.",
                state.Status,
                state.LastDeletedRawObservationCount,
                state.LastDeletedSeriesCount,
                state.NextCheckAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failedAt = _timeProvider.GetUtcNow().ToUniversalTime();
            await _operations.FailHistoryCleanupRunAsync(
                runId,
                failedAt,
                nextCheckAt,
                HistoryCleanupFailureCodes.BatchFailed,
                exception.GetType().Name,
                cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "History cleanup batch failed ({ExceptionType}); nextCheck={NextCheckAt}.",
                exception.GetType().Name,
                nextCheckAt);
            throw;
        }
        finally
        {
            _singleFlight.Release();
        }
    }
}
