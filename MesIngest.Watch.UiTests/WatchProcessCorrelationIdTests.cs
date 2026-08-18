namespace MesIngest.Watch.UiTests;

public sealed class WatchProcessCorrelationIdTests
{
    [Fact]
    public void Ui_test_mode_uses_the_fixed_visual_correlation_identity()
    {
        Assert.Equal(
            "00000000000000000000000000000023",
            WatchProcessCorrelationId.Create(
                variable => variable == "MESINGEST_WATCH_UI_TEST_MODE" ? "1" : null));
    }

    [Fact]
    public void Production_mode_generates_a_fresh_guid_identity()
    {
        var first = WatchProcessCorrelationId.Create(_ => null);
        var second = WatchProcessCorrelationId.Create(_ => null);

        Assert.Equal(32, first.Length);
        Assert.True(Guid.TryParseExact(first, "N", out _));
        Assert.NotEqual(first, second);
    }
}
