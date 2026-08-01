using System.Globalization;

namespace MesIngest.Watch;

/// <summary>
/// Server-side /api/alerts browse parameters for the Watch thin client.
/// SortBy null means Host default priority sort (active ERROR/WARNING then LastSeenAt).
/// </summary>
internal sealed record WatchAlertBrowseQuery(
    string? SortBy = null,
    string Direction = "desc",
    int Limit = 100,
    string? Cursor = null)
{
    public static WatchAlertBrowseQuery Default { get; } = new();

    /// <summary>
    /// Maps Alerts grid column headers to Host /api/alerts sortBy allow-list tokens.
    /// Non-allow-list columns return null (must not start a sort).
    /// </summary>
    public static string? SortToken(string? header) =>
        header switch
        {
            "Code" => "code",
            "Severity" => "severity",
            "AlertId" => "alertId",
            "last seen" => "lastSeenAt",
            "first seen" => "firstSeenAt",
            _ => null,
        };

    public static IReadOnlyList<string> AllowListTokens { get; } =
    [
        "lastSeenAt",
        "firstSeenAt",
        "code",
        "severity",
        "alertId",
    ];

    public string ToRelativeUrl()
    {
        var parts = new List<string>
        {
            Pair("limit", Limit.ToString(CultureInfo.InvariantCulture)),
        };

        if (!string.IsNullOrWhiteSpace(SortBy))
        {
            parts.Add(Pair("sortBy", SortBy));
            parts.Add(Pair("direction", Direction));
        }

        if (!string.IsNullOrWhiteSpace(Cursor))
        {
            parts.Add(Pair("cursor", Cursor));
        }

        return "/api/alerts?" + string.Join("&", parts);
    }

    private static string Pair(string name, string value) =>
        Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
}
