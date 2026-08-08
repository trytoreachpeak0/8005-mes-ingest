using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 09 seam: WatchLayoutPreferences — Demand/Alert pane ratio
/// defaults, bounds, corrupt/version recovery, and local file round-trip.
/// </summary>
public class WatchLayoutPreferencesTests
{
    [Fact]
    public void Default_demand_share_is_seventy_percent()
    {
        Assert.Equal(0.7, WatchLayoutPreferences.DefaultDemandShare);
    }

    [Fact]
    public void Normalize_accepts_valid_demand_share()
    {
        Assert.Equal(0.55, WatchLayoutPreferences.NormalizeDemandShare(0.55));
    }

    [Fact]
    public void Normalize_rejects_out_of_range_and_returns_default()
    {
        Assert.Equal(0.7, WatchLayoutPreferences.NormalizeDemandShare(0));
        Assert.Equal(0.7, WatchLayoutPreferences.NormalizeDemandShare(1));
        Assert.Equal(0.7, WatchLayoutPreferences.NormalizeDemandShare(-0.1));
        Assert.Equal(0.7, WatchLayoutPreferences.NormalizeDemandShare(1.5));
        Assert.Equal(0.7, WatchLayoutPreferences.NormalizeDemandShare(double.NaN));
        Assert.Equal(0.7, WatchLayoutPreferences.NormalizeDemandShare(double.PositiveInfinity));
    }

    [Fact]
    public void Load_missing_file_returns_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-layout-{Guid.NewGuid():N}.json");

        var share = WatchLayoutPreferences.LoadDemandShare(path);

        Assert.Equal(0.7, share);
    }

    [Fact]
    public void Load_corrupt_json_returns_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-layout-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");

        try
        {
            Assert.Equal(0.7, WatchLayoutPreferences.LoadDemandShare(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_incompatible_version_returns_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-layout-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"version":99,"demandShare":0.4}""");

        try
        {
            Assert.Equal(0.7, WatchLayoutPreferences.LoadDemandShare(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_out_of_range_share_returns_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-layout-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"version":1,"demandShare":1.2}""");

        try
        {
            Assert.Equal(0.7, WatchLayoutPreferences.LoadDemandShare(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_then_load_round_trips_demand_share()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-layout-{Guid.NewGuid():N}.json");

        try
        {
            WatchLayoutPreferences.SaveDemandShare(path, 0.62);
            Assert.Equal(0.62, WatchLayoutPreferences.LoadDemandShare(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_normalizes_out_of_range_to_default_before_write()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-layout-{Guid.NewGuid():N}.json");

        try
        {
            WatchLayoutPreferences.SaveDemandShare(path, 2.0);
            Assert.Equal(0.7, WatchLayoutPreferences.LoadDemandShare(path));
            var text = File.ReadAllText(path);
            Assert.Contains("\"demandShare\":0.7", text.Replace(" ", ""));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Default_file_path_is_under_local_app_data_not_host_config()
    {
        var path = WatchLayoutPreferences.DefaultFilePath;
        Assert.Contains("MesIngest.Watch", path);
        Assert.Contains("layout-preferences.json", path);
        Assert.DoesNotContain("appsettings", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Star_heights_from_demand_share_sum_to_one()
    {
        var (demand, alert) = WatchLayoutPreferences.ToStarHeights(0.7);
        Assert.Equal(0.7, demand);
        Assert.Equal(0.3, alert, precision: 10);
        Assert.Equal(1.0, demand + alert, precision: 10);
    }

    [Fact]
    public void Window_size_and_detail_share_round_trip_as_one_versioned_layout()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-layout-{Guid.NewGuid():N}.json");
        var expected = new WatchWindowLayout(1440, 900, 0.58);

        try
        {
            WatchLayoutPreferences.Save(path, expected);

            Assert.Equal(expected, WatchLayoutPreferences.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{\"version\":99,\"windowWidth\":1440,\"windowHeight\":900,\"demandShare\":0.6}")]
    [InlineData("{\"version\":1,\"windowWidth\":200,\"windowHeight\":900,\"demandShare\":0.6}")]
    [InlineData("{\"version\":1,\"windowWidth\":1440,\"windowHeight\":9000,\"demandShare\":0.6}")]
    [InlineData("{\"version\":1,\"windowWidth\":1440,\"windowHeight\":900,\"demandShare\":1.5}")]
    public void Incompatible_or_out_of_bounds_layout_falls_back_atomically(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-layout-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        try
        {
            Assert.Equal(WatchWindowLayout.Default, WatchLayoutPreferences.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
