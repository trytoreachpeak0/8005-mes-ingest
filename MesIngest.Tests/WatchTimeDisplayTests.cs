using MesIngest.Watch;

namespace MesIngest.Tests;

public class WatchTimeDisplayTests
{
    private static TimeZoneInfo ResolveTz(string windowsId, string ianaId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
    }

    [Fact]
    public void Formats_utc_instant_in_beijing_without_dst_shift()
    {
        var beijing = ResolveTz("China Standard Time", "Asia/Shanghai");
        var utc = new DateTimeOffset(2026, 7, 15, 2, 30, 45, TimeSpan.Zero);

        var text = WatchTimeDisplay.Format(utc, beijing);

        Assert.Equal("2026-07-15 10:30:45 +08:00", text);
    }

    [Fact]
    public void Formats_pacific_summer_time_with_dst_offset()
    {
        var pacific = ResolveTz("Pacific Standard Time", "America/Los_Angeles");
        // 2026-07-15 18:00 UTC → 11:00 PDT (UTC-07)
        var utc = new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero);

        var text = WatchTimeDisplay.Format(utc, pacific);

        Assert.Equal("2026-07-15 11:00:00 -07:00", text);
    }

    [Fact]
    public void Formats_pacific_standard_time_without_dst()
    {
        var pacific = ResolveTz("Pacific Standard Time", "America/Los_Angeles");
        // 2026-01-15 18:00 UTC → 10:00 PST (UTC-08)
        var utc = new DateTimeOffset(2026, 1, 15, 18, 0, 0, TimeSpan.Zero);

        var text = WatchTimeDisplay.Format(utc, pacific);

        Assert.Equal("2026-01-15 10:00:00 -08:00", text);
    }

    [Fact]
    public void Formats_nullable_null_as_empty()
    {
        Assert.Equal(string.Empty, WatchTimeDisplay.FormatNullable(null));
    }

    [Fact]
    public void Preserves_instant_when_source_offset_differs_from_display_zone()
    {
        var beijing = ResolveTz("China Standard Time", "Asia/Shanghai");
        // Same instant as 2026-08-01 10:00 +08:00
        var storedAsUtc = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero);

        var text = WatchTimeDisplay.Format(storedAsUtc, beijing);

        Assert.Equal("2026-08-01 10:00:00 +08:00", text);
    }
}
