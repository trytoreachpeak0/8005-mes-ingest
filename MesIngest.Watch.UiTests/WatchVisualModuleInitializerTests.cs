namespace MesIngest.Watch.UiTests;

public sealed class WatchVisualModuleInitializerTests
{
    [Fact]
    public void Explicit_baseline_directory_is_normalized_for_copied_golden_runs()
    {
        var variable = WatchVisualModuleInitializer.BaselineDirectoryEnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        var configured = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "watch-golden",
            "SelectedUi");

        try
        {
            Environment.SetEnvironmentVariable(variable, configured);

            Assert.Equal(
                System.IO.Path.GetFullPath(configured),
                WatchVisualModuleInitializer.ResolveExplicitBaselineDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void Project_relative_baselines_remain_the_default()
    {
        var variable = WatchVisualModuleInitializer.BaselineDirectoryEnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, "   ");

            Assert.Null(WatchVisualModuleInitializer.ResolveExplicitBaselineDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
