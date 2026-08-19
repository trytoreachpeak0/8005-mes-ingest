using System.Diagnostics;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 26. The factory acceptance run happens once, on plant equipment, against a
/// live read-only Oracle. Its conclusions are release sign-off input, so the parts that
/// decide what may be written down as a pass are exercised here as the shipped script,
/// not described in prose. The recurring failure these guard against is a run that
/// silently upgrades laboratory or golden-machine evidence into a factory pass, or that
/// drops an unexecuted check instead of naming it.
/// </summary>
public sealed class FactoryAcceptanceToolsTests
{
    private const string CanonicalQueryVersion =
        "MES_TASK_UNION/sha256:54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae";

    private const string CanonicalQuerySha256 =
        "54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae";

    /// <summary>An offline or simulated probe cannot become a live plant pass.</summary>
    [Fact]
    public void Probe_state_downgrades_a_passed_result_that_never_reached_oracle()
    {
        var probe = ProbeLog(
            scope: "OFFLINE_ARTIFACT_ONLY",
            connectionAttempted: "false",
            actualMode: "NOT_AVAILABLE",
            outcome: "Success",
            result: "PASSED");

        var state = ReadProbeState(probe, exitCode: 0);

        Assert.Equal("NOT_EXECUTED", state.Result);
        Assert.False(state.ConnectionAttempted);
    }

    /// <summary>
    /// A live round that executed some other statement is a failure, not a pass: the
    /// acceptance claim is about one approved six-branch UNION ALL, by hash.
    /// </summary>
    [Fact]
    public void Probe_state_fails_when_the_executed_query_is_not_the_canonical_artifact()
    {
        var probe = ProbeLog(
            scope: "LIVE_ORACLE",
            connectionAttempted: "true",
            actualMode: "Thin",
            outcome: "Success",
            result: "PASSED",
            querySha256: new string('0', 64));

        var state = ReadProbeState(probe, exitCode: 0);

        Assert.Equal("FAILED", state.Result);
    }

    /// <summary>A non-zero probe exit code cannot be read as a pass whatever the log says.</summary>
    [Fact]
    public void Probe_state_fails_when_the_probe_process_did_not_exit_successfully()
    {
        var probe = ProbeLog(
            scope: "LIVE_ORACLE",
            connectionAttempted: "true",
            actualMode: "Thin",
            outcome: "Success",
            result: "PASSED");

        var state = ReadProbeState(probe, exitCode: 2);

        Assert.Equal("FAILED", state.Result);
    }

    /// <summary>The one shape that may be recorded as live plant evidence.</summary>
    [Fact]
    public void Probe_state_passes_a_live_thin_success_on_the_canonical_query()
    {
        var probe = ProbeLog(
            scope: "LIVE_ORACLE",
            connectionAttempted: "true",
            actualMode: "Thin",
            outcome: "Success",
            result: "PASSED",
            rowCount: 128);

        var state = ReadProbeState(probe, exitCode: 0);

        Assert.Equal("PASSED", state.Result);
        Assert.True(state.ConnectionAttempted);
        Assert.Equal("LIVE_ORACLE", state.ExecutionScope);
        Assert.Equal(128, state.RowCount);
    }

    /// <summary>
    /// Evidence leaves the plant. Credentials, bearer tokens and the Oracle descriptor
    /// host must not travel with it, including inside a provider error message.
    /// </summary>
    [Fact]
    public void Redaction_removes_credentials_datasource_and_bearer_tokens()
    {
        var raw = string.Join(
            "\n",
            "Authorization: Bearer 7f3c9a1b5d",
            "User Id=fwmes;Password=fwmes;Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=172.19.1.152)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=SQMES)))",
            "Server=LAB-WIN-01;Initial Catalog=MesIngest_Factory;SharedSecret=abcdef");

        var rawPath = WriteTemporary(raw);
        var output = RunTools(
            $"Protect-FactoryAcceptanceText -Text ([IO.File]::ReadAllText('{rawPath}'))");

        Assert.DoesNotContain("7f3c9a1b5d", output, StringComparison.Ordinal);
        Assert.DoesNotContain("fwmes", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("172.19.1.152", output, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef", output, StringComparison.Ordinal);
        Assert.DoesNotContain("MesIngest_Factory", output, StringComparison.Ordinal);
        Assert.Contains("[redacted]", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The read-only boundary is a property of the one artifact the Host is allowed to
    /// execute, so it is measured on the shipped SQL rather than asserted in prose.
    /// </summary>
    [Fact]
    public void Canonical_statement_is_one_read_only_six_branch_union_all()
    {
        var query = Path.Combine(
            RepositoryPaths.CSharpRoot, "..", "..", "queries", "mes-task-union", "query.sql");
        Assert.True(File.Exists(query), $"Missing canonical query: {query}");

        var output = RunTools(
            $"$r = Test-CanonicalReadOnlyStatement -Sql ([IO.File]::ReadAllText('{Escape(query)}')) " +
            $"-ExpectedSha256 '{CanonicalQuerySha256}'; " +
            "Write-Output \"readOnly=$($r.ReadOnly) branches=$($r.TaskTypeBranchCount) " +
            "unionAll=$($r.UnionAllCount) statements=$($r.StatementCount) approved=$($r.MatchesApprovedArtifact) " +
            "writes=$($r.WriteKeywordsFound.Count)\"");

        Assert.Contains(
            "readOnly=True branches=6 unionAll=5 statements=1 approved=True writes=0",
            output,
            StringComparison.Ordinal);
    }

    /// <summary>A statement carrying a write is never approved, whatever else matches.</summary>
    [Fact]
    public void Canonical_statement_check_rejects_a_write_bearing_statement()
    {
        var sqlPath = WriteTemporary("SELECT 1 FROM DUAL;\nDELETE FROM FWMES.TASKS");
        var output = RunTools(
            $"$r = Test-CanonicalReadOnlyStatement -Sql ([IO.File]::ReadAllText('{sqlPath}')) " +
            $"-ExpectedSha256 '{CanonicalQuerySha256}'; " +
            "Write-Output \"readOnly=$($r.ReadOnly) writes=$($r.WriteKeywordsFound -join ',')\"");

        Assert.Contains("readOnly=False", output, StringComparison.Ordinal);
        Assert.Contains("DELETE", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A round source configured with a recording drives the same production entry, which
    /// is exactly why it must be refused here: replayed rounds are release-smoke evidence
    /// and can never be factory acceptance.
    /// </summary>
    [Fact]
    public void Live_round_source_configuration_refuses_a_configured_recording()
    {
        var settings = WriteTemporary(
            "{\"MesIngest\":{\"SnapshotSource\":\"Oracle\",\"OracleMode\":\"Thin\"," +
            "\"ReplayRoundsFromRecordingPath\":\"rounds.json\"}}");

        var output = RunTools(
            $"try {{ Assert-LiveRoundSourceConfiguration -SettingsPath '{settings}' -ExpectedMode Thin }} " +
            "catch { Write-Output $_.Exception.Message }");

        Assert.Contains("RECORDED_ROUNDS_ARE_NOT_FACTORY_EVIDENCE", output, StringComparison.Ordinal);
    }

    /// <summary>A source that is not the live Oracle entry cannot be accepted either.</summary>
    [Fact]
    public void Live_round_source_configuration_refuses_a_non_oracle_snapshot_source()
    {
        var settings = WriteTemporary("{\"MesIngest\":{\"SnapshotSource\":\"None\",\"OracleMode\":\"Thin\"}}");

        var output = RunTools(
            $"try {{ Assert-LiveRoundSourceConfiguration -SettingsPath '{settings}' -ExpectedMode Thin }} " +
            "catch { Write-Output $_.Exception.Message }");

        Assert.Contains("LIVE_ORACLE_ROUND_SOURCE_REQUIRED", output, StringComparison.Ordinal);
    }

    /// <summary>The accepted configuration returns the mode and timeout it will record.</summary>
    [Fact]
    public void Live_round_source_configuration_accepts_thin_oracle_and_reports_the_command_timeout()
    {
        var settings = WriteTemporary(
            "{\"MesIngest\":{\"SnapshotSource\":\"Oracle\",\"OracleMode\":\"Thin\"," +
            "\"OracleCommandTimeoutSeconds\":45}}");

        var output = RunTools(
            $"$c = Assert-LiveRoundSourceConfiguration -SettingsPath '{settings}' -ExpectedMode Thin; " +
            "Write-Output \"mode=$($c.Mode) timeout=$($c.CommandTimeoutSeconds)\"");

        Assert.Contains("mode=Thin timeout=45", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A skip is a reviewable commitment: who owns it, what environment it needs, and
    /// which release gate stays open. A skip without those is indistinguishable from an
    /// omission, so the tools refuse to produce one.
    /// </summary>
    [Fact]
    public void A_named_skip_without_owner_and_prerequisite_is_refused()
    {
        var output = RunTools(
            "try { New-AcceptanceCheck -Id 'LIVE_ORACLE_THICK_MODE' -Title 'Thick' " +
            "-Gate 'FACTORY_ORACLE_ACCEPTANCE' -Status SKIPPED -Detail 'not run' } " +
            "catch { Write-Output $_.Exception.Message }");

        Assert.Contains("NAMED_SKIP_REQUIRES_OWNER_AND_PREREQUISITE", output, StringComparison.Ordinal);
    }

    /// <summary>A failure has to carry what failed, or it cannot be triaged on site.</summary>
    [Fact]
    public void A_failed_check_without_detail_is_refused()
    {
        var output = RunTools(
            "try { New-AcceptanceCheck -Id 'SQLSERVER_RESTART_CONSISTENCY' -Title 'Restart' " +
            "-Gate 'FACTORY_SQLSERVER_ACCEPTANCE' -Status FAILED -Detail '' } " +
            "catch { Write-Output $_.Exception.Message }");

        Assert.Contains("FAILED_CHECK_REQUIRES_DETAIL", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The summary is built from a declared checklist. A check that never ran and was
    /// never named cannot leave the summary silently smaller.
    /// </summary>
    [Fact]
    public void Summary_refuses_to_close_while_a_declared_check_has_no_result()
    {
        var output = RunTools(
            "$checks = @(New-AcceptanceCheck -Id 'A' -Title 'a' -Gate 'G' -Status PASSED -Detail 'ok'); " +
            "try { New-FactoryAcceptanceSummary -Checks $checks -ExpectedCheckIds @('A','B') " +
            "-RollbackReadiness 'READY' } catch { Write-Output $_.Exception.Message }");

        Assert.Contains("UNREPORTED_ACCEPTANCE_CHECK: B", output, StringComparison.Ordinal);
    }

    /// <summary>Named skips are reported apart from passes; they never join the pass list.</summary>
    [Fact]
    public void Summary_separates_passes_named_skips_and_failures()
    {
        var output = RunTools(
            "$checks = @(" +
            "(New-AcceptanceCheck -Id 'A' -Title 'a' -Gate 'G' -Status PASSED -Detail 'ok')," +
            "(New-AcceptanceCheck -Id 'B' -Title 'b' -Gate 'G' -Status SKIPPED -Detail 'no client' " +
            "-Owner 'plant IT' -Requires 'Oracle Instant Client')," +
            "(New-AcceptanceCheck -Id 'C' -Title 'c' -Gate 'G' -Status FAILED -Detail 'boom')); " +
            "$s = New-FactoryAcceptanceSummary -Checks $checks -ExpectedCheckIds @('A','B','C') " +
            "-RollbackReadiness 'READY'; " +
            "Write-Output \"status=$($s.status) passed=$($s.passed.Count) skipped=$($s.namedSkips.Count) " +
            "failed=$($s.failed.Count)\"");

        Assert.Contains("status=FAILED passed=1 skipped=1 failed=1", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no failure but an outstanding named skip, the run is not a plain pass. The
    /// distinct status is what stops "passed" from being read as "everything ran".
    /// </summary>
    [Fact]
    public void Summary_with_only_named_skips_is_not_reported_as_a_plain_pass()
    {
        var output = RunTools(
            "$checks = @(" +
            "(New-AcceptanceCheck -Id 'A' -Title 'a' -Gate 'G' -Status PASSED -Detail 'ok')," +
            "(New-AcceptanceCheck -Id 'B' -Title 'b' -Gate 'G' -Status SKIPPED -Detail 'no desktop' " +
            "-Owner 'plant operator' -Requires 'interactive desktop')); " +
            "$s = New-FactoryAcceptanceSummary -Checks $checks -ExpectedCheckIds @('A','B') " +
            "-RollbackReadiness 'READY'; Write-Output \"status=$($s.status)\"");

        Assert.Contains("status=PASSED_WITH_NAMED_SKIPS", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A round that failed or came back structurally incomplete must not be allowed to
    /// look like a Demand disappearance. The projection identity has to be unchanged
    /// across it, and the check says so rather than the operator assuming it.
    /// </summary>
    [Fact]
    public void Non_success_rounds_that_moved_the_projection_are_reported_as_a_defect()
    {
        var rounds = WriteTemporary(
            "[{\"pollTraceId\":\"a\",\"outcome\":\"SUCCESS\",\"catalogRevision\":10,\"demandCount\":4}," +
            "{\"pollTraceId\":\"b\",\"outcome\":\"FAILURE\",\"catalogRevision\":11,\"demandCount\":2}]");

        var output = RunTools(
            $"$r = Test-NonSuccessRoundsPreservedProjection -Rounds " +
            $"(Get-Content -Raw -LiteralPath '{rounds}' | ConvertFrom-Json); " +
            "Write-Output \"preserved=$($r.Preserved) offenders=$($r.Offenders -join ',')\"");

        Assert.Contains("preserved=False", output, StringComparison.Ordinal);
        Assert.Contains("offenders=b", output, StringComparison.Ordinal);
    }

    /// <summary>A failed round that left the projection alone is normal plant evidence.</summary>
    [Fact]
    public void Non_success_rounds_that_left_the_projection_alone_are_accepted()
    {
        var rounds = WriteTemporary(
            "[{\"pollTraceId\":\"a\",\"outcome\":\"SUCCESS\",\"catalogRevision\":10,\"demandCount\":4}," +
            "{\"pollTraceId\":\"b\",\"outcome\":\"FAILURE\",\"catalogRevision\":10,\"demandCount\":4}," +
            "{\"pollTraceId\":\"c\",\"outcome\":\"SUCCESS\",\"catalogRevision\":11,\"demandCount\":5}]");

        var output = RunTools(
            $"$r = Test-NonSuccessRoundsPreservedProjection -Rounds " +
            $"(Get-Content -Raw -LiteralPath '{rounds}' | ConvertFrom-Json); " +
            "Write-Output \"preserved=$($r.Preserved) nonSuccess=$($r.NonSuccessCount)\"");

        Assert.Contains("preserved=True nonSuccess=1", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plant copy has to be the ticket 25 artifact, byte for byte. Identity is
    /// verified against the manifest inventory, and an edited file must break it.
    /// </summary>
    [Fact]
    public void Package_identity_detects_a_file_that_differs_from_the_release_manifest()
    {
        var package = Path.Combine(Path.GetTempPath(), "mes-ingest-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        try
        {
            var payload = Path.Combine(package, "service");
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "kept.txt"), "kept");
            File.WriteAllText(Path.Combine(payload, "edited.txt"), "original");

            var manifest = Path.Combine(package, "RELEASE-MANIFEST.json");
            var runManifest = RunTools(
                "$files = @(Get-ChildItem -LiteralPath '" + Escape(package) + "' -File -Recurse | " +
                "ForEach-Object { [ordered]@{ path = $_.FullName.Substring(" + package.Length + ")" +
                ".TrimStart('\\','/').Replace('\\','/'); length = $_.Length; " +
                "sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } }); " +
                "[ordered]@{ schemaVersion = 3; sourceCommit = 'deadbeef'; sourceDirty = $false; " +
                "files = $files } | ConvertTo-Json -Depth 6 | " +
                "Set-Content -LiteralPath '" + Escape(manifest) + "' -Encoding UTF8; Write-Output 'WROTE'");
            Assert.Contains("WROTE", runManifest, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(payload, "edited.txt"), "tampered");

            var output = RunTools(
                $"$r = Test-PackageIdentity -PackageRoot '{Escape(package)}'; " +
                "Write-Output \"identical=$($r.Identical) mismatched=$($r.Mismatched -join ',') " +
                "commit=$($r.SourceCommit)\"");

            Assert.Contains("identical=False", output, StringComparison.Ordinal);
            Assert.Contains("service/edited.txt", output, StringComparison.Ordinal);
            Assert.Contains("commit=deadbeef", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    /// <summary>
    /// A run can die between checks — a Host that will not start, a target that goes away.
    /// The checks it never reached are neither passes nor named skips, so they close as red
    /// with the abort reason rather than vanishing from the summary.
    /// </summary>
    [Fact]
    public void An_aborted_run_closes_its_unreached_checks_as_red_evidence()
    {
        var output = RunTools(
            "$checks = @((New-AcceptanceCheck -Id 'A' -Title 'a' -Gate 'G' -Status PASSED -Detail 'ok')); " +
            "$all = Complete-AbortedAcceptanceChecks -Checks $checks -ExpectedCheckIds @('A','B','C') " +
            "-Reason 'the packaged Host exited during startup'; " +
            "$s = New-FactoryAcceptanceSummary -Checks $all -ExpectedCheckIds @('A','B','C') " +
            "-RollbackReadiness 'READY'; " +
            "Write-Output \"status=$($s.status) failed=$($s.failed.Count) gate=$($s.failed[0].gate)\"; " +
            "Write-Output $s.failed[0].detail");

        Assert.Contains("status=FAILED failed=2 gate=FACTORY_RUN_ABORTED", output, StringComparison.Ordinal);
        Assert.Contains("the packaged Host exited during startup", output, StringComparison.Ordinal);
    }

    /// <summary>The shipped package has to carry the acceptance entry point and its tools.</summary>
    [Fact]
    public void Release_package_requires_the_factory_acceptance_entry_point()
    {
        var validation = Path.Combine(RepositoryPaths.CSharpRoot, "pack", "validation");
        Assert.True(File.Exists(Path.Combine(validation, "Invoke-FactoryAcceptance.ps1")));
        Assert.True(File.Exists(Path.Combine(validation, "FactoryAcceptanceTools.ps1")));

        var validator = File.ReadAllText(
            Path.Combine(RepositoryPaths.CSharpRoot, "pack", "Test-ReleasePackage.ps1"));
        Assert.Contains("validation\\Invoke-FactoryAcceptance.ps1", validator, StringComparison.Ordinal);
        Assert.Contains("validation\\FactoryAcceptanceTools.ps1", validator, StringComparison.Ordinal);
    }

    private static ProbeState ReadProbeState(string probePath, int exitCode)
    {
        var output = RunTools(
            $"$s = Get-LiveOracleProbeState -ExpectedMode Thin " +
            $"-ProbeOutput ([IO.File]::ReadAllText('{probePath}')) " +
            $"-ExpectedQueryVersion '{CanonicalQueryVersion}' " +
            $"-ExpectedQuerySha256 '{CanonicalQuerySha256}' -ExitCode {exitCode}; " +
            "Write-Output \"RESULT|$($s.Result)|$($s.ExecutionScope)|$($s.ConnectionAttempted)|$($s.RowCount)\"");

        var line = output
            .Split('\n')
            .Select(candidate => candidate.Trim())
            .FirstOrDefault(candidate => candidate.StartsWith("RESULT|", StringComparison.Ordinal));
        Assert.True(line is not null, $"The tools produced no probe state. Output:\n{output}");

        var parts = line!.Split('|');
        return new ProbeState(
            parts[1],
            parts[2],
            bool.Parse(parts[3]),
            int.Parse(parts[4]));
    }

    private sealed record ProbeState(
        string Result,
        string ExecutionScope,
        bool ConnectionAttempted,
        int RowCount);

    private static string ProbeLog(
        string scope,
        string connectionAttempted,
        string actualMode,
        string outcome,
        string result,
        string? querySha256 = null,
        int rowCount = 0)
    {
        var sha = querySha256 ?? CanonicalQuerySha256;
        return WriteTemporary(string.Join(
            "\n",
            "MesIngest Oracle MES_TASK_UNION round probe",
            $"execution_scope={scope}",
            $"connection_attempted={connectionAttempted}",
            "requested_mode=Thin",
            $"actual_mode={actualMode}",
            "driver=Oracle.ManagedDataAccess.Core",
            "query_id=MES_TASK_UNION",
            $"query_version=MES_TASK_UNION/sha256:{sha}",
            $"query_sha256={sha}",
            "duration_ms=412",
            $"outcome={outcome}",
            $"row_count={rowCount}",
            $"result={result}"));
    }

    private static string WriteTemporary(string content)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "mes-ingest-acceptance-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return Escape(path);
    }

    private static string Escape(string path) => path.Replace("'", "''");

    private static string RunTools(string script)
    {
        var tools = Path.Combine(
            RepositoryPaths.CSharpRoot, "pack", "validation", "FactoryAcceptanceTools.ps1");
        return RunPowerShell($". '{Escape(tools)}'; {script}");
    }

    private static string RunPowerShell(string script)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.CSharpRoot,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo);
        Assert.True(process is not null, "pwsh did not start; the shipped acceptance tools cannot be exercised.");

        var output = process!.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return output;
    }
}
