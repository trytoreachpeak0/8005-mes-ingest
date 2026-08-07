using MesIngest.Watch;
using System.Windows;
using System.Windows.Controls;

namespace MesIngest.Tests;

public class WatchV2ShellTests
{
    [Fact]
    public void Shell_starts_on_overview_and_exposes_only_the_four_approved_pages() =>
        RunInSta(() =>
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
            var window = new MainWindow(
                new MesIngestApiClient(http),
                new WatchOptions { BaseUrl = "http://127.0.0.1:9", RefreshSeconds = 60 },
                layoutPreferencesPath: Path.Combine(Path.GetTempPath(), $"watch-v2-{Guid.NewGuid():N}.json"));

            var navigation = (ListBox)window.FindName("PrimaryNavigation");
            var labels = navigation.Items
                .OfType<ListBoxItem>()
                .Select(item => item.Content?.ToString())
                .ToArray();
            var overview = (FrameworkElement)window.FindName("OverviewPage");

            Assert.Equal(
                new[] { "概览", "MES 任务 / TransportDemand", "IngestAlert", "设置" },
                labels);
            Assert.Equal(0, navigation.SelectedIndex);
            Assert.Equal(Visibility.Visible, overview.Visibility);
            Assert.DoesNotContain(labels, label => label is "性能分析" or "诊断" or "遥测");

            var demandsPage = (FrameworkElement)window.FindName("DemandsPage");
            var alertsPage = (FrameworkElement)window.FindName("AlertsPage");
            navigation.SelectedIndex = 1;
            Assert.Equal(Visibility.Visible, demandsPage.Visibility);
            Assert.Equal(Visibility.Collapsed, alertsPage.Visibility);
            navigation.SelectedIndex = 2;
            Assert.Equal(Visibility.Collapsed, demandsPage.Visibility);
            Assert.Equal(Visibility.Visible, alertsPage.Visibility);

            window.Close();
        });

    [Fact]
    public void Settings_masks_credential_and_exposes_legal_timeout_range() =>
        RunInSta(() =>
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
            var window = new MainWindow(
                new MesIngestApiClient(http),
                new WatchOptions { BaseUrl = "http://127.0.0.1:9", RefreshSeconds = 60 },
                layoutPreferencesPath: Path.Combine(Path.GetTempPath(), $"watch-v2-{Guid.NewGuid():N}.json"));

            var credential = (PasswordBox)window.FindName("HostCredentialInput");
            var timeout = (TextBox)window.FindName("RequestTimeoutInput");

            Assert.Equal('\u25cf', credential.PasswordChar);
            Assert.Equal("30", timeout.Text);
            Assert.Equal("1–300 秒", timeout.ToolTip);

            window.Close();
        });

    [Fact]
    public void Applying_host_immediately_clears_old_business_state_and_returns_to_overview() =>
        RunInSta(() =>
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
            var window = new MainWindow(
                new MesIngestApiClient(http),
                new WatchOptions { BaseUrl = "http://old-host:5088", RefreshSeconds = 60 },
                layoutPreferencesPath: Path.Combine(Path.GetTempPath(), $"watch-v2-{Guid.NewGuid():N}.json"),
                hostAdapterFactory: _ => new ImmediateHostAdapter());

            var navigation = (ListBox)window.FindName("PrimaryNavigation");
            var demands = (DataGrid)window.FindName("DemandsGrid");
            var alerts = (DataGrid)window.FindName("AlertsGrid");
            var taskType = (ComboBox)window.FindName("VisibleTaskTypeFilter");
            var sublot = (TextBox)window.FindName("VisibleSublotFilter");
            var demandId = (TextBox)window.FindName("VisibleDemandIdFilter");
            var oldDemand = new object();
            var oldAlert = new object();
            demands.ItemsSource = new[] { oldDemand };
            alerts.ItemsSource = new[] { oldAlert };
            demands.SelectedItem = oldDemand;
            alerts.SelectedItem = oldAlert;
            taskType.SelectedValue = "DIE_TO_OVEN";
            sublot.Text = "OLD_SUBLOT";
            demandId.Text = "old-demand";
            navigation.SelectedIndex = 3;

            ((TextBox)window.FindName("HostBaseUrlInput")).Text = "http://new-host:5088";
            ((PasswordBox)window.FindName("HostCredentialInput")).Password = "new-secret";
            ((TextBox)window.FindName("RequestTimeoutInput")).Text = "20";
            ((Button)window.FindName("ApplyHostButton")).RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(0, navigation.SelectedIndex);
            Assert.Empty(demands.Items);
            Assert.Empty(alerts.Items);
            Assert.Null(demands.SelectedItem);
            Assert.Null(alerts.SelectedItem);
            Assert.Equal(string.Empty, taskType.SelectedValue?.ToString());
            Assert.Equal(string.Empty, sublot.Text);
            Assert.Equal(string.Empty, demandId.Text);
            Assert.StartsWith(
                "尚无成功轮询",
                ((TextBlock)window.FindName("OverviewPollHealthText")).Text,
                StringComparison.Ordinal);

            window.Close();
        });

    private static void RunInSta(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA test did not finish");
        Assert.Null(caught);
    }

    private sealed class ImmediateHostAdapter : WatchHostQueryAdapterStub
    {
        public override Task VerifyContractAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task<WatchPollHealthDto?> FetchPollHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult<WatchPollHealthDto?>(null);
    }
}
