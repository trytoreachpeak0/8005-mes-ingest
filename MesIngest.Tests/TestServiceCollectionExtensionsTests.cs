using MesIngest.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

public sealed class TestServiceCollectionExtensionsTests
{
    [Fact]
    public void RemoveMesTaskUnionPollHostedService_removes_only_the_poll_loop_and_preserves_other_hosted_services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostedService, NewMesIngestHostSessionService>();
        services.AddSingleton<IHostedService, MesTaskUnionPollHostedService>();
        services.AddSingleton<IHostedService, HistoryCleanupHostedService>();

        services.RemoveMesTaskUnionPollHostedService();

        var hostedServiceTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .ToArray();
        Assert.Equal(
            [typeof(NewMesIngestHostSessionService), typeof(HistoryCleanupHostedService)],
            hostedServiceTypes);
        Assert.DoesNotContain(typeof(MesTaskUnionPollHostedService), hostedServiceTypes);
    }
}
