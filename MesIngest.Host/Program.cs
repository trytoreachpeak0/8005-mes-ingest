using System.Text.Json.Serialization;
using MesIngest.Core;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.Infrastructure.SqlServer;
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

var newV2Enabled = !string.IsNullOrWhiteSpace(configured.NewSqlServerConnectionString);
if (newV2Enabled && configured.ZeroDropEnterThreshold <= 0)
{
    throw new InvalidOperationException(
        "MesIngest:ZeroDropEnterThreshold must be greater than zero when the V2 projection is enabled.");
}
if (newV2Enabled
    && configured.ZeroDropClearStreak != TaskTypeProtectionPolicy.RequiredRecoveryStreak)
{
    throw new InvalidOperationException(
        $"MesIngest:ZeroDropClearStreak must be {TaskTypeProtectionPolicy.RequiredRecoveryStreak} "
        + "when the V2 projection is enabled.");
}
if (!probeOracle && !builder.Environment.IsDevelopment() && !newV2Enabled)
{
    throw new InvalidOperationException(
        "MesIngest:NewSqlServerConnectionString is required outside the Development environment.");
}
var legacyAlongsideV2Enabled = newV2Enabled
    && builder.Environment.IsDevelopment()
    && configured.EnableLegacyDevelopmentEndpoints;
var legacySurfaceEnabled = !newV2Enabled || legacyAlongsideV2Enabled;
var legacyRuntimeEnabled = probeOracle || legacySurfaceEnabled;

builder.Services.AddSingleton(configured);
builder.Services.AddSingleton(TimeProvider.System);
if (newV2Enabled)
{
    builder.Services.AddSingleton<IMesIngestProjection>(
        new SqlServerMesIngestProjection(
            configured.NewSqlServerConnectionString,
            configured.ZeroDropEnterThreshold));
    builder.Services.AddSingleton<RoundIngestor>();
    builder.Services.AddHostedService<NewMesIngestHostSessionService>();
}
builder.Services.AddSingleton<ILatencyTelemetry>(sp =>
    new LoggingLatencyTelemetry(sp.GetRequiredService<ILoggerFactory>().CreateLogger("MesIngest.Latency")));

if (legacyRuntimeEnabled)
{
    builder.Services.AddSingleton<IDemandIdAllocator, GuidDemandIdAllocator>();
    builder.Services.AddSingleton<TransportDemandReconciler>();
    builder.Services.AddSingleton<ITransportDemandStore>(sp =>
    {
        var options = sp.GetRequiredService<MesIngestHostOptions>();
        var telemetry = sp.GetRequiredService<ILatencyTelemetry>();
        var timeProvider = sp.GetRequiredService<TimeProvider>();
        ITransportDemandStore inner = !string.IsNullOrWhiteSpace(options.SqlServerConnectionString)
            ? new SqlServerTransportDemandStore(
                options.SqlServerConnectionString,
                telemetry,
                TimeSpan.FromHours(Math.Max(0, options.ChangeFeedRetentionHours)),
                clock: timeProvider.GetUtcNow,
                alertRetention: TimeSpan.FromDays(Math.Max(0, options.AlertRetentionDays)))
            : new InMemoryTransportDemandStore(
                TimeSpan.FromHours(Math.Max(0, options.ChangeFeedRetentionHours)),
                clock: timeProvider.GetUtcNow,
                alertRetention: TimeSpan.FromDays(Math.Max(0, options.AlertRetentionDays)));
        return new ObservingTransportDemandStore(inner, telemetry);
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
            queryTimeout: TimeSpan.FromSeconds(Math.Max(1, options.QueryTimeoutSeconds)),
            clock: sp.GetRequiredService<TimeProvider>().GetUtcNow,
            telemetry: sp.GetRequiredService<ILatencyTelemetry>());
    });
}

if (!probeOracle && legacySurfaceEnabled)
{
    builder.Services.AddHostedService<PollHostedService>();
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

if (!probeOracle && legacySurfaceEnabled)
{
    builder.Services.AddMesIngestOpenApi();
}

var app = builder.Build();

app.UseMiddleware<HostRequestLatencyMiddleware>();

app.Use(async (context, next) =>
{
    if (MesIngestOpenApi.IsPublicDocumentationPath(context.Request.Path))
    {
        await next();
        return;
    }

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

if (legacySurfaceEnabled
    && app.Services.GetRequiredService<MesIngestHostOptions>().RunOneShotOnStartup)
{
    var runner = app.Services.GetRequiredService<IngestRoundRunner>();
    await runner.RunOnceAsync();
}

if (legacySurfaceEnabled)
{
    app.UseMesIngestOpenApi();
}
if (newV2Enabled)
{
    app.MapNewMesIngestEndpoints();
}

if (legacySurfaceEnabled)
{
app.MapGet("/api/contract", () =>
    Results.Ok(new MesIngestContractInfo(
        MesIngestApiContract.Version,
        MesIngestApiContract.SchemaVersion)));

app.MapGet("/api/demands", (
    ITransportDemandStore store,
    TimeProvider timeProvider,
    string? status,
    string? taskType,
    string? sublot,
    string? demandId,
    string? datesFrom,
    string? datesTo,
    string? goneAtFrom,
    string? goneAtTo,
    string? sortBy,
    string? direction,
    int? limit,
    string? cursor) =>
{
    if (!DemandListQueryParser.TryParseStatus(status, out var parsedStatus, out var statusError))
    {
        return Results.BadRequest(new { error = statusError });
    }

    if (!DemandListQueryParser.TryParseSortBy(sortBy, out var parsedSortBy, out var sortError))
    {
        return Results.BadRequest(new { error = sortError });
    }

    if (!DemandListQueryParser.TryParseDirection(direction, out var parsedDirection, out var directionError))
    {
        return Results.BadRequest(new { error = directionError });
    }

    if (!DemandListQueryParser.TryParseLimit(limit, out var parsedLimit, out var limitError))
    {
        return Results.BadRequest(new { error = limitError });
    }

    if (!DemandListQueryParser.TryParseDemandId(demandId, out var demandIdMatch, out var demandIdError))
    {
        return Results.BadRequest(new { error = demandIdError });
    }

    if (!DemandListQueryParser.TryParseDateTimeOffset(datesFrom, out var parsedDatesFrom, out var datesFromError))
    {
        return Results.BadRequest(new { error = datesFromError });
    }

    if (!DemandListQueryParser.TryParseDateTimeOffset(datesTo, out var parsedDatesTo, out var datesToError))
    {
        return Results.BadRequest(new { error = datesToError });
    }

    if (!DemandListQueryParser.TryParseDateTimeOffset(goneAtFrom, out var parsedGoneAtFrom, out var goneAtFromError))
    {
        return Results.BadRequest(new { error = goneAtFromError });
    }

    if (!DemandListQueryParser.TryParseDateTimeOffset(goneAtTo, out var parsedGoneAtTo, out var goneAtToError))
    {
        return Results.BadRequest(new { error = goneAtToError });
    }

    if (!DemandListCursor.TryDecode(cursor, parsedSortBy, parsedDirection, out _, out var cursorError))
    {
        return Results.BadRequest(new { error = cursorError });
    }

    var query = new DemandListQuery
    {
        Status = parsedStatus,
        TaskType = taskType,
        Sublot = sublot,
        DemandId = demandIdMatch,
        DatesFrom = parsedDatesFrom,
        DatesTo = parsedDatesTo,
        GoneAtFrom = parsedGoneAtFrom,
        GoneAtTo = parsedGoneAtTo,
        SortBy = parsedSortBy,
        Direction = parsedDirection,
        Limit = parsedLimit,
        Cursor = cursor,
        AsOf = timeProvider.GetUtcNow(),
    };

    var alerts = store.ListAlerts();
    var page = store.QueryPage(query);
    return Results.Ok(new DemandPageDto(
        page.Items.Select(d => DemandDto.From(d, DemandDto.RelevantAlerts(alerts, d))).ToList(),
        page.NextCursor,
        page.HasMore));
})
.WithName("ListDemands");

app.MapGet("/api/demands/{demandId}", (string demandId, ITransportDemandStore store) =>
{
    var demand = store.GetById(demandId);
    if (demand is null)
    {
        return Results.NotFound();
    }

    var alerts = DemandDto.RelevantAlerts(store.ListAlerts(), demand);
    return Results.Ok(DemandDto.From(demand, alerts));
})
.WithName("GetDemand");

app.MapGet("/api/alerts", (
    ITransportDemandStore store,
    string? active,
    string? code,
    string? severity,
    string? from,
    string? to,
    string? sortBy,
    string? direction,
    string? limit,
    string? cursor) =>
{
    if (!AlertListQueryParser.TryParseActive(active, out var parsedActive, out var activeError))
    {
        return Results.BadRequest(new { error = activeError });
    }

    if (!AlertListQueryParser.TryParseSortBy(sortBy, out var parsedSortBy, out var sortError))
    {
        return Results.BadRequest(new { error = sortError });
    }

    if (!AlertListQueryParser.TryParseDirection(direction, out var parsedDirection, out var directionError))
    {
        return Results.BadRequest(new { error = directionError });
    }

    if (!AlertListQueryParser.TryParseLimit(limit, out var parsedLimit, out var limitError))
    {
        return Results.BadRequest(new { error = limitError });
    }

    if (!AlertListQueryParser.TryParseDateTimeOffset(from, out var parsedFrom, out var fromError))
    {
        return Results.BadRequest(new { error = fromError });
    }

    if (!AlertListQueryParser.TryParseDateTimeOffset(to, out var parsedTo, out var toError))
    {
        return Results.BadRequest(new { error = toError });
    }

    if (!string.IsNullOrWhiteSpace(severity)
        && !severity.Equals(AlertSeverities.Error, StringComparison.OrdinalIgnoreCase)
        && !severity.Equals(AlertSeverities.Warning, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "severity must be ERROR or WARNING" });
    }

    var useDefaultPriority =
        string.IsNullOrWhiteSpace(sortBy)
        && string.IsNullOrWhiteSpace(direction);

    var query = new AlertListQuery
    {
        Active = parsedActive,
        Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
        Severity = string.IsNullOrWhiteSpace(severity) ? null : severity.Trim().ToUpperInvariant(),
        From = parsedFrom,
        To = parsedTo,
        SortBy = parsedSortBy,
        Direction = parsedDirection,
        Limit = parsedLimit,
        Cursor = cursor,
        UseDefaultPrioritySort = useDefaultPriority,
    };

    try
    {
        var page = store.QueryAlerts(query);
        return Results.Ok(new AlertPageDto(
            page.Items.Select(AlertDto.From).ToList(),
            page.NextCursor,
            page.HasMore));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("ListAlerts");

app.MapGet("/api/poll-health", (ITransportDemandStore store) =>
{
    var health = store.GetLatestPollHealth();
    if (health is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(PollHealthDto.From(health, store.GetState().TaskTypePauses));
})
.WithName("GetPollHealth");

app.MapGet("/api/demand-changes", (
    ITransportDemandStore store,
    TimeProvider timeProvider,
    string? afterSequence,
    int? limit) =>
{
    if (!DemandChangeFeedQueryParser.TryParseAfterSequence(afterSequence, out var parsedAfter, out var afterError))
    {
        return Results.BadRequest(new { error = afterError });
    }

    if (!DemandChangeFeedQueryParser.TryParseLimit(limit, out var parsedLimit, out var limitError))
    {
        return Results.BadRequest(new { error = limitError });
    }

    try
    {
        var page = store.QueryChangeFeed(new DemandChangeFeedQuery
        {
            AfterSequence = parsedAfter,
            Limit = parsedLimit,
            AsOf = timeProvider.GetUtcNow(),
        });
        return Results.Ok(DemandChangeFeedPageDto.From(page));
    }
    catch (SyncCursorExpiredException ex)
    {
        return Results.Json(
            new
            {
                error = SyncCursorExpiredException.ErrorCode,
                code = SyncCursorExpiredException.ErrorCode,
                afterSequence = ex.AfterSequence,
                earliestAvailableSequence = ex.EarliestAvailableSequence,
                highWatermark = ex.HighWatermark,
            },
            statusCode: StatusCodes.Status410Gone);
    }
})
.WithName("ListDemandChanges");
}

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

internal sealed record DemandPageDto(
    IReadOnlyList<DemandDto> Items,
    string? NextCursor,
    bool HasMore);

internal sealed record DemandChangeFeedPageDto(
    IReadOnlyList<DemandChangeDto> Items,
    long? NextAfterSequence,
    bool HasMore,
    long HighWatermark,
    long? EarliestAvailableSequence)
{
    public static DemandChangeFeedPageDto From(DemandChangeFeedPage page) =>
        new(
            page.Items.Select(DemandChangeDto.From).ToList(),
            page.NextAfterSequence,
            page.HasMore,
            page.HighWatermark,
            page.EarliestAvailableSequence);
}

internal sealed record DemandChangeDto(
    long Sequence,
    string DemandId,
    string ChangeType,
    DateTimeOffset ChangedAt,
    DemandChangePayloadDto Payload)
{
    public static DemandChangeDto From(DemandChangeFeedEntry entry) =>
        new(
            entry.Sequence,
            entry.DemandId,
            entry.ChangeType == DemandChangeType.Created ? "CREATED" : "GONE",
            entry.ChangedAt,
            DemandChangePayloadDto.From(entry.Payload));
}

internal sealed record DemandChangePayloadDto(
    string DemandId,
    string TaskType,
    string Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset Dates,
    string? Package,
    string Status,
    bool LocationRisk,
    string? LocationRiskCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? GoneAt)
{
    public static DemandChangePayloadDto From(DemandChangePayload p) =>
        new(
            p.DemandId,
            p.TaskType,
            p.Sublot,
            p.Area,
            p.Eqp,
            p.Step,
            p.Dates,
            p.Package,
            p.Status == DemandStatus.Visible ? "VISIBLE" : "GONE",
            p.LocationRisk,
            p.LocationRiskCode,
            p.CreatedAt,
            p.GoneAt);
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

internal sealed record AlertPageDto(
    IReadOnlyList<AlertDto> Items,
    string? NextCursor,
    bool HasMore);

internal sealed record AlertDto(
    string AlertId,
    string Code,
    string Severity,
    string? TaskType,
    string? Sublot,
    string? DemandId,
    string? Message,
    string? Details,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int OccurrenceCount,
    bool IsActive,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? CreatedAt)
{
    public static AlertDto From(IngestAlert a) => new(
        a.AlertId ?? string.Empty,
        a.Code,
        a.Severity ?? IngestAlertCatalog.SeverityFor(a.Code),
        a.TaskType,
        a.Sublot,
        a.DemandId,
        a.Message,
        a.Details,
        a.EffectiveFirstSeenAt,
        a.EffectiveLastSeenAt,
        Math.Max(1, a.OccurrenceCount),
        a.IsActive,
        a.ResolvedAt,
        a.EffectiveFirstSeenAt);
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
    IReadOnlyList<TaskTypePauseDto> TaskTypePauses,
    string? FailureStage = null,
    double? OracleDurationMs = null)
{
    public static PollHealthDto From(PollHealth h, IReadOnlyList<TaskTypePauseState> pauses) => new(
        h.StartedAt,
        h.EndedAt,
        h.DurationMs,
        h.RowCount,
        h.Success,
        h.Outcome,
        pauses.Select(TaskTypePauseDto.From).ToList(),
        h.FailureStage,
        h.OracleDurationMs);
}

public partial class Program;
