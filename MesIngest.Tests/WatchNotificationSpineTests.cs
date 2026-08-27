using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MesIngest.Watch;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace MesIngest.Tests;

[Collection("WpfDesktop")]
public sealed class WatchNotificationSpineTests
{
    private static readonly DateTimeOffset StartedAt =
        DateTimeOffset.Parse("2026-08-26T08:00:00Z");

    [Fact]
    public void Production_window_rejects_notifications_without_typed_bilingual_content() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("typed-contract");
            WatchWorkspaceWindow? window = null;
            try
            {
                window = CreateWorkspace(root, new ManualTimerTimeProvider(StartedAt));
                var notification = new WatchNotificationEvent(
                    new WatchNotificationSource(
                        "test.untyped",
                        WatchNotificationScope.Global,
                        "untyped"),
                    WatchNotificationSeverity.Warning,
                    "警告",
                    "未类型化通知",
                    "不得进入生产窗口");

                var error = Assert.Throws<ArgumentException>(() =>
                    window.PresentNotification(notification));

                Assert.Contains("typed bilingual", error.Message, StringComparison.Ordinal);
                Assert.Empty(NotificationItems(window).Items);
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });

    [Theory]
    [InlineData((int)WatchNotificationSeverity.Information, 3)]
    [InlineData((int)WatchNotificationSeverity.Success, 3)]
    [InlineData((int)WatchNotificationSeverity.Warning, 5)]
    [InlineData((int)WatchNotificationSeverity.Error, 8)]
    public void Window_notifications_expire_on_the_semantic_3_5_8_second_deadlines(
        int severityValue,
        int lifetimeSeconds) =>
        StaTestRunner.Run(() =>
        {
            var severity = (WatchNotificationSeverity)severityValue;
            var root = NewTempDirectory($"lifetime-{severity}");
            WatchWorkspaceWindow? window = null;
            try
            {
                var clock = new ManualTimerTimeProvider(StartedAt);
                window = CreateWorkspace(root, clock);
                window.PresentNotification(Notification(
                    $"lifetime-{severity}",
                    severity,
                    title: $"{severity} deadline"));

                var items = NotificationItems(window);
                Assert.Single(items.Items);
                Assert.Equal(
                    $"{lifetimeSeconds} 秒",
                    TemplateElement<TextBlock>(window, items, 0, "NotificationTimerText").Text);

                clock.Advance(TimeSpan.FromMilliseconds((lifetimeSeconds * 1000) - 1));
                Assert.Single(items.Items);
                Assert.Equal(
                    "1 秒",
                    TemplateElement<TextBlock>(window, items, 0, "NotificationTimerText").Text);

                clock.Advance(TimeSpan.FromMilliseconds(1));
                Assert.Empty(items.Items);
                Assert.Equal(Visibility.Collapsed, NotificationOverlay(window).Visibility);
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Same_source_merges_moves_to_top_extends_deadline_and_priority_keeps_three_cards() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("merge-priority");
            WatchWorkspaceWindow? window = null;
            try
            {
                var clock = new ManualTimerTimeProvider(StartedAt);
                window = CreateWorkspace(root, clock);
                window.PresentNotification(Notification(
                    "warning-source",
                    WatchNotificationSeverity.Warning,
                    "接入质量下降",
                    "首次状态"));
                clock.Advance(TimeSpan.FromSeconds(4));
                window.PresentNotification(Notification(
                    "success-source",
                    WatchNotificationSeverity.Success,
                    "设置已保存"));
                window.PresentNotification(Notification(
                    "error-source",
                    WatchNotificationSeverity.Error,
                    "Host 契约不兼容"));
                window.PresentNotification(Notification(
                    "warning-source",
                    WatchNotificationSeverity.Warning,
                    "接入质量仍在下降",
                    "最新真实状态"));

                var items = NotificationItems(window);
                Assert.Equal(3, items.Items.Count);
                Assert.Equal(
                    new[] { "接入质量仍在下降", "Host 契约不兼容", "设置已保存" },
                    Titles(window, items));
                Assert.Equal(
                    "最新真实状态",
                    TemplateElement<TextBlock>(window, items, 0, "NotificationMessageText").Text);
                Assert.Equal(
                    "已合并 2 次",
                    TemplateElement<TextBlock>(window, items, 0, "NotificationOccurrenceText").Text);
                Assert.Equal(
                    "5 秒",
                    TemplateElement<TextBlock>(window, items, 0, "NotificationTimerText").Text);

                window.PresentNotification(Notification(
                    "information-source",
                    WatchNotificationSeverity.Information,
                    "刷新条件已更新"));

                Assert.Equal(3, items.Items.Count);
                Assert.Equal(
                    new[] { "刷新条件已更新", "接入质量仍在下降", "Host 契约不兼容" },
                    Titles(window, items));
                Assert.DoesNotContain("设置已保存", Titles(window, items));

                clock.Advance(TimeSpan.FromSeconds(4));
                Assert.Equal(
                    "1 秒",
                    TemplateElement<TextBlock>(window, items, 0, "NotificationTimerText").Text);
                Assert.Contains("接入质量仍在下降", Titles(window, items));
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Polite_announcement_is_emitted_once_per_new_semantic_event_not_for_merge_or_timer()
    {
        var clock = new ManualTimerTimeProvider(StartedAt);
        using var coordinator = new WatchWindowNotificationCoordinator(clock);
        var announcements = new List<string>();
        coordinator.SnapshotChanged += (_, change) =>
        {
            if (change.Announcement is not null)
            {
                announcements.Add(change.Announcement);
            }
        };

        coordinator.Present(Notification(
            "continuing-fault",
            WatchNotificationSeverity.Error,
            "发现新的持续故障"));
        clock.Advance(TimeSpan.FromSeconds(1));
        coordinator.Present(Notification(
            "continuing-fault",
            WatchNotificationSeverity.Error,
            "持续故障仍在活动"));
        clock.Advance(TimeSpan.FromSeconds(1));
        coordinator.Present(Notification(
            "continuing-fault-recovered",
            WatchNotificationSeverity.Success,
            "读取已恢复"));

        Assert.Equal(2, announcements.Count);
        Assert.Contains("发现新的持续故障", announcements[0], StringComparison.Ordinal);
        Assert.Contains("读取已恢复", announcements[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Real_local_preferences_save_reports_success_and_controlled_retryable_failure() =>
        StaTestRunner.Run(() =>
        {
            var successRoot = NewTempDirectory("settings-success");
            var failureRoot = NewTempDirectory("settings-failure");
            WatchWorkspaceWindow? successWindow = null;
            WatchWorkspaceWindow? failureWindow = null;
            try
            {
                var successClock = new ManualTimerTimeProvider(StartedAt);
                successWindow = CreateWorkspace(successRoot, successClock);
                NavigateToSettings(successWindow);
                Click(successWindow, "SaveRefreshIntervalsButton");

                var successItems = NotificationItems(successWindow);
                Assert.Single(successItems.Items);
                Assert.Equal("本机设置已保存", Titles(successWindow, successItems).Single());
                Assert.Equal(
                    Visibility.Collapsed,
                    TemplateElement<ButtonBase>(
                        successWindow,
                        successItems,
                        0,
                        "NotificationActionButton").Visibility);
                Assert.Null(successWindow.FindName("SettingsInfoBar"));

                successClock.Advance(TimeSpan.FromSeconds(3));
                Assert.Empty(successItems.Items);
                successWindow.Close();
                successWindow = null;

                var failureClock = new ManualTimerTimeProvider(StartedAt);
                failureWindow = CreateWorkspace(
                    failureRoot,
                    failureClock,
                    workspacePreferencesPath: failureRoot);
                NavigateToSettings(failureWindow);
                Click(failureWindow, "SaveRefreshIntervalsButton");

                var failureItems = NotificationItems(failureWindow);
                Assert.Single(failureItems.Items);
                Assert.Equal("无法保存本机设置", Titles(failureWindow, failureItems).Single());
                Assert.Equal(
                    "本机设置文件无法写入。请检查文件权限后重试。",
                    TemplateElement<TextBlock>(
                        failureWindow,
                        failureItems,
                        0,
                        "NotificationMessageText").Text);
                var retry = TemplateElement<ButtonBase>(
                    failureWindow,
                    failureItems,
                    0,
                    "NotificationActionButton");
                Assert.Equal(Visibility.Visible, retry.Visibility);
                Assert.Equal("重试保存", retry.Content);
                Assert.NotNull(retry.Command);
                retry.Command.Execute(retry.CommandParameter);

                Assert.Single(failureItems.Items);
                Assert.Equal(
                    "已合并 2 次",
                    TemplateElement<TextBlock>(
                        failureWindow,
                        failureItems,
                        0,
                        "NotificationOccurrenceText").Text);
                Assert.Equal(
                    "8 秒",
                    TemplateElement<TextBlock>(
                        failureWindow,
                        failureItems,
                        0,
                        "NotificationTimerText").Text);
            }
            finally
            {
                successWindow?.Close();
                failureWindow?.Close();
                DeleteDirectory(successRoot);
                DeleteDirectory(failureRoot);
            }
        });

    [Fact]
    public void Overlay_preserves_page_measure_focus_scope_and_independent_dismissal() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("overlay");
            WatchWorkspaceWindow? window = null;
            try
            {
                var clock = new ManualTimerTimeProvider(StartedAt);
                window = CreateWorkspace(root, clock);
                NavigateToSettings(window);
                window.UpdateLayout();

                var workspace = Assert.IsType<Grid>(window.FindName("WorkspaceContent"));
                var measuredBefore = new Size(workspace.ActualWidth, workspace.ActualHeight);
                var focusedInput = Assert.IsAssignableFrom<Control>(
                    window.FindName("RequestTimeoutInput"));
                Assert.Same(focusedInput, Keyboard.Focus(focusedInput));

                window.PresentNotification(Notification(
                    "page-settings",
                    WatchNotificationSeverity.Success,
                    "页面设置已保存",
                    scope: WatchNotificationScope.ForPage(WatchWorkspacePage.Settings)));
                window.PresentNotification(Notification(
                    "host-global",
                    WatchNotificationSeverity.Error,
                    "Host 契约不兼容",
                    scope: WatchNotificationScope.Global));
                window.UpdateLayout();

                var overlay = NotificationOverlay(window);
                var dialogHost = Assert.IsType<ContentDialogHost>(
                    window.FindName("WorkspaceDialogHost"));
                Assert.Equal(1, Grid.GetRow(overlay));
                Assert.Equal(20, Panel.GetZIndex(overlay));
                Assert.Equal(380, overlay.Width);
                Assert.Equal(HorizontalAlignment.Right, overlay.HorizontalAlignment);
                Assert.Equal(new Thickness(0, 44, 24, 0), overlay.Margin);
                Assert.Equal(40, Panel.GetZIndex(dialogHost));
                Assert.True(dialogHost.IsDisableSiblingsEnabled);
                Assert.Equal(measuredBefore, new Size(workspace.ActualWidth, workspace.ActualHeight));
                Assert.Same(focusedInput, Keyboard.FocusedElement);

                var items = NotificationItems(window);
                Assert.Equal(2, items.Items.Count);
                var card = TemplateElement<Border>(window, items, 0, "NotificationCard");
                var cardGrid = Assert.IsType<Grid>(card.Child);
                Assert.Equal(
                    new[] { 4d, 44d, 1d, 40d },
                    cardGrid.ColumnDefinitions.Select(column => column.Width.Value));
                var close = TemplateElement<ButtonBase>(
                    window,
                    items,
                    0,
                    "NotificationDismissButton");
                Assert.Equal(32, close.Width);
                Assert.Equal(32, close.Height);
                Assert.Equal("关闭通知", AutomationProperties.GetName(close));
                Assert.NotNull(close.Command);
                close.Command.Execute(close.CommandParameter);
                Assert.Single(items.Items);
                Assert.Equal("页面设置已保存", Titles(window, items).Single());

                window.PresentNotification(Notification(
                    "host-global-active",
                    WatchNotificationSeverity.Error,
                    "Host 连接仍失败",
                    scope: WatchNotificationScope.Global));

                Click(window, "OverviewNavigationItem");
                Assert.Single(items.Items);
                Assert.Equal("Host 连接仍失败", Titles(window, items).Single());
                Click(window, "SettingsNavigationItem");
                Assert.Single(items.Items);
                Assert.Equal("Host 连接仍失败", Titles(window, items).Single());
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Hover_and_keyboard_focus_pause_all_deadlines_then_resume_remaining_time() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("focus-pause");
            WatchWorkspaceWindow? window = null;
            try
            {
                var clock = new ManualTimerTimeProvider(StartedAt);
                window = CreateWorkspace(root, clock);
                NavigateToSettings(window);
                window.PresentNotification(Notification(
                    "focus-pause",
                    WatchNotificationSeverity.Success,
                    "等待键盘处理",
                    scope: WatchNotificationScope.ForPage(WatchWorkspacePage.Settings)));

                var items = NotificationItems(window);
                var overlay = NotificationOverlay(window);
                overlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                {
                    RoutedEvent = Mouse.MouseEnterEvent,
                });
                clock.Advance(TimeSpan.FromSeconds(4));
                Assert.Single(items.Items);
                Assert.Equal(
                    "3 秒",
                    TemplateElement<TextBlock>(window, items, 0, "NotificationTimerText").Text);

                overlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                {
                    RoutedEvent = Mouse.MouseLeaveEvent,
                });
                clock.Advance(TimeSpan.FromSeconds(1));
                Assert.Equal(
                    "2 秒",
                    TemplateElement<TextBlock>(window, items, 0, "NotificationTimerText").Text);

                var close = TemplateElement<ButtonBase>(
                    window,
                    items,
                    0,
                    "NotificationDismissButton");
                Assert.Same(close, Keyboard.Focus(close));
                Assert.True(NotificationOverlay(window).IsKeyboardFocusWithin);

                clock.Advance(TimeSpan.FromSeconds(4));
                Assert.Single(items.Items);
                Assert.Equal(
                    "2 秒",
                    TemplateElement<TextBlock>(window, items, 0, "NotificationTimerText").Text);

                var settingsInput = Assert.IsAssignableFrom<Control>(
                    window.FindName("RequestTimeoutInput"));
                Assert.Same(settingsInput, Keyboard.Focus(settingsInput));
                Dispatcher.CurrentDispatcher.Invoke(
                    () => { },
                    DispatcherPriority.ApplicationIdle);
                clock.Advance(TimeSpan.FromSeconds(2));
                Assert.Empty(items.Items);
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Host_field_validation_stays_in_the_settings_surface_without_a_toast() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("settings-validation");
            WatchWorkspaceWindow? window = null;
            try
            {
                window = CreateWorkspace(root, new ManualTimerTimeProvider(StartedAt));
                NavigateToSettings(window);
                var timeout = Assert.IsAssignableFrom<System.Windows.Controls.TextBox>(
                    window.FindName("RequestTimeoutInput"));
                timeout.Text = "0";
                Click(window, "ApplyHostButton");

                Assert.Empty(NotificationItems(window).Items);
                var stableState = Assert.IsAssignableFrom<System.Windows.Controls.TextBlock>(
                    window.FindName("SettingsHostStateText"));
                Assert.Contains("无法应用 Host 设置", stableState.Text, StringComparison.Ordinal);
                Assert.Contains("1–300", stableState.Text, StringComparison.Ordinal);
                Assert.Contains(
                    "1–300",
                    AutomationProperties.GetHelpText(timeout),
                    StringComparison.Ordinal);
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Notification_body_keeps_primary_text_contrast_and_high_contrast_semantics() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("themes");
            WatchWorkspaceWindow? window = null;
            Wpf.Ui.Markup.ThemesDictionary? activeTheme = null;
            try
            {
                window = CreateWorkspace(root, new ManualTimerTimeProvider(StartedAt));
                window.PresentNotification(Notification(
                    "themes",
                    WatchNotificationSeverity.Warning,
                    "主题语义",
                    "正文必须在所有主题中保持可读。"));
                var items = NotificationItems(window);

                foreach (var theme in new[]
                {
                    Wpf.Ui.Appearance.ApplicationTheme.Light,
                    Wpf.Ui.Appearance.ApplicationTheme.Dark,
                })
                {
                    if (activeTheme is not null)
                    {
                        window.Resources.MergedDictionaries.Remove(activeTheme);
                    }

                    activeTheme = new Wpf.Ui.Markup.ThemesDictionary { Theme = theme };
                    window.Resources.MergedDictionaries.Add(activeTheme);
                    window.ApplyWatchThemeResources(theme);
                    window.UpdateLayout();
                    var card = TemplateElement<Border>(window, items, 0, "NotificationCard");
                    var message = TemplateElement<TextBlock>(
                        window, items, 0, "NotificationMessageText");
                    var windowBackground = Assert.IsType<SolidColorBrush>(window.Background).Color;
                    var cardBrush = Assert.IsType<SolidColorBrush>(card.Background);
                    var textBrush = Assert.IsType<SolidColorBrush>(message.Foreground);
                    var effectiveCard = Composite(cardBrush.Color, windowBackground);
                    var effectiveText = Composite(textBrush.Color, effectiveCard);

                    Assert.True(
                        ContrastRatio(effectiveText, effectiveCard) >= 4.5,
                        $"{theme} notification body contrast was below 4.5:1.");
                    Assert.Equal("警告", TemplateElement<TextBlock>(
                        window, items, 0, "NotificationSeverityText").Text);
                    Assert.Equal("警告严重度", AutomationProperties.GetName(
                        TemplateElement<Wpf.Ui.Controls.SymbolIcon>(
                            window, items, 0, "NotificationSeverityIcon")));
                }

                window.ApplyWatchThemeResources(
                    Wpf.Ui.Appearance.ApplicationTheme.HighContrast);
                Assert.NotNull(window.Resources["WatchAccentSoftBrush"]);
                Assert.Equal("警告", TemplateElement<TextBlock>(
                    window, items, 0, "NotificationSeverityText").Text);
                Assert.Equal("警告严重度", AutomationProperties.GetName(
                    TemplateElement<Wpf.Ui.Controls.SymbolIcon>(
                        window, items, 0, "NotificationSeverityIcon")));
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Notification_controls_expose_stable_uia_semantics_and_keyboard_targets() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("uia");
            WatchWorkspaceWindow? window = null;
            try
            {
                window = CreateWorkspace(root, new ManualTimerTimeProvider(StartedAt));
                window.PresentNotification(WatchNotificationEvent.CreateLocalized(
                    new WatchNotificationSource(
                        "test.semantic-event",
                        WatchNotificationScope.ForPage(WatchWorkspacePage.Settings),
                        "uia"),
                    WatchNotificationSeverity.Warning,
                    WatchLocalizedNotificationContent.Create(
                        ("警告", "Warning"),
                        ("AREA 配置需要确认", "AREA profile requires confirmation"),
                        ("磁盘版本已经改变，请选择下一步。", "The disk version changed. Choose the next step."),
                        ("查看详情", "View details")),
                    () => { }));

                var items = NotificationItems(window);
                var card = TemplateElement<Border>(window, items, 0, "NotificationCard");
                var action = TemplateElement<ButtonBase>(
                    window, items, 0, "NotificationActionButton");
                var close = TemplateElement<ButtonBase>(
                    window, items, 0, "NotificationDismissButton");
                var icon = TemplateElement<Wpf.Ui.Controls.SymbolIcon>(
                    window, items, 0, "NotificationSeverityIcon");
                var liveRegion = Assert.IsType<System.Windows.Controls.TextBlock>(
                    window.FindName("NotificationLiveRegion"));

                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(liveRegion));
                Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(card));
                Assert.DoesNotContain("秒", AutomationProperties.GetName(card), StringComparison.Ordinal);
                Assert.Contains("警告", AutomationProperties.GetName(card), StringComparison.Ordinal);
                Assert.Contains("首次出现", AutomationProperties.GetHelpText(card), StringComparison.Ordinal);
                Assert.Equal("警告严重度", AutomationProperties.GetName(icon));
                Assert.True(action.Focusable);
                Assert.NotNull(action.FocusVisualStyle);
                Assert.True(KeyboardNavigation.GetIsTabStop(action));
                Assert.True(action.MinWidth >= 32);
                Assert.True(action.MinHeight >= 32);
                Assert.Equal("查看详情", action.ToolTip);
                Assert.Equal("查看详情", AutomationProperties.GetName(action));
                Assert.Equal("NotificationActionButton", AutomationProperties.GetAutomationId(action));

                Assert.True(close.Focusable);
                Assert.NotNull(close.FocusVisualStyle);
                Assert.True(KeyboardNavigation.GetIsTabStop(close));
                Assert.True(close.Width >= 32);
                Assert.True(close.Height >= 32);
                Assert.Equal("关闭通知", close.ToolTip);
                Assert.Equal("关闭通知", AutomationProperties.GetName(close));
                Assert.Equal("NotificationDismissButton", AutomationProperties.GetAutomationId(close));
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });

    [Theory]
    [InlineData(760)]
    [InlineData(720)]
    public void Narrow_window_uses_top_single_column_with_navigation_safe_margins(double width) =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory($"narrow-{width:0}");
            WatchWorkspaceWindow? window = null;
            try
            {
                window = CreateWorkspace(root, new ManualTimerTimeProvider(StartedAt));
                window.Width = width;
                window.Height = 820;
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var overlay = NotificationOverlay(window);
                Assert.True(double.IsNaN(overlay.Width));
                Assert.Equal(HorizontalAlignment.Stretch, overlay.HorizontalAlignment);
                Assert.Equal(new Thickness(60, 44, 12, 0), overlay.Margin);
                Assert.Equal(20, Panel.GetZIndex(overlay));
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Notification_motion_respects_system_reduced_motion(
        bool reducedMotion,
        bool expectsTranslation) =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory($"motion-{reducedMotion}");
            WatchWorkspaceWindow? window = null;
            try
            {
                window = CreateWorkspace(
                    root,
                    new ManualTimerTimeProvider(StartedAt),
                    reducedMotion: reducedMotion);
                window.PresentNotification(Notification(
                    "motion",
                    WatchNotificationSeverity.Information,
                    "动态通知"));

                if (expectsTranslation)
                {
                    var transform = Assert.IsType<TranslateTransform>(
                        NotificationOverlay(window).RenderTransform);
                    Assert.True(transform.HasAnimatedProperties);
                }
                else
                {
                    Assert.Same(Transform.Identity, NotificationOverlay(window).RenderTransform);
                }
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Closing_window_cancels_notification_updates_and_action_callbacks() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory("dispose");
            WatchWorkspaceWindow? window = null;
            try
            {
                var invoked = 0;
                var clock = new ManualTimerTimeProvider(StartedAt);
                window = CreateWorkspace(root, clock);
                window.PresentNotification(WatchNotificationEvent.CreateLocalized(
                    new WatchNotificationSource(
                        "test.semantic-event",
                        WatchNotificationScope.Global,
                        "dispose"),
                    WatchNotificationSeverity.Error,
                    WatchLocalizedNotificationContent.Create(
                        ("错误", "Error"),
                        ("窗口即将关闭", "Window is closing"),
                        ("关闭后不得继续回调。", "Callbacks must stop after closing."),
                        ("执行动作", "Run action")),
                    () => invoked++));
                var command = TemplateElement<ButtonBase>(
                    window,
                    NotificationItems(window),
                    0,
                    "NotificationActionButton").Command;
                Assert.NotNull(command);

                window.Close();
                window = null;
                clock.Advance(TimeSpan.FromSeconds(30));
                command!.Execute(null);

                Assert.Equal(0, invoked);
            }
            finally
            {
                window?.Close();
                DeleteDirectory(root);
            }
        });

    private static WatchWorkspaceWindow CreateWorkspace(
        string root,
        TimeProvider clock,
        string? workspacePreferencesPath = null,
        bool reducedMotion = true)
    {
        var window = new WatchWorkspaceWindow(
            new WatchHostSettings("http://host-a", "secret", 30),
            WatchV2Preferences.Default,
            Path.Combine(root, "connection.json"),
            workspacePreferencesPath ?? Path.Combine(root, "workspace.json"),
            timeProvider: clock,
            initializeOnLoaded: false,
            areaFilterProfilesDirectoryPath: Path.Combine(root, "area-filters"),
            notificationReducedMotionProvider: () => reducedMotion);
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static WatchNotificationEvent Notification(
        string identity,
        WatchNotificationSeverity severity,
        string title,
        string message = "受控通知说明",
        WatchNotificationScope? scope = null)
    {
        var severityText = severity switch
        {
            WatchNotificationSeverity.Success => new WatchLocalizedText("成功", "Success"),
            WatchNotificationSeverity.Warning => new WatchLocalizedText("警告", "Warning"),
            WatchNotificationSeverity.Error => new WatchLocalizedText("错误", "Error"),
            _ => new WatchLocalizedText("信息", "Information"),
        };
        return WatchNotificationEvent.CreateLocalized(
            new WatchNotificationSource(
                "test.semantic-event",
                scope ?? WatchNotificationScope.ForPage(WatchWorkspacePage.Overview),
                identity),
            severity,
            WatchLocalizedNotificationContent.Create(
                severityText,
                new WatchLocalizedText(title, title),
                new WatchLocalizedText(message, message)));
    }

    private static Grid NotificationOverlay(WatchWorkspaceWindow window) =>
        Assert.IsType<Grid>(window.FindName("NotificationOverlay"));

    private static ItemsControl NotificationItems(WatchWorkspaceWindow window) =>
        Assert.IsType<ItemsControl>(window.FindName("NotificationItemsControl"));

    private static IReadOnlyList<string> Titles(
        WatchWorkspaceWindow window,
        ItemsControl items)
    {
        window.UpdateLayout();
        return Enumerable.Range(0, items.Items.Count)
            .Select(index => TemplateElement<TextBlock>(
                window,
                items,
                index,
                "NotificationTitleText").Text)
            .ToArray();
    }

    private static T TemplateElement<T>(
        WatchWorkspaceWindow window,
        ItemsControl items,
        int index,
        string name)
        where T : DependencyObject
    {
        window.UpdateLayout();
        var presenter = Assert.IsType<ContentPresenter>(
            items.ItemContainerGenerator.ContainerFromIndex(index));
        presenter.ApplyTemplate();
        var template = Assert.IsType<DataTemplate>(presenter.ContentTemplate);
        return Assert.IsAssignableFrom<T>(template.FindName(name, presenter));
    }

    private static void NavigateToSettings(WatchWorkspaceWindow window) =>
        Click(window, "SettingsNavigationItem");

    private static void Click(WatchWorkspaceWindow window, string name) =>
        Assert.IsAssignableFrom<ButtonBase>(window.FindName(name))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static string NewTempDirectory(string scenario)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"watch-notification-{scenario}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        return Color.FromRgb(
            (byte)Math.Round((foreground.R * alpha) + (background.R * (1 - alpha))),
            (byte)Math.Round((foreground.G * alpha) + (background.G * (1 - alpha))),
            (byte)Math.Round((foreground.B * alpha) + (background.B * (1 - alpha))));
    }

    private static double ContrastRatio(Color first, Color second)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.04045
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) =>
            (0.2126 * Channel(color.R))
            + (0.7152 * Channel(color.G))
            + (0.0722 * Channel(color.B));

        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }
}
