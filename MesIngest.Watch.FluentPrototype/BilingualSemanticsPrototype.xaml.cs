using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MesIngest.Watch.FluentPrototype;

public partial class BilingualSemanticsPrototype : UserControl
{
    private readonly string[] _variants = ["A", "B", "C"];
    private string _variant = "A";
    private bool _english;

    public BilingualSemanticsPrototype()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyState();
            Focus();
        };
    }

    public void Initialize(string variant, string scenario)
    {
        _variant = _variants.Contains(variant.Trim().ToUpperInvariant())
            ? variant.Trim().ToUpperInvariant()
            : "A";
        _english = scenario.Contains("english", StringComparison.OrdinalIgnoreCase)
            || scenario.Contains("en", StringComparison.OrdinalIgnoreCase);
        if (IsLoaded)
        {
            ApplyState();
        }
    }

    private void ApplyState()
    {
        VariantA.Visibility = _variant == "A" ? Visibility.Visible : Visibility.Collapsed;
        VariantB.Visibility = _variant == "B" ? Visibility.Visible : Visibility.Collapsed;
        VariantC.Visibility = _variant == "C" ? Visibility.Visible : Visibility.Collapsed;

        PrototypeLocalization.Apply(this, _english);
        var variantKey = $"{_variant}.Name";
        ReviewVariantLabel.Text = PrototypeLocalization.Get(variantKey, _english);
        VariantIntent.Text = PrototypeLocalization.Get(variantKey, _english);
        StateSummary.Text = PrototypeLocalization.Get("State.SummaryZh", _english);
        ReviewLanguageLabel.Text = PrototypeLocalization.Get(_english ? "Review.LanguageEn" : "Review.LanguageZh", _english);
        LanguageButton.Content = PrototypeLocalization.Get(_english ? "Review.SwitchChinese" : "Review.SwitchEnglish", _english);
        ObservationGridA.Columns = 2;
        MissingGridA.Columns = _english ? 2 : 3;
    }

    private void Cycle(int direction)
    {
        var current = Array.IndexOf(_variants, _variant);
        _variant = _variants[(current + direction + _variants.Length) % _variants.Length];
        ApplyState();
    }

    private void OnPreviousVariant(object sender, RoutedEventArgs e) => Cycle(-1);
    private void OnNextVariant(object sender, RoutedEventArgs e) => Cycle(1);

    private void OnToggleLanguage(object sender, RoutedEventArgs e)
    {
        _english = !_english;
        ApplyState();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
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
