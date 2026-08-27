using System.Windows.Automation;
using System.Windows.Controls;

namespace MesIngest.Watch;

internal partial class WatchWorkspaceWindow
{
    private void ApplyLocalizedReadabilityAuditText()
    {
        var text = _displayLanguageState.Catalog.ReadabilityAudit;

        ReadabilityPageTitleText.Text = text.PageTitle;
        AutomationProperties.SetName(ReadabilityAuditPage, text.PageTitle);
        ReadabilityStateFilterLabel.Text = text.StateFilter;
        ReadabilityWorkTypeFilterLabel.Text = text.WorkTypeFilter;
        ReadabilityBlockerFilterLabel.Text = text.BlockerFilter;
        ReadabilitySearchFilterLabel.Text = text.SearchFilter;
        ReadabilityAreaFilterLabel.Text = text.AreaFilter;
        ReadabilityApplyFilterButton.Content = text.ApplyFilters;
        ReadabilityClearFilterButton.Content = text.ClearFilters;
        ReadabilityPreviousPageButton.Content = text.PreviousPage;
        ReadabilityNextPageButton.Content = text.NextPage;
        ReadabilityGoToPageButton.Content = text.GoToPage;
        ReadabilityOpenSeriesButton.Content = text.ViewSeries;
        ReadabilityCurrentObservationHeadingText.Text = text.CurrentObservation;
        ReadabilityCurrentBlockerHeadingText.Text = text.CurrentBlocker;
        ReadabilityQualificationHeadingText.Text = text.ExternalReadability;
        ReadabilityMissingSemanticsHeadingText.Text = text.MissingSemantics;
        ReadabilityValueSemanticsItems.ItemsSource = text.ValueSemantics;
        ReadabilityDeepEvidenceExpander.Header = text.DeepEvidence;
        ((HeaderedContentControl)ReadabilityDeepEvidenceTabs.Items[0]).Header = text.QualificationChecks;
        ((HeaderedContentControl)ReadabilityDeepEvidenceTabs.Items[1]).Header = text.BlockerEvidence;
        ((HeaderedContentControl)ReadabilityDeepEvidenceTabs.Items[2]).Header = text.RawObservations;

        UpdateReadabilityChoiceLabels(text);
        AutomationProperties.SetName(ReadabilityApplyFilterButton, text.ApplyFilters);
        AutomationProperties.SetName(ReadabilityPreviousPageButton, text.PreviousPage);
        AutomationProperties.SetName(ReadabilityNextPageButton, text.NextPage);
        AutomationProperties.SetName(ReadabilityGoToPageButton, text.GoToPage);
        AutomationProperties.SetName(ReadabilityCurrentObservationHeadingText, text.CurrentObservation);
        AutomationProperties.SetName(ReadabilityCurrentBlockerHeadingText, text.CurrentBlocker);
        AutomationProperties.SetName(ReadabilityQualificationHeadingText, text.ExternalReadability);
        AutomationProperties.SetName(ReadabilityMissingSemanticsHeadingText, text.MissingSemantics);
    }

    private void UpdateReadabilityChoiceLabels(WatchReadabilityAuditText text)
    {
        if (ReadabilityWorkTypeFilter.Items.OfType<ComboBoxItem>().FirstOrDefault() is { } allWorkTypes)
        {
            allWorkTypes.Content = text.AllWorkTypes;
        }

        foreach (var item in ReadabilityBlockerFilter.Items.OfType<ComboBoxItem>())
        {
            var code = item.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(code))
            {
                item.Content = text.AllBlockers;
                item.ToolTip = null;
                continue;
            }

            var meaning = text.DescribeBlocker(code);
            item.Content = $"{meaning.Description} · {meaning.RawCode}";
            item.ToolTip = meaning.Description;
        }
    }
}
