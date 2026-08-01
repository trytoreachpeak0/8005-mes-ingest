namespace MesIngest.Watch;

internal static class AlertDemandLocateHints
{
    public static string NotFound(string demandId) =>
        $"Exact DemandId {demandId} was not found in Host TransportDemand history. "
        + "Verify the previousDemandId in the alert and Host history; Watch will not substitute the new DemandId.";

    public static string OutsideCurrentBrowse(string demandId, string status) =>
        $"Exact DemandId {demandId} exists (status={status}) but is outside the current browse page. "
        + "Clear TASK_TYPE/SUBLOT filters or widen the GoneAt history window if status=GONE; Watch will not substitute another DemandId.";
}
