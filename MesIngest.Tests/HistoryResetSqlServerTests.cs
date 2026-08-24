using System.Net;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.Infrastructure.SqlServer;
using MesIngest.ReferenceConsumer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class HistoryResetSqlServerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HistoryResetSqlServerTests(WebApplicationFactory<Program> factory) =>
        _factory = factory;

    [Ticket01SqlServerFact]
    public async Task Unrecoverable_rebuild_stays_not_current_until_the_exact_risk_acknowledgement()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var projection = new SqlServerMesIngestProjection(
            database.ConnectionString,
            historyEpochBootstrapIntent: HistoryEpochBootstrapIntent.UnrecoverableRebuild);
        await projection.BeginHostSessionAsync();
        var volume = await projection.ResolveDatabaseVolumeAsync();

        await projection.CommitRoundAsync(SuccessRound("poll-history-reset-1", 1));
        await projection.CommitRoundAsync(SuccessRound("poll-history-reset-2", 2));
        await projection.CommitRoundAsync(SuccessRound("poll-history-reset-3", 3));
        await projection.ObserveStoragePressureAsync(
            volume,
            VolumeSpaceSample.FromPercent(volume.VolumeRoot, 1_000_000, 50m),
            new DateTimeOffset(2026, 8, 24, 12, 5, 0, TimeSpan.Zero));

        var diagnostics = await projection.ReadCurrentIngestAttentionAsync(
            new CurrentIngestAttentionQuery());
        var historyReset = Assert.Single(diagnostics.Items.Where(item =>
            item.Kind == CurrentIngestAttentionKinds.HistoryReset));
        Assert.Equal(HistoryResetStatuses.AcknowledgementRequired, historyReset.ErrorCode);
        Assert.Equal(
            (await projection.ReadHistoryResetStateAsync()).HistoryEpoch,
            diagnostics.Snapshot.HistoryEpoch);
        Assert.Equal(
            RestartBarrierPhaseContract.Normal,
            (await projection.GetAbsenceAuthorityAsync()).Phase);
        await Assert.ThrowsAsync<IngestNotCurrentException>(
            () => projection.ReadExternallyReadableDemandCatalogAsync());

        using (ConfigureProductionV2Environment(database.ConnectionString))
        await using (var factory = _factory.WithWebHostBuilder(
            builder => builder.UseEnvironment(Environments.Production)))
        {
            using var httpClient = factory.CreateClient();
            using var response = await httpClient.GetAsync(
                HttpExternallyReadableDemandCatalogClient.CatalogPath);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(
                IngestNotCurrentException.ErrorCode,
                body.RootElement.GetProperty("code").GetString());

            var referenceClient = new HttpExternallyReadableDemandCatalogClient(httpClient);
            var referenceFailure = await Assert.ThrowsAsync<HttpRequestException>(
                () => referenceClient.ReadAsync(knownIdentity: null));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, referenceFailure.StatusCode);
        }

        var restarted = new SqlServerMesIngestProjection(database.ConnectionString);
        await restarted.BeginHostSessionAsync();

        Assert.True((await restarted.ReadHistoryResetStateAsync()).RequiresAcknowledgement);
        await Assert.ThrowsAsync<IngestNotCurrentException>(
            () => restarted.ReadExternallyReadableDemandCatalogAsync());

        var acknowledgedAt = new DateTimeOffset(2026, 8, 24, 12, 10, 0, TimeSpan.Zero);
        var administration = new SqlServerMesIngestProjection(
            database.ConnectionString,
            timeProvider: new FixedTimeProvider(acknowledgedAt),
            localAdministrationContextProvider: new FixedAdministrationContextProvider(
                new LocalAdministrationContext(
                    @"FACTORY\authorized-operator",
                    Environment.MachineName,
                    IsLocalDatabaseHost: true,
                    IsAuthorized: true)));
        var required = await administration.ReadHistoryResetStateAsync();
        var request = new HistoryResetAcknowledgementRequest(
            required.DatabaseName,
            required.HistoryEpoch,
            "accept unrecoverable loss before reopening the new epoch",
            HistoryResetAcknowledgementPolicy.RequiredRiskAcceptance);

        var acknowledged = await administration.AcknowledgeHistoryResetAsync(request);
        var repeated = await administration.AcknowledgeHistoryResetAsync(request);

        Assert.Equal(HistoryResetStatuses.Acknowledged, acknowledged.Status);
        Assert.Equal(acknowledged.AcknowledgementAuditId, repeated.AcknowledgementAuditId);
        Assert.NotNull(acknowledged.AcknowledgementAuditId);
        Assert.NotNull((await administration.ReadExternallyReadableDemandCatalogAsync()).Snapshot);
        await Assert.ThrowsAsync<HistoryEpochMismatchException>(() =>
            administration.ReadExternallyReadableDemandCatalogAsync(
                new ExternallyReadableDemandCatalogIdentity(
                    HistoryEpoch.CreateNew(),
                    CatalogRevision: 0)));

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Operation, OperationTargetId, ExecutionIdentity, Reason,
                   RiskAcceptance, HistoryEpoch, StatusBefore, StatusAfter,
                   OccurredAt, COUNT(*) OVER ()
            FROM mesingest.LocalAdministrationAudits;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("HISTORY_RESET_ACKNOWLEDGEMENT", reader.GetString(0));
        Assert.Equal(required.HistoryEpoch.Value.ToString("D"), reader.GetString(1));
        Assert.Equal(@"FACTORY\authorized-operator", reader.GetString(2));
        Assert.Equal(request.Reason, reader.GetString(3));
        Assert.Equal(request.RiskAcceptance, reader.GetString(4));
        Assert.Equal(required.HistoryEpoch.Value, reader.GetGuid(5));
        Assert.Equal(HistoryResetStatuses.AcknowledgementRequired, reader.GetString(6));
        Assert.Equal(HistoryResetStatuses.Acknowledged, reader.GetString(7));
        Assert.Equal(acknowledgedAt, reader.GetFieldValue<DateTimeOffset>(8));
        Assert.Equal(1, reader.GetInt32(9));
    }

    [Ticket01SqlServerFact]
    public async Task Wrong_epoch_and_direct_status_edit_cannot_acknowledge_the_unrecoverable_rebuild()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var projection = new SqlServerMesIngestProjection(
            database.ConnectionString,
            historyEpochBootstrapIntent: HistoryEpochBootstrapIntent.UnrecoverableRebuild);
        await projection.BeginHostSessionAsync();
        var required = await projection.ReadHistoryResetStateAsync();

        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE mesingest.SchemaInfo
                SET HistoryResetStatus = N'ACKNOWLEDGED',
                    HistoryResetAcknowledgementAuditId = N'direct-sql-is-not-an-audit'
                WHERE Id = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        Assert.True((await projection.ReadHistoryResetStateAsync()).RequiresAcknowledgement);
        var administration = new SqlServerMesIngestProjection(
            database.ConnectionString,
            localAdministrationContextProvider: new FixedAdministrationContextProvider(
                new LocalAdministrationContext(
                    @"FACTORY\authorized-operator",
                    Environment.MachineName,
                    IsLocalDatabaseHost: true,
                    IsAuthorized: true)));
        var wrongEpoch = await Assert.ThrowsAsync<HistoryResetAdministrationException>(() =>
            administration.AcknowledgeHistoryResetAsync(
                new HistoryResetAcknowledgementRequest(
                    required.DatabaseName,
                    HistoryEpoch.CreateNew(),
                    "wrong epoch must not open current reads",
                    HistoryResetAcknowledgementPolicy.RequiredRiskAcceptance)));

        Assert.Equal(HistoryResetAdministrationErrorCodes.WrongHistoryEpoch, wrongEpoch.Code);
        Assert.True((await administration.ReadHistoryResetStateAsync()).RequiresAcknowledgement);
        await Assert.ThrowsAsync<IngestNotCurrentException>(
            () => administration.ReadExternallyReadableDemandCatalogAsync());
        await using var auditConnection = new SqlConnection(database.ConnectionString);
        await auditConnection.OpenAsync();
        await using var auditCount = auditConnection.CreateCommand();
        auditCount.CommandText = "SELECT COUNT(*) FROM mesingest.LocalAdministrationAudits;";
        Assert.Equal(0, Convert.ToInt32(await auditCount.ExecuteScalarAsync()));
    }

    private static MesTaskUnionRound SuccessRound(string pollTraceId, int minute)
    {
        var at = new DateTimeOffset(2026, 8, 24, 12, minute, 0, TimeSpan.Zero);
        return new MesTaskUnionRound(
            pollTraceId,
            "history-reset-test-v1",
            MesTaskUnionRoundOutcome.Success,
            at,
            at.AddSeconds(1),
            [new MesTaskUnionObservation(
                "WIRE_TO_NITROGEN",
                "HISTORY-RESET-001",
                "A1",
                "EQ-1",
                "STEP",
                at,
                "PKG")]);
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

    private sealed class FixedAdministrationContextProvider(LocalAdministrationContext context) :
        ILocalAdministrationContextProvider
    {
        public Task<LocalAdministrationContext> GetContextAsync(
            string connectionString,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(context);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
