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
        DateTimeOffset goLiveBaseline)
    {
        if (snapshot.Kind != SnapshotOutcomeKind.Success)
        {
            return new ReconcileResult(prior);
        }

        var created = new List<TransportDemand>(prior.Demands);
        var visibleKeys = prior.Demands
            .Where(d => d.Status == DemandStatus.Visible)
            .Select(d => (d.TaskType, d.Sublot))
            .ToHashSet();

        foreach (var row in snapshot.Rows)
        {
            if (row.Dates < goLiveBaseline)
            {
                continue;
            }

            var key = (row.TaskType, row.Sublot);
            if (visibleKeys.Contains(key))
            {
                continue;
            }

            created.Add(new TransportDemand
            {
                DemandId = _demandIds.Next(),
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
            visibleKeys.Add(key);
        }

        return new ReconcileResult(new ProjectionState(created));
    }
}
