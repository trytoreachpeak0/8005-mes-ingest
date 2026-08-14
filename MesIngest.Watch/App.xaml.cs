namespace MesIngest.Watch;

internal partial class App : Application
{
    private WatchV2ApplicationComposition? _composition;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = WatchOptionsLoader.Load(WatchOptionsLoader.BuildDefault());
        var processFileLocations = WatchProcessFileLocations.Resolve();
        _composition = WatchV2ApplicationComposition.Create(
            options,
            connectionPreferencesPath: processFileLocations.ConnectionPreferencesPath,
            workspacePreferencesPath: processFileLocations.WorkspacePreferencesPath,
            timeProvider: WatchProcessTimeProvider.Resolve(),
            areaFilterProfilesDirectoryPath: processFileLocations.AreaFilterProfilesDirectoryPath);
        _composition.CreateMainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _composition?.Dispose();
        base.OnExit(e);
    }
}
