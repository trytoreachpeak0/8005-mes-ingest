using MesIngest.Core;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesIngest.Tests;

public sealed class MesTaskUnionPollRunnerTests
{
    [Theory]
    [InlineData(MesTaskUnionRoundOutcome.Success)]
    [InlineData(MesTaskUnionRoundOutcome.Failure)]
    [InlineData(MesTaskUnionRoundOutcome.Incomplete)]
    public async Task RunOnce_reads_and_commits_exactly_one_causal_round(
        MesTaskUnionRoundOutcome outcome)
    {
        var round = CreateRound(outcome, "poll-runner");
        var source = new RecordingRoundSource(round);
        var projection = new RecordingProjection();
        var runner = new MesTaskUnionPollRunner(source, new RoundIngestor(projection));

        var receipt = await runner.RunOnceAsync();

        Assert.Equal("poll-runner", receipt.PollTraceId);
        Assert.Same(round, Assert.Single(projection.Rounds));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Host_cancellation_stops_before_projection_commit()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var projection = new RecordingProjection();
        var runner = new MesTaskUnionPollRunner(
            new RecordingRoundSource(CreateRound(MesTaskUnionRoundOutcome.Success, "cancelled")),
            new RoundIngestor(projection));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunOnceAsync(cts.Token));

        Assert.Empty(projection.Rounds);
    }

    [Fact]
    public async Task Hosted_service_keeps_polling_after_unrelated_Watch_closes_and_stops_only_with_Host()
    {
        var source = new RepeatingRoundSource();
        var projection = new RecordingProjection();
        var service = new MesTaskUnionPollHostedService(
            new StoragePressureGuardedPollRunner(
                new MesTaskUnionPollRunner(source, new RoundIngestor(projection)),
                new AllowStoragePressurePollGate()),
            new MesIngestHostOptions
            {
                ContinuousPollEnabled = true,
                PostPollDelaySeconds = 1,
            },
            NullLogger<MesTaskUnionPollHostedService>.Instance);
        using var watchLifetime = new CancellationTokenSource();

        await service.StartAsync(CancellationToken.None);
        await source.WaitForCallsAsync(1, TimeSpan.FromSeconds(5));
        watchLifetime.Cancel();
        await source.WaitForCallsAsync(2, TimeSpan.FromSeconds(5));

        Assert.True(source.Calls >= 2);
        Assert.True(projection.Rounds.Count >= 2);
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(stopTimeout.Token);
        var stoppedAt = source.Calls;
        await Task.Delay(100);
        Assert.Equal(stoppedAt, source.Calls);
    }

    private static MesTaskUnionRound CreateRound(
        MesTaskUnionRoundOutcome outcome,
        string pollTraceId)
    {
        // The parentheses are load-bearing on the golden machine's pinned SDK 8.0.4xx:
        // its C# 12 parser reads `Success ? [` as the nullable array type `Success?[]`
        // and fails. Newer compilers resolve the ambiguity, so this only breaks where
        // the packaged release gate builds.
        MesTaskUnionObservation[] observations = (outcome is MesTaskUnionRoundOutcome.Success)
            ? [new MesTaskUnionObservation("T", "S", null, null, null, null, null)]
            : [];

        return new(
            pollTraceId,
            CanonicalMesTaskUnionQuery.QueryVersion,
            outcome,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            observations);
    }

    private sealed class RecordingRoundSource(MesTaskUnionRound round) : IMesTaskUnionRoundSource
    {
        public int Calls { get; private set; }

        public Task<MesTaskUnionRound> ReadRoundAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(round);
        }
    }

    private sealed class RepeatingRoundSource : IMesTaskUnionRoundSource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<MesTaskUnionRound> ReadRoundAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            return Task.FromResult(CreateRound(MesTaskUnionRoundOutcome.Success, $"host-poll-{call}"));
        }

        public async Task WaitForCallsAsync(int expected, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (Calls < expected)
            {
                await Task.Delay(1, cts.Token);
            }
        }
    }

    private sealed class RecordingProjection : IMesIngestProjection
    {
        private readonly object _sync = new();
        private readonly List<MesTaskUnionRound> _rounds = [];
        public IReadOnlyList<MesTaskUnionRound> Rounds
        {
            get
            {
                lock (_sync)
                {
                    return _rounds.ToArray();
                }
            }
        }

        public Task BeginHostSessionAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RoundCommitReceipt> CommitRoundAsync(
            MesTaskUnionRound round,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _rounds.Add(round);
            }

            return Task.FromResult(new RoundCommitReceipt(
                round.PollTraceId,
                round.Outcome,
                round.Outcome is MesTaskUnionRoundOutcome.Success ? "commit" : null,
                [],
                [],
                false));
        }

        public Task<DemandSeriesSnapshot?> GetDemandSeriesByKeyAsync(string workType, string sublot, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DemandSeriesSnapshot?> GetDemandSeriesAsync(string seriesId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DemandSeriesListSnapshot> ListDemandSeriesAsync(DemandSeriesBrowseQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DemandSeriesDetailSnapshot?> GetDemandSeriesAtSnapshotAsync(string seriesId, string snapshotReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ReadabilityAuditListSnapshot> ListReadabilityAuditAsync(ReadabilityAuditQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ReadabilityAuditDetailSnapshot?> GetReadabilityAuditDetailAsync(string demandId, string snapshotReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ErrorSearchListSnapshot> ListErrorSearchAsync(ErrorSearchQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ErrorSearchDetailSnapshot?> GetErrorSearchDetailAsync(string seriesId, string snapshotReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ErrorSearchRawEvidenceSnapshot?> GetErrorSearchRawEvidenceAsync(string seriesId, string evidenceId, string snapshotReference, ErrorSearchRawEvidenceQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CurrentIngestAttentionSnapshot> ReadCurrentIngestAttentionAsync(CurrentIngestAttentionQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchOverviewSnapshot> ReadWatchOverviewAsync(WatchOverviewQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternallyReadableDemandCatalogRead> ReadExternallyReadableDemandCatalogAsync(ExternallyReadableDemandCatalogIdentity? knownIdentity = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HistoricalObjectReadResult<PollTraceSnapshot>> GetPollTraceAsync(string pollTraceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HistoryRetentionAdvanceResult> AdvanceHistoryRetentionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RetentionEligibleSeriesCleanupResult?> CleanupNextRetentionEligibleSeriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AbsenceAuthoritySnapshot> GetAbsenceAuthorityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AbsenceAuthoritySnapshot?> GetAbsenceAuthorityAsync(string hostSessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TaskTypeProtectionSnapshot>> ListTaskTypeProtectionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskTypeProtectionSnapshot?> GetTaskTypeProtectionAsync(string workType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AllowStoragePressurePollGate : IStoragePressurePollGate
    {
        public Task<bool> CanQueryMesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }
}
