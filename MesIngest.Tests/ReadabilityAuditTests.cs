using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Text.Json;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class ReadabilityAuditTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly byte[] TokenSigningKey = Enumerable.Range(41, 32)
        .Select(value => checked((byte)value))
        .ToArray();

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public ReadabilityAuditTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public void Blocker_catalog_is_complete_and_lead_priority_never_discards_other_reasons()
    {
        Assert.Equal(
            [
                "LONG_GONE_BUT_VISIBLE",
                "DUPLICATE_TRANSPORT_DEMAND_KEY",
                "SUBLOT_MULTIPLE_WORK_TYPES",
                "REQUIRED_MES_FIELD_MISSING",
                "INVALID_MES_FIELD_FORMAT",
                "DEMAND_GONE",
                "SERIES_ARCHIVED",
            ],
            ReadabilityBlockerCatalog.Definitions.Select(definition => definition.Code));

        var blockers = ReadabilityBlockerCatalog.NormalizeMatchedCodes(
        [
            "SERIES_ARCHIVED",
            "REQUIRED_MES_FIELD_MISSING",
            "LONG_GONE_BUT_VISIBLE",
            "REQUIRED_MES_FIELD_MISSING",
        ]);

        Assert.Equal(
            ["LONG_GONE_BUT_VISIBLE", "REQUIRED_MES_FIELD_MISSING", "SERIES_ARCHIVED"],
            blockers);
        Assert.Equal("LONG_GONE_BUT_VISIBLE", ReadabilityBlockerCatalog.SelectLead(blockers));
        Assert.Null(ReadabilityBlockerCatalog.SelectLead([]));
    }

    [Fact]
    public void Audit_query_normalizes_identifiers_sets_and_enforces_page_contract()
    {
        var normalized = new ReadabilityAuditQuery(
            new ReadabilityAuditFilter
            {
                ReadabilityStates = [" READABLE ", "NOT_READABLE", "READABLE"],
                WorkTypes = ["WIRE_TO_NITROGEN", " DIE_ATTACH "],
                Blockers = ["DEMAND_GONE", " DEMAND_GONE "],
                DemandId = "  Demand-Ticket10  ",
                SublotContains = "  sl-Ticket10  ",
                MesAreas = [" N3-8 ", "N3-3", "N3-8"],
            },
            PageSize: 200)
            .NormalizeAndValidate();

        Assert.Equal(["NOT_READABLE", "READABLE"], normalized.Filter.ReadabilityStates);
        Assert.Equal([" DIE_ATTACH ", "WIRE_TO_NITROGEN"], normalized.Filter.WorkTypes);
        Assert.Equal(["DEMAND_GONE"], normalized.Filter.Blockers);
        Assert.Equal("Demand-Ticket10", normalized.Filter.DemandId);
        Assert.Equal("  sl-Ticket10  ", normalized.Filter.SublotContains);
        Assert.Equal(["N3-3", "N3-8"], normalized.Filter.MesAreas);
        Assert.Throws<ReadabilityAuditException>(() =>
            (normalized with { PageSize = 201 }).NormalizeAndValidate());
        Assert.Throws<ReadabilityAuditException>(() =>
            (normalized with { PageNumber = 2, SnapshotReference = null }).NormalizeAndValidate());
        Assert.Throws<ReadabilityAuditException>(() =>
            (normalized with { Cursor = "opaque", SnapshotReference = null }).NormalizeAndValidate());
        Assert.Throws<ReadabilityAuditException>(() =>
            new ReadabilityAuditQuery(new ReadabilityAuditFilter
            {
                DemandId = new string('D', 65),
            }).NormalizeAndValidate());
        Assert.Throws<ReadabilityAuditException>(() =>
            new ReadabilityAuditQuery(new ReadabilityAuditFilter
            {
                SublotContains = new string('S', 257),
            }).NormalizeAndValidate());
        Assert.Throws<ReadabilityAuditException>(() =>
            new ReadabilityAuditQuery(new ReadabilityAuditFilter
            {
                WorkTypes = [new string('W', 129)],
            }).NormalizeAndValidate());
    }

    [Fact]
    public void Audit_tokens_bind_snapshot_filters_area_order_contract_and_reject_tampering_or_reuse()
    {
        var snapshot = new ReadabilityAuditSnapshotIdentity(
            HistoryEpoch.CreateNew(),
            "commit-ticket10-a",
            ProjectionSequence: 17,
            new DateTimeOffset(2026, 8, 13, 22, 10, 0, TimeSpan.Zero),
            "poll-ticket10-a",
            CatalogRevision: 4);
        var filter = new ReadabilityAuditFilter
        {
            ReadabilityStates = ["NOT_READABLE", "READABLE"],
            WorkTypes = ["WIRE_TO_NITROGEN", "DIE_ATTACH"],
            Blockers = ["DEMAND_GONE", "REQUIRED_MES_FIELD_MISSING"],
            DemandId = " demand-ticket10 ",
            SublotContains = " sl-ticket10 ",
            MesAreas = ["N3-8", "N3-3"],
        }.Normalize();

        var snapshotReference = ReadabilityAuditTokenCodec.CreateSnapshotReference(
            snapshot,
            TokenSigningKey);
        Assert.True(ReadabilityAuditTokenCodec.TryReadSnapshotReference(
            snapshotReference,
            TokenSigningKey,
            out var decodedSnapshot,
            out var snapshotError));
        Assert.Null(snapshotError);
        Assert.Equal(snapshot, decodedSnapshot);

        var cursor = ReadabilityAuditTokenCodec.CreateCursor(
            snapshot,
            filter,
            ReadabilityAuditOrder.Default,
            pageSize: 25,
            targetPageNumber: 2,
            afterReadabilityRank: 0,
            afterLeadBlockerPriority: 30,
            afterDemandLastSeenAt: new DateTimeOffset(2026, 8, 13, 22, 5, 0, TimeSpan.Zero),
            afterDemandId: "demand-ticket10-anchor",
            TokenSigningKey);

        Assert.True(ReadabilityAuditTokenCodec.TryReadCursor(
            cursor,
            snapshot,
            filter,
            ReadabilityAuditOrder.Default,
            expectedPageSize: 25,
            TokenSigningKey,
            out var decodedCursor,
            out var cursorError));
        Assert.Null(cursorError);
        Assert.Equal(2, decodedCursor!.TargetPageNumber);
        Assert.Equal(
            ReadabilityAuditTokenCodec.ComputeFilterHash(filter),
            decodedCursor.FilterHash);

        var changedArea = filter with { MesAreas = ["N3-9"] };
        Assert.False(ReadabilityAuditTokenCodec.TryReadCursor(
            cursor,
            snapshot,
            changedArea,
            ReadabilityAuditOrder.Default,
            expectedPageSize: 25,
            TokenSigningKey,
            out _,
            out var mismatch));
        Assert.Equal(ReadabilityAuditErrorCodes.CursorMismatch, mismatch!.Code);

        Assert.False(ReadabilityAuditTokenCodec.TryReadCursor(
            cursor,
            snapshot with { HistoryEpoch = HistoryEpoch.CreateNew() },
            filter,
            ReadabilityAuditOrder.Default,
            expectedPageSize: 25,
            TokenSigningKey,
            out _,
            out var historyEpochMismatch));
        Assert.Equal(ReadabilityAuditErrorCodes.CursorMismatch, historyEpochMismatch!.Code);

        var separator = cursor.IndexOf('.');
        var tampered = string.Concat(
            cursor.AsSpan(0, separator - 1),
            cursor[separator - 1] == 'A' ? "B" : "A",
            cursor.AsSpan(separator));
        Assert.False(ReadabilityAuditTokenCodec.TryReadCursor(
            tampered,
            snapshot,
            filter,
            ReadabilityAuditOrder.Default,
            expectedPageSize: 25,
            TokenSigningKey,
            out _,
            out var tamperedError));
        Assert.Equal(ReadabilityAuditErrorCodes.InvalidCursor, tamperedError!.Code);

        var browseReference = DemandSeriesSnapshotTokenCodec.CreateSnapshotReference(
            new DemandSeriesSnapshotIdentity(
                HistoryEpoch.CreateNew(),
                snapshot.ProjectionCommitId,
                snapshot.ProjectionSequence,
                snapshot.ProjectionCommittedAt,
                snapshot.PollTraceId),
            TokenSigningKey);
        Assert.False(ReadabilityAuditTokenCodec.TryReadSnapshotReference(
            browseReference,
            TokenSigningKey,
            out _,
            out var crossPurposeError));
        Assert.Equal(
            ReadabilityAuditErrorCodes.InvalidSnapshotReference,
            crossPurposeError!.Code);

        Assert.False(ReadabilityAuditTokenCodec.TryReadSnapshotReference(
            new string('A', 4097),
            TokenSigningKey,
            out _,
            out var oversizedSnapshotError));
        Assert.Equal(
            ReadabilityAuditErrorCodes.InvalidSnapshotReference,
            oversizedSnapshotError!.Code);
        Assert.False(ReadabilityAuditTokenCodec.TryReadCursor(
            new string('A', 4097),
            snapshot,
            filter,
            ReadabilityAuditOrder.Default,
            expectedPageSize: 25,
            TokenSigningKey,
            out _,
            out var oversizedCursorError));
        Assert.Equal(ReadabilityAuditErrorCodes.InvalidCursor, oversizedCursorError!.Code);
    }

    [Ticket01SqlServerFact]
    public async Task Audit_filters_facets_area_order_and_detail_share_one_exact_snapshot()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var time = new DateTimeOffset(2026, 8, 13, 23, 0, 0, TimeSpan.Zero);

        var receipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket10-complex",
            time,
            Observation("SL-T10-READABLE", "N3-3", "WB-03", "QFN"),
            Observation("SL-T10-MULTI-DUP", "N3-8", "WB-08", "A"),
            Observation("SL-T10-MULTI-DUP", "N3-9", "WB-09", "B"),
            Observation("SL-T10-MULTI-DUP", "N3-8", "WB-08", "A", "DIE_ATTACH"),
            Observation("SL-T10-MISSING", "N3-8", null, "QFN"),
            Observation("SL-T10-INVALID-AREA", "N03-08", "WB-08", "QFN")));

        var audit = await GetJsonAsync(client, "/api/v2/readability-audit?pageSize=20");
        var contract = await GetJsonAsync(client, "/api/v2/contract");
        var catalog = await GetJsonAsync(client, "/api/v2/externally-readable-demand-catalog");

        Assert.Equal(receipt.ProjectionCommitId, audit.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(NewMesIngestContract.Version, contract.GetProperty("contractVersion").GetString());
        Assert.Equal(NewMesIngestContract.SchemaVersion, contract.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            ReadabilityBlockerCatalog.Definitions.Select(definition => definition.Code),
            contract.GetProperty("readabilityBlockerCatalog").EnumerateArray()
                .Select(definition => definition.GetProperty("code").GetString()));
        Assert.Equal(
            ReadabilityQualificationCheckCatalog.Definitions.Select(definition => definition.Code),
            contract.GetProperty("readabilityQualificationCheckCatalog").EnumerateArray()
                .Select(definition => definition.GetProperty("code").GetString()));
        Assert.Equal(1L, audit.GetProperty("snapshot").GetProperty("catalogRevision").GetInt64());
        Assert.Equal(5L, audit.GetProperty("exactTotalDemandCount").GetInt64());
        var items = audit.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(
            catalog.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("demandId").GetString())
                .OrderBy(value => value, StringComparer.Ordinal),
            items.Where(item =>
                    item.GetProperty("externalReadabilityState").GetString() == "READABLE")
                .Select(item => item.GetProperty("demandId").GetString())
                .OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal("NOT_READABLE", items[0].GetProperty("externalReadabilityState").GetString());
        Assert.Equal("DUPLICATE_TRANSPORT_DEMAND_KEY", items[0].GetProperty("leadReadabilityBlocker").GetString());
        Assert.Equal(
            ["DUPLICATE_TRANSPORT_DEMAND_KEY", "SUBLOT_MULTIPLE_WORK_TYPES"],
            items[0].GetProperty("readabilityBlockers").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.Equal("READABLE", items[^1].GetProperty("externalReadabilityState").GetString());
        Assert.Equal("VISIBLE", items[^1].GetProperty("demandStatus").GetString());

        var stateFacets = audit.GetProperty("facets").GetProperty("readabilityStates")
            .EnumerateArray().ToDictionary(
                facet => facet.GetProperty("state").GetString()!,
                facet => facet.GetProperty("demandCount").GetInt64());
        Assert.Equal(1L, stateFacets["READABLE"]);
        Assert.Equal(4L, stateFacets["NOT_READABLE"]);
        var blockerFacets = audit.GetProperty("facets").GetProperty("blockers")
            .EnumerateArray().ToDictionary(
                facet => facet.GetProperty("code").GetString()!,
                facet => facet.GetProperty("demandCount").GetInt64());
        Assert.Equal(1L, blockerFacets["DUPLICATE_TRANSPORT_DEMAND_KEY"]);
        Assert.Equal(2L, blockerFacets["SUBLOT_MULTIPLE_WORK_TYPES"]);
        Assert.True(blockerFacets.Values.Sum() > stateFacets["NOT_READABLE"]);

        var blockerFilteredFacets = await GetJsonAsync(
            client,
            "/api/v2/readability-audit?blocker=DUPLICATE_TRANSPORT_DEMAND_KEY&pageSize=20");
        Assert.Equal(1L, blockerFilteredFacets.GetProperty("exactTotalDemandCount").GetInt64());
        var overlappingFacetCounts = blockerFilteredFacets.GetProperty("facets").GetProperty("blockers")
            .EnumerateArray().ToDictionary(
                facet => facet.GetProperty("code").GetString()!,
                facet => facet.GetProperty("demandCount").GetInt64());
        Assert.Equal(2L, overlappingFacetCounts["SUBLOT_MULTIPLE_WORK_TYPES"]);

        var stateFilteredFacets = await GetJsonAsync(
            client,
            "/api/v2/readability-audit?state=READABLE&pageSize=20");
        var stateCountsIgnoringStateFilter = stateFilteredFacets.GetProperty("facets")
            .GetProperty("readabilityStates").EnumerateArray().ToDictionary(
                facet => facet.GetProperty("state").GetString()!,
                facet => facet.GetProperty("demandCount").GetInt64());
        Assert.Equal(4L, stateCountsIgnoringStateFilter["NOT_READABLE"]);

        var scoped = await GetJsonAsync(
            client,
            "/api/v2/readability-audit?area=N3-3&area=N3-8&pageSize=20");
        Assert.Equal(3L, scoped.GetProperty("exactTotalDemandCount").GetInt64());
        Assert.Equal(
            ["SL-T10-MISSING", "SL-T10-MULTI-DUP", "SL-T10-READABLE"],
            scoped.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("transportDemandKey").GetProperty("sublot").GetString())
                .OrderBy(value => value, StringComparer.Ordinal).ToArray());

        var filtered = await GetJsonAsync(
            client,
            "/api/v2/readability-audit?state=NOT_READABLE&blocker=DUPLICATE_TRANSPORT_DEMAND_KEY&blocker=REQUIRED_MES_FIELD_MISSING&sublot=t10&pageSize=20");
        Assert.Equal(2L, filtered.GetProperty("exactTotalDemandCount").GetInt64());
        Assert.All(filtered.GetProperty("items").EnumerateArray(), item =>
            Assert.Equal("NOT_READABLE", item.GetProperty("externalReadabilityState").GetString()));

        var caseInsensitiveDemandId = await GetJsonAsync(
            client,
            $"/api/v2/readability-audit?demandId=%20{Uri.EscapeDataString(items[^1].GetProperty("demandId").GetString()!.ToUpperInvariant())}%20&pageSize=20");
        Assert.Equal(1L, caseInsensitiveDemandId.GetProperty("exactTotalDemandCount").GetInt64());

        var workTypeOr = await GetJsonAsync(
            client,
            "/api/v2/readability-audit?workType=WIRE_TO_NITROGEN&workType=DIE_ATTACH&pageSize=20");
        Assert.Equal(5L, workTypeOr.GetProperty("exactTotalDemandCount").GetInt64());

        var duplicate = items.Single(item =>
            item.GetProperty("transportDemandKey").GetProperty("sublot").GetString()
                == "SL-T10-MULTI-DUP"
            && item.GetProperty("transportDemandKey").GetProperty("workType").GetString()
                == "WIRE_TO_NITROGEN");
        Assert.Equal(2, duplicate.GetProperty("currentRawObservationCount").GetInt32());
        var snapshotReference = audit.GetProperty("snapshotReference").GetString()!;
        var detail = await GetJsonAsync(
            client,
            $"/api/v2/readability-audit/{duplicate.GetProperty("demandId").GetString()}?snapshot={Uri.EscapeDataString(snapshotReference)}");
        Assert.Equal(receipt.ProjectionCommitId, detail.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal(7, detail.GetProperty("qualificationChecks").GetArrayLength());
        var checkResults = detail.GetProperty("qualificationChecks").EnumerateArray()
            .ToDictionary(
                check => check.GetProperty("code").GetString()!,
                check => check.GetProperty("result").GetString());
        Assert.Equal("FAIL", checkResults["UNIQUE_RAW_OBSERVATION"]);
        Assert.Equal("FAIL", checkResults["ONE_WORK_TYPE_PER_SUBLOT"]);
        Assert.Equal("NOT_EVALUATED", checkResults["REQUIRED_MES_FIELDS_PRESENT"]);
        Assert.Equal("NOT_EVALUATED", checkResults["MES_FIELD_FORMAT_VALID"]);
        Assert.Equal(2, detail.GetProperty("latestRawObservations").GetArrayLength());
        Assert.Equal(
            duplicate.GetProperty("readabilityBlockers").GetRawText(),
            JsonSerializer.Serialize(
                detail.GetProperty("blockers").EnumerateArray()
                    .Select(blocker => blocker.GetProperty("code").GetString()).ToArray()));
        Assert.All(detail.GetProperty("blockers").EnumerateArray(), blocker =>
            Assert.NotEmpty(blocker.GetProperty("evidence").EnumerateArray()));
        Assert.Equal(
            duplicate.GetProperty("latestObservationPollTraceId").GetString(),
            detail.GetProperty("latestObservationPollTrace").GetProperty("pollTraceId").GetString());
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Audit_lists_every_demand_generation_with_readability_separate_from_lifecycle()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var time = new DateTimeOffset(2026, 8, 13, 20, 0, 0, TimeSpan.Zero);
        const string sublot = "SL-T10-GENERATIONS";

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket10-generation-one",
            time,
            Observation(sublot, "N3-3", "WB-03", "QFN-1")));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket10-authority",
            time.AddMinutes(1),
            Observation(sublot, "N3-3", "WB-03", "QFN-1")));
        await ingestor.IngestAsync(SuccessRound("poll-ticket10-gone-one", time.AddMinutes(2)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket10-generation-two",
            time.AddMinutes(3),
            Observation(sublot, "N3-8", "WB-08", "QFN-2")));
        await ingestor.IngestAsync(SuccessRound("poll-ticket10-gone-two", time.AddMinutes(4)));
        await ingestor.IngestAsync(SuccessRound("poll-ticket10-archive", time.AddHours(12).AddMinutes(4)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket10-long-gone-visible",
            time.AddHours(12).AddMinutes(5),
            Observation(sublot, "N3-9", "WB-09", "QFN-3")));

        var audit = await GetJsonAsync(
            client,
            "/api/v2/readability-audit?sublot=sl-t10-generations&pageSize=20");
        var items = audit.GetProperty("items").EnumerateArray()
            .OrderBy(item => item.GetProperty("generation").GetInt32()).ToArray();

        Assert.Equal(3, items.Length);
        Assert.Equal([1, 2, 3], items.Select(item => item.GetProperty("generation").GetInt32()));
        Assert.Equal(["GONE", "GONE", "LONG_GONE_BUT_VISIBLE"], items.Select(item => item.GetProperty("demandStatus").GetString()));
        Assert.All(items, item =>
            Assert.Equal(
                "LONG_GONE_BUT_VISIBLE",
                item.GetProperty("seriesCurrentPresence").GetString()));
        Assert.All(items, item =>
        {
            Assert.Equal("ARCHIVED", item.GetProperty("seriesLifecycle").GetString());
            Assert.Equal("NOT_READABLE", item.GetProperty("externalReadabilityState").GetString());
        });
        Assert.Contains(
            "LONG_GONE_BUT_VISIBLE",
            items[2].GetProperty("readabilityBlockers").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "SERIES_ARCHIVED",
            items[0].GetProperty("readabilityBlockers").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "DEMAND_GONE",
            items[0].GetProperty("readabilityBlockers").EnumerateArray()
                .Select(value => value.GetString()));
        var historicalSnapshot = audit.GetProperty("snapshotReference").GetString()!;
        var historicalDetail = await GetJsonAsync(
            client,
            $"/api/v2/readability-audit/{items[0].GetProperty("demandId").GetString()}?snapshot={Uri.EscapeDataString(historicalSnapshot)}");
        Assert.False(historicalDetail.GetProperty("demand").GetProperty("isCurrentGeneration").GetBoolean());
        Assert.Equal(
            items[2].GetProperty("demandId").GetString(),
            historicalDetail.GetProperty("series").GetProperty("currentDemandId").GetString());
        Assert.Equal(
            "LONG_GONE_BUT_VISIBLE",
            historicalDetail.GetProperty("series").GetProperty("currentPresence").GetString());
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Audit_snapshot_stays_frozen_when_a_new_commit_changes_blockers_without_changing_catalog_revision()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var time = new DateTimeOffset(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);
        const string sublot = "SL-T10-FROZEN";

        var firstReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket10-frozen-invalid",
            time,
            Observation(sublot, "N03-08", "WB-08", "QFN")));
        var first = await GetJsonAsync(client, "/api/v2/readability-audit?pageSize=1");
        var firstItem = Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal("INVALID_MES_FIELD_FORMAT", firstItem.GetProperty("leadReadabilityBlocker").GetString());
        Assert.Equal(0L, first.GetProperty("snapshot").GetProperty("catalogRevision").GetInt64());

        var secondReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket10-frozen-missing",
            time.AddMinutes(1),
            Observation(sublot, null, "WB-08", "QFN")));
        var snapshot = first.GetProperty("snapshotReference").GetString()!;
        var oldAgain = await GetJsonAsync(
            client,
            $"/api/v2/readability-audit?pageSize=1&snapshot={Uri.EscapeDataString(snapshot)}");
        var refreshed = await GetJsonAsync(client, "/api/v2/readability-audit?pageSize=1");

        Assert.Equal(firstReceipt.ProjectionCommitId, oldAgain.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal("INVALID_MES_FIELD_FORMAT", Assert.Single(oldAgain.GetProperty("items").EnumerateArray()).GetProperty("leadReadabilityBlocker").GetString());
        Assert.Equal(secondReceipt.ProjectionCommitId, refreshed.GetProperty("snapshot").GetProperty("projectionCommitId").GetString());
        Assert.Equal("REQUIRED_MES_FIELD_MISSING", Assert.Single(refreshed.GetProperty("items").EnumerateArray()).GetProperty("leadReadabilityBlocker").GetString());
        Assert.Equal(0L, refreshed.GetProperty("snapshot").GetProperty("catalogRevision").GetInt64());

        var oldDetail = await GetJsonAsync(
            client,
            $"/api/v2/readability-audit/{firstItem.GetProperty("demandId").GetString()}?snapshot={Uri.EscapeDataString(snapshot)}");
        Assert.Equal(
            "INVALID_MES_FIELD_FORMAT",
            Assert.Single(oldDetail.GetProperty("blockers").EnumerateArray()).GetProperty("code").GetString());
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Audit_order_and_bounded_pages_are_stable_and_credentials_fail_explicitly()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var time = new DateTimeOffset(2026, 8, 13, 22, 0, 0, TimeSpan.Zero);

        using (var unavailable = await client.GetAsync("/api/v2/readability-audit"))
        {
            Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
            using var error = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
            Assert.Equal(
                ReadabilityAuditErrorCodes.ProjectionNotAvailable,
                error.RootElement.GetProperty("code").GetString());
        }
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket10-pages",
            time,
            Observation("SL-T10-PAGE-A", "N3-3", "WB-03", "QFN"),
            Observation("SL-T10-PAGE-B", "N3-8", null, "QFN"),
            Observation("SL-T10-PAGE-C", "N3-9", "WB-09", "QFN")));

        var page1 = await GetJsonAsync(client, "/api/v2/readability-audit?pageSize=1");
        var defaultPage = await GetJsonAsync(client, "/api/v2/readability-audit");
        Assert.Equal(3L, page1.GetProperty("exactTotalDemandCount").GetInt64());
        Assert.Equal(ReadabilityAuditQuery.DefaultPageSize, defaultPage.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, page1.GetProperty("totalPages").GetInt32());
        Assert.True(page1.GetProperty("hasMore").GetBoolean());
        var snapshot = page1.GetProperty("snapshotReference").GetString()!;
        var cursor = page1.GetProperty("nextCursor").GetString()!;
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket10-pages-after-freeze",
            time.AddMinutes(1),
            Observation("SL-T10-PAGE-A", "N3-3", null, "QFN"),
            Observation("SL-T10-PAGE-B", "N3-8", "WB-08", "QFN"),
            Observation("SL-T10-PAGE-C", "N3-9", "WB-09", "QFN"),
            Observation("SL-T10-PAGE-D", "N3-4", "WB-04", "QFN")));
        var page2 = await GetJsonAsync(
            client,
            $"/api/v2/readability-audit?pageSize=1&snapshot={Uri.EscapeDataString(snapshot)}&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(2, page2.GetProperty("pageNumber").GetInt32());
        Assert.NotEqual(
            Assert.Single(page1.GetProperty("items").EnumerateArray()).GetProperty("demandId").GetString(),
            Assert.Single(page2.GetProperty("items").EnumerateArray()).GetProperty("demandId").GetString());
        var cursor2 = page2.GetProperty("nextCursor").GetString()!;
        var page3 = await GetJsonAsync(
            client,
            $"/api/v2/readability-audit?pageSize=1&snapshot={Uri.EscapeDataString(snapshot)}&cursor={Uri.EscapeDataString(cursor2)}");
        Assert.Equal(3, page3.GetProperty("pageNumber").GetInt32());
        Assert.False(page3.GetProperty("hasMore").GetBoolean());
        Assert.Equal(
            3,
            new[] { page1, page2, page3 }
                .SelectMany(page => page.GetProperty("items").EnumerateArray())
                .Select(item => item.GetProperty("demandId").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());
        var fullPage = await GetJsonAsync(
            client,
            $"/api/v2/readability-audit?pageSize=200&snapshot={Uri.EscapeDataString(snapshot)}");
        var readableDemandIds = fullPage.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("externalReadabilityState").GetString() == "READABLE")
            .Select(item => item.GetProperty("demandId").GetString()!)
            .ToArray();
        Assert.Equal(readableDemandIds.Order(StringComparer.Ordinal), readableDemandIds);
        var fullPageDemandIds = fullPage.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("demandId").GetString()!)
            .ToArray();
        Assert.Equal(
            fullPageDemandIds,
            new[] { page1, page2, page3 }
                .SelectMany(page => page.GetProperty("items").EnumerateArray())
                .Select(item => item.GetProperty("demandId").GetString()!)
                .ToArray());
        var refreshed = await GetJsonAsync(client, "/api/v2/readability-audit?pageSize=200");
        Assert.Equal(4L, refreshed.GetProperty("exactTotalDemandCount").GetInt64());

        using (var mismatched = await client.GetAsync(
            $"/api/v2/readability-audit?pageSize=1&state=READABLE&snapshot={Uri.EscapeDataString(snapshot)}&cursor={Uri.EscapeDataString(cursor)}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
            using var error = JsonDocument.Parse(await mismatched.Content.ReadAsStringAsync());
            Assert.Equal(ReadabilityAuditErrorCodes.CursorMismatch, error.RootElement.GetProperty("code").GetString());
        }
        using (var repeatedSnapshot = await client.GetAsync(
            $"/api/v2/readability-audit/missing-demand-ticket10?snapshot={Uri.EscapeDataString(snapshot)}&snapshot={Uri.EscapeDataString(snapshot)}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, repeatedSnapshot.StatusCode);
            using var error = JsonDocument.Parse(await repeatedSnapshot.Content.ReadAsStringAsync());
            Assert.Equal(ReadabilityAuditErrorCodes.InvalidQuery, error.RootElement.GetProperty("code").GetString());
        }
        using (var oversizedDetailSnapshot = await client.GetAsync(
            $"/api/v2/readability-audit/missing-demand-ticket10?snapshot={new string('A', 4097)}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, oversizedDetailSnapshot.StatusCode);
            using var error = JsonDocument.Parse(await oversizedDetailSnapshot.Content.ReadAsStringAsync());
            Assert.Equal(ReadabilityAuditErrorCodes.InvalidQuery, error.RootElement.GetProperty("code").GetString());
        }

        var tampered = SignedTokenTampering.TamperSignature(snapshot);
        using (var rejected = await client.GetAsync(
            $"/api/v2/readability-audit?pageSize=1&snapshot={Uri.EscapeDataString(tampered)}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal(ReadabilityAuditErrorCodes.InvalidSnapshotReference, error.RootElement.GetProperty("code").GetString());
        }

        using (var tooLarge = await client.GetAsync("/api/v2/readability-audit?pageSize=201"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        }
        using (var notAnInteger = await client.GetAsync("/api/v2/readability-audit?pageSize=abc"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, notAnInteger.StatusCode);
            using var error = JsonDocument.Parse(await notAnInteger.Content.ReadAsStringAsync());
            Assert.Equal(ReadabilityAuditErrorCodes.InvalidQuery, error.RootElement.GetProperty("code").GetString());
        }
        using (var invalidArea = await client.GetAsync("/api/v2/readability-audit?area=N03-08"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidArea.StatusCode);
        }
        using (var missingDetail = await client.GetAsync(
            $"/api/v2/readability-audit/missing-demand-ticket10?snapshot={Uri.EscapeDataString(snapshot)}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missingDetail.StatusCode);
        }
        var signingKey = await ReadSnapshotSigningKeyAsync(database.ConnectionString);
        var retained = page1.GetProperty("snapshot");
        var expiredReference = ReadabilityAuditTokenCodec.CreateSnapshotReference(
            new ReadabilityAuditSnapshotIdentity(
                HistoryEpoch.FromGuid(Guid.Parse(
                    retained.GetProperty("historyEpoch").GetString()!)),
                $"missing-{Guid.NewGuid():N}",
                retained.GetProperty("projectionSequence").GetInt64() + 1000,
                retained.GetProperty("projectionCommittedAt").GetDateTimeOffset(),
                "missing-poll-ticket10",
                retained.GetProperty("catalogRevision").GetInt64()),
            signingKey);
        using (var expired = await client.GetAsync(
            $"/api/v2/readability-audit?snapshot={Uri.EscapeDataString(expiredReference)}"))
        {
            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
            using var error = JsonDocument.Parse(await expired.Content.ReadAsStringAsync());
            Assert.Equal(ReadabilityAuditErrorCodes.SnapshotNotFound, error.RootElement.GetProperty("code").GetString());
        }
        AssertDatabaseEvidence(database);
    }

    private static async Task<byte[]> ReadSnapshotSigningKeyAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SnapshotTokenSigningKey FROM mesingest.SchemaInfo WHERE Id = 1;";
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static MesTaskUnionObservation Observation(
        string sublot,
        string? area,
        string? eqp,
        string package,
        string workType = "WIRE_TO_NITROGEN") =>
        new(
            workType,
            sublot,
            area,
            eqp,
            "焊线2",
            new DateTimeOffset(2026, 8, 13, 9, 58, 0, TimeSpan.FromHours(8)),
            package);

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket10-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string uri)
    {
        using var response = await client.GetAsync(uri);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder => builder.UseProductionSqlApiTestHost());

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__SnapshotSource"] = MesIngestHostOptions.OracleRoundSource,
            [$"{MesIngestHostOptions.SectionName}__ContinuousPollEnabled"] = "true",
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
}
