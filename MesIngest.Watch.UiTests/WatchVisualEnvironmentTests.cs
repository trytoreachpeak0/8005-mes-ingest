using System.Windows.Interop;
using System.Windows.Media;

namespace MesIngest.Watch.UiTests;

public sealed class WatchVisualEnvironmentTests
{
    [Fact]
    [Trait("Category", "watch-xaml-visual")]
    [Trait("Category", "watch-xaml-environment")]
    public void Current_environment_matches_the_visual_baseline_contract()
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var result = WatchVisualEnvironment.Evaluate(WatchVisualEnvironment.Capture());
        var isRequired = string.Equals(
            Environment.GetEnvironmentVariable("MESINGEST_WATCH_REQUIRE_VISUAL_ENVIRONMENT"),
            "1",
            StringComparison.Ordinal);

        Assert.SkipWhen(!isRequired && !result.IsCompatible, result.FormatReport());
        Assert.True(result.IsCompatible, result.FormatReport());
    }

    [Fact]
    public void Matching_calibrated_environment_is_accepted()
    {
        var result = WatchVisualEnvironment.Evaluate(MatchingSnapshot());

        Assert.True(result.IsCompatible);
        Assert.Empty(result.Differences);
    }

    [Fact]
    [Trait("Category", "watch-ui-environment")]
    public void Current_environment_supports_real_window_uia_journeys()
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var isRequired = string.Equals(
            Environment.GetEnvironmentVariable("MESINGEST_WATCH_REQUIRE_JOURNEY_ENVIRONMENT"),
            "1",
            StringComparison.Ordinal);
        var result = WatchVisualEnvironment.EvaluateJourney(WatchVisualEnvironment.Capture());

        Assert.SkipWhen(
            !isRequired && !result.IsCompatible,
            "WATCH_UI_JOURNEY_ENVIRONMENT_UNAVAILABLE:" + Environment.NewLine
            + string.Join(Environment.NewLine, result.Differences.Select(item => $"- {item}")));
        Assert.True(
            result.IsCompatible,
            "WATCH_UI_JOURNEY_ENVIRONMENT_UNAVAILABLE:" + Environment.NewLine
            + string.Join(Environment.NewLine, result.Differences.Select(item => $"- {item}")));
    }

    [Fact]
    public void Journey_environment_accepts_supported_dpi_and_rejects_shared_contract_drift()
    {
        var compatible = MatchingSnapshot() with
        {
            DesktopWidth = 2560,
            DesktopHeight = 1440,
            Dpi = 144,
        };

        Assert.True(WatchVisualEnvironment.EvaluateJourney(compatible).IsCompatible);

        var drifted = compatible with
        {
            TimeZoneId = "UTC",
            RenderingMode = RenderMode.Default,
        };
        var result = WatchVisualEnvironment.EvaluateJourney(drifted);

        Assert.Contains(result.Differences, item => item.Contains("timezone", StringComparison.Ordinal));
        Assert.Contains(result.Differences, item => item.Contains("rendering mode", StringComparison.Ordinal));
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
            TimeZoneId: "UTC",
            InstalledFonts: ["Segoe UI"],
            RenderingMode: RenderMode.Default));

        Assert.False(result.IsCompatible);
        Assert.Collection(
            result.Differences,
            item => Assert.Contains("active input desktop", item, StringComparison.Ordinal),
            item => Assert.Contains("desktop=1920x1080", item, StringComparison.Ordinal),
            item => Assert.Contains("DPI=96", item, StringComparison.Ordinal),
            item => Assert.Contains("light theme", item, StringComparison.Ordinal),
            item => Assert.Contains("culture=zh-CN", item, StringComparison.Ordinal),
            item => Assert.Contains("UI culture=zh-CN", item, StringComparison.Ordinal),
            item => Assert.Contains("timezone=China Standard Time", item, StringComparison.Ordinal),
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
        TimeZoneId: "China Standard Time",
        InstalledFonts: ["Microsoft YaHei UI", "Consolas"],
        RenderingMode: RenderMode.SoftwareOnly);
}
