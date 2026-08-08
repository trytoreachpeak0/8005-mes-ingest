using MesIngest.Watch;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

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
            var taskType = (ComboBox)window.FindName("DemandTaskTypeFilter");
            var sublot = (TextBox)window.FindName("DemandSublotFilter");
            var demandId = (TextBox)window.FindName("DemandIdFilter");
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

    [Fact]
    public void Window_restores_versioned_geometry_and_reset_layout_returns_to_defaults() =>
        RunInSta(() =>
        {
            var layoutPath = Path.Combine(Path.GetTempPath(), $"watch-layout-{Guid.NewGuid():N}.json");
            WatchLayoutPreferences.Save(layoutPath, new WatchWindowLayout(1360, 840, 0.56));
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
            var window = new MainWindow(
                new MesIngestApiClient(http),
                new WatchOptions { BaseUrl = "http://127.0.0.1:9", RefreshSeconds = 60 },
                layoutPreferencesPath: layoutPath);

            Assert.Equal(1360, window.Width);
            Assert.Equal(840, window.Height);
            Assert.Equal(0.56, ((RowDefinition)window.FindName("DemandsRow")).Height.Value, 3);

            ((Button)window.FindName("ResetLayoutButton")).RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(WatchWindowLayout.Default.WindowWidth, window.Width);
            Assert.Equal(WatchWindowLayout.Default.WindowHeight, window.Height);
            Assert.Equal(
                WatchWindowLayout.Default.DemandShare,
                ((RowDefinition)window.FindName("DemandsRow")).Height.Value,
                3);
            window.Close();
            Assert.Equal(WatchWindowLayout.Default, WatchLayoutPreferences.Load(layoutPath));
            File.Delete(layoutPath);
        });

    [Fact]
    public void Applying_host_persists_address_timeout_and_only_the_external_credential_reference() =>
        RunInSta(() =>
        {
            var preferencesPath = Path.Combine(
                Path.GetTempPath(),
                $"watch-connection-{Guid.NewGuid():N}.json");
            using var http = new HttpClient { BaseAddress = new Uri("http://old-host:5088/") };
            var window = new MainWindow(
                new MesIngestApiClient(http),
                new WatchOptions { BaseUrl = "http://old-host:5088", RefreshSeconds = 60 },
                layoutPreferencesPath: Path.Combine(Path.GetTempPath(), $"watch-layout-{Guid.NewGuid():N}.json"),
                connectionPreferencesPath: preferencesPath,
                hostAdapterFactory: _ => new ImmediateHostAdapter());
            ((TextBox)window.FindName("HostBaseUrlInput")).Text = "https://new-host.factory.test:5088";
            ((PasswordBox)window.FindName("HostCredentialInput")).Password = "plain-secret-never-persist";
            ((TextBox)window.FindName("RequestTimeoutInput")).Text = "45";

            ((Button)window.FindName("ApplyHostButton")).RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));

            var saved = WatchConnectionPreferencesStore.Load(
                preferencesPath,
                WatchConnectionPreferences.Default);
            Assert.Equal("https://new-host.factory.test:5088", saved.BaseUrl);
            Assert.Equal(45, saved.RequestTimeoutSeconds);
            Assert.Equal(WatchCredentialReference.ExternalConfiguration, saved.CredentialReference);
            Assert.DoesNotContain(
                "plain-secret-never-persist",
                File.ReadAllText(preferencesPath),
                StringComparison.Ordinal);
            window.Close();
            File.Delete(preferencesPath);
        });

    [Fact]
    public void Critical_controls_expose_stable_semantic_automation_names_and_keyboard_focus() =>
        RunInSta(() =>
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
            var window = new MainWindow(
                new MesIngestApiClient(http),
                new WatchOptions { BaseUrl = "http://127.0.0.1:9", RefreshSeconds = 60 },
                layoutPreferencesPath: Path.Combine(Path.GetTempPath(), $"watch-v2-{Guid.NewGuid():N}.json"));
            var expectedNames = new Dictionary<string, string>
            {
                ["PrimaryNavigation"] = "主导航",
                ["CurrentHostContextText"] = "当前 Host 基址",
                ["ErrorBannerText"] = "连接错误",
                ["WarningBannerText"] = "连接警告",
                ["AlertActivityFilter"] = "IngestAlert 状态筛选",
                ["AlertQueryButton"] = "查询 IngestAlert",
                ["AlertsGrid"] = "IngestAlert 单页结果",
                ["HostBaseUrlInput"] = "Host 基址",
                ["HostCredentialInput"] = "只读 API 凭据",
                ["RequestTimeoutInput"] = "全局请求超时秒数",
                ["ApplyHostButton"] = "应用 Host 设置",
                ["DemandTaskTypeFilter"] = "TransportDemand TASK_TYPE 筛选",
                ["DemandQueryButton"] = "查询 TransportDemand",
                ["DemandsGrid"] = "TransportDemand 单页结果",
                ["PanesSplitter"] = "调整 TransportDemand 表格和详情区域",
            };

            foreach (var (controlName, automationName) in expectedNames)
            {
                var control = Assert.IsAssignableFrom<DependencyObject>(window.FindName(controlName));
                Assert.Equal(automationName, AutomationProperties.GetName(control));
                if (control is Control focusableControl)
                {
                    Assert.True(focusableControl.Focusable, $"{controlName} must be keyboard focusable");
                }
            }

            var navigation = (ListBox)window.FindName("PrimaryNavigation");
            Assert.Equal(
                new[] { "概览导航", "TransportDemand 导航", "IngestAlert 导航", "设置导航" },
                navigation.Items.Cast<ListBoxItem>().Select(AutomationProperties.GetName));
            window.Close();
        });

    [Fact]
    public void Narrow_dpi_equivalent_width_uses_wrapping_headers_and_shared_visual_tokens() =>
        RunInSta(() =>
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
            var window = new MainWindow(
                new MesIngestApiClient(http),
                new WatchOptions { BaseUrl = "http://127.0.0.1:9", RefreshSeconds = 60 },
                layoutPreferencesPath: Path.Combine(Path.GetTempPath(), $"watch-v2-{Guid.NewGuid():N}.json"));

            Assert.Equal(720, window.MinWidth);
            Assert.Equal(600, window.MinHeight);
            Assert.Equal("Microsoft YaHei UI", window.FontFamily.Source);
            Assert.Equal(
                System.Windows.Media.TextFormattingMode.Display,
                System.Windows.Media.TextOptions.GetTextFormattingMode(window));
            Assert.Equal(
                System.Windows.Media.TextRenderingMode.ClearType,
                System.Windows.Media.TextOptions.GetTextRenderingMode(window));
            Assert.IsType<WrapPanel>(window.FindName("OverviewHeaderPanel"));
            Assert.IsType<WrapPanel>(window.FindName("AlertHeaderPanel"));
            Assert.IsType<WrapPanel>(window.FindName("DemandHeaderPanel"));
            foreach (var token in new[]
                     {
                         "WatchSurfaceBrush",
                         "WatchBorderBrush",
                         "WatchTextBrush",
                         "WatchMutedTextBrush",
                         "WatchAccentBrush",
                         "WatchWarningBrush",
                         "WatchErrorBrush",
                     })
            {
                Assert.NotNull(window.FindResource(token));
            }
            window.Close();
        });

    [Fact]
    public void Composition_restores_saved_host_and_timeout_but_keeps_credential_in_external_configuration() =>
        RunInSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"watch-composition-{Guid.NewGuid():N}");
            var connectionPath = Path.Combine(root, "connection.json");
            WatchConnectionPreferencesStore.Save(
                connectionPath,
                new WatchConnectionPreferences(
                    "https://saved-host.factory.test:5088",
                    60,
                    WatchCredentialReference.ExternalConfiguration));
            var options = new WatchOptions
            {
                BaseUrl = "http://configured-host:5088",
                SharedSecret = "external-secret",
                RequestTimeoutSeconds = 30,
            };
            using var composition = WatchApplicationComposition.Create(
                options,
                _ => new ImmediateHostAdapter(),
                logDirectory: Path.Combine(root, "logs"),
                layoutPreferencesPath: Path.Combine(root, "layout.json"),
                connectionPreferencesPath: connectionPath);
            var window = composition.CreateMainWindow();

            Assert.Equal(
                "https://saved-host.factory.test:5088",
                ((TextBox)window.FindName("HostBaseUrlInput")).Text);
            Assert.Equal("60", ((TextBox)window.FindName("RequestTimeoutInput")).Text);
            Assert.Equal("external-secret", ((PasswordBox)window.FindName("HostCredentialInput")).Password);
            window.Close();
            Directory.Delete(root, recursive: true);
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
