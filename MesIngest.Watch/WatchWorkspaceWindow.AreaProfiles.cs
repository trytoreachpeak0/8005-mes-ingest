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

    private sealed record AreaProfileEditorViewState(
        int CaretIndex,
        int SelectionStart,
        int SelectionLength,
        int FirstVisibleLine,
        double HorizontalOffset,
        double VerticalOffset);

    private WatchAreaFilterProfileStore _areaProfileStore = null!;
    private IWatchAreaProfileDirectoryLauncher _areaProfileDirectoryLauncher = null!;
    private IReadOnlyList<WatchAreaFilterProfilePresentationRow> _areaProfileRows = [];
    private WatchAreaFilterProfile? _areaProfileDraft;
    private string? _selectedAreaProfileName;
    private string? _areaProfileStartupError;
    private bool _areaProfileDraftIsDirty;
    private bool _areaProfileRowsLoaded;
    private bool _isRenderingAreaProfiles;
    private AreaProfileFileOperationConfirmation? _areaProfileFileOperationConfirmation;
    private long _areaProfileOperationGeneration;

    internal Task AreaProfileOperationTask { get; private set; } = Task.CompletedTask;

    private void InitializeAreaFilterProfiles(
        string? areaFilterProfilesDirectoryPath,
        TimeProvider? timeProvider,
        IWatchAreaProfileDirectoryLauncher? areaProfileDirectoryLauncher,
        IWatchAreaProfileDirectoryEventSource? areaProfileDirectoryEventSource)
    {
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
            && change.ProfileNames.Contains(selectedName, StringComparer.OrdinalIgnoreCase)
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

            _selectedAreaProfileName = rename.ProfileName;
            if (_areaProfileDraft is not null)
            {
                _areaProfileDraft = _areaProfileDraft with
                {
                    ProfileName = rename.ProfileName,
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
                if (!_areaProfileDraftIsDirty && reloadSelectedDraft)
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
