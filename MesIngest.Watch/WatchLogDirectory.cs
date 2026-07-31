using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MesIngest.Watch;

internal static class WatchLogDirectory
{
    public static void Open(Window owner, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                owner,
                $"Could not open log directory:{Environment.NewLine}{ex.Message}",
                "Open local log directory",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
