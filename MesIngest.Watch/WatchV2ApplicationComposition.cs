using System.IO;
using System.Windows.Interop;
using System.Windows.Media;

namespace MesIngest.Watch;

/// <summary>
/// Production composition root for the replacement Watch contract. Tests may
/// replace the V2 Host client, clock, and per-user file locations while still
/// exercising the same session, presenters, profile store, and WPF window.
/// </summary>
internal sealed class WatchV2ApplicationComposition : IDisposable
{
    private readonly WatchOptions _options;
    private readonly Func<WatchHostSettings, IWatchV2ApiClient>? _clientFactory;
    private readonly string _connectionPreferencesPath;
    private readonly string _workspacePreferencesPath;
    private readonly string _areaFilterProfilesDirectoryPath;
    private readonly TimeProvider? _timeProvider;
    private WatchWorkspaceWindow? _window;
    private bool _disposed;

    private WatchV2ApplicationComposition(
        WatchOptions options,
        Func<WatchHostSettings, IWatchV2ApiClient>? clientFactory,
        string? connectionPreferencesPath,
        string? workspacePreferencesPath,
        TimeProvider? timeProvider,
        string? areaFilterProfilesDirectoryPath)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clientFactory = clientFactory;
        _connectionPreferencesPath = connectionPreferencesPath
            ?? WatchConnectionPreferencesStore.DefaultFilePath;
        _workspacePreferencesPath = workspacePreferencesPath
            ?? WatchV2PreferencesStore.DefaultFilePath;
        _areaFilterProfilesDirectoryPath = areaFilterProfilesDirectoryPath
            ?? (workspacePreferencesPath is null
                ? WatchAreaFilterProfileStore.DefaultDirectoryPath
                : Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(_workspacePreferencesPath))
                        ?? throw new ArgumentException(
                            "工作区首选项路径必须包含父目录。",
                            nameof(workspacePreferencesPath)),
                    "area-filters"));
        _timeProvider = timeProvider;

        if (_options.RenderingMode == WatchRenderingMode.SoftwareOnly)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
    }

    internal static WatchV2ApplicationComposition Create(
        WatchOptions options,
        Func<WatchHostSettings, IWatchV2ApiClient>? clientFactory = null,
        string? connectionPreferencesPath = null,
        string? workspacePreferencesPath = null,
        TimeProvider? timeProvider = null,
        string? areaFilterProfilesDirectoryPath = null) =>
        new(
            options,
            clientFactory,
            connectionPreferencesPath,
            workspacePreferencesPath,
            timeProvider,
            areaFilterProfilesDirectoryPath);

    internal WatchWorkspaceWindow CreateMainWindow(bool initializeOnLoaded = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is not null)
        {
            throw new InvalidOperationException("The production composition owns one Watch window.");
        }

        var connection = WatchConnectionPreferencesStore.Load(
            _connectionPreferencesPath,
            WatchConnectionPreferences.FromOptions(_options));
        var hostSettings = new WatchHostSettings(
            connection.BaseUrl,
            _options.SharedSecret,
            connection.RequestTimeoutSeconds);
        var preferences = WatchV2PreferencesStore.Load(_workspacePreferencesPath);
        _window = new WatchWorkspaceWindow(
            hostSettings,
            preferences,
            _connectionPreferencesPath,
            _workspacePreferencesPath,
            _clientFactory,
            _timeProvider,
            initializeOnLoaded,
            _areaFilterProfilesDirectoryPath);
        return _window;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window?.Dispose();
        _window = null;
    }
}
