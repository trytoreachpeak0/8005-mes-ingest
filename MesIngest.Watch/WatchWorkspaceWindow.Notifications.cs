using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Controls;

namespace MesIngest.Watch;

internal partial class WatchWorkspaceWindow
{
    private static readonly TimeSpan NotificationMotionDuration =
        TimeSpan.FromMilliseconds(180);
    private const double NotificationMotionOffset = 10;
    private const double NotificationDesktopWidth = 380;
    private const double NotificationCollisionContentWidth = 900;

    private readonly ObservableCollection<WatchNotificationCardViewModel> _notificationCards = [];
    private readonly Func<bool> _notificationReducedMotionProvider;
    private long _notificationTransitionVersion;

    internal void PresentNotification(WatchNotificationEvent notification) =>
        _notificationCoordinator.Present(notification.LocalizedContent is null
            ? notification with
            {
                LocalizedContent = WatchFeedbackText.Localize(notification),
            }
            : notification);

    private void InitializeNotifications()
    {
        NotificationItemsControl.ItemsSource = _notificationCards;
        _notificationCoordinator.SnapshotChanged += OnNotificationSnapshotChanged;
        SystemParameters.StaticPropertyChanged += OnNotificationSystemParameterChanged;
        ApplyLocalizedNotificationChrome();
        ApplyNotificationSnapshot(
            new WatchNotificationSnapshotChangedEventArgs(
                _notificationCoordinator.GetSnapshot(),
                announcement: null));
    }

    private void ApplyLocalizedNotificationChrome()
    {
        var feedback = _displayLanguageState.Catalog.Feedback;
        AutomationProperties.SetName(NotificationOverlay, feedback.OverlayName);
        AutomationProperties.SetName(NotificationLiveRegion, feedback.LiveRegionName);
        AutomationProperties.SetName(NotificationItemsControl, feedback.CardsName);
    }

    private void OnNotificationSnapshotChanged(
        object? sender,
        WatchNotificationSnapshotChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    ApplyLocalizedNotificationChrome();
                    ApplyLocalizedActiveWorkspaceDialog();
                    ApplyNotificationSnapshot(e);
                }
            });
            return;
        }

        ApplyLocalizedNotificationChrome();
        ApplyLocalizedActiveWorkspaceDialog();
        ApplyNotificationSnapshot(e);
    }

    private void ApplyNotificationSnapshot(WatchNotificationSnapshotChangedEventArgs change)
    {
        var desiredKeys = change.Items.Select(item => item.SourceKey).ToHashSet(StringComparer.Ordinal);
        for (var index = _notificationCards.Count - 1; index >= 0; index--)
        {
            if (!desiredKeys.Contains(_notificationCards[index].SourceKey))
            {
                _notificationCards.RemoveAt(index);
            }
        }

        for (var index = 0; index < change.Items.Count; index++)
        {
            var snapshot = change.Items[index];
            var existingIndex = IndexOfNotification(snapshot.SourceKey);
            WatchNotificationCardViewModel card;
            if (existingIndex < 0)
            {
                card = new WatchNotificationCardViewModel(
                    snapshot,
                    _notificationCoordinator.Dismiss,
                    _notificationCoordinator.ExecuteAction);
                _notificationCards.Insert(index, card);
            }
            else
            {
                card = _notificationCards[existingIndex];
                card.Update(snapshot);
                if (existingIndex != index)
                {
                    _notificationCards.Move(existingIndex, index);
                }
            }
        }

        ApplyNotificationMotion(change.ShouldAnimate);

        if (change.Announcement is not null)
        {
            NotificationLiveRegion.Text = change.Announcement;
            AutomationProperties.SetName(NotificationLiveRegion, change.Announcement);
            var peer = UIElementAutomationPeer.FromElement(NotificationLiveRegion)
                ?? new FrameworkElementAutomationPeer(NotificationLiveRegion);
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void ApplyNotificationMotion(bool shouldAnimate)
    {
        var transition = ++_notificationTransitionVersion;
        if (_notificationCards.Count == 0)
        {
            if (!shouldAnimate || IsNotificationReducedMotion())
            {
                StopNotificationAnimations();
                NotificationOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            NotificationOverlay.Visibility = Visibility.Visible;
            var exit = new DoubleAnimation(1, 0, NotificationMotionDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            exit.Completed += (_, _) =>
            {
                if (_disposed
                    || transition != _notificationTransitionVersion
                    || _notificationCards.Count != 0)
                {
                    return;
                }

                NotificationOverlay.Visibility = Visibility.Collapsed;
                StopNotificationAnimations();
            };
            NotificationOverlay.BeginAnimation(
                OpacityProperty,
                exit,
                HandoffBehavior.SnapshotAndReplace);
            return;
        }

        NotificationOverlay.Visibility = Visibility.Visible;
        if (!shouldAnimate)
        {
            return;
        }

        NotificationOverlay.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.35, 1, NotificationMotionDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            },
            HandoffBehavior.SnapshotAndReplace);

        if (IsNotificationReducedMotion())
        {
            ClearNotificationTranslation();
            return;
        }

        var transform = NotificationOverlay.RenderTransform as TranslateTransform
            ?? new TranslateTransform();
        NotificationOverlay.RenderTransform = transform;
        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-NotificationMotionOffset, 0, NotificationMotionDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void OnNotificationSystemParameterChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (_disposed
            || (!string.IsNullOrEmpty(e.PropertyName)
                && !string.Equals(
                    e.PropertyName,
                    nameof(SystemParameters.ClientAreaAnimation),
                    StringComparison.Ordinal)))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_disposed && IsNotificationReducedMotion())
                {
                    ClearNotificationTranslation();
                }
            });
            return;
        }

        if (IsNotificationReducedMotion())
        {
            ClearNotificationTranslation();
        }
    }

    private bool IsNotificationReducedMotion() => _notificationReducedMotionProvider();

    private void ClearNotificationTranslation()
    {
        if (NotificationOverlay.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
        }

        NotificationOverlay.RenderTransform = Transform.Identity;
    }

    private void StopNotificationAnimations()
    {
        NotificationOverlay.BeginAnimation(OpacityProperty, null);
        NotificationOverlay.Opacity = 1;
        ClearNotificationTranslation();
    }

    private void ReflowNotificationOverlay(double contentWidth)
    {
        var isNarrow = contentWidth < NotificationCollisionContentWidth;
        NotificationOverlay.Width = isNarrow ? double.NaN : NotificationDesktopWidth;
        NotificationOverlay.HorizontalAlignment = isNarrow
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
        NotificationOverlay.Margin = isNarrow
            ? new Thickness(60, 44, 12, 0)
            : new Thickness(0, 44, 24, 0);
    }

    private int IndexOfNotification(string sourceKey)
    {
        for (var index = 0; index < _notificationCards.Count; index++)
        {
            if (string.Equals(
                    _notificationCards[index].SourceKey,
                    sourceKey,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private void OnNotificationHostMouseEnter(object sender, MouseEventArgs e) =>
        _notificationCoordinator.SetPaused(paused: true);

    private void OnNotificationHostMouseLeave(object sender, MouseEventArgs e) =>
        _notificationCoordinator.SetPaused(NotificationOverlay.IsKeyboardFocusWithin);

    private void OnNotificationHostGotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e) =>
        _notificationCoordinator.SetPaused(paused: true);

    private void OnNotificationHostLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e) =>
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                _notificationCoordinator.SetPaused(
                    NotificationOverlay.IsMouseOver || NotificationOverlay.IsKeyboardFocusWithin);
            }
        });

    private void DisposeNotifications()
    {
        SystemParameters.StaticPropertyChanged -= OnNotificationSystemParameterChanged;
        _notificationCoordinator.SnapshotChanged -= OnNotificationSnapshotChanged;
        _notificationCoordinator.Dispose();
        StopNotificationAnimations();
        _notificationCards.Clear();
        NotificationLiveRegion.Text = string.Empty;
        AutomationProperties.SetName(NotificationLiveRegion, string.Empty);
        NotificationOverlay.Visibility = Visibility.Collapsed;
    }

    private sealed class WatchNotificationCardViewModel : INotifyPropertyChanged
    {
        private WatchNotificationSeverity _severity;
        private string _severityText = string.Empty;
        private string _title = string.Empty;
        private string _message = string.Empty;
        private string? _actionLabel;
        private string _occurrenceText = string.Empty;
        private string _timerText = string.Empty;
        private string _automationName = string.Empty;
        private string _dismissText = string.Empty;
        private string _severityAutomationName = string.Empty;

        public WatchNotificationCardViewModel(
            WatchNotificationSnapshot snapshot,
            Action<string> dismiss,
            Action<string> executeAction)
        {
            SourceKey = snapshot.SourceKey;
            DismissCommand = new WatchNotificationCommand(() => dismiss(SourceKey));
            ActionCommand = new WatchNotificationCommand(() => executeAction(SourceKey));
            Update(snapshot);
        }

        public string SourceKey { get; }
        public WatchNotificationSeverity Severity { get => _severity; private set => Set(ref _severity, value); }
        public string SeverityText { get => _severityText; private set => Set(ref _severityText, value); }
        public string Title { get => _title; private set => Set(ref _title, value); }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public string? ActionLabel { get => _actionLabel; private set { if (Set(ref _actionLabel, value)) OnPropertyChanged(nameof(ActionVisibility)); } }
        public string OccurrenceText { get => _occurrenceText; private set => Set(ref _occurrenceText, value); }
        public string TimerText { get => _timerText; private set => Set(ref _timerText, value); }
        public string AutomationName { get => _automationName; private set => Set(ref _automationName, value); }
        public string AutomationHelpText => $"{Message}。{OccurrenceText}";
        public string DismissText { get => _dismissText; private set => Set(ref _dismissText, value); }
        public string SeverityAutomationName { get => _severityAutomationName; private set => Set(ref _severityAutomationName, value); }
        public Visibility ActionVisibility => ActionLabel is null ? Visibility.Collapsed : Visibility.Visible;
        public ICommand ActionCommand { get; }
        public ICommand DismissCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(WatchNotificationSnapshot snapshot)
        {
            Severity = snapshot.Severity;
            SeverityText = snapshot.SeverityText;
            Title = snapshot.Title;
            Message = snapshot.Message;
            ActionLabel = snapshot.ActionLabel;
            OccurrenceText = snapshot.OccurrenceText;
            TimerText = snapshot.TimerText;
            AutomationName = snapshot.AutomationName;
            DismissText = snapshot.DismissText;
            SeverityAutomationName = snapshot.SeverityAutomationName;
            OnPropertyChanged(nameof(AutomationHelpText));
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class WatchNotificationCommand(Action execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
