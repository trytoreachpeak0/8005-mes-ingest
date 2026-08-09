using System.Runtime.CompilerServices;

namespace MesIngest.Watch.UiTests;

public static class WatchVisualModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        WatchVisualCaptureConverter.Initialize();
        VerifyXaml.Initialize();
        VerifierSettings.InitializePlugins();
        Verifier.UseProjectRelativeDirectory("Baselines");
    }
}
