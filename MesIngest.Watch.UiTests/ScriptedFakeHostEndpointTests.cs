namespace MesIngest.Watch.UiTests;

public sealed class ScriptedFakeHostEndpointTests
{
    [Fact]
    public void Production_visual_journey_can_request_one_stable_loopback_endpoint()
    {
        Assert.Equal(
            "http://127.0.0.1:51543",
            ScriptedFakeHost.ResolveListenUrl("http://127.0.0.1:51543"));
        Assert.Equal(
            "http://127.0.0.1:0",
            ScriptedFakeHost.ResolveListenUrl(null));
        Assert.Throws<ArgumentException>(() =>
            ScriptedFakeHost.ResolveListenUrl("http://0.0.0.0:51543"));
    }
}
