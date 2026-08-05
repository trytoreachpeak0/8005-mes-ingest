using System.Windows;
using System.Windows.Threading;

namespace MesIngest.Watch.Prototype;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var variant = e.Args
            .FirstOrDefault(argument => argument.StartsWith("--variant=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1] ?? "A";
        var capturePath = e.Args
            .FirstOrDefault(argument => argument.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];
        var scenario = e.Args
            .FirstOrDefault(argument => argument.StartsWith("--scenario=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1] ?? "Healthy";

        var window = new MainWindow(variant, scenario);
        MainWindow = window;

        if (!string.IsNullOrWhiteSpace(capturePath))
        {
            window.ContentRendered += (_, _) =>
            {
                window.Dispatcher.BeginInvoke(() =>
                {
                    window.Capture(capturePath);
                    Shutdown();
                }, DispatcherPriority.ApplicationIdle);
            };
        }

        window.Show();
    }
}
