using System.Globalization;

namespace MesIngest.Watch;

/// <summary>
/// Builds Excel-friendly clipboard text for Watch DataGrid cells and rows.
/// </summary>
internal static class WatchGridClipboard
{
    public static string FormatValue(object? value, TimeZoneInfo? timeZone = null) =>
        value switch
        {
            null => "null",
            DateTimeOffset dto => WatchTimeDisplay.Format(dto, timeZone),
            string s => s,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "null",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
        };

    public static string FormatRow(IEnumerable<object?> values, TimeZoneInfo? timeZone = null) =>
        string.Join("\t", values.Select(v => EscapeTsvField(FormatValue(v, timeZone))));

    public static string FormatRowWithHeaders(
        IReadOnlyList<string> headers,
        IEnumerable<object?> values,
        TimeZoneInfo? timeZone = null)
    {
        var headerLine = string.Join("\t", headers.Select(EscapeTsvField));
        return headerLine + "\n" + FormatRow(values, timeZone);
    }

    public static string EscapeTsvField(string value)
    {
        if (value.IndexOfAny(['\t', '\n', '\r', '"']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static (IReadOnlyList<string> Headers, IReadOnlyList<object?> Values) ProjectRow(
        object rowItem,
        IEnumerable<(string Header, string PropertyPath)> columns)
    {
        var headers = new List<string>();
        var values = new List<object?>();
        foreach (var (header, path) in columns)
        {
            headers.Add(header);
            values.Add(ReadProperty(rowItem, path));
        }

        return (headers, values);
    }

    public static object? ReadProperty(object rowItem, string propertyPath)
    {
        object? current = rowItem;
        foreach (var segment in propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null)
            {
                return null;
            }

            var type = current.GetType();
            var prop = type.GetProperty(segment);
            if (prop is null)
            {
                return null;
            }

            current = prop.GetValue(current);
        }

        return current;
    }
}
