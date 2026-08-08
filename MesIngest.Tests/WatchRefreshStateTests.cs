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

    [Fact]
    public void Partial_success_updates_last_success_but_keeps_failing_endpoint_error()
    {
        var firstSuccess = DateTimeOffset.Parse("2026-07-31T10:00:00Z");
        var pageSuccess = DateTimeOffset.Parse("2026-07-31T10:01:00Z");
        var state = WatchRefreshState.Empty
            .ApplySuccess(firstSuccess)
            .ApplyFailure("endpoint=/api/alerts stage=HTTP_CONNECT timeoutSeconds=30 elapsedMs=12")
            .ApplyPartialSuccess(pageSuccess);

        Assert.Equal(pageSuccess, state.LastSuccessAt);
        Assert.Contains("/api/alerts", state.FetchError, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_context_keeps_transport_details_with_last_success_and_staleness()
    {
        var successAt = DateTimeOffset.Parse("2026-08-08T01:00:00Z");
        var failAt = DateTimeOffset.Parse("2026-08-08T01:02:05Z");
        var state = WatchRefreshState.Empty.ApplySuccess(successAt);

        var text = state.FormatFailure(
            "endpoint=/api/alerts stage=WATCH_TIMEOUT timeoutSeconds=30 elapsedMs=30000 correlationId=c-9",
            failAt);

        Assert.Contains("endpoint=/api/alerts", text, StringComparison.Ordinal);
        Assert.Contains("elapsedMs=30000", text, StringComparison.Ordinal);
        Assert.Contains("correlationId=c-9", text, StringComparison.Ordinal);
        Assert.Contains("lastSuccess=", text, StringComparison.Ordinal);
        Assert.Contains("stale=2m5s", text, StringComparison.Ordinal);
    }
}
