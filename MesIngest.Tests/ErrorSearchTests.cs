using System.Globalization;
using System.Net;
using System.Text.Json;
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
public sealed class ErrorSearchTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly byte[] TokenSigningKey = Enumerable.Range(73, 32)
        .Select(value => checked((byte)value))
        .ToArray();

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public ErrorSearchTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public void Error_search_query_normalizes_filters_and_resolves_exact_utc_windows()
    {
        var asOf = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        var normalized = new ErrorSearchQuery(
            new ErrorSearchFilter
            {
                Categories = [" data_format ", "OBSERVATION_CONFLICT", "DATA_FORMAT"],
                ErrorCodes = [" invalid_mes_field_format ", "INVALID_MES_FIELD_FORMAT"],
                ActivityStates = [" ended ", "ACTIVE", "ENDED"],
                SeriesId = "  Series-Ticket11  ",
                DemandId = "  Demand-Ticket11  ",
                SublotContains = "  sl-Ticket11  ",
            },
            ErrorSearchWindowSelection.Last7Days,
            PageSize: 200)
            .NormalizeAndValidate();

        Assert.Equal(["DATA_FORMAT", "OBSERVATION_CONFLICT"], normalized.Filter.Categories);
        Assert.Equal(["INVALID_MES_FIELD_FORMAT"], normalized.Filter.ErrorCodes);
        Assert.Equal(["ACTIVE", "ENDED"], normalized.Filter.ActivityStates);
        Assert.Equal("SERIES-TICKET11", normalized.Filter.SeriesId);
        Assert.Equal("DEMAND-TICKET11", normalized.Filter.DemandId);
        Assert.Equal("SL-TICKET11", normalized.Filter.SublotContains);

        var defaultWindow = normalized.Window.Resolve(asOf);
        Assert.Equal(ErrorSearchWindowKinds.Last7Days, defaultWindow.Kind);
        Assert.Equal(asOf.AddHours(-7 * 24), defaultWindow.FromUtc);
        Assert.Equal(asOf, defaultWindow.ToUtc);

        Assert.Equal(asOf.AddHours(-24), ErrorSearchWindowSelection.Last24Hours.Resolve(asOf).FromUtc);
        Assert.Equal(asOf.AddHours(-30 * 24), ErrorSearchWindowSelection.Last30Days.Resolve(asOf).FromUtc);
        Assert.Null(ErrorSearchWindowSelection.AllHistory.Resolve(asOf).FromUtc);

        var custom = ErrorSearchWindowSelection.Custom(
            DateTimeOffset.Parse("2026-08-13T07:00:00+08:00"),
            toUtc: null).Resolve(asOf);
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 23, 0, 0, TimeSpan.Zero), custom.FromUtc);
        Assert.Equal(asOf, custom.ToUtc);

        Assert.True(ErrorSearchInterval.Overlaps(
            asOf.AddHours(-48), asOf.AddHours(-12), defaultWindow, asOf));
        Assert.False(ErrorSearchInterval.Overlaps(
            asOf.AddHours(-192), defaultWindow.FromUtc, defaultWindow, asOf));
        Assert.False(ErrorSearchInterval.Overlaps(
            defaultWindow.ToUtc, periodEndUtc: null, defaultWindow, asOf));
        Assert.True(ErrorSearchInterval.Overlaps(
            asOf.AddHours(-1), periodEndUtc: null, defaultWindow, asOf));

        Assert.Throws<ErrorSearchException>(() =>
            ErrorSearchWindowSelection.Custom(asOf, asOf).Resolve(asOf));
        Assert.Throws<ErrorSearchException>(() =>
            ErrorSearchWindowSelection.Custom(asOf.AddHours(-1), asOf.AddMinutes(1)).Resolve(asOf));
        Assert.Throws<ErrorSearchException>(() =>
            (normalized with { PageSize = 201 }).NormalizeAndValidate());
        Assert.Throws<ErrorSearchException>(() =>
            new ErrorSearchQuery(
                new ErrorSearchFilter
                {
                    Categories = ["DATA_COMPLETENESS"],
                    ErrorCodes = ["INVALID_MES_FIELD_FORMAT"],
                },
                ErrorSearchWindowSelection.Last7Days).NormalizeAndValidate());
    }

    [Fact]
    public void Error_search_tokens_bind_history_epoch_asof_high_water_filter_window_order_and_page_size()
    {
        var identity = new ErrorSearchSnapshotIdentity(
            HistoryEpoch.FromGuid(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new DateTimeOffset(2026, 8, 14, 0, 15, 0, TimeSpan.Zero),
            "commit-ticket11-a",
            ProjectionSequence: 41,
            new DateTimeOffset(2026, 8, 13, 23, 59, 0, TimeSpan.Zero),
            "poll-ticket11-a");
        var filter = new ErrorSearchFilter
        {
            Categories = ["OBSERVATION_CONFLICT", "DATA_FORMAT"],
            ErrorCodes = ["INVALID_MES_FIELD_FORMAT", "DUPLICATE_TRANSPORT_DEMAND_KEY"],
            ActivityStates = ["ENDED", "ACTIVE"],
            SeriesId = " series-ticket11 ",
            DemandId = " demand-ticket11 ",
            SublotContains = " sl-ticket11 ",
        }.Normalize();
        var window = ErrorSearchWindowSelection.Last7Days.Resolve(identity.ErrorSearchAsOf);
        var snapshot = new ErrorSearchSnapshotReference(
            identity,
            filter,
            window,
            ErrorSearchOrder.Default);

        var snapshotToken = ErrorSearchTokenCodec.CreateSnapshotReference(snapshot, TokenSigningKey);
        Assert.True(ErrorSearchTokenCodec.TryReadSnapshotReference(
            snapshotToken,
            TokenSigningKey,
            out var decodedSnapshot,
            out var snapshotError));
        Assert.Null(snapshotError);
        Assert.Equal(snapshot.Snapshot, decodedSnapshot!.Snapshot);
        Assert.Equal(snapshot.Filter.Categories, decodedSnapshot.Filter.Categories);
        Assert.Equal(snapshot.Filter.ErrorCodes, decodedSnapshot.Filter.ErrorCodes);
        Assert.Equal(snapshot.Filter.ActivityStates, decodedSnapshot.Filter.ActivityStates);
        Assert.Equal(snapshot.Filter.SeriesId, decodedSnapshot.Filter.SeriesId);
        Assert.Equal(snapshot.Filter.DemandId, decodedSnapshot.Filter.DemandId);
        Assert.Equal(snapshot.Filter.SublotContains, decodedSnapshot.Filter.SublotContains);
        Assert.Equal(snapshot.Window, decodedSnapshot.Window);
        Assert.Equal(snapshot.Order, decodedSnapshot.Order);

        var cursor = ErrorSearchTokenCodec.CreateCursor(
            snapshot,
            pageSize: 25,
            targetPageNumber: 2,
            afterActivityRank: 0,
            afterLatestMatchedEvidenceAt: identity.ErrorSearchAsOf.AddMinutes(-5),
            afterSeriesId: "series-ticket11-anchor",
            TokenSigningKey);
        Assert.True(ErrorSearchTokenCodec.TryReadCursor(
            cursor,
            snapshot,
            expectedPageSize: 25,
            TokenSigningKey,
            out var decodedCursor,
            out var cursorError));
        Assert.Null(cursorError);
        Assert.Equal(2, decodedCursor!.TargetPageNumber);

        Assert.False(ErrorSearchTokenCodec.TryReadCursor(
            cursor,
            snapshot with { Filter = filter with { SeriesId = "ANOTHER-SERIES" } },
            expectedPageSize: 25,
            TokenSigningKey,
            out _,
            out var filterMismatch));
        Assert.Equal(ErrorSearchErrorCodes.CursorMismatch, filterMismatch!.Code);
        Assert.False(ErrorSearchTokenCodec.TryReadCursor(
            cursor,
            snapshot,
            expectedPageSize: 26,
            TokenSigningKey,
            out _,
            out var pageSizeMismatch));
        Assert.Equal(ErrorSearchErrorCodes.CursorMismatch, pageSizeMismatch!.Code);
        Assert.False(ErrorSearchTokenCodec.TryReadCursor(
            cursor,
            snapshot with
            {
                Snapshot = identity with { ProjectionSequence = identity.ProjectionSequence + 1 },
            },
            expectedPageSize: 25,
            TokenSigningKey,
            out _,
            out var highWaterMismatch));
        Assert.Equal(ErrorSearchErrorCodes.CursorMismatch, highWaterMismatch!.Code);
        Assert.False(ErrorSearchTokenCodec.TryReadCursor(
            cursor,
            snapshot with
            {
                Snapshot = identity with { HistoryEpoch = HistoryEpoch.CreateNew() },
            },
            expectedPageSize: 25,
            TokenSigningKey,
            out _,
            out var historyEpochMismatch));
        Assert.Equal(ErrorSearchErrorCodes.CursorMismatch, historyEpochMismatch!.Code);

        var previousContractSnapshot = snapshot with
        {
            Snapshot = identity with { ContractVersion = "2026.08.new-mes-ingest.previous" },
        };
        var previousContractSnapshotToken = ErrorSearchTokenCodec.CreateSnapshotReference(
            previousContractSnapshot,
            TokenSigningKey);
        Assert.False(ErrorSearchTokenCodec.TryReadSnapshotReference(
            previousContractSnapshotToken,
            TokenSigningKey,
            out _,
            out var contractSnapshotMismatch));
        Assert.Equal(ErrorSearchErrorCodes.SnapshotMismatch, contractSnapshotMismatch!.Code);
        var previousContractCursor = ErrorSearchTokenCodec.CreateCursor(
            previousContractSnapshot,
            pageSize: 25,
            targetPageNumber: 2,
            afterActivityRank: 0,
            afterLatestMatchedEvidenceAt: identity.ErrorSearchAsOf.AddMinutes(-5),
            afterSeriesId: "series-ticket11-anchor",
            TokenSigningKey);
        Assert.False(ErrorSearchTokenCodec.TryReadCursor(
            previousContractCursor,
            previousContractSnapshot,
            expectedPageSize: 25,
            TokenSigningKey,
            out _,
            out var contractCursorMismatch));
        Assert.Equal(ErrorSearchErrorCodes.CursorMismatch, contractCursorMismatch!.Code);

        var tampered = SignedTokenTampering.TamperSignature(cursor);
        Assert.False(ErrorSearchTokenCodec.TryReadCursor(
            tampered,
            snapshot,
            expectedPageSize: 25,
            TokenSigningKey,
            out _,
            out var tamperedError));
        Assert.Equal(ErrorSearchErrorCodes.InvalidCursor, tamperedError!.Code);

        var signatureSeparator = cursor.IndexOf('.');
        var signature = cursor[(signatureSeparator + 1)..];
        const string base64UrlAlphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var lastSignatureValue = base64UrlAlphabet.IndexOf(signature[^1]);
        var nonCanonicalLastValue = lastSignatureValue ^ 1;
        Assert.Equal(lastSignatureValue >> 2, nonCanonicalLastValue >> 2);
        var nonCanonicalSignature =
            signature[..^1] + base64UrlAlphabet[nonCanonicalLastValue];
        var nonCanonical = cursor[..(signatureSeparator + 1)] + nonCanonicalSignature;
        Assert.False(ErrorSearchTokenCodec.TryReadCursor(
            nonCanonical,
            snapshot,
            expectedPageSize: 25,
            TokenSigningKey,
            out _,
            out var nonCanonicalError));
        Assert.Equal(ErrorSearchErrorCodes.InvalidCursor, nonCanonicalError!.Code);

        var auditReference = ReadabilityAuditTokenCodec.CreateSnapshotReference(
            new ReadabilityAuditSnapshotIdentity(
                HistoryEpoch.CreateNew(),
                identity.ProjectionCommitId,
                identity.ProjectionSequence,
                identity.ProjectionCommittedAt,
                identity.PollTraceId,
                CatalogRevision: 0),
            TokenSigningKey);
        Assert.False(ErrorSearchTokenCodec.TryReadSnapshotReference(
            auditReference,
            TokenSigningKey,
            out _,
            out var crossPurposeError));
        Assert.Equal(ErrorSearchErrorCodes.InvalidSnapshotReference, crossPurposeError!.Code);
    }

    [Fact]
    public void Error_search_filter_vocabulary_covers_every_published_code_and_rejects_invalid_values()
    {
        Assert.Equal(
            [
                "REQUIRED_MES_FIELD_MISSING",
                "INVALID_MES_FIELD_FORMAT",
                "DUPLICATE_TRANSPORT_DEMAND_KEY",
                "SUBLOT_MULTIPLE_WORK_TYPES",
                "LONG_GONE_BUT_VISIBLE",
            ],
            SeriesErrorCatalog.Definitions.Select(definition => definition.Code));
        Assert.Equal(
            ["DATA_COMPLETENESS", "DATA_FORMAT", "LIFECYCLE_CONFLICT", "OBSERVATION_CONFLICT"],
            SeriesErrorCatalog.Definitions.Select(definition => definition.Category)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

        var sameCategoryOr = new ErrorSearchQuery(
            new ErrorSearchFilter
            {
                Categories = [" observation_conflict "],
                ErrorCodes =
                [
                    " duplicate_transport_demand_key ",
                    "sublot_multiple_work_types",
                ],
            },
            ErrorSearchWindowSelection.AllHistory).NormalizeAndValidate();
        Assert.Equal(["OBSERVATION_CONFLICT"], sameCategoryOr.Filter.Categories);
        Assert.Equal(
            ["DUPLICATE_TRANSPORT_DEMAND_KEY", "SUBLOT_MULTIPLE_WORK_TYPES"],
            sameCategoryOr.Filter.ErrorCodes);

        Assert.Throws<ErrorSearchException>(() => new ErrorSearchQuery(
            new ErrorSearchFilter { ActivityStates = ["CURRENT"] },
            ErrorSearchWindowSelection.Last7Days).NormalizeAndValidate());
        Assert.Throws<ErrorSearchException>(() => new ErrorSearchQuery(
            new ErrorSearchFilter { ErrorCodes = ["UNPUBLISHED_ERROR"] },
            ErrorSearchWindowSelection.Last7Days).NormalizeAndValidate());
        Assert.Throws<ErrorSearchException>(() => new ErrorSearchQuery(
            new ErrorSearchFilter { SeriesId = new string('S', 65) },
            ErrorSearchWindowSelection.Last7Days).NormalizeAndValidate());
        Assert.Throws<ErrorSearchException>(() => new ErrorSearchQuery(
            new ErrorSearchFilter { DemandId = new string('D', 65) },
            ErrorSearchWindowSelection.Last7Days).NormalizeAndValidate());
        Assert.Throws<ErrorSearchException>(() => new ErrorSearchQuery(
            new ErrorSearchFilter { SublotContains = new string('L', 257) },
            ErrorSearchWindowSelection.Last7Days).NormalizeAndValidate());
        Assert.Throws<ErrorSearchException>(() => new ErrorSearchQuery(
            new ErrorSearchFilter(),
            ErrorSearchWindowSelection.Last7Days,
            Cursor: "opaque-without-snapshot").NormalizeAndValidate());
    }

    [Ticket01SqlServerFact]
    public async Task Rolling_and_custom_windows_use_utc_half_open_period_overlap()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var asOf = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(asOf);
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        var beforeFrom = asOf.AddDays(-8);
        var atFrom = asOf.AddDays(-7);
        var afterFrom = asOf.AddDays(-6);
        const string endedAtFrom = "SL-T11-ENDS-AT-FROM";
        const string crossesFrom = "SL-T11-CROSSES-FROM";
        const string startsAtFrom = "SL-T11-STARTS-AT-FROM";
        const string startsAtTo = "SL-T11-STARTS-AT-TO";

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-window-before-from",
            beforeFrom,
            MissingEqp(endedAtFrom),
            MissingEqp(crossesFrom)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-window-at-from",
            atFrom,
            Valid(endedAtFrom),
            MissingEqp(crossesFrom),
            MissingEqp(startsAtFrom)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-window-after-from",
            afterFrom,
            Valid(endedAtFrom),
            MissingEqp(crossesFrom),
            Valid(startsAtFrom)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-window-at-to",
            asOf,
            Valid(endedAtFrom),
            MissingEqp(crossesFrom),
            Valid(startsAtFrom),
            MissingEqp(startsAtTo)));

        var defaultWindow = await GetJsonAsync(client, "/api/v2/error-search?pageSize=20");
        Assert.Equal(asOf, defaultWindow.GetProperty("snapshot").GetProperty("errorSearchAsOf").GetDateTimeOffset());
        Assert.Equal(ErrorSearchWindowKinds.Last7Days, defaultWindow.GetProperty("window").GetProperty("kind").GetString());
        Assert.Equal(atFrom, defaultWindow.GetProperty("window").GetProperty("fromUtc").GetDateTimeOffset());
        Assert.Equal(asOf, defaultWindow.GetProperty("window").GetProperty("toUtc").GetDateTimeOffset());
        Assert.Equal(2L, defaultWindow.GetProperty("totalSeriesCount").GetInt64());
        var defaultItems = defaultWindow.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(
            [crossesFrom, startsAtFrom],
            defaultItems.Select(ReadSublot).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(defaultItems, item => ReadSublot(item) == endedAtFrom);
        Assert.DoesNotContain(defaultItems, item => ReadSublot(item) == startsAtTo);

        var crossing = defaultItems.Single(item => ReadSublot(item) == crossesFrom);
        Assert.Equal("ACTIVE", crossing.GetProperty("activityState").GetString());
        Assert.Equal(beforeFrom, crossing.GetProperty("latestMatchedEvidenceAt").GetDateTimeOffset());
        var ended = defaultItems.Single(item => ReadSublot(item) == startsAtFrom);
        Assert.Equal("ENDED", ended.GetProperty("activityState").GetString());
        Assert.Equal(afterFrom, ended.GetProperty("latestMatchedEvidenceAt").GetDateTimeOffset());

        var last24 = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=LAST_24_HOURS&pageSize=20");
        Assert.Equal([crossesFrom], last24.GetProperty("items").EnumerateArray().Select(ReadSublot));

        var last30 = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=LAST_30_DAYS&pageSize=20");
        Assert.Equal(3L, last30.GetProperty("totalSeriesCount").GetInt64());
        Assert.DoesNotContain(last30.GetProperty("items").EnumerateArray(), item => ReadSublot(item) == startsAtTo);

        var allHistory = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&pageSize=20");
        Assert.Equal(3L, allHistory.GetProperty("totalSeriesCount").GetInt64());

        var custom = await GetJsonAsync(
            client,
            "/api/v2/error-search?from=" + Escape(atFrom) + "&to=" + Escape(asOf) + "&pageSize=20");
        Assert.Equal(ErrorSearchWindowKinds.Custom, custom.GetProperty("window").GetProperty("kind").GetString());
        Assert.Equal(2L, custom.GetProperty("totalSeriesCount").GetInt64());

        var endedOnly = await GetJsonAsync(
            client,
            "/api/v2/error-search?state=ENDED&pageSize=20");
        Assert.Equal([startsAtFrom], endedOnly.GetProperty("items").EnumerateArray().Select(ReadSublot));
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Filters_facets_series_dedup_and_all_four_categories_share_one_snapshot()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var time = new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(time.AddHours(13));
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        const string twoCategories = "SL-T11-TWO-CATEGORIES";
        const string duplicate = "SL-T11-DUPLICATE";
        const string multi = "SL-T11-MULTI-WORK";
        const string lifecycle = "SL-T11-LONG-GONE";
        var badTwoCategories = Observation(twoCategories, "N03-08", null, "QFN-TWO");
        var goodTwoCategories = Valid(twoCategories);
        var duplicateA = Observation(duplicate, "N3-3", "WB-31", "QFN-A");
        var duplicateB = Observation(duplicate, "N3-4", "WB-32", "QFN-B");
        var multiWire = Observation(multi, "N3-5", "WB-41", "QFN-WIRE");
        var multiDie = Observation(multi, "N3-6", "WB-42", "QFN-DIE", "DIE_ATTACH");
        var lifecycleRow = Valid(lifecycle);

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-facets-bootstrap",
            time,
            badTwoCategories,
            duplicateA,
            duplicateB,
            multiWire,
            multiDie,
            lifecycleRow));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-facets-authority-progress",
            time.AddMinutes(1),
            goodTwoCategories,
            duplicateA,
            duplicateB,
            multiWire,
            multiDie,
            lifecycleRow));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-facets-gone",
            time.AddMinutes(2),
            goodTwoCategories,
            duplicateA,
            duplicateB,
            multiWire,
            multiDie));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-facets-archive",
            time.AddHours(12).AddMinutes(2),
            goodTwoCategories,
            duplicateA,
            duplicateB,
            multiWire,
            multiDie));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-facets-long-gone-visible",
            time.AddHours(12).AddMinutes(3),
            goodTwoCategories,
            duplicateA,
            duplicateB,
            multiWire,
            multiDie,
            lifecycleRow));

        var all = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&pageSize=20");
        Assert.Equal(5L, all.GetProperty("totalSeriesCount").GetInt64());
        Assert.Equal(5, all.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("seriesId").GetString())
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var categories = ReadFacetCounts(all, "categories", "category");
        Assert.Equal(1L, categories["DATA_COMPLETENESS"]);
        Assert.Equal(1L, categories["DATA_FORMAT"]);
        Assert.Equal(3L, categories["OBSERVATION_CONFLICT"]);
        Assert.Equal(1L, categories["LIFECYCLE_CONFLICT"]);
        Assert.True(categories.Values.Sum() > all.GetProperty("totalSeriesCount").GetInt64());

        var states = ReadFacetCounts(all, "activityStates", "state");
        Assert.Equal(4L, states["ACTIVE"]);
        Assert.Equal(1L, states["ENDED"]);

        var ended = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&state=ENDED&pageSize=20");
        Assert.Equal(1L, ended.GetProperty("totalSeriesCount").GetInt64());
        var statesIgnoringOwnFilter = ReadFacetCounts(ended, "activityStates", "state");
        Assert.Equal(4L, statesIgnoringOwnFilter["ACTIVE"]);
        Assert.Equal(1L, statesIgnoringOwnFilter["ENDED"]);

        var completeness = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&category=DATA_COMPLETENESS&pageSize=20");
        Assert.Equal([twoCategories], completeness.GetProperty("items").EnumerateArray().Select(ReadSublot));
        var categoriesIgnoringOwnFilter = ReadFacetCounts(completeness, "categories", "category");
        Assert.Equal(1L, categoriesIgnoringOwnFilter["DATA_FORMAT"]);
        Assert.Equal(3L, categoriesIgnoringOwnFilter["OBSERVATION_CONFLICT"]);
        Assert.Equal(1L, categoriesIgnoringOwnFilter["LIFECYCLE_CONFLICT"]);

        var caseInsensitiveCode = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&code=%20invalid_mes_field_format%20&pageSize=20");
        Assert.Equal([twoCategories], caseInsensitiveCode.GetProperty("items").EnumerateArray().Select(ReadSublot));

        var sublotContains = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&sublot=%20t11-multi-work%20&pageSize=20");
        Assert.Equal(2L, sublotContains.GetProperty("totalSeriesCount").GetInt64());
        Assert.All(sublotContains.GetProperty("items").EnumerateArray(), item =>
            Assert.Equal(multi, ReadSublot(item)));

        var lifecycleFiltered = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&category=LIFECYCLE_CONFLICT&state=ACTIVE&pageSize=20");
        var lifecycleItem = Assert.Single(lifecycleFiltered.GetProperty("items").EnumerateArray());
        Assert.Equal(lifecycle, ReadSublot(lifecycleItem));
        Assert.Equal("LONG_GONE_BUT_VISIBLE", Assert.Single(
            lifecycleItem.GetProperty("matchedErrors").EnumerateArray()).GetProperty("code").GetString());
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Demand_id_filter_keeps_only_the_matching_generation_period_and_evidence()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var time = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(time.AddMinutes(10));
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        const string sublot = "SL-T11-DEMAND-GENERATIONS";

        var first = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-generation-one",
            time,
            MissingEqp(sublot)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-generation-authority",
            time.AddMinutes(1),
            MissingEqp(sublot)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-generation-gone",
            time.AddMinutes(2)));
        var second = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-generation-two",
            time.AddMinutes(3),
            MissingEqp(sublot)));
        var firstDemandId = Assert.Single(first.DemandIds);
        var secondDemandId = Assert.Single(second.DemandIds);

        var seriesWide = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&code=REQUIRED_MES_FIELD_MISSING&sublot=t11-demand-generations&pageSize=20");
        var seriesItem = Assert.Single(seriesWide.GetProperty("items").EnumerateArray());
        Assert.Equal(2, seriesItem.GetProperty("matchedPeriodCount").GetInt32());
        Assert.Equal(2, seriesItem.GetProperty("matchedDemandGenerationCount").GetInt32());
        Assert.Equal("ACTIVE", seriesItem.GetProperty("activityState").GetString());

        var firstGeneration = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&code=required_mes_field_missing&demandId=%20"
            + Uri.EscapeDataString(firstDemandId.ToUpperInvariant()) + "%20&pageSize=20");
        var firstItem = Assert.Single(firstGeneration.GetProperty("items").EnumerateArray());
        Assert.Equal(seriesItem.GetProperty("seriesId").GetString(), firstItem.GetProperty("seriesId").GetString());
        Assert.Equal("ENDED", firstItem.GetProperty("activityState").GetString());
        Assert.Equal(1, firstItem.GetProperty("matchedPeriodCount").GetInt32());
        Assert.Equal(1, firstItem.GetProperty("matchedDemandGenerationCount").GetInt32());

        var secondGeneration = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&demandId="
            + Uri.EscapeDataString(secondDemandId.ToUpperInvariant()) + "&pageSize=20");
        var secondItem = Assert.Single(secondGeneration.GetProperty("items").EnumerateArray());
        Assert.Equal(seriesItem.GetProperty("seriesId").GetString(), secondItem.GetProperty("seriesId").GetString());
        Assert.Equal("ACTIVE", secondItem.GetProperty("activityState").GetString());
        Assert.Equal(1, secondItem.GetProperty("matchedPeriodCount").GetInt32());
        Assert.Equal(1, secondItem.GetProperty("matchedDemandGenerationCount").GetInt32());

        var seriesId = seriesItem.GetProperty("seriesId").GetString()!;
        var bySeries = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&seriesId=%20"
            + Uri.EscapeDataString(seriesId.ToUpperInvariant()) + "%20&pageSize=20");
        Assert.Single(bySeries.GetProperty("items").EnumerateArray());
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Frozen_high_water_keeps_pages_state_facets_and_order_stable_after_backdated_commit()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var eventTime = new DateTimeOffset(2026, 8, 13, 18, 0, 0, TimeSpan.Zero);
        var asOf = eventTime.AddHours(2);
        var clock = new AdjustableTimeProvider(asOf);
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-freeze-a",
            eventTime,
            MissingEqp("SL-T11-FREEZE-A"),
            MissingEqp("SL-T11-FREEZE-B"),
            MissingEqp("SL-T11-FREEZE-C")));

        var page1 = await GetJsonAsync(client, "/api/v2/error-search?pageSize=1");
        var frozenReference = page1.GetProperty("snapshotReference").GetString()!;
        var cursor = page1.GetProperty("nextCursor").GetString()!;
        var frozenSequence = page1.GetProperty("snapshot").GetProperty("projectionSequence").GetInt64();
        var historyEpoch = await ReadHistoryEpochAsync(database.ConnectionString);
        Assert.Equal(
            historyEpoch.Value.ToString("D"),
            page1.GetProperty("snapshot").GetProperty("historyEpoch").GetString());
        Assert.Equal(asOf, page1.GetProperty("snapshot").GetProperty("errorSearchAsOf").GetDateTimeOffset());
        Assert.Equal(3L, page1.GetProperty("totalSeriesCount").GetInt64());
        Assert.Equal(3, page1.GetProperty("totalPages").GetInt32());
        Assert.True(page1.GetProperty("hasMore").GetBoolean());

        var frozenFull = await GetJsonAsync(
            client,
            "/api/v2/error-search?pageSize=200&snapshot=" + Uri.EscapeDataString(frozenReference));
        var frozenIds = frozenFull.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("seriesId").GetString()!).ToArray();
        Assert.Equal(frozenIds.Order(StringComparer.Ordinal), frozenIds);
        Assert.All(frozenFull.GetProperty("items").EnumerateArray(), item =>
            Assert.Equal("ACTIVE", item.GetProperty("activityState").GetString()));

        // This commit occurs after the snapshot high-water but carries a domain
        // event time before ErrorSearchAsOf. A time-only fence would leak it.
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-freeze-backdated-b",
            eventTime.AddMinutes(1),
            Valid("SL-T11-FREEZE-A"),
            Observation("SL-T11-FREEZE-B", "N3-3", " ", "QFN-B"),
            MissingEqp("SL-T11-FREEZE-C"),
            MissingEqp("SL-T11-FREEZE-D")));

        var page2 = await GetJsonAsync(
            client,
            "/api/v2/error-search?pageSize=1&snapshot=" + Uri.EscapeDataString(frozenReference)
            + "&cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal(2, page2.GetProperty("pageNumber").GetInt32());
        Assert.Equal(3L, page2.GetProperty("totalSeriesCount").GetInt64());
        Assert.Equal(frozenSequence, page2.GetProperty("snapshot").GetProperty("projectionSequence").GetInt64());
        Assert.Equal(frozenIds[1], Assert.Single(page2.GetProperty("items").EnumerateArray())
            .GetProperty("seriesId").GetString());

        var frozenAgain = await GetJsonAsync(
            client,
            "/api/v2/error-search?pageSize=200&snapshot=" + Uri.EscapeDataString(frozenReference));
        Assert.Equal(frozenFull.GetRawText(), frozenAgain.GetRawText());

        clock.SetUtcNow(asOf.AddMinutes(1));
        var refreshed = await GetJsonAsync(client, "/api/v2/error-search?pageSize=200");
        Assert.True(refreshed.GetProperty("snapshot").GetProperty("projectionSequence").GetInt64() > frozenSequence);
        Assert.Equal(asOf.AddMinutes(1), refreshed.GetProperty("snapshot").GetProperty("errorSearchAsOf").GetDateTimeOffset());
        Assert.Equal(4L, refreshed.GetProperty("totalSeriesCount").GetInt64());
        Assert.Contains(refreshed.GetProperty("items").EnumerateArray(), item => ReadSublot(item) == "SL-T11-FREEZE-D");
        Assert.Equal("ENDED", refreshed.GetProperty("items").EnumerateArray()
            .Single(item => ReadSublot(item) == "SL-T11-FREEZE-A")
            .GetProperty("activityState").GetString());
        AssertActiveFirstAndStable(refreshed.GetProperty("items").EnumerateArray().ToArray());

        using (var mismatch = await client.GetAsync(
            "/api/v2/error-search?pageSize=1&state=ENDED&snapshot=" + Uri.EscapeDataString(frozenReference)
            + "&cursor=" + Uri.EscapeDataString(cursor)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
            AssertErrorCode(mismatch, ErrorSearchErrorCodes.CursorMismatch);
        }

        var tampered = SignedTokenTampering.TamperSignature(frozenReference);
        using (var rejected = await client.GetAsync(
            "/api/v2/error-search?snapshot=" + Uri.EscapeDataString(tampered)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            AssertErrorCode(rejected, ErrorSearchErrorCodes.InvalidSnapshotReference);
        }
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Http_failures_invalid_queries_and_successful_empty_results_remain_distinct()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var asOf = new DateTimeOffset(2026, 8, 14, 2, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(asOf);
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        using (var unavailable = await client.GetAsync("/api/v2/error-search"))
        {
            Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
            AssertErrorCode(unavailable, ErrorSearchErrorCodes.ProjectionNotAvailable);
        }

        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket11-empty-success",
            asOf.AddMinutes(-1),
            Valid("SL-T11-NO-ERROR")));
        var empty = await GetJsonAsync(client, "/api/v2/error-search");
        Assert.Equal(0L, empty.GetProperty("totalSeriesCount").GetInt64());
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        Assert.False(empty.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextCursor").ValueKind);
        Assert.False(empty.TryGetProperty("healthy", out _));
        Assert.False(empty.TryGetProperty("message", out _));

        var invalidUris = new[]
        {
            "/api/v2/error-search?category=DATA_COMPLETENESS&code=INVALID_MES_FIELD_FORMAT",
            "/api/v2/error-search?window=LAST_7_DAYS&from=" + Escape(asOf.AddHours(-1)),
            "/api/v2/error-search?from=" + Escape(asOf) + "&to=" + Escape(asOf),
            "/api/v2/error-search?to=" + Escape(asOf.AddTicks(1)),
            "/api/v2/error-search?pageSize=201",
            "/api/v2/error-search?area=N3-3",
            "/api/v2/error-search?sortBy=latestMatchedEvidenceAt",
        };
        foreach (var uri in invalidUris)
        {
            using var invalid = await client.GetAsync(uri);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            AssertErrorCode(invalid, ErrorSearchErrorCodes.InvalidQuery);
        }

        var signingKey = await ReadSnapshotSigningKeyAsync(database.ConnectionString);
        var historyEpoch = await ReadHistoryEpochAsync(database.ConnectionString);
        var retained = empty.GetProperty("snapshot");
        var missingSnapshot = new ErrorSearchSnapshotReference(
            new ErrorSearchSnapshotIdentity(
                historyEpoch,
                retained.GetProperty("errorSearchAsOf").GetDateTimeOffset(),
                $"missing-{Guid.NewGuid():N}",
                retained.GetProperty("projectionSequence").GetInt64() + 1000,
                retained.GetProperty("projectionCommittedAt").GetDateTimeOffset(),
                "missing-poll-ticket11"),
            new ErrorSearchFilter(),
            ErrorSearchWindowSelection.Last7Days.Resolve(
                retained.GetProperty("errorSearchAsOf").GetDateTimeOffset()),
            ErrorSearchOrder.Default);
        var missingReference = ErrorSearchTokenCodec.CreateSnapshotReference(
            missingSnapshot,
            signingKey);
        using (var mismatchedSnapshot = await client.GetAsync(
            "/api/v2/error-search?state=ACTIVE&snapshot=" + Uri.EscapeDataString(missingReference)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, mismatchedSnapshot.StatusCode);
            AssertErrorCode(mismatchedSnapshot, ErrorSearchErrorCodes.SnapshotMismatch);
        }

        var missingCursor = ErrorSearchTokenCodec.CreateCursor(
            missingSnapshot,
            pageSize: ErrorSearchQuery.DefaultPageSize,
            targetPageNumber: 2,
            afterActivityRank: 0,
            afterLatestMatchedEvidenceAt: missingSnapshot.Snapshot.ErrorSearchAsOf.AddMinutes(-1),
            afterSeriesId: "missing-series-anchor",
            signingKey);
        using (var mismatchedCursor = await client.GetAsync(
            "/api/v2/error-search?state=ACTIVE&snapshot=" + Uri.EscapeDataString(missingReference)
            + "&cursor=" + Uri.EscapeDataString(missingCursor)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, mismatchedCursor.StatusCode);
            AssertErrorCode(mismatchedCursor, ErrorSearchErrorCodes.CursorMismatch);
        }

        var crossEpochSnapshot = new ErrorSearchSnapshotReference(
            new ErrorSearchSnapshotIdentity(
                HistoryEpoch.CreateNew(),
                retained.GetProperty("errorSearchAsOf").GetDateTimeOffset(),
                retained.GetProperty("projectionCommitId").GetString()!,
                retained.GetProperty("projectionSequence").GetInt64(),
                retained.GetProperty("projectionCommittedAt").GetDateTimeOffset(),
                retained.GetProperty("pollTraceId").GetString()!),
            new ErrorSearchFilter(),
            ErrorSearchWindowSelection.Last7Days.Resolve(
                retained.GetProperty("errorSearchAsOf").GetDateTimeOffset()),
            ErrorSearchOrder.Default);
        var crossEpochReference = ErrorSearchTokenCodec.CreateSnapshotReference(
            crossEpochSnapshot,
            signingKey);
        using (var crossEpoch = await client.GetAsync(
            "/api/v2/error-search?snapshot=" + Uri.EscapeDataString(crossEpochReference)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, crossEpoch.StatusCode);
            AssertErrorCode(crossEpoch, ErrorSearchErrorCodes.SnapshotMismatch);
        }

        var crossEpochCursor = ErrorSearchTokenCodec.CreateCursor(
            crossEpochSnapshot,
            pageSize: ErrorSearchQuery.DefaultPageSize,
            targetPageNumber: 2,
            afterActivityRank: 0,
            afterLatestMatchedEvidenceAt: crossEpochSnapshot.Snapshot.ErrorSearchAsOf.AddMinutes(-1),
            afterSeriesId: "cross-epoch-series-anchor",
            signingKey);
        using (var crossEpoch = await client.GetAsync(
            "/api/v2/error-search?snapshot=" + Uri.EscapeDataString(crossEpochReference)
            + "&cursor=" + Uri.EscapeDataString(crossEpochCursor)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, crossEpoch.StatusCode);
            AssertErrorCode(crossEpoch, ErrorSearchErrorCodes.CursorMismatch);
        }

        using (var expired = await client.GetAsync(
            "/api/v2/error-search?snapshot=" + Uri.EscapeDataString(missingReference)))
        {
            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
            AssertErrorCode(expired, ErrorSearchErrorCodes.SnapshotNotFound);
        }

        var projection = factory.Services.GetRequiredService<IMesIngestProjection>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => projection.ListErrorSearchAsync(
            new ErrorSearchQuery(new ErrorSearchFilter(), ErrorSearchWindowSelection.Last7Days),
            cancellation.Token));
        AssertDatabaseEvidence(database);
    }

    private static MesTaskUnionObservation MissingEqp(string sublot) =>
        Observation(sublot, "N3-3", null, "QFN-MISSING");

    private static MesTaskUnionObservation Valid(string sublot) =>
        Observation(sublot, "N3-3", "WB-03", "QFN-VALID");

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
            "mes-task-union-ticket11-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static string ReadSublot(JsonElement item) =>
        item.GetProperty("transportDemandKey").GetProperty("sublot").GetString()!;

    private static Dictionary<string, long> ReadFacetCounts(
        JsonElement root,
        string collection,
        string key) =>
        root.GetProperty("facets").GetProperty(collection).EnumerateArray().ToDictionary(
            facet => facet.GetProperty(key).GetString()!,
            facet => facet.GetProperty("seriesCount").GetInt64(),
            StringComparer.Ordinal);

    private static void AssertActiveFirstAndStable(IReadOnlyList<JsonElement> items)
    {
        var ordering = items.Select(item => new
        {
            ActivityRank = item.GetProperty("activityState").GetString() == "ACTIVE" ? 0 : 1,
            EvidenceAt = item.GetProperty("latestMatchedEvidenceAt").GetDateTimeOffset(),
            SeriesId = item.GetProperty("seriesId").GetString()!,
        }).ToArray();
        Assert.Equal(
            ordering
                .OrderBy(item => item.ActivityRank)
                .ThenByDescending(item => item.EvidenceAt)
                .ThenBy(item => item.SeriesId, StringComparer.Ordinal),
            ordering);
    }

    private static string Escape(DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string uri)
    {
        using var response = await client.GetAsync(uri);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private static void AssertErrorCode(HttpResponseMessage response, string expected)
    {
        using var error = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        Assert.Equal(expected, error.RootElement.GetProperty("code").GetString());
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

    private static async Task<HistoryEpoch> ReadHistoryEpochAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT HistoryEpoch FROM mesingest.SchemaInfo WHERE Id = 1;";
        return HistoryEpoch.FromGuid((Guid)(await command.ExecuteScalarAsync())!);
    }

    private WebApplicationFactory<Program> CreateFactory(AdjustableTimeProvider clock) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });

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
        _output.WriteLine(
            $"SQL Server {database.ProductVersion}; compatibility {database.CompatibilityLevel}");
    }
}
