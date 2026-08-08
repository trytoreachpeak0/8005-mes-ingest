namespace MesIngest.Watch.UiTests;

public sealed class WatchVisualEnvironmentTests
{
    [Fact]
    public void Matching_calibrated_environment_is_accepted()
    {
        var result = WatchVisualEnvironment.Evaluate(MatchingSnapshot());

        Assert.True(result.IsCompatible);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Every_environment_drift_is_reported_before_visual_artifacts_can_be_written()
    {
        var result = WatchVisualEnvironment.Evaluate(new WatchVisualEnvironmentSnapshot(
            HasInteractiveInputDesktop: false,
            DesktopWidth: 2560,
            DesktopHeight: 1440,
            Dpi: 120,
            AppsUseLightTheme: false,
            CultureName: "en-US",
            UiCultureName: "en-US",
            InstalledFonts: ["Segoe UI"],
            RenderingMode: "Auto"));

        Assert.False(result.IsCompatible);
        Assert.Collection(
            result.Differences,
            item => Assert.Contains("active input desktop", item, StringComparison.Ordinal),
            item => Assert.Contains("desktop=1920x1080", item, StringComparison.Ordinal),
            item => Assert.Contains("DPI=96", item, StringComparison.Ordinal),
            item => Assert.Contains("light theme", item, StringComparison.Ordinal),
            item => Assert.Contains("culture=zh-CN", item, StringComparison.Ordinal),
            item => Assert.Contains("UI culture=zh-CN", item, StringComparison.Ordinal),
            item => Assert.Contains("Microsoft YaHei UI", item, StringComparison.Ordinal),
            item => Assert.Contains("Consolas", item, StringComparison.Ordinal),
            item => Assert.Contains("rendering mode=SoftwareOnly", item, StringComparison.Ordinal));
    }

    private static WatchVisualEnvironmentSnapshot MatchingSnapshot() => new(
        HasInteractiveInputDesktop: true,
        DesktopWidth: 1920,
        DesktopHeight: 1080,
        Dpi: 96,
        AppsUseLightTheme: true,
        CultureName: "zh-CN",
        UiCultureName: "zh-CN",
        InstalledFonts: ["Microsoft YaHei UI", "Consolas"],
        RenderingMode: "SoftwareOnly");
}
