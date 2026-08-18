using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace MesIngest.Watch.UiTests;

public sealed class WatchWindowVisualEquivalenceTests
{
    private const int Width = 1440;
    private const int Height = 900;

    [Fact]
    public void Identical_captures_are_equivalent()
    {
        var capture = CreateCapture();
        var report = WatchWindowVisualEquivalence.Compare(capture, capture);

        Assert.True(report.AreEquivalent, report.Rejection);
        Assert.Equal(0, report.DifferingPixels);
    }

    [Fact]
    public void Antialiasing_jitter_on_a_glyph_is_equivalent()
    {
        // The real signature measured on the golden machine: a small block of grey text
        // pixels each moving by one or two levels, with the glyph support untouched.
        var expected = CreateCapture();
        var actual = CreateCapture(mutate: bitmap =>
        {
            for (var y = 856; y < 868; y++)
            {
                for (var x = 326; x < 336; x++)
                {
                    var value = bitmap.GetPixel(x, y).R;
                    var shifted = (byte)(value - ((x + y) % 2 == 0 ? 1 : 2));
                    bitmap.SetPixel(x, y, Color.FromArgb(255, shifted, shifted, shifted));
                }
            }
        });

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.True(report.AreEquivalent, report.Rejection);
        Assert.Equal(120, report.DifferingPixels);
        Assert.Equal(2, report.MaxObservedDelta);
        Assert.Single(report.Components);
    }

    [Fact]
    public void A_changed_frame_size_is_rejected()
    {
        var expected = CreateCapture();
        var actual = CreateCapture(width: Width, height: Height - 1);

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.False(report.AreEquivalent);
        Assert.Contains("frame size changed", report.Rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_neutral_colour_change_is_rejected()
    {
        var expected = CreateCapture();
        var actual = CreateCapture(mutate: bitmap =>
            bitmap.SetPixel(400, 400, Color.FromArgb(255, 253, 254, 254)));

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.False(report.AreEquivalent);
        Assert.Contains("is not neutral", report.Rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void A_difference_larger_than_the_magnitude_limit_is_rejected()
    {
        var expected = CreateCapture();
        var actual = CreateCapture(mutate: bitmap =>
            bitmap.SetPixel(400, 400, Color.FromArgb(255, 250, 250, 250)));

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.False(report.AreEquivalent);
        Assert.Contains("changed by 4", report.Rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void An_alpha_change_is_rejected()
    {
        var expected = CreateCapture();
        var actual = CreateCapture(mutate: bitmap =>
            bitmap.SetPixel(400, 400, Color.FromArgb(254, 254, 254, 254)));

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.False(report.AreEquivalent);
        Assert.Contains("alpha channel changed", report.Rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void A_global_one_level_shift_is_rejected_by_the_pixel_budget()
    {
        var expected = CreateCapture();
        var actual = CreateCapture(mutate: bitmap =>
        {
            for (var y = 0; y < 40; y++)
            {
                for (var x = 0; x < 200; x++)
                {
                    bitmap.SetPixel(x, y, Color.FromArgb(255, 253, 253, 253));
                }
            }
        });

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.False(report.AreEquivalent);
        Assert.Contains("differing pixels", report.Rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wide_differing_region_is_rejected_by_the_locality_rule()
    {
        var expected = CreateCapture();
        var actual = CreateCapture(mutate: bitmap =>
        {
            for (var x = 100; x < 400; x++)
            {
                bitmap.SetPixel(x, 500, Color.FromArgb(255, 253, 253, 253));
            }
        });

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.False(report.AreEquivalent);
        Assert.Contains("differing region", report.Rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void Scattered_differences_are_rejected_by_the_component_limit()
    {
        var expected = CreateCapture();
        var actual = CreateCapture(mutate: bitmap =>
        {
            for (var index = 0; index < 40; index++)
            {
                bitmap.SetPixel(100 + (index * 12), 300, Color.FromArgb(255, 253, 253, 253));
            }
        });

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.False(report.AreEquivalent);
        Assert.Contains("separate regions", report.Rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void Moved_ink_is_rejected()
    {
        // A one pixel text shift turns background into ink and ink into background, which
        // exceeds the magnitude limit long before the ink mask rule is reached.
        var expected = CreateCapture();
        var actual = CreateCapture(mutate: bitmap =>
        {
            for (var y = 856; y < 868; y++)
            {
                bitmap.SetPixel(326, y, Color.FromArgb(255, 254, 254, 254));
                bitmap.SetPixel(336, y, Color.FromArgb(255, 35, 35, 35));
            }
        });

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.False(report.AreEquivalent);
    }

    private static byte[] CreateCapture(
        int width = Width,
        int height = Height,
        Action<Bitmap>? mutate = null)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(255, 254, 254, 254));
            }
        }

        // A block of glyph-like ink using the grey levels the golden machine produces.
        var levels = new byte[] { 35, 79, 119, 155, 190, 222 };
        for (var y = 856; y < 868 && y < height; y++)
        {
            for (var x = 326; x < 354 && x < width; x++)
            {
                var level = levels[(x + y) % levels.Length];
                bitmap.SetPixel(x, y, Color.FromArgb(255, level, level, level));
            }
        }

        mutate?.Invoke(bitmap);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
