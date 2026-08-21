namespace MesIngest.Watch;

internal sealed class WatchDemandSeriesGenerationFocusRequestedEventArgs(
    string demandId) : EventArgs
{
    public string DemandId { get; } = string.IsNullOrWhiteSpace(demandId)
        ? throw new ArgumentException("DemandId is required.", nameof(demandId))
        : demandId;
}

internal interface IWatchDemandSeriesInspectorWindow
{
    event EventHandler? Closed;

    event EventHandler<WatchDemandSeriesGenerationFocusRequestedEventArgs>?
        GenerationFocusRequested;

    bool IsVisible { get; }

    void Update(WatchDemandSeriesInspectorPresentation presentation);

    void Show();

    bool Activate();

    void Close();
}

internal sealed class WatchDemandSeriesInspectorCoordinator : IDisposable
{
    private readonly Func<IWatchDemandSeriesInspectorWindow> _windowFactory;
    private IWatchDemandSeriesInspectorWindow? _window;
    private bool _disposed;

    internal WatchDemandSeriesInspectorCoordinator(
        Func<IWatchDemandSeriesInspectorWindow>? windowFactory = null)
    {
        _windowFactory = windowFactory ?? (() => new WatchDemandSeriesInspectorWindow());
    }

    internal bool IsOpen => _window is not null;

    internal IWatchDemandSeriesInspectorWindow? CurrentWindow => _window;

    internal event EventHandler? StateChanged;

    internal event EventHandler<WatchDemandSeriesGenerationFocusRequestedEventArgs>?
        GenerationFocusRequested;

    internal void OpenOrShow(WatchDemandSeriesInspectorPresentation presentation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(presentation);

        var window = _window ?? CreateWindow();
        window.Update(presentation);
        if (!window.IsVisible)
        {
            window.Show();
        }

        window.Activate();
    }

    internal void Update(WatchDemandSeriesInspectorPresentation presentation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(presentation);
        _window?.Update(presentation);
    }

    internal bool ShowExisting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is not { } window)
        {
            return false;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        window.Activate();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var window = _window;
        if (window is null)
        {
            return;
        }

        Detach(window);
        _window = null;
        window.Close();
    }

    private IWatchDemandSeriesInspectorWindow CreateWindow()
    {
        var window = _windowFactory();
        window.Closed += OnWindowClosed;
        window.GenerationFocusRequested += OnGenerationFocusRequested;
        _window = window;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return window;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not IWatchDemandSeriesInspectorWindow window
            || !ReferenceEquals(window, _window))
        {
            return;
        }

        Detach(window);
        _window = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnGenerationFocusRequested(
        object? sender,
        WatchDemandSeriesGenerationFocusRequestedEventArgs e) =>
        GenerationFocusRequested?.Invoke(this, e);

    private void Detach(IWatchDemandSeriesInspectorWindow window)
    {
        window.Closed -= OnWindowClosed;
        window.GenerationFocusRequested -= OnGenerationFocusRequested;
    }
}
