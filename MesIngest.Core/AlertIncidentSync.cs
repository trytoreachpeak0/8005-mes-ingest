using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MesIngest.Core;

public static class IngestAlertCatalog
{
    public static readonly IReadOnlySet<string> PollCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            AlertCodes.PollFailure,
            AlertCodes.PollIncomplete,
        };

    public static readonly IReadOnlySet<string> ReconcileCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            AlertCodes.DuplicateReconcileKey,
            AlertCodes.PausedZeroDrop,
            AlertCodes.FieldDrift,
            AlertCodes.ReappearAfterGone,
        };

    public static readonly IReadOnlySet<string> SuccessRoundManagedCodes =
        new HashSet<string>(PollCodes.Concat(ReconcileCodes), StringComparer.Ordinal);

    public static string SeverityFor(string code) =>
        code switch
        {
            AlertCodes.ReappearAfterGone => AlertSeverities.Warning,
            _ => AlertSeverities.Error,
        };

    public static int SeverityRank(string? severity) =>
        string.Equals(severity, AlertSeverities.Error, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
}

public static class AlertDetailsFingerprint
{
    public static string Compute(string? detailsJson)
    {
        var payload = detailsJson ?? string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public static class AlertMessageSanitizer
{
    private static readonly Regex SecretLike = new(
        @"(?i)(password|pwd|secret|authorization|bearer|connection\s*string)\s*[=:]\s*\S+",
        RegexOptions.Compiled);

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        return SecretLike.Replace(message, "$1=***");
    }
}

/// <summary>
/// Applies round observations to existing IngestAlert incidents (upsert / fingerprint split / resolve / retention).
/// </summary>
public static class AlertIncidentSync
{
    public static readonly TimeSpan DefaultResolvedRetention = TimeSpan.FromDays(365);

    public static IReadOnlyList<IngestAlert> Apply(
        IReadOnlyList<IngestAlert> existing,
        IReadOnlyList<IngestAlert> observations,
        DateTimeOffset asOf,
        IReadOnlySet<string> managedCodes,
        TimeSpan? resolvedRetention = null,
        Func<string>? alertIdAllocator = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(managedCodes);

        var idAllocator = alertIdAllocator ?? (() => Guid.NewGuid().ToString("N"));
        var retention = resolvedRetention ?? DefaultResolvedRetention;
        var next = existing
            .Select(NormalizeExisting)
            .ToList();

        var matchedActiveIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in observations)
        {
            var observation = NormalizeObservation(raw, asOf);
            if (!managedCodes.Contains(observation.Code))
            {
                continue;
            }

            var identity = IdentityKey(observation);
            var fingerprint = observation.DetailsFingerprint
                ?? AlertDetailsFingerprint.Compute(observation.Details);

            var activeSameIdentity = next
                .Where(a => a.IsActive && IdentityKey(a) == identity)
                .ToList();

            var sameFingerprint = activeSameIdentity
                .FirstOrDefault(a => string.Equals(
                    a.DetailsFingerprint,
                    fingerprint,
                    StringComparison.Ordinal));

            if (sameFingerprint is not null)
            {
                var idx = next.FindIndex(a =>
                    string.Equals(a.AlertId, sameFingerprint.AlertId, StringComparison.Ordinal));
                next[idx] = sameFingerprint with
                {
                    LastSeenAt = asOf,
                    OccurrenceCount = sameFingerprint.OccurrenceCount + 1,
                    Message = observation.Message ?? sameFingerprint.Message,
                    Details = observation.Details ?? sameFingerprint.Details,
                    CreatedAt = sameFingerprint.EffectiveFirstSeenAt,
                };
                matchedActiveIds.Add(sameFingerprint.AlertId!);
                continue;
            }

            foreach (var stale in activeSameIdentity)
            {
                var idx = next.FindIndex(a =>
                    string.Equals(a.AlertId, stale.AlertId, StringComparison.Ordinal));
                next[idx] = stale with
                {
                    IsActive = false,
                    ResolvedAt = asOf,
                };
            }

            var alertId = idAllocator();
            next.Add(observation with
            {
                AlertId = alertId,
                Severity = IngestAlertCatalog.SeverityFor(observation.Code),
                DetailsFingerprint = fingerprint,
                FirstSeenAt = asOf,
                LastSeenAt = asOf,
                OccurrenceCount = 1,
                IsActive = true,
                ResolvedAt = null,
                CreatedAt = asOf,
            });
            matchedActiveIds.Add(alertId);
        }

        for (var i = 0; i < next.Count; i++)
        {
            var alert = next[i];
            if (!alert.IsActive
                || !managedCodes.Contains(alert.Code)
                || matchedActiveIds.Contains(alert.AlertId!))
            {
                continue;
            }

            next[i] = alert with
            {
                IsActive = false,
                ResolvedAt = asOf,
            };
        }

        if (retention <= TimeSpan.Zero)
        {
            return next;
        }

        var cutoff = asOf - retention;
        return next
            .Where(a => a.IsActive || (a.ResolvedAt ?? a.EffectiveLastSeenAt) >= cutoff)
            .ToList();
    }

    public static string IdentityKey(IngestAlert alert)
    {
        var code = alert.Code;
        return code switch
        {
            AlertCodes.PollFailure or AlertCodes.PollIncomplete => code,
            AlertCodes.PausedZeroDrop => $"{code}|{alert.TaskType ?? string.Empty}",
            AlertCodes.DuplicateReconcileKey =>
                $"{code}|{alert.TaskType ?? string.Empty}|{alert.Sublot ?? string.Empty}",
            _ =>
                $"{code}|{alert.TaskType ?? string.Empty}|{alert.Sublot ?? string.Empty}|{alert.DemandId ?? string.Empty}",
        };
    }

    private static IngestAlert NormalizeExisting(IngestAlert alert)
    {
        var first = alert.FirstSeenAt ?? alert.CreatedAt ?? DateTimeOffset.UtcNow;
        var last = alert.LastSeenAt ?? first;
        return alert with
        {
            AlertId = string.IsNullOrWhiteSpace(alert.AlertId)
                ? Guid.NewGuid().ToString("N")
                : alert.AlertId,
            Severity = string.IsNullOrWhiteSpace(alert.Severity)
                ? IngestAlertCatalog.SeverityFor(alert.Code)
                : alert.Severity,
            DetailsFingerprint = alert.DetailsFingerprint
                ?? AlertDetailsFingerprint.Compute(alert.Details),
            FirstSeenAt = first,
            LastSeenAt = last,
            CreatedAt = first,
            OccurrenceCount = Math.Max(1, alert.OccurrenceCount),
        };
    }

    private static IngestAlert NormalizeObservation(IngestAlert alert, DateTimeOffset asOf)
    {
        var details = alert.Details;
        var fingerprint = alert.DetailsFingerprint ?? AlertDetailsFingerprint.Compute(details);
        return alert with
        {
            Severity = IngestAlertCatalog.SeverityFor(alert.Code),
            Details = details,
            DetailsFingerprint = fingerprint,
            Message = alert.Message,
            FirstSeenAt = asOf,
            LastSeenAt = asOf,
            CreatedAt = asOf,
            IsActive = true,
            ResolvedAt = null,
            OccurrenceCount = 1,
        };
    }
}

public static class AlertDetailsBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string FieldDrift(
        TransportDemand frozen,
        MesSnapshotRow observed)
    {
        var fields = new List<object>();
        AddIfDifferent(fields, "Area", frozen.Area, observed.Area);
        AddIfDifferent(fields, "Eqp", frozen.Eqp, observed.Eqp);
        AddIfDifferent(fields, "Step", frozen.Step, observed.Step);
        if (frozen.Dates != observed.Dates)
        {
            fields.Add(new
            {
                field = "Dates",
                frozen = frozen.Dates,
                observed = observed.Dates,
            });
        }

        AddIfDifferent(fields, "Package", frozen.Package, observed.Package);
        return JsonSerializer.Serialize(new { fields }, JsonOptions);
    }

    public static string Duplicate(int duplicateCount, IEnumerable<MesSnapshotRow> rows) =>
        JsonSerializer.Serialize(
            new
            {
                duplicateCount,
                conflictingRows = rows.Select(r => new
                {
                    taskType = r.TaskType,
                    sublot = r.Sublot,
                    area = r.Area,
                    eqp = r.Eqp,
                    step = r.Step,
                    dates = r.Dates,
                    package = r.Package,
                }).ToList(),
            },
            JsonOptions);

    public static string Poll(
        string? failureStage,
        double? durationMs,
        int rowCount,
        string? reason,
        int? timeoutSeconds = null)
    {
        return JsonSerializer.Serialize(
            new
            {
                failureStage,
                timeoutSeconds,
                durationMs = durationMs is null ? null : (long?)Math.Round(durationMs.Value),
                rowCount,
                reason = AlertMessageSanitizer.Sanitize(reason),
            },
            JsonOptions);
    }

    /// <summary>
    /// Stable fingerprint payload for POLL_* — excludes per-round durationMs.
    /// </summary>
    public static string PollFingerprint(string? failureStage, string? reason, int? timeoutSeconds = null) =>
        AlertDetailsFingerprint.Compute(
            JsonSerializer.Serialize(
                new
                {
                    failureStage,
                    timeoutSeconds,
                    reason = AlertMessageSanitizer.Sanitize(reason),
                },
                JsonOptions));

    public static string Reappear(string? previousDemandId, string newDemandId) =>
        JsonSerializer.Serialize(
            new { previousDemandId, newDemandId },
            JsonOptions);

    public static string PausedZeroDrop(
        int lastHealthyNonZeroCount,
        int recoveryStreak,
        int enterThreshold,
        int clearStreakRequired) =>
        JsonSerializer.Serialize(
            new
            {
                lastHealthyNonZeroCount,
                recoveryStreak,
                enterThreshold,
                clearStreakRequired,
            },
            JsonOptions);

    /// <summary>
    /// Stable fingerprint for PAUSED_ZERO_DROP — excludes recoveryStreak so the same
    /// incident continues through recovery rounds until the pause clears.
    /// </summary>
    public static string PausedZeroDropFingerprint(
        int lastHealthyNonZeroCount,
        int enterThreshold,
        int clearStreakRequired) =>
        AlertDetailsFingerprint.Compute(
            JsonSerializer.Serialize(
                new
                {
                    lastHealthyNonZeroCount,
                    enterThreshold,
                    clearStreakRequired,
                },
                JsonOptions));

    private static void AddIfDifferent(
        List<object> fields,
        string field,
        string? frozen,
        string? observed)
    {
        if (string.Equals(frozen, observed, StringComparison.Ordinal))
        {
            return;
        }

        fields.Add(new { field, frozen, observed });
    }
}
