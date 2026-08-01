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
    public void Reappear_actions_copy_and_locate_previous_and_new_ids_without_ambiguity()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                var copied = new List<string>();
                var located = new List<string>();
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
                Assert.Equal(["previous-gone-id", "new-visible-id"], located);
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

    private static void Click(AlertDetailWindow window, string buttonName) =>
        ((Button)window.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
