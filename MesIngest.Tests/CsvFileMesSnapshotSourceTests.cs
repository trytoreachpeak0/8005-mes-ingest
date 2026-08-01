using System.Text;
using MesIngest.Core;

namespace MesIngest.Tests;

public class CsvFileMesSnapshotSourceTests
{
    [Fact]
    public async Task Reads_recorded_csv_and_interprets_dates_as_beijing()
    {
        var csv = """
            TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE
            STAGING_TO_WIRE,Q1-1,N01-01,EQ1,焊线,2026-08-01T10:00:00,PKG-A
            """;
        var path = Path.Combine(Path.GetTempPath(), $"mes-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        try
        {
            var source = new CsvFileMesSnapshotSource(path);
            var outcome = await source.ReadAsync();

            Assert.Equal(SnapshotOutcomeKind.Success, outcome.Kind);
            var row = Assert.Single(outcome.Rows);
            Assert.Equal("STAGING_TO_WIRE", row.TaskType);
            Assert.Equal("Q1-1", row.Sublot);
            Assert.Equal(TimeSpan.FromHours(8), row.Dates.Offset);
            Assert.Equal(10, row.Dates.Hour);
            Assert.Equal("PKG-A", row.Package);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Missing_required_column_returns_incomplete_outcome()
    {
        var csv = """
            TASK_TYPE,SUBLOT,AREA,EQP,STEP,PACKAGE
            DIE_TO_OVEN,Q1,N01-01,EQ1,烘箱,PKG
            """;
        var path = Path.Combine(Path.GetTempPath(), $"mes-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        try
        {
            var source = new CsvFileMesSnapshotSource(path);
            var outcome = await source.ReadAsync();
            Assert.Equal(SnapshotOutcomeKind.Incomplete, outcome.Kind);
            Assert.Empty(outcome.Rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Unparseable_dates_returns_incomplete_outcome()
    {
        var csv = """
            TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE
            DIE_TO_OVEN,Q1,N01-01,EQ1,烘箱,not-a-date,PKG
            """;
        var path = Path.Combine(Path.GetTempPath(), $"mes-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        try
        {
            var source = new CsvFileMesSnapshotSource(path);
            var outcome = await source.ReadAsync();
            Assert.Equal(SnapshotOutcomeKind.Incomplete, outcome.Kind);
            Assert.Empty(outcome.Rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Explicit_offset_on_dates_is_preserved()
    {
        var csv = """
            TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE
            STAGING_TO_WIRE,Q1-1,N01-01,EQ1,焊线,2026-08-01T10:00:00Z,PKG-A
            """;
        var path = Path.Combine(Path.GetTempPath(), $"mes-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        try
        {
            var source = new CsvFileMesSnapshotSource(path);
            var outcome = await source.ReadAsync();

            Assert.Equal(SnapshotOutcomeKind.Success, outcome.Kind);
            var row = Assert.Single(outcome.Rows);
            Assert.Equal(TimeSpan.Zero, row.Dates.Offset);
            Assert.Equal(10, row.Dates.Hour);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Empty_task_type_returns_incomplete_outcome()
    {
        var csv = """
            TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE
            ,Q1,N01-01,EQ1,烘箱,2026-08-01T10:00:00,PKG
            """;
        var path = Path.Combine(Path.GetTempPath(), $"mes-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        try
        {
            var source = new CsvFileMesSnapshotSource(path);
            var outcome = await source.ReadAsync();
            Assert.Equal(SnapshotOutcomeKind.Incomplete, outcome.Kind);
            Assert.Empty(outcome.Rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Short_data_row_returns_incomplete_outcome()
    {
        var csv = """
            TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE
            DIE_TO_OVEN,Q1
            """;
        var path = Path.Combine(Path.GetTempPath(), $"mes-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8);

        try
        {
            var source = new CsvFileMesSnapshotSource(path);
            var outcome = await source.ReadAsync();
            Assert.Equal(SnapshotOutcomeKind.Incomplete, outcome.Kind);
            Assert.Empty(outcome.Rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Zero_byte_file_returns_incomplete_outcome()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mes-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());

        try
        {
            var source = new CsvFileMesSnapshotSource(path);
            var outcome = await source.ReadAsync();
            Assert.Equal(SnapshotOutcomeKind.Incomplete, outcome.Kind);
            Assert.Empty(outcome.Rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Header_only_empty_result_returns_success_with_no_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mes-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(
            path,
            "TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE\n",
            Encoding.UTF8);

        try
        {
            var source = new CsvFileMesSnapshotSource(path);
            var outcome = await source.ReadAsync();
            Assert.Equal(SnapshotOutcomeKind.Success, outcome.Kind);
            Assert.Empty(outcome.Rows);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
