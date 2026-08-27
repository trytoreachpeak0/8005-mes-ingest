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
        ApplyLocalizedShellAndSettingsText();
        ApplyLocalizedAreaFilterStaticText();
        RenderAreaProfiles(editorViewState: areaEditorViewState);
        InitializeIntervalInputs();
        DisplayLanguageInput.SelectedValue = _displayLanguageState.Current;

        var overview = WatchOverviewPresentation.Project(_session.State, _areaContext);
        RenderHostFooter(_session.State, overview);

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
