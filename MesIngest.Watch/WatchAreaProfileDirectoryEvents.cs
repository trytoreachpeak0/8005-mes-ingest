using System.IO;

namespace MesIngest.Watch;

internal enum WatchAreaProfileDirectoryEventKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
}

/// <summary>
/// One raw file system event observed in the AREA profile directory. Raw events
/// carry no semantics: interpretation (debounce, companion filtering, extension
/// revalidation) belongs to <see cref="WatchAreaFilterProfileStore"/>.
/// </summary>
internal sealed record WatchAreaProfileDirectoryEvent(
    WatchAreaProfileDirectoryEventKind Kind,
    string FileName,
    string? PreviousFileName = null);

/// <summary>
/// One interpreted directory change: the profile names whose files appeared,
/// disappeared, or changed inside a single debounce window.
/// </summary>
internal sealed record WatchAreaProfileRename(
    string PreviousProfileName,
    string NewProfileName);

internal sealed record WatchAreaProfileDirectoryChange(
    IReadOnlyList<string> AffectedProfileNames,
    IReadOnlyList<WatchAreaProfileRename> Renames,
    IReadOnlyList<string> DeletedProfileNames,
    bool RequiresFullRescan = false);

internal sealed record WatchAreaProfileDirectoryFailure(Exception Exception);

internal enum WatchAreaProfileDirectoryWatchStatus
{
    Watching,
    Degraded,
    Stopped,
}

internal sealed record WatchAreaProfileDirectoryWatchStateChange(
    WatchAreaProfileDirectoryWatchStatus Status,
    string Message);

internal interface IWatchAreaProfileDirectoryEventSource : IDisposable
{
    event EventHandler<WatchAreaProfileDirectoryEvent>? Raised;

    event EventHandler<WatchAreaProfileDirectoryFailure>? Failed;

    void Start(string directoryPath);

    void Stop();
}

/// <summary>
/// Thin <see cref="FileSystemWatcher"/> adapter. Deliberately not unit tested:
/// the real watcher quirks it exposes (8.3 short name matching on the extension
/// filter, editor companion files) are defended against by the interpretation
/// rules in <see cref="WatchAreaFilterProfileStore"/>, which are.
/// </summary>
internal sealed class WatchAreaProfileDirectoryWatcher : IWatchAreaProfileDirectoryEventSource
{
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public event EventHandler<WatchAreaProfileDirectoryEvent>? Raised;

    public event EventHandler<WatchAreaProfileDirectoryFailure>? Failed;

    public void Start(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watcher is not null)
        {
            return;
        }

        var watcher = new FileSystemWatcher(directoryPath)
        {
            Filter = $"*{WatchAreaFilterProfileStore.ProfileExtension}",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.Attributes,
        };
        watcher.Created += (_, e) =>
            Raise(WatchAreaProfileDirectoryEventKind.Created, e.Name);
        watcher.Changed += (_, e) =>
            Raise(WatchAreaProfileDirectoryEventKind.Changed, e.Name);
        watcher.Deleted += (_, e) =>
            Raise(WatchAreaProfileDirectoryEventKind.Deleted, e.Name);
        watcher.Renamed += (_, e) =>
            Raise(WatchAreaProfileDirectoryEventKind.Renamed, e.Name, e.OldName);
        watcher.Error += (_, e) =>
            Failed?.Invoke(this, new WatchAreaProfileDirectoryFailure(e.GetException()));
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Raised = null;
        Failed = null;
        Stop();
    }

    private void Raise(
        WatchAreaProfileDirectoryEventKind kind,
        string? fileName,
        string? previousFileName = null)
    {
        if (_disposed || fileName is null)
        {
            return;
        }

        Raised?.Invoke(this, new WatchAreaProfileDirectoryEvent(
            kind,
            fileName,
            previousFileName));
    }
}
