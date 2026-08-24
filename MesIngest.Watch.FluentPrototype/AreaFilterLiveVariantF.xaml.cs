using System.Windows;

namespace MesIngest.Watch.FluentPrototype;

public partial class AreaFilterLiveVariantF
{
    private const string ValidContent = "N1-1\nN1-2\nN1-3\nN1-4\nN1-5\nN1-6\nN1-7";
    private const string InvalidContent = "N1-1\nN1-2\nN1-3\nN1-4\nN1-5\nN1-6\nN1-7\nN1-x";

    public AreaFilterLiveVariantF() => InitializeComponent();

    public void ApplyScenario(bool invalid)
    {
        EditorText.Text = invalid ? InvalidContent : ValidContent;
        LineNumbers.Text = invalid ? "1\n2\n3\n4\n5\n6\n7\n8\n9" : "1\n2\n3\n4\n5\n6\n7\n8";
        ValidSummary.Visibility = invalid ? Visibility.Collapsed : Visibility.Visible;
        InvalidSummary.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        SyncText.Text = invalid ? "已同步 · 15:09:04" : "已同步 · 15:07:57";
    }
}
