using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

internal sealed class WatchVisualScenario : IDisposable
{
    private static readonly DateTimeOffset FixedNow =
        DateTimeOffset.Parse("2026-08-08T04:42:16Z");

    private readonly WatchVisualCase _visualCase;
    private readonly WatchApplicationComposition _composition;
    private readonly FixedTimeProvider _timeProvider;
    private readonly string _root;
    private WatchVisualCapture? _captureTarget;

    private WatchVisualScenario(
        WatchVisualCase visualCase,
        WatchApplicationComposition composition,
        MainWindow window,
        FixedTimeProvider timeProvider,
        string root)
    {
        _visualCase = visualCase;
        _composition = composition;
        _timeProvider = timeProvider;
        _root = root;
        Window = window;
    }

    public MainWindow Window { get; }

    public static WatchVisualScenario Create(WatchVisualCase visualCase)
    {
        var host = BuildHost(visualCase.State);
        var timeProvider = new FixedTimeProvider();
        var root = Path.Combine(Path.GetTempPath(), $"watch-visual-{Guid.NewGuid():N}");
        var composition = WatchApplicationComposition.Create(
            Options(),
            host.CreateAdapter,
            logDirectory: Path.Combine(root, "logs"),
            layoutPreferencesPath: Path.Combine(root, "layout.json"),
            autoRefreshPreferencesPath: Path.Combine(root, "auto-refresh.json"),
            connectionPreferencesPath: Path.Combine(root, "connection.json"),
            timeProvider: timeProvider);
        var window = composition.CreateMainWindow();
        window.Width = visualCase.Width;
        window.Height = visualCase.Height;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.ShowInTaskbar = false;
        return new WatchVisualScenario(visualCase, composition, window, timeProvider, root);
    }

    public async Task PrepareAsync()
    {
        Window.Show();
        PumpUntil(() => !Text("PageRefreshContextText")
            .Contains("lastSuccess=(none)", StringComparison.Ordinal));
        if (_visualCase.State != WatchVisualState.OverviewOfflineStale)
        {
            _timeProvider.Advance(TimeSpan.FromSeconds(6));
            PumpUntil(() => !Visible("ErrorBanner"));
        }

        switch (_visualCase.State)
        {
            case WatchVisualState.OverviewHealthy:
            case WatchVisualState.OverviewDegraded:
                break;
            case WatchVisualState.OverviewOfflineStale:
                Click("OverviewRefreshButton");
                PumpUntil(() => Visible("ErrorBanner"));
                break;
            case WatchVisualState.DemandsVisibleSelected:
                Navigate(1);
                PumpUntil(() => Grid("DemandsGrid").Items.Count == FakeDemands(visible: true).Count);
                SelectFirst("DemandsGrid");
                break;
            case WatchVisualState.DemandsGoneSelected:
                Navigate(1);
                ((TabControl)Window.FindName("DemandStatusTabs")).SelectedIndex = 1;
                PumpUntil(() => Grid("DemandsGrid").Items.Count == FakeDemands(visible: false).Count);
                SelectFirst("DemandsGrid");
                break;
            case WatchVisualState.DemandsEmpty:
                Navigate(1);
                PumpUntil(() => Text("RowCountText").Contains("当前查询无结果", StringComparison.Ordinal));
                break;
            case WatchVisualState.DemandsLoading:
                Navigate(1);
                PumpUntil(() => Visible("DemandBusyText"));
                break;
            case WatchVisualState.DemandsFailureRetainsResults:
                Navigate(1);
                PumpUntil(() => Grid("DemandsGrid").Items.Count > 0);
                Click("DemandRefreshButton");
                PumpUntil(() => Visible("ErrorBanner"));
                break;
            case WatchVisualState.AlertsActiveSelected:
                Navigate(2);
                PumpUntil(() => Grid("AlertsGrid").Items.Count == FakeAlerts(active: true).Count);
                SelectFirst("AlertsGrid");
                break;
            case WatchVisualState.AlertsResolved:
                Navigate(2);
                PumpUntil(() => Grid("AlertsGrid").Items.Count > 0);
                ((ComboBox)Window.FindName("AlertActivityFilter")).SelectedIndex = 1;
                Click("AlertQueryButton");
                PumpUntil(() => Grid("AlertsGrid").Items.Cast<WatchAlertDto>().All(item => !item.IsActive));
                SelectFirst("AlertsGrid");
                break;
            case WatchVisualState.AlertsEmpty:
                Navigate(2);
                PumpUntil(() => Text("AlertCountText").Contains("当前查询无结果", StringComparison.Ordinal));
                break;
            case WatchVisualState.AlertsLoading:
                Navigate(2);
                PumpUntil(() => Visible("AlertBusyText"));
                break;
            case WatchVisualState.AlertsFailureRetainsResults:
                Navigate(2);
                PumpUntil(() => Grid("AlertsGrid").Items.Count > 0);
                Click("AlertRefreshButton");
                PumpUntil(() => Visible("ErrorBanner"));
                break;
            case WatchVisualState.SettingsDefault:
                Navigate(3);
                break;
            case WatchVisualState.SettingsValidationError:
                Navigate(3);
                ((TextBox)Window.FindName("RequestTimeoutInput")).Text = "0";
                Click("ApplyHostButton");
                PumpUntil(() => !string.IsNullOrWhiteSpace(Text("SettingsValidationText")));
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        Window.UpdateLayout();
        await Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    public WatchVisualCapture CreateCaptureTarget()
    {
        if (_captureTarget is not null)
        {
            return _captureTarget;
        }

        var content = Window.Content as FrameworkElement
            ?? throw new InvalidOperationException("The Watch visual window content is not a FrameworkElement.");
        var width = _visualCase.Width;
        var height = _visualCase.Height;
        Window.Hide();
        Window.Content = null;
        var surface = new WatchVisualCapture
        {
            Width = width,
            Height = height,
            Background = Window.Background,
            Child = content,
            FlowDirection = Window.FlowDirection,
            Language = Window.Language,
            Resources = Window.Resources,
            SnapsToDevicePixels = Window.SnapsToDevicePixels,
            UseLayoutRounding = Window.UseLayoutRounding,
        };
        surface.SetValue(TextElement.FontFamilyProperty, Window.FontFamily);
        surface.SetValue(TextElement.FontSizeProperty, Window.FontSize);
        surface.SetValue(TextElement.FontStretchProperty, Window.FontStretch);
        surface.SetValue(TextElement.FontStyleProperty, Window.FontStyle);
        surface.SetValue(TextElement.FontWeightProperty, Window.FontWeight);
        surface.SetValue(TextElement.ForegroundProperty, Window.Foreground);
        TextOptions.SetTextFormattingMode(surface, TextOptions.GetTextFormattingMode(Window));
        TextOptions.SetTextRenderingMode(surface, TextOptions.GetTextRenderingMode(Window));

        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        _captureTarget = surface;
        return surface;
    }

    public void Dispose()
    {
        _composition.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Navigate(int index)
    {
        ((ListBox)Window.FindName("PrimaryNavigation")).SelectedIndex = index;
        PumpDispatcher();
    }

    private void SelectFirst(string name)
    {
        var grid = Grid(name);
        grid.SelectedIndex = 0;
        grid.ScrollIntoView(grid.SelectedItem);
        PumpDispatcher();
    }

    private void Click(string name) =>
        ((Button)Window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private DataGrid Grid(string name) => (DataGrid)Window.FindName(name);

    private string Text(string name) => Window.FindName(name) switch
    {
        TextBlock text => text.Text,
        _ => throw new InvalidOperationException($"{name} is not a TextBlock."),
    };

    private bool Visible(string name) =>
        ((FrameworkElement)Window.FindName(name)).Visibility == Visibility.Visible;

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpDispatcher();
        }

        Assert.True(condition(), "Timed out while preparing the deterministic Watch visual state.");
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static ScriptedFakeHost BuildHost(WatchVisualState state)
    {
        var demandGate = state == WatchVisualState.DemandsLoading ? new FakeHostGate() : null;
        var alertGate = state == WatchVisualState.AlertsLoading ? new FakeHostGate() : null;
        var demandCalls = 0;
        var alertCalls = 0;
        var offline = state == WatchVisualState.OverviewOfflineStale;
        var emptyDemands = state == WatchVisualState.DemandsEmpty;
        var emptyAlerts = state == WatchVisualState.AlertsEmpty;
        var failDemands = state == WatchVisualState.DemandsFailureRetainsResults;
        var failAlerts = state == WatchVisualState.AlertsFailureRetainsResults;
        var showAlerts = state is WatchVisualState.OverviewDegraded
            or WatchVisualState.AlertsActiveSelected
            or WatchVisualState.AlertsResolved
            or WatchVisualState.AlertsLoading
            or WatchVisualState.AlertsFailureRetainsResults;

        var scenario = new FakeHostScenario($"visual-{state}")
        {
            PollHealth = FakeHostReply.Sequence<FakeHostUnit, WatchPollHealthDto?>(
                FakeHostReply.Return<WatchPollHealthDto?>(Health()),
                offline
                    ? FakeHostReply.Fail<WatchPollHealthDto?>(
                        WatchHostFailureKind.Network,
                        "/api/poll-health",
                        "固定假 Host 已离线")
                    : FakeHostReply.Return<WatchPollHealthDto?>(Health())),
            DemandPage = FakeHostReply.Select<WatchDemandBrowseQuery, WatchDemandPage>(query =>
            {
                demandCalls++;
                if (offline && demandCalls > 1)
                {
                    return FakeHostReply.Fail<WatchDemandPage>(
                        WatchHostFailureKind.Network,
                        "/api/demands",
                        "固定假 Host 已离线");
                }

                if (failDemands && demandCalls > 2)
                {
                    return FakeHostReply.Fail<WatchDemandPage>(
                        WatchHostFailureKind.Network,
                        "/api/demands",
                        "刷新失败，保留最后成功的 TransportDemand");
                }

                IReadOnlyList<WatchDemandDto> items = emptyDemands
                    ? []
                    : FakeDemands(query.Status != "GONE");
                var page = new WatchDemandPage(items, null, false);
                return demandGate is not null && demandCalls > 1
                    ? FakeHostReply.After(demandGate, page)
                    : FakeHostReply.Return(page);
            }),
            AlertPage = FakeHostReply.Select<WatchAlertBrowseQuery, WatchAlertPage>(query =>
            {
                alertCalls++;
                if (offline && alertCalls > 1)
                {
                    return FakeHostReply.Fail<WatchAlertPage>(
                        WatchHostFailureKind.Network,
                        "/api/alerts",
                        "固定假 Host 已离线");
                }

                if (failAlerts && alertCalls > 2)
                {
                    return FakeHostReply.Fail<WatchAlertPage>(
                        WatchHostFailureKind.Network,
                        "/api/alerts",
                        "刷新失败，保留最后成功的 IngestAlert");
                }

                IReadOnlyList<WatchAlertDto> items = emptyAlerts || !showAlerts
                    ? []
                    : FakeAlerts(query.Active != false);
                var page = new WatchAlertPage(items, null, false);
                return alertGate is not null && alertCalls > 1
                    ? FakeHostReply.After(alertGate, page)
                    : FakeHostReply.Return(page);
            }),
        };
        return new ScriptedFakeHost(scenario);
    }

    private static WatchOptions Options() => new()
    {
        BaseUrl = "http://fake-watch.visual",
        SharedSecret = string.Empty,
        RequestTimeoutSeconds = 30,
        RefreshSeconds = 300,
        RenderingMode = WatchRenderingMode.SoftwareOnly,
    };

    private static WatchPollHealthDto Health() => new(
        FixedNow.AddSeconds(-3),
        FixedNow.AddSeconds(-1),
        1842,
        626,
        true,
        "SUCCESS",
        [new WatchTaskTypePauseDto("WIRE_TO_GATE", false, 84, 0)],
        OracleDurationMs: 1220);

    internal static IReadOnlyList<WatchDemandDto> FakeDemands(bool visible)
    {
        var taskTypes = new[]
        {
            "DIE_TO_WIRE_STAGING",
            "DIE_TO_OVEN",
            "WIRE_TO_GATE",
            "WIRE_TO_OPTICAL",
            "STAGING_TO_WIRE",
            "WIRE_TO_NITROGEN",
        };
        return taskTypes.Select((taskType, index) => new WatchDemandDto(
            DemandId: $"{index + 1:x8}{new string((char)('a' + index), 24)}",
            TaskType: taskType,
            Sublot: $"Q260808-{index + 1:00}-超长子批号",
            Area: $"AREA-{index + 1:00}",
            Eqp: $"EQP-{index + 1:00}",
            Step: index == 2 ? "焊线2与入库等待的超长下一工序名称" : $"STEP-{index + 1:00}",
            Dates: FixedNow.AddHours(-index - 1),
            Package: $"PKG-{index + 1:00}-长封装名称",
            Status: visible ? "VISIBLE" : "GONE",
            MesLastSeenAt: FixedNow.AddMinutes(-index),
            DisappearCount: visible ? index : 3,
            LocationRisk: index == 4,
            LocationRiskCode: index == 4 ? "AREA_UNPARSEABLE" : null,
            CreatedAt: FixedNow.AddDays(-1).AddMinutes(index),
            GoneAt: visible ? null : FixedNow.AddMinutes(-index)))
            .ToArray();
    }

    internal static IReadOnlyList<WatchAlertDto> FakeAlerts(bool active)
    {
        var codes = new[]
        {
            "POLL_FAILURE",
            "POLL_INCOMPLETE",
            "DUPLICATE_RECONCILE_KEY",
            "PAUSED_ZERO_DROP",
            "FIELD_DRIFT",
            "REAPPEAR_AFTER_GONE",
        };
        var taskTypes = new[]
        {
            "DIE_TO_WIRE_STAGING",
            "DIE_TO_OVEN",
            "WIRE_TO_GATE",
            "WIRE_TO_OPTICAL",
            "STAGING_TO_WIRE",
            "WIRE_TO_NITROGEN",
        };
        return codes.Select((code, index) => new WatchAlertDto(
            AlertId: $"alert-{index + 1:00}",
            Code: code,
            Severity: index < 5 ? "ERROR" : "WARNING",
            TaskType: taskTypes[index],
            Sublot: $"Q260808-{index + 1:00}-超长子批号",
            DemandId: FakeDemands(visible: true)[index].DemandId,
            Message: $"{code}：用于视觉回归的固定中文长文本，说明 MES 快照、投影和当前查询之间的关系。",
            Details: "area=AREA-01; expected=EQP-01; observed=EQP-99; 不包含凭据或生产数据",
            FirstSeenAt: FixedNow.AddMinutes(-20 - index),
            LastSeenAt: FixedNow.AddMinutes(-index),
            OccurrenceCount: index + 1,
            IsActive: active,
            ResolvedAt: active ? null : FixedNow.AddMinutes(-index),
            CreatedAt: FixedNow.AddMinutes(-30 - index)))
            .ToArray();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = FixedNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}

public sealed class WatchVisualCapture : Border
{
}
