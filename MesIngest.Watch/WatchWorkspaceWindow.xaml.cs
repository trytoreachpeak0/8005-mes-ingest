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
        IWatchAreaProfileDirectoryLauncher? areaProfileDirectoryLauncher = null)
    {
        _currentHostSettings = initialHostSettings
            ?? throw new ArgumentNullException(nameof(initialHostSettings));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _connectionPreferencesPath = Path.GetFullPath(connectionPreferencesPath);
        _workspacePreferencesPath = Path.GetFullPath(workspacePreferencesPath);
        _session = new WatchV2WorkspaceSession(clientFactory, timeProvider);
        _autoRefresh = new WatchV2AutoRefreshCoordinator(
            _session,
            preferences.RefreshIntervals,
            timeProvider);
        InitializeAreaFilterProfiles(
            areaFilterProfilesDirectoryPath,
            timeProvider,
            areaProfileDirectoryLauncher);

        InitializeComponent();
        InitializeDemandSeriesPage();
        InitializeReadabilityAuditAndAreaProfiles();
        InitializeDataPageAreaProfileSelectors();
        InitializeTicket22Pages();
        WorkspaceContent.SizeChanged += OnWorkspaceContentSizeChanged;
        WorkspaceNavigation.PaneOpened += OnWorkspaceNavigationPaneStateChanged;
        WorkspaceNavigation.PaneClosed += OnWorkspaceNavigationPaneStateChanged;
        if (Application.Current is null)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);
        }
        InitializeIntervalInputs();
        ApplyDisplayPreferences(preferences.Display);
        PopulateSettingsInputs();
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

    internal WatchV2WorkspaceState WorkspaceState => _session.State;

    internal WatchV2AutoRefreshSettings AutoRefreshSettings => _autoRefresh.Settings;

    internal WatchAreaDisplayContext AreaContext => _areaContext;

    internal WatchWorkspacePage ActivePage => _activePage;

    internal OverviewNavigationIntent? LastOverviewNavigationIntent { get; private set; }

    internal Task InitializationTask { get; private set; } = Task.CompletedTask;

    internal Task DemandSeriesNavigationTask { get; private set; } = Task.CompletedTask;

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

        if (_session.State.ConnectionStatus != WatchHostConnectionStatus.Connected)
        {
            return;
        }

        var overviewRefresh = _session.RefreshOverviewAsync(_overviewQuery, cancellationToken);
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
        WatchV2DisplayPreferences display)
    {
        ArgumentNullException.ThrowIfNull(refreshIntervals);
        ArgumentNullException.ThrowIfNull(display);
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var view in Enum.GetValues<WatchV2DataView>())
        {
            _autoRefresh.Update(view, refreshIntervals.For(view));
        }

        _preferences = new WatchV2Preferences(_autoRefresh.Settings, display);
        WatchV2PreferencesStore.Save(_workspacePreferencesPath, _preferences);
        ApplyDisplayPreferences(display, restoreGeometry: false);
        RenderWorkspace();
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
                _lifetimeCancellation.Token);
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
                cancellationToken)
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
        var refresh = _session.RefreshOverviewAsync(_overviewQuery, cancellationToken);
        RenderWorkspace();
        await refresh.ConfigureAwait(true);
        RenderWorkspace();
    }

    private void InitializeDemandSeriesPage()
    {
        WatchGridClipboardBehavior.Attach(DemandSeriesGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(DemandSeriesGenerationGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(DemandSeriesRawObservationGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(DemandSeriesConditionGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(DemandSeriesErrorPeriodGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(DemandSeriesErrorEvidenceGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(DemandSeriesEventGrid, preserveSelectionUnit: true);
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
        CancellationToken cancellationToken)
    {
        try
        {
            await RefreshLatestDemandSeriesAndRenderAsync(
                    _demandSeriesQuery,
                    desiredSeriesId,
                    cancellationToken)
                .ConfigureAwait(true);
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
            if (!string.IsNullOrWhiteSpace(desiredSeriesId)
                && committed.Items.Any(item => string.Equals(
                    item.SeriesId,
                    desiredSeriesId,
                    StringComparison.Ordinal)))
            {
                await SelectDemandSeriesAndRenderAsync(
                        desiredSeriesId,
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

        var selection = _session.SelectDemandSeriesAsync(seriesId, cancellationToken);
        RenderWorkspace();
        await selection.ConfigureAwait(true);
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
            ? comboBox.Text
            : comboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString()
            : comboBox.Text;
        value = ReadDemandSeriesText(value);
        return value is null || value.StartsWith("全部", StringComparison.Ordinal)
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
            if (string.Equals(candidate.Content?.ToString(), value, StringComparison.Ordinal))
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

    private static void SetSegmentSelectionStatus(
        Wpf.Ui.Controls.Button button,
        bool isSelected) =>
        AutomationProperties.SetItemStatus(
            button,
            isSelected ? "已选择" : "未选择");

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

        if (Dispatcher.CheckAccess())
        {
            RenderWorkspace();
        }
        else
        {
            _ = Dispatcher.BeginInvoke((Action)RenderWorkspace);
        }
    }

    private void RenderWorkspace()
    {
        if (_disposed)
        {
            return;
        }

        var state = _session.State;
        var presentation = WatchOverviewPresentation.Project(state, _areaContext);
        OverviewContextText.Text =
            $"{presentation.SnapshotFacts} · {presentation.ClientAttemptFacts} · 自动刷新 {_preferences.RefreshIntervals.Overview.IntervalSeconds} 秒";
        OverviewInfoBar.IsOpen = presentation.IsInfoOpen;
        OverviewInfoBar.Severity = ToInfoBarSeverity(presentation.InfoSeverity);
        OverviewInfoBar.Title = presentation.InfoTitle;
        OverviewInfoBar.Message = presentation.InfoMessage;
        SeriesSummaryValue.Text = presentation.SeriesValue;
        SeriesSummaryDetail.Text = presentation.SeriesDetail;
        ReadabilitySummaryValue.Text = presentation.ReadabilityValue;
        ReadabilitySummaryDetail.Text = presentation.ReadabilityDetail;
        ErrorsSummaryValue.Text = presentation.ErrorsValue;
        ErrorsSummaryDetail.Text = presentation.ErrorsDetail;
        AttentionSummaryValue.Text = presentation.AttentionValue;
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
            $"概览快照、客户端读取时间与自动刷新策略：{OverviewContextText.Text}");
        AutomationProperties.SetName(
            OverviewInfoBar,
            presentation.IsInfoOpen
                ? $"{presentation.InfoTitle}。{presentation.InfoMessage}"
                : "概览读取状态：当前无活动通知");
        StaleNoticeText.Text = presentation.IsStale
            ? "数据可能已过期；卡片仍属于上方标明的 Host 已提交范围。"
            : string.Empty;
        AutomationProperties.SetName(
            StaleNoticeText,
            presentation.IsStale ? StaleNoticeText.Text : "概览数据未标记为陈旧");
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
        RenderAttentionFacetSummary(state.Overview.Snapshot?.Attention);
        RenderRecentActivity(presentation);
        RenderHostFooter(state, presentation);
        RenderDataPageAreaProfileSelectors();
        RenderDemandSeries(state);
        RenderReadabilityAudit(state);
        RenderErrorSearch(state);
        RenderCurrentAttention(state);
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

    private void RenderDemandSeries(WatchV2WorkspaceState state)
    {
        var presentation = WatchDemandSeriesPresentation.Project(
            state,
            _demandSeriesQuery,
            _areaContext,
            _demandSeriesNavigation,
            _focusedDemandId);
        _isRenderingDemandSeries = true;
        try
        {
            DemandSeriesContextText.Text = string.Join(
                " · ",
                new[]
                {
                    $"本机 AREA：{presentation.LocalAreaHeading}",
                    presentation.HostAreaScope,
                    presentation.SnapshotFacts,
                    presentation.ClientAttemptFacts,
                    $"自动刷新 {_preferences.RefreshIntervals.DemandSeries.IntervalSeconds} 秒",
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            AutomationProperties.SetName(
                DemandSeriesContextText,
                presentation.HasSnapshot
                    ? $"需求系列快照与 AREA 范围：{DemandSeriesContextText.Text}"
                    : "需求系列快照与 AREA 范围");
            var demandFacets = state.DemandSeries.Snapshot?.Facets;
            DemandSeriesTrackingFacetText.Text = demandFacets is null
                ? "Tracking —"
                : $"Tracking {demandFacets.TrackingCount:N0}";
            DemandSeriesArchivedFacetText.Text = demandFacets is null
                ? "Archived —"
                : $"Archived {demandFacets.ArchivedCount:N0}";
            AutomationProperties.SetName(
                DemandSeriesTrackingFacetPill,
                $"Host 精确分面：{DemandSeriesTrackingFacetText.Text}");
            AutomationProperties.SetName(
                DemandSeriesArchivedFacetPill,
                $"Host 精确分面：{DemandSeriesArchivedFacetText.Text}");

            var showSource = presentation.SourceComparison
                != WatchDemandSeriesSourceComparison.None;
            DemandSeriesInfoBar.IsOpen = presentation.IsInfoOpen || showSource;
            DemandSeriesInfoBar.Severity = ToInfoBarSeverity(
                presentation.IsInfoOpen
                    ? presentation.InfoSeverity
                    : presentation.SourceComparisonSeverity);
            DemandSeriesInfoBar.Title = presentation.IsInfoOpen
                ? presentation.InfoTitle
                : showSource ? "来源快照比较" : string.Empty;
            var infoParts = new[]
            {
                presentation.IsInfoOpen ? presentation.InfoMessage : null,
                showSource ? presentation.SourceSnapshotSummary : null,
                showSource ? presentation.SourceComparisonMessage : null,
            }.Where(value => !string.IsNullOrWhiteSpace(value));
            DemandSeriesInfoBar.Message = string.Join(" ", infoParts);
            AutomationProperties.SetName(
                DemandSeriesInfoBar,
                DemandSeriesInfoBar.IsOpen
                    ? $"{DemandSeriesInfoBar.Title}。{DemandSeriesInfoBar.Message}"
                    : "需求系列读取状态");

            DemandSeriesPageSummaryText.Text = presentation.PageSummary;
            DemandSeriesOrderText.Text = presentation.OrderSummary;
            AutomationProperties.SetName(
                DemandSeriesPageSummaryText,
                presentation.HasSnapshot
                    ? $"需求系列精确分页摘要：{presentation.PageSummary}"
                    : "需求系列精确分页摘要");
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

            DemandSeriesAllAreasConfirmPanel.Visibility =
                ShouldOfferDemandSeriesAllAreasConfirmation()
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            DemandSeriesAllAreasConfirmButton.IsEnabled =
                DemandSeriesAllAreasConfirmPanel.Visibility == Visibility.Visible;

            var detail = presentation.Detail;
            DemandSeriesDetailHeadingText.Text = detail is null
                ? "选择一个需求系列以查看详情"
                : $"{detail.SeriesHeading} · {detail.LifecycleSummary}";
            AutomationProperties.SetName(
                DemandSeriesDetailHeadingText,
                detail is null
                    ? "选中需求系列详情"
                    : $"选中需求系列详情：{DemandSeriesDetailHeadingText.Text}");
            DemandSeriesDetailFactsText.Text = detail is null
                ? "选择后显示 Series 生命周期时间与创建证据。"
                : $"SeriesStartedAt {detail.StartedAt} · ArchivedAt {detail.ArchivedAt} · 创建 PollTrace {detail.CreatedPollTraceId} · 创建 ProjectionCommit {detail.CreatedProjectionCommitId} · 最近 PollTrace {detail.LatestPollTraceId} · 最近 ProjectionCommit {detail.LatestProjectionCommitId}";
            DemandSeriesGenerationGrid.ItemsSource = detail?.Generations;
            DemandSeriesLifecycleMilestones.ItemsSource = detail?.LifecycleMilestones;
            DemandSeriesRawObservationGrid.ItemsSource = detail?.RawObservations;
            var liveMes = detail?.FocusedLiveMesFields;
            DemandSeriesLiveMesFieldsText.Text = detail is null
                ? "选择 Demand 后显示唯一可信 LiveMesFieldSet。"
                : liveMes is null
                    ? $"DemandId {detail.FocusedDemandId} · 当前无可信 LiveMesFieldSet · {detail.MesSourceDateLabel} {detail.MesSourceDateValue}"
                    : $"DemandId {detail.FocusedDemandId} · AREA {liveMes.Area} · EQP {liveMes.Eqp} · STEP {liveMes.Step} · {detail.MesSourceDateLabel} {liveMes.MesSourceDate} · PACKAGE {liveMes.Package}";
            DemandSeriesObservationSummaryText.Text = detail?.ObservationSummary
                ?? "尚无 Demand 原始观测摘要。";
            var focusedGeneration = detail?.Generations.FirstOrDefault(row => string.Equals(
                row.DemandId,
                detail.FocusedDemandId,
                StringComparison.Ordinal));
            DemandSeriesReadabilityBlockersText.Text = focusedGeneration is null
                ? "尚无当前 Demand 资格结论。"
                : focusedGeneration.ReadabilityBlockers.Count == 0
                    ? $"{focusedGeneration.ExternalReadabilityState} · 无资格阻断"
                    : $"{focusedGeneration.ExternalReadabilityState} · 资格阻断 {string.Join('、', focusedGeneration.ReadabilityBlockers)}";
            AutomationProperties.SetName(
                DemandSeriesLiveMesFieldsText,
                $"可信 LiveMesFieldSet：{DemandSeriesLiveMesFieldsText.Text}");
            AutomationProperties.SetName(
                DemandSeriesReadabilityBlockersText,
                $"当前 Demand 资格阻断：{DemandSeriesReadabilityBlockersText.Text}");
            DemandSeriesConditionGrid.ItemsSource = detail?.CurrentConditions;
            var selectedPeriodId = (DemandSeriesErrorPeriodGrid.SelectedItem
                as WatchDemandErrorPeriodPresentation)?.PeriodId;
            DemandSeriesErrorPeriodGrid.ItemsSource = detail?.ErrorPeriods;
            var selectedPeriod = detail?.ErrorPeriods.FirstOrDefault(period => string.Equals(
                    period.PeriodId,
                    selectedPeriodId,
                    StringComparison.Ordinal))
                ?? detail?.ErrorPeriods.FirstOrDefault();
            DemandSeriesErrorPeriodGrid.SelectedItem = selectedPeriod;
            DemandSeriesErrorEvidenceGrid.ItemsSource = selectedPeriod?.Evidence;
            DemandSeriesEventGrid.ItemsSource = detail?.Events;
            DemandSeriesGenerationGrid.SelectedItem = detail?.Generations.FirstOrDefault(row =>
                string.Equals(row.DemandId, detail.FocusedDemandId, StringComparison.Ordinal));
            DemandSeriesCopyTimeButton.IsEnabled = detail is not null;
            DemandSeriesCopyEvidenceButton.IsEnabled = detail is not null;
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
            .Select(item => item.Content?.ToString())
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var value in values
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (existing.Add(value))
            {
                DemandSeriesWorkTypeFilter.Items.Add(new ComboBoxItem { Content = value });
            }
        }
    }

    private void RenderAttentionFacetSummary(WatchOverviewAttentionSummary? attention)
    {
        var summary = attention is null
            ? "等待严重度分面"
            : attention.Severities.Count == 0
                ? "Host 未返回严重度分面"
                : string.Join(
                    " · ",
                    attention.Severities.Select(facet =>
                        $"{facet.Count:N0} {AttentionFacetLabel(facet.Value)}"));
        AttentionSummaryFacetText.Text = summary;
        AutomationProperties.SetName(
            AttentionSummaryFacetText,
            $"接入告警严重度精确分面：{summary}");
    }

    private void RenderRecentActivity(WatchOverviewPresentation presentation)
    {
        RecentActivityItems.Children.Clear();
        if (!presentation.HasSnapshot)
        {
            var waiting = new Wpf.Ui.Controls.TextBlock
            {
                Text = "等待 Host 概览快照。",
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
                Text = "Host 在该快照窗口内没有报告重点转换；这不是健康结论。",
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
            };
            AutomationProperties.SetName(action, $"打开重点动态 {activity.Heading}");
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
        var latestViewFailure = new[]
            {
                state.Overview.LastFailureAt,
                state.DemandSeries.LastFailureAt,
                state.ReadabilityAudit.LastFailureAt,
                state.ErrorSearch.LastFailureAt,
                state.CurrentAttention.LastFailureAt,
            }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();
        var hasViewFailure = latestViewFailure != default;
        HostNavigationItem.Content = state.ConnectionStatus switch
        {
            WatchHostConnectionStatus.Connected when hasViewFailure => "Host 已连接 · 读取失败",
            WatchHostConnectionStatus.Connected => "Host 已连接",
            WatchHostConnectionStatus.Connecting => "Host 连接中",
            WatchHostConnectionStatus.Failed => "Host 连接失败",
            _ => "Host 未连接",
        };
        HostNavigationIcon.Symbol = state.ConnectionStatus switch
        {
            WatchHostConnectionStatus.Connected when hasViewFailure => SymbolRegular.CloudError24,
            WatchHostConnectionStatus.Connected => SymbolRegular.CloudCheckmark24,
            WatchHostConnectionStatus.Connecting => SymbolRegular.CloudSync24,
            WatchHostConnectionStatus.Failed => SymbolRegular.CloudDismiss24,
            _ => SymbolRegular.CloudOff24,
        };
        OverviewHostStatusText.Text = HostNavigationItem.Content?.ToString() ?? "Host 未连接";
        OverviewHostStatusIcon.Symbol = HostNavigationIcon.Symbol;
        var hostStatusStyleKey = state.ConnectionStatus switch
        {
            WatchHostConnectionStatus.Connected when hasViewFailure => "StatusPillCaution",
            WatchHostConnectionStatus.Connected => "StatusPillSuccess",
            WatchHostConnectionStatus.Connecting => "StatusPillAccent",
            WatchHostConnectionStatus.Failed => "StatusPillCritical",
            _ => "StatusPill",
        };
        OverviewHostStatusPill.Style = (Style)FindResource(hostStatusStyleKey);
        SettingsHostStatusPill.Style = (Style)FindResource(hostStatusStyleKey);
        SettingsHostStatusText.Text = OverviewHostStatusText.Text;
        SettingsHostStatusIcon.Symbol = HostNavigationIcon.Symbol;
        var (settingsIconBackground, settingsIconForeground) = state.ConnectionStatus switch
        {
            WatchHostConnectionStatus.Connected when hasViewFailure =>
                ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            WatchHostConnectionStatus.Connected =>
                ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            WatchHostConnectionStatus.Connecting =>
                ("AccentFillColorTertiaryBrush", "AccentTextFillColorPrimaryBrush"),
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
            $"概览 Host 状态：{OverviewHostStatusText.Text}");
        AutomationProperties.SetName(
            HostNavigationItem,
            $"Host 状态：{HostNavigationItem.Content}；打开连接设置");
        HostNavigationItem.ToolTip = hasViewFailure
            ? $"{overview.HostDetail} · 最近页面读取失败 {latestViewFailure:yyyy-MM-dd HH:mm:ss}"
            : overview.HostDetail;
        SettingsHostStateText.Text = $"{HostNavigationItem.Content} · {overview.HostDetail}";
        AutomationProperties.SetName(
            SettingsHostStatusPill,
            $"设置 Host 状态：{SettingsHostStatusText.Text}");
    }

    private static void SetNavigationAction(
        Wpf.Ui.Controls.Button button,
        OverviewNavigationIntent? intent)
    {
        button.Tag = intent;
        button.IsEnabled = intent is not null;
    }

    private static string AttentionFacetLabel(string value) => value switch
    {
        CurrentIngestAttentionKinds.SeriesError => "Series 错误",
        CurrentIngestAttentionKinds.PollRunFailure => "轮询失败",
        CurrentIngestAttentionKinds.TaskTypeProtection => "任务类型保护",
        CurrentIngestAttentionKinds.UnassignedMesObservation => "未分配观测",
        CurrentIngestAttentionSeverities.Error => "ERROR",
        CurrentIngestAttentionSeverities.Warning => "WARNING",
        _ => value,
    };

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
        _activePage = page;
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

    private void InitializeIntervalInputs()
    {
        ConfigureIntervalInput(OverviewIntervalInput, _preferences.RefreshIntervals.Overview.IntervalSeconds);
        ConfigureIntervalInput(DemandSeriesIntervalInput, _preferences.RefreshIntervals.DemandSeries.IntervalSeconds);
        ConfigureIntervalInput(ReadabilityAuditIntervalInput, _preferences.RefreshIntervals.ReadabilityAudit.IntervalSeconds);
        ConfigureIntervalInput(ErrorSearchIntervalInput, _preferences.RefreshIntervals.ErrorSearch.IntervalSeconds);
        ConfigureIntervalInput(CurrentAttentionIntervalInput, _preferences.RefreshIntervals.CurrentIngestAttention.IntervalSeconds);
    }

    private static void ConfigureIntervalInput(ComboBox comboBox, int selectedSeconds)
    {
        comboBox.ItemsSource = WatchV2AutoRefreshSetting.AllowedIntervals
            .Select(seconds => new RefreshIntervalChoice(seconds, $"{seconds} 秒"))
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
        RememberWindowSizeCheckBox.IsChecked = _preferences.Display.RememberWindowSize;
        KeepNavigationPaneOpenCheckBox.IsChecked = _preferences.Display.IsNavigationPaneOpen;
    }

    private void ApplyDisplayPreferences(
        WatchV2DisplayPreferences display,
        bool restoreGeometry = true)
    {
        if (restoreGeometry)
        {
            var preferred = display.RememberWindowSize
                ? display
                : WatchV2DisplayPreferences.Default;
            var workArea = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, Math.Min(preferred.WindowWidth, workArea.Width));
            Height = Math.Max(MinHeight, Math.Min(preferred.WindowHeight, workArea.Height));
        }

        WorkspaceNavigation.IsPaneOpen = display.IsNavigationPaneOpen;
    }

    private async void OnApplyHostClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var credential = string.IsNullOrWhiteSpace(HostCredentialInput.Password)
                ? _currentHostSettings.Credential
                : HostCredentialInput.Password;
            if (!int.TryParse(RequestTimeoutInput.Text, out var timeoutSeconds)
                || timeoutSeconds is < 1 or > 300)
            {
                throw new ArgumentException("请求超时必须是 1–300 秒之间的整数。");
            }

            var settings = new WatchHostSettings(
                HostBaseUrlInput.Text,
                credential,
                timeoutSeconds);
            ShowSettingsInfo(
                InfoBarSeverity.Informational,
                "正在应用 Host 设置",
                "旧 Host 业务状态已立即清空；正在验证新 Host 契约。");
            if (!await ApplyHostAsync(settings, _lifetimeCancellation.Token).ConfigureAwait(true))
            {
                return;
            }

            PopulateSettingsInputs();
            ShowSettingsInfo(
                _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Error,
                _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected
                    ? "Host 设置已应用"
                    : "Host 连接失败",
                _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected
                    ? "契约兼容，概览已读取并恢复自动刷新。"
                    : "旧 Host 数据不会恢复；请检查地址、凭据、超时和契约版本。");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the window is a neutral end to an in-flight apply.
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowSettingsInfo(InfoBarSeverity.Error, "无法应用 Host 设置", exception.Message);
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
                RememberWindowSizeCheckBox.IsChecked == true,
                Math.Max(MinWidth, Width),
                Math.Max(MinHeight, Height),
                KeepNavigationPaneOpenCheckBox.IsChecked == true);
            var hostGeneration = _session.State.HostGeneration;
            ApplyLocalPreferences(refresh, display);
            if (_session.State.HostGeneration != hostGeneration)
            {
                throw new InvalidOperationException("Local preferences must not replace the Host session.");
            }

            ShowSettingsInfo(
                InfoBarSeverity.Success,
                "本机设置已保存",
                "自动刷新保持开启；当前 Host 会话未重建。");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowSettingsInfo(InfoBarSeverity.Error, "无法保存本机设置", exception.Message);
        }
    }

    private void OnRestoreDefaultSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaults = WatchV2Preferences.Default;
            var hostGeneration = _session.State.HostGeneration;
            ApplyLocalPreferences(defaults.RefreshIntervals, defaults.Display);
            InitializeIntervalInputs();
            PopulateSettingsInputs();
            ApplyDisplayPreferences(defaults.Display);
            if (_session.State.HostGeneration != hostGeneration)
            {
                throw new InvalidOperationException("Restoring local defaults must not replace the Host session.");
            }

            ShowSettingsInfo(
                InfoBarSeverity.Success,
                "已恢复默认设置",
                "窗口恢复为 1440×900、紧凑导航 rail；五个数据视图保持 10 秒自动刷新。Host 会话未重建。");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowSettingsInfo(InfoBarSeverity.Error, "无法恢复默认设置", exception.Message);
        }
    }

    private static WatchV2AutoRefreshSetting ReadInterval(ComboBox comboBox) =>
        comboBox.SelectedValue is int seconds
            ? new WatchV2AutoRefreshSetting(seconds)
            : throw new ArgumentException("请选择 10、30、60 或 300 秒。");

    private void ShowSettingsInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.Title = title;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
        AutomationProperties.SetName(SettingsInfoBar, $"{title}。{message}");
        AutomationProperties.SetHelpText(SettingsHostStatusText, $"{title}。{message}");
    }

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
            _focusedDemandId = null;
            var seriesId = (DemandSeriesGrid.SelectedItem as WatchDemandSeriesRowPresentation)?.SeriesId;
            await SelectDemandSeriesAndRenderAsync(
                    seriesId,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private void OnDemandSeriesGenerationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingDemandSeries
            || DemandSeriesGenerationGrid.SelectedItem
                is not WatchDemandGenerationPresentation generation)
        {
            return;
        }

        _focusedDemandId = generation.DemandId;
        RenderWorkspace();
    }

    private void OnDemandSeriesErrorPeriodSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingDemandSeries)
        {
            return;
        }

        DemandSeriesErrorEvidenceGrid.ItemsSource =
            (DemandSeriesErrorPeriodGrid.SelectedItem
                as WatchDemandErrorPeriodPresentation)?.Evidence;
    }

    private void OnDemandSeriesFullEvidenceClick(object sender, RoutedEventArgs e)
    {
        var showFullEvidence = DemandSeriesFullEvidenceTabs.Visibility != Visibility.Visible;
        DemandSeriesLifecycleEvidencePanel.Visibility = showFullEvidence
            ? Visibility.Collapsed
            : Visibility.Visible;
        DemandSeriesFullEvidenceTabs.Visibility = showFullEvidence
            ? Visibility.Visible
            : Visibility.Collapsed;
        DemandSeriesFullEvidenceButton.Content = showFullEvidence
            ? "返回生命周期"
            : "完整证据";
        AutomationProperties.SetName(
            DemandSeriesFullEvidenceButton,
            showFullEvidence ? "返回需求系列生命周期" : "显示完整需求系列证据");
    }

    private void OnDemandSeriesCopyTimeClick(object sender, RoutedEventArgs e)
    {
        if (_session.State.DemandSeries.Detail?.Series is not { } series)
        {
            return;
        }

        WatchGridClipboardBehavior.TrySetClipboardText(
            WatchDemandSeriesClipboard.FormatFocusedDemandTimes(
                series,
                _focusedDemandId));
    }

    private void OnDemandSeriesCopyEvidenceClick(object sender, RoutedEventArgs e)
    {
        if (_session.State.DemandSeries.Detail?.Series is not { } series)
        {
            return;
        }

        WatchGridClipboardBehavior.TrySetClipboardText(
            WatchDemandSeriesClipboard.FormatSeriesEvidence(series));
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
            DemandSeriesInfoBar.IsOpen = true;
            DemandSeriesInfoBar.Severity = InfoBarSeverity.Error;
            DemandSeriesInfoBar.Title = "无法执行需求系列操作";
            DemandSeriesInfoBar.Message = exception.Message;
            AutomationProperties.SetName(
                DemandSeriesInfoBar,
                $"{DemandSeriesInfoBar.Title}。{DemandSeriesInfoBar.Message}");
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
            _lifetimeCancellation.Token);
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

        const string accessibleName = "展开或折叠主导航";
        AutomationProperties.SetName(toggle, accessibleName);
        AutomationProperties.SetHelpText(
            toggle,
            "在 48 epx 紧凑导航与 232 epx 展开导航之间切换");
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
            contentWidth < (double)FindResource("DemandSeriesMasterDetailStackBreakpoint"),
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
            (GridLength)FindResource("ReadabilityMasterColumnWidth"));
        ConfigureResponsivePageViewport(
            ReadabilityAuditLayoutGrid,
            ReadabilityAuditPage,
            stackReadability || useOuterScrolling);

        ReflowMasterDetail(
            contentWidth < (double)FindResource("AreaProfileMasterDetailStackBreakpoint"),
            AreaProfileEditorCard,
            AreaProfileMasterColumn,
            AreaProfileGapColumn,
            AreaProfileEditorColumn,
            AreaProfileVerticalGap,
            AreaProfileBottomRow,
            (GridLength)FindResource("AreaProfileMasterColumnWidth"));
        ReflowTicket22Pages(contentWidth, useOuterScrolling);
        WorkspaceContent.Margin = contentWidth < 760
            ? new Thickness(12)
            : new Thickness(24, 16, 24, 16);
    }

    private void ReflowDemandSeries(bool stack, bool useOuterScrolling)
    {
        var gap = (GridLength)FindResource("DemandSeriesSectionGap");

        Grid.SetColumn(DemandSeriesMasterPanel, 0);
        Grid.SetRow(DemandSeriesMasterPanel, 0);
        Grid.SetColumn(DemandSeriesDetailPanel, 0);
        Grid.SetRow(DemandSeriesDetailPanel, 2);

        DemandSeriesMasterDetailPrimaryRow.Height = stack
            ? GridLength.Auto
            : new GridLength(0.9, GridUnitType.Star);
        DemandSeriesMasterDetailGapRow.Height = gap;
        DemandSeriesMasterDetailBottomRow.Height = stack
            ? GridLength.Auto
            : new GridLength(1.1, GridUnitType.Star);

        ConfigureResponsivePageViewport(
            DemandSeriesLayoutGrid,
            DemandSeriesScrollViewer,
            stack || useOuterScrolling);
    }

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
        GridLength expandedMasterWidth)
    {
        var collapsed = (GridLength)FindResource("WatchCollapsedGridLength");
        var gap = (GridLength)FindResource("WatchMasterDetailGapWidth");
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
        ConfigureTitleBarButton(titleBar, "PART_MinimizeButton", "最小化窗口");
        ConfigureTitleBarButton(titleBar, "PART_CloseButton", "关闭窗口");
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
            WindowState == WindowState.Maximized ? "还原窗口" : "最大化窗口");

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
        if (!_preferences.Display.RememberWindowSize)
        {
            return;
        }

        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(0, 0, Width, Height) : RestoreBounds;
            var display = new WatchV2DisplayPreferences(
                rememberWindowSize: true,
                windowWidth: Math.Max(MinWidth, bounds.Width),
                windowHeight: Math.Max(MinHeight, bounds.Height),
                isNavigationPaneOpen: WorkspaceNavigation.IsPaneOpen);
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
        _autoRefresh.RefreshStateChanged -= OnAutoRefreshStateChanged;
        WorkspaceContent.SizeChanged -= OnWorkspaceContentSizeChanged;
        WorkspaceNavigation.PaneOpened -= OnWorkspaceNavigationPaneStateChanged;
        WorkspaceNavigation.PaneClosed -= OnWorkspaceNavigationPaneStateChanged;
        _lifetimeCancellation.Cancel();
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
