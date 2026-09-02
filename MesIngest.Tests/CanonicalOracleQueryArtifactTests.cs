using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MesIngest.Core;

namespace MesIngest.Tests;

public sealed class CanonicalOracleQueryArtifactTests
{
    [Fact]
    public void Source_build_output_and_runtime_artifact_have_identical_raw_sha256()
    {
        var sourcePath = FindRepositoryQuery();
        var runtimePath = Path.Combine(
            AppContext.BaseDirectory,
            "queries",
            "mes-task-union",
            "query.sql");

        var source = CanonicalMesTaskUnionQuery.Load(sourcePath);
        var runtime = CanonicalMesTaskUnionQuery.Load(runtimePath);

        Assert.Equal(CanonicalMesTaskUnionQuery.ExpectedSha256, source.Sha256);
        Assert.Equal(source.Sha256, runtime.Sha256);
        Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(runtimePath));
        Assert.Equal($"MES_TASK_UNION/sha256:{source.Sha256}", source.QueryVersion);
    }

    [Fact]
    public void Sublot_box_count_source_build_output_and_runtime_artifact_have_identical_raw_sha256()
    {
        var sourcePath = FindRepositoryQuery("sublot-box-count");
        var runtimePath = Path.Combine(
            AppContext.BaseDirectory,
            "queries",
            "sublot-box-count",
            "query.sql");

        var source = CanonicalSublotBoxCountQuery.Load(sourcePath);
        var runtime = CanonicalSublotBoxCountQuery.Load(runtimePath);

        Assert.Equal(CanonicalSublotBoxCountQuery.ExpectedSha256, source.Sha256);
        Assert.Equal(source.Sha256, runtime.Sha256);
        Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(runtimePath));
        Assert.Equal($"SUBLOT_BOX_COUNT/sha256:{source.Sha256}", source.QueryVersion);
    }

    [Fact]
    public void Sublot_box_count_artifact_is_one_read_only_select_with_one_bound_parameter()
    {
        var artifact = CanonicalSublotBoxCountQuery.Load(FindRepositoryQuery("sublot-box-count"));
        var executableSql = StripCommentsAndQuotedLiterals(artifact.Sql);

        Assert.Matches("^\\s*SELECT\\b", executableSql);
        Assert.Single(
            Regex.Matches(executableSql, ":sublot", RegexOptions.IgnoreCase).Cast<Match>());
        Assert.False(
            executableSql.TrimEnd().EndsWith(';'),
            "ODP.NET rejects a SQL statement terminator with ORA-00911.");
        Assert.DoesNotMatch(
            "\\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|GRANT|REVOKE|EXEC|EXECUTE|CALL|BEGIN|COMMIT|ROLLBACK|SET)\\b",
            executableSql);
    }

    [Fact]
    public void Canonical_artifact_is_one_read_only_select_and_has_no_DML_DDL()
    {
        var artifact = CanonicalMesTaskUnionQuery.Load(FindRepositoryQuery());
        var executableSql = StripCommentsAndQuotedLiterals(artifact.Sql);

        Assert.Matches("^\\s*SELECT\\b", executableSql);
        Assert.Equal(5, Regex.Matches(executableSql, "\\bUNION\\s+ALL\\b", RegexOptions.IgnoreCase).Count);
        Assert.DoesNotMatch(
            "\\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|GRANT|REVOKE|EXEC|EXECUTE|CALL|BEGIN|COMMIT|ROLLBACK|SET)\\b",
            executableSql);
    }

    [Fact]
    public void Missing_empty_or_digest_mismatched_artifact_is_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mes-query-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var empty = Path.Combine(root, "empty.sql");
        var tampered = Path.Combine(root, "tampered.sql");
        File.WriteAllBytes(empty, []);
        File.WriteAllText(tampered, "SELECT 1 FROM DUAL");

        try
        {
            Assert.Equal(
                CanonicalQueryArtifactFailure.Missing,
                Assert.Throws<CanonicalQueryArtifactException>(
                    () => CanonicalMesTaskUnionQuery.Load(Path.Combine(root, "missing.sql"))).Failure);
            Assert.Equal(
                CanonicalQueryArtifactFailure.Empty,
                Assert.Throws<CanonicalQueryArtifactException>(
                    () => CanonicalMesTaskUnionQuery.Load(empty)).Failure);
            Assert.Equal(
                CanonicalQueryArtifactFailure.DigestMismatch,
                Assert.Throws<CanonicalQueryArtifactException>(
                    () => CanonicalMesTaskUnionQuery.Load(tampered)).Failure);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindRepositoryQuery()
        => FindRepositoryQuery("mes-task-union");

    private static string FindRepositoryQuery(string queryDirectory)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "queries",
                queryDirectory,
                "query.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate canonical MES_TASK_UNION query.sql.");
    }

    private static string StripCommentsAndQuotedLiterals(string sql) =>
        Regex.Replace(
            Regex.Replace(
                Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline),
                @"--[^\r\n]*",
                " "),
            @"'(?:''|[^'])*'",
            "''");
}
