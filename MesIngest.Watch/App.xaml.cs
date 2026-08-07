using MesIngest.Core;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Windows.Interop;
using System.Windows.Media;

namespace MesIngest.Watch;

internal partial class App : Application
{
    private WatchLocalLogDispatcher? _logDispatcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = WatchOptionsLoader.Load(WatchOptionsLoader.BuildDefault());
        if (options.RenderingMode == WatchRenderingMode.SoftwareOnly)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        _logDispatcher = new WatchLocalLogDispatcher();
        var telemetryIoDiagnostics = new WatchTelemetryIoDiagnosticBuffer();
        var logDirectory = string.IsNullOrWhiteSpace(options.LogDirectory)
            ? null
            : Path.GetFullPath(options.LogDirectory);
        var journal = WatchConnectionEventJournal.FromOptions(
            options,
            directory: logDirectory,
            onWriteFailure: ex => telemetryIoDiagnostics.Record("watch-connection", ex),
            dispatcher: _logDispatcher);
        var telemetry = WatchLatencyFileTelemetry.FromOptions(
            options,
            directory: logDirectory,
            onWriteFailure: ex => telemetryIoDiagnostics.Record("watch-latency", ex),
            dispatcher: _logDispatcher);
        var initialSettings = new WatchHostSettings(
            options.BaseUrl,
            options.SharedSecret,
            options.RequestTimeoutSeconds);
        var client = MesIngestApiClient.CreateForHost(initialSettings, telemetry);
        var window = new MainWindow(
            client,
            options,
            journal,
            telemetryIoDiagnostics: telemetryIoDiagnostics,
            hostAdapterFactory: settings => MesIngestApiClient.CreateForHost(settings, telemetry));
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logDispatcher?.Dispose();
        base.OnExit(e);
    }
}
