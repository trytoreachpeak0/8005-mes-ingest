using System.IO;
using System.Windows.Interop;
using System.Windows.Media;
using MesIngest.Core;

namespace MesIngest.Watch;

/// <summary>
/// Legacy V1 composition retained temporarily for pre-ticket-19 regression
/// fixtures. <see cref="App"/> no longer references this type; production starts
/// exclusively through <see cref="WatchV2ApplicationComposition"/>.
/// </summary>
internal sealed class WatchApplicationComposition : IDisposable
{
    private readonly WatchOptions _options;
    private readonly WatchLocalLogDispatcher _logDispatcher;
    private readonly WatchTelemetryIoDiagnosticBuffer _telemetryIoDiagnostics;
    private readonly WatchConnectionEventJournal _journal;
    private readonly WatchLatencyFileTelemetry _telemetry;
    private readonly Func<WatchHostSettings, IWatchHostQueryAdapter> _hostAdapterFactory;
    private readonly string? _layoutPreferencesPath;
    private readonly string? _autoRefreshPreferencesPath;
    private readonly string? _connectionPreferencesPath;
    private readonly TimeProvider? _timeProvider;
    private bool _disposed;

    private WatchApplicationComposition(
        WatchOptions options,
        Func<WatchHostSettings, IWatchHostQueryAdapter>? hostAdapterFactory,
        string? logDirectory,
        string? layoutPreferencesPath,
        string? autoRefreshPreferencesPath,
        string? connectionPreferencesPath,
        TimeProvider? timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _layoutPreferencesPath = layoutPreferencesPath;
        _autoRefreshPreferencesPath = autoRefreshPreferencesPath;
        _connectionPreferencesPath = connectionPreferencesPath;
        _timeProvider = timeProvider;

        if (_options.RenderingMode == WatchRenderingMode.SoftwareOnly)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        _logDispatcher = new WatchLocalLogDispatcher();
        _telemetryIoDiagnostics = new WatchTelemetryIoDiagnosticBuffer();
        var resolvedLogDirectory = ResolveLogDirectory(_options, logDirectory);
        _journal = WatchConnectionEventJournal.FromOptions(
            _options,
            directory: resolvedLogDirectory,
            onWriteFailure: ex => _telemetryIoDiagnostics.Record("watch-connection", ex),
            dispatcher: _logDispatcher);
        _telemetry = WatchLatencyFileTelemetry.FromOptions(
            _options,
            directory: resolvedLogDirectory,
            onWriteFailure: ex => _telemetryIoDiagnostics.Record("watch-latency", ex),
            dispatcher: _logDispatcher);
        _hostAdapterFactory = hostAdapterFactory
            ?? (settings => MesIngestApiClient.CreateForHost(settings, _telemetry));
    }

    public static WatchApplicationComposition Create(
        WatchOptions options,
        Func<WatchHostSettings, IWatchHostQueryAdapter>? hostAdapterFactory = null,
        string? logDirectory = null,
        string? layoutPreferencesPath = null,
        string? autoRefreshPreferencesPath = null,
        string? connectionPreferencesPath = null,
        TimeProvider? timeProvider = null) =>
        new(
            options,
            hostAdapterFactory,
            logDirectory,
            layoutPreferencesPath,
            autoRefreshPreferencesPath,
            connectionPreferencesPath,
            timeProvider);

    public MainWindow CreateMainWindow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connectionPreferencesPath = _connectionPreferencesPath
            ?? (_layoutPreferencesPath is null
                ? WatchConnectionPreferencesStore.DefaultFilePath
                : Path.ChangeExtension(_layoutPreferencesPath, ".connection.json"));
        var connectionPreferences = WatchConnectionPreferencesStore.Load(
            connectionPreferencesPath,
            WatchConnectionPreferences.FromOptions(_options));
        _options.BaseUrl = connectionPreferences.BaseUrl;
        _options.RequestTimeoutSeconds = connectionPreferences.RequestTimeoutSeconds;
        var initialSettings = new WatchHostSettings(
            _options.BaseUrl,
            _options.SharedSecret,
            _options.RequestTimeoutSeconds);
        var client = MesIngestApiClient.CreateForHost(initialSettings, _telemetry);
        return new MainWindow(
            client,
            _options,
            _journal,
            layoutPreferencesPath: _layoutPreferencesPath,
            autoRefreshPreferencesPath: _autoRefreshPreferencesPath,
            connectionPreferencesPath: connectionPreferencesPath,
            telemetryIoDiagnostics: _telemetryIoDiagnostics,
            hostAdapterFactory: _hostAdapterFactory,
            timeProvider: _timeProvider);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logDispatcher.Dispose();
    }

    private static string? ResolveLogDirectory(WatchOptions options, string? overrideDirectory)
    {
        var configured = overrideDirectory ?? options.LogDirectory;
        return string.IsNullOrWhiteSpace(configured)
            ? null
            : Path.GetFullPath(configured);
    }
}
