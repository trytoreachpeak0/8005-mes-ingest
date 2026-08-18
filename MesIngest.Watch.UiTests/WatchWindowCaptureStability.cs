namespace MesIngest.Watch.UiTests;

internal static class WatchWindowCaptureStability
{
    private const int RequiredConsecutiveFrames = 3;

    public static byte[] Capture(
        Func<byte[]> captureFrame,
        Action waitForNextFrame,
        int maximumFrames = 20)
    {
        ArgumentNullException.ThrowIfNull(captureFrame);
        ArgumentNullException.ThrowIfNull(waitForNextFrame);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumFrames,
            RequiredConsecutiveFrames);

        byte[]? previous = null;
        var consecutiveFrames = 0;

        for (var frameNumber = 1; frameNumber <= maximumFrames; frameNumber++)
        {
            waitForNextFrame();
            var current = captureFrame();
            if (previous is not null && current.AsSpan().SequenceEqual(previous))
            {
                consecutiveFrames++;
            }
            else
            {
                consecutiveFrames = 1;
            }

            if (consecutiveFrames == RequiredConsecutiveFrames)
            {
                return current;
            }

            previous = current;
        }

        throw new InvalidOperationException(
            $"Window capture did not produce three consecutive byte-identical frames "
            + $"within {maximumFrames} frames.");
    }
}
