using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

internal static class TestServiceCollectionExtensions
{
    public static IWebHostBuilder UseProductionSqlApiTestHost(this IWebHostBuilder builder) =>
        builder
            .UseEnvironment(Environments.Production)
            .ConfigureTestServices(services => services.RemoveMesTaskUnionPollHostedService());

    public static void RemoveMesTaskUnionPollHostedService(this IServiceCollection services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(MesTaskUnionPollHostedService))
            {
                services.RemoveAt(index);
            }
        }
    }
}
