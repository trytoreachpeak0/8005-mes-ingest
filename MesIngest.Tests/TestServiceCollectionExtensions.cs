using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

internal static class TestServiceCollectionExtensions
{
    public static IWebHostBuilder UseProductionSqlApiTestHost(this IWebHostBuilder builder) =>
        builder
            .UseEnvironment(Environments.Production)
            .ConfigureTestServices(services => services.RemoveMesTaskUnionPollHostedService());

    /// <summary>
    /// The production host judges raw-evidence availability and retention against
    /// its <see cref="TimeProvider"/>. A test whose rounds carry fixed fixture dates
    /// must run the host on a clock inside that window, or it silently expires once
    /// the wall clock passes the fixture date plus
    /// <c>HistoryRetentionPolicy.RawObservationAvailabilityWindow</c>.
    /// </summary>
    public static IWebHostBuilder UseProductionSqlApiTestHost(
        this IWebHostBuilder builder,
        TimeProvider clock) =>
        builder
            .UseProductionSqlApiTestHost()
            .ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            });

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
