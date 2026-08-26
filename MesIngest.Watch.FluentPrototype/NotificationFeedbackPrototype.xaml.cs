using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace MesIngest.Watch.FluentPrototype;

public partial class NotificationFeedbackPrototype : UserControl
{
    private static readonly string[] VariantNames =
    [
        "A — 独立卡片栈",
        "B — 分组活动面板",
        "C — 紧凑命令条",
    ];

    private readonly NotificationPrototypeState _state = new();
    private readonly DispatcherTimer _dismissTimer;
    private readonly HashSet<string> _announcedFaultSources = new(StringComparer.Ordinal);
    private int _variantIndex;

    public NotificationFeedbackPrototype()
    {
        InitializeComponent();
        DataContext = _state;
        _state.Toasts.CollectionChanged += (_, _) => _state.NotifyToastCollectionChanged();
        _dismissTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _dismissTimer.Tick += OnDismissTimerTick;
        _dismissTimer.Start();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void Initialize(string? variant, string? scenario)
    {
        _variantIndex = variant?.Trim().ToUpperInvariant() switch { "B" => 1, "C" => 2, _ => 0 };
        RenderVariant();
        Loaded += async (_, _) => await ApplyScenarioAsync(scenario ?? "stack");
    }

    private async Task ApplyScenarioAsync(string scenario)
    {
        var normalized = scenario.Trim().ToLowerInvariant();
        if (normalized.Contains("narrow")) SetNarrowWindow(true);
        if (normalized.Contains("reduced")) _state.ReducedMotion = true;

        if (normalized.Contains("narrow"))
        {
            AddToast("Host:contract", "Error", "错误", "Host 契约不兼容", "窄窗下通知改为顶部安全边距内的单列。", "打开设置", 8);
            _state.LastEventText = "窄窗：顶部单列 · 安全边距 · 减少动态可用";
        }
        else if (normalized.Contains("silent")) await RunSilentRefreshAsync();
        else if (normalized.Contains("success")) ShowOperationSuccess();
        else if (normalized.Contains("fault")) RaiseContinuingFault();
        else if (normalized.Contains("recovery")) { RaiseContinuingFault(); RecoverFault(); }
        else if (normalized.Contains("conflict")) await ShowConflictDialogAsync();
        else ShowStackDemo();
    }

    private async void OnSilentRefreshClick(object sender, RoutedEventArgs e) => await RunSilentRefreshAsync();
    private void OnSuccessClick(object sender, RoutedEventArgs e) => ShowOperationSuccess();
    private void OnFaultClick(object sender, RoutedEventArgs e) => RaiseContinuingFault();
    private void OnRepeatFaultClick(object sender, RoutedEventArgs e) => RepeatContinuingFault();
    private void OnStackClick(object sender, RoutedEventArgs e) => ShowStackDemo();
    private void OnRecoveryClick(object sender, RoutedEventArgs e) => RecoverFault();
    private async void OnConflictClick(object sender, RoutedEventArgs e) => await ShowConflictDialogAsync();

    private async Task RunSilentRefreshAsync()
    {
        _state.RefreshStateText = "正在后台刷新…";
        _state.LastEventText = "自动刷新开始：无 toast、页面不重排";
        await Task.Delay(850);
        _state.FreshnessText = $"最近成功 {DateTime.Now:HH:mm:ss} · 自动刷新 10 秒";
        _state.RefreshStateText = "后台轮询空闲";
        _state.LastEventText = "自动刷新成功：只更新固定 freshness";
    }

    private void ShowOperationSuccess()
    {
        AddToast("AREA:save", "Success", "成功", "AREA 配置已保存", "东区.txt 已应用到需求系列与资格审计。", "打开文件", 3);
        _state.LastEventText = "用户操作成功：3 秒后自动关闭";
    }

    private void RaiseContinuingFault()
    {
        const string source = "ErrorSearch:MES_AREA_FORMAT_INVALID";
        _state.ActiveFault = true;
        _state.ActiveFaultCount = 1;
        _state.ActiveFaultOccurrences++;
        if (_announcedFaultSources.Add(source))
        {
            AddToast(source, "Error", "错误", "发现新的持续故障", "MES_AREA_FORMAT_INVALID 正在阻断 1 个 Demand。", "查看详情", 8);
            _state.LastEventText = "首次故障：toast 一次，同时收缩到页标题";
        }
        else
        {
            _state.LastEventText = "同一活动故障：仅更新标题状态，不重复通知";
        }
    }

    private void RepeatContinuingFault()
    {
        if (!_state.ActiveFault) RaiseContinuingFault();
        else
        {
            _state.ActiveFaultOccurrences++;
            _state.LastEventText = "同一故障再次轮询：没有新 toast / UIA 播报";
        }
    }

    private void RecoverFault()
    {
        if (!_state.ActiveFault)
        {
            _state.LastEventText = "当前没有可恢复的持续故障";
            return;
        }

        _state.ActiveFault = false;
        _state.ActiveFaultCount = 0;
        _state.ActiveFaultOccurrences = 0;
        _announcedFaultSources.Clear();
        AddToast("ErrorSearch:recovered", "Success", "已恢复", "错误检索已恢复", "最近成功轮次未再发现 MES_AREA_FORMAT_INVALID。", "查看历史", 3);
        _state.LastEventText = "恢复通知：持续故障状态已清除";
    }

    private void ShowStackDemo()
    {
        _state.Toasts.Clear();
        AddToast("AREA:save", "Success", "成功", "AREA 配置已保存", "东区.txt 已应用。", "打开文件", 3);
        AddToast("IngestAlert:zero-drop", "Warning", "警告", "接入质量下降", "PAUSED_ZERO_DROP 已连续出现。", "查看告警", 5);
        AddToast("IngestAlert:zero-drop", "Warning", "警告", "接入质量下降", "同源事件合并，不新增第四张卡。", "查看告警", 5);
        AddToast("Host:contract", "Error", "错误", "Host 契约不兼容", "当前窗口保留上次成功数据。", "打开设置", 8);
        AddToast("Query:obsolete", "Info", "信息", "查询条件已应用", "低优先级旧通知会在溢出时丢弃。", "查看结果", 3);
        _state.LastEventText = "最多三条 · 同源合并 2 次 · 错误/警告优先保留";
    }

    private async Task ShowConflictDialogAsync()
    {
        _state.AutoSavePaused = true;
        _state.LastEventText = "AREA 并发写入冲突：自动保存已暂停";
        var dialog = new ContentDialog(DialogHost)
        {
            Title = "AREA 文件已被其他程序修改",
            PrimaryButtonText = "覆盖并保存",
            SecondaryButtonText = "重新载入文件",
            CloseButtonText = "稍后处理",
            DefaultButton = ContentDialogButton.Secondary,
            DialogWidth = 520,
            Content = new StackPanel
            {
                Children =
                {
                    new System.Windows.Controls.TextBlock { Text = "东区.txt 在 12:42:18 发生外部修改。当前草稿与磁盘版本不能自动合并。", TextWrapping = TextWrapping.Wrap },
                    new System.Windows.Controls.TextBlock { Text = "关闭此对话框不会丢弃草稿，但自动保存会继续暂停。", Margin = new Thickness(0, 12, 0, 0), Foreground = (Brush)FindResource("WatchTextSecondaryBrush"), TextWrapping = TextWrapping.Wrap },
                },
            },
        };

        var result = await dialog.ShowAsync();
        if (result is ContentDialogResult.Primary or ContentDialogResult.Secondary)
        {
            _state.AutoSavePaused = false;
            _state.LastEventText = result == ContentDialogResult.Primary ? "已覆盖保存并恢复自动保存" : "已重新载入磁盘版本";
        }
        else
        {
            _state.LastEventText = "冲突仍未解决：自动保存保持暂停";
        }
    }

    private void AddToast(string source, string severity, string severityText, string title, string message, string actionLabel, double seconds)
    {
        var existing = _state.Toasts.FirstOrDefault(item => item.Source == source);
        if (existing is not null)
        {
            existing.Occurrences++;
            existing.RemainingSeconds = Math.Max(existing.RemainingSeconds, seconds);
            _state.Toasts.Move(_state.Toasts.IndexOf(existing), 0);
            AnimateToastHost();
            return;
        }

        PrototypeToast? toast = null;
        toast = new PrototypeToast
        {
            Source = source,
            Severity = severity,
            SeverityText = severityText,
            Title = title,
            Message = message,
            ActionLabel = actionLabel,
            DurationSeconds = seconds,
            RemainingSeconds = seconds,
            CreatedAt = DateTime.Now,
            DismissCommand = new PrototypeCommand(() => DismissToast(toast!)),
        };
        _state.Toasts.Insert(0, toast);
        TrimToThree();
        AnimateToastHost();
    }

    private void TrimToThree()
    {
        while (_state.Toasts.Count > 3)
        {
            var discard = _state.Toasts
                .OrderBy(item => SeverityRank(item.Severity))
                .ThenBy(item => item.CreatedAt)
                .First();
            _state.Toasts.Remove(discard);
        }
    }

    private static int SeverityRank(string severity) => severity switch { "Error" => 3, "Warning" => 2, "Success" => 1, _ => 0 };

    private void DismissToast(PrototypeToast toast)
    {
        _state.Toasts.Remove(toast);
        _state.LastEventText = _state.ActiveFault
            ? "toast 已关闭；页标题持续故障仍保留"
            : "toast 已关闭";
    }

    private void OnDismissTimerTick(object? sender, EventArgs e)
    {
        if (_state.TimersPaused) return;
        foreach (var toast in _state.Toasts.ToArray())
        {
            toast.RemainingSeconds -= 0.1;
            if (toast.RemainingSeconds <= 0) DismissToast(toast);
        }
    }

    private void AnimateToastHost()
    {
        var duration = TimeSpan.FromMilliseconds(180);
        ToastOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, duration));
        if (_state.ReducedMotion)
        {
            ToastOverlay.RenderTransform = Transform.Identity;
            return;
        }

        var transform = new TranslateTransform(0, -10);
        ToastOverlay.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void OnPreviousVariant(object sender, RoutedEventArgs e) => CycleVariant(-1);
    private void OnNextVariant(object sender, RoutedEventArgs e) => CycleVariant(1);
    private void CycleVariant(int delta) { _variantIndex = (_variantIndex + delta + VariantNames.Length) % VariantNames.Length; RenderVariant(); }

    private void RenderVariant()
    {
        VariantA.Visibility = _variantIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        VariantB.Visibility = _variantIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        VariantC.Visibility = _variantIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        VariantLabel.Text = VariantNames[_variantIndex];
        _state.LastEventText = $"当前方案：{VariantNames[_variantIndex]}";
        _state.NotifyDerivedState();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBox or System.Windows.Controls.PasswordBox or System.Windows.Controls.RichTextBox) return;
        if (e.Key == Key.Left) { CycleVariant(-1); e.Handled = true; }
        else if (e.Key == Key.Right) { CycleVariant(1); e.Handled = true; }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize.Width <= 900);

    private void ApplyResponsiveLayout(bool isNarrow)
    {
        _state.IsNarrow = isNarrow;
        DesktopOverview.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;
        NarrowOverview.Visibility = isNarrow ? Visibility.Visible : Visibility.Collapsed;
        Navigation.IsPaneOpen = false;
        ToastOverlay.Width = isNarrow ? double.NaN : 380;
        ToastOverlay.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        ToastOverlay.Margin = isNarrow ? new Thickness(60, 92, 12, 0) : new Thickness(0, 92, 24, 0);
        WidthToggleButton.Content = isNarrow ? "恢复桌面宽度" : "切到窄窗";
    }

    private void OnWidthToggleClick(object sender, RoutedEventArgs e) => SetNarrowWindow(!_state.IsNarrow);

    private void SetNarrowWindow(bool narrow)
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        window.Width = narrow ? 760 : 1440;
        window.Height = narrow ? 820 : 900;
        ApplyResponsiveLayout(narrow);
    }

    private void OnMotionModeChanged(object sender, RoutedEventArgs e)
    {
        _state.LastEventText = _state.ReducedMotion ? "Windows 减少动态：toast 仅淡入" : "标准动态：180 ms 淡入 + 10 epx 位移";
        _state.NotifyDerivedState();
    }

    private void OnToastHostMouseEnter(object sender, MouseEventArgs e) => PauseTimers(true);
    private void OnToastHostMouseLeave(object sender, MouseEventArgs e) => PauseTimers(ToastOverlay.IsKeyboardFocusWithin);
    private void OnToastHostGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => PauseTimers(true);
    private void OnToastHostLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Dispatcher.BeginInvoke(() => PauseTimers(ToastOverlay.IsMouseOver || ToastOverlay.IsKeyboardFocusWithin));

    private void PauseTimers(bool paused)
    {
        _state.TimersPaused = paused;
        _state.NotifyDerivedState();
    }
}
