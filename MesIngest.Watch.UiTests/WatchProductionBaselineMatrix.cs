namespace MesIngest.Watch.UiTests;

internal static class WatchProductionBaselineMatrix
{
    public static IReadOnlyList<string> Names { get; } =
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
    ];

    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("MESINGEST_WATCH_COMPARE_WINDOW_BASELINES"),
            "1",
            StringComparison.Ordinal)
        || string.Equals(
            Environment.GetEnvironmentVariable("MESINGEST_WATCH_CAPTURE_WINDOW_CANDIDATES"),
            "1",
            StringComparison.Ordinal);

    public static bool Contains(string name) => Names.Contains(name, StringComparer.Ordinal);
}
