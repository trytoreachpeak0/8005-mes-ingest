using MesIngest.Core.SeriesProjection;

namespace MesIngest.Host;

public sealed class StoragePressurePollGate : IStoragePressurePollGate
{
    private readonly IStoragePressureOperations _operations;
    private readonly IVolumeSpaceReader _spaceReader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StoragePressurePollGate> _logger;

    public StoragePressurePollGate(
        IStoragePressureOperations operations,
        IVolumeSpaceReader spaceReader,
        TimeProvider timeProvider,
        ILogger<StoragePressurePollGate> logger)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _spaceReader = spaceReader ?? throw new ArgumentNullException(nameof(spaceReader));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> CanQueryMesAsync(CancellationToken cancellationToken = default)
    {
        var volume = await _operations.ResolveDatabaseVolumeAsync(cancellationToken)
            .ConfigureAwait(false);
        var sample = _spaceReader.Read(volume.VolumeRoot);
        var state = await _operations.ObserveStoragePressureAsync(
                volume,
                sample,
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);

        if (state.IsPaused)
        {
            _logger.LogCritical(
                "StoragePressurePause remains active for database {DatabaseName} on {VolumeRoot}; "
                + "available={AvailablePercent:F3}%.",
                state.DatabaseName,
                state.Space.VolumeRoot,
                state.Space.AvailablePercent);
            return false;
        }

        if (string.Equals(
                state.Status,
                StoragePressureStatuses.Warning,
                StringComparison.Ordinal))
        {
            _logger.LogCritical(
                "MesIngest database storage is below the critical warning threshold; "
                + "database={DatabaseName}, volume={VolumeRoot}, available={AvailablePercent:F3}%.",
                state.DatabaseName,
                state.Space.VolumeRoot,
                state.Space.AvailablePercent);
        }

        return true;
    }
}
