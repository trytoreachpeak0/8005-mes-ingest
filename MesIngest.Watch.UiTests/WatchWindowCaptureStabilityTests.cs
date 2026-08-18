namespace MesIngest.Watch.UiTests;

public sealed class WatchWindowCaptureStabilityTests
{
    [Fact]
    public void Returns_first_frame_that_is_byte_identical_three_times_after_transient_animation()
    {
        var frames = new Queue<byte[]>(
        [
            [1],
            [2],
            [2],
            [2],
            [3],
        ]);

        var actual = WatchWindowCaptureStability.Capture(
            () => frames.Dequeue(),
            static () => { },
            maximumFrames: 5);

        Assert.Equal([2], actual);
        Assert.Single(frames);
    }

    [Fact]
    public void Rejects_a_capture_that_never_settles()
    {
        var next = 0;

        var exception = Assert.Throws<InvalidOperationException>(
            () => WatchWindowCaptureStability.Capture(
                () => [(byte)(next++ % 2)],
                static () => { },
                maximumFrames: 5));

        Assert.Contains("three consecutive byte-identical frames", exception.Message);
    }
}
