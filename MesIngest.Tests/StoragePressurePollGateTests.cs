using MesIngest.Core;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesIngest.Tests;

public sealed class StoragePressurePollGateTests
{
    [Fact]
    public async Task Paused_gate_is_checked_before_the_MES_runner_and_creates_no_round()
    {
        var gate = new BlockingStoragePressurePollGate(allowMesQuery: false);
        var runner = new RecordingPollRunner();
        var service = new MesTaskUnionPollHostedService(
            new StoragePressureGuardedPollRunner(runner, gate),
            new MesIngestHostOptions
            {
                ContinuousPollEnabled = true,
                PollStartIntervalSeconds = 60,
            },
            NullLogger<MesTaskUnionPollHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await gate.Checked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, gate.CheckCount);
        Assert.Equal(0, runner.RunCount);
    }

    [Fact]
    public async Task Recovered_gate_allows_only_the_next_normal_scheduled_attempt()
    {
        var gate = new BlockingStoragePressurePollGate(allowMesQuery: true);
        var runner = new RecordingPollRunner();
        var guarded = new StoragePressureGuardedPollRunner(runner, gate);
        using var cancellation = new CancellationTokenSource();
        runner.AfterRun = cancellation.Cancel;

        await SingleFlightPollLoop.RunAsync(
            async ct =>
            {
                await guarded.RunOnceIfAllowedAsync(ct);
            },
            TimeSpan.FromMinutes(1),
            cancellation.Token,
            delay: static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        Assert.Equal(1, gate.CheckCount);
        Assert.Equal(1, runner.RunCount);
    }

    private sealed class BlockingStoragePressurePollGate(bool allowMesQuery) :
        IStoragePressurePollGate
    {
        public TaskCompletionSource Checked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CheckCount { get; private set; }

        public Task<bool> CanQueryMesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckCount++;
            Checked.TrySetResult();
            return Task.FromResult(allowMesQuery);
        }
    }

    private sealed class RecordingPollRunner : IMesTaskUnionPollRunner
    {
        public int RunCount { get; private set; }

        public Action? AfterRun { get; set; }

        public Task<RoundCommitReceipt> RunOnceAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunCount++;
            AfterRun?.Invoke();
            return Task.FromResult(new RoundCommitReceipt(
                $"poll-{RunCount}",
                MesTaskUnionRoundOutcome.Success,
                $"commit-{RunCount}",
                [],
                [],
                false));
        }
    }
}
