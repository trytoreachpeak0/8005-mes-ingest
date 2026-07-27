namespace MesIngest.Core;

public sealed class TransportDemandReconciler
{
    public const int DefaultZeroDropClearStreak = 2;

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
        int disappearThreshold = 2,
        int zeroDropEnterThreshold = 10,
        int zeroDropClearStreak = DefaultZeroDropClearStreak,
        RestartRecovery? restartRecovery = null)
    {
        if (snapshot.Kind != SnapshotOutcomeKind.Success)
        {
            return new ReconcileResult(prior);
        }

        restartRecovery ??= RestartRecovery.Normal;
        var isBarrierRound = restartRecovery.Phase == RestartRecoveryPhase.BarrierRound;

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

        var countsByType = snapshot.Rows
            .GroupBy(r => r.TaskType)
            .ToDictionary(g => g.Key, g => g.Count());
        var pausePrior = restartRecovery.Phase == RestartRecoveryPhase.PostBarrierRound
            ? AdoptBarrierRoundBaseline(prior.TaskTypePauses, restartRecovery.BarrierRoundCountsByType)
            : prior.TaskTypePauses;
        var nextPauses = AdvancePauseStates(
            pausePrior,
            countsByType,
            zeroDropEnterThreshold,
            zeroDropClearStreak,
            out var enteredPauseTypes,
            isBarrierRound);

        var pausedTypes = nextPauses
            .Where(p => p.PausedZeroDrop)
            .Select(p => p.TaskType)
            .ToHashSet(StringComparer.Ordinal);

        var next = new List<TransportDemand>();
        var visibleKeys = new HashSet<(string TaskType, string Sublot)>();
        var goneKeys = prior.Demands
            .Where(d => d.Status == DemandStatus.Gone)
            .Select(d => (d.TaskType, d.Sublot))
            .ToHashSet();
        var alerts = new List<IngestAlert>();
        var alertedDuplicateKeys = new HashSet<(string TaskType, string Sublot)>();

        foreach (var taskType in enteredPauseTypes.OrderBy(t => t, StringComparer.Ordinal))
        {
            alerts.Add(new IngestAlert(
                Code: "PAUSED_ZERO_DROP",
                TaskType: taskType,
                Message: "TASK_TYPE count dropped to 0 after healthy non-zero baseline; disappear/GONE suspended for this type."));
        }

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
            else if (isBarrierRound || pausedTypes.Contains(demand.TaskType))
            {
                next.Add(demand);
            }
            else
            {
                var disappearCount = demand.DisappearCount + 1;
                var gone = disappearCount >= disappearThreshold;
                next.Add(demand with
                {
                    DisappearCount = disappearCount,
                    Status = gone ? DemandStatus.Gone : DemandStatus.Visible,
                    GoneAt = gone ? now : demand.GoneAt,
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
                CreatedAt = now,
                GoneAt = null,
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

        return new ReconcileResult(new ProjectionState(next, nextPauses), alerts);
    }

    private static IReadOnlyList<TaskTypePauseState> AdoptBarrierRoundBaseline(
        IReadOnlyList<TaskTypePauseState> priorPauses,
        IReadOnlyDictionary<string, int>? barrierRoundCountsByType)
    {
        if (barrierRoundCountsByType is null || barrierRoundCountsByType.Count == 0)
        {
            return priorPauses;
        }

        var byType = priorPauses.ToDictionary(p => p.TaskType, StringComparer.Ordinal);
        foreach (var (taskType, count) in barrierRoundCountsByType)
        {
            if (count <= 0)
            {
                continue;
            }

            if (byType.TryGetValue(taskType, out var prior))
            {
                // Never demote a persisted healthy baseline across restart (Story 20).
                if (count > prior.LastHealthyNonZeroCount)
                {
                    byType[taskType] = prior with { LastHealthyNonZeroCount = count };
                }
            }
            else
            {
                byType[taskType] = new TaskTypePauseState(
                    TaskType: taskType,
                    PausedZeroDrop: false,
                    LastHealthyNonZeroCount: count,
                    RecoveryStreak: 0);
            }
        }

        return byType.Values
            .OrderBy(p => p.TaskType, StringComparer.Ordinal)
            .ToList();
    }

    private static List<TaskTypePauseState> AdvancePauseStates(
        IReadOnlyList<TaskTypePauseState> priorPauses,
        IReadOnlyDictionary<string, int> countsByType,
        int zeroDropEnterThreshold,
        int zeroDropClearStreak,
        out List<string> enteredPauseTypes,
        bool isBarrierRound = false)
    {
        enteredPauseTypes = new List<string>();
        var byType = priorPauses.ToDictionary(p => p.TaskType, StringComparer.Ordinal);
        var relevantTypes = byType.Keys
            .Concat(countsByType.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal);

        var next = new List<TaskTypePauseState>();
        foreach (var taskType in relevantTypes)
        {
            byType.TryGetValue(taskType, out var prior);
            var lastHealthy = prior?.LastHealthyNonZeroCount ?? 0;
            var paused = prior?.PausedZeroDrop ?? false;
            var recovery = prior?.RecoveryStreak ?? 0;
            var count = countsByType.GetValueOrDefault(taskType, 0);

            if (count > 0)
            {
                // Barrier round must not demote a persisted healthy baseline (Story 20).
                if (isBarrierRound)
                {
                    if (count > lastHealthy)
                    {
                        lastHealthy = count;
                    }
                }
                else
                {
                    lastHealthy = count;
                }

                if (paused)
                {
                    recovery += 1;
                    if (recovery >= zeroDropClearStreak)
                    {
                        paused = false;
                        recovery = 0;
                    }
                }
                else
                {
                    recovery = 0;
                }
            }
            else
            {
                recovery = 0;
                if (!isBarrierRound && !paused && lastHealthy >= zeroDropEnterThreshold)
                {
                    paused = true;
                    enteredPauseTypes.Add(taskType);
                }
            }

            if (paused || lastHealthy > 0 || recovery > 0 || prior is not null)
            {
                next.Add(new TaskTypePauseState(
                    TaskType: taskType,
                    PausedZeroDrop: paused,
                    LastHealthyNonZeroCount: lastHealthy,
                    RecoveryStreak: recovery));
            }
        }

        return next;
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
