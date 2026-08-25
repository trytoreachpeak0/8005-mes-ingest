using System.IO;
using System.Text;
using MesIngest.Core;
using IOPath = System.IO.Path;

namespace MesIngest.Watch;

/// <summary>
/// Appends field-comparable latency lines beside WatchConnectionEvent logs.
/// Write failures are swallowed so a successful Host fetch is never failed by telemetry IO.
/// </summary>
internal sealed class WatchLatencyFileTelemetry : ILatencyTelemetry
{
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly long _maxSizeBytes;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<Exception>? _onWriteFailure;
    private readonly WatchLocalLogDispatcher _dispatcher;

    public WatchLatencyFileTelemetry(
        string directory,
        int retentionDays = 15,
        long maxSizeBytes = 100L * 1024 * 1024,
        Func<DateTimeOffset>? utcNow = null,
        Action<Exception>? onWriteFailure = null,
        WatchLocalLogDispatcher? dispatcher = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Directory is required.", nameof(directory));
        }

        if (retentionDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }

        if (maxSizeBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSizeBytes));
        }

        _directory = directory;
        _retentionDays = retentionDays;
        _maxSizeBytes = maxSizeBytes;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _onWriteFailure = onWriteFailure;
        _dispatcher = dispatcher ?? new WatchLocalLogDispatcher();
    }

    public static WatchLatencyFileTelemetry FromOptions(
        WatchOptions options,
        string? directory = null,
        Action<Exception>? onWriteFailure = null,
        WatchLocalLogDispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(
            directory ?? WatchConnectionEventJournal.DefaultDirectory,
            options.ConnectionLogRetentionDays,
            options.ConnectionLogMaxSizeMb * 1024L * 1024L,
            onWriteFailure: onWriteFailure,
            dispatcher: dispatcher);
    }

    public void Record(LatencyEvent evt)
    {
        var recordedAt = _utcNow();
        var line = $"recordedAt={recordedAt:O} {LatencyLogFormatter.Format(evt)}";
        _dispatcher.TryEnqueue(
            () =>
            {
                Directory.CreateDirectory(_directory);
                var path = IOPath.Combine(_directory, $"watch-latency-{recordedAt:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                WatchLocalLogRetention.Enforce(
                    _directory,
                    _retentionDays,
                    _maxSizeBytes,
                    _utcNow,
                    _onWriteFailure);
            },
            _onWriteFailure);
    }

    internal Task DrainAsync() => _dispatcher.DrainAsync();
}
