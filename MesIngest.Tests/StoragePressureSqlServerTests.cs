using System.Net;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.Infrastructure.SqlServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class StoragePressureSqlServerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StoragePressureSqlServerTests(WebApplicationFactory<Program> factory) =>
        _factory = factory;

    [Ticket01SqlServerFact]
    public async Task Below_ten_percent_pauses_before_external_reads_and_restart_cannot_auto_resume()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var projection = new SqlServerMesIngestProjection(database.ConnectionString);
        await projection.CommitRoundAsync(SuccessRound("poll-storage-pressure"));
        var volume = await projection.ResolveDatabaseVolumeAsync();
        var warning = await projection.ObserveStoragePressureAsync(
            volume,
            VolumeSpaceSample.FromPercent(volume.VolumeRoot, 1_000_000, 14.999m),
            new DateTimeOffset(2026, 8, 24, 8, 59, 0, TimeSpan.Zero));
        Assert.Equal(StoragePressureStatuses.Warning, warning.Status);
        Assert.False(warning.IsPaused);
        Assert.NotNull((await projection.ReadExternallyReadableDemandCatalogAsync()).Snapshot);

        var entered = await projection.ObserveStoragePressureAsync(
            volume,
            VolumeSpaceSample.FromPercent(volume.VolumeRoot, 1_000_000, 9.999m),
            new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero));

        Assert.True(entered.IsPaused);
        Assert.Equal("poll-storage-pressure", (await projection.GetPollTraceAsync(
            "poll-storage-pressure")).Value!.PollTraceId);
        var attention = await projection.ReadCurrentIngestAttentionAsync(
            new CurrentIngestAttentionQuery());
        var storageAttention = Assert.Single(attention.Items.Where(item =>
            item.Kind == CurrentIngestAttentionKinds.StoragePressure));
        Assert.Equal(StoragePressureStatuses.Paused, storageAttention.ErrorCode);
        Assert.Equal(entered.PauseId, storageAttention.Evidence.EvidenceId);
        Assert.Equal(entered.Space.AvailablePercent, storageAttention.Evidence.AvailablePercent);
        Assert.True(attention.StoragePressure!.IsPaused);

        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE mesingest.StoragePressureState
                SET StoragePressureStatus = N'HEALTHY', AvailableBytes = TotalBytes
                WHERE Id = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var restarted = new SqlServerMesIngestProjection(database.ConnectionString);
        Assert.True((await restarted.ReadStoragePressureStateAsync()).IsPaused);
        var afterSpaceRecovered = await restarted.ObserveStoragePressureAsync(
            volume,
            VolumeSpaceSample.FromPercent(volume.VolumeRoot, 1_000_000, 50m),
            new DateTimeOffset(2026, 8, 24, 9, 5, 0, TimeSpan.Zero));
        Assert.True(afterSpaceRecovered.IsPaused);
        await Assert.ThrowsAsync<IngestNotCurrentException>(
            () => restarted.ReadExternallyReadableDemandCatalogAsync());

        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = _factory.WithWebHostBuilder(
            builder => builder.UseEnvironment(Environments.Production));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v2/externally-readable-demand-catalog");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            IngestNotCurrentException.ErrorCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Ticket01SqlServerFact]
    public async Task Authorized_local_recovery_audits_identity_and_reopens_only_the_exact_epoch()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var bootstrap = new SqlServerMesIngestProjection(database.ConnectionString);
        await bootstrap.BeginHostSessionAsync();
        var volume = await bootstrap.ResolveDatabaseVolumeAsync();
        var paused = await bootstrap.ObserveStoragePressureAsync(
            volume,
            VolumeSpaceSample.FromPercent(volume.VolumeRoot, 1_000_000, 9m),
            new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero));
        var productionContext = await new SqlServerLocalAdministrationContextProvider()
            .GetContextAsync(database.ConnectionString);
        Assert.True(productionContext.IsLocalDatabaseHost);
        Assert.True(productionContext.IsAuthorized);
        Assert.False(string.IsNullOrWhiteSpace(productionContext.ExecutionIdentity));
        var safeSpace = VolumeSpaceSample.FromPercent(volume.VolumeRoot, 1_000_000, 20m);
        var administration = new SqlServerMesIngestProjection(
            database.ConnectionString,
            volumeSpaceReader: new FixedVolumeSpaceReader(safeSpace),
            localAdministrationContextProvider: new FixedAdministrationContextProvider(
                new LocalAdministrationContext(
                    @"FACTORY\authorized-operator",
                    Environment.MachineName,
                    IsLocalDatabaseHost: true,
                    IsAuthorized: true)));

        var wrongEpoch = await Assert.ThrowsAsync<StoragePressureAdministrationException>(() =>
            administration.ResumeStoragePressureAsync(new StoragePressureRecoveryRequest(
                paused.DatabaseName,
                HistoryEpoch.CreateNew(),
                "expanded database volume")));
        Assert.Equal(StoragePressureAdministrationErrorCodes.WrongHistoryEpoch, wrongEpoch.Code);
        Assert.True((await bootstrap.ReadStoragePressureStateAsync()).IsPaused);

        var recovered = await administration.ResumeStoragePressureAsync(
            new StoragePressureRecoveryRequest(
                paused.DatabaseName,
                paused.HistoryEpoch,
                "expanded database volume"));
        var repeated = await administration.ResumeStoragePressureAsync(
            new StoragePressureRecoveryRequest(
                paused.DatabaseName,
                paused.HistoryEpoch,
                "expanded database volume"));

        Assert.False(recovered.IsPaused);
        Assert.Equal(recovered.RecoveryAuditId, repeated.RecoveryAuditId);
        Assert.NotNull(recovered.RecoveryAuditId);
        Assert.Equal(0L, (await administration.ReadExternallyReadableDemandCatalogAsync())
            .CatalogRevision);

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ExecutionIdentity, Reason, HistoryEpoch, StatusBefore, StatusAfter,
                   COUNT(*) OVER ()
            FROM mesingest.StoragePressureRecoveryAudits;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(@"FACTORY\authorized-operator", reader.GetString(0));
        Assert.Equal("expanded database volume", reader.GetString(1));
        Assert.Equal(paused.HistoryEpoch.Value, reader.GetGuid(2));
        Assert.Equal(StoragePressureStatuses.Paused, reader.GetString(3));
        Assert.Equal(StoragePressureStatuses.Healthy, reader.GetString(4));
        Assert.Equal(1, reader.GetInt32(5));
    }

    private static MesTaskUnionRound SuccessRound(string pollTraceId)
    {
        var at = new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);
        return new MesTaskUnionRound(
            pollTraceId,
            "storage-pressure-test-v1",
            MesTaskUnionRoundOutcome.Success,
            at,
            at.AddSeconds(1),
            [new MesTaskUnionObservation("WIRE_TO_NITROGEN", "SP-001", "A1", "EQ-1", "STEP", at, "PKG")]);
    }

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
        });

    private sealed class FixedVolumeSpaceReader(VolumeSpaceSample sample) : IVolumeSpaceReader
    {
        public VolumeSpaceSample Read(string resolvedVolumeRoot)
        {
            Assert.Equal(sample.VolumeRoot, resolvedVolumeRoot, ignoreCase: true);
            return sample;
        }
    }

    private sealed class FixedAdministrationContextProvider(LocalAdministrationContext context) :
        ILocalAdministrationContextProvider
    {
        public Task<LocalAdministrationContext> GetContextAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(context);
        }
    }
}
