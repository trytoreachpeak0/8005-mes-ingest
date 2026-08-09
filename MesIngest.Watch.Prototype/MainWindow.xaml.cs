using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MesIngest.Watch.Prototype;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly string[] VariantKeys = ["A", "B", "C", "D", "E"];
    private static readonly string[] VariantNames = ["运维工作台", "关系解释器", "聚焦浏览器", "任务关联告警", "告警关联任务"];
    private readonly DispatcherTimer _autoRefreshTimer;
    private readonly Dictionary<string, DateTimeOffset> _lastRefreshByPage = [];
    private CancellationTokenSource? _refreshCancellation;
    private long _refreshGeneration;
    private int _variantIndex;
    private string _scenario = "Active";
    private NavigationItem? _selectedNavigation;
    private object? _selectedPage;
    private TaskRow? _selectedTask;
    private AlertRow? _selectedAlert;
    private string _refreshStatus = "当前窗口使用评审假数据；尚未连接服务。";
    private string _connectionLabel = "在线";
    private string _pollStatus = "最近 poll 成功 · 12:42:16";
    private string _emptyStateText = string.Empty;
    private string _pageLabel = "已加载 1–25 · 下一页 cursor 可用";
    private string _dataWindowLabel = "最后成功 12:42:16";
    private int _activeAlertCount = 6;
    private int _visibleDemandCount = 672;

    public MainWindow(string initialVariant, string initialScenario, string initialPage = "OV")
    {
        InitializeComponent();
        DataContext = this;
        Navigation =
        [
            new("OV", "概览", "约 10 秒判断 Host 接入、活动 IngestAlert 与 VISIBLE TransportDemand。", "12:42:16", new OverviewPage()),
            new("TS", "任务浏览", "同时核对七个来源字段与本地投影实例；两种可见状态分别保留。", "12:42:16", new TasksPage()),
            new("AL", "接入告警", "只读查看接入过程产生的六类告警及关联任务，不提供处置动作。", "12:42:15", new AlertsPage()),
            new("ST", "设置", "只保留服务连接、按页刷新与基本显示设置。", "本机", new SettingsPage())
        ];

        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshCurrentPageAsync();
        SelectedNavigation = Navigation[0];
        _variantIndex = Math.Max(0, Array.FindIndex(VariantKeys, key => key.Equals(initialVariant, StringComparison.OrdinalIgnoreCase)));
        ApplyVariant();

        var normalizedScenario = NormalizeScenario(initialScenario);
        foreach (var item in ScenarioPicker.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, normalizedScenario, StringComparison.OrdinalIgnoreCase))
            {
                ScenarioPicker.SelectedItem = item;
                break;
            }
        }

        ApplyScenario(normalizedScenario);
        var requestedPage = Navigation.FirstOrDefault(item => item.Code.Equals(initialPage, StringComparison.OrdinalIgnoreCase));
        if (requestedPage is not null) SelectedNavigation = requestedPage;
    }

    public ObservableCollection<NavigationItem> Navigation { get; }
    public ObservableCollection<TaskRow> Tasks { get; } = [];
    public ObservableCollection<AlertRow> Alerts { get; } = [];
    public ObservableCollection<TaskTypeCount> TaskCounts { get; } = [];

    public NavigationItem? SelectedNavigation
    {
        get => _selectedNavigation;
        set
        {
            if (!SetField(ref _selectedNavigation, value) || value is null) return;
            CancelRefresh("已切换页面；旧请求已取消，最后成功数据保持不变。", incrementGeneration: true);
            SelectedPage = value.Page;
            RefreshStatus = _lastRefreshByPage.TryGetValue(value.Code, out var refreshedAt)
                ? $"{value.Label}最后成功刷新 {refreshedAt:HH:mm:ss} · 其它页面未刷新"
                : $"{value.Label}尚未刷新 · 其它页面保持原状态";
        }
    }

    public object? SelectedPage { get => _selectedPage; private set => SetField(ref _selectedPage, value); }
    public TaskRow? SelectedTask { get => _selectedTask; set => SetField(ref _selectedTask, value); }
    public AlertRow? SelectedAlert { get => _selectedAlert; set => SetField(ref _selectedAlert, value); }
    public string RefreshStatus { get => _refreshStatus; private set => SetField(ref _refreshStatus, value); }
    public string ConnectionLabel { get => _connectionLabel; private set => SetField(ref _connectionLabel, value); }
    public string PollStatus { get => _pollStatus; private set => SetField(ref _pollStatus, value); }
    public string EmptyStateText { get => _emptyStateText; private set => SetField(ref _emptyStateText, value); }
    public string PageLabel { get => _pageLabel; private set => SetField(ref _pageLabel, value); }
    public string DataWindowLabel { get => _dataWindowLabel; private set => SetField(ref _dataWindowLabel, value); }
    public int ActiveAlertCount { get => _activeAlertCount; private set => SetField(ref _activeAlertCount, value); }
    public int VisibleDemandCount { get => _visibleDemandCount; private set => SetField(ref _visibleDemandCount, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetCaptureMode()
    {
        PrototypeSwitcher.Visibility = Visibility.Collapsed;
        PrototypeControls.Visibility = Visibility.Collapsed;
    }

    public void Capture(string path, int pixelWidth, int pixelHeight)
    {
        var absolutePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        RootLayout.Width = pixelWidth;
        RootLayout.Height = pixelHeight;
        RootLayout.Measure(new Size(pixelWidth, pixelHeight));
        RootLayout.Arrange(new Rect(0, 0, pixelWidth, pixelHeight));
        RootLayout.UpdateLayout();
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(RootLayout);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(absolutePath);
        encoder.Save(stream);
    }

    private void ApplyScenario(string scenario)
    {
        _scenario = scenario;
        Tasks.Clear(); Alerts.Clear(); TaskCounts.Clear();
        foreach (var task in BuildTasks(scenario)) Tasks.Add(task);
        foreach (var alert in BuildAlerts(scenario)) Alerts.Add(alert);
        foreach (var item in BuildTaskCounts(scenario)) TaskCounts.Add(item);

        ConnectionLabel = scenario == "Offline" ? "离线" : "在线";
        PollStatus = scenario switch
        {
            "Offline" => "服务连接失败 · 保留 12:39:44 成功窗口",
            "Empty" => "最近 poll 成功 · 当前快照为空",
            _ => "最近 poll 成功 · 12:42:16"
        };
        DataWindowLabel = scenario == "Offline" ? "最后成功 12:39:44 · 已过期" : "最后成功 12:42:16";
        VisibleDemandCount = scenario == "Empty" ? 0 : scenario == "Paging" ? 12_483 : 672;
        ActiveAlertCount = Alerts.Count;
        EmptyStateText = scenario == "Empty" ? "成功空结果：当前没有活动 IngestAlert，也没有 VISIBLE TransportDemand。" : string.Empty;
        PageLabel = scenario == "Paging" ? "已加载 51–75 / 12,483 · cursor=eyJzZXEiOj..." : "已加载 1–25 · 下一页 cursor 可用";
        SelectedTask = Tasks.FirstOrDefault();
        SelectedAlert = Alerts.FirstOrDefault();
        RefreshStatus = $"已切换为“{ScenarioDisplayName(scenario)}”评审假数据 · 未发出网络请求";
    }

    private static IEnumerable<TaskRow> BuildTasks(string scenario)
    {
        if (scenario == "Empty") return [];
        var rows = new[]
        {
            new TaskRow("dmd-7f91c2a8", "DIE_TO_WIRE_STAGING", "Q26063201-2", "C6-12", "3ZPS122", "焊线", "2026-07-24 13:58:12", "SOP8/PP(150mil)(12R)", "VISIBLE", "2026-08-07 12:42:16", 0, "—", "2026-08-07 08:10:02", "—", "DUPLICATE_RECONCILE_KEY"),
            new TaskRow("dmd-2a10bd44", "DIE_TO_OVEN", "Q26063224-8", "C6-14", "3ZPS136", "装片烘烤", "2026-07-24 13:55:34", "SOP8/PP(150mil)(12R)", "VISIBLE", "2026-08-07 12:42:16", 0, "—", "2026-08-07 08:12:47", "—", "REAPPEAR_AFTER_GONE"),
            new TaskRow("dmd-98c44ab1", "WIRE_TO_GATE", "Q26063189-5", "B4-09", "WB-031", "焊线关卡", "2026-07-24 13:50:08", "QFN32 5x5", "VISIBLE", "2026-08-07 12:42:16", 0, "—", "2026-08-07 08:15:19", "—", "FIELD_DRIFT"),
            new TaskRow("dmd-06bd33c9", "WIRE_TO_OPTICAL", "Q26063177-3", "A3-18", "WB-114", "三光检验", "2026-07-24 13:47:21", "SOT23-6L", "VISIBLE", "2026-08-07 12:42:16", 0, "—", "2026-08-07 08:20:31", "—", "—"),
            new TaskRow("dmd-a7e14502", "STAGING_TO_WIRE", "Q26063240-1", "D7-04", "WB-208", "键合", "2026-07-24 13:44:59", "DFN8 3x3", "VISIBLE", "2026-08-07 12:42:16", 0, "—", "2026-08-07 08:25:40", "—", "PAUSED_ZERO_DROP"),
            new TaskRow("dmd-b31f084d", "WIRE_TO_NITROGEN", "Q26063161-7", "N2-06", "WB1-017", "焊线2", "2026-07-24 13:41:17", "TSSOP20", "VISIBLE", "2026-08-07 12:42:16", 0, "—", "2026-08-07 08:31:26", "—", "—"),
            new TaskRow("dmd-e8a14273", "STAGING_TO_WIRE", "Q26063252-6", "", "WB-219", "焊线", "2026-07-24 13:38:42", "QFN48 7x7", "VISIBLE", "2026-08-07 12:42:16", 0, "AREA_EMPTY", "2026-08-07 08:34:11", "—", "—"),
            new TaskRow("dmd-10fc9e51", "DIE_TO_OVEN", "Q26063268-4", "C00-7", "3ZPS141", "装片压力烘烤", "2026-07-24 13:35:07", "SOP16 300mil", "VISIBLE", "2026-08-07 12:42:16", 0, "AREA_UNPARSEABLE", "2026-08-07 08:39:54", "—", "—")
        };
        return scenario == "Offline" ? rows.Take(5) : rows;
    }

    private static IEnumerable<AlertRow> BuildAlerts(string scenario)
    {
        if (scenario is "Healthy" or "Offline" or "Empty") return [];
        return
        [
            new("ERROR", "POLL_FAILURE", "全局", "—", "—", "—", "MES 快照读取失败；当轮投影保持不变。", "phase=oracle-read; timeout=30s", 3, "是", "12:36:20", "12:42:12", "—"),
            new("ERROR", "POLL_INCOMPLETE", "全局", "—", "—", "—", "快照存在必需列或必需值错误；当轮投影保持不变。", "invalidRows=2; fields=TASK_TYPE,DATES", 1, "是", "12:41:50", "12:41:50", "—"),
            new("ERROR", "DUPLICATE_RECONCILE_KEY", "业务键", "DIE_TO_WIRE_STAGING", "Q26063201-2", "dmd-7f91c2a8", "同一快照中 TASK_TYPE + SUBLOT 重复；仅阻断该键对账。", "rows=2; key=DIE_TO_WIRE_STAGING|Q26063201-2", 4, "是", "12:35:02", "12:42:16", "—"),
            new("ERROR", "PAUSED_ZERO_DROP", "任务类型", "STAGING_TO_WIRE", "—", "—", "健康非零基线后骤降为 0；暂停该类型的消失计数与 GONE。", "previous=246; current=0; recovery=0/2", 2, "是", "12:40:01", "12:42:16", "—"),
            new("ERROR", "FIELD_DRIFT", "Demand", "WIRE_TO_GATE", "Q26063189-5", "dmd-98c44ab1", "VISIBLE 业务键的冻结 MES 字段发生变化；继续保留原投影。", "field=EQP; frozen=WB-031; observed=WB-044", 5, "是", "12:33:17", "12:42:16", "—"),
            new("WARNING", "REAPPEAR_AFTER_GONE", "新 Demand", "DIE_TO_OVEN", "Q26063224-8", "dmd-2a10bd44", "同一业务键在 GONE 后再现；已创建新的 DemandId。", "previousDemandId=dmd-old-116a; newDemandId=dmd-2a10bd44", 1, "是", "12:41:49", "12:41:49", "—")
        ];
    }

    private static IEnumerable<TaskTypeCount> BuildTaskCounts(string scenario)
    {
        var counts = new[] { new TaskTypeCount("DIE_TO_WIRE_STAGING", 105), new TaskTypeCount("DIE_TO_OVEN", 94), new TaskTypeCount("WIRE_TO_GATE", 84), new TaskTypeCount("WIRE_TO_OPTICAL", 52), new TaskTypeCount("STAGING_TO_WIRE", 246), new TaskTypeCount("WIRE_TO_NITROGEN", 91) };
        return scenario == "Empty" ? counts.Select(item => item with { Count = 0 }) : counts;
    }

    private async Task RefreshCurrentPageAsync()
    {
        if (SelectedNavigation is null) return;
        if (_refreshCancellation is not null)
        {
            CancelRefresh("用户取消当前页刷新；保留最后成功数据。", incrementGeneration: true);
            return;
        }

        var generation = ++_refreshGeneration;
        var page = SelectedNavigation;
        _refreshCancellation = new CancellationTokenSource();
        RefreshButton.Content = "取消刷新";
        RefreshStatus = $"正在刷新 {page.Label} · 单飞请求 generation {generation} · 可取消";
        try
        {
            await Task.Delay(_scenario == "Offline" ? 3500 : 900, _refreshCancellation.Token);
            if (generation != _refreshGeneration || page != SelectedNavigation) return;
            var completedAt = DateTimeOffset.Now;
            _lastRefreshByPage[page.Code] = completedAt;
            page.Freshness = completedAt.ToString("HH:mm:ss");
            RefreshStatus = _scenario == "Offline"
                ? $"{page.Label}刷新失败 {completedAt:HH:mm:ss} · 保留 12:39:44 成功窗口"
                : $"{page.Label}刷新成功 {completedAt:HH:mm:ss} · 只更新当前页 · generation {generation}";
        }
        catch (OperationCanceledException) { }
        finally
        {
            _refreshCancellation?.Dispose();
            _refreshCancellation = null;
            RefreshButton.Content = "刷新当前页";
        }
    }

    private void CancelRefresh(string status, bool incrementGeneration)
    {
        if (incrementGeneration) _refreshGeneration++;
        if (_refreshCancellation is null) return;
        _refreshCancellation.Cancel();
        RefreshStatus = status;
    }

    private void ApplyVariant()
    {
        VariantA.Visibility = _variantIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        VariantB.Visibility = _variantIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        VariantC.Visibility = _variantIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        VariantD.Visibility = _variantIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        VariantE.Visibility = _variantIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        VariantLabel.Text = $"{VariantKeys[_variantIndex]} — {VariantNames[_variantIndex]}";
    }

    private void CycleVariant(int direction)
    {
        _variantIndex = (_variantIndex + direction + VariantKeys.Length) % VariantKeys.Length;
        ApplyVariant();
    }

    private void OpenPage(string code)
    {
        var page = Navigation.First(item => item.Code == code);
        SelectedNavigation = page;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshCurrentPageAsync();
    private void OnPreviousVariantClick(object sender, RoutedEventArgs e) => CycleVariant(-1);
    private void OnNextVariantClick(object sender, RoutedEventArgs e) => CycleVariant(1);
    private void OnOpenAlertsClick(object sender, RoutedEventArgs e) => OpenPage("AL");
    private void OnOpenTasksClick(object sender, RoutedEventArgs e) => OpenPage("TS");
    private void OnNextPageClick(object sender, RoutedEventArgs e) => PageLabel = "已加载 26–50 · 上一页/下一页 cursor 均可用";

    private void OnAlertDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AlertRow alert }) SelectedAlert = alert;
        OpenPage("AL");
    }

    private void OnOpenRelatedDemandClick(object sender, RoutedEventArgs e)
    {
        if (SelectedAlert is null || SelectedAlert.DemandId == "—")
        {
            RefreshStatus = "该告警没有 DemandId；保持当前全局或任务类型范围。";
            return;
        }

        SelectedTask = Tasks.FirstOrDefault(task => task.DemandId == SelectedAlert.DemandId) ?? SelectedTask;
        OpenPage("TS");
    }

    private void OnScenarioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ScenarioPicker.SelectedItem is not ComboBoxItem { Tag: string scenario }) return;
        CancelRefresh("场景已切换；旧请求取消。", incrementGeneration: true);
        ApplyScenario(scenario);
    }

    private void OnAutoRefreshChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (AutoRefreshToggle.IsChecked == true)
        {
            _autoRefreshTimer.Start();
            RefreshStatus = $"仅为当前的 {SelectedNavigation?.Label} 启用 30 秒自动刷新。";
        }
        else
        {
            _autoRefreshTimer.Stop();
            RefreshStatus = $"{SelectedNavigation?.Label} 恢复默认手动刷新。";
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelRefresh("用户取消当前页刷新；保留最后成功数据。", incrementGeneration: true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F5)
        {
            await RefreshCurrentPageAsync();
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or DataGridCell) return;
        if (e.Key == Key.Left) { CycleVariant(-1); e.Handled = true; }
        else if (e.Key == Key.Right) { CycleVariant(1); e.Handled = true; }
    }

    private static string NormalizeScenario(string scenario) => scenario.ToLowerInvariant() switch
    {
        "healthy" => "Healthy", "offline" => "Offline", "empty" => "Empty", "paging" => "Paging", _ => "Active"
    };

    private static string ScenarioDisplayName(string scenario) => scenario switch
    {
        "Healthy" => "健康", "Offline" => "服务离线", "Empty" => "成功空结果", "Paging" => "分页浏览", _ => "当前异常"
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class NavigationItem : INotifyPropertyChanged
{
    private string _freshness;
    public NavigationItem(string code, string label, string description, string freshness, object page) { Code = code; Label = label; Description = description; _freshness = freshness; Page = page; }
    public string Code { get; }
    public string Label { get; }
    public string Description { get; }
    public object Page { get; }
    public string Freshness { get => _freshness; set { if (_freshness == value) return; _freshness = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Freshness))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class OverviewPage;
public sealed class TasksPage;
public sealed class AlertsPage;
public sealed class SettingsPage;

public sealed record TaskRow(string DemandId, string TaskType, string Sublot, string Area, string Eqp, string Step, string Dates, string Package, string Status, string MesLastSeenAt, int DisappearCount, string LocationRiskCode, string CreatedAt, string GoneAt, string AlertSummary);
public sealed record AlertRow(string Severity, string Code, string Scope, string TaskType, string Sublot, string DemandId, string Message, string Details, int OccurrenceCount, string IsActive, string FirstSeenAt, string LastSeenAt, string ResolvedAt);
public sealed record TaskTypeCount(string TaskType, int Count);
