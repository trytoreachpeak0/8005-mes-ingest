using System.Net;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using MesIngest.Infrastructure.SqlServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class DemandSeriesFrozenSnapshotTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly byte[] TokenSigningKey = Enumerable.Range(1, 32)
        .Select(value => checked((byte)value))
        .ToArray();

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public DemandSeriesFrozenSnapshotTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public void Snapshot_and_cursor_tokens_round_trip_their_complete_bindings()
    {
        var snapshot = CreateTokenSnapshot();
        var filter = new DemandSeriesBrowseFilter
        {
            Lifecycles = ["TRACKING", "ARCHIVED"],
            CurrentPresences = ["VISIBLE"],
            WorkTypes = ["WIRE_TO_NITROGEN"],
            SublotContains = "SL-TICKET08",
            MesAreas = ["N3-8", "N3-3"],
        };

        var snapshotReference = DemandSeriesSnapshotTokenCodec.CreateSnapshotReference(
            snapshot,
            TokenSigningKey);
        var snapshotRead = DemandSeriesSnapshotTokenCodec.TryReadSnapshotReference(
            snapshotReference,
            TokenSigningKey,
            out var decodedSnapshot,
            out var snapshotError);

        Assert.True(snapshotRead);
        Assert.Null(snapshotError);
        AssertSnapshotIdentity(snapshot, Assert.IsType<DemandSeriesSnapshotIdentity>(decodedSnapshot));

        var afterStartedAt = new DateTimeOffset(2026, 8, 13, 1, 55, 0, TimeSpan.Zero);
        var cursorToken = DemandSeriesSnapshotTokenCodec.CreateCursor(
            snapshot,
            filter,
            DemandSeriesBrowseOrder.Default,
            pageSize: 25,
            targetPageNumber: 2,
            afterStartedAt,
            afterSeriesId: "series-ticket08-anchor",
            TokenSigningKey);
        var cursorRead = DemandSeriesSnapshotTokenCodec.TryReadCursor(
            cursorToken,
            snapshot,
            filter,
            DemandSeriesBrowseOrder.Default,
            expectedPageSize: 25,
            TokenSigningKey,
            out var decodedCursor,
            out var cursorError);

        Assert.True(cursorRead);
        Assert.Null(cursorError);
        var cursor = Assert.IsType<DemandSeriesBrowseCursor>(decodedCursor);
        Assert.Equal(NewMesIngestContract.Version, cursor.ContractVersion);
        Assert.Equal(snapshot.HistoryEpoch, cursor.HistoryEpoch);
        Assert.Equal(snapshot.ProjectionCommitId, cursor.ProjectionCommitId);
        Assert.Equal(snapshot.ProjectionSequence, cursor.ProjectionSequence);
        Assert.Equal(DemandSeriesSnapshotTokenCodec.ComputeFilterHash(filter), cursor.FilterHash);
        Assert.Equal(DemandSeriesBrowseOrder.Default, cursor.Order);
        Assert.Equal(25, cursor.PageSize);
        Assert.Equal(2, cursor.TargetPageNumber);
        Assert.Equal(afterStartedAt, cursor.AfterStartedAt);
        Assert.Equal("series-ticket08-anchor", cursor.AfterSeriesId);

        Assert.False(DemandSeriesSnapshotTokenCodec.TryReadCursor(
            snapshotReference,
            snapshot,
            filter,
            DemandSeriesBrowseOrder.Default,
            expectedPageSize: 25,
            TokenSigningKey,
            out _,
            out var snapshotAsCursorError));
        Assert.Equal(
            DemandSeriesBrowseErrorCodes.InvalidCursor,
            Assert.IsType<DemandSeriesBrowseTokenError>(snapshotAsCursorError).Code);
        Assert.False(DemandSeriesSnapshotTokenCodec.TryReadSnapshotReference(
            cursorToken,
            TokenSigningKey,
            out _,
            out var cursorAsSnapshotError));
        Assert.Equal(
            DemandSeriesBrowseErrorCodes.InvalidSnapshotReference,
            Assert.IsType<DemandSeriesBrowseTokenError>(cursorAsSnapshotError).Code);
    }

    [Fact]
    public void Cursor_is_rejected_when_the_history_epoch_does_not_match()
    {
        var snapshot = CreateTokenSnapshot();
        var filter = new DemandSeriesBrowseFilter { Lifecycles = ["TRACKING"] };
        var token = DemandSeriesSnapshotTokenCodec.CreateCursor(
            snapshot,
            filter,
            DemandSeriesBrowseOrder.Default,
            pageSize: 50,
            targetPageNumber: 2,
            afterStartedAt: new DateTimeOffset(2026, 8, 13, 1, 55, 0, TimeSpan.Zero),
            afterSeriesId: "series-ticket08-epoch-anchor",
            TokenSigningKey);
        var anotherEpoch = snapshot with
        {
            HistoryEpoch = HistoryEpoch.FromGuid(
                Guid.Parse("20260823-0000-4000-8000-000000000007")),
        };

        var success = DemandSeriesSnapshotTokenCodec.TryReadCursor(
            token,
            anotherEpoch,
            filter,
            DemandSeriesBrowseOrder.Default,
            expectedPageSize: 50,
            TokenSigningKey,
            out var decoded,
            out var error);

        Assert.False(success);
        Assert.Null(decoded);
        Assert.Equal(
            DemandSeriesBrowseErrorCodes.CursorMismatch,
            Assert.IsType<DemandSeriesBrowseTokenError>(error).Code);
    }

    [Fact]
    public void Tampered_snapshot_reference_is_rejected_instead_of_decoding()
    {
        var token = DemandSeriesSnapshotTokenCodec.CreateSnapshotReference(
            CreateTokenSnapshot(),
            TokenSigningKey);
        var separator = token.IndexOf('.');
        Assert.InRange(separator, 1, token.Length - 2);
        var tampered = token.ToCharArray();
        tampered[separator + 1] = tampered[separator + 1] == 'A' ? 'B' : 'A';

        var success = DemandSeriesSnapshotTokenCodec.TryReadSnapshotReference(
            new string(tampered),
            TokenSigningKey,
            out var decoded,
            out var error);

        Assert.False(success);
        Assert.Null(decoded);
        Assert.Equal(
            DemandSeriesBrowseErrorCodes.InvalidSnapshotReference,
            Assert.IsType<DemandSeriesBrowseTokenError>(error).Code);
    }

    [Fact]
    public void Cursor_is_rejected_when_the_normalized_filter_does_not_match()
    {
        var snapshot = CreateTokenSnapshot();
        var issuedFilter = new DemandSeriesBrowseFilter
        {
            Lifecycles = ["TRACKING"],
            WorkTypes = ["WIRE_TO_NITROGEN"],
            MesAreas = ["N3-3", "N3-8"],
        };
        var mismatchedFilter = issuedFilter with { WorkTypes = ["DIE_ATTACH"] };
        var token = DemandSeriesSnapshotTokenCodec.CreateCursor(
            snapshot,
            issuedFilter,
            DemandSeriesBrowseOrder.Default,
            pageSize: 50,
            targetPageNumber: 2,
            afterStartedAt: new DateTimeOffset(2026, 8, 13, 1, 55, 0, TimeSpan.Zero),
            afterSeriesId: "series-ticket08-filter-anchor",
            TokenSigningKey);

        var success = DemandSeriesSnapshotTokenCodec.TryReadCursor(
            token,
            snapshot,
            mismatchedFilter,
            DemandSeriesBrowseOrder.Default,
            expectedPageSize: 50,
            TokenSigningKey,
            out var decoded,
            out var error);

        Assert.False(success);
        Assert.Null(decoded);
        Assert.Equal(
            DemandSeriesBrowseErrorCodes.CursorMismatch,
            Assert.IsType<DemandSeriesBrowseTokenError>(error).Code);
    }

    [Ticket01SqlServerFact]
    public async Task Current_list_count_filter_and_fields_do_not_depend_on_raw_history()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var completedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

        var receipt = await ingestor.IngestAsync(CreateRound(
            "poll-ticket06-current-materialized",
            completedAt.AddSeconds(-2),
            completedAt,
            area: "N3-3",
            eqp: "WB-06",
            package: "QFN-06"));

        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM mesingest.DemandRawObservations;";
            Assert.True(await command.ExecuteNonQueryAsync() > 0);
        }

        var current = await GetJsonAsync(
            client,
            "/api/v2/demand-series?pageSize=100&page=1&area=N3-3");

        AssertListAtCommit(
            current,
            receipt,
            "poll-ticket06-current-materialized",
            "N3-3",
            "WB-06",
            "QFN-06");
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Current_detail_does_not_wait_for_raw_history()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var projection = new SqlServerMesIngestProjection(database.ConnectionString);
        await projection.BeginHostSessionAsync();
        var completedAt = new DateTimeOffset(2026, 8, 23, 12, 15, 0, TimeSpan.Zero);
        var receipt = await projection.CommitRoundAsync(CreateRound(
            "poll-ticket06-current-detail",
            completedAt.AddSeconds(-2),
            completedAt,
            area: "N3-3",
            eqp: "WB-06",
            package: "QFN-06"));

        await using var blocker = new SqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var blockingTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = (SqlTransaction)blockingTransaction;
            lockCommand.CommandText =
                "SELECT COUNT_BIG(*) FROM mesingest.DemandRawObservations WITH (TABLOCKX, HOLDLOCK);";
            Assert.True(Convert.ToInt64(await lockCommand.ExecuteScalarAsync()) > 0);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var current = await projection.GetDemandSeriesAsync(
            Assert.Single(receipt.SeriesIds),
            timeout.Token);

        Assert.NotNull(current);
        Assert.Equal("N3-3", current.CurrentDemand.LiveMesFields?.Area);
        Assert.Single(current.Demands);
        Assert.Empty(current.RawObservations);
        Assert.Empty(current.Events);
        Assert.Empty(current.ErrorPeriods);
        await blockingTransaction.RollbackAsync();
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Snapshot_reference_from_another_history_epoch_is_rejected()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var completedAt = new DateTimeOffset(2026, 8, 23, 12, 30, 0, TimeSpan.Zero);

        await ingestor.IngestAsync(CreateRound(
            "poll-ticket06-cross-epoch",
            completedAt.AddSeconds(-2),
            completedAt,
            area: "N3-3",
            eqp: "WB-06",
            package: "QFN-06"));
        var current = await GetJsonAsync(client, DemandSeriesListUri);
        var signingKey = await ReadSnapshotTokenSigningKeyAsync(database.ConnectionString);
        Assert.True(DemandSeriesSnapshotTokenCodec.TryReadSnapshotReference(
            current.GetProperty("snapshotReference").GetString()!,
            signingKey,
            out var identity,
            out var decodeError));
        Assert.Null(decodeError);
        var anotherEpochReference = DemandSeriesSnapshotTokenCodec.CreateSnapshotReference(
            Assert.IsType<DemandSeriesSnapshotIdentity>(identity) with
            {
                HistoryEpoch = HistoryEpoch.CreateNew(),
            },
            signingKey);

        using var response = await client.GetAsync(
            DemandSeriesListUri
            + $"&snapshot={Uri.EscapeDataString(anotherEpochReference)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            DemandSeriesBrowseErrorCodes.SnapshotMismatch,
            body.RootElement.GetProperty("code").GetString());
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Old_snapshot_detail_stays_at_commit_a_until_a_latest_refresh_reads_commit_b()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var completedAt = new DateTimeOffset(2026, 8, 13, 2, 0, 0, TimeSpan.Zero);

        using (var unavailable = await client.GetAsync(DemandSeriesListUri))
        {
            Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
            using var unavailableJson = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
            Assert.Equal(
                DemandSeriesBrowseErrorCodes.ProjectionNotAvailable,
                unavailableJson.RootElement.GetProperty("code").GetString());
        }

        var receiptA = await ingestor.IngestAsync(CreateRound(
            "poll-ticket08-z-commit-a",
            completedAt.AddSeconds(-2),
            completedAt,
            area: "N3-3",
            eqp: "WB-03",
            package: "QFN-A"));
        var listA = await GetJsonAsync(client, DemandSeriesListUri);
        AssertListAtCommit(listA, receiptA, "poll-ticket08-z-commit-a", "N3-3", "WB-03", "QFN-A");
        var snapshotReferenceA = listA.GetProperty("snapshotReference").GetString()!;
        var historyEpochA = listA.GetProperty("snapshot").GetProperty("historyEpoch").GetString()!;
        var itemA = Assert.Single(listA.GetProperty("items").EnumerateArray());
        var seriesId = itemA.GetProperty("seriesId").GetString()!;
        var sequenceA = itemA.GetProperty("lastSeriesSequence").GetInt64();

        var receiptB = await ingestor.IngestAsync(CreateRound(
            "poll-ticket08-a-commit-b",
            completedAt.AddSeconds(-1),
            completedAt,
            area: "N3-8",
            eqp: "WB-08",
            package: "QFN-B"));
        Assert.True(receiptB.ProjectionSequence > receiptA.ProjectionSequence);

        var detailAtA = await GetJsonAsync(client, DetailUri(seriesId, snapshotReferenceA));
        AssertDetailAtCommit(
            detailAtA,
            receiptA,
            snapshotReferenceA,
            "poll-ticket08-z-commit-a",
            "N3-3",
            "WB-03",
            "QFN-A");
        Assert.Equal(
            historyEpochA,
            detailAtA.GetProperty("snapshot").GetProperty("historyEpoch").GetString());
        Assert.Equal(
            ["poll-ticket08-z-commit-a"],
            detailAtA.GetProperty("rawObservations")
                .EnumerateArray()
                .Select(item => item.GetProperty("pollTraceId").GetString())
                .ToArray());
        Assert.DoesNotContain(
            detailAtA.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("projectionCommitId").GetString() == receiptB.ProjectionCommitId);

        var byKeyAtA = await GetJsonAsync(
            client,
            ByKeyUri("WIRE_TO_NITROGEN", "SL-TICKET08-001", snapshotReferenceA));
        AssertDetailAtCommit(
            byKeyAtA,
            receiptA,
            snapshotReferenceA,
            "poll-ticket08-z-commit-a",
            "N3-3",
            "WB-03",
            "QFN-A");

        using (var invalidSnapshot = await client.GetAsync(
            ByKeyUri("WIRE_TO_NITROGEN", "SL-TICKET08-NOT-FOUND", "not-a-snapshot")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidSnapshot.StatusCode);
            using var invalidJson = JsonDocument.Parse(
                await invalidSnapshot.Content.ReadAsStringAsync());
            Assert.Equal(
                DemandSeriesBrowseErrorCodes.InvalidSnapshotReference,
                invalidJson.RootElement.GetProperty("code").GetString());
        }

        using (var repeatedSnapshot = await client.GetAsync(
            ByKeyUri("WIRE_TO_NITROGEN", "SL-TICKET08-001", snapshotReferenceA)
            + $"&snapshot={Uri.EscapeDataString(snapshotReferenceA)}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, repeatedSnapshot.StatusCode);
            using var repeatedJson = JsonDocument.Parse(
                await repeatedSnapshot.Content.ReadAsStringAsync());
            Assert.Equal(
                DemandSeriesBrowseErrorCodes.InvalidQuery,
                repeatedJson.RootElement.GetProperty("code").GetString());
        }

        var listB = await GetJsonAsync(client, DemandSeriesListUri);
        AssertListAtCommit(listB, receiptB, "poll-ticket08-a-commit-b", "N3-8", "WB-08", "QFN-B");
        Assert.Equal(
            listA.GetProperty("snapshot").GetProperty("projectionCommittedAt").GetDateTimeOffset(),
            listB.GetProperty("snapshot").GetProperty("projectionCommittedAt").GetDateTimeOffset());
        var itemB = Assert.Single(listB.GetProperty("items").EnumerateArray());
        Assert.True(itemB.GetProperty("lastSeriesSequence").GetInt64() > sequenceA);
        var snapshotReferenceB = listB.GetProperty("snapshotReference").GetString()!;
        Assert.NotEqual(snapshotReferenceA, snapshotReferenceB);

        var detailAtB = await GetJsonAsync(client, DetailUri(seriesId, snapshotReferenceB));
        AssertDetailAtCommit(
            detailAtB,
            receiptB,
            snapshotReferenceB,
            "poll-ticket08-a-commit-b",
            "N3-8",
            "WB-08",
            "QFN-B");
        Assert.Equal(
            ["poll-ticket08-z-commit-a", "poll-ticket08-a-commit-b"],
            detailAtB.GetProperty("rawObservations")
                .EnumerateArray()
                .Select(item => item.GetProperty("pollTraceId").GetString())
                .ToArray());

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Old_snapshot_detail_remains_readable_after_the_production_host_restarts()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var completedAt = new DateTimeOffset(2026, 8, 13, 3, 0, 0, TimeSpan.Zero);
        string snapshotReferenceA;
        string seriesId;
        RoundCommitReceipt receiptA;
        RoundCommitReceipt receiptB;

        await using (var firstFactory = CreateFactory())
        {
            using var firstClient = firstFactory.CreateClient();
            var ingestor = firstFactory.Services.GetRequiredService<RoundIngestor>();
            receiptA = await ingestor.IngestAsync(CreateRound(
                "poll-ticket08-restart-a",
                completedAt.AddSeconds(-2),
                completedAt,
                area: "N3-3",
                eqp: "WB-03",
                package: "QFN-A"));
            var listA = await GetJsonAsync(firstClient, DemandSeriesListUri);
            snapshotReferenceA = listA.GetProperty("snapshotReference").GetString()!;
            seriesId = Assert.Single(listA.GetProperty("items").EnumerateArray())
                .GetProperty("seriesId")
                .GetString()!;

            receiptB = await ingestor.IngestAsync(CreateRound(
                "poll-ticket08-restart-b",
                completedAt.AddSeconds(-1),
                completedAt,
                area: "N3-8",
                eqp: "WB-08",
                package: "QFN-B"));
        }

        await using (var restartedFactory = CreateFactory())
        {
            using var restartedClient = restartedFactory.CreateClient();
            var detailAtA = await GetJsonAsync(
                restartedClient,
                DetailUri(seriesId, snapshotReferenceA));
            AssertDetailAtCommit(
                detailAtA,
                receiptA,
                snapshotReferenceA,
                "poll-ticket08-restart-a",
                "N3-3",
                "WB-03",
                "QFN-A");
            Assert.DoesNotContain(
                detailAtA.GetProperty("events").EnumerateArray(),
                item => item.GetProperty("projectionCommitId").GetString() == receiptB.ProjectionCommitId);

            var listB = await GetJsonAsync(restartedClient, DemandSeriesListUri);
            AssertListAtCommit(
                listB,
                receiptB,
                "poll-ticket08-restart-b",
                "N3-8",
                "WB-08",
                "QFN-B");
        }

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Frozen_pages_are_exact_stable_and_reject_tampered_or_mismatched_credentials()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var completedAt = new DateTimeOffset(2026, 8, 13, 4, 0, 0, TimeSpan.Zero);
        var observations = Enumerable.Range(1, 5)
            .Select(index => new MesTaskUnionObservation(
                "WIRE_TO_NITROGEN",
                $"SL-TICKET08-PAGE-{index}",
                "N3-3",
                $"WB-{index:00}",
                "焊线2",
                new DateTimeOffset(2026, 8, 13, 10, index, 0, TimeSpan.FromHours(8)),
                $"QFN-{index}"))
            .ToArray();
        await ingestor.IngestAsync(new MesTaskUnionRound(
            "poll-ticket08-page-a",
            "mes-task-union-ticket08-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations));

        var page1 = await GetJsonAsync(client, "/api/v2/demand-series?pageSize=2&page=1");
        Assert.Equal(5L, page1.GetProperty("exactTotalCount").GetInt64());
        Assert.Equal(3, page1.GetProperty("totalPages").GetInt32());
        var snapshotReference = page1.GetProperty("snapshotReference").GetString()!;
        var cursor = page1.GetProperty("nextCursor").GetString()!;
        var firstIds = page1.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("seriesId").GetString()!)
            .ToArray();

        await ingestor.IngestAsync(new MesTaskUnionRound(
            "poll-ticket08-page-b",
            "mes-task-union-ticket08-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddMinutes(1).AddSeconds(-2),
            completedAt.AddMinutes(1),
            [.. observations, observations[0] with { Sublot = "SL-TICKET08-PAGE-NEW" }]));

        var page2Uri = "/api/v2/demand-series?pageSize=2"
            + $"&snapshot={Uri.EscapeDataString(snapshotReference)}"
            + $"&cursor={Uri.EscapeDataString(cursor)}";
        var page2 = await GetJsonAsync(client, page2Uri);
        Assert.Equal(5L, page2.GetProperty("exactTotalCount").GetInt64());
        Assert.Equal(2, page2.GetProperty("pageNumber").GetInt32());
        var secondIds = page2.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("seriesId").GetString()!)
            .ToArray();
        Assert.Empty(firstIds.Intersect(secondIds, StringComparer.Ordinal));

        var directPage2 = await GetJsonAsync(
            client,
            "/api/v2/demand-series?pageSize=2&page=2"
            + $"&snapshot={Uri.EscapeDataString(snapshotReference)}");
        Assert.Equal(secondIds, directPage2.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("seriesId").GetString()!)
            .ToArray());

        using var mismatched = await client.GetAsync(
            page2Uri + "&workType=DIE_ATTACH");
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        using var mismatchedJson = JsonDocument.Parse(await mismatched.Content.ReadAsStringAsync());
        Assert.Equal(
            DemandSeriesBrowseErrorCodes.CursorMismatch,
            mismatchedJson.RootElement.GetProperty("code").GetString());

        var tamperedCursor = SignedTokenTampering.TamperSignature(cursor);
        using var tampered = await client.GetAsync(
            "/api/v2/demand-series?pageSize=2"
            + $"&snapshot={Uri.EscapeDataString(snapshotReference)}"
            + $"&cursor={Uri.EscapeDataString(tamperedCursor)}");
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        using var tamperedJson = JsonDocument.Parse(await tampered.Content.ReadAsStringAsync());
        Assert.Equal(
            DemandSeriesBrowseErrorCodes.InvalidCursor,
            tamperedJson.RootElement.GetProperty("code").GetString());

        var latest = await GetJsonAsync(client, "/api/v2/demand-series?pageSize=2&page=1");
        var latestSnapshot = latest.GetProperty("snapshotReference").GetString()!;
        using (var crossSnapshot = await client.GetAsync(
                   "/api/v2/demand-series?pageSize=2"
                   + $"&snapshot={Uri.EscapeDataString(latestSnapshot)}"
                   + $"&cursor={Uri.EscapeDataString(cursor)}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, crossSnapshot.StatusCode);
            using var crossSnapshotJson = JsonDocument.Parse(await crossSnapshot.Content.ReadAsStringAsync());
            Assert.Equal(
                DemandSeriesBrowseErrorCodes.CursorMismatch,
                crossSnapshotJson.RootElement.GetProperty("code").GetString());
        }
        using (var crossPageSize = await client.GetAsync(
                   "/api/v2/demand-series?pageSize=3"
                   + $"&snapshot={Uri.EscapeDataString(snapshotReference)}"
                   + $"&cursor={Uri.EscapeDataString(cursor)}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, crossPageSize.StatusCode);
            using var crossPageSizeJson = JsonDocument.Parse(await crossPageSize.Content.ReadAsStringAsync());
            Assert.Equal(
                DemandSeriesBrowseErrorCodes.CursorMismatch,
                crossPageSizeJson.RootElement.GetProperty("code").GetString());
        }

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Frozen_detail_retains_missing_field_period_after_recovery_without_leaking_its_future_close()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var firstAt = new DateTimeOffset(2026, 8, 13, 5, 0, 0, TimeSpan.Zero);
        var invalid = new MesTaskUnionObservation(
            "WIRE_TO_NITROGEN",
            "SL-TICKET08-ERROR-PERIOD",
            Area: null,
            Eqp: "WB-03",
            Step: "焊线2",
            MesSourceDate: new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.FromHours(8)),
            Package: "QFN-A");
        var receiptA = await ingestor.IngestAsync(new MesTaskUnionRound(
            "poll-ticket08-period-a",
            "mes-task-union-ticket08-v1",
            MesTaskUnionRoundOutcome.Success,
            firstAt.AddSeconds(-2),
            firstAt,
            [invalid]));
        var listA = await GetJsonAsync(client, DemandSeriesListUri);
        var snapshotA = listA.GetProperty("snapshotReference").GetString()!;
        var seriesId = Assert.Single(listA.GetProperty("items").EnumerateArray())
            .GetProperty("seriesId").GetString()!;

        var recoveredAt = firstAt.AddMinutes(1);
        var receiptB = await ingestor.IngestAsync(new MesTaskUnionRound(
            "poll-ticket08-period-b",
            "mes-task-union-ticket08-v1",
            MesTaskUnionRoundOutcome.Success,
            recoveredAt.AddSeconds(-2),
            recoveredAt,
            [invalid with { Area = "N3-3" }]));

        var detailA = await GetJsonAsync(client, DetailUri(seriesId, snapshotA));
        var conditionA = Assert.Single(detailA.GetProperty("currentConditions").EnumerateArray());
        Assert.Equal("REQUIRED_MES_FIELD_MISSING", conditionA.GetProperty("code").GetString());
        Assert.Equal(receiptA.ProjectionCommitId, conditionA.GetProperty("latestProjectionCommitId").GetString());
        var periodA = Assert.Single(detailA.GetProperty("errorPeriods").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, periodA.GetProperty("endedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, periodA.GetProperty("endReason").ValueKind);
        Assert.DoesNotContain(
            periodA.GetProperty("evidence").EnumerateArray(),
            evidence => evidence.GetProperty("projectionCommitId").GetString() == receiptB.ProjectionCommitId);

        var listB = await GetJsonAsync(client, DemandSeriesListUri);
        var detailB = await GetJsonAsync(
            client,
            DetailUri(seriesId, listB.GetProperty("snapshotReference").GetString()!));
        Assert.Empty(detailB.GetProperty("currentConditions").EnumerateArray());
        var periodB = Assert.Single(detailB.GetProperty("errorPeriods").EnumerateArray());
        Assert.Equal(recoveredAt, periodB.GetProperty("endedAt").GetDateTimeOffset());
        Assert.Equal("CONDITION_CLEARED", periodB.GetProperty("endReason").GetString());
        Assert.Contains(
            periodB.GetProperty("evidence").EnumerateArray(),
            evidence => evidence.GetProperty("projectionCommitId").GetString() == receiptB.ProjectionCommitId);

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Frozen_snapshot_combines_all_lifecycle_presence_states_with_exact_pages_and_provenance()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var t0 = new DateTimeOffset(2026, 8, 13, 6, 0, 0, TimeSpan.Zero);
        var visible = MatrixObservation("SL-TICKET08-MATRIX-V", "N3-1", "WB-V");
        var trackingGone = MatrixObservation("SL-TICKET08-MATRIX-G", "N3-2", "WB-G");
        var archivedGone = MatrixObservation("SL-TICKET08-MATRIX-AG", "N3-3", "WB-AG");
        var archivedVisible = MatrixObservation("SL-TICKET08-MATRIX-AL", "N3-4", "WB-AL");

        var r1 = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket08-matrix-r1", t0, visible, archivedGone, archivedVisible));
        var r2 = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket08-matrix-r2", t0.AddMinutes(1), visible, archivedGone, archivedVisible));
        var r3 = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket08-matrix-r3", t0.AddMinutes(2), visible, trackingGone));
        var r4 = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket08-matrix-r4", t0.AddHours(12).AddMinutes(2), visible));
        var r5 = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket08-matrix-r5", t0.AddHours(12).AddMinutes(3), visible, archivedVisible));

        var page1 = await GetJsonAsync(client, "/api/v2/demand-series?pageSize=2&page=1");
        var snapshot = page1.GetProperty("snapshotReference").GetString()!;
        var page2 = await GetJsonAsync(
            client,
            "/api/v2/demand-series?pageSize=2"
            + $"&snapshot={Uri.EscapeDataString(snapshot)}"
            + $"&cursor={Uri.EscapeDataString(page1.GetProperty("nextCursor").GetString()!)}");

        foreach (var page in new[] { page1, page2 })
        {
            Assert.Equal(r5.ProjectionCommitId, page.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
            Assert.Equal(r5.ProjectionSequence, page.GetProperty("snapshot").GetProperty("projectionSequence").GetInt64());
            Assert.Equal(4L, page.GetProperty("exactTotalCount").GetInt64());
            Assert.Equal(2, page.GetProperty("totalPages").GetInt32());
            var facets = page.GetProperty("facets");
            Assert.Equal(2L, facets.GetProperty("trackingCount").GetInt64());
            Assert.Equal(2L, facets.GetProperty("archivedCount").GetInt64());
            Assert.Equal(1L, facets.GetProperty("visibleCount").GetInt64());
            Assert.Equal(2L, facets.GetProperty("goneCount").GetInt64());
            Assert.Equal(1L, facets.GetProperty("longGoneButVisibleCount").GetInt64());
        }
        Assert.True(page1.GetProperty("hasMore").GetBoolean());
        Assert.False(page2.GetProperty("hasMore").GetBoolean());

        var items = page1.GetProperty("items").EnumerateArray()
            .Concat(page2.GetProperty("items").EnumerateArray())
            .ToArray();
        Assert.Equal(4, items.Length);
        Assert.Equal(4, items.Select(item => item.GetProperty("seriesId").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            items.OrderByDescending(item => item.GetProperty("startedAt").GetDateTimeOffset())
                .ThenBy(item => item.GetProperty("seriesId").GetString(), StringComparer.Ordinal)
                .Select(item => item.GetProperty("seriesId").GetString()),
            items.Select(item => item.GetProperty("seriesId").GetString()));

        var bySublot = items.ToDictionary(
            item => item.GetProperty("sublot").GetString()!,
            StringComparer.Ordinal);
        AssertState(bySublot[visible.Sublot!], "TRACKING", "VISIBLE", 1, r5);
        AssertState(bySublot[trackingGone.Sublot!], "TRACKING", "GONE", 1, r4);
        AssertState(bySublot[archivedGone.Sublot!], "ARCHIVED", "GONE", 1, r4);
        AssertState(bySublot[archivedVisible.Sublot!], "ARCHIVED", "LONG_GONE_BUT_VISIBLE", 2, r5);
        Assert.All(items, item =>
        {
            Assert.Equal("WIRE_TO_NITROGEN", item.GetProperty("workType").GetString());
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("seriesId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("currentDemandId").GetString()));
            Assert.NotEqual(default, item.GetProperty("startedAt").GetDateTimeOffset());
        });

        var archivedOnly = await GetJsonAsync(
            client,
            "/api/v2/demand-series?pageSize=100&page=1&lifecycle=ARCHIVED"
            + $"&snapshot={Uri.EscapeDataString(snapshot)}");
        Assert.Equal(2L, archivedOnly.GetProperty("exactTotalCount").GetInt64());
        Assert.Equal(0L, archivedOnly.GetProperty("facets").GetProperty("trackingCount").GetInt64());
        Assert.Equal(2L, archivedOnly.GetProperty("facets").GetProperty("archivedCount").GetInt64());
        Assert.Equal(
            [archivedGone.Sublot, archivedVisible.Sublot],
            archivedOnly.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("sublot").GetString())
                .Order(StringComparer.Ordinal).ToArray());
        var goneOnly = await GetJsonAsync(
            client,
            "/api/v2/demand-series?pageSize=100&page=1&presence=GONE"
            + $"&snapshot={Uri.EscapeDataString(snapshot)}");
        Assert.Equal(2L, goneOnly.GetProperty("exactTotalCount").GetInt64());
        var archivedArea = await GetJsonAsync(
            client,
            "/api/v2/demand-series?pageSize=100&page=1&area=N3-3"
            + $"&snapshot={Uri.EscapeDataString(snapshot)}");
        Assert.Equal(archivedGone.Sublot, Assert.Single(archivedArea.GetProperty("items").EnumerateArray())
            .GetProperty("sublot").GetString());

        var receiptByPoll = new Dictionary<string, RoundCommitReceipt>(StringComparer.Ordinal)
        {
            [r1.PollTraceId] = r1,
            [r2.PollTraceId] = r2,
            [r3.PollTraceId] = r3,
            [r4.PollTraceId] = r4,
            [r5.PollTraceId] = r5,
        };
        foreach (var item in items)
        {
            var detail = await GetJsonAsync(
                client,
                DetailUri(item.GetProperty("seriesId").GetString()!, snapshot));
            var events = detail.GetProperty("events").EnumerateArray().ToArray();
            Assert.Equal(
                Enumerable.Range(1, events.Length).Select(value => (long)value),
                events.Select(value => value.GetProperty("seriesSequence").GetInt64()));
            Assert.All(events, value =>
            {
                var receipt = receiptByPoll[value.GetProperty("pollTraceId").GetString()!];
                Assert.Equal(receipt.ProjectionCommitId, value.GetProperty("projectionCommitId").GetString());
                Assert.True(value.GetProperty("payloadVersion").GetInt32() >= 1);
                Assert.Equal(detail.GetProperty("seriesId").GetString(), value.GetProperty("seriesId").GetString());
            });
            Assert.All(detail.GetProperty("rawObservations").EnumerateArray(), value =>
            {
                var receipt = receiptByPoll[value.GetProperty("pollTraceId").GetString()!];
                Assert.Equal(receipt.ProjectionCommitId, value.GetProperty("projectionCommitId").GetString());
                Assert.Equal(receiptByPoll[value.GetProperty("pollTraceId").GetString()!].PollTraceId,
                    value.GetProperty("pollTraceId").GetString());
                Assert.NotEqual(default, value.GetProperty("observedAt").GetDateTimeOffset());
                Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("demandId").GetString()));
            });
        }

        var longGoneDetail = await GetJsonAsync(
            client,
            DetailUri(bySublot[archivedVisible.Sublot!].GetProperty("seriesId").GetString()!, snapshot));
        var generations = longGoneDetail.GetProperty("demands").EnumerateArray().ToArray();
        Assert.Equal([1, 2], generations.Select(value => value.GetProperty("generation").GetInt32()).ToArray());
        Assert.Equal("GONE", generations[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, generations[0].GetProperty("predecessorDemandId").ValueKind);
        Assert.Equal("LONG_GONE_BUT_VISIBLE", generations[1].GetProperty("status").GetString());
        Assert.Equal(generations[0].GetProperty("demandId").GetString(), generations[1].GetProperty("predecessorDemandId").GetString());
        Assert.Equal(generations[1].GetProperty("demandId").GetString(), longGoneDetail.GetProperty("currentDemand").GetProperty("demandId").GetString());
        var successorEvent = Assert.Single(longGoneDetail.GetProperty("events").EnumerateArray()
            .Where(value => value.GetProperty("eventType").GetString() == "TRANSPORT_DEMAND_CREATED"
                && value.GetProperty("pollTraceId").GetString() == r5.PollTraceId));
        Assert.Equal(generations[1].GetProperty("demandId").GetString(), successorEvent.GetProperty("subjectId").GetString());
        using (var payload = JsonDocument.Parse(successorEvent.GetProperty("payloadJson").GetString()!))
        {
            Assert.Equal(2, payload.RootElement.GetProperty("generation").GetInt32());
            Assert.Equal(generations[0].GetProperty("demandId").GetString(), payload.RootElement.GetProperty("predecessorDemandId").GetString());
            Assert.Equal("POSTARCHIVE_REAPPEARANCE", payload.RootElement.GetProperty("reason").GetString());
        }
        var historicalDemandLookup = await GetJsonAsync(
            client,
            "/api/v2/demand-series?pageSize=100&page=1"
            + $"&snapshot={Uri.EscapeDataString(snapshot)}"
            + $"&demandId={Uri.EscapeDataString(generations[0].GetProperty("demandId").GetString()!)}");
        Assert.Equal(
            longGoneDetail.GetProperty("seriesId").GetString(),
            Assert.Single(historicalDemandLookup.GetProperty("items").EnumerateArray())
                .GetProperty("seriesId").GetString());

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Frozen_conflict_detail_keeps_duplicate_multiset_and_multi_work_type_evidence_after_recovery()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var at = new DateTimeOffset(2026, 8, 13, 7, 0, 0, TimeSpan.Zero);
        const string sublot = "SL-TICKET08-COMBINED-CONFLICT";
        var a1 = ConflictObservation("LOADPORT_TO_OVEN", sublot, "A1-1", "EQ-A1", "PKG-A1");
        var a2 = ConflictObservation("LOADPORT_TO_OVEN", sublot, "A1-2", "EQ-A2", "PKG-A2");
        var b = ConflictObservation("STAGING_TO_WIRE", sublot, "B2-2", "EQ-B", "PKG-B");
        var conflict = await ingestor.IngestAsync(SuccessRound("poll-ticket08-conflict-a", at, a1, b, a2));
        var listA = await GetJsonAsync(client, DemandSeriesListUri);
        var snapshotA = listA.GetProperty("snapshotReference").GetString()!;
        Assert.Equal(2L, listA.GetProperty("exactTotalCount").GetInt64());
        Assert.All(listA.GetProperty("items").EnumerateArray(), item => Assert.Equal("NOT_READABLE", item.GetProperty("externalReadabilityState").GetString()));

        var itemA = Assert.Single(listA.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("workType").GetString() == "LOADPORT_TO_OVEN"));
        var seriesId = itemA.GetProperty("seriesId").GetString()!;
        var detailA = await GetJsonAsync(client, DetailUri(seriesId, snapshotA));
        Assert.Equal(JsonValueKind.Null, detailA.GetProperty("currentDemand").GetProperty("liveMesFields").ValueKind);
        Assert.Equal(
            ["DUPLICATE_TRANSPORT_DEMAND_KEY", "SUBLOT_MULTIPLE_WORK_TYPES"],
            detailA.GetProperty("currentDemand").GetProperty("readabilityBlockers")
                .EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, detailA.GetProperty("currentConditions").GetArrayLength());
        Assert.Equal(2, detailA.GetProperty("errorPeriods").GetArrayLength());
        Assert.Equal(
            ["DUPLICATE_TRANSPORT_DEMAND_KEY", "SUBLOT_MULTIPLE_WORK_TYPES"],
            detailA.GetProperty("currentConditions").EnumerateArray()
                .Select(value => value.GetProperty("code").GetString())
                .Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["DUPLICATE_TRANSPORT_DEMAND_KEY", "SUBLOT_MULTIPLE_WORK_TYPES"],
            detailA.GetProperty("errorPeriods").EnumerateArray()
                .Select(value => value.GetProperty("code").GetString())
                .Order(StringComparer.Ordinal).ToArray());
        Assert.All(detailA.GetProperty("errorPeriods").EnumerateArray(), period =>
        {
            var evidence = Assert.Single(period.GetProperty("evidence").EnumerateArray());
            Assert.Equal(conflict.PollTraceId, evidence.GetProperty("pollTraceId").GetString());
            Assert.Equal(conflict.ProjectionCommitId, evidence.GetProperty("projectionCommitId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(evidence.GetProperty("demandId").GetString()));
            Assert.Equal(JsonValueKind.Null, period.GetProperty("endedAt").ValueKind);
        });
        Assert.Equal([0, 2], detailA.GetProperty("rawObservations").EnumerateArray()
            .Select(value => value.GetProperty("ordinal").GetInt32()).ToArray());
        Assert.All(detailA.GetProperty("rawObservations").EnumerateArray(), value =>
        {
            Assert.Equal(conflict.PollTraceId, value.GetProperty("pollTraceId").GetString());
            Assert.Equal(conflict.ProjectionCommitId, value.GetProperty("projectionCommitId").GetString());
            Assert.Equal(seriesId, value.GetProperty("seriesId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("demandId").GetString()));
        });
        Assert.Equal(
            ["A1-1", "A1-2"],
            detailA.GetProperty("rawObservations").EnumerateArray()
                .Select(value => value.GetProperty("area").GetString())
                .Order(StringComparer.Ordinal).ToArray());

        var filteredWorkType = await GetJsonAsync(
            client,
            "/api/v2/demand-series?pageSize=100&page=1&workType=LOADPORT_TO_OVEN"
            + $"&snapshot={Uri.EscapeDataString(snapshotA)}");
        Assert.Equal(seriesId, Assert.Single(filteredWorkType.GetProperty("items").EnumerateArray())
            .GetProperty("seriesId").GetString());
        var duplicateArea = await GetJsonAsync(
            client,
            "/api/v2/demand-series?pageSize=100&page=1&area=A1-1"
            + $"&snapshot={Uri.EscapeDataString(snapshotA)}");
        Assert.Equal(0L, duplicateArea.GetProperty("exactTotalCount").GetInt64());

        var recoveryAt = at.AddMinutes(1);
        var recovered = await ingestor.IngestAsync(SuccessRound("poll-ticket08-conflict-b", recoveryAt, a1));
        var frozenAgain = await GetJsonAsync(client, DetailUri(seriesId, snapshotA));
        Assert.Equal(detailA.GetRawText(), frozenAgain.GetRawText());

        var latest = await GetJsonAsync(client, DemandSeriesListUri);
        var detailB = await GetJsonAsync(
            client,
            DetailUri(seriesId, latest.GetProperty("snapshotReference").GetString()!));
        Assert.Equal("READABLE", detailB.GetProperty("currentDemand").GetProperty("externalReadabilityState").GetString());
        Assert.Empty(detailB.GetProperty("currentConditions").EnumerateArray());
        Assert.Equal(2, detailB.GetProperty("errorPeriods").GetArrayLength());
        Assert.All(detailB.GetProperty("errorPeriods").EnumerateArray(), period =>
        {
            Assert.Equal(recoveryAt, period.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal("CONDITION_CLEARED", period.GetProperty("endReason").GetString());
            Assert.Contains(period.GetProperty("evidence").EnumerateArray(), evidence =>
                evidence.GetProperty("projectionCommitId").GetString() == recovered.ProjectionCommitId);
        });
        Assert.Equal(3, detailB.GetProperty("rawObservations").GetArrayLength());

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Prearchive_reappearance_creates_a_frozen_successor_without_rewriting_the_gone_snapshot()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var t0 = new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
        var first = MatrixObservation("SL-TICKET08-PREARCHIVE", "N3-3", "WB-G1");

        var generation1 = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket08-prearchive-r1", t0, first));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket08-prearchive-r2", t0.AddMinutes(1), first));
        var gone = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket08-prearchive-r3", t0.AddMinutes(2)));
        var listGone = await GetJsonAsync(client, DemandSeriesListUri);
        var snapshotGone = listGone.GetProperty("snapshotReference").GetString()!;
        var itemGone = Assert.Single(listGone.GetProperty("items").EnumerateArray());
        var seriesId = itemGone.GetProperty("seriesId").GetString()!;
        var demand1 = itemGone.GetProperty("currentDemandId").GetString()!;
        AssertState(itemGone, "TRACKING", "GONE", 1, gone);

        var reappearedAt = t0.AddMinutes(3);
        var generation2 = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket08-prearchive-r4",
            reappearedAt,
            first with { Area = "N3-8", Eqp = "WB-G2", Package = "QFN-G2" }));

        var frozenGone = await GetJsonAsync(client, DetailUri(seriesId, snapshotGone));
        Assert.Equal("TRACKING", frozenGone.GetProperty("lifecycle").GetString());
        Assert.Equal("GONE", frozenGone.GetProperty("currentPresence").GetString());
        var frozenDemand = Assert.Single(frozenGone.GetProperty("demands").EnumerateArray());
        Assert.Equal(demand1, frozenDemand.GetProperty("demandId").GetString());
        Assert.Equal("GONE", frozenDemand.GetProperty("status").GetString());
        Assert.DoesNotContain(
            frozenGone.GetProperty("events").EnumerateArray(),
            value => value.GetProperty("projectionCommitId").GetString() == generation2.ProjectionCommitId);

        var listVisible = await GetJsonAsync(client, DemandSeriesListUri);
        var snapshotVisible = listVisible.GetProperty("snapshotReference").GetString()!;
        var itemVisible = Assert.Single(listVisible.GetProperty("items").EnumerateArray());
        Assert.Equal(seriesId, itemVisible.GetProperty("seriesId").GetString());
        AssertState(itemVisible, "TRACKING", "VISIBLE", 2, generation2);
        var detailVisible = await GetJsonAsync(client, DetailUri(seriesId, snapshotVisible));
        var demands = detailVisible.GetProperty("demands").EnumerateArray().ToArray();
        Assert.Equal([1, 2], demands.Select(value => value.GetProperty("generation").GetInt32()).ToArray());
        Assert.Equal(demand1, demands[0].GetProperty("demandId").GetString());
        Assert.Equal("GONE", demands[0].GetProperty("status").GetString());
        Assert.Equal(gone.ProjectionCommitId, demands[0].GetProperty("latestProjectionCommitId").GetString());
        var demand2 = demands[1].GetProperty("demandId").GetString()!;
        Assert.NotEqual(demand1, demand2);
        Assert.Equal(demand1, demands[1].GetProperty("predecessorDemandId").GetString());
        Assert.Equal("VISIBLE", demands[1].GetProperty("status").GetString());
        Assert.Equal(generation2.ProjectionCommitId, demands[1].GetProperty("createdProjectionCommitId").GetString());
        Assert.Equal(demand2, detailVisible.GetProperty("currentDemand").GetProperty("demandId").GetString());
        var created = Assert.Single(detailVisible.GetProperty("events").EnumerateArray()
            .Where(value => value.GetProperty("eventType").GetString() == "TRANSPORT_DEMAND_CREATED"
                && value.GetProperty("projectionCommitId").GetString() == generation2.ProjectionCommitId));
        Assert.Equal(demand2, created.GetProperty("subjectId").GetString());
        Assert.Equal(generation2.PollTraceId, created.GetProperty("pollTraceId").GetString());
        using (var payload = JsonDocument.Parse(created.GetProperty("payloadJson").GetString()!))
        {
            Assert.Equal(demand1, payload.RootElement.GetProperty("predecessorDemandId").GetString());
            Assert.Equal("PREARCHIVE_REAPPEARANCE", payload.RootElement.GetProperty("reason").GetString());
        }
        Assert.Equal(
            Enumerable.Range(1, detailVisible.GetProperty("events").GetArrayLength()).Select(value => (long)value),
            detailVisible.GetProperty("events").EnumerateArray()
                .Select(value => value.GetProperty("seriesSequence").GetInt64()));
        var rawGeneration2 = Assert.Single(detailVisible.GetProperty("rawObservations").EnumerateArray()
            .Where(value => value.GetProperty("projectionCommitId").GetString() == generation2.ProjectionCommitId));
        Assert.Equal(demand2, rawGeneration2.GetProperty("demandId").GetString());
        Assert.Equal(reappearedAt, rawGeneration2.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(generation1.ProjectionCommitId, demands[0].GetProperty("createdProjectionCommitId").GetString());

        AssertDatabaseEvidence(database);
    }

    private const string DemandSeriesListUri = "/api/v2/demand-series?pageSize=100&page=1";

    private static DemandSeriesSnapshotIdentity CreateTokenSnapshot() =>
        new(
            HistoryEpoch: HistoryEpoch.FromGuid(
                Guid.Parse("20260823-0000-4000-8000-000000000006")),
            ProjectionCommitId: "projection-ticket08-a",
            ProjectionSequence: 42,
            ProjectionCommittedAt: new DateTimeOffset(2026, 8, 13, 2, 0, 0, TimeSpan.Zero),
            PollTraceId: "poll-ticket08-a");

    private static void AssertSnapshotIdentity(
        DemandSeriesSnapshotIdentity expected,
        DemandSeriesSnapshotIdentity actual)
    {
        Assert.Equal(expected.HistoryEpoch, actual.HistoryEpoch);
        Assert.Equal(expected.ProjectionCommitId, actual.ProjectionCommitId);
        Assert.Equal(expected.ProjectionSequence, actual.ProjectionSequence);
        Assert.Equal(expected.ProjectionCommittedAt, actual.ProjectionCommittedAt);
        Assert.Equal(expected.PollTraceId, actual.PollTraceId);
        Assert.Equal(expected.ContractVersion, actual.ContractVersion);
    }

    private static MesTaskUnionRound CreateRound(
        string pollTraceId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string area,
        string eqp,
        string package) =>
        new(
            PollTraceId: pollTraceId,
            QueryVersion: "mes-task-union-ticket08-v1",
            Outcome: MesTaskUnionRoundOutcome.Success,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            Observations:
            [
                new MesTaskUnionObservation(
                    WorkType: "WIRE_TO_NITROGEN",
                    Sublot: "SL-TICKET08-001",
                    Area: area,
                    Eqp: eqp,
                    Step: "焊线2",
                    MesSourceDate: new DateTimeOffset(
                        2026,
                        8,
                        13,
                        9,
                        58,
                        0,
                        TimeSpan.FromHours(8)),
                    Package: package),
            ]);

    private static MesTaskUnionObservation MatrixObservation(string sublot, string area, string eqp) =>
        new(
            "WIRE_TO_NITROGEN",
            sublot,
            area,
            eqp,
            "焊线2",
            new DateTimeOffset(2026, 8, 13, 13, 0, 0, TimeSpan.FromHours(8)),
            "QFN-MATRIX");

    private static MesTaskUnionObservation ConflictObservation(
        string workType,
        string sublot,
        string area,
        string eqp,
        string package) =>
        new(
            workType,
            sublot,
            area,
            eqp,
            "CONFLICT-STEP",
            new DateTimeOffset(2026, 8, 13, 14, 0, 0, TimeSpan.FromHours(8)),
            package);

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket08-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static void AssertState(
        JsonElement item,
        string lifecycle,
        string presence,
        int generation,
        RoundCommitReceipt latestReceipt)
    {
        Assert.Equal(lifecycle, item.GetProperty("lifecycle").GetString());
        Assert.Equal(presence, item.GetProperty("currentPresence").GetString());
        Assert.Equal(generation, item.GetProperty("currentGeneration").GetInt32());
        Assert.True(item.GetProperty("lastSeriesSequence").GetInt64() >= 2);
        Assert.Equal(latestReceipt.PollTraceId, item.GetProperty("latestPollTraceId").GetString());
        Assert.Equal(latestReceipt.ProjectionCommitId, item.GetProperty("latestProjectionCommitId").GetString());
    }

    private static void AssertListAtCommit(
        JsonElement list,
        RoundCommitReceipt receipt,
        string pollTraceId,
        string area,
        string eqp,
        string package)
    {
        Assert.Equal(1, list.GetProperty("pageNumber").GetInt32());
        Assert.Equal(100, list.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, list.GetProperty("totalPages").GetInt32());
        Assert.Equal(1L, list.GetProperty("exactTotalCount").GetInt64());
        Assert.False(list.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("nextCursor").ValueKind);

        Assert.False(string.IsNullOrWhiteSpace(list.GetProperty("snapshotReference").GetString()));
        var snapshot = list.GetProperty("snapshot");
        Assert.True(Guid.TryParseExact(
            snapshot.GetProperty("historyEpoch").GetString(),
            "D",
            out var historyEpoch));
        Assert.NotEqual(Guid.Empty, historyEpoch);
        Assert.Equal(receipt.ProjectionCommitId, snapshot.GetProperty("projectionCommitId").GetString());
        Assert.Equal(receipt.ProjectionSequence, snapshot.GetProperty("projectionSequence").GetInt64());
        Assert.Equal(pollTraceId, snapshot.GetProperty("pollTraceId").GetString());
        Assert.Equal(NewMesIngestContract.Version, snapshot.GetProperty("contractVersion").GetString());

        var facets = list.GetProperty("facets");
        Assert.Equal(1L, facets.GetProperty("trackingCount").GetInt64());
        Assert.Equal(0L, facets.GetProperty("archivedCount").GetInt64());
        Assert.Equal(1L, facets.GetProperty("visibleCount").GetInt64());

        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("WIRE_TO_NITROGEN", item.GetProperty("workType").GetString());
        Assert.Equal("SL-TICKET08-001", item.GetProperty("sublot").GetString());
        Assert.Equal("TRACKING", item.GetProperty("lifecycle").GetString());
        Assert.Equal("VISIBLE", item.GetProperty("currentPresence").GetString());
        Assert.Equal(pollTraceId, item.GetProperty("latestPollTraceId").GetString());
        Assert.Equal(receipt.ProjectionCommitId, item.GetProperty("latestProjectionCommitId").GetString());
        Assert.True(item.GetProperty("lastSeriesSequence").GetInt64() > 0);
        AssertLiveFields(item.GetProperty("liveMesFields"), area, eqp, package);
    }

    private static void AssertDetailAtCommit(
        JsonElement detail,
        RoundCommitReceipt receipt,
        string snapshotReference,
        string pollTraceId,
        string area,
        string eqp,
        string package)
    {
        Assert.Equal(snapshotReference, detail.GetProperty("snapshotReference").GetString());
        var snapshot = detail.GetProperty("snapshot");
        Assert.True(Guid.TryParseExact(
            snapshot.GetProperty("historyEpoch").GetString(),
            "D",
            out var historyEpoch));
        Assert.NotEqual(Guid.Empty, historyEpoch);
        Assert.Equal(receipt.ProjectionCommitId, snapshot.GetProperty("projectionCommitId").GetString());
        Assert.Equal(receipt.ProjectionSequence, snapshot.GetProperty("projectionSequence").GetInt64());
        Assert.Equal(pollTraceId, snapshot.GetProperty("pollTraceId").GetString());

        var series = detail;
        Assert.Equal("WIRE_TO_NITROGEN", series.GetProperty("workType").GetString());
        Assert.Equal("SL-TICKET08-001", series.GetProperty("sublot").GetString());
        Assert.Equal(receipt.ProjectionCommitId, series.GetProperty("latestProjectionCommitId").GetString());
        Assert.True(series.GetProperty("lastSeriesSequence").GetInt64() > 0);
        var currentDemand = series.GetProperty("currentDemand");
        Assert.Equal(receipt.ProjectionCommitId, currentDemand.GetProperty("latestProjectionCommitId").GetString());
        AssertLiveFields(currentDemand.GetProperty("liveMesFields"), area, eqp, package);
    }

    private static void AssertLiveFields(
        JsonElement fields,
        string area,
        string eqp,
        string package)
    {
        Assert.Equal(area, fields.GetProperty("area").GetString());
        Assert.Equal(eqp, fields.GetProperty("eqp").GetString());
        Assert.Equal("焊线2", fields.GetProperty("step").GetString());
        Assert.Equal(package, fields.GetProperty("package").GetString());
    }

    private static string DetailUri(string seriesId, string snapshotReference) =>
        $"/api/v2/demand-series/{Uri.EscapeDataString(seriesId)}?snapshot={Uri.EscapeDataString(snapshotReference)}";

    private static string ByKeyUri(string workType, string sublot, string snapshotReference) =>
        "/api/v2/demand-series/by-key"
        + $"?workType={Uri.EscapeDataString(workType)}"
        + $"&sublot={Uri.EscapeDataString(sublot)}"
        + $"&snapshot={Uri.EscapeDataString(snapshotReference)}";

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string requestUri)
    {
        using var response = await client.GetAsync(requestUri);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}): {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<byte[]> ReadSnapshotTokenSigningKeyAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SnapshotTokenSigningKey FROM mesingest.SchemaInfo WHERE Id = 1;";
        return Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Production));

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "false",
            [$"{MesIngestHostOptions.SectionName}__RunOneShotOnStartup"] = "false",
        });

    private void AssertDatabaseEvidence(Ticket01SqlServerDatabase database)
    {
        Assert.False(database.IsLocalDb);
        Assert.Equal(database.ExpectedProductMajor, database.ProductMajor);
        Assert.Equal(database.ExpectedCompatibilityLevel, database.CompatibilityLevel);
        Assert.InRange(database.EngineEdition, 1, 4);
        _output.WriteLine(
            $"Real SQL Server ProductVersion={database.ProductVersion}; "
            + $"ProductMajor={database.ProductMajor}; "
            + $"EngineEdition={database.EngineEdition}; "
            + $"CompatibilityLevel={database.CompatibilityLevel}");
    }
}
