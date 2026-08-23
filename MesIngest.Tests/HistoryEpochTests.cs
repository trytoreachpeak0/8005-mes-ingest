using System.Collections.Concurrent;
using System.Data;
using MesIngest.Core.SeriesProjection;
using MesIngest.Infrastructure.SqlServer;
using Microsoft.Data.SqlClient;

namespace MesIngest.Tests;

public sealed class HistoryEpochTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 23, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void History_epoch_value_rejects_an_empty_guid()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => HistoryEpoch.FromGuid(Guid.Empty));
        Assert.Contains("non-empty", exception.Message, StringComparison.Ordinal);
    }

    [Ticket01SqlServerFact]
    public async Task Concurrent_bootstrap_fault_recovery_and_new_host_sessions_preserve_one_epoch_on_commits_and_read_fences()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var failingCheckpoint = new OneShotFailingCheckpointObserver();
        var first = new SqlServerMesIngestProjection(database.ConnectionString);
        var concurrent = new SqlServerMesIngestProjection(database.ConnectionString);

        await Task.WhenAll(
            first.BeginHostSessionAsync(),
            concurrent.BeginHostSessionAsync());

        var identity = await ReadSchemaIdentityAsync(database.ConnectionString);
        Assert.Equal(1, identity.RowCount);
        Assert.Equal(NewMesIngestContract.SchemaVersion, identity.SchemaVersion);
        Assert.NotEqual(Guid.Empty, identity.HistoryEpoch);

        var failing = new SqlServerMesIngestProjection(
            database.ConnectionString,
            checkpointObserver: failingCheckpoint);
        await failing.BeginHostSessionAsync();
        await Assert.ThrowsAsync<SqlException>(
            () => failing.CommitRoundAsync(SuccessRound("poll-history-epoch-fault")));
        Assert.Equal(identity.HistoryEpoch, failingCheckpoint.ObservedHistoryEpoch?.Value);
        Assert.Equal(
            identity.HistoryEpoch,
            (await ReadSchemaIdentityAsync(database.ConnectionString)).HistoryEpoch);

        var recoveredReads = new RecordingReadBoundaryObserver();
        var recovered = new SqlServerMesIngestProjection(
            database.ConnectionString,
            readBoundaryObserver: recoveredReads);
        var recoveredReceipt = await recovered.CommitRoundAsync(
            SuccessRound("poll-history-epoch-recovered"));

        Assert.Equal(identity.HistoryEpoch, recoveredReceipt.HistoryEpoch?.Value);
        Assert.NotNull(recoveredReceipt.ProjectionCommitId);
        Assert.Equal(
            identity.HistoryEpoch,
            await ReadCommitHistoryEpochAsync(
                database.ConnectionString,
                recoveredReceipt.ProjectionCommitId!));
        Assert.NotEqual(
            identity.HistoryEpoch.ToString("N"),
            recoveredReceipt.ProjectionCommitId);

        var list = await recovered.ListDemandSeriesAsync(
            new DemandSeriesBrowseQuery(new DemandSeriesBrowseFilter()));
        var catalog = await recovered.ReadExternallyReadableDemandCatalogAsync();
        var attention = await recovered.ReadCurrentIngestAttentionAsync(
            new CurrentIngestAttentionQuery());

        Assert.Single(list.Items);
        Assert.NotNull(catalog.Snapshot);
        Assert.NotNull(attention.Snapshot);
        Assert.Equal(
            new[]
            {
                ProjectionReadSurface.DemandSeries,
                ProjectionReadSurface.ReadabilityAudit,
                ProjectionReadSurface.Catalog,
                ProjectionReadSurface.CurrentIngestAttention,
            },
            recoveredReads.Fences.Keys.Order().ToArray());
        Assert.All(
            recoveredReads.Fences.Values,
            fence => Assert.Equal(identity.HistoryEpoch, fence.HistoryEpoch.Value));
        Assert.All(
            recoveredReads.Fences.Values,
            fence => Assert.Equal(recoveredReceipt.ProjectionCommitId, fence.ProjectionCommitId));

        var hostSessionBeforeRestart = await ReadCurrentHostSessionIdAsync(
            database.ConnectionString);
        var restarted = new SqlServerMesIngestProjection(database.ConnectionString);
        await restarted.BeginHostSessionAsync();
        var hostSessionAfterRestart = await ReadCurrentHostSessionIdAsync(
            database.ConnectionString);

        Assert.NotEqual(hostSessionBeforeRestart, hostSessionAfterRestart);
        Assert.NotEqual(identity.HistoryEpoch.ToString("N"), hostSessionAfterRestart);
        Assert.Equal(
            identity.HistoryEpoch,
            (await ReadSchemaIdentityAsync(database.ConnectionString)).HistoryEpoch);
    }

    [Ticket01SqlServerFact]
    public async Task Planned_empty_database_and_unrecoverable_rebuild_requests_create_distinct_epochs()
    {
        await using var plannedDatabase = await Ticket01SqlServerDatabase.CreateAsync();
        await using var rebuiltDatabase = await Ticket01SqlServerDatabase.CreateAsync();

        await new SqlServerMesIngestProjection(
                plannedDatabase.ConnectionString,
                historyEpochBootstrapIntent: HistoryEpochBootstrapIntent.PlannedEmptyDatabase)
            .BeginHostSessionAsync();
        await new SqlServerMesIngestProjection(
                rebuiltDatabase.ConnectionString,
                historyEpochBootstrapIntent: HistoryEpochBootstrapIntent.UnrecoverableRebuild)
            .BeginHostSessionAsync();

        var planned = await ReadSchemaIdentityAsync(plannedDatabase.ConnectionString);
        var rebuilt = await ReadSchemaIdentityAsync(rebuiltDatabase.ConnectionString);
        Assert.NotEqual(Guid.Empty, planned.HistoryEpoch);
        Assert.NotEqual(Guid.Empty, rebuilt.HistoryEpoch);
        Assert.NotEqual(planned.HistoryEpoch, rebuilt.HistoryEpoch);
    }

    [Ticket01SqlServerFact]
    public async Task New_epoch_requests_refuse_a_nonempty_database_without_rotating_its_epoch()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .BeginHostSessionAsync();
        var original = await ReadSchemaIdentityAsync(database.ConnectionString);

        foreach (var intent in Enum.GetValues<HistoryEpochBootstrapIntent>())
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new SqlServerMesIngestProjection(
                        database.ConnectionString,
                        historyEpochBootstrapIntent: intent)
                    .BeginHostSessionAsync());
            Assert.Contains("empty database", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                original.HistoryEpoch,
                (await ReadSchemaIdentityAsync(database.ConnectionString)).HistoryEpoch);
        }
    }

    [Ticket01SqlServerFact]
    public async Task Existing_schema_without_history_epoch_structure_is_strictly_rejected()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        await new SqlServerMesIngestProjection(database.ConnectionString)
            .BeginHostSessionAsync();

        await ExecuteAsync(
            database.ConnectionString,
            """
            ALTER TABLE mesingest.ProjectionCommits
                DROP CONSTRAINT FK_MesIngest_ProjectionCommits_HistoryEpoch;
            ALTER TABLE mesingest.SchemaInfo
                DROP CONSTRAINT UQ_MesIngest_SchemaInfo_HistoryEpoch;
            ALTER TABLE mesingest.ProjectionCommits DROP COLUMN HistoryEpoch;
            ALTER TABLE mesingest.SchemaInfo DROP COLUMN HistoryEpoch;
            """);

        var exception = await Assert.ThrowsAsync<SqlException>(
            () => new SqlServerMesIngestProjection(database.ConnectionString)
                .BeginHostSessionAsync());
        Assert.Contains("HistoryEpoch", exception.Message, StringComparison.Ordinal);
    }

    private static MesTaskUnionRound SuccessRound(string pollTraceId) =>
        new(
            pollTraceId,
            "history-epoch-query-v1",
            MesTaskUnionRoundOutcome.Success,
            At,
            At.AddSeconds(1),
            [
                new MesTaskUnionObservation(
                    "HISTORY-EPOCH-WORK",
                    "SL-HISTORY-EPOCH",
                    "A1-1",
                    "EQP-HISTORY-EPOCH",
                    "入库",
                    At,
                    "PKG-HISTORY-EPOCH"),
            ]);

    private static async Task<SchemaIdentity> ReadSchemaIdentityAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*), MAX(SchemaVersion), MAX(HistoryEpoch) FROM mesingest.SchemaInfo;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new SchemaIdentity(reader.GetInt32(0), reader.GetInt32(1), reader.GetGuid(2));
    }

    private static async Task<string> ReadCurrentHostSessionIdAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT HostSessionId FROM mesingest.HostSessions WHERE IsCurrent = 1;";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> ReadCommitHistoryEpochAsync(
        string connectionString,
        string projectionCommitId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT HistoryEpoch FROM mesingest.ProjectionCommits "
            + "WHERE ProjectionCommitId = @projectionCommitId;";
        command.Parameters.Add("@projectionCommitId", SqlDbType.NVarChar, 64).Value =
            projectionCommitId;
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record SchemaIdentity(int RowCount, int SchemaVersion, Guid HistoryEpoch);

    private sealed class RecordingReadBoundaryObserver : IProjectionReadBoundaryObserver
    {
        public ConcurrentDictionary<ProjectionReadSurface, ProjectionReadFence> Fences { get; } = new();

        public Task OnFenceSelectedAsync(
            ProjectionReadSurface surface,
            ProjectionReadFence fence,
            CancellationToken cancellationToken)
        {
            Fences[surface] = fence;
            return Task.CompletedTask;
        }
    }

    private sealed class OneShotFailingCheckpointObserver : IProjectionCommitCheckpointObserver
    {
        private int _failed;

        public HistoryEpoch? ObservedHistoryEpoch { get; private set; }

        public async Task OnCheckpointAsync(
            ProjectionCommitCheckpoint checkpoint,
            ProjectionCommitCheckpointContext context,
            SqlConnection connection,
            SqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            if (checkpoint != ProjectionCommitCheckpoint.RoundEvidencePersisted
                || Interlocked.Exchange(ref _failed, 1) != 0)
            {
                return;
            }

            ObservedHistoryEpoch = context.HistoryEpoch;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "THROW 51098, 'HistoryEpoch test fault.', 1;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
