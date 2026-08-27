using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MesIngest.Watch;

internal partial class WatchWorkspaceWindow
{
    internal WatchV2DataView? ActiveAutoRefreshView => _autoRefresh.ActiveView;

    private void InitializeTicket22Pages()
    {
        InitializeErrorSearchPage();
        InitializeCurrentIngestAttentionPage();
    }

    private static int ReadPageSize(ComboBox comboBox, int maximum, string pageName)
    {
        var value = ReadSingleChoiceValue(comboBox);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize)
            && pageSize is >= 1
            && pageSize <= maximum
            ? pageSize
            : throw new ArgumentException($"{pageName}每页数量必须介于 1 和 {maximum}。", nameof(comboBox));
    }

    private static IReadOnlyList<string> ReadChoiceValues(ComboBox comboBox)
    {
        var selectedItem = comboBox.SelectedItem as ComboBoxItem;
        var selectedContent = selectedItem?.Content?.ToString();
        var value = !comboBox.IsEditable
            || (selectedItem is not null
                && string.Equals(comboBox.Text, selectedContent, StringComparison.Ordinal))
                    ? selectedItem?.Tag?.ToString()
                        ?? selectedContent
                        ?? comboBox.Text
                    : comboBox.Text;
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith("全部", StringComparison.Ordinal))
        {
            return [];
        }

        return value
            .Split([',', ';', '，'], StringSplitOptions.RemoveEmptyEntries)
            .Select(candidate => candidate.Trim())
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadSingleChoiceValue(ComboBox comboBox)
    {
        if (comboBox.IsEditable && !string.IsNullOrWhiteSpace(comboBox.Text))
        {
            return comboBox.Text.Trim();
        }

        return comboBox.SelectedValue?.ToString()
            ?? (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? comboBox.Text;
    }

    private static void SelectChoice(ComboBox comboBox, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            if (comboBox.IsEditable)
            {
                comboBox.SelectedIndex = -1;
                comboBox.Text = comboBox.Tag?.ToString() ?? string.Empty;
            }
            else
            {
                comboBox.SelectedIndex = 0;
            }
            return;
        }

        if (values.Count == 1)
        {
            foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
            {
                var candidate = item.Tag?.ToString() ?? item.Content?.ToString();
                if (string.Equals(candidate, values[0], StringComparison.Ordinal))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }
        }

        comboBox.SelectedIndex = -1;
        if (comboBox.IsEditable)
        {
            comboBox.Text = string.Join(", ", values);
        }
    }

    private string BuildPageEndpoint(WatchV2WorkspaceState state, string endpointPath)
    {
        var baseUrl = string.IsNullOrWhiteSpace(state.BaseUrl)
            ? _currentHostSettings.BaseUrl
            : state.BaseUrl;
        return $"{baseUrl.TrimEnd('/')}{endpointPath}";
    }

    private void SetHeaderStatus(
        Border pill,
        TextBlock text,
        string automationLabel,
        string value,
        string styleKey)
    {
        pill.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        text.Text = value;
        var automationName = $"{automationLabel}：{value}";
        AutomationProperties.SetName(pill, automationName);
        AutomationProperties.SetName(text, automationName);
        pill.ToolTip = automationName;
    }

    private void ReflowTicket22Pages(double contentWidth, bool useOuterScrolling)
    {
        var stack = contentWidth < (double)FindResource("Ticket22ThreeCardStackBreakpoint");
        ReflowErrorSearch(stack, useOuterScrolling);
        ReflowCurrentAttention(stack, useOuterScrolling);
    }

}
