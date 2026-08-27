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
        var demand = new ClipboardDemandRow(
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
            ("locationRiskCode", "LocationRiskCode"),
            ("disappear", "DisappearCount"),
        };

        var (headers, values) = WatchGridClipboard.ProjectRow(demand, columns);
        var text = WatchGridClipboard.FormatRowWithHeaders(headers, values, beijing);

        Assert.StartsWith(
            "DemandId\tTASK_TYPE\tSUBLOT\tstatus\t当前工序进入时间 (DATES)\tlast seen\tcreated\tgone at\tAREA\tEQP\tSTEP\tPACKAGE\tlocationRisk\tlocationRiskCode\tdisappear\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("deadbeef\tDIE_TO_OVEN\tS1\tVISIBLE\t", text, StringComparison.Ordinal);
        Assert.Contains("\tnull\tnull\tE1\tSTEP1\tP\tFalse\tnull\t0", text, StringComparison.Ordinal);
        Assert.Contains("2026-07-15 10:30:45 +08:00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_menu_headers_preserve_view_details_with_clipboard_actions()
    {
        var headers = WatchGridClipboardBehavior.ComposeContextMenuHeaders(
            ["查看详情", "复制 DemandId"]);

        Assert.Equal(
            ["查看详情", "复制 DemandId", "复制单元格", "复制整行", "复制整行（含列名）"],
            headers);
    }

    [Fact]
    public void Context_menu_headers_without_existing_items_are_clipboard_only()
    {
        var headers = WatchGridClipboardBehavior.ComposeContextMenuHeaders(null);

        Assert.Equal(
            ["复制单元格", "复制整行", "复制整行（含列名）"],
            headers);
    }

    [Fact]
    public void Context_menu_headers_project_in_english_without_translating_preserved_commands()
    {
        var headers = WatchGridClipboardBehavior.ComposeContextMenuHeaders(
            ["View raw JSON", "Copy DemandId"],
            WatchDisplayLanguage.English);

        Assert.Equal(
            ["View raw JSON", "Copy DemandId", "Copy cell", "Copy row", "Copy row with headers"],
            headers);
    }

    [Fact]
    public void Attached_copy_commands_reproject_in_place_when_shared_language_changes()
    {
        StaTestRunner.Run(() =>
        {
            var grid = new System.Windows.Controls.DataGrid
            {
                ContextMenu = new System.Windows.Controls.ContextMenu(),
            };
            grid.ContextMenu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "RAW_JSON",
            });
            var language = new WatchDisplayLanguageState(WatchDisplayLanguage.SimplifiedChinese);

            WatchGridClipboardBehavior.Attach(grid, language, preserveSelectionUnit: true);
            Assert.Equal(
                ["RAW_JSON", "复制单元格", "复制整行", "复制整行（含列名）"],
                grid.ContextMenu.Items.Cast<System.Windows.Controls.MenuItem>()
                    .Select(item => item.Header?.ToString()).ToArray());

            language.ApplyCommitted(WatchDisplayLanguage.English);

            Assert.Equal(
                ["RAW_JSON", "Copy cell", "Copy row", "Copy row with headers"],
                grid.ContextMenu.Items.Cast<System.Windows.Controls.MenuItem>()
                    .Select(item => item.Header?.ToString()).ToArray());
        });
    }

    [Fact]
    public void Projects_alert_row_preserving_column_order()
    {
        var alert = new ClipboardAlertRow(
            AlertId: "a1",
            Code: "POLL_FAILURE",
            Severity: "ERROR",
            TaskType: null,
            Sublot: "S2",
            DemandId: "abcdef",
            Message: "line1\nline2",
            Details: null,
            FirstSeenAt: null,
            LastSeenAt: null,
            OccurrenceCount: 1,
            IsActive: true,
            ResolvedAt: null,
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

    /// <summary>
    /// A grid row shape only. WatchGridClipboard projects by column binding path, so
    /// these tests need a row with the paths a grid binds — not a contract DTO.
    /// </summary>
    private sealed record ClipboardDemandRow(
        string DemandId,
        string TaskType,
        string Sublot,
        string? Area,
        string? Eqp,
        string? Step,
        DateTimeOffset Dates,
        string? Package,
        string Status,
        DateTimeOffset MesLastSeenAt,
        int DisappearCount,
        bool LocationRisk,
        string? LocationRiskCode,
        DateTimeOffset CreatedAt,
        DateTimeOffset? GoneAt);

    private sealed record ClipboardAlertRow(
        string AlertId,
        string Code,
        string Severity,
        string? TaskType,
        string? Sublot,
        string? DemandId,
        string? Message,
        string? Details,
        DateTimeOffset? FirstSeenAt,
        DateTimeOffset? LastSeenAt,
        int OccurrenceCount,
        bool IsActive,
        DateTimeOffset? ResolvedAt,
        DateTimeOffset? CreatedAt);
}
