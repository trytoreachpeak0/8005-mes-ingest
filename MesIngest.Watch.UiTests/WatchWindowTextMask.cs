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
        return new WatchWindowTextMask(
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

        return new WatchWindowTextMask(
            dto.FrameWidth,
            dto.FrameHeight,
            dto.Regions.Select(static region =>
                    new Rectangle(region.X, region.Y, region.Width, region.Height))
                .ToArray());
    }

    public void Save(string path)
    {
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

    public IReadOnlyList<Rectangle> Union(WatchWindowTextMask other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (FrameWidth != other.FrameWidth || FrameHeight != other.FrameHeight)
        {
            throw new InvalidDataException(
                $"Text-mask frame changed: {FrameWidth}x{FrameHeight} -> "
                + $"{other.FrameWidth}x{other.FrameHeight}.");
        }

        return Regions.Concat(other.Regions).Distinct().ToArray();
    }

    private sealed record TextMaskDto(
        int SchemaVersion,
        int FrameWidth,
        int FrameHeight,
        IReadOnlyList<TextRegionDto> Regions);

    private sealed record TextRegionDto(int X, int Y, int Width, int Height);
}
