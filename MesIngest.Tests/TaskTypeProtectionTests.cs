using System.Net.Http.Json;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class TaskTypeProtectionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ProtectedWorkType = "WIRE_TO_NITROGEN";
    private const string IndependentWorkType = "LOADPORT_TO_OVEN";
    private const int EnterThreshold = 10;
    private const int RequiredRecoveryStreak = 2;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public TaskTypeProtectionTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Ticket01SqlServerFact]
    public async Task Zero_drop_enters_protection_and_only_unprotected_work_type_marks_gone()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var startedAt = new DateTimeOffset(2026, 8, 13, 10, 0, 2, TimeSpan.Zero);

        var fixture = await EnterProtectionAsync(
            ingestor,
            client,
            "ticket07-isolation",
            startedAt);

        AssertVisible(
            await ReadSeriesAsync(client, ProtectedWorkType, fixture.ProtectedSublot),
            fixture.ProtectedDemandId);
        AssertGone(
            await ReadSeriesAsync(client, IndependentWorkType, fixture.IndependentSublot),
            fixture.IndependentDemandId,
            fixture.ZeroDropCompletedAt);

        var protectedState = await ReadProtectionAsync(client, ProtectedWorkType);
        AssertProtectionState(
            protectedState,
            ProtectedWorkType,
            "PAUSED_ZERO_DROP",
            isCurrentAttention: true,
            lastHealthyNonZeroCount: EnterThreshold,
            latestObservedCount: 0,
            recoveryStreak: 0,
            protectionAllowsAbsenceAuthority: false,
            effectiveAbsenceAuthorityAvailable: false,
            fixture.ZeroDropReceipt);
        Assert.False(string.IsNullOrWhiteSpace(protectedState.GetProperty("episodeId").GetString()));
        Assert.Equal(
            fixture.ZeroDropCompletedAt,
            protectedState.GetProperty("enteredAt").GetDateTimeOffset());

        var independentState = await ReadProtectionAsync(client, IndependentWorkType);
        AssertProtectionState(
            independentState,
            IndependentWorkType,
            "MONITORING",
            isCurrentAttention: false,
            lastHealthyNonZeroCount: 1,
            latestObservedCount: 0,
            recoveryStreak: 0,
            protectionAllowsAbsenceAuthority: true,
            effectiveAbsenceAuthorityAvailable: true,
            fixture.ZeroDropReceipt);
        Assert.Equal(JsonValueKind.Null, independentState.GetProperty("episodeId").ValueKind);
        Assert.Equal(JsonValueKind.Null, independentState.GetProperty("enteredAt").ValueKind);

        var trace = await ReadTraceAsync(client, fixture.ZeroDropReceipt.PollTraceId);
        var protectedDecision = ReadDecision(trace, ProtectedWorkType);
        AssertDecision(
            protectedDecision,
            "MONITORING",
            "PAUSED_ZERO_DROP",
            observedCount: 0,
            lastHealthyNonZeroCount: EnterThreshold,
            recoveryStreakBefore: 0,
            recoveryStreakAfter: 0,
            protectionAllowsAbsenceAuthority: false,
            effectiveAbsenceAuthorityAvailable: false,
            eventCount: 1);
        var independentDecision = ReadDecision(trace, IndependentWorkType);
        AssertDecision(
            independentDecision,
            "MONITORING",
            "MONITORING",
            observedCount: 0,
            lastHealthyNonZeroCount: 1,
            recoveryStreakBefore: 0,
            recoveryStreakAfter: 0,
            protectionAllowsAbsenceAuthority: true,
            effectiveAbsenceAuthorityAvailable: true,
            eventCount: 0);

        var enteredEvent = Assert.Single(protectedState.GetProperty("events").EnumerateArray());
        AssertProtectionEvent(
            enteredEvent,
            expectedSequence: 1,
            "TASK_TYPE_PROTECTION_ENTERED",
            "MONITORING",
            "PAUSED_ZERO_DROP",
            fixture.ZeroDropReceipt,
            observedCount: 0,
            lastHealthyNonZeroCount: EnterThreshold,
            recoveryStreak: 0);
        Assert.Equal(
            enteredEvent.GetProperty("eventId").GetString(),
            Assert.Single(protectedDecision.GetProperty("eventIds").EnumerateArray()).GetString());

        var list = await ReadProtectionListAsync(client);
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        Assert.Equal(
            [IndependentWorkType, ProtectedWorkType],
            list.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("workType").GetString())
                .ToArray());

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Two_nonzero_rounds_clear_protection_but_following_round_restores_authority_before_absence_can_mark_gone()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var startedAt = new DateTimeOffset(2026, 8, 13, 11, 0, 2, TimeSpan.Zero);
        var fixture = await EnterProtectionAsync(
            ingestor,
            client,
            "ticket07-recovery",
            startedAt);
        var recoveryRows = HealthyObservations(
            ProtectedWorkType,
            "SL-TICKET07-RECOVERY",
            startedAt.AddMinutes(3),
            EnterThreshold);

        var firstRecoveryReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-recovery-first",
            startedAt.AddMinutes(3),
            recoveryRows));
        var firstRecovery = await ReadProtectionAsync(client, ProtectedWorkType);
        AssertProtectionState(
            firstRecovery,
            ProtectedWorkType,
            "RECOVERING",
            isCurrentAttention: true,
            lastHealthyNonZeroCount: EnterThreshold,
            latestObservedCount: EnterThreshold,
            recoveryStreak: 1,
            protectionAllowsAbsenceAuthority: false,
            effectiveAbsenceAuthorityAvailable: false,
            firstRecoveryReceipt);
        AssertVisible(
            await ReadSeriesAsync(client, ProtectedWorkType, fixture.ProtectedSublot),
            fixture.ProtectedDemandId);
        var firstDecision = ReadDecision(
            await ReadTraceAsync(client, firstRecoveryReceipt.PollTraceId),
            ProtectedWorkType);
        AssertDecision(
            firstDecision,
            "PAUSED_ZERO_DROP",
            "RECOVERING",
            EnterThreshold,
            EnterThreshold,
            0,
            1,
            protectionAllowsAbsenceAuthority: false,
            effectiveAbsenceAuthorityAvailable: false,
            eventCount: 1);

        var secondRecoveryReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-recovery-second",
            startedAt.AddMinutes(4),
            recoveryRows));
        var authorityPending = await ReadProtectionAsync(client, ProtectedWorkType);
        AssertProtectionState(
            authorityPending,
            ProtectedWorkType,
            "AUTHORITY_PENDING",
            isCurrentAttention: true,
            lastHealthyNonZeroCount: EnterThreshold,
            latestObservedCount: EnterThreshold,
            recoveryStreak: RequiredRecoveryStreak,
            protectionAllowsAbsenceAuthority: false,
            effectiveAbsenceAuthorityAvailable: false,
            secondRecoveryReceipt);
        AssertVisible(
            await ReadSeriesAsync(client, ProtectedWorkType, fixture.ProtectedSublot),
            fixture.ProtectedDemandId);
        var secondDecision = ReadDecision(
            await ReadTraceAsync(client, secondRecoveryReceipt.PollTraceId),
            ProtectedWorkType);
        AssertDecision(
            secondDecision,
            "RECOVERING",
            "AUTHORITY_PENDING",
            EnterThreshold,
            EnterThreshold,
            1,
            RequiredRecoveryStreak,
            protectionAllowsAbsenceAuthority: false,
            effectiveAbsenceAuthorityAvailable: false,
            eventCount: 2);

        var authoritativeAbsenceReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-recovery-authoritative-absence",
            startedAt.AddMinutes(5)));
        AssertGone(
            await ReadSeriesAsync(client, ProtectedWorkType, fixture.ProtectedSublot),
            fixture.ProtectedDemandId,
            startedAt.AddMinutes(5));
        var restored = await ReadProtectionAsync(client, ProtectedWorkType);
        AssertProtectionState(
            restored,
            ProtectedWorkType,
            "MONITORING",
            isCurrentAttention: false,
            lastHealthyNonZeroCount: EnterThreshold,
            latestObservedCount: 0,
            recoveryStreak: 0,
            protectionAllowsAbsenceAuthority: true,
            effectiveAbsenceAuthorityAvailable: true,
            authoritativeAbsenceReceipt);
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("episodeId").ValueKind);
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("enteredAt").ValueKind);

        var events = restored.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(5, events.Length);
        Assert.Equal(
            [1L, 2L, 3L, 4L, 5L],
            events.Select(item => item.GetProperty("workTypeSequence").GetInt64()).ToArray());
        Assert.Equal(
            [
                "TASK_TYPE_PROTECTION_ENTERED",
                "TASK_TYPE_PROTECTION_RECOVERY_PROGRESS",
                "TASK_TYPE_PROTECTION_RECOVERY_PROGRESS",
                "TASK_TYPE_PROTECTION_CLEARED",
                "TASK_TYPE_ABSENCE_AUTHORITY_RESTORED",
            ],
            events.Select(item => item.GetProperty("eventType").GetString()).ToArray());
        Assert.All(events, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("eventId").GetString()));
            Assert.Equal(ProtectedWorkType, item.GetProperty("workType").GetString());
            Assert.Equal(fixture.EpisodeId, item.GetProperty("episodeId").GetString());
            Assert.Equal(RequiredRecoveryStreak, item.GetProperty("requiredRecoveryStreak").GetInt32());
        });
        Assert.Equal(
            events[2].GetProperty("pollTraceId").GetString(),
            events[3].GetProperty("pollTraceId").GetString());
        Assert.Equal(
            events[2].GetProperty("projectionCommitId").GetString(),
            events[3].GetProperty("projectionCommitId").GetString());

        var restoredDecision = ReadDecision(
            await ReadTraceAsync(client, authoritativeAbsenceReceipt.PollTraceId),
            ProtectedWorkType);
        AssertDecision(
            restoredDecision,
            "AUTHORITY_PENDING",
            "MONITORING",
            observedCount: 0,
            lastHealthyNonZeroCount: EnterThreshold,
            recoveryStreakBefore: RequiredRecoveryStreak,
            recoveryStreakAfter: 0,
            protectionAllowsAbsenceAuthority: true,
            effectiveAbsenceAuthorityAvailable: true,
            eventCount: 1);

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Failure_incomplete_replay_and_conflict_do_not_change_protection_recovery_or_events()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var startedAt = new DateTimeOffset(2026, 8, 13, 12, 0, 2, TimeSpan.Zero);
        await EnterProtectionAsync(ingestor, client, "ticket07-isolation-gates", startedAt);
        var recoveryRows = HealthyObservations(
            ProtectedWorkType,
            "SL-TICKET07-ISOLATION-RECOVERY",
            startedAt.AddMinutes(3),
            EnterThreshold);
        var acceptedRecoveryRound = SuccessRound(
            "poll-ticket07-isolation-recovery",
            startedAt.AddMinutes(3),
            recoveryRows);
        var acceptedReceipt = await ingestor.IngestAsync(acceptedRecoveryRound);
        var beforeState = (await ReadProtectionAsync(client, ProtectedWorkType)).GetRawText();
        var beforeTrace = (await ReadTraceAsync(client, acceptedReceipt.PollTraceId)).GetRawText();

        var failureReceipt = await ingestor.IngestAsync(NonSuccessRound(
            "poll-ticket07-isolation-failure",
            MesTaskUnionRoundOutcome.Failure,
            startedAt.AddMinutes(4)));
        var incompleteReceipt = await ingestor.IngestAsync(NonSuccessRound(
            "poll-ticket07-isolation-incomplete",
            MesTaskUnionRoundOutcome.Incomplete,
            startedAt.AddMinutes(5),
            recoveryRows));
        Assert.Null(failureReceipt.ProjectionCommitId);
        Assert.Null(incompleteReceipt.ProjectionCommitId);
        Assert.Equal(
            JsonValueKind.Null,
            (await ReadTraceAsync(client, failureReceipt.PollTraceId))
                .GetProperty("projectionCommit").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            (await ReadTraceAsync(client, incompleteReceipt.PollTraceId))
                .GetProperty("projectionCommit").ValueKind);

        var replay = await ingestor.IngestAsync(acceptedRecoveryRound);
        Assert.True(replay.IsReplay);
        Assert.Equal(acceptedReceipt.ProjectionCommitId, replay.ProjectionCommitId);

        await Assert.ThrowsAsync<PollTraceConflictException>(() =>
            ingestor.IngestAsync(acceptedRecoveryRound with
            {
                Observations =
                [
                    .. recoveryRows,
                    ValidObservation(
                        ProtectedWorkType,
                        "SL-TICKET07-CONFLICTING-CONTENT",
                        startedAt),
                ],
            }));

        Assert.Equal(beforeState, (await ReadProtectionAsync(client, ProtectedWorkType)).GetRawText());
        Assert.Equal(beforeTrace, (await ReadTraceAsync(client, acceptedReceipt.PollTraceId)).GetRawText());
        var state = await ReadProtectionAsync(client, ProtectedWorkType);
        Assert.Equal("RECOVERING", state.GetProperty("phase").GetString());
        Assert.Equal(1, state.GetProperty("recoveryStreak").GetInt32());
        Assert.Equal(2, state.GetProperty("events").GetArrayLength());

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Protection_progress_survives_restart_and_both_gates_must_allow_absence_authority()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        var startedAt = new DateTimeOffset(2026, 8, 13, 13, 0, 2, TimeSpan.Zero);
        ProtectionFixture fixture;
        MesTaskUnionObservation[] recoveryRows;
        string persistedState;

        await using (var initialFactory = CreateFactory())
        {
            using var client = initialFactory.CreateClient();
            var ingestor = initialFactory.Services.GetRequiredService<RoundIngestor>();
            fixture = await EnterProtectionAsync(
                ingestor,
                client,
                "ticket07-restart",
                startedAt);
            recoveryRows = HealthyObservations(
                ProtectedWorkType,
                "SL-TICKET07-RESTART-RECOVERY",
                startedAt.AddMinutes(3),
                count: 1);
            var progressReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket07-restart-progress",
                startedAt.AddMinutes(3),
                recoveryRows));
            var progress = await ReadProtectionAsync(client, ProtectedWorkType);
            AssertProtectionState(
                progress,
                ProtectedWorkType,
                "RECOVERING",
                isCurrentAttention: true,
                lastHealthyNonZeroCount: 1,
                latestObservedCount: 1,
                recoveryStreak: 1,
                protectionAllowsAbsenceAuthority: false,
                effectiveAbsenceAuthorityAvailable: false,
                progressReceipt);
            persistedState = progress.GetRawText();
        }

        await using (var restartedFactory = CreateFactory())
        {
            using var client = restartedFactory.CreateClient();
            var ingestor = restartedFactory.Services.GetRequiredService<RoundIngestor>();
            Assert.Equal(persistedState, (await ReadProtectionAsync(client, ProtectedWorkType)).GetRawText());

            var secondRecoveryReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket07-restart-second-recovery",
                startedAt.AddMinutes(4),
                recoveryRows));
            var pending = await ReadProtectionAsync(client, ProtectedWorkType);
            AssertProtectionState(
                pending,
                ProtectedWorkType,
                "AUTHORITY_PENDING",
                isCurrentAttention: true,
                lastHealthyNonZeroCount: 1,
                latestObservedCount: 1,
                recoveryStreak: RequiredRecoveryStreak,
                protectionAllowsAbsenceAuthority: false,
                effectiveAbsenceAuthorityAvailable: false,
                secondRecoveryReceipt);
            Assert.False(
                (await ReadTraceAsync(client, secondRecoveryReceipt.PollTraceId))
                    .GetProperty("projectionCommit")
                    .GetProperty("absenceAuthority")
                    .GetBoolean());

            var restartPostBarrierReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket07-restart-post-barrier",
                startedAt.AddMinutes(5)));
            var restartStillBlocks = await ReadProtectionAsync(client, ProtectedWorkType);
            AssertProtectionState(
                restartStillBlocks,
                ProtectedWorkType,
                "AUTHORITY_PENDING",
                isCurrentAttention: true,
                lastHealthyNonZeroCount: 1,
                latestObservedCount: 0,
                recoveryStreak: RequiredRecoveryStreak,
                protectionAllowsAbsenceAuthority: false,
                effectiveAbsenceAuthorityAvailable: false,
                restartPostBarrierReceipt);
            AssertVisible(
                await ReadSeriesAsync(client, ProtectedWorkType, fixture.ProtectedSublot),
                fixture.ProtectedDemandId);
            var postBarrierDecision = ReadDecision(
                await ReadTraceAsync(client, restartPostBarrierReceipt.PollTraceId),
                ProtectedWorkType);
            Assert.False(
                postBarrierDecision.GetProperty("protectionAllowsAbsenceAuthority").GetBoolean());
            Assert.False(
                postBarrierDecision.GetProperty("effectiveAbsenceAuthorityAvailable").GetBoolean());
            Assert.Empty(postBarrierDecision.GetProperty("eventIds").EnumerateArray());

            var bothGatesOpenReceipt = await ingestor.IngestAsync(SuccessRound(
                "poll-ticket07-restart-both-gates-open",
                startedAt.AddMinutes(6)));
            AssertGone(
                await ReadSeriesAsync(client, ProtectedWorkType, fixture.ProtectedSublot),
                fixture.ProtectedDemandId,
                startedAt.AddMinutes(6));
            var bothGatesOpenDecision = ReadDecision(
                await ReadTraceAsync(client, bothGatesOpenReceipt.PollTraceId),
                ProtectedWorkType);
            Assert.True(
                bothGatesOpenDecision.GetProperty("protectionAllowsAbsenceAuthority").GetBoolean());
            Assert.True(
                bothGatesOpenDecision.GetProperty("effectiveAbsenceAuthorityAvailable").GetBoolean());
            var restored = await ReadProtectionAsync(client, ProtectedWorkType);
            Assert.Equal("MONITORING", restored.GetProperty("phase").GetString());
            Assert.False(restored.GetProperty("isCurrentAttention").GetBoolean());
            Assert.Equal(
                "TASK_TYPE_ABSENCE_AUTHORITY_RESTORED",
                restored.GetProperty("events").EnumerateArray().Last()
                    .GetProperty("eventType").GetString());
        }

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Distinct_recognizable_keys_define_healthy_count_without_raw_duplicate_inflation()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var startedAt = new DateTimeOffset(2026, 8, 13, 14, 0, 2, TimeSpan.Zero);
        const string belowThresholdWorkType = "STAGING_TO_WIRE";
        const string atThresholdWorkType = "OVEN_TO_STAGING";
        var nineDistinct = HealthyObservations(
            belowThresholdWorkType,
            "SL-TICKET07-COUNT-NINE",
            startedAt,
            EnterThreshold - 1);
        var tenDistinct = HealthyObservations(
            atThresholdWorkType,
            "SL-TICKET07-COUNT-TEN",
            startedAt,
            EnterThreshold);
        tenDistinct[^1] = tenDistinct[^1] with
        {
            Area = null,
            Eqp = "",
            Step = null,
            MesSourceDate = null,
            Package = " ",
        };
        var missingSublot = ValidObservation(
            atThresholdWorkType,
            "SL-TICKET07-UNASSIGNED-SUBLOT",
            startedAt) with
        {
            Sublot = null,
        };
        var missingWorkType = ValidObservation(
            atThresholdWorkType,
            "SL-TICKET07-UNASSIGNED-WORK-TYPE",
            startedAt) with
        {
            WorkType = " ",
        };
        var repeatedSameKey = Enumerable.Repeat(nineDistinct[0], 9);
        var rows = nineDistinct
            .Concat(repeatedSameKey)
            .Concat(tenDistinct)
            .Append(tenDistinct[0])
            .Append(missingSublot)
            .Append(missingWorkType)
            .ToArray();

        var baselineReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-count-baseline",
            startedAt,
            rows));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-count-restart-authority",
            startedAt.AddMinutes(1),
            rows));
        var baselineTrace = await ReadTraceAsync(client, baselineReceipt.PollTraceId);
        Assert.Equal(
            EnterThreshold - 1,
            ReadDecision(baselineTrace, belowThresholdWorkType)
                .GetProperty("observedCount").GetInt32());
        Assert.Equal(
            EnterThreshold,
            ReadDecision(baselineTrace, atThresholdWorkType)
                .GetProperty("observedCount").GetInt32());

        var zeroReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-count-zero",
            startedAt.AddMinutes(2)));
        var belowThreshold = await ReadProtectionAsync(client, belowThresholdWorkType);
        AssertProtectionState(
            belowThreshold,
            belowThresholdWorkType,
            "MONITORING",
            isCurrentAttention: false,
            lastHealthyNonZeroCount: EnterThreshold - 1,
            latestObservedCount: 0,
            recoveryStreak: 0,
            protectionAllowsAbsenceAuthority: true,
            effectiveAbsenceAuthorityAvailable: true,
            zeroReceipt);
        var protectedAtThreshold = await ReadProtectionAsync(client, atThresholdWorkType);
        AssertProtectionState(
            protectedAtThreshold,
            atThresholdWorkType,
            "PAUSED_ZERO_DROP",
            isCurrentAttention: true,
            lastHealthyNonZeroCount: EnterThreshold,
            latestObservedCount: 0,
            recoveryStreak: 0,
            protectionAllowsAbsenceAuthority: false,
            effectiveAbsenceAuthorityAvailable: false,
            zeroReceipt);
        AssertGone(
            await ReadSeriesAsync(client, belowThresholdWorkType, nineDistinct[0].Sublot!),
            (await ReadSeriesAsync(client, belowThresholdWorkType, nineDistinct[0].Sublot!))
                .GetProperty("currentDemand").GetProperty("demandId").GetString()!,
            startedAt.AddMinutes(2));
        var protectedSeries = await ReadSeriesAsync(client, atThresholdWorkType, tenDistinct[0].Sublot!);
        AssertVisible(
            protectedSeries,
            protectedSeries.GetProperty("currentDemand").GetProperty("demandId").GetString()!);

        AssertDatabaseEvidence(database);
    }

    [Ticket01SqlServerFact]
    public async Task Protected_gone_series_does_not_archive_while_other_work_type_can_archive()
    {
        await using var database = await Ticket01SqlServerDatabase.CreateAsync();
        using var hostEnvironment = ConfigureProductionV2Environment(database.ConnectionString);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ingestor = factory.Services.GetRequiredService<RoundIngestor>();
        var startedAt = new DateTimeOffset(2026, 8, 13, 15, 0, 2, TimeSpan.Zero);
        const string protectedTargetSublot = "SL-TICKET07-ARCHIVE-PROTECTED";
        const string independentTargetSublot = "SL-TICKET07-ARCHIVE-INDEPENDENT";
        var protectedBaseline = HealthyObservations(
            ProtectedWorkType,
            "SL-TICKET07-ARCHIVE-BASELINE",
            startedAt,
            EnterThreshold);
        var independentBaseline = ValidObservation(
            IndependentWorkType,
            "SL-TICKET07-ARCHIVE-INDEPENDENT-BASELINE",
            startedAt.AddMinutes(-1));
        var rows = protectedBaseline
            .Append(ValidObservation(ProtectedWorkType, protectedTargetSublot, startedAt.AddMinutes(-2)))
            .Append(independentBaseline)
            .Append(ValidObservation(IndependentWorkType, independentTargetSublot, startedAt.AddMinutes(-2)))
            .ToArray();

        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-archive-baseline",
            startedAt,
            rows));
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-archive-restart-authority",
            startedAt.AddMinutes(1),
            rows));
        var protectedBeforeGone = await ReadSeriesAsync(client, ProtectedWorkType, protectedTargetSublot);
        var independentBeforeGone = await ReadSeriesAsync(client, IndependentWorkType, independentTargetSublot);
        var protectedSeriesId = protectedBeforeGone.GetProperty("seriesId").GetString()!;
        var independentSeriesId = independentBeforeGone.GetProperty("seriesId").GetString()!;
        var protectedDemandId = protectedBeforeGone.GetProperty("currentDemand").GetProperty("demandId").GetString()!;
        var independentDemandId = independentBeforeGone.GetProperty("currentDemand").GetProperty("demandId").GetString()!;

        var goneAt = startedAt.AddMinutes(2);
        await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-archive-gone",
            goneAt,
            protectedBaseline.Append(independentBaseline).ToArray()));
        AssertGone(
            await ReadSeriesAsync(client, ProtectedWorkType, protectedTargetSublot),
            protectedDemandId,
            goneAt);
        AssertGone(
            await ReadSeriesAsync(client, IndependentWorkType, independentTargetSublot),
            independentDemandId,
            goneAt);

        var archiveAttemptAt = goneAt.AddHours(12);
        var archiveReceipt = await ingestor.IngestAsync(SuccessRound(
            "poll-ticket07-archive-zero-drop-boundary",
            archiveAttemptAt));
        var protectedAfter = await ReadSeriesAsync(client, ProtectedWorkType, protectedTargetSublot);
        Assert.Equal(protectedSeriesId, protectedAfter.GetProperty("seriesId").GetString());
        AssertGone(protectedAfter, protectedDemandId, goneAt);
        Assert.Equal(JsonValueKind.Null, protectedAfter.GetProperty("archivedAt").ValueKind);
        Assert.DoesNotContain(
            protectedAfter.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("eventType").GetString() == "GONE_TIMEOUT_ARCHIVED");

        var independentAfter = await ReadSeriesAsync(client, IndependentWorkType, independentTargetSublot);
        Assert.Equal(independentSeriesId, independentAfter.GetProperty("seriesId").GetString());
        Assert.Equal("ARCHIVED", independentAfter.GetProperty("lifecycle").GetString());
        Assert.Equal(archiveAttemptAt, independentAfter.GetProperty("archivedAt").GetDateTimeOffset());
        Assert.Contains(
            independentAfter.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("eventType").GetString() == "GONE_TIMEOUT_ARCHIVED"
                && item.GetProperty("pollTraceId").GetString() == archiveReceipt.PollTraceId
                && item.GetProperty("projectionCommitId").GetString() == archiveReceipt.ProjectionCommitId);

        var protectedDecision = ReadDecision(
            await ReadTraceAsync(client, archiveReceipt.PollTraceId),
            ProtectedWorkType);
        Assert.False(
            protectedDecision.GetProperty("effectiveAbsenceAuthorityAvailable").GetBoolean());
        Assert.True(
            ReadDecision(await ReadTraceAsync(client, archiveReceipt.PollTraceId), IndependentWorkType)
                .GetProperty("effectiveAbsenceAuthorityAvailable").GetBoolean());

        AssertDatabaseEvidence(database);
    }

    private static async Task<ProtectionFixture> EnterProtectionAsync(
        RoundIngestor ingestor,
        HttpClient client,
        string pollPrefix,
        DateTimeOffset baselineCompletedAt)
    {
        var protectedRows = HealthyObservations(
            ProtectedWorkType,
            $"SL-{pollPrefix}-PROTECTED",
            baselineCompletedAt,
            EnterThreshold);
        var independentSublot = $"SL-{pollPrefix}-INDEPENDENT";
        var allRows = protectedRows
            .Append(ValidObservation(
                IndependentWorkType,
                independentSublot,
                baselineCompletedAt.AddMinutes(-1)))
            .ToArray();

        await ingestor.IngestAsync(SuccessRound(
            $"poll-{pollPrefix}-baseline",
            baselineCompletedAt,
            allRows));
        await ingestor.IngestAsync(SuccessRound(
            $"poll-{pollPrefix}-restart-authority",
            baselineCompletedAt.AddMinutes(1),
            allRows));

        var protectedSublot = protectedRows[0].Sublot!;
        var protectedDemandId = (await ReadSeriesAsync(client, ProtectedWorkType, protectedSublot))
            .GetProperty("currentDemand").GetProperty("demandId").GetString()!;
        var independentDemandId = (await ReadSeriesAsync(client, IndependentWorkType, independentSublot))
            .GetProperty("currentDemand").GetProperty("demandId").GetString()!;

        var zeroDropCompletedAt = baselineCompletedAt.AddMinutes(2);
        var zeroDropReceipt = await ingestor.IngestAsync(SuccessRound(
            $"poll-{pollPrefix}-zero-drop",
            zeroDropCompletedAt));
        var entered = await ReadProtectionAsync(client, ProtectedWorkType);
        return new ProtectionFixture(
            protectedSublot,
            protectedDemandId,
            independentSublot,
            independentDemandId,
            entered.GetProperty("episodeId").GetString()!,
            zeroDropCompletedAt,
            zeroDropReceipt);
    }

    private static MesTaskUnionObservation[] HealthyObservations(
        string workType,
        string sublotPrefix,
        DateTimeOffset completedAt,
        int count) =>
        Enumerable.Range(1, count)
            .Select(index => ValidObservation(
                workType,
                $"{sublotPrefix}-{index:00}",
                completedAt.AddMinutes(-index)))
            .ToArray();

    private static MesTaskUnionObservation ValidObservation(
        string workType,
        string sublot,
        DateTimeOffset mesSourceDate) =>
        new(
            workType,
            sublot,
            Area: "A1-1",
            Eqp: "EQP-TICKET07",
            Step: "STEP-TICKET07",
            mesSourceDate,
            Package: "PKG-TICKET07");

    private static MesTaskUnionRound SuccessRound(
        string pollTraceId,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket07-v1",
            MesTaskUnionRoundOutcome.Success,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static MesTaskUnionRound NonSuccessRound(
        string pollTraceId,
        MesTaskUnionRoundOutcome outcome,
        DateTimeOffset completedAt,
        params MesTaskUnionObservation[] observations) =>
        new(
            pollTraceId,
            "mes-task-union-ticket07-v1",
            outcome,
            completedAt.AddSeconds(-2),
            completedAt,
            observations);

    private static async Task<JsonElement> ReadProtectionAsync(HttpClient client, string workType) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/api/v2/task-type-protections/{Uri.EscapeDataString(workType)}");

    private static async Task<JsonElement> ReadProtectionListAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/v2/task-type-protections");

    private static async Task<JsonElement> ReadSeriesAsync(
        HttpClient client,
        string workType,
        string sublot) =>
        await client.GetFromJsonAsync<JsonElement>(
            "/api/v2/demand-series/by-key"
            + $"?workType={Uri.EscapeDataString(workType)}"
            + $"&sublot={Uri.EscapeDataString(sublot)}");

    private static async Task<JsonElement> ReadTraceAsync(HttpClient client, string pollTraceId) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/api/v2/poll-traces/{Uri.EscapeDataString(pollTraceId)}");

    private static JsonElement ReadDecision(JsonElement trace, string workType) =>
        Assert.Single(
            trace.GetProperty("projectionCommit")
                .GetProperty("taskTypeProtectionDecisions")
                .EnumerateArray()
                .Where(item => item.GetProperty("workType").GetString() == workType));

    private static void AssertDecision(
        JsonElement decision,
        string phaseBefore,
        string phaseAfter,
        int observedCount,
        int lastHealthyNonZeroCount,
        int recoveryStreakBefore,
        int recoveryStreakAfter,
        bool protectionAllowsAbsenceAuthority,
        bool effectiveAbsenceAuthorityAvailable,
        int eventCount)
    {
        Assert.Equal(phaseBefore, decision.GetProperty("phaseBefore").GetString());
        Assert.Equal(phaseAfter, decision.GetProperty("phaseAfter").GetString());
        Assert.Equal(observedCount, decision.GetProperty("observedCount").GetInt32());
        Assert.Equal(lastHealthyNonZeroCount, decision.GetProperty("lastHealthyNonZeroCount").GetInt32());
        Assert.Equal(recoveryStreakBefore, decision.GetProperty("recoveryStreakBefore").GetInt32());
        Assert.Equal(recoveryStreakAfter, decision.GetProperty("recoveryStreakAfter").GetInt32());
        Assert.Equal(
            protectionAllowsAbsenceAuthority,
            decision.GetProperty("protectionAllowsAbsenceAuthority").GetBoolean());
        Assert.Equal(
            effectiveAbsenceAuthorityAvailable,
            decision.GetProperty("effectiveAbsenceAuthorityAvailable").GetBoolean());
        Assert.Equal(eventCount, decision.GetProperty("eventIds").GetArrayLength());
    }

    private static void AssertProtectionState(
        JsonElement state,
        string workType,
        string phase,
        bool isCurrentAttention,
        int lastHealthyNonZeroCount,
        int latestObservedCount,
        int recoveryStreak,
        bool protectionAllowsAbsenceAuthority,
        bool effectiveAbsenceAuthorityAvailable,
        RoundCommitReceipt receipt)
    {
        Assert.Equal(workType, state.GetProperty("workType").GetString());
        Assert.Equal(phase, state.GetProperty("phase").GetString());
        Assert.Equal(isCurrentAttention, state.GetProperty("isCurrentAttention").GetBoolean());
        Assert.Equal(lastHealthyNonZeroCount, state.GetProperty("lastHealthyNonZeroCount").GetInt32());
        Assert.Equal(latestObservedCount, state.GetProperty("latestObservedCount").GetInt32());
        Assert.Equal(recoveryStreak, state.GetProperty("recoveryStreak").GetInt32());
        Assert.Equal(
            RequiredRecoveryStreak,
            state.GetProperty("requiredRecoveryStreak").GetInt32());
        Assert.Equal(
            protectionAllowsAbsenceAuthority,
            state.GetProperty("protectionAllowsAbsenceAuthority").GetBoolean());
        Assert.Equal(
            effectiveAbsenceAuthorityAvailable,
            state.GetProperty("effectiveAbsenceAuthorityAvailable").GetBoolean());
        Assert.Equal(receipt.PollTraceId, state.GetProperty("latestPollTraceId").GetString());
        Assert.Equal(receipt.ProjectionCommitId, state.GetProperty("latestProjectionCommitId").GetString());
    }

    private static void AssertProtectionEvent(
        JsonElement item,
        long expectedSequence,
        string eventType,
        string phaseBefore,
        string phaseAfter,
        RoundCommitReceipt receipt,
        int observedCount,
        int lastHealthyNonZeroCount,
        int recoveryStreak)
    {
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("eventId").GetString()));
        Assert.Equal(ProtectedWorkType, item.GetProperty("workType").GetString());
        Assert.Equal(expectedSequence, item.GetProperty("workTypeSequence").GetInt64());
        Assert.Equal(eventType, item.GetProperty("eventType").GetString());
        Assert.Equal(phaseBefore, item.GetProperty("phaseBefore").GetString());
        Assert.Equal(phaseAfter, item.GetProperty("phaseAfter").GetString());
        Assert.Equal(receipt.PollTraceId, item.GetProperty("pollTraceId").GetString());
        Assert.Equal(receipt.ProjectionCommitId, item.GetProperty("projectionCommitId").GetString());
        Assert.Equal(observedCount, item.GetProperty("observedCount").GetInt32());
        Assert.Equal(lastHealthyNonZeroCount, item.GetProperty("lastHealthyNonZeroCount").GetInt32());
        Assert.Equal(recoveryStreak, item.GetProperty("recoveryStreak").GetInt32());
        Assert.Equal(
            RequiredRecoveryStreak,
            item.GetProperty("requiredRecoveryStreak").GetInt32());
        _ = item.GetProperty("occurredAt").GetDateTimeOffset();
    }

    private static void AssertVisible(JsonElement series, string demandId)
    {
        Assert.Equal("TRACKING", series.GetProperty("lifecycle").GetString());
        Assert.Equal("VISIBLE", series.GetProperty("currentPresence").GetString());
        Assert.Equal(demandId, series.GetProperty("currentDemand").GetProperty("demandId").GetString());
        Assert.Equal("VISIBLE", series.GetProperty("currentDemand").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, series.GetProperty("currentDemand").GetProperty("goneConfirmedAt").ValueKind);
    }

    private static void AssertGone(JsonElement series, string demandId, DateTimeOffset goneConfirmedAt)
    {
        Assert.Equal("TRACKING", series.GetProperty("lifecycle").GetString());
        Assert.Equal("GONE", series.GetProperty("currentPresence").GetString());
        Assert.Equal(demandId, series.GetProperty("currentDemand").GetProperty("demandId").GetString());
        Assert.Equal("GONE", series.GetProperty("currentDemand").GetProperty("status").GetString());
        Assert.Equal(
            goneConfirmedAt.ToUniversalTime(),
            series.GetProperty("currentDemand").GetProperty("goneConfirmedAt").GetDateTimeOffset());
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        _factory.WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Production));

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

    private static IDisposable ConfigureProductionV2Environment(string connectionString) =>
        new Ticket01ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["DOTNET_ENVIRONMENT"] = Environments.Production,
            [$"{MesIngestHostOptions.SectionName}__NewSqlServerConnectionString"] = connectionString,
            [$"{MesIngestHostOptions.SectionName}__EnableLegacyDevelopmentEndpoints"] = "false",
            [$"{MesIngestHostOptions.SectionName}__ZeroDropEnterThreshold"] = EnterThreshold.ToString(),
            [$"{MesIngestHostOptions.SectionName}__ZeroDropClearStreak"] = RequiredRecoveryStreak.ToString(),
        });

    private sealed record ProtectionFixture(
        string ProtectedSublot,
        string ProtectedDemandId,
        string IndependentSublot,
        string IndependentDemandId,
        string EpisodeId,
        DateTimeOffset ZeroDropCompletedAt,
        RoundCommitReceipt ZeroDropReceipt);

}
