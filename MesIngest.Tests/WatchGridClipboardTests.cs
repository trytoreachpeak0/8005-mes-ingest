using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 08 seam: WatchGridClipboard pure-text projection
/// (null literal, local time display, Tab/newline escaping, column order).
/// </summary>
public class WatchGridClipboardTests
{
    private static TimeZoneInfo ResolveTz(string windowsId, string ianaId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
    }

    [Fact]
    public void Formats_null_as_literal_null()
    {
        Assert.Equal("null", WatchGridClipboard.FormatValue(null));
    }

    [Fact]
    public void Formats_datetime_as_local_display_text()
    {
        var beijing = ResolveTz("China Standard Time", "Asia/Shanghai");
        var utc = new DateTimeOffset(2026, 7, 15, 2, 30, 45, TimeSpan.Zero);

        Assert.Equal(
            "2026-07-15 10:30:45 +08:00",
            WatchGridClipboard.FormatValue(utc, beijing));
    }

    [Fact]
    public void Formats_row_with_tab_separators_preserving_column_order()
    {
        var text = WatchGridClipboard.FormatRow(["A", null, "C"]);

        Assert.Equal("A\tnull\tC", text);
    }

    [Fact]
    public void Formats_row_with_headers_as_header_line_then_value_line()
    {
        var text = WatchGridClipboard.FormatRowWithHeaders(
            ["DemandId", "TASK_TYPE", "SUBLOT"],
            ["abc", "DIE_TO_OVEN", null]);

        Assert.Equal("DemandId\tTASK_TYPE\tSUBLOT\nabc\tDIE_TO_OVEN\tnull", text);
    }

    [Fact]
    public void Escapes_tab_and_newline_inside_cell_for_tsv()
    {
        var text = WatchGridClipboard.FormatRow(["plain", "has\ttab", "has\nline", "say \"hi\""]);

        Assert.Equal("plain\t\"has\ttab\"\t\"has\nline\"\t\"say \"\"hi\"\"\"", text);
    }

    [Fact]
    public void Formats_empty_string_as_empty_not_null_literal()
    {
        Assert.Equal(string.Empty, WatchGridClipboard.FormatValue(string.Empty));
        Assert.Equal("\t", WatchGridClipboard.FormatRow(["", ""]));
    }

    [Fact]
    public void Formats_bool_and_int_with_invariant_text()
    {
        Assert.Equal("True", WatchGridClipboard.FormatValue(true));
        Assert.Equal("3", WatchGridClipboard.FormatValue(3));
    }

    [Fact]
    public void Projects_demand_row_in_grid_column_order_with_null_gone_at()
    {
        var beijing = ResolveTz("China Standard Time", "Asia/Shanghai");
        var demand = new WatchDemandDto(
            DemandId: "deadbeef",
            TaskType: "DIE_TO_OVEN",
            Sublot: "S1",
            Area: null,
            Eqp: "E1",
            Step: "STEP1",
            Dates: new DateTimeOffset(2026, 7, 15, 2, 30, 45, TimeSpan.Zero),
            Package: "P",
            Status: "VISIBLE",
            MesLastSeenAt: new DateTimeOffset(2026, 7, 15, 3, 0, 0, TimeSpan.Zero),
            DisappearCount: 0,
            LocationRisk: false,
            LocationRiskCode: null,
            CreatedAt: new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero),
            GoneAt: null);

        var columns = new (string Header, string PropertyPath)[]
        {
            ("DemandId", "DemandId"),
            ("TASK_TYPE", "TaskType"),
            ("SUBLOT", "Sublot"),
            ("status", "Status"),
            ("当前工序进入时间 (DATES)", "Dates"),
            ("last seen", "MesLastSeenAt"),
            ("created", "CreatedAt"),
            ("gone at", "GoneAt"),
            ("AREA", "Area"),
            ("EQP", "Eqp"),
            ("STEP", "Step"),
            ("PACKAGE", "Package"),
            ("locationRisk", "LocationRisk"),
            ("disappear", "DisappearCount"),
        };

        var (headers, values) = WatchGridClipboard.ProjectRow(demand, columns);
        var text = WatchGridClipboard.FormatRowWithHeaders(headers, values, beijing);

        Assert.StartsWith(
            "DemandId\tTASK_TYPE\tSUBLOT\tstatus\t当前工序进入时间 (DATES)\tlast seen\tcreated\tgone at\tAREA\tEQP\tSTEP\tPACKAGE\tlocationRisk\tdisappear\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("deadbeef\tDIE_TO_OVEN\tS1\tVISIBLE\t", text, StringComparison.Ordinal);
        Assert.Contains("\tnull\tnull\tE1\tSTEP1\tP\tFalse\t0", text, StringComparison.Ordinal);
        Assert.Contains("2026-07-15 10:30:45 +08:00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Projects_alert_row_preserving_column_order()
    {
        var alert = new WatchAlertDto(
            Code: "POLL_FAILURE",
            TaskType: null,
            Sublot: "S2",
            DemandId: "abcdef",
            Message: "line1\nline2",
            CreatedAt: null);

        var columns = new (string Header, string PropertyPath)[]
        {
            ("Code", "Code"),
            ("created", "CreatedAt"),
            ("TASK_TYPE", "TaskType"),
            ("SUBLOT", "Sublot"),
            ("DemandId", "DemandId"),
            ("Message", "Message"),
        };

        var (headers, values) = WatchGridClipboard.ProjectRow(alert, columns);
        var text = WatchGridClipboard.FormatRow(values);

        Assert.Equal("POLL_FAILURE\tnull\tnull\tS2\tabcdef\t\"line1\nline2\"", text);
        Assert.Equal(
            new[] { "Code", "created", "TASK_TYPE", "SUBLOT", "DemandId", "Message" },
            headers);
    }
}
