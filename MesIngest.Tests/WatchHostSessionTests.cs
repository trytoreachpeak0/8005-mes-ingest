using MesIngest.Core;
using MesIngest.Watch;
using System.Net;

namespace MesIngest.Tests;

public class WatchHostSessionTests
{
    [Fact]
    public async Task Apply_verifies_contract_before_poll_health_and_commits_new_host()
    {
        var adapter = new RecordingHostAdapter();
        WatchHostSettings? capturedSettings = null;
        using var session = new WatchHostSession(settings =>
        {
            capturedSettings = settings;
            return adapter;
        });

        await session.ApplyAsync(new WatchHostSettings("http://new-host:5088", "top-secret", 12));

        Assert.Equal(new[] { "contract", "poll-health" }, adapter.Calls);
        Assert.Equal(WatchHostConnectionStatus.Connected, session.State.Status);
        Assert.Equal("http://new-host:5088", session.State.BaseUrl);
        Assert.Equal(12, capturedSettings!.RequestTimeoutSeconds);
        Assert.Equal("top-secret", capturedSettings.Credential);
        Assert.NotNull(session.State.PollHealth);
        Assert.DoesNotContain("top-secret", session.State.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_new_host_cancels_and_permanently_ignores_old_host_completion()
    {
        var oldHost = new RecordingHostAdapter(blockContract: true, ignoreCancellation: true);
        var newHost = new RecordingHostAdapter();
        using var session = new WatchHostSession(settings =>
            settings.BaseUrl.Contains("old", StringComparison.Ordinal) ? oldHost : newHost);

        var oldApply = session.ApplyAsync(new WatchHostSettings("http://old-host:5088", "old", 30));
        await oldHost.ContractStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.ApplyAsync(new WatchHostSettings("http://new-host:5088", "new", 30));
        oldHost.ReleaseContract();
        await oldApply;

        Assert.True(oldHost.ObservedCancellation);
        Assert.Equal("http://new-host:5088", session.State.BaseUrl);
        Assert.Equal(WatchHostConnectionStatus.Connected, session.State.Status);
        Assert.Same(newHost.Health, session.State.PollHealth);
    }

    [Fact]
    public async Task Failed_new_host_has_no_old_health_or_last_success_and_reports_authentication()
    {
        var healthy = new RecordingHostAdapter();
        var unauthorized = new RecordingHostAdapter(
            failure: new WatchHostQueryException(
                WatchHostFailureKind.Authentication,
                "/api/contract",
                "corr-auth",
                "Host refused the supplied credential."));
        using var session = new WatchHostSession(settings =>
            settings.BaseUrl.Contains("good", StringComparison.Ordinal) ? healthy : unauthorized);

        await session.ApplyAsync(new WatchHostSettings("http://good-host:5088", "good", 30));
        Assert.NotNull(session.State.LastSuccessfulAt);

        await session.ApplyAsync(new WatchHostSettings("http://bad-host:5088", "bad", 30));

        Assert.Equal(WatchHostConnectionStatus.Failed, session.State.Status);
        Assert.Equal(WatchHostFailureKind.Authentication, session.State.FailureKind);
        Assert.Null(session.State.PollHealth);
        Assert.Null(session.State.LastSuccessfulAt);
        Assert.Equal("corr-auth", session.State.CorrelationId);
        Assert.DoesNotContain("bad", session.State.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Settings_reject_timeout_outside_one_to_three_hundred_seconds(int timeoutSeconds)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WatchHostSettings("http://host:5088", "secret", timeoutSeconds));

        Assert.Equal("requestTimeoutSeconds", error.ParamName);
    }

    [Fact]
    public async Task Production_adapter_centralizes_bearer_timeout_correlation_and_redaction()
    {
        const string secret = "credential-must-never-escape";
        var requests = new List<(string Path, string? Authorization, string? Correlation)>();
        var handler = new DelegateHandler((request, _) =>
        {
            requests.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues(LatencyHeaders.CorrelationId, out var values)
                    ? values.Single()
                    : null));
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/api/contract" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(WatchHttpTestStubs.MatchingContractJson),
                },
                _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    ReasonPhrase = $"credential {secret} rejected",
                },
            });
        });
        using var adapter = MesIngestApiClient.CreateForHost(
            new WatchHostSettings("http://host:5088", secret, 17),
            handler: handler);

        await adapter.VerifyContractAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<WatchHostQueryException>(() =>
            adapter.FetchPollHealthAsync(CancellationToken.None));

        Assert.All(requests, request => Assert.Equal($"Bearer {secret}", request.Authorization));
        Assert.All(requests, request => Assert.False(string.IsNullOrWhiteSpace(request.Correlation)));
        Assert.Equal(WatchHostFailureKind.Authentication, error.Kind);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("(masked)", error.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingHostAdapter : IWatchHostQueryAdapter
    {
        private readonly bool _blockContract;
        private readonly bool _ignoreCancellation;
        private readonly WatchHostQueryException? _failure;
        private readonly TaskCompletionSource _releaseContract = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingHostAdapter(
            bool blockContract = false,
            bool ignoreCancellation = false,
            WatchHostQueryException? failure = null)
        {
            _blockContract = blockContract;
            _ignoreCancellation = ignoreCancellation;
            _failure = failure;
        }

        public List<string> Calls { get; } = [];
        public TaskCompletionSource ContractStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedCancellation { get; private set; }
        public WatchPollHealthDto Health { get; } = new(
            DateTimeOffset.Parse("2026-08-07T01:00:00Z"),
            DateTimeOffset.Parse("2026-08-07T01:00:01Z"),
            1000,
            4,
            true,
            "SUCCESS",
            []);

        public async Task VerifyContractAsync(CancellationToken cancellationToken)
        {
            Calls.Add("contract");
            ContractStarted.TrySetResult();
            if (_failure is not null)
            {
                throw _failure;
            }

            if (!_blockContract)
            {
                return;
            }

            using var registration = cancellationToken.Register(() => ObservedCancellation = true);
            if (_ignoreCancellation)
            {
                await _releaseContract.Task;
            }
            else
            {
                await _releaseContract.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<WatchPollHealthDto?> FetchPollHealthAsync(CancellationToken cancellationToken)
        {
            Calls.Add("poll-health");
            return Task.FromResult<WatchPollHealthDto?>(Health);
        }

        public void ReleaseContract() => _releaseContract.TrySetResult();

        public void Dispose()
        {
        }
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
            _send = send;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _send(request, cancellationToken);
    }
}
