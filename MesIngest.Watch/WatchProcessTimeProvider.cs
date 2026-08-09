using System.Globalization;

namespace MesIngest.Watch;

internal static class WatchProcessTimeProvider
{
    private const string UiTestModeVariable = "MESINGEST_WATCH_UI_TEST_MODE";
    private const string FixedUtcNowVariable = "MESINGEST_WATCH_UI_FIXED_UTC_NOW";

    public static bool IsUiTestMode => string.Equals(
        Environment.GetEnvironmentVariable(UiTestModeVariable),
        "1",
        StringComparison.Ordinal);

    public static TimeProvider? Resolve()
    {
        if (!IsUiTestMode)
        {
            return null;
        }

        var configured = Environment.GetEnvironmentVariable(FixedUtcNowVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{FixedUtcNowVariable} is required when {UiTestModeVariable}=1.");
        }

        if (!DateTimeOffset.TryParseExact(
                configured,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var fixedNow))
        {
            throw new InvalidOperationException(
                $"{FixedUtcNowVariable} must be an invariant round-trip timestamp.");
        }

        return new FixedTimeProvider(fixedNow.ToUniversalTime());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
