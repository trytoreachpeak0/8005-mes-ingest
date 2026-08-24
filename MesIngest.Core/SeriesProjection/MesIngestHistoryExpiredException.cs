namespace MesIngest.Core.SeriesProjection;

public sealed class MesIngestHistoryExpiredException : Exception
{
    public MesIngestHistoryExpiredException(HistoricalReadBoundary boundary)
        : base("The requested MesIngest history is no longer available.")
    {
        Boundary = boundary;
    }

    public HistoricalReadBoundary Boundary { get; }
}
