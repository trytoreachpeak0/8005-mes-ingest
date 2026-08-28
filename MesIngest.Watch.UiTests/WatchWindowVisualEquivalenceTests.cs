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
        Assert.Equal(0, report.IgnoredDifferingPixels);
    }

    [Fact]
    public void Text_region_pixels_are_excluded_from_visual_comparison()
    {
        var expected = CreateCapture();
        var actual = CreateCapture(mutate: bitmap =>
        {
            for (var y = 856; y < 868; y++)
            {
                for (var x = 326; x < 354; x++)
                {
                    bitmap.SetPixel(x, y, Color.Magenta);
                }
            }
        });

        var report = WatchWindowVisualEquivalence.Compare(
            expected,
            actual,
            ignoredRegions: [new Rectangle(324, 854, 32, 16)]);

        Assert.True(report.AreEquivalent, report.Rejection);
        Assert.Equal(0, report.DifferingPixels);
        Assert.Equal(336, report.IgnoredDifferingPixels);
        Assert.Equal("text-masked", report.Classification);
        Assert.False(report.ConsumesOrdinaryBudget);
    }

    [Fact]
    public void Text_mask_does_not_hide_a_change_outside_its_bounds()
    {
        var expected = CreateCapture();
        var actual = CreateCapture(mutate: bitmap =>
        {
            bitmap.SetPixel(340, 860, Color.Magenta);
            bitmap.SetPixel(600, 500, Color.Black);
        });

        var report = WatchWindowVisualEquivalence.Compare(
            expected,
            actual,
            ignoredRegions: [new Rectangle(324, 854, 32, 16)]);

        Assert.False(report.AreEquivalent);
        Assert.Contains("changed by", report.Rejection, StringComparison.Ordinal);
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
    public void Antialiasing_jitter_across_many_glyph_edges_is_not_rejected_as_global_erosion()
    {
        var expected = CreateTextHeavyCapture();
        var actual = CreateTextHeavyCapture(mutateInk: true);

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.True(report.AreEquivalent, report.Rejection);
        Assert.True(report.DifferingPixels > 648);
        Assert.Equal(2, report.MaxObservedDelta);
        Assert.True(report.IsRasterizationOnly);
    }

    [Fact]
    public void Edge_raster_jitter_above_one_percent_of_the_frame_is_rejected()
    {
        var expected = CreateTextHeavyCapture(columns: 18);
        var actual = CreateTextHeavyCapture(mutateInk: true, columns: 18);

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.False(report.AreEquivalent);
        Assert.Contains("differing pixels", report.Rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void Coloured_text_edge_compositing_jitter_is_raster_equivalent()
    {
        var expected = CreateColouredGlyphCapture();
        var actual = CreateColouredGlyphCapture(mutateEdge: true);

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.True(report.AreEquivalent, report.Rejection);
        Assert.True(report.IsRasterizationOnly);
        Assert.Equal(2, report.MaxObservedDelta);
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
    public void A_wide_high_contrast_control_edge_jitter_is_rejected()
    {
        var expected = CreateWideEdgeCapture();
        var actual = CreateWideEdgeCapture(mutateEdge: true);

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);

        Assert.False(report.AreEquivalent);
        Assert.Contains("differing region", report.Rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tall_high_contrast_edge_jitter_is_rejected()
    {
        var expected = CreateTallEdgeCapture();
        var actual = CreateTallEdgeCapture(mutateEdge: true);

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

    private static byte[] CreateTextHeavyCapture(bool mutateInk = false, int columns = 16)
    {
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(255, 254, 254, 254));

        for (var row = 0; row < 8; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var left = 80 + (column * 72);
                var top = 80 + (row * 72);
                for (var y = top; y < top + 12; y++)
                {
                    for (var x = left; x < left + 8; x++)
                    {
                        var level = (byte)(80 + ((x + y) % 5 * 28));
                        if (mutateInk)
                        {
                            level = (byte)(level - ((x + y) % 2 == 0 ? 1 : 2));
                        }

                        bitmap.SetPixel(x, y, Color.FromArgb(255, level, level, level));
                    }
                }
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] CreateColouredGlyphCapture(bool mutateEdge = false)
    {
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(255, 220, 245, 220));
        for (var y = 420; y < 432; y++)
        {
            for (var x = 700; x < 708; x++)
            {
                var colour = Color.FromArgb(255, 20, 110, 20);
                if (mutateEdge)
                {
                    colour = Color.FromArgb(255, 22, 110, 21);
                }

                bitmap.SetPixel(x, y, colour);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] CreateWideEdgeCapture(bool mutateEdge = false)
    {
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(255, 254, 254, 254));
        var value = mutateEdge ? 101 : 100;
        for (var x = 100; x < 400; x++)
        {
            bitmap.SetPixel(x, 500, Color.FromArgb(255, value, value, value));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] CreateTallEdgeCapture(bool mutateEdge = false)
    {
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(255, 254, 254, 254));
        var value = mutateEdge ? 101 : 100;
        for (var y = 300; y < 349; y++)
        {
            bitmap.SetPixel(600, y, Color.FromArgb(255, value, value, value));
            bitmap.SetPixel(601, y, Color.FromArgb(255, value, value, value));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
