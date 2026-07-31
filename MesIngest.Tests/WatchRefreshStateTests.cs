using MesIngest.Watch;

namespace MesIngest.Tests;

public class WatchRefreshStateTests
{
    [Fact]
    public void Successful_refresh_updates_last_success_and_clears_stale()
    {
        var now = DateTimeOffset.Parse("2026-07-31T10:00:00Z");
        var state = WatchRefreshState.Empty;

        state = state.ApplySuccess(now);

        Assert.Equal(now, state.LastSuccessAt);
        Assert.Null(state.FetchError);
        Assert.Equal(TimeSpan.Zero, state.StaleDuration(now));
    }

    [Fact]
    public void Failure_keeps_last_success_and_computes_stale_duration()
    {
        var successAt = DateTimeOffset.Parse("2026-07-31T10:00:00Z");
        var failAt = DateTimeOffset.Parse("2026-07-31T10:02:30Z");
        var state = WatchRefreshState.Empty
            .ApplySuccess(successAt)
            .ApplyFailure("endpoint=/api/demands stage=WATCH_TIMEOUT timeoutSeconds=30 elapsedMs=30000");

        Assert.Equal(successAt, state.LastSuccessAt);
        Assert.Equal(TimeSpan.FromMinutes(2.5), state.StaleDuration(failAt));
        Assert.Contains("/api/demands", state.FetchError, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_before_any_success_has_null_last_success_and_null_stale()
    {
        var failAt = DateTimeOffset.Parse("2026-07-31T10:00:00Z");
        var state = WatchRefreshState.Empty.ApplyFailure("Connection refused");

        Assert.Null(state.LastSuccessAt);
        Assert.Null(state.StaleDuration(failAt));
        Assert.Equal("Connection refused", state.FetchError);
    }

    [Fact]
    public void Format_includes_last_success_and_stale_when_available()
    {
        var successAt = DateTimeOffset.Parse("2026-07-31T02:00:00Z");
        var now = DateTimeOffset.Parse("2026-07-31T02:05:00Z");
        var state = WatchRefreshState.Empty.ApplySuccess(successAt).ApplyFailure("timeout");

        var text = state.FormatWatchRefreshLine(now);

        Assert.Contains("lastSuccess=", text, StringComparison.Ordinal);
        Assert.Contains("stale=", text, StringComparison.Ordinal);
        Assert.Contains("5m", text, StringComparison.Ordinal);
    }
}
