using System.Text.Json;

namespace MesIngest.Tests;

/// <summary>
/// Supporting smoke for ticket 11 factory validation pack (not a formal product seam).
/// </summary>
public class FactoryValidationPackTests
{
    private static string CSharpRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var hostProj = Path.Combine(dir.FullName, "MesIngest.Host", "MesIngest.Host.csproj");
                if (File.Exists(hostProj))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("Could not locate mes/ingest/csharp root from test base directory.");
        }
    }

    private static string PackRoot => Path.Combine(CSharpRoot, "pack");

    private static string ValidationRoot => Path.Combine(PackRoot, "validation");

    // Since the 2026-09-02 split this repository *is* the MES ingest root:
    // queries/, experiments/ and evidence/ sit beside the projects rather than
    // two levels up in the old mes/ingest/csharp monorepo layout.
    private static string MesRoot => CSharpRoot;

    [Fact]
    public void Pack_includes_factory_validation_checklist_and_return_templates()
    {
        Assert.True(File.Exists(Path.Combine(PackRoot, "FACTORY-VALIDATION.md")));
        Assert.True(File.Exists(Path.Combine(ValidationRoot, "run-manifest.example.json")));
        Assert.True(File.Exists(Path.Combine(ValidationRoot, "execution-log.md")));
        Assert.True(File.Exists(Path.Combine(ValidationRoot, "RETURN-CHECKLIST.md")));
        Assert.True(File.Exists(Path.Combine(ValidationRoot, "signoff.md")));
        Assert.True(File.Exists(Path.Combine(ValidationRoot, "Invoke-FactoryValidation.ps1")));
    }

    /// <summary>
    /// Ticket 24: the validation guidance must keep the three gate families apart and
    /// force a named skip for whichever one did not actually run, so a local or golden
    /// machine pass can never be written up as a factory pass.
    /// </summary>
    [Fact]
    public void Validation_guidance_separates_evidence_tiers_and_demands_named_skips()
    {
        var checklist = File.ReadAllText(Path.Combine(PackRoot, "FACTORY-VALIDATION.md"));
        var returnChecklist = File.ReadAllText(Path.Combine(ValidationRoot, "RETURN-CHECKLIST.md"));

        Assert.Contains("证据分级", checklist, StringComparison.Ordinal);
        Assert.Contains("黄金机", checklist, StringComparison.Ordinal);
        Assert.Contains("真实兼容 SQL Server 门禁", checklist, StringComparison.Ordinal);
        Assert.Contains("工厂 Oracle 验收", checklist, StringComparison.Ordinal);
        Assert.Contains("具名 skip", checklist, StringComparison.Ordinal);
        Assert.Contains("FILE_REPLAY", checklist, StringComparison.Ordinal);
        Assert.Contains("live_oracle_probe_passed", checklist, StringComparison.Ordinal);
        Assert.Contains("--probe-oracle", checklist, StringComparison.Ordinal);
        // Real WPF on the golden machine goes through the interactive scheduled task;
        // PowerShell Direct stays a deployment and evidence-retrieval channel.
        Assert.Contains("gpt_win11", checklist, StringComparison.Ordinal);
        Assert.Contains("交互计划任务", checklist, StringComparison.Ordinal);
        Assert.Contains("PowerShell Direct", checklist, StringComparison.Ordinal);

        Assert.Contains("具名 skip", returnChecklist, StringComparison.Ordinal);
        Assert.Contains("PACKAGED_WATCH_PROCESS_INDEPENDENCE_AND_STARTUP_BUDGET", returnChecklist, StringComparison.Ordinal);
        Assert.Contains("证据分级", returnChecklist, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_validation_checklist_covers_probe_service_wpf_and_signoff_split()
    {
        var text = File.ReadAllText(Path.Combine(PackRoot, "FACTORY-VALIDATION.md"));

        Assert.Contains("appsettings.Local.json", text, StringComparison.Ordinal);
        Assert.Contains("--probe-oracle", text, StringComparison.Ordinal);
        Assert.Contains("Thin", text, StringComparison.Ordinal);
        Assert.Contains("Thick", text, StringComparison.Ordinal);
        Assert.Contains("/api/v2/poll-traces", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VISIBLE", text, StringComparison.Ordinal);
        Assert.Contains("/api/v2/current-ingest-attention", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("canonical_poll_trace_identity_complete", text, StringComparison.Ordinal);
        Assert.Contains("live_oracle_probe_passed", text, StringComparison.Ordinal);
        Assert.Contains("WPF", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("关闭", text, StringComparison.Ordinal);
        Assert.Contains("验证包已就绪", text, StringComparison.Ordinal);
        Assert.Contains("工厂已签字通过", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Return_checklist_forbids_password_bearing_config_and_requires_metrics()
    {
        var text = File.ReadAllText(Path.Combine(ValidationRoot, "RETURN-CHECKLIST.md"));

        Assert.Contains("脱敏", text, StringComparison.Ordinal);
        Assert.Contains("manifest", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("耗时", text, StringComparison.Ordinal);
        Assert.Contains("行数", text, StringComparison.Ordinal);
        Assert.Contains("appsettings.Local.json", text, StringComparison.Ordinal);
        Assert.Contains("密码", text, StringComparison.Ordinal);
        Assert.Contains("禁止", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_validation_collector_covers_ticket_15_read_only_measurements()
    {
        var script = File.ReadAllText(Path.Combine(ValidationRoot, "Invoke-FactoryValidation.ps1"));

        Assert.Contains("ValidateSet(\"A\", \"B\", \"C\")", script, StringComparison.Ordinal);
        Assert.Contains("RequestTimeoutSeconds", script, StringComparison.Ordinal);
        Assert.Contains("PollSampleCount", script, StringComparison.Ordinal);
        Assert.Contains("PollSampleWaitTimeoutSeconds", script, StringComparison.Ordinal);
        Assert.Contains("pollTraceHighWater", script, StringComparison.Ordinal);
        Assert.Contains("DISTINCT_POLL_ROUNDS", script, StringComparison.Ordinal);
        Assert.Contains("X-Correlation-Id", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Authorization", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SharedSecretEnvironmentVariable", script, StringComparison.Ordinal);
        Assert.Contains("/api/v2/contract", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v2/poll-traces/", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v2/demand-series", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v2/current-ingest-attention", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v2/externally-readable-demand-catalog", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CarrySnapshotReference", script, StringComparison.Ordinal);
        Assert.Contains("&snapshot=", script, StringComparison.Ordinal);
        Assert.Contains("PageParameter \"pageNumber\"", script, StringComparison.Ordinal);
        Assert.Contains("dates-samples.tsv", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("watch-latency", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Get-WinEvent", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CANONICAL_POLL_TRACE_IDENTITY", script, StringComparison.Ordinal);
        Assert.Contains("LIVE_ORACLE_PROBE_PASSED", script, StringComparison.Ordinal);
        Assert.Contains("@($thinProbeEvidence, $thickProbeEvidence) | Where-Object", script, StringComparison.Ordinal);
        Assert.Contains("canonical_poll_trace_identity_complete", script, StringComparison.Ordinal);
        Assert.Contains("live_oracle_probe_passed", script, StringComparison.Ordinal);
        Assert.DoesNotContain("missingRequiredEvidence.Add(\"ORACLE_QUERY\")", script, StringComparison.Ordinal);
        Assert.DoesNotContain("missingRequiredEvidence.Add(\"SQL_QUERY\")", script, StringComparison.Ordinal);
        Assert.DoesNotContain("missingRequiredEvidence.Add(\"SQL_WRITE\")", script, StringComparison.Ordinal);
        Assert.Contains("technical-capture-incomplete", script, StringComparison.Ordinal);
        Assert.Contains("missing_required_evidence", script, StringComparison.Ordinal);
        Assert.Contains("authenticated_v2_get_confirmed = $false", script, StringComparison.Ordinal);
        Assert.Contains("GET", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Sqlcmd", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OracleCommand", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Post", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Put", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Delete", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Factory_validation_collector_imports_thin_and_thick_probe_evidence_without_promoting_offline_runs()
    {
        var script = File.ReadAllText(Path.Combine(ValidationRoot, "Invoke-FactoryValidation.ps1"));

        Assert.Contains("ThinProbeLog", script, StringComparison.Ordinal);
        Assert.Contains("ThickProbeLog", script, StringComparison.Ordinal);
        Assert.Contains("Import-OracleProbeEvidence", script, StringComparison.Ordinal);
        Assert.Contains("execution_scope", script, StringComparison.Ordinal);
        Assert.Contains("connection_attempted", script, StringComparison.Ordinal);
        Assert.Contains("requested_mode", script, StringComparison.Ordinal);
        Assert.Contains("actual_mode", script, StringComparison.Ordinal);
        Assert.Contains("query_version", script, StringComparison.Ordinal);
        Assert.Contains("query_sha256", script, StringComparison.Ordinal);
        Assert.Contains("NOT_EXECUTED", script, StringComparison.Ordinal);
        Assert.Contains("LIVE_ORACLE", script, StringComparison.Ordinal);
        Assert.Contains("cannot be recorded as PASSED", script, StringComparison.Ordinal);
        Assert.Contains("thin = $thinProbeEvidence", script, StringComparison.Ordinal);
        Assert.Contains("thick = $thickProbeEvidence", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_manifest_example_is_mes_ingest_validation_shape_without_password_fields()
    {
        var path = Path.Combine(ValidationRoot, "run-manifest.example.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.Equal("mes-ingest-factory-validation", root.GetProperty("experiment_id").GetString());
        Assert.True(root.TryGetProperty("probe", out _));
        Assert.True(root.TryGetProperty("formal_source_evidence", out var formalSourceEvidence));
        Assert.False(formalSourceEvidence.GetProperty("canonical_poll_trace_identity_complete").GetBoolean());
        Assert.False(formalSourceEvidence.GetProperty("live_oracle_probe_passed").GetBoolean());
        Assert.True(root.TryGetProperty("poll_rounds", out var rounds));
        Assert.Equal(JsonValueKind.Array, rounds.ValueKind);
        Assert.True(rounds.GetArrayLength() >= 1);

        var first = rounds[0];
        Assert.True(first.TryGetProperty("duration_ms", out _));
        Assert.True(first.TryGetProperty("row_count", out _));
        Assert.True(first.TryGetProperty("success", out _));
        Assert.Equal(
            "MES_TASK_UNION/sha256:54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae",
            first.GetProperty("query_version").GetString());
        Assert.True(first.TryGetProperty("content_digest", out _));
        Assert.True(first.TryGetProperty("poll_trace_id", out _));

        Assert.True(root.TryGetProperty("request_metrics", out _));
        Assert.True(root.TryGetProperty("dates_semantics", out _));
        Assert.True(root.TryGetProperty("latency_evidence", out _));

        var manualChecks = root.GetProperty("manual_checks");
        Assert.False(manualChecks.GetProperty("authenticated_v2_get_confirmed").GetBoolean());
        Assert.False(manualChecks.GetProperty("dates_semantics_confirmed").GetBoolean());

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OraclePassword", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Publish_script_stages_factory_validation_artifacts()
    {
        var script = File.ReadAllText(Path.Combine(PackRoot, "Publish-MesIngest.ps1"));
        Assert.Contains("FACTORY-VALIDATION.md", script, StringComparison.Ordinal);
        Assert.Contains("validation", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_doc_points_at_factory_validation_pack()
    {
        var install = File.ReadAllText(Path.Combine(PackRoot, "INSTALL.md"));
        Assert.Contains("FACTORY-VALIDATION.md", install, StringComparison.Ordinal);
    }

    [Fact]
    public void Repo_has_experiment_plan_and_evidence_import_guidance()
    {
        var plan = Path.Combine(MesRoot, "experiments", "definitions", "mes-ingest-factory-validation", "plan.md");
        Assert.True(File.Exists(plan));

        var planText = File.ReadAllText(plan);
        Assert.Contains("mes-ingest-factory-validation", planText, StringComparison.Ordinal);
        Assert.Contains("evidence/runs", planText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("import-run", planText, StringComparison.OrdinalIgnoreCase);

        var evidenceReadme = File.ReadAllText(Path.Combine(MesRoot, "evidence", "README.md"));
        Assert.Contains("MesIngest", evidenceReadme, StringComparison.Ordinal);
        Assert.Contains("mes-ingest-factory-validation", evidenceReadme, StringComparison.Ordinal);

        var experimentsReadme = File.ReadAllText(Path.Combine(MesRoot, "experiments", "README.md"));
        Assert.Contains("mes-ingest-factory-validation", experimentsReadme, StringComparison.Ordinal);
    }
}
