using System.Globalization;
using System.Text;

namespace MesIngest.Core;

public interface IMesSnapshotSource
{
    Task<MesSnapshotOutcome> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads a recorded MES_TASK_UNION CSV with header:
/// TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE
/// DATES without offset are interpreted as Beijing time (UTC+8).
/// </summary>
public sealed class CsvFileMesSnapshotSource : IMesSnapshotSource
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    private readonly string _path;

    public CsvFileMesSnapshotSource(string path)
    {
        _path = path;
    }

    public async Task<MesSnapshotOutcome> ReadAsync(CancellationToken cancellationToken = default)
    {
        var lines = await File.ReadAllLinesAsync(_path, Encoding.UTF8, cancellationToken);
        if (lines.Length == 0)
        {
            return MesSnapshotOutcome.Success(Array.Empty<MesSnapshotRow>());
        }

        var header = ParseCsvLine(lines[0]);
        var index = header
            .Select((name, i) => (name, i))
            .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);

        string[] required = ["TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "DATES", "PACKAGE"];
        if (required.Any(name => !index.ContainsKey(name)))
        {
            return MesSnapshotOutcome.Incomplete();
        }

        var rows = new List<MesSnapshotRow>();
        for (var lineNumber = 1; lineNumber < lines.Length; lineNumber++)
        {
            var line = lines[lineNumber];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cols = ParseCsvLine(line);
            try
            {
                var taskType = cols[index["TASK_TYPE"]];
                var sublot = cols[index["SUBLOT"]];
                if (string.IsNullOrWhiteSpace(taskType) || string.IsNullOrWhiteSpace(sublot))
                {
                    return MesSnapshotOutcome.Incomplete();
                }

                if (!TryParseDates(cols[index["DATES"]], out var dates))
                {
                    return MesSnapshotOutcome.Incomplete();
                }

                rows.Add(new MesSnapshotRow(
                    TaskType: taskType,
                    Sublot: sublot,
                    Area: EmptyToNull(cols[index["AREA"]]),
                    Eqp: EmptyToNull(cols[index["EQP"]]),
                    Step: EmptyToNull(cols[index["STEP"]]),
                    Dates: dates,
                    Package: EmptyToNull(cols[index["PACKAGE"]])));
            }
            catch (IndexOutOfRangeException)
            {
                return MesSnapshotOutcome.Incomplete();
            }
        }

        return MesSnapshotOutcome.Success(rows);
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryParseDates(string raw, out DateTimeOffset dates)
    {
        if (HasExplicitOffset(raw))
        {
            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                    out dates))
            {
                return true;
            }
        }
        else if (DateTime.TryParse(
                     raw,
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.AllowWhiteSpaces,
                     out var clock))
        {
            var unspecified = DateTime.SpecifyKind(clock, DateTimeKind.Unspecified);
            dates = new DateTimeOffset(unspecified, BeijingOffset);
            return true;
        }

        dates = default;
        return false;
    }

    private static bool HasExplicitOffset(string raw) =>
        raw.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
        || raw.Contains('+', StringComparison.Ordinal)
        || (raw.Contains('T', StringComparison.Ordinal)
            && raw.LastIndexOf('-') > raw.IndexOf('T'));

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        values.Add(current.ToString());
        return values.ToArray();
    }
}
