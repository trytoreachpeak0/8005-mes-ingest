using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 11 seam: AlertDetailsProjection — structured details for each alert code,
/// plus legacy / null Details handling (no raw-JSON-only presentation).
/// </summary>
public class AlertDetailsProjectionTests
{
    [Fact]
    public void FieldDrift_projects_field_comparison_rows()
    {
        var projection = AlertDetailsProjection.From(
            "FIELD_DRIFT",
            """{"fields":[{"field":"Area","frozen":"A","observed":"B"},{"field":"Eqp","frozen":"E1","observed":"E2"}]}""");

        Assert.Equal(AlertDetailsProjectionKind.FieldDrift, projection.Kind);
        Assert.Equal(
            [
                new AlertFieldDriftRow("Area", "A", "B"),
                new AlertFieldDriftRow("Eqp", "E1", "E2"),
            ],
            projection.FieldRows);
        Assert.Empty(projection.KeyValues);
    }

    [Fact]
    public void PollFailure_projects_key_value_rows()
    {
        var projection = AlertDetailsProjection.From(
            "POLL_FAILURE",
            """{"failureStage":"ORACLE_QUERY","timeoutSeconds":30,"durationMs":45000,"rowCount":0,"reason":"timeout"}""");

        Assert.Equal(AlertDetailsProjectionKind.KeyValue, projection.Kind);
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "failureStage", Value: "ORACLE_QUERY" });
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "timeoutSeconds", Value: "30" });
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "durationMs", Value: "45000" });
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "rowCount", Value: "0" });
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "reason", Value: "timeout" });
    }

    [Fact]
    public void Duplicate_projects_count_and_conflicting_row_summaries()
    {
        var projection = AlertDetailsProjection.From(
            "DUPLICATE_RECONCILE_KEY",
            """{"duplicateCount":2,"conflictingRows":[{"taskType":"DIE_TO_OVEN","sublot":"S1","area":"A","eqp":"E","step":"ST","dates":"2026-07-15T02:00:00+00:00","package":"P"}]}""");

        Assert.Equal(AlertDetailsProjectionKind.KeyValue, projection.Kind);
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "duplicateCount", Value: "2" });
        Assert.Contains(
            projection.KeyValues,
            kv => kv.Key == "conflictingRows[0]"
                && kv.Value.Contains("DIE_TO_OVEN", StringComparison.Ordinal)
                && kv.Value.Contains("S1", StringComparison.Ordinal));
    }

    [Fact]
    public void Reappear_projects_previous_and_new_demand_ids()
    {
        var projection = AlertDetailsProjection.From(
            "REAPPEAR_AFTER_GONE",
            """{"previousDemandId":"aaa111","newDemandId":"bbb222"}""");

        Assert.Contains(projection.KeyValues, kv => kv is { Key: "previousDemandId", Value: "aaa111" });
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "newDemandId", Value: "bbb222" });
    }

    [Fact]
    public void PausedZeroDrop_projects_health_and_recovery_fields()
    {
        var projection = AlertDetailsProjection.From(
            "PAUSED_ZERO_DROP",
            """{"lastHealthyNonZeroCount":12,"recoveryStreak":1,"enterThreshold":10,"clearStreakRequired":3}""");

        Assert.Contains(projection.KeyValues, kv => kv is { Key: "lastHealthyNonZeroCount", Value: "12" });
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "recoveryStreak", Value: "1" });
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "enterThreshold", Value: "10" });
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "clearStreakRequired", Value: "3" });
    }

    [Fact]
    public void PollIncomplete_projects_key_value_rows()
    {
        var projection = AlertDetailsProjection.From(
            "POLL_INCOMPLETE",
            """{"failureStage":"ROW_PARSE","timeoutSeconds":null,"durationMs":1200,"rowCount":3,"reason":"bad row"}""");

        Assert.Equal(AlertDetailsProjectionKind.KeyValue, projection.Kind);
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "failureStage", Value: "ROW_PARSE" });
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "durationMs", Value: "1200" });
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "rowCount", Value: "3" });
        Assert.Contains(projection.KeyValues, kv => kv is { Key: "reason", Value: "bad row" });
    }

    [Fact]
    public void Null_details_yields_empty_structured_projection()
    {
        var projection = AlertDetailsProjection.From("POLL_INCOMPLETE", null);

        Assert.Equal(AlertDetailsProjectionKind.Empty, projection.Kind);
        Assert.Empty(projection.KeyValues);
        Assert.Empty(projection.FieldRows);
        Assert.Null(projection.LegacySummary);
    }

    [Fact]
    public void Unparseable_or_legacy_details_yield_legacy_summary_not_crash()
    {
        var projection = AlertDetailsProjection.From("FIELD_DRIFT", "not-json {{{");

        Assert.Equal(AlertDetailsProjectionKind.Legacy, projection.Kind);
        Assert.Equal("not-json {{{", projection.LegacySummary);
        Assert.Empty(projection.FieldRows);
    }

    [Fact]
    public void Empty_object_details_yield_empty_key_values()
    {
        var projection = AlertDetailsProjection.From("POLL_FAILURE", "{}");

        Assert.Equal(AlertDetailsProjectionKind.KeyValue, projection.Kind);
        Assert.Empty(projection.KeyValues);
    }
}
