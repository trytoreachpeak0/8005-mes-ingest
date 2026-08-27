using System.Windows.Automation;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace MesIngest.Watch;

internal partial class WatchWorkspaceWindow
{
    private ContentDialog? _activeWorkspaceDialog;
    private string? _activeWorkspaceDialogKind;
    private string? _offeredAllAreasConfirmationKey;

    internal ContentDialog? ActiveWorkspaceDialog => _activeWorkspaceDialog;

    internal string? ActiveWorkspaceDialogKind => _activeWorkspaceDialogKind;

    internal Task ActiveWorkspaceDialogTask { get; private set; } = Task.CompletedTask;

    private void OfferDemandSeriesAllAreasDialogIfNeeded()
    {
        if (!ShouldOfferDemandSeriesAllAreasConfirmation())
        {
            _offeredAllAreasConfirmationKey = null;
            return;
        }

        var view = _session.State.DemandSeries;
        var key = string.Join(
            '|',
            view.HostGeneration,
            view.RequestGeneration,
            view.Snapshot?.SnapshotReference,
            _demandSeriesNavigation?.SeriesId ?? _demandSeriesQuery.Filter.SeriesId);
        if (string.Equals(_offeredAllAreasConfirmationKey, key, StringComparison.Ordinal)
            || _activeWorkspaceDialog is not null)
        {
            return;
        }

        _offeredAllAreasConfirmationKey = key;
        ActiveWorkspaceDialogTask = ShowDemandSeriesAllAreasDialogAsync();
    }

    private async Task ShowDemandSeriesAllAreasDialogAsync()
    {
        var dialog = new ContentDialog(WorkspaceDialogHost)
        {
            Title = "目标不在当前 AREA 范围",
            PrimaryButtonText = "切换到全部 AREA",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            DialogWidth = 520,
            Content = new StackPanel
            {
                Children =
                {
                    DialogText(
                        "切换到“全部 AREA”会清除冻结游标，并重新查询第一页和目标详情。"),
                    DialogText(
                        "这只改变本机显示范围，不会改变 Host 业务投影或 Dispatch 范围。",
                        new Thickness(0, 12, 0, 0)),
                },
            },
        };
        AutomationProperties.SetName(dialog, "确认切换到全部 AREA 范围");
        _activeWorkspaceDialog = dialog;
        _activeWorkspaceDialogKind = "all-areas";
        try
        {
            var result = await dialog.ShowAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            if (result == ContentDialogResult.Primary
                && await Dispatcher.InvokeAsync(
                    ShouldOfferDemandSeriesAllAreasConfirmation))
            {
                var operation = await Dispatcher.InvokeAsync(() =>
                    RunDemandSeriesUiActionAsync(() =>
                        ConfirmDemandSeriesAllAreasAsync(_lifetimeCancellation.Token)));
                await operation.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Window shutdown closes the overlay without changing the query.
        }
        finally
        {
            if (ReferenceEquals(_activeWorkspaceDialog, dialog))
            {
                _activeWorkspaceDialog = null;
                _activeWorkspaceDialogKind = null;
            }
        }
    }

    private async Task ShowAreaProfileWriteConflictDialogAsync(string profileName)
    {
        if (_activeWorkspaceDialog is not null
            || !string.Equals(
                _areaProfileWriteConflictProfileName,
                profileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dialog = new ContentDialog(WorkspaceDialogHost)
        {
            Title = "AREA 文件已被其他程序修改",
            PrimaryButtonText = "覆盖并保存",
            SecondaryButtonText = "重新载入文件",
            CloseButtonText = "稍后处理",
            DefaultButton = ContentDialogButton.Secondary,
            DialogWidth = 520,
            Content = new StackPanel
            {
                Children =
                {
                    DialogText(
                        $"{profileName}.txt 的磁盘版本与当前草稿都已改变，无法自动合并。"),
                    DialogText(
                        "关闭此对话框不会丢弃草稿，但自动保存会继续暂停。",
                        new Thickness(0, 12, 0, 0)),
                },
            },
        };
        AutomationProperties.SetName(dialog, $"{profileName}.txt AREA 并发写入冲突");
        _activeWorkspaceDialog = dialog;
        _activeWorkspaceDialogKind = "area-write-conflict";
        try
        {
            var result = await dialog.ShowAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            if (result == ContentDialogResult.Primary)
            {
                var operation = await Dispatcher.InvokeAsync(() =>
                {
                    OnAreaProfileKeepLocalEditClick(dialog, new RoutedEventArgs());
                    return AreaProfileOperationTask;
                });
                await operation.ConfigureAwait(false);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                var operation = await Dispatcher.InvokeAsync(() =>
                {
                    OnAreaProfileUseDiskVersionClick(dialog, new RoutedEventArgs());
                    return AreaProfileOperationTask;
                });
                await operation.ConfigureAwait(false);
            }
            // None/Close deliberately leaves the conflict marker and dirty
            // draft in place, which keeps auto-save paused.
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Window shutdown is equivalent to "later" for the draft.
        }
        finally
        {
            if (ReferenceEquals(_activeWorkspaceDialog, dialog))
            {
                _activeWorkspaceDialog = null;
                _activeWorkspaceDialogKind = null;
            }
        }
    }

    private Wpf.Ui.Controls.TextBlock DialogText(
        string text,
        Thickness? margin = null)
    {
        var block = new Wpf.Ui.Controls.TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(),
        };
        if (margin is not null)
        {
            block.SetResourceReference(
                System.Windows.Documents.TextElement.ForegroundProperty,
                "TextFillColorSecondaryBrush");
        }

        return block;
    }
}
