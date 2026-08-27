namespace MesIngest.Watch;

internal sealed class WatchShellText(WatchDisplayLanguage language)
    : WatchTextCatalogSection(language)
{
    private static WatchTextCatalogEntry E(string id, string zh, string en) => new(id, zh, en);

    private static readonly WatchTextCatalogEntry PrimaryNavigationEntry = E("shell.navigation.name", "主导航", "Primary navigation");
    private static readonly WatchTextCatalogEntry OverviewEntry = E("shell.navigation.overview", "概览", "Overview");
    private static readonly WatchTextCatalogEntry DemandSeriesEntry = E("shell.navigation.demandSeries", "需求系列", "Demand series");
    private static readonly WatchTextCatalogEntry ReadabilityAuditEntry = E("shell.navigation.readabilityAudit", "资格审计", "Eligibility audit");
    private static readonly WatchTextCatalogEntry ErrorSearchEntry = E("shell.navigation.errorSearch", "错误检索", "Error search");
    private static readonly WatchTextCatalogEntry AreaFilterEntry = E("shell.navigation.areaFilter", "AREA 筛选", "AREA filters");
    private static readonly WatchTextCatalogEntry CurrentAttentionEntry = E("shell.navigation.currentAttention", "接入告警", "Ingest alerts");
    private static readonly WatchTextCatalogEntry SettingsEntry = E("shell.navigation.settings", "设置", "Settings");
    private static readonly WatchTextCatalogEntry NavigationSuffixEntry = E("shell.navigation.suffix", "{0}导航", "{0} navigation");
    private static readonly WatchTextCatalogEntry HostSettingsEntry = E("shell.navigation.hostSettings", "Host 状态与连接设置", "Host status and connection settings");
    private static readonly WatchTextCatalogEntry HostDisconnectedEntry = E("shell.host.disconnected", "Host 未连接", "Host disconnected");
    private static readonly WatchTextCatalogEntry HostConnectedEntry = E("shell.host.connected", "Host 已连接", "Host connected");
    private static readonly WatchTextCatalogEntry HostConnectingEntry = E("shell.host.connecting", "Host 连接中", "Host connecting");
    private static readonly WatchTextCatalogEntry HostFailedEntry = E("shell.host.failed", "Host 连接失败", "Host connection failed");
    private static readonly WatchTextCatalogEntry ReadFailedEntry = E("shell.host.readFailed", "读取失败", "read failed");
    private static readonly WatchTextCatalogEntry HostNotConnectedTooltipEntry = E("shell.host.notConnectedTooltip", "Host 尚未连接", "Host is not connected");
    private static readonly WatchTextCatalogEntry NavigationToggleEntry = E("shell.navigation.toggle", "展开或折叠主导航", "Expand or collapse primary navigation");
    private static readonly WatchTextCatalogEntry NavigationToggleHelpEntry = E("shell.navigation.toggleHelp", "在 48 epx 紧凑导航与 224 epx 展开导航之间切换", "Switch between the compact 48 epx navigation rail and the expanded 224 epx navigation pane");
    private static readonly WatchTextCatalogEntry MinimizeWindowEntry = E("shell.window.minimize", "最小化窗口", "Minimize window");
    private static readonly WatchTextCatalogEntry MaximizeWindowEntry = E("shell.window.maximize", "最大化窗口", "Maximize window");
    private static readonly WatchTextCatalogEntry RestoreWindowEntry = E("shell.window.restore", "还原窗口", "Restore window");
    private static readonly WatchTextCatalogEntry CloseWindowEntry = E("shell.window.close", "关闭窗口", "Close window");

    private static readonly IReadOnlyList<WatchTextCatalogEntry> CatalogEntries =
    [
        PrimaryNavigationEntry,
        OverviewEntry,
        DemandSeriesEntry,
        ReadabilityAuditEntry,
        ErrorSearchEntry,
        AreaFilterEntry,
        CurrentAttentionEntry,
        SettingsEntry,
        NavigationSuffixEntry,
        HostSettingsEntry,
        HostDisconnectedEntry,
        HostConnectedEntry,
        HostConnectingEntry,
        HostFailedEntry,
        ReadFailedEntry,
        HostNotConnectedTooltipEntry,
        NavigationToggleEntry,
        NavigationToggleHelpEntry,
        MinimizeWindowEntry,
        MaximizeWindowEntry,
        RestoreWindowEntry,
        CloseWindowEntry,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => CatalogEntries;
    public string PrimaryNavigationName => Text(PrimaryNavigationEntry);
    public string Overview => Text(OverviewEntry);
    public string DemandSeries => Text(DemandSeriesEntry);
    public string ReadabilityAudit => Text(ReadabilityAuditEntry);
    public string ErrorSearch => Text(ErrorSearchEntry);
    public string AreaFilter => Text(AreaFilterEntry);
    public string CurrentAttention => Text(CurrentAttentionEntry);
    public string Settings => Text(SettingsEntry);
    public string HostSettingsAutomationName => Text(HostSettingsEntry);
    public string HostDisconnected => Text(HostDisconnectedEntry);
    public string HostConnected => Text(HostConnectedEntry);
    public string HostConnecting => Text(HostConnectingEntry);
    public string HostFailed => Text(HostFailedEntry);
    public string ReadFailed => Text(ReadFailedEntry);
    public string HostNotConnectedTooltip => Text(HostNotConnectedTooltipEntry);
    public string NavigationToggle => Text(NavigationToggleEntry);
    public string NavigationToggleHelp => Text(NavigationToggleHelpEntry);
    public string MinimizeWindow => Text(MinimizeWindowEntry);
    public string MaximizeWindow => Text(MaximizeWindowEntry);
    public string RestoreWindow => Text(RestoreWindowEntry);
    public string CloseWindow => Text(CloseWindowEntry);
    public string NavigationName(string label) => string.Format(Text(NavigationSuffixEntry), label);
}
