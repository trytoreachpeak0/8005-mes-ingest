using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesIngest.Core;

public enum AlertSortColumn
{
    LastSeenAt,
    FirstSeenAt,
    Code,
    Severity,
    AlertId,
    TaskType,
    Sublot,
    DemandId,
    Message,
}

public sealed class AlertListQuery
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 200;

    public bool? Active { get; init; }
    public string? Code { get; init; }
    public string? Severity { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public AlertSortColumn SortBy { get; init; } = AlertSortColumn.LastSeenAt;
    public SortDirection Direction { get; init; } = SortDirection.Desc;
    public int Limit { get; init; } = DefaultLimit;
    public string? Cursor { get; init; }
    public bool UseDefaultPrioritySort { get; init; } = true;
}

public sealed record AlertListPage(
    IReadOnlyList<IngestAlert> Items,
    string? NextCursor,
    bool HasMore);

public static class AlertListQueryParser
{
    public static bool TryParseActive(string? raw, out bool? active, out string? error)
    {
        active = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            active = parsed;
            return true;
        }

        if (raw.Equals("1", StringComparison.Ordinal) || raw.Equals("0", StringComparison.Ordinal))
        {
            active = raw == "1";
            return true;
        }

        error = "active must be true or false";
        return false;
    }

    public static bool TryParseSortBy(string? raw, out AlertSortColumn sortBy, out string? error)
    {
        sortBy = AlertSortColumn.LastSeenAt;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "lastseenat":
                sortBy = AlertSortColumn.LastSeenAt;
                return true;
            case "firstseenat":
                sortBy = AlertSortColumn.FirstSeenAt;
                return true;
            case "code":
                sortBy = AlertSortColumn.Code;
                return true;
            case "severity":
                sortBy = AlertSortColumn.Severity;
                return true;
            case "alertid":
                sortBy = AlertSortColumn.AlertId;
                return true;
            case "tasktype":
                sortBy = AlertSortColumn.TaskType;
                return true;
            case "sublot":
                sortBy = AlertSortColumn.Sublot;
                return true;
            case "demandid":
                sortBy = AlertSortColumn.DemandId;
                return true;
            case "message":
                sortBy = AlertSortColumn.Message;
                return true;
            default:
                error = "sortBy must be one of lastSeenAt, firstSeenAt, code, severity, alertId, taskType, sublot, demandId, message";
                return false;
        }
    }

    public static bool TryParseDirection(string? raw, out SortDirection direction, out string? error)
    {
        direction = SortDirection.Desc;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (raw.Equals("asc", StringComparison.OrdinalIgnoreCase))
        {
            direction = SortDirection.Asc;
            return true;
        }

        if (raw.Equals("desc", StringComparison.OrdinalIgnoreCase))
        {
            direction = SortDirection.Desc;
            return true;
        }

        error = "direction must be asc or desc";
        return false;
    }

    public static bool TryParseLimit(string? raw, out int limit, out string? error)
    {
        limit = AlertListQuery.DefaultLimit;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1)
        {
            error = "limit must be a positive integer";
            return false;
        }

        if (parsed > AlertListQuery.MaxLimit)
        {
            error = $"limit must be <= {AlertListQuery.MaxLimit}";
            return false;
        }

        limit = parsed;
        return true;
    }

    public static bool TryParseDateTimeOffset(string? raw, out DateTimeOffset? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            error = "invalid DateTimeOffset";
            return false;
        }

        value = parsed;
        return true;
    }
}

public static class AlertListCursor
{
    private sealed record CursorPayload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("s")] string SortBy,
        [property: JsonPropertyName("d")] string Direction,
        [property: JsonPropertyName("a")] bool? IsActive,
        [property: JsonPropertyName("r")] int? SeverityRank,
        [property: JsonPropertyName("t")] string? Time,
        [property: JsonPropertyName("c")] string? Code,
        [property: JsonPropertyName("sev")] string? Severity,
        [property: JsonPropertyName("tt")] string? TaskType,
        [property: JsonPropertyName("sl")] string? Sublot,
        [property: JsonPropertyName("did")] string? DemandId,
        [property: JsonPropertyName("m")] string? Message,
        [property: JsonPropertyName("id")] string AlertId);

    public static string Encode(IngestAlert alert, AlertListQuery query)
    {
        var payload = new CursorPayload(
            Version: 1,
            SortBy: query.SortBy.ToString(),
            Direction: query.Direction.ToString(),
            IsActive: query.UseDefaultPrioritySort ? alert.IsActive : null,
            SeverityRank: query.UseDefaultPrioritySort
                ? IngestAlertCatalog.SeverityRank(alert.Severity)
                : null,
            Time: query.SortBy switch
            {
                AlertSortColumn.FirstSeenAt => alert.EffectiveFirstSeenAt.ToString("O"),
                AlertSortColumn.LastSeenAt => alert.EffectiveLastSeenAt.ToString("O"),
                _ => alert.EffectiveLastSeenAt.ToString("O"),
            },
            Code: query.SortBy == AlertSortColumn.Code ? alert.Code : null,
            Severity: query.SortBy == AlertSortColumn.Severity ? alert.Severity : null,
            TaskType: query.SortBy == AlertSortColumn.TaskType ? alert.TaskType : null,
            Sublot: query.SortBy == AlertSortColumn.Sublot ? alert.Sublot : null,
            DemandId: query.SortBy == AlertSortColumn.DemandId ? alert.DemandId : null,
            Message: query.SortBy == AlertSortColumn.Message ? alert.Message : null,
            AlertId: alert.AlertId ?? string.Empty);

        var json = JsonSerializer.Serialize(payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(
        string? cursor,
        AlertSortColumn sortBy,
        SortDirection direction,
        out CursorState? state,
        out string? error)
    {
        state = null;
        error = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch
            {
                2 => "==",
                3 => "=",
                _ => "",
            };
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var payload = JsonSerializer.Deserialize<CursorPayload>(json);
            if (payload is null || payload.Version != 1)
            {
                error = "cursor is invalid";
                return false;
            }

            if (!string.Equals(payload.SortBy, sortBy.ToString(), StringComparison.Ordinal)
                || !string.Equals(payload.Direction, direction.ToString(), StringComparison.Ordinal))
            {
                error = "cursor does not match sort";
                return false;
            }

            DateTimeOffset? time = null;
            if (!string.IsNullOrWhiteSpace(payload.Time)
                && DateTimeOffset.TryParse(payload.Time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                time = parsed;
            }

            state = new CursorState(
                payload.IsActive,
                payload.SeverityRank,
                time,
                payload.Code,
                payload.Severity,
                payload.TaskType,
                payload.Sublot,
                payload.DemandId,
                payload.Message,
                payload.AlertId);
            return true;
        }
        catch
        {
            error = "cursor is invalid";
            return false;
        }
    }

    public sealed record CursorState(
        bool? IsActive,
        int? SeverityRank,
        DateTimeOffset? Time,
        string? Code,
        string? Severity,
        string? TaskType,
        string? Sublot,
        string? DemandId,
        string? Message,
        string AlertId);
}

public static class AlertListPaging
{
    public static AlertListPage Page(
        IEnumerable<IngestAlert> source,
        AlertListQuery query,
        AlertListCursor.CursorState? cursor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        IEnumerable<IngestAlert> filtered = source;
        if (query.Active is bool active)
        {
            filtered = filtered.Where(a => a.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(query.Code))
        {
            filtered = filtered.Where(a =>
                string.Equals(a.Code, query.Code, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            filtered = filtered.Where(a =>
                string.Equals(a.Severity, query.Severity, StringComparison.OrdinalIgnoreCase));
        }

        if (query.From is DateTimeOffset from)
        {
            filtered = filtered.Where(a => a.EffectiveLastSeenAt >= from);
        }

        if (query.To is DateTimeOffset to)
        {
            filtered = filtered.Where(a => a.EffectiveLastSeenAt <= to);
        }

        var ordered = Order(filtered, query).ToList();
        if (cursor is not null)
        {
            ordered = ordered.Where(a => AfterCursor(a, cursor, query)).ToList();
        }

        var limit = Math.Clamp(query.Limit, 1, AlertListQuery.MaxLimit);
        var take = ordered.Take(limit + 1).ToList();
        var hasMore = take.Count > limit;
        if (hasMore)
        {
            take.RemoveAt(take.Count - 1);
        }

        return new AlertListPage(
            take,
            hasMore && take.Count > 0 ? AlertListCursor.Encode(take[^1], query) : null,
            hasMore);
    }

    private static IOrderedEnumerable<IngestAlert> Order(
        IEnumerable<IngestAlert> source,
        AlertListQuery query)
    {
        if (query.UseDefaultPrioritySort
            && query.SortBy == AlertSortColumn.LastSeenAt
            && query.Direction == SortDirection.Desc)
        {
            return source
                .OrderByDescending(a => a.IsActive)
                .ThenBy(a => IngestAlertCatalog.SeverityRank(a.Severity))
                .ThenByDescending(a => a.EffectiveLastSeenAt)
                .ThenBy(a => a.AlertId ?? string.Empty, StringComparer.Ordinal);
        }

        IOrderedEnumerable<IngestAlert> ordered = query.SortBy switch
        {
            AlertSortColumn.FirstSeenAt => query.Direction == SortDirection.Asc
                ? source.OrderBy(a => a.EffectiveFirstSeenAt)
                : source.OrderByDescending(a => a.EffectiveFirstSeenAt),
            AlertSortColumn.Code => query.Direction == SortDirection.Asc
                ? source.OrderBy(a => a.Code, StringComparer.Ordinal)
                : source.OrderByDescending(a => a.Code, StringComparer.Ordinal),
            AlertSortColumn.Severity => query.Direction == SortDirection.Asc
                ? source.OrderBy(a => IngestAlertCatalog.SeverityRank(a.Severity))
                : source.OrderByDescending(a => IngestAlertCatalog.SeverityRank(a.Severity)),
            AlertSortColumn.AlertId => query.Direction == SortDirection.Asc
                ? source.OrderBy(a => a.AlertId ?? string.Empty, StringComparer.Ordinal)
                : source.OrderByDescending(a => a.AlertId ?? string.Empty, StringComparer.Ordinal),
            AlertSortColumn.TaskType => query.Direction == SortDirection.Asc
                ? source.OrderBy(a => a.TaskType, StringComparer.Ordinal)
                : source.OrderByDescending(a => a.TaskType, StringComparer.Ordinal),
            AlertSortColumn.Sublot => query.Direction == SortDirection.Asc
                ? source.OrderBy(a => a.Sublot, StringComparer.Ordinal)
                : source.OrderByDescending(a => a.Sublot, StringComparer.Ordinal),
            AlertSortColumn.DemandId => query.Direction == SortDirection.Asc
                ? source.OrderBy(a => a.DemandId, StringComparer.Ordinal)
                : source.OrderByDescending(a => a.DemandId, StringComparer.Ordinal),
            AlertSortColumn.Message => query.Direction == SortDirection.Asc
                ? source.OrderBy(a => a.Message, StringComparer.Ordinal)
                : source.OrderByDescending(a => a.Message, StringComparer.Ordinal),
            _ => query.Direction == SortDirection.Asc
                ? source.OrderBy(a => a.EffectiveLastSeenAt)
                : source.OrderByDescending(a => a.EffectiveLastSeenAt),
        };

        return ordered.ThenBy(a => a.AlertId ?? string.Empty, StringComparer.Ordinal);
    }

    private static bool AfterCursor(
        IngestAlert alert,
        AlertListCursor.CursorState cursor,
        AlertListQuery query)
    {
        if (query.UseDefaultPrioritySort
            && query.SortBy == AlertSortColumn.LastSeenAt
            && query.Direction == SortDirection.Desc)
        {
            var activeCmp = (alert.IsActive ? 1 : 0).CompareTo(cursor.IsActive == true ? 1 : 0);
            if (activeCmp != 0)
            {
                return activeCmp < 0;
            }

            var sevCmp = IngestAlertCatalog.SeverityRank(alert.Severity)
                .CompareTo(cursor.SeverityRank ?? 0);
            if (sevCmp != 0)
            {
                return sevCmp > 0;
            }

            var timeCmp = alert.EffectiveLastSeenAt.CompareTo(cursor.Time ?? DateTimeOffset.MinValue);
            if (timeCmp != 0)
            {
                return timeCmp < 0;
            }

            return string.CompareOrdinal(alert.AlertId ?? string.Empty, cursor.AlertId) > 0;
        }

        var idCmp = string.CompareOrdinal(alert.AlertId ?? string.Empty, cursor.AlertId);
        return query.SortBy switch
        {
            AlertSortColumn.FirstSeenAt => AfterScalar(
                alert.EffectiveFirstSeenAt.CompareTo(cursor.Time ?? DateTimeOffset.MinValue),
                idCmp,
                query.Direction),
            AlertSortColumn.Code => AfterScalar(
                string.Compare(alert.Code, cursor.Code ?? string.Empty, StringComparison.Ordinal),
                idCmp,
                query.Direction),
            AlertSortColumn.Severity => AfterScalar(
                IngestAlertCatalog.SeverityRank(alert.Severity)
                    .CompareTo(IngestAlertCatalog.SeverityRank(cursor.Severity)),
                idCmp,
                query.Direction),
            AlertSortColumn.AlertId => query.Direction == SortDirection.Asc
                ? idCmp > 0
                : idCmp < 0,
            AlertSortColumn.TaskType => AfterScalar(
                string.Compare(alert.TaskType, cursor.TaskType, StringComparison.Ordinal),
                idCmp,
                query.Direction),
            AlertSortColumn.Sublot => AfterScalar(
                string.Compare(alert.Sublot, cursor.Sublot, StringComparison.Ordinal),
                idCmp,
                query.Direction),
            AlertSortColumn.DemandId => AfterScalar(
                string.Compare(alert.DemandId, cursor.DemandId, StringComparison.Ordinal),
                idCmp,
                query.Direction),
            AlertSortColumn.Message => AfterScalar(
                string.Compare(alert.Message, cursor.Message, StringComparison.Ordinal),
                idCmp,
                query.Direction),
            _ => AfterScalar(
                alert.EffectiveLastSeenAt.CompareTo(cursor.Time ?? DateTimeOffset.MinValue),
                idCmp,
                query.Direction),
        };
    }

    private static bool AfterScalar(int primaryCmp, int idCmp, SortDirection direction)
    {
        if (primaryCmp != 0)
        {
            return direction == SortDirection.Asc ? primaryCmp > 0 : primaryCmp < 0;
        }

        return idCmp > 0;
    }
}
