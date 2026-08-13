using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

public sealed class MesFieldValidationAndSeriesErrorCatalogTests
{
    [Fact]
    public void Required_field_validation_preserves_null_empty_and_blank_source_values()
    {
        var observation = new MesTaskUnionObservation(
            WorkType: "WIRE_TO_NITROGEN",
            Sublot: "SL-FIELD-VALIDATION",
            Area: null,
            Eqp: "",
            Step: "\t",
            MesSourceDate: null,
            Package: "  ");

        var result = MesFieldValidation.Evaluate(observation);

        Assert.Null(result.Fields.Area);
        Assert.Equal("", result.Fields.Eqp);
        Assert.Equal("\t", result.Fields.Step);
        Assert.Null(result.Fields.MesSourceDate);
        Assert.Equal("  ", result.Fields.Package);
        Assert.Equal(
            ["AREA", "EQP", "STEP", "DATES", "PACKAGE"],
            result.Issues.Select(issue => issue.SubjectKind));
        Assert.All(result.Issues, issue =>
        {
            Assert.Equal("REQUIRED_MES_FIELD_MISSING", issue.Code);
            Assert.Equal("DATA_COMPLETENESS", issue.Category);
        });
    }

    [Fact]
    public void Area_validation_uses_the_exact_domain_format_without_normalizing_the_source_value()
    {
        string[] validAreas = ["A1-1", "N3-3", "Z99-99"];
        foreach (var area in validAreas)
        {
            var valid = MesFieldValidation.Evaluate(CreateValidObservation(area));

            Assert.Equal(area, valid.Fields.Area);
            Assert.Empty(valid.Issues);
        }

        string[] invalidAreas =
        [
            "a1-1",
            "A0-1",
            "A01-1",
            "A1-0",
            "A1-01",
            "A100-1",
            "A1-100",
            "AA1-1",
            " A1-1",
            "A1-1 ",
            "A1-1\n",
        ];
        foreach (var area in invalidAreas)
        {
            var invalid = MesFieldValidation.Evaluate(CreateValidObservation(area));

            Assert.Equal(area, invalid.Fields.Area);
            var issue = Assert.Single(invalid.Issues);
            Assert.Equal("INVALID_MES_FIELD_FORMAT", issue.Code);
            Assert.Equal("DATA_FORMAT", issue.Category);
            Assert.Equal("AREA", issue.SubjectKind);
            Assert.Equal(area, issue.ObservedValue);
            Assert.Equal("^[A-Z][1-9][0-9]?-[1-9][0-9]?$", issue.ExpectedRule);
        }
    }

    [Fact]
    public void Nonblank_unparseable_dates_is_format_evidence_not_missing_evidence()
    {
        var observation = CreateValidObservation("N3-3") with
        {
            MesSourceDate = null,
            MesSourceDateRaw = "not-an-oracle-date",
        };

        var result = MesFieldValidation.Evaluate(observation);

        Assert.Null(result.Fields.MesSourceDate);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("INVALID_MES_FIELD_FORMAT", issue.Code);
        Assert.Equal("DATA_FORMAT", issue.Category);
        Assert.Equal("DATES", issue.SubjectKind);
        Assert.Equal("not-an-oracle-date", issue.ObservedValue);
        Assert.Equal("ORACLE_DATE_OR_TIMESTAMP", issue.ExpectedRule);
    }

    [Fact]
    public void Series_error_catalog_publishes_the_complete_stable_first_version()
    {
        var definitions = SeriesErrorCatalog.Definitions;

        Assert.Equal(
            [
                "REQUIRED_MES_FIELD_MISSING",
                "INVALID_MES_FIELD_FORMAT",
                "DUPLICATE_TRANSPORT_DEMAND_KEY",
                "SUBLOT_MULTIPLE_WORK_TYPES",
                "LONG_GONE_BUT_VISIBLE",
            ],
            definitions.Select(definition => definition.Code));
        Assert.Equal(5, definitions.Select(definition => definition.Code).Distinct().Count());
        Assert.Equal(
            ["DATA_COMPLETENESS", "DATA_FORMAT", "OBSERVATION_CONFLICT", "LIFECYCLE_CONFLICT"],
            definitions.Select(definition => definition.Category).Distinct());
        Assert.All(definitions, definition => Assert.Equal("ERROR", definition.Severity));
        Assert.Equal(
            ["DEMAND", "DEMAND", "DEMAND", "DEMAND", "SERIES"],
            definitions.Select(definition => definition.Scope));
        Assert.Equal(
            [
                "A required MES field is null, empty, or whitespace.",
                "A present MES field does not match its domain format.",
                "One round contains multiple raw observations for a TransportDemandKey.",
                "One SUBLOT appears in multiple WorkTypes in the same round.",
                "An archived DemandSeries became visible in MES again.",
            ],
            definitions.Select(definition => definition.Meaning));
    }

    private static MesTaskUnionObservation CreateValidObservation(string area) =>
        new(
            WorkType: "WIRE_TO_NITROGEN",
            Sublot: "SL-FIELD-VALIDATION",
            Area: area,
            Eqp: "EQP-01",
            Step: "STEP-01",
            MesSourceDate: new DateTimeOffset(2026, 8, 13, 8, 30, 0, TimeSpan.FromHours(8)),
            Package: "PKG-01");
}
