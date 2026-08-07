using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

public sealed class WatchCompositionRootTests
{
    [Fact]
    public void Overview_cards_show_business_summaries_and_navigate_to_default_pages()
    {
        RunInSta(() =>
        {
            var health = new WatchPollHealthDto(
                DateTimeOffset.Parse("2026-08-08T09:29:59+08:00"),
                DateTimeOffset.Parse("2026-08-08T09:30:00+08:00"),
                1000,
                27,
                true,
                "SUCCESS",
                []);
            var snapshot = new WatchSnapshot(
                [Demand("d-1", "DIE_TO_OVEN"), Demand("d-2", "DIE_TO_OVEN")],
                [Alert("a-1", "WARNING")],
                health,
                FetchError: null,
                DemandsHasMore: false,
                AlertsHasMore: true,
                DemandsSucceeded: true,
                AlertsSucceeded: true,
                PollHealthSucceeded: true);
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("overview-session")
            {
                PollHealth = FakeHostReply.Return<WatchPollHealthDto?>(health),
                Snapshot = FakeHostReply.Return(snapshot),
            });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-overview-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            PumpUntil(() => ((TextBlock)window.FindName("OverviewConclusionText"))?.Text == "△ 仅有 WARNING");

            Assert.Contains("最近 MES 快照行数=27", ((TextBlock)window.FindName("OverviewPollHealthText")).Text);
            Assert.Contains("活动告警：100+", ((TextBlock)window.FindName("OverviewAlertText")).Text);
            Assert.Contains("当前页：DIE_TO_OVEN 2", ((TextBlock)window.FindName("OverviewDemandText")).Text);

            var navigation = (ListBox)window.FindName("PrimaryNavigation");
            ((Button)window.FindName("OverviewAlertsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, navigation.SelectedIndex);
            navigation.SelectedIndex = 0;
            ((Button)window.FindName("OverviewDemandsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, navigation.SelectedIndex);

            window.Close();
        });
    }

    [Fact]
    public void Formal_composition_root_starts_the_window_through_the_host_session()
    {
        RunInSta(() =>
        {
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("fake-ui-session"));
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-ui-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            PumpUntil(() => fakeHost.Timeline.Any(entry =>
                entry.Operation == FakeHostOperation.PollHealth
                && entry.State == FakeHostRequestState.Completed));

            var navigation = (ListBox)window.FindName("PrimaryNavigation");
            Assert.Equal(0, navigation.SelectedIndex);
            Assert.Contains(fakeHost.Timeline, entry => entry.Operation == FakeHostOperation.Contract);
            Assert.Contains(fakeHost.Timeline, entry => entry.Operation == FakeHostOperation.PollHealth);

            window.Close();
        });
    }

    private static WatchOptions FakeOptions() => new()
    {
        BaseUrl = "http://fake-watch.test",
        SharedSecret = "fake-secret-never-log",
        RequestTimeoutSeconds = 30,
        RefreshSeconds = 300,
    };

    private static WatchDemandDto Demand(string id, string taskType) => new(
        id,
        taskType,
        $"S-{id}",
        "A01-01",
        "EQP-1",
        "STEP-1",
        DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"),
        "PKG",
        "VISIBLE",
        DateTimeOffset.Parse("2026-08-08T09:00:00+08:00"),
        0,
        false,
        null,
        DateTimeOffset.Parse("2026-08-08T08:05:00+08:00"),
        null);

    private static WatchAlertDto Alert(string id, string severity) => new(
        id,
        "REAPPEAR_AFTER_GONE",
        severity,
        "DIE_TO_OVEN",
        "S-1",
        "d-1",
        "fake alert",
        null,
        DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"),
        DateTimeOffset.Parse("2026-08-08T09:00:00+08:00"),
        1,
        true,
        null,
        DateTimeOffset.Parse("2026-08-08T08:00:00+08:00"));

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        Assert.True(condition(), "Timed out while waiting for the formal Watch window to load.");
    }

    private static void RunInSta(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA UI test thread did not finish.");
        Assert.Null(caught);
    }

}
