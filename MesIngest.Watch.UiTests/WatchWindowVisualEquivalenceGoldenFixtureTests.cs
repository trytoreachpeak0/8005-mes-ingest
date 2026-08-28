using System.IO;

namespace MesIngest.Watch.UiTests;

/// <summary>
/// Validates the visual-equivalence predicate against real golden-machine captures.
/// </summary>
/// <remarks>
/// These captures are large and live outside the repository, so the suite points at them
/// through <c>MESINGEST_WATCH_GOLDEN_FIXTURES</c> (the <c>.artifacts/golden-renderer</c>
/// directory). When the variable is absent the tests skip by name rather than silently
/// pass, so the release gate can account for them.
/// </remarks>
public sealed class WatchWindowVisualEquivalenceGoldenFixtureTests
{
    private const string NoUpdateLayoutRun =
        "ticket-23-font-state-no-updatelayout/run-20260818-151219-watch-window-stability"
        + "/Results/watch-window-stability";

    private const string SqueezedButtonRun =
        "ticket-23-height-experiment/run-20260818-193714-watch-window-stability"
        + "/Results/watch-window-stability";

    private const string ShiftedGlyphRun =
        "ticket-23-glyph-offset-experiment/run-20260818-211655-watch-window-stability"
        + "/Results/watch-window-stability";

    private const string CrossDeploymentAreaTextRun =
        "ticket-ticket-12-bilingual-promoted-final/"
        + "run-20260828-121739-watch-window-promoted-stability/Results/"
        + "watch-window-promoted-stability/run-01/production-workspace-19-22";

    [Fact]
    public void The_recorded_antialiasing_flip_is_accepted()
    {
        // run-08 and run-09 of the same batch differ only in the 跳转 glyph: 116 pixels,
        // all neutral, deltas of -1 and -2, glyph support untouched. This is the exact
        // difference the golden renderer used to fail on.
        var normal = LoadFixture($"{NoUpdateLayoutRun}/run-08/production-workspace-19-22/07-current-ingest-attention.png");
        var flipped = LoadFixture($"{NoUpdateLayoutRun}/run-09/production-workspace-19-22/07-current-ingest-attention.png");

        var report = WatchWindowVisualEquivalence.Compare(normal, flipped);
        Console.WriteLine($"[fixture] antialiasing flip -> {report.Describe()}");

        Assert.True(report.AreEquivalent, report.Rejection);
        Assert.Equal(116, report.DifferingPixels);
        Assert.Equal(2, report.MaxObservedDelta);
    }

    [Fact]
    public void The_recorded_cross_deployment_area_text_raster_flip_is_accepted()
    {
        var expected = LoadFixture($"{CrossDeploymentAreaTextRun}/expected.png");
        var actual = LoadFixture($"{CrossDeploymentAreaTextRun}/actual.png");

        var report = WatchWindowVisualEquivalence.Compare(expected, actual);
        Console.WriteLine($"[fixture] cross-deployment AREA text -> {report.Describe()}");

        Assert.True(report.AreEquivalent, report.Rejection);
        Assert.True(report.DifferingPixels > 648);
        Assert.InRange(report.MaxObservedDelta, 1, 3);
        Assert.True(report.IsRasterizationOnly);
    }

    [Fact]
    public void A_one_pixel_control_geometry_change_is_rejected()
    {
        // Same page and same text, but the command is 52x31 instead of 52x32.
        var natural = LoadFixture($"{NoUpdateLayoutRun}/run-08/production-workspace-19-22/07-current-ingest-attention.png");
        var squeezed = LoadFixture($"{SqueezedButtonRun}/run-01/production-workspace-19-22/07-current-ingest-attention.png");

        var report = WatchWindowVisualEquivalence.Compare(natural, squeezed);
        Console.WriteLine($"[fixture] 52x31 vs 52x32 -> {report.Describe()}");

        Assert.False(report.AreEquivalent);
    }

    [Fact]
    public void A_one_pixel_glyph_shift_is_rejected()
    {
        // Identical control geometry, identical text, glyph moved down one pixel.
        var natural = LoadFixture($"{NoUpdateLayoutRun}/run-08/production-workspace-19-22/07-current-ingest-attention.png");
        var shifted = LoadFixture($"{ShiftedGlyphRun}/run-01/production-workspace-19-22/07-current-ingest-attention.png");

        var report = WatchWindowVisualEquivalence.Compare(natural, shifted);
        Console.WriteLine($"[fixture] glyph shifted 1px -> {report.Describe()}");

        Assert.False(report.AreEquivalent);
    }

    [Fact]
    public void A_different_page_is_rejected()
    {
        var attention = LoadFixture($"{NoUpdateLayoutRun}/run-08/production-workspace-19-22/07-current-ingest-attention.png");
        var errorSearch = LoadFixture($"{NoUpdateLayoutRun}/run-08/production-workspace-19-22/06-error-search-variant-a.png");

        var report = WatchWindowVisualEquivalence.Compare(attention, errorSearch);
        Console.WriteLine($"[fixture] different page -> {report.Describe()}");

        Assert.False(report.AreEquivalent);
    }

    private static byte[] LoadFixture(string relativePath)
    {
        var root = Environment.GetEnvironmentVariable("MESINGEST_WATCH_GOLDEN_FIXTURES");
        if (string.IsNullOrWhiteSpace(root))
        {
            Assert.Skip(
                "MESINGEST_WATCH_GOLDEN_FIXTURES is not set; real golden captures are "
                + "unavailable in this environment.");
        }

        var path = Path.Combine(root!, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            Assert.Skip($"Golden capture fixture is missing: {path}");
        }

        return File.ReadAllBytes(path);
    }
}
