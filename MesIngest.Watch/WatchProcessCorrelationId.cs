namespace MesIngest.Watch;

internal static class WatchProcessCorrelationId
{
    private const string UiTestModeVariable = "MESINGEST_WATCH_UI_TEST_MODE";
    private const string FixedVisualIdentity = "00000000000000000000000000000023";

    public static string Create(Func<string, string?>? getEnvironmentVariable = null)
    {
        var read = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        return string.Equals(read(UiTestModeVariable), "1", StringComparison.Ordinal)
            ? FixedVisualIdentity
            : Guid.NewGuid().ToString("N");
    }
}
