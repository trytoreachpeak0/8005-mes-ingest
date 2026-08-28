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
            var union = loaded.UnionForComparison(new WatchWindowTextMask(
                1440,
                900,
                [new Rectangle(10, 20, 30, 40), new Rectangle(100, 200, 50, 20)]),
                1440,
                900);

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

        var error = Assert.Throws<InvalidDataException>(() =>
            first.UnionForComparison(second, 1440, 900));

        Assert.Contains("PNG frame", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_mask_refuses_regions_outside_the_png_frame()
    {
        var mask = new WatchWindowTextMask(
            1440,
            900,
            [new Rectangle(1430, 10, 20, 20)]);

        var error = Assert.Throws<InvalidDataException>(() =>
            mask.ValidateForFrame(1440, 900));

        Assert.Contains("outside", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_mask_refuses_excessive_frame_coverage()
    {
        var mask = new WatchWindowTextMask(
            100,
            100,
            [
                new Rectangle(0, 0, 100, 14),
                new Rectangle(0, 20, 100, 14),
                new Rectangle(0, 40, 100, 14),
                new Rectangle(0, 60, 100, 14),
            ]);

        var error = Assert.Throws<InvalidDataException>(() =>
            mask.ValidateForFrame(100, 100));

        Assert.Contains("Text mask covers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_mask_refuses_one_excessive_region()
    {
        var mask = new WatchWindowTextMask(
            100,
            100,
            [new Rectangle(0, 0, 100, 16)]);

        var error = Assert.Throws<InvalidDataException>(() =>
            mask.ValidateForFrame(100, 100));

        Assert.Contains("region", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Text_mask_refuses_excessive_comparison_growth()
    {
        var baseline = new WatchWindowTextMask(100, 100, [new Rectangle(0, 0, 10, 10)]);
        var actual = new WatchWindowTextMask(100, 100, [new Rectangle(0, 20, 100, 6)]);

        var error = Assert.Throws<InvalidDataException>(() =>
            baseline.UnionForComparison(actual, 100, 100));

        Assert.Contains("grew", error.Message, StringComparison.Ordinal);
    }
}
