using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MesIngest.Watch.UiTests;

internal sealed partial class WatchJourneyEvidence
{
    private readonly string _directory;
    private readonly string[] _sensitiveValues;

    public WatchJourneyEvidence(
        string rootDirectory,
        string journeyName,
        IEnumerable<string?> sensitiveValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyName);
        _directory = Path.Combine(rootDirectory, SafeFileName(journeyName));
        _sensitiveValues = sensitiveValues
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static value => value.Length)
            .ToArray();
        Directory.CreateDirectory(_directory);
        WriteText("watch-logs.txt", string.Empty);
    }

    public string DirectoryPath => _directory;

    public void RecordStep(string step, byte[] pngBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        ArgumentNullException.ThrowIfNull(pngBytes);
        File.WriteAllBytes(Path.Combine(_directory, $"{SafeFileName(step)}.png"), pngBytes);
    }

    public void RecordUiaTree(string tree) => WriteText("uia-tree.txt", tree);

    public void RecordProcessOutput(string stdout, string stderr)
    {
        WriteText("stdout.txt", stdout);
        WriteText("stderr.txt", stderr);
    }

    public void RecordFakeHostTimeline(IEnumerable<string> entries, string summary)
    {
        WriteText("fake-host-timeline.txt", string.Join(Environment.NewLine, entries));
        WriteText("fake-host-summary.txt", summary);
    }

    public void RecordEnvironment(string manifest) => WriteText("environment.txt", manifest);

    public void RecordWatchLogs(string logDirectory)
    {
        if (!Directory.Exists(logDirectory))
        {
            WriteText("watch-logs.txt", "(no Watch log directory was created)");
            return;
        }

        var output = new StringBuilder();
        foreach (var file in Directory.GetFiles(logDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            output.AppendLine($"--- {Path.GetFileName(file)} ---");
            try
            {
                output.AppendLine(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.AppendLine($"(unreadable: {ex.GetType().Name}: {ex.Message})");
            }
        }

        WriteText("watch-logs.txt", output.ToString());
    }

    public void RecordFailure(string step, Exception exception, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        ArgumentNullException.ThrowIfNull(exception);
        WriteText(
            "failure.txt",
            string.Join(
                Environment.NewLine,
                $"step={step}",
                $"exception={exception.GetType().FullName}",
                $"timeoutMs={timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}",
                $"message={exception.Message}",
                "firstFailureRetained=true",
                exception.StackTrace ?? "(no stack trace)"));
    }

    public void RecordWindowComparison(byte[] expected, byte[] actual, byte[] diff)
    {
        File.WriteAllBytes(Path.Combine(_directory, "expected.png"), expected);
        File.WriteAllBytes(Path.Combine(_directory, "actual.png"), actual);
        File.WriteAllBytes(Path.Combine(_directory, "diff.png"), diff);
    }

    public void RecordReceivedXaml(string xaml) => WriteText("received.xaml", xaml);

    private void WriteText(string fileName, string value) =>
        File.WriteAllText(
            Path.Combine(_directory, fileName),
            Redact(value ?? string.Empty),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private string Redact(string value)
    {
        var redacted = value;
        foreach (var sensitive in _sensitiveValues)
        {
            redacted = redacted.Replace(sensitive, "(redacted)", StringComparison.Ordinal);
        }

        redacted = AuthorizationRegex().Replace(redacted, "$1(redacted)");
        redacted = SensitiveQueryRegex().Replace(redacted, "$1(redacted)");
        return redacted;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray());
        return sanitized.Trim().Replace(' ', '-');
    }

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*(?:bearer\\s+)?)[^\\s;,]+")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex("(?i)((?:cursor|demandId|credential|sharedSecret)=)[^&\\s;,]+")]
    private static partial Regex SensitiveQueryRegex();
}
