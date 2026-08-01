namespace MesIngest.Watch;

/// <summary>
/// Serializes state-changing refreshes without allowing timer/load-more work to
/// accumulate. Reset is queued because it carries the latest filter/sort state
/// and must eventually replace any window fetched with the previous state.
/// </summary>
internal sealed class WatchRefreshAdmission
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> WaitAsync(
        WatchBrowseRefreshKind kind,
        CancellationToken cancellationToken = default)
    {
        if (kind == WatchBrowseRefreshKind.Reset)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        return await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
    }

    public void Release() => _gate.Release();
}
