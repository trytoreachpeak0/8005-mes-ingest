using MesIngest.Watch;
using Microsoft.Extensions.Configuration;

namespace MesIngest.Tests;

public class WatchOptionsLoaderTests
{
    [Fact]
    public void Flat_MesIngestWatch_env_keys_override_Watch_section_BaseUrl()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Watch:BaseUrl"] = "http://127.0.0.1:5088",
                ["Watch:RefreshSeconds"] = "2",
                ["Watch:SharedSecret"] = "",
                // AddEnvironmentVariables(prefix: "MesIngestWatch__") strips the prefix,
                // so MesIngestWatch__BaseUrl lands as root "BaseUrl".
                ["BaseUrl"] = "http://10.0.0.5:5088",
            })
            .Build();

        var options = WatchOptionsLoader.Load(config);

        Assert.Equal("http://10.0.0.5:5088", options.BaseUrl);
    }

    [Fact]
    public void Flat_env_keys_override_RefreshSeconds_and_SharedSecret()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Watch:BaseUrl"] = "http://127.0.0.1:5088",
                ["Watch:RefreshSeconds"] = "2",
                ["Watch:SharedSecret"] = "from-json",
                ["RefreshSeconds"] = "5",
                ["SharedSecret"] = "from-env",
            })
            .Build();

        var options = WatchOptionsLoader.Load(config);

        Assert.Equal(5, options.RefreshSeconds);
        Assert.Equal("from-env", options.SharedSecret);
    }

    [Fact]
    public void RefreshSeconds_below_one_normalizes_to_two_once()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Watch:RefreshSeconds"] = "0",
            })
            .Build();

        var options = WatchOptionsLoader.Load(config);

        Assert.Equal(2, options.RefreshSeconds);
    }

    [Fact]
    public void Documented_MesIngestWatch_prefix_env_overrides_BaseUrl()
    {
        var key = WatchOptionsLoader.EnvPrefix + "BaseUrl";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "http://192.168.1.9:5088");

            var options = WatchOptionsLoader.Load(WatchOptionsLoader.BuildDefault());

            Assert.Equal("http://192.168.1.9:5088", options.BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }
}
