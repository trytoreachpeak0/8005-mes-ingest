using System.Text.Json;
using MesIngest.Core;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 24: the packaged release smoke asserts a committed, non-empty externally
/// readable catalog. That only holds while its scripted rows still satisfy the
/// production MES field validation — a row that stops qualifying produces durable
/// ERROR evidence and an empty catalog instead, which the first golden-machine run
/// surfaced only after a full deploy. These checks are the cheap version of it.
/// </summary>
public sealed class ReleaseSmokeScriptedRoundsTests
{
    private static readonly string[] RequiredColumns =
        ["TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "DATES", "PACKAGE"];

    [Fact]
    public void Scripted_rounds_declare_the_approved_query_and_the_required_columns()
    {
        var recording = LoadRecording();

        Assert.Equal(1, recording.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            CanonicalMesTaskUnionQuery.QueryVersion,
            recording.RootElement.GetProperty("queryVersion").GetString());
        Assert.Equal(RequiredColumns, ColumnNames(recording));
        Assert.Equal(
            "DateTimeOffset",
            recording.RootElement.GetProperty("columns")
                .EnumerateArray()
                .Single(column => column.GetProperty("name").GetString() == "DATES")
                .GetProperty("kind").GetString());
        Assert.NotEmpty(recording.RootElement.GetProperty("rounds").EnumerateArray());
    }

    [Fact]
    public void Every_scripted_row_qualifies_as_externally_readable()
    {
        var recording = LoadRecording();
        var columns = ColumnNames(recording);

        foreach (var observation in Observations(recording, columns))
        {
            var issues = MesFieldValidation.Evaluate(observation).Issues;

            Assert.True(
                issues.Count == 0,
                "A scripted release-smoke row does not satisfy MES field validation "
                + $"({string.Join(", ", issues.Select(issue => $"{issue.SubjectKind}:{issue.Code} expected {issue.ExpectedRule}"))}). "
                + "The smoke would then observe an empty externally readable catalog.");
        }
    }

    [Fact]
    public void Scripted_rounds_exercise_an_update_and_a_second_transport_demand_key()
    {
        var recording = LoadRecording();
        var columns = ColumnNames(recording);
        var rounds = recording.RootElement.GetProperty("rounds").EnumerateArray().ToArray();

        var firstRoundKeys = KeyTokens(rounds[0], columns);
        var lastRoundKeys = KeyTokens(rounds[^1], columns);

        Assert.True(rounds.Length >= 2, "The scripted rounds must show the projection changing.");
        // A single unchanging round would let a stuck projection pass the catalog checks.
        Assert.Single(firstRoundKeys);
        Assert.Equal(2, lastRoundKeys.Count);
        Assert.Subset(lastRoundKeys.ToHashSet(StringComparer.Ordinal), firstRoundKeys.ToHashSet(StringComparer.Ordinal));
    }

    private static IReadOnlyList<string> KeyTokens(JsonElement round, string[] columns) =>
        Observations(round, columns)
            .Select(observation => TransportDemandKeyIdentity.CreateToken(
                observation.WorkType!,
                observation.Sublot!))
            .ToArray();

    private static IEnumerable<MesTaskUnionObservation> Observations(
        JsonDocument recording,
        string[] columns) =>
        recording.RootElement.GetProperty("rounds")
            .EnumerateArray()
            .SelectMany(round => Observations(round, columns));

    private static IEnumerable<MesTaskUnionObservation> Observations(
        JsonElement round,
        string[] columns)
    {
        foreach (var row in round.GetProperty("rows").EnumerateArray())
        {
            var values = row.EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Equal(columns.Length, values.Length);
            string? Value(string name) => values[Array.IndexOf(columns, name)];

            var dates = Value("DATES");
            Assert.True(
                DateTimeOffset.TryParse(dates, out var parsedDates),
                $"A scripted release-smoke DATES value is not a parsable timestamp: {dates}");

            yield return new MesTaskUnionObservation(
                Value("TASK_TYPE"),
                Value("SUBLOT"),
                Value("AREA"),
                Value("EQP"),
                Value("STEP"),
                parsedDates,
                Value("PACKAGE"),
                dates);
        }
    }

    private static string[] ColumnNames(JsonDocument recording) =>
        recording.RootElement.GetProperty("columns")
            .EnumerateArray()
            .Select(column => column.GetProperty("name").GetString()!)
            .ToArray();

    private static JsonDocument LoadRecording() => JsonDocument.Parse(
        File.ReadAllText(
            Path.Combine(CSharpRoot, "pack", "validation", "release-smoke-rounds.json")));

    private static string CSharpRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "pack", "Publish-MesIngest.ps1")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate mes/ingest/csharp root.");
        }
    }
}
