using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.Infrastructure.SqlServer;
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
public sealed class ProjectionCommitAtomicityConcurrencyTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string QueryVersion = "mes-task-union-ticket16-v1";
    private const string Ticket12RawSecret = "ticket12-frozen-read-secret";
    private const string SeriesListPath = "/api/v2/demand-series?pageSize=100";
    private const string CatalogPath = "/api/v2/externally-readable-demand-catalog";
    private const string AttentionPath =
        "/api/v2/current-ingest-attention?pageSize=200&pageNumber=1";
    private static readonly DateTimeOffset EvidenceDate =
        new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public ProjectionCommitAtomicityConcurrencyTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Ticket01SqlServerFact]
    public async Task Six_SQL_failpoints_roll_back_every_public_surface_then_retry_replay_and_conflict_are_atomic()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var at = new DateTimeOffset(2026, 8, 16, 1, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(at);
        var observer = new SqlFaultCheckpointObserver();
        var passedCheckpoints = new List<ProjectionCommitCheckpoint>();
        BusinessSurfaceFingerprint committedFingerprint;

        await using (var factory = CreateFactory(clock, checkpointObserver: observer))
        {
            using var client = factory.CreateClient();
            var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
            var baselineRows = AtomicBaselineRows();
            await ingestor.IngestAsync(SuccessRound("poll-ticket16-atomic-seed-1", at, baselineRows));
            await ingestor.IngestAsync(SuccessRound("poll-ticket16-atomic-seed-2", at.AddMinutes(1), baselineRows));
            var baseline = await ReadAtomicBusinessFingerprintAsync(client);
            using (var baselineErrorDocument = JsonDocument.Parse(baseline.SecondarySeriesDetail!))
            {
                var baselineError = baselineErrorDocument.RootElement;
                Assert.Contains(
                    baselineError.GetProperty("currentConditions").EnumerateArray(),
                    item => item.GetProperty("subjectKind").GetString() == "EQP");
                Assert.Contains(
                    baselineError.GetProperty("errorPeriods").EnumerateArray(),
                    item => item.GetProperty("endedAt").ValueKind == JsonValueKind.Null);
            }

            var faultRounds = new List<MesTaskUnionRound>();
            var checkpoints = Enum.GetValues<ProjectionCommitCheckpoint>();
            for (var index = 0; index < checkpoints.Length; index++)
            {
                var checkpoint = checkpoints[index];
                var pollTraceId = $"poll-ticket16-fault-{index + 1}";
                var round = SuccessRound(
                    pollTraceId,
                    at.AddMinutes(10 + index),
                    AtomicRecoveryRows());
                faultRounds.Add(round);
                observer.Arm(pollTraceId, checkpoint);

                var failure = await Assert.ThrowsAsync<SqlException>(
                    () => ingestor.IngestAsync(round));
                Assert.Equal(SqlFaultCheckpointObserver.ErrorNumber, failure.Number);
                Assert.True(observer.WasTriggered(pollTraceId, checkpoint));
                await AssertPollTraceNotFoundAsync(client, pollTraceId);
                Assert.Equal(
                    baseline,
                    await ReadAtomicBusinessFingerprintAsync(client));
                passedCheckpoints.Add(checkpoint);
            }

            Assert.Equal(Enum.GetValues<ProjectionCommitCheckpoint>(), passedCheckpoints);
            var retryRound = faultRounds[^1];
            var retry = await ingestor.IngestAsync(retryRound);
            Assert.False(retry.IsReplay);
            Assert.NotNull(retry.ProjectionCommitId);
            Assert.NotNull(retry.ProjectionSequence);

            var replay = await ingestor.IngestAsync(retryRound);
            Assert.True(replay.IsReplay);
            Assert.Equal(retry.ProjectionCommitId, replay.ProjectionCommitId);
            Assert.Equal(retry.ProjectionSequence, replay.ProjectionSequence);
            Assert.Equal(retry.SeriesIds, replay.SeriesIds);
            Assert.Equal(retry.DemandIds, replay.DemandIds);

            var beforeConflict = await ReadAtomicBusinessFingerprintAsync(client);
            var conflictingRound = retryRound with
            {
                Observations = retryRound.Observations
                    .Concat(
                    [
                        Observation(
                            "ATOMIC-CONFLICT",
                            "SL-TICKET16-CONFLICT",
                            "N2-3"),
                    ])
                    .ToArray(),
            };
            var conflict = await Assert.ThrowsAsync<PollTraceConflictException>(
                () => ingestor.IngestAsync(conflictingRound));
            Assert.Equal(retryRound.PollTraceId, conflict.PollTraceId);
            Assert.Equal(
                beforeConflict,
                await ReadAtomicBusinessFingerprintAsync(client));

            var acceptedTrace = await ReadJsonAsync(
                client,
                $"/api/v2/poll-traces/{Uri.EscapeDataString(retryRound.PollTraceId)}");
            Assert.Equal(
                retry.ProjectionCommitId,
                acceptedTrace.GetProperty("projectionCommit").GetProperty("projectionCommitId").GetString());
            committedFingerprint = beforeConflict;
        }

        await using (var restartedFactory = CreateFactory(clock))
        {
            using var restartedClient = restartedFactory.CreateClient();
            Assert.Equal(
                committedFingerprint,
                await ReadAtomicBusinessFingerprintAsync(restartedClient));
            for (var index = 0; index < passedCheckpoints.Count - 1; index++)
            {
                await AssertPollTraceNotFoundAsync(
                    restartedClient,
                    $"poll-ticket16-fault-{index + 1}");
            }
        }

        var metadata = await ReadServerMetadataAsync(database.ConnectionString);
        Assert.Equal(database.ProductVersion, metadata.ProductVersion);
        Assert.Equal(database.EngineEdition, metadata.EngineEdition);
        Assert.Equal(database.CompatibilityLevel, metadata.CompatibilityLevel);
        WriteMarker(new
        {
            sqlProductName = $"Microsoft SQL Server / {metadata.Edition}",
            productVersion = metadata.ProductVersion,
            productMajor = database.ProductMajor,
            engineEdition = metadata.EngineEdition,
            compatibilityLevel = metadata.CompatibilityLevel,
            checkpoints = passedCheckpoints.Select(checkpoint => new
            {
                name = checkpoint.ToString(),
                passed = true,
            }),
            retries = 1,
            replays = 1,
            conflicts = 1,
            assertions = new[]
            {
                new { name = "failed_round_retry_commits_once", passed = true },
                new { name = "same_content_replay_is_inert", passed = true },
                new { name = "different_content_conflict_preserves_facts", passed = true },
                new { name = "all_six_SQL_failpoints_are_absent_after_restart", passed = true },
            },
        });
    }

    [Ticket01SqlServerFact]
    public async Task Concurrent_writers_form_one_monotonic_sequence_one_generation_and_one_catalog_revision_per_round()
    {
        const int writerCount = 8;
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var at = new DateTimeOffset(2026, 8, 16, 3, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(at);
        long catalogRevisionAfterWriters;
        long projectionSequenceBeforeRestart;
        IReadOnlyList<long> seriesSequencesBeforeRestart;
        HashSet<string> projectionCommitIdsBeforeRestart;
        string finalArea;

        await using (var factory = CreateFactory(clock))
        {
            using var client = factory.CreateClient();
            var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
            var seedObservation = Observation(
                "CONCURRENT-WORK",
                "SL-TICKET16-CONCURRENT",
                "C1-1");
            var seedRows = new[]
            {
                seedObservation,
                Observation(
                    "CONCURRENT-PROTECTED",
                    "SL-TICKET16-CONCURRENT-PROTECTED-1",
                    "P1-1"),
                Observation(
                    "CONCURRENT-PROTECTED",
                    "SL-TICKET16-CONCURRENT-PROTECTED-2",
                    "P2-1"),
            };
            var barrierReceipts = await Task.WhenAll(
                ingestor.IngestAsync(SuccessRound(
                    "poll-ticket16-concurrent-seed-1",
                    at,
                    seedRows)),
                ingestor.IngestAsync(SuccessRound(
                    "poll-ticket16-concurrent-seed-2",
                    at,
                    seedRows)));
            foreach (var barrierReceipt in barrierReceipts)
            {
                var trace = await ReadJsonAsync(
                    client,
                    $"/api/v2/poll-traces/{Uri.EscapeDataString(barrierReceipt.PollTraceId)}");
                Assert.False(
                    trace.GetProperty("projectionCommit").GetProperty("absenceAuthority").GetBoolean());
            }
            var catalogBefore = await ReadCatalogAsync(client);
            Assert.Equal(1L, CatalogRevision(catalogBefore.Body));

            var writerTasks = Enumerable.Range(0, writerCount)
                .Select(index => ingestor.IngestAsync(SuccessRound(
                    $"poll-ticket16-concurrent-{index}",
                    at.AddMinutes(2),
                    [Observation(
                        "CONCURRENT-WORK",
                        "SL-TICKET16-CONCURRENT",
                        $"C{index + 2}-1")])))
                .ToArray();
            var receipts = await Task.WhenAll(writerTasks).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(writerCount, receipts.Length);
            Assert.All(receipts, receipt =>
            {
                Assert.False(receipt.IsReplay);
                Assert.NotNull(receipt.ProjectionCommitId);
                Assert.NotNull(receipt.ProjectionSequence);
                Assert.Single(receipt.SeriesIds);
                Assert.Single(receipt.DemandIds);
            });
            Assert.Equal(
                writerCount,
                receipts.Select(receipt => receipt.ProjectionCommitId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(
                writerCount,
                receipts.Select(receipt => receipt.ProjectionSequence).Distinct().Count());
            AssertStrictlyIncreasing(
                receipts.Select(receipt => receipt.ProjectionSequence!.Value)
                    .OrderBy(value => value)
                    .ToArray());

            foreach (var receipt in receipts)
            {
                var trace = await ReadJsonAsync(
                    client,
                    $"/api/v2/poll-traces/{Uri.EscapeDataString(receipt.PollTraceId)}");
                Assert.True(
                    trace.GetProperty("projectionCommit").GetProperty("absenceAuthority").GetBoolean());
                var protectedDecision = Assert.Single(
                    trace.GetProperty("projectionCommit")
                        .GetProperty("taskTypeProtectionDecisions")
                        .EnumerateArray()
                        .Where(item => item.GetProperty("workType").GetString()
                            == "CONCURRENT-PROTECTED"));
                Assert.False(
                    protectedDecision.GetProperty("protectionAllowsAbsenceAuthority").GetBoolean());
                Assert.False(
                    protectedDecision.GetProperty("effectiveAbsenceAuthorityAvailable").GetBoolean());
            }

            var protection = await ReadJsonAsync(
                client,
                "/api/v2/task-type-protections/CONCURRENT-PROTECTED");
            Assert.Equal("PAUSED_ZERO_DROP", protection.GetProperty("phase").GetString());
            foreach (var protectedSublot in new[]
                     {
                         "SL-TICKET16-CONCURRENT-PROTECTED-1",
                         "SL-TICKET16-CONCURRENT-PROTECTED-2",
                     })
            {
                var protectedSeries = await ReadSeriesByKeyAsync(
                    client,
                    "CONCURRENT-PROTECTED",
                    protectedSublot);
                Assert.Equal(
                    DemandSeriesLifecycleContract.Visible,
                    protectedSeries.GetProperty("currentPresence").GetString());
            }

            var detail = await ReadSeriesByKeyAsync(
                client,
                "CONCURRENT-WORK",
                "SL-TICKET16-CONCURRENT");
            Assert.Single(detail.GetProperty("demands").EnumerateArray());
            Assert.Equal(1, detail.GetProperty("currentDemand").GetProperty("generation").GetInt32());
            var seriesSequences = detail.GetProperty("events").EnumerateArray()
                .Select(item => item.GetProperty("seriesSequence").GetInt64())
                .ToArray();
            Assert.Equal(seriesSequences.Length, seriesSequences.Distinct().Count());
            AssertStrictlyIncreasing(seriesSequences);
            Assert.Equal(
                seriesSequences[^1],
                detail.GetProperty("lastSeriesSequence").GetInt64());
            finalArea = detail.GetProperty("currentDemand")
                .GetProperty("liveMesFields")
                .GetProperty("area")
                .GetString()!;

            var catalogAfterWriters = await ReadCatalogAsync(client);
            catalogRevisionAfterWriters = CatalogRevision(catalogAfterWriters.Body);
            Assert.Equal(
                CatalogRevision(catalogBefore.Body) + writerCount,
                catalogRevisionAfterWriters);
            var lastWriter = receipts.MaxBy(receipt => receipt.ProjectionSequence)!
                .ProjectionCommitId;
            Assert.Equal(
                lastWriter,
                catalogAfterWriters.Body.GetProperty("projectionCommitId").GetString());

            var noOp = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket16-concurrent-noop",
                at.AddMinutes(3),
                [Observation(
                    "CONCURRENT-WORK",
                    "SL-TICKET16-CONCURRENT",
                    finalArea)]));
            Assert.Equal(
                catalogRevisionAfterWriters,
                CatalogRevision((await ReadCatalogAsync(client)).Body));
            projectionSequenceBeforeRestart = noOp.ProjectionSequence!.Value;
            seriesSequencesBeforeRestart = seriesSequences;
            projectionCommitIdsBeforeRestart = barrierReceipts
                .Concat(receipts)
                .Append(noOp)
                .Select(receipt => receipt.ProjectionCommitId!)
                .ToHashSet(StringComparer.Ordinal);
        }

        await using (var restartedFactory = CreateFactory(clock))
        {
            using var client = restartedFactory.CreateClient();
            var ingestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            var multipleChanges = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket16-concurrent-after-restart",
                at.AddMinutes(4),
                [
                    Observation(
                        "CONCURRENT-WORK",
                        "SL-TICKET16-CONCURRENT",
                        "C10-1",
                        eqp: "RESTART-EQP"),
                    Observation(
                        "CONCURRENT-WORK-NEW",
                        "SL-TICKET16-CONCURRENT-NEW",
                        "C11-1"),
                ]));
            Assert.True(multipleChanges.ProjectionSequence > projectionSequenceBeforeRestart);
            Assert.DoesNotContain(
                multipleChanges.ProjectionCommitId,
                projectionCommitIdsBeforeRestart);
            Assert.Equal(
                catalogRevisionAfterWriters + 1,
                CatalogRevision((await ReadCatalogAsync(client)).Body));
            var detail = await ReadSeriesByKeyAsync(
                client,
                "CONCURRENT-WORK",
                "SL-TICKET16-CONCURRENT");
            Assert.Single(detail.GetProperty("demands").EnumerateArray());
            Assert.Equal(1, detail.GetProperty("currentDemand").GetProperty("generation").GetInt32());
            var afterRestartSequences = detail.GetProperty("events").EnumerateArray()
                .Select(item => item.GetProperty("seriesSequence").GetInt64())
                .ToArray();
            Assert.Equal(
                seriesSequencesBeforeRestart,
                afterRestartSequences.Take(seriesSequencesBeforeRestart.Count));
            AssertStrictlyIncreasing(afterRestartSequences);
            Assert.True(afterRestartSequences[^1] > seriesSequencesBeforeRestart[^1]);
        }

        WriteMarker(new
        {
            writerConcurrency = writerCount,
            assertions = new[]
            {
                new { name = "concurrent_writer_sequences_are_unique_and_monotonic", passed = true },
                new { name = "same_key_has_one_current_generation", passed = true },
                new { name = "catalog_revision_changes_once_per_round", passed = true },
                new { name = "concurrent_rounds_respect_restart_barrier", passed = true },
                new { name = "concurrent_rounds_respect_task_type_protection", passed = true },
                new { name = "restart_preserves_and_advances_series_sequence_and_commit_identity", passed = true },
            },
        });
    }

    [Ticket01SqlServerFact]
    public async Task Demand_catalog_and_attention_reads_are_wholly_old_or_new_at_a_concurrent_commit_fence()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var at = new DateTimeOffset(2026, 8, 16, 5, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(at.AddHours(1));
        var observer = new GatedProjectionReadBoundaryObserver();
        await using var factory = CreateFactory(clock, readBoundaryObserver: observer);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        var rowA = Observation("READ-WORK-A", "SL-TICKET16-READ-A", "R1-1");
        var receiptA = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket16-read-a",
            at,
            [rowA]));
        var expectedSeriesA = await ReadRawSuccessAsync(client, SeriesListPath);
        var seriesGate = observer.Arm(ProjectionReadSurface.DemandSeries);
        var pendingSeries = ReadRawSuccessAsync(client, SeriesListPath);
        var seriesFences = await seriesGate.Selected.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(seriesFences);
        Assert.Equal(receiptA.ProjectionCommitId, seriesFences[0].ProjectionCommitId);
        Assert.Equal(receiptA.ProjectionSequence, seriesFences[0].ProjectionSequence);
        var rowB = Observation("READ-WORK-B", "SL-TICKET16-READ-B", "R2-1");
        var writerB = ingestor.IngestAsync(SuccessRound(
            "poll-ticket16-read-b",
            at.AddMinutes(1),
            [rowA, rowB]));
        try
        {
            await Task.Delay(100);
        }
        finally
        {
            seriesGate.Release();
        }
        Assert.Equal(expectedSeriesA, await pendingSeries.WaitAsync(TimeSpan.FromSeconds(10)));
        var receiptB = await writerB.WaitAsync(TimeSpan.FromSeconds(10));
        var seriesB = JsonDocument.Parse(await ReadRawSuccessAsync(client, SeriesListPath)).RootElement;
        Assert.Equal(receiptB.ProjectionCommitId, seriesB.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(2L, seriesB.GetProperty("exactTotalCount").GetInt64());

        var expectedCatalogB = await ReadCatalogAsync(client);
        var catalogGate = observer.Arm(ProjectionReadSurface.Catalog);
        var pendingCatalog = ReadCatalogAsync(client);
        var catalogFences = await catalogGate.Selected.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(catalogFences);
        Assert.Equal(CatalogRevision(expectedCatalogB.Body), catalogFences[0].CatalogRevision);
        var writerC = ingestor.IngestAsync(SuccessRound(
            "poll-ticket16-read-c",
            at.AddMinutes(2),
            [
                rowA with { Area = "R3-1" },
                rowB with { Area = "R4-1" },
            ]));
        try
        {
            await Task.Delay(100);
        }
        finally
        {
            catalogGate.Release();
        }
        var oldCatalog = await pendingCatalog.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expectedCatalogB.Body.GetRawText(), oldCatalog.Body.GetRawText());
        Assert.Equal(expectedCatalogB.ETag, oldCatalog.ETag);
        var receiptC = await writerC.WaitAsync(TimeSpan.FromSeconds(10));
        var catalogC = await ReadCatalogAsync(client);
        Assert.Equal(CatalogRevision(expectedCatalogB.Body) + 1, CatalogRevision(catalogC.Body));
        Assert.Equal(receiptC.ProjectionCommitId, catalogC.Body.GetProperty("projectionCommitId").GetString());
        Assert.All(catalogC.Body.GetProperty("items").EnumerateArray(), item =>
            Assert.Equal(
                receiptC.ProjectionCommitId,
                item.GetProperty("valueProjectionCommitId").GetString()));

        var expectedAttentionC = await ReadRawSuccessAsync(client, AttentionPath);
        var attentionGate = observer.Arm(
            ProjectionReadSurface.CurrentIngestAttention,
            expectedInvocationCount: 2);
        var pendingAttention1 = ReadRawSuccessAsync(client, AttentionPath);
        var pendingAttention2 = ReadRawSuccessAsync(client, AttentionPath);
        var attentionFences = await attentionGate.Selected.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, attentionFences.Count);
        Assert.All(attentionFences, fence =>
        {
            Assert.Equal(receiptC.ProjectionCommitId, fence.ProjectionCommitId);
            Assert.Equal(receiptC.ProjectionSequence, fence.ProjectionSequence);
        });
        var writerD = ingestor.IngestAsync(SuccessRound(
            "poll-ticket16-read-d",
            at.AddMinutes(3),
            [
                rowA with { Area = "R3-1", Eqp = null },
                rowB with { Area = "R4-1" },
                new MesTaskUnionObservation(
                    WorkType: null,
                    Sublot: "SL-TICKET16-READ-UNASSIGNED",
                    Area: "R5-1",
                    Eqp: "UNASSIGNED-EQP",
                    Step: "UNASSIGNED-STEP",
                    MesSourceDate: EvidenceDate,
                    Package: "UNASSIGNED-PACKAGE"),
            ]));
        RoundCommitReceipt receiptD;
        try
        {
            await Task.Delay(100);
            Assert.False(writerD.IsCompleted);
        }
        finally
        {
            attentionGate.Release();
        }
        receiptD = await writerD.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expectedAttentionC, await pendingAttention1.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(expectedAttentionC, await pendingAttention2.WaitAsync(TimeSpan.FromSeconds(10)));
        var attentionD = await ReadJsonAsync(client, AttentionPath);
        Assert.Equal(
            receiptD.ProjectionCommitId,
            attentionD.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        var kinds = attentionD.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("kind").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(CurrentIngestAttentionKinds.SeriesError, kinds);
        Assert.Contains(CurrentIngestAttentionKinds.UnassignedMesObservation, kinds);

        WriteMarker(new
        {
            readerConcurrency = 2,
            assertions = new[]
            {
                new { name = "demand_series_response_is_one_fence", passed = true },
                new { name = "catalog_response_is_one_revision_fence", passed = true },
                new { name = "concurrent_attention_readers_remain_on_the_old_fence", passed = true },
            },
        });
    }

    internal async Task RunFrozenReadCommitConsistencyMatrixAsync()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(
            database.ConnectionString,
            Ticket12RawSecret);
        var at = new DateTimeOffset(2026, 8, 24, 2, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(at.AddHours(1));
        var frozenObserver = new GatedProjectionReadBoundaryObserver();
        var overviewObserver = new MatrixOverviewReadBoundaryObserver();
        await using var factory = CreateFactory(
            clock,
            readBoundaryObserver: frozenObserver,
            overviewReadBoundaryObserver: overviewObserver);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Ticket12RawSecret);
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        var rowA = Observation(
            "FROZEN-MATRIX",
            "SL-TICKET12-FROZEN-MATRIX",
            "N1-1",
            eqp: null);
        var receiptA = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-frozen-a",
            at,
            [rowA]));

        var demandA = await ReadJsonAsync(client, SeriesListPath);
        var historyEpoch = Guid.Parse(
            demandA.GetProperty("snapshot").GetProperty("historyEpoch").GetString()!);
        var demandSnapshot = demandA.GetProperty("snapshotReference").GetString()!;
        var demandItem = Assert.Single(demandA.GetProperty("items").EnumerateArray());
        var seriesId = demandItem.GetProperty("seriesId").GetString()!;
        var frozenDemandListPath = SeriesListPath
            + "&snapshot=" + Uri.EscapeDataString(demandSnapshot);
        var frozenDemandDetailPath = "/api/v2/demand-series/"
            + Uri.EscapeDataString(seriesId)
            + "?snapshot=" + Uri.EscapeDataString(demandSnapshot);

        var auditA = await ReadJsonAsync(
            client,
            "/api/v2/readability-audit?pageSize=100");
        var auditSnapshot = auditA.GetProperty("snapshotReference").GetString()!;
        var auditItem = Assert.Single(auditA.GetProperty("items").EnumerateArray());
        var demandId = auditItem.GetProperty("demandId").GetString()!;
        var frozenAuditListPath = "/api/v2/readability-audit?pageSize=100"
            + "&snapshot=" + Uri.EscapeDataString(auditSnapshot);
        var frozenAuditDetailPath = "/api/v2/readability-audit/"
            + Uri.EscapeDataString(demandId)
            + "?snapshot=" + Uri.EscapeDataString(auditSnapshot);

        var errorA = await ReadJsonAsync(client, "/api/v2/error-search?pageSize=100");
        var errorSnapshot = errorA.GetProperty("snapshotReference").GetString()!;
        var errorItem = Assert.Single(errorA.GetProperty("items").EnumerateArray());
        Assert.Equal(seriesId, errorItem.GetProperty("seriesId").GetString());
        var frozenErrorListPath = "/api/v2/error-search?pageSize=100&snapshot="
            + Uri.EscapeDataString(errorSnapshot);
        var frozenErrorDetailPath = "/api/v2/error-search/"
            + Uri.EscapeDataString(seriesId)
            + "?snapshot=" + Uri.EscapeDataString(errorSnapshot);
        var errorDetailA = await ReadJsonAsync(client, frozenErrorDetailPath);
        var evidenceId = errorDetailA.GetProperty("periods").EnumerateArray()
            .SelectMany(period => period.GetProperty("evidence").EnumerateArray())
            .First(evidence => evidence.GetProperty("rawEvidenceAvailable").GetBoolean())
            .GetProperty("evidenceId").GetString()!;
        var frozenRawEvidencePath = "/api/v2/error-search/"
            + Uri.EscapeDataString(seriesId)
            + "/evidence/" + Uri.EscapeDataString(evidenceId)
            + "/raw-observations?snapshot=" + Uri.EscapeDataString(errorSnapshot)
            + "&fields=workType,area,eqp&maxItems=20";
        var pollTraceAPath = "/api/v2/poll-traces/"
            + Uri.EscapeDataString(receiptA.PollTraceId);

        var demandGate = frozenObserver.Arm(
            ProjectionReadSurface.DemandSeries,
            expectedInvocationCount: 2);
        var auditGate = frozenObserver.Arm(
            ProjectionReadSurface.ReadabilityAudit,
            expectedInvocationCount: 2);
        var errorGate = frozenObserver.Arm(
            ProjectionReadSurface.ErrorSearch,
            expectedInvocationCount: 3);
        var pollEvidenceGate = frozenObserver.Arm(
            ProjectionReadSurface.PollEvidence,
            expectedInvocationCount: 1);
        var pendingDemandList = ReadRawSuccessAsync(client, frozenDemandListPath);
        var pendingDemandDetail = ReadRawSuccessAsync(client, frozenDemandDetailPath);
        var pendingAuditList = ReadRawSuccessAsync(client, frozenAuditListPath);
        var pendingAuditDetail = ReadRawSuccessAsync(client, frozenAuditDetailPath);
        var pendingErrorList = ReadRawSuccessAsync(client, frozenErrorListPath);
        var pendingErrorDetail = ReadRawSuccessAsync(client, frozenErrorDetailPath);
        var pendingRawEvidence = ReadRawSuccessAsync(client, frozenRawEvidencePath);
        var pendingPollEvidence = ReadRawSuccessAsync(client, pollTraceAPath);
        var pendingOverview = ReadRawSuccessAsync(client, "/api/v2/watch-overview");

        var demandFences = await demandGate.Selected.WaitAsync(TimeSpan.FromSeconds(10));
        var auditFences = await auditGate.Selected.WaitAsync(TimeSpan.FromSeconds(10));
        var errorFences = await errorGate.Selected.WaitAsync(TimeSpan.FromSeconds(10));
        var pollEvidenceFences = await pollEvidenceGate.Selected.WaitAsync(TimeSpan.FromSeconds(10));
        var overviewFence = await overviewObserver.FenceSelected.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(
            demandFences.Concat(auditFences).Concat(errorFences).Concat(pollEvidenceFences),
            fence =>
            {
                Assert.Equal(historyEpoch, fence.HistoryEpoch.Value);
                Assert.Equal(receiptA.ProjectionCommitId, fence.ProjectionCommitId);
                Assert.Equal(receiptA.ProjectionSequence, fence.ProjectionSequence);
            });
        Assert.Equal(historyEpoch, overviewFence.HistoryEpoch!.Value);
        Assert.Equal(receiptA.ProjectionCommitId, overviewFence.ProjectionCommitId);
        Assert.Equal(receiptA.ProjectionSequence, overviewFence.ProjectionSequence);

        var rowB = rowA with { Area = "N2-2", Eqp = "EQP-TICKET12-RECOVERED" };
        var writerB = ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-frozen-b",
            at.AddMinutes(1),
            [rowB]));
        RoundCommitReceipt receiptB;
        try
        {
            await Task.Delay(100);
            Assert.False(writerB.IsCompleted);
            overviewObserver.Release();
            receiptB = await writerB.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(
                new[] { pendingDemandList, pendingDemandDetail, pendingAuditList,
                    pendingAuditDetail, pendingErrorList, pendingErrorDetail,
                    pendingRawEvidence, pendingPollEvidence },
                pending => Assert.False(pending.IsCompleted));
        }
        finally
        {
            overviewObserver.Release();
            demandGate.Release();
            auditGate.Release();
            errorGate.Release();
            pollEvidenceGate.Release();
        }

        using var oldOverview = JsonDocument.Parse(
            await pendingOverview.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            receiptA.ProjectionCommitId,
            oldOverview.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(
            historyEpoch,
            oldOverview.RootElement.GetProperty("snapshot").GetProperty("historyEpoch").GetGuid());
        Assert.Equal(
            1L,
            oldOverview.RootElement.GetProperty("readability").GetProperty("notReadableCount").GetInt64());
        Assert.Equal(
            1L,
            oldOverview.RootElement.GetProperty("errors").GetProperty("activeSeriesCount").GetInt64());

        using var frozenDemandList = JsonDocument.Parse(
            await pendingDemandList.WaitAsync(TimeSpan.FromSeconds(10)));
        using var frozenDemandDetail = JsonDocument.Parse(
            await pendingDemandDetail.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            receiptA.ProjectionCommitId,
            frozenDemandList.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(
            historyEpoch,
            frozenDemandList.RootElement.GetProperty("snapshot").GetProperty("historyEpoch").GetGuid());
        Assert.Equal(1L, frozenDemandList.RootElement.GetProperty("exactTotalCount").GetInt64());
        Assert.Equal(
            1L,
            frozenDemandList.RootElement.GetProperty("facets").GetProperty("trackingCount").GetInt64());
        Assert.Equal(
            1L,
            frozenDemandList.RootElement.GetProperty("facets").GetProperty("visibleCount").GetInt64());
        Assert.Equal(
            receiptA.ProjectionCommitId,
            frozenDemandDetail.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(
            historyEpoch,
            frozenDemandDetail.RootElement.GetProperty("snapshot").GetProperty("historyEpoch").GetGuid());
        Assert.Equal(
            JsonValueKind.Null,
            frozenDemandDetail.RootElement.GetProperty("currentDemand")
                .GetProperty("liveMesFields").GetProperty("eqp").ValueKind);

        using var frozenAuditList = JsonDocument.Parse(
            await pendingAuditList.WaitAsync(TimeSpan.FromSeconds(10)));
        using var frozenAuditDetail = JsonDocument.Parse(
            await pendingAuditDetail.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            receiptA.ProjectionCommitId,
            frozenAuditList.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(
            historyEpoch,
            frozenAuditList.RootElement.GetProperty("snapshot").GetProperty("historyEpoch").GetGuid());
        Assert.Equal(1L, frozenAuditList.RootElement.GetProperty("exactTotalDemandCount").GetInt64());
        var frozenAuditStateFacet = frozenAuditList.RootElement.GetProperty("facets")
            .GetProperty("readabilityStates").EnumerateArray()
            .Single(facet => facet.GetProperty("state").GetString() == "NOT_READABLE");
        Assert.Equal("NOT_READABLE", frozenAuditStateFacet.GetProperty("state").GetString());
        Assert.Equal(1L, frozenAuditStateFacet.GetProperty("demandCount").GetInt64());
        Assert.Equal(
            receiptA.ProjectionCommitId,
            frozenAuditDetail.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(
            historyEpoch,
            frozenAuditDetail.RootElement.GetProperty("snapshot").GetProperty("historyEpoch").GetGuid());
        Assert.NotEmpty(frozenAuditDetail.RootElement.GetProperty("blockers").EnumerateArray());

        using var frozenErrorList = JsonDocument.Parse(
            await pendingErrorList.WaitAsync(TimeSpan.FromSeconds(10)));
        using var frozenErrorDetail = JsonDocument.Parse(
            await pendingErrorDetail.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            receiptA.ProjectionCommitId,
            frozenErrorList.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(
            historyEpoch,
            frozenErrorList.RootElement.GetProperty("snapshot").GetProperty("historyEpoch").GetGuid());
        Assert.Equal(1L, frozenErrorList.RootElement.GetProperty("totalSeriesCount").GetInt64());
        var frozenErrorStateFacet = frozenErrorList.RootElement.GetProperty("facets")
            .GetProperty("activityStates").EnumerateArray()
            .Single(facet => facet.GetProperty("state").GetString() == "ACTIVE");
        Assert.Equal("ACTIVE", frozenErrorStateFacet.GetProperty("state").GetString());
        Assert.Equal(1L, frozenErrorStateFacet.GetProperty("seriesCount").GetInt64());
        Assert.Equal("ACTIVE", Assert.Single(frozenErrorList.RootElement.GetProperty("items").EnumerateArray())
            .GetProperty("activityState").GetString());
        Assert.Equal(
            receiptA.ProjectionCommitId,
            frozenErrorDetail.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(
            historyEpoch,
            frozenErrorDetail.RootElement.GetProperty("snapshot").GetProperty("historyEpoch").GetGuid());
        Assert.NotEmpty(frozenErrorDetail.RootElement.GetProperty("periods").EnumerateArray());

        using var frozenRawEvidence = JsonDocument.Parse(
            await pendingRawEvidence.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            historyEpoch,
            frozenRawEvidence.RootElement.GetProperty("snapshot").GetProperty("historyEpoch").GetGuid());
        Assert.Equal(
            receiptA.ProjectionCommitId,
            frozenRawEvidence.RootElement.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(evidenceId, frozenRawEvidence.RootElement.GetProperty("evidenceId").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            Assert.Single(frozenRawEvidence.RootElement.GetProperty("items").EnumerateArray())
                .GetProperty("fields").GetProperty("eqp").ValueKind);

        using var frozenPollEvidence = JsonDocument.Parse(
            await pendingPollEvidence.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            receiptA.ProjectionCommitId,
            frozenPollEvidence.RootElement.GetProperty("projectionCommit")
                .GetProperty("projectionCommitId").GetString());
        Assert.Equal(historyEpoch, frozenPollEvidence.RootElement.GetProperty("historyEpoch").GetGuid());
        Assert.Equal(
            JsonValueKind.Null,
            Assert.Single(frozenPollEvidence.RootElement.GetProperty("observations").EnumerateArray())
                .GetProperty("eqp").ValueKind);

        var currentDemand = await ReadJsonAsync(client, SeriesListPath);
        var currentAudit = await ReadJsonAsync(
            client,
            "/api/v2/readability-audit?pageSize=100");
        var currentError = await ReadJsonAsync(client, "/api/v2/error-search?pageSize=100");
        var currentOverview = await ReadJsonAsync(client, "/api/v2/watch-overview");
        var currentPollEvidence = await ReadJsonAsync(
            client,
            "/api/v2/poll-traces/" + Uri.EscapeDataString(receiptB.PollTraceId));
        Assert.All(
            new[] { currentDemand, currentAudit, currentError, currentOverview },
            body =>
            {
                Assert.Equal(historyEpoch, body.GetProperty("snapshot").GetProperty("historyEpoch").GetGuid());
                Assert.Equal(
                    receiptB.ProjectionCommitId,
                    body.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
            });
        Assert.Equal(
            "EQP-TICKET12-RECOVERED",
            Assert.Single(currentDemand.GetProperty("items").EnumerateArray())
                .GetProperty("liveMesFields").GetProperty("eqp").GetString());
        Assert.Equal(
            "READABLE",
            Assert.Single(currentAudit.GetProperty("items").EnumerateArray())
                .GetProperty("externalReadabilityState").GetString());
        Assert.Equal(
            "ENDED",
            Assert.Single(currentError.GetProperty("items").EnumerateArray())
                .GetProperty("activityState").GetString());
        Assert.Equal(
            1L,
            currentOverview.GetProperty("readability").GetProperty("readableCount").GetInt64());
        Assert.Equal(
            0L,
            currentOverview.GetProperty("errors").GetProperty("activeSeriesCount").GetInt64());
        Assert.Equal(
            receiptB.ProjectionCommitId,
            currentPollEvidence.GetProperty("projectionCommit").GetProperty("projectionCommitId").GetString());
        Assert.Equal(historyEpoch, currentPollEvidence.GetProperty("historyEpoch").GetGuid());
        Assert.Equal(
            "EQP-TICKET12-RECOVERED",
            Assert.Single(currentPollEvidence.GetProperty("observations").EnumerateArray())
                .GetProperty("eqp").GetString());

        var sessionBaseline = await ReadUserSessionCountAsync(database.ConnectionString);
        const int cancellationCount = 5;
        for (var index = 0; index < cancellationCount; index++)
        {
            var cancellationGate = frozenObserver.Arm(
                ProjectionReadSurface.ErrorSearch,
                expectedInvocationCount: 1);
            using var cancellation = new CancellationTokenSource();
            var cancelledRead = ReadRawSuccessAsync(
                client,
                frozenErrorListPath,
                cancellation.Token);
            await cancellationGate.Selected.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRead);
            cancellationGate.Release();
            Assert.Equal(0, await ReadActiveUserTransactionCountAsync(database.ConnectionString));
        }

        var receiptC = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket12-frozen-c",
                at.AddMinutes(2),
                [rowB with { Area = "N3-3" }]))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await ReadActiveUserTransactionCountAsync(database.ConnectionString));
        Assert.True(
            await ReadUserSessionCountAsync(database.ConnectionString) <= sessionBaseline,
            "Cancelled frozen reads must return their SQL connections to the existing pool baseline.");
        var afterCancellation = await ReadJsonAsync(client, SeriesListPath);
        Assert.Equal(
            receiptC.ProjectionCommitId,
            afterCancellation.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());

        var isolation = await ReadSnapshotIsolationOptionsAsync(database.ConnectionString);
        Assert.Equal(0, isolation.SnapshotIsolationState);
        Assert.False(isolation.IsReadCommittedSnapshotOn);
        WriteTicket12Marker(new
        {
            matrixSurfaces = new[]
            {
                "DemandSeries",
                "ReadabilityAudit",
                "ErrorSearch",
                "WatchOverview",
                "PollEvidence",
            },
            frozenReaders = 8,
            writerCompletedWhileFrozenReadersWereHeld = true,
            cancellations = cancellationCount,
            cancellationLeftActiveUserTransactions = 0,
            snapshotIsolationUsed = false,
        });
    }

    [Ticket01SqlServerFact]
    public async Task Failure_incomplete_and_mid_transaction_cancellation_preserve_business_projection_and_open_error_periods()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var at = new DateTimeOffset(2026, 8, 16, 7, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(at.AddHours(1));
        var observer = new CancellableCheckpointObserver();
        await using var factory = CreateFactory(clock, checkpointObserver: observer);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var invalid = Observation(
            "ISOLATION-WORK",
            "SL-TICKET16-ISOLATION",
            "I1-1",
            eqp: null);
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket16-isolation-seed-1",
            at,
            [invalid]));
        var accepted = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket16-isolation-seed-2",
            at.AddMinutes(1),
            [invalid]));
        var baseline = await ReadBusinessFingerprintAsync(
            client,
            "ISOLATION-WORK",
            "SL-TICKET16-ISOLATION",
            includeAttention: false);

        var failureRound = new MesTaskUnionRound(
            "poll-ticket16-isolation-failure",
            QueryVersion,
            MesTaskUnionRoundOutcome.Failure,
            at.AddMinutes(2).AddSeconds(-1),
            at.AddMinutes(2),
            [],
            new MesTaskUnionRoundDiagnostic(
                "ORACLE_EXECUTE",
                "TICKET16_FAILURE",
                "Controlled Ticket 16 failure."));
        var incompleteRound = new MesTaskUnionRound(
            "poll-ticket16-isolation-incomplete",
            QueryVersion,
            MesTaskUnionRoundOutcome.Incomplete,
            at.AddMinutes(3).AddSeconds(-1),
            at.AddMinutes(3),
            [invalid with { Eqp = "RECOVERED-EQP" }],
            new MesTaskUnionRoundDiagnostic(
                "RESULT_SHAPE",
                "TICKET16_INCOMPLETE",
                "Controlled Ticket 16 incomplete result."));
        var failure = await ingestor.IngestAsync(failureRound);
        var incomplete = await ingestor.IngestAsync(incompleteRound);
        Assert.Null(failure.ProjectionCommitId);
        Assert.Null(incomplete.ProjectionCommitId);
        Assert.False(failure.IsReplay);
        Assert.False(incomplete.IsReplay);
        Assert.Equal(
            baseline,
            await ReadBusinessFingerprintAsync(
                client,
                "ISOLATION-WORK",
                "SL-TICKET16-ISOLATION",
                includeAttention: false));

        foreach (var round in new[] { failureRound, incompleteRound })
        {
            var trace = await ReadJsonAsync(
                client,
                $"/api/v2/poll-traces/{Uri.EscapeDataString(round.PollTraceId)}");
            Assert.Equal(JsonValueKind.Null, trace.GetProperty("projectionCommit").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, trace.GetProperty("diagnostic").ValueKind);
        }
        var attention = await ReadJsonAsync(client, AttentionPath);
        Assert.Equal(
            accepted.ProjectionCommitId,
            attention.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Contains(
            attention.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == CurrentIngestAttentionKinds.PollRunFailure);

        const string cancelledPollTraceId = "poll-ticket16-isolation-cancelled";
        observer.Arm(cancelledPollTraceId);
        using var cancellation = new CancellationTokenSource();
        var pendingCancellation = ingestor.IngestAsync(
            SuccessRound(
                cancelledPollTraceId,
                at.AddMinutes(4),
                [invalid with { Eqp = "RECOVERED-EQP" }]),
            cancellation.Token);
        await observer.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pendingCancellation);
        await AssertPollTraceNotFoundAsync(client, cancelledPollTraceId);
        Assert.Equal(
            baseline,
            await ReadBusinessFingerprintAsync(
                client,
                "ISOLATION-WORK",
                "SL-TICKET16-ISOLATION",
                includeAttention: false));
        var detail = await ReadSeriesByKeyAsync(
            client,
            "ISOLATION-WORK",
            "SL-TICKET16-ISOLATION");
        Assert.Single(detail.GetProperty("currentConditions").EnumerateArray());
        var period = Assert.Single(detail.GetProperty("errorPeriods").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, period.GetProperty("endedAt").ValueKind);

        WriteMarker(new
        {
            assertions = new[]
            {
                new { name = "failure_and_incomplete_only_advance_technical_poll_evidence", passed = true },
                new { name = "cancellation_before_commit_rolls_back_business_and_idempotency_facts", passed = true },
                new { name = "unsuccessful_rounds_do_not_close_active_error_periods", passed = true },
            },
        });
    }

    [Ticket01SqlServerFact]
    public async Task Combined_projection_covers_conflicts_lifecycle_protection_attention_and_is_identical_after_restart()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        var at = new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(at);
        HistoricalBundle beforeRestart;

        await using (var factory = CreateFactory(clock))
        {
            using var client = factory.CreateClient();
            var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
            var baselineRows = JointRows(
                includeLifecycle: true,
                includeProtected: true,
                includeUnassigned: false);
            clock.SetUtcNow(at);
            await ingestor.IngestAsync(SuccessRound(
                "poll-ticket16-joint-seed-1",
                at,
                baselineRows));
            clock.SetUtcNow(at.AddMinutes(1));
            await ingestor.IngestAsync(SuccessRound(
                "poll-ticket16-joint-seed-2",
                at.AddMinutes(1),
                baselineRows));

            var goneAt = at.AddMinutes(2);
            clock.SetUtcNow(goneAt);
            await ingestor.IngestAsync(SuccessRound(
                "poll-ticket16-joint-gone",
                goneAt,
                JointRows(
                    includeLifecycle: false,
                    includeProtected: false,
                    includeUnassigned: false)));
            var gone = await ReadSeriesByKeyAsync(
                client,
                "JOINT-LIFECYCLE",
                "SL-TICKET16-JOINT-LIFECYCLE");
            Assert.Equal(DemandSeriesLifecycleContract.Gone, gone.GetProperty("currentPresence").GetString());

            var archiveAt = goneAt.Add(DemandSeriesArchivePolicy.MinimumGoneDuration);
            clock.SetUtcNow(archiveAt);
            await ingestor.IngestAsync(SuccessRound(
                "poll-ticket16-joint-archive",
                archiveAt,
                JointRows(
                    includeLifecycle: false,
                    includeProtected: false,
                    includeUnassigned: false)));
            var archived = await ReadSeriesByKeyAsync(
                client,
                "JOINT-LIFECYCLE",
                "SL-TICKET16-JOINT-LIFECYCLE");
            Assert.Equal(DemandSeriesLifecycleContract.Archived, archived.GetProperty("lifecycle").GetString());
            Assert.Contains(
                archived.GetProperty("events").EnumerateArray(),
                item => item.GetProperty("eventType").GetString()
                    == DemandSeriesLifecycleContract.GoneTimeoutArchivedEvent);

            var finalAt = archiveAt.AddMinutes(1);
            clock.SetUtcNow(finalAt);
            var finalReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket16-joint-final",
                finalAt,
                JointRows(
                    includeLifecycle: true,
                    includeProtected: false,
                    includeUnassigned: true)));
            var failureAt = finalAt.AddMinutes(1);
            clock.SetUtcNow(failureAt);
            await ingestor.IngestAsync(new MesTaskUnionRound(
                "poll-ticket16-joint-failure",
                QueryVersion,
                MesTaskUnionRoundOutcome.Failure,
                failureAt.AddSeconds(-1),
                failureAt,
                [],
                new MesTaskUnionRoundDiagnostic(
                    "ORACLE_EXECUTE",
                    "TICKET16_JOINT_FAILURE",
                    "Controlled joint-scenario failure.")));

            var list = await ReadJsonAsync(client, SeriesListPath);
            Assert.Equal(7L, list.GetProperty("exactTotalCount").GetInt64());
            Assert.Equal(
                finalReceipt.ProjectionCommitId,
                list.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());

            var duplicate = await ReadSeriesByKeyAsync(
                client,
                "JOINT-CONFLICT-A",
                "SL-TICKET16-JOINT-CONFLICT");
            var duplicateCodes = duplicate.GetProperty("currentConditions").EnumerateArray()
                .Select(item => item.GetProperty("code").GetString())
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("DUPLICATE_TRANSPORT_DEMAND_KEY", duplicateCodes);
            Assert.Contains("SUBLOT_MULTIPLE_WORK_TYPES", duplicateCodes);
            var duplicateObservations = duplicate.GetProperty("rawObservations").EnumerateArray().ToArray();
            Assert.Equal(10, duplicateObservations.Length);
            Assert.Equal(
                2,
                duplicateObservations.Count(item =>
                    item.GetProperty("pollTraceId").GetString() == "poll-ticket16-joint-final"));
            Assert.All(duplicateObservations, item =>
                Assert.Equal("ASSIGNED", item.GetProperty("assignment").GetString()));

            var membership = await ReadSeriesByKeyAsync(
                client,
                "JOINT-CONFLICT-B",
                "SL-TICKET16-JOINT-CONFLICT");
            Assert.Contains(
                membership.GetProperty("currentConditions").EnumerateArray(),
                item => item.GetProperty("code").GetString() == "SUBLOT_MULTIPLE_WORK_TYPES");

            var longGone = await ReadSeriesByKeyAsync(
                client,
                "JOINT-LIFECYCLE",
                "SL-TICKET16-JOINT-LIFECYCLE");
            Assert.Equal(DemandSeriesLifecycleContract.Archived, longGone.GetProperty("lifecycle").GetString());
            Assert.Equal(
                DemandSeriesLifecycleContract.LongGoneButVisible,
                longGone.GetProperty("currentPresence").GetString());
            Assert.Equal(2, longGone.GetProperty("demands").GetArrayLength());
            Assert.Equal(2, longGone.GetProperty("currentDemand").GetProperty("generation").GetInt32());
            Assert.Contains(
                longGone.GetProperty("currentConditions").EnumerateArray(),
                item => item.GetProperty("code").GetString()
                    == DemandSeriesLifecycleContract.LongGoneButVisible);

            var protection = await ReadJsonAsync(
                client,
                "/api/v2/task-type-protections/JOINT-PROTECTED");
            Assert.Equal("PAUSED_ZERO_DROP", protection.GetProperty("phase").GetString());
            Assert.True(protection.GetProperty("isCurrentAttention").GetBoolean());
            Assert.False(protection.GetProperty("effectiveAbsenceAuthorityAvailable").GetBoolean());
            foreach (var sublot in new[] { "SL-TICKET16-PROTECTED-1", "SL-TICKET16-PROTECTED-2" })
            {
                var protectedSeries = await ReadSeriesByKeyAsync(client, "JOINT-PROTECTED", sublot);
                Assert.Equal(
                    DemandSeriesLifecycleContract.Visible,
                    protectedSeries.GetProperty("currentPresence").GetString());
            }

            var activeError = await ReadSeriesByKeyAsync(
                client,
                "JOINT-ERROR",
                "SL-TICKET16-JOINT-ERROR");
            Assert.Contains(
                activeError.GetProperty("currentConditions").EnumerateArray(),
                item => item.GetProperty("subjectKind").GetString() == "EQP");

            var catalog = await ReadCatalogAsync(client);
            Assert.Equal(2L, CatalogRevision(catalog.Body));
            Assert.Equal(3, catalog.Body.GetProperty("count").GetInt32());
            var attention = await ReadJsonAsync(client, AttentionPath);
            var attentionKinds = attention.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("kind").GetString())
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains(CurrentIngestAttentionKinds.SeriesError, attentionKinds);
            Assert.Contains(CurrentIngestAttentionKinds.PollRunFailure, attentionKinds);
            Assert.Contains(CurrentIngestAttentionKinds.TaskTypeProtection, attentionKinds);
            Assert.Contains(CurrentIngestAttentionKinds.UnassignedMesObservation, attentionKinds);

            beforeRestart = await ReadHistoricalBundleAsync(client);
        }

        await using (var restartedFactory = CreateFactory(clock))
        {
            using var client = restartedFactory.CreateClient();
            Assert.Equal(beforeRestart, await ReadHistoricalBundleAsync(client));
        }

        WriteMarker(new
        {
            assertions = new[]
            {
                new { name = "joint_unique_duplicate_and_multi_work_type_projection_passed", passed = true },
                new { name = "gone_archive_and_long_gone_but_visible_passed", passed = true },
                new { name = "task_type_protection_blocks_absence_authority", passed = true },
                new { name = "all_current_attention_kinds_are_present", passed = true },
                new { name = "restart_preserves_formal_historical_and_current_reads", passed = true },
            },
        });
    }

    private static IReadOnlyList<MesTaskUnionObservation> AtomicBaselineRows() =>
    [
        Observation("ATOMIC-BASE", "SL-TICKET16-ATOMIC-BASE", "N1-1"),
        Observation("ATOMIC-ERROR", "SL-TICKET16-ATOMIC-ERROR", "N1-2", eqp: null),
        Observation("ATOMIC-DUP", "SL-TICKET16-ATOMIC-CONFLICT", "N1-3"),
        Observation("ATOMIC-DUP", "SL-TICKET16-ATOMIC-CONFLICT", "N1-4"),
        Observation("ATOMIC-MULTI", "SL-TICKET16-ATOMIC-CONFLICT", "N1-5"),
        new MesTaskUnionObservation(
            WorkType: null,
            Sublot: "SL-TICKET16-ATOMIC-UNASSIGNED",
            Area: "N1-6",
            Eqp: "UNASSIGNED-EQP",
            Step: "UNASSIGNED-STEP",
            MesSourceDate: EvidenceDate,
            Package: "UNASSIGNED-PACKAGE"),
    ];

    private static IReadOnlyList<MesTaskUnionObservation> AtomicRecoveryRows() =>
    [
        Observation("ATOMIC-BASE", "SL-TICKET16-ATOMIC-BASE", "N2-1"),
        Observation("ATOMIC-ERROR", "SL-TICKET16-ATOMIC-ERROR", "N1-2"),
        Observation("ATOMIC-NEW", "SL-TICKET16-ATOMIC-NEW", "N2-2"),
    ];

    private static IReadOnlyList<MesTaskUnionObservation> JointRows(
        bool includeLifecycle,
        bool includeProtected,
        bool includeUnassigned)
    {
        var rows = new List<MesTaskUnionObservation>
        {
            Observation("JOINT-UNIQUE", "SL-TICKET16-JOINT-UNIQUE", "J1-1"),
            Observation("JOINT-CONFLICT-A", "SL-TICKET16-JOINT-CONFLICT", "J1-2"),
            Observation("JOINT-CONFLICT-B", "SL-TICKET16-JOINT-CONFLICT", "J1-3"),
            Observation("JOINT-CONFLICT-A", "SL-TICKET16-JOINT-CONFLICT", "J1-4"),
            Observation("JOINT-ERROR", "SL-TICKET16-JOINT-ERROR", "J1-5", eqp: null),
        };
        if (includeLifecycle)
        {
            rows.Add(Observation(
                "JOINT-LIFECYCLE",
                "SL-TICKET16-JOINT-LIFECYCLE",
                "J1-6"));
        }
        if (includeProtected)
        {
            rows.Add(Observation(
                "JOINT-PROTECTED",
                "SL-TICKET16-PROTECTED-1",
                "J1-7"));
            rows.Add(Observation(
                "JOINT-PROTECTED",
                "SL-TICKET16-PROTECTED-2",
                "J1-8"));
        }
        if (includeUnassigned)
        {
            rows.Add(new MesTaskUnionObservation(
                WorkType: null,
                Sublot: "SL-TICKET16-JOINT-UNASSIGNED",
                Area: "J1-9",
                Eqp: "UNASSIGNED-EQP",
                Step: "UNASSIGNED-STEP",
                MesSourceDate: EvidenceDate,
                Package: "UNASSIGNED-PACKAGE"));
        }
        return rows;
    }

    private static MesTaskUnionObservation Observation(
        string workType,
        string sublot,
        string area,
        string? eqp = "EQP-TICKET16") =>
        new(
            workType,
            sublot,
            area,
            eqp,
            "STEP-TICKET16",
            EvidenceDate,
            "PACKAGE-TICKET16");

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        IReadOnlyList<MesTaskUnionObservation> observations) =>
        new(
            pollTraceId,
            QueryVersion,
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-1),
            completedAt,
            observations);

    private static async Task<JsonElement> ReadJsonAsync(HttpClient client, string path)
    {
        var body = await ReadRawSuccessAsync(client, path);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static Task<string> ReadRawSuccessAsync(HttpClient client, string path) =>
        ReadRawSuccessAsync(client, path, CancellationToken.None);

    private static async Task<string> ReadRawSuccessAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected HTTP success from {path}, got {(int)response.StatusCode}: {body}");
        return body;
    }

    private static Task<JsonElement> ReadSeriesByKeyAsync(
        HttpClient client,
        string workType,
        string sublot) =>
        ReadJsonAsync(
            client,
            $"/api/v2/demand-series/by-key?workType={Uri.EscapeDataString(workType)}"
            + $"&sublot={Uri.EscapeDataString(sublot)}");

    private static Task<string> ReadSeriesByKeyRawAsync(
        HttpClient client,
        string workType,
        string sublot) =>
        ReadRawSuccessAsync(
            client,
            $"/api/v2/demand-series/by-key?workType={Uri.EscapeDataString(workType)}"
            + $"&sublot={Uri.EscapeDataString(sublot)}");

    private static async Task<CatalogCapture> ReadCatalogAsync(HttpClient client)
    {
        using var response = await client.GetAsync(CatalogPath);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected catalog success, got {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return new CatalogCapture(
            document.RootElement.Clone(),
            response.Headers.ETag?.ToString());
    }

    private static long CatalogRevision(JsonElement catalog) =>
        catalog.GetProperty("catalogRevision").GetInt64();

    private static async Task AssertPollTraceNotFoundAsync(
        HttpClient client,
        string pollTraceId)
    {
        using var response = await client.GetAsync(
            $"/api/v2/poll-traces/{Uri.EscapeDataString(pollTraceId)}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<BusinessSurfaceFingerprint> ReadBusinessFingerprintAsync(
        HttpClient client,
        string workType,
        string sublot,
        bool includeAttention,
        string? secondaryWorkType = null,
        string? secondarySublot = null)
    {
        var catalog = await ReadCatalogAsync(client);
        return new BusinessSurfaceFingerprint(
            await ReadRawSuccessAsync(client, SeriesListPath),
            await ReadSeriesByKeyRawAsync(client, workType, sublot),
            secondaryWorkType is null || secondarySublot is null
                ? null
                : await ReadSeriesByKeyRawAsync(client, secondaryWorkType, secondarySublot),
            catalog.Body.GetRawText(),
            catalog.ETag,
            includeAttention ? await ReadRawSuccessAsync(client, AttentionPath) : null);
    }

    private static Task<BusinessSurfaceFingerprint> ReadAtomicBusinessFingerprintAsync(
        HttpClient client) =>
        ReadBusinessFingerprintAsync(
            client,
            "ATOMIC-BASE",
            "SL-TICKET16-ATOMIC-BASE",
            includeAttention: true,
            secondaryWorkType: "ATOMIC-ERROR",
            secondarySublot: "SL-TICKET16-ATOMIC-ERROR");

    private static async Task<HistoricalBundle> ReadHistoricalBundleAsync(HttpClient client)
    {
        var catalog = await ReadCatalogAsync(client);
        return new HistoricalBundle(
            await ReadRawSuccessAsync(client, SeriesListPath),
            await ReadSeriesByKeyRawAsync(
                client,
                "JOINT-CONFLICT-A",
                "SL-TICKET16-JOINT-CONFLICT"),
            await ReadSeriesByKeyRawAsync(
                client,
                "JOINT-CONFLICT-B",
                "SL-TICKET16-JOINT-CONFLICT"),
            await ReadSeriesByKeyRawAsync(
                client,
                "JOINT-LIFECYCLE",
                "SL-TICKET16-JOINT-LIFECYCLE"),
            await ReadSeriesByKeyRawAsync(
                client,
                "JOINT-ERROR",
                "SL-TICKET16-JOINT-ERROR"),
            catalog.Body.GetRawText(),
            catalog.ETag,
            await ReadRawSuccessAsync(client, AttentionPath),
            await ReadRawSuccessAsync(
                client,
                "/api/v2/task-type-protections/JOINT-PROTECTED"),
            await ReadRawSuccessAsync(
                client,
                "/api/v2/poll-traces/poll-ticket16-joint-final"),
            await ReadRawSuccessAsync(
                client,
                "/api/v2/poll-traces/poll-ticket16-joint-failure"));
    }

    private static void AssertStrictlyIncreasing(IReadOnlyList<long> values)
    {
        for (var index = 1; index < values.Count; index++)
        {
            Assert.True(
                values[index] > values[index - 1],
                $"Expected a strictly increasing sequence, got {string.Join(",", values)}.");
        }
    }

    private static async Task<ServerMetadata> ReadServerMetadataAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CONVERT(nvarchar(128), SERVERPROPERTY('Edition')),
                CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
                CONVERT(int, SERVERPROPERTY('EngineEdition')),
                CONVERT(int, DATABASEPROPERTYEX(DB_NAME(), 'CompatibilityLevel'));
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ServerMetadata(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private static async Task<int> ReadActiveUserTransactionCountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.dm_tran_database_transactions AS databaseTransaction
            INNER JOIN sys.dm_tran_session_transactions AS sessionTransaction
                ON sessionTransaction.transaction_id = databaseTransaction.transaction_id
            INNER JOIN sys.dm_exec_sessions AS session
                ON session.session_id = sessionTransaction.session_id
            WHERE databaseTransaction.database_id = DB_ID()
              AND session.is_user_process = 1;
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> ReadUserSessionCountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.dm_exec_sessions
            WHERE is_user_process = 1
              AND database_id = DB_ID();
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<SnapshotIsolationOptions> ReadSnapshotIsolationOptionsAsync(
        string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_isolation_state, is_read_committed_snapshot_on
            FROM sys.databases
            WHERE database_id = DB_ID();
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new SnapshotIsolationOptions(reader.GetByte(0), reader.GetBoolean(1));
    }

    private void WriteMarker(object marker) =>
        _output.WriteLine(
            "MESINGEST_TICKET16_REPORT_JSON:"
            + JsonSerializer.Serialize(marker));

    private void WriteTicket12Marker(object marker) =>
        _output.WriteLine(
            "MESINGEST_TICKET12_FROZEN_READ_REPORT_JSON:"
            + JsonSerializer.Serialize(marker));

    private WebApplicationFactory<Program> CreateFactory(
        TimeProvider clock,
        IProjectionCommitCheckpointObserver? checkpointObserver = null,
        IProjectionReadBoundaryObserver? readBoundaryObserver = null,
        IWatchOverviewReadBoundaryObserver? overviewReadBoundaryObserver = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
                if (checkpointObserver is not null)
                {
                    services.RemoveAll<IProjectionCommitCheckpointObserver>();
                    services.AddSingleton(checkpointObserver);
                }
                if (readBoundaryObserver is not null)
                {
                    services.RemoveAll<IProjectionReadBoundaryObserver>();
                    services.AddSingleton(readBoundaryObserver);
                }
                if (overviewReadBoundaryObserver is not null)
                {
                    services.RemoveAll<IWatchOverviewReadBoundaryObserver>();
                    services.AddSingleton(overviewReadBoundaryObserver);
                }
            });
        });

    private static IDisposable ConfigureProductionV2Environment(
        string connectionString,
        string? sharedSecret = null) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__SnapshotSource"] = "Oracle",
            [$"{MesIngestHostOptions.SectionName}__SharedSecret"] = sharedSecret,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
            [$"{MesIngestHostOptions.SectionName}__ZeroDropEnterThreshold"] = "2",
            [$"{MesIngestHostOptions.SectionName}__ZeroDropClearStreak"] = "2",
        });

    private sealed class SqlFaultCheckpointObserver : IProjectionCommitCheckpointObserver
    {
        public const int ErrorNumber = 51616;
        private readonly ConcurrentDictionary<CheckpointArm, byte> _armed = new();
        private readonly ConcurrentDictionary<CheckpointArm, byte> _triggered = new();

        public void Arm(string pollTraceId, ProjectionCommitCheckpoint checkpoint) =>
            Assert.True(_armed.TryAdd(new CheckpointArm(pollTraceId, checkpoint), 0));

        public bool WasTriggered(string pollTraceId, ProjectionCommitCheckpoint checkpoint) =>
            _triggered.ContainsKey(new CheckpointArm(pollTraceId, checkpoint));

        public async Task OnCheckpointAsync(
            ProjectionCommitCheckpoint checkpoint,
            ProjectionCommitCheckpointContext context,
            SqlConnection connection,
            SqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            var arm = new CheckpointArm(context.PollTraceId, checkpoint);
            if (!_armed.TryRemove(arm, out _))
            {
                return;
            }

            Assert.True(_triggered.TryAdd(arm, 0));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"THROW {ErrorNumber}, 'TICKET16_INJECTED_SQL_FAILURE', 1;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private readonly record struct CheckpointArm(
            string PollTraceId,
            ProjectionCommitCheckpoint Checkpoint);
    }

    private sealed class CancellableCheckpointObserver : IProjectionCommitCheckpointObserver
    {
        private readonly TaskCompletionSource<bool> _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _pollTraceId;
        private int _triggered;

        public Task Reached => _reached.Task;

        public void Arm(string pollTraceId)
        {
            Assert.Null(_pollTraceId);
            _pollTraceId = pollTraceId;
        }

        public async Task OnCheckpointAsync(
            ProjectionCommitCheckpoint checkpoint,
            ProjectionCommitCheckpointContext context,
            SqlConnection connection,
            SqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            if (checkpoint != ProjectionCommitCheckpoint.BeforeCommit
                || !string.Equals(context.PollTraceId, _pollTraceId, StringComparison.Ordinal)
                || Interlocked.Exchange(ref _triggered, 1) != 0)
            {
                return;
            }

            _reached.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class GatedProjectionReadBoundaryObserver : IProjectionReadBoundaryObserver
    {
        private readonly object _sync = new();
        private readonly Dictionary<ProjectionReadSurface, ReadGate> _gates = [];

        public ReadGate Arm(
            ProjectionReadSurface surface,
            int expectedInvocationCount = 1)
        {
            var gate = new ReadGate(surface, expectedInvocationCount);
            lock (_sync)
            {
                _gates[surface] = gate;
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
                claimed = _gates.TryGetValue(surface, out var candidate)
                    && candidate.TryClaim(surface, fence)
                        ? candidate
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

    private sealed class MatrixOverviewReadBoundaryObserver : IWatchOverviewReadBoundaryObserver
    {
        private readonly TaskCompletionSource<OperationalSnapshotIdentity> _selected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OperationalSnapshotIdentity> FenceSelected => _selected.Task;

        public Task OnFenceSelectedAsync(
            OperationalSnapshotIdentity snapshot,
            CancellationToken cancellationToken)
        {
            _selected.TrySetResult(snapshot);
            return _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed record BusinessSurfaceFingerprint(
        string SeriesList,
        string SeriesDetail,
        string? SecondarySeriesDetail,
        string Catalog,
        string? CatalogETag,
        string? Attention);

    private sealed record CatalogCapture(JsonElement Body, string? ETag);

    private sealed record SnapshotIsolationOptions(
        byte SnapshotIsolationState,
        bool IsReadCommittedSnapshotOn);

    private sealed record HistoricalBundle(
        string SeriesList,
        string DuplicateSeries,
        string MembershipSeries,
        string LifecycleSeries,
        string ErrorSeries,
        string Catalog,
        string? CatalogETag,
        string Attention,
        string Protection,
        string SuccessTrace,
        string FailureTrace);

    private sealed record ServerMetadata(
        string Edition,
        string ProductVersion,
        int EngineEdition,
        int CompatibilityLevel);
}
