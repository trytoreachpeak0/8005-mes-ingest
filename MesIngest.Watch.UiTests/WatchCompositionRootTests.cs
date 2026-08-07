using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

public sealed class WatchCompositionRootTests
{
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
