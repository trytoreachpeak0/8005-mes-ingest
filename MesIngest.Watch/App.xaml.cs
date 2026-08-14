namespace MesIngest.Watch;

internal partial class App : Application
{
    private WatchV2ApplicationComposition? _composition;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = WatchOptionsLoader.Load(WatchOptionsLoader.BuildDefault());
        _composition = WatchV2ApplicationComposition.Create(
            options,
            timeProvider: WatchProcessTimeProvider.Resolve());
        _composition.CreateMainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _composition?.Dispose();
        base.OnExit(e);
    }
}
