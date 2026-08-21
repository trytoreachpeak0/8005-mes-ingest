using MesIngest.Watch;

namespace MesIngest.Tests;

internal sealed class RecordingDemandSeriesInspectorWindow :
    IWatchDemandSeriesInspectorWindow
{
    public event EventHandler? Closed;

    public event EventHandler<WatchDemandSeriesGenerationFocusRequestedEventArgs>?
        GenerationFocusRequested;

    public bool IsVisible { get; private set; }

    public int ShowCount { get; private set; }

    public int ActivateCount { get; private set; }

    public WatchDemandSeriesInspectorPresentation? Presentation { get; private set; }

    public void Update(WatchDemandSeriesInspectorPresentation presentation) =>
        Presentation = presentation;

    public void Show()
    {
        IsVisible = true;
        ShowCount++;
    }

    public bool Activate()
    {
        ActivateCount++;
        return true;
    }

    public void Close() => SimulateClose();

    public void SimulateClose()
    {
        IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void RequestGeneration(string demandId) => GenerationFocusRequested?.Invoke(
        this,
        new WatchDemandSeriesGenerationFocusRequestedEventArgs(demandId));
}
