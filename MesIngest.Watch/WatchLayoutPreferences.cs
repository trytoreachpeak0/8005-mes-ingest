using System.IO;
using System.Text.Json;
using IOPath = System.IO.Path;

namespace MesIngest.Watch;

/// <summary>
/// Local per-user Demand/Alert pane height ratio for Watch.
/// Stored under LocalApplicationData — never Host/SQL/appsettings.
/// </summary>
internal static class WatchLayoutPreferences
{
    public const int CurrentVersion = 1;
    public const double DefaultDemandShare = 0.7;

    /// <summary>Exclusive open range: share must be strictly between 0 and 1.</summary>
    private const double MinExclusive = 0.0;
    private const double MaxExclusive = 1.0;

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

    public static double LoadDemandShare(string? path = null)
    {
        path ??= DefaultFilePath;
        if (!File.Exists(path))
        {
            return DefaultDemandShare;
        }

        try
        {
            var text = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<LayoutPreferenceDocument>(text, JsonOptions);
            if (doc is null || doc.Version != CurrentVersion)
            {
                return DefaultDemandShare;
            }

            return NormalizeDemandShare(doc.DemandShare);
        }
        catch (JsonException)
        {
            return DefaultDemandShare;
        }
        catch (IOException)
        {
            return DefaultDemandShare;
        }
    }

    public static void SaveDemandShare(string path, double demandShare)
    {
        var share = NormalizeDemandShare(demandShare);
        var dir = IOPath.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var doc = new LayoutPreferenceDocument
        {
            Version = CurrentVersion,
            DemandShare = share,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions));
    }

    private static bool IsValidShare(double demandShare) =>
        !double.IsNaN(demandShare)
        && !double.IsInfinity(demandShare)
        && demandShare > MinExclusive
        && demandShare < MaxExclusive;

    private sealed class LayoutPreferenceDocument
    {
        public int Version { get; set; }
        public double DemandShare { get; set; }
    }
}
