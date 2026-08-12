using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace MesIngest.Watch.FluentPrototype;

public partial class SelectedAlertsPage : UserControl
{
    public SelectedAlertsPage() => InitializeComponent();

    public void ApplyScenario(string scenario)
    {
        var resolved = scenario == "resolved";
        PageContextText.Text = resolved
            ? "已恢复历史 · 最近成功 12:42:15 · 自动刷新 30 秒"
            : "活动告警 · 最近成功 12:42:15 · 自动刷新 30 秒";
        ActiveButton.Appearance = resolved ? ControlAppearance.Transparent : ControlAppearance.Primary;
        ResolvedButton.Appearance = resolved ? ControlAppearance.Primary : ControlAppearance.Transparent;
        QueueTitle.Text = resolved ? "已恢复告警" : "活动告警";
        QueueCount.Text = resolved ? "12" : "3";
        ClearConditionsButton.IsEnabled = resolved;
        ClearConditionsButton.Opacity = resolved ? 1 : 0.45;
        QueueCountBorder.Background = FindResource(resolved ? "WatchSuccessSoftBrush" : "WatchCriticalSoftBrush") as System.Windows.Media.Brush;
        QueueCount.Foreground = FindResource(resolved ? "WatchSuccessBrush" : "WatchCriticalBrush") as System.Windows.Media.Brush;
        PaginationText.Text = resolved
            ? "第 1 / 2 页 · 共 12 条"
            : "第 1 / 8 页 · 共 76 条";
        LifecycleText.Text = resolved
            ? "已自动恢复 · ResolvedAt 12:41:58 · 共 4 次"
            : "活动 · 最近出现 12:41:52 · 共 4 次";
        AlertInfo.Severity = resolved ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        AlertInfo.Title = resolved ? "异常条件已消失" : "投影保持不变";
    }
}
