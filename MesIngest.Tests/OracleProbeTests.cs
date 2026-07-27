using System.Text;
using MesIngest.Core;
using MesIngest.Host;

namespace MesIngest.Tests;

public class OracleProbeTests
{
    [Fact]
    public async Task Success_returns_exit_zero_without_password()
    {
        var source = new FixedMesSnapshotSource(MesSnapshotOutcome.Success([]));
        var output = new StringWriter();

        var code = await OracleProbe.RunAsync(
            source,
            output,
            OracleClientMode.Thin,
            instantClientOnPath: false);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("probe_result=ok", text);
        Assert.Contains("requested_mode=Thin", text);
        Assert.Contains("driver_kind=managed", text);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failure_outcome_returns_nonzero()
    {
        var source = new FixedMesSnapshotSource(MesSnapshotOutcome.Failure());
        var output = new StringWriter();

        var code = await OracleProbe.RunAsync(source, output, OracleClientMode.Thick);

        Assert.Equal(2, code);
        Assert.Contains("probe_result=failed", output.ToString());
    }

    [Fact]
    public async Task Exception_sanitizes_password_and_returns_nonzero()
    {
        var source = new ThrowingSource(
            new InvalidOperationException("User Id=mes;Password=SuperSecret123;Data Source=host/orcl"));
        var output = new StringWriter();

        var code = await OracleProbe.RunAsync(source, output, OracleClientMode.Thin);

        Assert.Equal(1, code);
        var text = output.ToString();
        Assert.Contains("probe_result=failed", text);
        Assert.DoesNotContain("SuperSecret123", text);
        Assert.Contains("Password=***", text);
    }

    [Fact]
    public void Published_queries_match_repo_mes_task_union_manuscript()
    {
        var published = Path.Combine(
            AppContext.BaseDirectory, "queries", "mes-task-union", "query.sql");
        Assert.True(File.Exists(published), $"Missing published SQL at {published}");

        var manuscript = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "queries", "mes-task-union", "query.sql"));
        Assert.True(File.Exists(manuscript), $"Missing manuscript at {manuscript}");

        var publishedText = File.ReadAllText(published, Encoding.UTF8);
        var manuscriptText = File.ReadAllText(manuscript, Encoding.UTF8);
        Assert.Equal(manuscriptText, publishedText);
        Assert.Contains("UNION ALL", publishedText, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingSource : IMesSnapshotSource
    {
        private readonly Exception _error;

        public ThrowingSource(Exception error) => _error = error;

        public Task<MesSnapshotOutcome> ReadAsync(CancellationToken cancellationToken = default) =>
            throw _error;
    }
}
