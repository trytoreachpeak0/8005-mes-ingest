using System.Drawing;
using System.IO;

namespace MesIngest.Watch.UiTests;

public sealed class WatchWindowTextMaskTests
{
    [Fact]
    public void Text_mask_round_trips_and_unions_distinct_regions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-text-mask-{Guid.NewGuid():N}.json");
        try
        {
            var first = new WatchWindowTextMask(
                1440,
                900,
                [new Rectangle(10, 20, 30, 40)]);
            first.Save(path);

            var loaded = WatchWindowTextMask.Load(path);
            var union = loaded.Union(new WatchWindowTextMask(
                1440,
                900,
                [new Rectangle(10, 20, 30, 40), new Rectangle(100, 200, 50, 20)]));

            Assert.Equal(1440, loaded.FrameWidth);
            Assert.Equal(900, loaded.FrameHeight);
            Assert.Equal(2, union.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Text_mask_refuses_a_different_frame()
    {
        var first = new WatchWindowTextMask(1440, 900, []);
        var second = new WatchWindowTextMask(1439, 900, []);

        var error = Assert.Throws<InvalidDataException>(() => first.Union(second));

        Assert.Contains("frame changed", error.Message, StringComparison.Ordinal);
    }
}
