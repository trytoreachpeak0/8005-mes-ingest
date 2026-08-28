using System.Drawing;
using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace MesIngest.Watch.UiTests;

internal sealed record WatchWindowTextMask(
    int FrameWidth,
    int FrameHeight,
    IReadOnlyList<Rectangle> Regions)
{
    private const int SchemaVersion = 1;
    private const int TextRasterPadding = 2;
    private const double MaxFrameCoverage = 0.55;
    private const double MaxRegionCoverage = 0.15;
    private const double MaxComparisonCoverageGrowth = 0.05;
    private const int MaxFrameDimension = 16_384;
    private const long MaxFramePixels = 100_000_000;

    public static WatchWindowTextMask Capture(
        AutomationElement root,
        UIA3Automation automation,
        IntPtr windowHandle)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(automation);

        var client = WatchWindowNative.GetClientScreenRectangle(windowHandle);
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var regions = new HashSet<Rectangle>();
        var remaining = 5000;

        void Visit(AutomationElement element)
        {
            if (remaining-- <= 0)
            {
                return;
            }

            string? controlType = null;
            try
            {
                controlType = element.ControlType.ToString();
            }
            catch
            {
                // Unsupported UIA properties are not text authority.
            }

            if (controlType is "Text")
            {
                try
                {
                    var bounds = element.BoundingRectangle;
                    bounds.Inflate(TextRasterPadding, TextRasterPadding);
                    var clipped = Rectangle.Intersect(client, bounds);
                    if (!clipped.IsEmpty)
                    {
                        regions.Add(new Rectangle(
                            clipped.Left - client.Left,
                            clipped.Top - client.Top,
                            clipped.Width,
                            clipped.Height));
                    }
                }
                catch
                {
                    // A missing rectangle leaves the pixels under normal comparison.
                }
            }

            var child = walker.GetFirstChild(element);
            while (child is not null && remaining > 0)
            {
                Visit(child);
                child = walker.GetNextSibling(child);
            }
        }

        Visit(root);
        return CreateValidated(
            client.Width,
            client.Height,
            regions.OrderBy(static region => region.Y)
                .ThenBy(static region => region.X)
                .ThenBy(static region => region.Width)
                .ThenBy(static region => region.Height)
                .ToArray());
    }

    public static WatchWindowTextMask Load(string path)
    {
        var dto = JsonSerializer.Deserialize<TextMaskDto>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"Text mask is empty: {path}");
        if (dto.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported text-mask schema {dto.SchemaVersion}: {path}");
        }

        return CreateValidated(
            dto.FrameWidth,
            dto.FrameHeight,
            (dto.Regions ?? throw new InvalidDataException(
                $"Text-mask regions are missing: {path}"))
                .Select(static region =>
                    new Rectangle(region.X, region.Y, region.Width, region.Height))
                .ToArray());
    }

    public void Save(string path)
    {
        Validate();
        var dto = new TextMaskDto(
            SchemaVersion,
            FrameWidth,
            FrameHeight,
            Regions.Select(static region =>
                    new TextRegionDto(region.X, region.Y, region.Width, region.Height))
                .ToArray());
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }

    public IReadOnlyList<Rectangle> UnionForComparison(
        WatchWindowTextMask other,
        int frameWidth,
        int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(other);
        ValidateForFrame(frameWidth, frameHeight);
        other.ValidateForFrame(frameWidth, frameHeight);
        if (FrameWidth != other.FrameWidth || FrameHeight != other.FrameHeight)
        {
            throw new InvalidDataException(
                $"Text-mask frame changed: {FrameWidth}x{FrameHeight} -> "
                + $"{other.FrameWidth}x{other.FrameHeight}.");
        }

        var union = Regions.Concat(other.Regions).Distinct().ToArray();
        var referenceCoverage = CoveredPixels(Regions, frameWidth, frameHeight);
        var unionCoverage = CoveredPixels(union, frameWidth, frameHeight);
        var allowedGrowth = (long)Math.Ceiling(frameWidth * (double)frameHeight
            * MaxComparisonCoverageGrowth);
        if (unionCoverage - referenceCoverage > allowedGrowth)
        {
            throw new InvalidDataException(
                $"Text-mask coverage grew by {unionCoverage - referenceCoverage} pixels; "
                + $"limit is {allowedGrowth} for a {frameWidth}x{frameHeight} frame.");
        }

        return union;
    }

    public void ValidateForFrame(int frameWidth, int frameHeight)
    {
        if (FrameWidth != frameWidth || FrameHeight != frameHeight)
        {
            throw new InvalidDataException(
                $"Text-mask frame is {FrameWidth}x{FrameHeight}; PNG frame is "
                + $"{frameWidth}x{frameHeight}.");
        }

        Validate();
    }

    private static WatchWindowTextMask CreateValidated(
        int frameWidth,
        int frameHeight,
        IReadOnlyList<Rectangle> regions)
    {
        var mask = new WatchWindowTextMask(frameWidth, frameHeight, regions);
        mask.Validate();
        return mask;
    }

    private void Validate()
    {
        if (FrameWidth <= 0
            || FrameHeight <= 0
            || FrameWidth > MaxFrameDimension
            || FrameHeight > MaxFrameDimension
            || (long)FrameWidth * FrameHeight > MaxFramePixels)
        {
            throw new InvalidDataException(
                $"Text-mask frame is invalid or too large: {FrameWidth}x{FrameHeight}.");
        }

        foreach (var region in Regions)
        {
            if (region.Width <= 0
                || region.Height <= 0
                || region.X < 0
                || region.Y < 0
                || (long)region.X + region.Width > FrameWidth
                || (long)region.Y + region.Height > FrameHeight)
            {
                throw new InvalidDataException(
                    $"Text region [{region.X},{region.Y} {region.Width}x{region.Height}] "
                    + $"is outside the {FrameWidth}x{FrameHeight} frame.");
            }

            var regionLimit = (long)Math.Ceiling(
                FrameWidth * (double)FrameHeight * MaxRegionCoverage);
            if ((long)region.Width * region.Height > regionLimit)
            {
                throw new InvalidDataException(
                    $"Text region [{region.X},{region.Y} {region.Width}x{region.Height}] "
                    + $"covers more than {MaxRegionCoverage:P0} of the frame.");
            }
        }

        var covered = CoveredPixels(Regions, FrameWidth, FrameHeight);
        var limit = (long)Math.Ceiling(FrameWidth * (double)FrameHeight * MaxFrameCoverage);
        if (covered > limit)
        {
            throw new InvalidDataException(
                $"Text mask covers {covered} pixels; limit is {limit} "
                + $"({MaxFrameCoverage:P0} of the frame).");
        }
    }

    private static long CoveredPixels(
        IReadOnlyList<Rectangle> regions,
        int frameWidth,
        int frameHeight)
    {
        var covered = new bool[checked(frameWidth * frameHeight)];
        foreach (var region in regions)
        {
            for (var y = region.Top; y < region.Bottom; y++)
            {
                Array.Fill(covered, true, (y * frameWidth) + region.Left, region.Width);
            }
        }

        return covered.LongCount(static value => value);
    }

    private sealed record TextMaskDto(
        int SchemaVersion,
        int FrameWidth,
        int FrameHeight,
        IReadOnlyList<TextRegionDto> Regions);

    private sealed record TextRegionDto(int X, int Y, int Width, int Height);
}
