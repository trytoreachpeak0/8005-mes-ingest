using System.Text.RegularExpressions;

namespace MesIngest.Core.SeriesProjection;

public sealed record MesFieldValidationIssue(
    string Code,
    string Category,
    string SubjectKind,
    string? ObservedValue,
    string ExpectedRule);

public sealed record MesFieldValidationResult(
    LiveMesFieldSetSnapshot Fields,
    IReadOnlyList<MesFieldValidationIssue> Issues);

public sealed record SeriesErrorDefinition(
    string Code,
    string Category,
    string Severity,
    string Scope,
    string Meaning);

public static class SeriesErrorCatalog
{
    public static IReadOnlyList<SeriesErrorDefinition> Definitions { get; } =
        Array.AsReadOnly<SeriesErrorDefinition>(
        [
            new("REQUIRED_MES_FIELD_MISSING", "DATA_COMPLETENESS", "ERROR", "DEMAND", "A required MES field is null, empty, or whitespace."),
            new("INVALID_MES_FIELD_FORMAT", "DATA_FORMAT", "ERROR", "DEMAND", "A present MES field does not match its domain format."),
            new("DUPLICATE_TRANSPORT_DEMAND_KEY", "OBSERVATION_CONFLICT", "ERROR", "DEMAND", "One round contains multiple raw observations for a TransportDemandKey."),
            new("SUBLOT_MULTIPLE_WORK_TYPES", "OBSERVATION_CONFLICT", "ERROR", "DEMAND", "One SUBLOT appears in multiple WorkTypes in the same round."),
            new("LONG_GONE_BUT_VISIBLE", "LIFECYCLE_CONFLICT", "ERROR", "SERIES", "An archived DemandSeries became visible in MES again."),
        ]);

    public static SeriesErrorDefinition GetRequired(string code) =>
        Definitions.Single(definition => string.Equals(definition.Code, code, StringComparison.Ordinal));
}

public static class MesFieldValidation
{
    private const string RequiredFieldMissing = "REQUIRED_MES_FIELD_MISSING";
    private const string DataCompleteness = "DATA_COMPLETENESS";
    private const string RequiredRule = "NON_WHITESPACE_VALUE_REQUIRED";
    private const string InvalidMesFieldFormat = "INVALID_MES_FIELD_FORMAT";
    private const string DataFormat = "DATA_FORMAT";
    private const string AreaRule = "^[A-Z][1-9][0-9]?-[1-9][0-9]?$";

    private static readonly Regex AreaFormat = new(
        AreaRule,
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static MesFieldValidationResult Evaluate(MesTaskUnionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var issues = new List<MesFieldValidationIssue>();
        AddMissingTextIssue(issues, "AREA", observation.Area);
        if (!string.IsNullOrWhiteSpace(observation.Area))
        {
            var match = AreaFormat.Match(observation.Area);
            if (!match.Success || match.Length != observation.Area.Length)
            {
                issues.Add(new MesFieldValidationIssue(
                    InvalidMesFieldFormat,
                    DataFormat,
                    "AREA",
                    observation.Area,
                    AreaRule));
            }
        }
        AddMissingTextIssue(issues, "EQP", observation.Eqp);
        AddMissingTextIssue(issues, "STEP", observation.Step);
        if (observation.MesSourceDate is null)
        {
            issues.Add(new MesFieldValidationIssue(
                RequiredFieldMissing,
                DataCompleteness,
                "DATES",
                ObservedValue: null,
                RequiredRule));
        }
        AddMissingTextIssue(issues, "PACKAGE", observation.Package);

        return new MesFieldValidationResult(
            new LiveMesFieldSetSnapshot(
                observation.Area,
                observation.Eqp,
                observation.Step,
                observation.MesSourceDate,
                observation.Package),
            issues);
    }

    private static void AddMissingTextIssue(
        ICollection<MesFieldValidationIssue> issues,
        string subjectKind,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        issues.Add(new MesFieldValidationIssue(
            RequiredFieldMissing,
            DataCompleteness,
            subjectKind,
            value,
            RequiredRule));
    }
}
