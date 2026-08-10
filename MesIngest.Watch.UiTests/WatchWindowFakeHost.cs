using System.Collections.Concurrent;
using System.Globalization;
using MesIngest.Core;
using MesIngest.Watch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MesIngest.Watch.UiTests;

internal sealed record WatchWindowHttpReply<T>(
    T Value,
    TimeSpan Delay = default,
    int StatusCode = StatusCodes.Status200OK);

internal sealed class WatchWindowScenario
{
    private readonly object _sync = new();
    private readonly Queue<WatchWindowHttpReply<WatchDemandPage>> _visiblePages;
    private readonly Queue<WatchWindowHttpReply<WatchDemandPage>> _gonePages;
    private readonly Queue<WatchWindowHttpReply<WatchAlertPage>> _alertPages;
    private readonly Dictionary<string, WatchDemandDto> _demandsById;
    private bool _online = true;

    private WatchWindowScenario(
        IEnumerable<WatchWindowHttpReply<WatchDemandPage>> visiblePages,
        IEnumerable<WatchWindowHttpReply<WatchDemandPage>> gonePages,
        IEnumerable<WatchWindowHttpReply<WatchAlertPage>> alertPages,
        WatchPollHealthDto health)
    {
        _visiblePages = new Queue<WatchWindowHttpReply<WatchDemandPage>>(visiblePages);
        _gonePages = new Queue<WatchWindowHttpReply<WatchDemandPage>>(gonePages);
        _alertPages = new Queue<WatchWindowHttpReply<WatchAlertPage>>(alertPages);
        Health = health;
        _demandsById = _visiblePages.Concat(_gonePages)
            .SelectMany(static reply => reply.Value.Items)
            .GroupBy(static demand => demand.DemandId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
    }

    public WatchPollHealthDto Health { get; }

    public bool IsOnline
    {
        get
        {
            lock (_sync)
            {
                return _online;
            }
        }
    }

    public static WatchWindowScenario Healthy(
        IReadOnlyList<WatchDemandPage>? visiblePages = null,
        IReadOnlyList<WatchDemandPage>? gonePages = null,
        IReadOnlyList<WatchAlertDto>? alerts = null) =>
        Scripted(
            (visiblePages ?? [new WatchDemandPage([WatchWindowScenarioData.VisibleDemand("visible-001")], null, false)])
                .Select(static page => new WatchWindowHttpReply<WatchDemandPage>(page)),
            (gonePages ?? [new WatchDemandPage([WatchWindowScenarioData.GoneDemand("gone-001")], null, false)])
                .Select(static page => new WatchWindowHttpReply<WatchDemandPage>(page)),
            [new WatchWindowHttpReply<WatchAlertPage>(new WatchAlertPage(
                alerts ?? [WatchWindowScenarioData.ActiveAlert("alert-001", "visible-001")],
                null,
                false))]);

    public static WatchWindowScenario Scripted(
        IEnumerable<WatchWindowHttpReply<WatchDemandPage>> visiblePages,
        IEnumerable<WatchWindowHttpReply<WatchDemandPage>> gonePages,
        IEnumerable<WatchWindowHttpReply<WatchAlertPage>> alertPages) =>
        new(
            EnsureAtLeastOne(visiblePages, new WatchDemandPage([], null, false)),
            EnsureAtLeastOne(gonePages, new WatchDemandPage([], null, false)),
            EnsureAtLeastOne(alertPages, new WatchAlertPage([], null, false)),
            WatchWindowScenarioData.HealthyPoll());

    public void SetOnline(bool online)
    {
        lock (_sync)
        {
            _online = online;
        }
    }

    public WatchWindowHttpReply<WatchDemandPage> NextDemand(string? status)
    {
        lock (_sync)
        {
            return NextOrRepeat(
                string.Equals(status, "GONE", StringComparison.OrdinalIgnoreCase)
                    ? _gonePages
                    : _visiblePages);
        }
    }

    public WatchWindowHttpReply<WatchAlertPage> NextAlerts()
    {
        lock (_sync)
        {
            return NextOrRepeat(_alertPages);
        }
    }

    public WatchDemandDto? FindDemand(string demandId)
    {
        lock (_sync)
        {
            return _demandsById.GetValueOrDefault(demandId);
        }
    }

    private static Queue<WatchWindowHttpReply<T>> EnsureAtLeastOne<T>(
        IEnumerable<WatchWindowHttpReply<T>> replies,
        T fallback)
    {
        var queue = new Queue<WatchWindowHttpReply<T>>(replies);
        if (queue.Count == 0)
        {
            queue.Enqueue(new WatchWindowHttpReply<T>(fallback));
        }

        return queue;
    }

    private static WatchWindowHttpReply<T> NextOrRepeat<T>(Queue<WatchWindowHttpReply<T>> queue)
    {
        var reply = queue.Peek();
        if (queue.Count > 1)
        {
            queue.Dequeue();
        }

        return reply;
    }
}

internal sealed class WatchWindowFakeHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly WatchWindowScenario _scenario;
    private readonly ConcurrentQueue<string> _timeline = new();

    private WatchWindowFakeHost(WebApplication application, WatchWindowScenario scenario)
    {
        _application = application;
        _scenario = scenario;
    }

    public string BaseUrl { get; private set; } = string.Empty;
    public IReadOnlyList<string> Timeline => _timeline.ToArray();

    public string TimelineSummary => string.Join(
        Environment.NewLine,
        Timeline
            .GroupBy(static entry => entry, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => $"{group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)}"));

    public static async Task<WatchWindowFakeHost> StartAsync(
        WatchWindowScenario scenario,
        string? listenUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(listenUrl ?? "http://127.0.0.1:0");
        var app = builder.Build();
        var host = new WatchWindowFakeHost(app, scenario);
        host.MapEndpoints();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        host.BaseUrl = addresses?.SingleOrDefault()
            ?? throw new InvalidOperationException("Fake Host did not publish a loopback address.");
        return host;
    }

    public void SetOnline(bool online) => _scenario.SetOnline(online);

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }

    private void MapEndpoints()
    {
        _application.Use(async (context, next) =>
        {
            Record(context.Request);
            if (!_scenario.IsOnline)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { error = "fake Host offline" });
                return;
            }

            await next(context);
        });

        _application.MapGet("/api/contract", () =>
            Results.Json(new MesIngestContractInfo(
                MesIngestApiContract.Version,
                MesIngestApiContract.SchemaVersion)));
        _application.MapGet("/api/poll-health", () => Results.Json(_scenario.Health));
        _application.MapGet("/api/demands", async (HttpContext context) =>
        {
            var reply = _scenario.NextDemand(context.Request.Query["status"].FirstOrDefault());
            if (reply.Delay > TimeSpan.Zero)
            {
                await Task.Delay(reply.Delay, context.RequestAborted);
            }

            return Results.Json(reply.Value, statusCode: reply.StatusCode);
        });
        _application.MapGet("/api/demands/{demandId}", (string demandId) =>
        {
            var demand = _scenario.FindDemand(demandId);
            return demand is null ? Results.NotFound() : Results.Json(demand);
        });
        _application.MapGet("/api/alerts", async (HttpContext context) =>
        {
            var reply = _scenario.NextAlerts();
            if (reply.Delay > TimeSpan.Zero)
            {
                await Task.Delay(reply.Delay, context.RequestAborted);
            }

            return Results.Json(reply.Value, statusCode: reply.StatusCode);
        });
    }

    private void Record(HttpRequest request)
    {
        var keys = request.Query.Keys.OrderBy(static key => key, StringComparer.Ordinal).ToArray();
        var queryShape = keys.Length == 0 ? string.Empty : $"?keys={string.Join(',', keys)}";
        _timeline.Enqueue($"{request.Method} {request.Path}{queryShape}");
    }
}

internal static class WatchWindowScenarioData
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-08T09:30:00+08:00");

    public static WatchDemandDto VisibleDemand(string demandId) =>
        new(
            demandId,
            "WIRE_TO_GATE",
            "SLOT-LOT-001",
            "A01",
            "WB-01",
            "焊线",
            Now.AddMinutes(-15),
            "BASKET-LONG-PACKAGE",
            "VISIBLE",
            Now.AddSeconds(-2),
            0,
            false,
            null,
            Now.AddMinutes(-15),
            null);

    public static WatchDemandDto GoneDemand(string demandId) =>
        VisibleDemand(demandId) with
        {
            TaskType = "DIE_TO_OVEN",
            Sublot = "GONE-LOT-001",
            Status = "GONE",
            GoneAt = Now.AddMinutes(-2),
        };

    public static WatchAlertDto ActiveAlert(string alertId, string demandId) =>
        new(
            alertId,
            "FIELD_DRIFT",
            "ERROR",
            "WIRE_TO_GATE",
            "SLOT-LOT-001",
            demandId,
            "MES 字段漂移，需要核验当前 TransportDemand。",
            "{\"field\":\"AREA\",\"frozen\":\"A01\",\"observed\":\"A02\"}",
            Now.AddMinutes(-5),
            Now.AddSeconds(-5),
            2,
            true,
            null,
            Now.AddMinutes(-5));

    public static WatchPollHealthDto HealthyPoll() =>
        new(
            Now.AddSeconds(-4),
            Now.AddSeconds(-1),
            3000,
            12,
            true,
            "SUCCESS",
            []);
}
