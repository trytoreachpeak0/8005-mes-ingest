using System.Windows.Media;

namespace MesIngest.Watch;

/// <summary>
/// Shared light industrial operations palette. A fresh dictionary per window avoids
/// WPF's cross-thread URI resource loading cache in parallel test hosts.
/// </summary>
internal sealed class WatchThemeDictionary : ResourceDictionary
{
    public WatchThemeDictionary()
    {
        Add("WatchBackgroundBrush", Brush(0xFF, 0xFF, 0xFF));
        Add("WatchSurfaceBrush", Brush(0xF7, 0xF9, 0xFB));
        Add("WatchNavigationBrush", Brush(0xF3, 0xF5, 0xF7));
        Add("WatchBorderBrush", Brush(0xD8, 0xDE, 0xE4));
        Add("WatchTextBrush", Brush(0x26, 0x32, 0x38));
        Add("WatchSecondaryTextBrush", Brush(0x45, 0x5A, 0x64));
        Add("WatchMutedTextBrush", Brush(0x5F, 0x6B, 0x76));
        Add("WatchAccentBrush", Brush(0x15, 0x65, 0xC0));
        Add("WatchWarningBrush", Brush(0xEF, 0x6C, 0x00));
        Add("WatchErrorBrush", Brush(0xC6, 0x28, 0x28));
        Add("WatchChangedBrush", Brush(0xFF, 0xE0, 0xB2));
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
