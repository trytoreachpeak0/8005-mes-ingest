using System.Text;
using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Storage seam for the live directory sync: a real temporary directory, an
/// injected clock, and a manually driven event source. No test here waits on
/// real time.
/// </summary>
public sealed class WatchAreaFilterProfileDirectoryWatchTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_profile_file_reaches_the_directory_change_once_the_debounce_window_closes()
    {
        using var directory = new TemporaryProfileDirectory();
        var clock = new ManualTimerTimeProvider(StartedAt);
        var events = new ManualAreaProfileDirectoryEventSource();
        using var store = new WatchAreaFilterProfileStore(
            directory.Path,
            clock,
            directoryEventSource: events);
        var changes = new List<WatchAreaProfileDirectoryChange>();
        store.DirectoryChanged += (_, change) => changes.Add(change);
        store.StartWatchingDirectory();

        directory.WriteProfile("西区", "A1-1\nA1-2");
        events.RaiseCreated("西区.txt");

        Assert.Empty(changes);

        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

        var change = Assert.Single(changes);
        Assert.Equal(["西区"], change.ProfileNames);
        Assert.Equal(
            ["西区"],
            store.EnumerateProfiles().Select(summary => summary.ProfileName));
    }

    [Fact]
    public void A_deleted_profile_file_reaches_the_directory_change_and_leaves_the_listing()
    {
        using var directory = new TemporaryProfileDirectory();
        var clock = new ManualTimerTimeProvider(StartedAt);
        var events = new ManualAreaProfileDirectoryEventSource();
        directory.WriteProfile("焊线区域", "B2-2");
        using var store = new WatchAreaFilterProfileStore(
            directory.Path,
            clock,
            directoryEventSource: events);
        var changes = new List<WatchAreaProfileDirectoryChange>();
        store.DirectoryChanged += (_, change) => changes.Add(change);
        store.StartWatchingDirectory();
        Assert.Single(store.EnumerateProfiles());

        directory.DeleteProfile("焊线区域");
        events.RaiseDeleted("焊线区域.txt");
        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

        var change = Assert.Single(changes);
        Assert.Equal(["焊线区域"], change.ProfileNames);
        Assert.Empty(store.EnumerateProfiles());
    }

    [Fact]
    public void A_burst_of_copied_files_is_coalesced_into_one_directory_change()
    {
        using var directory = new TemporaryProfileDirectory();
        var clock = new ManualTimerTimeProvider(StartedAt);
        var events = new ManualAreaProfileDirectoryEventSource();
        using var store = new WatchAreaFilterProfileStore(
            directory.Path,
            clock,
            directoryEventSource: events);
        var changes = new List<WatchAreaProfileDirectoryChange>();
        store.DirectoryChanged += (_, change) => changes.Add(change);
        store.StartWatchingDirectory();

        foreach (var profileName in new[] { "西区", "东区", "北区" })
        {
            directory.WriteProfile(profileName, "A1-1");
            events.RaiseCreated($"{profileName}.txt");
            events.RaiseChanged($"{profileName}.txt");
            clock.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.Empty(changes);

        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

        var change = Assert.Single(changes);
        Assert.Equal(["东区", "北区", "西区"], change.ProfileNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_continuous_stream_of_events_is_still_announced_instead_of_starving_the_window()
    {
        using var directory = new TemporaryProfileDirectory();
        var clock = new ManualTimerTimeProvider(StartedAt);
        var events = new ManualAreaProfileDirectoryEventSource();
        using var store = new WatchAreaFilterProfileStore(
            directory.Path,
            clock,
            directoryEventSource: events);
        var changes = new List<WatchAreaProfileDirectoryChange>();
        store.DirectoryChanged += (_, change) => changes.Add(change);
        store.StartWatchingDirectory();
        directory.WriteProfile("西区", "A1-1");

        // An editor that keeps rewriting the file must not push the window back
        // for as long as it keeps writing.
        for (var round = 0; round < 5; round++)
        {
            events.RaiseChanged("西区.txt");
            clock.Advance(
                WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow
                    - TimeSpan.FromMilliseconds(50));
        }

        Assert.True(
            changes.Count >= 2,
            $"a continuous stream produced {changes.Count} announcements");
        Assert.All(changes, change => Assert.Equal(["西区"], change.ProfileNames));
    }

    [Fact]
    public void A_failed_start_leaves_the_store_startable_again()
    {
        using var directory = new TemporaryProfileDirectory();
        var clock = new ManualTimerTimeProvider(StartedAt);
        var events = new ManualAreaProfileDirectoryEventSource { FailNextStart = true };
        using var store = new WatchAreaFilterProfileStore(
            directory.Path,
            clock,
            directoryEventSource: events);
        var changes = new List<WatchAreaProfileDirectoryChange>();
        store.DirectoryChanged += (_, change) => changes.Add(change);

        Assert.Throws<IOException>(store.StartWatchingDirectory);
        Assert.Null(events.StartedDirectoryPath);

        store.StartWatchingDirectory();
        Assert.Equal(directory.Path, events.StartedDirectoryPath);

        directory.WriteProfile("西区", "A1-1");
        events.RaiseCreated("西区.txt");
        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

        Assert.Equal(["西区"], Assert.Single(changes).ProfileNames);
    }

    [Fact]
    public void Repeated_events_for_one_file_raise_one_directory_change_per_window()
    {
        using var directory = new TemporaryProfileDirectory();
        var clock = new ManualTimerTimeProvider(StartedAt);
        var events = new ManualAreaProfileDirectoryEventSource();
        using var store = new WatchAreaFilterProfileStore(
            directory.Path,
            clock,
            directoryEventSource: events);
        var changes = new List<WatchAreaProfileDirectoryChange>();
        store.DirectoryChanged += (_, change) => changes.Add(change);
        store.StartWatchingDirectory();
        directory.WriteProfile("西区", "A1-1");

        events.RaiseCreated("西区.txt");
        events.RaiseChanged("西区.txt");
        events.RaiseChanged("西区.txt");
        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

        events.RaiseChanged("西区.txt");
        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal(["西区"], change.ProfileNames));
    }

    [Fact]
    public void Editor_companion_files_never_reach_the_directory_change()
    {
        using var directory = new TemporaryProfileDirectory();
        var clock = new ManualTimerTimeProvider(StartedAt);
        var events = new ManualAreaProfileDirectoryEventSource();
        using var store = new WatchAreaFilterProfileStore(
            directory.Path,
            clock,
            directoryEventSource: events);
        var changes = new List<WatchAreaProfileDirectoryChange>();
        store.DirectoryChanged += (_, change) => changes.Add(change);
        store.StartWatchingDirectory();

        // A dot file, a non-TXT extension, and the long name behind an 8.3 short
        // name that the watcher extension filter also matches.
        directory.WriteRawFile(".西区.txt.swp", "A1-1");
        directory.WriteRawFile("西区.txt.bak", "A1-1");
        directory.WriteRawFile("西区.txtbackup", "A1-1");
        events.RaiseCreated(".西区.txt.swp");
        events.RaiseCreated("西区.txt.bak");
        events.RaiseCreated("西区.txtbackup");
        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

        Assert.Empty(changes);
        Assert.Empty(store.EnumerateProfiles());
    }

    [Fact]
    public void Hidden_and_system_txt_files_are_neither_announced_nor_listed()
    {
        using var directory = new TemporaryProfileDirectory();
        var clock = new ManualTimerTimeProvider(StartedAt);
        var events = new ManualAreaProfileDirectoryEventSource();
        using var store = new WatchAreaFilterProfileStore(
            directory.Path,
            clock,
            directoryEventSource: events);
        var changes = new List<WatchAreaProfileDirectoryChange>();
        store.DirectoryChanged += (_, change) => changes.Add(change);
        store.StartWatchingDirectory();

        directory.WriteProfile("隐藏范围", "A1-1");
        directory.SetAttributes("隐藏范围.txt", FileAttributes.Hidden);
        directory.WriteProfile("系统范围", "A1-2");
        directory.SetAttributes("系统范围.txt", FileAttributes.System);
        events.RaiseCreated("隐藏范围.txt");
        events.RaiseCreated("系统范围.txt");
        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

        Assert.Empty(changes);
        Assert.Empty(store.EnumerateProfiles());
    }

    [Fact]
    public void A_rename_announces_both_the_old_and_the_new_profile_name()
    {
        using var directory = new TemporaryProfileDirectory();
        var clock = new ManualTimerTimeProvider(StartedAt);
        var events = new ManualAreaProfileDirectoryEventSource();
        directory.WriteProfile("旧名", "A1-1");
        using var store = new WatchAreaFilterProfileStore(
            directory.Path,
            clock,
            directoryEventSource: events);
        var changes = new List<WatchAreaProfileDirectoryChange>();
        store.DirectoryChanged += (_, change) => changes.Add(change);
        store.StartWatchingDirectory();

        directory.MoveProfile("旧名", "新名");
        events.RaiseRenamed("新名.txt", "旧名.txt");
        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

        var change = Assert.Single(changes);
        Assert.Equal(["新名", "旧名"], change.ProfileNames.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["新名"],
            store.EnumerateProfiles().Select(summary => summary.ProfileName));
    }

    [Fact]
    public void A_disposed_store_stops_the_event_source_and_announces_nothing_further()
    {
        using var directory = new TemporaryProfileDirectory();
        var clock = new ManualTimerTimeProvider(StartedAt);
        var events = new ManualAreaProfileDirectoryEventSource();
        var store = new WatchAreaFilterProfileStore(
            directory.Path,
            clock,
            directoryEventSource: events);
        var changes = new List<WatchAreaProfileDirectoryChange>();
        store.DirectoryChanged += (_, change) => changes.Add(change);
        store.StartWatchingDirectory();
        directory.WriteProfile("西区", "A1-1");
        events.RaiseCreated("西区.txt");

        store.Dispose();
        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
        events.RaiseCreated("西区.txt");
        clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

        Assert.Empty(changes);
        Assert.True(events.IsDisposed);
    }

    private sealed class TemporaryProfileDirectory : IDisposable
    {
        public TemporaryProfileDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mes-watch-area-watch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void WriteProfile(string profileName, string content) =>
            WriteRawFile($"{profileName}.txt", content);

        public void WriteRawFile(string fileName, string content) =>
            File.WriteAllText(
                System.IO.Path.Combine(Path, fileName),
                content,
                new UTF8Encoding(false));

        public void DeleteProfile(string profileName) =>
            File.Delete(System.IO.Path.Combine(Path, $"{profileName}.txt"));

        public void MoveProfile(string sourceProfileName, string destinationProfileName) =>
            File.Move(
                System.IO.Path.Combine(Path, $"{sourceProfileName}.txt"),
                System.IO.Path.Combine(Path, $"{destinationProfileName}.txt"));

        public void SetAttributes(string fileName, FileAttributes attributes) =>
            File.SetAttributes(System.IO.Path.Combine(Path, fileName), attributes);

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Path))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}
