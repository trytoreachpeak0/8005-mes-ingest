using MesIngest.Core.SeriesProjection;

namespace MesIngest.Host;

internal sealed class NewMesIngestHostSessionService : IHostedService
{
    private readonly IMesIngestProjection _projection;

    public NewMesIngestHostSessionService(IMesIngestProjection projection)
    {
        _projection = projection;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _projection.BeginHostSessionAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
