using System.Windows;
using System.Windows.Controls;
using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 16 seam: AlertDetailWindow user actions for the previous GONE and
/// new VISIBLE TransportDemand instances of a REAPPEAR_AFTER_GONE alert.
/// </summary>
public class AlertDetailWindowActionTests
{
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
