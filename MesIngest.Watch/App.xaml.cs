using MesIngest.Core;
using Microsoft.Extensions.Configuration;

namespace MesIngest.Watch;

internal partial class App : Application
{
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

        var journal = WatchConnectionEventJournal.FromOptions(options);
        var client = new MesIngestApiClient(
            http,
            options.RequestTimeoutSeconds,
            telemetry: WatchLatencyFileTelemetry.FromOptions(
                options,
                onWriteFailure: ex =>
                {
                    try
                    {
                        WatchLatencyWriteFailureJournal.Append(journal, ex);
                    }
                    catch
                    {
                        // Diagnostic journal write is best-effort.
                    }
                }));
        var window = new MainWindow(client, options, journal);
        window.Show();
    }
}
