namespace MesIngest.Watch;

internal sealed class WatchSettingsText(WatchDisplayLanguage language)
    : WatchTextCatalogSection(language)
{
    private static WatchTextCatalogEntry E(string id, string zh, string en) => new(id, zh, en);

    private static readonly WatchTextCatalogEntry PageTitleEntry = E("settings.page.title", "设置", "Settings");
    private static readonly WatchTextCatalogEntry PageSubtitleEntry = E("settings.page.subtitle", "Host 连接、自动刷新与本机显示偏好", "Host connection, automatic refresh, and local display preferences");
    private static readonly WatchTextCatalogEntry RestoreDefaultsEntry = E("settings.restore.defaults", "恢复默认设置", "Restore default settings");
    private static readonly WatchTextCatalogEntry RestoreDefaultsAutomationEntry = E("settings.restore.defaultsAutomation", "恢复 Watch 默认设置", "Restore Watch default settings");
    private static readonly WatchTextCatalogEntry PageAutomationEntry = E("settings.page.automation", "设置页面", "Settings page");
    private static readonly WatchTextCatalogEntry HostConnectionEntry = E("settings.host.title", "Host 连接", "Host connection");
    private static readonly WatchTextCatalogEntry ServiceAddressEntry = E("settings.host.address", "服务地址", "Service address");
    private static readonly WatchTextCatalogEntry HostBaseAddressEntry = E("settings.host.baseAddress", "Host 基址", "Host base address");
    private static readonly WatchTextCatalogEntry CredentialEntry = E("settings.host.credential", "访问凭据", "Access credential");
    private static readonly WatchTextCatalogEntry CredentialAutomationEntry = E("settings.host.credentialAutomation", "只读 API 凭据", "Read-only API credential");
    private static readonly WatchTextCatalogEntry CredentialHelpEntry = E("settings.host.credentialHelp", "留空继续使用当前外部配置凭据；输入值仅用于本次进程，保存后立即清空。", "Leave blank to keep the current externally configured credential. An entered value is used only for this process and cleared immediately after saving.");
    private static readonly WatchTextCatalogEntry RequestTimeoutEntry = E("settings.host.timeout", "请求超时", "Request timeout");
    private static readonly WatchTextCatalogEntry RequestTimeoutTooltipEntry = E("settings.host.timeoutTooltip", "1–300 秒", "1–300 seconds");
    private static readonly WatchTextCatalogEntry RequestTimeoutAutomationEntry = E("settings.host.timeoutAutomation", "全局请求超时秒数", "Global request timeout in seconds");
    private static readonly WatchTextCatalogEntry RequestTimeoutRangeEntry = E("settings.host.timeoutRange", "秒 · 合法范围 1–300", "seconds · valid range 1–300");
    private static readonly WatchTextCatalogEntry ConnectionChangeEntry = E("settings.host.changeTitle", "连接变更", "Connection change");
    private static readonly WatchTextCatalogEntry ConnectionChangeHelpEntry = E("settings.host.changeHelp", "应用后取消旧 Host 的全部请求，清空旧业务窗口并返回概览。", "Applying cancels all requests to the old Host, clears the old business view, and returns to Overview.");
    private static readonly WatchTextCatalogEntry CredentialLocalTitleEntry = E("settings.host.localCredentialTitle", "凭据仅保存在本机", "Credential remains local");
    private static readonly WatchTextCatalogEntry CredentialLocalMessageEntry = E("settings.host.localCredentialMessage", "界面不会显示、复制或记录完整令牌。", "The interface never displays, copies, or logs the complete token.");
    private static readonly WatchTextCatalogEntry ApplyHostEntry = E("settings.host.apply", "应用 Host 设置", "Apply Host settings");
    private static readonly WatchTextCatalogEntry RefreshTitleEntry = E("settings.refresh.title", "自动刷新间隔", "Automatic refresh intervals");
    private static readonly WatchTextCatalogEntry RefreshHelpEntry = E("settings.refresh.help", "所有数据视图始终自动刷新；这里只配置各视图的刷新时间。", "All data views always refresh automatically; configure only their intervals here.");
    private static readonly WatchTextCatalogEntry OverviewHelpEntry = E("settings.refresh.overviewHelp", "Host、投影、当前关注与跨页摘要", "Host, projection, current attention, and cross-page summaries");
    private static readonly WatchTextCatalogEntry DemandSeriesHelpEntry = E("settings.refresh.demandSeriesHelp", "完整生命周期与当前出现范围", "Complete lifecycle and current-presence scope");
    private static readonly WatchTextCatalogEntry ReadabilityHelpEntry = E("settings.refresh.readabilityHelp", "外部可读资格与全部阻断原因", "External readability and every blocking reason");
    private static readonly WatchTextCatalogEntry ErrorSearchHelpEntry = E("settings.refresh.errorSearchHelp", "活动与已结束的历史错误", "Active and ended historical errors");
    private static readonly WatchTextCatalogEntry CurrentAttentionHelpEntry = E("settings.refresh.attentionHelp", "Host 当前仍需关注的接入项", "Current Host ingest items that still need attention");
    private static readonly WatchTextCatalogEntry RefreshAutomationEntry = E("settings.refresh.automation", "{0}自动刷新间隔", "{0} automatic refresh interval");
    private static readonly WatchTextCatalogEntry DisplaySectionEntry = E("settings.display.title", "显示与布局", "Display and layout");
    private static readonly WatchTextCatalogEntry DisplayHelpEntry = E("settings.display.help", "本机偏好，不影响 Host 或业务数据。", "Local preferences; they do not affect Host or business data.");
    private static readonly WatchTextCatalogEntry LanguageLabelEntry = E("settings.display.language", "显示语言", "Display language");
    private static readonly WatchTextCatalogEntry LanguageHelpEntry = E("settings.display.languageHelp", "保存后全部已打开窗口立即使用同一语言", "All open windows use the same language immediately after saving");
    private static readonly WatchTextCatalogEntry RememberLayoutEntry = E("settings.display.rememberLayout", "记住窗口布局", "Remember window layout");
    private static readonly WatchTextCatalogEntry RememberLayoutHelpEntry = E("settings.display.rememberLayoutHelp", "重启后恢复主窗口和详情窗口布局", "Restore the main and Inspector window layouts after restart");
    private static readonly WatchTextCatalogEntry RestoreLayoutEntry = E("settings.display.restoreLayout", "恢复默认布局", "Restore default layout");
    private static readonly WatchTextCatalogEntry MoreLocalEntry = E("settings.display.moreLocal", "更多本机设置", "More local settings");
    private static readonly WatchTextCatalogEntry MoreLocalAutomationEntry = E("settings.display.moreLocalAutomation", "更多本机刷新与导航设置", "More local refresh and navigation settings");
    private static readonly WatchTextCatalogEntry DefaultPaneEntry = E("settings.display.defaultPane", "默认展开导航窗格", "Open navigation pane by default");
    private static readonly WatchTextCatalogEntry DefaultPaneHelpEntry = E("settings.display.defaultPaneHelp", "关闭时使用 48 epx 紧凑 rail", "When closed, use the compact 48 epx rail");
    private static readonly WatchTextCatalogEntry OnEntry = E("settings.common.on", "开", "On");
    private static readonly WatchTextCatalogEntry OffEntry = E("settings.common.off", "关", "Off");
    private static readonly WatchTextCatalogEntry SaveEntry = E("settings.save", "保存刷新与显示设置", "Save refresh and display settings");
    private static readonly WatchTextCatalogEntry SecondsChoiceEntry = E("settings.refresh.seconds", "{0} 秒", "{0} seconds");

    private static readonly IReadOnlyList<WatchTextCatalogEntry> CatalogEntries =
    [
        PageTitleEntry,
        PageSubtitleEntry,
        RestoreDefaultsEntry,
        RestoreDefaultsAutomationEntry,
        PageAutomationEntry,
        HostConnectionEntry,
        ServiceAddressEntry,
        HostBaseAddressEntry,
        CredentialEntry,
        CredentialAutomationEntry,
        CredentialHelpEntry,
        RequestTimeoutEntry,
        RequestTimeoutTooltipEntry,
        RequestTimeoutAutomationEntry,
        RequestTimeoutRangeEntry,
        ConnectionChangeEntry,
        ConnectionChangeHelpEntry,
        CredentialLocalTitleEntry,
        CredentialLocalMessageEntry,
        ApplyHostEntry,
        RefreshTitleEntry,
        RefreshHelpEntry,
        OverviewHelpEntry,
        DemandSeriesHelpEntry,
        ReadabilityHelpEntry,
        ErrorSearchHelpEntry,
        CurrentAttentionHelpEntry,
        RefreshAutomationEntry,
        DisplaySectionEntry,
        DisplayHelpEntry,
        LanguageLabelEntry,
        LanguageHelpEntry,
        RememberLayoutEntry,
        RememberLayoutHelpEntry,
        RestoreLayoutEntry,
        MoreLocalEntry,
        MoreLocalAutomationEntry,
        DefaultPaneEntry,
        DefaultPaneHelpEntry,
        OnEntry,
        OffEntry,
        SaveEntry,
        SecondsChoiceEntry,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => CatalogEntries;
    public string PageTitle => Text(PageTitleEntry);
    public string PageSubtitle => Text(PageSubtitleEntry);
    public string RestoreDefaults => Text(RestoreDefaultsEntry);
    public string RestoreDefaultsAutomationName => Text(RestoreDefaultsAutomationEntry);
    public string PageAutomationName => Text(PageAutomationEntry);
    public string HostConnection => Text(HostConnectionEntry);
    public string ServiceAddress => Text(ServiceAddressEntry);
    public string HostBaseAddressAutomationName => Text(HostBaseAddressEntry);
    public string Credential => Text(CredentialEntry);
    public string CredentialAutomationName => Text(CredentialAutomationEntry);
    public string CredentialHelp => Text(CredentialHelpEntry);
    public string RequestTimeout => Text(RequestTimeoutEntry);
    public string RequestTimeoutTooltip => Text(RequestTimeoutTooltipEntry);
    public string RequestTimeoutAutomationName => Text(RequestTimeoutAutomationEntry);
    public string RequestTimeoutRange => Text(RequestTimeoutRangeEntry);
    public string ConnectionChange => Text(ConnectionChangeEntry);
    public string ConnectionChangeHelp => Text(ConnectionChangeHelpEntry);
    public string CredentialLocalTitle => Text(CredentialLocalTitleEntry);
    public string CredentialLocalMessage => Text(CredentialLocalMessageEntry);
    public string ApplyHost => Text(ApplyHostEntry);
    public string RefreshTitle => Text(RefreshTitleEntry);
    public string RefreshHelp => Text(RefreshHelpEntry);
    public string OverviewHelp => Text(OverviewHelpEntry);
    public string DemandSeriesHelp => Text(DemandSeriesHelpEntry);
    public string ReadabilityHelp => Text(ReadabilityHelpEntry);
    public string ErrorSearchHelp => Text(ErrorSearchHelpEntry);
    public string CurrentAttentionHelp => Text(CurrentAttentionHelpEntry);
    public string RefreshAutomationName(string page) => string.Format(Text(RefreshAutomationEntry), page);
    public string DisplaySectionTitle => Text(DisplaySectionEntry);
    public string DisplaySectionHelp => Text(DisplayHelpEntry);
    public string LanguageLabel => Text(LanguageLabelEntry);
    public string LanguageHelp => Text(LanguageHelpEntry);
    public string RememberLayout => Text(RememberLayoutEntry);
    public string RememberLayoutHelp => Text(RememberLayoutHelpEntry);
    public string RestoreLayout => Text(RestoreLayoutEntry);
    public string MoreLocalSettings => Text(MoreLocalEntry);
    public string MoreLocalSettingsAutomationName => Text(MoreLocalAutomationEntry);
    public string DefaultNavigationPane => Text(DefaultPaneEntry);
    public string DefaultNavigationPaneHelp => Text(DefaultPaneHelpEntry);
    public string On => Text(OnEntry);
    public string Off => Text(OffEntry);
    public string Save => Text(SaveEntry);
    public string FormatSeconds(int seconds) => string.Format(Text(SecondsChoiceEntry), seconds);
}
