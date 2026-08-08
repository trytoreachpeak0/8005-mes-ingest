using MesIngest.Watch;

namespace MesIngest.Tests;

public class WatchAutoRefreshScheduleTests
{
    [Fact]
    public void Defaults_keep_all_four_views_off_at_ten_seconds()
    {
        var preferences = WatchAutoRefreshPreferences.Default;

        foreach (var view in Enum.GetValues<WatchRefreshView>())
        {
            Assert.Equal(new WatchAutoRefreshSetting(false, 10), preferences.For(view));
        }
    }

    [Fact]
    public void Only_the_active_enabled_view_becomes_due_after_a_full_interval()
    {
        var clock = new AdjustableTimeProvider(
            DateTimeOffset.Parse("2026-08-08T10:00:00+08:00"));
        var preferences = WatchAutoRefreshPreferences.Default
            .With(WatchRefreshView.Visible, new WatchAutoRefreshSetting(true, 30))
            .With(WatchRefreshView.Alerts, new WatchAutoRefreshSetting(true, 10));
        var schedule = new WatchAutoRefreshSchedule(preferences, clock);

        Assert.True(schedule.Activate(WatchRefreshView.Visible));
        var request = schedule.BeginRequest(WatchRefreshView.Visible);
        schedule.EndRequest(WatchRefreshView.Visible, request);

        clock.SetUtcNow(DateTimeOffset.Parse("2026-08-08T10:00:29+08:00"));
        Assert.False(schedule.TryTakeDue(WatchRefreshView.Visible, requestInProgress: false));
        Assert.False(schedule.TryTakeDue(WatchRefreshView.Alerts, requestInProgress: false));

        clock.SetUtcNow(DateTimeOffset.Parse("2026-08-08T10:00:30+08:00"));
        Assert.True(schedule.TryTakeDue(WatchRefreshView.Visible, requestInProgress: false));
    }

    [Fact]
    public void Busy_due_tick_is_skipped_and_request_completion_restarts_the_interval()
    {
        var clock = new AdjustableTimeProvider(
            DateTimeOffset.Parse("2026-08-08T10:00:00+08:00"));
        var preferences = WatchAutoRefreshPreferences.Default
            .With(WatchRefreshView.Overview, new WatchAutoRefreshSetting(true, 10));
        var schedule = new WatchAutoRefreshSchedule(preferences, clock);
        Assert.True(schedule.Activate(WatchRefreshView.Overview));
        var request = schedule.BeginRequest(WatchRefreshView.Overview);
        schedule.EndRequest(WatchRefreshView.Overview, request);

        clock.SetUtcNow(DateTimeOffset.Parse("2026-08-08T10:00:10+08:00"));
        Assert.False(schedule.TryTakeDue(WatchRefreshView.Overview, requestInProgress: true));

        var manual = schedule.BeginRequest(WatchRefreshView.Overview);
        clock.SetUtcNow(DateTimeOffset.Parse("2026-08-08T10:00:14+08:00"));
        schedule.EndRequest(WatchRefreshView.Overview, manual);
        clock.SetUtcNow(DateTimeOffset.Parse("2026-08-08T10:00:23+08:00"));
        Assert.False(schedule.TryTakeDue(WatchRefreshView.Overview, requestInProgress: false));
        clock.SetUtcNow(DateTimeOffset.Parse("2026-08-08T10:00:24+08:00"));
        Assert.True(schedule.TryTakeDue(WatchRefreshView.Overview, requestInProgress: false));
    }

    [Fact]
    public void Switching_back_to_an_enabled_view_requests_an_immediate_refresh()
    {
        var preferences = WatchAutoRefreshPreferences.Default
            .With(WatchRefreshView.Gone, new WatchAutoRefreshSetting(true, 60));
        var schedule = new WatchAutoRefreshSchedule(preferences);

        Assert.False(schedule.Activate(WatchRefreshView.Visible));
        Assert.True(schedule.Activate(WatchRefreshView.Gone));
        schedule.Deactivate();
        Assert.True(schedule.Activate(WatchRefreshView.Gone));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(301)]
    public void Unsupported_intervals_are_rejected(int intervalSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WatchAutoRefreshSetting(true, intervalSeconds));
    }

    [Fact]
    public void Preferences_round_trip_each_view_independently()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"watch-refresh-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "auto-refresh.json");
        try
        {
            var expected = WatchAutoRefreshPreferences.Default
                .With(WatchRefreshView.Overview, new WatchAutoRefreshSetting(true, 10))
                .With(WatchRefreshView.Visible, new WatchAutoRefreshSetting(true, 30))
                .With(WatchRefreshView.Gone, new WatchAutoRefreshSetting(false, 60))
                .With(WatchRefreshView.Alerts, new WatchAutoRefreshSetting(true, 300));

            WatchAutoRefreshPreferencesStore.Save(path, expected);

            Assert.Equal(expected, WatchAutoRefreshPreferencesStore.Load(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Missing_or_invalid_preferences_fall_back_to_safe_defaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"watch-refresh-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "auto-refresh.json");
        try
        {
            Assert.Equal(WatchAutoRefreshPreferences.Default, WatchAutoRefreshPreferencesStore.Load(path));

            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ \"overview\": { \"enabled\": true, \"intervalSeconds\": 11 } }");

            Assert.Equal(WatchAutoRefreshPreferences.Default, WatchAutoRefreshPreferencesStore.Load(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Incompatible_auto_refresh_version_falls_back_to_safe_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-refresh-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """{"version":99,"overview":{"enabled":true,"intervalSeconds":10},"visible":{"enabled":true,"intervalSeconds":10},"gone":{"enabled":true,"intervalSeconds":10},"alerts":{"enabled":true,"intervalSeconds":10}}""");

        try
        {
            Assert.Equal(WatchAutoRefreshPreferences.Default, WatchAutoRefreshPreferencesStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
