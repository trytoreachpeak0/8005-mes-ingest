namespace MesIngest.Watch;

internal static class AlertDemandLocateHints
{
    public static string NotFound(AlertDemandTarget target) =>
        target.Kind == AlertDemandTargetKind.PreviousGone
            ? $"Exact DemandId {target.DemandId} was not found in Host TransportDemand history. "
                + "Verify the previousDemandId in the alert and Host history; Watch will not substitute the new DemandId."
            : $"Exact DemandId {target.DemandId} was not found in Host TransportDemands. "
                + "Verify the alert DemandId and Host projection history; Watch did not change the requested ID.";

    public static string OutsideCurrentBrowse(AlertDemandTarget target, string status)
    {
        var suffix = target.Kind == AlertDemandTargetKind.PreviousGone
            ? "Watch will not substitute the new DemandId."
            : "Watch did not change the requested ID.";
        return $"Exact DemandId {target.DemandId} exists (status={status}) but is outside the current browse page. "
            + $"Clear TASK_TYPE/SUBLOT filters or widen the GoneAt history window if status=GONE; {suffix}";
    }
}
