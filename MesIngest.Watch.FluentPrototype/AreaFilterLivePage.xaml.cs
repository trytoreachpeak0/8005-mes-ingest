using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MesIngest.Watch.FluentPrototype;

public partial class AreaFilterLivePage
{
    private static readonly string[] VariantNames =
    [
        "D — 主从 · 命令归左栏",
        "E — 顶部芯片 · 编辑器满宽",
        "F — 单列表 · 内联展开",
    ];

    private int _variantIndex;
    private bool _invalid;

    public AreaFilterLivePage()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void SelectVariant(string? variant)
    {
        _variantIndex = variant?.Trim().ToUpperInvariant() switch
        {
            "E" or "B" => 1,
            "F" or "C" => 2,
            _ => 0,
        };
        RenderVariant();
    }

    public void ApplyScenario(string scenario)
    {
        _invalid = scenario.Contains("invalid", StringComparison.OrdinalIgnoreCase);
        RenderVariant();
    }

    private void OnPreviousVariant(object sender, RoutedEventArgs e) => Cycle(-1);
    private void OnNextVariant(object sender, RoutedEventArgs e) => Cycle(1);

    private void OnToggleScenario(object sender, RoutedEventArgs e)
    {
        _invalid = !_invalid;
        RenderVariant();
    }

    private void Cycle(int delta)
    {
        _variantIndex = (_variantIndex + delta + VariantNames.Length) % VariantNames.Length;
        RenderVariant();
    }

    private void RenderVariant()
    {
        VariantD.Visibility = _variantIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        VariantE.Visibility = _variantIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        VariantF.Visibility = _variantIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        VariantLabel.Text = VariantNames[_variantIndex];
        VariantD.ApplyScenario(_invalid);
        VariantE.ApplyScenario(_invalid);
        VariantF.ApplyScenario(_invalid);
        ScenarioButton.Content = _invalid ? "切回有效态" : "切到非法态";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox or PasswordBox)
        {
            return;
        }

        if (e.Key == Key.Left)
        {
            Cycle(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            Cycle(1);
            e.Handled = true;
        }
    }
}
