using System.Globalization;

namespace MesIngest.Watch;

/// <summary>
/// Converts stored DateTimeOffset instants to the Watch operator's display timezone.
/// </summary>
internal static class WatchTimeDisplay
{
    public const string Pattern = "yyyy-MM-dd HH:mm:ss zzz";

    public static string Format(DateTimeOffset value, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(value, zone);
        return local.ToString(Pattern, CultureInfo.InvariantCulture);
    }

    public static string FormatNullable(DateTimeOffset? value, TimeZoneInfo? timeZone = null) =>
        value is null ? string.Empty : Format(value.Value, timeZone);
}
