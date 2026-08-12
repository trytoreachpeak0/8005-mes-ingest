using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MesIngest.Watch.FluentPrototype;

public partial class SelectedOverviewPage : UserControl
{
    private static readonly string[] VariantNames =
    [
        "A — 分页卡片 + 重点动态",
        "B — 待关注优先",
        "C — 分页运行总表",
    ];

    private int _variantIndex;

    public SelectedOverviewPage()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void SelectVariant(string? variant)
    {
        _variantIndex = variant?.Trim().ToUpperInvariant() switch { "B" => 1, "C" => 2, _ => 0 };
        RenderVariant();
    }

    public void SetReviewSwitcherVisible(bool isVisible) => ReviewSwitcher.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

    public void ApplyScenario(string scenario)
    {
        var offline = scenario.Contains("offline", StringComparison.OrdinalIgnoreCase);
        OfflineInfo.IsOpen = offline;
        PageContextText.Text = offline ? "各分页运行摘要 · 最后成功 12:39:44 · 自动刷新 10 秒" : "各分页运行摘要 · 最近成功 12:42:16 · 自动刷新 10 秒";
        HostStateText.Text = offline ? "Host 已断开" : "Host 已连接";
        HostStateText.Foreground = offline ? (Brush)FindResource("WatchCriticalBrush") : (Brush)FindResource("WatchSuccessBrush");
        HostIcon.Foreground = HostStateText.Foreground;
        HostIcon.Symbol = offline ? Wpf.Ui.Controls.SymbolRegular.CloudOff24 : Wpf.Ui.Controls.SymbolRegular.CloudCheckmark24;
    }

    private void OnPreviousVariant(object sender, RoutedEventArgs e) => Cycle(-1);
    private void OnNextVariant(object sender, RoutedEventArgs e) => Cycle(1);
    private void Cycle(int delta) { _variantIndex = (_variantIndex + delta + VariantNames.Length) % VariantNames.Length; RenderVariant(); }
    private void RenderVariant()
    {
        VariantA.Visibility = _variantIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        VariantB.Visibility = _variantIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        VariantC.Visibility = _variantIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        VariantLabel.Text = VariantNames[_variantIndex];
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.OriginalSource is PasswordBox) return;
        if (e.Key == Key.Left) { Cycle(-1); e.Handled = true; }
        else if (e.Key == Key.Right) { Cycle(1); e.Handled = true; }
    }
}
