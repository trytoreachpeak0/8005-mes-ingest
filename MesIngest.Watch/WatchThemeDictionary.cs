using System.Windows.Media;

namespace MesIngest.Watch;

/// <summary>
/// Shared light industrial operations palette. A fresh dictionary per window avoids
/// WPF's cross-thread URI resource loading cache in parallel test hosts.
/// </summary>
public sealed class WatchThemeDictionary : ResourceDictionary
{
    public WatchThemeDictionary()
    {
        Add("WatchBackgroundBrush", Brush(0xF5, 0xF7, 0xFA));
        Add("WatchSurfaceBrush", Brush(0xFF, 0xFF, 0xFF));
        Add("WatchNavigationBrush", Brush(0x14, 0x24, 0x3B));
        Add("WatchNavigationSelectedBrush", Brush(0x29, 0x46, 0x6C));
        Add("WatchNavigationTextBrush", Brush(0xBF, 0xCD, 0xE0));
        Add("WatchNavigationMutedBrush", Brush(0x77, 0x90, 0xAE));
        Add("WatchFilterBrush", Brush(0xEE, 0xF2, 0xF7));
        Add("WatchBorderBrush", Brush(0xE2, 0xE7, 0xEE));
        Add("WatchStrongBorderBrush", Brush(0xC9, 0xD2, 0xDE));
        Add("WatchTextBrush", Brush(0x15, 0x20, 0x33));
        Add("WatchSecondaryTextBrush", Brush(0x4B, 0x5B, 0x70));
        Add("WatchMutedTextBrush", Brush(0x72, 0x80, 0x96));
        Add("WatchAccentBrush", Brush(0x25, 0x63, 0xEB));
        Add("WatchAccentSoftBrush", Brush(0xEA, 0xF1, 0xFF));
        Add("WatchSuccessBrush", Brush(0x16, 0x84, 0x5B));
        Add("WatchSuccessSoftBrush", Brush(0xE9, 0xF7, 0xF1));
        Add("WatchWarningBrush", Brush(0xA4, 0x5A, 0x00));
        Add("WatchWarningSoftBrush", Brush(0xFF, 0xF4, 0xDE));
        Add("WatchErrorBrush", Brush(0xC4, 0x3D, 0x4B));
        Add("WatchErrorSoftBrush", Brush(0xFF, 0xF0, 0xF2));
        Add("WatchChangedBrush", Brush(0xFF, 0xF4, 0xDE));
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
