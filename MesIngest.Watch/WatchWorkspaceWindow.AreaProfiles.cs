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
    IReadOnlyList<string> MesAreas,
    int DiagnosticCount,
    bool IsValid,
    bool IsApplied,
    DateTimeOffset? FileLastModifiedAt,
    WatchAreaFilterProfileAvailability Availability)
{
    public bool IsAllAreas { get; init; }

    public int MesAreaCount => MesAreas.Count;

    public bool IsMissing =>
        Availability == WatchAreaFilterProfileAvailability.AppliedSnapshotWithoutFile;

    /// <summary>
    /// The file no longer parses to the AREA sequence the display scope was
    /// taken from. Only carries meaning while <see cref="IsApplied"/>.
    /// </summary>
    public bool HasDrifted { get; init; }

    public string? LastModifiedText => FileLastModifiedAt is { } lastModifiedAt
        ? WatchTimeDisplay.Format(lastModifiedAt)
        : null;

    /// <summary>
    /// The badge belongs to "currently applied" and to nothing else. Invalid
    /// content and drift are a second, orthogonal dimension carried by
    /// <see cref="AttentionText"/>, so neither one can hide the other.
    /// </summary>
    public string AppliedBadgeText => "当前应用";

    /// <summary>
    /// Spoken only. On screen the corner shows 有效 for a healthy profile and
    /// hands the slot over to the badge or to <see cref="AttentionText"/>
    /// otherwise, but a screen reader still needs the word said out loud.
    /// </summary>
    public string ValidityText => IsValid ? "有效" : "无效";

    public string? AttentionText => this switch
    {
        { IsMissing: true } => "文件已删除 · 范围仍生效",
        { IsValid: false, IsApplied: true } => "内容非法 · 待重新应用",
        { IsValid: false } => "内容非法 · 需修复",
        { IsApplied: true, HasDrifted: true } => "待重新应用",
        _ => null,
    };

    public bool HasAttention => AttentionText is not null;

    public bool CanSaveAs => !IsAllAreas && (CanSaveAsOverride ?? IsValid);

    public bool? CanSaveAsOverride { get; init; }

    public bool CanRenameOrDelete =>
        !IsAllAreas && !IsMissing && !BlocksIdentityChangingCommands;

    public bool BlocksIdentityChangingCommands { get; init; }

    public string MetadataText => IsAllAreas
        ? "不限制显示范围"
        : IsMissing
        ? $"{MesAreaCount:N0} 个 AREA · 已应用快照"
        : $"{MesAreaCount:N0} 个 AREA · {LastModifiedText} 修改";

    public string AutomationName => string.Join(
        '；',
        new[]
        {
            ProfileName,
            IsAllAreas ? null : $"{MesAreaCount:N0} 个 AREA",
            IsApplied ? AppliedBadgeText : IsMissing ? "文件已删除" : ValidityText,
            AttentionText,
            IsAllAreas
                ? "不限制显示范围"
                : IsMissing ? "已应用快照" : $"{LastModifiedText} 修改",
        }.Where(part => part is not null));
}

/// <summary>
/// What the apply button offers for the selected profile. The display scope is
/// the AREA snapshot taken when the user last pressed apply, so what separates
/// <see cref="Applied"/> from <see cref="Reapply"/> is that snapshot, never the
/// file's bytes.
/// </summary>
internal enum WatchAreaProfileApplyAction
{
    Apply,
    Applied,
    Reapply,
}

/// <summary>
/// Where the selected profile stands. <see cref="Missing"/> is reserved for a
/// profile that is still the applied one after its file went away: the scope
/// outlives the file, so the button keeps naming it instead of vanishing.
/// </summary>
internal enum WatchAreaProfileFileCondition
{
    Valid,
    Invalid,
    Missing,
}

internal sealed record WatchAreaProfileApplyButtonState(
    WatchAreaProfileApplyAction Action,
    bool IsEnabled,
    string? BlockedReason)
{
    public string Content => Action switch
    {
        WatchAreaProfileApplyAction.Applied => "已应用",
        WatchAreaProfileApplyAction.Reapply => "重新应用",
        _ => "应用此配置",
    };

    public string AutomationName => Action switch
    {
        WatchAreaProfileApplyAction.Applied => "选中 AREA 配置已是当前显示范围",
        WatchAreaProfileApplyAction.Reapply => "重新应用选中 AREA 配置",
        _ => "应用选中 AREA 配置",
    } + (BlockedReason is { } reason ? $"；{reason}" : string.Empty);

    /// <summary>
    /// The five button appearances the spec fixed: the two blocked 重新应用
    /// rows look alike and differ only in the reason they carry. Editing a file
    /// never moves the display scope, so the only thing that turns 已应用 into
    /// 重新应用 is the parsed AREA sequence drifting away from the snapshot —
    /// comments and blank lines change the file without changing that sequence.
    /// </summary>
    public static WatchAreaProfileApplyButtonState Evaluate(
        bool isCurrentApplied,
        WatchAreaProfileFileCondition condition,
        bool matchesAppliedSnapshot) => (isCurrentApplied, condition) switch
    {
        (false, WatchAreaProfileFileCondition.Valid) =>
            new(WatchAreaProfileApplyAction.Apply, true, null),
        (false, _) =>
            new(WatchAreaProfileApplyAction.Apply, false, null),
        (true, WatchAreaProfileFileCondition.Missing) =>
            new(
                WatchAreaProfileApplyAction.Reapply,
                false,
                "文件已删除 · 当前显示范围仍生效"),
        (true, WatchAreaProfileFileCondition.Invalid) =>
            new(
                WatchAreaProfileApplyAction.Reapply,
                false,
                "内容非法不可应用 · 当前显示范围保持不变"),
        _ => matchesAppliedSnapshot
            ? new(WatchAreaProfileApplyAction.Applied, false, null)
            : new(WatchAreaProfileApplyAction.Reapply, true, null),
    };
}

internal partial class WatchWorkspaceWindow
{
    private const string AllAreasProfileDisplayName = "全部 AREA（不筛选）";

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

    private sealed record AreaProfileEditorViewState(
        int CaretIndex,
        int SelectionStart,
        int SelectionLength,
        int FirstVisibleLine,
        double HorizontalOffset,
        double VerticalOffset);

    private WatchAreaFilterProfileStore _areaProfileStore = null!;
    private IWatchAreaProfileDirectoryLauncher _areaProfileDirectoryLauncher = null!;
    private TimeProvider _areaProfileClock = TimeProvider.System;
    private ITimer? _areaProfileAutoSaveTimer;
    private IReadOnlyList<WatchAreaFilterProfilePresentationRow> _areaProfileRows = [];
    private WatchAreaFilterProfile? _areaProfileDraft;
    private string? _selectedAreaProfileName;
    private bool _isAllAreasSelected;
    private string? _areaProfileStartupError;
    private bool _areaProfileDraftIsDirty;
    private bool _areaProfileRowsLoaded;
    private bool _isRenderingAreaProfiles;
    private string? _areaProfileWriteConflictProfileName;
    private bool _areaProfileDraftLostItsFile;
    private AreaProfileFileOperationConfirmation? _areaProfileFileOperationConfirmation;
    private long _areaProfileOperationGeneration;

    internal Task AreaProfileOperationTask { get; private set; } = Task.CompletedTask;

    private void InitializeAreaFilterProfiles(
        string? areaFilterProfilesDirectoryPath,
        TimeProvider? timeProvider,
        IWatchAreaProfileDirectoryLauncher? areaProfileDirectoryLauncher,
        IWatchAreaProfileDirectoryEventSource? areaProfileDirectoryEventSource)
    {
        _areaProfileClock = timeProvider ?? TimeProvider.System;
        _areaProfileStore = new WatchAreaFilterProfileStore(
            areaFilterProfilesDirectoryPath,
            timeProvider,
            directoryEventSource: areaProfileDirectoryEventSource);
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

    private void InitializeAreaFilterProfilePage()
    {
        WatchGridClipboardBehavior.Attach(AreaProfileValidationGrid, preserveSelectionUnit: true);
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

        StartWatchingAreaProfileDirectory();
    }

    private void StartWatchingAreaProfileDirectory()
    {
        try
        {
            _areaProfileStore.DirectoryChanged += OnAreaProfileDirectoryChanged;
            _areaProfileStore.StartWatchingDirectory();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            _areaProfileStore.DirectoryChanged -= OnAreaProfileDirectoryChanged;
            ShowAreaProfileInfo(
                InfoBarSeverity.Warning,
                "无法监视 AREA 配置目录",
                $"列表不会自动跟随目录变化。{exception.Message}");
        }
    }

    private void OnAreaProfileDirectoryChanged(
        object? sender,
        WatchAreaProfileDirectoryChange change)
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            QueueAreaProfileDirectoryChange(change);
            return;
        }

        Dispatcher.BeginInvoke(() => QueueAreaProfileDirectoryChange(change));
    }

    private void QueueAreaProfileDirectoryChange(WatchAreaProfileDirectoryChange change)
    {
        FollowSelectedAreaProfileRename(change.Renames);
        var selectedProfileToReload = !_areaProfileDraftIsDirty
            && _selectedAreaProfileName is { } selectedName
            && change.AffectedProfileNames.Contains(
                selectedName,
                StringComparer.OrdinalIgnoreCase)
            && !change.DeletedProfileNames.Contains(
                selectedName,
                StringComparer.OrdinalIgnoreCase)
                ? selectedName
                : null;
        if (selectedProfileToReload is null)
        {
            ApplyAreaProfileDirectoryChange(reloadedDraft: null);
            AreaProfileOperationTask = Task.CompletedTask;
            return;
        }

        var loadTask = _areaProfileStore.LoadAfterExternalChangeAsync(
            selectedProfileToReload,
            _lifetimeCancellation.Token);
        if (loadTask.IsCompletedSuccessfully)
        {
            ApplyAreaProfileDirectoryChange(loadTask.Result);
            AreaProfileOperationTask = Task.CompletedTask;
            return;
        }

        AreaProfileOperationTask = RunAreaProfileUiActionAsync(async () =>
        {
            var reloadedDraft = await loadTask.ConfigureAwait(false);
            await Dispatcher.InvokeAsync(
                () => ApplyAreaProfileDirectoryChange(reloadedDraft));
        });
    }

    private void FollowSelectedAreaProfileRename(
        IReadOnlyList<WatchAreaProfileRename> renames)
    {
        foreach (var rename in renames)
        {
            if (!string.Equals(
                    _selectedAreaProfileName,
                    rename.PreviousProfileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _selectedAreaProfileName = rename.NewProfileName;
            if (_areaProfileDraft is not null)
            {
                _areaProfileDraft = _areaProfileDraft with
                {
                    ProfileName = rename.NewProfileName,
                };
            }
        }
    }

    /// <summary>
    /// Re-reads the directory into the list and, when supplied, replaces the
    /// selected clean editor buffer without changing its view or keyboard focus.
    /// </summary>
    private void ApplyAreaProfileDirectoryChange(WatchAreaFilterProfile? reloadedDraft)
    {
        if (_disposed || AreaProfileList is null)
        {
            return;
        }

        try
        {
            AreaProfileEditorViewState? editorViewState = null;
            if (reloadedDraft is not null
                && !_areaProfileDraftIsDirty
                && _selectedAreaProfileName is { } selectedName
                && string.Equals(
                    reloadedDraft.ProfileName,
                    selectedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(
                        AreaProfileEditor.Text,
                        reloadedDraft.Content,
                        StringComparison.Ordinal))
                {
                    editorViewState = CaptureAreaProfileEditorViewState();
                }

                _areaProfileDraft = reloadedDraft;
            }

            ReloadAreaProfileRows(
                _areaProfileStore.LoadAppliedState().CurrentApplied,
                reloadSelectedDraft: false);
            RenderAreaProfiles(editorViewState: editorViewState);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            ShowAreaProfileInfo(
                InfoBarSeverity.Warning,
                "无法读取本机 AREA 配置",
                $"配置列表可能不是最新的。{exception.Message}");
        }
    }

    private void RenderAreaProfiles(
        bool reloadProfiles = false,
        AreaProfileEditorViewState? editorViewState = null)
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
                    IsApplied = row.IsAllAreas
                        ? applied.IsAllAreas
                        : string.Equals(
                            row.ProfileName,
                            applied.ProfileName,
                            StringComparison.OrdinalIgnoreCase),
                })
                .ToArray();

            var areaProfileDirectoryPath = Path.GetFullPath(_areaProfileStore.DirectoryPath);
            AreaProfileDirectoryText.Text = FormatAreaProfileDirectoryCaption(
                areaProfileDirectoryPath);
            AreaProfileDirectoryText.ToolTip = areaProfileDirectoryPath;
            AutomationProperties.SetHelpText(
                AreaProfileDirectoryText,
                areaProfileDirectoryPath);
            if (!_isAllAreasSelected
                && _selectedAreaProfileName is null
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

            if (!_isAllAreasSelected
                && _selectedAreaProfileName is null
                && !_areaProfileDraftIsDirty
                && applied.IsAllAreas)
            {
                SelectAllAreasRow();
            }

            _areaProfileDraft ??= WatchAreaFilterProfileParser.Parse(
                string.Empty,
                string.Empty);
            var applyState = EvaluateAreaProfileApplyState(applied, _areaProfileDraft);
            _areaProfileRows = MarkDriftedAreaProfileRows(
                _areaProfileRows,
                applied,
                _areaProfileDraft);
            var searchText = AreaProfileSearchInput.Text.Trim();
            var visibleRows = FilterAreaProfileRowsBySearch(_areaProfileRows);
            var fileCount = _areaProfileRows.Count(row => !row.IsAllAreas && !row.IsMissing);
            var invalidCount = _areaProfileRows.Count(row =>
                !row.IsAllAreas && !row.IsMissing && !row.IsValid);
            AreaProfileListSummaryText.Text = searchText.Length == 0
                ? $"{fileCount:N0} 个文件 · {invalidCount:N0} 个需要修复"
                : $"显示 {visibleRows.Count(row => !row.IsAllAreas):N0} / {fileCount:N0} 个配置"
                    + $" · {invalidCount:N0} 个需要修复";

            AreaProfileAppliedStateText.Text = applied.AppliedAt is { } appliedAt
                ? $"当前应用：{applied.DisplaySummary} · {WatchTimeDisplay.Format(appliedAt)}"
                : $"当前应用：{applied.DisplaySummary}";
            AutomationProperties.SetName(
                AreaProfileAppliedStateText,
                applied.MesAreas.Count == 0
                    ? AreaProfileAppliedStateText.Text
                    : $"{AreaProfileAppliedStateText.Text}；AREA {string.Join('、', applied.MesAreas)}");
            AreaProfileList.ItemsSource = visibleRows;
            AreaProfileList.SelectedItem = visibleRows.FirstOrDefault(row =>
                row.IsAllAreas
                    ? _isAllAreasSelected
                    : string.Equals(
                        row.ProfileName,
                        _selectedAreaProfileName,
                        StringComparison.OrdinalIgnoreCase));

            if (!string.Equals(AreaProfileNameInput.Text, _areaProfileDraft.ProfileName, StringComparison.Ordinal))
            {
                AreaProfileNameInput.Text = _areaProfileDraft.ProfileName;
            }
            if (!string.Equals(AreaProfileEditor.Text, _areaProfileDraft.Content, StringComparison.Ordinal))
            {
                AreaProfileEditor.Text = _areaProfileDraft.Content;
                if (editorViewState is null)
                {
                    AreaProfileEditor.ScrollToHome();
                    AreaProfileLineNumbersText.RenderTransform = new TranslateTransform();
                }
                else
                {
                    RestoreAreaProfileEditorViewState(editorViewState);
                }
            }
            UpdateAreaProfileLineNumbers(AreaProfileEditor.Text);

            AreaProfileEditor.IsReadOnly = _isAllAreasSelected;
            AreaProfileValidationGrid.ItemsSource = _isAllAreasSelected
                ? Array.Empty<WatchAreaFilterProfileDiagnostic>()
                : _areaProfileDraft.Diagnostics;
            var contentByteCount = Encoding.UTF8.GetByteCount(_areaProfileDraft.Content);
            AreaProfileFileTitleText.Text = _isAllAreasSelected
                ? AllAreasProfileDisplayName
                : string.IsNullOrWhiteSpace(_areaProfileDraft.ProfileName)
                    ? "新建 AREA 配置"
                    : $"{_areaProfileDraft.ProfileName}.txt";
            AreaProfileValidCountText.Text = _isAllAreasSelected
                ? "不限制显示范围"
                : _areaProfileDraft.IsValid
                    ? $"✓ {_areaProfileDraft.MesAreas.Count:N0} 个有效 AREA"
                    : $"{_areaProfileDraft.Diagnostics.Count:N0} 项问题 · 无效";
            AreaProfileValidCountPill.SetResourceReference(
                FrameworkElement.StyleProperty,
                _isAllAreasSelected || _areaProfileDraft.IsValid
                    ? "StatusPillSuccess"
                    : "StatusPillCritical");
            AreaProfileValidationSummaryText.Text = _isAllAreasSelected
                ? "显示所有 AREA，不应用 TXT 筛选"
                : applyState.BlockedReason
                    ?? (_areaProfileDraft.IsValid
                        ? $"✓ 格式有效 · {contentByteCount:N0} B"
                        : $"{_areaProfileDraft.Diagnostics.Count:N0} 项问题 · 非法内容不可应用");
            AreaProfileDiskStateText.Text = DescribeAreaProfileDiskState();
            AreaProfileValidationExpander.Visibility = _isAllAreasSelected
                || _areaProfileDraft.IsValid
                ? Visibility.Collapsed
                : Visibility.Visible;
            AreaProfileApplyButton.Content = applyState.Content;
            AreaProfileApplyButton.IsEnabled = applyState.IsEnabled;
            AutomationProperties.SetName(
                AreaProfileApplyButton,
                applyState.AutomationName);
            // A file-backed draft uses the row context menu. Once an external
            // delete has taken away the row, the surviving unnamed buffer gets
            // one recovery command in the same left card instead.
            AreaProfileSaveDraftAsButton.Visibility = _areaProfileDraftLostItsFile
                ? Visibility.Visible
                : Visibility.Collapsed;
            AreaProfileSaveDraftAsButton.IsEnabled = _areaProfileDraftLostItsFile
                && !WatchAreaFilterProfileStore.HasContentDiagnostics(_areaProfileDraft);
            AutomationProperties.SetName(
                AreaProfileFileTitleText,
                _isAllAreasSelected
                    ? $"当前 AREA 范围：{AreaProfileFileTitleText.Text}"
                    : $"当前 AREA TXT 文件：{AreaProfileFileTitleText.Text}");
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

    /// <summary>
    /// The display scope is a snapshot, so what the apply button offers is
    /// decided by the selected profile's relation to that snapshot rather than
    /// by the state of any file.
    /// </summary>
    private WatchAreaProfileApplyButtonState EvaluateAreaProfileApplyState(
        WatchAppliedAreaFilterProfile applied,
        WatchAreaFilterProfile draft)
    {
        if (_isAllAreasSelected)
        {
            return WatchAreaProfileApplyButtonState.Evaluate(
                applied.IsAllAreas,
                WatchAreaProfileFileCondition.Valid,
                matchesAppliedSnapshot: true);
        }

        var isCurrentApplied = _selectedAreaProfileName is { } selectedName
            && string.Equals(
                selectedName,
                applied.ProfileName,
                StringComparison.OrdinalIgnoreCase);
        var condition = isCurrentApplied && IsSelectedAreaProfileMissing()
            ? WatchAreaProfileFileCondition.Missing
            : draft.IsValid
                ? WatchAreaProfileFileCondition.Valid
                : WatchAreaProfileFileCondition.Invalid;
        return WatchAreaProfileApplyButtonState.Evaluate(
            isCurrentApplied,
            condition,
            applied.MesAreas.SequenceEqual(draft.MesAreas, StringComparer.Ordinal));
    }

    /// <summary>
    /// Drift is the parsed AREA sequence moving away from the snapshot, so a
    /// comment-only or blank-line edit never raises it. The selected row is
    /// judged against the editor buffer rather than against its file, so the
    /// row and the apply button cannot disagree during the second before
    /// auto-save lands. What every row <em>displays</em> still comes from its
    /// file — the list stays a picture of the directory, not of the buffer.
    /// </summary>
    private IReadOnlyList<WatchAreaFilterProfilePresentationRow> MarkDriftedAreaProfileRows(
        IReadOnlyList<WatchAreaFilterProfilePresentationRow> rows,
        WatchAppliedAreaFilterProfile applied,
        WatchAreaFilterProfile draft) => rows
        .Select(row => row with
        {
            CanSaveAsOverride = IsSelectedAreaProfileRow(row)
                ? draft.IsValid
                : null,
            BlocksIdentityChangingCommands = IsSelectedAreaProfileRow(row)
                && _areaProfileDraftIsDirty,
            HasDrifted = !row.IsAllAreas
                && !row.IsMissing
                && row.IsApplied
                && !applied.MesAreas.SequenceEqual(
                    IsSelectedAreaProfileRow(row) ? draft.MesAreas : row.MesAreas,
                    StringComparer.Ordinal),
        })
        .ToArray();

    private bool IsSelectedAreaProfileRow(
        WatchAreaFilterProfilePresentationRow row) => string.Equals(
        row.ProfileName,
        _selectedAreaProfileName,
        StringComparison.OrdinalIgnoreCase);

    private bool IsSelectedAreaProfileMissing() => _areaProfileRows.Any(row =>
        row.IsMissing
        && string.Equals(
            row.ProfileName,
            _selectedAreaProfileName,
            StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One sentence for where the editor buffer stands relative to its file.
    /// The states are ordered by what the user has to act on first.
    /// </summary>
    private string DescribeAreaProfileDiskState()
    {
        if (_isAllAreasSelected)
        {
            return "不对应 TXT 文件";
        }

        if (_areaProfileWriteConflictProfileName is not null)
        {
            return "磁盘已变更 · 等待选择";
        }

        if (_areaProfileDraftLostItsFile)
        {
            return "文件已删除 · 未命名草稿";
        }

        if (IsSelectedAreaProfileMissing())
        {
            return "文件已删除 · 范围仍生效";
        }

        return _areaProfileDraftIsDirty
            ? "未落盘 · 即将自动保存"
            : _selectedAreaProfileName is null
                ? "尚未保存"
                : "已自动保存";
    }

    private AreaProfileEditorViewState CaptureAreaProfileEditorViewState()
    {
        var scrollViewer = FindVisualDescendant<ScrollViewer>(
            AreaProfileEditor,
            static _ => true);
        return new AreaProfileEditorViewState(
            AreaProfileEditor.CaretIndex,
            AreaProfileEditor.SelectionStart,
            AreaProfileEditor.SelectionLength,
            AreaProfileEditor.GetFirstVisibleLineIndex(),
            scrollViewer?.HorizontalOffset ?? 0,
            scrollViewer?.VerticalOffset ?? 0);
    }

    private void RestoreAreaProfileEditorViewState(AreaProfileEditorViewState state)
    {
        var contentLength = AreaProfileEditor.Text.Length;
        var selectionStart = Math.Min(state.SelectionStart, contentLength);
        var selectionLength = Math.Min(
            state.SelectionLength,
            contentLength - selectionStart);
        AreaProfileEditor.Select(selectionStart, selectionLength);
        if (selectionLength == 0)
        {
            AreaProfileEditor.CaretIndex = Math.Min(state.CaretIndex, contentLength);
        }

        AreaProfileEditor.UpdateLayout();
        if (FindVisualDescendant<ScrollViewer>(
                AreaProfileEditor,
                static _ => true) is { } scrollViewer)
        {
            scrollViewer.ScrollToHorizontalOffset(state.HorizontalOffset);
            scrollViewer.ScrollToVerticalOffset(state.VerticalOffset);
        }
        else
        {
            var lastLine = Math.Max(0, AreaProfileEditor.LineCount - 1);
            AreaProfileEditor.ScrollToLine(Math.Clamp(state.FirstVisibleLine, 0, lastLine));
        }
    }

    private void ReloadAreaProfileRows(
        WatchAppliedAreaFilterProfile applied,
        bool reloadSelectedDraft = true)
    {
        var fileRows = _areaProfileStore
            .EnumerateProfiles()
            .Select(summary =>
            {
                var parsed = summary.IsMissing
                    ? CreateMissingAppliedProfileDraft(applied)
                    : _areaProfileStore.Load(summary.ProfileName);
                return new WatchAreaFilterProfilePresentationRow(
                    summary.ProfileName,
                    parsed.MesAreas,
                    parsed.Diagnostics.Count,
                    parsed.IsValid,
                    summary.IsApplied,
                    summary.FileLastModifiedAt,
                    summary.Availability);
            })
            .ToArray();
        var rows = new[]
        {
            new WatchAreaFilterProfilePresentationRow(
                AllAreasProfileDisplayName,
                [],
                DiagnosticCount: 0,
                IsValid: true,
                IsApplied: applied.IsAllAreas,
                FileLastModifiedAt: null,
                WatchAreaFilterProfileAvailability.Present)
            {
                IsAllAreas = true,
            },
        }.Concat(fileRows).ToArray();

        if (_selectedAreaProfileName is { } selectedName)
        {
            if (rows.FirstOrDefault(row => string.Equals(
                    row.ProfileName,
                    selectedName,
                    StringComparison.OrdinalIgnoreCase)) is { } selectedRow)
            {
                if (selectedRow.IsMissing && _areaProfileDraftIsDirty)
                {
                    AbandonDeletedAreaProfileFileIdentity();
                }
                else
                {
                    _selectedAreaProfileName = selectedRow.ProfileName;
                    if (selectedRow.IsMissing)
                    {
                        _areaProfileDraft = _areaProfileDraft is { } existingDraft
                            && string.Equals(
                                existingDraft.ProfileName,
                                selectedRow.ProfileName,
                                StringComparison.OrdinalIgnoreCase)
                                ? existingDraft with { FileFingerprint = null }
                                : CreateMissingAppliedProfileDraft(applied);
                    }
                    else if (!_areaProfileDraftIsDirty && reloadSelectedDraft)
                    {
                        _areaProfileDraft = _areaProfileStore.Load(selectedRow.ProfileName);
                    }
                }
            }
            else if (_areaProfileDraftIsDirty)
            {
                // Somebody else removed the file while the user still had
                // unwritten input. Dropping the buffer here would throw away
                // what they typed, so it becomes an unnamed draft that only
                // "save as" can put back on disk.
                AbandonDeletedAreaProfileFileIdentity();
            }
            else if (FindAdjacentAreaProfileRow(rows, selectedName) is { } neighbourRow)
            {
                if (neighbourRow.IsAllAreas)
                {
                    SelectAllAreasRow();
                }
                else
                {
                    _selectedAreaProfileName = neighbourRow.ProfileName;
                    _areaProfileDraft = _areaProfileStore.Load(neighbourRow.ProfileName);
                }
            }
            else
            {
                _selectedAreaProfileName = null;
                _areaProfileDraft = null;
            }
        }

        if (!_isAllAreasSelected
            && _selectedAreaProfileName is null
            && !_areaProfileDraftIsDirty
            && applied.ProfileName is { } appliedProfileName
            && rows.FirstOrDefault(row => string.Equals(
                row.ProfileName,
                appliedProfileName,
                StringComparison.OrdinalIgnoreCase)) is { } appliedRow)
        {
            _selectedAreaProfileName = appliedRow.ProfileName;
            _areaProfileDraft = appliedRow.IsMissing
                ? CreateMissingAppliedProfileDraft(applied)
                : _areaProfileStore.Load(appliedRow.ProfileName);
        }

        _areaProfileRows = rows;
        _areaProfileRowsLoaded = true;
    }

    private void AbandonDeletedAreaProfileFileIdentity()
    {
        _isAllAreasSelected = false;
        _selectedAreaProfileName = null;
        _areaProfileDraftLostItsFile = true;
        _areaProfileDraft = _areaProfileDraft is { } orphanedDraft
            ? WatchAreaFilterProfileParser.Parse(string.Empty, orphanedDraft.Content)
            : null;
        ClearAreaProfileWriteConflict();
    }

    private static WatchAreaFilterProfile CreateMissingAppliedProfileDraft(
        WatchAppliedAreaFilterProfile applied) => WatchAreaFilterProfileParser.Parse(
        applied.ProfileName ?? string.Empty,
        string.Join('\n', applied.MesAreas));

    /// <summary>
    /// Picks the row that takes the place of one that left the list, so a
    /// profile deleted underneath a clean editor moves the selection to what
    /// is now in that position rather than leaving the page with nothing. Both
    /// sides are narrowed by the active search, because a neighbour the search
    /// hides would leave the list showing no selection at all.
    /// </summary>
    private WatchAreaFilterProfilePresentationRow? FindAdjacentAreaProfileRow(
        IReadOnlyList<WatchAreaFilterProfilePresentationRow> refreshedRows,
        string vanishedProfileName)
    {
        var visibleBefore = FilterAreaProfileRowsBySearch(_areaProfileRows);
        var visibleAfter = FilterAreaProfileRowsBySearch(refreshedRows);
        if (visibleAfter.Count == 0)
        {
            return null;
        }

        var previousIndex = -1;
        for (var index = 0; index < visibleBefore.Count; index++)
        {
            if (string.Equals(
                    visibleBefore[index].ProfileName,
                    vanishedProfileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                previousIndex = index;
                break;
            }
        }

        return previousIndex < 0
            ? null
            : visibleAfter[Math.Min(previousIndex, visibleAfter.Count - 1)];
    }

    private IReadOnlyList<WatchAreaFilterProfilePresentationRow> FilterAreaProfileRowsBySearch(
        IReadOnlyList<WatchAreaFilterProfilePresentationRow> rows)
    {
        var searchText = AreaProfileSearchInput?.Text.Trim() ?? string.Empty;
        return searchText.Length == 0
            ? rows
            : rows
                .Where(row => row.IsAllAreas || row.ProfileName.Contains(
                    searchText,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
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
        const string formatHint = "每行一个 AREA · 格式：A1-1 或 A11-11 · 空行和 # 注释会忽略";
        var fullPath = Path.GetFullPath(directoryPath);
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        string storageCaption;
        if (TryFormatLocalApplicationDataCaption(
                fullPath,
                localApplicationData,
                out var caption))
        {
            storageCaption = caption;
        }
        else if (string.Equals(
                Environment.GetEnvironmentVariable("MESINGEST_WATCH_UI_TEST_MODE"),
                "1",
                StringComparison.Ordinal)
            && TryFormatLocalApplicationDataCaption(
                fullPath,
                Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                out caption))
        {
            storageCaption = caption;
        }
        else
        {
            var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
            storageCaption = $"{directoryName} · 本机 TXT · UTF-8";
        }

        return $"{storageCaption} · {formatHint}";
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

        if (row.IsAllAreas
            ? _isAllAreasSelected
            : !_isAllAreasSelected && string.Equals(
                _selectedAreaProfileName,
                row.ProfileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_areaProfileWriteConflictProfileName is not null)
        {
            // Walking away from the prompt would resolve the conflict by
            // dropping one of the two versions without saying so.
            RenderAreaProfiles();
            ShowAreaProfileInfo(
                InfoBarSeverity.Warning,
                "请先处理 AREA 写入冲突",
                "当前配置的磁盘版本与你的输入都还在；先选择保留哪一份，再切换配置。");
            return;
        }

        FlushAreaProfileAutoSave();
        try
        {
            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
            if (row.IsAllAreas)
            {
                SelectAllAreasRow();
                RenderAreaProfiles();
                return;
            }

            var selectedDraft = LoadSelectedAreaProfileDraft(row);
            _isAllAreasSelected = false;
            _selectedAreaProfileName = row.ProfileName;
            _areaProfileDraft = selectedDraft;
            _areaProfileDraftIsDirty = false;
            _areaProfileDraftLostItsFile = false;
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

    private WatchAreaFilterProfile LoadSelectedAreaProfileDraft(
        WatchAreaFilterProfilePresentationRow row)
    {
        if (!row.IsMissing)
        {
            return _areaProfileStore.Load(row.ProfileName);
        }

        var appliedState = _areaProfileStore.LoadAppliedState();
        if (appliedState.Diagnostic is { } diagnostic)
        {
            throw new InvalidOperationException(diagnostic.Message);
        }

        if (!string.Equals(
                appliedState.CurrentApplied.ProfileName,
                row.ProfileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "该缺失配置已不再是当前应用范围；请重新选择后再恢复。");
        }

        return CreateMissingAppliedProfileDraft(appliedState.CurrentApplied);
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
        ScheduleAreaProfileAutoSave();
        RenderAreaProfiles();
        if (focusToPreserve is UIElement { IsVisible: true, IsEnabled: true } element
            && ReferenceEquals(Keyboard.FocusedElement, this))
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => element.Focus());
        }
    }

    /// <summary>
    /// Restarts the idle countdown after every keystroke, so a burst of typing
    /// produces one write once the user pauses instead of one write per change.
    /// </summary>
    private void ScheduleAreaProfileAutoSave()
    {
        if (_disposed || _areaProfileWriteConflictProfileName is not null)
        {
            return;
        }

        _areaProfileAutoSaveTimer ??= _areaProfileClock.CreateTimer(
            _ => OnAreaProfileAutoSaveDue(),
            state: null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _areaProfileAutoSaveTimer.Change(
            WatchAreaFilterProfileStore.EditorAutoSaveDelay,
            Timeout.InfiniteTimeSpan);
    }

    private void CancelAreaProfileAutoSave() => _areaProfileAutoSaveTimer?.Change(
        Timeout.InfiniteTimeSpan,
        Timeout.InfiniteTimeSpan);

    private void OnAreaProfileAutoSaveDue()
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            FlushAreaProfileAutoSave();
            return;
        }

        Dispatcher.BeginInvoke(FlushAreaProfileAutoSave);
    }

    /// <summary>
    /// Writes the buffer out now. Called by the idle timer, by <c>Ctrl+S</c>,
    /// and before anything that would take the buffer away from the user:
    /// selecting another profile, leaving the page, or closing the window.
    /// </summary>
    private void FlushAreaProfileAutoSave()
    {
        CancelAreaProfileAutoSave();
        if (_disposed
            || !_areaProfileDraftIsDirty
            || AreaProfileList is null
            // The conflict prompt exists to let the user choose; writing while
            // it is on screen would decide for them.
            || _areaProfileWriteConflictProfileName is not null)
        {
            return;
        }

        AreaProfileOperationTask = RunAreaProfileUiActionAsync(() =>
        {
            var draft = CurrentAreaProfileDraft();
            if (WatchAreaFilterProfileStore.HasUnusableProfileName(draft))
            {
                // An unnamed draft has nowhere to be written; it waits for
                // "save as" and stays dirty until then.
                return Task.CompletedTask;
            }

            // Only a draft that has never been on disk is created outright.
            // Anything that was loaded from a file is written against its
            // fingerprint, so a file deleted underneath the editor keeps the
            // buffer instead of being silently recreated.
            var result = _selectedAreaProfileName is null && draft.FileFingerprint is null
                ? _areaProfileStore.AutoSave(draft.ProfileName, draft.Content)
                : _areaProfileStore.AutoSave(
                    draft.ProfileName,
                    draft.Content,
                    RequireLoadedAreaProfileFingerprint(draft));
            if (!result.Saved)
            {
                if (HasAreaProfileDiagnostic(
                        result.Diagnostics,
                        WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk)
                    && _selectedAreaProfileName is { } conflictingProfileName)
                {
                    BeginAreaProfileWriteConflict(conflictingProfileName);
                    return Task.CompletedTask;
                }

                throw new InvalidOperationException(ProjectAreaDiagnostics(result.Diagnostics));
            }

            AdoptSavedAreaProfile(result.Draft);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Puts the page in front of the one decision it cannot make on the user's
    /// behalf: their unwritten input and the file on disk both changed, and
    /// writing either one over the other would lose work. Automatic saving
    /// stays suspended until the choice is made.
    /// </summary>
    private void BeginAreaProfileWriteConflict(string profileName)
    {
        _areaProfileWriteConflictProfileName = profileName;
        CancelAreaProfileAutoSave();
        AreaProfileWriteConflictInfo.Title = $"{profileName}.txt 的磁盘版本与你的输入都已改变";
        AreaProfileWriteConflictPanel.Visibility = Visibility.Visible;
        RenderAreaProfiles();
    }

    private void ClearAreaProfileWriteConflict()
    {
        if (_areaProfileWriteConflictProfileName is null)
        {
            return;
        }

        _areaProfileWriteConflictProfileName = null;
        AreaProfileWriteConflictPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Keeps the editor buffer. The store reads the file's current fingerprint
    /// and writes against it inside one transaction, so this rebases the
    /// optimistic-concurrency baseline without opening a window for a third
    /// writer to be overwritten unnoticed.
    /// </summary>
    private void OnAreaProfileKeepLocalEditClick(object sender, RoutedEventArgs e)
    {
        if (_areaProfileWriteConflictProfileName is not { } profileName)
        {
            return;
        }

        AreaProfileOperationTask = RunAreaProfileUiActionAsync(() =>
        {
            var draft = CurrentAreaProfileDraft();
            var result = _areaProfileStore.OverwriteWithLocalEdit(
                profileName,
                draft.Content);
            if (!result.Saved)
            {
                throw new InvalidOperationException(ProjectAreaDiagnostics(result.Diagnostics));
            }

            ClearAreaProfileWriteConflict();
            AdoptSavedAreaProfile(result.Draft);
            AreaProfileEditor.Focus();
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Discards the editor buffer in favour of what the other writer produced.
    /// </summary>
    private void OnAreaProfileUseDiskVersionClick(object sender, RoutedEventArgs e)
    {
        if (_areaProfileWriteConflictProfileName is not { } profileName)
        {
            return;
        }

        AreaProfileOperationTask = RunAreaProfileUiActionAsync(() =>
        {
            var onDisk = _areaProfileStore.Load(profileName);
            ClearAreaProfileWriteConflict();
            AdoptSavedAreaProfile(onDisk);
            AreaProfileEditor.Focus();
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Makes a profile that is now on disk the selected, clean editor buffer.
    /// Every path that finishes a write goes through here so none of them can
    /// forget one of the four fields that describe the editor's disk state.
    /// </summary>
    private void AdoptSavedAreaProfile(WatchAreaFilterProfile saved)
    {
        _isAllAreasSelected = false;
        _selectedAreaProfileName = saved.ProfileName;
        _areaProfileDraft = saved;
        _areaProfileDraftIsDirty = false;
        _areaProfileDraftLostItsFile = false;
        RenderAreaProfiles(reloadProfiles: true);
    }

    private void SelectAllAreasRow()
    {
        _isAllAreasSelected = true;
        _selectedAreaProfileName = null;
        _areaProfileDraft = null;
        _areaProfileDraftIsDirty = false;
        _areaProfileDraftLostItsFile = false;
    }

    /// <summary>
    /// <c>Ctrl+S</c>, the standing gesture of <see cref="ApplicationCommands.Save"/>,
    /// stays available as the explicit write for users who would rather not
    /// trust the idle timer.
    /// </summary>
    private void OnAreaProfileSaveNowExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_areaProfileWriteConflictProfileName is not null)
        {
            // An explicit write that quietly does nothing is worse than one
            // that says why it was refused.
            ShowAreaProfileInfo(
                InfoBarSeverity.Warning,
                "请先处理 AREA 写入冲突",
                "该配置的磁盘版本已被其他程序修改；先选择保留哪一份，写盘才会继续。");
            return;
        }

        FlushAreaProfileAutoSave();
    }

    private void DisposeAreaProfileAutoSave()
    {
        _areaProfileAutoSaveTimer?.Dispose();
        _areaProfileAutoSaveTimer = null;
    }

    private void OnAreaProfileSaveAsClick(object sender, RoutedEventArgs e)
    {
        if (!SelectAreaProfileFileCommandTarget(sender, out var invoker))
        {
            return;
        }

        BeginAreaProfileFileOperation(
            AreaProfileFileOperation.SaveAs,
            "另存为",
            IsSelectedAreaProfileMissing()
                ? _selectedAreaProfileName ?? string.Empty
                : string.Empty,
            invoker);
    }

    private void OnAreaProfileRenameClick(object sender, RoutedEventArgs e)
    {
        if (!SelectAreaProfileFileCommandTarget(sender, out var invoker))
        {
            return;
        }

        BeginAreaProfileFileOperation(
            AreaProfileFileOperation.Rename,
            "重命名",
            _selectedAreaProfileName ?? string.Empty,
            invoker);
    }

    private void OnAreaProfileDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!SelectAreaProfileFileCommandTarget(sender, out var invoker))
        {
            return;
        }

        if (_selectedAreaProfileName is not { } selectedName)
        {
            return;
        }

        BeginAreaProfileFileOperation(
            AreaProfileFileOperation.Delete,
            $"再次确认删除“{selectedName}.txt”",
            selectedName,
            invoker);
    }

    private bool SelectAreaProfileFileCommandTarget(
        object sender,
        out IInputElement? invoker)
    {
        invoker = sender as IInputElement;
        if (sender is not MenuItem
            {
                CommandParameter: WatchAreaFilterProfilePresentationRow target,
            })
        {
            return true;
        }

        // Context-menu items disappear as soon as they invoke a command. The
        // list is the stable keyboard origin to restore after the inline
        // confirmation closes, whether the operation keeps or replaces the row.
        invoker = AreaProfileList;

        if (!string.Equals(
                _selectedAreaProfileName,
                target.ProfileName,
                StringComparison.OrdinalIgnoreCase))
        {
            AreaProfileList.SelectedItem = _areaProfileRows.FirstOrDefault(row =>
                string.Equals(
                    row.ProfileName,
                    target.ProfileName,
                    StringComparison.OrdinalIgnoreCase));
        }

        return string.Equals(
            _selectedAreaProfileName,
            target.ProfileName,
            StringComparison.OrdinalIgnoreCase);
    }

    private void BeginAreaProfileFileOperation(
        AreaProfileFileOperation operation,
        string prompt,
        string targetName,
        IInputElement? invoker)
    {
        if (operation is AreaProfileFileOperation.Rename or AreaProfileFileOperation.Delete)
        {
            FlushAreaProfileAutoSave();
        }

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
                $"再次确认删除“{sourceProfileName}.txt”；若该配置为当前应用，删除后已应用 AREA 快照与显示范围仍生效",
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
                    // A fresh draft leaves the old file behind, conflict and
                    // all; keeping the prompt would suspend auto-save for a
                    // profile the editor no longer shows.
                    ClearAreaProfileWriteConflict();
                    _isAllAreasSelected = false;
                    _selectedAreaProfileName = null;
                    _areaProfileDraft = WatchAreaFilterProfileParser.Parse(profileName, string.Empty);
                    _areaProfileDraftIsDirty = true;
                    _areaProfileDraftLostItsFile = false;
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

                    // Saving the buffer elsewhere settles the conflict: the
                    // editor now follows the new file, and the old one keeps
                    // whatever the other writer put there.
                    ClearAreaProfileWriteConflict();
                    CloseAreaProfileFileOperation();
                    ShowAreaProfileInfo(
                        InfoBarSeverity.Success,
                        "AREA 配置已另存为",
                        $"已创建 {result.Draft.ProfileName}.txt；原文件和当前应用范围均未改变。");
                    AdoptSavedAreaProfile(result.Draft);
                    break;
                }
                case AreaProfileFileOperation.Rename:
                {
                    var selectedName = confirmation.SourceProfileName
                        ?? throw new InvalidOperationException("请先选择要重命名的 AREA TXT 配置。");
                    if (_areaProfileDraftIsDirty)
                    {
                        CloseAreaProfileFileOperation();
                        throw new InvalidOperationException("当前 AREA TXT 尚未落盘；请等待自动保存完成后再重命名。");
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

                    _isAllAreasSelected = false;
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
                        throw new InvalidOperationException("当前 AREA TXT 尚未落盘；请等待自动保存完成后再删除。");
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

                    if (result.AppliedProfileWasDeleted)
                    {
                        _isAllAreasSelected = false;
                        _selectedAreaProfileName = result.CurrentApplied.ProfileName;
                        _areaProfileDraft = _areaProfileDraft is { } deletedDraft
                            ? deletedDraft with { FileFingerprint = null }
                            : CreateMissingAppliedProfileDraft(result.CurrentApplied);
                    }
                    else
                    {
                        _isAllAreasSelected = false;
                        _selectedAreaProfileName = null;
                        _areaProfileDraft = WatchAreaFilterProfileParser.Parse(
                            string.Empty,
                            string.Empty);
                    }

                    _areaProfileDraftIsDirty = false;
                    CloseAreaProfileFileOperation(
                        focusFallback: result.AppliedProfileWasDeleted
                            ? AreaProfileList
                            : AreaProfileNewButton);

                    ShowAreaProfileInfo(
                        InfoBarSeverity.Success,
                        "AREA 配置已删除",
                        result.AppliedProfileWasDeleted
                            ? $"{selectedName}.txt 已删除；已应用 AREA 快照与当前显示范围仍生效，可按原名另存恢复。"
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
        if (_isAllAreasSelected)
        {
            ApplyAllAreas();
            return;
        }

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

            _isAllAreasSelected = false;
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

    private void ApplyAllAreas()
    {
        AreaProfileOperationTask = RunAreaProfileUiActionAsync(async operation =>
        {
            CloseAreaProfileFileOperation(restoreInvokerFocus: false);
            var result = _areaProfileStore.ApplyAllAreas();
            SelectAllAreasRow();
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
                await Dispatcher.InvokeAsync(() =>
                {
                    ShowAreaProfileInfo(
                        InfoBarSeverity.Error,
                        "无法完成 AREA 配置操作",
                        exception.Message);
                    RenderAreaProfiles();
                });
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
