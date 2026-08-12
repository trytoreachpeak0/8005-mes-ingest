using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MesIngest.Watch.FluentPrototype;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);

        var variant = ReadArgument(e.Args, "--variant=") ?? "A";
        var page = ReadArgument(e.Args, "--page=") ?? "series";
        var scenario = ReadArgument(e.Args, "--scenario=") ?? "healthy";
        var capture = ReadArgument(e.Args, "--capture=");
        var width = ReadDimension(e.Args, "--width=", 1440);
        var height = ReadDimension(e.Args, "--height=", 900);

        var window = new MainWindow(variant, page, scenario)
        {
            Width = width,
            Height = height,
            Left = 0,
            Top = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        MainWindow = window;
        if (string.IsNullOrWhiteSpace(capture))
        {
            window.Show();
            return;
        }

        var captured = false;
        window.ContentRendered += (_, _) =>
        {
            if (captured)
            {
                return;
            }

            captured = true;
            window.Dispatcher.BeginInvoke(() =>
            {
                window.Capture(capture, width, height);
                window.Close();
                Shutdown();
            }, DispatcherPriority.ApplicationIdle);
        };
        window.Show();
    }

    private static string? ReadArgument(IEnumerable<string> args, string prefix) =>
        args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];

    private static int ReadDimension(IEnumerable<string> args, string prefix, int fallback) =>
        int.TryParse(ReadArgument(args, prefix), out var value) && value >= 720 ? value : fallback;
}
