using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace MesIngest.Watch.UiTests;

/// <summary>
/// Bounded visual-equivalence check for real-window captures.
/// </summary>
/// <remarks>
/// The golden renderer compares captures byte for byte. That is the right default: it
/// catches layout, colour, typography and content regressions with no judgement calls.
/// It also rejects a class of difference that carries no visual information at all —
/// WPF can rasterise an identical glyph run into slightly different antialiasing
/// intensities between processes, leaving the glyph's pixel support untouched and moving
/// individual grey levels by one or two.
///
/// This type accepts only that class and nothing else. Every rule below has to hold; each
/// one exists to reject a specific family of real regressions:
///
///   1. identical dimensions               - resize / DPI / layout container changes
///   2. ink-mask invariance                - backstop for rule 4 (see below)
///   3. achromatic delta (dR = dG = dB)     - accent, status and theme colour changes
///   4. bounded magnitude                  - contrast, opacity and brightness changes
///   5. locality (component count and size) - global gamma shifts, large-area repaints
///   6. pixel budget                       - slow erosion of the baseline
///   7. alpha invariance                   - compositing changes
///
/// Rule 4 is what rejects moved text, a different glyph, font or weight, a moved control
/// and an added or removed element: each of those turns background into ink somewhere,
/// which is a swing of tens of levels rather than 3. Rules 5 and 6 bound how much of the
/// frame may differ at all.
///
/// Rule 2 states that requirement directly rather than leaving it implicit in a magnitude
/// bound. While <see cref="WatchWindowVisualEquivalenceOptions.MaxAbsoluteDelta"/> stays
/// at 3 it is unreachable - a pixel cannot cross from clearly ink to clearly background
/// within 3 levels - and the band around the threshold keeps it from flaking on the
/// threshold itself. It is here so the invariant survives someone raising the magnitude
/// bound: they have to confront the ink rule instead of silently losing the guarantee.
///
/// Acceptance is never silent: callers are expected to record the returned report as
/// evidence and to surface it in the run summary.
/// </remarks>
internal sealed record WatchWindowVisualEquivalenceOptions
{
    public static WatchWindowVisualEquivalenceOptions Default { get; } = new();

    /// <summary>Largest per-channel difference an individual pixel may carry.</summary>
    public int MaxAbsoluteDelta { get; init; } = 3;

    /// <summary>Largest number of connected regions of differing pixels.</summary>
    public int MaxComponents { get; init; } = 24;

    /// <summary>Largest bounding box a single differing region may occupy.</summary>
    public int MaxComponentWidth { get; init; } = 128;

    public int MaxComponentHeight { get; init; } = 48;

    /// <summary>Floor for the per-comparison pixel budget, used for small frames.</summary>
    public int MinDifferingPixelBudget { get; init; } = 512;

    /// <summary>Per-comparison pixel budget as a fraction of the frame.</summary>
    public double MaxDifferingPixelFraction { get; init; } = 0.0005;

    /// <summary>Luminance below which a pixel counts as glyph ink on a light surface.</summary>
    public int InkThreshold { get; init; } = 250;

    /// <summary>How many capture steps in one run may be accepted by tolerance.</summary>
    public int MaxToleratedStepsPerRun { get; init; } = 4;

    /// <summary>Total differing pixels one run may accept across all of its steps.</summary>
    public int MaxDifferingPixelsPerRun { get; init; } = 1536;

    public int DifferingPixelBudget(int width, int height) => Math.Max(
        MinDifferingPixelBudget,
        (int)Math.Round(width * (double)height * MaxDifferingPixelFraction));
}

internal sealed record WatchWindowVisualEquivalenceReport(
    bool AreEquivalent,
    string Rejection,
    int DifferingPixels,
    int MaxObservedDelta,
    IReadOnlyList<Rectangle> Components)
{
    public string Describe() => AreEquivalent
        ? string.Format(
            CultureInfo.InvariantCulture,
            "visually equivalent: {0} pixels, max delta {1}, {2} region(s) {3}",
            DifferingPixels,
            MaxObservedDelta,
            Components.Count,
            FormatComponents())
        : $"not visually equivalent: {Rejection}";

    private string FormatComponents() => string.Join(
        " ",
        Components.Select(component => string.Format(
            CultureInfo.InvariantCulture,
            "[{0},{1} {2}x{3}]",
            component.X,
            component.Y,
            component.Width,
            component.Height)));
}

internal static class WatchWindowVisualEquivalence
{
    public static WatchWindowVisualEquivalenceReport Compare(
        byte[] expectedPng,
        byte[] actualPng,
        WatchWindowVisualEquivalenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(expectedPng);
        ArgumentNullException.ThrowIfNull(actualPng);
        options ??= WatchWindowVisualEquivalenceOptions.Default;

        using var expectedStream = new MemoryStream(expectedPng);
        using var actualStream = new MemoryStream(actualPng);
        using var expectedSource = new Bitmap(expectedStream);
        using var actualSource = new Bitmap(actualStream);

        if (expectedSource.Width != actualSource.Width
            || expectedSource.Height != actualSource.Height)
        {
            return Reject(
                $"frame size changed: {expectedSource.Width}x{expectedSource.Height} -> "
                + $"{actualSource.Width}x{actualSource.Height}");
        }

        // Read the decoded surfaces directly. Going through Graphics.DrawImage would
        // rescale by the ratio of the source and destination resolutions - the captures
        // carry 95.99 DPI while a new Bitmap defaults to 96 - which silently displaces
        // every pixel. LockBits converts the pixel format without any geometric transform.
        var expected = expectedSource;
        var actual = actualSource;
        var width = expected.Width;
        var height = expected.Height;

        var expectedData = expected.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        var actualData = actual.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            return CompareLocked(expectedData, actualData, width, height, options);
        }
        finally
        {
            expected.UnlockBits(expectedData);
            actual.UnlockBits(actualData);
        }
    }

    private static WatchWindowVisualEquivalenceReport CompareLocked(
        BitmapData expectedData,
        BitmapData actualData,
        int width,
        int height,
        WatchWindowVisualEquivalenceOptions options)
    {
        var rowLength = width * 4;
        var expectedRow = new byte[rowLength];
        var actualRow = new byte[rowLength];
        var differing = new bool[width * height];
        var differingPixels = 0;
        var maxObservedDelta = 0;
        var inkLow = options.InkThreshold - options.MaxAbsoluteDelta;
        var inkHigh = options.InkThreshold + options.MaxAbsoluteDelta;
        var budget = options.DifferingPixelBudget(width, height);

        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(expectedData.Scan0 + (y * expectedData.Stride), expectedRow, 0, rowLength);
            Marshal.Copy(actualData.Scan0 + (y * actualData.Stride), actualRow, 0, rowLength);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                var deltaB = actualRow[offset] - expectedRow[offset];
                var deltaG = actualRow[offset + 1] - expectedRow[offset + 1];
                var deltaR = actualRow[offset + 2] - expectedRow[offset + 2];
                var deltaA = actualRow[offset + 3] - expectedRow[offset + 3];
                if (deltaB == 0 && deltaG == 0 && deltaR == 0 && deltaA == 0)
                {
                    continue;
                }

                // 7. alpha invariance
                if (deltaA != 0)
                {
                    return Reject($"alpha channel changed at {x},{y}");
                }

                // 3. achromatic delta
                if (deltaR != deltaG || deltaG != deltaB)
                {
                    return Reject(
                        $"colour changed at {x},{y}: delta ({deltaR},{deltaG},{deltaB}) is not neutral");
                }

                // 4. bounded magnitude
                var magnitude = Math.Abs(deltaR);
                if (magnitude > options.MaxAbsoluteDelta)
                {
                    return Reject(
                        $"pixel {x},{y} changed by {magnitude}, limit is {options.MaxAbsoluteDelta}");
                }

                // 2. ink-mask invariance, evaluated with a band so it cannot flake on the
                //    threshold itself. Pixels inside the band are already bounded by rule 4.
                var expectedValue = expectedRow[offset + 2];
                var actualValue = actualRow[offset + 2];
                if ((expectedValue < inkLow && actualValue > inkHigh)
                    || (expectedValue > inkHigh && actualValue < inkLow))
                {
                    return Reject(
                        $"ink mask changed at {x},{y}: {expectedValue} -> {actualValue}");
                }

                differing[(y * width) + x] = true;
                differingPixels++;
                maxObservedDelta = Math.Max(maxObservedDelta, magnitude);

                // 6. pixel budget
                if (differingPixels > budget)
                {
                    return Reject(
                        $"more than {budget} differing pixels ({width}x{height} frame)");
                }
            }
        }

        if (differingPixels == 0)
        {
            return new WatchWindowVisualEquivalenceReport(
                true, string.Empty, 0, 0, Array.Empty<Rectangle>());
        }

        // 5. locality
        var components = FindComponents(differing, width, height, options.MaxComponents);
        if (components is null)
        {
            return Reject(
                $"differences form more than {options.MaxComponents} separate regions",
                differingPixels,
                maxObservedDelta);
        }

        foreach (var component in components)
        {
            if (component.Width > options.MaxComponentWidth
                || component.Height > options.MaxComponentHeight)
            {
                return Reject(
                    $"a differing region is {component.Width}x{component.Height} at "
                    + $"{component.X},{component.Y}, limit is "
                    + $"{options.MaxComponentWidth}x{options.MaxComponentHeight}",
                    differingPixels,
                    maxObservedDelta);
            }
        }

        return new WatchWindowVisualEquivalenceReport(
            true, string.Empty, differingPixels, maxObservedDelta, components);
    }

    private static List<Rectangle>? FindComponents(
        bool[] differing,
        int width,
        int height,
        int maxComponents)
    {
        var visited = new bool[differing.Length];
        var components = new List<Rectangle>();
        var stack = new Stack<int>();

        for (var index = 0; index < differing.Length; index++)
        {
            if (!differing[index] || visited[index])
            {
                continue;
            }

            if (components.Count == maxComponents)
            {
                return null;
            }

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            stack.Push(index);
            visited[index] = true;

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var currentX = current % width;
                var currentY = current / width;
                minX = Math.Min(minX, currentX);
                minY = Math.Min(minY, currentY);
                maxX = Math.Max(maxX, currentX);
                maxY = Math.Max(maxY, currentY);

                for (var dy = -1; dy <= 1; dy++)
                {
                    var neighbourY = currentY + dy;
                    if (neighbourY < 0 || neighbourY >= height)
                    {
                        continue;
                    }

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var neighbourX = currentX + dx;
                        if (neighbourX < 0 || neighbourX >= width)
                        {
                            continue;
                        }

                        var neighbour = (neighbourY * width) + neighbourX;
                        if (differing[neighbour] && !visited[neighbour])
                        {
                            visited[neighbour] = true;
                            stack.Push(neighbour);
                        }
                    }
                }
            }

            components.Add(new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1));
        }

        return components;
    }

    private static WatchWindowVisualEquivalenceReport Reject(
        string reason,
        int differingPixels = 0,
        int maxObservedDelta = 0) => new(
            false, reason, differingPixels, maxObservedDelta, Array.Empty<Rectangle>());
}
