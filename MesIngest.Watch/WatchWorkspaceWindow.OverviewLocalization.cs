using System.Windows.Automation;

namespace MesIngest.Watch;

internal partial class WatchWorkspaceWindow
{
    private void ApplyLocalizedOverviewText()
    {
        var text = _displayLanguageState.Catalog.Overview;
        OverviewPageTitleText.Text = text.PageTitle;
        SeriesSummaryTitleText.Text = text.Series;
        ReadabilitySummaryTitleText.Text = text.Readability;
        ErrorsSummaryTitleText.Text = text.Errors;
        AreaSummaryTitleText.Text = text.Area;
        AttentionSummaryTitleText.Text = text.Attention;
        SeriesSummaryAction.Content = text.View;
        ReadabilitySummaryAction.Content = text.View;
        ErrorsSummaryAction.Content = text.View;
        AttentionSummaryAction.Content = text.View;
        AreaSummaryManageButton.Content = text.Manage;
        ReadableSummaryAction.Content = text.Readable;
        NotReadableSummaryAction.Content = text.NotReadable;
        ActiveErrorsSummaryAction.Content = text.Active;
        PriorErrorsSummaryAction.Content = text.PriorSevenDays;
        RecentActivityHeadingText.Text = text.RecentHighlights;
        RecentActivityHelpText.Text = text.RecentHighlightsHelp;
        RecentActivityOrderText.Text = text.NewestFirst;
        OverviewScopeHeadingText.Text = text.CurrentScope;
        OverviewScopeHelpText.Text = text.ScopeHelp;
        OverviewScopeSeriesLabel.Text = text.Series;
        OverviewScopeReadabilityLabel.Text = text.Readability;
        OverviewManageAreaButton.Content = text.ManageAreaFilters;

        AutomationProperties.SetName(OverviewPage, text.PageTitle);
        AutomationProperties.SetName(SeriesSummaryCard, text.Series);
        AutomationProperties.SetName(ReadabilitySummaryCard, text.Readability);
        AutomationProperties.SetName(ErrorsSummaryCard, text.Errors);
        AutomationProperties.SetName(AreaSummaryCard, text.Area);
        AutomationProperties.SetName(AttentionSummaryCard, text.Attention);
        AutomationProperties.SetName(RecentActivityItems, text.RecentHighlights);
        AutomationProperties.SetName(SeriesSummaryAction, text.ViewAllFirstPage(text.Series));
        AutomationProperties.SetName(ReadabilitySummaryAction, text.ViewAllFirstPage(text.Readability));
        AutomationProperties.SetName(ErrorsSummaryAction, text.ViewAllFirstPage(text.Errors));
        AutomationProperties.SetName(AttentionSummaryAction, text.ViewAllFirstPage(text.Attention));
        AutomationProperties.SetName(AreaSummaryManageButton, $"{text.Manage} {text.Area}");
        AutomationProperties.SetName(OverviewManageAreaButton, text.ManageAreaFilters);
    }
}
