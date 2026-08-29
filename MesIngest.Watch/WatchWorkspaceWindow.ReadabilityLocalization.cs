using System.Windows.Automation;
using System.Windows.Controls;

namespace MesIngest.Watch;

internal partial class WatchWorkspaceWindow
{
    private void ApplyLocalizedReadabilityAuditText()
    {
        var text = _displayLanguageState.Catalog.ReadabilityAudit;

        ReadabilityPageTitleText.Text = text.PageTitle;
        ReadabilityPerPageLabel.Text = text.PerPage;
        ReadabilityFacetOverlapHelpText.Text = text.FacetOverlapHelp;
        AutomationProperties.SetName(ReadabilityAuditPage, text.PageAutomationName);
        AutomationProperties.SetName(ReadabilityAuditGrid, text.MasterListAutomationName);
        ReadabilityStateFilterLabel.Text = text.StateFilter;
        ReadabilityWorkTypeFilterLabel.Text = text.WorkTypeFilter;
        ReadabilityBlockerFilterLabel.Text = text.BlockerFilter;
        ReadabilitySearchFilterLabel.Text = text.SearchFilter;
        ReadabilityAreaFilterLabel.Text = text.AreaFilter;
        AutomationProperties.SetName(
            ReadabilityAreaProfileSelector,
            text.AreaSelectorAutomationName);
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
        var columns = _displayLanguageState.Catalog.Columns;
        ReadabilityAreaLabelText.Text = columns.Area;
        ReadabilityEqpLabelText.Text = columns.Eqp;
        ReadabilityStepLabelText.Text = columns.Step;
        ReadabilitySourceDateLabelText.Text = columns.SourceDate;
        ReadabilityPackageLabelText.Text = columns.Package;
        ReadabilityDeepEvidenceExpander.Header = text.DeepEvidence;
        ((HeaderedContentControl)ReadabilityDeepEvidenceTabs.Items[0]).Header = text.QualificationChecks;
        ((HeaderedContentControl)ReadabilityDeepEvidenceTabs.Items[1]).Header = text.BlockerEvidence;
        ((HeaderedContentControl)ReadabilityDeepEvidenceTabs.Items[2]).Header = text.RawObservations;

        UpdateReadabilityChoiceLabels(text);
        AutomationProperties.SetName(ReadabilityApplyFilterButton, text.ApplyFiltersAutomationName);
        AutomationProperties.SetName(ReadabilityClearFilterButton, text.ClearFiltersAutomationName);
        AutomationProperties.SetName(ReadabilityPreviousPageButton, text.PreviousPageAutomationName);
        AutomationProperties.SetName(ReadabilityNextPageButton, text.NextPageAutomationName);
        AutomationProperties.SetName(ReadabilityGoToPageButton, text.GoToPageAutomationName);
        AutomationProperties.SetName(ReadabilityOpenSeriesButton, text.ViewSeriesAutomationName);
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
            item.Content = text.CodeWithMeaning(meaning);
            item.ToolTip = meaning.Description;
        }
    }
}
