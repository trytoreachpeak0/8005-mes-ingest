using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MesIngest.Watch;

internal enum AlertDetailsProjectionKind
{
    Empty,
    FieldDrift,
    KeyValue,
    Legacy,
}

internal sealed record AlertFieldDriftRow(string Field, string? Frozen, string? Observed);

internal sealed record AlertDetailKeyValue(string Key, string Value);

/// <summary>
/// Projects IngestAlert Details JSON into structured rows for the Watch detail UI.
/// </summary>
internal sealed record AlertDetailsProjection(
    AlertDetailsProjectionKind Kind,
    IReadOnlyList<AlertFieldDriftRow> FieldRows,
    IReadOnlyList<AlertDetailKeyValue> KeyValues,
    string? LegacySummary)
{
    public static AlertDetailsProjection Empty { get; } = new(
        AlertDetailsProjectionKind.Empty,
        [],
        [],
        null);

    public static AlertDetailsProjection From(string code, string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return Empty;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(detailsJson);
        }
        catch (JsonException)
        {
            return new AlertDetailsProjection(
                AlertDetailsProjectionKind.Legacy,
                [],
                [],
                detailsJson);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new AlertDetailsProjection(
                    AlertDetailsProjectionKind.Legacy,
                    [],
                    [],
                    detailsJson);
            }

            if (string.Equals(code, "FIELD_DRIFT", StringComparison.Ordinal))
            {
                return ProjectFieldDrift(doc.RootElement, detailsJson);
            }

            return ProjectKeyValues(code, doc.RootElement);
        }
    }

    private static AlertDetailsProjection ProjectFieldDrift(JsonElement root, string raw)
    {
        if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return new AlertDetailsProjection(
                AlertDetailsProjectionKind.Legacy,
                [],
                [],
                raw);
        }

        var rows = new List<AlertFieldDriftRow>();
        foreach (var item in fields.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            rows.Add(new AlertFieldDriftRow(
                ReadString(item, "field") ?? string.Empty,
                ReadString(item, "frozen"),
                ReadString(item, "observed")));
        }

        return new AlertDetailsProjection(
            AlertDetailsProjectionKind.FieldDrift,
            rows,
            [],
            null);
    }

    private static AlertDetailsProjection ProjectKeyValues(string code, JsonElement root)
    {
        var pairs = new List<AlertDetailKeyValue>();

        if (string.Equals(code, "DUPLICATE_RECONCILE_KEY", StringComparison.Ordinal))
        {
            if (root.TryGetProperty("duplicateCount", out var count))
            {
                pairs.Add(new AlertDetailKeyValue("duplicateCount", FormatElement(count)));
            }

            if (root.TryGetProperty("conflictingRows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var row in rows.EnumerateArray())
                {
                    pairs.Add(new AlertDetailKeyValue(
                        $"conflictingRows[{index}]",
                        SummarizeObject(row)));
                    index++;
                }
            }

            return new AlertDetailsProjection(AlertDetailsProjectionKind.KeyValue, [], pairs, null);
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array
                || property.Value.ValueKind == JsonValueKind.Object)
            {
                pairs.Add(new AlertDetailKeyValue(property.Name, SummarizeObject(property.Value)));
                continue;
            }

            pairs.Add(new AlertDetailKeyValue(property.Name, FormatElement(property.Value)));
        }

        return new AlertDetailsProjection(AlertDetailsProjectionKind.KeyValue, [], pairs, null);
    }

    private static string SummarizeObject(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return $"[{element.GetArrayLength()} items]";
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return FormatElement(element);
        }

        var parts = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            parts.Add($"{property.Name}={FormatElement(property.Value)}");
        }

        return string.Join(", ", parts);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return FormatElement(value);
    }

    private static string FormatElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.GetRawText(),
        };
}

internal enum UnifiedEventSource
{
    HostIngestAlert,
    WatchConnectionEvent,
}

internal enum UnifiedEventSourceFilter
{
    All,
    HostIngestAlert,
    WatchConnectionEvent,
}

/// <summary>
/// View-model for a non-modal Host IngestAlert detail window.
/// </summary>
internal enum AlertDemandTargetKind
{
    Generic,
    PreviousGone,
    NewVisible,
}

internal sealed record AlertDemandTarget(string DemandId, AlertDemandTargetKind Kind);

internal sealed record ReappearDemandTargets(
    AlertDemandTarget? Previous,
    AlertDemandTarget? New)
{
    public static ReappearDemandTargets Empty { get; } = new(null, null);

    public bool HasAny => Previous is not null || New is not null;
}

internal sealed record AlertDemandComparisonRow(
    string Field,
    string PreviousValue,
    string CurrentValue,
    bool IsDifferent);

/// <summary>
/// Projects complete related TransportDemand snapshots into stable, side-by-side rows.
/// Rows are never filtered by difference so operators retain the full record context.
/// </summary>
internal static class AlertDemandComparisonProjection
{
    public static IReadOnlyList<AlertDemandComparisonRow> Compare(
        WatchDemandDto? previous,
        WatchDemandDto? current,
        TimeZoneInfo? timeZone = null) =>
        Project(previous, current, compareValues: true, timeZone ?? TimeZoneInfo.Local);

    public static IReadOnlyList<AlertDemandComparisonRow> Single(
        WatchDemandDto current,
        TimeZoneInfo? timeZone = null) =>
        Project(null, current, compareValues: false, timeZone ?? TimeZoneInfo.Local);

    private static IReadOnlyList<AlertDemandComparisonRow> Project(
        WatchDemandDto? previous,
        WatchDemandDto? current,
        bool compareValues,
        TimeZoneInfo timeZone)
    {
        var values = new (string Field, string Previous, string Current)[]
        {
            ("DemandId", Text(previous?.DemandId), Text(current?.DemandId)),
            ("TASK_TYPE", Text(previous?.TaskType), Text(current?.TaskType)),
            ("SUBLOT", Text(previous?.Sublot), Text(current?.Sublot)),
            ("AREA", Text(previous?.Area), Text(current?.Area)),
            ("EQP", Text(previous?.Eqp), Text(current?.Eqp)),
            ("STEP", Text(previous?.Step), Text(current?.Step)),
            ("DATES", Time(previous?.Dates, timeZone), Time(current?.Dates, timeZone)),
            ("PACKAGE", Text(previous?.Package), Text(current?.Package)),
            ("Status", Text(previous?.Status), Text(current?.Status)),
            ("MesLastSeenAt", Time(previous?.MesLastSeenAt, timeZone), Time(current?.MesLastSeenAt, timeZone)),
            ("DisappearCount", Number(previous?.DisappearCount), Number(current?.DisappearCount)),
            ("LocationRisk", Boolean(previous?.LocationRisk), Boolean(current?.LocationRisk)),
            ("LocationRiskCode", Text(previous?.LocationRiskCode), Text(current?.LocationRiskCode)),
            ("CreatedAt", Time(previous?.CreatedAt, timeZone), Time(current?.CreatedAt, timeZone)),
            ("GoneAt", Time(previous?.GoneAt, timeZone), Time(current?.GoneAt, timeZone)),
        };

        return values
            .Select(value => new AlertDemandComparisonRow(
                value.Field,
                value.Previous,
                value.Current,
                compareValues && !string.Equals(value.Previous, value.Current, StringComparison.Ordinal)))
            .ToList();
    }

    private static string Text(string? value) => value ?? "null";

    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string Boolean(bool? value) =>
        value is null ? "null" : value.Value ? "true" : "false";

    private static string Time(DateTimeOffset? value, TimeZoneInfo timeZone) =>
        value is null ? "null" : WatchTimeDisplay.Format(value.Value, timeZone);
}

internal sealed record AlertDetailViewModel(
    string? AlertId,
    string Code,
    string? Severity,
    UnifiedEventSource Source,
    bool IsActive,
    string LifecycleLabel,
    string? TaskType,
    string? Sublot,
    string? DemandId,
    ReappearDemandTargets ReappearTargets,
    string? Message,
    string CreatedAtText,
    string FirstSeenAtText,
    string LastSeenAtText,
    string ResolvedAtText,
    int OccurrenceCount,
    string DetailsJson,
    AlertDetailsProjection Projection,
    bool IsHistoricalSnapshot,
    string SnapshotStatusText)
{
    public string SourceLabel => Source switch
    {
        UnifiedEventSource.HostIngestAlert => "Host IngestAlert",
        UnifiedEventSource.WatchConnectionEvent => "Watch Connection Event",
        _ => Source.ToString(),
    };

    public bool HasReappearDemandTargets => ReappearTargets.HasAny;

    public static AlertDetailViewModel From(WatchAlertDto alert, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var projection = AlertDetailsProjection.From(alert.Code, alert.Details);
        var reappearTargets = ResolveReappearDemandTargets(alert, projection);
        return new AlertDetailViewModel(
            AlertId: alert.AlertId,
            Code: alert.Code,
            Severity: alert.Severity,
            Source: UnifiedEventSource.HostIngestAlert,
            IsActive: alert.IsActive,
            LifecycleLabel: alert.IsActive ? "active" : "resolved",
            TaskType: alert.TaskType,
            Sublot: alert.Sublot,
            DemandId: alert.DemandId,
            ReappearTargets: reappearTargets,
            Message: alert.Message,
            CreatedAtText: WatchTimeDisplay.FormatNullable(alert.CreatedAt, zone),
            FirstSeenAtText: WatchTimeDisplay.FormatNullable(alert.FirstSeenAt ?? alert.CreatedAt, zone),
            LastSeenAtText: WatchTimeDisplay.FormatNullable(alert.LastSeenAt ?? alert.CreatedAt, zone),
            ResolvedAtText: WatchTimeDisplay.FormatNullable(alert.ResolvedAt, zone),
            OccurrenceCount: alert.OccurrenceCount,
            DetailsJson: alert.Details ?? string.Empty,
            Projection: projection,
            IsHistoricalSnapshot: false,
            SnapshotStatusText: "live");
    }

    private static ReappearDemandTargets ResolveReappearDemandTargets(
        WatchAlertDto alert,
        AlertDetailsProjection projection)
    {
        if (!string.Equals(alert.Code, "REAPPEAR_AFTER_GONE", StringComparison.Ordinal))
        {
            return ReappearDemandTargets.Empty;
        }

        var previousDemandId = ReadDemandId(projection, "previousDemandId");
        var newDemandId = ReadDemandId(projection, "newDemandId") ?? NormalizeDemandId(alert.DemandId);
        return new ReappearDemandTargets(
            Previous: previousDemandId is null
                ? null
                : new AlertDemandTarget(previousDemandId, AlertDemandTargetKind.PreviousGone),
            New: newDemandId is null
                ? null
                : new AlertDemandTarget(newDemandId, AlertDemandTargetKind.NewVisible));
    }

    private static string? ReadDemandId(AlertDetailsProjection projection, string key) =>
        NormalizeDemandId(projection.KeyValues.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.Ordinal))?.Value);

    private static string? NormalizeDemandId(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();

    public AlertDetailViewModel MarkHistorical() =>
        this with
        {
            IsHistoricalSnapshot = true,
            SnapshotStatusText = "historical snapshot (not in current page)",
        };

    public AlertDetailViewModel ApplyUpdate(WatchAlertDto alert) =>
        From(alert) with
        {
            IsHistoricalSnapshot = false,
            SnapshotStatusText = "live",
        };

    public string BuildSummary()
    {
        var sb = new StringBuilder();
        sb.Append("AlertId=").Append(AlertId ?? "null").AppendLine();
        sb.Append("Code=").Append(Code).AppendLine();
        sb.Append("Severity=").Append(Severity ?? "null").AppendLine();
        sb.Append("Source=").Append(SourceLabel).AppendLine();
        sb.Append("Lifecycle=").Append(LifecycleLabel).AppendLine();
        sb.Append("CreatedAt=").Append(string.IsNullOrEmpty(CreatedAtText) ? "null" : CreatedAtText).AppendLine();
        sb.Append("FirstSeenAt=").Append(FirstSeenAtText).AppendLine();
        sb.Append("LastSeenAt=").Append(LastSeenAtText).AppendLine();
        sb.Append("ResolvedAt=").Append(string.IsNullOrEmpty(ResolvedAtText) ? "null" : ResolvedAtText).AppendLine();
        sb.Append("OccurrenceCount=").Append(OccurrenceCount).AppendLine();
        sb.Append("TASK_TYPE=").Append(TaskType ?? "null").AppendLine();
        sb.Append("SUBLOT=").Append(Sublot ?? "null").AppendLine();
        sb.Append("DemandId=").Append(DemandId ?? "null").AppendLine();
        sb.Append("Message=").Append(Message ?? "null").AppendLine();
        sb.Append("Snapshot=").Append(SnapshotStatusText);
        return sb.ToString();
    }
}

/// <summary>
/// One row in the unified Host Alert / Watch Connection Event viewer.
/// </summary>
internal sealed record UnifiedWatchEvent(
    UnifiedEventSource Source,
    string SourceLabel,
    DateTimeOffset At,
    string AtText,
    string CodeOrKind,
    string? SeverityOrStage,
    string? EndpointOrTaskType,
    string? DemandId,
    string? Message,
    WatchAlertDto? Alert,
    WatchConnectionEvent? ConnectionEvent)
{
    public static UnifiedWatchEvent FromAlert(WatchAlertDto alert, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var at = alert.LastSeenAt ?? alert.CreatedAt ?? DateTimeOffset.MinValue;
        return new UnifiedWatchEvent(
            Source: UnifiedEventSource.HostIngestAlert,
            SourceLabel: "Host IngestAlert",
            At: at,
            AtText: WatchTimeDisplay.Format(at, zone),
            CodeOrKind: alert.Code,
            SeverityOrStage: alert.Severity,
            EndpointOrTaskType: alert.TaskType,
            DemandId: alert.DemandId,
            Message: alert.Message,
            Alert: alert,
            ConnectionEvent: null);
    }

    public static UnifiedWatchEvent FromConnectionEvent(
        WatchConnectionEvent connectionEvent,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        return new UnifiedWatchEvent(
            Source: UnifiedEventSource.WatchConnectionEvent,
            SourceLabel: "Watch Connection Event",
            At: connectionEvent.At,
            AtText: WatchTimeDisplay.Format(connectionEvent.At, zone),
            CodeOrKind: connectionEvent.Kind.ToString(),
            SeverityOrStage: connectionEvent.Stage,
            EndpointOrTaskType: connectionEvent.Endpoint,
            DemandId: null,
            Message: connectionEvent.Message,
            Alert: null,
            ConnectionEvent: connectionEvent);
    }

    public static IReadOnlyList<UnifiedWatchEvent> Filter(
        IEnumerable<UnifiedWatchEvent> events,
        UnifiedEventSourceFilter filter) =>
        filter switch
        {
            UnifiedEventSourceFilter.HostIngestAlert => events
                .Where(e => e.Source == UnifiedEventSource.HostIngestAlert)
                .ToList(),
            UnifiedEventSourceFilter.WatchConnectionEvent => events
                .Where(e => e.Source == UnifiedEventSource.WatchConnectionEvent)
                .ToList(),
            _ => events.ToList(),
        };
}
