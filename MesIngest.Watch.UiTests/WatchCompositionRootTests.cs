using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MesIngest.Watch;

namespace MesIngest.Watch.UiTests;

public sealed class WatchCompositionRootTests
{
    private static readonly Lazy<StaDispatcherHost> StaHost = new(() => new StaDispatcherHost());

    [Fact]
    public void Enabled_visible_auto_refresh_runs_immediately_when_returning_and_persists()
    {
        RunInSta(() =>
        {
            var demandCalls = 0;
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("visible-auto-refresh")
            {
                DemandPage = FakeHostReply.Select<WatchDemandBrowseQuery, WatchDemandPage>(_ =>
                {
                    demandCalls++;
                    return FakeHostReply.Return(new WatchDemandPage(
                        [Demand($"d-{demandCalls}", "WIRE_TO_GATE")],
                        null,
                        HasMore: false));
                }),
            });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-auto-{Guid.NewGuid():N}");
            var preferencesPath = Path.Combine(testRoot, "auto-refresh.json");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"),
                autoRefreshPreferencesPath: preferencesPath);
            var window = composition.CreateMainWindow();

            window.Show();
            var navigation = (ListBox)window.FindName("PrimaryNavigation");
            navigation.SelectedIndex = 1;
            PumpUntil(() => demandCalls >= 2);

            var interval = (ComboBox)window.FindName("DemandAutoRefreshInterval");
            var enabled = (ToggleButton)window.FindName("DemandAutoRefreshCheckBox");
            interval.SelectedItem = 30;
            enabled.IsChecked = true;

            navigation.SelectedIndex = 2;
            navigation.SelectedIndex = 1;
            PumpUntil(() => demandCalls >= 3);

            var saved = WatchAutoRefreshPreferencesStore.Load(preferencesPath);
            Assert.Equal(new WatchAutoRefreshSetting(true, 30), saved.Visible);
            Assert.False(saved.Gone.Enabled);
            window.Close();
        });
    }

    [Fact]
    public void Auto_refresh_timer_starts_after_recovering_from_initial_host_failure()
    {
        RunInSta(() =>
        {
            var pollHealthCalls = 0;
            var health = new WatchPollHealthDto(
                DateTimeOffset.Parse("2026-08-08T09:29:59+08:00"),
                DateTimeOffset.Parse("2026-08-08T09:30:00+08:00"),
                1000,
                1,
                true,
                "SUCCESS",
                []);
            var fakeHost = new ScriptedFakeHost(
                new FakeHostScenario("initial-failure")
                {
                    Contract = FakeHostReply.Fail<FakeHostUnit>(
                        WatchHostFailureKind.Network,
                        "/api/contract",
                        "initial fake failure"),
                },
                new FakeHostScenario("recovered-auto-refresh")
                {
                    PollHealth = FakeHostReply.Select<FakeHostUnit, WatchPollHealthDto?>(_ =>
                    {
                        pollHealthCalls++;
                        return FakeHostReply.Return<WatchPollHealthDto?>(health);
                    }),
                });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-auto-recover-{Guid.NewGuid():N}");
            var preferencesPath = Path.Combine(testRoot, "auto-refresh.json");
            WatchAutoRefreshPreferencesStore.Save(
                preferencesPath,
                WatchAutoRefreshPreferences.Default.With(
                    WatchRefreshView.Overview,
                    new WatchAutoRefreshSetting(true, 10)));
            var clock = new ManualTimeProvider(
                DateTimeOffset.Parse("2026-08-08T10:00:00+08:00"));
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"),
                autoRefreshPreferencesPath: preferencesPath,
                timeProvider: clock);
            var window = composition.CreateMainWindow();

            window.Show();
            PumpUntil(() => ((Border)window.FindName("ErrorBanner")).Visibility == Visibility.Visible);
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 3;
            ((Button)window.FindName("ApplyHostButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var cancel = (Button)window.FindName("OverviewCancelButton");
            PumpUntil(() => pollHealthCalls >= 2 && !cancel.IsEnabled);

            clock.SetUtcNow(DateTimeOffset.Parse("2026-08-08T10:00:10+08:00"));
            PumpUntil(() => pollHealthCalls >= 3);

            window.Close();
        });
    }

    [Fact]
    public void Failed_new_host_never_auto_refreshes_or_displays_the_old_host_window()
    {
        RunInSta(() =>
        {
            var oldDemandCalls = 0;
            var oldHost = new FakeHostScenario("old-host")
            {
                DemandPage = FakeHostReply.Select<WatchDemandBrowseQuery, WatchDemandPage>(_ =>
                {
                    oldDemandCalls++;
                    return FakeHostReply.Return(new WatchDemandPage(
                        [Demand("old-demand", "WIRE_TO_GATE")],
                        null,
                        false));
                }),
            };
            var failedNewHost = new FakeHostScenario("failed-new-host")
            {
                Contract = FakeHostReply.Fail<FakeHostUnit>(
                    WatchHostFailureKind.Timeout,
                    "/api/contract",
                    "new Host timed out"),
            };
            var fakeHost = new ScriptedFakeHost(oldHost, failedNewHost);
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-host-isolation-{Guid.NewGuid():N}");
            var preferencesPath = Path.Combine(testRoot, "auto-refresh.json");
            WatchAutoRefreshPreferencesStore.Save(
                preferencesPath,
                WatchAutoRefreshPreferences.Default.With(
                    WatchRefreshView.Overview,
                    new WatchAutoRefreshSetting(true, 10)));
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"),
                autoRefreshPreferencesPath: preferencesPath);
            var window = composition.CreateMainWindow();

            window.Show();
            PumpUntil(() => oldDemandCalls == 1);
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 3;
            ((TextBox)window.FindName("HostBaseUrlInput")).Text = "http://new-host.test";
            ((Button)window.FindName("ApplyHostButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var error = (TextBlock)window.FindName("ErrorBannerText");
            PumpUntil(() => error.Text.Contains("new Host timed out", StringComparison.Ordinal));

            Assert.Equal(1, oldDemandCalls);
            Assert.DoesNotContain(
                "old-demand",
                ((TextBlock)window.FindName("OverviewDemandText")).Text,
                StringComparison.Ordinal);
            Assert.Contains("timeoutSeconds=30", error.Text, StringComparison.Ordinal);
            Assert.Contains("elapsedMs=0", error.Text, StringComparison.Ordinal);
            window.Close();
        });
    }

    [Fact]
    public void Visible_and_gone_tabs_load_once_and_restore_independent_cached_windows()
    {
        RunInSta(() =>
        {
            var requests = new List<WatchDemandBrowseQuery>();
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("independent-demand-tabs")
            {
                DemandPage = FakeHostReply.Select<WatchDemandBrowseQuery, WatchDemandPage>(query =>
                {
                    requests.Add(query);
                    var item = string.Equals(query.Status, "GONE", StringComparison.Ordinal)
                        ? Demand("gone-cached", "DIE_TO_OVEN")
                        : Demand("visible-cached", "WIRE_TO_GATE");
                    return FakeHostReply.Return(new WatchDemandPage([item], null, false));
                }),
            });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-demand-tabs-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 1;
            var grid = (DataGrid)window.FindName("DemandsGrid");
            PumpUntil(() => grid.Items.Count == 1
                && ((WatchDemandDto)grid.Items[0]).DemandId == "visible-cached");
            var tabs = (TabControl)window.FindName("DemandStatusTabs");
            var requestCountBeforeGone = requests.Count;

            tabs.SelectedIndex = 1;

            PumpUntil(() => grid.Items.Count == 1
                && ((WatchDemandDto)grid.Items[0]).DemandId == "gone-cached");
            var goneRequest = requests.Last();
            Assert.Equal("GONE", goneRequest.Status);
            Assert.Equal("goneAt", goneRequest.SortBy);
            Assert.NotNull(goneRequest.GoneAtFrom);
            Assert.InRange(DateTimeOffset.UtcNow - goneRequest.GoneAtFrom.Value,
                TimeSpan.FromHours(23.99), TimeSpan.FromHours(24.01));
            Assert.Equal(requestCountBeforeGone + 1, requests.Count);

            tabs.SelectedIndex = 0;
            PumpUntil(() => ((WatchDemandDto)grid.Items[0]).DemandId == "visible-cached");
            tabs.SelectedIndex = 1;
            PumpUntil(() => ((WatchDemandDto)grid.Items[0]).DemandId == "gone-cached");

            Assert.Equal(requestCountBeforeGone + 1, requests.Count);
            window.Close();
        });
    }

    [Fact]
    public void Demand_refresh_updates_selected_details_then_clears_them_when_the_id_disappears()
    {
        RunInSta(() =>
        {
            var calls = 0;
            var selectedId = "abcdef0123456789abcdef0123456789";
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("demand-detail-refresh")
            {
                DemandPage = FakeHostReply.Select<WatchDemandBrowseQuery, WatchDemandPage>(_ =>
                {
                    calls++;
                    var item = calls switch
                    {
                        <= 2 => Demand(selectedId, "DIE_TO_OVEN") with { Eqp = "EQP-OLD" },
                        3 => Demand(selectedId, "DIE_TO_OVEN") with { Eqp = "EQP-NEW" },
                        _ => Demand("fedcba9876543210fedcba9876543210", "WIRE_TO_GATE"),
                    };
                    return FakeHostReply.Return(new WatchDemandPage([item], null, false));
                }),
            });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-demand-detail-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 1;
            var grid = (DataGrid)window.FindName("DemandsGrid");
            var detailsPanel = (FrameworkElement)window.FindName("DemandDetailsPanel");
            var placeholder = (FrameworkElement)window.FindName("DemandDetailsPlaceholder");
            var notice = (TextBlock)window.FindName("DemandNoticeText");
            var refresh = (Button)window.FindName("DemandRefreshButton");
            PumpUntil(() => grid.Items.Count == 1
                && ((WatchDemandDto)grid.Items[0]).DemandId == selectedId);

            grid.SelectedItem = grid.Items[0];

            PumpUntil(() => detailsPanel.Visibility == Visibility.Visible
                && ((WatchDemandDetails)detailsPanel.DataContext)
                    .MesInputGroup.Fields.Single(field => field.Name == "EQP").Value == "EQP-OLD");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => grid.SelectedItem is WatchDemandDto selected
                && selected.DemandId == selectedId
                && ((WatchDemandDetails)detailsPanel.DataContext)
                    .MesInputGroup.Fields.Single(field => field.Name == "EQP").Value == "EQP-NEW");

            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            PumpUntil(() => grid.SelectedItem is null
                && detailsPanel.Visibility == Visibility.Collapsed
                && placeholder.Visibility == Visibility.Visible
                && notice.Text == "所选 TransportDemand 已不在当前页");
            window.Close();
        });
    }

    [Fact]
    public void Leaving_gone_cancels_its_refresh_and_a_late_response_cannot_mix_visible_data()
    {
        RunInSta(() =>
        {
            var gate = new FakeHostGate();
            var goneCalls = 0;
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("gone-late-after-tab-switch")
            {
                DemandPage = FakeHostReply.Select<WatchDemandBrowseQuery, WatchDemandPage>(query =>
                {
                    if (!string.Equals(query.Status, "GONE", StringComparison.Ordinal))
                    {
                        return FakeHostReply.Return(new WatchDemandPage(
                            [Demand("visible-stable", "WIRE_TO_GATE")], null, false));
                    }

                    goneCalls++;
                    return goneCalls == 1
                        ? FakeHostReply.Return(new WatchDemandPage(
                            [Demand("gone-stable", "DIE_TO_OVEN")], null, false))
                        : FakeHostReply.After(
                            gate,
                            new WatchDemandPage([Demand("gone-late", "DIE_TO_OVEN")], null, false),
                            completeAfterCancellation: true);
                }),
            });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-gone-late-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 1;
            var grid = (DataGrid)window.FindName("DemandsGrid");
            PumpUntil(() => grid.Items.Count == 1
                && ((WatchDemandDto)grid.Items[0]).DemandId == "visible-stable");
            var tabs = (TabControl)window.FindName("DemandStatusTabs");
            tabs.SelectedIndex = 1;
            PumpUntil(() => ((WatchDemandDto)grid.Items[0]).DemandId == "gone-stable");

            ((Button)window.FindName("DemandRefreshButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => goneCalls == 2
                && ((Button)window.FindName("DemandCancelButton")).IsEnabled);
            tabs.SelectedIndex = 0;
            PumpUntil(() => ((WatchDemandDto)grid.Items[0]).DemandId == "visible-stable");
            gate.Release();
            PumpUntil(() => fakeHost.Timeline.Any(entry =>
                entry.Operation == FakeHostOperation.DemandPage
                && entry.State == FakeHostRequestState.CompletedAfterCancellation));

            Assert.Equal("visible-stable", ((WatchDemandDto)grid.Items[0]).DemandId);
            tabs.SelectedIndex = 1;
            PumpUntil(() => ((WatchDemandDto)grid.Items[0]).DemandId == "gone-stable");
            Assert.DoesNotContain(
                grid.Items.Cast<WatchDemandDto>(),
                demand => demand.DemandId == "gone-late");
            window.Close();
        });
    }

    [Fact]
    public void Gone_failed_page_navigation_and_primary_navigation_cancel_preserve_the_last_successful_window()
    {
        RunInSta(() =>
        {
            var gate = new FakeHostGate();
            var gonePageOneCalls = 0;
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("gone-failure-and-navigation")
            {
                DemandPage = FakeHostReply.Select<WatchDemandBrowseQuery, WatchDemandPage>(query =>
                {
                    if (!string.Equals(query.Status, "GONE", StringComparison.Ordinal))
                    {
                        return FakeHostReply.Return(new WatchDemandPage(
                            [Demand("visible-stable", "WIRE_TO_GATE")], null, false));
                    }

                    if (query.Cursor == "gone-next")
                    {
                        return FakeHostReply.Fail<WatchDemandPage>(
                            WatchHostFailureKind.Http,
                            "/api/demands",
                            "fake gone page failure");
                    }

                    gonePageOneCalls++;
                    return gonePageOneCalls switch
                    {
                        1 => FakeHostReply.Return(new WatchDemandPage(
                            [Demand("gone-committed", "DIE_TO_OVEN")], "gone-next", true)),
                        2 => FakeHostReply.Return(new WatchDemandPage(
                            [Demand("gone-refreshed", "DIE_TO_OVEN")], "gone-next-2", true)),
                        _ => FakeHostReply.After(
                            gate,
                            new WatchDemandPage([Demand("gone-late", "DIE_TO_OVEN")], null, false),
                            completeAfterCancellation: true),
                    };
                }),
            });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-gone-failure-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            var navigation = (ListBox)window.FindName("PrimaryNavigation");
            navigation.SelectedIndex = 1;
            var grid = (DataGrid)window.FindName("DemandsGrid");
            ((TabControl)window.FindName("DemandStatusTabs")).SelectedIndex = 1;
            PumpUntil(() => grid.Items.Count == 1
                && ((WatchDemandDto)grid.Items[0]).DemandId == "gone-committed");

            ((Button)window.FindName("DemandNextButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => ((Border)window.FindName("ErrorBanner")).Visibility == Visibility.Visible);
            Assert.Equal("第 1 页", ((TextBlock)window.FindName("DemandPageText")).Text);
            Assert.Equal("gone-committed", ((WatchDemandDto)grid.Items[0]).DemandId);
            var failureText = ((TextBlock)window.FindName("ErrorBannerText")).Text;
            Assert.Contains("endpoint=/api/demands", failureText, StringComparison.Ordinal);
            Assert.Contains("timeoutSeconds=30", failureText, StringComparison.Ordinal);
            Assert.Contains("elapsedMs=", failureText, StringComparison.Ordinal);
            Assert.Contains("correlationId=", failureText, StringComparison.Ordinal);
            Assert.Contains("lastSuccess=", failureText, StringComparison.Ordinal);
            Assert.Contains("stale=", failureText, StringComparison.Ordinal);

            ((Button)window.FindName("DemandRefreshButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => ((WatchDemandDto)grid.Items[0]).DemandId == "gone-refreshed");

            ((Button)window.FindName("DemandRefreshButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => gonePageOneCalls == 3
                && ((Button)window.FindName("DemandCancelButton")).IsEnabled);
            navigation.SelectedIndex = 0;
            gate.Release();
            PumpUntil(() => fakeHost.Timeline.Any(entry =>
                entry.Operation == FakeHostOperation.DemandPage
                && entry.State == FakeHostRequestState.CompletedAfterCancellation));
            navigation.SelectedIndex = 1;
            PumpUntil(() => ((WatchDemandDto)grid.Items[0]).DemandId == "gone-refreshed");

            Assert.Equal("第 1 页", ((TextBlock)window.FindName("DemandPageText")).Text);
            Assert.DoesNotContain(
                grid.Items.Cast<WatchDemandDto>(),
                demand => demand.DemandId == "gone-late");
            window.Close();
        });
    }

    [Fact]
    public void Visible_demand_page_submits_filters_and_navigates_cursor_pages()
    {
        RunInSta(() =>
        {
            var requests = new List<WatchDemandBrowseQuery>();
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("visible-browse")
            {
                DemandPage = FakeHostReply.Select<WatchDemandBrowseQuery, WatchDemandPage>(query =>
                {
                    requests.Add(query);
                    if (query.Cursor == "filtered-next")
                    {
                        return FakeHostReply.Return(new WatchDemandPage(
                            [Demand("bbbbbb0123456789abcdef0123456789", "DIE_TO_OVEN")],
                            null,
                            HasMore: false));
                    }

                    if (query.TaskType == "DIE_TO_OVEN")
                    {
                        return FakeHostReply.Return(new WatchDemandPage(
                            [Demand("aaaaaa0123456789abcdef0123456789", "DIE_TO_OVEN")],
                            "filtered-next",
                            HasMore: true));
                    }

                    return FakeHostReply.Return(new WatchDemandPage(
                        [Demand("default", "WIRE_TO_GATE")],
                        null,
                        HasMore: false));
                }),
            });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-visible-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            PumpUntil(() => requests.Count >= 1);
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 1;
            PumpUntil(() => ((TextBlock)window.FindName("DemandPageText"))?.Text == "第 1 页");

            ((ComboBox)window.FindName("DemandTaskTypeFilter")).SelectedValue = "DIE_TO_OVEN";
            ((TextBox)window.FindName("DemandSublotFilter")).Text = " S-2 ";
            ((TextBox)window.FindName("DemandIdFilter")).Text = "ABCDEF012345";
            ((TextBox)window.FindName("DemandRangeFromFilter")).Text = "2026-08-08T08:00:00+08:00";
            ((TextBox)window.FindName("DemandRangeToFilter")).Text = "2026-08-08T10:00:00+08:00";
            ((Button)window.FindName("DemandQueryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            PumpUntil(() => requests.Any(query => query.TaskType == "DIE_TO_OVEN"));
            var submitted = requests.Last(query => query.TaskType == "DIE_TO_OVEN");
            Assert.Equal("S-2", submitted.Sublot);
            Assert.Equal("abcdef012345", submitted.DemandId);
            Assert.Equal(100, submitted.Limit);
            Assert.Null(submitted.Cursor);
            var grid = (DataGrid)window.FindName("DemandsGrid");
            PumpUntil(() => grid.Items.Count == 1
                && ((WatchDemandDto)grid.Items[0]).DemandId.StartsWith("aaaaaa", StringComparison.Ordinal));
            Assert.True(((Button)window.FindName("DemandNextButton")).IsEnabled);

            ((Button)window.FindName("DemandNextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            PumpUntil(() => ((TextBlock)window.FindName("DemandPageText")).Text == "第 2 页");
            Assert.Contains(requests, query => query.Cursor == "filtered-next");
            Assert.StartsWith("bbbbbb", ((WatchDemandDto)grid.Items[0]).DemandId, StringComparison.Ordinal);
            Assert.False(((Button)window.FindName("DemandNextButton")).IsEnabled);
            Assert.True(((Button)window.FindName("DemandPreviousButton")).IsEnabled);

            window.Close();
        });
    }

    [Fact]
    public void Visible_query_failure_keeps_the_committed_window_and_query()
    {
        RunInSta(() =>
        {
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("visible-failure")
            {
                DemandPage = FakeHostReply.Select<WatchDemandBrowseQuery, WatchDemandPage>(query =>
                    query.TaskType == "DIE_TO_OVEN"
                        ? FakeHostReply.Fail<WatchDemandPage>(
                            WatchHostFailureKind.Http,
                            "/api/demands",
                            "fake validation failure")
                        : FakeHostReply.Return(new WatchDemandPage(
                            [Demand("committed", "WIRE_TO_GATE")],
                            "cursor-2",
                            HasMore: true))),
            });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-visible-failure-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 1;
            var grid = (DataGrid)window.FindName("DemandsGrid");
            PumpUntil(() => grid.Items.Count == 1);
            ((ComboBox)window.FindName("DemandTaskTypeFilter")).SelectedValue = "DIE_TO_OVEN";
            ((Button)window.FindName("DemandQueryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var banner = (Border)window.FindName("ErrorBanner");
            PumpUntil(() => banner.Visibility == Visibility.Visible);

            Assert.Equal("committed", ((WatchDemandDto)grid.Items[0]).DemandId);
            Assert.Equal("第 1 页", ((TextBlock)window.FindName("DemandPageText")).Text);
            Assert.True(((Button)window.FindName("DemandNextButton")).IsEnabled);
            Assert.DoesNotContain(
                "TASK_TYPE=DIE_TO_OVEN",
                ((TextBlock)window.FindName("DemandCommittedQueryText")).Text,
                StringComparison.Ordinal);
            Assert.Contains("fake validation failure", ((TextBlock)window.FindName("ErrorBannerText")).Text);

            window.Close();
        });
    }

    [Fact]
    public void Visible_user_cancel_ignores_a_late_fake_host_response()
    {
        RunInSta(() =>
        {
            var gate = new FakeHostGate();
            var demandCalls = 0;
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("visible-cancel")
            {
                DemandPage = FakeHostReply.Select<WatchDemandBrowseQuery, WatchDemandPage>(_ =>
                {
                    demandCalls++;
                    return demandCalls < 3
                        ? FakeHostReply.Return(new WatchDemandPage(
                            [Demand("committed", "WIRE_TO_GATE")],
                            "cursor-2",
                            HasMore: true))
                        : FakeHostReply.After(
                            gate,
                            new WatchDemandPage(
                                [Demand("late", "DIE_TO_OVEN")],
                                null,
                                HasMore: false),
                            completeAfterCancellation: true);
                }),
            });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-visible-cancel-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            PumpUntil(() => demandCalls >= 1);
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 1;
            var grid = (DataGrid)window.FindName("DemandsGrid");
            PumpUntil(() => grid.Items.Count == 1 && demandCalls >= 2);
            ((Button)window.FindName("DemandRefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var cancel = (Button)window.FindName("DemandCancelButton");
            PumpUntil(() => cancel.IsEnabled && demandCalls >= 3);

            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            gate.Release();

            PumpUntil(() => !cancel.IsEnabled
                && ((TextBlock)window.FindName("DemandNoticeText")).Text == "已取消");
            PumpUntil(() => fakeHost.Timeline.Any(entry =>
                entry.Operation == FakeHostOperation.DemandPage
                && entry.State == FakeHostRequestState.CompletedAfterCancellation));
            Assert.Equal("committed", ((WatchDemandDto)grid.Items[0]).DemandId);
            Assert.True(((Button)window.FindName("DemandNextButton")).IsEnabled);
            Assert.Contains(fakeHost.Timeline, entry =>
                entry.Operation == FakeHostOperation.DemandPage
                && entry.State == FakeHostRequestState.CompletedAfterCancellation);

            window.Close();
        });
    }

    [Fact]
    public void Visible_empty_page_names_the_empty_query_state()
    {
        RunInSta(() =>
        {
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("visible-empty"));
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-visible-empty-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 1;
            var rowCount = (TextBlock)window.FindName("RowCountText");
            PumpUntil(() => rowCount.Text.StartsWith("当前查询无结果", StringComparison.Ordinal));

            Assert.Contains(
                "已提交：status=VISIBLE",
                ((TextBlock)window.FindName("DemandCommittedQueryText")).Text);
            window.Close();
        });
    }

    [Fact]
    public void Slow_overview_refresh_exposes_busy_state_and_cancel()
    {
        RunInSta(() =>
        {
            var health = new WatchPollHealthDto(
                DateTimeOffset.Parse("2026-08-08T09:29:59+08:00"),
                DateTimeOffset.Parse("2026-08-08T09:30:00+08:00"),
                1000,
                1,
                true,
                "SUCCESS",
                []);
            var gate = new FakeHostGate();
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("slow-overview")
            {
                PollHealth = FakeHostReply.Sequence<FakeHostUnit, WatchPollHealthDto?>(
                    FakeHostReply.Return<WatchPollHealthDto?>(health),
                    FakeHostReply.After<WatchPollHealthDto?>(gate, health)),
            });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-slow-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            var cancel = (Button)window.FindName("OverviewCancelButton");
            var busy = (TextBlock)window.FindName("OverviewBusyText");
            PumpUntil(() => cancel.IsEnabled);
            Assert.Equal(Visibility.Visible, busy.Visibility);

            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => !cancel.IsEnabled);
            Assert.Equal(Visibility.Collapsed, busy.Visibility);
            Assert.Equal("已取消", ((TextBlock)window.FindName("OverviewNoticeText")).Text);
            PumpUntil(() => fakeHost.Timeline.Any(entry =>
                entry.Operation == FakeHostOperation.PollHealth
                && entry.State == FakeHostRequestState.Canceled));

            window.Close();
        });
    }

    [Fact]
    public void Navigating_during_initial_overview_refresh_still_loads_the_target_view()
    {
        RunInSta(() =>
        {
            var health = new WatchPollHealthDto(
                DateTimeOffset.Parse("2026-08-08T09:29:59+08:00"),
                DateTimeOffset.Parse("2026-08-08T09:30:00+08:00"),
                1000,
                1,
                true,
                "SUCCESS",
                []);
            var gate = new FakeHostGate();
            var demandCalls = 0;
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("navigate-during-overview")
            {
                PollHealth = FakeHostReply.Sequence<FakeHostUnit, WatchPollHealthDto?>(
                    FakeHostReply.Return<WatchPollHealthDto?>(health),
                    FakeHostReply.After<WatchPollHealthDto?>(gate, health)),
                DemandPage = FakeHostReply.Select<WatchDemandBrowseQuery, WatchDemandPage>(_ =>
                {
                    demandCalls++;
                    return FakeHostReply.Return(new WatchDemandPage(
                        [Demand($"d-{demandCalls}", "WIRE_TO_GATE")],
                        null,
                        false));
                }),
            });
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-overview-navigation-{Guid.NewGuid():N}");
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"));
            var window = composition.CreateMainWindow();

            window.Show();
            PumpUntil(() => ((Button)window.FindName("OverviewCancelButton")).IsEnabled);
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 1;
            var grid = (DataGrid)window.FindName("DemandsGrid");
            PumpUntil(() => demandCalls >= 2 && grid.Items.Count == 1);

            Assert.Equal("d-2", ((WatchDemandDto)grid.Items[0]).DemandId);
            gate.Release();
            window.Close();
        });
    }

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
                DemandPage = FakeHostReply.Return(new WatchDemandPage(
                    snapshot.Demands,
                    snapshot.DemandsNextCursor,
                    snapshot.DemandsHasMore)),
                AlertPage = FakeHostReply.Return(new WatchAlertPage(
                    snapshot.Alerts,
                    snapshot.AlertsNextCursor,
                    snapshot.AlertsHasMore)),
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
            Assert.Contains("[WARNING]", ((TextBlock)window.FindName("OverviewAlertText")).Text);
            Assert.Contains("当前页：DIE_TO_OVEN 2", ((TextBlock)window.FindName("OverviewDemandText")).Text);
            Assert.Contains("最近成功连接", ((TextBlock)window.FindName("OverviewHostText")).Text);

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

    [Fact]
    public void Injected_clock_controls_rendered_refresh_times_for_deterministic_visuals()
    {
        RunInSta(() =>
        {
            var fakeHost = new ScriptedFakeHost(new FakeHostScenario("fixed-visual-clock"));
            var testRoot = Path.Combine(Path.GetTempPath(), $"watch-clock-{Guid.NewGuid():N}");
            var clock = new ManualTimeProvider(
                DateTimeOffset.Parse("2001-02-03T04:05:06Z"));
            using var composition = WatchApplicationComposition.Create(
                FakeOptions(),
                fakeHost.CreateAdapter,
                logDirectory: Path.Combine(testRoot, "logs"),
                layoutPreferencesPath: Path.Combine(testRoot, "layout.json"),
                timeProvider: clock);
            var window = composition.CreateMainWindow();

            window.Show();
            var refreshContext = (TextBlock)window.FindName("PageRefreshContextText");
            PumpUntil(() => !refreshContext.Text.Contains("lastSuccess=(none)", StringComparison.Ordinal));

            Assert.Contains(
                "2001-02-03",
                refreshContext.Text,
                StringComparison.Ordinal);
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
        StaHost.Value.Run(action);
    }

    private sealed class StaDispatcherHost
    {
        private readonly Dispatcher _dispatcher;

        public StaDispatcherHost()
        {
            Dispatcher? dispatcher = null;
            using var started = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                started.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "Watch UI test STA",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(15)), "STA UI test dispatcher did not start.");
            _dispatcher = dispatcher!;
        }

        public void Run(Action action)
        {
            Exception? caught = null;
            _dispatcher.Invoke(() =>
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
            Assert.Null(caught);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value.ToUniversalTime();
    }

}
