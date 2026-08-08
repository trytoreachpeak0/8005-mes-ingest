using System.IO;
using System.Text.Json;
using IOPath = System.IO.Path;

namespace MesIngest.Watch;

internal sealed record WatchWindowLayout(
    double WindowWidth,
    double WindowHeight,
    double DemandShare)
{
    public static WatchWindowLayout Default { get; } = new(1180, 720, 0.7);
}

/// <summary>
/// Versioned, per-user window geometry for Watch. Stored under LocalApplicationData
/// and deliberately contains no business state or Host response data.
/// </summary>
internal static class WatchLayoutPreferences
{
    public const int CurrentVersion = 1;
    public const double DefaultDemandShare = 0.7;

    private const double MinWindowWidth = 720;
    private const double MaxWindowWidth = 7680;
    private const double MinWindowHeight = 600;
    private const double MaxWindowHeight = 4320;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string DefaultFilePath =>
        IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MesIngest.Watch",
            "layout-preferences.json");

    public static double NormalizeDemandShare(double demandShare) =>
        IsValidShare(demandShare) ? demandShare : DefaultDemandShare;

    public static (double DemandStar, double AlertStar) ToStarHeights(double demandShare)
    {
        var share = NormalizeDemandShare(demandShare);
        return (share, 1.0 - share);
    }

    public static WatchWindowLayout Load(string? path = null)
    {
        path ??= DefaultFilePath;
        if (!File.Exists(path))
        {
            return WatchWindowLayout.Default;
        }

        try
        {
            var document = JsonSerializer.Deserialize<LayoutPreferenceDocument>(
                File.ReadAllText(path),
                JsonOptions);
            if (document is null
                || document.Version != CurrentVersion
                || !IsValidShare(document.DemandShare))
            {
                return WatchWindowLayout.Default;
            }

            // Version 1 originally contained only demandShare. Preserve that safe
            // preference while defaulting the fields added by ticket 10.
            if (document.WindowWidth == 0 && document.WindowHeight == 0)
            {
                return WatchWindowLayout.Default with { DemandShare = document.DemandShare };
            }

            return IsValidWindowSize(document.WindowWidth, document.WindowHeight)
                ? new WatchWindowLayout(
                    document.WindowWidth,
                    document.WindowHeight,
                    document.DemandShare)
                : WatchWindowLayout.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return WatchWindowLayout.Default;
        }
    }

    public static void Save(string path, WatchWindowLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var safe = IsValidWindowSize(layout.WindowWidth, layout.WindowHeight)
            && IsValidShare(layout.DemandShare)
            ? layout
            : WatchWindowLayout.Default;
        var fullPath = IOPath.GetFullPath(path);
        var directory = IOPath.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new LayoutPreferenceDocument
        {
            Version = CurrentVersion,
            WindowWidth = safe.WindowWidth,
            WindowHeight = safe.WindowHeight,
            DemandShare = safe.DemandShare,
        };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(document, JsonOptions));
    }

    public static double LoadDemandShare(string? path = null) => Load(path).DemandShare;

    public static void SaveDemandShare(string path, double demandShare)
    {
        var current = Load(path);
        Save(path, current with { DemandShare = NormalizeDemandShare(demandShare) });
    }

    private static bool IsValidShare(double demandShare) =>
        double.IsFinite(demandShare)
        && demandShare > 0
        && demandShare < 1;

    private static bool IsValidWindowSize(double width, double height) =>
        double.IsFinite(width)
        && double.IsFinite(height)
        && width is >= MinWindowWidth and <= MaxWindowWidth
        && height is >= MinWindowHeight and <= MaxWindowHeight;

    private sealed class LayoutPreferenceDocument
    {
        public int Version { get; set; }
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public double DemandShare { get; set; }
    }
}
