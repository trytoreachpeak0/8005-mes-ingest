using System.Windows;
using System.Windows.Controls;

namespace MesIngest.Watch.FluentPrototype;

public partial class SelectedShellView : UserControl
{
    public SelectedShellView() => InitializeComponent();

    public void SetData(PrototypeData data)
    {
        DataContext = data;
        TasksPage.SetData(data);
    }

    public void Navigate(string page)
    {
        var normalized = page.Trim().ToLowerInvariant();
        OverviewPage.Visibility = normalized is "overview" or "ov" ? Visibility.Visible : Visibility.Collapsed;
        TasksPage.Visibility = normalized is "tasks" or "task" or "ts" ? Visibility.Visible : Visibility.Collapsed;
        AlertsPage.Visibility = normalized is "alerts" or "alert" or "al" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = normalized is "settings" or "setting" or "st" ? Visibility.Visible : Visibility.Collapsed;

        if (OverviewPage.Visibility == Visibility.Collapsed
            && TasksPage.Visibility == Visibility.Collapsed
            && AlertsPage.Visibility == Visibility.Collapsed
            && SettingsPage.Visibility == Visibility.Collapsed)
        {
            OverviewPage.Visibility = Visibility.Visible;
        }

        OverviewNav.IsActive = OverviewPage.Visibility == Visibility.Visible;
        TasksNav.IsActive = TasksPage.Visibility == Visibility.Visible;
        AlertsNav.IsActive = AlertsPage.Visibility == Visibility.Visible;
        SettingsNav.IsActive = SettingsPage.Visibility == Visibility.Visible;
    }

    public void ApplyScenario(string scenario)
    {
        var normalized = scenario.Trim().ToLowerInvariant();
        OverviewPage.ApplyScenario(normalized);
        TasksPage.ApplyScenario(normalized);
        AlertsPage.ApplyScenario(normalized);
        SettingsPage.ApplyScenario(normalized);

        var offline = normalized == "offline";
        HostNav.Content = offline ? "Host 已断开" : "Host 已连接";
        HostNav.ToolTip = offline
            ? "Host 已断开 · 保留上次成功窗口"
            : "Host 已连接 · 契约兼容 · http://127.0.0.1:49837";
    }

    private void OnOverviewClick(object sender, RoutedEventArgs e) => Navigate("overview");
    private void OnTasksClick(object sender, RoutedEventArgs e) => Navigate("tasks");
    private void OnAlertsClick(object sender, RoutedEventArgs e) => Navigate("alerts");
    private void OnSettingsClick(object sender, RoutedEventArgs e) => Navigate("settings");
}
