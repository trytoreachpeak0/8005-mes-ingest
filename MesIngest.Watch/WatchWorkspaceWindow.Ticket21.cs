using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MesIngest.Core.SeriesProjection;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace MesIngest.Watch;

internal enum WatchAreaProfileDirectoryOpenDisposition
{
    Opened,
    SuppressedForUiTest,
}

internal interface IWatchAreaProfileDirectoryLauncher
{
    WatchAreaProfileDirectoryOpenDisposition Open(string directoryPath);
}

internal sealed class WatchAreaProfileDirectoryLauncher : IWatchAreaProfileDirectoryLauncher
{
    private const string UiTestModeVariable = "MESINGEST_WATCH_UI_TEST_MODE";
    private readonly Func<string, string?> _readEnvironmentVariable;
    private readonly Action<ProcessStartInfo> _startShell;

    public WatchAreaProfileDirectoryLauncher(
        Func<string, string?>? readEnvironmentVariable = null,
        Action<ProcessStartInfo>? startShell = null)
    {
        _readEnvironmentVariable = readEnvironmentVariable
            ?? Environment.GetEnvironmentVariable;
        _startShell = startShell ?? (startInfo => Process.Start(startInfo));
    }

    public WatchAreaProfileDirectoryOpenDisposition Open(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var directory = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(directory);
        if (string.Equals(
                _readEnvironmentVariable(UiTestModeVariable),
                "1",
                StringComparison.Ordinal))
        {
            return WatchAreaProfileDirectoryOpenDisposition.SuppressedForUiTest;
        }

        try
        {
            _startShell(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "无法通过平台文件管理器打开 AREA 配置目录。",
                exception);
        }

        return WatchAreaProfileDirectoryOpenDisposition.Opened;
    }
}

internal sealed record WatchAreaFilterProfilePresentationRow(
    string ProfileName,
    int MesAreaCount,
    int DiagnosticCount,
    bool IsValid,
    bool IsApplied,
    DateTimeOffset LastModifiedAt)
{
    public string LastModifiedText => WatchTimeDisplay.Format(LastModifiedAt);

    public string StatusText => IsApplied
        ? IsValid
            ? "当前应用"
            : "当前应用 · 无效"
        : IsValid
            ? "有效"
            : "无效";

    public string MetadataText =>
        $"{MesAreaCount:N0} 个 AREA · {LastModifiedText} 修改";

    public string DisplaySummary => $"{ProfileName} · {StatusText}";

    public string AutomationName =>
        $"{ProfileName}；{MesAreaCount:N0} 个 AREA；{StatusText}；{LastModifiedText} 修改";
}

internal partial class WatchWorkspaceWindow
{
    private enum AreaProfileFileOperation
    {
        Create,
        SaveAs,
        Rename,
        Delete,
    }

    private sealed record AreaProfileFileOperationConfirmation(
        AreaProfileFileOperation Operation,
        string? SourceProfileName,
        string? SourceFileFingerprint,
        IInputElement? Invoker);

    private WatchAreaFilterProfileStore _areaProfileStore = null!;
    private IWatchAreaProfileDirectoryLauncher _areaProfileDirectoryLauncher = null!;
    private IReadOnlyList<WatchAreaFilterProfilePresentationRow> _areaProfileRows = [];
    private WatchAreaFilterProfile? _areaProfileDraft;
    private string? _selectedAreaProfileName;
    private string? _areaProfileStartupError;
    private bool _areaProfileDraftIsDirty;
    private bool _areaProfileRowsLoaded;
    private bool _isRenderingAreaProfiles;
    private bool _isRenderingReadabilityAudit;
    private AreaProfileFileOperationConfirmation? _areaProfileFileOperationConfirmation;
    private long _areaProfileOperationGeneration;
    private long _readabilityAuditOperationGeneration;

    internal Task ReadabilityAuditNavigationTask { get; private set; } = Task.CompletedTask;

    internal Task AreaProfileOperationTask { get; private set; } = Task.CompletedTask;

    private void InitializeAreaFilterProfiles(
        string? areaFilterProfilesDirectoryPath,
        TimeProvider? timeProvider,
        IWatchAreaProfileDirectoryLauncher? areaProfileDirectoryLauncher)
    {
        _areaProfileStore = new WatchAreaFilterProfileStore(
            areaFilterProfilesDirectoryPath,
            timeProvider);
        _areaProfileDirectoryLauncher = areaProfileDirectoryLauncher
            ?? new WatchAreaProfileDirectoryLauncher();
        try
        {
            var appliedState = _areaProfileStore.LoadAppliedState();
            _areaContext = appliedState.CurrentApplied.ToDisplayContext();
            _areaProfileStartupError = appliedState.Diagnostic?.Message;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            _areaContext = WatchAreaDisplayContext.AllAreas;
            _areaProfileStartupError = exception.Message;
        }

        _overviewQuery = new WatchOverviewQuery(_areaContext.MesAreas).NormalizeAndValidate();
        _demandSeriesQuery = WatchDemandSeriesQueries.StartLatest(
            new DemandSeriesBrowseFilter { MesAreas = _areaContext.MesAreas },
            _demandSeriesQuery.PageSize);
        _readabilityAuditQuery = WatchReadabilityAuditQueries.StartLatest(
            new ReadabilityAuditFilter { MesAreas = _areaContext.MesAreas },
            _readabilityAuditQuery.PageSize);
    }

    private void InitializeReadabilityAuditAndAreaProfiles()
    {
        WatchGridClipboardBehavior.Attach(ReadabilityStateFacetGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityBlockerFacetGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityAuditGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityQualificationGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityBlockerEvidenceGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(ReadabilityRawObservationGrid, preserveSelectionUnit: true);
        WatchGridClipboardBehavior.Attach(AreaProfileValidationGrid, preserveSelectionUnit: true);

        ReadabilityWorkTypeFilter.Items.Add(new ComboBoxItem
        {
            Content = "全部 WorkType",
            Tag = string.Empty,
        });
        ReadabilityBlockerFilter.Items.Add(new ComboBoxItem
        {
            Content = "全部阻断原因",
            Tag = string.Empty,
        });
        foreach (var definition in ReadabilityBlockerCatalog.Definitions)
        {
            ReadabilityBlockerFilter.Items.Add(new ComboBoxItem
            {
                Content = definition.Code,
                Tag = definition.Code,
                ToolTip = definition.Meaning,
            });
        }

        ReadabilityWorkTypeFilter.SelectedIndex = 0;
        ReadabilityBlockerFilter.SelectedIndex = 0;
        SyncReadabilityFilterControls(_readabilityAuditQuery);
        AreaProfileEditor.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnAreaProfileEditorScrollChanged));
        RenderAreaProfiles(reloadProfiles: true);
        if (_areaProfileStartupError is not null)
        {
            ShowAreaProfileInfo(
                InfoBarSeverity.Warning,
                "无法恢复上次 AREA 配置",
                $"已回退到全部 AREA。{_areaProfileStartupError}");
        }
    }

    private async Task LoadReadabilityAuditNavigationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshLatestReadabilityAuditAndRenderAsync(
                    _readabilityAuditQuery,
                    desiredDemandId: null,
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

    private async Task RefreshLatestReadabilityAuditAndRenderAsync(
        ReadabilityAuditQuery query,
        string? desiredDemandId,
        CancellationToken cancellationToken)
    {
        var operation = BeginReadabilityAuditOperation();
        if (!await SuspendReadabilityAuditAutoRefreshAsync(operation, cancellationToken)
                .ConfigureAwait(true))
        {
            return;
        }

        _readabilityAuditQuery = query;
        var refresh = _session.RefreshLatestReadabilityAuditPageAsync(query, cancellationToken);
        RenderWorkspace();
        await refresh.ConfigureAwait(true);
        if (!IsCurrentReadabilityAuditOperation(operation, cancellationToken))
        {
            return;
        }

        RenderWorkspace();
        var view = _session.State.ReadabilityAudit;
        if (!view.IsStale && view.Snapshot is { } committed)
        {
            _readabilityAuditQuery = CanonicalReadabilityAuditAutoRefreshQuery(committed);
            if (!string.IsNullOrWhiteSpace(desiredDemandId)
                && committed.Items.Any(item => string.Equals(
                    item.DemandId,
                    desiredDemandId,
                    StringComparison.Ordinal)))
            {
                await SelectReadabilityDemandAndRenderAsync(
                        desiredDemandId,
                        operation,
                        cancellationToken,
                        manageAutoRefresh: false)
                    .ConfigureAwait(true);
                if (!IsCurrentReadabilityAuditOperation(operation, cancellationToken))
                {
                    return;
                }
            }
        }

        ReactivateReadabilityAuditIfCurrentPage();
    }

    private async Task RefreshFrozenReadabilityAuditAndRenderAsync(
        ReadabilityAuditQuery frozenRequest,
        ReadabilityAuditQuery automaticRequest,
        CancellationToken cancellationToken)
    {
        var operation = BeginReadabilityAuditOperation();
        if (!await SuspendReadabilityAuditAutoRefreshAsync(operation, cancellationToken)
                .ConfigureAwait(true))
        {
            return;
        }

        _readabilityAuditQuery = automaticRequest;
        var refresh = _session.RefreshReadabilityAuditAsync(frozenRequest, cancellationToken);
        RenderWorkspace();
        await refresh.ConfigureAwait(true);
        if (!IsCurrentReadabilityAuditOperation(operation, cancellationToken))
        {
            return;
        }

        RenderWorkspace();
        var view = _session.State.ReadabilityAudit;
        if (!view.IsStale && view.Snapshot is { } committed)
        {
            _readabilityAuditQuery = CanonicalReadabilityAuditAutoRefreshQuery(committed);
        }

        ReactivateReadabilityAuditIfCurrentPage();
    }

    internal Task SelectReadabilityDemandAndRenderAsync(
        string? demandId,
        CancellationToken cancellationToken = default) =>
        SelectReadabilityDemandAndRenderAsync(
            demandId,
            BeginReadabilityAuditOperation(),
            cancellationToken,
            manageAutoRefresh: true);

    private async Task SelectReadabilityDemandAndRenderAsync(
        string? demandId,
        long operation,
        CancellationToken cancellationToken,
        bool manageAutoRefresh)
    {
        if (manageAutoRefresh
            && !await SuspendReadabilityAuditAutoRefreshAsync(operation, cancellationToken)
                .ConfigureAwait(true))
        {
            return;
        }

        if (!IsCurrentReadabilityAuditOperation(operation, cancellationToken))
        {
            return;
        }

        var selection = _session.SelectReadabilityDemandAsync(demandId, cancellationToken);
        RenderWorkspace();
        await selection.ConfigureAwait(true);
        if (!IsCurrentReadabilityAuditOperation(operation, cancellationToken))
        {
            return;
        }

        RenderWorkspace();
        if (manageAutoRefresh)
        {
            ReactivateReadabilityAuditIfCurrentPage();
        }
    }

    private long BeginReadabilityAuditOperation() =>
        Interlocked.Increment(ref _readabilityAuditOperationGeneration);

    private bool IsCurrentReadabilityAuditOperation(
        long operation,
        CancellationToken cancellationToken) =>
        !_disposed
        && !cancellationToken.IsCancellationRequested
        && operation == Interlocked.Read(ref _readabilityAuditOperationGeneration);

    private async Task<bool> SuspendReadabilityAuditAutoRefreshAsync(
        long operation,
        CancellationToken cancellationToken)
    {
        if (_autoRefresh.ActiveView == WatchV2DataView.ReadabilityAudit)
        {
            _autoRefresh.Deactivate();
            await _autoRefresh.WaitForIdleAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(true);
        }

        return IsCurrentReadabilityAuditOperation(operation, cancellationToken);
    }

    private void ReactivateReadabilityAuditIfCurrentPage()
    {
        if (_activePage == WatchWorkspacePage.ReadabilityAudit
            && _session.State.ConnectionStatus == WatchHostConnectionStatus.Connected)
        {
            _autoRefresh.ActivateReadabilityAudit(_readabilityAuditQuery);
        }
    }

    private static ReadabilityAuditQuery CanonicalReadabilityAuditAutoRefreshQuery(
        ReadabilityAuditListSnapshot snapshot) => snapshot.TotalPages == 0
        ? WatchReadabilityAuditQueries.StartLatest(snapshot.Filter, snapshot.PageSize)
        : WatchReadabilityAuditQueries.OpenFrozenPage(snapshot, snapshot.PageNumber);

    private ReadabilityAuditFilter ReadReadabilityAuditFilter()
    {
        var readabilityStates = ReadReadabilityStateDraft();
        if (readabilityStates.Any(state => state is not ExternalReadabilityStates.Readable
                and not ExternalReadabilityStates.NotReadable))
        {
            throw new ArgumentException("资格只能是 READABLE 或 NOT_READABLE；多个值请用逗号分隔。");
        }

        var blockers = ReadReadabilityChoices(ReadabilityBlockerFilter);
        var knownBlockers = ReadabilityBlockerCatalog.Definitions
            .Select(definition => definition.Code)
            .ToHashSet(StringComparer.Ordinal);
        if (blockers.Any(blocker => !knownBlockers.Contains(blocker)))
        {
            throw new ArgumentException("阻断原因必须使用页面列出的稳定 blocker code；多个值请用逗号分隔。");
        }

        return new ReadabilityAuditFilter
        {
            ReadabilityStates = readabilityStates,
            WorkTypes = ReadReadabilityChoices(ReadabilityWorkTypeFilter),
            Blockers = blockers,
            DemandId = ReadReadabilityText(ReadabilityDemandIdFilter.Text),
            SublotContains = string.IsNullOrWhiteSpace(ReadabilitySublotFilter.Text)
                ? null
                : ReadabilitySublotFilter.Text,
            MesAreas = _areaContext.MesAreas,
        };
    }

    private static IReadOnlyList<string> ReadReadabilityChoices(ComboBox comboBox)
    {
        var value = comboBox.IsEditable
            ? comboBox.Text
            : comboBox.SelectedItem is ComboBoxItem item
                ? item.Tag?.ToString() ?? item.Content?.ToString()
                : comboBox.Text;
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith("全部", StringComparison.Ordinal))
        {
            return [];
        }

        return value
            .Split([',', ';', '，'], StringSplitOptions.RemoveEmptyEntries)
            .Select(candidate => candidate.Trim())
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ReadReadabilityText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private int ReadReadabilityPageSize()
    {
        var value = ReadabilityPageSizeInput.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? item.Content?.ToString()
            : ReadabilityPageSizeInput.Text;
        return int.TryParse(value, out var pageSize)
            && pageSize is ReadabilityAuditQuery.DefaultPageSize
                or ReadabilityAuditQuery.MaximumPageSize
            ? pageSize
            : throw new ArgumentException("每页数量必须是 100 或 200。");
    }

    private void SyncReadabilityFilterControls(ReadabilityAuditQuery query)
    {
        _isRenderingReadabilityAudit = true;
        try
        {
            SetReadabilityStateDraft(query.Filter.ReadabilityStates);
            SelectReadabilityChoice(ReadabilityWorkTypeFilter, query.Filter.WorkTypes);
            SelectReadabilityChoice(ReadabilityBlockerFilter, query.Filter.Blockers);
            ReadabilityDemandIdFilter.Text = query.Filter.DemandId ?? string.Empty;
            ReadabilitySublotFilter.Text = query.Filter.SublotContains ?? string.Empty;
            SelectReadabilityChoice(ReadabilityPageSizeInput, [query.PageSize.ToString()]);
        }
        finally
        {
            _isRenderingReadabilityAudit = false;
        }

        UpdateReadabilityClearFilterState();
    }

    private void OnReadabilityFilterDraftChanged(object sender, RoutedEventArgs e)
    {
        if (!_isRenderingReadabilityAudit)
        {
            UpdateReadabilityClearFilterState();
        }
    }

    private void UpdateReadabilityClearFilterState()
    {
        if (ReadabilityClearFilterButton is null)
        {
            return;
        }

        try
        {
            var draft = ReadReadabilityAuditFilter();
            ReadabilityClearFilterButton.IsEnabled =
                draft.ReadabilityStates.Count > 0
                || draft.WorkTypes.Count > 0
                || draft.Blockers.Count > 0
                || draft.DemandId is not null
                || draft.SublotContains is not null
                || ReadReadabilityPageSize() != ReadabilityAuditQuery.DefaultPageSize;
        }
        catch (ArgumentException)
        {
            // An invalid non-empty draft is still clearable back to the default query.
            ReadabilityClearFilterButton.IsEnabled = true;
        }
    }

    private static void SelectReadabilityChoice(
        ComboBox comboBox,
        IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            comboBox.SelectedIndex = 0;
            return;
        }

        if (values.Count == 1)
        {
            foreach (var candidate in comboBox.Items.OfType<ComboBoxItem>())
            {
                var candidateValue = candidate.Tag?.ToString() ?? candidate.Content?.ToString();
                if (string.Equals(candidateValue, values[0], StringComparison.Ordinal))
                {
                    comboBox.SelectedItem = candidate;
                    return;
                }
            }
        }

        if (comboBox.IsEditable)
        {
            comboBox.SelectedIndex = -1;
            comboBox.Text = string.Join(", ", values);
        }
    }

    private void AddObservedReadabilityWorkTypes(IEnumerable<string> values)
    {
        var existing = ReadabilityWorkTypeFilter.Items
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
                ReadabilityWorkTypeFilter.Items.Add(new ComboBoxItem
                {
                    Content = value,
                    Tag = value,
                });
            }
        }
    }

    private void RenderReadabilityAudit(WatchV2WorkspaceState state)
    {
        _isRenderingReadabilityAudit = true;
        try
        {
            var presentation = WatchReadabilityAuditPresentation.Project(
                state,
                _readabilityAuditQuery,
                _areaContext);
            ReadabilityRefreshPolicyText.Text = state.ReadabilityAudit.IsRefreshing
                ? "正在读取 · 自动刷新保持开启"
                : $"每 {_preferences.RefreshIntervals.ReadabilityAudit.IntervalSeconds} 秒自动刷新";
            ReadabilityAuditInfoBar.IsOpen = presentation.IsInfoOpen;
            ReadabilityAuditInfoBar.Severity = ToInfoBarSeverity(presentation.InfoSeverity);
            ReadabilityAuditInfoBar.Title = presentation.InfoTitle;
            ReadabilityAuditInfoBar.Message = presentation.InfoMessage;
            AutomationProperties.SetName(
                ReadabilityAuditInfoBar,
                presentation.IsInfoOpen
                    ? $"{presentation.InfoTitle}。{presentation.InfoMessage}"
                    : "资格审计读取状态：当前无活动通知");
            ReadabilitySnapshotFactsText.Text = presentation.SnapshotFacts;
            ReadabilityClientAttemptText.Text = presentation.ClientAttemptFacts;
            ReadabilityAreaScopeText.Text =
                $"{presentation.LocalAreaHeading} · {presentation.LocalAreaDetail} · {presentation.HostAreaScope}";
            ReadabilityOrderSummaryText.Text =
                $"{presentation.OrderSummary} · {presentation.HostFilterSummary}";
            ReadabilityPageSummaryText.Text = presentation.PageSummary;
            AutomationProperties.SetName(
                ReadabilityPageSummaryText,
                $"资格审计精确总数与页码：{presentation.PageSummary}");
            AutomationProperties.SetName(
                ReadabilityAreaScopeText,
                $"资格审计 AREA 范围：{ReadabilityAreaScopeText.Text}");

            ReadabilityStateFacetGrid.ItemsSource = presentation.StateFacets;
            ReadabilityBlockerFacetGrid.ItemsSource = presentation.BlockerFacets;
            AddObservedReadabilityWorkTypes(presentation.Rows.Select(row => row.WorkType));
            ReadabilityAuditGrid.ItemsSource = presentation.Rows;
            ReadabilityAuditGrid.SelectedItem = presentation.Rows.FirstOrDefault(row =>
                string.Equals(
                    row.DemandId,
                    state.ReadabilityAudit.SelectedId,
                    StringComparison.Ordinal));

            ReadabilityEmptyInfoBar.IsOpen = presentation.EmptyResultMessage.Length > 0;
            ReadabilityEmptyInfoBar.Title = presentation.EmptyResultMessage.Length > 0
                ? "当前已提交条件没有命中"
                : string.Empty;
            ReadabilityEmptyInfoBar.Message = presentation.EmptyResultMessage;
            AutomationProperties.SetName(
                ReadabilityEmptyInfoBar,
                presentation.EmptyResultMessage.Length > 0
                    ? $"{ReadabilityEmptyInfoBar.Title}。{presentation.EmptyResultMessage}"
                    : "资格审计结果不为空或尚未成功读取");
            ReadabilityPreviousPageButton.IsEnabled = presentation.CanGoPrevious
                && !presentation.IsRefreshing;
            ReadabilityNextPageButton.IsEnabled = presentation.CanGoNext
                && !presentation.IsRefreshing;
            ReadabilityGoToPageButton.IsEnabled = presentation.HasSnapshot
                && !presentation.IsRefreshing;
            ReadabilityPageNumberInput.Text = state.ReadabilityAudit.Snapshot?.PageNumber
                .ToString() ?? "1";

            var detail = presentation.Detail;
            ReadabilityDetailHeadingText.Text = detail?.Heading ?? "选择一个 Demand 世代";
            ReadabilityDetailFactsText.Text = detail is null
                ? presentation.SelectionNotice
                    ?? "详情必须与当前列表使用同一个冻结 snapshotReference。"
                : $"{detail.Facts} · 全部阻断 {detail.AllBlockersSummary}";
            ReadabilitySeriesFactsText.Text = detail?.SeriesFacts ?? "尚未选择 Series。";
            ReadabilityLiveMesFactsText.Text = detail is null
                ? "选择后显示可信 LiveMesFieldSet，或保留全部原始观测冲突证据。"
                : $"{detail.LiveMesFacts} · {detail.ObservationSummary} · {detail.PollTraceFacts}";
            AutomationProperties.SetName(
                ReadabilityDetailFactsText,
                $"资格审计详情快照事实：{ReadabilityDetailFactsText.Text}");
            AutomationProperties.SetName(
                ReadabilitySeriesFactsText,
                $"资格审计所属 Series 事实：{ReadabilitySeriesFactsText.Text}");
            AutomationProperties.SetName(
                ReadabilityLiveMesFactsText,
                $"资格审计可信 MES 字段或原始观测冲突：{ReadabilityLiveMesFactsText.Text}");
            ReadabilityQualificationGrid.ItemsSource = detail?.QualificationChecks;
            ReadabilityBlockerEvidenceGrid.ItemsSource = detail?.BlockerEvidence;
            ReadabilityRawObservationGrid.ItemsSource = detail?.RawObservations;
            ReadabilityOpenSeriesButton.IsEnabled = detail is not null;
            ReadabilityDetailInfoBar.IsOpen = detail is null;
            var selectedDemandId = state.ReadabilityAudit.SelectedId;
            var detailReadFailed = selectedDemandId is not null
                && state.ReadabilityAudit.LastFailureAt is not null;
            ReadabilityDetailInfoBar.Severity = presentation.SelectionNotice is not null
                || detailReadFailed
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Informational;
            ReadabilityDetailInfoBar.Title = presentation.SelectionNotice is not null
                ? "原选择已清除"
                : selectedDemandId is null
                    ? "尚未选择 Demand"
                    : detailReadFailed
                        ? "无法读取同快照详情"
                        : "正在读取同快照详情";
            ReadabilityDetailInfoBar.Message = presentation.SelectionNotice
                ?? (selectedDemandId is null
                    ? "从左侧列表选择 Demand 后读取同一审计快照的资格检查与全部证据。"
                    : detailReadFailed
                        ? $"Demand {selectedDemandId} 的详情读取失败；列表仍属于上方标明的冻结快照。"
                        : $"已选择 Demand {selectedDemandId}；正在读取当前 snapshotReference 的详情。");
            AutomationProperties.SetName(
                ReadabilityDetailInfoBar,
                $"{ReadabilityDetailInfoBar.Title}。{ReadabilityDetailInfoBar.Message}");
            RenderSelectedReadabilityAudit(presentation, state);
        }
        finally
        {
            _isRenderingReadabilityAudit = false;
        }
    }

    private async void OnReadabilityApplyFiltersClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var query = WatchReadabilityAuditQueries.StartLatest(
                ReadReadabilityAuditFilter(),
                ReadReadabilityPageSize());
            await RefreshLatestReadabilityAuditAndRenderAsync(
                    query,
                    desiredDemandId: null,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnReadabilityClearFiltersClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var query = WatchReadabilityAuditQueries.StartLatest(
                new ReadabilityAuditFilter { MesAreas = _areaContext.MesAreas },
                ReadabilityAuditQuery.DefaultPageSize);
            SyncReadabilityFilterControls(query);
            await RefreshLatestReadabilityAuditAndRenderAsync(
                    query,
                    desiredDemandId: null,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnReadabilityPreviousPageClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var snapshot = _session.State.ReadabilityAudit.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的资格审计快照。");
            var request = WatchReadabilityAuditQueries.OpenFrozenPage(
                snapshot,
                snapshot.PageNumber - 1);
            await RefreshFrozenReadabilityAuditAndRenderAsync(
                    request,
                    request,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnReadabilityNextPageClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var snapshot = _session.State.ReadabilityAudit.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的资格审计快照。");
            var request = WatchReadabilityAuditQueries.OpenNextFrozenPage(snapshot)
                ?? throw new InvalidOperationException("当前冻结审计快照没有下一页。");
            var automaticRequest = WatchReadabilityAuditQueries.OpenFrozenPage(
                snapshot,
                snapshot.PageNumber + 1);
            await RefreshFrozenReadabilityAuditAndRenderAsync(
                    request,
                    automaticRequest,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnReadabilityGoToPageClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var snapshot = _session.State.ReadabilityAudit.Snapshot
                ?? throw new InvalidOperationException("当前没有可分页的资格审计快照。");
            if (!int.TryParse(ReadabilityPageNumberInput.Text, out var pageNumber))
            {
                throw new ArgumentException("页码必须是整数。");
            }

            var request = WatchReadabilityAuditQueries.OpenFrozenPage(snapshot, pageNumber);
            await RefreshFrozenReadabilityAuditAndRenderAsync(
                    request,
                    request,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async void OnReadabilitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingReadabilityAudit)
        {
            return;
        }

        await RunReadabilityUiActionAsync(() => SelectReadabilityDemandAndRenderAsync(
            (ReadabilityAuditGrid.SelectedItem as WatchReadabilityAuditRowPresentation)?.DemandId,
            _lifetimeCancellation.Token)).ConfigureAwait(true);
    }

    private async void OnReadabilityOpenSeriesClick(object sender, RoutedEventArgs e) =>
        await RunReadabilityUiActionAsync(async () =>
        {
            var snapshot = _session.State.ReadabilityAudit.Snapshot
                ?? throw new InvalidOperationException("当前没有资格审计快照。");
            var selectedDemandId = _session.State.ReadabilityAudit.SelectedId
                ?? throw new InvalidOperationException("请先选择一个 Demand。");
            var detail = _session.State.ReadabilityAudit.Detail;
            if (detail is null
                || !string.Equals(
                    detail.SnapshotReference,
                    snapshot.SnapshotReference,
                    StringComparison.Ordinal)
                || !string.Equals(
                    detail.Demand.DemandId,
                    selectedDemandId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("当前没有与冻结资格审计快照一致的 Demand 详情。");
            }
            var navigation = WatchDemandSeriesNavigationContext.FromReadabilityAudit(
                snapshot,
                detail.Series.SeriesId,
                detail.Demand.DemandId);
            await NavigateToDemandSeriesAsync(navigation, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async Task RunReadabilityUiActionAsync(Func<Task> action)
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
            or ReadabilityAuditException)
        {
            ReadabilityAuditInfoBar.IsOpen = true;
            ReadabilityAuditInfoBar.Severity = InfoBarSeverity.Error;
            ReadabilityAuditInfoBar.Title = "无法执行资格审计操作";
            ReadabilityAuditInfoBar.Message = exception.Message;
            AutomationProperties.SetName(
                ReadabilityAuditInfoBar,
                $"{ReadabilityAuditInfoBar.Title}。{ReadabilityAuditInfoBar.Message}");
        }
    }

    private void RenderAreaProfiles(bool reloadProfiles = false)
    {
        if (AreaProfileList is null || _areaProfileStore is null)
        {
            return;
        }

        _isRenderingAreaProfiles = true;
        try
        {
            WatchAppliedAreaFilterProfile applied;
            try
            {
                var appliedState = _areaProfileStore.LoadAppliedState();
                applied = appliedState.CurrentApplied;
                if (reloadProfiles || !_areaProfileRowsLoaded)
                {
                    ReloadAreaProfileRows(applied);
                }
                if (appliedState.Diagnostic is { } diagnostic)
                {
                    _areaProfileStartupError = diagnostic.Message;
                    ShowAreaProfileInfo(
                        InfoBarSeverity.Warning,
                        "无法恢复上次 AREA 配置",
                        diagnostic.Message);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                if (!_areaProfileRowsLoaded)
                {
                    _areaProfileRows = [];
                }
                applied = new WatchAppliedAreaFilterProfile(
                    _areaContext.MesAreas.Count == 0 ? null : _areaContext.ProfileName,
                    _areaContext.MesAreas,
                    _areaContext.LastUpdatedAt);
                _areaProfileStartupError ??= exception.Message;
                ShowAreaProfileInfo(
                    InfoBarSeverity.Warning,
                    "无法读取本机 AREA 配置",
                    $"当前已应用显示范围保持不变。{exception.Message}");
            }

            _areaProfileRows = _areaProfileRows
                .Select(row => row with
                {
                    IsApplied = string.Equals(
                        row.ProfileName,
                        applied.ProfileName,
                        StringComparison.OrdinalIgnoreCase),
                })
                .ToArray();
            var searchText = AreaProfileSearchInput.Text.Trim();
            var visibleRows = _areaProfileRows
                .Where(row => searchText.Length == 0
                    || row.ProfileName.Contains(
                        searchText,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var invalidCount = _areaProfileRows.Count(row => !row.IsValid);

            var areaProfileDirectoryPath = Path.GetFullPath(_areaProfileStore.DirectoryPath);
            AreaProfileDirectoryText.Text = FormatAreaProfileDirectoryCaption(
                areaProfileDirectoryPath);
            AreaProfileDirectoryText.ToolTip = areaProfileDirectoryPath;
            AutomationProperties.SetHelpText(
                AreaProfileDirectoryText,
                areaProfileDirectoryPath);
            AreaProfileListSummaryText.Text = searchText.Length == 0
                ? $"{_areaProfileRows.Count:N0} 个文件 · {invalidCount:N0} 个需要修复"
                : $"显示 {visibleRows.Length:N0} / {_areaProfileRows.Count:N0} 个文件"
                    + $" · {invalidCount:N0} 个需要修复";
            if (_selectedAreaProfileName is null
                && !_areaProfileDraftIsDirty
                && applied.ProfileName is { } appliedProfileName
                && _areaProfileRows.FirstOrDefault(row => string.Equals(
                    row.ProfileName,
                    appliedProfileName,
                    StringComparison.OrdinalIgnoreCase)) is { } appliedRow)
            {
                _selectedAreaProfileName = appliedRow.ProfileName;
                try
                {
                    _areaProfileDraft = _areaProfileStore.Load(appliedRow.ProfileName);
                    _areaProfileDraftIsDirty = false;
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException)
                {
                    _selectedAreaProfileName = null;
                    ShowAreaProfileInfo(
                        InfoBarSeverity.Warning,
                        "无法读取当前应用配置的 TXT",
                        $"已应用 AREA 快照保持不变。{exception.Message}");
                }
            }

            AreaProfileAppliedStateText.Text = applied.AppliedAt is { } appliedAt
                ? $"当前应用：{applied.DisplaySummary} · {WatchTimeDisplay.Format(appliedAt)}"
                : $"当前应用：{applied.DisplaySummary}";
            AutomationProperties.SetName(
                AreaProfileAppliedStateText,
                applied.MesAreas.Count == 0
                    ? AreaProfileAppliedStateText.Text
                    : $"{AreaProfileAppliedStateText.Text}；AREA {string.Join('、', applied.MesAreas)}");
            AreaProfileList.ItemsSource = visibleRows;
            AreaProfileList.SelectedItem = visibleRows.FirstOrDefault(row => string.Equals(
                row.ProfileName,
                _selectedAreaProfileName,
                StringComparison.OrdinalIgnoreCase));

            _areaProfileDraft ??= WatchAreaFilterProfileParser.Parse(
                string.Empty,
                string.Empty);
            if (!string.Equals(AreaProfileNameInput.Text, _areaProfileDraft.ProfileName, StringComparison.Ordinal))
            {
                AreaProfileNameInput.Text = _areaProfileDraft.ProfileName;
            }
            if (!string.Equals(AreaProfileEditor.Text, _areaProfileDraft.Content, StringComparison.Ordinal))
            {
                AreaProfileEditor.Text = _areaProfileDraft.Content;
                AreaProfileEditor.ScrollToHome();
                AreaProfileLineNumbersText.RenderTransform = new TranslateTransform();
            }
            UpdateAreaProfileLineNumbers(AreaProfileEditor.Text);

            AreaProfileValidationGrid.ItemsSource = _areaProfileDraft.Diagnostics;
            var contentByteCount = Encoding.UTF8.GetByteCount(_areaProfileDraft.Content);
            AreaProfileFileTitleText.Text = string.IsNullOrWhiteSpace(_areaProfileDraft.ProfileName)
                ? "新建 AREA 配置"
                : $"{_areaProfileDraft.ProfileName}.txt";
            AreaProfileValidCountText.Text = _areaProfileDraft.IsValid
                ? $"✓ {_areaProfileDraft.MesAreas.Count:N0} 个有效 AREA"
                : $"{_areaProfileDraft.Diagnostics.Count:N0} 项问题 · 无效";
            AreaProfileValidCountPill.SetResourceReference(
                FrameworkElement.StyleProperty,
                _areaProfileDraft.IsValid
                    ? "StatusPillSuccess"
                    : "StatusPillCritical");
            AreaProfileValidationSummaryText.Text = _areaProfileDraft.IsValid
                ? $"✓ 格式有效 · {contentByteCount:N0} B"
                : $"{_areaProfileDraft.Diagnostics.Count:N0} 项问题 · 非法草稿不可应用或保存";
            AreaProfileDiskStateText.Text = _areaProfileDraftIsDirty
                ? "草稿未保存"
                : _selectedAreaProfileName is null
                    ? "尚未保存"
                    : "磁盘版本未变化";
            AreaProfileValidationExpander.Visibility = _areaProfileDraft.IsValid
                ? Visibility.Collapsed
                : Visibility.Visible;
            var isNewProfile = _selectedAreaProfileName is null;
            var isSelectedProfileApplied = !isNewProfile
                && string.Equals(
                    _selectedAreaProfileName,
                    applied.ProfileName,
                    StringComparison.OrdinalIgnoreCase)
                && applied.MesAreas.SequenceEqual(
                    _areaProfileDraft.MesAreas,
                    StringComparer.Ordinal);
            AreaProfileDiscardButton.IsEnabled = _areaProfileDraftIsDirty;
            AreaProfileSaveButton.IsEnabled = _areaProfileDraft.IsValid
                && (isNewProfile || _areaProfileDraftIsDirty);
            AreaProfileApplyButton.IsEnabled = _areaProfileDraft.IsValid
                && (isNewProfile || _areaProfileDraftIsDirty || !isSelectedProfileApplied);
            AreaProfileSaveAsButton.IsEnabled = _areaProfileDraft.IsValid;
            AreaProfileRenameButton.IsEnabled = !isNewProfile && !_areaProfileDraftIsDirty;
            AreaProfileDeleteButton.IsEnabled = !isNewProfile && !_areaProfileDraftIsDirty;
            AreaProfileFileReloadButton.IsEnabled = !isNewProfile;
            AutomationProperties.SetName(
                AreaProfileFileTitleText,
                $"当前 AREA TXT 文件：{AreaProfileFileTitleText.Text}");
            AutomationProperties.SetName(
                AreaProfileValidCountText,
                $"AREA 配置有效数量：{AreaProfileValidCountText.Text}");
            AutomationProperties.SetName(
                AreaProfileValidationSummaryText,
                $"AREA 配置校验：{AreaProfileValidationSummaryText.Text}");
            AutomationProperties.SetName(
                AreaProfileDiskStateText,
                $"AREA 配置保存状态：{AreaProfileDiskStateText.Text}");
            RenderDataPageAreaProfileSelectors(reloadProfiles);
        }
        finally
        {
            _isRenderingAreaProfiles = false;
        }
    }

    private void ReloadAreaProfileRows(WatchAppliedAreaFilterProfile applied)
    {
        var rows = _areaProfileStore
            .EnumerateProfiles()
            .Select(summary =>
            {
                var parsed = _areaProfileStore.Load(summary.ProfileName);
                return new WatchAreaFilterProfilePresentationRow(
                    summary.ProfileName,
                    parsed.MesAreas.Count,
                    parsed.Diagnostics.Count,
                    parsed.IsValid,
                    summary.IsApplied,
                    summary.LastModifiedAt);
            })
            .ToArray();

        if (_selectedAreaProfileName is { } selectedName)
        {
            if (rows.FirstOrDefault(row => string.Equals(
                    row.ProfileName,
                    selectedName,
                    StringComparison.OrdinalIgnoreCase)) is { } selectedRow)
            {
                _selectedAreaProfileName = selectedRow.ProfileName;
                if (!_areaProfileDraftIsDirty)
                {
                    _areaProfileDraft = _areaProfileStore.Load(selectedRow.ProfileName);
                }
            }
            else
            {
                _selectedAreaProfileName = null;
                if (!_areaProfileDraftIsDirty)
                {
                    _areaProfileDraft = null;
                }
            }
        }

        if (_selectedAreaProfileName is null
            && !_areaProfileDraftIsDirty
            && applied.ProfileName is { } appliedProfileName
            && rows.FirstOrDefault(row => string.Equals(
                row.ProfileName,
                appliedProfileName,
                StringComparison.OrdinalIgnoreCase)) is { } appliedRow)
        {
            _selectedAreaProfileName = appliedRow.ProfileName;
            _areaProfileDraft = _areaProfileStore.Load(appliedRow.ProfileName);
        }

        _areaProfileRows = rows;
        _areaProfileRowsLoaded = true;
    }

    private void OnAreaProfileOpenDirectoryClick(object sender, RoutedEventArgs e)
    {
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(() =>
        {
            var directory = _areaProfileStore.DirectoryPath;
            Directory.CreateDirectory(directory);
            var disposition = _areaProfileDirectoryLauncher.Open(directory);
            ShowAreaProfileInfo(
                InfoBarSeverity.Informational,
                disposition == WatchAreaProfileDirectoryOpenDisposition.Opened
                    ? "AREA 配置目录已打开"
                    : "AREA 配置目录已准备",
                disposition == WatchAreaProfileDirectoryOpenDisposition.Opened
                    ? $"已通过平台文件管理器打开 {directory}。"
                    : $"已确认 {directory} 存在；UI 测试模式未启动文件管理器。");
            return Task.CompletedTask;
        });
    }

    private void OnAreaProfileReloadClick(object sender, RoutedEventArgs e)
    {
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(() =>
        {
            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
            var applied = _areaProfileStore.LoadAppliedState().CurrentApplied;
            ReloadAreaProfileRows(applied);
            RenderAreaProfiles();
            ShowAreaProfileInfo(
                InfoBarSeverity.Success,
                "AREA 配置已重新加载",
                $"已从本机 TXT 重新读取 {_areaProfileRows.Count:N0} 个配置；当前选择和未保存草稿已保留。数据库显示范围未改变。");
            return Task.CompletedTask;
        });
    }

    private void OnAreaProfileSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isRenderingAreaProfiles)
        {
            RenderAreaProfiles();
        }
    }

    private void OnAreaProfileEditorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (AreaProfileLineNumbersText.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            AreaProfileLineNumbersText.RenderTransform = transform;
        }

        transform.Y = -e.VerticalOffset;
    }

    private void UpdateAreaProfileLineNumbers(string content) =>
        AreaProfileLineNumbersText.Text = FormatAreaProfileLineNumbers(content);

    internal static string FormatAreaProfileLineNumbers(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lineCount = 1;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r')
            {
                lineCount++;
                if (index + 1 < content.Length && content[index + 1] == '\n')
                {
                    index++;
                }
            }
            else if (content[index] == '\n')
            {
                lineCount++;
            }
        }

        return string.Join(
            Environment.NewLine,
            Enumerable.Range(1, lineCount));
    }

    internal static string FormatAreaProfileDirectoryCaption(string directoryPath)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (TryFormatLocalApplicationDataCaption(
                fullPath,
                localApplicationData,
                out var caption))
        {
            return caption;
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable("MESINGEST_WATCH_UI_TEST_MODE"),
                "1",
                StringComparison.Ordinal)
            && TryFormatLocalApplicationDataCaption(
                fullPath,
                Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                out caption))
        {
            return caption;
        }

        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        return $"{directoryName} · 本机 TXT · UTF-8";
    }

    private static bool TryFormatLocalApplicationDataCaption(
        string fullPath,
        string? localApplicationDataPath,
        out string caption)
    {
        caption = string.Empty;
        if (string.IsNullOrWhiteSpace(localApplicationDataPath)
            || !Path.IsPathFullyQualified(localApplicationDataPath))
        {
            return false;
        }

        var localApplicationData = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(localApplicationDataPath));
        var isRoot = string.Equals(
            fullPath,
            localApplicationData,
            StringComparison.OrdinalIgnoreCase);
        var isDescendant = fullPath.StartsWith(
            localApplicationData + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        if (!isRoot && !isDescendant)
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(localApplicationData, fullPath);
        caption = relativePath == "."
            ? "%LocalAppData% · UTF-8"
            : $"%LocalAppData%\\{relativePath} · UTF-8";
        return true;
    }

    private void OnAreaProfileNewClick(object sender, RoutedEventArgs e)
    {
        BeginAreaProfileFileOperation(
            AreaProfileFileOperation.Create,
            "命名新配置",
            "new-area-filter",
            sender as IInputElement);
    }

    private void OnAreaProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingAreaProfiles
            || AreaProfileList.SelectedItem is not WatchAreaFilterProfilePresentationRow row)
        {
            return;
        }

        try
        {
            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
            _selectedAreaProfileName = row.ProfileName;
            _areaProfileDraft = _areaProfileStore.Load(row.ProfileName);
            _areaProfileDraftIsDirty = false;
            RenderAreaProfiles();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            ShowAreaProfileInfo(
                InfoBarSeverity.Error,
                "无法读取 AREA TXT 配置",
                exception.Message);
        }
    }

    private void OnAreaProfileDraftChanged(object sender, TextChangedEventArgs e)
    {
        if (_isRenderingAreaProfiles)
        {
            return;
        }

        var focusToPreserve = Keyboard.FocusedElement;
        CloseAreaProfileFileOperation(restoreInvokerFocus: false);
        var loadedFingerprint = _areaProfileDraft?.FileFingerprint;
        _areaProfileDraft = WatchAreaFilterProfileParser.Parse(
            AreaProfileNameInput.Text,
            AreaProfileEditor.Text) with
        {
            FileFingerprint = loadedFingerprint,
        };
        _areaProfileDraftIsDirty = true;
        RenderAreaProfiles();
        if (focusToPreserve is UIElement { IsVisible: true, IsEnabled: true } element
            && ReferenceEquals(Keyboard.FocusedElement, this))
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => element.Focus());
        }
    }

    private void OnAreaProfileDiscardClick(object sender, RoutedEventArgs e)
    {
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(() =>
        {
            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
            ReloadSelectedAreaProfileDraft();
            ShowAreaProfileInfo(
                InfoBarSeverity.Informational,
                "已放弃 AREA 草稿修改",
                "编辑器已恢复为所选 TXT 的磁盘内容；当前应用范围没有改变。");
            return Task.CompletedTask;
        });
    }

    private void OnAreaProfileFileReloadClick(object sender, RoutedEventArgs e)
    {
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(() =>
        {
            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
            ReloadSelectedAreaProfileDraft();
            ShowAreaProfileInfo(
                InfoBarSeverity.Success,
                "AREA TXT 已从磁盘加载",
                "编辑器已读取所选 TXT 的当前磁盘内容；当前应用范围没有改变。");
            return Task.CompletedTask;
        });
    }

    private void ReloadSelectedAreaProfileDraft()
    {
        if (_selectedAreaProfileName is { } selectedName)
        {
            _areaProfileDraft = _areaProfileStore.Load(selectedName);
            _areaProfileDraftIsDirty = false;
            RenderAreaProfiles(reloadProfiles: true);
            return;
        }

        var applied = _areaProfileStore.LoadAppliedState().CurrentApplied;
        if (applied.ProfileName is { } appliedName
            && _areaProfileStore.EnumerateProfiles().FirstOrDefault(summary => string.Equals(
                summary.ProfileName,
                appliedName,
                StringComparison.OrdinalIgnoreCase)) is { } appliedSummary)
        {
            _selectedAreaProfileName = appliedSummary.ProfileName;
            _areaProfileDraft = _areaProfileStore.Load(appliedSummary.ProfileName);
        }
        else
        {
            _selectedAreaProfileName = null;
            _areaProfileDraft = WatchAreaFilterProfileParser.Parse(string.Empty, string.Empty);
        }

        _areaProfileDraftIsDirty = false;
        RenderAreaProfiles(reloadProfiles: true);
    }

    private void OnAreaProfileSaveClick(object sender, RoutedEventArgs e)
    {
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(() =>
        {
            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
            var draft = CurrentAreaProfileDraft();
            var result = _selectedAreaProfileName is null
                ? _areaProfileStore.SaveAs(draft.ProfileName, draft.Content)
                : _areaProfileStore.Save(
                    draft.ProfileName,
                    draft.Content,
                    RequireLoadedAreaProfileFingerprint(draft));
            if (!result.Saved)
            {
                throw new InvalidOperationException(ProjectAreaDiagnostics(result.Diagnostics));
            }

            _selectedAreaProfileName = result.Draft.ProfileName;
            _areaProfileDraft = result.Draft;
            _areaProfileDraftIsDirty = false;
            ShowAreaProfileInfo(
                InfoBarSeverity.Success,
                "AREA 配置已保存",
                $"{result.Draft.ProfileName}.txt 已以 UTF-8 原子写入；当前应用范围未静默改变。");
            RenderAreaProfiles(reloadProfiles: true);
            return Task.CompletedTask;
        });
    }

    private void OnAreaProfileSaveAsClick(object sender, RoutedEventArgs e) =>
        BeginAreaProfileFileOperation(
            AreaProfileFileOperation.SaveAs,
            "另存为",
            string.Empty,
            sender as IInputElement);

    private void OnAreaProfileRenameClick(object sender, RoutedEventArgs e) =>
        BeginAreaProfileFileOperation(
            AreaProfileFileOperation.Rename,
            "重命名",
            _selectedAreaProfileName ?? string.Empty,
            sender as IInputElement);

    private void OnAreaProfileDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_selectedAreaProfileName is not { } selectedName)
        {
            return;
        }

        BeginAreaProfileFileOperation(
            AreaProfileFileOperation.Delete,
            $"再次确认删除“{selectedName}.txt”",
            selectedName,
            sender as IInputElement);
    }

    private void BeginAreaProfileFileOperation(
        AreaProfileFileOperation operation,
        string prompt,
        string targetName,
        IInputElement? invoker)
    {
        var sourceProfileName = operation is AreaProfileFileOperation.Rename
            or AreaProfileFileOperation.Delete
                ? _selectedAreaProfileName
                    ?? throw new InvalidOperationException(
                        "请先选择要操作的 AREA TXT 配置。")
                : null;
        string? sourceFileFingerprint = null;
        if (operation is AreaProfileFileOperation.Rename or AreaProfileFileOperation.Delete)
        {
            try
            {
                var appliedState = _areaProfileStore.LoadAppliedState();
                if (appliedState.Diagnostic is { } diagnostic)
                {
                    CloseAreaProfileFileOperation(restoreInvokerFocus: false);
                    ShowAreaProfileInfo(
                        InfoBarSeverity.Error,
                        "无法确认已应用 AREA 范围",
                        $"{diagnostic.Message} 文件操作已取消；请先修复标记或明确应用全部 AREA。");
                    return;
                }

                var displayedFingerprint = _areaProfileDraft?.FileFingerprint;
                var source = _areaProfileStore.Load(sourceProfileName);
                if (displayedFingerprint is null
                    || !string.Equals(
                        _areaProfileDraft?.ProfileName,
                        sourceProfileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    CloseAreaProfileFileOperation(restoreInvokerFocus: false);
                    ShowAreaProfileInfo(
                        InfoBarSeverity.Error,
                        "无法准备 AREA 文件操作",
                        "当前 AREA TXT 未记录所显示的磁盘版本；请重新加载后再试。");
                    return;
                }

                if (source.FileFingerprint is null
                    || !string.Equals(
                        source.FileFingerprint,
                        displayedFingerprint,
                        StringComparison.Ordinal))
                {
                    CloseAreaProfileFileOperation(restoreInvokerFocus: false);
                    ShowAreaProfileInfo(
                        InfoBarSeverity.Error,
                        "AREA TXT 已在磁盘更改",
                        ProjectAreaDiagnostics(
                            [
                                new WatchAreaFilterProfileDiagnostic(
                                    WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk,
                                    "所显示的 AREA TXT 已在磁盘更改；请重新加载后再选择文件操作。"),
                            ]));
                    return;
                }

                sourceFileFingerprint = displayedFingerprint;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                CloseAreaProfileFileOperation(restoreInvokerFocus: false);
                ShowAreaProfileInfo(
                    InfoBarSeverity.Error,
                    "无法准备 AREA 文件操作",
                    exception.Message);
                return;
            }
        }

        _areaProfileFileOperationConfirmation = new AreaProfileFileOperationConfirmation(
            operation,
            sourceProfileName,
            sourceFileFingerprint,
            invoker);
        AreaProfileFileOperationPromptText.Text = operation switch
        {
            AreaProfileFileOperation.Rename => $"重命名“{sourceProfileName}.txt”",
            AreaProfileFileOperation.Delete =>
                $"再次确认删除“{sourceProfileName}.txt”；若确认时该配置为当前应用，删除将回退为全部 AREA，并扩大概览、需求系列和资格审计范围",
            _ => prompt,
        };
        AreaProfileFileOperationConfirmButton.Content = operation switch
        {
            AreaProfileFileOperation.Create => "确认名称",
            AreaProfileFileOperation.SaveAs => "另存为",
            AreaProfileFileOperation.Rename => "重命名",
            AreaProfileFileOperation.Delete => "确认删除",
            _ => "确认",
        };
        AreaProfileTargetNameInput.Visibility = operation == AreaProfileFileOperation.Delete
            ? Visibility.Collapsed
            : Visibility.Visible;
        AreaProfileTargetNameInput.Text = targetName;
        AreaProfileFileOperationPanel.Visibility = Visibility.Visible;
        AutomationProperties.SetName(
            AreaProfileFileOperationPanel,
            AreaProfileFileOperationPromptText.Text);
        AreaProfileFileOperationPromptText.ToolTip = AreaProfileFileOperationPromptText.Text;
        AutomationProperties.SetHelpText(
            AreaProfileFileOperationConfirmButton,
            AreaProfileFileOperationPromptText.Text);
        AutomationProperties.SetName(
            AreaProfileFileOperationConfirmButton,
            operation switch
            {
                AreaProfileFileOperation.Rename => $"确认重命名 {sourceProfileName}.txt",
                AreaProfileFileOperation.Delete => $"确认删除 {sourceProfileName}.txt",
                AreaProfileFileOperation.Create => "确认新建 AREA 配置名称",
                AreaProfileFileOperation.SaveAs => "确认另存 AREA TXT 配置",
                _ => "确认 AREA 文件操作",
            });
        if (AreaProfileTargetNameInput.Visibility == Visibility.Visible)
        {
            AreaProfileTargetNameInput.Focus();
            AreaProfileTargetNameInput.SelectAll();
        }
        else
        {
            AreaProfileFileOperationConfirmButton.Focus();
        }
    }

    private void OnAreaProfileFileOperationCancelClick(object sender, RoutedEventArgs e) =>
        CloseAreaProfileFileOperation();

    private void CloseAreaProfileFileOperation(
        bool restoreInvokerFocus = true,
        IInputElement? focusFallback = null)
    {
        var confirmation = _areaProfileFileOperationConfirmation;
        var focusedInsidePanel = restoreInvokerFocus
            && Keyboard.FocusedElement is DependencyObject focused
            && IsDescendantOrSelf(focused, AreaProfileFileOperationPanel);
        _areaProfileFileOperationConfirmation = null;
        AreaProfileFileOperationPanel.Visibility = Visibility.Collapsed;
        AreaProfileTargetNameInput.Visibility = Visibility.Visible;
        AreaProfileTargetNameInput.Clear();
        AutomationProperties.SetHelpText(AreaProfileFileOperationConfirmButton, string.Empty);
        AreaProfileFileOperationPromptText.ToolTip = null;
        if (focusedInsidePanel)
        {
            var focusTarget = focusFallback ?? confirmation?.Invoker;
            if (focusTarget is UIElement { IsVisible: true, IsEnabled: true } element)
            {
                element.Focus();
            }
        }
    }

    private static bool IsDescendantOrSelf(
        DependencyObject candidate,
        DependencyObject ancestor)
    {
        for (DependencyObject? current = candidate;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void OnAreaProfileFileOperationConfirmClick(object sender, RoutedEventArgs e)
    {
        var confirmation = _areaProfileFileOperationConfirmation;
        if (confirmation is null)
        {
            AreaProfileOperationTask = Task.CompletedTask;
            return;
        }

        var targetName = AreaProfileTargetNameInput.Text;
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(async operation =>
        {
            switch (confirmation.Operation)
            {
                case AreaProfileFileOperation.Create:
                {
                    var profileName = RequireSafeAreaProfileName(targetName);
                    _selectedAreaProfileName = null;
                    _areaProfileDraft = WatchAreaFilterProfileParser.Parse(profileName, string.Empty);
                    _areaProfileDraftIsDirty = true;
                    CloseAreaProfileFileOperation();
                    ShowAreaProfileInfo(
                        InfoBarSeverity.Informational,
                        "新 AREA 草稿已命名",
                        "请填写至少一个有效 AREA，然后保存；尚未创建或应用本机 TXT 文件。");
                    RenderAreaProfiles();
                    AreaProfileEditor.Focus();
                    break;
                }
                case AreaProfileFileOperation.SaveAs:
                {
                    var draft = CurrentAreaProfileDraft();
                    var result = _areaProfileStore.SaveAs(
                        targetName,
                        draft.Content);
                    if (!result.Saved)
                    {
                        throw new InvalidOperationException(ProjectAreaDiagnostics(result.Diagnostics));
                    }

                    _selectedAreaProfileName = result.Draft.ProfileName;
                    _areaProfileDraft = result.Draft;
                    _areaProfileDraftIsDirty = false;
                    CloseAreaProfileFileOperation();
                    ShowAreaProfileInfo(
                        InfoBarSeverity.Success,
                        "AREA 配置已另存为",
                        $"已创建 {result.Draft.ProfileName}.txt；原文件和当前应用范围均未改变。");
                    RenderAreaProfiles(reloadProfiles: true);
                    break;
                }
                case AreaProfileFileOperation.Rename:
                {
                    var selectedName = confirmation.SourceProfileName
                        ?? throw new InvalidOperationException("请先选择要重命名的 AREA TXT 配置。");
                    if (_areaProfileDraftIsDirty)
                    {
                        CloseAreaProfileFileOperation();
                        throw new InvalidOperationException("请先保存或放弃未保存修改，再重命名当前 AREA TXT 配置。");
                    }

                    var result = _areaProfileStore.Rename(
                        selectedName,
                        targetName,
                        confirmation.SourceFileFingerprint
                            ?? throw new InvalidOperationException(
                                "重命名确认缺少源文件指纹；请重新选择并确认。"));
                    if (!result.Renamed)
                    {
                        if (HasAreaProfileDiagnostic(
                                result.Diagnostics,
                                WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk))
                        {
                            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
                        }

                        throw new InvalidOperationException(ProjectAreaDiagnostics(result.Diagnostics));
                    }

                    _selectedAreaProfileName = result.Draft.ProfileName;
                    _areaProfileDraft = result.Draft;
                    _areaProfileDraftIsDirty = false;
                    CloseAreaProfileFileOperation();
                    if (string.Equals(
                            result.CurrentApplied.ProfileName,
                            result.Draft.ProfileName,
                            StringComparison.OrdinalIgnoreCase)
                        && _areaContext.MesAreas.SequenceEqual(
                            result.CurrentApplied.MesAreas,
                            StringComparer.Ordinal))
                    {
                        _areaContext = result.CurrentApplied.ToDisplayContext();
                        RenderWorkspace();
                    }

                    ShowAreaProfileInfo(
                        InfoBarSeverity.Success,
                        "AREA 配置已重命名",
                        $"{selectedName}.txt 已重命名为 {result.Draft.ProfileName}.txt；AREA 内容未改变。");
                    RenderAreaProfiles(reloadProfiles: true);
                    break;
                }
                case AreaProfileFileOperation.Delete:
                {
                    var selectedName = confirmation.SourceProfileName
                        ?? throw new InvalidOperationException("请先选择要删除的 AREA TXT 配置。");
                    if (_areaProfileDraftIsDirty)
                    {
                        CloseAreaProfileFileOperation();
                        throw new InvalidOperationException("请先保存或放弃未保存修改，再删除当前 AREA TXT 配置。");
                    }

                    var result = _areaProfileStore.Delete(
                        selectedName,
                        confirmation.SourceFileFingerprint
                            ?? throw new InvalidOperationException(
                                "删除确认缺少源文件指纹；请重新选择并确认。"));
                    if (!result.Deleted)
                    {
                        if (HasAreaProfileDiagnostic(
                                result.Diagnostics,
                                WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk))
                        {
                            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
                        }

                        throw new InvalidOperationException(ProjectAreaDiagnostics(result.Diagnostics));
                    }

                    _selectedAreaProfileName = null;
                    _areaProfileDraft = WatchAreaFilterProfileParser.Parse(string.Empty, string.Empty);
                    _areaProfileDraftIsDirty = false;
                    CloseAreaProfileFileOperation(focusFallback: AreaProfileNewButton);
                    if (result.AppliedProfileWasDeleted)
                    {
                        await ApplyAreaContextAsync(
                                result.CurrentApplied.ToDisplayContext(),
                                _lifetimeCancellation.Token)
                            .ConfigureAwait(true);
                        if (!IsCurrentAreaProfileOperation(operation))
                        {
                            return;
                        }

                    }

                    ShowAreaProfileInfo(
                        InfoBarSeverity.Success,
                        "AREA 配置已删除",
                        result.AppliedProfileWasDeleted
                            ? $"{selectedName}.txt 已删除；该配置原为当前应用范围，现已明确回退到全部 AREA。"
                            : $"{selectedName}.txt 已删除；当前应用范围未改变。");
                    RenderAreaProfiles(reloadProfiles: true);
                    await Dispatcher.InvokeAsync(
                        () =>
                        {
                            AreaProfileList.BringIntoView();
                            Keyboard.Focus(AreaProfileList);
                        },
                        DispatcherPriority.Input);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(confirmation),
                        confirmation.Operation,
                        null);
            }
        });
    }

    private static string RequireSafeAreaProfileName(string? profileName)
    {
        var nameCheck = WatchAreaFilterProfileParser.Parse(profileName, "A1-1");
        if (!nameCheck.IsValid)
        {
            throw new InvalidOperationException(ProjectAreaDiagnostics(nameCheck.Diagnostics));
        }

        return nameCheck.ProfileName;
    }

    private static string RequireLoadedAreaProfileFingerprint(
        WatchAreaFilterProfile draft) => draft.FileFingerprint
        ?? throw new InvalidOperationException(
            "当前 AREA TXT 未记录磁盘版本；请重新加载后再保存。");

    private static bool HasAreaProfileDiagnostic(
        IReadOnlyList<WatchAreaFilterProfileDiagnostic> diagnostics,
        string code) => diagnostics.Any(diagnostic => string.Equals(
        diagnostic.Code,
        code,
        StringComparison.Ordinal));

    private void OnAreaProfileApplyClick(object sender, RoutedEventArgs e)
    {
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(async operation =>
        {
            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
            var draft = CurrentAreaProfileDraft();
            var result = _selectedAreaProfileName is null
                ? _areaProfileStore.SaveAsAndApply(draft.ProfileName, draft.Content)
                : _areaProfileStore.SaveAndApply(
                    draft.ProfileName,
                    draft.Content,
                    RequireLoadedAreaProfileFingerprint(draft));
            if (!result.Saved)
            {
                throw new InvalidOperationException(ProjectAreaDiagnostics(result.Diagnostics));
            }

            _selectedAreaProfileName = result.Draft.ProfileName;
            _areaProfileDraft = result.Draft;
            _areaProfileDraftIsDirty = false;
            RenderAreaProfiles(reloadProfiles: true);
            if (!result.Applied && result.ApplyDiagnostic is { } applyDiagnostic)
            {
                ShowAreaProfileInfo(
                    InfoBarSeverity.Error,
                    "AREA 配置已保存但范围未应用",
                    applyDiagnostic.Message);
                return;
            }

            if (!result.Applied)
            {
                throw new InvalidOperationException(ProjectAreaDiagnostics(result.Diagnostics));
            }

            _selectedAreaProfileName = result.Draft.ProfileName;
            _areaProfileDraftIsDirty = false;
            await ApplyAreaContextAsync(
                    result.CurrentApplied.ToDisplayContext(),
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            if (!IsCurrentAreaProfileOperation(operation))
            {
                return;
            }
            ShowAreaProfileInfo(
                InfoBarSeverity.Success,
                "AREA 配置已应用",
                "概览、需求系列和资格审计已清除冻结游标并从第一页重新读取；错误检索与接入告警未改变。");
            RenderAreaProfiles(reloadProfiles: true);
        });
    }

    private void OnAreaApplyAllAreasClick(object sender, RoutedEventArgs e)
    {
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(async operation =>
        {
            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
            var result = _areaProfileStore.ApplyAllAreas();
            await ApplyAreaContextAsync(
                    result.CurrentApplied.ToDisplayContext(),
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            if (!IsCurrentAreaProfileOperation(operation))
            {
                return;
            }
            ShowAreaProfileInfo(
                InfoBarSeverity.Success,
                "已应用全部 AREA",
                "本机范围标记已持久化；三个 AREA 相关只读视图已从第一页重新读取。");
            RenderAreaProfiles(reloadProfiles: true);
        });
    }

    private WatchAreaFilterProfile CurrentAreaProfileDraft()
    {
        var loadedFingerprint = _areaProfileDraft?.FileFingerprint;
        return _areaProfileDraft = WatchAreaFilterProfileParser.Parse(
            AreaProfileNameInput.Text,
            AreaProfileEditor.Text) with
        {
            FileFingerprint = loadedFingerprint,
        };
    }

    private Task RunAreaProfileUiActionAsync(Func<Task> action) =>
        RunAreaProfileUiActionAsync(_ => action());

    private async Task RunAreaProfileUiActionAsync(Func<long, Task> action)
    {
        var operation = Interlocked.Increment(ref _areaProfileOperationGeneration);
        try
        {
            await action(operation).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the window is a neutral end to an in-flight action.
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or WatchOverviewException
            or DemandSeriesBrowseException
            or ReadabilityAuditException)
        {
            if (operation == Interlocked.Read(ref _areaProfileOperationGeneration))
            {
                ShowAreaProfileInfo(
                    InfoBarSeverity.Error,
                    "无法完成 AREA 配置操作",
                    exception.Message);
                RenderAreaProfiles();
            }
        }
    }

    private bool IsCurrentAreaProfileOperation(long operation) =>
        operation == Interlocked.Read(ref _areaProfileOperationGeneration);

    private void ShowAreaProfileInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        AreaProfileInfoBar.Severity = severity;
        AreaProfileInfoBar.Title = title;
        AreaProfileInfoBar.Message = message;
        AreaProfileInfoBar.IsOpen = true;
        AutomationProperties.SetName(AreaProfileInfoBar, $"{title}。{message}");
    }

    private static string ProjectAreaDiagnostics(
        IReadOnlyList<WatchAreaFilterProfileDiagnostic> diagnostics) => diagnostics.Count == 0
        ? "AREA 配置未通过校验。"
        : string.Join("；", diagnostics.Select(diagnostic => diagnostic.LineNumber is { } line
            ? $"第 {line} 行 {diagnostic.Code}: {diagnostic.Message}"
            : $"{diagnostic.Code}: {diagnostic.Message}"));
}
