using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesIngest.Core;

public enum DemandSortColumn
{
    Dates,
    DemandId,
    GoneAt,
    TaskType,
    Sublot,
    CreatedAt,
    MesLastSeenAt,
    Status,
    Area,
    Eqp,
    Step,
    Package,
    LocationRisk,
    DisappearCount,
}

public enum SortDirection
{
    Asc,
    Desc,
}

public sealed record DemandIdMatch(string Value, bool IsPrefix);

public sealed class DemandListQuery
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 200;
    public static readonly TimeSpan DefaultGoneWindow = TimeSpan.FromHours(24);

    public DemandStatus Status { get; init; } = DemandStatus.Visible;
    public string? TaskType { get; init; }
    public string? Sublot { get; init; }
    public DemandIdMatch? DemandId { get; init; }
    public DateTimeOffset? DatesFrom { get; init; }
    public DateTimeOffset? DatesTo { get; init; }
    public DateTimeOffset? GoneAtFrom { get; init; }
    public DateTimeOffset? GoneAtTo { get; init; }
    public DemandSortColumn SortBy { get; init; } = DemandSortColumn.Dates;
    public SortDirection Direction { get; init; } = SortDirection.Desc;
    public int Limit { get; init; } = DefaultLimit;
    public string? Cursor { get; init; }
    public DateTimeOffset AsOf { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EffectiveGoneAtFrom =>
        Status == DemandStatus.Gone
            ? GoneAtFrom ?? AsOf - DefaultGoneWindow
            : null;

    public DateTimeOffset? EffectiveGoneAtTo =>
        Status == DemandStatus.Gone ? GoneAtTo : null;
}

public sealed record DemandListPage(
    IReadOnlyList<TransportDemand> Items,
    string? NextCursor,
    bool HasMore);

public static class DemandListQueryParser
{
    public static bool TryParseStatus(string? raw, out DemandStatus status, out string? error)
    {
        status = DemandStatus.Visible;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (raw.Equals("VISIBLE", StringComparison.OrdinalIgnoreCase))
        {
            status = DemandStatus.Visible;
            return true;
        }

        if (raw.Equals("GONE", StringComparison.OrdinalIgnoreCase))
        {
            status = DemandStatus.Gone;
            return true;
        }

        error = "status must be VISIBLE or GONE";
        return false;
    }

    public static bool TryParseSortBy(string? raw, out DemandSortColumn sortBy, out string? error)
    {
        sortBy = DemandSortColumn.Dates;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "dates":
                sortBy = DemandSortColumn.Dates;
                return true;
            case "demandid":
                sortBy = DemandSortColumn.DemandId;
                return true;
            case "goneat":
                sortBy = DemandSortColumn.GoneAt;
                return true;
            case "tasktype":
                sortBy = DemandSortColumn.TaskType;
                return true;
            case "sublot":
                sortBy = DemandSortColumn.Sublot;
                return true;
            case "createdat":
                sortBy = DemandSortColumn.CreatedAt;
                return true;
            case "meslastseenat":
                sortBy = DemandSortColumn.MesLastSeenAt;
                return true;
            case "status":
                sortBy = DemandSortColumn.Status;
                return true;
            case "area":
                sortBy = DemandSortColumn.Area;
                return true;
            case "eqp":
                sortBy = DemandSortColumn.Eqp;
                return true;
            case "step":
                sortBy = DemandSortColumn.Step;
                return true;
            case "package":
                sortBy = DemandSortColumn.Package;
                return true;
            case "locationrisk":
                sortBy = DemandSortColumn.LocationRisk;
                return true;
            case "disappearcount":
                sortBy = DemandSortColumn.DisappearCount;
                return true;
            default:
                error = "sortBy is not in the allow-list";
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

    public static bool TryParseLimit(int? raw, out int limit, out string? error)
    {
        limit = DemandListQuery.DefaultLimit;
        error = null;
        if (raw is null)
        {
            return true;
        }

        if (raw.Value < 1 || raw.Value > DemandListQuery.MaxLimit)
        {
            error = $"limit must be between 1 and {DemandListQuery.MaxLimit}";
            return false;
        }

        limit = raw.Value;
        return true;
    }

    public static bool TryParseDemandId(string? raw, out DemandIdMatch? match, out string? error)
    {
        match = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var value = raw.Trim().ToLowerInvariant();
        if (value.Length > 64)
        {
            error = "demandId is too long";
            return false;
        }

        if (!IsHex(value))
        {
            error = "demandId must be a full lowercase hex id or at least 6 hex prefix characters";
            return false;
        }

        if (value.Length < 6)
        {
            error = "demandId hex prefix must be at least 6 characters";
            return false;
        }

        if (value.Length > 32)
        {
            error = "demandId must be at most 32 hex characters";
            return false;
        }

        match = new DemandIdMatch(value, IsPrefix: value.Length < 32);
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

        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            value = parsed;
            return true;
        }

        error = "invalid DateTimeOffset";
        return false;
    }

    private static bool IsHex(string value)
    {
        foreach (var ch in value)
        {
            var isHex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}

public static class DemandListCursor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Encode(DemandSortColumn sortBy, SortDirection direction, TransportDemand last)
    {
        var payload = new CursorPayload
        {
            Version = 1,
            SortBy = ToSortToken(sortBy),
            Direction = direction == SortDirection.Asc ? "asc" : "desc",
            DemandId = last.DemandId,
            Dates = sortBy == DemandSortColumn.Dates ? last.Dates : null,
            GoneAt = sortBy == DemandSortColumn.GoneAt ? last.GoneAt : null,
            CreatedAt = sortBy == DemandSortColumn.CreatedAt ? last.CreatedAt : null,
            MesLastSeenAt = sortBy == DemandSortColumn.MesLastSeenAt ? last.MesLastSeenAt : null,
            TaskType = sortBy == DemandSortColumn.TaskType ? last.TaskType : null,
            Sublot = sortBy == DemandSortColumn.Sublot ? last.Sublot : null,
            Status = sortBy == DemandSortColumn.Status ? StatusSortValue(last.Status) : null,
            Area = sortBy == DemandSortColumn.Area ? last.Area : null,
            Eqp = sortBy == DemandSortColumn.Eqp ? last.Eqp : null,
            Step = sortBy == DemandSortColumn.Step ? last.Step : null,
            Package = sortBy == DemandSortColumn.Package ? last.Package : null,
            LocationRisk = sortBy == DemandSortColumn.LocationRisk ? last.LocationRisk : null,
            DisappearCount = sortBy == DemandSortColumn.DisappearCount ? last.DisappearCount : null,
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    public static bool TryDecode(
        string? cursor,
        DemandSortColumn sortBy,
        SortDirection direction,
        out CursorPayload payload,
        out string? error)
    {
        payload = default!;
        error = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(cursor));
            var decoded = JsonSerializer.Deserialize<CursorPayload>(json, JsonOptions);
            if (decoded is null || decoded.Version != 1)
            {
                error = "cursor is invalid";
                return false;
            }

            if (!string.Equals(decoded.SortBy, ToSortToken(sortBy), StringComparison.Ordinal)
                || !string.Equals(
                    decoded.Direction,
                    direction == SortDirection.Asc ? "asc" : "desc",
                    StringComparison.Ordinal))
            {
                error = "cursor does not match sortBy/direction";
                return false;
            }

            if (string.IsNullOrWhiteSpace(decoded.DemandId))
            {
                error = "cursor is invalid";
                return false;
            }

            payload = decoded;
            return true;
        }
        catch (Exception)
        {
            error = "cursor is invalid";
            return false;
        }
    }

    public static string ToSortToken(DemandSortColumn sortBy) =>
        sortBy switch
        {
            DemandSortColumn.Dates => "dates",
            DemandSortColumn.DemandId => "demandId",
            DemandSortColumn.GoneAt => "goneAt",
            DemandSortColumn.TaskType => "taskType",
            DemandSortColumn.Sublot => "sublot",
            DemandSortColumn.CreatedAt => "createdAt",
            DemandSortColumn.MesLastSeenAt => "mesLastSeenAt",
            DemandSortColumn.Status => "status",
            DemandSortColumn.Area => "area",
            DemandSortColumn.Eqp => "eqp",
            DemandSortColumn.Step => "step",
            DemandSortColumn.Package => "package",
            DemandSortColumn.LocationRisk => "locationRisk",
            DemandSortColumn.DisappearCount => "disappearCount",
            _ => "dates",
        };

    public static string StatusSortValue(DemandStatus status) =>
        status == DemandStatus.Gone ? "GONE" : "VISIBLE";

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }

    public sealed record CursorPayload
    {
        public int Version { get; init; }
        public string SortBy { get; init; } = "";
        public string Direction { get; init; } = "";
        public string DemandId { get; init; } = "";
        public DateTimeOffset? Dates { get; init; }
        public DateTimeOffset? GoneAt { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? MesLastSeenAt { get; init; }
        public string? TaskType { get; init; }
        public string? Sublot { get; init; }
        public string? Status { get; init; }
        public string? Area { get; init; }
        public string? Eqp { get; init; }
        public string? Step { get; init; }
        public string? Package { get; init; }
        public bool? LocationRisk { get; init; }
        public int? DisappearCount { get; init; }
    }
}

public static class DemandListPaging
{
    public static DemandListPage Page(
        IEnumerable<TransportDemand> source,
        DemandListQuery query,
        DemandListCursor.CursorPayload? cursor)
    {
        var filtered = ApplyFilters(source, query);
        IEnumerable<TransportDemand> ordered = ApplySort(filtered, query.SortBy, query.Direction);
        if (cursor is not null)
        {
            ordered = ordered.Where(d => IsAfterCursor(d, query.SortBy, query.Direction, cursor));
        }

        var taken = ordered.Take(query.Limit + 1).ToList();
        var hasMore = taken.Count > query.Limit;
        if (hasMore)
        {
            taken.RemoveAt(taken.Count - 1);
        }

        string? nextCursor = null;
        if (hasMore && taken.Count > 0)
        {
            nextCursor = DemandListCursor.Encode(query.SortBy, query.Direction, taken[^1]);
        }

        return new DemandListPage(taken, nextCursor, hasMore);
    }

    public static IEnumerable<TransportDemand> ApplyFilters(
        IEnumerable<TransportDemand> source,
        DemandListQuery query)
    {
        IEnumerable<TransportDemand> q = source.Where(d => d.Status == query.Status);

        if (!string.IsNullOrWhiteSpace(query.TaskType))
        {
            q = q.Where(d => string.Equals(d.TaskType, query.TaskType, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(query.Sublot))
        {
            q = q.Where(d => string.Equals(d.Sublot, query.Sublot, StringComparison.Ordinal));
        }

        if (query.DemandId is { } id)
        {
            q = id.IsPrefix
                ? q.Where(d => d.DemandId.StartsWith(id.Value, StringComparison.Ordinal))
                : q.Where(d => string.Equals(d.DemandId, id.Value, StringComparison.Ordinal));
        }

        if (query.DatesFrom is { } datesFrom)
        {
            q = q.Where(d => d.Dates >= datesFrom);
        }

        if (query.DatesTo is { } datesTo)
        {
            q = q.Where(d => d.Dates <= datesTo);
        }

        if (query.EffectiveGoneAtFrom is { } goneFrom)
        {
            q = q.Where(d => d.GoneAt is { } goneAt && goneAt >= goneFrom);
        }

        if (query.EffectiveGoneAtTo is { } goneTo)
        {
            q = q.Where(d => d.GoneAt is { } goneAt && goneAt <= goneTo);
        }

        return q;
    }

    public static IOrderedEnumerable<TransportDemand> ApplySort(
        IEnumerable<TransportDemand> source,
        DemandSortColumn sortBy,
        SortDirection direction)
    {
        return sortBy switch
        {
            DemandSortColumn.DemandId => direction == SortDirection.Asc
                ? source.OrderBy(d => d.DemandId, StringComparer.Ordinal)
                : source.OrderByDescending(d => d.DemandId, StringComparer.Ordinal),
            DemandSortColumn.GoneAt => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.GoneAt ?? DateTimeOffset.MinValue)
                    : source.OrderByDescending(d => d.GoneAt ?? DateTimeOffset.MinValue),
                direction),
            DemandSortColumn.CreatedAt => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.CreatedAt)
                    : source.OrderByDescending(d => d.CreatedAt),
                direction),
            DemandSortColumn.MesLastSeenAt => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.MesLastSeenAt)
                    : source.OrderByDescending(d => d.MesLastSeenAt),
                direction),
            DemandSortColumn.TaskType => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.TaskType, StringComparer.Ordinal)
                    : source.OrderByDescending(d => d.TaskType, StringComparer.Ordinal),
                direction),
            DemandSortColumn.Sublot => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.Sublot, StringComparer.Ordinal)
                    : source.OrderByDescending(d => d.Sublot, StringComparer.Ordinal),
                direction),
            DemandSortColumn.Status => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => DemandListCursor.StatusSortValue(d.Status), StringComparer.Ordinal)
                    : source.OrderByDescending(d => DemandListCursor.StatusSortValue(d.Status), StringComparer.Ordinal),
                direction),
            DemandSortColumn.Area => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.Area, StringComparer.Ordinal)
                    : source.OrderByDescending(d => d.Area, StringComparer.Ordinal),
                direction),
            DemandSortColumn.Eqp => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.Eqp, StringComparer.Ordinal)
                    : source.OrderByDescending(d => d.Eqp, StringComparer.Ordinal),
                direction),
            DemandSortColumn.Step => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.Step, StringComparer.Ordinal)
                    : source.OrderByDescending(d => d.Step, StringComparer.Ordinal),
                direction),
            DemandSortColumn.Package => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.Package, StringComparer.Ordinal)
                    : source.OrderByDescending(d => d.Package, StringComparer.Ordinal),
                direction),
            DemandSortColumn.LocationRisk => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.LocationRisk)
                    : source.OrderByDescending(d => d.LocationRisk),
                direction),
            DemandSortColumn.DisappearCount => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.DisappearCount)
                    : source.OrderByDescending(d => d.DisappearCount),
                direction),
            _ => ThenByDemandId(
                direction == SortDirection.Asc
                    ? source.OrderBy(d => d.Dates)
                    : source.OrderByDescending(d => d.Dates),
                direction),
        };
    }

    private static IOrderedEnumerable<TransportDemand> ThenByDemandId(
        IOrderedEnumerable<TransportDemand> ordered,
        SortDirection _) =>
        ordered.ThenBy(d => d.DemandId, StringComparer.Ordinal);

    public static bool IsAfterCursor(
        TransportDemand demand,
        DemandSortColumn sortBy,
        SortDirection direction,
        DemandListCursor.CursorPayload cursor)
    {
        // DemandId is always the ascending tie-break (see ApplySort / SQL ORDER BY).
        // Desc flips only the primary column — never the DemandId secondary key.
        if (sortBy == DemandSortColumn.DemandId)
        {
            var idCmp = string.Compare(demand.DemandId, cursor.DemandId, StringComparison.Ordinal);
            return direction == SortDirection.Asc ? idCmp > 0 : idCmp < 0;
        }

        var primary = sortBy switch
        {
            DemandSortColumn.GoneAt => (demand.GoneAt ?? DateTimeOffset.MinValue)
                .CompareTo(cursor.GoneAt ?? DateTimeOffset.MinValue),
            DemandSortColumn.CreatedAt => demand.CreatedAt.CompareTo(cursor.CreatedAt ?? default),
            DemandSortColumn.MesLastSeenAt => demand.MesLastSeenAt.CompareTo(cursor.MesLastSeenAt ?? default),
            DemandSortColumn.TaskType => string.Compare(demand.TaskType, cursor.TaskType ?? "", StringComparison.Ordinal),
            DemandSortColumn.Sublot => string.Compare(demand.Sublot, cursor.Sublot ?? "", StringComparison.Ordinal),
            DemandSortColumn.Status => string.Compare(
                DemandListCursor.StatusSortValue(demand.Status),
                cursor.Status ?? "",
                StringComparison.Ordinal),
            DemandSortColumn.Area => string.Compare(demand.Area, cursor.Area, StringComparison.Ordinal),
            DemandSortColumn.Eqp => string.Compare(demand.Eqp, cursor.Eqp, StringComparison.Ordinal),
            DemandSortColumn.Step => string.Compare(demand.Step, cursor.Step, StringComparison.Ordinal),
            DemandSortColumn.Package => string.Compare(demand.Package, cursor.Package, StringComparison.Ordinal),
            DemandSortColumn.LocationRisk => demand.LocationRisk.CompareTo(cursor.LocationRisk ?? false),
            DemandSortColumn.DisappearCount => demand.DisappearCount.CompareTo(cursor.DisappearCount ?? 0),
            _ => demand.Dates.CompareTo(cursor.Dates ?? default),
        };

        if (primary != 0)
        {
            return direction == SortDirection.Asc ? primary > 0 : primary < 0;
        }

        return string.Compare(demand.DemandId, cursor.DemandId, StringComparison.Ordinal) > 0;
    }
}
