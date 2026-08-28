using System.Drawing;
using System.Globalization;
using System.IO;

namespace MesIngest.Watch.UiTests;

/// <summary>
/// Compares two real-window candidate directories with the same bounded
/// visual-equivalence predicate the promoted-baseline gate uses.
/// </summary>
/// <remarks>
/// The candidate stability gate lives in PowerShell and compares SHA-256 hashes, which is
/// the right fast path. When hashes diverge it needs the same judgement the C# gate makes,
/// otherwise the two gates would disagree about what counts as a regression. Rather than
/// reimplement the predicate in PowerShell, <c>Test-WatchWindowBaselineStability.ps1</c>
/// invokes this entry point with the two run directories.
/// Each candidate must also carry the UIA Text-region mask captured with it. The union
/// of both masks removes text pixels from visual judgement while leaving control and
/// layout pixels under the same bounded predicate. Both masks are validated against the
/// PNG frame before even the byte-identical fast path can pass.
/// </remarks>
[Trait("Category", "watch-window-candidate-equivalence")]
public sealed class WatchWindowCandidateEquivalenceTests
{
    [Fact]
    public void Candidate_directories_are_visually_equivalent()
    {
        var referenceDirectory = Environment.GetEnvironmentVariable(
            "MESINGEST_WATCH_CANDIDATE_REFERENCE");
        var actualDirectory = Environment.GetEnvironmentVariable(
            "MESINGEST_WATCH_CANDIDATE_ACTUAL");
        if (string.IsNullOrWhiteSpace(referenceDirectory)
            || string.IsNullOrWhiteSpace(actualDirectory))
        {
            Assert.Skip(
                "MESINGEST_WATCH_CANDIDATE_REFERENCE and MESINGEST_WATCH_CANDIDATE_ACTUAL "
                + "are not set; this entry point is driven by the stability gate.");
        }

        var reference = EnumerateCandidates(referenceDirectory!);
        var actual = EnumerateCandidates(actualDirectory!);

        var missing = reference.Keys.Except(actual.Keys, StringComparer.Ordinal).ToArray();
        var unexpected = actual.Keys.Except(reference.Keys, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0 && unexpected.Length == 0,
            $"Candidate set changed. Missing: [{string.Join(", ", missing)}]. "
            + $"Unexpected: [{string.Join(", ", unexpected)}].");

        var options = WatchWindowVisualEquivalenceOptions.Default;
        var evidence = new WatchJourneyEvidence(
            WatchWindowJourneySupport.ResolveArtifactRoot(),
            "candidate-equivalence",
            []);
        var toleratedSteps = 0;
        var toleratedPixels = 0;
        var budgetedToleratedSteps = 0;
        var budgetedToleratedPixels = 0;
        var failures = new List<string>();

        foreach (var name in reference.Keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            var expectedBytes = File.ReadAllBytes(reference[name]);
            var actualBytes = File.ReadAllBytes(actual[name]);
            var referenceMask = LoadTextMask(reference[name]);
            var actualMask = LoadTextMask(actual[name]);
            var frame = ReadFrame(actualBytes);
            var ignoredRegions = referenceMask.UnionForComparison(
                actualMask,
                frame.Width,
                frame.Height);
            evidence.RecordTextMask(
                Path.GetFileNameWithoutExtension(name),
                actualBytes,
                ignoredRegions);
            if (expectedBytes.AsSpan().SequenceEqual(actualBytes))
            {
                continue;
            }

            var report = WatchWindowVisualEquivalence.Compare(
                expectedBytes,
                actualBytes,
                options,
                ignoredRegions);
            if (!report.AreEquivalent)
            {
                failures.Add($"{name}: {report.Rejection}");
                continue;
            }

            toleratedSteps++;
            toleratedPixels += report.TotalDifferingPixels;
            if (report.ConsumesOrdinaryBudget)
            {
                budgetedToleratedSteps++;
                budgetedToleratedPixels += report.DifferingPixels;
            }
            evidence.RecordVisualEquivalence(
                Path.GetFileNameWithoutExtension(name),
                report.TotalDifferingPixels,
                report.MaxObservedDelta,
                report.Describe(),
                report.Classification,
                report.ConsumesOrdinaryBudget,
                report.ConsumesOrdinaryBudget ? report.DifferingPixels : 0,
                expectedBytes,
                actualBytes,
                WatchWindowBaseline.CreateDiff(expectedBytes, actualBytes));
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "WATCH_WINDOW_VISUAL_EQUIVALENCE_ACCEPTED: candidate={0} pixels={1} "
                + "maxDelta={2} regions={3} classification={4}",
                name,
                report.TotalDifferingPixels,
                report.MaxObservedDelta,
                report.Components.Count,
                report.Classification));
        }

        if (budgetedToleratedSteps > options.MaxToleratedStepsPerRun)
        {
            failures.Add(
                $"{budgetedToleratedSteps} non-raster candidates needed tolerance, "
                + $"limit is {options.MaxToleratedStepsPerRun}");
        }

        if (budgetedToleratedPixels > options.MaxDifferingPixelsPerRun)
        {
            failures.Add(
                $"{budgetedToleratedPixels} non-raster differing pixels tolerated, "
                + $"limit is {options.MaxDifferingPixelsPerRun}");
        }

        Assert.True(
            failures.Count == 0,
            "Real-window candidates are not visually equivalent:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, failures));

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "WATCH_WINDOW_CANDIDATE_EQUIVALENCE_OK: tolerated={0} pixels={1}",
            toleratedSteps,
            toleratedPixels));
    }

    private static Size ReadFrame(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);
        return bitmap.Size;
    }

    private static Dictionary<string, string> EnumerateCandidates(string directory)
    {
        Assert.True(Directory.Exists(directory), $"Candidate directory is missing: {directory}");
        return Directory
            .EnumerateFiles(directory, "*.candidate.png", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetFileName(path),
                path => path,
                StringComparer.Ordinal);
    }

    private static WatchWindowTextMask LoadTextMask(string candidatePath)
    {
        const string suffix = ".candidate.png";
        Assert.EndsWith(suffix, candidatePath, StringComparison.Ordinal);
        var maskPath = candidatePath[..^suffix.Length] + ".candidate.text-mask.json";
        Assert.True(
            File.Exists(maskPath),
            $"Candidate text mask is missing: {maskPath}");
        return WatchWindowTextMask.Load(maskPath);
    }
}
