using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;
using NavigationView = Wpf.Ui.Controls.NavigationView;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;

namespace MesIngest.Tests;

[Collection("WpfDesktop")]
public sealed class WatchBilingualFoundationProductionTests
{
    [Fact]
    public void Fresh_production_window_starts_in_chinese_and_language_choices_are_self_named() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory();
            try
            {
                using var composition = CreateComposition(root);
                var window = composition.CreateMainWindow(initializeOnLoaded: false);

                Click(window, "SettingsNavigationItem");
                var selector = Assert.IsType<ComboBox>(window.FindName("DisplayLanguageInput"));
                var navigation = Assert.IsType<NavigationView>(window.FindName("WorkspaceNavigation"));

                Assert.Equal(WatchDisplayLanguage.SimplifiedChinese, composition.DisplayLanguageState.Current);
                Assert.Equal(WatchDisplayLanguage.SimplifiedChinese, window.DisplayLanguageState.Current);
                Assert.Equal(WatchDisplayLanguage.SimplifiedChinese, selector.SelectedValue);
                Assert.Equal(["简体中文", "English"], ChoiceLabels(selector));
                Assert.Equal("主导航", AutomationProperties.GetName(navigation));
                Assert.Equal(
                    "设置",
                    Assert.IsType<NavigationViewItem>(window.FindName("SettingsNavigationItem"))
                        .Content?.ToString());

                window.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Saving_english_reprojects_the_open_window_and_a_restarted_composition_restores_english() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory();
            try
            {
                using (var composition = CreateComposition(root))
                {
                    var window = composition.CreateMainWindow(initializeOnLoaded: false);
                    Click(window, "SettingsNavigationItem");
                    SelectLanguage(window, WatchDisplayLanguage.English);

                    Click(window, "SaveRefreshIntervalsButton");

                    var navigation = Assert.IsType<NavigationView>(window.FindName("WorkspaceNavigation"));
                    Assert.Equal(WatchDisplayLanguage.English, composition.DisplayLanguageState.Current);
                    Assert.Equal("Primary navigation", AutomationProperties.GetName(navigation));
                    Assert.Equal(
                        "Settings",
                        Assert.IsType<NavigationViewItem>(window.FindName("SettingsNavigationItem"))
                            .Content?.ToString());
                    Assert.Equal(
                        WatchDisplayLanguage.English,
                        WatchV2PreferencesStore.Load(PreferencesPath(root)).DisplayLanguage);

                    window.Close();
                }

                using (var restarted = CreateComposition(root))
                {
                    var restartedWindow = restarted.CreateMainWindow(initializeOnLoaded: false);
                    var selector = Assert.IsType<ComboBox>(restartedWindow.FindName("DisplayLanguageInput"));

                    Assert.Equal(WatchDisplayLanguage.English, restarted.DisplayLanguageState.Current);
                    Assert.Equal(WatchDisplayLanguage.English, selector.SelectedValue);
                    Assert.Equal(
                        "Settings",
                        Assert.IsType<NavigationViewItem>(restartedWindow.FindName("SettingsNavigationItem"))
                            .Content?.ToString());

                    restartedWindow.Close();
                }
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Failed_language_save_keeps_the_committed_runtime_language_and_visible_projection() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory();
            Directory.CreateDirectory(root);
            var unusablePreferencesTarget = Path.Combine(root, "workspace-target-is-a-directory");
            Directory.CreateDirectory(unusablePreferencesTarget);

            try
            {
                using var composition = WatchV2ApplicationComposition.Create(
                    new WatchOptions { BaseUrl = "http://host-a" },
                    connectionPreferencesPath: Path.Combine(root, "connection.json"),
                    workspacePreferencesPath: unusablePreferencesTarget);
                var window = composition.CreateMainWindow(initializeOnLoaded: false);
                Click(window, "SettingsNavigationItem");
                SelectLanguage(window, WatchDisplayLanguage.English);

                Click(window, "SaveRefreshIntervalsButton");

                Assert.Equal(WatchDisplayLanguage.SimplifiedChinese, composition.DisplayLanguageState.Current);
                Assert.Equal(WatchDisplayLanguage.SimplifiedChinese, window.DisplayLanguageState.Current);
                Assert.Equal(
                    "设置",
                    Assert.IsType<NavigationViewItem>(window.FindName("SettingsNavigationItem"))
                        .Content?.ToString());

                window.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Unknown_saved_language_starts_the_real_window_without_losing_other_preferences() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory();
            Directory.CreateDirectory(root);
            File.WriteAllText(
                PreferencesPath(root),
                """
                {
                  "version": 3,
                  "intervalSeconds": {
                    "overview": 10,
                    "demandSeries": 30,
                    "readabilityAudit": 60,
                    "errorSearch": 300,
                    "currentIngestAttention": 10
                  },
                  "display": {
                    "language": "future-language",
                    "rememberWindowSize": true,
                    "windowWidth": 1440,
                    "windowHeight": 900,
                    "isNavigationPaneOpen": true
                  }
                }
                """);

            try
            {
                using var composition = CreateComposition(root);
                var window = composition.CreateMainWindow(initializeOnLoaded: false);
                var navigation = Assert.IsType<NavigationView>(window.FindName("WorkspaceNavigation"));

                Assert.Equal(WatchDisplayLanguage.SimplifiedChinese, composition.DisplayLanguageState.Current);
                Assert.Equal(10, window.AutoRefreshSettings.Overview.IntervalSeconds);
                Assert.True(navigation.IsPaneOpen);

                window.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Composition_and_window_share_one_observable_language_state() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory();
            try
            {
                using var composition = CreateComposition(root);
                var window = composition.CreateMainWindow(initializeOnLoaded: false);
                var changeCount = 0;
                composition.DisplayLanguageState.Changed += (_, _) => changeCount++;

                Assert.Same(composition.DisplayLanguageState, window.DisplayLanguageState);
                Assert.Equal(WatchDisplayLanguage.SimplifiedChinese, composition.DisplayLanguageState.Current);

                Click(window, "SettingsNavigationItem");
                SelectLanguage(window, WatchDisplayLanguage.English);
                Click(window, "SaveRefreshIntervalsButton");

                Assert.Equal(1, changeCount);
                Assert.Equal(WatchDisplayLanguage.English, composition.DisplayLanguageState.Current);
                Assert.Equal(
                    "Settings",
                    WatchTextCatalog.For(composition.DisplayLanguageState.Current).Settings.PageTitle);

                window.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    [Fact]
    public void Switching_language_preserves_active_page_canonical_filter_selection_focus_and_host_requests() =>
        StaTestRunner.Run(() =>
        {
            var root = NewTempDirectory();
            var client = new RecordingClient();
            try
            {
                using var composition = CreateComposition(root, client);
                var window = composition.CreateMainWindow(initializeOnLoaded: false);
                window.InitializeAsync().GetAwaiter().GetResult();
                window.NavigateFromOverview(new OverviewNavigationIntent(
                    OverviewNavigationTargets.DemandSeries,
                    PageNumber: 1,
                    Lifecycles: ["TRACKING"],
                    CurrentPresences: ["VISIBLE"],
                    SeriesId: "series-filter-17"));
                window.DemandSeriesNavigationTask.GetAwaiter().GetResult();
                var committedFilter = Assert.IsType<DemandSeriesListSnapshot>(
                    window.WorkspaceState.DemandSeries.Snapshot).Filter;
                var selectedId = window.WorkspaceState.DemandSeries.SelectedId;

                Click(window, "SettingsNavigationItem");
                window.Show();
                window.UpdateLayout();
                var selector = Assert.IsType<ComboBox>(window.FindName("DisplayLanguageInput"));
                Assert.Same(selector, System.Windows.Input.Keyboard.Focus(selector));
                SelectLanguage(window, WatchDisplayLanguage.English);
                var stateBefore = window.WorkspaceState;
                var requestsBefore = client.TotalRequestCount;

                Click(window, "SaveRefreshIntervalsButton");

                Assert.Equal(WatchWorkspacePage.Settings, window.ActivePage);
                Assert.Same(stateBefore, window.WorkspaceState);
                Assert.Equal(requestsBefore, client.TotalRequestCount);
                Assert.Equal(committedFilter, window.WorkspaceState.DemandSeries.Snapshot?.Filter);
                Assert.Equal(selectedId, window.WorkspaceState.DemandSeries.SelectedId);
                Assert.Equal("series-filter-17", Assert.IsType<TextBox>(window.FindName("DemandSeriesSeriesIdFilter")).Text);
                Assert.Same(selector, System.Windows.Input.Keyboard.FocusedElement);

                window.Close();
            }
            finally
            {
                DeleteDirectory(root);
            }
        });

    private static WatchV2ApplicationComposition CreateComposition(
        string root,
        IWatchV2ApiClient? client = null) =>
        WatchV2ApplicationComposition.Create(
            new WatchOptions
            {
                BaseUrl = "http://host-a",
                SharedSecret = "external-only",
                RequestTimeoutSeconds = 30,
            },
            client is null ? null : _ => client,
            connectionPreferencesPath: Path.Combine(root, "connection.json"),
            workspacePreferencesPath: PreferencesPath(root));

    private static void Click(WatchWorkspaceWindow window, string name) =>
        Assert.IsAssignableFrom<ButtonBase>(window.FindName(name))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void SelectLanguage(
        WatchWorkspaceWindow window,
        WatchDisplayLanguage language)
    {
        var selector = Assert.IsType<ComboBox>(window.FindName("DisplayLanguageInput"));
        selector.SelectedValue = language;
        Assert.Equal(language, selector.SelectedValue);
    }

    private static string[] ChoiceLabels(ComboBox selector) =>
        selector.Items
            .Cast<object>()
            .Select(item => item switch
            {
                ComboBoxItem comboBoxItem => comboBoxItem.Content?.ToString(),
                _ when !string.IsNullOrWhiteSpace(selector.DisplayMemberPath) =>
                    item.GetType().GetProperty(selector.DisplayMemberPath)?.GetValue(item)?.ToString(),
                _ => item.ToString(),
            })
            .Select(Assert.IsType<string>)
            .ToArray();

    private static string PreferencesPath(string root) =>
        Path.Combine(root, "workspace.json");

    private static string NewTempDirectory() =>
        Path.Combine(Path.GetTempPath(), $"watch-bilingual-window-{Guid.NewGuid():N}");

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingClient : IWatchV2ApiClient
    {
        private static readonly DateTimeOffset At =
            DateTimeOffset.Parse("2026-08-27T14:05:06+08:00");

        public int TotalRequestCount { get; private set; }

        public Task VerifyContractAsync(CancellationToken cancellationToken)
        {
            TotalRequestCount++;
            return Task.CompletedTask;
        }

        public Task<WatchOverviewSnapshot> FetchOverviewAsync(
            WatchOverviewQuery query,
            CancellationToken cancellationToken = default)
        {
            TotalRequestCount++;
            var navigation = new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries);
            return Task.FromResult(new WatchOverviewSnapshot(
                new OperationalSnapshotIdentity("overview-commit", 1, At, "overview-poll", 1, 1, At),
                query.MesAreas ?? [],
                new WatchOverviewSeriesSummary(0, 0, 0, 0, 0, navigation, navigation, navigation, navigation, navigation),
                new WatchOverviewReadabilitySummary(0, 0, 0, navigation, navigation, navigation),
                new WatchOverviewErrorSummary(0, 0, navigation, navigation, navigation),
                new WatchOverviewAttentionSummary(0, [], [], navigation),
                [],
                WatchOverviewRecentActivityStates.NoRecentHighlights,
                WatchOverviewRecentActivityStates.NoRecentHighlightsMessage));
        }

        public Task<DemandSeriesListSnapshot> FetchDemandSeriesAsync(
            DemandSeriesBrowseQuery query,
            CancellationToken cancellationToken = default)
        {
            TotalRequestCount++;
            return Task.FromResult(new DemandSeriesListSnapshot(
                new DemandSeriesSnapshotIdentity(
                    HistoryEpoch.FromGuid(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                    "demand-commit",
                    1,
                    At,
                    "demand-poll"),
                "demand-snapshot",
                query.Filter,
                query.Order,
                0,
                new DemandSeriesFacets(0, 0, 0, 0, 0),
                query.PageSize,
                query.PageNumber,
                0,
                [],
                NextCursor: null,
                HasMore: false));
        }

        public Task<DemandSeriesDetailSnapshot> FetchDemandSeriesDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReadabilityAuditListSnapshot> FetchReadabilityAuditAsync(
            ReadabilityAuditQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReadabilityAuditDetailSnapshot> FetchReadabilityAuditDetailAsync(
            string demandId,
            string snapshotReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ErrorSearchListSnapshot> FetchErrorSearchAsync(
            ErrorSearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ErrorSearchDetailSnapshot> FetchErrorSearchDetailAsync(
            string seriesId,
            string snapshotReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ErrorSearchRawEvidenceSnapshot> FetchErrorRawEvidenceAsync(
            string seriesId,
            string evidenceId,
            string snapshotReference,
            ErrorSearchRawEvidenceQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CurrentIngestAttentionSnapshot> FetchCurrentAttentionAsync(
            CurrentIngestAttentionQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
