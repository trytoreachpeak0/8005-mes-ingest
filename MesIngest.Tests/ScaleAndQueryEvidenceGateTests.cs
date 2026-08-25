using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MesIngest.Tests;

public sealed class ScaleAndQueryEvidenceGateTests
{
    [Fact]
    public void Accelerated_stability_fixture_passes_only_with_complete_bounded_concurrent_evidence()
    {
        var result = RunStabilityFixture(CreatePassingStabilityFixture());

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("MESINGEST_ACCELERATED_STABILITY_FIXTURE: passed=True", result.Output, StringComparison.Ordinal);
        Assert.Contains("soakEscalationRequired=False", result.Output, StringComparison.Ordinal);
        Assert.Contains("p95LatencyMs=125", result.Output, StringComparison.Ordinal);
        Assert.Contains("p99LatencyMs=410", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Accelerated_stability_fixture_fails_closed_for_every_runtime_risk_or_missing_evidence()
    {
        var cases = new (string ExpectedCode, Action<JsonObject> Mutate)[]
        {
            ("STABILITY_DURATION_OUT_OF_RANGE", fixture => fixture["workload"]!["durationSeconds"] = 1_799),
            ("STABILITY_SAMPLE_NOT_FIXED_OR_BOUNDED", fixture => fixture["workload"]!["maximumRawObservationRows"] = 250_001),
            ("STABILITY_SAMPLE_NOT_FIXED_OR_BOUNDED", fixture => fixture["workload"]!["concurrentClients"] = 7),
            ("STABILITY_BUILD_OR_CONTRACT_IDENTITY_INCOMPLETE", fixture => fixture["identity"]!["schemaVersion"] = 28),
            ("STABILITY_API_P95", fixture => fixture["latency"]!["p95LatencyMs"] = 2_000),
            ("STABILITY_API_P99", fixture => fixture["latency"]!["p99LatencyMs"] = 5_000),
            ("STABILITY_LATENCY_EVIDENCE_INCOMPLETE", fixture => fixture["latency"]!["surfaceStages"]![0]!["degraded"] = true),
            ("STABILITY_LATENCY_DEGRADATION", fixture => fixture["latency"]!["surfaceStages"]![0]!["lastHalfP95LatencyMs"] = 9_999),
            ("STABILITY_API_P95", fixture => fixture["latency"]!["surfaceSummaries"]![0]!["p95LatencyMs"] = 2_000),
            ("STABILITY_API_P99", fixture => fixture["latency"]!["surfaceSummaries"]![0]!["p99LatencyMs"] = 5_000),
            ("STABILITY_LATENCY_EVIDENCE_INCOMPLETE", fixture => fixture["latency"]!["surfaceSummaries"]!.AsArray().RemoveAt(0)),
            ("STABILITY_LATENCY_EVIDENCE_INCOMPLETE", fixture => fixture["latency"]!["phaseEvents"] = new JsonArray()),
            ("STABILITY_LATENCY_EVIDENCE_INCOMPLETE", fixture => fixture["latency"]!["phaseEvents"]![0]!.AsObject().Remove("state")),
            ("STABILITY_LATENCY_EVIDENCE_INCOMPLETE", fixture => fixture["latency"]!["phaseSummaries"]![0]!["workloadStage"] = "stable"),
            ("STABILITY_ERROR_701", fixture => fixture["resources"]!["error701Count"] = 1),
            ("STABILITY_XEVENT_DROPPED", fixture => fixture["resources"]!["xeventDroppedEventCount"] = 1),
            ("STABILITY_RESOURCE_SEMAPHORE", fixture => fixture["resources"]!["resourceSemaphoreSustainedSamples"] = 2),
            ("STABILITY_RESOURCE_SEMAPHORE", fixture =>
            {
                fixture["resources"]!["resourceSemaphoreAttributedIncreaseIntervals"] = 2;
                fixture["resources"]!["resourceSnapshots"]![1]!["attributedResourceSemaphoreWaitingTasks"] = 1;
                fixture["resources"]!["resourceSnapshots"]![2]!["attributedResourceSemaphoreWaitingTasks"] = 2;
            }),
            ("STABILITY_SPILL", fixture => fixture["resources"]!["spillCount"] = 1),
            ("STABILITY_SPILL_EVIDENCE_INCOMPLETE", fixture => fixture["resources"]!["spillCount"] = 1),
            ("STABILITY_SPILL_EVIDENCE_INCOMPLETE", fixture => fixture["resources"]!["spillDiagnosticsComplete"] = false),
            ("STABILITY_UNBOUNDED_LOCK_WAIT", fixture => fixture["resources"]!["maximumLockWaitMs"] = 5_001),
            ("STABILITY_HOST_MEMORY_TREND", fixture => fixture["resources"]!["hostStableGenerationWorkingSetSlopeMbPerMinute"] = 2.1),
            ("STABILITY_SQL_MEMORY_TREND", fixture => fixture["resources"]!["sqlWorkingSetSlopeMbPerMinute"] = 8.1),
            ("STABILITY_SQL_MEMORY_ENVELOPE", fixture => fixture["resources"]!["sqlWorkingSetPeakMb"] = 2_049.0),
            ("STABILITY_SQL_MEMORY_ENVELOPE", fixture => fixture["resources"]!["resourceSnapshots"]![0]!["sqlWorkingSetMb"] = 5_000.0),
            ("STABILITY_DATABASE_USED_TREND", fixture => fixture["resources"]!["logicalDatabaseUsedSlopeMbPerMinute"] = 1.1),
            ("STABILITY_DATABASE_FILE_TREND", fixture => fixture["resources"]!["physicalDataFileSlopeMbPerMinute"] = 1.1),
            ("STABILITY_LDF_TREND", fixture => fixture["resources"]!["ldfStableWindowUsedSlopeMbPerMinute"] = 1.1),
            ("STABILITY_LDF_TREND", fixture => fixture["resources"]!["ldfLateGrowthIntervalCount"] = 1),
            ("STABILITY_LDF_TREND", fixture => fixture["resources"]!["ldfPersistentLogReuseWaitSamples"] = 2),
            ("STABILITY_RESOURCE_EVIDENCE_INCOMPLETE", fixture => fixture["resources"]!["resourceSnapshots"]![0]!.AsObject().Remove("ldfMb")),
            ("STABILITY_UNBOUNDED_LOCK_WAIT", fixture =>
            {
                fixture["resources"]!["resourceSnapshots"]![0]!["maximumLockWaitMs"] = 5_001.0;
                fixture["resources"]!["resourceSnapshots"]![0]!["blockedRequestCount"] = 1;
            }),
            ("STABILITY_TEMPDB_TREND", fixture => fixture["resources"]!["tempdbUsedSlopeMbPerMinute"] = 2.1),
            ("STABILITY_HANDLE_TREND", fixture => fixture["resources"]!["hostStableGenerationHandleSlopePerMinute"] = 1.1),
            ("STABILITY_OVERLAPPING_POLL", fixture => fixture["behavior"]!["maximumConcurrentPolls"] = 2),
            ("STABILITY_CATCH_UP_BURST", fixture => fixture["behavior"]!["catchUpBurstCount"] = 1),
            ("STABILITY_HTTP_ERROR", fixture => fixture["behavior"]!["unexpectedHttpOutcomeCount"] = 1),
            ("STABILITY_HTTP_EVIDENCE_INCOMPLETE", fixture => fixture["behavior"]!["httpOutcomesComplete"] = false),
            ("STABILITY_HTTP_EVIDENCE_INCOMPLETE", fixture => fixture["behavior"]!["httpOutcomes"] = new JsonArray()),
            ("STABILITY_HTTP_EVIDENCE_INCOMPLETE", fixture => fixture["behavior"]!["httpOutcomes"]![0]!["statusCode"] = 500),
            ("STABILITY_HTTP_EVIDENCE_INCOMPLETE", fixture => fixture["behavior"]!["httpOutcomes"]!.AsArray().Add(
                JsonSerializer.SerializeToNode(new
                {
                    surface = "DemandSeriesIntentionalExpiry",
                    statusCode = 500,
                    errorCode = "MES_INGEST_HISTORY_EXPIRED",
                    classification = "EXPECTED_HISTORY_EXPIRED",
                    count = 1,
                }))),
            ("STABILITY_HTTP_EVIDENCE_INCOMPLETE", fixture => fixture["behavior"]!["expectedHistoryExpiredCount"] = 2),
            ("STABILITY_HISTORY_EXPIRY_CONTRACT", fixture => fixture["behavior"]!["expectedHistoryExpiredCount"] = 0),
            ("STABILITY_CURRENT_LOGICAL_READ_GROWTH", fixture => fixture["behavior"]!["currentLogicalReadGrowthPassed"] = false),
            ("STABILITY_FROZEN_COMMIT_MISMATCH", fixture => fixture["behavior"]!["frozenCommitMismatchCount"] = 1),
            ("STABILITY_FROZEN_READ_BLOCKED_PROJECTION", fixture => fixture["behavior"]!["projectionCommitsDuringFrozenReads"] = 0),
            ("STABILITY_FROZEN_READ_BLOCKED_PROJECTION", fixture => fixture["behavior"]!["frozenWindowsWithoutProjection"] = 1),
            ("STABILITY_CLEANUP_BACKLOG", fixture => fixture["behavior"]!["cleanupBacklogCount"] = 1),
            ("STABILITY_EARLIEST_AVAILABLE_NOT_ADVANCED", fixture => fixture["behavior"]!["earliestAvailableAdvanced"] = false),
            ("STABILITY_STORAGE_PRESSURE_UNEXPECTED", fixture => fixture["behavior"]!["storagePressurePauseCount"] = 1),
            ("STABILITY_STORAGE_PRESSURE_UNEXPECTED", fixture => fixture["resources"]!["resourceSnapshots"]![0]!["storagePressureStatus"] = "PAUSED_LOW_DISK"),
            ("STABILITY_RETRY_CONTRACT", fixture => fixture["behavior"]!["retrySchedulePassed"] = false),
            ("STABILITY_LOGICAL_DAY_BOUNDARY", fixture => fixture["behavior"]!["logicalDayBoundaryPassed"] = false),
            ("STABILITY_RESTART_STATE", fixture => fixture["behavior"]!["historyEpochPreservedAcrossRestart"] = false),
            ("STABILITY_EVIDENCE_INCOMPLETE", fixture => fixture["evidence"]!.AsObject().Remove("resourceSnapshotsComplete")),
            ("STABILITY_RUNTIME_EXCEPTION", fixture => fixture["evidence"]!["runtimeFailureType"] = "InvalidOperationException"),
        };

        foreach (var (expectedCode, mutate) in cases)
        {
            var fixture = JsonSerializer.SerializeToNode(CreatePassingStabilityFixture())!.AsObject();
            mutate(fixture);

            var result = RunStabilityFixture(fixture);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(expectedCode, result.Output, StringComparison.Ordinal);
            Assert.Contains("soakEscalationRequired=True", result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Accelerated_stability_fixture_does_not_call_instance_lifetime_semaphore_or_one_autogrowth_sustained()
    {
        var fixture = JsonSerializer.SerializeToNode(CreatePassingStabilityFixture())!.AsObject();
        fixture["resources"]!["resourceSemaphoreInstanceWaitingTaskDelta"] = 11;
        fixture["resources"]!["ldfAutogrowthEventCount"] = 1;
        fixture["resources"]!["ldfObservedGrowthIntervalCount"] = 1;
        fixture["resources"]!["ldfPhysicalMbPeak"] = 72.0;
        fixture["resources"]!["resourceSnapshots"]![0]!["ldfMb"] = 64.0;
        fixture["resources"]!["resourceSemaphoreSustainedSamples"] = 1;
        fixture["resources"]!["resourceSemaphoreAttributedActiveOrPendingSamples"] = 2;
        fixture["resources"]!["resourceSemaphoreMaximumConsecutiveActiveOrPendingSamples"] = 1;
        fixture["resources"]!["maximumPendingMemoryGrants"] = 1;
        fixture["resources"]!["resourceSnapshots"]![7]!["pendingMemoryGrants"] = 1;
        fixture["resources"]!["resourceSnapshots"]![8]!["pendingMemoryGrants"] = 1;

        var result = RunStabilityFixture(fixture);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.DoesNotContain("STABILITY_RESOURCE_SEMAPHORE", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("STABILITY_LDF_TREND", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Accelerated_stability_fixture_keeps_expected_history_expiry_separate_from_http_errors()
    {
        var fixture = JsonSerializer.SerializeToNode(CreatePassingStabilityFixture())!.AsObject();
        fixture["behavior"]!["expectedHistoryExpiredCount"] = 8;
        fixture["behavior"]!["httpOutcomes"]![9]!["count"] = 8;

        var result = RunStabilityFixture(fixture);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.DoesNotContain("STABILITY_HTTP_ERROR", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Stability_xevent_fixture_preserves_numeric_log_file_type_for_autogrowth_attribution()
    {
        const string xevent = """
            <event name="database_file_size_change" timestamp="2026-08-25T00:00:00Z">
              <data name="file_type"><value>1</value><text>Log file</text></data>
              <data name="is_automatic"><value>1</value></data>
              <action name="query_hash"><value>11798918993481001298</value></action>
              <action name="sql_text"><value>/* MESINGEST_QUERY:MARK_ABSENT_VISIBLE_DEMANDS */ SELECT 1</value></action>
            </event>
            """;
        var root = Path.Combine(Path.GetTempPath(), $"mesingest-xevent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixturePath = Path.Combine(root, "autogrowth.xel.xml");
        File.WriteAllText(fixturePath, xevent);
        try
        {
            var result = RunScriptFixture("-ValidateXEventEnvelopeFixturePath", fixturePath);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("fileType=1 automatic=1", result.Output, StringComparison.Ordinal);
            Assert.Contains("queryHash=0XA3BE2E4FAC0B6952", result.Output, StringComparison.Ordinal);
            Assert.Contains("queryName=MARK_ABSENT_VISIBLE_DEMANDS", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("18446744073709551616")]
    [InlineData("0x1234567890ABCDEF0")]
    public void Stability_xevent_fixture_rejects_malformed_or_out_of_range_hashes(string queryHash)
    {
        var xevent = $"""
            <event name="sort_warning" timestamp="2026-08-25T00:00:00Z">
              <action name="query_hash"><value>{queryHash}</value></action>
              <action name="sql_text"><value>/* MESINGEST_QUERY:MARK_ABSENT_VISIBLE_DEMANDS */ SELECT 1</value></action>
            </event>
            """;
        var root = Path.Combine(Path.GetTempPath(), $"mesingest-xevent-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixturePath = Path.Combine(root, "invalid-hash.xel.xml");
        File.WriteAllText(fixturePath, xevent);
        try
        {
            var result = RunScriptFixture("-ValidateXEventEnvelopeFixturePath", fixturePath);
            Assert.NotEqual(0, result.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Poll_projection_avoids_sql_sorts_for_bounded_demand_series_candidate_sets()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "MesIngest.Infrastructure",
            "SqlServer",
            "SqlServerMesIngestProjection.cs"));

        Assert.Contains("MESINGEST_QUERY:MARK_ABSENT_VISIBLE_DEMANDS", source, StringComparison.Ordinal);
        Assert.Contains("MESINGEST_QUERY:ARCHIVE_OVERDUE_GONE_SERIES", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY s.SeriesId;", source, StringComparison.Ordinal);
        Assert.Contains("visible.Sort", source, StringComparison.Ordinal);
        Assert.Contains("candidates.Sort", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Frozen_detail_and_cleanup_queries_reserve_bounded_sort_workspace_for_the_low_memory_gate()
    {
        var browse = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "MesIngest.Infrastructure",
            "SqlServer",
            "SqlServerMesIngestProjection.Browse.cs"));
        var retention = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "MesIngest.Infrastructure",
            "SqlServer",
            "SqlServerMesIngestProjection.Retention.cs"));

        Assert.Contains("MESINGEST_QUERY:FROZEN_SERIES_STATE", browse, StringComparison.Ordinal);
        Assert.Contains("MESINGEST_QUERY:FROZEN_DEMAND_GENERATIONS", browse, StringComparison.Ordinal);
        Assert.Equal(2, browse.Split("OPTION (MIN_GRANT_PERCENT = 1.0)", StringSplitOptions.None).Length - 1);
        Assert.Contains("MESINGEST_QUERY:HISTORY_CLEANUP_EXPIRED_BATCH", retention, StringComparison.Ordinal);
        Assert.Contains("OPTION (MIN_GRANT_PERCENT = 1.0)", retention, StringComparison.Ordinal);
    }

    [Fact]
    public void Attributed_spill_fixture_reports_the_real_spill_without_hiding_evidence_gaps()
    {
        var fixture = JsonSerializer.SerializeToNode(CreatePassingStabilityFixture())!.AsObject();
        ConfigureOneAttributedSpill(fixture);

        var result = RunStabilityFixture(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("STABILITY_SPILL", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("STABILITY_SPILL_EVIDENCE_INCOMPLETE", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("STABILITY_LATENCY_EVIDENCE_INCOMPLETE", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Spill_attribution_fixture_fails_closed_for_wrong_counts_orphan_rows_and_invalid_sample_windows()
    {
        var cases = new Action<JsonObject>[]
        {
            fixture => fixture["resources"]!["spillDiagnostics"]![0]!["count"] = 2,
            fixture => fixture["latency"]!["spillCorrelations"]![0]!["queryHash"] = "0xORPHAN",
            fixture => fixture["latency"]!["spillCorrelations"]![0]!["firstSampleStartedAt"] = "2026-08-25T00:06:00Z",
            fixture => fixture["resources"]!["spillDiagnostics"]![0]!["queryName"] = null,
            fixture => fixture["latency"]!["spillCorrelations"]![0]!["queryName"] = "OTHER_QUERY",
            fixture => fixture["resources"]!["spillDiagnostics"]![0]!["queryHash"] = "garbage",
        };
        foreach (var mutate in cases)
        {
            var fixture = JsonSerializer.SerializeToNode(CreatePassingStabilityFixture())!.AsObject();
            ConfigureOneAttributedSpill(fixture);
            mutate(fixture);
            var result = RunStabilityFixture(fixture);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("INCOMPLETE", result.Output, StringComparison.Ordinal);
        }

        var zeroSpill = JsonSerializer.SerializeToNode(CreatePassingStabilityFixture())!.AsObject();
        zeroSpill["latency"]!["spillCorrelations"] = JsonSerializer.SerializeToNode(new[]
        {
            new { surface = "orphan" },
        });
        var zeroResult = RunStabilityFixture(zeroSpill);
        Assert.NotEqual(0, zeroResult.ExitCode);
        Assert.Contains("STABILITY_LATENCY_EVIDENCE_INCOMPLETE", zeroResult.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaged_accelerated_stability_gate_declares_ticket_28_workload_and_diagnostics()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1"));
        var deterministicRunner = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "Invoke-Ticket28DeterministicContract.ps1"));

        foreach (var evidence in new[]
                 {
                     "AcceleratedConcurrencyStability",
                     "ValidateStabilityFixturePath",
                     "StabilityDurationMinutes",
                     "DatabaseFileRoot",
                     "escapedDatabaseDataFilePath",
                     "acceleratedPollStartIntervalSeconds",
                     "acceleratedCleanupCheckIntervalSeconds",
                     "defaultPollStartIntervalSeconds",
                     "failureBackoffSeconds",
                     "watchRefreshSeconds",
                     "referenceCatalogReads",
                     "frozenDetailReads",
                     "logicalDayBoundaryPassed",
                     "historyEpochPreservedAcrossRestart",
                     "packagedWatchClientReads",
                     "packagedReferenceConsumerReads",
                     "packageManifestSha256",
                     "watchClientSha256",
                     "referenceConsumerSha256",
                     "testAssemblySha256",
                     "Get-VerifiedPackageIdentity",
                     "RELEASE_MANIFEST_FILE_HASH_MISMATCH",
                     "runtimeFailureStage",
                     "runtimeFailureDetailType",
                     "runtimeFailureCode",
                     "CLEANUP_HOST_PROCESS_REMAINS",
                     "CLEANUP_XEVENT_SESSION_REMAINS",
                     "CLEANUP_DATABASE_REMAINS",
                     "hostExitedBeforeFailure",
                     "CapacityEvidencePath",
                     "ticket27CapacityPassed",
                     "currentLogicalReadGrowthPassed",
                     "frozenCommitMismatchCount",
                     "projectionCommitsDuringFrozenReads",
                     "sys.dm_os_process_memory",
                     "sys.dm_exec_query_memory_grants",
                     "sys.dm_os_wait_stats",
                     "RESOURCE_SEMAPHORE",
                     "resourceSemaphoreAttributedActiveOrPendingSamples",
                     "hostProcessGeneration",
                     "HostSessionId",
                     "ldfStableWindowUsedSlopeMbPerMinute",
                     "ldfStableWindowPhysicalGrowthCount",
                     "ldfAutogrowthEventCount",
                     "database_file_size_change",
                     "logReuseWait",
                     "httpOutcomes",
                     "EXPECTED_HISTORY_EXPIRED",
                     "MES_INGEST_HISTORY_EXPIRED",
                     "query_hash",
                     "query_plan_hash",
                     "node_id",
                     "spillDiagnostics",
                     "surfaceStages",
                     "error701Count",
                     "dropped_event_count",
                     "ALLOW_SINGLE_EVENT_LOSS",
                     "spillCount",
                     "maximumLockWaitMs",
                     "MAX(wait_time)",
                     "tempdbVersionStoreMb",
                     "StoragePressureStatus",
                     "EarliestAvailableHostUtc",
                     "soakEscalationRequired",
                     "4-hour-or-24-hour-real-soak",
                 })
        {
            Assert.Contains(evidence, script, StringComparison.Ordinal);
        }

        Assert.Equal(
            1,
            script.Split("identity = [pscustomobject][ordered]@{", StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "$_.name -ne 'DemandSeriesFrozenDetail'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "LEFT JOIN sys.dm_exec_sessions AS sessionRow",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SUM(CASE WHEN session_id IN",
            script,
            StringComparison.Ordinal);
        Assert.Contains("latencySampleBuckets", script, StringComparison.Ordinal);
        Assert.Contains("--configuration Release", deterministicRunner, StringComparison.Ordinal);
        Assert.Contains("TestDefinitions.UnitTest", deterministicRunner, StringComparison.Ordinal);
        Assert.Contains("testAssemblySha256", deterministicRunner, StringComparison.Ordinal);
        Assert.Contains("Copy-Item", deterministicRunner, StringComparison.Ordinal);
        Assert.Contains("sourceCommitBefore", deterministicRunner, StringComparison.Ordinal);
        Assert.Contains("sourceCommitAfter", deterministicRunner, StringComparison.Ordinal);
        Assert.Contains("sourceStatusBefore", deterministicRunner, StringComparison.Ordinal);
        Assert.Contains("sourceStatusAfter", deterministicRunner, StringComparison.Ordinal);
    }

    [Fact]
    public void Fast_capacity_fixture_projects_fifteen_days_with_margin_below_hard_limits_and_reports_advisories()
    {
        var fixture = JsonSerializer.SerializeToNode(CreatePassingCapacityFixture())!.AsObject();
        fixture["sample"]!["storage"]!["logicalUsedMb"] = 48.0;
        fixture["sample"]!["checkpoints"]![4]!["logicalUsedMb"] = 22.0;

        var result = RunCapacityFixture(fixture);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("MESINGEST_FAST_CAPACITY_FIXTURE: passed=True", result.Output, StringComparison.Ordinal);
        Assert.Contains("projectedLogicalUsedMb=", result.Output, StringComparison.Ordinal);
        Assert.Contains("projectedPhysicalDataMb=", result.Output, StringComparison.Ordinal);
        Assert.Contains("projectedLdfMb=", result.Output, StringComparison.Ordinal);
        Assert.Contains("targetDays=15", result.Output, StringComparison.Ordinal);
        Assert.Contains("targetRawObservationRows=55542600", result.Output, StringComparison.Ordinal);
        Assert.Contains("escalationRequired=False", result.Output, StringComparison.Ordinal);
        Assert.Contains("CAPACITY_LOGICAL_70_PERCENT_ESCALATION", result.Output, StringComparison.Ordinal);
        Assert.Contains("CAPACITY_GROWTH_NONLINEAR", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Fast_capacity_fixture_fails_closed_for_hard_limit_sample_compression_or_cleanup_risk()
    {
        var cases = new (string ExpectedCode, Action<JsonObject> Mutate)[]
        {
            ("CAPACITY_LOGICAL_HARD_LIMIT", fixture =>
                fixture["sample"]!["storage"]!["logicalUsedMb"] = 60.0),
            ("CAPACITY_PHYSICAL_HARD_LIMIT", fixture =>
                fixture["sample"]!["dataFileGrowthMb"] = 17_000.0),
            ("CAPACITY_LDF_HARD_LIMIT", fixture =>
                fixture["sample"]!["storage"]!["ldfMb"] = 1_576.0),
            ("CAPACITY_SAMPLE_INSUFFICIENT", fixture =>
            {
                fixture["sample"]!["rawObservationCount"] = 120_600;
                fixture["sample"]!["historyObservationCount"] = 120_000;
                fixture["sample"]!["historyRoundCount"] = 200;
            }),
            ("CAPACITY_PAGE_COMPRESSION_UNCERTAIN", fixture =>
                fixture["environment"]!["pageCompressionVerified"] = false),
            ("CAPACITY_RAW_CLEANUP_UNCERTAIN", fixture =>
                fixture["cleanup"]!["deletedRawObservationRows"] = 248_999),
            ("CAPACITY_RAW_CLEANUP_UNCERTAIN", fixture =>
            {
                fixture["cleanup"]!["expectedRawObservationRows"] = 249_000;
                fixture["cleanup"]!["deletedRawObservationRows"] = 249_000;
            }),
            ("CAPACITY_SERIES_CLEANUP_UNCERTAIN", fixture =>
                fixture["cleanup"]!["deletedEligibleSeries"] = 24),
            ("CAPACITY_ACTIVE_SERIES_SPLIT", fixture =>
                fixture["cleanup"]!["activeGraphUnchanged"] = false),
            ("CAPACITY_CLEANUP_EVIDENCE_INCOMPLETE", fixture =>
                fixture["cleanup"]!.AsObject().Remove("remainingRawObservationRows")),
            ("CAPACITY_VERSION_STORE_UNCERTAIN", fixture =>
                fixture["environment"]!["versionStoreMeasured"] = false),
            ("CAPACITY_TEMPDB_UNCERTAIN", fixture =>
                fixture["environment"]!["tempdbMeasured"] = false),
            ("CAPACITY_AUTOGROWTH_UNCERTAIN", fixture =>
                fixture["environment"]!["autoGrowthVerified"] = false),
            ("CAPACITY_TRANSIENT_STORAGE_EVIDENCE_INCOMPLETE", fixture =>
                fixture["sample"]!.AsObject().Remove("tombstoneLogicalUsedMb")),
        };

        foreach (var (expectedCode, mutate) in cases)
        {
            var fixture = JsonSerializer.SerializeToNode(CreatePassingCapacityFixture())!.AsObject();
            mutate(fixture);

            var result = RunCapacityFixture(fixture);

            Assert.True(
                result.ExitCode != 0,
                $"Expected {expectedCode} to fail closed, but fixture passed.{Environment.NewLine}{result.Output}");
            Assert.Contains(expectedCode, result.Output, StringComparison.Ordinal);
            Assert.Contains("escalationRequired=True", result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Fast_capacity_gate_rejects_more_than_250000_raw_observations_before_opening_sql()
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.CSharpRoot,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-ProfileDays", "0",
                     "-DatabaseName", "MesIngest_Scale_CapacityTooLarge",
                     "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                     "-FastCapacityProjection",
                     "-RepresentativeHistoryRounds", "416",
                     "-BaselineEvidencePath", "not-opened.json",
                 })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Windows PowerShell did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Capacity size guard did not finish.");
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("refuses to materialize more than 250,000", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Set MES_INGEST_SCALE_EVIDENCE_SQLSERVER", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaged_fast_capacity_gate_records_ticket_27_model_and_cleanup_evidence()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1"));

        foreach (var evidence in new[]
                 {
                     "FastCapacityProjection",
                     "targetRawObservationRows",
                     "safetyMarginFraction",
                     "escalationFraction",
                     "logicalMbPerRound",
                     "logicalMbPerRow",
                     "allocationProjection",
                     "data_compression_desc",
                     "tombstoneAllocations",
                     "databaseVersionStorePeakMb",
                     "versionStoreFormula",
                     "tempdbVersionStorePeakMb",
                     "tempdbFormula",
                     "ldfIncrementMb",
                     "logReuseWait",
                     "is_percent_growth",
                     "HistoryCleanupMaximumRawObservationRowsPerBatch",
                     "HistoryCleanupMaximumSeriesPerBatch",
                     "HistoryCleanupTimeBudgetSeconds",
                     "activeGraphUnchanged",
                     "graphIdentitySha256",
                     "errorPeriodCount",
                     "errorEvidenceCount",
                     "CAPACITY_GROWTH_NONLINEAR",
                     "CAPACITY_LOGICAL_70_PERCENT_ESCALATION",
                     "CAPACITY_PHYSICAL_70_PERCENT_ESCALATION",
                     "CAPACITY_LDF_70_PERCENT_ESCALATION",
                     "CAPACITY_LOGICAL_HARD_LIMIT",
                     "CAPACITY_PHYSICAL_HARD_LIMIT",
                     "CAPACITY_LDF_HARD_LIMIT",
                     "ConfirmFullScaleEscalation",
                 })
        {
            Assert.Contains(evidence, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Packaged_scale_gate_declares_every_ticket_02_evidence_surface()
    {
        var csharpRoot = RepositoryPaths.CSharpRoot;
        var gatePath = Path.Combine(
            csharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");

        Assert.True(File.Exists(gatePath), $"Missing scale evidence gate: {gatePath}");

        var gate = File.ReadAllText(gatePath);
        var tier1Runner = File.ReadAllText(Path.Combine(csharpRoot, "Invoke-RuntimeFeedbackTier1.ps1"));
        var install = File.ReadAllText(Path.Combine(csharpRoot, "pack", "INSTALL.md"));
        foreach (var profile in new[] { "0", "7", "15" })
        {
            Assert.Contains($"historyDays = {profile}", gate, StringComparison.Ordinal);
        }

        foreach (var path in new[]
                 {
                     "/api/v2/demand-series",
                     "/api/v2/externally-readable-demand-catalog",
                     "/api/v2/current-ingest-attention",
                     "/api/v2/watch-overview",
                     "/api/v2/readability-audit",
                     "/api/v2/error-search",
                     "/raw-observations",
                 })
        {
            Assert.Contains(path, gate, StringComparison.Ordinal);
        }

        foreach (var evidence in new[]
                 {
                     "query_post_execution_showplan",
                     "sql_statement_completed",
                     "logical_reads",
                     "duration",
                     "granted_memory_kb",
                     "SpillToTempDb",
                     "sys.dm_db_partition_stats",
                     "sys.database_files",
                     "data_compression_desc",
                     "schemaVersion",
                     "contractVersion",
                     "historyEpoch",
                     "sourceCommit",
                     "sqlSkippedTests",
                     "MISSING_ACTUAL_PLAN",
                     "EMPTY_SCALE_DATABASE",
                     "actualPlanCount",
                     "statementCount",
                     "logicalReads",
                     "maxGrantedMemoryKb",
                     "spillCount",
                     "physicalFiles",
                     "allocations",
                     "CLUSTERED",
                     "NONCLUSTERED",
                     "NON_CANONICAL_SCALE_PROFILE",
                     "hostSha256",
                     "sourceDirty",
                     "maxServerMemoryMb",
                     "compatibilityLevel",
                     "recoveryModel",
                     "xp_delete_files",
                 })
        {
            Assert.Contains(evidence, gate, StringComparison.Ordinal);
        }

        Assert.Contains("Invoke-ScaleAndQueryEvidence.ps1", install, StringComparison.Ordinal);
        Assert.Contains("MESINGEST_SCALE_EVIDENCE_ONLY", install, StringComparison.Ordinal);
        Assert.Contains("hostAssemblySha256", tier1Runner, StringComparison.Ordinal);
        Assert.Contains("testAssemblySha256", tier1Runner, StringComparison.Ordinal);
        Assert.Contains("sourceCommit", tier1Runner, StringComparison.Ordinal);
        Assert.Contains("trxVerified", gate, StringComparison.Ordinal);
        Assert.Contains("SQL_TIER1_BUILD_MISMATCH", gate, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output) RunStabilityFixture(object fixture) =>
        RunJsonFixture(
            fixture,
            "stability",
            "MesIngest_Scale_StabilityFixture",
            "-ValidateStabilityFixturePath");

    private static object CreatePassingStabilityFixture() => new
    {
        identity = new
        {
            sourceCommit = new string('a', 40),
            hostSha256 = new string('b', 64),
            packageManifestSha256 = new string('c', 64),
            watchClientSha256 = new string('d', 64),
            referenceConsumerSha256 = new string('e', 64),
            contractVersion = "2026.08.new-mes-ingest.v2.2",
            schemaVersion = 29,
            historyEpoch = "11111111-1111-1111-1111-111111111111",
        },
        environment = new
        {
            sqlProductMajor = 16,
            compatibilityLevel = 160,
            maxServerMemoryMb = 1536,
            recoveryModel = "SIMPLE",
            defaultPollStartIntervalSeconds = 60,
            failureBackoffSeconds = new[] { 60, 120, 300 },
            watchRefreshSeconds = new { overview = 30, currentAttention = 30, demandSeries = 60, readabilityAudit = 60, errorSearch = 60 },
            defaultCleanupCheckIntervalSeconds = 3600,
            acceleratedPollStartIntervalSeconds = 1,
            acceleratedCleanupCheckIntervalSeconds = 60,
        },
        workload = new
        {
            durationSeconds = 1_800,
            seriesCount = 600,
            initialRawObservationRows = 60_600,
            maximumRawObservationRows = 65_000,
            representativeHistoryRounds = 100,
            concurrentClients = 8,
            operations = new
            {
                successfulPolls = 1_700,
                watchApiReads = 20_000,
                frozenDetailReads = 2_000,
                referenceCatalogReads = 2_000,
                packagedWatchClientReads = 10,
                packagedReferenceConsumerReads = 2,
                cleanupChecks = 30,
                hostRestarts = 1,
            },
        },
        latency = new
        {
            sampleCount = 24_000,
            p95LatencyMs = 125.0,
            p99LatencyMs = 410.0,
            firstQuartileP95LatencyMs = 120.0,
            lastQuartileP95LatencyMs = 130.0,
            maximumSurfaceP95LatencyMs = 125.0,
            maximumSurfaceP99LatencyMs = 410.0,
            stableStageDegradationCount = 0,
            surfaceStagesComplete = true,
            surfaceSummaries = CreatePassingSurfaceSummaries(),
            surfaceStages = CreatePassingSurfaceStages(),
            phaseSummaries = CreatePassingPhaseSummaries(),
            phaseEvents = new object[]
            {
                new { kind = "cleanup", occurredAt = "2026-08-25T00:01:00Z", processGeneration = 1, state = "RUNNING" },
                new { kind = "restart", occurredAt = "2026-08-25T00:15:00Z", processGeneration = 2, state = "STARTED" },
            },
            spillCorrelations = Array.Empty<object>(),
        },
        resources = new
        {
            snapshotCount = 16,
            error701Count = 0,
            xeventDroppedEventCount = 0,
            resourceSemaphoreSustainedSamples = 0,
            resourceSemaphoreInstanceWaitingTaskDelta = 0,
            resourceSemaphoreInstanceWaitMsDelta = 0,
            resourceSemaphoreAttributedActiveOrPendingSamples = 0,
            resourceSemaphoreMaximumConsecutiveActiveOrPendingSamples = 0,
            resourceSemaphoreAttributedWaitingTaskDelta = 0,
            resourceSemaphoreAttributedWaitMsDelta = 0,
            resourceSemaphoreAttributedIncreaseIntervals = 0,
            spillCount = 0,
            spillDiagnosticsComplete = true,
            spillDiagnostics = Array.Empty<object>(),
            maximumLockWaitMs = 1_000.0,
            unboundedLockWaitCount = 0,
            maximumPendingMemoryGrants = 0,
            hostWorkingSetSlopeMbPerMinute = 0.0,
            hostProcessGenerationCount = 2,
            hostStableProcessGeneration = 2,
            hostStableGenerationSnapshotCount = 4,
            hostStableGenerationWorkingSetSlopeMbPerMinute = 0.0,
            hostStableGenerationHandleSlopePerMinute = 0.0,
            sqlWorkingSetSlopeMbPerMinute = 0.0,
            hostWorkingSetPeakMb = 180.0,
            sqlWorkingSetPeakMb = 1_900.0,
            hostHandlePeak = 400,
            logicalDatabaseUsedSlopeMbPerMinute = 0.0,
            physicalDataFileSlopeMbPerMinute = 0.0,
            ldfSlopeMbPerMinute = 0.0,
            ldfPhysicalMbPeak = 72.0,
            ldfUsedMbPeak = 20.0,
            ldfAutogrowthEventCount = 0,
            ldfObservedGrowthIntervalCount = 0,
            ldfStableWindowSnapshotCount = 4,
            ldfStableWindowPhysicalGrowthCount = 0,
            ldfPostGrowthPlateauSnapshotCount = 4,
            ldfLateGrowthIntervalCount = 0,
            ldfStableWindowUsedSlopeMbPerMinute = 0.0,
            ldfPersistentLogReuseWaitSamples = 0,
            ldfTrendComplete = true,
            tempdbUsedSlopeMbPerMinute = 0.0,
            hostHandleSlopePerMinute = 0.0,
            databaseVersionStorePeakMb = 1.0,
            tempdbVersionStorePeakMb = 2.0,
            resourceSnapshots = CreatePassingResourceSnapshots(),
        },
        behavior = new
        {
            maximumConcurrentPolls = 1,
            catchUpBurstCount = 0,
            pollSessionGenerationCount = 2,
            catchUpAnalysisComplete = true,
            httpErrorCount = 0,
            unexpectedHttpOutcomeCount = 0,
            expectedHistoryExpiredCount = 1,
            frozenConsistencyHttpErrorCount = 0,
            httpOutcomesComplete = true,
            httpOutcomes = CreatePassingHttpOutcomes(),
            currentLogicalReadGrowthPassed = true,
            frozenCommitMismatchCount = 0,
            projectionCommitsDuringFrozenReads = 1_600,
            frozenWindowsWithoutProjection = 0,
            cleanupBacklogCount = 0,
            earliestAvailableAdvanced = true,
            storagePressurePauseCount = 0,
            retrySchedulePassed = true,
            logicalDayBoundaryPassed = true,
            historyEpochPreservedAcrossRestart = true,
            restartStatePreserved = true,
        },
        evidence = new
        {
            runtimeFailureType = (string?)null,
            resourceSnapshotsComplete = true,
            latencySamplesComplete = true,
            xeventSignalsComplete = true,
            cleanupEvidenceComplete = true,
            deterministicContractEvidenceComplete = true,
        },
    };

    private static object[] CreatePassingSurfaceSummaries() =>
        RequiredStabilitySurfaces.Select(surface => (object)new
        {
            surface,
            sampleCount = 2_000,
            p95LatencyMs = 125.0,
            p99LatencyMs = 410.0,
        }).ToArray();

    private static object[] CreatePassingSurfaceStages() =>
        (from generation in new[] { 1, 2 }
         from surface in RequiredStabilitySurfaces
         select (object)new
         {
             surface,
             processGeneration = generation,
             stage = "stable",
             cleanupPhases = new[] { generation == 1 ? "RUNNING" : "IDLE" },
             sampleCount = 1_000,
             p95LatencyMs = 125.0,
             p99LatencyMs = 410.0,
             firstHalfP95LatencyMs = 120.0,
             lastHalfP95LatencyMs = 130.0,
             degraded = false,
         }).ToArray();

    private static object[] CreatePassingPhaseSummaries() =>
        (from generation in new[] { 1, 2 }
         from surface in RequiredStabilitySurfaces
         from workloadStage in new[] { "stabilizing", "stable" }
         select (object)new
         {
             surface,
             processGeneration = generation,
             workloadStage,
             cleanupPhase = generation == 1 ? "RUNNING" : "IDLE",
             sampleCount = workloadStage == "stable" ? 1_000 : 100,
             p95LatencyMs = 125.0,
             p99LatencyMs = 410.0,
             startedAt = workloadStage == "stable"
                 ? "2026-08-25T00:02:00Z"
                 : "2026-08-25T00:00:00Z",
             completedAt = workloadStage == "stable"
                 ? "2026-08-25T00:14:00Z"
                 : "2026-08-25T00:01:59Z",
         }).ToArray();

    private static object[] CreatePassingResourceSnapshots() =>
        Enumerable.Range(0, 16).Select(index => (object)new
        {
            capturedAt = $"2026-08-25T00:{index:D2}:00Z",
            hostProcessGeneration = index < 8 ? 1 : 2,
            pendingMemoryGrants = 0,
            attributedResourceSemaphoreActiveWaitTasks = 0,
            attributedResourceSemaphoreWaitingTasks = 0,
            attributedResourceSemaphoreWaitMs = 0,
            hostWorkingSetMb = 180.0,
            hostHandleCount = 400,
            sqlWorkingSetMb = 1_900.0,
            logicalDatabaseUsedMb = 100.0,
            physicalDataFileMb = 128.0,
            ldfMb = 72.0,
            logUsedMb = 20.0,
            logReuseWait = "NOTHING",
            tempdbUsedMb = 2.0,
            databaseVersionStoreMb = 1.0,
            tempdbVersionStoreMb = 2.0,
            maximumLockWaitMs = 1_000.0,
            blockedRequestCount = 0,
            storagePressureStatus = "IDLE",
        }).ToArray();

    private static object[] CreatePassingHttpOutcomes() =>
        RequiredStabilitySurfaces.Select(surface => (object)new
        {
            surface,
            statusCode = 200,
            errorCode = (string?)null,
            classification = "SUCCESS",
            count = 1_000,
        }).Append(new
        {
            surface = "DemandSeriesIntentionalExpiry",
            statusCode = 410,
            errorCode = "MES_INGEST_HISTORY_EXPIRED",
            classification = "EXPECTED_HISTORY_EXPIRED",
            count = 1,
        }).ToArray();

    private static void ConfigureOneAttributedSpill(JsonObject fixture)
    {
        fixture["resources"]!["spillCount"] = 1;
        fixture["resources"]!["spillDiagnosticsComplete"] = true;
        fixture["resources"]!["spillDiagnostics"] = JsonSerializer.SerializeToNode(new[]
        {
            new
            {
                warningEvent = "sort_warning",
                @operator = "Sort/Sort",
                node_id = "7",
                queryName = "MARK_ABSENT_VISIBLE_DEMANDS",
                queryHash = "0X0000000000001111",
                queryPlanHash = "0X0000000000002222",
                apiSurfaces = new[] { "DemandSeriesDefault" },
                count = 1,
                firstOccurredAt = "2026-08-25T00:05:00Z",
                lastOccurredAt = "2026-08-25T00:05:00Z",
            },
        });
        fixture["latency"]!["spillCorrelations"] = JsonSerializer.SerializeToNode(new[]
        {
            new
            {
                surface = "DemandSeriesDefault",
                processGeneration = 1,
                workloadStage = "stable",
                cleanupPhase = "RUNNING",
                warningEvent = "sort_warning",
                queryName = "MARK_ABSENT_VISIBLE_DEMANDS",
                queryHash = "0X0000000000001111",
                queryPlanHash = "0X0000000000002222",
                nodeId = "7",
                firstOccurredAt = "2026-08-25T00:05:00Z",
                lastOccurredAt = "2026-08-25T00:05:00Z",
                firstSampleStartedAt = "2026-08-25T00:04:59Z",
                lastSampleCompletedAt = "2026-08-25T00:05:01Z",
                count = 1,
            },
        });
    }

    private static readonly string[] RequiredStabilitySurfaces =
    [
        "Overview", "CurrentIngestAttention", "DemandSeriesDefault", "DemandSeriesVisible",
        "DemandSeriesWorkType", "ReadabilityAudit", "ErrorSearch",
        "ExternallyReadableDemandCatalog", "DemandSeriesFrozenDetail",
    ];

    private static (int ExitCode, string Output) RunCapacityFixture(object fixture) =>
        RunJsonFixture(
            fixture,
            "capacity",
            "MesIngest_Scale_CapacityFixture",
            "-ValidateCapacityFixturePath");

    private static (int ExitCode, string Output) RunJsonFixture(
        object fixture,
        string fixtureKind,
        string databaseName,
        string fixtureArgument)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"mesingest-{fixtureKind}-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixturePath = Path.Combine(root, "fixture.json");
        File.WriteAllText(fixturePath, JsonSerializer.Serialize(fixture));

        try
        {
            var script = Path.Combine(
                RepositoryPaths.CSharpRoot,
                "pack",
                "validation",
                "Invoke-ScaleAndQueryEvidence.ps1");
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryPaths.CSharpRoot,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-ProfileDays", "0",
                         "-DatabaseName", databaseName,
                         "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                         fixtureArgument, fixturePath,
                     })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(
                process.WaitForExit(30_000),
                $"{fixtureKind} fixture validation did not finish.");
            return (process.ExitCode, stdout + Environment.NewLine + stderr);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (int ExitCode, string Output) RunScriptFixture(string argument, string value)
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.CSharpRoot,
        };
        foreach (var item in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-ProfileDays", "0",
                     "-DatabaseName", "MesIngest_Scale_XEventFixture",
                     "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                     argument, value,
                 })
        {
            start.ArgumentList.Add(item);
        }
        start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Windows PowerShell did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "XEvent fixture validation did not finish.");
        return (process.ExitCode, stdout + Environment.NewLine + stderr);
    }

    private static object CreatePassingCapacityFixture() => new
    {
        baseline = new
        {
            storage = new { logicalUsedMb = 16.0, physicalDataMb = 64.0, ldfMb = 64.0 },
        },
        sample = new
        {
            rawObservationCount = 249_600,
            historyObservationCount = 249_000,
            historyRoundCount = 415,
            roundIntervalSeconds = 14,
            observationsPerRound = 600,
            storage = new { logicalUsedMb = 26.0, physicalDataMb = 64.0, ldfMb = 64.0 },
            checkpoints = new[]
            {
                new { historyRoundCount = 0, logicalUsedMb = 16.0 },
                new { historyRoundCount = 100, logicalUsedMb = 18.4 },
                new { historyRoundCount = 200, logicalUsedMb = 20.8 },
                new { historyRoundCount = 300, logicalUsedMb = 23.2 },
                new { historyRoundCount = 415, logicalUsedMb = 26.0 },
            },
            dataFileGrowthMb = 64.0,
            peakLogUsedMb = 8.0,
            tombstoneObservedCount = 25,
            tombstoneLogicalUsedMb = 0.03125,
            projectedTombstoneCount = 180,
            databaseVersionStorePeakMb = 2.0,
            tempdbVersionStorePeakMb = 3.0,
            tempdbUserObjectsImpactMb = 0.0,
            tempdbInternalObjectsImpactMb = 0.125,
        },
        environment = new
        {
            recoveryModel = "SIMPLE",
            logReuseWait = "NOTHING",
            compatibilityLevel = 160,
            maxServerMemoryMb = 1536,
            pageCompressionVerified = true,
            autoGrowthVerified = true,
            versionStoreMeasured = true,
            tempdbMeasured = true,
        },
        cleanup = new
        {
            expectedRawObservationRows = 249_600,
            deletedRawObservationRows = 249_600,
            expectedEligibleSeries = 25,
            deletedEligibleSeries = 25,
            tombstonesWritten = 25,
            activeSeriesWholeBefore = 420,
            activeSeriesWholeAfter = 420,
            activeGraphUnchanged = true,
            remainingRawObservationRows = 0,
            remainingRetentionEligibleSeries = 0,
            defaultCheckIntervalSeconds = 3_600,
            defaultRawObservationBatch = 210_000,
            defaultSeriesBatch = 25,
            defaultTimeBudgetSeconds = 15,
        },
    };

    [Fact]
    public void Packaged_scale_gate_can_scope_evidence_to_one_query_surface()
    {
        var gatePath = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var gate = File.ReadAllText(gatePath);

        Assert.Contains("[string] $QuerySurface = 'All'", gate, StringComparison.Ordinal);
        Assert.Contains("$QuerySurface -eq 'All'", gate, StringComparison.Ordinal);
        Assert.Contains("$surfaceCatalog | Where-Object { $_.scope -eq $QuerySurface }", gate, StringComparison.Ordinal);
        Assert.Contains("kind = 'raw-evidence'", gate, StringComparison.Ordinal);
        Assert.Contains("demandId=scale-demand-000001", gate, StringComparison.Ordinal);
        Assert.Contains("[long] $RepresentativeHistoryRounds = 0", gate, StringComparison.Ordinal);
        Assert.Contains("DemandSeries = 'DEMAND_SERIES'", gate, StringComparison.Ordinal);
        Assert.Contains("ExternallyReadableDemandCatalog = 'EXTERNALLY_READABLE_DEMAND_CATALOG'", gate, StringComparison.Ordinal);
        Assert.Contains("CurrentIngestAttention = 'CURRENT_INGEST_ATTENTION'", gate, StringComparison.Ordinal);
        Assert.Contains("Overview = 'OVERVIEW'", gate, StringComparison.Ordinal);
        Assert.Contains("_RAW_HISTORY_READ", gate, StringComparison.Ordinal);
        Assert.Contains("_SPILL", gate, StringComparison.Ordinal);
        Assert.Contains("_ABNORMAL_MEMORY_GRANT", gate, StringComparison.Ordinal);
        Assert.Contains("_LOGICAL_READ_GROWTH", gate, StringComparison.Ordinal);
        Assert.Contains("_MEMORY_GRANT_GROWTH", gate, StringComparison.Ordinal);
        Assert.Contains("_RUNTIME_IO_INCOMPLETE", gate, StringComparison.Ordinal);
        Assert.Contains("_MEMORY_GRANT_EVIDENCE_INCOMPLETE", gate, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0", "1", "Representative history evidence requires BaselineEvidencePath")]
    [InlineData("7", "0", "ProfileDays 7/15 requires ConfirmFullScaleEscalation")]
    public void Scale_gate_requires_complete_representative_or_escalated_evidence_before_opening_sql(
        string profileDays,
        string representativeRounds,
        string expectedError)
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.CSharpRoot,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-ProfileDays", profileDays,
                     "-RepresentativeHistoryRounds", representativeRounds,
                     "-DatabaseName", "MesIngest_Scale_IncompleteEvidence",
                     "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                 })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Windows PowerShell did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Scale evidence gate did not finish.");
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(expectedError, stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Set MES_INGEST_SCALE_EVIDENCE_SQLSERVER", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Scale_gate_rejects_full_profile_plus_representative_rounds_before_opening_sql()
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.CSharpRoot,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-ProfileDays", "7",
                     "-RepresentativeHistoryRounds", "1",
                     "-DatabaseName", "MesIngest_Scale_InvalidRepresentativeProfile",
                     "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                 })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Windows PowerShell did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Scale evidence gate did not finish.");
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("RepresentativeHistoryRounds is available only with ProfileDays 0", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Set MES_INGEST_SCALE_EVIDENCE_SQLSERVER", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Scale_gate_rejects_a_system_database_before_opening_sql()
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"mesingest-scale-reject-{Guid.NewGuid():N}");

        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryPaths.CSharpRoot,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-DatabaseName", "master",
                         "-ProfileDays", "0",
                         "-OutputRoot", outputRoot,
                         "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                     })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment["MES_INGEST_SCALE_EVIDENCE_SQLSERVER"] =
                "Server=127.0.0.1,1;Database=master;User ID=secret-user;Password=secret-password;Encrypt=False;TrustServerCertificate=True";

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "Scale evidence gate did not finish.");
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("Refusing unsafe scale database name", stdout + stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-user", stdout + stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-password", stdout + stderr, StringComparison.Ordinal);
            Assert.False(Directory.Exists(outputRoot));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Evidence_validator_rejects_a_missing_query_surface_plan_without_sql()
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var root = Path.Combine(Path.GetTempPath(), $"mesingest-scale-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixturePath = Path.Combine(root, "fixture.json");
        var surfaces = new[]
        {
            "DemandSeries",
            "ExternallyReadableDemandCatalog",
            "CurrentIngestAttention",
            "Overview",
            "ReadabilityAudit",
            "ErrorSearch",
            "RawEvidence",
        };
        File.WriteAllText(
            fixturePath,
            JsonSerializer.Serialize(new
            {
                queries = surfaces.Select(name => new
                {
                    name,
                    statementCount = 1,
                    actualPlanCount = name == "Overview" ? 0 : 1,
                }),
                statementMetrics = new[] { new { logical_reads = 1 } },
                actualPlans = new[] { new { planSha256 = new string('a', 64) } },
                data = new { series_count = 600, raw_observations = 600 },
                tests = new { satisfied = true },
                build = new { sourceCommit = new string('b', 40) },
                profile = new { canonical = true },
            }));

        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryPaths.CSharpRoot,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-ProfileDays", "0",
                         "-DatabaseName", "MesIngest_Scale_FixtureOnly",
                         "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                         "-ValidateEvidenceFixturePath", fixturePath,
                     })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "Evidence fixture validation did not finish.");
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(
                "MISSING_QUERY_SURFACE_EVIDENCE:Overview",
                stdout + stderr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Set MES_INGEST_SCALE_EVIDENCE_SQLSERVER",
                stdout + stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("DemandSeries", "DEMAND_SERIES", true)]
    [InlineData("ExternallyReadableDemandCatalog", "EXTERNALLY_READABLE_DEMAND_CATALOG", true)]
    [InlineData("CurrentIngestAttention", "CURRENT_INGEST_ATTENTION", true)]
    [InlineData("Overview", "OVERVIEW", true)]
    [InlineData("ReadabilityAudit", "READABILITY_AUDIT", true)]
    public void Evidence_validator_rejects_bounded_current_read_regressions_without_sql(
        string querySurface,
        string failurePrefix,
        bool requiresZeroRawHistoryReads)
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var root = Path.Combine(Path.GetTempPath(), $"mesingest-demand-series-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixturePath = Path.Combine(root, "fixture.json");
        File.WriteAllText(
            fixturePath,
            JsonSerializer.Serialize(new
            {
                queries = new[]
                {
                    new
                    {
                        name = querySurface == "DemandSeries" ? "DemandSeriesDefault" : querySurface,
                        statementCount = 1,
                        actualPlanCount = 1,
                        rawObservationPlanOperators = 1,
                        rawObservationLogicalReads = 12,
                        runtimeIoComplete = false,
                        memoryGrantEvidenceComplete = false,
                        spillCount = 1,
                        maxGrantedMemoryKb = 16384,
                    },
                },
                statementMetrics = new[] { new { logical_reads = 12 } },
                actualPlans = new[] { new { planSha256 = new string('a', 64) } },
                data = new { series_count = 600, raw_observations = 600 },
                tests = new { satisfied = true },
                build = new { sourceCommit = new string('b', 40) },
                profile = new { canonical = true },
            }));

        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryPaths.CSharpRoot,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-ProfileDays", "0",
                         "-DatabaseName", "MesIngest_Scale_DemandSeriesFixture",
                         "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                         "-QuerySurface", querySurface,
                         "-ValidateEvidenceFixturePath", fixturePath,
                     })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "Evidence fixture validation did not finish.");
            Assert.NotEqual(0, process.ExitCode);
            var output = stdout + stderr;
            if (requiresZeroRawHistoryReads)
            {
                Assert.Contains($"{failurePrefix}_RAW_HISTORY_READ", output, StringComparison.Ordinal);
            }
            Assert.Contains($"{failurePrefix}_RUNTIME_IO_INCOMPLETE", output, StringComparison.Ordinal);
            Assert.Contains($"{failurePrefix}_MEMORY_GRANT_EVIDENCE_INCOMPLETE", output, StringComparison.Ordinal);
            Assert.Contains($"{failurePrefix}_SPILL", output, StringComparison.Ordinal);
            Assert.Contains($"{failurePrefix}_ABNORMAL_MEMORY_GRANT", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Set MES_INGEST_SCALE_EVIDENCE_SQLSERVER", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("PollTrace", "POLL_TRACE", true, 2097152)]
    [InlineData("RawEvidence", "RAW_EVIDENCE", false, 131072)]
    public void Evidence_validator_rejects_historical_object_scans_without_sql(
        string querySurface,
        string failurePrefix,
        bool requiresEarliestIdentity,
        long maxResponseBytes)
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var root = Path.Combine(Path.GetTempPath(), $"mesingest-historical-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixturePath = Path.Combine(root, "fixture.json");
        File.WriteAllText(
            fixturePath,
            JsonSerializer.Serialize(new
            {
                queries = new[]
                {
                    new
                    {
                        name = querySurface,
                        statementCount = 1,
                        actualPlanCount = 1,
                        runtimeIoComplete = true,
                        memoryGrantEvidenceComplete = true,
                        spillCount = 0,
                        maxGrantedMemoryKb = 0,
                        responseBytes = 1,
                        maxResponseBytes,
                        objectKeySeekComplete = false,
                        unrelatedHistoryScanCount = 1,
                        earliestIdentityComplete = false,
                    },
                },
                statementMetrics = new[] { new { logical_reads = 1 } },
                actualPlans = new[] { new { planSha256 = new string('a', 64) } },
                data = new { series_count = 600, raw_observations = 600 },
                tests = new { satisfied = true },
                build = new { sourceCommit = new string('b', 40) },
                profile = new { canonical = true },
            }));

        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryPaths.CSharpRoot,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-ProfileDays", "0",
                         "-DatabaseName", "MesIngest_Scale_HistoricalFixture",
                         "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                         "-QuerySurface", querySurface,
                         "-ValidateEvidenceFixturePath", fixturePath,
                     })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "Evidence fixture validation did not finish.");
            Assert.NotEqual(0, process.ExitCode);
            var output = stdout + stderr;
            Assert.Contains($"{failurePrefix}_OBJECT_KEY_SEEK", output, StringComparison.Ordinal);
            Assert.Contains($"{failurePrefix}_UNRELATED_HISTORY_SCAN", output, StringComparison.Ordinal);
            if (requiresEarliestIdentity)
            {
                Assert.Contains($"{failurePrefix}_EARLIEST_IDENTITY", output, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("Set MES_INGEST_SCALE_EVIDENCE_SQLSERVER", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Percentile_validator_uses_nearest_rank_for_small_tail_samples()
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.CSharpRoot,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-ProfileDays", "0",
                     "-DatabaseName", "MesIngest_Scale_PercentileOnly",
                     "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                     "-ValidatePercentileFixture", "1,2,3,4,100",
                 })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Windows PowerShell did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Percentile fixture validation did not finish.");
        Assert.True(process.ExitCode == 0, stdout + Environment.NewLine + stderr);
        Assert.Contains("p50=3", stdout, StringComparison.Ordinal);
        Assert.Contains("p95=100", stdout, StringComparison.Ordinal);
        Assert.Contains("p99=100", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Set MES_INGEST_SCALE_EVIDENCE_SQLSERVER", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowPlan_validator_treats_missing_runtime_counter_attributes_as_zero_without_sql()
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var root = Path.Combine(Path.GetTempPath(), $"mesingest-showplan-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixturePath = Path.Combine(root, "showplan.xml");
        File.WriteAllText(
            fixturePath,
            """
            <ShowPlanXML>
              <BatchSequence><Batch><Statements><StmtSimple><QueryPlan>
                <RelOp PhysicalOp="Index Seek">
                  <IndexScan><Object Database="[fixture]" Schema="[mesingest]" Table="[DemandSeries]" Index="[ix_fixture]" /></IndexScan>
                  <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="1" ActualLogicalReads="2" /></RunTimeInformation>
                </RelOp>
              </QueryPlan></StmtSimple></Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """);

        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryPaths.CSharpRoot,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-ProfileDays", "0",
                         "-DatabaseName", "MesIngest_Scale_ShowPlanOnly",
                         "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                         "-ValidateShowPlanFixturePath", fixturePath,
                     })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "ShowPlan fixture validation did not finish.");
            Assert.True(process.ExitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("operators=1", stdout, StringComparison.Ordinal);
            Assert.Contains("scans=0", stdout, StringComparison.Ordinal);
            Assert.Contains("logicalReads=2", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("Set MES_INGEST_SCALE_EVIDENCE_SQLSERVER", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ShowPlan_validator_treats_an_empty_runtime_io_set_as_zero_without_sql()
    {
        var script = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "validation",
            "Invoke-ScaleAndQueryEvidence.ps1");
        var root = Path.Combine(Path.GetTempPath(), $"mesingest-empty-showplan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixturePath = Path.Combine(root, "showplan.xml");
        File.WriteAllText(
            fixturePath,
            "<ShowPlanXML><BatchSequence><Batch><Statements /></Batch></BatchSequence></ShowPlanXML>");

        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryPaths.CSharpRoot,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-ProfileDays", "0",
                         "-DatabaseName", "MesIngest_Scale_EmptyShowPlan",
                         "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                         "-ValidateShowPlanFixturePath", fixturePath,
                     })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "ShowPlan fixture validation did not finish.");
            Assert.True(process.ExitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("operators=0", stdout, StringComparison.Ordinal);
            Assert.Contains("logicalReads=0", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
