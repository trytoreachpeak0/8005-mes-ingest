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
    string? Cursor = null,
    bool? Active = true,
    string? Code = null,
    string? Severity = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null)
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
            "TASK_TYPE" => "taskType",
            "SUBLOT" => "sublot",
            "DemandId" => "demandId",
            "Message" => "message",
            _ => null,
        };

    public static IReadOnlyList<string> AllowListTokens { get; } =
    [
        "lastSeenAt",
        "firstSeenAt",
        "code",
        "severity",
        "alertId",
        "taskType",
        "sublot",
        "demandId",
        "message",
    ];

    public bool TryApplySort(string? header, out WatchAlertBrowseQuery next)
    {
        var token = SortToken(header);
        if (token is null)
        {
            next = this;
            return false;
        }

        var direction = string.Equals(SortBy, token, StringComparison.Ordinal)
            && string.Equals(Direction, "asc", StringComparison.Ordinal)
                ? "desc"
                : "asc";
        next = this with { SortBy = token, Direction = direction };
        return true;
    }

    public string ToRelativeUrl()
    {
        var parts = new List<string>
        {
            Pair("limit", Limit.ToString(CultureInfo.InvariantCulture)),
        };

        if (Active is bool active)
        {
            parts.Add(Pair("active", active ? "true" : "false"));
        }

        if (!string.IsNullOrWhiteSpace(SortBy))
        {
            parts.Add(Pair("sortBy", SortBy));
            parts.Add(Pair("direction", Direction));
        }

        if (!string.IsNullOrWhiteSpace(Cursor))
        {
            parts.Add(Pair("cursor", Cursor));
        }

        AddIfPresent(parts, "code", Code);
        AddIfPresent(parts, "severity", Severity);
        AddIfPresent(parts, "from", FormatOffset(From));
        AddIfPresent(parts, "to", FormatOffset(To));

        return "/api/alerts?" + string.Join("&", parts);
    }

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
