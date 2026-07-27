using Microsoft.Extensions.Configuration;

namespace MesIngest.Watch;

/// <summary>
/// Loads <see cref="WatchOptions"/> from the <c>Watch</c> JSON section, then overlays
/// flat root keys produced by <c>AddEnvironmentVariables(prefix: "MesIngestWatch__")</c>
/// (e.g. <c>MesIngestWatch__BaseUrl</c> → <c>BaseUrl</c>).
/// </summary>
internal static class WatchOptionsLoader
{
    public const string EnvPrefix = "MesIngestWatch__";
    public const string SectionName = "Watch";

    public static WatchOptions Load(IConfiguration config)
    {
        var options = new WatchOptions();
        config.GetSection(SectionName).Bind(options);
        // Flat MesIngestWatch__* keys land at the configuration root after prefix strip.
        config.Bind(options);

        if (options.RefreshSeconds < 1)
        {
            options.RefreshSeconds = 2;
        }

        return options;
    }

    public static IConfiguration BuildDefault()
    {
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: EnvPrefix)
            .Build();
    }
}
