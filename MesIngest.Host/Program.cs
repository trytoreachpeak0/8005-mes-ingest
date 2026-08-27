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
// Local files are convenient defaults, but deployment environment variables are
// the explicit operator override and must retain higher precedence.
builder.Configuration.AddEnvironmentVariables();

var configured = new MesIngestHostOptions();
builder.Configuration.GetSection(MesIngestHostOptions.SectionName).Bind(configured);
configured.ValidateContractShape(builder.Configuration);
var bindingSecurityPolicy = SharedSecretAuth.ValidateStartup(configured, builder.Configuration);
builder.WebHost.UseUrls(bindingSecurityPolicy.Urls);

var projectionEnabled = !string.IsNullOrWhiteSpace(configured.NewSqlServerConnectionString);
ProductionHostStartupPolicy.Validate(
    configured,
    builder.Environment.IsDevelopment(),
    probeOracle,
    projectionEnabled);
if (projectionEnabled && configured.ZeroDropEnterThreshold <= 0)
{
    throw new InvalidOperationException(
        "MesIngest:ZeroDropEnterThreshold must be greater than zero when the projection is enabled.");
}
if (projectionEnabled
    && configured.ZeroDropClearStreak != TaskTypeProtectionPolicy.RequiredRecoveryStreak)
{
    throw new InvalidOperationException(
        $"MesIngest:ZeroDropClearStreak must be {TaskTypeProtectionPolicy.RequiredRecoveryStreak} "
        + "when the projection is enabled.");
}
if (projectionEnabled)
{
    builder.Services.AddSingleton<IVolumeSpaceReader, PhysicalVolumeSpaceReader>();
    configured.ValidateHistoryCleanupPolicy();
}
if (probeOracle)
{
    ReleaseSmokeRoundReplay.ValidateProbeIsLive(configured);
}

var oracleRuntimeEnabled = projectionEnabled && configured.IsOracleSnapshotSource();

if (ReleaseSmokeRoundReplay.IsConfigured(configured) && !oracleRuntimeEnabled)
{
    // Silently ignoring the recording would let a smoke believe it drove the
    // production entry while the configured runtime never reads it.
    throw new InvalidOperationException(
        "MesIngest:ReplayRoundsFromRecordingPath only feeds the production Oracle round "
        + "source; set MesIngest:NewSqlServerConnectionString and MesIngest:SnapshotSource=Oracle, "
        + "or remove the recording path.");
}

// Resolved before the container so a missing acknowledgement or an unreadable
// recording fails startup loudly instead of on the first poll.
var replayedRoundExecutor = ReleaseSmokeRoundReplay.Resolve(configured);

builder.Services.AddSingleton(configured);
builder.Services.AddSingleton(bindingSecurityPolicy);
builder.Services.AddSingleton(TimeProvider.System);
if (projectionEnabled)
{
    builder.Services.AddSingleton<IWatchOverviewReadBoundaryObserver>(
        NoopWatchOverviewReadBoundaryObserver.Instance);
    builder.Services.AddSingleton<IProjectionCommitCheckpointObserver>(
        NoopProjectionCommitCheckpointObserver.Instance);
    builder.Services.AddSingleton<IProjectionReadBoundaryObserver>(
        NoopProjectionReadBoundaryObserver.Instance);
    builder.Services.AddSingleton<SqlServerMesIngestProjection>(sp =>
        new SqlServerMesIngestProjection(
            configured.NewSqlServerConnectionString,
            configured.ZeroDropEnterThreshold,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IWatchOverviewReadBoundaryObserver>(),
            sp.GetRequiredService<IProjectionCommitCheckpointObserver>(),
            sp.GetRequiredService<IProjectionReadBoundaryObserver>(),
            volumeSpaceReader: sp.GetRequiredService<IVolumeSpaceReader>()));
    builder.Services.AddSingleton<IMesIngestProjection>(sp =>
        sp.GetRequiredService<SqlServerMesIngestProjection>());
    builder.Services.AddSingleton<IHistoryCleanupOperations>(sp =>
        sp.GetRequiredService<SqlServerMesIngestProjection>());
    builder.Services.AddSingleton<IStoragePressureOperations>(sp =>
        sp.GetRequiredService<SqlServerMesIngestProjection>());
    builder.Services.AddSingleton<IStoragePressurePollGate, StoragePressurePollGate>();
    builder.Services.AddSingleton<IngestWorkPriorityGate>();
    builder.Services.AddSingleton<IHistoryCleanupBatchRunner, HistoryCleanupBatchRunner>();
    builder.Services.AddHostedService<HistoryCleanupHostedService>();
    builder.Services.AddSingleton<RoundIngestor>();
    builder.Services.AddHostedService<NewMesIngestHostSessionService>();
    if (oracleRuntimeEnabled)
    {
        if (replayedRoundExecutor is not null)
        {
            builder.Services.AddSingleton(replayedRoundExecutor);
        }

        builder.Services.AddSingleton<IMesTaskUnionRoundSource>(sp =>
        {
            var options = sp.GetRequiredService<MesIngestHostOptions>();
            var contentRoot = sp.GetRequiredService<IHostEnvironment>().ContentRootPath;
            return new OracleMesTaskUnionRoundSource(
                options.ToOracleSnapshotOptions(contentRoot),
                sp.GetService<IOracleStatementExecutor>(),
                timeProvider: sp.GetRequiredService<TimeProvider>(),
                executorFactory: sp.GetRequiredService<IOracleStatementExecutorFactory>());
        });
        builder.Services.AddSingleton<IOracleStatementExecutorFactory, OracleStatementExecutorFactory>();
        builder.Services.AddSingleton<ISublotBoxCountReader>(sp =>
        {
            var options = sp.GetRequiredService<MesIngestHostOptions>();
            var contentRoot = sp.GetRequiredService<IHostEnvironment>().ContentRootPath;
            return new OracleSublotBoxCountReader(
                options.ToSublotBoxCountOracleOptions(contentRoot),
                sp.GetRequiredService<IOracleStatementExecutorFactory>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<OracleSublotBoxCountReader>>());
        });
        builder.Services.AddSingleton<MesTaskUnionPollRunner>();
        builder.Services.AddSingleton<IMesTaskUnionPollRunner>(sp =>
            sp.GetRequiredService<MesTaskUnionPollRunner>());
        builder.Services.AddSingleton<StoragePressureGuardedPollRunner>();
        if (!probeOracle)
        {
            builder.Services.AddHostedService<MesTaskUnionPollHostedService>();
        }
    }
    else
    {
        builder.Services.AddSingleton<ISublotBoxCountReader, UnavailableSublotBoxCountReader>();
    }
}
builder.Services.AddSingleton<ILatencyTelemetry>(sp =>
    new LoggingLatencyTelemetry(sp.GetRequiredService<ILoggerFactory>().CreateLogger("MesIngest.Latency")));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

if (!probeOracle && projectionEnabled)
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

    var policy = context.RequestServices.GetRequiredService<BindingSecurityPolicy>();
    if (!SharedSecretAuth.IsAuthorized(context.Request, policy))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new NewMesIngestErrorDto(
            "UNAUTHORIZED",
            "Bearer SharedSecret is required."));
        return;
    }

    await next();
});

app.UseMiddleware<MesIngestHistoryExpirationMiddleware>();

if (probeOracle)
{
    var options = app.Services.GetRequiredService<MesIngestHostOptions>();
    if (!options.IsOracleSnapshotSource())
    {
        Console.Error.WriteLine(
            "MesIngest:SnapshotSource must be Oracle for --probe-oracle.");
        return 2;
    }

    var contentRoot = app.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;
    var source = new OracleMesTaskUnionRoundSource(
        options.ToOracleSnapshotOptions(contentRoot),
        timeProvider: app.Services.GetRequiredService<TimeProvider>());
    var exitCode = await OracleProbe.RunAsync(source, Console.Out);
    return exitCode;
}

if (app.Services.GetRequiredService<MesIngestHostOptions>().RunOneShotOnStartup)
{
    if (oracleRuntimeEnabled)
    {
        await app.Services.GetRequiredService<StoragePressureGuardedPollRunner>()
            .RunOnceIfAllowedAsync();
    }
}

if (projectionEnabled)
{
    app.UseMesIngestOpenApi();
    app.MapNewMesIngestEndpoints();
}

app.Run();
return 0;

public partial class Program;
