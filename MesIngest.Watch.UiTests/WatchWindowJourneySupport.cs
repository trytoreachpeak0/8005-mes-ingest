using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using MesIngest.Watch;
using FlaUIApplication = FlaUI.Core.Application;

namespace MesIngest.Watch.UiTests;

/// <summary>
/// Journey plumbing shared by the packaged-Watch UI journeys: where evidence goes,
/// which executable to launch, how to wait for exit, and how a failure or a cleanup
/// problem is recorded. Extracted so no journey suite owns another suite's helpers.
/// </summary>
internal static class WatchWindowJourneySupport
{
    internal static string DumpUiaTree(
        AutomationElement root,
        UIA3Automation automation)
    {
        var output = new StringBuilder();
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var remaining = 5000;

        void Append(AutomationElement element, int depth)
        {
            if (remaining-- <= 0)
            {
                output.AppendLine("... UIA tree truncated at 5000 elements ...");
                return;
            }

            output.Append(' ', depth * 2)
                .Append(SafeProperty(() => element.ControlType.ToString()))
                .Append(" id=").Append(SafeProperty(() => element.Properties.AutomationId.ValueOrDefault))
                .Append(" name=").Append(SafeProperty(() => element.Properties.Name.ValueOrDefault))
                .Append(" help=").Append(SafeProperty(() => element.Properties.HelpText.ValueOrDefault))
                .Append(" itemStatus=").Append(SafeProperty(() => element.Properties.ItemStatus.ValueOrDefault))
                .Append(" enabled=").Append(SafeProperty(() => element.Properties.IsEnabled.ValueOrDefault.ToString()))
                .AppendLine();
            var child = walker.GetFirstChild(element);
            while (child is not null && remaining > 0)
            {
                Append(child, depth + 1);
                child = walker.GetNextSibling(child);
            }
        }

        Append(root, 0);
        return output.ToString();
    }

    private static string? SafeProperty(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return $"(unsupported:{ex.GetType().Name})";
        }
    }

    internal static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        OperationCanceledException? cancellation = null;
        if (!process.HasExited)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                // Bounded cleanup below is authoritative.
            }
            catch (OperationCanceledException ex)
            {
                cancellation = ex;
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        if (cancellation is not null)
        {
            ExceptionDispatchInfo.Capture(cancellation).Throw();
        }
    }

    internal static bool TryRecordFailure(
        WatchJourneyEvidence evidence,
        string step,
        Exception exception,
        TimeSpan timeout)
    {
        try
        {
            evidence.RecordFailure(step, exception, timeout);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void CaptureCleanupFailure(
        ref Exception? primaryFailure,
        ref string failedStep,
        Exception cleanupFailure,
        string cleanupStep)
    {
        if (primaryFailure is not null)
        {
            return;
        }

        primaryFailure = cleanupFailure;
        failedStep = cleanupStep;
    }

    internal static string ResolveArtifactRoot()
    {
        var configured = Environment.GetEnvironmentVariable("MESINGEST_WATCH_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return Path.GetFullPath(configured);
        }

        var root = Path.Combine(Path.GetTempPath(), $"watch-window-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    internal static string ResolveWatchExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("MESINGEST_WATCH_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var explicitPath = Path.GetFullPath(configured);
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException("Configured Watch executable does not exist.", explicitPath);
            }

            return explicitPath;
        }

        var targetDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = targetDirectory.Parent?.Name
            ?? throw new InvalidOperationException("Cannot resolve UI test configuration directory.");
        var csharpDirectory = targetDirectory.Parent?.Parent?.Parent?.Parent
            ?? throw new InvalidOperationException("Cannot resolve MesIngest csharp directory.");
        var path = Path.Combine(
            csharpDirectory.FullName,
            "MesIngest.Watch",
            "bin",
            configuration,
            "net8.0-windows",
            "MesIngest.Watch.exe");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Build MesIngest.Watch before running real-window journeys.",
                path);
        }

        return path;
    }
}
