namespace MesIngest.Core.SeriesProjection;

public static class HistoryCleanupRunStatuses
{
    public const string NotRun = "NOT_RUN";
    public const string Running = "RUNNING";
    public const string Succeeded = "SUCCEEDED";
    public const string BudgetExhausted = "BUDGET_EXHAUSTED";
    public const string YieldedToPoll = "YIELDED_TO_POLL";
    public const string Interrupted = "INTERRUPTED";
    public const string Failed = "FAILED";

    public static IReadOnlyList<string> All { get; } =
        [NotRun, Running, Succeeded, BudgetExhausted, YieldedToPoll, Interrupted, Failed];
}

public static class HistoryCleanupFailureCodes
{
    public const string BatchFailed = "HISTORY_CLEANUP_BATCH_FAILED";
}

public sealed record HistoryRawCleanupBatchResult(
    DateTimeOffset AdvancedAt,
    DateTimeOffset RawObservationCutoff,
    int ExpiredPollTraceCount,
    int DeletedRawObservationCount,
    DateTimeOffset EarliestAvailableHostUtc,
    bool HasMoreExpiredPollTraces);

public sealed record HistoryCleanupStateSnapshot(
    string Status,
    string? RunId,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastCompletedAt,
    DateTimeOffset? LastSuccessfulAt,
    DateTimeOffset? NextCheckAt,
    int LastExpiredPollTraceCount,
    int LastDeletedRawObservationCount,
    int LastDeletedSeriesCount,
    long TotalExpiredPollTraceCount,
    long TotalDeletedRawObservationCount,
    long TotalDeletedSeriesCount,
    DateTimeOffset? EarliestAvailableHostUtc,
    string? LastFailureCode,
    string? LastFailureReason)
{
    public static HistoryCleanupStateSnapshot NotRun { get; } = new(
        HistoryCleanupRunStatuses.NotRun,
        RunId: null,
        LastStartedAt: null,
        LastCompletedAt: null,
        LastSuccessfulAt: null,
        NextCheckAt: null,
        LastExpiredPollTraceCount: 0,
        LastDeletedRawObservationCount: 0,
        LastDeletedSeriesCount: 0,
        TotalExpiredPollTraceCount: 0,
        TotalDeletedRawObservationCount: 0,
        TotalDeletedSeriesCount: 0,
        EarliestAvailableHostUtc: null,
        LastFailureCode: null,
        LastFailureReason: null);

    internal HistoryCleanupStateSnapshot Begin(
        string runId,
        DateTimeOffset startedAt,
        DateTimeOffset nextCheckAt) => this with
    {
        Status = HistoryCleanupRunStatuses.Running,
        RunId = runId,
        LastStartedAt = startedAt,
        LastCompletedAt = null,
        NextCheckAt = nextCheckAt,
        LastExpiredPollTraceCount = 0,
        LastDeletedRawObservationCount = 0,
        LastDeletedSeriesCount = 0,
    };

    internal HistoryCleanupStateSnapshot RecordRawProgress(
        int expiredPollTraceCount,
        int deletedRawObservationCount,
        DateTimeOffset earliestAvailableHostUtc) => this with
    {
        LastExpiredPollTraceCount = checked(LastExpiredPollTraceCount + expiredPollTraceCount),
        LastDeletedRawObservationCount = checked(
            LastDeletedRawObservationCount + deletedRawObservationCount),
        TotalExpiredPollTraceCount = checked(TotalExpiredPollTraceCount + expiredPollTraceCount),
        TotalDeletedRawObservationCount = checked(
            TotalDeletedRawObservationCount + deletedRawObservationCount),
        EarliestAvailableHostUtc = earliestAvailableHostUtc,
    };

    internal HistoryCleanupStateSnapshot RecordSeriesProgress() => this with
    {
        LastDeletedSeriesCount = checked(LastDeletedSeriesCount + 1),
        TotalDeletedSeriesCount = checked(TotalDeletedSeriesCount + 1),
    };

    internal HistoryCleanupStateSnapshot Complete(
        string status,
        DateTimeOffset completedAt,
        DateTimeOffset nextCheckAt) => this with
    {
        Status = status,
        LastCompletedAt = completedAt,
        LastSuccessfulAt = status == HistoryCleanupRunStatuses.Interrupted
            ? LastSuccessfulAt
            : completedAt,
        NextCheckAt = nextCheckAt,
        LastFailureCode = null,
        LastFailureReason = null,
    };

    internal HistoryCleanupStateSnapshot Fail(
        DateTimeOffset failedAt,
        DateTimeOffset nextCheckAt,
        string failureCode,
        string failureReason) => this with
    {
        Status = HistoryCleanupRunStatuses.Failed,
        LastCompletedAt = failedAt,
        NextCheckAt = nextCheckAt,
        LastFailureCode = failureCode,
        LastFailureReason = failureReason,
    };
}

public interface IHistoryCleanupOperations
{
    Task<HistoryCleanupStateSnapshot> BeginHistoryCleanupRunAsync(
        string runId,
        DateTimeOffset startedAt,
        DateTimeOffset nextCheckAt,
        CancellationToken cancellationToken = default);

    Task<HistoryRawCleanupBatchResult> AdvanceHistoryRetentionBatchAsync(
        string runId,
        int maximumRawObservationRows,
        int maximumPollTraces,
        CancellationToken cancellationToken = default);

    Task<RetentionEligibleSeriesCleanupResult?> CleanupNextRetentionEligibleSeriesAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<HistoryCleanupStateSnapshot> CompleteHistoryCleanupRunAsync(
        string runId,
        string status,
        DateTimeOffset completedAt,
        DateTimeOffset nextCheckAt,
        CancellationToken cancellationToken = default);

    Task<HistoryCleanupStateSnapshot> FailHistoryCleanupRunAsync(
        string runId,
        DateTimeOffset failedAt,
        DateTimeOffset nextCheckAt,
        string failureCode,
        string failureReason,
        CancellationToken cancellationToken = default);

    Task<HistoryCleanupStateSnapshot> ReadHistoryCleanupStateAsync(
        CancellationToken cancellationToken = default);
}
