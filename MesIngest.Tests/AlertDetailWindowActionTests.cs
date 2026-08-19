using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MesIngest.Core;
using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 16 seam: AlertDetailWindow user actions for the previous GONE and
/// new VISIBLE TransportDemand instances of a REAPPEAR_AFTER_GONE alert.
/// </summary>
[Collection("WpfDesktop")]
public class AlertDetailWindowActionTests
{
    [Fact]
    public void Detail_and_related_actions_share_accessible_responsive_watch_chrome()
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new AlertDetailWindow(AlertDetailViewModel.From(Alert(
                    code: "REAPPEAR_AFTER_GONE",
                    demandId: "new-visible-id",
                    details: """{"previousDemandId":"previous-gone-id","newDemandId":"new-visible-id"}""")));
                var expectedNames = new Dictionary<string, string>
                {
                    ["CopySummaryButton"] = "复制 IngestAlert 摘要",
                    ["CopyDetailsButton"] = "复制 IngestAlert Details JSON",
                    ["CopyDemandIdButton"] = "复制关联 TransportDemand DemandId",
                    ["LocateDemandButton"] = "精确查看关联 TransportDemand",
                    ["CopyPreviousDemandIdButton"] = "复制先前 GONE DemandId",
                    ["LocatePreviousDemandButton"] = "精确查看先前 GONE TransportDemand",
                    ["CopyNewDemandIdButton"] = "复制当前再现 DemandId",
                    ["LocateNewDemandButton"] = "精确查看当前再现 TransportDemand",
                    ["SearchBusinessKeyButton"] = "按 TASK_TYPE 和 SUBLOT 查当前 VISIBLE TransportDemand",
                    ["RelatedDemandGrid"] = "关联 TransportDemand 快照",
                    ["FieldDriftGrid"] = "IngestAlert 字段漂移详情",
                    ["KeyValueGrid"] = "IngestAlert 键值详情",
                };

                Assert.True(window.Width <= 960, "150% DPI on a 1440px-wide desktop leaves about 960 DIPs");
                Assert.True(window.Height <= 600, "150% DPI on a 900px-high desktop leaves about 600 DIPs");
                Assert.True(window.MinWidth <= 720);
                Assert.IsType<WrapPanel>(window.FindName("AlertDetailActionPanel"));
                foreach (var (controlName, expectedName) in expectedNames)
                {
                    var control = Assert.IsAssignableFrom<DependencyObject>(window.FindName(controlName));
                    Assert.Equal(expectedName, AutomationProperties.GetName(control));
                }
                foreach (var token in new[]
                         {
                             "WatchSurfaceBrush",
                             "WatchBorderBrush",
                             "WatchTextBrush",
                             "WatchMutedTextBrush",
                             "WatchWarningBrush",
                             "WatchErrorBrush",
                         })
                {
                    Assert.NotNull(window.FindResource(token));
                }
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Alert detail accessibility test did not finish");
        Assert.Null(caught);
    }

    [Fact]
    public void Reappear_detail_loads_complete_previous_and_new_demands_and_marks_changed_rows()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                var requestedIds = new List<string>();
                var previous = Demand(
                    demandId: "previous-gone-id",
                    status: "GONE",
                    area: "A",
                    goneAt: new DateTimeOffset(2026, 8, 3, 2, 0, 0, TimeSpan.Zero));
                var current = Demand(
                    demandId: "new-visible-id",
                    status: "VISIBLE",
                    area: "B");
                var demands = new Dictionary<string, WatchDemandDto>(StringComparer.Ordinal)
                {
                    [previous.DemandId] = previous,
                    [current.DemandId] = current,
                };
                var alert = Alert(
                    code: "REAPPEAR_AFTER_GONE",
                    demandId: current.DemandId,
                    details: """{"previousDemandId":"previous-gone-id","newDemandId":"new-visible-id"}""");
                var window = new AlertDetailWindow(
                    AlertDetailViewModel.From(alert),
                    loadDemand: (demandId, _) =>
                    {
                        requestedIds.Add(demandId);
                        return Task.FromResult<WatchDemandDto?>(demands[demandId]);
                    });

                window.Show();

                var grid = (DataGrid)window.FindName("RelatedDemandGrid");
                var rows = Assert.IsAssignableFrom<IEnumerable<AlertDemandComparisonRow>>(grid.ItemsSource).ToList();
                Assert.Equal(["previous-gone-id", "new-visible-id"], requestedIds);
                Assert.Equal(15, rows.Count);
                Assert.Contains(rows, row => row is
                {
                    Field: "TASK_TYPE",
                    PreviousValue: "DIE_TO_OVEN",
                    CurrentValue: "DIE_TO_OVEN",
                    IsDifferent: false,
                });
                Assert.Contains(rows, row => row is
                {
                    Field: "AREA",
                    PreviousValue: "A",
                    CurrentValue: "B",
                    IsDifferent: true,
                });
                Assert.Contains(rows, row => row is
                {
                    Field: "DemandId",
                    PreviousValue: "previous-gone-id",
                    CurrentValue: "new-visible-id",
                    IsDifferent: true,
                });
                Assert.Equal(Visibility.Visible, grid.Visibility);
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(thread.Join(0), "STA complete comparison test did not finish");
        Assert.Null(caught);
    }

    [Fact]
    public void Ordinary_alert_detail_loads_one_complete_related_demand_snapshot()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                var demand = Demand("ordinary-id", "VISIBLE", "A");
                var window = new AlertDetailWindow(
                    AlertDetailViewModel.From(Alert("FIELD_DRIFT", demand.DemandId, "{}")),
                    loadDemand: (demandId, _) => Task.FromResult<WatchDemandDto?>(
                        demandId == demand.DemandId ? demand : null));

                window.Show();

                var grid = (DataGrid)window.FindName("RelatedDemandGrid");
                var rows = Assert.IsAssignableFrom<IEnumerable<AlertDemandComparisonRow>>(grid.ItemsSource).ToList();
                Assert.Equal(15, rows.Count);
                Assert.All(rows, row => Assert.False(row.IsDifferent));
                Assert.Equal(Visibility.Collapsed, grid.Columns[1].Visibility);
                Assert.Equal("Value", grid.Columns[2].Header);
                Assert.Contains(rows, row => row is { Field: "AREA", CurrentValue: "A" });
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(thread.Join(0), "STA single snapshot test did not finish");
        Assert.Null(caught);
    }

    [Fact]
    public void Reappear_actions_copy_and_locate_previous_and_new_ids_without_ambiguity()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                var copied = new List<string>();
                var located = new List<AlertDemandTarget>();
                var alert = new WatchAlertDto(
                    AlertId: "reappear-1",
                    Code: "REAPPEAR_AFTER_GONE",
                    Severity: "WARNING",
                    TaskType: "DIE_TO_OVEN",
                    Sublot: "S1",
                    DemandId: "new-visible-id",
                    Message: "reappeared",
                    Details: """{"previousDemandId":"previous-gone-id","newDemandId":"new-visible-id"}""",
                    FirstSeenAt: null,
                    LastSeenAt: null,
                    OccurrenceCount: 1,
                    IsActive: true,
                    ResolvedAt: null,
                    CreatedAt: null);
                var window = new AlertDetailWindow(
                    AlertDetailViewModel.From(alert),
                    locateDemand: located.Add,
                    copyText: copied.Add);

                Assert.Equal(
                    "previous-gone-id",
                    ((TextBlock)window.FindName("PreviousDemandIdText")).Text);
                Assert.Equal(
                    "new-visible-id",
                    ((TextBlock)window.FindName("NewDemandIdText")).Text);
                Assert.Equal(
                    "先前 GONE",
                    ((TextBlock)window.FindName("PreviousDemandRoleText")).Text);
                Assert.Equal(
                    "当前再现",
                    ((TextBlock)window.FindName("NewDemandRoleText")).Text);
                Assert.Equal("reappear-1", ((TextBlock)window.FindName("AlertIdText")).Text);
                Assert.NotNull(window.FindName("CreatedAtText"));
                Assert.Equal(
                    Visibility.Collapsed,
                    ((FrameworkElement)window.FindName("GenericDemandActionsPanel")).Visibility);

                Click(window, "CopyPreviousDemandIdButton");
                Click(window, "CopyNewDemandIdButton");
                Click(window, "CopySummaryButton");
                Click(window, "CopyDetailsButton");
                Click(window, "LocatePreviousDemandButton");
                Click(window, "LocateNewDemandButton");

                Assert.Equal("previous-gone-id", copied[0]);
                Assert.Equal("new-visible-id", copied[1]);
                Assert.Contains("AlertId=reappear-1", copied[2], StringComparison.Ordinal);
                Assert.Equal(alert.Details, copied[3]);
                Assert.Equal(
                    [
                        new AlertDemandTarget("previous-gone-id", AlertDemandTargetKind.PreviousGone),
                        new AlertDemandTarget("new-visible-id", AlertDemandTargetKind.NewVisible),
                    ],
                    located);

                var historicalHint = AlertDemandLocateHints.OutsideCurrentBrowse(located[0], "GONE");
                window.SetLocateHint(historicalHint);
                var hintText = (TextBlock)window.FindName("LocateHintText");
                Assert.Equal(Visibility.Visible, hintText.Visibility);
                Assert.Equal(historicalHint, hintText.Text);

                window.SetLocateHint(null);
                Assert.Equal(Visibility.Collapsed, hintText.Visibility);
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(thread.Join(0), "STA action test did not finish");
        Assert.Null(caught);
    }

    [Fact]
    public void Missing_previous_id_and_non_reappear_alerts_do_not_show_invalid_previous_action()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                var missingPrevious = Alert(
                    code: "REAPPEAR_AFTER_GONE",
                    demandId: "new-visible-id",
                    details: """{"newDemandId":"new-visible-id"}""");
                var reappearWindow = new AlertDetailWindow(AlertDetailViewModel.From(missingPrevious));

                Assert.Equal(
                    Visibility.Collapsed,
                    ((FrameworkElement)reappearWindow.FindName("PreviousDemandActionsPanel")).Visibility);
                Assert.False(((Button)reappearWindow.FindName("CopyPreviousDemandIdButton")).IsEnabled);
                Assert.Equal(
                    Visibility.Visible,
                    ((FrameworkElement)reappearWindow.FindName("NewDemandActionsPanel")).Visibility);
                reappearWindow.Close();

                var ordinary = Alert(
                    code: "POLL_FAILURE",
                    demandId: "ordinary-id",
                    details: """{"previousDemandId":"must-not-be-an-action"}""");
                var ordinaryWindow = new AlertDetailWindow(AlertDetailViewModel.From(ordinary));

                Assert.Equal(
                    Visibility.Collapsed,
                    ((FrameworkElement)ordinaryWindow.FindName("ReappearDemandActionsPanel")).Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    ((FrameworkElement)ordinaryWindow.FindName("GenericDemandActionsPanel")).Visibility);
                ordinaryWindow.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(thread.Join(0), "STA contrast test did not finish");
        Assert.Null(caught);
    }

    [Fact]
    public void Business_key_action_is_explicit_and_no_association_hides_all_navigation_actions()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                var searched = new List<TransportDemandKey>();
                var searchable = Alert("FIELD_DRIFT", demandId: null, details: "{}") with
                {
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "S1",
                };
                var searchableWindow = new AlertDetailWindow(
                    AlertDetailViewModel.From(searchable),
                    searchBusinessKey: searched.Add);

                Assert.Equal(
                    Visibility.Collapsed,
                    ((FrameworkElement)searchableWindow.FindName("GenericDemandActionsPanel")).Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    ((FrameworkElement)searchableWindow.FindName("ReappearDemandActionsPanel")).Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    ((FrameworkElement)searchableWindow.FindName("BusinessKeyDemandActionsPanel")).Visibility);
                Assert.Equal(
                    "按业务键查任务",
                    ((Button)searchableWindow.FindName("SearchBusinessKeyButton")).Content);

                Click(searchableWindow, "SearchBusinessKeyButton");
                Assert.Equal([new TransportDemandKey("DIE_TO_OVEN", "S1")], searched);
                searchableWindow.Close();

                var unrelated = searchable with { TaskType = null, Sublot = null };
                var unrelatedWindow = new AlertDetailWindow(AlertDetailViewModel.From(unrelated));
                Assert.Equal(
                    Visibility.Collapsed,
                    ((FrameworkElement)unrelatedWindow.FindName("GenericDemandActionsPanel")).Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    ((FrameworkElement)unrelatedWindow.FindName("ReappearDemandActionsPanel")).Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    ((FrameworkElement)unrelatedWindow.FindName("BusinessKeyDemandActionsPanel")).Visibility);
                unrelatedWindow.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(thread.Join(0), "STA business-key action test did not finish");
        Assert.Null(caught);
    }

    private static WatchAlertDto Alert(string code, string? demandId, string? details) =>
        new(
            AlertId: "alert-contrast",
            Code: code,
            Severity: "WARNING",
            TaskType: "DIE_TO_OVEN",
            Sublot: "S1",
            DemandId: demandId,
            Message: "message",
            Details: details,
            FirstSeenAt: null,
            LastSeenAt: null,
            OccurrenceCount: 1,
            IsActive: true,
            ResolvedAt: null,
            CreatedAt: null);

    private static WatchDemandDto Demand(
        string demandId,
        string status,
        string? area,
        DateTimeOffset? goneAt = null) =>
        new(
            DemandId: demandId,
            TaskType: "DIE_TO_OVEN",
            Sublot: "S1",
            Area: area,
            Eqp: "EQ-1",
            Step: "STEP-1",
            Dates: new DateTimeOffset(2026, 8, 3, 1, 0, 0, TimeSpan.Zero),
            Package: "PKG-1",
            Status: status,
            MesLastSeenAt: new DateTimeOffset(2026, 8, 3, 1, 5, 0, TimeSpan.Zero),
            DisappearCount: status == "GONE" ? 2 : 0,
            LocationRisk: false,
            LocationRiskCode: null,
            CreatedAt: new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero),
            GoneAt: goneAt);

    private static void Click(AlertDetailWindow window, string buttonName) =>
        ((Button)window.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
