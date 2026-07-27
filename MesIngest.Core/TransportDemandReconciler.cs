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

        var presentKeys = snapshot.Rows
            .Select(r => (r.TaskType, r.Sublot))
            .ToHashSet();

        var next = new List<TransportDemand>();
        var visibleKeys = new HashSet<(string TaskType, string Sublot)>();
        var goneKeys = prior.Demands
            .Where(d => d.Status == DemandStatus.Gone)
            .Select(d => (d.TaskType, d.Sublot))
            .ToHashSet();
        var alerts = new List<IngestAlert>();

        foreach (var demand in prior.Demands)
        {
            if (demand.Status != DemandStatus.Visible)
            {
                next.Add(demand);
                continue;
            }

            var key = (demand.TaskType, demand.Sublot);
            visibleKeys.Add(key);
            if (presentKeys.Contains(key))
            {
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

        foreach (var row in snapshot.Rows)
        {
            if (row.Dates < goLiveBaseline)
            {
                continue;
            }

            var key = (row.TaskType, row.Sublot);
            if (!visibleKeys.Add(key))
            {
                continue;
            }

            var demandId = _demandIds.Next();
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
}
