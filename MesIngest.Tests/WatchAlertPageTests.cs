using System.Windows;
using System.Windows.Controls;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchAlertPageTests
{
    [Fact]
    public void Alert_page_exposes_the_ticket_07_filters_window_columns_and_read_only_actions()
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
                var window = new MainWindow(
                    new MesIngestApiClient(http),
                    new WatchOptions { BaseUrl = "http://127.0.0.1:9", RefreshSeconds = 60 });

                Assert.Equal(
                    ["活动", "已解除"],
                    Items((ComboBox)window.FindName("AlertActivityFilter")));
                Assert.Equal(
                    new[] { string.Empty }.Concat(WatchAlertDraft.ProductionCodes),
                    Items((ComboBox)window.FindName("AlertCodeFilter")));
                Assert.Equal(
                    [string.Empty, "ERROR", "WARNING"],
                    Items((ComboBox)window.FindName("AlertSeverityFilter")));
                Assert.NotNull(window.FindName("AlertRangeFromFilter"));
                Assert.NotNull(window.FindName("AlertRangeToFilter"));
                Assert.NotNull(window.FindName("AlertQueryButton"));
                Assert.NotNull(window.FindName("AlertResetButton"));
                Assert.NotNull(window.FindName("AlertRefreshButton"));
                Assert.NotNull(window.FindName("AlertCancelButton"));
                Assert.NotNull(window.FindName("AlertPreviousButton"));
                Assert.NotNull(window.FindName("AlertNextButton"));

                var grid = (DataGrid)window.FindName("AlertsGrid");
                Assert.True(grid.IsReadOnly);
                Assert.Equal(
                    [
                        "Code",
                        "Severity",
                        "AlertId",
                        "last seen",
                        "first seen",
                        "TASK_TYPE",
                        "SUBLOT",
                        "DemandId",
                        "Message",
                        "OccurrenceCount",
                        "IsActive",
                        "ResolvedAt",
                    ],
                    grid.Columns.Select(column => column.Header?.ToString()).ToArray());
                Assert.Contains(
                    grid.ContextMenu.Items.OfType<MenuItem>(),
                    item => Equals(item.Header, "查看详情"));
                Assert.DoesNotContain(
                    grid.ContextMenu.Items.OfType<MenuItem>(),
                    item => new[] { "确认", "指派", "备注", "关闭", "工单" }
                        .Any(command => item.Header?.ToString()?.Contains(command, StringComparison.Ordinal) == true));

                var detail = new AlertDetailWindow(AlertDetailViewModel.From(Alert()));
                Assert.Equal(ResizeMode.CanResize, detail.ResizeMode);
                detail.Close();
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA alert page test did not finish");
        Assert.Null(caught);
    }

    private static string[] Items(ComboBox combo) =>
        combo.Items.Cast<object>().Select(item => item?.ToString() ?? string.Empty).ToArray();

    private static WatchAlertDto Alert() => new(
        "alert-1",
        "POLL_FAILURE",
        "ERROR",
        null,
        null,
        null,
        "message",
        "{}",
        DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"),
        DateTimeOffset.Parse("2026-08-08T09:00:00+08:00"),
        1,
        true,
        null,
        DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"));
}
