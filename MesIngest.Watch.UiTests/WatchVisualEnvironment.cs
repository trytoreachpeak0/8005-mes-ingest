using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MesIngest.Watch.UiTests;

internal sealed record WatchVisualEnvironmentSnapshot(
    bool HasInteractiveInputDesktop,
    int DesktopWidth,
    int DesktopHeight,
    int Dpi,
    bool AppsUseLightTheme,
    string CultureName,
    string UiCultureName,
    string TimeZoneId,
    IReadOnlyCollection<string> InstalledFonts,
    RenderMode RenderingMode);

internal sealed record WatchVisualEnvironmentResult(IReadOnlyList<string> Differences)
{
    public bool IsCompatible => Differences.Count == 0;

    public string FormatReport() => IsCompatible
        ? "WATCH_XAML_VISUAL_ENVIRONMENT_OK"
        : "WATCH_XAML_VISUAL_ENVIRONMENT_UNAVAILABLE:" + Environment.NewLine
          + string.Join(Environment.NewLine, Differences.Select(item => $"- {item}"));
}

internal static class WatchVisualEnvironment
{
    private static readonly string[] RequiredFonts = ["Microsoft YaHei UI", "Consolas"];

    public static WatchVisualEnvironmentResult Evaluate(WatchVisualEnvironmentSnapshot snapshot)
        => Evaluate(
            snapshot,
            minimumDesktopWidth: 1920,
            minimumDesktopHeight: 1080,
            requireExactDesktopSize: true,
            allowedDpis: [96]);

    public static WatchVisualEnvironmentResult EvaluateJourney(
        WatchVisualEnvironmentSnapshot snapshot) =>
        Evaluate(
            snapshot,
            minimumDesktopWidth: 1440,
            minimumDesktopHeight: 900,
            requireExactDesktopSize: false,
            allowedDpis: [96, 120, 144]);

    private static WatchVisualEnvironmentResult Evaluate(
        WatchVisualEnvironmentSnapshot snapshot,
        int minimumDesktopWidth,
        int minimumDesktopHeight,
        bool requireExactDesktopSize,
        IReadOnlyCollection<int> allowedDpis)
    {
        var differences = new List<string>();
        if (!snapshot.HasInteractiveInputDesktop)
        {
            differences.Add("expected an active input desktop; actual=unavailable");
        }

        if (requireExactDesktopSize
            && (snapshot.DesktopWidth != minimumDesktopWidth
                || snapshot.DesktopHeight != minimumDesktopHeight))
        {
            differences.Add(
                $"expected desktop={minimumDesktopWidth}x{minimumDesktopHeight}; actual={snapshot.DesktopWidth}x{snapshot.DesktopHeight}");
        }
        else if (!requireExactDesktopSize
                 && (snapshot.DesktopWidth < minimumDesktopWidth
                     || snapshot.DesktopHeight < minimumDesktopHeight))
        {
            differences.Add(
                $"expected desktop at least {minimumDesktopWidth}x{minimumDesktopHeight}; actual={snapshot.DesktopWidth}x{snapshot.DesktopHeight}");
        }

        if (!allowedDpis.Contains(snapshot.Dpi))
        {
            differences.Add(
                $"expected DPI={string.Join('/', allowedDpis)}; actual={snapshot.Dpi}");
        }

        if (!snapshot.AppsUseLightTheme)
        {
            differences.Add("expected Windows apps light theme; actual=dark or unknown");
        }

        if (!string.Equals(snapshot.CultureName, "zh-CN", StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"expected culture=zh-CN; actual={snapshot.CultureName}");
        }

        if (!string.Equals(snapshot.UiCultureName, "zh-CN", StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"expected UI culture=zh-CN; actual={snapshot.UiCultureName}");
        }

        if (!string.Equals(snapshot.TimeZoneId, "China Standard Time", StringComparison.Ordinal))
        {
            differences.Add($"expected timezone=China Standard Time; actual={snapshot.TimeZoneId}");
        }

        var installedFonts = snapshot.InstalledFonts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var requiredFont in RequiredFonts)
        {
            if (!installedFonts.Contains(requiredFont))
            {
                differences.Add($"expected installed font={requiredFont}; actual=missing");
            }
        }

        if (snapshot.RenderingMode != RenderMode.SoftwareOnly)
        {
            differences.Add(
                $"expected rendering mode=SoftwareOnly; actual={snapshot.RenderingMode}");
        }

        return new WatchVisualEnvironmentResult(differences);
    }

    public static WatchVisualEnvironmentSnapshot Capture()
    {
        using var personalize = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var theme = personalize?.GetValue("AppsUseLightTheme");
        var installedFonts = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .ToArray();

        return new WatchVisualEnvironmentSnapshot(
            HasInteractiveInputDesktop: HasInteractiveInputDesktop(),
            DesktopWidth: NativeMethods.GetSystemMetrics(0),
            DesktopHeight: NativeMethods.GetSystemMetrics(1),
            Dpi: checked((int)NativeMethods.GetDpiForSystem()),
            AppsUseLightTheme: theme is int value && value == 1,
            CultureName: CultureInfo.CurrentCulture.Name,
            UiCultureName: CultureInfo.CurrentUICulture.Name,
            TimeZoneId: TimeZoneInfo.Local.Id,
            InstalledFonts: installedFonts,
            RenderingMode: RenderOptions.ProcessRenderMode);
    }

    private static bool HasInteractiveInputDesktop()
    {
        if (!Environment.UserInteractive)
        {
            return false;
        }

        var desktop = NativeMethods.OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        return NativeMethods.CloseDesktop(desktop);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForSystem();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr OpenInputDesktop(
            uint flags,
            [MarshalAs(UnmanagedType.Bool)] bool inherit,
            uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseDesktop(IntPtr desktop);
    }
}

/// <summary>
/// One evidence rendering of a captured desktop environment, so every journey
/// records the same fields in the same order.
/// </summary>
internal static class WatchVisualEnvironmentEvidence
{
    public static string Format(WatchVisualEnvironmentSnapshot snapshot) => string.Join(
        Environment.NewLine,
        $"interactive={snapshot.HasInteractiveInputDesktop}",
        $"desktop={snapshot.DesktopWidth}x{snapshot.DesktopHeight}",
        $"dpi={snapshot.Dpi}",
        $"scale={snapshot.Dpi / 96d:P0}",
        $"lightTheme={snapshot.AppsUseLightTheme}",
        $"culture={snapshot.CultureName}",
        $"uiCulture={snapshot.UiCultureName}",
        $"timezone={snapshot.TimeZoneId}",
        $"rendering={snapshot.RenderingMode}",
        $"framework={Environment.Version}",
        $"os={Environment.OSVersion}",
        $"processArchitecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
}
