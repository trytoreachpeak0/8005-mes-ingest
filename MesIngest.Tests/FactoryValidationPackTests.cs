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

    private static string MesRoot
    {
        get
        {
            var csharp = new DirectoryInfo(CSharpRoot);
            // mes/ingest/csharp -> mes
            return csharp.Parent!.Parent!.FullName;
        }
    }

    [Fact]
    public void Pack_includes_factory_validation_checklist_and_return_templates()
    {
        Assert.True(File.Exists(Path.Combine(PackRoot, "FACTORY-VALIDATION.md")));
        Assert.True(File.Exists(Path.Combine(ValidationRoot, "run-manifest.example.json")));
        Assert.True(File.Exists(Path.Combine(ValidationRoot, "execution-log.md")));
        Assert.True(File.Exists(Path.Combine(ValidationRoot, "RETURN-CHECKLIST.md")));
        Assert.True(File.Exists(Path.Combine(ValidationRoot, "signoff.md")));
    }

    [Fact]
    public void Factory_validation_checklist_covers_probe_service_wpf_and_signoff_split()
    {
        var text = File.ReadAllText(Path.Combine(PackRoot, "FACTORY-VALIDATION.md"));

        Assert.Contains("appsettings.Local.json", text, StringComparison.Ordinal);
        Assert.Contains("--probe-oracle", text, StringComparison.Ordinal);
        Assert.Contains("Thin", text, StringComparison.Ordinal);
        Assert.Contains("Thick", text, StringComparison.Ordinal);
        Assert.Contains("poll-health", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VISIBLE", text, StringComparison.Ordinal);
        Assert.Contains("/api/alerts", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PAUSED_ZERO_DROP", text, StringComparison.Ordinal);
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
    public void Run_manifest_example_is_mes_ingest_validation_shape_without_password_fields()
    {
        var path = Path.Combine(ValidationRoot, "run-manifest.example.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.Equal("mes-ingest-factory-validation", root.GetProperty("experiment_id").GetString());
        Assert.True(root.TryGetProperty("probe", out _));
        Assert.True(root.TryGetProperty("poll_rounds", out var rounds));
        Assert.Equal(JsonValueKind.Array, rounds.ValueKind);
        Assert.True(rounds.GetArrayLength() >= 1);

        var first = rounds[0];
        Assert.True(first.TryGetProperty("duration_ms", out _));
        Assert.True(first.TryGetProperty("row_count", out _));
        Assert.True(first.TryGetProperty("success", out _));

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
