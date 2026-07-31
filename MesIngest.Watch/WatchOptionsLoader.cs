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

        if (options.RequestTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException(
                $"Watch:RequestTimeoutSeconds must be between 1 and 300; got {options.RequestTimeoutSeconds}.");
        }

        if (options.ConnectionLogRetentionDays < 1)
        {
            throw new InvalidOperationException(
                $"Watch:ConnectionLogRetentionDays must be >= 1; got {options.ConnectionLogRetentionDays}.");
        }

        if (options.ConnectionLogMaxSizeMb < 1)
        {
            throw new InvalidOperationException(
                $"Watch:ConnectionLogMaxSizeMb must be >= 1; got {options.ConnectionLogMaxSizeMb}.");
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
