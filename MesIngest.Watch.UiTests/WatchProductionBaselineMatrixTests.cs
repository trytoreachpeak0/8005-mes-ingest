namespace MesIngest.Watch.UiTests;

public sealed class WatchProductionBaselineMatrixTests
{
    [Fact]
    public void Shared_ticket_19_22_baseline_matrix_contains_every_approved_page_state_once()
    {
        Assert.Equal(
            [
                "01-overview",
                "01e-overview-navigation-expanded",
                "01f-overview-offline-retained",
                "02-settings",
                "02v-settings-timeout-validation",
                "03-demand-series-detail",
                "04-readability-audit-detail",
                "05-area-filter-profile",
                "06-error-search-variant-a",
                "07-current-ingest-attention",
                "08-current-attention-error-drill",
            ],
            WatchProductionBaselineMatrix.Names);
        Assert.Equal(
            WatchProductionBaselineMatrix.Names.Count,
            WatchProductionBaselineMatrix.Names.Distinct(StringComparer.Ordinal).Count());
    }
}
