using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MesIngest.Tests;

public sealed class ScaleAndQueryEvidenceGateTests
{
    [Fact]
    public void Fast_capacity_fixture_projects_thirty_days_with_margin_below_escalation_thresholds()
    {
        var fixture = CreatePassingCapacityFixture();

        var result = RunCapacityFixture(fixture);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("MESINGEST_FAST_CAPACITY_FIXTURE: passed=True", result.Output, StringComparison.Ordinal);
        Assert.Contains("projectedLogicalUsedMb=", result.Output, StringComparison.Ordinal);
        Assert.Contains("projectedPhysicalDataMb=", result.Output, StringComparison.Ordinal);
        Assert.Contains("projectedLdfMb=", result.Output, StringComparison.Ordinal);
        Assert.Contains("escalationRequired=False", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Fast_capacity_fixture_fails_closed_for_threshold_nonlinearity_sample_compression_or_cleanup_risk()
    {
        var cases = new (string ExpectedCode, Action<JsonObject> Mutate)[]
        {
            ("CAPACITY_LOGICAL_70_PERCENT_ESCALATION", fixture =>
                fixture["sample"]!["storage"]!["logicalUsedMb"] = 40.0),
            ("CAPACITY_PHYSICAL_70_PERCENT_ESCALATION", fixture =>
                fixture["sample"]!["dataFileGrowthMb"] = 12_000.0),
            ("CAPACITY_LDF_70_PERCENT_ESCALATION", fixture =>
                fixture["sample"]!["storage"]!["ldfMb"] = 1_200.0),
            ("CAPACITY_GROWTH_NONLINEAR", fixture =>
                fixture["sample"]!["checkpoints"]![4]!["logicalUsedMb"] = 40.0),
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

            Assert.NotEqual(0, result.ExitCode);
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
        foreach (var profile in new[] { "0", "7", "30" })
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

    private static (int ExitCode, string Output) RunCapacityFixture(object fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mesingest-capacity-fixture-{Guid.NewGuid():N}");
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
                         "-DatabaseName", "MesIngest_Scale_CapacityFixture",
                         "-ConfirmIsolatedDatabase", "MESINGEST_SCALE_EVIDENCE_ONLY",
                         "-ValidateCapacityFixturePath", fixturePath,
                     })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment.Remove("MES_INGEST_SCALE_EVIDENCE_SQLSERVER");

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "Capacity fixture validation did not finish.");
            return (process.ExitCode, stdout + Environment.NewLine + stderr);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
    [InlineData("7", "0", "ProfileDays 7/30 requires ConfirmFullScaleEscalation")]
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
