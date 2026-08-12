using System.Windows.Controls;
using System.Windows.Data;
using System.Windows;
using Wpf.Ui.Controls;

namespace MesIngest.Watch.FluentPrototype;

public partial class SelectedTasksPage : UserControl
{
    private PrototypeData? _data;

    public SelectedTasksPage() => InitializeComponent();

    public void SetData(PrototypeData data) => _data = data;

    public void ApplyScenario(string scenario)
    {
        var gone = scenario == "gone";
        PageContextText.Text = gone
            ? "GONE · 最近成功 12:42:14 · 自动刷新 30 秒"
            : "VISIBLE · 最近成功 12:42:14 · 自动刷新 10 秒";
        VisibleButton.Appearance = gone ? ControlAppearance.Transparent : ControlAppearance.Primary;
        GoneButton.Appearance = gone ? ControlAppearance.Primary : ControlAppearance.Transparent;
        ClearConditionsButton.IsEnabled = gone;
        ClearConditionsButton.Opacity = gone ? 1 : 0.45;
        WindowCountText.Text = gone
            ? "已消失任务窗口 · 本页 2 行 · 已选 1 行"
            : "当前任务窗口 · 本页 5 行 · 已选 1 行";
        if (_data is not null)
        {
            BindingOperations.ClearBinding(TasksGrid, ItemsControl.ItemsSourceProperty);
            TasksGrid.ItemsSource = gone ? _data.GoneTasks : _data.Tasks;
        }
        DemandIdText.Text = gone ? "journey-gone-008" : "journey-visible-001";
        LocalDemandIdText.Text = DemandIdText.Text;
        DisappearCountText.Text = gone ? "3" : "0";
        PaginationText.Text = gone
            ? "第 1 / 3 页 · 共 202 条"
            : "第 1 / 24 页 · 共 2,347 条";
        Page4Button.Visibility = gone ? Visibility.Collapsed : Visibility.Visible;
        Page5Button.Visibility = gone ? Visibility.Collapsed : Visibility.Visible;
        PageEllipsis.Visibility = gone ? Visibility.Collapsed : Visibility.Visible;
        LastPageButton.Visibility = gone ? Visibility.Collapsed : Visibility.Visible;
        DemandStatusText.Text = gone
            ? "GONE · 消失于 12:31:08 · disappearCount 3"
            : "VISIBLE · 最近观察 12:42:14 · 无位置风险";
        DemandStatusBorder.Background = FindResource(gone ? "WatchWarningSoftBrush" : "WatchSuccessSoftBrush") as System.Windows.Media.Brush;
        DemandStatusText.Foreground = FindResource(gone ? "WatchWarningBrush" : "WatchSuccessBrush") as System.Windows.Media.Brush;
    }
}
