using MesIngest.Core;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 10 seam: AlertListQueryParser.sortBy allow-list (distinct from Demand).
/// </summary>
public class AlertListQueryParserTests
{
    [Theory]
    [InlineData(null, AlertSortColumn.LastSeenAt)]
    [InlineData("", AlertSortColumn.LastSeenAt)]
    [InlineData("lastSeenAt", AlertSortColumn.LastSeenAt)]
    [InlineData("firstSeenAt", AlertSortColumn.FirstSeenAt)]
    [InlineData("code", AlertSortColumn.Code)]
    [InlineData("severity", AlertSortColumn.Severity)]
    [InlineData("alertId", AlertSortColumn.AlertId)]
    public void TryParseSortBy_accepts_alert_allow_list(string? raw, AlertSortColumn expected)
    {
        Assert.True(AlertListQueryParser.TryParseSortBy(raw, out var sortBy, out var error));
        Assert.Null(error);
        Assert.Equal(expected, sortBy);
    }

    [Theory]
    [InlineData("dates")]
    [InlineData("demandId")]
    [InlineData("goneAt")]
    [InlineData("package")]
    public void TryParseSortBy_rejects_demand_only_columns(string raw)
    {
        Assert.False(AlertListQueryParser.TryParseSortBy(raw, out _, out var error));
        Assert.Contains("lastSeenAt", error!, StringComparison.Ordinal);
    }
}
