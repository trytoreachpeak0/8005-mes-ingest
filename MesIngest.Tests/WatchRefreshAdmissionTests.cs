using MesIngest.Watch;

namespace MesIngest.Tests;

public class WatchRefreshAdmissionTests
{
    [Fact]
    public async Task Sort_reset_waits_for_active_refresh_instead_of_being_dropped()
    {
        var admission = new WatchRefreshAdmission();
        Assert.True(await admission.WaitAsync(WatchBrowseRefreshKind.PreserveWindow));

        var queuedSortReset = admission.WaitAsync(WatchBrowseRefreshKind.Reset);

        Assert.False(queuedSortReset.IsCompleted);
        admission.Release();
        Assert.True(await queuedSortReset);
        admission.Release();
    }

    [Fact]
    public async Task Automatic_refresh_remains_single_flight_and_is_not_queued()
    {
        var admission = new WatchRefreshAdmission();
        Assert.True(await admission.WaitAsync(WatchBrowseRefreshKind.Reset));

        Assert.False(await admission.WaitAsync(WatchBrowseRefreshKind.PreserveWindow));

        admission.Release();
    }
}
