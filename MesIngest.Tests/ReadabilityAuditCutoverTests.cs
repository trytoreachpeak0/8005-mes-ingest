using System.Text.Json;
using System.Net;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class ReadabilityAuditCutoverTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AuditPath = "/api/v2/readability-audit?pageSize=20";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public ReadabilityAuditCutoverTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Ticket01SqlServerFact]
    public async Task Current_audit_list_does_not_wait_for_raw_history()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var at = new DateTimeOffset(2026, 8, 24, 1, 0, 0, TimeSpan.Zero);

        var receipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-current",
            at,
            Observation("SL-TICKET09-CURRENT", "N3-3", "WB-09", "QFN-09")));

        await using var blocker = new SqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var blockingTransaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = blockingTransaction;
            lockCommand.CommandText =
                "SELECT COUNT_BIG(*) FROM mesingest.DemandRawObservations WITH (TABLOCKX, HOLDLOCK);";
            Assert.True(Convert.ToInt64(await lockCommand.ExecuteScalarAsync()) > 0);
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync(AuditPath, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            Assert.True(response.IsSuccessStatusCode, body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(
                receipt.ProjectionCommitId,
                json.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
            Assert.Equal(1L, json.RootElement.GetProperty("exactTotalDemandCount").GetInt64());
            var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("READABLE", item.GetProperty("externalReadabilityState").GetString());
            Assert.Empty(item.GetProperty("readabilityBlockers").EnumerateArray());
            Assert.Equal("N3-3", item.GetProperty("liveMesFields").GetProperty("area").GetString());
        }
        finally
        {
            await blockingTransaction.RollbackAsync();
        }

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Audit_snapshot_exposes_the_database_history_epoch()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var at = new DateTimeOffset(2026, 8, 24, 1, 15, 0, TimeSpan.Zero);

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-epoch",
            at,
            Observation("SL-TICKET09-EPOCH", "N3-3", "WB-09", "QFN-09")));
        var expectedHistoryEpoch = await ReadHistoryEpochAsync(database.ConnectionString);

        using var response = await client.GetAsync(AuditPath);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            expectedHistoryEpoch.ToString("D"),
            json.RootElement.GetProperty("snapshot").GetProperty("historyEpoch").GetString());

        var snapshot = json.RootElement.GetProperty("snapshot");
        var signingKey = await ReadSnapshotSigningKeyAsync(database.ConnectionString);
        var foreignEpochReference = ReadabilityAuditTokenCodec.CreateSnapshotReference(
            new ReadabilityAuditSnapshotIdentity(
                HistoryEpoch.CreateNew(),
                snapshot.GetProperty("projectionCommitId").GetString()!,
                snapshot.GetProperty("projectionSequence").GetInt64(),
                snapshot.GetProperty("projectionCommittedAt").GetDateTimeOffset(),
                snapshot.GetProperty("pollTraceId").GetString()!,
                snapshot.GetProperty("catalogRevision").GetInt64()),
            signingKey);
        using var rejected = await client.GetAsync(
            AuditPath + "&snapshot=" + Uri.EscapeDataString(foreignEpochReference));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var rejection = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal(
            ReadabilityAuditErrorCodes.SnapshotMismatch,
            rejection.RootElement.GetProperty("code").GetString());
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Audit_list_and_detail_are_wholly_old_or_new_at_a_concurrent_commit_fence()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var observer = new GatedProjectionReadBoundaryObserver();
        await using var factory = CreateFactory(observer);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var at = new DateTimeOffset(2026, 8, 24, 1, 30, 0, TimeSpan.Zero);
        const string sublot = "SL-TICKET09-CONCURRENT";

        var receiptA = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-concurrent-a",
            at,
            Observation(sublot, "N03-08", "WB-09", "QFN-09")));
        var expectedListA = await ReadRawSuccessAsync(client, AuditPath);
        using var listAJson = JsonDocument.Parse(expectedListA);
        var itemA = Assert.Single(listAJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            "INVALID_MES_FIELD_FORMAT",
            itemA.GetProperty("leadReadabilityBlocker").GetString());
        var demandId = itemA.GetProperty("demandId").GetString()!;
        var snapshotReference = listAJson.RootElement.GetProperty("snapshotReference").GetString()!;
        var catalogRevisionA = listAJson.RootElement.GetProperty("snapshot")
            .GetProperty("catalogRevision").GetInt64();
        var historyEpoch = await ReadHistoryEpochAsync(database.ConnectionString);
        var frozenListPath = AuditPath + "&snapshot=" + Uri.EscapeDataString(snapshotReference);
        var frozenDetailPath = "/api/v2/readability-audit/" + Uri.EscapeDataString(demandId)
            + "?snapshot=" + Uri.EscapeDataString(snapshotReference);

        var readGate = observer.Arm(ReadabilityAuditSurface(), expectedInvocationCount: 1);
        var pendingList = ReadRawSuccessAsync(client, frozenListPath);
        var selectedFences = await readGate.Selected.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(selectedFences);
        Assert.All(selectedFences, fence =>
        {
            Assert.Equal(receiptA.ProjectionCommitId, fence.ProjectionCommitId);
            Assert.Equal(receiptA.ProjectionSequence, fence.ProjectionSequence);
            Assert.Equal(historyEpoch, fence.HistoryEpoch.Value);
            Assert.Equal(catalogRevisionA, fence.CatalogRevision);
        });

        var writerB = ingestor.IngestAsync(SuccessRound(
            "poll-ticket09-concurrent-b",
            at.AddMinutes(1),
            Observation(sublot, null, "WB-09", "QFN-09")));
        RoundCommitReceipt receiptB;
        try
        {
            receiptB = await writerB.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            readGate.Release();
        }

        using var frozenListA = JsonDocument.Parse(
            await pendingList.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            receiptA.ProjectionCommitId,
            frozenListA.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        var frozenItemA = Assert.Single(
            frozenListA.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(demandId, frozenItemA.GetProperty("demandId").GetString());
        Assert.Equal(
            itemA.GetProperty("leadReadabilityBlocker").GetString(),
            frozenItemA.GetProperty("leadReadabilityBlocker").GetString());
        Assert.Equal(
            itemA.GetProperty("liveMesFields").GetProperty("mesSourceDate").GetDateTimeOffset(),
            frozenItemA.GetProperty("liveMesFields").GetProperty("mesSourceDate").GetDateTimeOffset());

        var detailGate = observer.Arm(ReadabilityAuditSurface(), expectedInvocationCount: 1);
        var pendingDetail = ReadRawSuccessAsync(client, frozenDetailPath);
        var detailFences = await detailGate.Selected.WaitAsync(TimeSpan.FromSeconds(10));
        var detailFence = Assert.Single(detailFences);
        Assert.Equal(receiptA.ProjectionCommitId, detailFence.ProjectionCommitId);
        Assert.Equal(receiptA.ProjectionSequence, detailFence.ProjectionSequence);
        detailGate.Release();
        using var detailA = JsonDocument.Parse(
            await pendingDetail.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            receiptA.ProjectionCommitId,
            detailA.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(
            catalogRevisionA,
            detailA.RootElement.GetProperty("snapshot").GetProperty("catalogRevision").GetInt64());
        Assert.Equal(
            "INVALID_MES_FIELD_FORMAT",
            Assert.Single(detailA.RootElement.GetProperty("blockers").EnumerateArray())
                .GetProperty("code").GetString());
        Assert.All(
            detailA.RootElement.GetProperty("blockers").EnumerateArray(),
            blocker => Assert.NotEmpty(blocker.GetProperty("evidence").EnumerateArray()));

        using var refreshed = JsonDocument.Parse(await ReadRawSuccessAsync(client, AuditPath));
        Assert.Equal(
            receiptB.ProjectionCommitId,
            refreshed.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(
            "REQUIRED_MES_FIELD_MISSING",
            Assert.Single(refreshed.RootElement.GetProperty("items").EnumerateArray())
                .GetProperty("leadReadabilityBlocker").GetString());
        AssertDatabaseEvidence(database);
    }

    private static ProjectionReadSurface ReadabilityAuditSurface() =>
        Enum.Parse<ProjectionReadSurface>("ReadabilityAudit", ignoreCase: false);

    private static MesTaskUnionObservation Observation(
        string sublot,
        string? area,
        string? eqp,
        string package) =>
        new(
            "WIRE_TO_NITROGEN",
            sublot,
            area,
            eqp,
            "焊线2",
            new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.FromHours(8)),
            package);

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket09-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static async Task<Guid> ReadHistoryEpochAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT HistoryEpoch FROM mesingest.SchemaInfo WHERE Id = 1;";
        return Assert.IsType<Guid>(await command.ExecuteScalarAsync());
    }

    private static async Task<byte[]> ReadSnapshotSigningKeyAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SnapshotTokenSigningKey FROM mesingest.SchemaInfo WHERE Id = 1;";
        return Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadRawSuccessAsync(HttpClient client, string uri)
    {
        using var response = await client.GetAsync(uri);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return body;
    }

    private WebApplicationFactory<Program> CreateFactory(
        IProjectionReadBoundaryObserver? readBoundaryObserver = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            if (readBoundaryObserver is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IProjectionReadBoundaryObserver>();
                    services.AddSingleton(readBoundaryObserver);
                });
            }
        });

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__SnapshotSource"] = "Oracle",
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
        });

    private void AssertDatabaseEvidence(Ticket01SqlServerDatabase database)
    {
        Assert.False(database.IsLocalDb);
        Assert.Equal(database.ExpectedProductMajor, database.ProductMajor);
        Assert.Equal(database.ExpectedCompatibilityLevel, database.CompatibilityLevel);
        _output.WriteLine(
            $"SQL Server {database.ProductVersion}; compatibility {database.CompatibilityLevel}");
    }

    private sealed class GatedProjectionReadBoundaryObserver : IProjectionReadBoundaryObserver
    {
        private readonly object _sync = new();
        private ReadGate? _gate;

        public ReadGate Arm(ProjectionReadSurface surface, int expectedInvocationCount)
        {
            var gate = new ReadGate(surface, expectedInvocationCount);
            lock (_sync)
            {
                _gate = gate;
            }

            return gate;
        }

        public Task OnFenceSelectedAsync(
            ProjectionReadSurface surface,
            ProjectionReadFence fence,
            CancellationToken cancellationToken)
        {
            ReadGate? claimed;
            lock (_sync)
            {
                claimed = _gate is not null && _gate.TryClaim(surface, fence)
                    ? _gate
                    : null;
            }

            return claimed is null
                ? Task.CompletedTask
                : claimed.WaitForReleaseAsync(cancellationToken);
        }
    }

    private sealed class ReadGate
    {
        private readonly int _expectedInvocationCount;
        private readonly List<ProjectionReadFence> _fences = [];
        private readonly TaskCompletionSource<IReadOnlyList<ProjectionReadFence>> _selected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReadGate(ProjectionReadSurface surface, int expectedInvocationCount)
        {
            Assert.True(expectedInvocationCount > 0);
            Surface = surface;
            _expectedInvocationCount = expectedInvocationCount;
        }

        public ProjectionReadSurface Surface { get; }
        public Task<IReadOnlyList<ProjectionReadFence>> Selected => _selected.Task;

        public bool TryClaim(ProjectionReadSurface surface, ProjectionReadFence fence)
        {
            if (surface != Surface || _fences.Count >= _expectedInvocationCount)
            {
                return false;
            }

            _fences.Add(fence);
            if (_fences.Count == _expectedInvocationCount)
            {
                _selected.TrySetResult(_fences.ToArray());
            }

            return true;
        }

        public Task WaitForReleaseAsync(CancellationToken cancellationToken) =>
            _release.Task.WaitAsync(cancellationToken);

        public void Release() => _release.TrySetResult(true);
    }
}
