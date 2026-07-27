using System.Text.Json.Serialization;
using MesIngest.Core;
using MesIngest.Host;
using Microsoft.Extensions.Hosting.WindowsServices;

var probeOracle = args.Any(a => string.Equals(a, "--probe-oracle", StringComparison.OrdinalIgnoreCase));

// Windows Service cwd is often System32; pin ContentRoot to the published exe directory.
// Interactive / WebApplicationFactory keep default content-root discovery.
WebApplicationBuilder builder = WindowsServiceHelpers.IsWindowsService()
    ? WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    })
    : WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var configured = new MesIngestHostOptions();
builder.Configuration.GetSection(MesIngestHostOptions.SectionName).Bind(configured);
SharedSecretAuth.ValidateStartup(configured, builder.Configuration);
if (!string.IsNullOrWhiteSpace(configured.Urls))
{
    builder.WebHost.UseUrls(configured.Urls);
}

builder.Services.AddSingleton(configured);

builder.Services.AddSingleton<IDemandIdAllocator, GuidDemandIdAllocator>();
builder.Services.AddSingleton<TransportDemandReconciler>();
builder.Services.AddSingleton<ITransportDemandStore>(sp =>
{
    var options = sp.GetRequiredService<MesIngestHostOptions>();
    if (!string.IsNullOrWhiteSpace(options.SqlServerConnectionString))
    {
        return new SqlServerTransportDemandStore(options.SqlServerConnectionString);
    }

    return new InMemoryTransportDemandStore();
});
builder.Services.AddSingleton<IMesSnapshotSource>(sp =>
{
    var options = sp.GetRequiredService<MesIngestHostOptions>();
    var contentRoot = sp.GetRequiredService<IHostEnvironment>().ContentRootPath;
    return CreateSnapshotSource(options, contentRoot);
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<MesIngestHostOptions>();
    return new IngestRoundRunner(
        sp.GetRequiredService<IMesSnapshotSource>(),
        sp.GetRequiredService<TransportDemandReconciler>(),
        sp.GetRequiredService<ITransportDemandStore>(),
        options.GoLiveBaseline,
        disappearThreshold: options.DisappearThreshold,
        zeroDropEnterThreshold: options.ZeroDropEnterThreshold,
        zeroDropClearStreak: options.ZeroDropClearStreak,
        queryTimeout: TimeSpan.FromSeconds(Math.Max(1, options.QueryTimeoutSeconds)));
});

if (!probeOracle)
{
    builder.Services.AddHostedService<PollHostedService>();
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    var options = context.RequestServices.GetRequiredService<MesIngestHostOptions>();
    var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
    if (!SharedSecretAuth.IsAuthorized(context.Request, options, configuration))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "shared secret required" });
        return;
    }

    await next();
});

if (probeOracle)
{
    var options = app.Services.GetRequiredService<MesIngestHostOptions>();
    if (!options.IsOracleSnapshotSource())
    {
        Console.Error.WriteLine(
            "MesIngest:SnapshotSource must be Oracle for --probe-oracle.");
        return 2;
    }

    var source = app.Services.GetRequiredService<IMesSnapshotSource>();
    bool? instantClientOnPath = null;
    if (source is OracleMesSnapshotSource oracle)
    {
        oracle.EnsureInitialized();
        instantClientOnPath = oracle.InstantClientOnPath;
    }

    var exitCode = await OracleProbe.RunAsync(
        source,
        Console.Out,
        options.ParseOracleMode(),
        instantClientOnPath);
    return exitCode;
}

if (app.Services.GetRequiredService<MesIngestHostOptions>().RunOneShotOnStartup)
{
    var runner = app.Services.GetRequiredService<IngestRoundRunner>();
    await runner.RunOnceAsync();
}

app.MapGet("/api/demands", (
    ITransportDemandStore store,
    string? status,
    string? taskType,
    string? sublot,
    string? demandId) =>
{
    DemandStatus? parsed = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!TryParseStatus(status, out var value))
        {
            return Results.BadRequest(new { error = "status must be VISIBLE or GONE" });
        }

        parsed = value;
    }

    var alerts = store.ListAlerts();
    var items = store.List(parsed, taskType, sublot, demandId)
        .Select(d => DemandDto.From(d, DemandDto.RelevantAlerts(alerts, d)))
        .ToList();
    return Results.Ok(items);
});

app.MapGet("/api/demands/{demandId}", (string demandId, ITransportDemandStore store) =>
{
    var demand = store.GetById(demandId);
    if (demand is null)
    {
        return Results.NotFound();
    }

    var alerts = DemandDto.RelevantAlerts(store.ListAlerts(), demand);
    return Results.Ok(DemandDto.From(demand, alerts));
});

app.MapGet("/api/alerts", (ITransportDemandStore store, int? limit) =>
{
    var items = store.ListAlerts(limit).Select(AlertDto.From).ToList();
    return Results.Ok(items);
});

app.MapGet("/api/poll-health", (ITransportDemandStore store) =>
{
    var health = store.GetLatestPollHealth();
    if (health is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(PollHealthDto.From(health, store.GetState().TaskTypePauses));
});

app.Run();
return 0;

static IMesSnapshotSource CreateSnapshotSource(MesIngestHostOptions options, string contentRoot)
{
    if (options.IsOracleSnapshotSource())
    {
        var oracleOptions = options.ToOracleSnapshotOptions(contentRoot);
        if (!File.Exists(oracleOptions.QuerySqlPath))
        {
            throw new FileNotFoundException(
                "Published MES_TASK_UNION SQL not found. Build/publish should copy mes/queries/mes-task-union beside the host.",
                oracleOptions.QuerySqlPath);
        }

        return new OracleMesSnapshotSource(oracleOptions);
    }

    if (string.IsNullOrWhiteSpace(options.SnapshotCsvPath))
    {
        throw new InvalidOperationException(
            "MesIngest:SnapshotCsvPath is required for File snapshot mode.");
    }

    return new CsvFileMesSnapshotSource(options.SnapshotCsvPath);
}

static bool TryParseStatus(string raw, out DemandStatus status)
{
    if (raw.Equals("VISIBLE", StringComparison.OrdinalIgnoreCase))
    {
        status = DemandStatus.Visible;
        return true;
    }

    if (raw.Equals("GONE", StringComparison.OrdinalIgnoreCase))
    {
        status = DemandStatus.Gone;
        return true;
    }

    status = default;
    return false;
}

internal sealed record DemandDto(
    string DemandId,
    string TaskType,
    string Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset Dates,
    string? Package,
    string Status,
    DateTimeOffset MesLastSeenAt,
    int DisappearCount,
    bool LocationRisk,
    string? LocationRiskCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? GoneAt,
    IReadOnlyList<AlertDto> Alerts)
{
    public static DemandDto From(TransportDemand d, IReadOnlyList<AlertDto> alerts) => new(
        d.DemandId,
        d.TaskType,
        d.Sublot,
        d.Area,
        d.Eqp,
        d.Step,
        d.Dates,
        d.Package,
        d.Status == DemandStatus.Visible ? "VISIBLE" : "GONE",
        d.MesLastSeenAt,
        d.DisappearCount,
        d.LocationRisk,
        d.LocationRiskCode,
        d.CreatedAt,
        d.GoneAt,
        alerts);

    public static IReadOnlyList<AlertDto> RelevantAlerts(
        IReadOnlyList<IngestAlert> alerts,
        TransportDemand demand) =>
        alerts
            .Where(a => IsRelevant(a, demand))
            .Select(AlertDto.From)
            .ToList();

    private static bool IsRelevant(IngestAlert alert, TransportDemand demand)
    {
        if (!string.IsNullOrWhiteSpace(alert.DemandId))
        {
            return string.Equals(alert.DemandId, demand.DemandId, StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(alert.TaskType)
            && !string.IsNullOrWhiteSpace(alert.Sublot)
            && string.Equals(alert.TaskType, demand.TaskType, StringComparison.Ordinal)
            && string.Equals(alert.Sublot, demand.Sublot, StringComparison.Ordinal);
    }
}

internal sealed record AlertDto(
    string Code,
    string? TaskType,
    string? Sublot,
    string? DemandId,
    string? Message,
    DateTimeOffset? CreatedAt)
{
    public static AlertDto From(IngestAlert a) => new(
        a.Code,
        a.TaskType,
        a.Sublot,
        a.DemandId,
        a.Message,
        a.CreatedAt);
}

internal sealed record TaskTypePauseDto(
    string TaskType,
    bool PausedZeroDrop,
    int LastHealthyNonZeroCount,
    int RecoveryStreak)
{
    public static TaskTypePauseDto From(TaskTypePauseState p) => new(
        p.TaskType,
        p.PausedZeroDrop,
        p.LastHealthyNonZeroCount,
        p.RecoveryStreak);
}

internal sealed record PollHealthDto(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double DurationMs,
    int RowCount,
    bool Success,
    string Outcome,
    IReadOnlyList<TaskTypePauseDto> TaskTypePauses)
{
    public static PollHealthDto From(PollHealth h, IReadOnlyList<TaskTypePauseState> pauses) => new(
        h.StartedAt,
        h.EndedAt,
        h.DurationMs,
        h.RowCount,
        h.Success,
        h.Outcome,
        pauses.Select(TaskTypePauseDto.From).ToList());
}

public partial class Program;
