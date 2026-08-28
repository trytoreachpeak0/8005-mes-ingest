using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace MesIngest.Watch.UiTests;

internal static class WatchWindowBaseline
{
    public static void Verify(
        string baselineName,
        byte[] actual,
        WatchJourneyEvidence evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineName);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.Equals(
                Environment.GetEnvironmentVariable("MESINGEST_WATCH_CAPTURE_WINDOW_CANDIDATES"),
                "1",
                StringComparison.Ordinal))
        {
            File.WriteAllBytes(
                Path.Combine(evidence.DirectoryPath, $"{baselineName}-1440x900.candidate.png"),
                actual);
            return;
        }

        var directory = ResolveBaselineDirectory();
        var expectedPath = Path.Combine(directory, $"{baselineName}-1440x900.verified.png");
        if (!File.Exists(expectedPath))
        {
            var receivedPath = Path.Combine(
                evidence.DirectoryPath,
                $"{baselineName}-1440x900.received.png");
            File.WriteAllBytes(receivedPath, actual);
            throw new Xunit.Sdk.XunitException(
                $"Window baseline is missing: {expectedPath}. Candidate: {receivedPath}");
        }

        var expected = File.ReadAllBytes(expectedPath);
        if (expected.AsSpan().SequenceEqual(actual))
        {
            return;
        }

        var diff = CreateDiff(expected, actual);
        var options = WatchWindowVisualEquivalenceOptions.Default;
        var report = WatchWindowVisualEquivalence.Compare(expected, actual, options);
        var budgetRejection = string.Empty;
        if (report.AreEquivalent
            && WithinRunBudget(baselineName, report, evidence, options, out budgetRejection))
        {
            evidence.RecordVisualEquivalence(
                baselineName,
                report.DifferingPixels,
                report.MaxObservedDelta,
                report.Describe(),
                report.IsRasterizationOnly,
                expected,
                actual,
                diff);
            Console.WriteLine(
                "WATCH_WINDOW_VISUAL_EQUIVALENCE_ACCEPTED: "
                + $"step={baselineName} pixels={report.DifferingPixels} "
                + $"maxDelta={report.MaxObservedDelta} "
                + $"classification={(report.IsRasterizationOnly ? "edge-raster-only" : "bounded-neutral")} "
                + $"runTotalSteps={evidence.AcceptedVisualEquivalenceSteps} "
                + $"runTotalPixels={evidence.AcceptedVisualEquivalencePixels} "
                + $"artifacts={evidence.DirectoryPath}");
            return;
        }

        evidence.RecordWindowComparison(expected, actual, diff);
        var reason = report.AreEquivalent ? budgetRejection : report.Rejection;
        throw new Xunit.Sdk.XunitException(
            $"Window baseline mismatch for {baselineName}: {reason}. "
            + $"See {evidence.DirectoryPath}.");
    }

    /// <summary>
    /// A single accepted capture is bounded by <see cref="WatchWindowVisualEquivalence"/>.
    /// This guards the run as a whole, so tolerance cannot erode a baseline one step at a
    /// time across a journey.
    /// </summary>
    private static bool WithinRunBudget(
        string baselineName,
        WatchWindowVisualEquivalenceReport report,
        WatchJourneyEvidence evidence,
        WatchWindowVisualEquivalenceOptions options,
        out string rejection)
    {
        if (report.IsRasterizationOnly)
        {
            rejection = string.Empty;
            return true;
        }

        var steps = evidence.AcceptedBudgetedVisualEquivalenceSteps + 1;
        if (steps > options.MaxToleratedStepsPerRun)
        {
            rejection =
                $"{steps} steps in this run would need visual-equivalence tolerance, "
                + $"limit is {options.MaxToleratedStepsPerRun} (this step: {baselineName})";
            return false;
        }

        var pixels = evidence.AcceptedBudgetedVisualEquivalencePixels + report.DifferingPixels;
        if (pixels > options.MaxDifferingPixelsPerRun)
        {
            rejection =
                $"{pixels} differing pixels would be tolerated in this run, "
                + $"limit is {options.MaxDifferingPixelsPerRun}";
            return false;
        }

        rejection = string.Empty;
        return true;
    }

    private static string ResolveBaselineDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(
            "MESINGEST_WATCH_WINDOW_BASELINE_DIRECTORY");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "WindowBaselines")
            : Path.GetFullPath(configured);
    }

    internal static byte[] CreateDiff(byte[] expectedBytes, byte[] actualBytes)
    {
        using var expectedStream = new MemoryStream(expectedBytes);
        using var actualStream = new MemoryStream(actualBytes);
        using var expected = new Bitmap(expectedStream);
        using var actual = new Bitmap(actualStream);
        var width = Math.Max(expected.Width, actual.Width);
        var height = Math.Max(expected.Height, actual.Height);
        using var diff = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x >= expected.Width || y >= expected.Height
                    || x >= actual.Width || y >= actual.Height)
                {
                    diff.SetPixel(x, y, Color.Magenta);
                    continue;
                }

                var expectedPixel = expected.GetPixel(x, y);
                var actualPixel = actual.GetPixel(x, y);
                diff.SetPixel(
                    x,
                    y,
                    expectedPixel == actualPixel
                        ? Color.FromArgb(255, 245, 245, 245)
                        : Color.FromArgb(255, 255, 0, 255));
            }
        }

        using var output = new MemoryStream();
        diff.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
}
