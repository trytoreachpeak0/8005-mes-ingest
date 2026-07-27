namespace MesIngest.Core;

public sealed class TransportDemandReconciler
{
    private readonly IDemandIdAllocator _demandIds;

    public TransportDemandReconciler(IDemandIdAllocator demandIds)
    {
        _demandIds = demandIds;
    }

    public ReconcileResult Reconcile(
        ProjectionState prior,
        MesSnapshotOutcome snapshot,
        DateTimeOffset now,
        DateTimeOffset goLiveBaseline,
        int disappearThreshold = 2)
    {
        if (snapshot.Kind != SnapshotOutcomeKind.Success)
        {
            return new ReconcileResult(prior);
        }

        var rowsByKey = snapshot.Rows
            .GroupBy(r => (r.TaskType, r.Sublot))
            .ToDictionary(g => g.Key, g => g.ToList());
        var duplicateKeys = rowsByKey
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => kv.Key)
            .ToHashSet();
        var uniquePresentKeys = rowsByKey
            .Where(kv => kv.Value.Count == 1)
            .Select(kv => kv.Key)
            .ToHashSet();

        var next = new List<TransportDemand>();
        var visibleKeys = new HashSet<(string TaskType, string Sublot)>();
        var goneKeys = prior.Demands
            .Where(d => d.Status == DemandStatus.Gone)
            .Select(d => (d.TaskType, d.Sublot))
            .ToHashSet();
        var alerts = new List<IngestAlert>();
        var alertedDuplicateKeys = new HashSet<(string TaskType, string Sublot)>();

        foreach (var demand in prior.Demands)
        {
            if (demand.Status != DemandStatus.Visible)
            {
                next.Add(demand);
                continue;
            }

            var key = (demand.TaskType, demand.Sublot);
            visibleKeys.Add(key);

            if (duplicateKeys.Contains(key))
            {
                next.Add(demand);
                TryAddDuplicateAlert(alerts, alertedDuplicateKeys, key, demand.DemandId);
                continue;
            }

            if (uniquePresentKeys.Contains(key))
            {
                var observed = rowsByKey[key][0];
                if (HasFieldDrift(demand, observed))
                {
                    alerts.Add(new IngestAlert(
                        Code: "FIELD_DRIFT",
                        TaskType: demand.TaskType,
                        Sublot: demand.Sublot,
                        DemandId: demand.DemandId,
                        Message: "MES fields drifted for still-VISIBLE demand; frozen projection retained."));
                }

                next.Add(demand with
                {
                    MesLastSeenAt = now,
                    DisappearCount = 0,
                });
            }
            else
            {
                var disappearCount = demand.DisappearCount + 1;
                next.Add(demand with
                {
                    DisappearCount = disappearCount,
                    Status = disappearCount >= disappearThreshold
                        ? DemandStatus.Gone
                        : DemandStatus.Visible,
                });
            }
        }

        foreach (var key in duplicateKeys)
        {
            TryAddDuplicateAlert(alerts, alertedDuplicateKeys, key, demandId: null);
        }

        foreach (var key in uniquePresentKeys)
        {
            var row = rowsByKey[key][0];
            if (row.Dates < goLiveBaseline)
            {
                continue;
            }

            if (!visibleKeys.Add(key))
            {
                continue;
            }

            var demandId = _demandIds.Next();
            var (locationRisk, locationRiskCode) = EvaluateLocationRisk(row.Area);
            next.Add(new TransportDemand
            {
                DemandId = demandId,
                TaskType = row.TaskType,
                Sublot = row.Sublot,
                Area = row.Area,
                Eqp = row.Eqp,
                Step = row.Step,
                Dates = row.Dates,
                Package = row.Package,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                DisappearCount = 0,
                LocationRisk = locationRisk,
                LocationRiskCode = locationRiskCode,
            });

            if (goneKeys.Contains(key))
            {
                alerts.Add(new IngestAlert(
                    Code: "REAPPEAR_AFTER_GONE",
                    TaskType: row.TaskType,
                    Sublot: row.Sublot,
                    DemandId: demandId,
                    Message: "Reconcile key reappeared after GONE; allocated a new DemandId."));
            }
        }

        return new ReconcileResult(new ProjectionState(next), alerts);
    }

    private static void TryAddDuplicateAlert(
        List<IngestAlert> alerts,
        HashSet<(string TaskType, string Sublot)> alertedDuplicateKeys,
        (string TaskType, string Sublot) key,
        string? demandId)
    {
        if (!alertedDuplicateKeys.Add(key))
        {
            return;
        }

        alerts.Add(new IngestAlert(
            Code: "DUPLICATE_RECONCILE_KEY",
            TaskType: key.TaskType,
            Sublot: key.Sublot,
            DemandId: demandId,
            Message: "Duplicate TASK_TYPE+SUBLOT rows in snapshot; create/update blocked for this key."));
    }

    private static bool HasFieldDrift(TransportDemand demand, MesSnapshotRow row) =>
        !StringEquals(demand.Area, row.Area)
        || !StringEquals(demand.Eqp, row.Eqp)
        || !StringEquals(demand.Step, row.Step)
        || demand.Dates != row.Dates
        || !StringEquals(demand.Package, row.Package);

    private static (bool Risk, string? Code) EvaluateLocationRisk(string? area)
    {
        if (string.IsNullOrWhiteSpace(area))
        {
            return (true, "AREA_EMPTY");
        }

        return IsParseableAreaCode(area.Trim())
            ? (false, null)
            : (true, "AREA_UNPARSEABLE");
    }

    /// <summary>
    /// AREA encoding: letter + two digits + '-' + two digits; neither numeric part may be 00.
    /// Phase-1 does not resolve AREA→station_name; format failure is the unparseable signal.
    /// </summary>
    private static bool IsParseableAreaCode(string area)
    {
        if (area.Length != 6 || area[3] != '-')
        {
            return false;
        }

        if (!char.IsAsciiLetter(area[0]))
        {
            return false;
        }

        if (!char.IsAsciiDigit(area[1]) || !char.IsAsciiDigit(area[2])
            || !char.IsAsciiDigit(area[4]) || !char.IsAsciiDigit(area[5]))
        {
            return false;
        }

        var left = area.Substring(1, 2);
        var right = area.Substring(4, 2);
        return left != "00" && right != "00";
    }

    private static bool StringEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);
}
