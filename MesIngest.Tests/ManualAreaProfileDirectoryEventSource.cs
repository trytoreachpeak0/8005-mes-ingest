using System.IO;
using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Directory event source whose raw events are pushed by the test, so the
/// interpretation rules can be exercised without a real
/// <see cref="System.IO.FileSystemWatcher"/>.
/// </summary>
internal sealed class ManualAreaProfileDirectoryEventSource
    : IWatchAreaProfileDirectoryEventSource
{
    public event EventHandler<WatchAreaProfileDirectoryEvent>? Raised;

    public event EventHandler<WatchAreaProfileDirectoryFailure>? Failed;

    public string? StartedDirectoryPath { get; private set; }

    public bool IsDisposed { get; private set; }

    public bool IsStarted => StartedDirectoryPath is not null;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    /// <summary>
    /// Makes the next <see cref="Start"/> fail the way an unreachable or
    /// unwatchable directory would.
    /// </summary>
    public bool FailNextStart { get; set; }

    public void Start(string directoryPath)
    {
        if (FailNextStart)
        {
            FailNextStart = false;
            throw new IOException("测试注入的目录监视启动失败。");
        }

        StartedDirectoryPath = directoryPath;
        StartCount++;
    }

    public void RaiseCreated(string fileName) =>
        Raise(WatchAreaProfileDirectoryEventKind.Created, fileName);

    public void RaiseChanged(string fileName) =>
        Raise(WatchAreaProfileDirectoryEventKind.Changed, fileName);

    public void RaiseDeleted(string fileName) =>
        Raise(WatchAreaProfileDirectoryEventKind.Deleted, fileName);

    public void RaiseRenamed(string fileName, string previousFileName) =>
        Raised?.Invoke(this, new WatchAreaProfileDirectoryEvent(
            WatchAreaProfileDirectoryEventKind.Renamed,
            fileName,
            previousFileName));

    public void RaiseFailure(Exception exception) =>
        Failed?.Invoke(this, new WatchAreaProfileDirectoryFailure(exception));

    public void Stop()
    {
        if (StartedDirectoryPath is null)
        {
            return;
        }

        StartedDirectoryPath = null;
        StopCount++;
    }

    public void Dispose()
    {
        Stop();
        IsDisposed = true;
    }

    private void Raise(WatchAreaProfileDirectoryEventKind kind, string fileName) =>
        Raised?.Invoke(this, new WatchAreaProfileDirectoryEvent(kind, fileName));
}
