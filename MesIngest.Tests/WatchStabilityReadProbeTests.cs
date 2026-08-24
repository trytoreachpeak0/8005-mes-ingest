using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchStabilityReadProbeTests
{
    [Fact]
    public async Task Only_the_explicit_headless_probe_switch_is_claimed()
    {
        Assert.Null(await WatchStabilityReadProbe.TryRunAsync([]));
        Assert.Null(await WatchStabilityReadProbe.TryRunAsync(["--ordinary-watch"]));

        var exitCode = await WatchStabilityReadProbe.TryRunAsync(
            ["--ticket28-stability-probe"]);

        Assert.Equal(2, exitCode);
    }
}
