using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MesIngest.Watch.FluentPrototype;

public sealed class NotificationPrototypeState : INotifyPropertyChanged
{
    private bool _activeFault;
    private int _activeFaultCount;
    private int _activeFaultOccurrences;
    private bool _autoSavePaused;
    private bool _isNarrow;
    private bool _reducedMotion;
    private bool _timersPaused;
    private string _freshnessText = "最近成功 12:42:16 · 自动刷新 10 秒";
    private string _refreshStateText = "后台轮询空闲";
    private string _lastEventText = "等待演示操作";

    public ObservableCollection<PrototypeToast> Toasts { get; } = [];

    public bool HasToasts => Toasts.Count > 0;
    public bool ActiveFault { get => _activeFault; set => Set(ref _activeFault, value); }
    public int ActiveFaultCount { get => _activeFaultCount; set => Set(ref _activeFaultCount, value); }
    public int ActiveFaultOccurrences { get => _activeFaultOccurrences; set => Set(ref _activeFaultOccurrences, value); }
    public bool AutoSavePaused { get => _autoSavePaused; set => Set(ref _autoSavePaused, value); }
    public bool IsNarrow { get => _isNarrow; set => Set(ref _isNarrow, value); }
    public bool ReducedMotion { get => _reducedMotion; set => Set(ref _reducedMotion, value); }
    public bool TimersPaused { get => _timersPaused; set => Set(ref _timersPaused, value); }
    public string FreshnessText { get => _freshnessText; set => Set(ref _freshnessText, value); }
    public string RefreshStateText { get => _refreshStateText; set => Set(ref _refreshStateText, value); }
    public string LastEventText { get => _lastEventText; set => Set(ref _lastEventText, value); }

    public string FaultSummary => ActiveFault
        ? $"错误 · {ActiveFaultCount} 项 · 同源 {ActiveFaultOccurrences} 次"
        : "无持续故障";

    public string WidthModeText => IsNarrow ? "窄窗 · 顶部单列" : "桌面 · 右上 380 epx";
    public string MotionModeText => ReducedMotion ? "减少动态 · 无位移" : "标准动态 · 180 ms";
    public string TimerModeText => TimersPaused ? "悬停/焦点中 · 计时暂停" : "计时运行中";

    public void NotifyToastCollectionChanged()
    {
        OnPropertyChanged(nameof(HasToasts));
    }

    public void NotifyDerivedState()
    {
        OnPropertyChanged(nameof(FaultSummary));
        OnPropertyChanged(nameof(WidthModeText));
        OnPropertyChanged(nameof(MotionModeText));
        OnPropertyChanged(nameof(TimerModeText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        NotifyDerivedState();
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PrototypeToast : INotifyPropertyChanged
{
    private int _occurrences = 1;
    private double _remainingSeconds;

    public required string Source { get; init; }
    public required string Severity { get; init; }
    public required string SeverityText { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required string ActionLabel { get; init; }
    public required double DurationSeconds { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required ICommand DismissCommand { get; init; }

    public int Occurrences { get => _occurrences; set { _occurrences = value; OnPropertyChanged(); OnPropertyChanged(nameof(OccurrenceText)); } }
    public double RemainingSeconds { get => _remainingSeconds; set { _remainingSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimerText)); } }
    public string OccurrenceText => Occurrences > 1 ? $"已合并 {Occurrences} 次" : "首次出现";
    public string TimerText => $"{Math.Max(0, Math.Ceiling(RemainingSeconds)):0} 秒";
    public string TimeText => CreatedAt.ToString("HH:mm:ss");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PrototypeCommand(Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
