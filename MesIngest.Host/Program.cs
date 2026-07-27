using System.Text.Json.Serialization;
using MesIngest.Core;
using MesIngest.Host;

var probeOracle = args.Any(a => string.Equals(a, "--probe-oracle", StringComparison.OrdinalIgnoreCase));

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.Configure<MesIngestHostOptions>(
    builder.Configuration.GetSection(MesIngestHostOptions.SectionName));

var configured = new MesIngestHostOptions();
builder.Configuration.GetSection(MesIngestHostOptions.SectionName).Bind(configured);
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

app.MapGet("/api/demands", (ITransportDemandStore store, string? status) =>
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

    var items = store.List(parsed).Select(DemandDto.From).ToList();
    return Results.Ok(items);
});

app.MapGet("/api/demands/{demandId}", (string demandId, ITransportDemandStore store) =>
{
    var demand = store.GetById(demandId);
    return demand is null ? Results.NotFound() : Results.Ok(DemandDto.From(demand));
});

app.MapGet("/api/alerts", (ITransportDemandStore store) =>
{
    var items = store.ListAlerts().Select(AlertDto.From).ToList();
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
    string? LocationRiskCode)
{
    public static DemandDto From(TransportDemand d) => new(
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
        d.LocationRiskCode);
}

internal sealed record AlertDto(
    string Code,
    string? TaskType,
    string? Sublot,
    string? DemandId,
    string? Message)
{
    public static AlertDto From(IngestAlert a) => new(
        a.Code,
        a.TaskType,
        a.Sublot,
        a.DemandId,
        a.Message);
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
