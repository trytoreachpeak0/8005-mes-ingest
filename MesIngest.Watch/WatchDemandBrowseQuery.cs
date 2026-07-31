using System.Globalization;

namespace MesIngest.Watch;

/// <summary>
/// Server-side /api/demands browse parameters for the Watch thin client.
/// </summary>
internal sealed record WatchDemandBrowseQuery(
    string Status = "VISIBLE",
    string? TaskType = null,
    string? Sublot = null,
    string? DemandId = null,
    DateTimeOffset? DatesFrom = null,
    DateTimeOffset? DatesTo = null,
    DateTimeOffset? GoneAtFrom = null,
    DateTimeOffset? GoneAtTo = null,
    string SortBy = "dates",
    string Direction = "desc",
    int Limit = 100,
    string? Cursor = null)
{
    public static WatchDemandBrowseQuery Default { get; } = new();

    public static bool IsDemandIdFilterReady(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var value = raw.Trim().ToLowerInvariant();
        if (value.Length is < 6 or > 32)
        {
            return false;
        }

        foreach (var ch in value)
        {
            var isHex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    public string ToRelativeUrl()
    {
        var parts = new List<string>
        {
            Pair("status", Status),
            Pair("sortBy", SortBy),
            Pair("direction", Direction),
            Pair("limit", Limit.ToString(CultureInfo.InvariantCulture)),
        };

        AddIfPresent(parts, "taskType", TaskType);
        AddIfPresent(parts, "sublot", Sublot);
        AddIfPresent(parts, "demandId", NormalizeDemandId(DemandId));
        AddIfPresent(parts, "datesFrom", FormatOffset(DatesFrom));
        AddIfPresent(parts, "datesTo", FormatOffset(DatesTo));
        AddIfPresent(parts, "goneAtFrom", FormatOffset(GoneAtFrom));
        AddIfPresent(parts, "goneAtTo", FormatOffset(GoneAtTo));
        AddIfPresent(parts, "cursor", Cursor);

        return "/api/demands?" + string.Join("&", parts);
    }

    private static string? NormalizeDemandId(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();

    private static string? FormatOffset(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    private static string Pair(string name, string value) =>
        Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);

    private static void AddIfPresent(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(Pair(name, value.Trim()));
        }
    }
}

internal sealed record WatchDemandPage(
    IReadOnlyList<WatchDemandDto> Items,
    string? NextCursor,
    bool HasMore);
