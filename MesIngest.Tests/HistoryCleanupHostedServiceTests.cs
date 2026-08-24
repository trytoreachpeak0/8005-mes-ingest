using MesIngest.Host;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesIngest.Tests;

public sealed class HistoryCleanupHostedServiceTests
{
    [Fact]
    public async Task Host_checks_cleanup_once_per_default_hour_without_an_external_scheduler()
    {
        var startedAt = new DateTimeOffset(2026, 8, 24, 1, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimerTimeProvider(startedAt);
        var runner = new RecordingHistoryCleanupBatchRunner();
        var service = new HistoryCleanupHostedService(
            runner,
            new MesIngestHostOptions(),
            clock,
            NullLogger<HistoryCleanupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.Equal(0, runner.RunCount);

        clock.Advance(TimeSpan.FromHours(1).Subtract(TimeSpan.FromTicks(1)));
        Assert.Equal(0, runner.RunCount);

        clock.Advance(TimeSpan.FromTicks(1));
        await runner.WaitForRunsAsync(1);
        Assert.Equal(startedAt.AddHours(1), runner.StartedAt.Single());

        clock.Advance(TimeSpan.FromHours(1));
        await runner.WaitForRunsAsync(2);
        Assert.Equal(startedAt.AddHours(2), runner.StartedAt.Last());

        await service.StopAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        await Task.Yield();
        Assert.Equal(2, runner.RunCount);
    }

    [Fact]
    public async Task Hourly_checks_keep_fixed_boundaries_without_catch_up_after_a_slow_batch()
    {
        var startedAt = new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimerTimeProvider(startedAt);
        var runner = new SlowFirstHistoryCleanupBatchRunner(clock);
        var service = new HistoryCleanupHostedService(
            runner,
            new MesIngestHostOptions(),
            clock,
            NullLogger<HistoryCleanupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        await runner.WaitForRunsAsync(1);
        Assert.Equal(startedAt.AddHours(1), runner.ScheduledAt[0]);
        Assert.Equal(startedAt.AddHours(1).AddMinutes(10), clock.GetUtcNow());

        clock.Advance(TimeSpan.FromMinutes(50).Subtract(TimeSpan.FromTicks(1)));
        Assert.Equal(1, runner.RunCount);
        clock.Advance(TimeSpan.FromTicks(1));
        await runner.WaitForRunsAsync(2);
        Assert.Equal(startedAt.AddHours(2), runner.ScheduledAt[1]);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Scheduler_logs_only_the_exception_type_when_its_runner_fails()
    {
        var startedAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimerTimeProvider(startedAt);
        var logger = new CapturingLogger<HistoryCleanupHostedService>();
        var service = new HistoryCleanupHostedService(
            new ThrowingHistoryCleanupBatchRunner(),
            new MesIngestHostOptions(),
            clock,
            logger);

        await service.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        await logger.Logged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Null(logger.Exception);
        Assert.DoesNotContain("secret scheduler detail", logger.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), logger.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingHistoryCleanupBatchRunner : IHistoryCleanupBatchRunner
    {
        private readonly TaskCompletionSource _firstRun = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRun = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCount;

        public int RunCount => Volatile.Read(ref _runCount);

        public List<DateTimeOffset> StartedAt { get; } = [];

        public Task<DateTimeOffset> RunBatchAsync(
            DateTimeOffset scheduledAt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (StartedAt)
            {
                StartedAt.Add(scheduledAt);
            }

            var count = Interlocked.Increment(ref _runCount);
            (count == 1 ? _firstRun : _secondRun).TrySetResult();
            return Task.FromResult(scheduledAt.AddHours(1));
        }

        public async Task WaitForRunsAsync(int count)
        {
            await (count == 1 ? _firstRun.Task : _secondRun.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class SlowFirstHistoryCleanupBatchRunner(ManualTimerTimeProvider clock) :
        IHistoryCleanupBatchRunner
    {
        private readonly TaskCompletionSource _firstRun = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRun = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCount;

        public int RunCount => Volatile.Read(ref _runCount);

        public List<DateTimeOffset> ScheduledAt { get; } = [];

        public Task<DateTimeOffset> RunBatchAsync(
            DateTimeOffset scheduledAt,
            CancellationToken cancellationToken)
        {
            ScheduledAt.Add(scheduledAt);
            var count = Interlocked.Increment(ref _runCount);
            if (count == 1)
            {
                clock.Advance(TimeSpan.FromMinutes(10));
                _firstRun.TrySetResult();
            }
            else
            {
                _secondRun.TrySetResult();
            }

            return Task.FromResult(scheduledAt.AddHours(1));
        }

        public Task WaitForRunsAsync(int count) =>
            (count == 1 ? _firstRun.Task : _secondRun.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ThrowingHistoryCleanupBatchRunner : IHistoryCleanupBatchRunner
    {
        public Task<DateTimeOffset> RunBatchAsync(
            DateTimeOffset scheduledAt,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("secret scheduler detail");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public TaskCompletionSource Logged { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string Message { get; private set; } = string.Empty;

        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
            Exception = exception;
            Logged.TrySetResult();
        }
    }
}
