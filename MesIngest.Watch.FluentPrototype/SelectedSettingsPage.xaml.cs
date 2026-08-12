using System.Windows.Controls;

namespace MesIngest.Watch.FluentPrototype;

public partial class SelectedSettingsPage : UserControl
{
    public SelectedSettingsPage() => InitializeComponent();

    public void ApplyScenario(string scenario)
    {
        var invalid = scenario is "validation" or "validation-error";
        ValidationInfo.IsOpen = invalid;
        TimeoutInput.Text = invalid ? "0" : "30";
    }
}
