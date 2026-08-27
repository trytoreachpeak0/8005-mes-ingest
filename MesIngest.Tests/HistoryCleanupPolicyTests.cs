using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.Infrastructure.SqlServer;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesIngest.Tests;

public sealed class HistoryCleanupPolicyTests
{
    [Fact]
    public void Cleanup_defaults_cover_one_hour_of_baseline_rows_with_bounded_time_and_keep_fifteen_day_retention()
    {
        var options = new MesIngestHostOptions();

        Assert.Equal(3_600, options.HistoryCleanupCheckIntervalSeconds);
        Assert.Equal(210_000, options.HistoryCleanupMaximumRawObservationRowsPerBatch);
        Assert.Equal(25, options.HistoryCleanupMaximumSeriesPerBatch);
        Assert.Equal(15, options.HistoryCleanupTimeBudgetSeconds);
        Assert.Equal(TimeSpan.FromDays(15), HistoryRetentionPolicy.RawObservationAvailabilityWindow);
        Assert.Equal(TimeSpan.FromDays(15), HistoryRetentionPolicy.RetentionEligibleDemandSeriesWindow);

        using var appsettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "MesIngest.Host",
            "appsettings.json")));
        var published = appsettings.RootElement.GetProperty(MesIngestHostOptions.SectionName);
        Assert.Equal(
            options.HistoryCleanupCheckIntervalSeconds,
            published.GetProperty(nameof(options.HistoryCleanupCheckIntervalSeconds)).GetInt32());
        Assert.Equal(
            options.HistoryCleanupMaximumRawObservationRowsPerBatch,
            published.GetProperty(nameof(options.HistoryCleanupMaximumRawObservationRowsPerBatch)).GetInt32());
        Assert.Equal(
            options.HistoryCleanupMaximumSeriesPerBatch,
            published.GetProperty(nameof(options.HistoryCleanupMaximumSeriesPerBatch)).GetInt32());
        Assert.Equal(
            options.HistoryCleanupTimeBudgetSeconds,
            published.GetProperty(nameof(options.HistoryCleanupTimeBudgetSeconds)).GetInt32());
    }

    [Theory]
    [InlineData(0, 210_000, 25, 15)]
    [InlineData(3_600, 0, 25, 15)]
    [InlineData(3_600, 210_000, 0, 15)]
    [InlineData(3_600, 210_000, 25, 0)]
    [InlineData(86_401, 210_000, 25, 15)]
    [InlineData(3_600, 1_000_001, 25, 15)]
    [InlineData(3_600, 24_999, 25, 15)]
    [InlineData(3_600, 210_000, 1_001, 15)]
    [InlineData(3_600, 210_000, 25, 301)]
    public void Cleanup_options_reject_non_positive_or_unbounded_operational_budgets(
        int intervalSeconds,
        int rawObservationRows,
        int seriesCount,
        int timeBudgetSeconds)
    {
        var options = new MesIngestHostOptions
        {
            HistoryCleanupCheckIntervalSeconds = intervalSeconds,
            HistoryCleanupMaximumRawObservationRowsPerBatch = rawObservationRows,
            HistoryCleanupMaximumSeriesPerBatch = seriesCount,
            HistoryCleanupTimeBudgetSeconds = timeBudgetSeconds,
        };

        Assert.Throws<InvalidOperationException>(options.ValidateHistoryCleanupPolicy);
    }

    [Fact]
    public async Task Cleanup_batch_stops_at_the_configured_raw_row_budget()
    {
        var now = new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero);
        var operations = new RecordingCleanupOperations(rawRowsDue: 40_000);
        var runner = new HistoryCleanupBatchRunner(
            operations,
            new MesIngestHostOptions
            {
                HistoryCleanupMaximumRawObservationRowsPerBatch = 30_000,
            },
            new AdjustableTimeProvider(now),
            new IngestWorkPriorityGate(),
            NullLogger<HistoryCleanupBatchRunner>.Instance);

        await runner.RunBatchAsync(now, CancellationToken.None);

        Assert.Equal([25_000, 5_000], operations.RawRowLimits);
        Assert.Equal(30_000, operations.State.LastDeletedRawObservationCount);
        Assert.Equal(0, operations.SeriesCalls);
        Assert.Equal(HistoryCleanupRunStatuses.BudgetExhausted, operations.State.Status);
        Assert.Equal(now.AddHours(1), operations.State.NextCheckAt);
    }

    [Fact]
    public async Task Budget_interruption_finishes_the_started_series_then_the_next_batch_resumes()
    {
        var now = new DateTimeOffset(2026, 8, 24, 4, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var operations = new SeriesBudgetCleanupOperations(clock, now);
        var runner = new HistoryCleanupBatchRunner(
            operations,
            new MesIngestHostOptions(),
            clock,
            new IngestWorkPriorityGate(),
            NullLogger<HistoryCleanupBatchRunner>.Instance);

        await runner.RunBatchAsync(now, CancellationToken.None);

        Assert.Equal(["series-1"], operations.CleanedSeriesIds);
        Assert.Equal(HistoryCleanupRunStatuses.BudgetExhausted, operations.State.Status);
        Assert.Equal(1, operations.State.LastDeletedSeriesCount);
        Assert.Equal(1, operations.State.TotalDeletedSeriesCount);

        clock.SetUtcNow(now.AddHours(1));
        await runner.RunBatchAsync(now.AddHours(1), CancellationToken.None);

        Assert.Equal(["series-1", "series-2"], operations.CleanedSeriesIds);
        Assert.Equal(HistoryCleanupRunStatuses.Succeeded, operations.State.Status);
        Assert.Equal(1, operations.State.LastDeletedSeriesCount);
        Assert.Equal(2, operations.State.TotalDeletedSeriesCount);
    }

    [Fact]
    public async Task Overrunning_batch_publishes_the_same_skipped_slot_the_host_will_use()
    {
        var now = new DateTimeOffset(2026, 8, 24, 4, 30, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var operations = new SeriesBudgetCleanupOperations(clock, now);
        var runner = new HistoryCleanupBatchRunner(
            operations,
            new MesIngestHostOptions
            {
                HistoryCleanupCheckIntervalSeconds = 15,
                HistoryCleanupTimeBudgetSeconds = 15,
            },
            clock,
            new IngestWorkPriorityGate(),
            NullLogger<HistoryCleanupBatchRunner>.Instance);

        var actualNextCheckAt = await runner.RunBatchAsync(now, CancellationToken.None);

        Assert.Equal(now.AddSeconds(30), operations.State.NextCheckAt);
        Assert.Equal(operations.State.NextCheckAt, actualNextCheckAt);
    }

    [Fact]
    public async Task Poll_already_in_progress_prevents_any_cleanup_data_transaction()
    {
        var now = new DateTimeOffset(2026, 8, 24, 5, 30, 0, TimeSpan.Zero);
        var operations = new RecordingCleanupOperations(rawRowsDue: 1);
        var gate = new IngestWorkPriorityGate();
        var runner = new HistoryCleanupBatchRunner(
            operations,
            new MesIngestHostOptions(),
            new AdjustableTimeProvider(now),
            gate,
            NullLogger<HistoryCleanupBatchRunner>.Instance);
        using var pollLease = await gate.EnterPollAsync(CancellationToken.None);

        await runner.RunBatchAsync(now, CancellationToken.None);

        Assert.Empty(operations.RawRowLimits);
        Assert.Equal(HistoryCleanupRunStatuses.YieldedToPoll, operations.State.Status);
    }

    [Fact]
    public async Task Pending_poll_prevents_another_cleanup_transaction_and_cleanup_never_overlaps_itself()
    {
        var now = new DateTimeOffset(2026, 8, 24, 6, 0, 0, TimeSpan.Zero);
        var operations = new GatedCleanupOperations(now);
        var gate = new IngestWorkPriorityGate();
        var runner = new HistoryCleanupBatchRunner(
            operations,
            new MesIngestHostOptions(),
            new AdjustableTimeProvider(now),
            gate,
            NullLogger<HistoryCleanupBatchRunner>.Instance);

        var firstCleanup = runner.RunBatchAsync(now, CancellationToken.None);
        await operations.RawTransactionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await runner.RunBatchAsync(now, CancellationToken.None);
        Assert.Equal(1, operations.BeginCalls);
        Assert.Equal(1, operations.MaximumRawConcurrency);

        var pollLeaseTask = gate.EnterPollAsync(CancellationToken.None).AsTask();
        operations.ReleaseRawTransaction.TrySetResult();
        using (await pollLeaseTask.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await firstCleanup.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, operations.RawCalls);
        Assert.Equal(HistoryCleanupRunStatuses.YieldedToPoll, operations.State.Status);
    }

    [Fact]
    public async Task Cancellation_stops_the_active_transaction_without_recording_a_failure_and_releases_polling()
    {
        var now = new DateTimeOffset(2026, 8, 24, 7, 0, 0, TimeSpan.Zero);
        var operations = new GatedCleanupOperations(now);
        var gate = new IngestWorkPriorityGate();
        var runner = new HistoryCleanupBatchRunner(
            operations,
            new MesIngestHostOptions(),
            new AdjustableTimeProvider(now),
            gate,
            NullLogger<HistoryCleanupBatchRunner>.Instance);
        using var cancellation = new CancellationTokenSource();

        var cleanup = runner.RunBatchAsync(now, cancellation.Token);
        await operations.RawTransactionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cleanup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(HistoryCleanupRunStatuses.Interrupted, operations.State.Status);
        Assert.Null(operations.State.LastFailureCode);

        using var pollLease = await gate.EnterPollAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Interrupted_retry_preserves_the_previous_failure_attention_until_a_success()
    {
        var first = new DateTimeOffset(2026, 8, 24, 7, 15, 0, TimeSpan.Zero);
        var failed = HistoryCleanupStateSnapshot.NotRun
            .Begin("failed-run", first, first.AddHours(1))
            .Fail(
                first.AddSeconds(1),
                first.AddHours(1),
                HistoryCleanupFailureCodes.BatchFailed,
                nameof(InvalidOperationException));
        var interrupted = failed
            .Begin("interrupted-run", first.AddHours(1), first.AddHours(2))
            .Complete(
                HistoryCleanupRunStatuses.Interrupted,
                first.AddHours(1).AddSeconds(1),
                first.AddHours(2));

        Assert.Equal(HistoryCleanupFailureCodes.BatchFailed, interrupted.LastFailureCode);
        Assert.Equal(nameof(InvalidOperationException), interrupted.LastFailureReason);
        Assert.Equal(first.AddSeconds(1), interrupted.LastFailureAt);
        Assert.Equal("failed-run", interrupted.LastFailureRunId);
        Assert.Null(interrupted.LastSuccessfulAt);
    }

    [Fact]
    public async Task Cancellation_does_not_wait_unboundedly_for_interruption_state_persistence()
    {
        var now = new DateTimeOffset(2026, 8, 24, 7, 20, 0, TimeSpan.Zero);
        var operations = new GatedCleanupOperations(now) { HangInterruptedCompletion = true };
        var runner = new HistoryCleanupBatchRunner(
            operations,
            new MesIngestHostOptions(),
            new AdjustableTimeProvider(now),
            new IngestWorkPriorityGate(),
            NullLogger<HistoryCleanupBatchRunner>.Instance);
        using var cancellation = new CancellationTokenSource();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var cleanup = runner.RunBatchAsync(now, cancellation.Token);
        await operations.RawTransactionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cleanup.WaitAsync(TimeSpan.FromSeconds(3)));
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Cancellation took {stopwatch.Elapsed} while terminal persistence was hung.");
    }

    [Fact]
    public async Task Begin_commits_running_then_throws_reconciles_the_same_run_to_failed()
    {
        var now = new DateTimeOffset(2026, 8, 24, 7, 25, 0, TimeSpan.Zero);
        var operations = new UncertainBeginCleanupOperations(
            _ => new IOException("response lost after commit"));
        var runner = new HistoryCleanupBatchRunner(
            operations,
            new MesIngestHostOptions(),
            new AdjustableTimeProvider(now),
            new IngestWorkPriorityGate(),
            NullLogger<HistoryCleanupBatchRunner>.Instance);

        var nextCheckAt = await runner.RunBatchAsync(now, CancellationToken.None);

        Assert.Equal(now.AddHours(1), nextCheckAt);
        Assert.Equal(HistoryCleanupRunStatuses.Failed, operations.State.Status);
        Assert.Equal(HistoryCleanupFailureCodes.BatchFailed, operations.State.LastFailureCode);
        Assert.Equal(nameof(IOException), operations.State.LastFailureReason);
        Assert.Equal(operations.BegunRunId, operations.FailedRunId);
        Assert.Equal(1, operations.FailCalls);
    }

    [Fact]
    public async Task Begin_commits_running_then_caller_is_cancelled_reconciles_the_same_run_to_failed()
    {
        var now = new DateTimeOffset(2026, 8, 24, 7, 27, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var operations = new UncertainBeginCleanupOperations(token =>
        {
            cancellation.Cancel();
            return new OperationCanceledException(token);
        });
        var runner = new HistoryCleanupBatchRunner(
            operations,
            new MesIngestHostOptions(),
            new AdjustableTimeProvider(now),
            new IngestWorkPriorityGate(),
            NullLogger<HistoryCleanupBatchRunner>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunBatchAsync(now, cancellation.Token));

        Assert.Equal(HistoryCleanupRunStatuses.Failed, operations.State.Status);
        Assert.Equal(HistoryCleanupFailureCodes.BatchFailed, operations.State.LastFailureCode);
        Assert.Equal(nameof(OperationCanceledException), operations.State.LastFailureReason);
        Assert.Equal(operations.BegunRunId, operations.FailedRunId);
        Assert.Equal(1, operations.FailCalls);
    }

    [Fact]
    public async Task One_poll_trace_cannot_exceed_the_hard_cleanup_transaction_bound()
    {
        var projection = new SqlServerMesIngestProjection(
            "Server=unused;Database=unused;Integrated Security=True;Encrypt=False");
        var completedAt = new DateTimeOffset(2026, 8, 24, 7, 30, 0, TimeSpan.Zero);
        var round = new MesTaskUnionRound(
            "poll-ticket16-over-bound",
            "ticket16-policy-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-1),
            completedAt,
            new OversizedObservationList());

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => projection.CommitRoundAsync(round));

        Assert.Equal("round", exception.ParamName);
    }

    private sealed class OversizedObservationList : IReadOnlyList<MesTaskUnionObservation>
    {
        public int Count => HistoryRetentionPolicy.MaximumRawObservationsPerPollTrace + 1;

        public MesTaskUnionObservation this[int index] =>
            throw new InvalidOperationException("The bound must be checked before enumeration.");

        public IEnumerator<MesTaskUnionObservation> GetEnumerator() =>
            throw new InvalidOperationException("The bound must be checked before enumeration.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class RecordingCleanupOperations(int rawRowsDue) : IHistoryCleanupOperations
    {
        private int _rawRowsDue = rawRowsDue;

        public List<int> RawRowLimits { get; } = [];

        public int SeriesCalls { get; private set; }

        public HistoryCleanupStateSnapshot State { get; private set; } =
            HistoryCleanupStateSnapshot.NotRun;

        public Task<HistoryCleanupStateSnapshot> BeginHistoryCleanupRunAsync(
            string runId,
            DateTimeOffset startedAt,
            DateTimeOffset nextCheckAt,
            CancellationToken cancellationToken = default)
        {
            State = State.Begin(runId, startedAt, nextCheckAt);
            return Task.FromResult(State);
        }

        public Task<HistoryRawCleanupBatchResult> AdvanceHistoryRetentionBatchAsync(
            string runId,
            int maximumRawObservationRows,
            int maximumPollTraces,
            CancellationToken cancellationToken = default)
        {
            RawRowLimits.Add(maximumRawObservationRows);
            var deleted = Math.Min(_rawRowsDue, maximumRawObservationRows);
            _rawRowsDue -= deleted;
            State = State.RecordRawProgress(
                expiredPollTraceCount: deleted == 0 ? 0 : 1,
                deletedRawObservationCount: deleted,
                earliestAvailableHostUtc: new DateTimeOffset(2026, 7, 25, 3, 0, 0, TimeSpan.Zero));
            return Task.FromResult(new HistoryRawCleanupBatchResult(
                State.LastStartedAt!.Value,
                State.LastStartedAt.Value.Subtract(HistoryRetentionPolicy.RawObservationAvailabilityWindow),
                deleted == 0 ? 0 : 1,
                deleted,
                State.EarliestAvailableHostUtc!.Value,
                HasMoreExpiredPollTraces: _rawRowsDue > 0));
        }

        public Task<RetentionEligibleSeriesCleanupResult?> CleanupNextRetentionEligibleSeriesAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            SeriesCalls++;
            return Task.FromResult<RetentionEligibleSeriesCleanupResult?>(null);
        }

        public Task<HistoryCleanupStateSnapshot> CompleteHistoryCleanupRunAsync(
            string runId,
            string status,
            DateTimeOffset completedAt,
            DateTimeOffset nextCheckAt,
            CancellationToken cancellationToken = default)
        {
            State = State.Complete(status, completedAt, nextCheckAt);
            return Task.FromResult(State);
        }

        public Task<bool> TryFailHistoryCleanupRunAsync(
            string runId,
            DateTimeOffset failedAt,
            DateTimeOffset nextCheckAt,
            string failureCode,
            string failureReason,
            CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(new NotSupportedException());

        public Task<HistoryCleanupStateSnapshot> ReadHistoryCleanupStateAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(State);
    }

    private sealed class UncertainBeginCleanupOperations(
        Func<CancellationToken, Exception> exceptionFactory) : IHistoryCleanupOperations
    {
        public int FailCalls { get; private set; }

        public string? BegunRunId { get; private set; }

        public string? FailedRunId { get; private set; }

        public HistoryCleanupStateSnapshot State { get; private set; } =
            HistoryCleanupStateSnapshot.NotRun;

        public Task<HistoryCleanupStateSnapshot> BeginHistoryCleanupRunAsync(
            string runId,
            DateTimeOffset startedAt,
            DateTimeOffset nextCheckAt,
            CancellationToken cancellationToken = default)
        {
            BegunRunId = runId;
            State = State.Begin(runId, startedAt, nextCheckAt);
            return Task.FromException<HistoryCleanupStateSnapshot>(
                exceptionFactory(cancellationToken));
        }

        public Task<HistoryRawCleanupBatchResult> AdvanceHistoryRetentionBatchAsync(
            string runId,
            int maximumRawObservationRows,
            int maximumPollTraces,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Begin did not return to the caller.");

        public Task<RetentionEligibleSeriesCleanupResult?> CleanupNextRetentionEligibleSeriesAsync(
            string runId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Begin did not return to the caller.");

        public Task<HistoryCleanupStateSnapshot> CompleteHistoryCleanupRunAsync(
            string runId,
            string status,
            DateTimeOffset completedAt,
            DateTimeOffset nextCheckAt,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An uncertain Begin must use safe reconciliation.");

        public Task<bool> TryFailHistoryCleanupRunAsync(
            string runId,
            DateTimeOffset failedAt,
            DateTimeOffset nextCheckAt,
            string failureCode,
            string failureReason,
            CancellationToken cancellationToken = default)
        {
            FailCalls++;
            FailedRunId = runId;
            State = State.Fail(failedAt, nextCheckAt, failureCode, failureReason);
            return Task.FromResult(true);
        }

        public Task<HistoryCleanupStateSnapshot> ReadHistoryCleanupStateAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(State);
    }

    private sealed class SeriesBudgetCleanupOperations(
        AdjustableTimeProvider clock,
        DateTimeOffset firstRunAt) : IHistoryCleanupOperations
    {
        private readonly Queue<string> _seriesIds = new(["series-1", "series-2"]);
        private bool _advancedFirstSeriesTime;

        public List<string> CleanedSeriesIds { get; } = [];

        public HistoryCleanupStateSnapshot State { get; private set; } =
            HistoryCleanupStateSnapshot.NotRun;

        public Task<HistoryCleanupStateSnapshot> BeginHistoryCleanupRunAsync(
            string runId,
            DateTimeOffset startedAt,
            DateTimeOffset nextCheckAt,
            CancellationToken cancellationToken = default)
        {
            State = State.Begin(runId, startedAt, nextCheckAt);
            return Task.FromResult(State);
        }

        public Task<HistoryRawCleanupBatchResult> AdvanceHistoryRetentionBatchAsync(
            string runId,
            int maximumRawObservationRows,
            int maximumPollTraces,
            CancellationToken cancellationToken = default)
        {
            var boundary = clock.GetUtcNow().Subtract(HistoryRetentionPolicy.RawObservationAvailabilityWindow);
            State = State.RecordRawProgress(0, 0, boundary);
            return Task.FromResult(new HistoryRawCleanupBatchResult(
                clock.GetUtcNow(),
                boundary,
                0,
                0,
                boundary,
                HasMoreExpiredPollTraces: false));
        }

        public Task<RetentionEligibleSeriesCleanupResult?> CleanupNextRetentionEligibleSeriesAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            if (_seriesIds.Count == 0)
            {
                return Task.FromResult<RetentionEligibleSeriesCleanupResult?>(null);
            }

            var seriesId = _seriesIds.Dequeue();
            CleanedSeriesIds.Add(seriesId);
            State = State.RecordSeriesProgress();
            if (!_advancedFirstSeriesTime)
            {
                _advancedFirstSeriesTime = true;
                clock.SetUtcNow(firstRunAt.AddSeconds(15));
            }

            return Task.FromResult<RetentionEligibleSeriesCleanupResult?>(new(
                clock.GetUtcNow(),
                seriesId,
                "WIRE_TO_NITROGEN",
                $"SL-{seriesId}",
                firstRunAt,
                1));
        }

        public Task<HistoryCleanupStateSnapshot> CompleteHistoryCleanupRunAsync(
            string runId,
            string status,
            DateTimeOffset completedAt,
            DateTimeOffset nextCheckAt,
            CancellationToken cancellationToken = default)
        {
            State = State.Complete(status, completedAt, nextCheckAt);
            return Task.FromResult(State);
        }

        public Task<bool> TryFailHistoryCleanupRunAsync(
            string runId,
            DateTimeOffset failedAt,
            DateTimeOffset nextCheckAt,
            string failureCode,
            string failureReason,
            CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(new NotSupportedException());

        public Task<HistoryCleanupStateSnapshot> ReadHistoryCleanupStateAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(State);
    }

    private sealed class GatedCleanupOperations(DateTimeOffset now) : IHistoryCleanupOperations
    {
        private int _rawConcurrency;

        public TaskCompletionSource RawTransactionStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRawTransaction { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int BeginCalls { get; private set; }

        public int RawCalls { get; private set; }

        public int MaximumRawConcurrency { get; private set; }

        public bool HangInterruptedCompletion { get; init; }

        public HistoryCleanupStateSnapshot State { get; private set; } =
            HistoryCleanupStateSnapshot.NotRun;

        public Task<HistoryCleanupStateSnapshot> BeginHistoryCleanupRunAsync(
            string runId,
            DateTimeOffset startedAt,
            DateTimeOffset nextCheckAt,
            CancellationToken cancellationToken = default)
        {
            BeginCalls++;
            State = State.Begin(runId, startedAt, nextCheckAt);
            return Task.FromResult(State);
        }

        public async Task<HistoryRawCleanupBatchResult> AdvanceHistoryRetentionBatchAsync(
            string runId,
            int maximumRawObservationRows,
            int maximumPollTraces,
            CancellationToken cancellationToken = default)
        {
            RawCalls++;
            var concurrency = Interlocked.Increment(ref _rawConcurrency);
            MaximumRawConcurrency = Math.Max(MaximumRawConcurrency, concurrency);
            RawTransactionStarted.TrySetResult();
            try
            {
                await ReleaseRawTransaction.Task.WaitAsync(cancellationToken);
                var boundary = now.Subtract(HistoryRetentionPolicy.RawObservationAvailabilityWindow);
                State = State.RecordRawProgress(1, 1, boundary);
                return new HistoryRawCleanupBatchResult(
                    now,
                    boundary,
                    1,
                    1,
                    boundary,
                    HasMoreExpiredPollTraces: true);
            }
            finally
            {
                Interlocked.Decrement(ref _rawConcurrency);
            }
        }

        public Task<RetentionEligibleSeriesCleanupResult?> CleanupNextRetentionEligibleSeriesAsync(
            string runId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Poll priority should prevent a Series transaction.");

        public async Task<HistoryCleanupStateSnapshot> CompleteHistoryCleanupRunAsync(
            string runId,
            string status,
            DateTimeOffset completedAt,
            DateTimeOffset nextCheckAt,
            CancellationToken cancellationToken = default)
        {
            if (status == HistoryCleanupRunStatuses.Interrupted && HangInterruptedCompletion)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            State = State.Complete(status, completedAt, nextCheckAt);
            return State;
        }

        public Task<bool> TryFailHistoryCleanupRunAsync(
            string runId,
            DateTimeOffset failedAt,
            DateTimeOffset nextCheckAt,
            string failureCode,
            string failureReason,
            CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(new NotSupportedException());

        public Task<HistoryCleanupStateSnapshot> ReadHistoryCleanupStateAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(State);
    }
}
