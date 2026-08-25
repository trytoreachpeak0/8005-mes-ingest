using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class ErrorSearchDetailTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string RawSecret = "ticket12-raw-secret";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public ErrorSearchDetailTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Ticket01SqlServerFact]
    public async Task Frozen_detail_uses_the_list_snapshot_and_excludes_later_or_unmatched_periods()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var observedAt = new DateTimeOffset(2026, 8, 15, 1, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(observedAt.AddHours(2));
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        const string sublot = "SL-T12-FROZEN-DETAIL";
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-frozen-open",
            observedAt,
            Observation(sublot, "N03-08", null, "QFN-OPEN")));

        var list = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&code=REQUIRED_MES_FIELD_MISSING"
            + "&sublot=t12-frozen-detail&pageSize=20");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        var seriesId = item.GetProperty("seriesId").GetString()!;
        var snapshotReference = list.GetProperty("snapshotReference").GetString()!;
        var frozenSequence = list.GetProperty("snapshot").GetProperty("projectionSequence").GetInt64();

        // A later commit changes the matching diagnostic and clears the unrelated
        // invalid-AREA condition. It must not bleed through the retained reference.
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-frozen-later",
            observedAt.AddMinutes(1),
            Observation(sublot, "N3-8", " ", "QFN-LATER")));

        var detail = await GetJsonAsync(
            client,
            DetailUri(seriesId, snapshotReference));
        Assert.Equal(snapshotReference, detail.GetProperty("snapshotReference").GetString());
        Assert.Equal(list.GetProperty("snapshot").GetRawText(), detail.GetProperty("snapshot").GetRawText());
        Assert.Equal(list.GetProperty("filter").GetRawText(), detail.GetProperty("filter").GetRawText());
        Assert.Equal(list.GetProperty("window").GetRawText(), detail.GetProperty("window").GetRawText());
        Assert.Equal(list.GetProperty("order").GetString(), detail.GetProperty("order").GetString());
        Assert.Equal(item.GetRawText(), detail.GetProperty("series").GetRawText());

        var period = Assert.Single(detail.GetProperty("periods").EnumerateArray());
        Assert.Equal("REQUIRED_MES_FIELD_MISSING", period.GetProperty("code").GetString());
        Assert.Equal("EQP", period.GetProperty("subjectKind").GetString());
        var frozenEvidence = Assert.Single(period.GetProperty("evidence").EnumerateArray());
        Assert.Equal("poll-ticket12-frozen-open", frozenEvidence.GetProperty("pollTraceId").GetString());
        Assert.DoesNotContain("INVALID_MES_FIELD_FORMAT", detail.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("poll-ticket12-frozen-later", detail.GetRawText(), StringComparison.Ordinal);
        AssertNoRawObservationPayload(detail);

        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(1));
        var refreshedList = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&code=REQUIRED_MES_FIELD_MISSING"
            + "&sublot=t12-frozen-detail&pageSize=20");
        Assert.True(
            refreshedList.GetProperty("snapshot").GetProperty("projectionSequence").GetInt64()
            > frozenSequence);
        var refreshedDetail = await GetJsonAsync(
            client,
            DetailUri(seriesId, refreshedList.GetProperty("snapshotReference").GetString()!));
        Assert.Contains(
            refreshedDetail.GetProperty("periods").EnumerateArray()
                .SelectMany(value => value.GetProperty("evidence").EnumerateArray()),
            value => value.GetProperty("pollTraceId").GetString() == "poll-ticket12-frozen-later");
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Raw_evidence_requires_explicit_authorization_and_returns_only_whitelisted_redacted_bounded_fields()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var observedAt = new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(observedAt.AddMinutes(10));
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        const string sublot = "SL-T12-RAW-POLICY";
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-raw-policy",
            observedAt,
            Observation(sublot, "N3-3", "WB-31", "password=secret-password-ticket12"),
            Observation(sublot, "N3-4", "WB-32", "Authorization: Bearer secret-ticket12")));

        var list = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY&code=DUPLICATE_TRANSPORT_DEMAND_KEY"
            + "&sublot=t12-raw-policy&pageSize=20");
        var seriesId = Assert.Single(list.GetProperty("items").EnumerateArray())
            .GetProperty("seriesId").GetString()!;
        var snapshotReference = list.GetProperty("snapshotReference").GetString()!;
        var detail = await GetJsonAsync(client, DetailUri(seriesId, snapshotReference));
        var evidence = detail.GetProperty("periods").EnumerateArray()
            .SelectMany(period => period.GetProperty("evidence").EnumerateArray())
            .Single(value => value.GetProperty("diagnosticValue").GetProperty("kind").GetString()
                == ErrorSearchDiagnosticValueKinds.RawObservationSet);
        var diagnostic = evidence.GetProperty("diagnosticValue");
        Assert.Equal(2, diagnostic.GetProperty("observationCount").GetInt32());
        Assert.NotEmpty(diagnostic.GetProperty("sha256Digest").GetString()!);
        Assert.Equal(JsonValueKind.Null, diagnostic.GetProperty("scalarValue").ValueKind);
        Assert.True(evidence.GetProperty("rawEvidenceAvailable").GetBoolean());
        AssertNoRawObservationPayload(list);
        AssertNoRawObservationPayload(detail);
        Assert.DoesNotContain("secret-ticket12", list.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-ticket12", detail.GetRawText(), StringComparison.Ordinal);

        var evidenceId = evidence.GetProperty("evidenceId").GetString()!;
        var rawUri = RawUri(seriesId, evidenceId, snapshotReference, "workType,package", 20);
        using (var denied = await client.GetAsync(rawUri))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            await AssertErrorCodeAsync(denied, ErrorSearchErrorCodes.RawAccessDenied);
        }

        using (var wrongCredentialRequest = new HttpRequestMessage(HttpMethod.Get, rawUri))
        {
            wrongCredentialRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", "wrong-ticket12-secret");
            using var wrongCredential = await client.SendAsync(wrongCredentialRequest);
            Assert.Equal(HttpStatusCode.Forbidden, wrongCredential.StatusCode);
            await AssertErrorCodeAsync(wrongCredential, ErrorSearchErrorCodes.RawAccessDenied);
        }

        using var allowed = await SendAuthorizedAsync(client, rawUri);
        var raw = await ReadSuccessJsonAsync(allowed);
        Assert.Equal(snapshotReference, raw.GetProperty("snapshotReference").GetString());
        Assert.Equal(seriesId, raw.GetProperty("seriesId").GetString());
        Assert.Equal(evidenceId, raw.GetProperty("evidenceId").GetString());
        Assert.Equal(["workType", "package"], raw.GetProperty("includedFields")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(2, raw.GetProperty("itemCount").GetInt32());
        var limits = raw.GetProperty("limits");
        Assert.Equal(20, limits.GetProperty("maxItems").GetInt32());
        Assert.Equal(2_048, limits.GetProperty("maxItemBytes").GetInt32());
        Assert.Equal(65_536, limits.GetProperty("maxTotalBytes").GetInt32());
        var payloadBytes = raw.GetProperty("payloadBytes").GetInt32();
        Assert.InRange(payloadBytes, 1, 65_536);
        Assert.Equal(Encoding.UTF8.GetByteCount(raw.GetRawText()), payloadBytes);
        var rawItems = raw.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, rawItems.Length);
        Assert.All(rawItems, rawItem => Assert.Equal(
            ["workType", "package"],
            rawItem.GetProperty("fields").EnumerateObject().Select(property => property.Name)));
        Assert.Contains(rawItems, rawItem =>
            rawItem.GetProperty("fields").GetProperty("package").GetString()!
                .Contains("[REDACTED]", StringComparison.Ordinal));
        Assert.DoesNotContain("secret-password-ticket12", raw.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-ticket12", raw.GetRawText(), StringComparison.Ordinal);
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Raw_evidence_from_an_expired_PollTrace_is_410_under_a_newer_retained_snapshot()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var observedAt = new DateTimeOffset(2026, 7, 1, 3, 0, 0, TimeSpan.Zero);
        var retainedAt = observedAt.AddDays(1);
        var clock = new AdjustableTimeProvider(retainedAt);
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        const string sublot = "SL-T13-EXPIRED-RAW-EVIDENCE";

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-expired-raw-open",
            observedAt,
            Observation(sublot, "N3-3", "WB-31", "QFN-A"),
            Observation(sublot, "N3-4", "WB-32", "QFN-B")));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket13-expired-raw-retained",
            retainedAt,
            Observation(sublot, "N3-3", "WB-31", "QFN-VALID")));

        var list = await GetJsonAsync(
            client,
            "/api/v2/error-search?window=ALL_HISTORY"
            + "&code=DUPLICATE_TRANSPORT_DEMAND_KEY&pageSize=20");
        var seriesId = Assert.Single(list.GetProperty("items").EnumerateArray())
            .GetProperty("seriesId").GetString()!;
        var snapshotReference = list.GetProperty("snapshotReference").GetString()!;
        var detail = await GetJsonAsync(client, DetailUri(seriesId, snapshotReference));
        var evidenceId = detail.GetProperty("periods").EnumerateArray()
            .SelectMany(period => period.GetProperty("evidence").EnumerateArray())
            .Single(value => value.GetProperty("pollTraceId").GetString()
                == "poll-ticket13-expired-raw-open")
            .GetProperty("evidenceId").GetString()!;

        clock.SetUtcNow(observedAt.AddDays(15));
        using var response = await SendAuthorizedAsync(
            client,
            RawUri(seriesId, evidenceId, snapshotReference, "workType,package", 20));
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal(
            PollEvidenceErrorCodes.MesIngestHistoryExpired,
            json.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            observedAt,
            json.RootElement.GetProperty("earliestAvailableHostUtc").GetDateTimeOffset());
    }

    [Ticket01SqlServerFact]
    public async Task Raw_evidence_rejections_are_stable_non_leaking_and_leave_the_snapshot_unchanged()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var observedAt = new DateTimeOffset(2026, 8, 15, 5, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(observedAt.AddMinutes(10));
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        const string sublot = "SL-T12-RAW-REJECTIONS";
        var boundaryValue = new string('\u754c', 512);
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-raw-rejections",
            observedAt,
            Enumerable.Range(0, 20)
                .Select(_ => Observation(sublot, boundaryValue, boundaryValue, boundaryValue) with
                {
                    Step = boundaryValue,
                })
                .ToArray()));

        const string listUri = "/api/v2/error-search?window=ALL_HISTORY"
            + "&code=DUPLICATE_TRANSPORT_DEMAND_KEY&sublot=t12-raw-rejections&pageSize=20";
        var baselineList = await GetJsonAsync(client, listUri);
        var seriesId = Assert.Single(baselineList.GetProperty("items").EnumerateArray())
            .GetProperty("seriesId").GetString()!;
        var snapshotReference = baselineList.GetProperty("snapshotReference").GetString()!;
        var detailUri = DetailUri(seriesId, snapshotReference);
        var baselineDetail = await GetJsonAsync(client, detailUri);
        var evidenceId = baselineDetail.GetProperty("periods").EnumerateArray()
            .SelectMany(period => period.GetProperty("evidence").EnumerateArray())
            .Single(value => value.GetProperty("diagnosticValue").GetProperty("kind").GetString()
                == ErrorSearchDiagnosticValueKinds.RawObservationSet)
            .GetProperty("evidenceId").GetString()!;

        var deniedUnknownUri = RawUri(
            "series-not-in-snapshot",
            "evidence-not-in-snapshot",
            snapshotReference,
            "package",
            20);
        using (var deniedUnknown = await client.GetAsync(deniedUnknownUri))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedUnknown.StatusCode);
            await AssertErrorCodeAsync(deniedUnknown, ErrorSearchErrorCodes.RawAccessDenied);
        }

        using (var hidden = await SendAuthorizedAsync(
            client,
            RawUri("series-not-in-snapshot", evidenceId, snapshotReference, "package", 20)))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            var body = await AssertErrorCodeAsync(hidden, ErrorSearchErrorCodes.ObjectNotInSnapshot);
            Assert.DoesNotContain(seriesId, body, StringComparison.Ordinal);
            Assert.DoesNotContain(evidenceId, body, StringComparison.Ordinal);
            Assert.DoesNotContain(boundaryValue, body, StringComparison.Ordinal);
        }

        using (var evidenceHidden = await SendAuthorizedAsync(
            client,
            RawUri(seriesId, "evidence-not-in-snapshot", snapshotReference, "package", 20)))
        {
            Assert.Equal(HttpStatusCode.NotFound, evidenceHidden.StatusCode);
            var body = await AssertErrorCodeAsync(
                evidenceHidden,
                ErrorSearchErrorCodes.ObjectNotInSnapshot);
            Assert.DoesNotContain(seriesId, body, StringComparison.Ordinal);
            Assert.DoesNotContain(evidenceId, body, StringComparison.Ordinal);
            Assert.DoesNotContain(boundaryValue, body, StringComparison.Ordinal);
        }

        using (var fieldRejected = await SendAuthorizedAsync(
            client,
            RawUri(seriesId, evidenceId, snapshotReference, "projectionCommitId", 20)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, fieldRejected.StatusCode);
            await AssertErrorCodeAsync(fieldRejected, ErrorSearchErrorCodes.RawFieldNotAllowed);
        }

        using (var limitRejected = await SendAuthorizedAsync(
            client,
            RawUri(seriesId, evidenceId, snapshotReference, "package", 21)))
        {
            // The frozen contract maps RAW_EVIDENCE_LIMIT_EXCEEDED to 413, and reserves
            // 400 for RAW_EVIDENCE_FIELD_NOT_ALLOWED. This assertion only started
            // executing once the packaged release gate stopped skipping the V2 suite.
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, limitRejected.StatusCode);
            await AssertErrorCodeAsync(limitRejected, ErrorSearchErrorCodes.RawLimitExceeded);
        }

        using (var payloadRejected = await SendAuthorizedAsync(
            client,
            RawUri(seriesId, evidenceId, snapshotReference, "area,eqp,step,package", 20)))
        {
            Assert.Equal((HttpStatusCode)413, payloadRejected.StatusCode);
            var body = await AssertErrorCodeAsync(payloadRejected, ErrorSearchErrorCodes.RawLimitExceeded);
            Assert.DoesNotContain(boundaryValue, body, StringComparison.Ordinal);
        }

        var detailAfterRejections = await GetJsonAsync(client, detailUri);
        var listAfterRejections = await GetJsonAsync(client, listUri);
        Assert.Equal(baselineDetail.GetRawText(), detailAfterRejections.GetRawText());
        Assert.Equal(baselineList.GetRawText(), listAfterRejections.GetRawText());
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Detail_marks_window_overlap_preserves_clear_and_gone_reasons_and_deduplicates_unchanged_evidence()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var startedAt = new DateTimeOffset(2026, 8, 15, 7, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(startedAt.AddMinutes(3));
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        const string clearedSublot = "SL-T12-WINDOW-CLEARED";
        const string goneSublot = "SL-T12-WINDOW-GONE";
        const string activeSublot = "SL-T12-WINDOW-ACTIVE";
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-window-open",
            startedAt,
            MissingEqp(clearedSublot),
            MissingEqp(goneSublot),
            MissingEqp(activeSublot)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-window-unchanged",
            startedAt.AddMinutes(1),
            MissingEqp(clearedSublot),
            MissingEqp(goneSublot),
            MissingEqp(activeSublot)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-window-close",
            startedAt.AddMinutes(2),
            Valid(clearedSublot),
            MissingEqp(activeSublot)));

        var from = Uri.EscapeDataString(startedAt.AddSeconds(30).ToString("O"));
        var to = Uri.EscapeDataString(startedAt.AddSeconds(90).ToString("O"));
        async Task<JsonElement> ReadDetailAsync(string sublot)
        {
            var list = await GetJsonAsync(
                client,
                $"/api/v2/error-search?from={from}&to={to}"
                + "&code=REQUIRED_MES_FIELD_MISSING&sublot="
                + Uri.EscapeDataString(sublot) + "&pageSize=20");
            var item = Assert.Single(list.GetProperty("items").EnumerateArray());
            return await GetJsonAsync(client, DetailUri(
                item.GetProperty("seriesId").GetString()!,
                list.GetProperty("snapshotReference").GetString()!));
        }

        var cleared = Assert.Single((await ReadDetailAsync(clearedSublot))
            .GetProperty("periods").EnumerateArray());
        var gone = Assert.Single((await ReadDetailAsync(goneSublot))
            .GetProperty("periods").EnumerateArray());
        var active = Assert.Single((await ReadDetailAsync(activeSublot))
            .GetProperty("periods").EnumerateArray());

        Assert.True(cleared.GetProperty("startsBeforeWindow").GetBoolean());
        Assert.True(cleared.GetProperty("endsAfterWindow").GetBoolean());
        Assert.Equal(startedAt, cleared.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(startedAt.AddMinutes(2), cleared.GetProperty("endedAt").GetDateTimeOffset());
        Assert.Equal("CONDITION_CLEARED", cleared.GetProperty("endReason").GetString());
        Assert.False(cleared.GetProperty("activeAtAsOf").GetBoolean());
        Assert.Equal(
            ["BOOTSTRAPPED_CURRENT_CONDITION", "CONDITION_CLEARED"],
            cleared.GetProperty("evidence").EnumerateArray()
                .Select(value => value.GetProperty("evidenceKind").GetString()));
        Assert.DoesNotContain(
            cleared.GetProperty("evidence").EnumerateArray(),
            value => value.GetProperty("pollTraceId").GetString() == "poll-ticket12-window-unchanged");

        Assert.True(gone.GetProperty("startsBeforeWindow").GetBoolean());
        Assert.True(gone.GetProperty("endsAfterWindow").GetBoolean());
        Assert.Equal(startedAt.AddMinutes(2), gone.GetProperty("endedAt").GetDateTimeOffset());
        Assert.Equal("DEMAND_GONE", gone.GetProperty("endReason").GetString());
        Assert.Equal(
            ["BOOTSTRAPPED_CURRENT_CONDITION", "DEMAND_GONE"],
            gone.GetProperty("evidence").EnumerateArray()
                .Select(value => value.GetProperty("evidenceKind").GetString()));

        Assert.True(active.GetProperty("activeAtAsOf").GetBoolean());
        Assert.True(active.GetProperty("endsAfterWindow").GetBoolean());
        Assert.Equal(JsonValueKind.Null, active.GetProperty("endedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, active.GetProperty("endReason").ValueKind);
        Assert.Single(active.GetProperty("evidence").EnumerateArray());

        var exactTo = Uri.EscapeDataString(startedAt.AddMinutes(2).ToString("O"));
        var exactList = await GetJsonAsync(
            client,
            $"/api/v2/error-search?from={from}&to={exactTo}"
            + "&code=REQUIRED_MES_FIELD_MISSING&sublot="
            + Uri.EscapeDataString(clearedSublot) + "&pageSize=20");
        var exactItem = Assert.Single(exactList.GetProperty("items").EnumerateArray());
        var exactPeriod = Assert.Single((await GetJsonAsync(client, DetailUri(
                exactItem.GetProperty("seriesId").GetString()!,
                exactList.GetProperty("snapshotReference").GetString()!)))
            .GetProperty("periods").EnumerateArray());
        Assert.Equal(startedAt.AddMinutes(2), exactPeriod.GetProperty("endedAt").GetDateTimeOffset());
        Assert.False(exactPeriod.GetProperty("endsAfterWindow").GetBoolean());
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Detail_separates_demand_generations_and_reconciles_series_generation_evidence_with_list_counts()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var time = new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(time.AddHours(13));
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        const string demandSublot = "SL-T12-DEMAND-GENERATIONS";
        const string seriesSublot = "SL-T12-SERIES-GENERATIONS";
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-generations-one",
            time,
            MissingEqp(demandSublot),
            Valid(seriesSublot)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-generations-authority",
            time.AddMinutes(1),
            MissingEqp(demandSublot),
            Valid(seriesSublot)));
        await ingestor.IngestAsync(SuccessRound("poll-ticket12-generations-gone-one", time.AddMinutes(2)));
        await ingestor.IngestAsync(SuccessRound("poll-ticket12-generations-archive", time.AddHours(12).AddMinutes(2)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-generations-two",
            time.AddHours(12).AddMinutes(3),
            MissingEqp(demandSublot),
            Valid(seriesSublot)));
        await ingestor.IngestAsync(SuccessRound("poll-ticket12-generations-gone-two", time.AddHours(12).AddMinutes(4)));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-generations-three",
            time.AddHours(12).AddMinutes(5),
            MissingEqp(demandSublot),
            Valid(seriesSublot)));

        async Task<(JsonElement Item, JsonElement Detail)> ReadAsync(string code, string sublot)
        {
            var list = await GetJsonAsync(
                client,
                "/api/v2/error-search?window=ALL_HISTORY&code=" + code
                + "&sublot=" + Uri.EscapeDataString(sublot) + "&pageSize=20");
            var item = Assert.Single(list.GetProperty("items").EnumerateArray());
            var detail = await GetJsonAsync(client, DetailUri(
                item.GetProperty("seriesId").GetString()!,
                list.GetProperty("snapshotReference").GetString()!));
            return (item, detail);
        }

        var demand = await ReadAsync("REQUIRED_MES_FIELD_MISSING", demandSublot);
        var demandPeriods = demand.Detail.GetProperty("periods").EnumerateArray().ToArray();
        Assert.Equal(demand.Item.GetProperty("matchedPeriodCount").GetInt32(), demandPeriods.Length);
        Assert.Equal(3, demandPeriods.Length);
        var demandIds = demandPeriods.SelectMany(period => period.GetProperty("evidence").EnumerateArray())
            .Select(value => value.GetProperty("demandId").GetString()!)
            .Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(demand.Item.GetProperty("matchedDemandGenerationCount").GetInt32(), demandIds.Length);
        Assert.Equal(3, demandIds.Length);
        Assert.Equal(3, demandPeriods.Select(period => period.GetProperty("target").GetString())
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, demandPeriods.Count(period =>
            period.GetProperty("endReason").ValueKind == JsonValueKind.String
            && period.GetProperty("endReason").GetString() == "DEMAND_GONE"));

        var series = await ReadAsync("LONG_GONE_BUT_VISIBLE", seriesSublot);
        var seriesPeriods = series.Detail.GetProperty("periods").EnumerateArray().ToArray();
        Assert.Equal(series.Item.GetProperty("matchedPeriodCount").GetInt32(), seriesPeriods.Length);
        Assert.Equal(2, seriesPeriods.Length);
        var seriesDemandIds = seriesPeriods.SelectMany(period => period.GetProperty("evidence").EnumerateArray())
            .Select(value => value.GetProperty("demandId").GetString()!)
            .Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(series.Item.GetProperty("matchedDemandGenerationCount").GetInt32(), seriesDemandIds.Length);
        Assert.Equal(2, seriesDemandIds.Length);
        Assert.Single(seriesPeriods.Select(period => period.GetProperty("target").GetString())
            .Distinct(StringComparer.Ordinal));
        Assert.All(seriesPeriods, period => Assert.All(
            period.GetProperty("evidence").EnumerateArray(),
            evidence => Assert.NotEmpty(evidence.GetProperty("demandId").GetString()!)));
        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Default_detail_summarizes_duplicate_missing_invalid_area_and_multi_worktype_diagnostics_without_raw_rows()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        var observedAt = new DateTimeOffset(2026, 8, 15, 23, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(observedAt.AddMinutes(10));
        using var environment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();

        const string scalarSublot = "SL-T12-SCALAR-SUMMARIES";
        const string duplicateSublot = "SL-T12-DUPLICATE-SUMMARY";
        const string membershipSublot = "SL-T12-MEMBERSHIP-SUMMARY";
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket12-default-summaries",
            observedAt,
            Observation(scalarSublot, "N03-08", null, "QFN-SCALAR"),
            Observation(duplicateSublot, "N3-3", "WB-31", "QFN-A"),
            Observation(duplicateSublot, "N3-4", "WB-32", "QFN-B"),
            Observation(membershipSublot, "N3-5", "WB-41", "QFN-WIRE"),
            Observation(membershipSublot, "N3-6", "WB-42", "QFN-DIE", "DIE_ATTACH")));

        async Task<JsonElement[]> ReadDetailsAsync(string sublot)
        {
            var list = await GetJsonAsync(
                client,
                "/api/v2/error-search?window=ALL_HISTORY&sublot="
                + Uri.EscapeDataString(sublot) + "&pageSize=20");
            AssertNoRawObservationPayload(list);
            var snapshot = list.GetProperty("snapshotReference").GetString()!;
            var details = new List<JsonElement>();
            foreach (var item in list.GetProperty("items").EnumerateArray())
            {
                var detail = await GetJsonAsync(client, DetailUri(
                    item.GetProperty("seriesId").GetString()!, snapshot));
                AssertNoRawObservationPayload(detail);
                details.Add(detail);
            }
            return details.ToArray();
        }

        var scalarDetail = Assert.Single(await ReadDetailsAsync(scalarSublot));
        var scalarPeriods = scalarDetail.GetProperty("periods").EnumerateArray().ToArray();
        Assert.Equal(
            ["INVALID_MES_FIELD_FORMAT", "REQUIRED_MES_FIELD_MISSING"],
            scalarPeriods.Select(period => period.GetProperty("code").GetString()).Order(StringComparer.Ordinal));
        Assert.All(scalarPeriods, period =>
        {
            var evidence = Assert.Single(period.GetProperty("evidence").EnumerateArray());
            Assert.Equal(ErrorSearchDiagnosticValueKinds.Scalar,
                evidence.GetProperty("diagnosticValue").GetProperty("kind").GetString());
            Assert.NotEmpty(evidence.GetProperty("expectedRule").GetString()!);
            Assert.Equal("poll-ticket12-default-summaries", evidence.GetProperty("pollTraceId").GetString());
            Assert.NotEmpty(evidence.GetProperty("demandId").GetString()!);
        });

        var duplicateDetail = Assert.Single(await ReadDetailsAsync(duplicateSublot));
        var duplicateEvidence = duplicateDetail.GetProperty("periods").EnumerateArray()
            .Single(period => period.GetProperty("code").GetString() == "DUPLICATE_TRANSPORT_DEMAND_KEY")
            .GetProperty("evidence").EnumerateArray().Single();
        var rawSummary = duplicateEvidence.GetProperty("diagnosticValue");
        Assert.Equal(ErrorSearchDiagnosticValueKinds.RawObservationSet, rawSummary.GetProperty("kind").GetString());
        Assert.Equal(2, rawSummary.GetProperty("observationCount").GetInt32());
        Assert.NotEmpty(rawSummary.GetProperty("sha256Digest").GetString()!);
        Assert.Equal(JsonValueKind.Null, rawSummary.GetProperty("scalarValue").ValueKind);

        var membershipDetails = await ReadDetailsAsync(membershipSublot);
        Assert.Equal(2, membershipDetails.Length);
        Assert.All(membershipDetails, detail =>
        {
            var evidence = detail.GetProperty("periods").EnumerateArray()
                .Single(period => period.GetProperty("code").GetString() == "SUBLOT_MULTIPLE_WORK_TYPES")
                .GetProperty("evidence").EnumerateArray().Single();
            Assert.Equal(ErrorSearchDiagnosticValueKinds.WorkTypeMembership,
                evidence.GetProperty("diagnosticValue").GetProperty("kind").GetString());
            Assert.Equal(
                ["DIE_ATTACH", "WIRE_TO_NITROGEN"],
                evidence.GetProperty("relatedWorkTypes").EnumerateArray()
                    .Select(value => value.GetString()).Order(StringComparer.Ordinal));
        });
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
            new DateTimeOffset(2026, 8, 15, 8, 58, 0, TimeSpan.FromHours(8)),
            package);

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket12-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static string DetailUri(string seriesId, string snapshotReference) =>
        "/api/v2/error-search/" + Uri.EscapeDataString(seriesId)
        + "?snapshot=" + Uri.EscapeDataString(snapshotReference);

    private static string RawUri(
        string seriesId,
        string evidenceId,
        string snapshotReference,
        string fields,
        int maxItems) =>
        "/api/v2/error-search/" + Uri.EscapeDataString(seriesId)
        + "/evidence/" + Uri.EscapeDataString(evidenceId)
        + "/raw-observations?snapshot=" + Uri.EscapeDataString(snapshotReference)
        + "&fields=" + Uri.EscapeDataString(fields)
        + "&maxItems=" + maxItems;

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(HttpClient client, string uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RawSecret);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string uri)
    {
        using var response = await client.GetAsync(uri);
        return await ReadSuccessJsonAsync(response);
    }

    private static async Task<JsonElement> ReadSuccessJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private static async Task<string> AssertErrorCodeAsync(
        HttpResponseMessage response,
        string expectedCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(expectedCode, json.RootElement.GetProperty("code").GetString());
        return body;
    }

    private static void AssertNoRawObservationPayload(JsonElement value)
    {
        var body = value.GetRawText();
        Assert.DoesNotContain("rawObservations", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("occurrenceCount", body, StringComparison.OrdinalIgnoreCase);
    }

    private WebApplicationFactory<Program> CreateFactory(AdjustableTimeProvider clock) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                // appsettings.Local.json is intentionally loaded after the default
                // environment providers and may contain an empty local secret.
                // Replace only the HTTP options seam so this production-host test
                // proves the raw capability with an explicit configured credential.
                services.RemoveAll<MesIngestHostOptions>();
                services.AddSingleton(new MesIngestHostOptions
                {
                    Urls = "http://127.0.0.1:5088",
                    SharedSecret = RawSecret,
                });
            });
        });

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__SharedSecret"] = RawSecret,
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
