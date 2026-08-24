using MesIngest.Core.SeriesProjection;

namespace MesIngest.Host;

public sealed class StoragePressureGuardedPollRunner
{
    private readonly IMesTaskUnionPollRunner _runner;
    private readonly IStoragePressurePollGate _storagePressurePollGate;

    public StoragePressureGuardedPollRunner(
        IMesTaskUnionPollRunner runner,
        IStoragePressurePollGate storagePressurePollGate)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _storagePressurePollGate = storagePressurePollGate
            ?? throw new ArgumentNullException(nameof(storagePressurePollGate));
    }

    public async Task<RoundCommitReceipt?> RunOnceIfAllowedAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _storagePressurePollGate.CanQueryMesAsync(cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        return await _runner.RunOnceAsync(cancellationToken).ConfigureAwait(false);
    }
}
