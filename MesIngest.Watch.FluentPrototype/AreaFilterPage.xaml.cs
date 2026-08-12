using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MesIngest.Watch.FluentPrototype;

public partial class AreaFilterPage
{
    private static readonly string[] VariantNames =
    [
        "A — 主从编辑器",
        "B — 状态总览 + 编辑抽屉",
        "C — 文件工作区",
    ];

    private int _variantIndex;

    public AreaFilterPage()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void SelectVariant(string? variant)
    {
        _variantIndex = variant?.Trim().ToUpperInvariant() switch
        {
            "B" => 1,
            "C" => 2,
            _ => 0,
        };
        RenderVariant();
    }

    private void OnPreviousVariant(object sender, RoutedEventArgs e) => Cycle(-1);
    private void OnNextVariant(object sender, RoutedEventArgs e) => Cycle(1);

    private void Cycle(int delta)
    {
        _variantIndex = (_variantIndex + delta + VariantNames.Length) % VariantNames.Length;
        RenderVariant();
    }

    private void RenderVariant()
    {
        VariantA.Visibility = _variantIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        VariantB.Visibility = _variantIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        VariantC.Visibility = _variantIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        VariantLabel.Text = VariantNames[_variantIndex];
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.OriginalSource is PasswordBox)
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
