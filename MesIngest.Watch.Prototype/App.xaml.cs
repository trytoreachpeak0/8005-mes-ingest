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
        var page = e.Args
            .FirstOrDefault(argument => argument.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1] ?? "OV";
        var width = ParseDimension(e.Args, "--width=", 1440);
        var height = ParseDimension(e.Args, "--height=", 900);

        var window = new MainWindow(variant, scenario, page)
        {
            Width = width,
            Height = height,
            Left = 0,
            Top = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        MainWindow = window;

        if (!string.IsNullOrWhiteSpace(capturePath))
        {
            window.SetCaptureMode();
            window.Capture(capturePath, width, height);
            Shutdown();
            return;
        }

        window.Show();
    }

    private static int ParseDimension(IEnumerable<string> args, string prefix, int fallback)
    {
        var raw = args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
        return int.TryParse(raw, out var value) && value >= 720 ? value : fallback;
    }
}
