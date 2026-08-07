using System.IO;
using System.Windows.Interop;
using System.Windows.Media;
using MesIngest.Core;

namespace MesIngest.Watch;

/// <summary>
/// Production composition root for the Watch desktop application. UI tests use this
/// same root and replace only the Host boundary adapter.
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
    private bool _disposed;

    private WatchApplicationComposition(
        WatchOptions options,
        Func<WatchHostSettings, IWatchHostQueryAdapter>? hostAdapterFactory,
        string? logDirectory,
        string? layoutPreferencesPath)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _layoutPreferencesPath = layoutPreferencesPath;

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
        string? layoutPreferencesPath = null) =>
        new(options, hostAdapterFactory, logDirectory, layoutPreferencesPath);

    public MainWindow CreateMainWindow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
            telemetryIoDiagnostics: _telemetryIoDiagnostics,
            hostAdapterFactory: _hostAdapterFactory);
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
