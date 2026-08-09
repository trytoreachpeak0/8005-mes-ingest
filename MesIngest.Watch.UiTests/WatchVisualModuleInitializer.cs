using System.Runtime.CompilerServices;

namespace MesIngest.Watch.UiTests;

public static class WatchVisualModuleInitializer
{
    internal const string BaselineDirectoryEnvironmentVariable =
        "MES_INGEST_WATCH_BASELINE_DIRECTORY";

    [ModuleInitializer]
    public static void Initialize()
    {
        WatchVisualCaptureConverter.Initialize();
        VerifyXaml.Initialize();
        VerifierSettings.InitializePlugins();

        var explicitDirectory = ResolveExplicitBaselineDirectory();
        if (explicitDirectory is null)
        {
            Verifier.UseProjectRelativeDirectory("Baselines/SelectedUi");
        }
        else
        {
            Verifier.DerivePathInfo((_, _, type, method) => new VerifyTests.PathInfo(
                explicitDirectory,
                type.Name,
                method.Name));
        }
    }

    internal static string? ResolveExplicitBaselineDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(
            BaselineDirectoryEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? null
            : System.IO.Path.GetFullPath(configured);
    }
}
