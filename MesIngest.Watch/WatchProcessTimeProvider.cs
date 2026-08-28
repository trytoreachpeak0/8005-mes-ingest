using System.Globalization;

namespace MesIngest.Watch;

internal static class WatchProcessTimeProvider
{
    private const string UiTestModeVariable = "MESINGEST_WATCH_UI_TEST_MODE";
    private const string FixedUtcNowVariable = "MESINGEST_WATCH_UI_FIXED_UTC_NOW";
    private const string FixedPresentationUtcNowVariable =
        "MESINGEST_WATCH_UI_FIXED_PRESENTATION_UTC_NOW";

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

        return ResolveRequired(FixedUtcNowVariable);
    }

    public static TimeProvider? ResolvePresentation()
    {
        if (!IsUiTestMode)
        {
            return null;
        }

        var configured = Environment.GetEnvironmentVariable(FixedPresentationUtcNowVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? ResolveRequired(FixedUtcNowVariable)
            : ResolveRequired(FixedPresentationUtcNowVariable);
    }

    private static TimeProvider ResolveRequired(string variable)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{variable} is required when {UiTestModeVariable}=1.");
        }

        if (!DateTimeOffset.TryParseExact(
                configured,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var fixedNow))
        {
            throw new InvalidOperationException(
                $"{variable} must be an invariant round-trip timestamp.");
        }

        return new FixedTimeProvider(fixedNow.ToUniversalTime());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
