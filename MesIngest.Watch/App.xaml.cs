using MesIngest.Core;
using Microsoft.Extensions.Configuration;

namespace MesIngest.Watch;

internal partial class App : Application
{
    private WatchLocalLogDispatcher? _logDispatcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = WatchOptionsLoader.Load(WatchOptionsLoader.BuildDefault());

        var http = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
        };
        if (!string.IsNullOrWhiteSpace(options.SharedSecret))
        {
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.SharedSecret);
        }

        _logDispatcher = new WatchLocalLogDispatcher();
        var telemetryIoDiagnostics = new WatchTelemetryIoDiagnosticBuffer();
        var journal = WatchConnectionEventJournal.FromOptions(
            options,
            onWriteFailure: ex => telemetryIoDiagnostics.Record("watch-connection", ex),
            dispatcher: _logDispatcher);
        var client = new MesIngestApiClient(
            http,
            options.RequestTimeoutSeconds,
            telemetry: WatchLatencyFileTelemetry.FromOptions(
                options,
                onWriteFailure: ex => telemetryIoDiagnostics.Record("watch-latency", ex),
                dispatcher: _logDispatcher));
        var window = new MainWindow(
            client,
            options,
            journal,
            telemetryIoDiagnostics: telemetryIoDiagnostics);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logDispatcher?.Dispose();
        base.OnExit(e);
    }
}
