using System.IO;
using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MesIngest.Core.SeriesProjection;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using TitleBar = Wpf.Ui.Controls.TitleBar;
using TitleBarButton = Wpf.Ui.Controls.TitleBarButton;

namespace MesIngest.Watch;

internal enum WatchWorkspacePage
{
    Overview,
    DemandSeries,
    ReadabilityAudit,
    ErrorSearch,
    AreaFilter,
    CurrentAttention,
    Settings,
}

internal sealed class WatchOverviewNavigationEventArgs(
    OverviewNavigationIntent intent) : EventArgs
{
    public OverviewNavigationIntent Intent { get; } = intent;
}

/// <summary>
/// Production ticket-19 shell. The window is a thin WPF projection over the
/// immutable V2 workspace state; all Host admission, cancellation, and stale
/// snapshot rules remain owned by <see cref="WatchV2WorkspaceSession"/>.
/// </summary>
internal partial class WatchWorkspaceWindow : IDisposable
{
    private const double MinimumFixedPageViewportHeight = 700;

    private readonly WatchV2WorkspaceSession _session;
    private readonly WatchV2AutoRefreshCoordinator _autoRefresh;
    private readonly WatchDemandSeriesInspectorCoordinator _demandSeriesInspectorCoordinator;
    private readonly WatchWindowNotificationCoordinator _notificationCoordinator;
    private readonly WatchDisplayLanguageState _displayLanguageState;
    private readonly TimeProvider _timeProvider;
    private readonly TimeProvider _presentationTimeProvider;
    private readonly string _connectionPreferencesPath;
    private readonly string _workspacePreferencesPath;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private WatchHostSettings _currentHostSettings;
    private WatchV2Preferences _preferences;
    private WatchAreaDisplayContext _areaContext = WatchAreaDisplayContext.AllAreas;
    private WatchOverviewQuery _overviewQuery = new();
    private DemandSeriesBrowseQuery _demandSeriesQuery = new(new DemandSeriesBrowseFilter());
    private ReadabilityAuditQuery _readabilityAuditQuery = new(new ReadabilityAuditFilter());
    private ErrorSearchQuery _errorSearchQuery = new(
        new ErrorSearchFilter(),
        ErrorSearchWindowSelection.Last7Days);
    private CurrentIngestAttentionQuery _currentAttentionQuery = new();
    private WatchDemandSeriesNavigationContext? _demandSeriesNavigation;
    private string? _focusedDemandId;
    private string? _demandSeriesLifecycleDraft;
    private WatchWorkspacePage _activePage = WatchWorkspacePage.Overview;
    private bool _isRenderingDemandSeries;
    private long _demandSeriesOperationGeneration;
    private bool _initialized;
    private bool _isWatchingSystemTheme;
    private bool _disposed;

    internal WatchWorkspaceWindow(
        WatchHostSettings initialHostSettings,
        WatchV2Preferences preferences,
        string connectionPreferencesPath,
        string workspacePreferencesPath,
        Func<WatchHostSettings, IWatchV2ApiClient>? clientFactory = null,
        TimeProvider? timeProvider = null,
        bool initializeOnLoaded = true,
        string? areaFilterProfilesDirectoryPath = null,
        IWatchAreaProfileDirectoryLauncher? areaProfileDirectoryLauncher = null,
        IWatchAreaProfileDirectoryEventSource? areaProfileDirectoryEventSource = null,
        WatchDemandSeriesInspectorCoordinator? demandSeriesInspectorCoordinator = null,
        Func<bool>? notificationReducedMotionProvider = null,
        WatchDisplayLanguageState? displayLanguageState = null,
        TimeProvider? presentationTimeProvider = null)
    {
        _currentHostSettings = initialHostSettings
            ?? throw new ArgumentNullException(nameof(initialHostSettings));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _displayLanguageState = displayLanguageState
            ?? new WatchDisplayLanguageState(preferences.DisplayLanguage);
        if (_displayLanguageState.Current != preferences.DisplayLanguage)
        {
            throw new ArgumentException(
                "The shared display language state must match the loaded preferences.",
                nameof(displayLanguageState));
        }
        _connectionPreferencesPath = Path.GetFullPath(connectionPreferencesPath);
        _workspacePreferencesPath = Path.GetFullPath(workspacePreferencesPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _presentationTimeProvider = presentationTimeProvider ?? _timeProvider;
        _session = new WatchV2WorkspaceSession(clientFactory, _timeProvider);
        _autoRefresh = new WatchV2AutoRefreshCoordinator(
            _session,
            preferences.RefreshIntervals,
            _timeProvider);
        _notificationCoordinator = new WatchWindowNotificationCoordinator(
            _timeProvider,
            _displayLanguageState);
        _notificationReducedMotionProvider = notificationReducedMotionProvider
            ?? (static () => !System.Windows.SystemParameters.ClientAreaAnimation);
        _demandSeriesInspectorCoordinator = demandSeriesInspectorCoordinator
            ?? new WatchDemandSeriesInspectorCoordinator(
                layoutLoader: LoadInspectorWindowLayout,
                layoutSaver: SaveInspectorWindowLayout,
                displayLanguageState: _displayLanguageState);
        _demandSeriesInspectorCoordinator.StateChanged += OnDemandSeriesInspectorStateChanged;
        _demandSeriesInspectorCoordinator.GenerationFocusRequested +=
            OnDemandSeriesInspectorGenerationFocusRequested;
        InitializeAreaFilterProfiles(
            areaFilterProfilesDirectoryPath,
            _timeProvider,
            areaProfileDirectoryLauncher,
            areaProfileDirectoryEventSource);

        InitializeComponent();
        InitializeNotifications();
        InitializeFeedbackLifecycle();
        InitializeDemandSeriesPage();
        InitializeReadabilityAuditPage();
        InitializeAreaFilterProfilePage();
        InitializeDataPageAreaProfileSelectors();
        InitializeTicket22Pages();
        WorkspaceContent.SizeChanged += OnWorkspaceContentSizeChanged;
        WorkspaceNavigation.PaneOpened += OnWorkspaceNavigationPaneStateChanged;
        WorkspaceNavigation.PaneClosed += OnWorkspaceNavigationPaneStateChanged;
        if (Application.Current is null)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);
        }
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        ApplyWatchThemeResources(
            Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme());
        InitializeIntervalInputs();
        InitializeDisplayLanguageInput();
        ApplyDisplayPreferences(preferences.Display);
        PopulateSettingsInputs();
        ApplyLocalizedShellAndSettingsText();
        _displayLanguageState.Changed += OnDisplayLanguageChanged;
        _autoRefresh.RefreshStateChanged += OnAutoRefreshStateChanged;
        SourceInitialized += OnWindowSourceInitialized;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        if (initializeOnLoaded)
        {
            Loaded += OnWindowLoaded;
        }

        NavigateTo(WatchWorkspacePage.Overview, activateRefresh: false);
        RenderWorkspace();
    }

    private void OnApplicationThemeChanged(
        Wpf.Ui.Appearance.ApplicationTheme theme,
        Color systemAccent)
    {
        if (_disposed)
        {
            return;
        }

        ApplyWatchThemeResources(theme);
    }

    internal void ApplyWatchThemeResources(Wpf.Ui.Appearance.ApplicationTheme theme)
    {
        Resources["WatchAccentSoftBrush"] = theme is Wpf.Ui.Appearance.ApplicationTheme.Dark
            or Wpf.Ui.Appearance.ApplicationTheme.HighContrast
                ? FindResource("ControlFillColorSecondaryBrush")
                : FindResource("WatchAccentSoftLightBrush");
    }

    internal WatchV2WorkspaceState WorkspaceState => _session.State;

    internal WatchV2AutoRefreshSettings AutoRefreshSettings => _autoRefresh.Settings;

    internal WatchAreaDisplayContext AreaContext => _areaContext;

    internal WatchWorkspacePage ActivePage => _activePage;

    internal WatchDemandSeriesInspectorCoordinator DemandSeriesInspectorCoordinator =>
        _demandSeriesInspectorCoordinator;

    internal WatchDisplayLanguageState DisplayLanguageState => _displayLanguageState;

    internal OverviewNavigationIntent? LastOverviewNavigationIntent { get; private set; }

    internal Task InitializationTask { get; private set; } = Task.CompletedTask;

    internal Task DemandSeriesNavigationTask { get; private set; } = Task.CompletedTask;

    internal Task DemandSeriesInspectorLoadTask { get; private set; } = Task.CompletedTask;

    internal event EventHandler<WatchOverviewNavigationEventArgs>? OverviewNavigationRequested;

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            await InitializationTask.ConfigureAwait(true);
            return;
        }

        _initialized = true;
        var initialization = InitializeCoreAsync(cancellationToken);
        InitializationTask = initialization;
        await initialization.ConfigureAwait(true);
    }

    internal async Task<bool> ApplyHostAsync(
        WatchHostSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        BeginErrorSearchOperation();
        BeginCurrentAttentionOperation();
        _errorSearchQuery = WatchErrorSearchQueries.StartLatest();
        _currentAttentionQuery = WatchCurrentIngestAttentionQueries.StartLatest();
        _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
        _selectedErrorPeriodId = null;
        _selectedErrorEvidenceId = null;
        _selectedCurrentAttentionIdentity = null;
        _currentAttentionSelectionNotice = null;
        ResetErrorSearchCursorHistory();
        SyncErrorSearchFilterControls(_errorSearchQuery);
        SyncCurrentAttentionFilterControls(_currentAttentionQuery);
        _currentHostSettings = settings;
        _autoRefresh.Deactivate();
        _demandSeriesInspectorCoordinator.CloseCurrent();
        NavigateTo(WatchWorkspacePage.Overview, activateRefresh: false);
        var apply = _session.ApplyAsync(settings, cancellationToken);
        var expectedHostGeneration = _session.State.HostGeneration;
        RenderWorkspace();
        await apply.ConfigureAwait(true);
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        RenderWorkspace();
        if (_session.State.HostGeneration != expectedHostGeneration)
        {
            return false;
        }

        WatchConnectionPreferencesStore.Save(
            _connectionPreferencesPath,
            new WatchConnectionPreferences(
                settings.BaseUrl,
                settings.RequestTimeoutSeconds,
                WatchCredentialReference.ExternalConfiguration));

        if (_session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            await RefreshOverviewAndRenderAsync(cancellationToken).ConfigureAwait(true);
            if (_disposed
                || cancellationToken.IsCancellationRequested
                || _session.State.HostGeneration != expectedHostGeneration)
            {
                return false;
            }

            _autoRefresh.ActivateOverview(_overviewQuery);
        }

        return true;
    }

    internal async Task ApplyAreaContextAsync(
        WatchAreaDisplayContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloseAreaProfileFileOperation(restoreInvokerFocus: false);
        _areaContext = (context ?? throw new ArgumentNullException(nameof(context)))
            .NormalizeAndValidate();
        _overviewQuery = new WatchOverviewQuery(_areaContext.MesAreas).NormalizeAndValidate();
        _demandSeriesQuery = WatchDemandSeriesQueries.StartLatest(
            _demandSeriesQuery.Filter with { MesAreas = _areaContext.MesAreas },
            _demandSeriesQuery.PageSize);
        SyncDemandSeriesFilterControls(_demandSeriesQuery);
        _readabilityAuditQuery = WatchReadabilityAuditQueries.StartLatest(
            _readabilityAuditQuery.Filter with { MesAreas = _areaContext.MesAreas },
            _readabilityAuditQuery.PageSize);
        SyncReadabilityFilterControls(_readabilityAuditQuery);
        RenderWorkspace();

        var demandAreaOperation = BeginDemandSeriesOperation();
        var auditAreaOperation = BeginReadabilityAuditOperation();
        _autoRefresh.Deactivate();
        await _autoRefresh.WaitForIdleAsync()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        if (!IsCurrentDemandSeriesOperation(demandAreaOperation, cancellationToken)
            || !IsCurrentReadabilityAuditOperation(auditAreaOperation, cancellationToken))
        {
            return;
        }

        if (_session.State.ConnectionStatus != WatchHostConnectionStatus.Connected)
        {
            return;
        }

        var overviewRefresh = _session.RefreshOverviewPageAsync(_overviewQuery, cancellationToken);
        var demandSeriesRefresh = _session.RefreshLatestDemandSeriesPageAsync(
            _demandSeriesQuery,
            cancellationToken);
        var readabilityRefresh = _session.RefreshLatestReadabilityAuditPageAsync(
            _readabilityAuditQuery,
            cancellationToken);
        RenderWorkspace();
        await Task.WhenAll(overviewRefresh, demandSeriesRefresh, readabilityRefresh)
            .ConfigureAwait(true);
        if (!IsCurrentDemandSeriesOperation(demandAreaOperation, cancellationToken)
            || !IsCurrentReadabilityAuditOperation(auditAreaOperation, cancellationToken))
        {
            return;
        }

        if (!_session.State.DemandSeries.IsStale
            && _session.State.DemandSeries.Snapshot is { } demandSnapshot)
        {
            _demandSeriesQuery = CanonicalDemandSeriesAutoRefreshQuery(demandSnapshot);
        }
        if (!_session.State.ReadabilityAudit.IsStale
            && _session.State.ReadabilityAudit.Snapshot is { } auditSnapshot)
        {
            _readabilityAuditQuery = CanonicalReadabilityAuditAutoRefreshQuery(auditSnapshot);
        }

        var desiredSeriesId = _demandSeriesNavigation?.SeriesId
            ?? _session.State.DemandSeries.SelectedId
            ?? _demandSeriesQuery.Filter.SeriesId;
        if (_activePage == WatchWorkspacePage.DemandSeries
            && !string.IsNullOrWhiteSpace(desiredSeriesId)
            && _session.State.DemandSeries.Snapshot is { } visibleDemandSnapshot
            && visibleDemandSnapshot.Items.Any(item => string.Equals(
                item.SeriesId,
                desiredSeriesId,
                StringComparison.Ordinal)))
        {
            await SelectDemandSeriesAndRenderAsync(
                    desiredSeriesId,
                    demandAreaOperation,
                    cancellationToken,
                    manageAutoRefresh: false)
                .ConfigureAwait(true);
            if (!IsCurrentDemandSeriesOperation(demandAreaOperation, cancellationToken))
            {
                return;
            }

            if (_demandSeriesNavigation?.OpenInspector == true
                && OpenOrShowDemandSeriesInspector())
            {
                await DemandSeriesInspectorLoadTask.ConfigureAwait(true);
                if (!IsCurrentDemandSeriesOperation(demandAreaOperation, cancellationToken))
                {
                    return;
                }
            }
        }

        RenderWorkspace();
        switch (_activePage)
        {
            case WatchWorkspacePage.Overview:
                _autoRefresh.ActivateOverview(_overviewQuery);
                break;
            case WatchWorkspacePage.DemandSeries:
                _autoRefresh.ActivateDemandSeries(_demandSeriesQuery);
                break;
            case WatchWorkspacePage.ReadabilityAudit:
                _autoRefresh.ActivateReadabilityAudit(_readabilityAuditQuery);
                break;
        }
    }

    internal void ApplyLocalPreferences(
        WatchV2AutoRefreshSettings refreshIntervals,
        WatchV2DisplayPreferences display) =>
        ApplyLocalPreferences(refreshIntervals, display, _preferences.DisplayLanguage);

    private void ApplyLocalPreferences(
        WatchV2AutoRefreshSettings refreshIntervals,
        WatchV2DisplayPreferences display,
        WatchDisplayLanguage displayLanguage)
    {
        ArgumentNullException.ThrowIfNull(refreshIntervals);
        ArgumentNullException.ThrowIfNull(display);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var proposed = new WatchV2Preferences(
            refreshIntervals,
            display,
            displayLanguage);
        WatchV2PreferencesStore.Save(_workspacePreferencesPath, proposed);
        foreach (var view in Enum.GetValues<WatchV2DataView>())
        {
            _autoRefresh.Update(view, refreshIntervals.For(view));
        }

        _preferences = proposed with { RefreshIntervals = _autoRefresh.Settings };
        ApplyDisplayPreferences(display, restoreGeometry: false);
        _displayLanguageState.ApplyCommitted(displayLanguage);
    }

    internal void NavigateFromOverview(OverviewNavigationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.PageNumber != 1 || intent.Cursor is not null)
        {
            throw new ArgumentException(
                "Overview navigation must start on page one with a null cursor.",
                nameof(intent));
        }

        if (intent.Target is OverviewNavigationTargets.DemandSeries
            or OverviewNavigationTargets.DemandSeriesDetail)
        {
            _demandSeriesNavigation = WatchDemandSeriesNavigationContext.FromOverview(
                _session.State.Overview.Snapshot,
                intent);
            _focusedDemandId = _demandSeriesNavigation?.FocusedDemandId;
        }

        LastOverviewNavigationIntent = intent;
        ApplyNavigationIntent(intent);
        OverviewNavigationRequested?.Invoke(
            this,
            new WatchOverviewNavigationEventArgs(intent));
        if (_activePage == WatchWorkspacePage.DemandSeries
            && _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            DemandSeriesNavigationTask = LoadDemandSeriesNavigationAsync(
                intent.SeriesId,
                _lifetimeCancellation.Token,
                openInspector: string.Equals(
                    intent.Target,
                    OverviewNavigationTargets.DemandSeriesDetail,
                    StringComparison.Ordinal));
        }
        else if (_activePage == WatchWorkspacePage.ReadabilityAudit
                 && _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            ReadabilityAuditNavigationTask = LoadReadabilityAuditNavigationAsync(
                _lifetimeCancellation.Token);
        }
        else if (_activePage == WatchWorkspacePage.ErrorSearch
                 && _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            ErrorSearchNavigationTask = LoadErrorSearchNavigationAsync(
                _lifetimeCancellation.Token);
        }
        else if (_activePage == WatchWorkspacePage.CurrentAttention
                 && _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            CurrentAttentionNavigationTask = LoadCurrentAttentionNavigationAsync(
                _lifetimeCancellation.Token);
        }
    }

    /// <summary>
    /// Strongly typed entry point for other V2 operational pages (notably the
    /// readability audit) to drill into a series without sharing their frozen
    /// snapshot. The DemandSeries page always acquires its own current snapshot
    /// and uses the source fence only for an explicit comparison message.
    /// </summary>
    internal Task NavigateToDemandSeriesAsync(
        WatchDemandSeriesNavigationContext navigation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var areas = new WatchOverviewQuery(navigation.RequestedMesAreas)
            .NormalizeAndValidate()
            .MesAreas ?? [];
        _demandSeriesNavigation = navigation;
        _focusedDemandId = navigation.FocusedDemandId;
        _demandSeriesQuery = WatchDemandSeriesQueries.StartLatest(
            new DemandSeriesBrowseFilter
            {
                SeriesId = navigation.SeriesId,
                DemandId = navigation.FocusedDemandId,
                MesAreas = areas,
            },
            _demandSeriesQuery.PageSize);
        SyncDemandSeriesFilterControls(_demandSeriesQuery);
        NavigateTo(WatchWorkspacePage.DemandSeries);
        DemandSeriesNavigationTask = _session.State.ConnectionStatus
            == WatchHostConnectionStatus.Connected
            ? LoadDemandSeriesNavigationAsync(
                navigation.SeriesId,
                cancellationToken,
                navigation.OpenInspector)
            : Task.CompletedTask;
        return DemandSeriesNavigationTask;
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var apply = _session.ApplyAsync(_currentHostSettings, cancellationToken);
        RenderWorkspace();
        await apply.ConfigureAwait(true);
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        RenderWorkspace();
        if (_session.State.ConnectionStatus != WatchHostConnectionStatus.Connected)
        {
            return;
        }

        await RefreshOverviewAndRenderAsync(cancellationToken).ConfigureAwait(true);
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _autoRefresh.ActivateOverview(_overviewQuery);
    }

    private async Task RefreshOverviewAndRenderAsync(CancellationToken cancellationToken)
    {
        var refresh = _session.RefreshOverviewPageAsync(_overviewQuery, cancellationToken);
        RenderWorkspace();
        await refresh.ConfigureAwait(true);
        RenderWorkspace();
    }

    private void InitializeDemandSeriesPage()
    {
        WatchGridClipboardBehavior.Attach(
            DemandSeriesGrid,
            _displayLanguageState,
            preserveSelectionUnit: true);
        DemandSeriesPresenceFilter.SelectedValuePath = nameof(FrameworkElement.Tag);
        DemandSeriesWorkTypeFilter.SelectedValuePath = nameof(FrameworkElement.Tag);
        DemandSeriesPresenceFilter.SelectionChanged += OnDemandSeriesFilterDraftChanged;
        DemandSeriesWorkTypeFilter.SelectionChanged += OnDemandSeriesFilterDraftChanged;
        DemandSeriesWorkTypeFilter.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(
            OnDemandSeriesFilterDraftTextChanged));
        DemandSeriesSublotFilter.TextChanged += OnDemandSeriesFilterDraftTextChanged;
        DemandSeriesSeriesIdFilter.TextChanged += OnDemandSeriesFilterDraftTextChanged;
        DemandSeriesDemandIdFilter.TextChanged += OnDemandSeriesFilterDraftTextChanged;
        DemandSeriesPageSizeFilter.SelectionChanged += OnDemandSeriesFilterDraftChanged;
        UpdateDemandSeriesClearFiltersState();
    }

    private async Task LoadDemandSeriesNavigationAsync(
        string? desiredSeriesId,
        CancellationToken cancellationToken,
        bool openInspector = false)
    {
        try
        {
            await RefreshLatestDemandSeriesAndRenderAsync(
                    _demandSeriesQuery,
                    desiredSeriesId,
                    cancellationToken)
                .ConfigureAwait(true);
            if (openInspector
                && string.Equals(
                    _session.State.DemandSeries.SelectedId,
                    desiredSeriesId,
                    StringComparison.Ordinal)
                && OpenOrShowDemandSeriesInspector())
            {
                await DemandSeriesInspectorLoadTask.ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || _lifetimeCancellation.IsCancellationRequested)
        {
            // Leaving the page or closing the window is a neutral end to navigation.
        }
    }

    private async Task RefreshLatestDemandSeriesAndRenderAsync(
        DemandSeriesBrowseQuery query,
        string? desiredSeriesId,
        CancellationToken cancellationToken)
    {
        var priorSelectedSeriesId = _session.State.DemandSeries.SelectedId;
        var operation = BeginDemandSeriesOperation();
        if (!await SuspendDemandSeriesAutoRefreshAsync(
                operation,
                cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        _demandSeriesQuery = query;
        var refresh = _session.RefreshLatestDemandSeriesPageAsync(query, cancellationToken);
        RenderWorkspace();
        await refresh.ConfigureAwait(true);
        if (!IsCurrentDemandSeriesOperation(operation, cancellationToken))
        {
            return;
        }

        RenderWorkspace();

        var view = _session.State.DemandSeries;
        if (!view.IsStale && view.Snapshot is { } committed)
        {
            _demandSeriesQuery = CanonicalDemandSeriesAutoRefreshQuery(committed);
            var targetSeriesId = committed.Items.Any(item => string.Equals(
                    item.SeriesId,
                    desiredSeriesId,
                    StringComparison.Ordinal))
                ? desiredSeriesId
                : committed.Items.Any(item => string.Equals(
                    item.SeriesId,
                    priorSelectedSeriesId,
                    StringComparison.Ordinal))
                    ? priorSelectedSeriesId
                    : priorSelectedSeriesId is null
                        && desiredSeriesId is null
                        && view.SelectionNotice is null
                            ? committed.Items.FirstOrDefault()?.SeriesId
                            : null;
            if (!string.IsNullOrWhiteSpace(targetSeriesId)
                && !string.Equals(view.SelectedId, targetSeriesId, StringComparison.Ordinal))
            {
                await SelectDemandSeriesAndRenderAsync(
                        targetSeriesId,
                        operation,
                        cancellationToken,
                        manageAutoRefresh: false)
                    .ConfigureAwait(true);
                if (!IsCurrentDemandSeriesOperation(operation, cancellationToken))
                {
                    return;
                }
            }
            else if (targetSeriesId is null && priorSelectedSeriesId is not null)
            {
                await SelectDemandSeriesAndRenderAsync(
                        seriesId: null,
                        operation,
                        cancellationToken,
                        manageAutoRefresh: false)
                    .ConfigureAwait(true);
                if (!IsCurrentDemandSeriesOperation(operation, cancellationToken))
                {
                    return;
                }
            }
        }

        if (_activePage == WatchWorkspacePage.DemandSeries
            && _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            _autoRefresh.ActivateDemandSeries(_demandSeriesQuery);
        }

        await EnsureOpenDemandSeriesInspectorDetailAsync(cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task RefreshFrozenDemandSeriesAndRenderAsync(
        DemandSeriesBrowseQuery frozenRequest,
        DemandSeriesBrowseQuery automaticRequest,
        CancellationToken cancellationToken)
    {
        var operation = BeginDemandSeriesOperation();
        if (!await SuspendDemandSeriesAutoRefreshAsync(
                operation,
                cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        _demandSeriesQuery = automaticRequest;
        var refresh = _session.RefreshDemandSeriesAsync(frozenRequest, cancellationToken);
        RenderWorkspace();
        await refresh.ConfigureAwait(true);
        if (!IsCurrentDemandSeriesOperation(operation, cancellationToken))
        {
            return;
        }

        RenderWorkspace();

        var view = _session.State.DemandSeries;
        if (!view.IsStale && view.Snapshot is { } committed)
        {
            _demandSeriesQuery = CanonicalDemandSeriesAutoRefreshQuery(committed);
        }

        if (_activePage == WatchWorkspacePage.DemandSeries
            && _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            _autoRefresh.ActivateDemandSeries(_demandSeriesQuery);
        }

        await EnsureOpenDemandSeriesInspectorDetailAsync(cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task SelectDemandSeriesAndRenderAsync(
        string? seriesId,
        CancellationToken cancellationToken) =>
        await SelectDemandSeriesAndRenderAsync(
            seriesId,
            BeginDemandSeriesOperation(),
            cancellationToken,
            manageAutoRefresh: true).ConfigureAwait(true);

    private async Task SelectDemandSeriesAndRenderAsync(
        string? seriesId,
        long operation,
        CancellationToken cancellationToken,
        bool manageAutoRefresh)
    {
        if (manageAutoRefresh
            && !await SuspendDemandSeriesAutoRefreshAsync(
                operation,
                cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        if (!IsCurrentDemandSeriesOperation(operation, cancellationToken))
        {
            return;
        }

        _session.SetDemandSeriesSelection(seriesId);
        _session.SetDemandSeriesFocus(_focusedDemandId);
        RenderWorkspace();
        if (_demandSeriesInspectorCoordinator.IsOpen
            && !string.IsNullOrWhiteSpace(seriesId))
        {
            if (CreateDemandSeriesInspectorStatePresentation() is { } loadingTarget)
            {
                _demandSeriesInspectorCoordinator.Update(loadingTarget);
            }

            var detailLoad = _session.LoadSelectedDemandSeriesDetailAsync(cancellationToken);
            RenderWorkspace();
            await detailLoad.ConfigureAwait(true);
        }

        if (!IsCurrentDemandSeriesOperation(operation, cancellationToken))
        {
            return;
        }

        RenderWorkspace();
        if (manageAutoRefresh
            && _activePage == WatchWorkspacePage.DemandSeries
            && _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            _autoRefresh.ActivateDemandSeries(_demandSeriesQuery);
        }

    }

    private long BeginDemandSeriesOperation() =>
        Interlocked.Increment(ref _demandSeriesOperationGeneration);

    private bool IsCurrentDemandSeriesOperation(
        long operation,
        CancellationToken cancellationToken) =>
        !_disposed
        && !cancellationToken.IsCancellationRequested
        && operation == Interlocked.Read(ref _demandSeriesOperationGeneration);

    private async Task<bool> SuspendDemandSeriesAutoRefreshAsync(
        long operation,
        CancellationToken cancellationToken)
    {
        if (_autoRefresh.ActiveView == WatchV2DataView.DemandSeries)
        {
            _autoRefresh.Deactivate();
            await _autoRefresh.WaitForIdleAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(true);
        }

        return IsCurrentDemandSeriesOperation(operation, cancellationToken);
    }

    private static DemandSeriesBrowseQuery CanonicalDemandSeriesAutoRefreshQuery(
        DemandSeriesListSnapshot snapshot) => snapshot.TotalPages == 0
        ? WatchDemandSeriesQueries.StartLatest(snapshot.Filter, snapshot.PageSize)
        : WatchDemandSeriesQueries.OpenFrozenPage(snapshot, snapshot.PageNumber);

    private DemandSeriesBrowseFilter ReadDemandSeriesFilter() => new()
    {
        Lifecycles = string.IsNullOrWhiteSpace(_demandSeriesLifecycleDraft)
            ? []
            : [_demandSeriesLifecycleDraft],
        CurrentPresences = ReadDemandSeriesChoice(DemandSeriesPresenceFilter),
        WorkTypes = ReadDemandSeriesChoice(DemandSeriesWorkTypeFilter),
        SublotContains = ReadDemandSeriesText(DemandSeriesSublotFilter.Text),
        SeriesId = ReadDemandSeriesText(DemandSeriesSeriesIdFilter.Text),
        DemandId = ReadDemandSeriesText(DemandSeriesDemandIdFilter.Text),
        MesAreas = _areaContext.MesAreas,
    };

    private static IReadOnlyList<string> ReadDemandSeriesChoice(ComboBox comboBox)
    {
        var value = comboBox.IsEditable
            ? comboBox.SelectedItem is ComboBoxItem editableItem
                && editableItem.Tag is string editableCanonical
                ? editableCanonical
                : comboBox.Text
            : comboBox.SelectedItem is ComboBoxItem item
                && item.Tag is string canonical
                ? canonical
                : comboBox.Text;
        value = ReadDemandSeriesText(value);
        return value is null
            ? []
            : [value];
    }

    private static string? ReadDemandSeriesText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private int ReadDemandSeriesPageSize()
    {
        var value = DemandSeriesPageSizeFilter.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString()
            : DemandSeriesPageSizeFilter.Text;
        return int.TryParse(value, out var pageSize)
            ? pageSize
            : throw new ArgumentException("每页数量必须是 25、50、100 或 200。");
    }

    private bool ShouldOfferDemandSeriesAllAreasConfirmation()
    {
        var view = _session.State.DemandSeries;
        return !view.IsRefreshing
            && view.LastFailureAt is null
            && view.Snapshot is { ExactTotalCount: 0 } snapshot
            && snapshot.Filter.MesAreas.Count > 0
            && !string.IsNullOrWhiteSpace(
                _demandSeriesNavigation?.SeriesId ?? _demandSeriesQuery.Filter.SeriesId);
    }

    internal async Task ConfirmDemandSeriesAllAreasAsync(
        CancellationToken cancellationToken = default)
    {
        if (!ShouldOfferDemandSeriesAllAreasConfirmation())
        {
            throw new InvalidOperationException(
                "The current DemandSeries result does not require an all-AREA confirmation.");
        }

        var applied = _areaProfileStore.ApplyAllAreas();
        await ApplyAreaContextAsync(
                applied.CurrentApplied.ToDisplayContext(),
                cancellationToken)
            .ConfigureAwait(true);
        RenderAreaProfiles(reloadProfiles: true);
    }

    private void SyncDemandSeriesFilterControls(DemandSeriesBrowseQuery query)
    {
        SetDemandSeriesLifecycleDraft(query.Filter.Lifecycles.SingleOrDefault());
        SelectDemandSeriesChoice(DemandSeriesPresenceFilter, query.Filter.CurrentPresences.SingleOrDefault());
        SelectDemandSeriesChoice(DemandSeriesWorkTypeFilter, query.Filter.WorkTypes.SingleOrDefault());
        DemandSeriesSublotFilter.Text = query.Filter.SublotContains ?? string.Empty;
        DemandSeriesSeriesIdFilter.Text = query.Filter.SeriesId ?? string.Empty;
        DemandSeriesDemandIdFilter.Text = query.Filter.DemandId ?? string.Empty;
        SelectDemandSeriesChoice(DemandSeriesPageSizeFilter, query.PageSize.ToString());
    }

    private static void SelectDemandSeriesChoice(ComboBox comboBox, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            comboBox.SelectedIndex = 0;
            return;
        }

        foreach (var candidate in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(candidate.Tag?.ToString(), value, StringComparison.Ordinal)
                || string.Equals(candidate.Content?.ToString(), value, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = candidate;
                return;
            }
        }

        if (comboBox.IsEditable)
        {
            comboBox.SelectedIndex = -1;
            comboBox.Text = value;
        }
    }

    private void OnDemandSeriesFilterDraftChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateDemandSeriesClearFiltersState();

    private void OnDemandSeriesLifecycleSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { Tag: string value })
        {
            SetDemandSeriesLifecycleDraft(value);
            UpdateDemandSeriesClearFiltersState();
        }
    }

    private void SetDemandSeriesLifecycleDraft(string? value)
    {
        _demandSeriesLifecycleDraft = string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
        var allSelected = _demandSeriesLifecycleDraft is null;
        var trackingSelected = string.Equals(
            _demandSeriesLifecycleDraft,
            DemandSeriesLifecycleContract.Tracking,
            StringComparison.Ordinal);
        var archivedSelected = string.Equals(
            _demandSeriesLifecycleDraft,
            DemandSeriesLifecycleContract.Archived,
            StringComparison.Ordinal);
        DemandSeriesLifecycleAllButton.Appearance = allSelected
            ? ControlAppearance.Primary
            : ControlAppearance.Transparent;
        DemandSeriesLifecycleTrackingButton.Appearance = trackingSelected
                ? ControlAppearance.Primary
                : ControlAppearance.Transparent;
        DemandSeriesLifecycleArchivedButton.Appearance = archivedSelected
                ? ControlAppearance.Primary
                : ControlAppearance.Transparent;
        SetSegmentSelectionStatus(DemandSeriesLifecycleAllButton, allSelected);
        SetSegmentSelectionStatus(DemandSeriesLifecycleTrackingButton, trackingSelected);
        SetSegmentSelectionStatus(DemandSeriesLifecycleArchivedButton, archivedSelected);
    }

    private void SetSegmentSelectionStatus(
        Wpf.Ui.Controls.Button button,
        bool isSelected) =>
        AutomationProperties.SetItemStatus(
            button,
            isSelected
                ? _displayLanguageState.Catalog.DemandSeries.Selected
                : _displayLanguageState.Catalog.DemandSeries.NotSelected);

    private void OnDemandSeriesFilterDraftTextChanged(object sender, TextChangedEventArgs e) =>
        UpdateDemandSeriesClearFiltersState();

    private void UpdateDemandSeriesClearFiltersState()
    {
        if (DemandSeriesClearFiltersButton is null)
        {
            return;
        }

        var hasChoice = _demandSeriesLifecycleDraft is not null
            || DemandSeriesPresenceFilter.SelectedIndex > 0
            || ReadDemandSeriesChoice(DemandSeriesWorkTypeFilter).Count > 0;
        var hasText = ReadDemandSeriesText(DemandSeriesSublotFilter.Text) is not null
            || ReadDemandSeriesText(DemandSeriesSeriesIdFilter.Text) is not null
            || ReadDemandSeriesText(DemandSeriesDemandIdFilter.Text) is not null;
        var pageSizeIsDefault = int.TryParse(
                (DemandSeriesPageSizeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString()
                    ?? DemandSeriesPageSizeFilter.Text,
                out var pageSize)
            && pageSize == DemandSeriesBrowseQuery.DefaultPageSize;
        DemandSeriesClearFiltersButton.IsEnabled = hasChoice || hasText || !pageSizeIsDefault;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;
        try
        {
            await InitializeAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the window is a neutral end to startup.
        }
    }

    private void OnAutoRefreshStateChanged(
        object? sender,
        WatchV2AutoRefreshEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        void RenderAndSynchronizeInspector()
        {
            if (_disposed)
            {
                return;
            }

            RenderWorkspace();
            if (e.View == WatchV2DataView.DemandSeries
                && e.Phase == WatchV2AutoRefreshPhase.Completed)
            {
                _ = SynchronizeOpenDemandSeriesInspectorSafelyAsync();
            }
        }

        if (Dispatcher.CheckAccess())
        {
            RenderAndSynchronizeInspector();
        }
        else
        {
            _ = Dispatcher.BeginInvoke((Action)RenderAndSynchronizeInspector);
        }
    }

    private async Task SynchronizeOpenDemandSeriesInspectorSafelyAsync()
    {
        try
        {
            await EnsureOpenDemandSeriesInspectorDetailAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Window shutdown is a neutral end to Inspector synchronization.
        }
    }

    private async Task EnsureOpenDemandSeriesInspectorDetailAsync(
        CancellationToken cancellationToken)
    {
        if (!_demandSeriesInspectorCoordinator.IsOpen
            || _session.State.DemandSeries is not { SelectedId: { Length: > 0 } seriesId } view
            || view.IsDetailLoading
            || DetailMatchesTargetSnapshot(view.Detail, seriesId, view.Snapshot))
        {
            return;
        }

        var load = _session.LoadSelectedDemandSeriesDetailAsync(cancellationToken);
        RenderWorkspace();
        await load.ConfigureAwait(true);
        if (!_disposed && !cancellationToken.IsCancellationRequested)
        {
            RenderWorkspace();
        }
    }

    private void RenderWorkspace()
    {
        if (_disposed)
        {
            return;
        }

        var state = _session.State;
        SynchronizeContinuingFeedback(state);
        var catalog = _displayLanguageState.Catalog;
        var overviewText = catalog.Overview;
        var presentation = WatchOverviewPresentation.Project(
            state,
            _areaContext,
            catalog,
            _presentationTimeProvider.GetUtcNow());
        OverviewContextText.Text =
            $"{presentation.SnapshotFacts} · {presentation.ClientAttemptFacts} · {overviewText.RefreshPolicy(_preferences.RefreshIntervals.Overview.IntervalSeconds)}";
        OverviewInfoBar.IsOpen = false;
        OverviewInfoBar.Severity = ToInfoBarSeverity(presentation.InfoSeverity);
        OverviewInfoBar.Title = presentation.InfoTitle;
        OverviewInfoBar.Message = presentation.InfoMessage;
        SeriesSummaryValue.Text = presentation.SeriesValue;
        SeriesSummaryUnitText.Text = presentation.SeriesUnit;
        SeriesSummaryDetail.Text = presentation.SeriesDetail;
        ReadabilitySummaryValue.Text = presentation.ReadabilityValue;
        ReadabilitySummaryUnitText.Text = presentation.ReadabilityUnit;
        ReadabilitySummaryDetail.Text = presentation.ReadabilityDetail;
        ErrorsSummaryValue.Text = presentation.ErrorsValue;
        ErrorsSummaryUnitText.Text = presentation.ErrorsUnit;
        ErrorsSummaryDetail.Text = presentation.ErrorsDetail;
        AttentionSummaryValue.Text = presentation.AttentionValue;
        AttentionSummaryUnitText.Text = presentation.AttentionUnit;
        AttentionSummaryDetail.Text = presentation.AttentionDetail;
        var overviewSnapshot = state.Overview.Snapshot;
        SetOverviewMetricState(
            ErrorsSummaryValue,
            overviewSnapshot?.Errors.ActiveSeriesCount > 0);
        SetOverviewMetricState(
            AttentionSummaryValue,
            overviewSnapshot?.Attention.ExactTotalItemCount > 0);
        SetOverviewSubsummaryState(
            NotReadableSummaryAction,
            overviewSnapshot?.Readability.NotReadableCount > 0,
            "SystemFillColorCriticalBrush");
        LocalAreaHeadingText.Text = presentation.LocalAreaHeading;
        LocalAreaDetailText.Text = presentation.LocalAreaDetail;
        HostAreaScopeText.Text = presentation.HostAreaScope;
        RecentActivityHeadingText.Text = presentation.RecentActivityHeading;
        AutomationProperties.SetName(
            OverviewContextText,
            overviewText.ContextAutomation(OverviewContextText.Text));
        AutomationProperties.SetName(
            OverviewInfoBar,
            overviewText.ReadStateAutomation(
                presentation.InfoTitle,
                presentation.InfoMessage,
                presentation.IsInfoOpen));
        StaleNoticeText.Text = presentation.IsStale
            ? overviewText.StaleSnapshot
            : string.Empty;
        AutomationProperties.SetName(
            StaleNoticeText,
            overviewText.StaleAutomation(StaleNoticeText.Text, presentation.IsStale));
        SetNavigationAction(SeriesSummaryAction, presentation.SeriesNavigation);
        SetNavigationAction(ReadabilitySummaryAction, presentation.ReadabilityNavigation);
        SetNavigationAction(ErrorsSummaryAction, presentation.ErrorsNavigation);
        SetNavigationAction(AttentionSummaryAction, presentation.AttentionNavigation);
        SetNavigationAction(ReadableSummaryAction, state.Overview.Snapshot?.Readability.ReadableNavigation);
        SetNavigationAction(
            NotReadableSummaryAction,
            state.Overview.Snapshot?.Readability.NotReadableNavigation);
        SetNavigationAction(ActiveErrorsSummaryAction, state.Overview.Snapshot?.Errors.ActiveNavigation);
        SetNavigationAction(PriorErrorsSummaryAction, state.Overview.Snapshot?.Errors.Prior7DaysNavigation);
        RenderAttentionFacetSummary(
            state.Overview.Snapshot?.Attention,
            presentation.Protection);
        RenderRecentActivity(presentation);
        RenderHostFooter(state, presentation);
        RenderDataPageAreaProfileSelectors();
        RenderDemandSeries(state);
        RenderReadabilityAudit(state);
        RenderErrorSearch(state);
        RenderCurrentAttention(state);
        PublishSelectionFeedback(state);
    }

    private static void SetOverviewMetricState(TextBlock metric, bool isCritical) =>
        metric.SetResourceReference(
            TextBlock.ForegroundProperty,
            isCritical
                ? "SystemFillColorCriticalBrush"
                : "TextFillColorPrimaryBrush");

    private static void SetOverviewSubsummaryState(
        Control action,
        bool isEmphasized,
        string emphasizedBrush) =>
        action.SetResourceReference(
            Control.ForegroundProperty,
            isEmphasized
                ? emphasizedBrush
                : "TextFillColorTertiaryBrush");

    private string FormatConciseHostAreaScope(
        IReadOnlyList<string>? mesAreas,
        bool hasSnapshot) =>
        _displayLanguageState.Catalog.Overview.ConciseHostAreaScope(mesAreas, hasSnapshot);

    private void RenderDemandSeries(WatchV2WorkspaceState state)
    {
        var presentation = WatchDemandSeriesPresentation.Project(
            state,
            _demandSeriesQuery,
            _areaContext,
            _demandSeriesNavigation,
            _focusedDemandId,
            _displayLanguageState.Catalog.DemandSeries);
        _isRenderingDemandSeries = true;
        try
        {
            var text = _displayLanguageState.Catalog.DemandSeries;
            var common = _displayLanguageState.Catalog.Common;
            var demandSeriesFullContext = string.Join(
                " · ",
                new[]
                {
                    text.FormatLocalAreaContext(presentation.LocalAreaHeading),
                    presentation.LocalAreaDetail,
                    presentation.HostAreaScope,
                    presentation.SnapshotFacts,
                    presentation.ClientAttemptFacts,
                    text.FormatAutoRefresh(_preferences.RefreshIntervals.DemandSeries.IntervalSeconds),
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var conciseHostAreaScope = text.FormatConciseHostScope(
                state.DemandSeries.Snapshot?.Filter.Normalize().MesAreas,
                presentation.HasSnapshot);
            var demandSeriesFreshness = state.DemandSeries.LastSuccessfulAt is { } lastSuccessfulAt
                ? text.FormatLastSuccess(WatchTimeDisplay.Format(lastSuccessfulAt))
                : text.WaitingHost;
            DemandSeriesContextText.Text =
                $"{text.FormatLocalAreaContext(presentation.LocalAreaHeading)} · {conciseHostAreaScope} · {demandSeriesFreshness} · {text.FormatAutoRefresh(_preferences.RefreshIntervals.DemandSeries.IntervalSeconds)}";
            DemandSeriesContextText.ToolTip = demandSeriesFullContext;
            AutomationProperties.SetHelpText(
                DemandSeriesContextText,
                demandSeriesFullContext);
            AutomationProperties.SetName(
                DemandSeriesContextText,
                presentation.HasSnapshot
                    ? text.FormatContextName(demandSeriesFullContext)
                    : text.ContextAutomationName);
            var demandFacets = state.DemandSeries.Snapshot?.Facets;
            DemandSeriesTrackingFacetText.Text =
                $"{text.Tracking} {(demandFacets is null ? common.NotLoaded : $"{demandFacets.TrackingCount:N0}")}";
            DemandSeriesArchivedFacetText.Text =
                $"{text.Archived} {(demandFacets is null ? common.NotLoaded : $"{demandFacets.ArchivedCount:N0}")}";
            AutomationProperties.SetName(
                DemandSeriesTrackingFacetPill,
                text.FormatFacetName(DemandSeriesTrackingFacetText.Text));
            AutomationProperties.SetName(
                DemandSeriesArchivedFacetPill,
                text.FormatFacetName(DemandSeriesArchivedFacetText.Text));

            var showSource = presentation.SourceComparison
                != WatchDemandSeriesSourceComparison.None;
            var showDemandSeriesInfo = showSource;
            DemandSeriesInfoBar.IsOpen = showDemandSeriesInfo;
            DemandSeriesInfoExpander.Visibility = showDemandSeriesInfo
                ? Visibility.Visible
                : Visibility.Collapsed;
            DemandSeriesInfoBar.Severity = ToInfoBarSeverity(
                presentation.SourceComparisonSeverity);
            DemandSeriesInfoBar.Title = showSource ? text.SourceComparison : string.Empty;
            var infoParts = new[]
            {
                showSource ? presentation.SourceSnapshotSummary : null,
                showSource ? presentation.SourceComparisonMessage : null,
            }.Where(value => !string.IsNullOrWhiteSpace(value));
            DemandSeriesInfoBar.Message = string.Join(" ", infoParts);
            DemandSeriesInfoHeaderTitle.Text = DemandSeriesInfoBar.Title;
            DemandSeriesInfoHeaderSummary.Text = DemandSeriesInfoBar.Message;
            AutomationProperties.SetName(
                DemandSeriesInfoBar,
                DemandSeriesInfoBar.IsOpen
                    ? text.FormatInfoName(DemandSeriesInfoBar.Title, DemandSeriesInfoBar.Message)
                    : text.ReadStateAutomationName);
            AutomationProperties.SetName(
                DemandSeriesInfoExpander,
                showDemandSeriesInfo
                    ? text.FormatTopInfoName(text.FormatInfoName(DemandSeriesInfoBar.Title, DemandSeriesInfoBar.Message))
                    : text.TopInfoAutomationName);

            DemandSeriesPageSummaryText.Text = presentation.PageSummary;
            DemandSeriesOrderText.Text = presentation.OrderSummary;
            AutomationProperties.SetName(
                DemandSeriesPageSummaryText,
                presentation.HasSnapshot
                    ? text.FormatPageSummaryName(presentation.PageSummary)
                    : text.FormatPageSummaryName(string.Empty));
            DemandSeriesPreviousButton.IsEnabled = presentation.CanGoPrevious
                && !presentation.IsRefreshing;
            DemandSeriesNextButton.IsEnabled = presentation.CanGoNext
                && !presentation.IsRefreshing;
            var hasPages = state.DemandSeries.Snapshot is { TotalPages: > 0 };
            DemandSeriesGoToPageButton.IsEnabled = hasPages && !presentation.IsRefreshing;
            DemandSeriesPageNumberInput.IsEnabled = hasPages && !presentation.IsRefreshing;
            if (!DemandSeriesPageNumberInput.IsKeyboardFocusWithin)
            {
                DemandSeriesPageNumberInput.Text = state.DemandSeries.Snapshot?.TotalPages == 0
                    ? "0"
                    : (state.DemandSeries.Snapshot?.PageNumber ?? 1).ToString();
            }

            var selectedSeriesId = state.DemandSeries.SelectedId;
            DemandSeriesGrid.ItemsSource = presentation.Rows;
            var hasEmptyResult = presentation.HasSnapshot && presentation.Rows.Count == 0;
            DemandSeriesGrid.Visibility = hasEmptyResult
                ? Visibility.Collapsed
                : Visibility.Visible;
            DemandSeriesEmptyState.Visibility = hasEmptyResult
                ? Visibility.Visible
                : Visibility.Collapsed;
            DemandSeriesEmptyState.IsOpen = hasEmptyResult;
            DemandSeriesGrid.SelectedItem = presentation.Rows.FirstOrDefault(row => string.Equals(
                row.SeriesId,
                selectedSeriesId,
                StringComparison.Ordinal));
            AddObservedWorkTypes(presentation.Rows.Select(row => row.WorkType));

            OfferDemandSeriesAllAreasDialogIfNeeded();

            var inspectorPresentation = CreateDemandSeriesInspectorStatePresentation();
            DemandSeriesOpenInspectorButton.IsEnabled = inspectorPresentation is not null;
            DemandSeriesOpenInspectorButton.Content = _demandSeriesInspectorCoordinator.IsOpen
                ? _displayLanguageState.Catalog.DemandSeries.ShowInspector
                : _displayLanguageState.Catalog.DemandSeries.OpenInspector;
            AutomationProperties.SetName(
                DemandSeriesOpenInspectorButton,
                _demandSeriesInspectorCoordinator.IsOpen
                    ? _displayLanguageState.Catalog.DemandSeries.ShowInspector
                    : _displayLanguageState.Catalog.DemandSeries.OpenInspector);
            if (_demandSeriesInspectorCoordinator.IsOpen)
            {
                if (inspectorPresentation is null)
                {
                    _demandSeriesInspectorCoordinator.Clear();
                }
                else
                {
                    _demandSeriesInspectorCoordinator.Update(inspectorPresentation);
                }
            }

            UpdateDemandSeriesClearFiltersState();
        }
        finally
        {
            _isRenderingDemandSeries = false;
        }
    }

    private void AddObservedWorkTypes(IEnumerable<string> values)
    {
        var existing = DemandSeriesWorkTypeFilter.Items
            .OfType<ComboBoxItem>()
            .Select(item => item.Tag?.ToString())
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var value in values
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (existing.Add(value))
            {
                DemandSeriesWorkTypeFilter.Items.Add(new ComboBoxItem
                {
                    Content = _displayLanguageState.Catalog.DemandSeries.DescribeWorkType(value),
                    Tag = value,
                });
            }
        }
    }

    private void RenderAttentionFacetSummary(
        WatchOverviewAttentionSummary? attention,
        WatchProtectionStatusPresentation protection)
    {
        var text = _displayLanguageState.Catalog.Overview;
        var severitySummary = attention is null
            ? text.WaitingSeverityFacets
            : attention.Severities.Count == 0
                ? text.NoSeverityFacets
                : string.Join(
                    " · ",
                    attention.Severities.Select(facet =>
                        $"{facet.Count:N0} {text.AttentionFacet(facet.Value)}"));
        var summary = $"{protection.Status} · {severitySummary}";
        AttentionSummaryFacetText.Text = summary;
        AutomationProperties.SetName(
            AttentionSummaryFacetText,
            text.AttentionAutomation(protection.Status, protection.Detail, severitySummary));
        AutomationProperties.SetName(
            AttentionSummaryCard,
            text.HealthAutomation(protection.Status, protection.Detail));
    }

    private void RenderRecentActivity(WatchOverviewPresentation presentation)
    {
        RecentActivityItems.Children.Clear();
        if (!presentation.HasSnapshot)
        {
            var waiting = new Wpf.Ui.Controls.TextBlock
            {
                Text = _displayLanguageState.Catalog.Overview.WaitingForHost,
                FontTypography = Wpf.Ui.Controls.FontTypography.Body,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 13, 16, 0),
            };
            RecentActivityItems.Children.Add(waiting);
            return;
        }

        if (presentation.RecentActivity.Count == 0)
        {
            var empty = new Wpf.Ui.Controls.TextBlock
            {
                Text = _displayLanguageState.Catalog.Overview.RecentEmptyExplanation,
                FontTypography = Wpf.Ui.Controls.FontTypography.Body,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 13, 16, 0),
            };
            RecentActivityItems.Children.Add(empty);
            return;
        }

        foreach (var activity in presentation.RecentActivity)
        {
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = ActivitySymbol(activity.Navigation.Target),
                VerticalAlignment = VerticalAlignment.Top,
            };
            icon.SetResourceReference(
                Wpf.Ui.Controls.IconElement.ForegroundProperty,
                activity.Severity switch
                {
                    WatchPresentationSeverity.Error => "SystemFillColorCriticalBrush",
                    WatchPresentationSeverity.Warning => "SystemFillColorCautionBrush",
                    _ => "AccentTextFillColorPrimaryBrush",
                });
            content.Children.Add(icon);
            var text = new StackPanel();
            text.Children.Add(new Wpf.Ui.Controls.TextBlock
            {
                Text = activity.Heading,
                FontTypography = Wpf.Ui.Controls.FontTypography.BodyStrong,
                TextWrapping = TextWrapping.Wrap,
            });
            var detail = new Wpf.Ui.Controls.TextBlock
            {
                Text = activity.Detail,
                Style = (Style)FindResource("CaptionText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 8, 0),
            };
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);
            content.Children.Add(text);
            var occurredAt = new Wpf.Ui.Controls.TextBlock
            {
                Text = activity.OccurredAt,
                Style = (Style)FindResource("CaptionText"),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(occurredAt, 2);
            content.Children.Add(occurredAt);

            var action = new Wpf.Ui.Controls.Button
            {
                Content = content,
                Tag = activity.Navigation,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 13, 16, 13),
                Appearance = ControlAppearance.Transparent,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
            };
            AutomationProperties.SetName(
                action,
                _displayLanguageState.Catalog.Overview.OpenActivity(activity.Heading));
            action.Click += OnOverviewIntentClick;
            var row = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = action,
            };
            row.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
            RecentActivityItems.Children.Add(row);
        }
    }

    private static SymbolRegular ActivitySymbol(string target) => target switch
    {
        OverviewNavigationTargets.ErrorSearch => SymbolRegular.Warning24,
        OverviewNavigationTargets.ReadabilityAudit => SymbolRegular.DocumentBulletList24,
        OverviewNavigationTargets.DemandSeries or OverviewNavigationTargets.DemandSeriesDetail =>
            SymbolRegular.Timeline24,
        _ => SymbolRegular.Alert24,
    };

    private void RenderHostFooter(
        WatchV2WorkspaceState state,
        WatchOverviewPresentation overview)
    {
        var catalog = _displayLanguageState.Catalog;
        var shell = catalog.Shell;
        var latestViewFailure = new[]
            {
                state.Overview.LastFailureAt,
                state.DemandSeries.LastFailureAt,
                state.ReadabilityAudit.LastFailureAt,
                state.ErrorSearch.LastFailureAt,
                state.CurrentAttention.LastFailureAt,
                state.Protection.LastFailureAt,
            }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();
        var hasViewFailure = latestViewFailure != default;
        var hasProtectionIssue = overview.Protection.RequiresAttention;
        HostNavigationItem.Content = state.ConnectionStatus switch
        {
            WatchHostConnectionStatus.Connected when hasViewFailure && hasProtectionIssue =>
                $"{shell.HostConnected} · {overview.Protection.Status} · {shell.ReadFailed}",
            WatchHostConnectionStatus.Connected when hasViewFailure =>
                $"{shell.HostConnected} · {shell.ReadFailed}",
            WatchHostConnectionStatus.Connected when hasProtectionIssue =>
                $"{shell.HostConnected} · {overview.Protection.Status}",
            WatchHostConnectionStatus.Connected => shell.HostConnected,
            WatchHostConnectionStatus.Connecting => shell.HostConnecting,
            WatchHostConnectionStatus.Failed => shell.HostFailed,
            _ => shell.HostDisconnected,
        };
        HostNavigationIcon.Symbol = state.ConnectionStatus switch
        {
            WatchHostConnectionStatus.Connected when hasViewFailure && hasProtectionIssue =>
                SymbolRegular.CloudError24,
            WatchHostConnectionStatus.Connected when hasViewFailure => SymbolRegular.CloudError24,
            WatchHostConnectionStatus.Connected when hasProtectionIssue => SymbolRegular.CloudError24,
            WatchHostConnectionStatus.Connected => SymbolRegular.CloudCheckmark24,
            WatchHostConnectionStatus.Connecting => SymbolRegular.CloudSync24,
            WatchHostConnectionStatus.Failed => SymbolRegular.CloudDismiss24,
            _ => SymbolRegular.CloudOff24,
        };
        OverviewHostStatusText.Text =
            HostNavigationItem.Content?.ToString() ?? shell.HostDisconnected;
        OverviewHostStatusIcon.Symbol = HostNavigationIcon.Symbol;
        var hostStatusStyleKey = HostStatusStyleKey(
            state.ConnectionStatus,
            hasViewFailure,
            hasProtectionIssue,
            overview.Protection.Severity);
        OverviewHostStatusPill.Style = (Style)FindResource(hostStatusStyleKey);
        SettingsHostStatusPill.Style = (Style)FindResource(hostStatusStyleKey);
        SettingsHostStatusText.Text = OverviewHostStatusText.Text;
        SettingsHostStatusIcon.Symbol = HostNavigationIcon.Symbol;
        var (settingsIconBackground, settingsIconForeground) = state.ConnectionStatus switch
        {
            WatchHostConnectionStatus.Connected
                when hasProtectionIssue
                     && overview.Protection.Severity == WatchPresentationSeverity.Error =>
                ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            WatchHostConnectionStatus.Connected when hasViewFailure =>
                ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            WatchHostConnectionStatus.Connected when hasProtectionIssue =>
                ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            WatchHostConnectionStatus.Connected =>
                ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            WatchHostConnectionStatus.Connecting =>
                ("ControlFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
            WatchHostConnectionStatus.Failed =>
                ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            _ => ("ControlFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
        };
        SettingsHostStatusIconSurface.SetResourceReference(
            Border.BackgroundProperty,
            settingsIconBackground);
        SettingsHostStatusIcon.SetResourceReference(
            Wpf.Ui.Controls.IconElement.ForegroundProperty,
            settingsIconForeground);
        AutomationProperties.SetName(
            OverviewHostStatusPill,
            catalog.Overview.OverviewHostStatusAutomation(OverviewHostStatusText.Text));
        AutomationProperties.SetName(
            HostNavigationItem,
            catalog.Overview.HostNavigationAutomation(
                HostNavigationItem.Content?.ToString() ?? string.Empty,
                overview.Protection.Detail));
        var viewFailureDetail = hasViewFailure
            ? catalog.Overview.RecentPageReadFailure(latestViewFailure, catalog)
            : string.Empty;
        HostNavigationItem.ToolTip =
            $"{overview.HostDetail} · {overview.Protection.Status} · {overview.Protection.Detail}{viewFailureDetail}";
        SettingsHostStateText.Text = $"{HostNavigationItem.Content} · {overview.HostDetail}";
        AutomationProperties.SetName(
            SettingsHostStatusPill,
            catalog.Overview.SettingsHostStatusAutomation(SettingsHostStatusText.Text));
    }

    internal static string HostStatusStyleKey(
        WatchHostConnectionStatus connectionStatus,
        bool hasViewFailure,
        bool hasProtectionIssue,
        WatchPresentationSeverity protectionSeverity) => connectionStatus switch
    {
        WatchHostConnectionStatus.Connected
            when hasProtectionIssue
                 && protectionSeverity == WatchPresentationSeverity.Error =>
            "StatusPillCritical",
        WatchHostConnectionStatus.Connected when hasViewFailure => "StatusPillCaution",
        WatchHostConnectionStatus.Connected when hasProtectionIssue => "StatusPillCaution",
        WatchHostConnectionStatus.Connected => "StatusPillSuccess",
        WatchHostConnectionStatus.Connecting => "StatusPill",
        WatchHostConnectionStatus.Failed => "StatusPillCritical",
        _ => "StatusPill",
    };

    private static void SetNavigationAction(
        Wpf.Ui.Controls.Button button,
        OverviewNavigationIntent? intent)
    {
        button.Tag = intent;
        button.IsEnabled = intent is not null;
    }

    private void ApplyNavigationIntent(OverviewNavigationIntent intent)
    {
        switch (intent.Target)
        {
            case OverviewNavigationTargets.DemandSeries:
            case OverviewNavigationTargets.DemandSeriesDetail:
                _demandSeriesQuery = new DemandSeriesBrowseQuery(
                    new DemandSeriesBrowseFilter
                    {
                        Lifecycles = intent.Lifecycles ?? [],
                        CurrentPresences = intent.CurrentPresences ?? [],
                        WorkTypes = intent.WorkType is null ? [] : [intent.WorkType],
                        MesAreas = intent.MesAreas ?? [],
                        SeriesId = intent.SeriesId,
                    },
                    PageNumber: intent.PageNumber,
                    Cursor: intent.Cursor).NormalizeAndValidate();
                SyncDemandSeriesFilterControls(_demandSeriesQuery);
                NavigateTo(WatchWorkspacePage.DemandSeries);
                break;
            case OverviewNavigationTargets.ReadabilityAudit:
                _readabilityAuditQuery = new ReadabilityAuditQuery(
                    new ReadabilityAuditFilter
                    {
                        ReadabilityStates = intent.ReadabilityStates ?? [],
                        WorkTypes = intent.WorkType is null ? [] : [intent.WorkType],
                        MesAreas = intent.MesAreas ?? [],
                    },
                    PageNumber: intent.PageNumber,
                    Cursor: intent.Cursor).NormalizeAndValidate();
                SyncReadabilityFilterControls(_readabilityAuditQuery);
                NavigateTo(WatchWorkspacePage.ReadabilityAudit);
                break;
            case OverviewNavigationTargets.ErrorSearch:
                _errorSearchQuery = WatchErrorSearchQueries.StartLatest(
                    new ErrorSearchFilter
                    {
                        ActivityStates = intent.ErrorActivityStates ?? [],
                        SeriesId = intent.SeriesId,
                    },
                    intent.ErrorWindow is null
                        ? ErrorSearchWindowSelection.Last7Days
                        : new ErrorSearchWindowSelection(intent.ErrorWindow));
                _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
                SyncErrorSearchFilterControls(_errorSearchQuery);
                NavigateTo(WatchWorkspacePage.ErrorSearch);
                break;
            case OverviewNavigationTargets.CurrentIngestAttention:
                _currentAttentionQuery = WatchCurrentIngestAttentionQueries.FromNavigation(intent);
                SyncCurrentAttentionFilterControls(_currentAttentionQuery);
                NavigateTo(WatchWorkspacePage.CurrentAttention);
                break;
            case OverviewNavigationTargets.TaskTypeProtection:
                _currentAttentionQuery = WatchCurrentIngestAttentionQueries.StartLatest(
                    [CurrentIngestAttentionKinds.TaskTypeProtection],
                    intent.AttentionSeverities,
                    pageNumber: intent.PageNumber);
                SyncCurrentAttentionFilterControls(_currentAttentionQuery);
                NavigateTo(WatchWorkspacePage.CurrentAttention);
                break;
            case OverviewNavigationTargets.PollTrace:
                _currentAttentionQuery = WatchCurrentIngestAttentionQueries.StartLatest(
                    [CurrentIngestAttentionKinds.PollRunFailure],
                    intent.AttentionSeverities,
                    pageNumber: intent.PageNumber);
                SyncCurrentAttentionFilterControls(_currentAttentionQuery);
                NavigateTo(WatchWorkspacePage.CurrentAttention);
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported overview navigation target '{intent.Target}'.",
                    nameof(intent));
        }
    }

    private void NavigateTo(
        WatchWorkspacePage page,
        bool activateRefresh = true)
    {
        var pageChanged = _activePage != page;
        if (pageChanged)
        {
            _notificationCoordinator.ClearPage(_activePage);
            FlushAreaProfileAutoSave();
            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
        }

        _activePage = page;
        if (page is WatchWorkspacePage.AreaFilter)
        {
            StartWatchingAreaProfileDirectory();
        }
        else
        {
            _areaProfileStore.StopWatchingDirectory();
        }

        OverviewPage.Visibility = page == WatchWorkspacePage.Overview ? Visibility.Visible : Visibility.Collapsed;
        DemandSeriesPage.Visibility = page == WatchWorkspacePage.DemandSeries ? Visibility.Visible : Visibility.Collapsed;
        ReadabilityAuditPage.Visibility = page == WatchWorkspacePage.ReadabilityAudit ? Visibility.Visible : Visibility.Collapsed;
        ErrorSearchPage.Visibility = page == WatchWorkspacePage.ErrorSearch ? Visibility.Visible : Visibility.Collapsed;
        AreaFilterPage.Visibility = page == WatchWorkspacePage.AreaFilter ? Visibility.Visible : Visibility.Collapsed;
        CurrentAttentionPage.Visibility = page == WatchWorkspacePage.CurrentAttention ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == WatchWorkspacePage.Settings ? Visibility.Visible : Visibility.Collapsed;
        OverviewNavigationItem.IsActive = page == WatchWorkspacePage.Overview;
        DemandSeriesNavigationItem.IsActive = page == WatchWorkspacePage.DemandSeries;
        ReadabilityAuditNavigationItem.IsActive = page == WatchWorkspacePage.ReadabilityAudit;
        ErrorSearchNavigationItem.IsActive = page == WatchWorkspacePage.ErrorSearch;
        AreaFilterNavigationItem.IsActive = page == WatchWorkspacePage.AreaFilter;
        CurrentAttentionNavigationItem.IsActive = page == WatchWorkspacePage.CurrentAttention;
        SettingsNavigationItem.IsActive = page == WatchWorkspacePage.Settings;
        HostNavigationItem.IsActive = false;

        if (pageChanged)
        {
            PageViewport(page).ScrollToTop();
        }

        if (_demandSeriesInspectorCoordinator.IsOpen)
        {
            RenderDemandSeries(_session.State);
        }

        if (!activateRefresh
            || _session.State.ConnectionStatus != WatchHostConnectionStatus.Connected)
        {
            return;
        }

        switch (page)
        {
            case WatchWorkspacePage.Overview:
                _autoRefresh.ActivateOverview(_overviewQuery);
                break;
            case WatchWorkspacePage.DemandSeries:
                _autoRefresh.ActivateDemandSeries(_demandSeriesQuery);
                break;
            case WatchWorkspacePage.ReadabilityAudit:
                _autoRefresh.ActivateReadabilityAudit(_readabilityAuditQuery);
                break;
            case WatchWorkspacePage.ErrorSearch:
                _autoRefresh.ActivateErrorSearch(_errorSearchQuery);
                break;
            case WatchWorkspacePage.CurrentAttention:
                _autoRefresh.ActivateCurrentAttention(_currentAttentionQuery);
                break;
            case WatchWorkspacePage.AreaFilter:
            case WatchWorkspacePage.Settings:
                _autoRefresh.Deactivate();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(page), page, null);
        }
    }

    private ScrollViewer PageViewport(WatchWorkspacePage page) => page switch
    {
        WatchWorkspacePage.Overview => OverviewPage,
        WatchWorkspacePage.DemandSeries => DemandSeriesScrollViewer,
        WatchWorkspacePage.ReadabilityAudit => ReadabilityAuditPage,
        WatchWorkspacePage.ErrorSearch => ErrorSearchBodyScrollViewer,
        WatchWorkspacePage.AreaFilter => AreaFilterPage,
        WatchWorkspacePage.CurrentAttention => CurrentAttentionPage,
        WatchWorkspacePage.Settings => SettingsPage,
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
    };

    private void InitializeIntervalInputs()
    {
        ConfigureIntervalInput(OverviewIntervalInput, _preferences.RefreshIntervals.Overview.IntervalSeconds);
        ConfigureIntervalInput(DemandSeriesIntervalInput, _preferences.RefreshIntervals.DemandSeries.IntervalSeconds);
        ConfigureIntervalInput(ReadabilityAuditIntervalInput, _preferences.RefreshIntervals.ReadabilityAudit.IntervalSeconds);
        ConfigureIntervalInput(ErrorSearchIntervalInput, _preferences.RefreshIntervals.ErrorSearch.IntervalSeconds);
        ConfigureIntervalInput(CurrentAttentionIntervalInput, _preferences.RefreshIntervals.CurrentIngestAttention.IntervalSeconds);
    }

    private void InitializeDisplayLanguageInput()
    {
        DisplayLanguageInput.ItemsSource =
        new WatchDisplayLanguageChoice[]
        {
            new WatchDisplayLanguageChoice(
                WatchDisplayLanguage.SimplifiedChinese,
                WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese)
                    .Common.SimplifiedChineseLanguageName),
            new WatchDisplayLanguageChoice(
                WatchDisplayLanguage.English,
                WatchTextCatalog.For(WatchDisplayLanguage.English)
                    .Common.EnglishLanguageName),
        };
        DisplayLanguageInput.DisplayMemberPath = nameof(WatchDisplayLanguageChoice.Label);
        DisplayLanguageInput.SelectedValuePath = nameof(WatchDisplayLanguageChoice.Language);
    }

    private void ConfigureIntervalInput(ComboBox comboBox, int selectedSeconds)
    {
        comboBox.ItemsSource = WatchV2AutoRefreshSetting.AllowedIntervals
            .Select(seconds => new RefreshIntervalChoice(
                seconds,
                _displayLanguageState.Catalog.Settings.FormatSeconds(seconds)))
            .ToArray();
        comboBox.DisplayMemberPath = nameof(RefreshIntervalChoice.Label);
        comboBox.SelectedValuePath = nameof(RefreshIntervalChoice.Seconds);
        comboBox.SelectedValue = selectedSeconds;
    }

    private void PopulateSettingsInputs()
    {
        HostBaseUrlInput.Text = _currentHostSettings.BaseUrl;
        HostCredentialInput.Password = string.Empty;
        RequestTimeoutInput.Text = _currentHostSettings.RequestTimeoutSeconds.ToString();
        RememberWindowLayoutCheckBox.IsChecked = _preferences.Display.RememberWindowLayout;
        KeepNavigationPaneOpenCheckBox.IsChecked = _preferences.Display.IsNavigationPaneOpen;
        DisplayLanguageInput.SelectedValue = _preferences.DisplayLanguage;
    }

    private void ApplyDisplayPreferences(
        WatchV2DisplayPreferences display,
        bool restoreGeometry = true)
    {
        if (restoreGeometry)
        {
            var requested = display.RememberWindowLayout
                ? display.MainWindowLayout ?? new WatchWindowLayout(
                    double.NaN,
                    double.NaN,
                    display.WindowWidth,
                    display.WindowHeight,
                    MonitorDeviceName: null,
                    Maximized: false)
                : null;
            WatchWindowLayoutService.Apply(
                this,
                requested,
                WatchWindowLayoutTokens.Main);
        }

        WorkspaceNavigation.IsPaneOpen = display.IsNavigationPaneOpen;
    }

    private async void OnApplyHostClick(object sender, RoutedEventArgs e)
    {
        var settingsText = _displayLanguageState.Catalog.Settings;
        try
        {
            var credential = string.IsNullOrWhiteSpace(HostCredentialInput.Password)
                ? _currentHostSettings.Credential
                : HostCredentialInput.Password;
            if (!int.TryParse(RequestTimeoutInput.Text, out var timeoutSeconds)
                || timeoutSeconds is < 1 or > 300)
            {
                throw new ArgumentException(settingsText.TimeoutValidation);
            }

            var settings = new WatchHostSettings(
                HostBaseUrlInput.Text,
                credential,
                timeoutSeconds);
            var applyTask = ApplyHostAsync(settings, _lifetimeCancellation.Token);
            SettingsHostStateText.Text = settingsText.ValidatingHost;
            AutomationProperties.SetName(SettingsHostStateText, settingsText.ValidatingHost);
            if (!await applyTask.ConfigureAwait(true))
            {
                return;
            }

            PopulateSettingsInputs();
            if (_session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
            {
                PresentSettingsFeedback(
                    WatchNotificationSeverity.Success,
                    "host.apply",
                    WatchFeedbackText.HostAppliedTitle,
                    WatchFeedbackText.HostAppliedMessage);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the window is a neutral end to an in-flight apply.
        }
        catch (ArgumentException exception)
        {
            var validationMessage = settingsText.ApplyValidation(exception.Message);
            SettingsHostStateText.Text = validationMessage;
            AutomationProperties.SetName(SettingsHostStateText, validationMessage);
            AutomationProperties.SetHelpText(RequestTimeoutInput, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PresentSettingsFailure(
                "host.apply",
                WatchFeedbackText.HostFailedTitle,
                WatchFeedbackText.HostFailedMessage,
                ApplyHostButton);
        }
        finally
        {
            HostCredentialInput.Password = string.Empty;
        }
    }

    private void OnSaveLocalPreferencesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var refresh = new WatchV2AutoRefreshSettings(
                ReadInterval(OverviewIntervalInput),
                ReadInterval(DemandSeriesIntervalInput),
                ReadInterval(ReadabilityAuditIntervalInput),
                ReadInterval(ErrorSearchIntervalInput),
                ReadInterval(CurrentAttentionIntervalInput));
            var display = new WatchV2DisplayPreferences(
                RememberWindowLayoutCheckBox.IsChecked == true,
                Math.Max(MinWidth, Width),
                Math.Max(MinHeight, Height),
                KeepNavigationPaneOpenCheckBox.IsChecked == true);
            if (display.RememberWindowLayout)
            {
                var mainLayout = WatchWindowLayoutService.Capture(this);
                display = new WatchV2DisplayPreferences(
                    rememberWindowLayout: true,
                    windowWidth: mainLayout.Width,
                    windowHeight: mainLayout.Height,
                    isNavigationPaneOpen: display.IsNavigationPaneOpen,
                    mainWindowLayout: mainLayout,
                    inspectorWindowLayout: _preferences.Display.InspectorWindowLayout);
            }
            var hostGeneration = _session.State.HostGeneration;
            var displayLanguage = DisplayLanguageInput.SelectedValue
                is WatchDisplayLanguage selectedLanguage
                    ? selectedLanguage
                    : _preferences.DisplayLanguage;
            ApplyLocalPreferences(refresh, display, displayLanguage);
            if (_session.State.HostGeneration != hostGeneration)
            {
                throw new InvalidOperationException("Local preferences must not replace the Host session.");
            }

            PresentNotification(WatchNotificationEvent.CreateLocalized(
                new WatchNotificationSource(
                    "settings.local-preferences",
                    WatchNotificationScope.ForPage(WatchWorkspacePage.Settings),
                    "workspace-preferences"),
                WatchNotificationSeverity.Success,
                new WatchLocalizedNotificationContent(
                    WatchFeedbackText.SuccessSeverity,
                    WatchFeedbackText.SettingsSaved,
                    WatchFeedbackText.SettingsSavedDetail)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            PresentNotification(WatchNotificationEvent.CreateLocalized(
                new WatchNotificationSource(
                    "settings.local-preferences",
                    WatchNotificationScope.ForPage(WatchWorkspacePage.Settings),
                    "workspace-preferences"),
                WatchNotificationSeverity.Error,
                new WatchLocalizedNotificationContent(
                    WatchFeedbackText.ErrorSeverity,
                    WatchFeedbackText.SettingsSaveFailed,
                    WatchFeedbackText.SettingsSaveFailedDetail,
                    WatchFeedbackText.RetrySaveAction),
                () => SaveRefreshIntervalsButton.RaiseEvent(
                    new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent))));
        }
    }

    private void OnRestoreDefaultLayoutClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var hostGeneration = _session.State.HostGeneration;
            var defaultDisplay = WatchV2DisplayPreferences.Default;
            ApplyLocalPreferences(_preferences.RefreshIntervals, defaultDisplay);
            RememberWindowLayoutCheckBox.IsChecked = defaultDisplay.RememberWindowLayout;
            KeepNavigationPaneOpenCheckBox.IsChecked = defaultDisplay.IsNavigationPaneOpen;
            ApplyDisplayPreferences(defaultDisplay);
            if (_session.State.HostGeneration != hostGeneration)
            {
                throw new InvalidOperationException(
                    "Restoring the local layout must not replace the Host session.");
            }

            PresentSettingsFeedback(
                WatchNotificationSeverity.Success,
                "layout.restore-default",
                WatchFeedbackText.LayoutRestoredTitle,
                WatchFeedbackText.LayoutRestoredMessage);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            PresentSettingsFailure(
                "layout.restore-default",
                WatchFeedbackText.LayoutFailedTitle,
                WatchFeedbackText.LayoutFailedMessage,
                RestoreDefaultLayoutButton);
        }
    }

    private void OnRestoreDefaultSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaults = WatchV2Preferences.Default;
            var hostGeneration = _session.State.HostGeneration;
            ApplyLocalPreferences(
                defaults.RefreshIntervals,
                defaults.Display,
                defaults.DisplayLanguage);
            InitializeIntervalInputs();
            PopulateSettingsInputs();
            ApplyDisplayPreferences(defaults.Display);
            if (_session.State.HostGeneration != hostGeneration)
            {
                throw new InvalidOperationException("Restoring local defaults must not replace the Host session.");
            }

            PresentSettingsFeedback(
                WatchNotificationSeverity.Success,
                "settings.restore-default",
                WatchFeedbackText.DefaultsRestoredTitle,
                WatchFeedbackText.DefaultsRestoredMessage);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            PresentSettingsFailure(
                "settings.restore-default",
                WatchFeedbackText.DefaultsFailedTitle,
                WatchFeedbackText.DefaultsFailedMessage,
                RestoreDefaultSettingsButton);
        }
    }

    private static WatchV2AutoRefreshSetting ReadInterval(ComboBox comboBox) =>
        comboBox.SelectedValue is int seconds
            ? new WatchV2AutoRefreshSetting(seconds)
            : throw new ArgumentException("请选择 10、30、60 或 300 秒。");

    private void PresentSettingsFeedback(
        WatchNotificationSeverity severity,
        string identity,
        WatchLocalizedText title,
        WatchLocalizedText message) =>
        PresentNotification(WatchNotificationEvent.CreateLocalized(
            new WatchNotificationSource(
                "settings.operation",
                WatchNotificationScope.ForPage(WatchWorkspacePage.Settings),
                identity),
            severity,
            new WatchLocalizedNotificationContent(
                severity == WatchNotificationSeverity.Success
                    ? WatchFeedbackText.SuccessSeverity
                    : WatchFeedbackText.InformationSeverity,
                title,
                message)));

    private void PresentSettingsFailure(
        string identity,
        WatchLocalizedText title,
        WatchLocalizedText message,
        System.Windows.Controls.Primitives.ButtonBase retryButton) =>
        PresentNotification(WatchNotificationEvent.CreateLocalized(
            new WatchNotificationSource(
                "settings.operation",
                WatchNotificationScope.ForPage(WatchWorkspacePage.Settings),
                identity),
            WatchNotificationSeverity.Error,
            new WatchLocalizedNotificationContent(
                WatchFeedbackText.ErrorSeverity,
                title,
                message,
                WatchFeedbackText.RetryAction),
            () => retryButton.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent))));

    private void OnOverviewIntentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OverviewNavigationIntent intent })
        {
            NavigateFromOverview(intent);
        }
    }

    private async void OnDemandSeriesApplyFiltersClick(object sender, RoutedEventArgs e) =>
        await RunDemandSeriesUiActionAsync(async () =>
        {
            _demandSeriesNavigation = null;
            _focusedDemandId = null;
            var query = WatchDemandSeriesQueries.StartLatest(
                ReadDemandSeriesFilter(),
                ReadDemandSeriesPageSize());
            await RefreshLatestDemandSeriesAndRenderAsync(
                    query,
                    query.Filter.SeriesId,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnDemandSeriesClearFiltersClick(object sender, RoutedEventArgs e) =>
        await RunDemandSeriesUiActionAsync(async () =>
        {
            SetDemandSeriesLifecycleDraft(null);
            DemandSeriesPresenceFilter.SelectedIndex = 0;
            DemandSeriesWorkTypeFilter.SelectedIndex = 0;
            DemandSeriesSublotFilter.Clear();
            DemandSeriesSeriesIdFilter.Clear();
            DemandSeriesDemandIdFilter.Clear();
            DemandSeriesPageSizeFilter.SelectedIndex = 2;
            _demandSeriesNavigation = null;
            _focusedDemandId = null;
            var query = WatchDemandSeriesQueries.StartLatest(
                new DemandSeriesBrowseFilter { MesAreas = _areaContext.MesAreas },
                ReadDemandSeriesPageSize());
            await RefreshLatestDemandSeriesAndRenderAsync(
                    query,
                    desiredSeriesId: null,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnDemandSeriesPreviousClick(object sender, RoutedEventArgs e) =>
        await RunDemandSeriesUiActionAsync(async () =>
        {
            var snapshot = _session.State.DemandSeries.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的需求系列快照。");
            var targetPage = snapshot.PageNumber - 1;
            var request = WatchDemandSeriesQueries.OpenFrozenPage(snapshot, targetPage);
            await RefreshFrozenDemandSeriesAndRenderAsync(
                    request,
                    request,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnDemandSeriesNextClick(object sender, RoutedEventArgs e) =>
        await RunDemandSeriesUiActionAsync(async () =>
        {
            var snapshot = _session.State.DemandSeries.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的需求系列快照。");
            var request = WatchDemandSeriesQueries.OpenNextFrozenPage(snapshot)
                ?? throw new InvalidOperationException("当前冻结快照没有下一页。");
            var automaticRequest = WatchDemandSeriesQueries.OpenFrozenPage(
                snapshot,
                snapshot.PageNumber + 1);
            await RefreshFrozenDemandSeriesAndRenderAsync(
                    request,
                    automaticRequest,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnDemandSeriesGoToPageClick(object sender, RoutedEventArgs e) =>
        await RunDemandSeriesUiActionAsync(async () =>
        {
            var snapshot = _session.State.DemandSeries.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的需求系列快照。");
            if (!int.TryParse(DemandSeriesPageNumberInput.Text, out var pageNumber))
            {
                throw new ArgumentException("页码必须是整数。");
            }

            var request = WatchDemandSeriesQueries.OpenFrozenPage(snapshot, pageNumber);
            await RefreshFrozenDemandSeriesAndRenderAsync(
                    request,
                    request,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnDemandSeriesAllAreasConfirmClick(object sender, RoutedEventArgs e) =>
        await RunDemandSeriesUiActionAsync(() => ConfirmDemandSeriesAllAreasAsync(
            _lifetimeCancellation.Token)).ConfigureAwait(true);

    private async void OnDemandSeriesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingDemandSeries)
        {
            return;
        }

        await RunDemandSeriesUiActionAsync(async () =>
        {
            _demandSeriesNavigation = null;
            _focusedDemandId = null;
            var seriesId = (DemandSeriesGrid.SelectedItem as WatchDemandSeriesRowPresentation)?.SeriesId;
            await SelectDemandSeriesAndRenderAsync(
                    seriesId,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private WatchDemandSeriesInspectorStatePresentation?
        CreateDemandSeriesInspectorStatePresentation()
    {
        var view = _session.State.DemandSeries;
        var snapshot = view.Snapshot;
        var item = snapshot?.Items.FirstOrDefault(candidate => string.Equals(
            candidate.SeriesId,
            view.SelectedId,
            StringComparison.Ordinal));
        if (snapshot is null || item is null)
        {
            return null;
        }

        var retainedDetail = view.Detail is { } candidateDetail
            && string.Equals(
                item.SeriesId,
                candidateDetail.Series.SeriesId,
                StringComparison.Ordinal)
            ? candidateDetail
            : null;
        var detail = retainedDetail is null
            ? null
            : WatchDemandSeriesInspectorPresentation.Project(
                retainedDetail,
                view.DetailFocusId ?? _focusedDemandId,
                _displayLanguageState.Catalog.Inspector);
        var retainedPriorSnapshot = retainedDetail is not null
            && !DetailMatchesTargetSnapshot(retainedDetail, item.SeriesId, snapshot);
        var frozenSnapshot = detail?.FrozenSnapshot
            ?? new WatchDemandSeriesFrozenSnapshotPresentation(
                snapshot.SnapshotReference,
                snapshot.Snapshot.ProjectionCommitId,
                snapshot.Snapshot.ProjectionSequence,
                snapshot.Snapshot.ProjectionCommittedAt,
                snapshot.Snapshot.PollTraceId);
        var page = WatchDemandSeriesPresentation.Project(
            _session.State,
            _demandSeriesQuery,
            _areaContext,
            _demandSeriesNavigation,
            _focusedDemandId,
            _displayLanguageState.Catalog.DemandSeries);
        var inspectorText = _displayLanguageState.Catalog.Inspector;
        var statusParts = new[]
        {
            view.DetailErrorMessage,
            view.IsStale ? page.InfoMessage : null,
            retainedPriorSnapshot
                ? inspectorText.FormatRetainedSnapshot(
                    snapshot.SnapshotReference,
                    retainedDetail!.SnapshotReference)
                : null,
            page.SourceComparison == WatchDemandSeriesSourceComparison.None
                ? null
                : page.SourceSnapshotSummary,
            page.SourceComparison == WatchDemandSeriesSourceComparison.None
                ? null
                : page.SourceComparisonMessage,
        }.Where(value => !string.IsNullOrWhiteSpace(value));
        var isPaused = _activePage != WatchWorkspacePage.DemandSeries;
        var statusTitle = view.DetailLastFailureAt is not null
            ? detail is null
                ? inspectorText.DetailReadFailed
                : inspectorText.DetailRefreshFailed
            : isPaused
                ? inspectorText.PagePaused
                : retainedPriorSnapshot
                    ? view.IsDetailLoading
                        ? inspectorText.RefreshingRetained
                        : inspectorText.SnapshotPending
                    : view.IsStale
                        ? inspectorText.RefreshFailed
                        : view.IsDetailLoading
                        ? inspectorText.ReadingSelection
                        : page.SourceComparison != WatchDemandSeriesSourceComparison.None
                            ? inspectorText.SourceComparison
                            : string.Empty;
        var statusMessage = string.Join(" ", statusParts);
        if (isPaused)
        {
            statusMessage = string.Join(
                " ",
                new[]
                {
                    inspectorText.FormatPausedSnapshot(snapshot.SnapshotReference),
                    statusMessage,
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return new WatchDemandSeriesInspectorStatePresentation(
            item.SeriesId,
            detail?.WorkType ?? item.WorkType,
            detail?.Sublot ?? item.Sublot,
            detail?.Lifecycle ?? item.Lifecycle,
            detail?.CurrentPresence ?? item.CurrentPresence,
            frozenSnapshot,
            detail,
            view.IsDetailLoading,
            retainedPriorSnapshot || view.IsStale || view.DetailLastFailureAt is not null,
            isPaused,
            retainedPriorSnapshot || view.DetailLastFailureAt is not null || view.IsStale
                ? detail is null
                    ? WatchPresentationSeverity.Error
                    : WatchPresentationSeverity.Warning
                : page.SourceComparisonSeverity,
            statusTitle,
            statusMessage);
    }

    private static bool DetailMatchesTargetSnapshot(
        DemandSeriesDetailSnapshot? detail,
        string seriesId,
        DemandSeriesListSnapshot? snapshot) =>
        detail is not null
        && snapshot is not null
        && string.Equals(detail.Series.SeriesId, seriesId, StringComparison.Ordinal)
        && string.Equals(
            detail.SnapshotReference,
            snapshot.SnapshotReference,
            StringComparison.Ordinal);

    private void OnDemandSeriesOpenInspectorClick(object sender, RoutedEventArgs e) =>
        OpenOrShowDemandSeriesInspector();

    private void OnDemandSeriesGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = OpenOrShowDemandSeriesInspector();
    }

    private void OnDemandSeriesGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            e.Handled = OpenOrShowDemandSeriesInspector();
        }
    }

    private bool OpenOrShowDemandSeriesInspector()
    {
        var presentation = CreateDemandSeriesInspectorStatePresentation();
        if (presentation is null)
        {
            return false;
        }

        if (presentation.Detail is null && !presentation.IsLoading)
        {
            var load = _session.LoadSelectedDemandSeriesDetailAsync(
                _lifetimeCancellation.Token);
            RenderDemandSeries(_session.State);
            presentation = CreateDemandSeriesInspectorStatePresentation()
                ?? presentation;
            DemandSeriesInspectorLoadTask = CompleteDemandSeriesInspectorLoadAsync(
                presentation.SeriesId,
                load,
                _lifetimeCancellation.Token);
        }
        else
        {
            DemandSeriesInspectorLoadTask = Task.CompletedTask;
        }

        _demandSeriesInspectorCoordinator.OpenOrShow(presentation);
        RenderDemandSeries(_session.State);

        return true;
    }

    private async Task CompleteDemandSeriesInspectorLoadAsync(
        string seriesId,
        Task load,
        CancellationToken cancellationToken)
    {
        await load.ConfigureAwait(true);
        if (_disposed
            || cancellationToken.IsCancellationRequested
            || !_demandSeriesInspectorCoordinator.IsOpen
            || !string.Equals(
                _session.State.DemandSeries.SelectedId,
                seriesId,
                StringComparison.Ordinal))
        {
            return;
        }

        RenderDemandSeries(_session.State);
    }

    private void OnDemandSeriesInspectorStateChanged(object? sender, EventArgs e)
    {
        if (!_demandSeriesInspectorCoordinator.IsOpen && !_disposed)
        {
            _focusedDemandId = null;
            _session.CancelDemandSeriesDetail();
        }

        if (!_disposed)
        {
            RenderDemandSeries(_session.State);
        }
    }

    private void OnDemandSeriesInspectorGenerationFocusRequested(
        object? sender,
        WatchDemandSeriesGenerationFocusRequestedEventArgs e)
    {
        _focusedDemandId = e.DemandId;
        _session.SetDemandSeriesFocus(e.DemandId);
        RenderDemandSeries(_session.State);
    }

    private async Task RunDemandSeriesUiActionAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the window is a neutral end to an in-flight action.
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or DemandSeriesBrowseException)
        {
            PresentOperationFailure(
                WatchWorkspacePage.DemandSeries,
                "demand-series.operation",
                WatchFeedbackText.DemandSeriesOperationTitle,
                WatchFeedbackText.OperationRetryMessage,
                WatchFeedbackText.DemandSeriesOperationAction);
        }
    }

    private void OnOverviewNavigationClick(object sender, RoutedEventArgs e) =>
        NavigateTo(WatchWorkspacePage.Overview);

    private async void OnDemandSeriesNavigationClick(object sender, RoutedEventArgs e)
    {
        NavigateTo(WatchWorkspacePage.DemandSeries);
        if (_session.State.ConnectionStatus != WatchHostConnectionStatus.Connected)
        {
            return;
        }

        DemandSeriesNavigationTask = LoadDemandSeriesNavigationAsync(
            _demandSeriesQuery.Filter.SeriesId,
            _lifetimeCancellation.Token,
            openInspector: false);
        await DemandSeriesNavigationTask.ConfigureAwait(true);
    }

    private async void OnReadabilityAuditNavigationClick(object sender, RoutedEventArgs e)
    {
        NavigateTo(WatchWorkspacePage.ReadabilityAudit);
        if (_session.State.ConnectionStatus != WatchHostConnectionStatus.Connected)
        {
            return;
        }

        ReadabilityAuditNavigationTask = LoadReadabilityAuditNavigationAsync(
            _lifetimeCancellation.Token);
        await ReadabilityAuditNavigationTask.ConfigureAwait(true);
    }

    private void OnErrorSearchNavigationClick(object sender, RoutedEventArgs e)
    {
        _errorSearchQuery = WatchErrorSearchQueries.StartLatest(
            _errorSearchQuery.Filter,
            _errorSearchQuery.Window,
            _errorSearchQuery.PageSize);
        _errorRawEvidence = WatchErrorRawEvidenceState.Empty;
        SyncErrorSearchFilterControls(_errorSearchQuery);
        NavigateTo(WatchWorkspacePage.ErrorSearch);
        ErrorSearchNavigationTask = _session.State.ConnectionStatus
            == WatchHostConnectionStatus.Connected
                ? LoadErrorSearchNavigationAsync(_lifetimeCancellation.Token)
                : Task.CompletedTask;
    }

    private void OnAreaFilterNavigationClick(object sender, RoutedEventArgs e) =>
        NavigateToAreaProfiles();

    private void NavigateToAreaProfiles()
    {
        NavigateTo(WatchWorkspacePage.AreaFilter);
        RenderAreaProfiles(reloadProfiles: true);
    }

    private void OnCurrentAttentionNavigationClick(object sender, RoutedEventArgs e)
    {
        _currentAttentionQuery = WatchCurrentIngestAttentionQueries.StartLatest(
            _currentAttentionQuery.Kinds,
            _currentAttentionQuery.Severities,
            _currentAttentionQuery.PageSize);
        SyncCurrentAttentionFilterControls(_currentAttentionQuery);
        NavigateTo(WatchWorkspacePage.CurrentAttention);
        CurrentAttentionNavigationTask = _session.State.ConnectionStatus
            == WatchHostConnectionStatus.Connected
                ? LoadCurrentAttentionNavigationAsync(_lifetimeCancellation.Token)
                : Task.CompletedTask;
    }

    private void OnSettingsNavigationClick(object sender, RoutedEventArgs e) =>
        NavigateTo(WatchWorkspacePage.Settings);

    private void OnWorkspaceNavigationLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.NavigationView navigation)
        {
            return;
        }

        ApplyLocalizedNavigationToggle(navigation);
    }

    private void ApplyLocalizedNavigationToggle(Wpf.Ui.Controls.NavigationView navigation)
    {
        navigation.ApplyTemplate();
        var toggle = FindVisualDescendant<FrameworkElement>(
            navigation,
            element => string.Equals(
                AutomationProperties.GetAutomationId(element),
                "NavigationToggleButton",
                StringComparison.Ordinal));
        if (toggle is null)
        {
            return;
        }

        var shell = _displayLanguageState.Catalog.Shell;
        var accessibleName = shell.NavigationToggle;
        AutomationProperties.SetName(toggle, accessibleName);
        AutomationProperties.SetHelpText(
            toggle,
            shell.NavigationToggleHelp);
        ToolTipService.SetToolTip(toggle, accessibleName);
    }

    private static T? FindVisualDescendant<T>(
        DependencyObject root,
        Predicate<T> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate && predicate(candidate))
            {
                return candidate;
            }

            var descendant = FindVisualDescendant(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ActualWidth < 900 && WorkspaceNavigation.IsPaneOpen)
        {
            WorkspaceNavigation.IsPaneOpen = false;
        }

        ReflowResponsiveWorkspace();
    }

    private void OnWorkspaceContentSizeChanged(object sender, SizeChangedEventArgs e) =>
        ReflowResponsiveWorkspace();

    private void OnWorkspaceNavigationPaneStateChanged(object sender, RoutedEventArgs e) =>
        ReflowResponsiveWorkspace();

    private void ReflowResponsiveWorkspace()
    {
        var navigationWidth = WorkspaceNavigation.ActualWidth > 0
            ? WorkspaceNavigation.ActualWidth
            : ActualWidth;
        var paneWidth = WorkspaceNavigation.IsPaneOpen
            ? WorkspaceNavigation.OpenPaneLength
            : WorkspaceNavigation.CompactPaneLength;
        var contentWidth = Math.Max(0, navigationWidth - paneWidth - 48);
        var contentHeight = WorkspaceContent.ActualHeight > 0
            ? WorkspaceContent.ActualHeight
            : Math.Max(
                0,
                WorkspaceNavigation.ActualHeight
                    - WorkspaceContent.Margin.Top
                    - WorkspaceContent.Margin.Bottom);
        var useOuterScrolling = contentHeight > 0
            && contentHeight < MinimumFixedPageViewportHeight;

        OverviewSummaryCards.Columns = contentWidth >= 1160 ? 5 : contentWidth >= 760 ? 3 : 2;
        OverviewSummaryRow.Height = contentWidth >= 1160
            ? new GridLength(174)
            : GridLength.Auto;
        var stackBody = contentWidth < 900;
        Grid.SetColumn(OverviewFactsCard, stackBody ? 0 : 2);
        Grid.SetRow(OverviewFactsCard, stackBody ? 2 : 0);
        OverviewBodyGap.Width = stackBody ? new GridLength(0) : new GridLength(12);
        FactsColumn.Width = stackBody ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        OverviewBodyVerticalGap.Height = stackBody ? new GridLength(16) : new GridLength(0);
        OverviewFactsRow.Height = stackBody ? GridLength.Auto : new GridLength(0);

        var stackSettings = contentWidth < 940;
        Grid.SetColumn(RefreshSettingsCard, stackSettings ? 0 : 2);
        Grid.SetRow(RefreshSettingsCard, stackSettings ? 2 : 0);
        SettingsGapColumn.Width = stackSettings ? new GridLength(0) : new GridLength(16);
        SettingsRightColumn.Width = stackSettings ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SettingsVerticalGap.Height = stackSettings ? new GridLength(16) : new GridLength(0);
        SettingsBottomRow.Height = stackSettings ? GridLength.Auto : new GridLength(0);

        ReflowDemandSeries(
            useOuterScrolling);

        var stackReadability = contentWidth
            < (double)FindResource("ReadabilityMasterDetailStackBreakpoint");
        ReflowMasterDetail(
            stackReadability,
            ReadabilityDetailRegion,
            ReadabilityMasterColumn,
            ReadabilityBodyGapColumn,
            ReadabilityDetailColumn,
            ReadabilityBodyVerticalGap,
            ReadabilityBodyBottomRow,
            (GridLength)FindResource("ReadabilityMasterColumnWidth"),
            (GridLength)FindResource("ReadabilityMasterDetailGapWidth"));
        ConfigureResponsivePageViewport(
            ReadabilityAuditLayoutGrid,
            ReadabilityAuditPage,
            stackReadability || useOuterScrolling);

        var stackArea = contentWidth
            < (double)FindResource("AreaProfileMasterDetailStackBreakpoint");
        ReflowMasterDetail(
            stackArea,
            AreaProfileEditorCard,
            AreaProfileMasterColumn,
            AreaProfileGapColumn,
            AreaProfileEditorColumn,
            AreaProfileVerticalGap,
            AreaProfileBottomRow,
            (GridLength)FindResource("AreaProfileMasterColumnWidth"),
            (GridLength)FindResource("WatchMasterDetailGapWidth"));
        ConfigureResponsivePageViewport(
            AreaFilterLayoutGrid,
            AreaFilterPage,
            stackArea || useOuterScrolling);
        ReflowTicket22Pages(contentWidth, useOuterScrolling);
        WorkspaceContent.Margin = contentWidth < 760
            ? new Thickness(12)
            : new Thickness(24, 16, 24, 16);
        ReflowNotificationOverlay(contentWidth);
    }

    private void ReflowDemandSeries(bool useOuterScrolling) =>
        ConfigureResponsivePageViewport(
            DemandSeriesLayoutGrid,
            DemandSeriesScrollViewer,
            useOuterScrolling);

    private static void ConfigureResponsivePageViewport(
        FrameworkElement pageLayout,
        ScrollViewer viewport,
        bool useOuterScrolling)
    {
        if (useOuterScrolling)
        {
            BindingOperations.ClearBinding(pageLayout, HeightProperty);
            pageLayout.Height = double.NaN;
            viewport.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            return;
        }

        pageLayout.SetBinding(
            HeightProperty,
            new Binding(nameof(FrameworkElement.ActualHeight))
            {
                Source = viewport,
                Mode = BindingMode.OneWay,
            });
        viewport.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        viewport.ScrollToTop();
    }

    private void ReflowMasterDetail(
        bool stack,
        UIElement detailCard,
        ColumnDefinition masterColumn,
        ColumnDefinition gapColumn,
        ColumnDefinition detailColumn,
        RowDefinition verticalGap,
        RowDefinition bottomRow,
        GridLength expandedMasterWidth,
        GridLength gap)
    {
        var collapsed = (GridLength)FindResource("WatchCollapsedGridLength");
        Grid.SetColumn(detailCard, stack ? 0 : 2);
        Grid.SetRow(detailCard, stack ? 2 : 0);
        masterColumn.Width = stack
            ? new GridLength(1, GridUnitType.Star)
            : expandedMasterWidth;
        gapColumn.Width = stack ? collapsed : gap;
        detailColumn.Width = stack
            ? collapsed
            : new GridLength(1, GridUnitType.Star);
        verticalGap.Height = stack ? gap : collapsed;
        bottomRow.Height = stack ? GridLength.Auto : collapsed;
    }

    private void OnWindowTitleBarLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TitleBar titleBar)
        {
            return;
        }

        titleBar.ApplyTemplate();
        var shell = _displayLanguageState.Catalog.Shell;
        ConfigureTitleBarButton(titleBar, "PART_MinimizeButton", shell.MinimizeWindow);
        ConfigureTitleBarButton(titleBar, "PART_CloseButton", shell.CloseWindow);
        UpdateMaximizeButtonAccessibility(titleBar);
        StateChanged -= OnWindowStateChangedForTitleBar;
        StateChanged += OnWindowStateChangedForTitleBar;
    }

    private void OnWindowStateChangedForTitleBar(object? sender, EventArgs e) =>
        UpdateMaximizeButtonAccessibility(WindowTitleBar);

    private void UpdateMaximizeButtonAccessibility(TitleBar titleBar) =>
        ConfigureTitleBarButton(
            titleBar,
            "PART_MaximizeButton",
            WindowState == WindowState.Maximized
                ? _displayLanguageState.Catalog.Shell.RestoreWindow
                : _displayLanguageState.Catalog.Shell.MaximizeWindow);

    private static void ConfigureTitleBarButton(
        TitleBar titleBar,
        string partName,
        string automationName)
    {
        if (titleBar.Template?.FindName(partName, titleBar) is not TitleBarButton button)
        {
            return;
        }

        button.Focusable = true;
        KeyboardNavigation.SetIsTabStop(button, true);
        AutomationProperties.SetName(button, automationName);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        SaveWindowGeometry();
        DisposeCore();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        FlushAreaProfileAutoSave();
        _demandSeriesInspectorCoordinator.CloseCurrent();
        if (_isWatchingSystemTheme)
        {
            Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(this);
            _isWatchingSystemTheme = false;
        }
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(
            this,
            Wpf.Ui.Controls.WindowBackdropType.Mica,
            updateAccents: true);
        _isWatchingSystemTheme = true;
    }

    private void SaveWindowGeometry()
    {
        if (!_preferences.Display.RememberWindowLayout)
        {
            return;
        }

        try
        {
            var layout = WatchWindowLayoutService.Capture(this);
            var display = new WatchV2DisplayPreferences(
                rememberWindowLayout: true,
                windowWidth: Math.Max(MinWidth, layout.Width),
                windowHeight: Math.Max(MinHeight, layout.Height),
                isNavigationPaneOpen: WorkspaceNavigation.IsPaneOpen,
                mainWindowLayout: layout,
                inspectorWindowLayout: _preferences.Display.InspectorWindowLayout);
            _preferences = _preferences with { Display = display };
            WatchV2PreferencesStore.Save(_workspacePreferencesPath, _preferences);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            // A display preference failure must not block a clean window close.
        }
    }

    private WatchWindowLayout? LoadInspectorWindowLayout() =>
        _preferences.Display.RememberWindowLayout
            ? _preferences.Display.InspectorWindowLayout
            : null;

    private void SaveInspectorWindowLayout(WatchWindowLayout layout)
    {
        if (!_preferences.Display.RememberWindowLayout)
        {
            return;
        }

        try
        {
            _preferences = _preferences with
            {
                Display = _preferences.Display.WithInspectorWindowLayout(layout),
            };
            WatchV2PreferencesStore.Save(_workspacePreferencesPath, _preferences);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            // Layout persistence must never prevent a window from closing.
        }
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _demandSeriesOperationGeneration);
        Interlocked.Increment(ref _readabilityAuditOperationGeneration);
        Interlocked.Increment(ref _areaProfileOperationGeneration);
        Interlocked.Increment(ref _errorSearchOperationGeneration);
        Interlocked.Increment(ref _currentAttentionOperationGeneration);
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        _displayLanguageState.Changed -= OnDisplayLanguageChanged;
        _autoRefresh.RefreshStateChanged -= OnAutoRefreshStateChanged;
        _demandSeriesInspectorCoordinator.StateChanged -= OnDemandSeriesInspectorStateChanged;
        _demandSeriesInspectorCoordinator.GenerationFocusRequested -=
            OnDemandSeriesInspectorGenerationFocusRequested;
        WorkspaceContent.SizeChanged -= OnWorkspaceContentSizeChanged;
        WorkspaceNavigation.PaneOpened -= OnWorkspaceNavigationPaneStateChanged;
        WorkspaceNavigation.PaneClosed -= OnWorkspaceNavigationPaneStateChanged;
        _areaProfileStore.DirectoryChanged -= OnAreaProfileDirectoryChanged;
        _areaProfileStore.DirectoryWatchStateChanged -=
            OnAreaProfileDirectoryWatchStateChanged;
        DisposeAreaProfileAutoSave();
        DisposeFeedbackLifecycle();
        DisposeNotifications();
        _areaProfileStore.Dispose();
        _lifetimeCancellation.Cancel();
        _demandSeriesInspectorCoordinator.Dispose();
        _autoRefresh.Dispose();
        _session.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private static InfoBarSeverity ToInfoBarSeverity(WatchPresentationSeverity severity) =>
        severity switch
        {
            WatchPresentationSeverity.Success => InfoBarSeverity.Success,
            WatchPresentationSeverity.Warning => InfoBarSeverity.Warning,
            WatchPresentationSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };

    private sealed record RefreshIntervalChoice(int Seconds, string Label);
}
