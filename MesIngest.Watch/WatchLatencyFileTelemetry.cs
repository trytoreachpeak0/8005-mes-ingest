using System.IO;
using System.Text;
using MesIngest.Core;
using IOPath = System.IO.Path;

namespace MesIngest.Watch;

/// <summary>
/// Appends field-comparable latency lines beside WatchConnectionEvent logs.
/// </summary>
internal sealed class WatchLatencyFileTelemetry : ILatencyTelemetry
{
    private readonly string _directory;
    private readonly object _gate = new();

    public WatchLatencyFileTelemetry(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Directory is required.", nameof(directory));
        }

        _directory = directory;
    }

    public static WatchLatencyFileTelemetry FromOptions(WatchOptions options, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(directory ?? WatchConnectionEventJournal.DefaultDirectory);
    }

    public void Record(LatencyEvent evt)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var line = LatencyLogFormatter.Format(evt);
            var path = IOPath.Combine(_directory, $"watch-latency-{DateTime.UtcNow:yyyyMMdd}.log");
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
