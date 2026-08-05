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
    private static readonly string[] VariantKeys = ["A", "B", "C"];
    private static readonly string[] VariantNames = ["全局态势看板", "事件处置优先", "证据时间线优先"];

    private readonly DispatcherTimer _autoRefreshTimer;
    private readonly Dictionary<string, DateTimeOffset> _lastRefreshByPage = new();
    private CancellationTokenSource? _refreshCancellation;
    private long _refreshGeneration;
    private int _variantIndex;
    private string _scenario = "Healthy";
    private NavigationItem? _selectedNavigation;
    private object? _selectedPage;
    private DemandRow? _selectedDemand;
    private AlertRow? _selectedAlert;
    private string _refreshStatus = "尚未刷新 · 当前窗口保留最后成功数据";
    private string _healthLabel = "健康";
    private string _healthDetail = "Host 在线 · 最近 poll 成功";
    private string _latencyLabel = "2.84 s";
    private string _ageLatencySummary = "MES 数据年龄：42 秒（字段 DATES） · 链路耗时：2.84 秒";
    private string _emptyStateText = string.Empty;
    private string _pageLabel = "已加载 1–25 · 下一页 cursor 可用";
    private int _activeAlertCount = 2;
    private int _visibleDemandCount = 626;

    public MainWindow(string initialVariant, string initialScenario)
    {
        InitializeComponent();
        DataContext = this;

        Navigation =
        [
            new("OV", "概览", "10 秒内判断接入健康、当前异常、VISIBLE Demand 与最新链路耗时。", "12:42:16", new OverviewPage()),
            new("DM", "运输需求", "显式提交筛选，VISIBLE/GONE 分别保存排序与游标分页。", "12:42:16", new DemandsPage()),
            new("AL", "告警分析", "只读定位 Alert、关联 Demand 和原始证据，不在 Watch 中处置。", "12:42:15", new AlertsPage()),
            new("PF", "性能分析", "分离链路处理耗时与 MES 数据年龄，下钻四阶段 trace。", "12:42:14", new PerformancePage()),
            new("DG", "诊断", "统一事件日志与脱敏诊断包，文本日志仅作辅助取证。", "12:41:58", new DiagnosticsPage()),
            new("ST", "设置", "单 Host、按页自动刷新、保留与容量上限。", "本机", new SettingsPage())
        ];

        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
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
    }

    public ObservableCollection<NavigationItem> Navigation { get; }
    public ObservableCollection<DemandRow> Demands { get; } = [];
    public ObservableCollection<AlertRow> Alerts { get; } = [];
    public ObservableCollection<StageRow> Stages { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];

    public NavigationItem? SelectedNavigation
    {
        get => _selectedNavigation;
        set
        {
            if (!SetField(ref _selectedNavigation, value) || value is null)
            {
                return;
            }

            CancelRefresh("已切换页面；旧请求已取消，未覆盖新页面。", incrementGeneration: true);
            SelectedPage = value.Page;
            RefreshStatus = _lastRefreshByPage.TryGetValue(value.Code, out var refreshedAt)
                ? $"{value.Label}最后成功刷新 {refreshedAt:HH:mm:ss} · 其它页面未刷新"
                : $"{value.Label}尚未刷新 · 其它页面保持原状态";
        }
    }

    public object? SelectedPage { get => _selectedPage; private set => SetField(ref _selectedPage, value); }
    public DemandRow? SelectedDemand { get => _selectedDemand; set => SetField(ref _selectedDemand, value); }
    public AlertRow? SelectedAlert { get => _selectedAlert; set => SetField(ref _selectedAlert, value); }
    public string RefreshStatus { get => _refreshStatus; private set => SetField(ref _refreshStatus, value); }
    public string HealthLabel { get => _healthLabel; private set => SetField(ref _healthLabel, value); }
    public string HealthDetail { get => _healthDetail; private set => SetField(ref _healthDetail, value); }
    public string LatencyLabel { get => _latencyLabel; private set => SetField(ref _latencyLabel, value); }
    public string AgeLatencySummary { get => _ageLatencySummary; private set => SetField(ref _ageLatencySummary, value); }
    public string EmptyStateText { get => _emptyStateText; private set => SetField(ref _emptyStateText, value); }
    public string PageLabel { get => _pageLabel; private set => SetField(ref _pageLabel, value); }
    public int ActiveAlertCount { get => _activeAlertCount; private set => SetField(ref _activeAlertCount, value); }
    public int VisibleDemandCount { get => _visibleDemandCount; private set => SetField(ref _visibleDemandCount, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Capture(string path)
    {
        var absolutePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(absolutePath);
        encoder.Save(stream);
    }

    private void ApplyScenario(string scenario)
    {
        _scenario = scenario;
        Demands.Clear();
        Alerts.Clear();
        Stages.Clear();
        Events.Clear();

        foreach (var demand in BuildDemands(scenario))
        {
            Demands.Add(demand);
        }

        foreach (var alert in BuildAlerts(scenario))
        {
            Alerts.Add(alert);
        }

        var slow = scenario == "Slow";
        AddStages(slow);
        AddEvents(scenario);

        HealthLabel = scenario switch
        {
            "Offline" => "离线",
            "Slow" => "需关注",
            "Alert" => "接入健康",
            "Empty" => "健康",
            _ => "健康"
        };
        HealthDetail = scenario switch
        {
            "Offline" => "Host 连接失败 · 保留 12:39:44 成功窗口",
            "Slow" => "Host 在线 · 最近 poll 成功但 MES 读取偏慢",
            "Alert" => "Host 在线 · poll 成功 · 领域异常待定位",
            "Empty" => "Host 在线 · 成功快照中无 VISIBLE Demand",
            _ => "Host 在线 · 最近 poll 成功 · 数据新鲜"
        };
        LatencyLabel = scenario switch
        {
            "Offline" => "未知",
            "Slow" => "12.7 s",
            _ => "2.84 s"
        };
        AgeLatencySummary = $"MES 数据年龄：42 秒（字段 DATES） · 链路耗时：{LatencyLabel.Replace("s", "秒")}";
        VisibleDemandCount = scenario == "Empty" ? 0 : scenario == "Paging" ? 12_483 : 626;
        ActiveAlertCount = Alerts.Count;
        EmptyStateText = scenario == "Empty" ? "当前没有活动异常或 VISIBLE Demand。这是成功空结果，不是连接失败。" : string.Empty;
        PageLabel = scenario == "Paging" ? "已加载 51–75 / 12,483 · cursor=eyJzZXEiOj..." : "已加载 1–25 · 下一页 cursor 可用";
        SelectedDemand = Demands.FirstOrDefault();
        SelectedAlert = Alerts.FirstOrDefault();
        RefreshStatus = $"已切换为“{ScenarioDisplayName(scenario)}”假数据场景 · 未发出网络请求";
    }

    private static IEnumerable<DemandRow> BuildDemands(string scenario)
    {
        if (scenario == "Empty")
        {
            return [];
        }

        return
        [
            new("dmd-7f91c2", "MOVE_IN", "SL240804-017", "VISIBLE", "FAB2 / EQP-17", scenario == "Slow" ? "4m 12s" : "42s", "12:42:16"),
            new("dmd-2a10bd", "MOVE_OUT", "SL240804-021", "VISIBLE", "FAB2 / EQP-03", "1m 08s", "12:42:16"),
            new("dmd-98c44a", "TRANSFER", "SL240804-008", "VISIBLE", "BUF1 / EQP-09", "2m 31s", "12:42:15"),
            new("dmd-06bd33", "MOVE_IN", "SL240804-025", "VISIBLE", "FAB1 / EQP-22", "18s", "12:42:16"),
            new("dmd-a7e145", "TRANSFER", "SL240804-013", "VISIBLE", "BUF2 / EQP-04", "3m 05s", "12:42:14")
        ];
    }

    private static IEnumerable<AlertRow> BuildAlerts(string scenario)
    {
        if (scenario is "Empty" or "Healthy")
        {
            return [];
        }

        if (scenario == "Offline")
        {
            return [new("WATCH", "HOST_UNREACHABLE", "无法连接 Host；这不是 IngestAlert。", "—", "12:42:09", "12:42:09 – 现在")];
        }

        if (scenario == "Slow")
        {
            return [new("WARN", "POLL_LATENCY_HIGH", "MES 读取阶段连续 3 轮超过 10 秒。", "dmd-7f91c2", "12:42:16", "12:40:04 – 12:42:16")];
        }

        return
        [
            new("ERROR", "DUPLICATE_ACTIVE_KEY", "同一 TASK_TYPE + SUBLOT 出现两条活动行。", "dmd-7f91c2", "12:42:16", "12:38:02 – 12:42:16"),
            new("WARN", "REAPPEAR", "GONE Demand 以同键新实例重新出现。", "dmd-2a10bd", "12:41:49", "12:41:49 – 12:41:49")
        ];
    }

    private void AddStages(bool slow)
    {
        Stages.Add(new("MES 读取", slow ? 78 : 43, slow ? "9.91 s" : "1.22 s"));
        Stages.Add(new("接入处理", slow ? 10 : 21, slow ? "1.27 s" : "0.60 s"));
        Stages.Add(new("本地投影", slow ? 9 : 27, slow ? "1.14 s" : "0.77 s"));
        Stages.Add(new("Watch 展示", slow ? 3 : 9, slow ? "0.38 s" : "0.25 s"));
    }

    private void AddEvents(string scenario)
    {
        Events.Add(new("12:42:16", "POLL", scenario == "Slow" ? "成功 · 12.7 s · MES_READ 慢" : "成功 · 2.84 s · 626 rows"));
        Events.Add(new("12:42:14", "WATCH", "当前页刷新完成 · response generation 18"));
        Events.Add(new("12:41:49", "ALERT", scenario switch
        {
            "Alert" => "REAPPEAR opened · dmd-2a10bd",
            "Slow" => "POLL_LATENCY_HIGH opened · MES_READ",
            _ => "无活动 IngestAlert"
        }));
        Events.Add(new("12:39:44", "CONNECTION", scenario == "Offline" ? "最后成功窗口；随后 Host unreachable" : "Host contract 兼容 · API v1"));
    }

    private async Task RefreshCurrentPageAsync()
    {
        if (SelectedNavigation is null)
        {
            return;
        }

        if (_refreshCancellation is not null)
        {
            CancelRefresh("用户取消当前页刷新；保留最后成功窗口。", incrementGeneration: true);
            return;
        }

        var generation = ++_refreshGeneration;
        var page = SelectedNavigation;
        var delay = _scenario == "Slow" ? TimeSpan.FromSeconds(6) : TimeSpan.FromMilliseconds(900);
        _refreshCancellation = new CancellationTokenSource();
        RefreshButton.Content = "取消刷新";
        RefreshStatus = $"正在刷新 {page.Label} · 单飞请求 generation {generation} · 可取消";

        try
        {
            await Task.Delay(delay, _refreshCancellation.Token);
            if (generation != _refreshGeneration || page != SelectedNavigation)
            {
                return;
            }

            var completedAt = DateTimeOffset.Now;
            _lastRefreshByPage[page.Code] = completedAt;
            page.Freshness = completedAt.ToString("HH:mm:ss");
            RefreshStatus = $"{page.Label}刷新成功 {completedAt:HH:mm:ss} · 只更新当前页 · generation {generation}";
        }
        catch (OperationCanceledException)
        {
            // The visible status was set by CancelRefresh.
        }
        finally
        {
            _refreshCancellation?.Dispose();
            _refreshCancellation = null;
            RefreshButton.Content = "刷新当前页";
        }
    }

    private void CancelRefresh(string status, bool incrementGeneration)
    {
        if (incrementGeneration)
        {
            _refreshGeneration++;
        }

        if (_refreshCancellation is not null)
        {
            _refreshCancellation.Cancel();
            RefreshStatus = status;
        }
    }

    private void ApplyVariant()
    {
        VariantA.Visibility = _variantIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        VariantB.Visibility = _variantIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        VariantC.Visibility = _variantIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        VariantLabel.Text = $"{VariantKeys[_variantIndex]} — {VariantNames[_variantIndex]}";
    }

    private void CycleVariant(int direction)
    {
        _variantIndex = (_variantIndex + direction + VariantKeys.Length) % VariantKeys.Length;
        ApplyVariant();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshCurrentPageAsync();
    private void OnPreviousVariantClick(object sender, RoutedEventArgs e) => CycleVariant(-1);
    private void OnNextVariantClick(object sender, RoutedEventArgs e) => CycleVariant(1);
    private void OnNextPageClick(object sender, RoutedEventArgs e) => PageLabel = "已加载 26–50 · 上一页/下一页 cursor 均可用";

    private void OnAlertDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AlertRow alert })
        {
            SelectedAlert = alert;
            SelectedNavigation = Navigation[2];
        }
    }

    private void OnScenarioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ScenarioPicker.SelectedItem is not ComboBoxItem { Tag: string scenario })
        {
            return;
        }

        CancelRefresh("场景已切换；旧请求取消。", incrementGeneration: true);
        ApplyScenario(scenario);
    }

    private void OnAutoRefreshChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (AutoRefreshToggle.IsChecked == true)
        {
            _autoRefreshTimer.Start();
            RefreshStatus = $"仅为{SelectedNavigation?.Label}启用 10 秒自动刷新；切页后跟随当前页。";
        }
        else
        {
            _autoRefreshTimer.Stop();
            RefreshStatus = $"{SelectedNavigation?.Label}恢复默认手动刷新。";
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or DataGridCell)
        {
            return;
        }

        if (e.Key == Key.Left)
        {
            CycleVariant(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            CycleVariant(1);
            e.Handled = true;
        }
    }

    private static string ScenarioDisplayName(string scenario) => scenario switch
    {
        "Alert" => "活动异常",
        "Slow" => "慢链路",
        "Offline" => "Host 离线",
        "Empty" => "空状态",
        "Paging" => "分页浏览",
        _ => "健康"
    };

    private static string NormalizeScenario(string scenario) => scenario.ToLowerInvariant() switch
    {
        "alert" => "Alert",
        "slow" => "Slow",
        "offline" => "Offline",
        "empty" => "Empty",
        "paging" => "Paging",
        _ => "Healthy"
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class NavigationItem : INotifyPropertyChanged
{
    private string _freshness;

    public NavigationItem(string code, string label, string description, string freshness, object page)
    {
        Code = code;
        Label = label;
        Description = description;
        _freshness = freshness;
        Page = page;
    }

    public string Code { get; }
    public string Label { get; }
    public string Description { get; }
    public object Page { get; }
    public string Freshness
    {
        get => _freshness;
        set
        {
            if (_freshness == value) return;
            _freshness = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Freshness)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class OverviewPage;
public sealed class DemandsPage;
public sealed class AlertsPage;
public sealed class PerformancePage;
public sealed class DiagnosticsPage;
public sealed class SettingsPage;

public sealed record DemandRow(string DemandId, string TaskType, string Sublot, string Status, string Location, string DataAge, string LastSeen);
public sealed record AlertRow(string Severity, string Code, string Message, string DemandId, string LastSeen, string TimeRange);
public sealed record StageRow(string Name, double Percent, string Duration);
public sealed record EventRow(string Time, string Kind, string Message);
