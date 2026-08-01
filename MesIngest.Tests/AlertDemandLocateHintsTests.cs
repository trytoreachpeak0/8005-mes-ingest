using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 16 seam: actionable operator feedback when an exact historical
/// DemandId cannot be selected in the current Watch browse window.
/// </summary>
public class AlertDemandLocateHintsTests
{
    [Fact]
    public void Missing_exact_id_points_to_host_history_without_substitution()
    {
        var hint = AlertDemandLocateHints.NotFound("previous-gone-id");

        Assert.Contains("Exact DemandId previous-gone-id", hint, StringComparison.Ordinal);
        Assert.Contains("Host TransportDemand history", hint, StringComparison.Ordinal);
        Assert.Contains("will not substitute the new DemandId", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_gone_id_points_to_filters_and_gone_at_history_window()
    {
        var hint = AlertDemandLocateHints.OutsideCurrentBrowse("previous-gone-id", "GONE");

        Assert.Contains("status=GONE", hint, StringComparison.Ordinal);
        Assert.Contains("Clear TASK_TYPE/SUBLOT filters", hint, StringComparison.Ordinal);
        Assert.Contains("GoneAt history window", hint, StringComparison.Ordinal);
        Assert.Contains("will not substitute another DemandId", hint, StringComparison.Ordinal);
    }
}
