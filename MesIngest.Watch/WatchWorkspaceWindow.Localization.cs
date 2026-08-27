using System.Windows.Automation;

namespace MesIngest.Watch;

internal partial class WatchWorkspaceWindow
{
    private void OnDisplayLanguageChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var focusedElement = System.Windows.Input.Keyboard.FocusedElement;
        var areaEditorViewState = AreaProfileEditor is null
            ? null
            : CaptureAreaProfileEditorViewState();
        var demandSeriesVerticalOffset = DemandSeriesScrollViewer.VerticalOffset;
        var demandSeriesHorizontalOffset = DemandSeriesScrollViewer.HorizontalOffset;
        ApplyLocalizedShellAndSettingsText();
        ApplyLocalizedAreaFilterStaticText();
        RenderAreaProfiles(editorViewState: areaEditorViewState);
        InitializeIntervalInputs();
        DisplayLanguageInput.SelectedValue = _displayLanguageState.Current;

        RenderWorkspace();
        DemandSeriesScrollViewer.ScrollToVerticalOffset(demandSeriesVerticalOffset);
        DemandSeriesScrollViewer.ScrollToHorizontalOffset(demandSeriesHorizontalOffset);

        if (focusedElement is System.Windows.IInputElement focusTarget
            && focusTarget.Focusable)
        {
            focusTarget.Focus();
        }
    }

    private void ApplyLocalizedShellAndSettingsText()
    {
        var catalog = _displayLanguageState.Catalog;
        var shell = catalog.Shell;
        var settings = catalog.Settings;
        ApplyLocalizedDemandSeriesText(catalog.DemandSeries);

        AutomationProperties.SetName(WorkspaceNavigation, shell.PrimaryNavigationName);
        ApplyNavigationText(OverviewNavigationItem, shell.Overview, shell);
        ApplyNavigationText(DemandSeriesNavigationItem, shell.DemandSeries, shell);
        ApplyNavigationText(ReadabilityAuditNavigationItem, shell.ReadabilityAudit, shell);
        ApplyNavigationText(ErrorSearchNavigationItem, shell.ErrorSearch, shell);
        ApplyNavigationText(AreaFilterNavigationItem, shell.AreaFilter, shell);
        ApplyNavigationText(CurrentAttentionNavigationItem, shell.CurrentAttention, shell);
        ApplyNavigationText(SettingsNavigationItem, shell.Settings, shell);
        AutomationProperties.SetName(
            HostNavigationItem,
            shell.HostSettingsAutomationName);

        AutomationProperties.SetName(SettingsPage, settings.PageAutomationName);
        SettingsPageTitleText.Text = settings.PageTitle;
        SettingsPageSubtitleText.Text = settings.PageSubtitle;
        RestoreDefaultSettingsButton.Content = settings.RestoreDefaults;
        AutomationProperties.SetName(
            RestoreDefaultSettingsButton,
            settings.RestoreDefaultsAutomationName);

        SettingsHostTitleText.Text = settings.HostConnection;
        SettingsHostAddressLabel.Text = settings.ServiceAddress;
        AutomationProperties.SetName(
            HostBaseUrlInput,
            settings.HostBaseAddressAutomationName);
        SettingsHostCredentialLabel.Text = settings.Credential;
        AutomationProperties.SetName(
            HostCredentialInput,
            settings.CredentialAutomationName);
        SettingsHostCredentialHelpText.Text = settings.CredentialHelp;
        SettingsRequestTimeoutLabel.Text = settings.RequestTimeout;
        RequestTimeoutInput.ToolTip = settings.RequestTimeoutTooltip;
        AutomationProperties.SetName(
            RequestTimeoutInput,
            settings.RequestTimeoutAutomationName);
        SettingsRequestTimeoutRangeText.Text = settings.RequestTimeoutRange;
        SettingsConnectionChangeTitleText.Text = settings.ConnectionChange;
        SettingsConnectionChangeHelpText.Text = settings.ConnectionChangeHelp;
        SettingsCredentialInfoBar.Title = settings.CredentialLocalTitle;
        SettingsCredentialInfoBar.Message = settings.CredentialLocalMessage;
        ApplyHostButton.Content = settings.ApplyHost;
        AutomationProperties.SetName(ApplyHostButton, settings.ApplyHost);

        SettingsRefreshTitleText.Text = settings.RefreshTitle;
        SettingsRefreshHelpText.Text = settings.RefreshHelp;
        ApplyRefreshSettingText(
            SettingsOverviewRefreshLabel,
            SettingsOverviewRefreshHelpText,
            OverviewIntervalInput,
            shell.Overview,
            settings.OverviewHelp,
            settings);
        ApplyRefreshSettingText(
            SettingsDemandSeriesRefreshLabel,
            SettingsDemandSeriesRefreshHelpText,
            DemandSeriesIntervalInput,
            shell.DemandSeries,
            settings.DemandSeriesHelp,
            settings);
        ApplyRefreshSettingText(
            SettingsReadabilityRefreshLabel,
            SettingsReadabilityRefreshHelpText,
            ReadabilityAuditIntervalInput,
            shell.ReadabilityAudit,
            settings.ReadabilityHelp,
            settings);
        ApplyRefreshSettingText(
            SettingsErrorSearchRefreshLabel,
            SettingsErrorSearchRefreshHelpText,
            ErrorSearchIntervalInput,
            shell.ErrorSearch,
            settings.ErrorSearchHelp,
            settings);
        ApplyRefreshSettingText(
            SettingsCurrentAttentionRefreshLabel,
            SettingsCurrentAttentionRefreshHelpText,
            CurrentAttentionIntervalInput,
            shell.CurrentAttention,
            settings.CurrentAttentionHelp,
            settings);

        SettingsDisplayTitleText.Text = settings.DisplaySectionTitle;
        SettingsDisplayHelpText.Text = settings.DisplaySectionHelp;
        SettingsLanguageLabel.Text = settings.LanguageLabel;
        SettingsLanguageHelpText.Text = settings.LanguageHelp;
        AutomationProperties.SetName(DisplayLanguageInput, settings.LanguageLabel);
        SettingsRememberLayoutLabel.Text = settings.RememberLayout;
        SettingsRememberLayoutHelpText.Text = settings.RememberLayoutHelp;
        RememberWindowLayoutCheckBox.OnContent = settings.On;
        RememberWindowLayoutCheckBox.OffContent = settings.Off;
        AutomationProperties.SetName(
            RememberWindowLayoutCheckBox,
            settings.RememberLayout);
        RestoreDefaultLayoutButton.Content = settings.RestoreLayout;
        AutomationProperties.SetName(
            RestoreDefaultLayoutButton,
            settings.RestoreLayout);
        AdvancedLocalPreferencesExpander.Header = settings.MoreLocalSettings;
        AutomationProperties.SetName(
            AdvancedLocalPreferencesExpander,
            settings.MoreLocalSettingsAutomationName);
        SettingsDefaultPaneLabel.Text = settings.DefaultNavigationPane;
        SettingsDefaultPaneHelpText.Text = settings.DefaultNavigationPaneHelp;
        KeepNavigationPaneOpenCheckBox.OnContent = settings.On;
        KeepNavigationPaneOpenCheckBox.OffContent = settings.Off;
        AutomationProperties.SetName(
            KeepNavigationPaneOpenCheckBox,
            settings.DefaultNavigationPane);
        SaveRefreshIntervalsButton.Content = settings.Save;
        AutomationProperties.SetName(SaveRefreshIntervalsButton, settings.Save);
        ApplyLocalizedAreaFilterStaticText();
        ApplyLocalizedOverviewText();
        ApplyLocalizedReadabilityAuditText();
        ApplyLocalizedErrorSearchText();
        ApplyLocalizedCurrentAttentionText();
    }

    private void ApplyLocalizedDemandSeriesText(WatchDemandSeriesText text)
    {
        DemandSeriesPageTitleText.Text = text.PageTitle;
        AutomationProperties.SetName(DemandSeriesPage, text.PageAutomationName);
        AutomationProperties.SetName(DemandSeriesScrollViewer, text.WorkspaceAutomationName);
        AutomationProperties.SetName(DemandSeriesContextText, text.ContextAutomationName);
        AutomationProperties.SetName(DemandSeriesFilterPanel, text.Filters);

        DemandSeriesLifecycleFilterLabel.Text = text.Lifecycle;
        DemandSeriesLifecycleAllButton.Content = text.All;
        DemandSeriesLifecycleTrackingButton.Content = text.DescribeLifecycle("TRACKING");
        DemandSeriesLifecycleArchivedButton.Content = text.DescribeLifecycle("ARCHIVED");
        AutomationProperties.SetName(DemandSeriesLifecycleSegment, text.Lifecycle);
        AutomationProperties.SetName(DemandSeriesLifecycleAllButton, text.FormatFilterChoiceName(text.Lifecycle, text.All));
        AutomationProperties.SetName(DemandSeriesLifecycleTrackingButton, text.FormatFilterChoiceName(text.Lifecycle, text.DescribeLifecycle("TRACKING")));
        AutomationProperties.SetName(DemandSeriesLifecycleArchivedButton, text.FormatFilterChoiceName(text.Lifecycle, text.DescribeLifecycle("ARCHIVED")));

        DemandSeriesPresenceFilterLabel.Text = text.Presence;
        LocalizeCanonicalChoices(
            DemandSeriesPresenceFilter,
            text.AllPresence,
            text.DescribePresence);
        AutomationProperties.SetName(DemandSeriesPresenceFilter, text.PresenceFilterAutomationName);

        DemandSeriesWorkTypeFilterLabel.Text = text.WorkType;
        LocalizeCanonicalChoices(
            DemandSeriesWorkTypeFilter,
            text.AllWorkTypes,
            text.DescribeWorkType);
        AutomationProperties.SetName(DemandSeriesWorkTypeFilter, text.WorkTypeFilterAutomationName);

        DemandSeriesSublotFilterLabel.Text = text.Sublot;
        DemandSeriesIdentityFilterLabel.Text = text.Identity;
        DemandSeriesAreaFilterLabel.Text = text.AreaFilter;
        AutomationProperties.SetName(DemandSeriesSublotFilter, text.SublotFilterAutomationName);
        AutomationProperties.SetName(DemandSeriesSeriesIdFilter, text.SeriesIdFilterAutomationName);
        AutomationProperties.SetName(DemandSeriesDemandIdFilter, text.DemandIdFilterAutomationName);
        AutomationProperties.SetName(DemandSeriesAreaProfileSelector, text.AreaSelectorAutomationName);

        DemandSeriesApplyFiltersButton.Content = text.ApplyFilters;
        DemandSeriesClearFiltersButton.Content = text.ClearFilters;
        AutomationProperties.SetName(DemandSeriesApplyFiltersButton, text.ApplyFilters);
        AutomationProperties.SetName(DemandSeriesClearFiltersButton, text.ClearFilters);

        DemandSeriesMasterHeadingText.Text = text.MasterHeading;
        DemandSeriesOpenInspectorButton.Content = _demandSeriesInspectorCoordinator.IsOpen
            ? text.ShowInspector
            : text.OpenInspector;
        AutomationProperties.SetName(DemandSeriesMasterPanel, text.MainListAutomationName);
        AutomationProperties.SetName(DemandSeriesGrid, text.ListAutomationName);
        AutomationProperties.SetName(DemandSeriesOpenInspectorButton, DemandSeriesOpenInspectorButton.Content.ToString()!);

        DemandSeriesGrid.Columns[3].Header = text.ColumnLifecycle;
        DemandSeriesGrid.Columns[4].Header = text.ColumnArea;
        DemandSeriesGrid.Columns[5].Header = text.ColumnDemand;
        DemandSeriesGrid.Columns[6].Header = text.ColumnGeneration;
        DemandSeriesGrid.Columns[7].Header = text.ColumnEvents;
        DemandSeriesGrid.Columns[8].Header = text.ColumnStarted;
        DemandSeriesGrid.Columns[9].Header = text.ColumnLastSeen;
        DemandSeriesGrid.Columns[10].Header = text.ColumnGoneSince;
        DemandSeriesGrid.Columns[11].Header = text.ColumnArchived;

        DemandSeriesEmptyState.Title = text.EmptyTitle;
        DemandSeriesEmptyState.Message = text.EmptyMessage;
        AutomationProperties.SetName(DemandSeriesEmptyState, text.EmptyAutomationName);
        DemandSeriesPerPageLabel.Text = text.PerPage;
        DemandSeriesPreviousButton.Content = text.Previous;
        DemandSeriesGoToPageButton.Content = text.GoToPage;
        DemandSeriesNextButton.Content = text.Next;
        AutomationProperties.SetName(DemandSeriesPreviousButton, text.Previous);
        AutomationProperties.SetName(DemandSeriesGoToPageButton, text.GoToPage);
        AutomationProperties.SetName(DemandSeriesNextButton, text.Next);
        AutomationProperties.SetName(DemandSeriesInfoBar, text.ReadStateAutomationName);
        AutomationProperties.SetName(DemandSeriesInfoExpander, text.TopInfoAutomationName);
        SetDemandSeriesLifecycleDraft(_demandSeriesLifecycleDraft);
    }

    private static void LocalizeCanonicalChoices(
        System.Windows.Controls.ComboBox comboBox,
        string allText,
        Func<string, string> describe)
    {
        var selectedCode = comboBox.SelectedItem is System.Windows.Controls.ComboBoxItem selected
            ? selected.Tag?.ToString()
            : null;
        foreach (var item in comboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            var code = item.Tag?.ToString();
            item.Content = string.IsNullOrWhiteSpace(code) ? allText : describe(code);
        }

        if (selectedCode is not null)
        {
            comboBox.SelectedValue = selectedCode;
        }
    }

    private static void ApplyNavigationText(
        Wpf.Ui.Controls.NavigationViewItem item,
        string label,
        WatchShellText shell)
    {
        item.Content = label;
        AutomationProperties.SetName(item, shell.NavigationName(label));
    }

    private static void ApplyRefreshSettingText(
        Wpf.Ui.Controls.TextBlock labelControl,
        Wpf.Ui.Controls.TextBlock helpControl,
        System.Windows.Controls.ComboBox input,
        string label,
        string help,
        WatchSettingsText settings)
    {
        labelControl.Text = label;
        helpControl.Text = help;
        AutomationProperties.SetName(
            input,
            settings.RefreshAutomationName(label));
    }
}
