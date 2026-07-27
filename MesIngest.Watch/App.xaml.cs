using Microsoft.Extensions.Configuration;

namespace MesIngest.Watch;

internal partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "MesIngestWatch__")
            .Build();

        var options = new WatchOptions();
        config.GetSection("Watch").Bind(options);

        var baseUrlEnv = Environment.GetEnvironmentVariable("MesIngestWatch__BaseUrl");
        if (!string.IsNullOrWhiteSpace(baseUrlEnv))
        {
            options.BaseUrl = baseUrlEnv;
        }

        var refreshEnv = Environment.GetEnvironmentVariable("MesIngestWatch__RefreshSeconds");
        if (int.TryParse(refreshEnv, out var refresh) && refresh > 0)
        {
            options.RefreshSeconds = refresh;
        }

        if (options.RefreshSeconds < 1)
        {
            options.RefreshSeconds = 2;
        }

        var http = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };

        var client = new MesIngestApiClient(http);
        var window = new MainWindow(client, options);
        window.Show();
    }
}
